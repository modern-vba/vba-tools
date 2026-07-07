namespace VbaDevTools.App.Diagnostics;

public sealed record DoctorCommandRequest(
    string? ProjectRoot,
    string? DocumentName,
    string StartDirectory);
