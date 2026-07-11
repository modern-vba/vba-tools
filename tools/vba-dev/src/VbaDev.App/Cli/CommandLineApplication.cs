using System.Text;
using VbaDev.App.Projects;

namespace VbaDev.App.Cli;

public sealed class CommandLineApplication
{
    private readonly IReadOnlyDictionary<string, ToolingCommandDefinition> commands;
    private readonly ProjectContextResolver projectContextResolver;
    private readonly Func<string> getWorkingDirectory;

    public CommandLineApplication(
        IEnumerable<ToolingCommandDefinition> commands,
        ProjectContextResolver projectContextResolver,
        Func<string> getWorkingDirectory)
    {
        this.commands = commands.ToDictionary(command => command.Name, StringComparer.OrdinalIgnoreCase);
        this.projectContextResolver = projectContextResolver;
        this.getWorkingDirectory = getWorkingDirectory;
    }

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

        try
        {
            return command.Execute(new ToolingCommandInvocation(
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
        ToolingCommandDefinition command,
        IReadOnlyDictionary<string, string?> options)
    {
        try
        {
            return command.ProjectResolutionMode switch
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
                ProjectResolutionMode.ExplicitWorkbookOrDocumentRequired when options.ContainsKey("--from") =>
                    ResolveExplicitWorkbookInvocation(options),
                ProjectResolutionMode.ExplicitWorkbookOrDocumentRequired => ProjectResolutionResult.Success(
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

    private ProjectResolutionResult ResolveExplicitWorkbookInvocation(IReadOnlyDictionary<string, string?> options)
    {
        if (options.ContainsKey("--project"))
        {
            return ProjectResolutionResult.Failure("--project cannot be used with --from.");
        }

        if (options.ContainsKey("--document"))
        {
            return ProjectResolutionResult.Failure("--document cannot be used with --from.");
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

    private static ParsedCommandLine ParseOptions(ToolingCommandDefinition command, IReadOnlyList<string> args)
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

    private static string RenderCommandHelp(ToolingCommandDefinition command)
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

    private sealed record ParsedCommandLine(
        IReadOnlyDictionary<string, string?> Options,
        IReadOnlyList<string> Positionals,
        string? Error)
    {
        public static ParsedCommandLine Success(
            IReadOnlyDictionary<string, string?> options,
            IReadOnlyList<string> positionals)
            => new(options, positionals, null);

        public static ParsedCommandLine Failure(string error) => new(new Dictionary<string, string?>(), [], error);
    }

    private sealed record CommandMatch(ToolingCommandDefinition? Command, int ConsumedArguments);

    private sealed record ProjectResolutionResult(
        ResolvedProject? Project,
        ResolvedProjectContext? Context,
        string? Error)
    {
        public static ProjectResolutionResult Success(ResolvedProject? project, ResolvedProjectContext? context)
            => new(project, context, null);

        public static ProjectResolutionResult Failure(string error) => new(null, null, error);
    }
}
