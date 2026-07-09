using VbaDev.App.Diagnostics;

namespace VbaDev.Infrastructure.Diagnostics;

public sealed class SkippedEnvironmentDiagnosticPort : IEnvironmentDiagnosticPort
{
    public IReadOnlyList<DiagnosticResult> RunEnvironmentDiagnostics()
        =>
        [
            DiagnosticResult.Skip("Excel COM startup", "Real Excel automation diagnostics are optional and are not enabled in this environment."),
            DiagnosticResult.Skip("Macro-enabled workbook creation", "Real Excel automation diagnostics are optional and are not enabled in this environment."),
            DiagnosticResult.Skip("VBIDE project access", "Real Excel automation diagnostics are optional and are not enabled in this environment."),
            DiagnosticResult.Skip("Locked workbook detection", "Locked workbook symptoms are reported by workbook commands when they run.")
        ];
}
