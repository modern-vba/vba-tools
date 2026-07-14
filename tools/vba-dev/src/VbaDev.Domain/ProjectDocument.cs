using System.Text.Json.Serialization;

namespace VbaDev.Domain;

/// <summary>
/// Describes the source, template, build, publish, CommonModules, and reference state for one project document.
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
    /// <param name="templatePath">The source template workbook path.</param>
    /// <param name="binPath">The generated build workbook path.</param>
    /// <param name="publishPath">The generated publish workbook path.</param>
    /// <param name="commonModules">The CommonModules entries installed into the document source set.</param>
    /// <param name="references">The VBA project references required by this document.</param>
    [JsonConstructor]
    public ProjectDocument(
        string kind,
        string sourcePath,
        string templatePath,
        string binPath,
        string publishPath,
        List<InstalledCommonModule>? commonModules = null,
        List<VbaProjectReference>? references = null)
    {
        Kind = kind;
        SourcePath = sourcePath;
        TemplatePath = templatePath;
        BinPath = binPath;
        PublishPath = publishPath;
        CommonModules = commonModules ?? [];
        References = references ?? [];
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
    /// Gets the workbook template path used as the starting point for builds.
    /// </summary>
    public string TemplatePath { get; init; }

    /// <summary>
    /// Gets the generated workbook path used by build and test commands.
    /// </summary>
    public string BinPath { get; init; }

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
    /// <param name="documentName">The document name used for source, bin, and publish paths.</param>
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
            $"bin/{documentName}.xlsm",
            $"publish/{documentName}.xlsm",
            commonModules?.ToList(),
            references?.ToList());
}
