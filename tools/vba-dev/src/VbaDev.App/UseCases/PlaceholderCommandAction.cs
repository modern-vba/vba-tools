namespace VbaDev.App.UseCases;

/// <summary>
/// Formats placeholder text for command surfaces that have not been implemented.
/// </summary>
public sealed class PlaceholderCommandAction
{
    /// <summary>
    /// Describes an unimplemented command.
    /// </summary>
    /// <param name="commandName">The command name to include in the message.</param>
    /// <returns>A user-facing placeholder message.</returns>
    public string Describe(string commandName) => $"Command '{commandName}' is not implemented yet.";
}
