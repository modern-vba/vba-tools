namespace VbaDev.App.Workbooks;

/// <summary>
/// Bounded VBA-state operations without workbook persistence or Excel lifetime authority.
/// </summary>
internal interface ISourceWorkbookReplacementSession : IVbaProjectReferenceProbeSession
{
    Task<string> GetProjectNameAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkbookModule>> GetModulesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkbookReference>> GetReferencesAsync(CancellationToken cancellationToken);

    Task<bool> RemoveReferenceAsync(string referenceName, CancellationToken cancellationToken);

    Task AddReferenceAsync(ResolvedVbaProjectReference reference, CancellationToken cancellationToken);

    Task RemoveModuleAsync(string moduleName, CancellationToken cancellationToken);

    Task ImportModuleAsync(VbeImportSourceFile sourceFile, CancellationToken cancellationToken);

    Task ExportModuleAsync(string moduleName, string destinationPath, CancellationToken cancellationToken);

    Task<VbeImportVerificationReport> VerifyAsync(CancellationToken cancellationToken);
}

/// <summary>Restricts an existing bounded session to VBA-state replacement operations.</summary>
internal sealed class SourceWorkbookReplacementSessionView(
    IWorkbookGenerationSession session) : ISourceWorkbookReplacementSession
{
    public Task<string> GetProjectNameAsync(CancellationToken cancellationToken)
        => session.GetProjectNameAsync(cancellationToken);

    public Task<IReadOnlyList<WorkbookModule>> GetModulesAsync(CancellationToken cancellationToken)
        => session.GetModulesAsync(cancellationToken);

    public Task<IReadOnlyList<WorkbookReference>> GetReferencesAsync(CancellationToken cancellationToken)
        => session.GetReferencesAsync(cancellationToken);

    public Task<bool> RemoveReferenceAsync(string referenceName, CancellationToken cancellationToken)
        => session.RemoveReferenceAsync(referenceName, cancellationToken);

    public Task AddReferenceAsync(ResolvedVbaProjectReference reference, CancellationToken cancellationToken)
        => session.AddReferenceAsync(reference, cancellationToken);

    public Task RemoveModuleAsync(string moduleName, CancellationToken cancellationToken)
        => session.RemoveModuleAsync(moduleName, cancellationToken);

    public Task ImportModuleAsync(VbeImportSourceFile sourceFile, CancellationToken cancellationToken)
        => session.ImportModuleAsync(sourceFile, cancellationToken);

    public Task ExportModuleAsync(string moduleName, string destinationPath, CancellationToken cancellationToken)
        => session.ExportModuleAsync(moduleName, destinationPath, cancellationToken);

    public Task<VbeImportVerificationReport> VerifyAsync(CancellationToken cancellationToken)
        => session.VerifyAsync(cancellationToken);

    public Task<VbaProjectReferenceProbeAttemptResult> TryResolveAsync(
        string referenceName, ResolvedVbaProjectReference candidate, CancellationToken cancellationToken)
        => ((IVbaProjectReferenceProbeSession)session)
            .TryResolveAsync(referenceName, candidate, cancellationToken);
}
