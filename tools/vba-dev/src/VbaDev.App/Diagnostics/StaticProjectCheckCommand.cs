using System.Text;
using VbaDev.App.Cli;
using VbaDev.App.Projects;

namespace VbaDev.App.Diagnostics;

/// <summary>
/// Runs deterministic project checks without starting Excel or resolving live references.
/// </summary>
public sealed class StaticProjectCheckCommand
{
    private readonly ProjectContextResolver projectContextResolver;
    private readonly IReadOnlyList<IDoctorProjectDiagnosticProvider> diagnosticProviders;

    /// <summary>
    /// Creates the static project check command.
    /// </summary>
    public StaticProjectCheckCommand(
        ProjectContextResolver projectContextResolver,
        IReadOnlyList<IDoctorProjectDiagnosticProvider> diagnosticProviders)
    {
        this.projectContextResolver = projectContextResolver;
        this.diagnosticProviders = diagnosticProviders;
    }

    /// <summary>
    /// Evaluates manifest-backed facts without active Excel automation.
    /// </summary>
    public CommandResult Run(StaticProjectCheckRequest request)
    {
        var results = new List<DiagnosticResult>();
        ResolvedProject project;
        try
        {
            project = projectContextResolver.ResolveProject(
                new ProjectResolutionRequest(
                    request.ProjectRoot,
                    DocumentName: null,
                    request.StartDirectory));
        }
        catch (ProjectManifestException exception)
        {
            results.Add(DiagnosticResult.Fail("Project manifest", exception.Message));
            return Render(results);
        }

        results.Add(DiagnosticResult.Pass(
            "Project manifest",
            $"Loaded {project.ManifestPath}."));
        foreach (var provider in diagnosticProviders)
        {
            provider.AddDiagnostics(results, project);
        }

        return Render(results);
    }

    private static CommandResult Render(IReadOnlyList<DiagnosticResult> results)
    {
        var output = new StringBuilder();
        output.AppendLine("vba-dev check");
        output.AppendLine();
        foreach (var result in results)
        {
            output.AppendLine($"[{RenderStatus(result.Status)}] {result.Name}: {result.Message}");
        }

        var exitCode = results.Any(result => result.Status is
            DiagnosticStatus.Fail or DiagnosticStatus.Unverified or DiagnosticStatus.Skip)
            ? 1
            : 0;
        return new CommandResult(exitCode, output.ToString(), string.Empty);
    }

    private static string RenderStatus(DiagnosticStatus status)
        => status switch
        {
            DiagnosticStatus.Pass => "PASS",
            DiagnosticStatus.Warn => "WARN",
            DiagnosticStatus.Fail => "FAIL",
            DiagnosticStatus.Unverified => "UNVERIFIED",
            DiagnosticStatus.Skip => "SKIP",
            _ => status.ToString().ToUpperInvariant()
        };
}

/// <summary>
/// Carries project resolution inputs for an Excel-free static check.
/// </summary>
public sealed record StaticProjectCheckRequest(
    string? ProjectRoot,
    string StartDirectory);
