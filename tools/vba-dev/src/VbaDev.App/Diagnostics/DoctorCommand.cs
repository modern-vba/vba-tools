using VbaDev.App.Cli;
using VbaDev.App.Projects;

namespace VbaDev.App.Diagnostics;

/// <summary>
/// Runs doctor diagnostics and renders the command report.
/// </summary>
public sealed class DoctorCommand
{
    private readonly DoctorDiagnosticPipeline diagnosticPipeline;
    private readonly DoctorReportRenderer reportRenderer;

    /// <summary>
    /// Creates the doctor command.
    /// </summary>
    /// <param name="diagnosticPipeline">The pipeline that collects doctor diagnostics.</param>
    /// <param name="reportRenderer">The renderer that maps diagnostics to command output.</param>
    public DoctorCommand(
        DoctorDiagnosticPipeline diagnosticPipeline,
        DoctorReportRenderer reportRenderer)
    {
        this.diagnosticPipeline = diagnosticPipeline;
        this.reportRenderer = reportRenderer;
    }

    /// <summary>
    /// Runs doctor diagnostics and formats the combined report.
    /// </summary>
    /// <param name="request">The doctor command input.</param>
    /// <returns>A command result whose exit code fails only when at least one diagnostic fails.</returns>
    public async Task<CommandResult> RunAsync(
        DoctorCommandRequest request,
        CancellationToken cancellationToken)
    {
        var requestedProjectIdentity = TryResolveProjectIdentity(request);
        try
        {
            var run = request.Scope == DoctorScope.Environment
                ? await diagnosticPipeline.RunEnvironmentAsync(cancellationToken)
                    .ConfigureAwait(false)
                : await diagnosticPipeline.RunAsync(request, cancellationToken)
                    .ConfigureAwait(false);
            return reportRenderer.Render(run, request);
        }
        catch (Exception exception) when (request.Scope == DoctorScope.Environment)
        {
            var results = DoctorDiagnosticPipeline.EnvironmentCheckIds
                .Select((checkId, index) => DoctorDiagnosticPipeline.EnsureEnvironmentDetails(
                    index == 0
                        ? DiagnosticResult.Unverified(
                            checkId,
                            $"Doctor infrastructure did not complete: {exception.Message}")
                        : DiagnosticResult.Skip(
                            checkId,
                            "The check was skipped because Doctor infrastructure did not complete.")))
                .ToArray();
            return reportRenderer.Render(
                new DoctorDiagnosticRun(
                    results,
                    Project: null,
                    Complete: false),
                request);
        }
        catch (Exception exception)
        {
            var results = new List<DiagnosticResult>
            {
                DiagnosticResult.Unverified(
                    "doctor.infrastructure",
                    $"Doctor infrastructure did not complete: {exception.Message}")
            };
            results.AddRange(DoctorDiagnosticPipeline.EnvironmentCheckIds.Select(
                (checkId, index) => DoctorDiagnosticPipeline.EnsureEnvironmentDetails(
                    index == 0
                        ? DiagnosticResult.Unverified(
                            checkId,
                            "Active environment evidence was not collected because Doctor infrastructure did not complete.")
                        : DiagnosticResult.Skip(
                            checkId,
                            "The check was skipped because Doctor infrastructure did not complete."))));
            return reportRenderer.Render(
                new DoctorDiagnosticRun(
                    results,
                    Project: requestedProjectIdentity,
                    Complete: false),
                request);
        }
    }

    private static string? TryResolveProjectIdentity(DoctorCommandRequest request)
    {
        if (request.Scope != DoctorScope.Project)
        {
            return null;
        }

        try
        {
            return ProjectContextResolver.ResolveProjectRoot(
                new ProjectResolutionRequest(
                    request.ProjectRoot,
                    DocumentName: null,
                    StartDirectory: request.StartDirectory));
        }
        catch (ProjectManifestException)
        {
            return string.IsNullOrWhiteSpace(request.ProjectRoot)
                ? null
                : Path.GetFullPath(request.ProjectRoot);
        }
    }
}
