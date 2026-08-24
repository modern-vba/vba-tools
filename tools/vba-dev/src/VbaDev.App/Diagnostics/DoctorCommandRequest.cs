namespace VbaDev.App.Diagnostics;

/// <summary>
/// Carries command-line inputs for the doctor command.
/// </summary>
/// <param name="ProjectRoot">The optional project root supplied by --project.</param>
/// <param name="StartDirectory">The directory used when searching upward for vba-project.json.</param>
/// <param name="Scope">The requested diagnostic authority.</param>
/// <param name="Format">The requested output representation.</param>
public sealed record DoctorCommandRequest(
    string? ProjectRoot,
    string StartDirectory,
    DoctorScope Scope = DoctorScope.Project,
    DoctorOutputFormat Format = DoctorOutputFormat.Text);

/// <summary>
/// Selects the project-independent or project-aware Doctor surface.
/// </summary>
public enum DoctorScope
{
    Project,
    Environment
}

/// <summary>
/// Selects the human-readable or machine-readable Doctor output.
/// </summary>
public enum DoctorOutputFormat
{
    Text,
    Json
}
