namespace VbaDev.App.Projects;

/// <summary>
/// Carries inputs used to locate a project manifest and optionally select a document.
/// </summary>
/// <param name="ProjectRoot">The explicit nonblank project root, or null to search upward.</param>
/// <param name="DocumentName">The explicit nonblank document name, or null for the primary document.</param>
/// <param name="StartDirectory">The directory used as the starting point for upward manifest search.</param>
public sealed record ProjectResolutionRequest(
    string? ProjectRoot,
    string? DocumentName,
    string StartDirectory)
{
    private string? projectRoot = ValidateSelector(ProjectRoot, nameof(ProjectRoot));
    private string? documentName = ValidateSelector(DocumentName, nameof(DocumentName));

    /// <summary>Gets the explicit nonblank project root, or null to search upward.</summary>
    public string? ProjectRoot
    {
        get => projectRoot;
        init => projectRoot = ValidateSelector(value, nameof(ProjectRoot));
    }

    /// <summary>Gets the explicit nonblank document name, or null for the primary document.</summary>
    public string? DocumentName
    {
        get => documentName;
        init => documentName = ValidateSelector(value, nameof(DocumentName));
    }

    private static string? ValidateSelector(string? value, string parameterName)
    {
        if (value is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        }

        return value;
    }
}
