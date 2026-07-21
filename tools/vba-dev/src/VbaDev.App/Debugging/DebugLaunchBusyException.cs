namespace VbaDev.App.Debugging;

/// <summary>
/// Represents a rejected launch while another process-local debug session is active.
/// </summary>
public sealed class DebugLaunchBusyException : Exception
{
    /// <summary>
    /// Creates the process-local concurrent-launch error.
    /// </summary>
    public DebugLaunchBusyException()
        : base("A VBA debug session is already active in this adapter process.")
    {
    }
}
