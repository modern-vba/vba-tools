namespace VbaDevTools.App.Diagnostics;

public interface IEnvironmentDiagnosticPort
{
    IReadOnlyList<DiagnosticResult> RunEnvironmentDiagnostics();
}
