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
            Option("--document", "Document name from the project manifest.", "<name>", aliases: ["-d"])
        };

        return
        [
            new("new excel", "Create an Excel workbook-backed VBA project.", "[options]", [Option("--name", "Project and document base name.", "<name>", aliases: ["-n"]), Option("--output", "Project root output directory.", "<dir>", aliases: ["-o"])], 10),
            new("common-module add", "Copy CommonModules entries into the selected document source set.", "[modules...] [options]", [.. projectOptions, Flag("--force", "Overwrite conflicting source files.")], 20, ProjectResolutionMode.Required),
            new("common-module list", "List CommonModules entries for the selected document.", "[options]", [.. projectOptions, Option("--format", "CommonModules output format.", "<text|json>", ["text", "json"], ["-f"])], 21, ProjectResolutionMode.Required),
            new("common-module update", "Update installed CommonModules entries.", "[options]", projectRootOptions, 22, ProjectResolutionMode.Required),
            new("reference add", "Add VBA project references to the selected document manifest.", "[references...] [options]", projectOptions, 30, ProjectResolutionMode.Required),
            new("reference list", "List VBA project references for the selected document.", "[options]", [.. projectOptions, Option("--format", "Reference output format.", "<text|json>", ["text", "json"], ["-f"])], 31, ProjectResolutionMode.Required),
            new("reference remove", "Remove VBA project references from the selected document manifest.", "[references...] [options]", projectOptions, 32, ProjectResolutionMode.Required),
            new("build", "Build the selected document into bin output.", "[options]", projectOptions, 30, ProjectResolutionMode.Required),
            new("test", "Run VBA unit tests for the selected document.", "[options]", [.. projectOptions, Option("--format", "Test output format.", "<text|ndjson>", ["text", "ndjson"], ["-f"]), Flag("--no-build", "Skip building before running tests.")], 40, ProjectResolutionMode.Required),
            new("publish", "Publish the selected document.", "[options]", projectOptions, 50, ProjectResolutionMode.Required),
            new("export", "Export modules from a workbook into source.", "[options]", [.. projectOptions, Option("--from", "Workbook to export from.", "<path>"), Option("--to", "Directory to export to.", "<dir>")], 70, ProjectResolutionMode.Required),
            new("doctor", "Check project and machine prerequisites.", "[options]", projectRootOptions, 80, ProjectResolutionMode.Optional)
        ];
    }

    private static CommandOptionDefinition Option(
        string name,
        string description,
        string valueDisplay,
        IReadOnlyList<string>? allowedValues = null,
        IReadOnlyList<string>? aliases = null)
        => new(name, description, RequiresValue: true, valueDisplay, allowedValues, aliases);

    private static CommandOptionDefinition Flag(string name, string description)
        => new(name, description, RequiresValue: false);
}
