using VbaDev.App.Build;
using VbaDev.App.CommonModules;
using VbaDev.App.Diagnostics;
using VbaDev.App.Export;
using VbaDev.App.Import;
using VbaDev.App.Projects;
using VbaDev.App.References;
using VbaDev.App.Testing;

namespace VbaDev.App.Cli;

/// <summary>
/// Builds the default command contracts and handlers for the VbaDev executable.
/// </summary>
public static class ToolingCommandCatalog
{
    /// <summary>
    /// Creates the command contract list advertised by help and capabilities output.
    /// </summary>
    /// <returns>The default commands supported by this executable.</returns>
    public static IReadOnlyList<ToolingCommandContract> CreateDefaultContracts()
    {
        var projectRootOptions = new[]
        {
            Option("--project", "Project root containing vba-project.json.", "<path>")
        };
        var projectOptions = new[]
        {
            Option("--project", "Project root containing vba-project.json.", "<path>"),
            Option("--document", "Document name from the project manifest.", "<name>", aliases: ["-d"])
        };

        return
        [
            new("new excel", "Create an Excel workbook-backed VBA project.", "[options]", [Option("--name", "Project and document base name.", "<name>", aliases: ["-n"]), Option("--output", "Project root output directory.", "<dir>", aliases: ["-o"])], 10, ToolingCommandContextPolicy.None),
            new("common-module add", "Copy CommonModules entries into the selected document source set.", "[modules...] [options]", [.. projectOptions, Flag("--force", "Overwrite conflicting source files.")], 20, ToolingCommandContextPolicy.DocumentRequired),
            new("common-module list", "List CommonModules entries for the selected document.", "[options]", [.. projectOptions, Option("--format", "CommonModules output format.", "<text|json>", ["text", "json"], ["-f"])], 21, ToolingCommandContextPolicy.DocumentRequired),
            new("common-module update", "Update installed CommonModules entries.", "[options]", projectRootOptions, 22, ToolingCommandContextPolicy.ProjectRequired),
            new("reference add", "Add VBA project references to the selected document manifest.", "[references...] [options]", projectOptions, 30, ToolingCommandContextPolicy.DocumentRequired),
            new("reference list", "List VBA project references for the selected document.", "[options]", [.. projectOptions, Option("--format", "Reference output format.", "<text|json>", ["text", "json"], ["-f"])], 31, ToolingCommandContextPolicy.DocumentRequired),
            new("reference remove", "Remove VBA project references from the selected document manifest.", "[references...] [options]", projectOptions, 32, ToolingCommandContextPolicy.DocumentRequired),
            new("build", "Build the selected document into bin output.", "[options]", projectOptions, 30, ToolingCommandContextPolicy.DocumentRequired),
            new("test", "Run VBA unit tests for the selected document.", "[options]", [.. projectOptions, Option("--format", "Test output format.", "<text|ndjson>", ["text", "ndjson"], ["-f"]), Flag("--no-build", "Skip building before running tests."), Option("--module", "Run tests from one test module.", "<name>"), Option("--procedure", "Run one test procedure. Requires --module.", "<name>")], 40, ToolingCommandContextPolicy.DocumentRequired, OutputSchemaVersion: "1.2"),
            new("publish", "Publish the selected document.", "[options]", projectOptions, 50, ToolingCommandContextPolicy.DocumentRequired),
            new("export", "Export modules from a workbook into source.", "[options]", [.. projectOptions, Option("--from", "Workbook to export from; skips project resolution when supplied.", "<path>"), Option("--to", "Directory to export to; defaults to the selected document source set, or the current directory with --from.", "<dir>")], 70, ToolingCommandContextPolicy.DocumentUnlessOptionPresent("--from", "--project", "--document")),
            new("import", "Run a path-only import of VBA sources into an existing workbook; unlike build, it does not use vba-project.json.", "[options]", [Option("--from", "Source directory containing .bas, .cls, and .frm files.", "<dir>"), Option("--to", "Existing workbook file to update in place.", "<path>")], 75, ToolingCommandContextPolicy.None),
            new("doctor", "Check project and machine prerequisites.", "[options]", projectRootOptions, 80, ToolingCommandContextPolicy.ProjectOptional),
            new("capabilities", "Print the command contract supported by this executable.", "[options]", [Option("--format", "Capabilities output format.", "<json>", ["json"])], 90, ToolingCommandContextPolicy.None)
        ];
    }

    /// <summary>
    /// Creates handlers that adapt parsed command invocations to application services.
    /// </summary>
    /// <param name="doctorCommand">The environment diagnostics command.</param>
    /// <param name="newProjectCommand">The project creation command.</param>
    /// <param name="commonModulesService">The CommonModules command service.</param>
    /// <param name="referenceService">The VBA project reference command service.</param>
    /// <param name="buildCommand">The build command.</param>
    /// <param name="publishCommand">The publish command.</param>
    /// <param name="testCommand">The test command.</param>
    /// <param name="exportCommand">The export command.</param>
    /// <param name="importCommand">The import command.</param>
    /// <returns>The default handler bindings keyed by command name.</returns>
    public static IReadOnlyList<ToolingCommandHandler> CreateDefaultHandlers(
        DoctorCommand doctorCommand,
        NewProjectCommand newProjectCommand,
        CommonModulesService commonModulesService,
        VbaProjectReferenceService referenceService,
        BuildCommand buildCommand,
        PublishCommand publishCommand,
        TestCommand testCommand,
        ExportCommand exportCommand,
        ImportCommand importCommand)
        =>
        [
            new("new excel", invocation => newProjectCommand.Run(new NewProjectCommandRequest(
                invocation.GetOption("--name"),
                null,
                invocation.GetOption("--output"),
                invocation.WorkingDirectory))),
            new("common-module add", invocation => commonModulesService.Add(
                invocation.RequireContext(),
                invocation.Positionals,
                invocation.HasOption("--force"))),
            new("common-module list", invocation => commonModulesService.List(
                invocation.RequireContext(),
                invocation.GetOption("--format") ?? "text")),
            new("common-module update", invocation => commonModulesService.Update(invocation.RequireProject())),
            new("reference add", invocation => referenceService.Add(invocation.RequireContext(), invocation.Positionals)),
            new("reference list", invocation => referenceService.List(
                invocation.RequireContext(),
                invocation.GetOption("--format") ?? "text")),
            new("reference remove", invocation => referenceService.Remove(invocation.RequireContext(), invocation.Positionals)),
            new("build", invocation => buildCommand.Run(invocation.RequireContext())),
            new("test", invocation => RunTestCommand(testCommand, invocation)),
            new("publish", invocation => publishCommand.Run(invocation.RequireContext())),
            new("export", invocation => RunExportCommand(exportCommand, invocation)),
            new("import", invocation => importCommand.Run(new ImportCommandRequest(
                invocation.GetOption("--from"),
                invocation.GetOption("--to"),
                invocation.WorkingDirectory))),
            new("doctor", invocation => doctorCommand.Run(new DoctorCommandRequest(
                invocation.GetOption("--project"),
                invocation.WorkingDirectory)))
        ];

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
