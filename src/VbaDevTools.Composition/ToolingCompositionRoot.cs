using VbaDevTools.App.Cli;
using VbaDevTools.App.Projects;
using VbaDevTools.Infrastructure.Projects;

namespace VbaDevTools.Composition;

public static class ToolingCompositionRoot
{
    public static CommandLineApplication CreateCommandLineApplication()
        => CreateCommandLineApplication(Directory.GetCurrentDirectory());

    public static CommandLineApplication CreateCommandLineApplication(string workingDirectory)
    {
        var manifestStore = new JsonProjectManifestStore();
        var projectContextResolver = new ProjectContextResolver(manifestStore);
        return new CommandLineApplication(
            ToolingCommandCatalog.CreateDefault(),
            projectContextResolver,
            () => workingDirectory);
    }
}
