namespace VbaDev.App.Workbooks;

/// <summary>
/// Runs one workbook generation callback inside a dedicated, strongly owned Excel process.
/// </summary>
public interface IWorkbookGenerationAutomation
{
    /// <summary>
    /// Opens the staged workbook, runs the supplied operation, and proves owned-process cleanup before returning.
    /// </summary>
    Task<TResult> RunAsync<TResult>(
        string workbookPath,
        WorkbookAutomationTimeouts timeouts,
        Func<IWorkbookGenerationSession, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken);
}

/// <summary>
/// Exposes the bounded operations available inside one owned workbook generation session.
/// </summary>
public interface IWorkbookGenerationSession : IVbaProjectReferenceProbeSession
{
    Task<string> GetProjectNameAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkbookModule>> GetModulesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkbookReference>> GetReferencesAsync(CancellationToken cancellationToken);

    Task<bool> RemoveReferenceAsync(string referenceName, CancellationToken cancellationToken);

    Task AddReferenceAsync(
        ResolvedVbaProjectReference reference,
        CancellationToken cancellationToken);

    /// <summary>
    /// Probes one ambiguous reference candidate against this session's current logical state.
    /// Implementations must leave that state unchanged before returning.
    /// </summary>
    Task<VbaProjectReferenceProbeAttemptResult> IVbaProjectReferenceProbeSession.TryResolveAsync(
        string referenceName,
        ResolvedVbaProjectReference candidate,
        CancellationToken cancellationToken)
        => Task.FromException<VbaProjectReferenceProbeAttemptResult>(
            new NotSupportedException(
                "This workbook generation session does not support in-session reference probing."));

    Task RemoveModuleAsync(string moduleName, CancellationToken cancellationToken);

    Task ImportModuleAsync(VbeImportSourceFile sourceFile, CancellationToken cancellationToken);

    Task ExportModuleAsync(
        string moduleName,
        string destinationPath,
        CancellationToken cancellationToken)
        => Task.FromException(new NotSupportedException(
            "This workbook generation session does not support module export."));

    /// <summary>
    /// Verifies every imported component before save, returning accepted
    /// identifier-recasing warnings while every other difference remains fatal.
    /// </summary>
    Task<VbeImportVerificationReport> VerifyAsync(CancellationToken cancellationToken);

    Task SaveAsync(CancellationToken cancellationToken);
}
