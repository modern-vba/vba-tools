using System.Text.Json.Serialization;

namespace VbaDev.Domain;

/// <summary>
/// Describes the source workbook, publish, legacy bin, CommonModules, and reference state for one project document.
/// </summary>
public sealed record ProjectDocument
{
    /// <summary>
    /// The manifest document kind value for Excel workbooks.
    /// </summary>
    public const string ExcelKind = "excel";

    /// <summary>
    /// Creates a document manifest entry.
    /// </summary>
    /// <param name="kind">The Office document kind stored in vba-project.json.</param>
    /// <param name="sourcePath">The source set path for exported VBA modules.</param>
    /// <param name="templatePath">The source workbook path used by authoring commands.</param>
    /// <param name="publishPath">The generated publish workbook path.</param>
    /// <param name="commonModules">The CommonModules entries installed into the document source set.</param>
    /// <param name="references">The VBA project references required by this document.</param>
    /// <param name="binPath">The optional deprecated workbook output path retained for compatibility.</param>
    [JsonConstructor]
    public ProjectDocument(
        string kind,
        string sourcePath,
        string templatePath,
        string publishPath,
        List<InstalledCommonModule>? commonModules,
        List<VbaProjectReference>? references,
        string? binPath = null)
    {
        Kind = kind;
        SourcePath = sourcePath;
        TemplatePath = templatePath;
        BinPath = binPath;
        PublishPath = publishPath;
        CommonModules = commonModules!;
        References = references!;
    }

    /// <summary>
    /// Gets the Office document kind stored in the manifest.
    /// </summary>
    public string Kind { get; init; }

    /// <summary>
    /// Gets the path containing exported VBA source files for this document.
    /// </summary>
    public string SourcePath { get; init; }

    /// <summary>
    /// Gets the source workbook path used by ordinary Build, Debug, Test, and project Export.
    /// </summary>
    public string TemplatePath { get; init; }

    /// <summary>
    /// Gets the optional deprecated workbook output path. Authoring commands do not use it.
    /// </summary>
    public string? BinPath { get; init; }

    /// <summary>
    /// Gets the generated workbook path used by publish commands.
    /// </summary>
    public string PublishPath { get; init; }

    /// <summary>
    /// Gets the CommonModules entries tracked for the document source set.
    /// </summary>
    public List<InstalledCommonModule> CommonModules { get; init; }

    /// <summary>
    /// Gets the VBA project references tracked for this document.
    /// </summary>
    public List<VbaProjectReference> References { get; init; }

    /// <summary>
    /// Creates the conventional path layout for an Excel document entry.
    /// </summary>
    /// <param name="documentName">The document name used for source and publish paths.</param>
    /// <param name="commonModules">The initial CommonModules entries.</param>
    /// <param name="references">The initial VBA project references.</param>
    /// <returns>An Excel document entry using VbaDev's default folder layout.</returns>
    public static ProjectDocument CreateExcel(
        string documentName,
        IReadOnlyList<InstalledCommonModule>? commonModules = null,
        IReadOnlyList<VbaProjectReference>? references = null)
        => new(
            ExcelKind,
            $"src/{documentName}",
            $"src/{documentName}/{documentName}.xlsm",
            $"publish/{documentName}.xlsm",
            commonModules?.ToList() ?? [],
            references?.ToList() ?? []);
}
