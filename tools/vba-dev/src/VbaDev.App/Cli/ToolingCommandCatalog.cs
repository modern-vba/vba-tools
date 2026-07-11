namespace VbaDev.App.Cli;

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
            new("test", "Run VBA unit tests for the selected document.", "[options]", [.. projectOptions, Option("--format", "Test output format.", "<text|ndjson>", ["text", "ndjson"], ["-f"]), Flag("--no-build", "Skip building before running tests."), Option("--module", "Run tests from one test module.", "<name>"), Option("--procedure", "Run one test procedure. Requires --module.", "<name>")], 40, ProjectResolutionMode.Required),
            new("publish", "Publish the selected document.", "[options]", projectOptions, 50, ProjectResolutionMode.Required),
            new("export", "Export modules from a workbook into source.", "[options]", [.. projectOptions, Option("--from", "Workbook to export from; skips project resolution when supplied.", "<path>"), Option("--to", "Directory to export to; defaults to the selected document source set, or the current directory with --from.", "<dir>")], 70, ProjectResolutionMode.Required),
            new("import", "Run a path-only import of VBA sources into an existing workbook; unlike build, it does not use project.json.", "[options]", [Option("--from", "Source directory containing .bas, .cls, and .frm files.", "<dir>"), Option("--to", "Existing workbook file to update in place.", "<path>")], 75),
            new("doctor", "Check project and machine prerequisites.", "[options]", projectRootOptions, 80, ProjectResolutionMode.Optional),
            new("capabilities", "Print the command contract supported by this executable.", "[options]", [Option("--format", "Capabilities output format.", "<json>", ["json"])], 90)
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
