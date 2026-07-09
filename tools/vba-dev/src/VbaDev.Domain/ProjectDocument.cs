using System.Text.Json.Serialization;

namespace VbaDev.Domain;

public sealed record ProjectDocument
{
    public const string ExcelKind = "excel";

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

    public string Kind { get; init; }

    public string SourcePath { get; init; }

    public string TemplatePath { get; init; }

    public string BinPath { get; init; }

    public string PublishPath { get; init; }

    public List<InstalledCommonModule> CommonModules { get; init; }

    public List<VbaProjectReference> References { get; init; }

    public static ProjectDocument CreateExcel(
        string documentName,
        IReadOnlyList<InstalledCommonModule>? commonModules = null,
        IReadOnlyList<VbaProjectReference>? references = null)
        => new(
            ExcelKind,
            $"src/{documentName}",
            $"src/{documentName}/{documentName}.xlsm",
            $"bin/{documentName}/{documentName}.xlsm",
            $"publish/{documentName}/{documentName}.xlsm",
            commonModules?.ToList(),
            references?.ToList());
}
