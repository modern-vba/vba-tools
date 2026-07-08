namespace VbaDevTools.App.Build;

public sealed class BuildCommandException : Exception
{
    public BuildCommandException(string message)
        : base(message)
    {
    }

    public BuildCommandException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
