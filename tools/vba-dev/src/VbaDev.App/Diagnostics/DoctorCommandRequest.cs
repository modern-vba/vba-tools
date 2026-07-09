namespace VbaDev.App.Diagnostics;

public sealed record DoctorCommandRequest(
    string? ProjectRoot,
    string StartDirectory);
