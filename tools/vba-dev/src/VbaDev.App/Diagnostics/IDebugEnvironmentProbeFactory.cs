namespace VbaDev.App.Diagnostics;

/// <summary>
/// Starts one strongly owned native Excel/VBE readiness probe.
/// </summary>
public interface IDebugEnvironmentProbeFactory
{
    /// <summary>
    /// Starts a probe session and returns ownership of all temporary process and file state.
    /// </summary>
    Task<IDebugEnvironmentProbeSession> StartAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Represents one owned native Excel/VBE readiness probe session.
/// </summary>
public interface IDebugEnvironmentProbeSession : IAsyncDisposable
{
    /// <summary>
    /// Gets diagnostics completed while the temporary workbook and owned Excel process were started.
    /// </summary>
    IReadOnlyList<DiagnosticResult> StartupDiagnostics => [];

    /// <summary>
    /// Gets the exact Excel process identifier owned by the probe.
    /// </summary>
    int ProcessId { get; }

    /// <summary>
    /// Gets whether kill-on-close Windows Job ownership was established for the process.
    /// </summary>
    bool StrongProcessOwnershipEstablished { get; }

    /// <summary>
    /// Runs the native VBE readiness stages.
    /// </summary>
    Task<IReadOnlyList<DiagnosticResult>> RunAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Identifies the exact diagnostic stage that prevented a probe session from starting.
/// </summary>
public sealed class DebugEnvironmentProbeStartException : Exception
{
    /// <summary>
    /// Creates a categorized probe-start failure.
    /// </summary>
    public DebugEnvironmentProbeStartException(
        string diagnosticName,
        string message,
        Exception? innerException = null,
        Exception? cleanupException = null,
        bool cleanupVerified = false)
        : base(message, innerException)
    {
        DiagnosticName = diagnosticName;
        CleanupException = cleanupException;
        CleanupVerified = cleanupVerified && cleanupException is null;
    }

    /// <summary>
    /// Gets the stable Doctor diagnostic name for the failed start stage.
    /// </summary>
    public string DiagnosticName { get; }

    /// <summary>
    /// Gets a cleanup failure that occurred after the categorized start failure, when present.
    /// </summary>
    public Exception? CleanupException { get; }

    /// <summary>
    /// Gets whether every process and temporary artifact created before the start failure was removed.
    /// </summary>
    public bool CleanupVerified { get; }
}
