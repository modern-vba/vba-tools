namespace VbaDev.App.Diagnostics;

public interface IEnvironmentDiagnosticPort
{
    IReadOnlyList<DiagnosticResult> RunEnvironmentDiagnostics();
}
