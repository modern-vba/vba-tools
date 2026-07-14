namespace VbaDev.App.Projects;

/// <summary>
/// Reports invalid, missing, or unreadable project manifest state.
/// </summary>
public sealed class ProjectManifestException : Exception
{
    /// <summary>
    /// Creates a project manifest exception with a user-facing message.
    /// </summary>
    /// <param name="message">The manifest error message.</param>
    public ProjectManifestException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates a project manifest exception that preserves the underlying parse or file error.
    /// </summary>
    /// <param name="message">The manifest error message.</param>
    /// <param name="innerException">The underlying exception.</param>
    public ProjectManifestException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
