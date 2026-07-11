using VbaDev.App.Projects;

namespace VbaDev.App.Cli;

public sealed record ToolingCommandInvocation(
    ToolingCommandContract Command,
    IReadOnlyDictionary<string, string?> Options,
    IReadOnlyList<string> Positionals,
    ResolvedProject? Project,
    ResolvedProjectContext? Context,
    string WorkingDirectory,
    IReadOnlyDictionary<string, ToolingCommandContract> Commands)
{
    public string? GetOption(string optionName)
        => Options.GetValueOrDefault(optionName);

    public bool HasOption(string optionName)
        => Options.ContainsKey(optionName);

    public ResolvedProject RequireProject()
        => Project ?? throw new InvalidOperationException($"Command '{Command.Name}' requires a resolved project.");

    public ResolvedProjectContext RequireContext()
        => Context ?? throw new InvalidOperationException($"Command '{Command.Name}' requires a resolved document context.");
}
