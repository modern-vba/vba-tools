namespace VbaDev.App.Workbooks;

/// <summary>
/// Describes an exported VBA source file selected for workbook import.
/// </summary>
/// <param name="SourcePath">The path to the .bas, .cls, or .frm source file.</param>
/// <param name="Kind">The VBA source kind inferred from the extension.</param>
/// <param name="BinaryPath">The matching .frx sidecar path for forms, when present.</param>
public sealed record VbaSourceFile(
    string SourcePath,
    VbaSourceKind Kind,
    string? BinaryPath)
{
    /// <summary>
    /// Gets the exact caller snapshot text that strict import decoding must reproduce, when supplied.
    /// </summary>
    internal string? ExpectedUnicodeText { get; init; }

    /// <summary>
    /// Gets the caller-facing path used when reporting a snapshot text mismatch, when supplied.
    /// </summary>
    internal string? ExpectedUnicodeTextSourcePath { get; init; }

    /// <summary>
    /// Gets the source file name used as flat source identity inside a document source set.
    /// </summary>
    public string FileName => Path.GetFileName(SourcePath);
}
