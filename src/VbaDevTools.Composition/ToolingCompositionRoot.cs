using VbaDevTools.App.Diagnostics;
using VbaDevTools.App.Cli;
using VbaDevTools.App.CommonModules;
using VbaDevTools.App.Projects;
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
        IInitialWorkbookCreator? initialWorkbookCreator = null)
    {
        var manifestStore = new JsonProjectManifestStore();
        var projectContextResolver = new ProjectContextResolver(manifestStore);
        var doctorCommand = new DoctorCommand(
            projectContextResolver,
            environmentDiagnosticPort ?? new SkippedEnvironmentDiagnosticPort());
        var newProjectCommand = new NewProjectCommand(
            manifestStore,
            initialWorkbookCreator ?? new ExcelComInitialWorkbookCreator(),
            new CommonModulesManifestReader());
        return new CommandLineApplication(
            ToolingCommandCatalog.CreateDefault(),
            projectContextResolver,
            doctorCommand,
            newProjectCommand,
            () => workingDirectory);
    }
}
