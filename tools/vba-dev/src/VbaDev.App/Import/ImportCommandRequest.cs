namespace VbaDev.App.Import;

/// <summary>
/// Carries paths for path-only workbook import operations.
/// </summary>
/// <param name="SourceDirectory">The required source directory.</param>
/// <param name="TargetWorkbook">The required target workbook.</param>
/// <param name="WorkingDirectory">The directory used to resolve relative paths.</param>
public sealed record ImportCommandRequest(
    string SourceDirectory,
    string TargetWorkbook,
    string WorkingDirectory);
