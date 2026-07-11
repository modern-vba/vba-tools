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
    public IReadOnlyList<DiagnosticResult> RunEnvironmentDiagnostics()
        =>
        [
            DiagnosticResult.Skip("Excel COM startup", "Real Excel automation diagnostics are optional and are not enabled in this environment."),
            DiagnosticResult.Skip("Macro-enabled workbook creation", "Real Excel automation diagnostics are optional and are not enabled in this environment."),
            DiagnosticResult.Skip("VBIDE project access", "Real Excel automation diagnostics are optional and are not enabled in this environment."),
            DiagnosticResult.Skip("Locked workbook detection", "Locked workbook symptoms are reported by workbook commands when they run.")
        ];
}
