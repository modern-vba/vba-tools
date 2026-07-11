using VbaDev.App.Cli;
using VbaDev.App.Build;
using VbaDev.App.CommonModules;
using VbaDev.App.Diagnostics;
using VbaDev.App.Export;
using VbaDev.App.Import;
using VbaDev.App.Projects;
using VbaDev.App.References;
using VbaDev.App.Testing;
using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Diagnostics;
using VbaDev.Infrastructure.Projects;
using VbaDev.Infrastructure.Workbooks;

namespace VbaDev.Composition;

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
        IWorkbookModuleExporter? workbookModuleExporter = null,
        IVbaProjectReferenceResolver? vbaProjectReferenceResolver = null,
        IProjectManifestStore? projectManifestStore = null)
    {
        var manifestStore = projectManifestStore ?? new JsonProjectManifestStore();
        var commonModulesManifestReader = new CommonModulesManifestReader();
        var commonModulesService = new CommonModulesService(commonModulesManifestReader, manifestStore);
        var referenceResolver = vbaProjectReferenceResolver ?? new RegistryVbaProjectReferenceResolver();
        var referencePlanner = new VbaProjectReferencePlanner(referenceResolver);
        var referenceService = new VbaProjectReferenceService(manifestStore, referencePlanner);
        var projectContextResolver = new ProjectContextResolver(manifestStore);
        var buildAutomation = workbookBuildAutomation ?? new ExcelComWorkbookBuildAutomation();
        var doctorCommand = new DoctorCommand(
            projectContextResolver,
            commonModulesManifestReader,
            referencePlanner,
            buildAutomation,
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
            buildAutomation,
            new WorkbookReferenceNormalizer(referencePlanner));
        var buildCommand = new BuildCommand(sourcePlanner, generationPipeline);
        var publishCommand = new PublishCommand(sourcePlanner, generationPipeline);
        var testCommand = new TestCommand(
            buildCommand,
            workbookTestRunner ?? new ExcelComWorkbookTestRunner(),
            new TestResultOutputFormatter());
        var exportCommand = new ExportCommand(
            workbookModuleExporter ?? new ExcelComWorkbookModuleExporter());
        var importCommand = new ImportCommand(buildAutomation);
        return new CommandLineApplication(
            ToolingCommandCatalog.CreateDefault(
                doctorCommand,
                newProjectCommand,
                commonModulesService,
                referenceService,
                buildCommand,
                publishCommand,
                testCommand,
                exportCommand,
                importCommand),
            projectContextResolver,
            () => workingDirectory);
    }
}
