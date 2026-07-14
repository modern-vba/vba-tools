using VbaDev.App.Projects;

namespace VbaDev.App.Cli;

/// <summary>
/// Carries parsed command-line input and resolved project context into a command handler.
/// </summary>
/// <param name="Command">The command contract being executed.</param>
/// <param name="Options">The parsed option values keyed by canonical option name.</param>
/// <param name="Positionals">The positional arguments left after command and option parsing.</param>
/// <param name="Project">The resolved project when the command policy requested one.</param>
/// <param name="Context">The resolved document context when the command policy requested one.</param>
/// <param name="WorkingDirectory">The working directory used for relative path resolution.</param>
/// <param name="Commands">The complete command catalog keyed by command name.</param>
public sealed record ToolingCommandInvocation(
    ToolingCommandContract Command,
    IReadOnlyDictionary<string, string?> Options,
    IReadOnlyList<string> Positionals,
    ResolvedProject? Project,
    ResolvedProjectContext? Context,
    string WorkingDirectory,
    IReadOnlyDictionary<string, ToolingCommandContract> Commands)
{
    /// <summary>
    /// Gets the parsed value for a canonical option name.
    /// </summary>
    /// <param name="optionName">The canonical option name.</param>
    /// <returns>The option value, null for a flag option or absent option.</returns>
    public string? GetOption(string optionName)
        => Options.GetValueOrDefault(optionName);

    /// <summary>
    /// Determines whether an option was supplied.
    /// </summary>
    /// <param name="optionName">The canonical option name.</param>
    /// <returns>True when the parser saw the option or one of its aliases.</returns>
    public bool HasOption(string optionName)
        => Options.ContainsKey(optionName);

    /// <summary>
    /// Gets the resolved project or throws when the command policy did not resolve one.
    /// </summary>
    /// <returns>The resolved project.</returns>
    public ResolvedProject RequireProject()
        => Project ?? throw new InvalidOperationException($"Command '{Command.Name}' requires a resolved project.");

    /// <summary>
    /// Gets the resolved document context or throws when the command policy did not resolve one.
    /// </summary>
    /// <returns>The resolved project and document context.</returns>
    public ResolvedProjectContext RequireContext()
        => Context ?? throw new InvalidOperationException($"Command '{Command.Name}' requires a resolved document context.");
}
