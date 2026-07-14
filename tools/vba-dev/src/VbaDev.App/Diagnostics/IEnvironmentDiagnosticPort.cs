namespace VbaDev.App.Diagnostics;

/// <summary>
/// Provides machine and host-environment diagnostics that are outside pure project manifest validation.
/// </summary>
public interface IEnvironmentDiagnosticPort
{
    /// <summary>
    /// Runs environment diagnostics for workbook-backed automation prerequisites.
    /// </summary>
    /// <returns>The diagnostics produced by the environment adapter.</returns>
    IReadOnlyList<DiagnosticResult> RunEnvironmentDiagnostics();
}
