namespace VbaDev.App.Import;

/// <summary>
/// Carries command-line inputs for path-only workbook import operations.
/// </summary>
/// <param name="FromPath">The source directory supplied by --from.</param>
/// <param name="ToPath">The target workbook supplied by --to.</param>
/// <param name="WorkingDirectory">The directory used to resolve relative option paths.</param>
public sealed record ImportCommandRequest(
    string? FromPath,
    string? ToPath,
    string WorkingDirectory);
