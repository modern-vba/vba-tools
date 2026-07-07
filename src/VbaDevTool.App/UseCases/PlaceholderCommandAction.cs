namespace VbaDevTools.App.UseCases;

public sealed class PlaceholderCommandAction
{
    public string Describe(string commandName) => $"Command '{commandName}' is not implemented yet.";
}
