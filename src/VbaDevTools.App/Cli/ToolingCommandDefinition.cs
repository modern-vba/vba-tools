namespace VbaDevTools.App.Cli;

public sealed record ToolingCommandDefinition(
    string Name,
    string Description,
    string UsageSuffix,
    IReadOnlyList<CommandOptionDefinition> Options,
    int DisplayOrder);
