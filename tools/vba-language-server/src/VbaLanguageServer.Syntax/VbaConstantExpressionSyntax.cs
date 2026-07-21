namespace VbaLanguageServer.Syntax;

/// <summary>
/// Validates the complete token shape of a VBA constant expression.
/// </summary>
internal static class VbaConstantExpressionSyntax
{
    private abstract record ExpressionNode;

    private sealed record LiteralExpressionNode(IReadOnlyList<VbaToken> Tokens)
        : ExpressionNode;

    private sealed record NameExpressionNode(string Name) : ExpressionNode;

    private sealed record UnaryExpressionNode(string Operator, ExpressionNode Operand)
        : ExpressionNode;

    private sealed record BinaryExpressionNode(
        string Operator,
        ExpressionNode Left,
        ExpressionNode Right)
        : ExpressionNode;

    private sealed record UnsupportedExpressionNode : ExpressionNode;

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
        => IsCompleteCore(tokens, start, end, allowQualifiedNames: true);

    /// <summary>
    /// Determines whether the slice is valid for conditional compilation.
    /// </summary>
    public static bool IsConditionalCompilationExpressionComplete(
        IReadOnlyList<VbaToken> tokens,
        int start,
        int end)
        => IsCompleteCore(tokens, start, end, allowQualifiedNames: false);

    internal static VbaConditionalCompilationExpressionEvaluation
        EvaluateConditionalCompilationExpression(
            IReadOnlyList<VbaToken> tokens,
            int start,
            int end,
            Func<string, VbaConditionalCompilationExpressionEvaluation> resolveConstant,
            bool supportsLongLong = false)
    {
        if (start < 0 || end > tokens.Count || start >= end)
        {
            return VbaConditionalCompilationExpressionEvaluation.Failure(
                VbaConditionalCompilationFailureKind.Malformed,
                "The conditional-compilation expression is malformed.");
        }

        var parser = new Parser(tokens, start, end, allowQualifiedNames: false);
        if (!parser.TryParse(out var expression))
        {
            return VbaConditionalCompilationExpressionEvaluation.Failure(
                VbaConditionalCompilationFailureKind.Malformed,
                "The conditional-compilation expression is malformed.");
        }

        return Evaluate(expression, resolveConstant, supportsLongLong);
    }

    private static VbaConditionalCompilationExpressionEvaluation Evaluate(
        ExpressionNode expression,
        Func<string, VbaConditionalCompilationExpressionEvaluation> resolveConstant,
        bool supportsLongLong)
        => expression switch
        {
            LiteralExpressionNode literal => EvaluateLiteral(literal, supportsLongLong),
            NameExpressionNode name => resolveConstant(name.Name),
            UnaryExpressionNode unary => EvaluateUnary(unary, resolveConstant, supportsLongLong),
            BinaryExpressionNode binary => EvaluateBinary(binary, resolveConstant, supportsLongLong),
            _ => Unsupported()
        };

    private static VbaConditionalCompilationExpressionEvaluation EvaluateLiteral(
        LiteralExpressionNode literal,
        bool supportsLongLong)
    {
        if (literal.Tokens.Count == 1)
        {
            var token = literal.Tokens[0];
            if (token.Text.Equals("True", StringComparison.OrdinalIgnoreCase))
            {
                return VbaConditionalCompilationExpressionEvaluation.Success(
                    VbaConditionalCompilationValue.FromBoolean(true));
            }

            if (token.Text.Equals("False", StringComparison.OrdinalIgnoreCase))
            {
                return VbaConditionalCompilationExpressionEvaluation.Success(
                    VbaConditionalCompilationValue.FromBoolean(false));
            }

            if (token.Text.Equals("Empty", StringComparison.OrdinalIgnoreCase))
            {
                return VbaConditionalCompilationExpressionEvaluation.Success(
                    VbaConditionalCompilationValue.FromInteger(0));
            }
        }

        var text = string.Concat(literal.Tokens.Select(token => token.Text));
        if (!supportsLongLong && text.EndsWith('^'))
        {
            return Unsupported();
        }

        if (text.StartsWith("&", StringComparison.Ordinal))
        {
            return EvaluateBasedIntegerLiteral(text);
        }

        var suffix = text[^1] is '%' or '&' or '^' ? text[^1] : '\0';
        var digits = text;
        if (suffix != '\0')
        {
            digits = digits[..^1];
        }

        if (!digits.All(char.IsAsciiDigit))
        {
            return Unsupported();
        }

        if (!long.TryParse(
            digits,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value))
        {
            return Overflow();
        }

        // Unsuffixed integral text above Long is represented through floating coercion in VBA.
        if (suffix == '\0' && value > int.MaxValue)
        {
            return Unsupported();
        }

        var integralWidth = suffix switch
        {
            '%' => VbaConditionalCompilationIntegralWidth.Int16,
            '&' => VbaConditionalCompilationIntegralWidth.Int32,
            '^' => VbaConditionalCompilationIntegralWidth.Int64,
            _ => value <= short.MaxValue
                ? VbaConditionalCompilationIntegralWidth.Int16
                : VbaConditionalCompilationIntegralWidth.Int32
        };
        return VbaConditionalCompilationExpressionEvaluation.Success(
            VbaConditionalCompilationValue.FromInteger(value, integralWidth));
    }

    private static VbaConditionalCompilationExpressionEvaluation EvaluateBasedIntegerLiteral(
        string text)
    {
        var suffix = text[^1] is '%' or '&' or '^' ? text[^1] : '\0';
        if (suffix != '\0')
        {
            text = text[..^1];
        }

        var radix = 8;
        var digits = text[1..];
        if (digits.StartsWith("H", StringComparison.OrdinalIgnoreCase))
        {
            radix = 16;
            digits = digits[1..];
        }
        else if (digits.StartsWith("O", StringComparison.OrdinalIgnoreCase))
        {
            digits = digits[1..];
        }

        System.Numerics.BigInteger magnitude = 0;
        foreach (var character in digits)
        {
            var digit = character is >= '0' and <= '9'
                ? character - '0'
                : char.ToUpperInvariant(character) - 'A' + 10;
            if (digit < 0 || digit >= radix)
            {
                return Unsupported();
            }

            magnitude = (magnitude * radix) + digit;
        }

        var integralWidth = suffix switch
        {
            '%' => VbaConditionalCompilationIntegralWidth.Int16,
            '&' => VbaConditionalCompilationIntegralWidth.Int32,
            '^' => VbaConditionalCompilationIntegralWidth.Int64,
            _ when magnitude <= ushort.MaxValue =>
                VbaConditionalCompilationIntegralWidth.Int16,
            _ => VbaConditionalCompilationIntegralWidth.Int32
        };
        var width = (int)integralWidth;
        var modulus = System.Numerics.BigInteger.One << width;
        if (magnitude >= modulus)
        {
            return Overflow();
        }

        if (magnitude >= (modulus >> 1))
        {
            magnitude -= modulus;
        }

        return magnitude < long.MinValue || magnitude > long.MaxValue
            ? Overflow()
            : VbaConditionalCompilationExpressionEvaluation.Success(
                VbaConditionalCompilationValue.FromInteger(
                    (long)magnitude,
                    integralWidth));
    }

    private static VbaConditionalCompilationExpressionEvaluation EvaluateUnary(
        UnaryExpressionNode unary,
        Func<string, VbaConditionalCompilationExpressionEvaluation> resolveConstant,
        bool supportsLongLong)
    {
        var operand = Evaluate(unary.Operand, resolveConstant, supportsLongLong);
        if (!operand.Succeeded)
        {
            return operand;
        }

        if (unary.Operator.Equals("Not", StringComparison.OrdinalIgnoreCase))
        {
            return VbaConditionalCompilationExpressionEvaluation.Success(
                operand.Value.Kind == VbaConditionalCompilationValueKind.Boolean
                    ? VbaConditionalCompilationValue.FromBoolean(!operand.Value.IsTruthy)
                    : VbaConditionalCompilationValue.FromInteger(
                        ~operand.Value.IntegralValue,
                        operand.Value.IntegralWidth));
        }

        if (unary.Operator == "-")
        {
            if (operand.Value.Kind == VbaConditionalCompilationValueKind.Boolean)
            {
                return Unsupported();
            }

            if (operand.Value.IntegralValue == long.MinValue)
            {
                return Overflow();
            }

            var value = -operand.Value.IntegralValue;
            return !FitsIntegralWidth(value, operand.Value.IntegralWidth)
                ? Overflow()
                : VbaConditionalCompilationExpressionEvaluation.Success(
                    VbaConditionalCompilationValue.FromInteger(
                        value,
                        operand.Value.IntegralWidth));
        }

        return Unsupported();
    }

    private static VbaConditionalCompilationExpressionEvaluation EvaluateBinary(
        BinaryExpressionNode binary,
        Func<string, VbaConditionalCompilationExpressionEvaluation> resolveConstant,
        bool supportsLongLong)
    {
        var left = Evaluate(binary.Left, resolveConstant, supportsLongLong);
        if (!left.Succeeded)
        {
            return left;
        }

        var right = Evaluate(binary.Right, resolveConstant, supportsLongLong);
        if (!right.Succeeded)
        {
            return right;
        }

        var leftValue = left.Value.IntegralValue;
        var rightValue = right.Value.IntegralValue;
        var operation = binary.Operator.ToUpperInvariant();
        var resultWidth = WidestIntegralWidth(
            left.Value.IntegralWidth,
            right.Value.IntegralWidth);
        if (operation is "+" or "-" or "*" or "\\" or "MOD"
            && (left.Value.Kind == VbaConditionalCompilationValueKind.Boolean
                || right.Value.Kind == VbaConditionalCompilationValueKind.Boolean))
        {
            return Unsupported();
        }

        try
        {
            return operation switch
            {
                "=" => Boolean(leftValue == rightValue),
                "<>" or "><" => Boolean(leftValue != rightValue),
                "<" => Boolean(leftValue < rightValue),
                ">" => Boolean(leftValue > rightValue),
                "<=" or "=<" => Boolean(leftValue <= rightValue),
                ">=" or "=>" => Boolean(leftValue >= rightValue),
                "+" => Integer(checked(leftValue + rightValue), resultWidth),
                "-" => Integer(checked(leftValue - rightValue), resultWidth),
                "*" => Integer(checked(leftValue * rightValue), resultWidth),
                "\\" when rightValue != 0 =>
                    Integer(checked(leftValue / rightValue), resultWidth),
                "MOD" when rightValue != 0 =>
                    Integer(checked(leftValue % rightValue), resultWidth),
                "AND" => Logical(left.Value, right.Value, leftValue & rightValue),
                "OR" => Logical(left.Value, right.Value, leftValue | rightValue),
                "XOR" => Logical(left.Value, right.Value, leftValue ^ rightValue),
                "EQV" => Logical(left.Value, right.Value, ~(leftValue ^ rightValue)),
                "IMP" => Logical(left.Value, right.Value, (~leftValue) | rightValue),
                _ => Unsupported()
            };
        }
        catch (OverflowException)
        {
            return Overflow();
        }
    }

    private static VbaConditionalCompilationExpressionEvaluation Logical(
        VbaConditionalCompilationValue left,
        VbaConditionalCompilationValue right,
        long value)
        => left.Kind == VbaConditionalCompilationValueKind.Boolean
            && right.Kind == VbaConditionalCompilationValueKind.Boolean
            ? Boolean(value != 0)
            : Integer(
                value,
                WidestIntegralWidth(left.IntegralWidth, right.IntegralWidth));

    private static VbaConditionalCompilationExpressionEvaluation Boolean(bool value)
        => VbaConditionalCompilationExpressionEvaluation.Success(
            VbaConditionalCompilationValue.FromBoolean(value));

    private static VbaConditionalCompilationExpressionEvaluation Integer(
        long value,
        VbaConditionalCompilationIntegralWidth width)
        => FitsIntegralWidth(value, width)
            ? VbaConditionalCompilationExpressionEvaluation.Success(
                VbaConditionalCompilationValue.FromInteger(value, width))
            : Overflow();

    private static VbaConditionalCompilationIntegralWidth WidestIntegralWidth(
        VbaConditionalCompilationIntegralWidth left,
        VbaConditionalCompilationIntegralWidth right)
        => left >= right ? left : right;

    private static bool FitsIntegralWidth(
        long value,
        VbaConditionalCompilationIntegralWidth width)
        => width switch
        {
            VbaConditionalCompilationIntegralWidth.Int16 =>
                value is >= short.MinValue and <= short.MaxValue,
            VbaConditionalCompilationIntegralWidth.Int32 =>
                value is >= int.MinValue and <= int.MaxValue,
            _ => true
        };

    private static VbaConditionalCompilationExpressionEvaluation Unsupported()
        => VbaConditionalCompilationExpressionEvaluation.Failure(
            VbaConditionalCompilationFailureKind.Unsupported,
            "The conditional-compilation expression uses unsupported value semantics.");

    private static VbaConditionalCompilationExpressionEvaluation Overflow()
        => VbaConditionalCompilationExpressionEvaluation.Failure(
            VbaConditionalCompilationFailureKind.Overflow,
            "The conditional-compilation integer expression overflowed the supported range.");

    private static bool IsCompleteCore(
        IReadOnlyList<VbaToken> tokens,
        int start,
        int end,
        bool allowQualifiedNames)
    {
        if (start < 0 || end > tokens.Count || start >= end)
        {
            return false;
        }

        var parser = new Parser(tokens, start, end, allowQualifiedNames);
        return parser.Parse();
    }

    private sealed class Parser(
        IReadOnlyList<VbaToken> tokens,
        int start,
        int end,
        bool allowQualifiedNames)
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

        public bool Parse() => TryParse(out _);

        public bool TryParse(out ExpressionNode expression)
        {
            if (ParseExpression(ImpPrecedence, out expression) && index == end)
            {
                return true;
            }

            expression = default!;
            return false;
        }

        private bool ParseExpression(int minimumPrecedence, out ExpressionNode expression)
        {
            if (expressionDepth >= MaximumExpressionDepth)
            {
                expression = default!;
                return false;
            }

            expressionDepth++;
            try
            {
                if (!ParsePrefix(out expression))
                {
                    return false;
                }

                while (TryGetBinaryOperator(
                        index,
                        out var precedence,
                        out var operatorTokenCount)
                    && precedence >= minimumPrecedence)
                {
                    var isRightAssociative = Matches(tokens[index], "^");
                    var operatorText = string.Concat(
                        tokens.Skip(index)
                            .Take(operatorTokenCount)
                            .Select(token => token.Text));
                    index += operatorTokenCount;
                    if (!ParseExpression(
                        precedence + (isRightAssociative ? 0 : 1),
                        out var right))
                    {
                        return false;
                    }

                    expression = new BinaryExpressionNode(operatorText, expression, right);
                }

                return true;
            }
            finally
            {
                expressionDepth--;
            }
        }

        private bool ParsePrefix(out ExpressionNode expression)
        {
            if (index >= end)
            {
                expression = default!;
                return false;
            }

            if (Matches(tokens[index], "-"))
            {
                var operatorText = tokens[index].Text;
                index++;
                if (!ParseExpression(UnarySignPrecedence, out var operand))
                {
                    expression = default!;
                    return false;
                }

                expression = new UnaryExpressionNode(operatorText, operand);
                return true;
            }

            if (Matches(tokens[index], "Not"))
            {
                var operatorText = tokens[index].Text;
                index++;
                if (!ParseExpression(NotPrecedence, out var operand))
                {
                    expression = default!;
                    return false;
                }

                expression = new UnaryExpressionNode(operatorText, operand);
                return true;
            }

            return ParsePrimary(out expression);
        }

        private bool ParsePrimary(out ExpressionNode expression)
        {
            if (index >= end)
            {
                expression = default!;
                return false;
            }

            var token = tokens[index];
            if (token.Kind == VbaTokenKind.StringLiteral)
            {
                index++;
                expression = new LiteralExpressionNode([token]);
                return token.Text.Length >= 2 && token.Text[^1] == '"';
            }

            if (token.Kind == VbaTokenKind.NumericLiteral
                || Matches(token, ".")
                || Matches(token, "&"))
            {
                var literalStart = index;
                var succeeded = ParseNumericLiteral();
                expression = succeeded
                    ? new LiteralExpressionNode(
                        tokens.Skip(literalStart).Take(index - literalStart).ToArray())
                    : default!;
                return succeeded;
            }

            if (token.Kind == VbaTokenKind.DateLiteral)
            {
                index++;
                expression = new LiteralExpressionNode([token]);
                return VbaDateLiteralSyntax.IsComplete(token);
            }

            if (IsIntrinsicConstantFunction(token))
            {
                if (index + 1 >= end
                    || !Matches(tokens[index + 1], "(")
                    || !ParseIntrinsicFunctionCall())
                {
                    expression = default!;
                    return false;
                }

                expression = new UnsupportedExpressionNode();
                return true;
            }

            if (VbaIdentifierSyntaxFacts.IsValidDeclaredName(token))
            {
                var isQualified = false;
                index++;
                while (allowQualifiedNames
                    && index + 1 < end
                    && Matches(tokens[index], ".")
                    && (AreAdjacent(tokens[index - 1], tokens[index])
                        || tokens[index - 1].Range.End.Line < tokens[index].Range.Start.Line)
                    && VbaIdentifierSyntaxFacts.IsValidDeclaredName(tokens[index + 1]))
                {
                    isQualified = true;
                    index += 2;
                }

                var hasTypeSuffix = false;
                if (index < end
                    && IsTypeSuffix(tokens[index])
                    && AreAdjacent(tokens[index - 1], tokens[index]))
                {
                    hasTypeSuffix = true;
                    index++;
                }

                expression = isQualified || hasTypeSuffix
                    ? new UnsupportedExpressionNode()
                    : new NameExpressionNode(token.Text);
                return true;
            }

            if (IsLiteralKeyword(token))
            {
                index++;
                expression = new LiteralExpressionNode([token]);
                return true;
            }

            if (!Matches(token, "("))
            {
                expression = default!;
                return false;
            }

            index++;
            if (!ParseExpression(ImpPrecedence, out expression)
                || index >= end
                || !Matches(tokens[index], ")"))
            {
                expression = default!;
                return false;
            }

            index++;
            return true;
        }

        private bool ParseIntrinsicFunctionCall()
        {
            index += 2;
            if (!ParseExpression(ImpPrecedence, out _)
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

        private bool TryGetBinaryOperator(
            int operatorIndex,
            out int precedence,
            out int operatorTokenCount)
        {
            if (operatorIndex + 1 < end
                && AreAdjacent(tokens[operatorIndex], tokens[operatorIndex + 1])
                && VbaExecutableExpressionSyntax.IsTwoTokenComparisonOperator(
                    tokens[operatorIndex],
                    tokens[operatorIndex + 1]))
            {
                precedence = ComparisonPrecedence;
                operatorTokenCount = 2;
                return true;
            }

            operatorTokenCount = 1;
            if (operatorIndex >= end)
            {
                precedence = 0;
                return false;
            }

            return TryGetBinaryPrecedence(tokens[operatorIndex], out precedence);
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
