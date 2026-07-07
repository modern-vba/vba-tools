namespace VbaDevTools.App.Cli;

public static class ToolingCommandCatalog
{
    public static IReadOnlyList<ToolingCommandDefinition> CreateDefault()
    {
        var projectRootOptions = new[]
        {
            Option("--project", "Project root containing project.json.", "<path>")
        };
        var projectOptions = new[]
        {
            Option("--project", "Project root containing project.json.", "<path>"),
            Option("--document", "Document name from the project manifest.", "<name>")
        };

        return
        [
            new("new", "Create a workbook-backed VBA project.", "<project-name> [--document <name>]", [Option("--document", "Document name. Defaults to the project name.", "<name>")], 10),
            new("add", "Copy CommonModules entries into the selected document source set.", "[modules...] [options]", projectOptions, 20, ProjectResolutionMode.Required),
            new("build", "Build the selected document into bin output.", "[options]", projectOptions, 30, ProjectResolutionMode.Required),
            new("test", "Run VBA unit tests for the selected built document.", "[options]", [.. projectOptions, Option("--format", "Test output format.", "<ndjson|text>", ["ndjson", "text"]), Flag("--build", "Build before running tests.")], 40, ProjectResolutionMode.Required),
            new("publish", "Publish the selected document.", "[options]", projectOptions, 50, ProjectResolutionMode.Required),
            new("update", "Update installed CommonModules entries.", "[options]", projectRootOptions, 60, ProjectResolutionMode.Required),
            new("export", "Export modules from a workbook into source.", "[options]", [.. projectOptions, Option("--from", "Workbook to export from.", "<path>"), Option("--to", "Directory to export to.", "<dir>")], 70, ProjectResolutionMode.Required),
            new("doctor", "Check project and machine prerequisites.", "[options]", projectRootOptions, 80, ProjectResolutionMode.Optional)
        ];
    }

    private static CommandOptionDefinition Option(
        string name,
        string description,
        string valueDisplay,
        IReadOnlyList<string>? allowedValues = null)
        => new(name, description, RequiresValue: true, valueDisplay, allowedValues);

    private static CommandOptionDefinition Flag(string name, string description)
        => new(name, description, RequiresValue: false);
}
