using VbaDev.App.Cli;
using VbaDev.App.Diagnostics;
using VbaDev.App.Export;
using VbaDev.App.HostEvents;
using VbaDev.App.Projects;
using VbaDev.App.References;
using VbaDev.App.Testing;
using VbaDev.App.Workbooks;
using VbaDev.Cli;
using VbaDev.Composition;
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
        IHostEventCatalogAutomation? hostEventCatalogAutomation = null)
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
            hostEventCatalogAutomation);
        return generatingExecutablePath is null
            ? VbaDevCommandLine.Create(composition)
            : VbaDevCommandLine.Create(composition, generatingExecutablePath);
    }
}
