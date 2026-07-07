using System.Text;
using VbaDevTools.App.Build;
using VbaDevTools.App.CommonModules;
using VbaDevTools.App.Diagnostics;
using VbaDevTools.App.Projects;
using VbaDevTools.App.Testing;

namespace VbaDevTools.App.Cli;

public sealed class CommandLineApplication
{
    private readonly IReadOnlyDictionary<string, ToolingCommandDefinition> commands;
    private readonly ProjectContextResolver projectContextResolver;
    private readonly DoctorCommand doctorCommand;
    private readonly NewProjectCommand newProjectCommand;
    private readonly CommonModulesService commonModulesService;
    private readonly BuildCommand buildCommand;
    private readonly PublishCommand publishCommand;
    private readonly TestCommand testCommand;
    private readonly Func<string> getWorkingDirectory;

    public CommandLineApplication(
        IEnumerable<ToolingCommandDefinition> commands,
        ProjectContextResolver projectContextResolver,
        DoctorCommand doctorCommand,
        NewProjectCommand newProjectCommand,
        CommonModulesService commonModulesService,
        BuildCommand buildCommand,
        PublishCommand publishCommand,
        TestCommand testCommand,
        Func<string> getWorkingDirectory)
    {
        this.commands = commands.ToDictionary(command => command.Name, StringComparer.OrdinalIgnoreCase);
        this.projectContextResolver = projectContextResolver;
        this.doctorCommand = doctorCommand;
        this.newProjectCommand = newProjectCommand;
        this.commonModulesService = commonModulesService;
        this.buildCommand = buildCommand;
        this.publishCommand = publishCommand;
        this.testCommand = testCommand;
        this.getWorkingDirectory = getWorkingDirectory;
    }

    public CommandResult Run(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || IsHelp(args[0]))
        {
            return CommandResult.Success(RenderRootHelp());
        }

        var commandName = args[0];
        if (!commands.TryGetValue(commandName, out var command))
        {
            return CommandResult.UsageError($"Unknown command '{commandName}'.{Environment.NewLine}{Environment.NewLine}{RenderRootHelp()}");
        }

        var commandArgs = args.Skip(1).ToArray();
        if (commandArgs.Any(IsHelp))
        {
            return CommandResult.Success(RenderCommandHelp(command));
        }

        var parsedArgs = ParseOptions(command, commandArgs);
        if (parsedArgs.Error is not null)
        {
            return CommandResult.UsageError(parsedArgs.Error);
        }

        if (command.Name.Equals("new", StringComparison.OrdinalIgnoreCase))
        {
            return newProjectCommand.Run(new NewProjectCommandRequest(
                parsedArgs.Positionals.FirstOrDefault(),
                parsedArgs.Options.GetValueOrDefault("--document"),
                getWorkingDirectory()));
        }

        if (command.Name.Equals("doctor", StringComparison.OrdinalIgnoreCase))
        {
            return doctorCommand.Run(new DoctorCommandRequest(
                parsedArgs.Options.GetValueOrDefault("--project"),
                parsedArgs.Options.GetValueOrDefault("--document"),
                getWorkingDirectory()));
        }

        var resolution = ResolveProjectContext(command, parsedArgs.Options);
        if (resolution.Error is not null)
        {
            return CommandResult.UsageError(resolution.Error);
        }

        if (command.Name.Equals("test", StringComparison.OrdinalIgnoreCase) && resolution.Context is not null)
        {
            try
            {
                var format = CommandDefaultResolver.ResolveTestFormat(
                    resolution.Context.Manifest,
                    parsedArgs.Options.GetValueOrDefault("--format"));
                return testCommand.Run(
                    resolution.Context,
                    new TestCommandRequest(
                        format,
                        parsedArgs.Options.ContainsKey("--build")));
            }
            catch (ProjectManifestException ex)
            {
                return CommandResult.UsageError(ex.Message);
            }
        }

        if (command.Name.Equals("add", StringComparison.OrdinalIgnoreCase) && resolution.Context is not null)
        {
            return commonModulesService.Add(resolution.Context, parsedArgs.Positionals);
        }

        if (command.Name.Equals("build", StringComparison.OrdinalIgnoreCase) && resolution.Context is not null)
        {
            return buildCommand.Run(resolution.Context);
        }

        if (command.Name.Equals("publish", StringComparison.OrdinalIgnoreCase) && resolution.Context is not null)
        {
            return publishCommand.Run(resolution.Context);
        }

        if (command.Name.Equals("update", StringComparison.OrdinalIgnoreCase) && resolution.Context is not null)
        {
            return commonModulesService.Update(resolution.Context);
        }

        return CommandResult.NotImplemented($"Command '{command.Name}' is not implemented yet.");
    }

    private ProjectResolutionResult ResolveProjectContext(
        ToolingCommandDefinition command,
        IReadOnlyDictionary<string, string?> options)
    {
        if (command.ProjectResolutionMode == ProjectResolutionMode.None)
        {
            return ProjectResolutionResult.Success(null);
        }

        var hasProjectOption = options.ContainsKey("--project");
        if (command.ProjectResolutionMode == ProjectResolutionMode.Optional && !hasProjectOption)
        {
            return ProjectResolutionResult.Success(null);
        }

        try
        {
            var context = projectContextResolver.Resolve(new ProjectResolutionRequest(
                ProjectRoot: options.GetValueOrDefault("--project"),
                DocumentName: options.GetValueOrDefault("--document"),
                StartDirectory: getWorkingDirectory()));
            return ProjectResolutionResult.Success(context);
        }
        catch (ProjectManifestException ex)
        {
            return ProjectResolutionResult.Failure(ex.Message);
        }
    }

    private static ParsedCommandLine ParseOptions(ToolingCommandDefinition command, IReadOnlyList<string> args)
    {
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var positionals = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
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

            var option = command.Options.FirstOrDefault(candidate => candidate.Name.Equals(optionName, StringComparison.OrdinalIgnoreCase));
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

    private sealed record ProjectResolutionResult(ResolvedProjectContext? Context, string? Error)
    {
        public static ProjectResolutionResult Success(ResolvedProjectContext? context) => new(context, null);

        public static ProjectResolutionResult Failure(string error) => new(null, error);
    }

    private string RenderRootHelp()
    {
        var builder = new StringBuilder();
        builder.AppendLine("vba-devtools");
        builder.AppendLine();
        builder.AppendLine("Usage:");
        builder.AppendLine("  vba-devtools <command> [options]");
        builder.AppendLine();
        builder.AppendLine("Commands:");
        foreach (var command in commands.Values.OrderBy(command => command.DisplayOrder))
        {
            builder.AppendLine($"  {command.Name,-10} {command.Description}");
        }

        return builder.ToString();
    }

    private static string RenderCommandHelp(ToolingCommandDefinition command)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"vba-devtools {command.Name}");
        builder.AppendLine();
        builder.AppendLine(command.Description);
        builder.AppendLine();
        builder.AppendLine("Usage:");
        builder.AppendLine($"  vba-devtools {command.Name} {command.UsageSuffix}".TrimEnd());

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
}
