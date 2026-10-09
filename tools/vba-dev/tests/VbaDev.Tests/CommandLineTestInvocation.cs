using VbaDev.App.Cli;
using VbaDev.App.Build;
using VbaDev.App.Diagnostics;
using VbaDev.App.Export;
using VbaDev.App.HostEvents;
using VbaDev.App.Projects;
using VbaDev.App.References;
using VbaDev.App.Testing;
using VbaDev.App.Workbooks;
using VbaDev.Cli;
using VbaDev.Composition;
using VbaDev.Domain;
using VbaDev.Infrastructure.Diagnostics;

namespace VbaDev.Tests;

internal static class CommandLineTestInvocation
{
    public static CommandResult Run(
        this VbaDevCommandLine commandLine,
        IReadOnlyList<string> args)
        => commandLine.RunAsync(args).GetAwaiter().GetResult();

    public static async Task<CommandResult> RunAsync(
        this VbaDevCommandLine commandLine,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        var exitCode = await commandLine.InvokeAsync(
            args,
            standardOutput,
            standardError,
            cancellationToken);
        return new CommandResult(
            exitCode,
            standardOutput.ToString(),
            standardError.ToString());
    }
}

internal static class CommandLineTestFactory
{
    public static VbaDevCommandLine Create()
        => Create(Directory.GetCurrentDirectory());

    public static VbaDevCommandLine Create(
        string workingDirectory,
        IEnvironmentDiagnosticPort? environmentDiagnosticPort = null,
        IInitialWorkbookCreator? initialWorkbookCreator = null,
        IWorkbookGenerationAutomation? workbookGenerationAutomation = null,
        IWorkbookTestRunner? workbookTestRunner = null,
        IWorkbookModuleExporter? workbookModuleExporter = null,
        IVbaProjectReferenceResolver? vbaProjectReferenceResolver = null,
        IProjectManifestStore? projectManifestStore = null,
        IVbaProjectReferenceAmbiguityProbe? vbaProjectReferenceAmbiguityProbe = null,
        string? generatingExecutablePath = null,
        IExportDestinationFileOperations? exportDestinationFileOperations = null,
        IProjectMaterializationDiagnosticPort? projectMaterializationDiagnosticPort = null,
        IProjectManifestMutationCoordinator? projectManifestMutationCoordinator = null,
        IProjectManifestMutationLeaseProvider? projectManifestMutationLeaseProvider = null,
        IHostEventCatalogAutomation? hostEventCatalogAutomation = null,
        IProjectSemanticInputProvider? projectSemanticInputProvider = null,
        ISourceWorkbookAutomation? sourceWorkbookAutomation = null)
    {
        var composition = ToolingCompositionRoot.CreateApplicationComposition(
            workingDirectory,
            environmentDiagnosticPort,
            initialWorkbookCreator,
            workbookGenerationAutomation,
            workbookTestRunner,
            workbookModuleExporter,
            vbaProjectReferenceResolver,
            projectManifestStore,
            vbaProjectReferenceAmbiguityProbe,
            exportDestinationFileOperations,
            projectMaterializationDiagnosticPort ??
                new DisabledProjectMaterializationDiagnosticPort(),
            projectManifestMutationCoordinator,
            projectManifestMutationLeaseProvider,
            hostEventCatalogAutomation,
            projectSemanticInputProvider: projectSemanticInputProvider ?? new FakeProjectSemanticInputProvider(vbaProjectReferenceResolver),
            persistSourceAnalysisFailureEvidence: false,
            sourceWorkbookAutomation: sourceWorkbookAutomation ??
                (workbookGenerationAutomation is null ? null :
                    new SourceWorkbookTestAutomation(workbookGenerationAutomation)));
        return generatingExecutablePath is null
            ? VbaDevCommandLine.Create(composition)
            : VbaDevCommandLine.Create(composition, generatingExecutablePath);
    }
}

/// <summary>Adapts the external Excel test double without restoring legacy Build routing.</summary>
internal sealed class SourceWorkbookTestAutomation(
    IWorkbookGenerationAutomation automation) : ISourceWorkbookAutomation
{
    public Task<TResult> RunAsync<TResult>(
        string workbookPath,
        WorkbookAutomationTimeouts timeouts,
        Func<ISourceWorkbookSession, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(workbookPath))
            throw new FileNotFoundException($"The source workbook was not found: {workbookPath}", workbookPath);
        return automation.RunAsync(workbookPath, timeouts,
            (session, token) => operation(new SourceWorkbookTestSession(session), token),
            cancellationToken);
    }

    private sealed class SourceWorkbookTestSession(
        IWorkbookGenerationSession session) : ISourceWorkbookSession
    {
        private readonly HashSet<string> removed = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<WorkbookModule> imported = [];

        public bool WasAlreadyOpen => false;

        public SourceWorkbookSaveState SaveState { get; private set; }

        public Task<bool> IsSavedAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }

        public Task<string> GetProjectNameAsync(CancellationToken cancellationToken)
            => session.GetProjectNameAsync(cancellationToken);

        public async Task<IReadOnlyList<WorkbookModule>> GetModulesAsync(CancellationToken cancellationToken)
            => (await session.GetModulesAsync(cancellationToken).ConfigureAwait(false))
                .Where(module => !removed.Contains(module.Name)).Concat(imported).ToArray();

        public async Task<IReadOnlyList<WorkbookReference>> GetReferencesAsync(CancellationToken cancellationToken)
            => (await session.GetReferencesAsync(cancellationToken).ConfigureAwait(false)).ToArray();

        public Task<bool> RemoveReferenceAsync(string name, CancellationToken cancellationToken)
            => session.RemoveReferenceAsync(name, cancellationToken);

        public Task AddReferenceAsync(ResolvedVbaProjectReference reference, CancellationToken cancellationToken)
            => session.AddReferenceAsync(reference, cancellationToken);

        public Task<VbaProjectReferenceProbeAttemptResult> TryResolveAsync(
            string name, ResolvedVbaProjectReference candidate, CancellationToken cancellationToken)
            => ((IVbaProjectReferenceProbeSession)session).TryResolveAsync(name, candidate, cancellationToken);

        public async Task RemoveModuleAsync(string name, CancellationToken cancellationToken)
        {
            await session.RemoveModuleAsync(name, cancellationToken).ConfigureAwait(false);
            removed.Add(name);
            imported.RemoveAll(module => module.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        public async Task ImportModuleAsync(VbeImportSourceFile source, CancellationToken cancellationToken)
        {
            await session.ImportModuleAsync(source, cancellationToken).ConfigureAwait(false);
            imported.Add(new WorkbookModule(source.ImportVerification.ComponentName, source.Kind switch
            {
                VbaSourceKind.StandardModule => WorkbookModuleKind.StandardModule,
                VbaSourceKind.ClassModule => WorkbookModuleKind.ClassModule,
                VbaSourceKind.Form => WorkbookModuleKind.Form,
                _ => throw new ArgumentOutOfRangeException(nameof(source))
            }));
        }

        public async Task ExportModuleAsync(string name, string destination, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var module = (await GetModulesAsync(cancellationToken).ConfigureAwait(false))
                .Single(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var header = module.Kind switch
            {
                WorkbookModuleKind.ClassModule => "VERSION 1.0 CLASS\nBEGIN\nEND\n",
                WorkbookModuleKind.Form => $"VERSION 5.00\nBegin VB.UserForm {name}\n" +
                    $"    OleObjectBlob = \"{name}.frx\":0000\nEnd\n",
                _ => string.Empty
            };
            File.WriteAllText(destination, header + $"Attribute VB_Name = \"{name}\"\n",
                new System.Text.UTF8Encoding(false));
            if (module.Kind == WorkbookModuleKind.Form)
                File.WriteAllBytes(Path.ChangeExtension(destination, ".frx"), [1, 2, 3]);
        }

        public Task<VbeImportVerificationReport> VerifyAsync(CancellationToken cancellationToken)
            => session.VerifyAsync(cancellationToken);

        public async Task SaveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SaveState = SourceWorkbookSaveState.Unknown;
            await session.SaveAsync(cancellationToken).ConfigureAwait(false);
            SaveState = SourceWorkbookSaveState.Saved;
        }
    }
}
