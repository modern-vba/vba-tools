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
public interface IWorkbookGenerationSession
{
    Task<IReadOnlyList<WorkbookModule>> GetModulesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkbookReference>> GetReferencesAsync(CancellationToken cancellationToken);

    Task<bool> RemoveReferenceAsync(string referenceName, CancellationToken cancellationToken);

    Task AddReferenceAsync(
        ResolvedVbaProjectReference reference,
        CancellationToken cancellationToken);

    Task RemoveModuleAsync(string moduleName, CancellationToken cancellationToken);

    Task ImportModuleAsync(VbaSourceFile sourceFile, CancellationToken cancellationToken);

    /// <summary>
    /// Runs the generation verification boundary before save. Exact imported-code verification is added separately.
    /// </summary>
    Task VerifyAsync(CancellationToken cancellationToken);

    Task SaveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Adapts the legacy synchronous workbook port for deterministic application tests.
/// Production composition uses a native <see cref="IWorkbookGenerationAutomation"/> implementation.
/// </summary>
internal sealed class SynchronousWorkbookGenerationAutomation(
    IWorkbookBuildAutomation automation) : IWorkbookGenerationAutomation
{
    public async Task<TResult> RunAsync<TResult>(
        string workbookPath,
        WorkbookAutomationTimeouts timeouts,
        Func<IWorkbookGenerationSession, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var session = automation.OpenWorkbook(workbookPath, cancellationToken);
        return await operation(
                new SynchronousWorkbookGenerationSession(session),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private sealed class SynchronousWorkbookGenerationSession(
        IWorkbookBuildSession session) : IWorkbookGenerationSession
    {
        public Task<IReadOnlyList<WorkbookModule>> GetModulesAsync(CancellationToken cancellationToken)
            => RunAsync(session.GetModules, cancellationToken);

        public Task<IReadOnlyList<WorkbookReference>> GetReferencesAsync(CancellationToken cancellationToken)
            => RunAsync(session.GetReferences, cancellationToken);

        public Task<bool> RemoveReferenceAsync(
            string referenceName,
            CancellationToken cancellationToken)
            => RunAsync(() => session.RemoveReference(referenceName), cancellationToken);

        public Task AddReferenceAsync(
            ResolvedVbaProjectReference reference,
            CancellationToken cancellationToken)
            => RunAsync(() => session.AddReference(reference), cancellationToken);

        public Task RemoveModuleAsync(string moduleName, CancellationToken cancellationToken)
            => RunAsync(() => session.RemoveModule(moduleName), cancellationToken);

        public Task ImportModuleAsync(VbaSourceFile sourceFile, CancellationToken cancellationToken)
            => RunAsync(() => session.ImportModule(sourceFile), cancellationToken);

        public Task VerifyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task SaveAsync(CancellationToken cancellationToken)
            => RunAsync(session.Save, cancellationToken);

        private static Task<T> RunAsync<T>(Func<T> operation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(operation());
        }

        private static Task RunAsync(Action operation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            operation();
            return Task.CompletedTask;
        }
    }
}
