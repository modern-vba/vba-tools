namespace VbaDev.App.Workbooks;

public sealed record VbaSourceFile(
    string SourcePath,
    VbaSourceKind Kind,
    string? BinaryPath)
{
    public string FileName => Path.GetFileName(SourcePath);
}
