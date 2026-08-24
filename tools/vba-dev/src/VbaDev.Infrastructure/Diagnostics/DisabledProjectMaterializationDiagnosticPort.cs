using VbaDev.App.Diagnostics;
using VbaDev.App.Projects;

namespace VbaDev.Infrastructure.Diagnostics;

/// <summary>
/// Omits active materialization probes in explicitly composed test or host environments.
/// </summary>
public sealed class DisabledProjectMaterializationDiagnosticPort
    : IProjectMaterializationDiagnosticPort
{
    /// <inheritdoc />
    public Task<ProjectMaterializationDiagnosticRun> RunAsync(
        ResolvedProject project,
        CancellationToken cancellationToken)
        => Task.FromResult(new ProjectMaterializationDiagnosticRun([]));
}
