namespace VbaDev.App.Export;

public sealed record ExportCommandRequest(
    string? FromPath,
    string? ToPath,
    string WorkingDirectory);
