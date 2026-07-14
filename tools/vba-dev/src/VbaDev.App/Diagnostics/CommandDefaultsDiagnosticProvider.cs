using VbaDev.App.Projects;

namespace VbaDev.App.Diagnostics;

/// <summary>
/// Adds diagnostics for manifest-backed command defaults.
/// </summary>
public sealed class CommandDefaultsDiagnosticProvider : IDoctorProjectDiagnosticProvider
{
    /// <inheritdoc />
    public void AddDiagnostics(List<DiagnosticResult> results, ResolvedProject project)
    {
        try
        {
            var format = CommandDefaultResolver.ResolveTestFormat(project.Manifest, null);
            results.Add(DiagnosticResult.Pass("Command defaults", $"Test output format resolves to '{format}'."));
        }
        catch (ProjectManifestException ex)
        {
            results.Add(DiagnosticResult.Fail("Command defaults", ex.Message));
        }
    }
}
