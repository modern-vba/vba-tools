namespace VbaDevTools.App.Cli;

public sealed record CommandOptionDefinition(
    string Name,
    string Description,
    bool RequiresValue,
    string? ValueDisplay = null,
    IReadOnlyList<string>? AllowedValues = null)
{
    public IReadOnlyList<string> AllowedValues { get; } = AllowedValues ?? [];

    public string HelpDisplay => RequiresValue && ValueDisplay is not null
        ? $"{Name} {ValueDisplay}"
        : Name;
}
