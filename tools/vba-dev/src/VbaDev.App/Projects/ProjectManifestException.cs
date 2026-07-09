namespace VbaDev.App.Projects;

public sealed class ProjectManifestException : Exception
{
    public ProjectManifestException(string message)
        : base(message)
    {
    }

    public ProjectManifestException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
