using VbaDevTools.App.Cli;
using VbaDevTools.App.Build;
using VbaDevTools.App.CommonModules;
using VbaDevTools.App.Diagnostics;
using VbaDevTools.App.Projects;
using VbaDevTools.App.Testing;
using VbaDevTools.App.Workbooks;
using VbaDevTools.Infrastructure.Diagnostics;
using VbaDevTools.Infrastructure.Projects;
using VbaDevTools.Infrastructure.Workbooks;

namespace VbaDevTools.Composition;

public static class ToolingCompositionRoot
{
    public static CommandLineApplication CreateCommandLineApplication()
        => CreateCommandLineApplication(Directory.GetCurrentDirectory());

    public static CommandLineApplication CreateCommandLineApplication(
        string workingDirectory,
        IEnvironmentDiagnosticPort? environmentDiagnosticPort = null,
        IInitialWorkbookCreator? initialWorkbookCreator = null,
        IWorkbookBuildAutomation? workbookBuildAutomation = null,
        IWorkbookTestRunner? workbookTestRunner = null)
    {
        var manifestStore = new JsonProjectManifestStore();
        var commonModulesManifestReader = new CommonModulesManifestReader();
        var commonModulesService = new CommonModulesService(commonModulesManifestReader);
        var projectContextResolver = new ProjectContextResolver(manifestStore);
        var doctorCommand = new DoctorCommand(
            projectContextResolver,
            environmentDiagnosticPort ?? new SkippedEnvironmentDiagnosticPort());
        var newProjectCommand = new NewProjectCommand(
            manifestStore,
            initialWorkbookCreator ?? new ExcelComInitialWorkbookCreator(),
            commonModulesManifestReader);
        var buildCommand = new BuildCommand(
            commonModulesManifestReader,
            commonModulesService,
            workbookBuildAutomation ?? new ExcelComWorkbookBuildAutomation());
        var testCommand = new TestCommand(
            buildCommand,
            workbookTestRunner ?? new ExcelComWorkbookTestRunner(),
            new TestResultOutputFormatter());
        return new CommandLineApplication(
            ToolingCommandCatalog.CreateDefault(),
            projectContextResolver,
            doctorCommand,
            newProjectCommand,
            commonModulesService,
            buildCommand,
            testCommand,
            () => workingDirectory);
    }
}
