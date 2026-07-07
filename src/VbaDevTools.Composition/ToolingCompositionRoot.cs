using VbaDevTools.App.Diagnostics;
using VbaDevTools.App.Cli;
using VbaDevTools.App.Projects;
using VbaDevTools.Infrastructure.Diagnostics;
using VbaDevTools.Infrastructure.Projects;

namespace VbaDevTools.Composition;

public static class ToolingCompositionRoot
{
    public static CommandLineApplication CreateCommandLineApplication()
        => CreateCommandLineApplication(Directory.GetCurrentDirectory());

    public static CommandLineApplication CreateCommandLineApplication(
        string workingDirectory,
        IEnvironmentDiagnosticPort? environmentDiagnosticPort = null)
    {
        var manifestStore = new JsonProjectManifestStore();
        var projectContextResolver = new ProjectContextResolver(manifestStore);
        var doctorCommand = new DoctorCommand(
            projectContextResolver,
            environmentDiagnosticPort ?? new SkippedEnvironmentDiagnosticPort());
        return new CommandLineApplication(
            ToolingCommandCatalog.CreateDefault(),
            projectContextResolver,
            doctorCommand,
            () => workingDirectory);
    }
}
