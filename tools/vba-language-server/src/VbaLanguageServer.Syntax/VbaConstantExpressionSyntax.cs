namespace VbaLanguageServer.Syntax;

/// <summary>
/// Validates the complete token shape of a VBA constant expression.
/// </summary>
internal static class VbaConstantExpressionSyntax
{
    /// <summary>
    /// Determines whether exactly one complete constant expression occupies the token slice.
    /// </summary>
    /// <param name="tokens">The significant tokens containing the expression.</param>
    /// <param name="start">The inclusive slice start.</param>
    /// <param name="end">The exclusive slice end.</param>
    /// <returns>True only when the entire slice is a strict constant-expression shape.</returns>
    public static bool IsComplete(
        IReadOnlyList<VbaToken> tokens,
        int start,
        int end)
    {
        if (start < 0 || end > tokens.Count || start >= end)
        {
            return false;
        }

        var parser = new Parser(tokens, start, end);
        return parser.Parse();
    }

    private sealed class Parser(
        IReadOnlyList<VbaToken> tokens,
        int start,
        int end)
    {
        private static readonly string SingleMaximumDigits = (
            ((System.Numerics.BigInteger.One << 24) - System.Numerics.BigInteger.One) << 104
        ).ToString(System.Globalization.CultureInfo.InvariantCulture);
        private static readonly string DoubleMaximumDigits = (
            ((System.Numerics.BigInteger.One << 53) - System.Numerics.BigInteger.One) << 971
        ).ToString(System.Globalization.CultureInfo.InvariantCulture);
        private const string CurrencyMaximumDigits = "9223372036854775807";
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
                    var isRightAssociative = Matches(tokens[index], "^");
                    index++;
                    if (!ParseExpression(precedence + (isRightAssociative ? 0 : 1)))
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

        private bool ParsePrimary()
        {
            if (index >= end)
            {
                return false;
            }

            var token = tokens[index];
            if (token.Kind == VbaTokenKind.StringLiteral)
            {
                index++;
                return token.Text.Length >= 2 && token.Text[^1] == '"';
            }

            if (token.Kind == VbaTokenKind.NumericLiteral
                || Matches(token, ".")
                || Matches(token, "&"))
            {
                return ParseNumericLiteral();
            }

            if (token.Kind == VbaTokenKind.DateLiteral)
            {
                index++;
                return VbaDateLiteralSyntax.IsComplete(token);
            }

            if (IsIntrinsicConstantFunction(token))
            {
                return index + 1 < end && Matches(tokens[index + 1], "(")
                    && ParseIntrinsicFunctionCall();
            }

            if (VbaIdentifierSyntaxFacts.IsValidDeclaredName(token))
            {
                index++;
                while (index + 1 < end
                    && Matches(tokens[index], ".")
                    && (AreAdjacent(tokens[index - 1], tokens[index])
                        || tokens[index - 1].Range.End.Line < tokens[index].Range.Start.Line)
                    && VbaIdentifierSyntaxFacts.IsValidDeclaredName(tokens[index + 1]))
                {
                    index += 2;
                }

                if (index < end
                    && IsTypeSuffix(tokens[index])
                    && AreAdjacent(tokens[index - 1], tokens[index]))
                {
                    index++;
                }

                return true;
            }

            if (IsLiteralKeyword(token))
            {
                index++;
                return true;
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

        private bool ParseIntrinsicFunctionCall()
        {
            index += 2;
            if (!ParseExpression(ImpPrecedence)
                || index >= end
                || !Matches(tokens[index], ")"))
            {
                return false;
            }

            index++;
            return true;
        }

        private bool ParseNumericLiteral()
        {
            if (Matches(tokens[index], "&"))
            {
                return TryConsumeBasedIntegerLiteral();
            }

            var literalStart = index;
            var isFloat = false;
            if (Matches(tokens[index], "."))
            {
                if (index + 1 >= end
                    || tokens[index + 1].Kind != VbaTokenKind.NumericLiteral
                    || !tokens[index + 1].Text.All(char.IsAsciiDigit)
                    || !AreAdjacent(tokens[index], tokens[index + 1]))
                {
                    return false;
                }

                index += 2;
                isFloat = true;
            }
            else
            {
                isFloat = tokens[index].Text.Contains('.', StringComparison.Ordinal);
                index++;
                if (!isFloat
                    && index < end
                    && Matches(tokens[index], ".")
                    && AreAdjacent(tokens[index - 1], tokens[index]))
                {
                    index++;
                    isFloat = true;
                }
            }

            if (TryConsumeDecimalExponent())
            {
                isFloat = true;
            }

            return TryConsumeDecimalTypeCharacter(literalStart, isFloat);
        }

        private bool TryConsumeDecimalExponent()
        {
            if (index >= end || !AreAdjacent(tokens[index - 1], tokens[index]))
            {
                return false;
            }

            var exponent = tokens[index];
            if (exponent.Kind == VbaTokenKind.Identifier
                && exponent.Text.Length > 1
                && exponent.Text[0] is 'D' or 'd' or 'E' or 'e'
                && ContainsOnlyAsciiDigits(exponent.Text.AsSpan(1)))
            {
                index++;
                return true;
            }

            if (exponent.Kind != VbaTokenKind.Identifier
                || !(exponent.Text.Equals("D", StringComparison.OrdinalIgnoreCase)
                    || exponent.Text.Equals("E", StringComparison.OrdinalIgnoreCase))
                || index + 2 >= end
                || tokens[index + 1].Text is not ("+" or "-")
                || tokens[index + 2].Kind != VbaTokenKind.NumericLiteral
                || !tokens[index + 2].Text.All(char.IsAsciiDigit)
                || !AreAdjacent(exponent, tokens[index + 1])
                || !AreAdjacent(tokens[index + 1], tokens[index + 2]))
            {
                return false;
            }

            index += 3;
            return true;
        }

        private bool TryConsumeBasedIntegerLiteral()
        {
            if (!TryReadBasedIntegerBody(index, out var radix, out var digits))
            {
                return false;
            }

            index += 2;
            string? suffix = null;
            if (index < end && AreAdjacent(tokens[index - 1], tokens[index]))
            {
                if (tokens[index].Text is "!" or "#" or "@")
                {
                    return false;
                }

                if (tokens[index].Text is "%" or "&" or "^")
                {
                    suffix = tokens[index].Text;
                    index++;
                }
            }

            return FitsBasedIntegerRange(radix, digits, suffix);
        }

        private bool TryConsumeDecimalTypeCharacter(int literalStart, bool isFloat)
        {
            var literalEnd = index;
            string? suffix = null;
            if (index < end
                && AreAdjacent(tokens[index - 1], tokens[index])
                && tokens[index].Text is "%" or "&" or "^" or "!" or "#" or "@")
            {
                suffix = tokens[index].Text;
            }

            if (isFloat)
            {
                if (suffix is "%" or "&" or "^")
                {
                    return false;
                }
            }

            if (suffix is not null)
            {
                index++;
            }

            if (!isFloat
                && suffix is "%" or "&" or "^"
                && !FitsDecimalIntegerRange(tokens[literalStart].Text, suffix))
            {
                return false;
            }

            return (!isFloat && suffix is "%" or "&" or "^")
                || FitsDecimalFloatingRange(literalStart, literalEnd, suffix);
        }

        private static bool FitsDecimalIntegerRange(string digits, string suffix)
        {
            if (!digits.All(char.IsAsciiDigit))
            {
                return false;
            }

            var significantDigits = digits.TrimStart('0');
            if (significantDigits.Length == 0)
            {
                return true;
            }

            var maximum = suffix switch
            {
                "%" => "32767",
                "&" => "2147483647",
                "^" => "9223372036854775807",
                _ => string.Empty
            };
            return significantDigits.Length < maximum.Length
                || (significantDigits.Length == maximum.Length
                    && string.CompareOrdinal(significantDigits, maximum) <= 0);
        }

        private bool FitsDecimalFloatingRange(
            int literalStart,
            int literalEnd,
            string? suffix)
        {
            var literal = string.Concat(
                tokens.Skip(literalStart)
                    .Take(literalEnd - literalStart)
                    .Select(token => token.Text));
            return suffix switch
            {
                "!" => IsWithinDecimalMagnitude(literal, SingleMaximumDigits, 0),
                "@" => IsWithinDecimalMagnitude(literal, CurrencyMaximumDigits, -4),
                _ => IsWithinDecimalMagnitude(literal, DoubleMaximumDigits, 0)
            };
        }

        private static bool IsWithinDecimalMagnitude(
            string literal,
            string maximumDigits,
            long maximumExponent)
        {
            var exponentIndex = -1;
            for (var characterIndex = 0; characterIndex < literal.Length; characterIndex++)
            {
                if (literal[characterIndex] is 'D' or 'd' or 'E' or 'e')
                {
                    exponentIndex = characterIndex;
                    break;
                }
            }

            var mantissa = exponentIndex < 0 ? literal : literal[..exponentIndex];
            var decimalPointIndex = mantissa.IndexOf('.', StringComparison.Ordinal);
            var fractionalDigits = decimalPointIndex < 0
                ? 0
                : mantissa.Length - decimalPointIndex - 1;
            var significantDigits = mantissa.Replace(
                ".",
                string.Empty,
                StringComparison.Ordinal).TrimStart('0');
            if (significantDigits.Length == 0)
            {
                return true;
            }

            if (!significantDigits.All(char.IsAsciiDigit))
            {
                return false;
            }

            long explicitExponent = 0;
            if (exponentIndex >= 0)
            {
                var exponentText = literal[(exponentIndex + 1)..];
                if (!long.TryParse(
                    exponentText,
                    System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out explicitExponent))
                {
                    return exponentText.StartsWith("-", StringComparison.Ordinal);
                }
            }

            if (explicitExponent < long.MinValue + fractionalDigits)
            {
                return true;
            }

            var decimalExponent = explicitExponent - fractionalDigits;
            if (decimalExponent > long.MaxValue - significantDigits.Length)
            {
                return false;
            }

            var order = decimalExponent + significantDigits.Length;
            var maximumOrder = maximumExponent + maximumDigits.Length;
            if (order != maximumOrder)
            {
                return order < maximumOrder;
            }

            var comparisonLength = Math.Max(significantDigits.Length, maximumDigits.Length);
            for (var digitIndex = 0; digitIndex < comparisonLength; digitIndex++)
            {
                var digit = digitIndex < significantDigits.Length
                    ? significantDigits[digitIndex]
                    : '0';
                var maximumDigit = digitIndex < maximumDigits.Length
                    ? maximumDigits[digitIndex]
                    : '0';
                if (digit != maximumDigit)
                {
                    return digit < maximumDigit;
                }
            }

            return true;
        }

        private bool TryGetBinaryPrecedence(VbaToken token, out int precedence)
        {
            if (Matches(token, "&")
                && TryReadBasedIntegerBody(index, out _, out _))
            {
                precedence = 0;
                return false;
            }

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

        private bool TryReadBasedIntegerBody(
            int prefixIndex,
            out int radix,
            out string digits)
        {
            radix = 0;
            digits = string.Empty;
            if (prefixIndex + 1 >= end
                || !Matches(tokens[prefixIndex], "&")
                || !AreAdjacent(tokens[prefixIndex], tokens[prefixIndex + 1]))
            {
                return false;
            }

            var body = tokens[prefixIndex + 1];
            if (body.Kind == VbaTokenKind.NumericLiteral
                && body.Text.All(char.IsAsciiDigit)
                && ContainsOnlyOctalDigits(body.Text))
            {
                radix = 8;
                digits = body.Text;
                return true;
            }

            if (body.Kind != VbaTokenKind.Identifier || body.Text.Length <= 1)
            {
                return false;
            }

            if (body.Text[0] is 'H' or 'h'
                && ContainsOnlyHexDigits(body.Text.AsSpan(1)))
            {
                radix = 16;
                digits = body.Text[1..];
                return true;
            }

            if (body.Text[0] is 'O' or 'o'
                && ContainsOnlyOctalDigits(body.Text.AsSpan(1)))
            {
                radix = 8;
                digits = body.Text[1..];
                return true;
            }

            return false;
        }

        private static bool FitsBasedIntegerRange(
            int radix,
            string digits,
            string? suffix)
        {
            var significantDigits = digits.TrimStart('0');
            if (significantDigits.Length == 0)
            {
                return true;
            }

            var maximumDigits = (radix, suffix) switch
            {
                (16, "%") => 4,
                (16, "^") => 16,
                (16, _) => 8,
                (8, "%") => 6,
                (8, "^") => 22,
                (8, _) => 11,
                _ => 0
            };
            if (significantDigits.Length > maximumDigits)
            {
                return false;
            }

            return radix != 8
                || significantDigits.Length < maximumDigits
                || suffix switch
                {
                    "%" or "^" => significantDigits[0] <= '1',
                    _ => significantDigits[0] <= '3'
                };
        }

        private static bool IsLiteralKeyword(VbaToken token)
            => token.Kind == VbaTokenKind.Keyword
                && (Matches(token, "True")
                    || Matches(token, "False")
                    || Matches(token, "Empty")
                    || Matches(token, "Null")
                    || Matches(token, "Nothing"));

        private static bool IsIntrinsicConstantFunction(VbaToken token)
            => token.Text.ToUpperInvariant() is
                "INT" or "FIX" or "ABS" or "SGN" or "LEN" or "LENB"
                or "CBOOL" or "CBYTE" or "CCUR" or "CDATE" or "CDBL"
                or "CINT" or "CLNG" or "CLNGLNG" or "CLNGPTR" or "CSNG"
                or "CSTR" or "CVAR";

        private static bool IsTypeSuffix(VbaToken token)
            => token.Text is "%" or "&" or "^" or "!" or "#" or "@" or "$";

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
