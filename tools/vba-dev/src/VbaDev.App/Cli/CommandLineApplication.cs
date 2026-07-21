using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VbaDev.App.Projects;

namespace VbaDev.App.Cli;

/// <summary>
/// Parses VbaDev command-line arguments, resolves project context, and dispatches command handlers.
/// </summary>
public sealed class CommandLineApplication
{
    private readonly IReadOnlyDictionary<string, ToolingCommandContract> commands;
    private readonly IReadOnlyDictionary<string, ToolingCommandHandler> handlers;
    private readonly ProjectContextResolver projectContextResolver;
    private readonly Func<string> getWorkingDirectory;

    /// <summary>
    /// Creates a command-line application from command contracts, handlers, and context resolution services.
    /// </summary>
    /// <param name="commands">The supported command contracts.</param>
    /// <param name="handlers">The executable command handlers.</param>
    /// <param name="projectContextResolver">The resolver used for project and document context policies.</param>
    /// <param name="getWorkingDirectory">A callback returning the current working directory for relative paths.</param>
    public CommandLineApplication(
        IEnumerable<ToolingCommandContract> commands,
        IEnumerable<ToolingCommandHandler> handlers,
        ProjectContextResolver projectContextResolver,
        Func<string> getWorkingDirectory)
    {
        this.commands = commands.ToDictionary(command => command.Name, StringComparer.OrdinalIgnoreCase);
        this.handlers = handlers.ToDictionary(handler => handler.CommandName, StringComparer.OrdinalIgnoreCase);
        this.projectContextResolver = projectContextResolver;
        this.getWorkingDirectory = getWorkingDirectory;
    }

    /// <summary>
    /// Runs one command-line invocation.
    /// </summary>
    /// <param name="args">The command-line arguments after the executable name.</param>
    /// <returns>The command result containing exit code and output streams.</returns>
    public CommandResult Run(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || IsHelp(args[0]))
        {
            return CommandResult.Success(RenderRootHelp());
        }

        var commandMatch = MatchCommand(args);
        if (commandMatch.Command is null)
        {
            var commandName = args[0];
            return CommandResult.UsageError($"Unknown command '{commandName}'.{Environment.NewLine}{Environment.NewLine}{RenderRootHelp()}");
        }

        var command = commandMatch.Command;
        var commandArgs = args.Skip(commandMatch.ConsumedArguments).ToArray();
        if (commandArgs.Any(IsHelp))
        {
            return CommandResult.Success(RenderCommandHelp(command));
        }

        var parsedArgs = ParseOptions(command, commandArgs);
        if (parsedArgs.Error is not null)
        {
            return CommandResult.UsageError(parsedArgs.Error);
        }

        var resolution = ResolveProjectForInvocation(command, parsedArgs.Options);
        if (resolution.Error is not null)
        {
            return CommandResult.UsageError(resolution.Error);
        }

        if (IsCapabilitiesCommand(command))
        {
            return RenderCapabilities(parsedArgs.Options);
        }

        if (!handlers.TryGetValue(command.Name, out var handler))
        {
            return CommandResult.UsageError($"Command '{command.Name}' is not executable.");
        }

        try
        {
            return handler.Execute(new ToolingCommandInvocation(
                command,
                parsedArgs.Options,
                parsedArgs.Positionals,
                resolution.Project,
                resolution.Context,
                getWorkingDirectory(),
                commands));
        }
        catch (InvalidOperationException ex)
        {
            return CommandResult.UsageError(ex.Message);
        }
    }

    private CommandMatch MatchCommand(IReadOnlyList<string> args)
    {
        foreach (var command in commands.Values.OrderByDescending(command => command.Name.Split(' ').Length))
        {
            var tokens = command.Name.Split(' ');
            if (args.Count < tokens.Length)
            {
                continue;
            }

            var matches = true;
            for (var i = 0; i < tokens.Length; i++)
            {
                if (!args[i].Equals(tokens[i], StringComparison.OrdinalIgnoreCase))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return new CommandMatch(command, tokens.Length);
            }
        }

        return new CommandMatch(null, 0);
    }

    private ProjectResolutionResult ResolveProjectForInvocation(
        ToolingCommandContract command,
        IReadOnlyDictionary<string, string?> options)
    {
        try
        {
            return command.ContextPolicy.Mode switch
            {
                ProjectResolutionMode.None => ProjectResolutionResult.Success(null, null),
                ProjectResolutionMode.ProjectOptional when !options.ContainsKey("--project") => ProjectResolutionResult.Success(null, null),
                ProjectResolutionMode.ProjectOptional => ProjectResolutionResult.Success(
                    ResolveProject(options.GetValueOrDefault("--project")),
                    null),
                ProjectResolutionMode.ProjectRequired => ProjectResolutionResult.Success(
                    ResolveProject(options.GetValueOrDefault("--project")),
                    null),
                ProjectResolutionMode.DocumentRequired => ProjectResolutionResult.Success(
                    null,
                    ResolveContext(options.GetValueOrDefault("--project"), options.GetValueOrDefault("--document"))),
                ProjectResolutionMode.DocumentUnlessOptionPresent when
                    command.ContextPolicy.ContextFreeOption is not null &&
                    options.ContainsKey(command.ContextPolicy.ContextFreeOption) =>
                    ResolveContextFreeInvocation(command.ContextPolicy, options),
                ProjectResolutionMode.DocumentUnlessOptionPresent => ProjectResolutionResult.Success(
                    null,
                    ResolveContext(options.GetValueOrDefault("--project"), options.GetValueOrDefault("--document"))),
                _ => ProjectResolutionResult.Failure($"Unsupported project resolution mode for command '{command.Name}'.")
            };
        }
        catch (ProjectManifestException ex)
        {
            return ProjectResolutionResult.Failure(ex.Message);
        }
    }

    private static ProjectResolutionResult ResolveContextFreeInvocation(
        ToolingCommandContextPolicy policy,
        IReadOnlyDictionary<string, string?> options)
    {
        foreach (var rejectedOption in policy.RejectedOptionsWhenContextFree)
        {
            if (options.ContainsKey(rejectedOption))
            {
                return ProjectResolutionResult.Failure($"{rejectedOption} cannot be used with {policy.ContextFreeOption}.");
            }
        }

        return ProjectResolutionResult.Success(null, null);
    }

    private ResolvedProject ResolveProject(string? projectRoot)
        => projectContextResolver.ResolveProject(new ProjectResolutionRequest(
            ProjectRoot: projectRoot,
            DocumentName: null,
            StartDirectory: getWorkingDirectory()));

    private ResolvedProjectContext ResolveContext(string? projectRoot, string? documentName)
        => projectContextResolver.Resolve(new ProjectResolutionRequest(
            ProjectRoot: projectRoot,
            DocumentName: documentName,
            StartDirectory: getWorkingDirectory()));

    private static ParsedCommandLine ParseOptions(ToolingCommandContract command, IReadOnlyList<string> args)
    {
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var positionals = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("-", StringComparison.Ordinal) || arg == "-")
            {
                positionals.Add(arg);
                continue;
            }

            var optionName = arg;
            string? inlineValue = null;
            var equalsIndex = arg.IndexOf('=', StringComparison.Ordinal);
            if (equalsIndex >= 0)
            {
                optionName = arg[..equalsIndex];
                inlineValue = arg[(equalsIndex + 1)..];
            }

            var option = command.Options.FirstOrDefault(candidate =>
                candidate.Name.Equals(optionName, StringComparison.OrdinalIgnoreCase) ||
                candidate.Aliases.Any(alias => alias.Equals(optionName, StringComparison.OrdinalIgnoreCase)));
            if (option is null)
            {
                return ParsedCommandLine.Failure($"Unknown option '{optionName}' for command '{command.Name}'.");
            }

            if (!option.RequiresValue)
            {
                if (inlineValue is not null)
                {
                    return ParsedCommandLine.Failure($"Option '{optionName}' does not accept a value.");
                }

                options[option.Name] = "true";
                continue;
            }

            var value = inlineValue;
            if (value is null)
            {
                if (i + 1 >= args.Count)
                {
                    return ParsedCommandLine.Failure($"Option '{optionName}' requires a value.");
                }

                value = args[++i];
            }

            if (option.AllowedValues.Count > 0 && !option.AllowedValues.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                return ParsedCommandLine.Failure($"Unsupported value '{value}' for {option.Name}.");
            }

            options[option.Name] = value;
        }

        return ParsedCommandLine.Success(options, positionals);
    }

    private string RenderRootHelp()
    {
        var builder = new StringBuilder();
        builder.AppendLine("vba-dev");
        builder.AppendLine();
        builder.AppendLine("Usage:");
        builder.AppendLine("  vba-dev <command> [options]");
        builder.AppendLine();
        builder.AppendLine("Commands:");
        foreach (var command in commands.Values
                     .GroupBy(command => command.Name.Split(' ')[0], StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.OrderBy(command => command.DisplayOrder).First())
                     .OrderBy(command => command.DisplayOrder))
        {
            var displayName = command.Name.Split(' ')[0];
            builder.AppendLine($"  {displayName,-14} {command.Description}");
        }

        return builder.ToString();
    }

    private CommandResult RenderCapabilities(IReadOnlyDictionary<string, string?> options)
    {
        var format = options.GetValueOrDefault("--format") ?? "json";
        if (!format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            return CommandResult.UsageError($"Unsupported value '{format}' for --format.");
        }

        var capabilities = new ToolCapabilities(
            ToolVersion: typeof(CommandLineApplication).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            ContractVersion: "1.0",
            Commands: commands.Values
                .Where(command => !IsCapabilitiesCommand(command))
                .OrderBy(command => command.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    command => command.Name,
                    command => new CommandCapability(OutputSchemaVersion: command.OutputSchemaVersion),
                    StringComparer.OrdinalIgnoreCase),
            DebugAdapter: new DebugAdapterCapability(
                ProtocolVersion: "1.0",
                Transport: "stdio",
                Command: "debug-adapter"));

        return CommandResult.Success(JsonSerializer.Serialize(capabilities, CapabilitiesJsonOptions) + Environment.NewLine);
    }

    private static string RenderCommandHelp(ToolingCommandContract command)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"vba-dev {command.Name}");
        builder.AppendLine();
        builder.AppendLine(command.Description);
        builder.AppendLine();
        builder.AppendLine("Usage:");
        builder.AppendLine($"  vba-dev {command.Name} {command.UsageSuffix}".TrimEnd());

        if (command.Options.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Options:");
            foreach (var option in command.Options)
            {
                builder.AppendLine($"  {option.HelpDisplay,-30} {option.Description}");
            }
        }

        return builder.ToString();
    }

    private static bool IsHelp(string arg) => arg is "--help" or "-h" or "/?";

    private static bool IsCapabilitiesCommand(ToolingCommandContract command)
        => command.Name.Equals("capabilities", StringComparison.OrdinalIgnoreCase);

    private sealed record ParsedCommandLine(
        IReadOnlyDictionary<string, string?> Options,
        IReadOnlyList<string> Positionals,
        string? Error)
    {
        /// <summary>
        /// Creates a parsed command line result with resolved options and positional arguments.
        /// </summary>
        /// <param name="options">The parsed option values keyed by option name.</param>
        /// <param name="positionals">The positional arguments that remain after command and option parsing.</param>
        /// <returns>The successful parse result.</returns>
        public static ParsedCommandLine Success(
            IReadOnlyDictionary<string, string?> options,
            IReadOnlyList<string> positionals)
            => new(options, positionals, null);

        /// <summary>
        /// Creates a parsed command line result that carries a parse error.
        /// </summary>
        /// <param name="error">The parse error to report.</param>
        /// <returns>The failed parse result.</returns>
        public static ParsedCommandLine Failure(string error) => new(new Dictionary<string, string?>(), [], error);
    }

    private sealed record CommandMatch(ToolingCommandContract? Command, int ConsumedArguments);

    private sealed record ProjectResolutionResult(
        ResolvedProject? Project,
        ResolvedProjectContext? Context,
        string? Error)
    {
        /// <summary>
        /// Creates a project resolution result for a command that can proceed.
        /// </summary>
        /// <param name="project">The resolved project, when the command requires one.</param>
        /// <param name="context">The resolved project context, when available.</param>
        /// <returns>The successful project resolution result.</returns>
        public static ProjectResolutionResult Success(ResolvedProject? project, ResolvedProjectContext? context)
            => new(project, context, null);

        /// <summary>
        /// Creates a project resolution result that carries a resolution error.
        /// </summary>
        /// <param name="error">The resolution error to report.</param>
        /// <returns>The failed project resolution result.</returns>
        public static ProjectResolutionResult Failure(string error) => new(null, null, error);
    }

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
