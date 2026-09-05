using VbaDev.App.Projects;
using VbaDev.App.Workbooks;

namespace VbaDev.App.Diagnostics;

/// <summary>
/// Collects project and machine diagnostics for the doctor command.
/// </summary>
public sealed class DoctorDiagnosticPipeline
{
    private const long JsonSafeIntegerMax = 9_007_199_254_740_991;

    /// <summary>
    /// Stable environment-readiness check identities in execution order.
    /// </summary>
    public static IReadOnlyList<string> EnvironmentCheckIds { get; } =
    [
        "platform.windows",
        "excel.comStartup",
        "excel.processOwnership",
        "excel.vbideProjectAccess",
        "excel.processCleanup"
    ];

    private readonly ProjectContextResolver projectContextResolver;
    private readonly IReadOnlyList<IDoctorProjectDiagnosticProvider> projectDiagnosticProviders;
    private readonly IReadOnlyList<IActiveDoctorProjectDiagnosticProvider> activeProjectDiagnosticProviders;
    private readonly IProjectMaterializationDiagnosticPort projectMaterializationDiagnosticPort;
    private readonly IEnvironmentDiagnosticPort environmentDiagnosticPort;
    private readonly VbaSourceAdmission sourceAdmission;

    /// <summary>
    /// Creates a doctor diagnostic pipeline.
    /// </summary>
    /// <param name="projectContextResolver">The resolver used to locate project manifests.</param>
    /// <param name="projectDiagnosticProviders">The project diagnostic providers to run when a project is found.</param>
    /// <param name="environmentDiagnosticPort">The machine and host diagnostic port.</param>
    public DoctorDiagnosticPipeline(
        ProjectContextResolver projectContextResolver,
        IReadOnlyList<IDoctorProjectDiagnosticProvider> projectDiagnosticProviders,
        IReadOnlyList<IActiveDoctorProjectDiagnosticProvider> activeProjectDiagnosticProviders,
        IProjectMaterializationDiagnosticPort projectMaterializationDiagnosticPort,
        IEnvironmentDiagnosticPort environmentDiagnosticPort)
        : this(projectContextResolver, projectDiagnosticProviders, activeProjectDiagnosticProviders,
            projectMaterializationDiagnosticPort, environmentDiagnosticPort,
            new VbaSourceAdmission(ActiveWindowsAnsiCodePage.Get))
    {
    }

    internal DoctorDiagnosticPipeline(
        ProjectContextResolver projectContextResolver,
        IReadOnlyList<IDoctorProjectDiagnosticProvider> projectDiagnosticProviders,
        IReadOnlyList<IActiveDoctorProjectDiagnosticProvider> activeProjectDiagnosticProviders,
        IProjectMaterializationDiagnosticPort projectMaterializationDiagnosticPort,
        IEnvironmentDiagnosticPort environmentDiagnosticPort,
        VbaSourceAdmission sourceAdmission)
    {
        this.projectContextResolver = projectContextResolver;
        this.projectDiagnosticProviders = projectDiagnosticProviders;
        this.activeProjectDiagnosticProviders = activeProjectDiagnosticProviders;
        this.projectMaterializationDiagnosticPort = projectMaterializationDiagnosticPort;
        this.environmentDiagnosticPort = environmentDiagnosticPort;
        this.sourceAdmission = sourceAdmission;
    }

    /// <summary>
    /// Runs all applicable doctor diagnostics.
    /// </summary>
    /// <param name="request">The doctor command request.</param>
    /// <returns>The completed diagnostic run.</returns>
    public async Task<DoctorDiagnosticRun> RunAsync(
        DoctorCommandRequest request,
        CancellationToken cancellationToken)
    {
        var results = new List<DiagnosticResult>();
        var project = TryResolveProject(
            request,
            results,
            out var projectIdentity);
        if (project is null)
        {
            AddSkippedProjectDiagnostics(results);
        }
        else
        {
            results.Add(DiagnosticResult.Pass("Project manifest", $"Loaded {project.ManifestPath}."));
            var sources = DoctorProjectSourceInspection.Capture(project, sourceAdmission, cancellationToken);
            foreach (var provider in projectDiagnosticProviders)
            {
                if (provider is IDoctorSourceDiagnosticProvider sourceProvider)
                {
                    sourceProvider.AddDiagnostics(results, project, sources);
                }
                else
                {
                    provider.AddDiagnostics(results, project);
                }
            }

            try
            {
                foreach (var provider in activeProjectDiagnosticProviders)
                {
                    await provider.AddDiagnosticsAsync(
                        results,
                        project,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException exception)
            {
                results.Add(DiagnosticResult.Unverified(
                    "project.activeDiagnostics",
                    $"Active project diagnostics were canceled: {exception.Message}"));
                results.AddRange(CreateUnavailableEnvironmentResults(
                    "Project diagnostics were canceled before active environment evidence could be collected."));
                return new DoctorDiagnosticRun(
                    results,
                    projectIdentity,
                    Complete: false,
                    Canceled: true);
            }

            var materializationRun = projectMaterializationDiagnosticPort is IDoctorSourceMaterializationDiagnosticPort sourcePort
                ? await sourcePort.RunAsync(project, sources, cancellationToken).ConfigureAwait(false)
                : await projectMaterializationDiagnosticPort.RunAsync(project, cancellationToken).ConfigureAwait(false);
            results.AddRange(materializationRun.Results);
            if (!materializationRun.Complete)
            {
                results.AddRange(CreateUnavailableEnvironmentResults(
                    "Project materialization did not complete before active environment evidence could be collected."));
                return new DoctorDiagnosticRun(
                    results,
                    projectIdentity,
                    Complete: false,
                    materializationRun.Canceled);
            }
        }

        EnvironmentDiagnosticRun environmentRun;
        try
        {
            environmentRun = await RunCanonicalEnvironmentDiagnosticsAsync(
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            environmentRun = new EnvironmentDiagnosticRun(
                CreateIncompleteEnvironmentResults(exception),
                Complete: false);
        }

        results.AddRange(environmentRun.Results);
        return new DoctorDiagnosticRun(
            results,
            projectIdentity,
            environmentRun.Complete,
            environmentRun.Canceled);
    }

    /// <summary>
    /// Runs only project-independent environment diagnostics.
    /// </summary>
    /// <returns>The completed environment diagnostic run.</returns>
    public async Task<DoctorDiagnosticRun> RunEnvironmentAsync(
        CancellationToken cancellationToken)
    {
        var environmentRun = await RunCanonicalEnvironmentDiagnosticsAsync(
            cancellationToken).ConfigureAwait(false);
        return new DoctorDiagnosticRun(
            environmentRun.Results,
            Project: null,
            environmentRun.Complete,
            environmentRun.Canceled);
    }

    private async Task<EnvironmentDiagnosticRun> RunCanonicalEnvironmentDiagnosticsAsync(
        CancellationToken cancellationToken)
    {
        var run = await environmentDiagnosticPort
            .RunEnvironmentDiagnosticsAsync(cancellationToken)
            .ConfigureAwait(false);
        var observedFailure = run.Results.Any(result =>
            result.Status == DiagnosticStatus.Fail);
        var malformedEnvironmentEvidence = run.Results.Any(result =>
            !EnvironmentCheckIds.Contains(result.Id, StringComparer.Ordinal));
        var earlierEnvironmentBlocker = false;
        var ownedExcelStarted = false;
        var results = EnvironmentCheckIds
            .Select(checkId =>
            {
                var matches = run.Results
                    .Where(candidate => candidate.Id.Equals(checkId, StringComparison.Ordinal))
                    .Take(2)
                    .ToArray();
                DiagnosticResult result;
                if (matches.Length == 1)
                {
                    result = matches[0];
                    if (result.DurationMilliseconds is < 0 or > JsonSafeIntegerMax)
                    {
                        malformedEnvironmentEvidence = true;
                        result = DiagnosticResult.Unverified(
                            checkId,
                            "The environment diagnostic adapter reported a duration outside the JSON safe-integer range.");
                    }
                    else if (string.IsNullOrWhiteSpace(result.Message))
                    {
                        malformedEnvironmentEvidence = true;
                        result = DiagnosticResult.Unverified(
                            checkId,
                            "The environment diagnostic adapter reported a blank check message.");
                    }
                    else if (result.Details.Count > 0 &&
                             !HasConsistentEnvironmentDetails(result))
                    {
                        malformedEnvironmentEvidence = true;
                        result = DiagnosticResult.Unverified(
                            checkId,
                            "The environment diagnostic adapter reported machine details that contradict the check status.");
                    }
                    else if (result.Status == DiagnosticStatus.Skip &&
                             checkId == "excel.processCleanup" &&
                             ownedExcelStarted)
                    {
                        malformedEnvironmentEvidence = true;
                        result = DiagnosticResult.Unverified(
                            checkId,
                            "The environment diagnostic adapter skipped cleanup after starting an owned Excel process.");
                    }
                    else if (result.Status == DiagnosticStatus.Skip &&
                             !earlierEnvironmentBlocker)
                    {
                        malformedEnvironmentEvidence = true;
                        result = DiagnosticResult.Unverified(
                            checkId,
                            "The environment diagnostic adapter skipped this check without an earlier blocker.");
                    }
                }
                else
                {
                    malformedEnvironmentEvidence = true;
                    result = DiagnosticResult.Unverified(
                        checkId,
                        matches.Length == 0
                            ? "The environment diagnostic adapter omitted this required check."
                            : "The environment diagnostic adapter duplicated this required check.");
                }

                if (checkId is "excel.comStartup" or "excel.processOwnership" &&
                    result.Status == DiagnosticStatus.Pass)
                {
                    ownedExcelStarted = true;
                }

                if (result.Status is DiagnosticStatus.Fail or DiagnosticStatus.Unverified)
                {
                    earlierEnvironmentBlocker = true;
                }

                return EnsureEnvironmentDetails(result);
            })
            .ToArray();
        return run with
        {
            Results = results,
            Complete = run.Complete && !malformedEnvironmentEvidence,
            Canceled = run.Canceled && !observedFailure
        };
    }

    internal static DiagnosticResult EnsureEnvironmentDetails(DiagnosticResult result)
    {
        if (result.Details.Count > 0)
        {
            return result;
        }

        var detailName = GetEnvironmentDetailName(result.Id);
        return result with
        {
            Details = new Dictionary<string, object?>
            {
                [detailName] = GetEnvironmentDetailValue(result.Status)
            }
        };
    }

    private static bool HasConsistentEnvironmentDetails(DiagnosticResult result)
    {
        var detailName = GetEnvironmentDetailName(result.Id);
        return result.Details.TryGetValue(detailName, out var detailValue) &&
               Equals(detailValue, GetEnvironmentDetailValue(result.Status));
    }

    private static string GetEnvironmentDetailName(string checkId)
        => checkId switch
        {
            "platform.windows" => "isWindows",
            "excel.comStartup" => "dedicatedInstanceStarted",
            "excel.processOwnership" => "ownedByInvocation",
            "excel.vbideProjectAccess" => "projectAccessSucceeded",
            "excel.processCleanup" => "ownedProcessReleased",
            _ => throw new ArgumentOutOfRangeException(nameof(checkId), checkId, null)
        };

    private static object? GetEnvironmentDetailValue(DiagnosticStatus status)
        => status switch
        {
            DiagnosticStatus.Pass => true,
            DiagnosticStatus.Fail => false,
            _ => null
        };

    private static IReadOnlyList<DiagnosticResult> CreateUnavailableEnvironmentResults(
        string reason)
        => EnvironmentCheckIds
            .Select((checkId, index) => EnsureEnvironmentDetails(
                index == 0
                    ? DiagnosticResult.Unverified(checkId, reason)
                    : DiagnosticResult.Skip(
                        checkId,
                        "The check was skipped because active environment evidence was unavailable.")))
            .ToArray();

    private static IReadOnlyList<DiagnosticResult> CreateIncompleteEnvironmentResults(
        Exception exception)
        => EnvironmentCheckIds
            .Select((checkId, index) => EnsureEnvironmentDetails(
                index == 0
                    ? DiagnosticResult.Unverified(
                        checkId,
                        $"Doctor infrastructure did not complete: {exception.Message}")
                    : DiagnosticResult.Skip(
                        checkId,
                        "The check was skipped because Doctor infrastructure did not complete.")))
            .ToArray();

    private ResolvedProject? TryResolveProject(
        DoctorCommandRequest request,
        List<DiagnosticResult> results,
        out string? projectIdentity)
    {
        var absoluteStartDirectory = Path.GetFullPath(request.StartDirectory);
        projectIdentity = string.IsNullOrWhiteSpace(request.ProjectRoot)
            ? absoluteStartDirectory
            : Path.GetFullPath(request.ProjectRoot, absoluteStartDirectory);
        try
        {
            var resolutionRequest = new ProjectResolutionRequest(
                request.ProjectRoot,
                null,
                request.StartDirectory);
            projectIdentity = ProjectContextResolver.ResolveProjectRoot(
                resolutionRequest);
            return projectContextResolver.ResolveProject(
                resolutionRequest with { ProjectRoot = projectIdentity });
        }
        catch (ProjectManifestException ex) when (request.ProjectRoot is null && ex.Message.Contains("walking upward", StringComparison.Ordinal))
        {
            results.Add(DiagnosticResult.Fail("Project manifest", ex.Message));
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

/// <summary>
/// Adds active, cancellable project diagnostics to a Doctor report.
/// </summary>
public interface IActiveDoctorProjectDiagnosticProvider
{
    /// <summary>
    /// Adds active diagnostics for a resolved project.
    /// </summary>
    Task AddDiagnosticsAsync(
        List<DiagnosticResult> results,
        ResolvedProject project,
        CancellationToken cancellationToken);
}
