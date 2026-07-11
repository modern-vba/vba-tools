namespace VbaDev.App.Diagnostics;

/// <summary>
/// Carries command-line inputs for the doctor command.
/// </summary>
/// <param name="ProjectRoot">The optional project root supplied by --project.</param>
/// <param name="StartDirectory">The directory used when searching upward for project.json.</param>
public sealed record DoctorCommandRequest(
    string? ProjectRoot,
    string StartDirectory);
