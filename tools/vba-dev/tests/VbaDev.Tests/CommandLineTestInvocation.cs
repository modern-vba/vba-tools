using VbaDev.App.Cli;
using VbaDev.App.Debugging;
using VbaDev.App.Diagnostics;
using VbaDev.App.Export;
using VbaDev.App.Projects;
using VbaDev.App.References;
using VbaDev.App.Testing;
using VbaDev.App.Workbooks;
using VbaDev.Cli;
using VbaDev.Composition;

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
        IWorkbookBuildAutomation? workbookBuildAutomation = null,
        IWorkbookTestRunner? workbookTestRunner = null,
        IWorkbookModuleExporter? workbookModuleExporter = null,
        IVbaProjectReferenceResolver? vbaProjectReferenceResolver = null,
        IProjectManifestStore? projectManifestStore = null,
        IDebugEnvironmentProbeFactory? debugEnvironmentProbeFactory = null,
        IVbaProjectReferenceAmbiguityProbe? vbaProjectReferenceAmbiguityProbe = null,
        string? generatingExecutablePath = null)
    {
        var composition = ToolingCompositionRoot.CreateApplicationComposition(
            workingDirectory,
            environmentDiagnosticPort,
            initialWorkbookCreator,
            workbookBuildAutomation,
            workbookTestRunner,
            workbookModuleExporter,
            vbaProjectReferenceResolver,
            projectManifestStore,
            debugEnvironmentProbeFactory,
            vbaProjectReferenceAmbiguityProbe);
        return generatingExecutablePath is null
            ? VbaDevCommandLine.Create(composition)
            : VbaDevCommandLine.Create(composition, generatingExecutablePath);
    }
}
