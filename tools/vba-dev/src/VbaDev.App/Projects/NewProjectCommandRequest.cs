namespace VbaDev.App.Projects;

/// <summary>
/// Carries command-line inputs for creating a new workbook-backed project.
/// </summary>
/// <param name="ProjectName">The requested project name supplied by --name.</param>
/// <param name="DocumentName">The optional document name; null uses the project name.</param>
/// <param name="OutputDirectory">The optional output directory supplied by --output.</param>
/// <param name="StartDirectory">The directory used for default output placement.</param>
public sealed record NewProjectCommandRequest(
    string? ProjectName,
    string? DocumentName,
    string? OutputDirectory,
    string StartDirectory);
