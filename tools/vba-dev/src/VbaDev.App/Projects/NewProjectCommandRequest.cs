namespace VbaDev.App.Projects;

/// <summary>
/// Carries command-line inputs for creating a new workbook-backed project.
/// </summary>
/// <param name="ProjectName">The requested project name supplied by --name.</param>
/// <param name="DocumentName">The optional document name; null uses the project name.</param>
/// <param name="OutputDirectory">The optional output directory supplied by --output.</param>
/// <param name="StartDirectory">The directory used for default output placement.</param>
/// <param name="ProjectNameSpecified">Whether --name was explicitly present; null infers presence from the value for API compatibility.</param>
/// <param name="OutputDirectorySpecified">Whether --output was explicitly present; null infers presence from the value for API compatibility.</param>
/// <param name="Format">The requested success receipt format.</param>
public sealed record NewProjectCommandRequest(
    string? ProjectName,
    string? DocumentName,
    string? OutputDirectory,
    string StartDirectory,
    bool? ProjectNameSpecified = null,
    bool? OutputDirectorySpecified = null,
    string Format = "text")
{
    /// <summary>Gets whether the project-name option was explicitly supplied.</summary>
    public bool HasProjectName => ProjectNameSpecified ?? ProjectName is not null;

    /// <summary>Gets whether the output option was explicitly supplied.</summary>
    public bool HasOutputDirectory => OutputDirectorySpecified ?? OutputDirectory is not null;
}
