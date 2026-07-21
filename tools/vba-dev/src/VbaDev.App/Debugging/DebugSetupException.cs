namespace VbaDev.App.Debugging;

/// <summary>
/// Represents a launch failure that occurs before execution belongs to the VBE.
/// </summary>
public sealed class DebugSetupException : Exception
{
    /// <summary>
    /// Creates a debug setup failure with a user-facing message.
    /// </summary>
    public DebugSetupException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates a debug setup failure that preserves the underlying automation failure.
    /// </summary>
    public DebugSetupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
