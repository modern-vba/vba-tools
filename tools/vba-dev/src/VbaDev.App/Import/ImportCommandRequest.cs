namespace VbaDev.App.Import;

public sealed record ImportCommandRequest(
    string? FromPath,
    string? ToPath,
    string WorkingDirectory);
