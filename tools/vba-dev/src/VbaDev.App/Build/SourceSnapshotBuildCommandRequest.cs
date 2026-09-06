namespace VbaDev.App.Build;

/// <summary>
/// Carries caller-owned source and output paths for a snapshot build.
/// </summary>
/// <param name="SourceSnapshotDirectory">The complete source snapshot directory.</param>
/// <param name="OutputWorkbook">The caller-owned workbook output path.</param>
/// <param name="WorkingDirectory">The directory used to resolve relative paths.</param>
public sealed record SourceSnapshotBuildCommandRequest(
    string SourceSnapshotDirectory,
    string OutputWorkbook,
    string WorkingDirectory);
