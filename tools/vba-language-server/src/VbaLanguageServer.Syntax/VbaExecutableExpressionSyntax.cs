namespace VbaLanguageServer.Syntax;

/// <summary>
/// Validates the complete token shape of a runtime VBA expression.
/// </summary>
internal static class VbaExecutableExpressionSyntax
{
    /// <summary>
    /// Determines whether exactly one complete runtime expression occupies the token slice.
    /// </summary>
    public static bool IsComplete(
        IReadOnlyList<VbaToken> tokens,
        int start,
        int end,
        VbaModuleKind moduleKind,
        bool allowLeadingMemberAccess)
    {
        if (start < 0 || end > tokens.Count || start >= end)
        {
            return false;
        }

        var parser = new Parser(
            tokens,
            start,
            end,
            moduleKind,
            allowLeadingMemberAccess);
        return parser.Parse();
    }

    private sealed class Parser(
        IReadOnlyList<VbaToken> tokens,
        int start,
        int end,
        VbaModuleKind moduleKind,
        bool allowLeadingMemberAccess)
    {
        private const int MaximumExpressionDepth = 128;
        private const int ImpPrecedence = 1;
        private const int EqvPrecedence = 2;
        private const int XorPrecedence = 3;
        private const int OrPrecedence = 4;
        private const int AndPrecedence = 5;
        private const int NotPrecedence = 6;
        private const int ComparisonPrecedence = 7;
        private const int ConcatenationPrecedence = 8;
        private const int AdditionPrecedence = 9;
        private const int ModPrecedence = 10;
        private const int IntegerDivisionPrecedence = 11;
        private const int MultiplicationPrecedence = 12;
        private const int UnarySignPrecedence = 13;
        private const int PowerPrecedence = 14;

        private int index = start;
        private int expressionDepth;

        public bool Parse()
            => ParseExpression(ImpPrecedence) && index == end;

        private bool ParseExpression(int minimumPrecedence)
        {
            if (expressionDepth >= MaximumExpressionDepth)
            {
                return false;
            }

            expressionDepth++;
            try
            {
                if (!ParsePrefix())
                {
                    return false;
                }

                while (index < end
                    && TryGetBinaryPrecedence(tokens[index], out var precedence)
                    && precedence >= minimumPrecedence)
                {
                    var rightAssociative = Matches(tokens[index], "^");
                    index++;
                    if (!ParseExpression(precedence + (rightAssociative ? 0 : 1)))
                    {
                        return false;
                    }
                }

                return true;
            }
            finally
            {
                expressionDepth--;
            }
        }

        private bool ParsePrefix()
        {
            if (index >= end)
            {
                return false;
            }

            if (Matches(tokens[index], "TypeOf"))
            {
                return ParseTypeOfExpression();
            }

            if (Matches(tokens[index], "-"))
            {
                index++;
                return ParseExpression(UnarySignPrecedence);
            }

            if (Matches(tokens[index], "Not"))
            {
                index++;
                return ParseExpression(NotPrecedence);
            }

            return ParsePrimary();
        }

        private bool ParseTypeOfExpression()
        {
            index++;
            if (index >= end
                || !CanStartObjectReference(tokens[index])
                || !ParsePrimary(rejectScalarTypeCharacter: true)
                || index >= end
                || !Matches(tokens[index], "Is"))
            {
                return false;
            }

            index++;
            return ParseQualifiedObjectType();
        }

        private bool CanStartObjectReference(VbaToken token)
            => VbaIdentifierSyntaxFacts.IsValidDeclaredName(token)
                || Matches(token, "Me")
                || allowLeadingMemberAccess && Matches(token, ".");

        private bool ParseQualifiedObjectType()
        {
            if (index >= end)
            {
                return false;
            }

            if (Matches(tokens[index], "Object"))
            {
                index++;
                return index >= end || !Matches(tokens[index], ".");
            }

            if (!VbaIdentifierSyntaxFacts.IsValidDeclaredName(tokens[index]))
            {
                return false;
            }

            index++;
            while (index < end && Matches(tokens[index], "."))
            {
                index++;
                if (index >= end
                    || !VbaIdentifierSyntaxFacts.IsValidDeclaredName(tokens[index]))
                {
                    return false;
                }

                index++;
            }

            return true;
        }

        private bool ParsePrimary(bool rejectScalarTypeCharacter = false)
        {
            if (index >= end)
            {
                return false;
            }

            var token = tokens[index];
            if (allowLeadingMemberAccess
                && Matches(token, ".")
                && !(index + 1 < end
                    && tokens[index + 1].Kind == VbaTokenKind.NumericLiteral
                    && AreAdjacent(token, tokens[index + 1])))
            {
                index++;
                if (index >= end || !IsMemberName(tokens[index]))
                {
                    return false;
                }

                index++;
                return ParsePostfix(
                    ConsumeIdentifierTypeCharacter(),
                    rejectScalarTypeCharacter);
            }

            if (token.Kind == VbaTokenKind.StringLiteral)
            {
                index++;
                return token.Text.Length >= 2
                    && token.Text[^1] == '"';
            }

            if (token.Kind == VbaTokenKind.DateLiteral)
            {
                index++;
                return VbaDateLiteralSyntax.IsComplete(token);
            }

            if (token.Kind == VbaTokenKind.NumericLiteral
                || Matches(token, ".")
                || Matches(token, "&"))
            {
                return ParseNumericLiteral();
            }

            if (IsLiteralKeyword(token))
            {
                index++;
                return true;
            }

            if (Matches(token, "Me"))
            {
                index++;
                return moduleKind is VbaModuleKind.ClassModule or VbaModuleKind.FormModule
                    && ParsePostfix(rejectScalarTypeCharacter: rejectScalarTypeCharacter);
            }

            if (Matches(token, "String"))
            {
                return ParseStringIntrinsic();
            }

            if (Matches(token, "Date"))
            {
                return ParseDateIntrinsic();
            }

            if (token.Kind == VbaTokenKind.Keyword
                && VbaLanguageVocabulary.CanBeBareCallTarget(token.Text))
            {
                index++;
                return ParsePostfix(rejectScalarTypeCharacter: rejectScalarTypeCharacter);
            }

            if (VbaIdentifierSyntaxFacts.IsValidDeclaredName(token))
            {
                index++;
                return ParsePostfix(
                    ConsumeIdentifierTypeCharacter(),
                    rejectScalarTypeCharacter);
            }

            if (!Matches(token, "("))
            {
                return false;
            }

            index++;
            if (!ParseExpression(ImpPrecedence)
                || index >= end
                || !Matches(tokens[index], ")"))
            {
                return false;
            }

            index++;
            return true;
        }

        private bool ParseDateIntrinsic()
        {
            index++;
            ConsumeAdjacentIntrinsicStringSuffix();
            if (index >= end || !Matches(tokens[index], "("))
            {
                return true;
            }

            index++;
            if (index >= end || !Matches(tokens[index], ")"))
            {
                return false;
            }

            index++;
            return true;
        }

        private bool ParseStringIntrinsic()
        {
            index++;
            ConsumeAdjacentIntrinsicStringSuffix();
            if (index >= end || !Matches(tokens[index], "("))
            {
                return false;
            }

            index++;
            var namedArgumentSeen = false;
            for (var argumentIndex = 0; argumentIndex < 2; argumentIndex++)
            {
                var namedArgument = index + 1 < end
                    && VbaIdentifierSyntaxFacts.IsValidDeclaredName(tokens[index])
                    && Matches(tokens[index + 1], ":=");
                if (namedArgument)
                {
                    namedArgumentSeen = true;
                    index += 2;
                }
                else if (namedArgumentSeen)
                {
                    return false;
                }

                if (!ParseExpression(ImpPrecedence))
                {
                    return false;
                }

                var separator = argumentIndex == 0 ? "," : ")";
                if (index >= end || !Matches(tokens[index], separator))
                {
                    return false;
                }

                index++;
            }

            return true;
        }

        private void ConsumeAdjacentIntrinsicStringSuffix()
        {
            if (index < end
                && Matches(tokens[index], "$")
                && AreAdjacent(tokens[index - 1], tokens[index]))
            {
                index++;
            }
        }

        private bool ParseNumericLiteral()
        {
            if (index >= end)
            {
                return false;
            }

            var literalStart = index;
            if (Matches(tokens[index], "&"))
            {
                return ParseBasedIntegerLiteral()
                    && VbaConstantExpressionSyntax.IsComplete(tokens, literalStart, index);
            }

            var consumedTrailingDecimalPoint = false;
            if (Matches(tokens[index], "."))
            {
                if (index + 1 >= end
                    || tokens[index + 1].Kind != VbaTokenKind.NumericLiteral
                    || !AreAdjacent(tokens[index], tokens[index + 1]))
                {
                    return false;
                }

                index += 2;
            }
            else if (tokens[index].Kind == VbaTokenKind.NumericLiteral)
            {
                index++;
                if (index < end
                    && Matches(tokens[index], ".")
                    && AreAdjacent(tokens[index - 1], tokens[index]))
                {
                    index++;
                    consumedTrailingDecimalPoint = true;
                }
            }
            else
            {
                return false;
            }

            if (consumedTrailingDecimalPoint
                && index < end
                && Matches(tokens[index], "."))
            {
                return false;
            }

            ParseDecimalExponent();
            if (index < end
                && IsNumericTypeCharacter(tokens[index])
                && AreAdjacent(tokens[index - 1], tokens[index]))
            {
                index++;
            }

            return VbaConstantExpressionSyntax.IsComplete(tokens, literalStart, index);
        }

        private bool ParseBasedIntegerLiteral()
        {
            if (index + 1 >= end || !AreAdjacent(tokens[index], tokens[index + 1]))
            {
                return false;
            }

            var body = tokens[index + 1];
            var completeBody = body.Kind == VbaTokenKind.NumericLiteral
                ? ContainsOnlyOctalDigits(body.Text)
                : body.Kind == VbaTokenKind.Identifier
                    && body.Text.Length > 1
                    && (body.Text[0] is 'H' or 'h'
                        ? ContainsOnlyHexDigits(body.Text.AsSpan(1))
                        : body.Text[0] is 'O' or 'o'
                            && ContainsOnlyOctalDigits(body.Text.AsSpan(1)));
            if (!completeBody)
            {
                return false;
            }

            index += 2;
            if (index < end
                && tokens[index].Text is "%" or "&" or "^"
                && AreAdjacent(tokens[index - 1], tokens[index]))
            {
                index++;
            }

            return true;
        }

        private void ParseDecimalExponent()
        {
            if (index >= end || !AreAdjacent(tokens[index - 1], tokens[index]))
            {
                return;
            }

            var exponent = tokens[index];
            if (exponent.Kind == VbaTokenKind.Identifier
                && exponent.Text.Length > 1
                && exponent.Text[0] is 'D' or 'd' or 'E' or 'e'
                && ContainsOnlyAsciiDigits(exponent.Text.AsSpan(1)))
            {
                index++;
                return;
            }

            if (exponent.Kind != VbaTokenKind.Identifier
                || !exponent.Text.Equals("D", StringComparison.OrdinalIgnoreCase)
                    && !exponent.Text.Equals("E", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var exponentIndex = index + 1;
            if (exponentIndex < end
                && tokens[exponentIndex].Text is "+" or "-"
                && AreAdjacent(exponent, tokens[exponentIndex]))
            {
                exponentIndex++;
            }

            if (exponentIndex >= end
                || tokens[exponentIndex].Kind != VbaTokenKind.NumericLiteral
                || !ContainsOnlyAsciiDigits(tokens[exponentIndex].Text)
                || !AreAdjacent(tokens[exponentIndex - 1], tokens[exponentIndex]))
            {
                return;
            }

            index = exponentIndex + 1;
        }

        private bool ParsePostfix(
            bool scalarTypeCharacterSeen = false,
            bool rejectScalarTypeCharacter = false)
        {
            if (scalarTypeCharacterSeen)
            {
                return !rejectScalarTypeCharacter
                    && ParseOptionalScalarInvocation();
            }

            while (index < end)
            {
                if (Matches(tokens[index], ".") || Matches(tokens[index], "!"))
                {
                    index++;
                    if (index >= end || !IsMemberName(tokens[index]))
                    {
                        return false;
                    }

                    index++;
                    if (ConsumeIdentifierTypeCharacter())
                    {
                        return !rejectScalarTypeCharacter
                            && ParseOptionalScalarInvocation();
                    }

                    continue;
                }

                if (!Matches(tokens[index], "("))
                {
                    break;
                }

                if (!ParseArgumentList())
                {
                    return false;
                }
            }

            return true;
        }

        private bool ParseOptionalScalarInvocation()
        {
            if (index < end
                && Matches(tokens[index], "(")
                && !ParseArgumentList())
            {
                return false;
            }

            return true;
        }

        private bool ConsumeIdentifierTypeCharacter()
        {
            if (index < end
                && IsIdentifierTypeCharacter(tokens[index])
                && AreAdjacent(tokens[index - 1], tokens[index]))
            {
                index++;
                return true;
            }

            return false;
        }

        private bool ParseArgumentList()
        {
            index++;
            if (index < end && Matches(tokens[index], ")"))
            {
                index++;
                return true;
            }

            var namedArgumentSeen = false;
            var expectingArgument = true;
            while (index < end)
            {
                if (Matches(tokens[index], ")"))
                {
                    if (!expectingArgument)
                    {
                        index++;
                        return true;
                    }

                    return false;
                }

                if (Matches(tokens[index], ","))
                {
                    if (namedArgumentSeen)
                    {
                        return false;
                    }

                    index++;
                    expectingArgument = true;
                    continue;
                }

                var namedArgument = index + 1 < end
                    && VbaIdentifierSyntaxFacts.IsValidDeclaredName(tokens[index])
                    && Matches(tokens[index + 1], ":=");
                if (namedArgument)
                {
                    namedArgumentSeen = true;
                    index += 2;
                    if (index >= end
                        || Matches(tokens[index], ",")
                        || Matches(tokens[index], ")"))
                    {
                        return false;
                    }
                }
                else if (namedArgumentSeen)
                {
                    return false;
                }

                if (!ParseExpression(ImpPrecedence))
                {
                    return false;
                }

                expectingArgument = false;
                if (index < end && Matches(tokens[index], ","))
                {
                    index++;
                    expectingArgument = true;
                    continue;
                }

                if (index < end && Matches(tokens[index], ")"))
                {
                    index++;
                    return true;
                }

                return false;
            }

            return false;
        }

        private static bool TryGetBinaryPrecedence(VbaToken token, out int precedence)
        {
            precedence = token.Text.ToUpperInvariant() switch
            {
                "IMP" => ImpPrecedence,
                "EQV" => EqvPrecedence,
                "XOR" => XorPrecedence,
                "OR" => OrPrecedence,
                "AND" => AndPrecedence,
                "=" or "<>" or "<" or ">" or "<=" or ">=" or "LIKE" or "IS" => ComparisonPrecedence,
                "&" => ConcatenationPrecedence,
                "+" or "-" => AdditionPrecedence,
                "MOD" => ModPrecedence,
                "\\" => IntegerDivisionPrecedence,
                "*" or "/" => MultiplicationPrecedence,
                "^" => PowerPrecedence,
                _ => 0
            };
            return precedence > 0;
        }

        private static bool IsLiteralKeyword(VbaToken token)
            => token.Kind == VbaTokenKind.Keyword
                && (Matches(token, "True")
                    || Matches(token, "False")
                    || Matches(token, "Empty")
                    || Matches(token, "Null")
                    || Matches(token, "Nothing"));

        private static bool IsMemberName(VbaToken token)
            => token.Kind is VbaTokenKind.Identifier or VbaTokenKind.Keyword;

        private static bool IsNumericTypeCharacter(VbaToken token)
            => token.Text is "%" or "&" or "^" or "!" or "#" or "@";

        private static bool IsIdentifierTypeCharacter(VbaToken token)
            => IsNumericTypeCharacter(token) || token.Text == "$";

        private static bool AreAdjacent(VbaToken left, VbaToken right)
            => left.Range.End.Offset == right.Range.Start.Offset;

        private static bool ContainsOnlyAsciiDigits(ReadOnlySpan<char> value)
        {
            foreach (var character in value)
            {
                if (!char.IsAsciiDigit(character))
                {
                    return false;
                }
            }

            return value.Length > 0;
        }

        private static bool ContainsOnlyHexDigits(ReadOnlySpan<char> value)
        {
            foreach (var character in value)
            {
                if (!Uri.IsHexDigit(character))
                {
                    return false;
                }
            }

            return value.Length > 0;
        }

        private static bool ContainsOnlyOctalDigits(ReadOnlySpan<char> value)
        {
            foreach (var character in value)
            {
                if (character is < '0' or > '7')
                {
                    return false;
                }
            }

            return value.Length > 0;
        }

        private static bool Matches(VbaToken token, string text)
            => token.Text.Equals(text, StringComparison.OrdinalIgnoreCase);
    }
}
