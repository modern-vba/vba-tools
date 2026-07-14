namespace VbaDev.App.CommonModules;

/// <summary>
/// Reports invalid CommonModules repository configuration or manifest contents.
/// </summary>
public sealed class CommonModulesManifestException : Exception
{
    /// <summary>
    /// Creates a manifest exception with a user-facing error message.
    /// </summary>
    /// <param name="message">The error message to return from the command.</param>
    public CommonModulesManifestException(string message)
        : base(message)
    {
    }
}
