namespace VbaDev.App.Projects;

public sealed record ProjectResolutionRequest(
    string? ProjectRoot,
    string? DocumentName,
    string StartDirectory);
