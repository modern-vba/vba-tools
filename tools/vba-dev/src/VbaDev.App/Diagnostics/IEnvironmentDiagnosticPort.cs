namespace VbaDev.App.Diagnostics;

/// <summary>
/// Provides machine and host-environment diagnostics that are outside pure project manifest validation.
/// </summary>
public interface IEnvironmentDiagnosticPort
{
    /// <summary>
    /// Runs environment diagnostics for workbook-backed automation prerequisites.
    /// </summary>
    /// <param name="cancellationToken">The cooperative cancellation token.</param>
    /// <returns>The diagnostic execution produced by the environment adapter.</returns>
    Task<EnvironmentDiagnosticRun> RunEnvironmentDiagnosticsAsync(
        CancellationToken cancellationToken);
}

/// <summary>
/// Describes the terminal state of one environment diagnostic execution.
/// </summary>
/// <param name="Results">The diagnostic results produced by the adapter.</param>
/// <param name="Complete">Whether the planned execution reached terminal classification.</param>
/// <param name="Canceled">Whether cooperative cancellation determined the terminal outcome.</param>
public sealed record EnvironmentDiagnosticRun(
    IReadOnlyList<DiagnosticResult> Results,
    bool Complete = true,
    bool Canceled = false);
