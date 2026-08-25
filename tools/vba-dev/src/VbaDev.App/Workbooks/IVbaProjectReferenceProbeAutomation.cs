namespace VbaDev.App.Workbooks;

/// <summary>
/// Owns one hidden Excel/VBE process while an ambiguity state machine probes references.
/// </summary>
public interface IVbaProjectReferenceProbeAutomation
{
    /// <summary>
    /// Runs one operation through a single bounded, invocation-owned probe process.
    /// </summary>
    Task<TResult> RunAsync<TResult>(
        VbaProjectReferenceProbeBaseline baseline,
        WorkbookAutomationTimeouts timeouts,
        Func<IVbaProjectReferenceProbeSession, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken);
}

/// <summary>
/// Probes candidates against one repeatable logical workbook baseline.
/// </summary>
public interface IVbaProjectReferenceProbeSession
{
    /// <summary>
    /// Attempts one registered GUID/version candidate and restores the same logical baseline.
    /// </summary>
    Task<VbaProjectReferenceProbeAttemptResult> TryResolveAsync(
        string referenceName,
        ResolvedVbaProjectReference candidate,
        CancellationToken cancellationToken);
}

/// <summary>
/// Identifies whether VBE accepted or conclusively rejected one candidate attempt.
/// </summary>
public enum VbaProjectReferenceProbeAttemptOutcome
{
    Accepted,
    Rejected
}

/// <summary>
/// Contains the authoritative VBE identity returned by one accepted attempt.
/// </summary>
public sealed record VbaProjectReferenceProbeAttemptResult(
    VbaProjectReferenceProbeAttemptOutcome Outcome,
    ResolvedVbaProjectReference? Reference)
{
    /// <summary>
    /// Creates an accepted attempt with its authoritative returned identity.
    /// </summary>
    public static VbaProjectReferenceProbeAttemptResult Accepted(
        ResolvedVbaProjectReference reference)
        => new(VbaProjectReferenceProbeAttemptOutcome.Accepted, reference);

    /// <summary>
    /// Creates a conclusive candidate rejection.
    /// </summary>
    public static VbaProjectReferenceProbeAttemptResult Rejected()
        => new(VbaProjectReferenceProbeAttemptOutcome.Rejected, null);
}

/// <summary>
/// Reports a classified candidate-local probe failure and whether later VBE work remains safe.
/// </summary>
public sealed class VbaProjectReferenceProbeAttemptException : Exception
{
    /// <summary>
    /// Creates a classified probe-attempt failure.
    /// </summary>
    public VbaProjectReferenceProbeAttemptException(
        string reasonCode,
        string message,
        bool processTrusted,
        Exception? innerException = null,
        object? partialResult = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        ReasonCode = reasonCode;
        ProcessTrusted = processTrusted;
        PartialResult = partialResult;
    }

    /// <summary>
    /// Gets the stable public unverified reason code.
    /// </summary>
    public string ReasonCode { get; }

    /// <summary>
    /// Gets whether later attempts may safely reuse the same owned process.
    /// </summary>
    public bool ProcessTrusted { get; }

    /// <summary>
    /// Gets the completed operation result retained when final lifecycle cleanup fails.
    /// </summary>
    public object? PartialResult { get; }
}

/// <summary>
/// Reports that the selected workbook baseline could not be prepared for probing.
/// </summary>
public sealed class VbaProjectReferenceProbeBaselineException : Exception
{
    /// <summary>
    /// Creates a baseline-preparation failure.
    /// </summary>
    public VbaProjectReferenceProbeBaselineException(
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
