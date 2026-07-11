using System.Text.Json;
using System.Text.Json.Serialization;
using VbaDev.App.Build;
using VbaDev.App.CommonModules;
using VbaDev.App.Diagnostics;
using VbaDev.App.Export;
using VbaDev.App.Import;
using VbaDev.App.Projects;
using VbaDev.App.References;
using VbaDev.App.Testing;

namespace VbaDev.App.Cli;

public static class ToolingCommandCatalog
{
    public static IReadOnlyList<ToolingCommandDefinition> CreateDefault(
        DoctorCommand doctorCommand,
        NewProjectCommand newProjectCommand,
        CommonModulesService commonModulesService,
        VbaProjectReferenceService referenceService,
        BuildCommand buildCommand,
        PublishCommand publishCommand,
        TestCommand testCommand,
        ExportCommand exportCommand,
        ImportCommand importCommand)
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
            new("new excel", "Create an Excel workbook-backed VBA project.", "[options]", [Option("--name", "Project and document base name.", "<name>", aliases: ["-n"]), Option("--output", "Project root output directory.", "<dir>", aliases: ["-o"])], 10, ProjectResolutionMode.None, invocation => newProjectCommand.Run(new NewProjectCommandRequest(
                invocation.GetOption("--name"),
                null,
                invocation.GetOption("--output"),
                invocation.WorkingDirectory))),
            new("common-module add", "Copy CommonModules entries into the selected document source set.", "[modules...] [options]", [.. projectOptions, Flag("--force", "Overwrite conflicting source files.")], 20, ProjectResolutionMode.DocumentRequired, invocation => commonModulesService.Add(
                invocation.RequireContext(),
                invocation.Positionals,
                invocation.HasOption("--force"))),
            new("common-module list", "List CommonModules entries for the selected document.", "[options]", [.. projectOptions, Option("--format", "CommonModules output format.", "<text|json>", ["text", "json"], ["-f"])], 21, ProjectResolutionMode.DocumentRequired, invocation => commonModulesService.List(
                invocation.RequireContext(),
                invocation.GetOption("--format") ?? "text")),
            new("common-module update", "Update installed CommonModules entries.", "[options]", projectRootOptions, 22, ProjectResolutionMode.ProjectRequired, invocation => commonModulesService.Update(invocation.RequireProject())),
            new("reference add", "Add VBA project references to the selected document manifest.", "[references...] [options]", projectOptions, 30, ProjectResolutionMode.DocumentRequired, invocation => referenceService.Add(invocation.RequireContext(), invocation.Positionals)),
            new("reference list", "List VBA project references for the selected document.", "[options]", [.. projectOptions, Option("--format", "Reference output format.", "<text|json>", ["text", "json"], ["-f"])], 31, ProjectResolutionMode.DocumentRequired, invocation => referenceService.List(
                invocation.RequireContext(),
                invocation.GetOption("--format") ?? "text")),
            new("reference remove", "Remove VBA project references from the selected document manifest.", "[references...] [options]", projectOptions, 32, ProjectResolutionMode.DocumentRequired, invocation => referenceService.Remove(invocation.RequireContext(), invocation.Positionals)),
            new("build", "Build the selected document into bin output.", "[options]", projectOptions, 30, ProjectResolutionMode.DocumentRequired, invocation => buildCommand.Run(invocation.RequireContext())),
            new("test", "Run VBA unit tests for the selected document.", "[options]", [.. projectOptions, Option("--format", "Test output format.", "<text|ndjson>", ["text", "ndjson"], ["-f"]), Flag("--no-build", "Skip building before running tests."), Option("--module", "Run tests from one test module.", "<name>"), Option("--procedure", "Run one test procedure. Requires --module.", "<name>")], 40, ProjectResolutionMode.DocumentRequired, invocation => RunTestCommand(testCommand, invocation), OutputSchemaVersion: "1.1"),
            new("publish", "Publish the selected document.", "[options]", projectOptions, 50, ProjectResolutionMode.DocumentRequired, invocation => publishCommand.Run(invocation.RequireContext())),
            new("export", "Export modules from a workbook into source.", "[options]", [.. projectOptions, Option("--from", "Workbook to export from; skips project resolution when supplied.", "<path>"), Option("--to", "Directory to export to; defaults to the selected document source set, or the current directory with --from.", "<dir>")], 70, ProjectResolutionMode.ExplicitWorkbookOrDocumentRequired, invocation => RunExportCommand(exportCommand, invocation)),
            new("import", "Run a path-only import of VBA sources into an existing workbook; unlike build, it does not use project.json.", "[options]", [Option("--from", "Source directory containing .bas, .cls, and .frm files.", "<dir>"), Option("--to", "Existing workbook file to update in place.", "<path>")], 75, ProjectResolutionMode.None, invocation => importCommand.Run(new ImportCommandRequest(
                invocation.GetOption("--from"),
                invocation.GetOption("--to"),
                invocation.WorkingDirectory))),
            new("doctor", "Check project and machine prerequisites.", "[options]", projectRootOptions, 80, ProjectResolutionMode.ProjectOptional, invocation => doctorCommand.Run(new DoctorCommandRequest(
                invocation.GetOption("--project"),
                invocation.WorkingDirectory))),
            new("capabilities", "Print the command contract supported by this executable.", "[options]", [Option("--format", "Capabilities output format.", "<json>", ["json"])], 90, ProjectResolutionMode.None, RenderCapabilities)
        ];
    }

    private static CommandResult RunTestCommand(TestCommand testCommand, ToolingCommandInvocation invocation)
    {
        try
        {
            var moduleName = invocation.GetOption("--module");
            var procedureName = invocation.GetOption("--procedure");
            if (!string.IsNullOrWhiteSpace(procedureName) && string.IsNullOrWhiteSpace(moduleName))
            {
                return CommandResult.UsageError("--procedure requires --module.");
            }

            var format = CommandDefaultResolver.ResolveTestFormat(
                invocation.RequireContext().Manifest,
                invocation.GetOption("--format"));
            return testCommand.Run(
                invocation.RequireContext(),
                new TestCommandRequest(
                    format,
                    !invocation.HasOption("--no-build"),
                    new WorkbookTestSelector(
                        string.IsNullOrWhiteSpace(moduleName) ? null : moduleName,
                        string.IsNullOrWhiteSpace(procedureName) ? null : procedureName)));
        }
        catch (ProjectManifestException ex)
        {
            return CommandResult.UsageError(ex.Message);
        }
    }

    private static CommandResult RunExportCommand(ExportCommand exportCommand, ToolingCommandInvocation invocation)
    {
        var request = new ExportCommandRequest(
            invocation.GetOption("--from"),
            invocation.GetOption("--to"),
            invocation.WorkingDirectory);

        return invocation.HasOption("--from")
            ? exportCommand.RunExplicit(request)
            : exportCommand.Run(invocation.RequireContext(), request);
    }

    private static CommandResult RenderCapabilities(ToolingCommandInvocation invocation)
    {
        var format = invocation.GetOption("--format") ?? "json";
        if (!format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            return CommandResult.UsageError($"Unsupported value '{format}' for --format.");
        }

        var capabilities = new ToolCapabilities(
            ToolVersion: typeof(ToolingCommandCatalog).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            ContractVersion: "1.0",
            Commands: invocation.Commands.Values
                .Where(command => !command.Name.Equals("capabilities", StringComparison.OrdinalIgnoreCase))
                .OrderBy(command => command.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    command => command.Name,
                    command => new CommandCapability(OutputSchemaVersion: command.OutputSchemaVersion),
                    StringComparer.OrdinalIgnoreCase));

        return CommandResult.Success(JsonSerializer.Serialize(capabilities, CapabilitiesJsonOptions) + Environment.NewLine);
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

    private sealed record ToolCapabilities(
        string ToolVersion,
        string ContractVersion,
        IReadOnlyDictionary<string, CommandCapability> Commands);

    private sealed record CommandCapability(string OutputSchemaVersion);

    private static readonly JsonSerializerOptions CapabilitiesJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
