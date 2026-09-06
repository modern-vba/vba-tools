using System.CommandLine;
using System.CommandLine.Completions;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using VbaDev.App.Cli;
using VbaDev.App.Projects;
using VbaDev.Composition;

namespace VbaDev.Cli;

/// <summary>
/// Constructs the one <c>System.CommandLine</c> graph used by <c>vba-dev</c>.
/// </summary>
internal static class VbaDevCommandGrammar
{
    internal static VbaDevCommandGraph Create(
        ToolingApplicationComposition composition,
        string generatingExecutablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generatingExecutablePath);
        var rootCommand = new RootCommand("VBA development tooling.");
        var helpOption = rootCommand.Options.OfType<HelpOption>().Single();
        var systemHelpAction = helpOption.Action as HelpAction
            ?? throw new InvalidOperationException("System.CommandLine root help action is missing.");
        rootCommand.Action = new RootHelpAction(systemHelpAction);
        helpOption.Action = new ExplicitHelpAction(systemHelpAction);
        var versionOption = rootCommand.Options.OfType<VersionOption>().Single();
        versionOption.Action = new CanonicalVersionAction(ReleaseVersion);
        var grammarFailureRules = new VbaDevGrammarFailureRules();
        grammarFailureRules.RequireStandalone(versionOption);
        var cancellationTransportOption = CreateStringOption(
            "--cancellation-transport",
            "Caller-owned cooperative cancellation transport.",
            "transport",
            ["stdin-v1"]);
        cancellationTransportOption.Hidden = true;
        cancellationTransportOption.Recursive = true;
        rootCommand.Add(cancellationTransportOption);
        var capabilityCommands = new List<VbaDevCommandCapabilityRegistration>();
        IReadOnlyList<VbaDevCommandCapabilityRegistration>? completedCapabilities = null;

        var newCommand = AddCommand(rootCommand, "new", "Create a VBA project.");
        var newExcelCommand = AddCapabilityCommand(
            newCommand,
            "excel",
            "Create an Excel workbook-backed VBA project.",
            "new excel",
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
        var newFormatOption = CreateStringOption(
            "--format",
            "Project creation receipt format.",
            "text|json",
            ["text", "json"],
            "-f");
        newExcelCommand.Add(newNameOption);
        newExcelCommand.Add(newOutputOption);
        newExcelCommand.Add(newFormatOption);
        newExcelCommand.SetAction(async (parseResult, cancellationToken) => WriteCommandResult(
            parseResult,
            await composition.NewProjectCommand.RunAsync(
                    new NewProjectCommandRequest(
                        parseResult.GetValue(newNameOption),
                        null,
                        parseResult.GetValue(newOutputOption),
                        composition.WorkingDirectory,
                        ProjectNameSpecified: parseResult.GetResult(newNameOption) is not null,
                        OutputDirectorySpecified: parseResult.GetResult(newOutputOption) is not null,
                        Format: parseResult.GetValue(newFormatOption) ?? "text"),
                    cancellationToken)
                .ConfigureAwait(false)));

        _ = VbaDevCommonModuleCommandFamily.Register(
            rootCommand,
            composition,
            grammarFailureRules,
            capabilityCommands);

        var completionsCommand = AddCommand(rootCommand, "completions", "Generate shell completion setup.");
        var completionsScriptCommand = AddCommand(
            completionsCommand,
            "script",
            "Write a shell completion registration script.");
        var completionsPowerShellCommand = AddCommand(
            completionsScriptCommand,
            "pwsh",
            "Write a PowerShell completion registration script.");
        completionsPowerShellCommand.SetAction(parseResult =>
        {
            parseResult.InvocationConfiguration.Output.Write(
                PowerShellCompletionScriptRenderer.Render(generatingExecutablePath));
            return 0;
        });

        _ = VbaDevReferenceCommandFamily.Register(
            rootCommand,
            composition,
            grammarFailureRules,
            capabilityCommands);

        _ = VbaDevHostEventCommandFamily.Register(
            rootCommand,
            composition,
            grammarFailureRules,
            capabilityCommands);

        var buildPublishCommandFamily = VbaDevBuildPublishCommandFamily.Create(
            composition,
            grammarFailureRules,
            capabilityCommands);
        buildPublishCommandFamily.RegisterBuild(rootCommand);
        _ = VbaDevTestCommandFamily.Register(
            rootCommand,
            composition,
            grammarFailureRules,
            capabilityCommands);
        buildPublishCommandFamily.RegisterPublish(rootCommand);
        _ = VbaDevImportExportCommandFamily.Register(
            rootCommand,
            composition,
            grammarFailureRules,
            capabilityCommands);
        _ = VbaDevInspectionCommandFamily.Register(
            rootCommand,
            composition,
            grammarFailureRules,
            capabilityCommands);

        var capabilitiesCommand = new Command(
            "capabilities",
            "Print the command contract supported by this executable.");
        var capabilitiesFormatOption = CreateStringOption(
            "--format",
            "Capabilities output format.",
            "json",
            ["json"],
            "-f");
        capabilitiesCommand.Add(capabilitiesFormatOption);
        capabilitiesCommand.SetAction(parseResult =>
        {
            var capabilities = new ToolCapabilities(
                ReleaseVersion,
                "1.0",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["build.sourceSnapshot"] = "2.0",
                    ["test.sourceSnapshot"] = "2.0",
                    ["invocation.stdinCancellation"] = "1.0",
                    ["sourceSnapshot.activeWindowsCodePage"] = "1.0",
                    ["projectCreation.pathValidation"] = "1.0",
                    ["hostEvent.list"] = "1.0"
                },
                GetActiveWindowsCodePage(),
                (completedCapabilities
                 ?? throw new InvalidOperationException("The vba-dev command graph is incomplete."))
                    .OrderBy(registration => registration.CommandPath, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        registration => registration.CommandPath,
                        registration => new CommandCapability(registration.OutputSchemaVersion),
                        StringComparer.OrdinalIgnoreCase));
            parseResult.InvocationConfiguration.Output.Write(
                JsonSerializer.Serialize(capabilities, CapabilitiesJsonOptions) + Environment.NewLine);
            return 0;
        });
        rootCommand.Add(capabilitiesCommand);

        completedCapabilities = ValidateCapabilityRegistrations(rootCommand, capabilityCommands);
        return new VbaDevCommandGraph(
            rootCommand,
            cancellationTransportOption,
            completedCapabilities,
            new VbaDevGrammarFailureRouter(rootCommand, grammarFailureRules));
    }

    private static string ReleaseVersion
        => typeof(VbaDevCommandLine).Assembly
               .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
               .InformationalVersion
           ?? throw new InvalidOperationException("vba-dev informational version metadata is missing.");

    internal static Command AddCommand(Command parent, string name, string description)
    {
        var command = new Command(name, description);
        parent.Add(command);
        return command;
    }

    internal static Command AddCapabilityCommand(
        Command parent,
        string name,
        string description,
        string commandPath,
        string outputSchemaVersion,
        ICollection<VbaDevCommandCapabilityRegistration> registrations)
    {
        var command = AddCommand(parent, name, description);
        registrations.Add(new VbaDevCommandCapabilityRegistration(
            command,
            commandPath,
            outputSchemaVersion));
        return command;
    }

    private static IReadOnlyList<VbaDevCommandCapabilityRegistration> ValidateCapabilityRegistrations(
        RootCommand rootCommand,
        IReadOnlyCollection<VbaDevCommandCapabilityRegistration> registrations)
    {
        IEqualityComparer<Command> commandComparer = ReferenceEqualityComparer.Instance;
        var reachableCommands = EnumerateCommands(rootCommand)
            .ToDictionary(
                entry => entry.Command,
                entry => entry.CommandPath,
                commandComparer);
        var registeredCommands = new HashSet<Command>(commandComparer);
        var registeredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var registration in registrations)
        {
            if (!reachableCommands.TryGetValue(registration.Command, out var actualPath))
            {
                throw new InvalidOperationException(
                    $"Capability command '{registration.CommandPath}' is not reachable from the completed root graph.");
            }

            if (!actualPath.Equals(registration.CommandPath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Capability command '{registration.CommandPath}' is registered beside '{actualPath}'.");
            }

            if (registration.Command.Subcommands.Count > 0 || registration.Command.Action is null)
            {
                throw new InvalidOperationException(
                    $"Capability command '{registration.CommandPath}' is not an actionable leaf.");
            }

            if (!registeredCommands.Add(registration.Command) ||
                !registeredPaths.Add(registration.CommandPath))
            {
                throw new InvalidOperationException(
                    $"Capability command '{registration.CommandPath}' is registered more than once.");
            }
        }

        return Array.AsReadOnly(registrations.ToArray());
    }

    private static IEnumerable<(Command Command, string CommandPath)> EnumerateCommands(
        Command parent,
        string parentPath = "")
    {
        foreach (var command in parent.Subcommands)
        {
            var commandPath = string.IsNullOrEmpty(parentPath)
                ? command.Name
                : $"{parentPath} {command.Name}";
            yield return (command, commandPath);
            foreach (var descendant in EnumerateCommands(command, commandPath))
            {
                yield return descendant;
            }
        }
    }

    internal static ProjectDocumentOptions AddProjectDocumentOptions(Command command)
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

    internal static Option<string> AddProjectOption(Command command)
    {
        var option = CreateStringOption(
            "--project",
            "Project root containing vba-project.json.",
            "path");
        command.Add(option);
        return option;
    }

    internal static Option<string> CreateStringOption(
        string name,
        string description,
        string helpName,
        IReadOnlyList<string>? acceptedValues = null,
        params string[] aliases)
    {
        var option = new VbaDevStringOption(name, aliases, acceptedValues)
        {
            Description = description,
            HelpName = helpName
        };
        if (acceptedValues is not null)
        {
            var frozenAcceptedValues = option.AcceptedValues
                ?? throw new InvalidOperationException(
                    $"Accepted values for option '{name}' are unavailable.");
            option.CustomParser = result =>
            {
                if (result.Tokens.Count == 0)
                {
                    return null;
                }

                var suppliedValue = result.Tokens[0].Value;
                var acceptedValue = frozenAcceptedValues.FirstOrDefault(candidate =>
                    candidate.Equals(suppliedValue, StringComparison.OrdinalIgnoreCase));
                if (acceptedValue is null)
                {
                    result.AddError(
                        $"Unsupported value '{suppliedValue}' for {name}. " +
                        $"Accepted values: {string.Join(", ", frozenAcceptedValues)}.");
                }

                return acceptedValue;
            };
            option.CompletionSources.Add(_ =>
                frozenAcceptedValues.Select(value => new CompletionItem(value)));
        }

        return option;
    }

    internal static int WriteCommandResult(ParseResult parseResult, CommandResult result)
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

    internal static CommandResult ResolveDocumentContext(
        ToolingApplicationComposition composition,
        string? projectRoot,
        string? documentName,
        Func<ResolvedProjectContext, CommandResult> run)
    {
        try
        {
            var context = composition.ProjectContextResolver.Resolve(new ProjectResolutionRequest(
                projectRoot,
                documentName,
                composition.WorkingDirectory));
            return run(context);
        }
        catch (ProjectManifestException ex)
        {
            return CommandResult.UsageError(ex.Message);
        }
    }

    internal static async Task<CommandResult> ResolveDocumentContextAsync(
        ToolingApplicationComposition composition,
        string? projectRoot,
        string? documentName,
        Func<ResolvedProjectContext, CancellationToken, Task<CommandResult>> run,
        CancellationToken cancellationToken)
    {
        try
        {
            var context = composition.ProjectContextResolver.Resolve(new ProjectResolutionRequest(
                projectRoot,
                documentName,
                composition.WorkingDirectory));
            return await run(context, cancellationToken).ConfigureAwait(false);
        }
        catch (ProjectManifestException ex)
        {
            return CommandResult.UsageError(ex.Message);
        }
    }

    internal static async Task<CommandResult> ResolveProjectAsync(
        ToolingApplicationComposition composition,
        string? projectRoot,
        Func<ResolvedProject, CancellationToken, Task<CommandResult>> run,
        CancellationToken cancellationToken)
    {
        try
        {
            var project = composition.ProjectContextResolver.ResolveProject(new ProjectResolutionRequest(
                projectRoot,
                null,
                composition.WorkingDirectory));
            return await run(project, cancellationToken).ConfigureAwait(false);
        }
        catch (ProjectManifestException ex)
        {
            return CommandResult.UsageError(ex.Message);
        }
    }

    private sealed class CanonicalVersionAction(string version) : SynchronousCommandLineAction
    {
        public override bool Terminating => true;

        public override bool ClearsParseErrors => false;

        public override int Invoke(ParseResult parseResult)
        {
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

    private sealed class ExplicitHelpAction(HelpAction helpAction)
        : SynchronousCommandLineAction, IVbaDevExplicitHelpAction
    {
        public override bool Terminating => true;

        public override bool ClearsParseErrors => false;

        public override int Invoke(ParseResult parseResult) => helpAction.Invoke(parseResult);
    }

    internal sealed record ProjectDocumentOptions(
        Option<string> Project,
        Option<string> Document);

    private sealed record ToolCapabilities(
        string ToolVersion,
        string ContractVersion,
        IReadOnlyDictionary<string, string> FeatureVersions,
        int? ActiveWindowsCodePage,
        IReadOnlyDictionary<string, CommandCapability> Commands);

    private sealed record CommandCapability(string OutputSchemaVersion);

    private static readonly JsonSerializerOptions CapabilitiesJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static int? GetActiveWindowsCodePage()
        => OperatingSystem.IsWindows()
            ? checked((int)GetACP())
            : null;

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();
}

internal sealed class VbaDevCommandGraph
{
    internal VbaDevCommandGraph(
        RootCommand rootCommand,
        Option<string> cancellationTransportOption,
        IReadOnlyList<VbaDevCommandCapabilityRegistration> capabilityRegistrations,
        VbaDevGrammarFailureRouter grammarFailureRouter)
    {
        if (!rootCommand.Options.Any(option => ReferenceEquals(option, cancellationTransportOption)))
        {
            throw new InvalidOperationException(
                "The cancellation transport symbol is not part of the completed root graph.");
        }

        if (!ReferenceEquals(rootCommand, grammarFailureRouter.RootCommand))
        {
            throw new InvalidOperationException(
                "The grammar failure router does not own the completed root graph.");
        }

        RootCommand = rootCommand;
        CancellationTransportOption = cancellationTransportOption;
        CapabilityRegistrations = capabilityRegistrations;
        GrammarFailureRouter = grammarFailureRouter;
    }

    internal RootCommand RootCommand { get; }

    internal Option<string> CancellationTransportOption { get; }

    internal IReadOnlyList<VbaDevCommandCapabilityRegistration> CapabilityRegistrations { get; }

    internal VbaDevGrammarFailureRouter GrammarFailureRouter { get; }
}

internal interface IVbaDevExplicitHelpAction
{
}

internal sealed record VbaDevCommandCapabilityRegistration(
    Command Command,
    string CommandPath,
    string OutputSchemaVersion);

internal sealed class VbaDevStringOption : Option<string>
{
    internal VbaDevStringOption(
        string name,
        string[] aliases,
        IReadOnlyList<string>? acceptedValues)
        : base(name, aliases)
    {
        AcceptedValues = acceptedValues is null
            ? null
            : Array.AsReadOnly(acceptedValues.ToArray());
    }

    internal IReadOnlyList<string>? AcceptedValues { get; }
}
