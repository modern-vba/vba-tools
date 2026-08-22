using System.CommandLine;
using System.CommandLine.Completions;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using VbaDev.App.Cli;
using VbaDev.App.Debugging;
using VbaDev.App.Diagnostics;
using VbaDev.App.Export;
using VbaDev.App.Import;
using VbaDev.App.Projects;
using VbaDev.App.Testing;
using VbaDev.Cli.Debugging;
using VbaDev.Composition;

namespace VbaDev.Cli;

/// <summary>
/// Owns the public <c>vba-dev</c> command graph and invokes it against supplied streams.
/// </summary>
public sealed class VbaDevCommandLine
{
    private readonly RootCommand rootCommand;

    private VbaDevCommandLine(RootCommand rootCommand)
    {
        this.rootCommand = rootCommand;
    }

    /// <summary>
    /// Creates the default command graph.
    /// </summary>
    /// <returns>The command line used by the standalone executable.</returns>
    public static VbaDevCommandLine CreateDefault()
    {
        return Create(
            ToolingCompositionRoot.CreateApplicationComposition(),
            async cancellationToken =>
            {
                var debugComposition = ToolingCompositionRoot.CreateDebugAdapterComposition();
                var adapter = new VbaDebugAdapter(
                    debugComposition.ProjectContextResolver,
                    debugComposition.LaunchCoordinator,
                    () => debugComposition.WorkingDirectory);
                await adapter.RunAsync(
                    Console.OpenStandardInput(),
                    Console.OpenStandardOutput(),
                    cancellationToken);
                return 0;
            });
    }

    /// <summary>
    /// Creates a command graph over shell-neutral composed application services.
    /// </summary>
    /// <param name="composition">The services and working directory used by command handlers.</param>
    /// <param name="debugAdapterRunner">The optional stdio debug-adapter transport runner.</param>
    /// <returns>A command line using the supplied application services.</returns>
    public static VbaDevCommandLine Create(
        ToolingApplicationComposition composition,
        Func<CancellationToken, Task<int>>? debugAdapterRunner = null)
    {
        var rootCommand = new RootCommand("VBA development tooling.");
        var helpOption = rootCommand.Options.OfType<HelpOption>().Single();
        rootCommand.Action = new RootHelpAction(
            helpOption.Action as HelpAction
            ?? throw new InvalidOperationException("System.CommandLine root help action is missing."));
        var versionOption = rootCommand.Options.OfType<VersionOption>().Single();
        versionOption.Action = new CanonicalVersionAction(ReleaseVersion);
        var capabilityCommands = new List<CommandCapabilityRegistration>();

        var newCommand = AddCommand(rootCommand, "new", "Create a VBA project.");
        var newExcelCommand = AddCapabilityCommand(
            newCommand,
            "excel",
            "Create an Excel workbook-backed VBA project.",
            "1.0",
            capabilityCommands);
        var newNameOption = CreateStringOption(
            "--name",
            "Project and document base name.",
            "name",
            aliases: "-n");
        var newOutputOption = CreateStringOption(
            "--output",
            "Project root output directory.",
            "dir",
            aliases: "-o");
        newExcelCommand.Add(newNameOption);
        newExcelCommand.Add(newOutputOption);
        newExcelCommand.SetAction(parseResult => WriteCommandResult(
            parseResult,
            composition.NewProjectCommand.Run(new NewProjectCommandRequest(
                parseResult.GetValue(newNameOption),
                null,
                parseResult.GetValue(newOutputOption),
                composition.WorkingDirectory))));

        var commonModuleCommand = AddCommand(rootCommand, "common-module", "Manage CommonModules entries.");
        var commonModuleAddCommand = AddCapabilityCommand(
            commonModuleCommand,
            "add",
            "Copy CommonModules entries into the selected document source set.",
            "1.0",
            capabilityCommands);
        var commonModuleAddOptions = AddProjectDocumentOptions(commonModuleAddCommand);
        var commonModuleArguments = new Argument<string[]>("modules")
        {
            Arity = ArgumentArity.ZeroOrMore,
            Description = "CommonModules entries to add."
        };
        var commonModuleForceOption = new Option<bool>("--force")
        {
            Description = "Overwrite conflicting source files."
        };
        commonModuleAddCommand.Add(commonModuleArguments);
        commonModuleAddCommand.Add(commonModuleForceOption);
        commonModuleAddCommand.SetAction(parseResult => WriteCommandResult(
            parseResult,
            ResolveDocumentContext(
                parseResult,
                composition,
                commonModuleAddOptions,
                context => composition.CommonModulesService.Add(
                    context,
                    parseResult.GetValue(commonModuleArguments) ?? [],
                    parseResult.GetValue(commonModuleForceOption)))));
        var commonModuleListCommand = AddCapabilityCommand(
            commonModuleCommand,
            "list",
            "List CommonModules entries for the selected document.",
            "1.0",
            capabilityCommands);
        var commonModuleListOptions = AddProjectDocumentOptions(commonModuleListCommand);
        var commonModuleListFormatOption = CreateStringOption(
            "--format",
            "CommonModules output format.",
            "text|json",
            ["text", "json"],
            "-f");
        commonModuleListCommand.Add(commonModuleListFormatOption);
        commonModuleListCommand.SetAction(parseResult => WriteCommandResult(
            parseResult,
            ResolveDocumentContext(
                parseResult,
                composition,
                commonModuleListOptions,
                context => composition.CommonModulesService.List(
                    context,
                    parseResult.GetValue(commonModuleListFormatOption) ?? "text"))));
        var commonModuleUpdateCommand = AddCapabilityCommand(
            commonModuleCommand,
            "update",
            "Update installed CommonModules entries.",
            "1.0",
            capabilityCommands);
        var commonModuleUpdateProjectOption = AddProjectOption(commonModuleUpdateCommand);
        commonModuleUpdateCommand.SetAction(parseResult => WriteCommandResult(
            parseResult,
            ResolveProject(
                parseResult,
                composition,
                commonModuleUpdateProjectOption,
                composition.CommonModulesService.Update)));

        var referenceCommand = AddCommand(rootCommand, "reference", "Manage VBA project references.");
        var referenceAddCommand = AddCapabilityCommand(
            referenceCommand,
            "add",
            "Add VBA project references to the selected document manifest.",
            "1.0",
            capabilityCommands);
        var referenceAddOptions = AddProjectDocumentOptions(referenceAddCommand);
        var referenceAddArguments = new Argument<string[]>("references")
        {
            Arity = ArgumentArity.OneOrMore,
            Description = "VBA project reference names to add."
        };
        referenceAddCommand.Add(referenceAddArguments);
        referenceAddCommand.SetAction(async (parseResult, cancellationToken) => WriteCommandResult(
            parseResult,
            await ResolveDocumentContextAsync(
                    parseResult,
                    composition,
                    referenceAddOptions,
                    (context, operationCancellationToken) => composition.ReferenceService.AddAsync(
                        context,
                        parseResult.GetValue(referenceAddArguments) ?? [],
                        operationCancellationToken),
                    cancellationToken)
                .ConfigureAwait(false)));
        var referenceListCommand = AddCapabilityCommand(
            referenceCommand,
            "list",
            "List VBA project references for the selected document.",
            "1.0",
            capabilityCommands);
        var referenceListOptions = AddProjectDocumentOptions(referenceListCommand);
        var referenceListFormatOption = CreateStringOption(
            "--format",
            "Reference output format.",
            "text|json",
            ["text", "json"],
            "-f");
        referenceListCommand.Add(referenceListFormatOption);
        referenceListCommand.SetAction(async (parseResult, cancellationToken) => WriteCommandResult(
            parseResult,
            await ResolveDocumentContextAsync(
                    parseResult,
                    composition,
                    referenceListOptions,
                    (context, operationCancellationToken) => composition.ReferenceService.ListAsync(
                        context,
                        parseResult.GetValue(referenceListFormatOption) ?? "text",
                        operationCancellationToken),
                    cancellationToken)
                .ConfigureAwait(false)));
        var referenceRemoveCommand = AddCapabilityCommand(
            referenceCommand,
            "remove",
            "Remove VBA project references from the selected document manifest.",
            "1.0",
            capabilityCommands);
        var referenceRemoveOptions = AddProjectDocumentOptions(referenceRemoveCommand);
        var referenceRemoveArguments = new Argument<string[]>("references")
        {
            Arity = ArgumentArity.OneOrMore,
            Description = "VBA project reference names to remove."
        };
        referenceRemoveCommand.Add(referenceRemoveArguments);
        referenceRemoveCommand.SetAction(parseResult => WriteCommandResult(
            parseResult,
            ResolveDocumentContext(
                parseResult,
                composition,
                referenceRemoveOptions,
                context => composition.ReferenceService.Remove(
                    context,
                    parseResult.GetValue(referenceRemoveArguments) ?? []))));

        var buildCommand = AddCapabilityCommand(
            rootCommand,
            "build",
            "Build the selected document into bin output.",
            "1.0",
            capabilityCommands);
        var buildOptions = AddProjectDocumentOptions(buildCommand);
        buildCommand.SetAction(async (parseResult, cancellationToken) => WriteCommandResult(
            parseResult,
            await ResolveDocumentContextAsync(
                    parseResult,
                    composition,
                    buildOptions,
                    composition.BuildCommand.RunAsync,
                    cancellationToken)
                .ConfigureAwait(false)));
        var testCommand = AddCapabilityCommand(
            rootCommand,
            "test",
            "Run VBA unit tests for the selected document.",
            "1.2",
            capabilityCommands);
        var testProjectOptions = AddProjectDocumentOptions(testCommand);
        var testFormatOption = CreateStringOption(
            "--format",
            "Test output format.",
            "text|ndjson",
            ["text", "ndjson"],
            "-f");
        var testNoBuildOption = new Option<bool>("--no-build")
        {
            Description = "Skip building before running tests."
        };
        var testModuleOption = CreateStringOption("--module", "Run tests from one test module.", "name");
        var testProcedureOption = CreateStringOption(
            "--procedure",
            "Run one test procedure. Requires --module.",
            "name");
        testCommand.Add(testFormatOption);
        testCommand.Add(testNoBuildOption);
        testCommand.Add(testModuleOption);
        testCommand.Add(testProcedureOption);
        var testOptions = new TestCommandOptions(
            testProjectOptions,
            testFormatOption,
            testNoBuildOption,
            testModuleOption,
            testProcedureOption);
        testCommand.SetAction(parseResult => WriteCommandResult(
            parseResult,
            RunTestCommand(parseResult, composition, testOptions)));
        var publishCommand = AddCapabilityCommand(
            rootCommand,
            "publish",
            "Publish the selected document.",
            "1.0",
            capabilityCommands);
        var publishOptions = AddProjectDocumentOptions(publishCommand);
        publishCommand.SetAction(async (parseResult, cancellationToken) => WriteCommandResult(
            parseResult,
            await ResolveDocumentContextAsync(
                    parseResult,
                    composition,
                    publishOptions,
                    composition.PublishCommand.RunAsync,
                    cancellationToken)
                .ConfigureAwait(false)));
        var exportCommand = AddCapabilityCommand(
            rootCommand,
            "export",
            "Export modules from a workbook into source.",
            "1.0",
            capabilityCommands);
        var exportProjectOptions = AddProjectDocumentOptions(exportCommand);
        var exportFromOption = CreateStringOption(
            "--from",
            "Workbook to export from; skips project resolution when supplied.",
            "path");
        var exportToOption = CreateStringOption(
            "--to",
            "Directory to export to; defaults to the selected document source set, or the current directory with --from.",
            "dir");
        exportCommand.Add(exportFromOption);
        exportCommand.Add(exportToOption);
        var exportOptions = new ExportCommandOptions(
            exportProjectOptions,
            exportFromOption,
            exportToOption);
        exportCommand.SetAction(parseResult => WriteCommandResult(
            parseResult,
            RunExportCommand(parseResult, composition, exportOptions)));
        var importCommand = AddCapabilityCommand(
            rootCommand,
            "import",
            "Run a path-only import of VBA sources into an existing workbook; unlike build, it does not use vba-project.json.",
            "1.0",
            capabilityCommands);
        var importFromOption = CreateStringOption(
            "--from",
            "Source directory containing .bas, .cls, and .frm files.",
            "dir");
        var importToOption = CreateStringOption(
            "--to",
            "Existing workbook file to update in place.",
            "path");
        importCommand.Add(importFromOption);
        importCommand.Add(importToOption);
        importCommand.SetAction(parseResult => WriteCommandResult(
            parseResult,
            composition.ImportCommand.Run(new ImportCommandRequest(
                parseResult.GetValue(importFromOption),
                parseResult.GetValue(importToOption),
                composition.WorkingDirectory))));
        var doctorCommand = AddCapabilityCommand(
            rootCommand,
            "doctor",
            "Check project and machine prerequisites.",
            "1.0",
            capabilityCommands);
        var doctorProjectOption = AddProjectOption(doctorCommand);
        doctorCommand.SetAction(parseResult => WriteCommandResult(
            parseResult,
            composition.DoctorCommand.Run(new DoctorCommandRequest(
                parseResult.GetValue(doctorProjectOption),
                composition.WorkingDirectory))));

        var capabilitiesCommand = new Command(
            "capabilities",
            "Print the command contract supported by this executable.");
        var capabilitiesFormatOption = CreateStringOption(
            "--format",
            "Capabilities output format.",
            "json",
            ["json"]);
        capabilitiesCommand.Add(capabilitiesFormatOption);
        capabilitiesCommand.SetAction(parseResult =>
        {
            var capabilities = new ToolCapabilities(
                ReleaseVersion,
                "1.0",
                capabilityCommands
                    .OrderBy(registration => registration.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        registration => registration.Name,
                        registration => new CommandCapability(registration.OutputSchemaVersion),
                        StringComparer.OrdinalIgnoreCase),
                new DebugAdapterCapability(
                    VbaDebugCapabilityContract.ProtocolVersion,
                    VbaDebugCapabilityContract.Transport,
                    VbaDebugCapabilityContract.AdapterCommand));
            parseResult.InvocationConfiguration.Output.Write(
                JsonSerializer.Serialize(capabilities, CapabilitiesJsonOptions) + Environment.NewLine);
            return 0;
        });
        rootCommand.Add(capabilitiesCommand);

        var debugAdapterCommand = new Command(
            VbaDebugCapabilityContract.AdapterCommand,
            "Run the internal stdio debug adapter.")
        {
            Hidden = true
        };
        var debugAdapterStdioOption = new Option<bool>("--stdio")
        {
            Description = "Use the Debug Adapter Protocol over standard input and output.",
            Required = true
        };
        debugAdapterCommand.Add(debugAdapterStdioOption);
        debugAdapterCommand.Validators.Add(commandResult =>
        {
            var stdioResult = commandResult.GetResult(debugAdapterStdioOption);
            if (stdioResult is null || stdioResult.Implicit || stdioResult.Tokens.Count > 0)
            {
                commandResult.AddError(
                    $"Usage: vba-dev {VbaDebugCapabilityContract.AdapterCommand} --stdio");
            }
        });
        debugAdapterCommand.SetAction(async (_, cancellationToken) =>
        {
            if (debugAdapterRunner is null)
            {
                return 1;
            }

            try
            {
                return await debugAdapterRunner(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return 130;
            }
        });
        rootCommand.Add(debugAdapterCommand);

        return new VbaDevCommandLine(rootCommand);
    }

    /// <summary>
    /// Parses and invokes the command line against explicit output streams.
    /// </summary>
    /// <param name="args">The arguments after the executable name.</param>
    /// <param name="standardOutput">The standard output writer.</param>
    /// <param name="standardError">The standard error writer.</param>
    /// <param name="cancellationToken">The cooperative invocation cancellation token.</param>
    /// <returns>The process exit code.</returns>
    public Task<int> InvokeAsync(
        IReadOnlyList<string> args,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken)
    {
        var configuration = new InvocationConfiguration
        {
            Output = standardOutput,
            Error = standardError,
            ProcessTerminationTimeout = Timeout.InfiniteTimeSpan
        };

        return rootCommand.Parse(args).InvokeAsync(configuration, cancellationToken);
    }

    private static string ReleaseVersion
        => typeof(VbaDevCommandLine).Assembly
               .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
               .InformationalVersion
           ?? throw new InvalidOperationException("vba-dev informational version metadata is missing.");

    private static Command AddCommand(Command parent, string name, string description)
    {
        var command = new Command(name, description);
        parent.Add(command);
        return command;
    }

    private static Command AddCapabilityCommand(
        Command parent,
        string name,
        string description,
        string outputSchemaVersion,
        ICollection<CommandCapabilityRegistration> registrations)
    {
        var command = AddCommand(parent, name, description);
        var parentPath = registrations
            .Select(registration => registration.Command)
            .Contains(parent)
            ? registrations.Single(registration => registration.Command == parent).Name
            : parent.Name;
        registrations.Add(new CommandCapabilityRegistration(
            command,
            parent is RootCommand ? name : $"{parentPath} {name}",
            outputSchemaVersion));
        return command;
    }

    private static ProjectDocumentOptions AddProjectDocumentOptions(Command command)
    {
        var projectOption = AddProjectOption(command);
        var documentOption = CreateStringOption(
            "--document",
            "Document name from the project manifest.",
            "name",
            aliases: "-d");
        command.Add(documentOption);
        return new ProjectDocumentOptions(projectOption, documentOption);
    }

    private static Option<string> AddProjectOption(Command command)
    {
        var option = CreateStringOption(
            "--project",
            "Project root containing vba-project.json.",
            "path");
        command.Add(option);
        return option;
    }

    private static Option<string> CreateStringOption(
        string name,
        string description,
        string helpName,
        IReadOnlyList<string>? acceptedValues = null,
        params string[] aliases)
    {
        var option = new Option<string>(name, aliases)
        {
            Description = description,
            HelpName = helpName
        };
        if (acceptedValues is not null)
        {
            option.CustomParser = result =>
            {
                if (result.Tokens.Count == 0)
                {
                    return null;
                }

                var suppliedValue = result.Tokens[0].Value;
                var acceptedValue = acceptedValues.FirstOrDefault(candidate =>
                    candidate.Equals(suppliedValue, StringComparison.OrdinalIgnoreCase));
                if (acceptedValue is null)
                {
                    result.AddError(
                        $"Unsupported value '{suppliedValue}' for {name}. " +
                        $"Accepted values: {string.Join(", ", acceptedValues)}.");
                }

                return acceptedValue;
            };
            option.CompletionSources.Add(_ => acceptedValues.Select(value => new CompletionItem(value)));
        }

        return option;
    }

    private static int WriteCommandResult(ParseResult parseResult, CommandResult result)
    {
        if (!string.IsNullOrEmpty(result.StandardOutput))
        {
            parseResult.InvocationConfiguration.Output.Write(result.StandardOutput);
        }

        if (!string.IsNullOrEmpty(result.StandardError))
        {
            parseResult.InvocationConfiguration.Error.Write(result.StandardError);
        }

        return result.ExitCode;
    }

    private static CommandResult ResolveDocumentContext(
        ParseResult parseResult,
        ToolingApplicationComposition composition,
        ProjectDocumentOptions options,
        Func<ResolvedProjectContext, CommandResult> run)
    {
        try
        {
            var context = composition.ProjectContextResolver.Resolve(new ProjectResolutionRequest(
                parseResult.GetValue(options.Project),
                parseResult.GetValue(options.Document),
                composition.WorkingDirectory));
            return run(context);
        }
        catch (ProjectManifestException ex)
        {
            return CommandResult.UsageError(ex.Message);
        }
    }

    private static async Task<CommandResult> ResolveDocumentContextAsync(
        ParseResult parseResult,
        ToolingApplicationComposition composition,
        ProjectDocumentOptions options,
        Func<ResolvedProjectContext, CancellationToken, Task<CommandResult>> run,
        CancellationToken cancellationToken)
    {
        try
        {
            var context = composition.ProjectContextResolver.Resolve(new ProjectResolutionRequest(
                parseResult.GetValue(options.Project),
                parseResult.GetValue(options.Document),
                composition.WorkingDirectory));
            return await run(context, cancellationToken).ConfigureAwait(false);
        }
        catch (ProjectManifestException ex)
        {
            return CommandResult.UsageError(ex.Message);
        }
    }

    private static CommandResult ResolveProject(
        ParseResult parseResult,
        ToolingApplicationComposition composition,
        Option<string> projectOption,
        Func<ResolvedProject, CommandResult> run)
    {
        try
        {
            var project = composition.ProjectContextResolver.ResolveProject(new ProjectResolutionRequest(
                parseResult.GetValue(projectOption),
                null,
                composition.WorkingDirectory));
            return run(project);
        }
        catch (ProjectManifestException ex)
        {
            return CommandResult.UsageError(ex.Message);
        }
    }

    private static CommandResult RunTestCommand(
        ParseResult parseResult,
        ToolingApplicationComposition composition,
        TestCommandOptions options)
    {
        var moduleName = parseResult.GetValue(options.Module);
        var procedureName = parseResult.GetValue(options.Procedure);
        if (!string.IsNullOrWhiteSpace(procedureName) && string.IsNullOrWhiteSpace(moduleName))
        {
            return CommandResult.UsageError("--procedure requires --module.");
        }

        return ResolveDocumentContext(
            parseResult,
            composition,
            options.Project,
            context =>
            {
                try
                {
                    var format = CommandDefaultResolver.ResolveTestFormat(
                        context.Manifest,
                        parseResult.GetValue(options.Format));
                    return composition.TestCommand.Run(
                        context,
                        new TestCommandRequest(
                            format,
                            !parseResult.GetValue(options.NoBuild),
                            new WorkbookTestSelector(
                                string.IsNullOrWhiteSpace(moduleName) ? null : moduleName,
                                string.IsNullOrWhiteSpace(procedureName) ? null : procedureName)));
                }
                catch (InvalidOperationException ex)
                {
                    return CommandResult.UsageError(ex.Message);
                }
            });
    }

    private static CommandResult RunExportCommand(
        ParseResult parseResult,
        ToolingApplicationComposition composition,
        ExportCommandOptions options)
    {
        var request = new ExportCommandRequest(
            parseResult.GetValue(options.From),
            parseResult.GetValue(options.To),
            composition.WorkingDirectory);
        if (parseResult.GetResult(options.From) is not null)
        {
            if (parseResult.GetResult(options.Project.Project) is not null)
            {
                return CommandResult.UsageError("--project cannot be used with --from.");
            }

            if (parseResult.GetResult(options.Project.Document) is not null)
            {
                return CommandResult.UsageError("--document cannot be used with --from.");
            }

            return composition.ExportCommand.RunExplicit(request);
        }

        return ResolveDocumentContext(
            parseResult,
            composition,
            options.Project,
            context => composition.ExportCommand.Run(context, request));
    }

    private sealed class CanonicalVersionAction(string version) : SynchronousCommandLineAction
    {
        public override bool Terminating => true;

        public override bool ClearsParseErrors => false;

        public override int Invoke(ParseResult parseResult)
        {
            if (parseResult.Tokens.Count != 1 ||
                !parseResult.Tokens[0].Value.Equals("--version", StringComparison.Ordinal))
            {
                parseResult.InvocationConfiguration.Error.Write(
                    $"Option '--version' cannot be combined with other arguments.{Environment.NewLine}");
                return 1;
            }

            parseResult.InvocationConfiguration.Output.Write(
                $"vba-dev {version}{Environment.NewLine}");
            return 0;
        }
    }

    private sealed class RootHelpAction(HelpAction helpAction) : SynchronousCommandLineAction
    {
        public override bool ClearsParseErrors => false;

        public override int Invoke(ParseResult parseResult) => helpAction.Invoke(parseResult);
    }

    private sealed record CommandCapabilityRegistration(
        Command Command,
        string Name,
        string OutputSchemaVersion);

    private sealed record ProjectDocumentOptions(
        Option<string> Project,
        Option<string> Document);

    private sealed record TestCommandOptions(
        ProjectDocumentOptions Project,
        Option<string> Format,
        Option<bool> NoBuild,
        Option<string> Module,
        Option<string> Procedure);

    private sealed record ExportCommandOptions(
        ProjectDocumentOptions Project,
        Option<string> From,
        Option<string> To);

    private sealed record ToolCapabilities(
        string ToolVersion,
        string ContractVersion,
        IReadOnlyDictionary<string, CommandCapability> Commands,
        DebugAdapterCapability DebugAdapter);

    private sealed record CommandCapability(string OutputSchemaVersion);

    private sealed record DebugAdapterCapability(
        string ProtocolVersion,
        string Transport,
        string Command);

    private static readonly JsonSerializerOptions CapabilitiesJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
