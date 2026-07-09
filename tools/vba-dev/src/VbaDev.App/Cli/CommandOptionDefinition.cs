namespace VbaDev.App.Cli;

public sealed record CommandOptionDefinition(
    string Name,
    string Description,
    bool RequiresValue,
    string? ValueDisplay = null,
    IReadOnlyList<string>? AllowedValues = null,
    IReadOnlyList<string>? Aliases = null)
{
    public IReadOnlyList<string> AllowedValues { get; } = AllowedValues ?? [];
    public IReadOnlyList<string> Aliases { get; } = Aliases ?? [];

    public string HelpDisplay => RequiresValue && ValueDisplay is not null
        ? string.Join(", ", AllNames.Select(name => $"{name} {ValueDisplay}"))
        : string.Join(", ", AllNames);

    private IEnumerable<string> AllNames
    {
        get
        {
            yield return Name;
            foreach (var alias in Aliases)
            {
                yield return alias;
            }
        }
    }
}
