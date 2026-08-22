namespace VbaDev.App.Workbooks;

/// <summary>
/// Identifies the fresh-workbook baseline used by one reference ambiguity probe.
/// </summary>
public sealed record VbaProjectReferenceProbeBaseline
{
    private VbaProjectReferenceProbeBaseline(
        VbaProjectReferenceProbeBaselineKind kind,
        string? workbookPath)
    {
        Kind = kind;
        WorkbookPath = workbookPath;
    }

    /// <summary>
    /// Gets the baseline kind.
    /// </summary>
    public VbaProjectReferenceProbeBaselineKind Kind { get; }

    /// <summary>
    /// Gets the selected source-template path, or <see langword="null"/> for a blank workbook.
    /// </summary>
    public string? WorkbookPath { get; }

    /// <summary>
    /// Creates a source-template baseline.
    /// </summary>
    public static VbaProjectReferenceProbeBaseline SourceTemplate(string workbookPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookPath);
        return new VbaProjectReferenceProbeBaseline(
            VbaProjectReferenceProbeBaselineKind.SourceTemplate,
            workbookPath);
    }

    /// <summary>
    /// Gets the environment-scope blank-workbook baseline.
    /// </summary>
    public static VbaProjectReferenceProbeBaseline BlankWorkbook { get; } =
        new(VbaProjectReferenceProbeBaselineKind.BlankWorkbook, null);
}

/// <summary>
/// Enumerates supported ambiguity-probe baseline sources.
/// </summary>
public enum VbaProjectReferenceProbeBaselineKind
{
    /// <summary>
    /// Fresh copies of the selected document source template.
    /// </summary>
    SourceTemplate,

    /// <summary>
    /// A new unsaved blank workbook for each candidate attempt.
    /// </summary>
    BlankWorkbook
}
