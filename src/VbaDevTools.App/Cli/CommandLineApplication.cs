using System.Text;

namespace VbaDevTools.App.Cli;

public sealed class CommandLineApplication
{
    private readonly IReadOnlyDictionary<string, ToolingCommandDefinition> commands;

    public CommandLineApplication(IEnumerable<ToolingCommandDefinition> commands)
    {
        this.commands = commands.ToDictionary(command => command.Name, StringComparer.OrdinalIgnoreCase);
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

        var validationError = ValidateOptions(command, commandArgs);
        if (validationError is not null)
        {
            return CommandResult.UsageError(validationError);
        }

        return CommandResult.NotImplemented($"Command '{command.Name}' is not implemented yet.");
    }

    private static string? ValidateOptions(ToolingCommandDefinition command, IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
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
                return $"Unknown option '{optionName}' for command '{command.Name}'.";
            }

            if (!option.RequiresValue)
            {
                if (inlineValue is not null)
                {
                    return $"Option '{optionName}' does not accept a value.";
                }

                continue;
            }

            var value = inlineValue;
            if (value is null)
            {
                if (i + 1 >= args.Count)
                {
                    return $"Option '{optionName}' requires a value.";
                }

                value = args[++i];
            }

            if (option.AllowedValues.Count > 0 && !option.AllowedValues.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                return $"Unsupported value '{value}' for {option.Name}.";
            }
        }

        return null;
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
