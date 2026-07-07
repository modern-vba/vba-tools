namespace VbaDevTools.App.Projects;

public sealed record NewProjectCommandRequest(
    string? ProjectName,
    string? DocumentName,
    string StartDirectory);
