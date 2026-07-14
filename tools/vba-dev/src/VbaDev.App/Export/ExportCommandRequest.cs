namespace VbaDev.App.Export;

/// <summary>
/// Carries command-line inputs for workbook module export operations.
/// </summary>
/// <param name="FromPath">The optional workbook path supplied by --from.</param>
/// <param name="ToPath">The optional destination directory supplied by --to.</param>
/// <param name="WorkingDirectory">The directory used to resolve relative option paths.</param>
public sealed record ExportCommandRequest(
    string? FromPath,
    string? ToPath,
    string WorkingDirectory);
