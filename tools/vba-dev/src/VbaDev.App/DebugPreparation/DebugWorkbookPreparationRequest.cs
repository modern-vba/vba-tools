namespace VbaDev.App.DebugPreparation;

/// <summary>Binds one immutable source snapshot to an exact live Excel process.</summary>
public sealed record DebugWorkbookPreparationRequest(
    string SourceSnapshotDirectory,
    string WorkingDirectory,
    string GenerationId,
    int ExcelProcessId,
    long ExcelProcessStartUtcTicks);

/// <summary>The exact binding whose caller may authorize replacement after capture.</summary>
public sealed record DebugWorkbookPreparationReady(
    string GenerationId,
    string WorkbookPath,
    int ExcelProcessId,
    long ExcelProcessStartUtcTicks);
