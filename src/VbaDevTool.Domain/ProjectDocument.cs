namespace VbaDevTools.Domain;

public sealed record ProjectDocument(
    string Kind,
    string SourcePath,
    string TemplatePath,
    string BinPath,
    string PublishPath)
{
    public const string ExcelKind = "excel";

    public static ProjectDocument CreateExcel(string documentName)
        => new(
            ExcelKind,
            $"src/{documentName}",
            $"src/{documentName}/{documentName}.xlsm",
            $"bin/{documentName}/{documentName}.xlsm",
            $"publish/{documentName}/{documentName}.xlsm");
}
