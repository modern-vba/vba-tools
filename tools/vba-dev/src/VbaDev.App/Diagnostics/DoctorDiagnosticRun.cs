namespace VbaDev.App.Diagnostics;

/// <summary>
/// Captures one Doctor execution and the request context it conclusively resolved.
/// </summary>
/// <param name="Results">The ordered diagnostic results.</param>
/// <param name="Project">The absolute resolved project root, or <see langword="null"/> when no project was resolved.</param>
/// <param name="Complete">Whether the planned diagnostic execution reached a terminal classification.</param>
/// <param name="Canceled">Whether cooperative cancellation determined the terminal outcome.</param>
public sealed record DoctorDiagnosticRun(
    IReadOnlyList<DiagnosticResult> Results,
    string? Project,
    bool Complete,
    bool Canceled = false);
