namespace VbaDebugAdapter.Infrastructure;

public sealed record DebugSessionId
{
    private DebugSessionId(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static DebugSessionId Parse(string value, string? parameterName = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!IsCanonicalHex32(value))
        {
            throw new ArgumentException(
                "The adapter session ID must contain 32 lowercase hexadecimal characters.",
                parameterName ?? nameof(value));
        }

        return new DebugSessionId(value);
    }

    public static bool TryParse(string? value, out DebugSessionId? sessionId)
    {
        if (value is not null && IsCanonicalHex32(value))
        {
            sessionId = new DebugSessionId(value);
            return true;
        }

        sessionId = null;
        return false;
    }

    public override string ToString() => Value;

    private static bool IsCanonicalHex32(string value)
        => value.Length == 32 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed record DebugGenerationId
{
    private DebugGenerationId(int value)
    {
        Value = value;
    }

    public static DebugGenerationId Initial { get; } = new(0);

    public int Value { get; }

    internal string WorkspaceDirectoryName => $"generation-{Value:D10}";

    public static DebugGenerationId FromValue(int value, string? parameterName = null)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName ?? nameof(value),
                value,
                "The debug generation must be nonnegative.");
        }

        return value == 0 ? Initial : new DebugGenerationId(value);
    }

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record DebugRestartPreparationId
{
    private DebugRestartPreparationId(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static DebugRestartPreparationId Parse(
        string value,
        string? parameterName = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!IsCanonicalHex32(value))
        {
            throw new ArgumentException(
                "The VBA restart preparation ID must contain 32 lowercase hexadecimal characters.",
                parameterName ?? nameof(value));
        }

        return new DebugRestartPreparationId(value);
    }

    public override string ToString() => Value;

    private static bool IsCanonicalHex32(string value)
        => value.Length == 32 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed record DebugRestartGeneration : IComparable<DebugRestartGeneration>
{
    private DebugRestartGeneration(int value)
    {
        Value = value;
    }

    public static DebugRestartGeneration Initial { get; } = new(0);

    public int Value { get; }

    public static DebugRestartGeneration FromValue(
        int value,
        string? parameterName = null)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName ?? nameof(value),
                value,
                "The restart generation must be nonnegative.");
        }

        return value == 0 ? Initial : new DebugRestartGeneration(value);
    }

    public DebugRestartGeneration Next()
    {
        if (Value == int.MaxValue)
        {
            throw new InvalidOperationException(
                "The VBA restart generation is exhausted.");
        }

        return new DebugRestartGeneration(Value + 1);
    }

    public static DebugRestartGeneration Max(
        DebugRestartGeneration left,
        DebugRestartGeneration right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return left.Value >= right.Value ? left : right;
    }

    public int CompareTo(DebugRestartGeneration? other)
        => other is null ? 1 : Value.CompareTo(other.Value);

    public override string ToString()
        => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
