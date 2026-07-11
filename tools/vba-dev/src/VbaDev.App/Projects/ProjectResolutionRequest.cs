namespace VbaDev.App.Projects;

/// <summary>
/// Carries inputs used to locate a project manifest and optionally select a document.
/// </summary>
/// <param name="ProjectRoot">The explicit project root supplied by --project, or null to search upward.</param>
/// <param name="DocumentName">The explicit document name supplied by --document, or null for the primary document.</param>
/// <param name="StartDirectory">The directory used as the starting point for upward manifest search.</param>
public sealed record ProjectResolutionRequest(
    string? ProjectRoot,
    string? DocumentName,
    string StartDirectory);
