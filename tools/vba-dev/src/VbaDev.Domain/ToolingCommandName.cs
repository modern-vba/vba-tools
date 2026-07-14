namespace VbaDev.Domain;

/// <summary>
/// Wraps the stable command-line name for a VbaDev tooling command.
/// </summary>
/// <param name="Value">The command token accepted by the command-line parser.</param>
public readonly record struct ToolingCommandName(string Value)
{
    /// <summary>
    /// Returns the command token used on the command line.
    /// </summary>
    /// <returns>The command name value.</returns>
    public override string ToString() => Value;
}
