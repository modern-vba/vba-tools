using System.Globalization;

namespace VbaLanguageServer.Syntax;

/// <summary>
/// Identifies the evaluated value kind of a supported ordinary VBA constant expression.
/// </summary>
internal enum VbaConstantValueKind
{
    Boolean,
    Integer,
    Floating,
    String
}

/// <summary>
/// Represents one evaluated ordinary VBA constant value used by semantic comparisons.
/// </summary>
internal readonly record struct VbaConstantValue
{
    private VbaConstantValue(
        VbaConstantValueKind kind,
        long integralValue,
        double floatingValue,
        string? stringValue)
    {
        Kind = kind;
        IntegralValue = integralValue;
        FloatingValue = floatingValue;
        StringValue = stringValue;
    }

    /// <summary>
    /// Gets the represented value kind.
    /// </summary>
    public VbaConstantValueKind Kind { get; }

    /// <summary>
    /// Gets the integral representation of a Boolean or integer value.
    /// </summary>
    public long IntegralValue { get; }

    /// <summary>
    /// Gets the binary floating-point representation of a Single or Double value.
    /// </summary>
    public double FloatingValue { get; }

    /// <summary>
    /// Gets the decoded contents of a String value.
    /// </summary>
    public string? StringValue { get; }

    /// <summary>
    /// Gets the canonical diagnostic presentation of the value.
    /// </summary>
    public string Presentation => Kind switch
    {
        VbaConstantValueKind.Boolean => IntegralValue == 0 ? "False" : "True",
        VbaConstantValueKind.Integer => IntegralValue.ToString(CultureInfo.InvariantCulture),
        VbaConstantValueKind.Floating => FloatingValue.ToString("R", CultureInfo.InvariantCulture),
        VbaConstantValueKind.String => $"\"{StringValue!.Replace("\"", "\"\"")}\"",
        _ => throw new InvalidOperationException($"Unsupported VBA constant value kind '{Kind}'.")
    };

    internal static VbaConstantValue FromBoolean(long value)
        => new(VbaConstantValueKind.Boolean, value, 0, null);

    internal static VbaConstantValue FromInteger(long value)
        => new(VbaConstantValueKind.Integer, value, 0, null);

    internal static VbaConstantValue FromFloating(double value)
        => new(VbaConstantValueKind.Floating, 0, value, null);

    internal static VbaConstantValue FromString(string value)
        => new(VbaConstantValueKind.String, 0, 0, value);

    internal bool HasSameEvaluatedValue(VbaConstantValue other)
    {
        if (Kind == other.Kind)
        {
            return Equals(other);
        }

        if (Kind == VbaConstantValueKind.Integer
            && other.Kind == VbaConstantValueKind.Floating)
        {
            return IsExactlyRepresentedBy(IntegralValue, other.FloatingValue);
        }

        return Kind == VbaConstantValueKind.Floating
            && other.Kind == VbaConstantValueKind.Integer
            && IsExactlyRepresentedBy(other.IntegralValue, FloatingValue);
    }

    private static bool IsExactlyRepresentedBy(long integralValue, double floatingValue)
    {
        var converted = (double)integralValue;
        return converted >= long.MinValue
            && converted <= long.MaxValue
            && (long)converted == integralValue
            && converted.Equals(floatingValue);
    }
}

/// <summary>
/// Represents supported evaluated evidence or an indeterminate ordinary constant expression.
/// </summary>
internal readonly record struct VbaConstantExpressionEvaluation(
    bool Succeeded,
    VbaConstantValue Value);

/// <summary>
/// Evaluates the supported closed subset of ordinary VBA constant expressions.
/// </summary>
internal static class VbaConstantExpressionEvaluator
{
    /// <summary>
    /// Evaluates a complete expression without guessing unresolved names or unsupported values.
    /// </summary>
    public static VbaConstantExpressionEvaluation Evaluate(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var tokens = VbaTokenStream.FromText(expression).Tokens
            .Where(token => token.Kind is not VbaTokenKind.Whitespace
                and not VbaTokenKind.NewLine
                and not VbaTokenKind.Comment
                and not VbaTokenKind.LineContinuation)
            .ToArray();
        if (tokens.Length == 1
            && tokens[0].Kind == VbaTokenKind.StringLiteral
            && TryDecodeStringLiteral(tokens[0].Text, out var stringValue))
        {
            return new VbaConstantExpressionEvaluation(
                true,
                VbaConstantValue.FromString(stringValue));
        }

        if (TryEvaluateFloatingLiteral(tokens, out var floatingValue))
        {
            return new VbaConstantExpressionEvaluation(
                true,
                VbaConstantValue.FromFloating(floatingValue));
        }

        if (tokens.Any(token => token.Kind == VbaTokenKind.Keyword
            && token.Text.Equals("Empty", StringComparison.OrdinalIgnoreCase)))
        {
            return new VbaConstantExpressionEvaluation(false, default);
        }

        var evaluation = VbaConstantExpressionSyntax
            .EvaluateOrdinaryConstantExpression(tokens, 0, tokens.Length);
        if (!evaluation.Succeeded)
        {
            return new VbaConstantExpressionEvaluation(false, default);
        }

        var value = evaluation.Value.Kind switch
        {
            VbaConditionalCompilationValueKind.Boolean
                => VbaConstantValue.FromBoolean(evaluation.Value.IntegralValue),
            _ => VbaConstantValue.FromInteger(evaluation.Value.IntegralValue)
        };
        return new VbaConstantExpressionEvaluation(true, value);
    }

    private static bool TryEvaluateFloatingLiteral(
        IReadOnlyList<VbaToken> tokens,
        out double value)
    {
        value = 0;
        if (!VbaConstantExpressionSyntax.IsComplete(tokens, 0, tokens.Count))
        {
            return false;
        }

        var text = string.Concat(tokens.Select(token => token.Text));
        var unsignedStart = text.Length > 0 && text[0] is '+' or '-' ? 1 : 0;
        if (unsignedStart >= text.Length || text[unsignedStart] == '&')
        {
            return false;
        }

        var suffix = text[^1] is '!' or '#' or '@' ? text[^1] : '\0';
        if (suffix == '@')
        {
            return false;
        }

        var numericText = suffix == '\0' ? text : text[..^1];
        var isFloating = suffix is '!' or '#'
            || numericText.Contains('.', StringComparison.Ordinal)
            || numericText.Contains('D', StringComparison.OrdinalIgnoreCase)
            || numericText.Contains('E', StringComparison.OrdinalIgnoreCase);
        if (!isFloating)
        {
            return false;
        }

        numericText = numericText.Replace('d', 'E').Replace('D', 'E');
        if (suffix == '!')
        {
            if (!float.TryParse(
                    numericText,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var singleValue)
                || !float.IsFinite(singleValue))
            {
                return false;
            }

            value = singleValue;
            return true;
        }

        return double.TryParse(
                numericText,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value)
            && double.IsFinite(value);
    }

    private static bool TryDecodeStringLiteral(string text, out string value)
    {
        if (text.Length < 2 || text[0] != '"' || text[^1] != '"')
        {
            value = string.Empty;
            return false;
        }

        value = text[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
        return true;
    }
}
