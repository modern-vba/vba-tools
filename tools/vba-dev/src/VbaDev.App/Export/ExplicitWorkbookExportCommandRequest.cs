namespace VbaDev.App.Export;

/// <summary>
/// Carries an explicit workbook export source and destination.
/// </summary>
/// <param name="SourceWorkbook">The required source workbook.</param>
/// <param name="DestinationDirectory">The optional destination directory.</param>
/// <param name="WorkingDirectory">The directory used to resolve relative paths.</param>
public sealed record ExplicitWorkbookExportCommandRequest(
    string SourceWorkbook,
    string? DestinationDirectory,
    string WorkingDirectory);
