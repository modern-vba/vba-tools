using VbaDev.App.Projects;

namespace VbaDev.App.Diagnostics;

/// <summary>
/// Collects project and machine diagnostics for the doctor command.
/// </summary>
public sealed class DoctorDiagnosticPipeline
{
    private readonly ProjectContextResolver projectContextResolver;
    private readonly IReadOnlyList<IDoctorProjectDiagnosticProvider> projectDiagnosticProviders;
    private readonly IEnvironmentDiagnosticPort environmentDiagnosticPort;

    /// <summary>
    /// Creates a doctor diagnostic pipeline.
    /// </summary>
    /// <param name="projectContextResolver">The resolver used to locate project manifests.</param>
    /// <param name="projectDiagnosticProviders">The project diagnostic providers to run when a project is found.</param>
    /// <param name="environmentDiagnosticPort">The machine and host diagnostic port.</param>
    public DoctorDiagnosticPipeline(
        ProjectContextResolver projectContextResolver,
        IReadOnlyList<IDoctorProjectDiagnosticProvider> projectDiagnosticProviders,
        IEnvironmentDiagnosticPort environmentDiagnosticPort)
    {
        this.projectContextResolver = projectContextResolver;
        this.projectDiagnosticProviders = projectDiagnosticProviders;
        this.environmentDiagnosticPort = environmentDiagnosticPort;
    }

    /// <summary>
    /// Runs all applicable doctor diagnostics.
    /// </summary>
    /// <param name="request">The doctor command request.</param>
    /// <returns>The collected diagnostic results.</returns>
    public IReadOnlyList<DiagnosticResult> Run(DoctorCommandRequest request)
    {
        var results = new List<DiagnosticResult>();
        var project = TryResolveProject(request, results);
        if (project is null)
        {
            AddSkippedProjectDiagnostics(results);
        }
        else
        {
            results.Add(DiagnosticResult.Pass("Project manifest", $"Loaded {project.ManifestPath}."));
            foreach (var provider in projectDiagnosticProviders)
            {
                provider.AddDiagnostics(results, project);
            }
        }

        results.AddRange(environmentDiagnosticPort.RunEnvironmentDiagnostics());
        return results;
    }

    private ResolvedProject? TryResolveProject(
        DoctorCommandRequest request,
        List<DiagnosticResult> results)
    {
        try
        {
            return projectContextResolver.ResolveProject(new ProjectResolutionRequest(
                request.ProjectRoot,
                null,
                request.StartDirectory));
        }
        catch (ProjectManifestException ex) when (request.ProjectRoot is null && ex.Message.Contains("walking upward", StringComparison.Ordinal))
        {
            return null;
        }
        catch (ProjectManifestException ex)
        {
            results.Add(DiagnosticResult.Fail("Project manifest", ex.Message));
            return null;
        }
    }

    private static void AddSkippedProjectDiagnostics(List<DiagnosticResult> results)
    {
        results.Add(DiagnosticResult.Skip("Project manifest", "No vba-project.json was found; project diagnostics were skipped."));
        results.Add(DiagnosticResult.Skip("Document paths", "No ProjectManifest was resolved."));
        results.Add(DiagnosticResult.Skip("CommonModulesRepository", "No ProjectManifest was resolved."));
        results.Add(DiagnosticResult.Skip("Command defaults", "No ProjectManifest was resolved."));
    }
}

/// <summary>
/// Adds one family of project diagnostics to a doctor report.
/// </summary>
public interface IDoctorProjectDiagnosticProvider
{
    /// <summary>
    /// Adds diagnostics for a resolved project.
    /// </summary>
    /// <param name="results">The report results to append to.</param>
    /// <param name="project">The resolved project to inspect.</param>
    void AddDiagnostics(List<DiagnosticResult> results, ResolvedProject project);
}
