using VbaDev.App.Diagnostics;

namespace VbaDev.Infrastructure.Diagnostics;

/// <summary>
/// Provides skipped environment diagnostics for compositions that do not enable live Excel checks.
/// </summary>
public sealed class SkippedEnvironmentDiagnosticPort : IEnvironmentDiagnosticPort
{
    /// <summary>
    /// Returns skipped diagnostics explaining that real Excel automation checks are disabled.
    /// </summary>
    /// <returns>The skipped diagnostic results.</returns>
    public Task<EnvironmentDiagnosticRun> RunEnvironmentDiagnosticsAsync(
        CancellationToken cancellationToken)
        => Task.FromResult(new EnvironmentDiagnosticRun(
        [
            DiagnosticResult.Unverified("platform.windows", "Active environment diagnostics are not enabled in this composition."),
            DiagnosticResult.Skip("excel.comStartup", "Active environment diagnostics are not enabled in this composition."),
            DiagnosticResult.Skip("excel.processOwnership", "Active environment diagnostics are not enabled in this composition."),
            DiagnosticResult.Skip("excel.vbideProjectAccess", "Active environment diagnostics are not enabled in this composition."),
            DiagnosticResult.Skip("excel.processCleanup", "Active environment diagnostics are not enabled in this composition.")
        ], Complete: false));
}
