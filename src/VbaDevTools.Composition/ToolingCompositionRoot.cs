using VbaDevTools.App.Cli;

namespace VbaDevTools.Composition;

public static class ToolingCompositionRoot
{
    public static CommandLineApplication CreateCommandLineApplication()
        => new(ToolingCommandCatalog.CreateDefault());
}
