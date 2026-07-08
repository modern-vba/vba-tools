using VbaDevTools.App.Cli;
using VbaDevTools.App.Build;
using VbaDevTools.App.CommonModules;
using VbaDevTools.App.Diagnostics;
using VbaDevTools.App.Export;
using VbaDevTools.App.Projects;
using VbaDevTools.App.References;
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
        IWorkbookTestRunner? workbookTestRunner = null,
        IWorkbookModuleExporter? workbookModuleExporter = null)
    {
        var manifestStore = new JsonProjectManifestStore();
        var commonModulesManifestReader = new CommonModulesManifestReader();
        var commonModulesService = new CommonModulesService(commonModulesManifestReader, manifestStore);
        var referenceService = new VbaProjectReferenceService(manifestStore);
        var projectContextResolver = new ProjectContextResolver(manifestStore);
        var doctorCommand = new DoctorCommand(
            projectContextResolver,
            commonModulesManifestReader,
            environmentDiagnosticPort ?? new SkippedEnvironmentDiagnosticPort());
        var newProjectCommand = new NewProjectCommand(
            manifestStore,
            initialWorkbookCreator ?? new ExcelComInitialWorkbookCreator(),
            commonModulesManifestReader,
            commonModulesService);
        var sourcePlanner = new WorkbookSourcePlanner(
            commonModulesManifestReader,
            commonModulesService);
        var generationPipeline = new WorkbookGenerationPipeline(
            workbookBuildAutomation ?? new ExcelComWorkbookBuildAutomation());
        var buildCommand = new BuildCommand(sourcePlanner, generationPipeline);
        var publishCommand = new PublishCommand(sourcePlanner, generationPipeline);
        var testCommand = new TestCommand(
            buildCommand,
            workbookTestRunner ?? new ExcelComWorkbookTestRunner(),
            new TestResultOutputFormatter());
        var exportCommand = new ExportCommand(
            workbookModuleExporter ?? new ExcelComWorkbookModuleExporter());
        return new CommandLineApplication(
            ToolingCommandCatalog.CreateDefault(),
            projectContextResolver,
            doctorCommand,
            newProjectCommand,
            commonModulesService,
            referenceService,
            buildCommand,
            publishCommand,
            testCommand,
            exportCommand,
            () => workingDirectory);
    }
}
