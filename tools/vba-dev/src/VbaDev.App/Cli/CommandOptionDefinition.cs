namespace VbaDev.App.Cli;

/// <summary>
/// Describes one supported command-line option for help text and parser validation.
/// </summary>
/// <param name="Name">The canonical option name, including its prefix.</param>
/// <param name="Description">The help text for the option.</param>
/// <param name="RequiresValue">Whether the option must be followed by a value.</param>
/// <param name="ValueDisplay">The placeholder to show after value-taking option names.</param>
/// <param name="AllowedValues">The allowed option values, or null when any value is accepted.</param>
/// <param name="Aliases">Additional option names accepted by the parser.</param>
public sealed record CommandOptionDefinition(
    string Name,
    string Description,
    bool RequiresValue,
    string? ValueDisplay = null,
    IReadOnlyList<string>? AllowedValues = null,
    IReadOnlyList<string>? Aliases = null)
{
    /// <summary>
    /// Gets the allowed option values, or an empty list when unrestricted.
    /// </summary>
    public IReadOnlyList<string> AllowedValues { get; } = AllowedValues ?? [];

    /// <summary>
    /// Gets the alternate option names accepted by the parser.
    /// </summary>
    public IReadOnlyList<string> Aliases { get; } = Aliases ?? [];

    /// <summary>
    /// Gets the comma-separated option spelling used in command help.
    /// </summary>
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
