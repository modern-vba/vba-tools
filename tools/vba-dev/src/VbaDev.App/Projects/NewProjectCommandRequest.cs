namespace VbaDev.App.Projects;

public sealed record NewProjectCommandRequest(
    string? ProjectName,
    string? DocumentName,
    string? OutputDirectory,
    string StartDirectory);
