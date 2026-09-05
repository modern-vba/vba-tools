using VbaDev.App.Projects;

namespace VbaDev.App.Diagnostics;

/// <summary>
/// Actively verifies whether project templates can be materialized in owned Excel processes.
/// </summary>
public interface IProjectMaterializationDiagnosticPort
{
    /// <summary>
    /// Checks every applicable project template without mutating the source workbooks.
    /// </summary>
    Task<ProjectMaterializationDiagnosticRun> RunAsync(
        ResolvedProject project,
        DoctorProjectSourceInspection sources,
        CancellationToken cancellationToken);
}

/// <summary>
/// Describes one project materialization diagnostic execution.
/// </summary>
public sealed record ProjectMaterializationDiagnosticRun(
    IReadOnlyList<DiagnosticResult> Results,
    bool Complete = true,
    bool Canceled = false);
