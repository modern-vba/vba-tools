using System.Collections.Immutable;
using VbaDebugAdapter.Build;
using VbaDebugAdapter.Debugging;
using VbaDebugAdapter.Infrastructure;

namespace VbaDebugAdapter.Cli;

internal sealed class StandaloneVbaDebugLaunchService : IStandaloneVbaDebugLaunchService
{
    private readonly DebugSourceAdmission sourceAdmission;
    private readonly IVbaDebugWorkbookBuilder workbookBuilder;
    private readonly IVbeDebugSessionFactory vbeDebugSessionFactory;
    private readonly IDebugCompilationSettingsReader? compilationSettingsReader;
    private readonly DebugCompilationEnvironmentFactory? compilationEnvironmentFactory;

    internal StandaloneVbaDebugLaunchService(
        DebugSourceAdmission sourceAdmission,
        IVbaDebugWorkbookBuilder workbookBuilder,
        IVbeDebugSessionFactory vbeDebugSessionFactory,
        IDebugCompilationSettingsReader? compilationSettingsReader = null,
        DebugCompilationEnvironmentFactory? compilationEnvironmentFactory = null)
    {
        this.sourceAdmission = sourceAdmission
            ?? throw new ArgumentNullException(nameof(sourceAdmission));
        this.workbookBuilder = workbookBuilder
            ?? throw new ArgumentNullException(nameof(workbookBuilder));
        this.vbeDebugSessionFactory = vbeDebugSessionFactory
            ?? throw new ArgumentNullException(nameof(vbeDebugSessionFactory));
        this.compilationSettingsReader = compilationSettingsReader;
        this.compilationEnvironmentFactory = compilationEnvironmentFactory;
    }

    public async Task<IPreparedDebugLaunchPlan> PrepareAsync(
        string vbaDevPath,
        IVbaDebugSessionWorkspaceLease workspaceLease,
        StandaloneVbaDebugLaunchRequest request,
        DebugRestartLaunchBinding? restartBinding,
        CancellationToken cancellationToken,
        IDebugLifecycleSink? lifecycleSink = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(workspaceLease);
        VbaDevSnapshotBuildResult? buildResult = null;
        var buildStarted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var generationId = DebugGenerationId.FromValue(
                request.RestartPreparation?.Generation.Value ?? 0);
            var admittedSource = sourceAdmission.Admit(
                request.SourceSnapshot,
                request.ModuleName,
                request.ProcedureName,
                generationId);
            var requiresConditionalCompilationVerification =
                admittedSource.RequiresConditionalCompilationVerification;
            if (requiresConditionalCompilationVerification &&
                (compilationSettingsReader is null ||
                 compilationEnvironmentFactory is null))
            {
                throw new DebugSetupException(
                    "Conditional-compilation debug participants require generated-workbook and " +
                    "visible Excel/VBE compiler context services.");
            }
            var canonicalProjectRoot = CanonicalizeProjectRoot(request.ProjectRoot);
            restartBinding?.ValidateLaunch(
                request,
                canonicalProjectRoot,
                admittedSource.Target,
                workspaceLease.SessionId);
            buildStarted = true;
            buildResult = await workbookBuilder.BuildAsync(
                vbaDevPath,
                workspaceLease,
                new VbaDevSnapshotBuildRequest(
                    canonicalProjectRoot,
                    request.DocumentName,
                    request.WorkbookFileName,
                    admittedSource.BuildSources),
                cancellationToken).ConfigureAwait(false);
            if (buildResult.GenerationId != generationId)
            {
                throw new DebugSetupException(
                    "The prepared debug generation does not match the requested launch generation.");
            }
            if (lifecycleSink is not null)
            {
                if (buildResult.Report is not null)
                {
                    await lifecycleSink.WriteAsync(new DebugLifecycleMessage(
                        "VBA snapshot build completed; source diagnostics were collected.")
                    {
                        SnapshotBuild = buildResult.Report
                    }, cancellationToken).ConfigureAwait(false);
                }
                foreach (var output in buildResult.Output)
                {
                    await lifecycleSink
                        .WriteAsync(new DebugLifecycleMessage(output), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            var builtCompilationSettings = requiresConditionalCompilationVerification
                ? compilationSettingsReader!.Read(buildResult.WorkbookPath)
                : null;
            var snapshot = new PreparedDebugLaunchPlanSnapshot(
                admittedSource,
                buildResult.GenerationWorkspacePath,
                new PreparedDebugLaunchSettings(
                    canonicalProjectRoot,
                    request.DocumentName,
                    request.WorkbookFileName,
                    request.ModuleName,
                    request.ProcedureName,
                    request.RestartPreparation is null
                        ? null
                        : request.RestartPreparation with { }),
                restartBinding);
            var plan = new PreparedDebugLaunchPlan(
                snapshot,
                buildResult,
                builtCompilationSettings,
                vbeDebugSessionFactory,
                compilationSettingsReader,
                compilationEnvironmentFactory,
                lifecycleSink);
            buildResult = null;
            return plan;
        }
        catch (Exception exception)
        {
            var completion = new DebugFailureCompletion(exception);
            var primary = exception is IDebugFailureEvidence evidence
                ? evidence.FailureOutcome.PrimaryFailure : exception;
            if (lifecycleSink is not null && primary is SnapshotBuildFailedException buildFailure)
            {
                try
                {
                    await lifecycleSink.WriteAsync(new DebugLifecycleMessage(
                        "VBA snapshot build failed; source diagnostics were collected.")
                    {
                        SnapshotBuild = buildFailure.Report
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception reportFailure)
                {
                    completion.AddFailure("build-diagnostics", "snapshot build report", DebugResourceKind.Observation, reportFailure);
                }
            }
            if (buildResult is not null)
            {
                await CompleteBuildFailureAsync(buildResult, completion).ConfigureAwait(false);
            }
            else if (!buildStarted)
            {
                foreach (var kind in new[] { DebugResourceKind.Process, DebugResourceKind.Com, DebugResourceKind.Handle })
                {
                    completion.AddEvidence(new("preparation-admission", "debug preparation", kind, true,
                        "Admission failed before any build or visible-session resources were acquired."));
                }
            }
            else if (exception is not IDebugFailureEvidence)
            {
                foreach (var kind in new[] { DebugResourceKind.Process, DebugResourceKind.Handle })
                {
                    completion.AddEvidence(new("preparation-build", "snapshot build", kind, false,
                        "The failed build supplied no owner release evidence."));
                }
            }
            completion.Complete().ThrowWithEvidence();
            throw;
        }
    }

    private static string CanonicalizeProjectRoot(string projectRoot)
    {
        try
        {
            if (!Path.IsPathFullyQualified(projectRoot))
            {
                throw new DebugSetupException(
                    "The VBA launch project must be an absolute path.");
            }
            return Path.GetFullPath(projectRoot);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new DebugSetupException(
                "The VBA launch project must be a valid absolute path.");
        }
    }

    private static async Task CompleteSessionFailureAsync(
        IVbeDebugSession session,
        DebugFailureCompletion completion)
    {
        try
        {
            await session.TerminateAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            completion.AddFailure("session-termination", "visible Excel", DebugResourceKind.Process,
                exception, session.ProcessId);
        }
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            completion.AddFailure("session-disposal", "visible Excel", DebugResourceKind.Handle,
                exception, session.ProcessId);
        }
        var ownerOutcome = (session as IDebugResourceOwnerEvidence)?.CleanupOutcome;
        if (ownerOutcome is not null)
        {
            completion.Merge(ownerOutcome);
        }
        foreach (var kind in new[] { DebugResourceKind.Process, DebugResourceKind.Com, DebugResourceKind.Handle })
        {
            if (ownerOutcome?.Evidence.Any(item => item.Kind == kind) != true)
            {
                completion.AddEvidence(new("session-cleanup", "visible Excel", kind, false,
                    "The session owner did not supply terminal release evidence.", session.ProcessId));
            }
        }
    }

    private static async Task CompleteBuildFailureAsync(
        VbaDevSnapshotBuildResult buildResult,
        DebugFailureCompletion completion)
    {
        try
        {
            await buildResult.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            completion.AddFailure("prepared-generation-cleanup", buildResult.GenerationWorkspacePath,
                DebugResourceKind.FileSystem, exception, retainedPath: buildResult.GenerationWorkspacePath);
        }
        if (((IDebugResourceOwnerEvidence)buildResult).CleanupOutcome is { } outcome)
        {
            completion.Merge(outcome);
        }
    }

    private sealed class PreparedDebugLaunchPlan : IPreparedDebugLaunchPlan, IDebugResourceOwnerEvidence
    {
        private const int Prepared = 0;
        private const int Committing = 1;
        private const int Disposed = 2;
        private const int Consumed = 3;

        private readonly VbaDevSnapshotBuildResult buildResult;
        private readonly DebugCompilationSettings? builtCompilationSettings;
        private readonly IVbeDebugSessionFactory vbeDebugSessionFactory;
        private readonly IDebugCompilationSettingsReader? compilationSettingsReader;
        private readonly DebugCompilationEnvironmentFactory? compilationEnvironmentFactory;
        private readonly IDebugLifecycleSink? lifecycleSink;
        private readonly object disposalGate = new();
        private readonly TaskCompletionSource commitCleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? disposal;
        private DebugFailureOutcome? cleanupOutcome;
        private int state;
        private int restartSessionReleased;

        public PreparedDebugLaunchPlanSnapshot Snapshot { get; }

        public bool RestartSessionReleased => Volatile.Read(ref restartSessionReleased) != 0;

        public DebugFailureOutcome? CleanupOutcome =>
            cleanupOutcome ?? ((IDebugResourceOwnerEvidence)buildResult).CleanupOutcome;

        internal PreparedDebugLaunchPlan(
            PreparedDebugLaunchPlanSnapshot snapshot,
            VbaDevSnapshotBuildResult buildResult,
            DebugCompilationSettings? builtCompilationSettings,
            IVbeDebugSessionFactory vbeDebugSessionFactory,
            IDebugCompilationSettingsReader? compilationSettingsReader,
            DebugCompilationEnvironmentFactory? compilationEnvironmentFactory,
            IDebugLifecycleSink? lifecycleSink)
        {
            Snapshot = snapshot;
            this.buildResult = buildResult;
            this.builtCompilationSettings = builtCompilationSettings;
            this.vbeDebugSessionFactory = vbeDebugSessionFactory;
            this.compilationSettingsReader = compilationSettingsReader;
            this.compilationEnvironmentFactory = compilationEnvironmentFactory;
            this.lifecycleSink = lifecycleSink;
        }

        public async Task<IStandaloneVbaDebugRunningSession> CommitAsync(
            DebugRestartLaunchBinding? restartBinding,
            CancellationToken cancellationToken)
        {
            var previousState = Interlocked.CompareExchange(
                ref state,
                Committing,
                Prepared);
            if (previousState != Prepared)
            {
                throw new InvalidOperationException(previousState == Disposed
                    ? "The prepared debug launch plan has been disposed."
                    : "The prepared debug launch plan has already been committed or consumed.");
            }

            IVbeDebugSession? visibleSession = null;
            try
            {
                ValidateCommitBinding(Snapshot.RestartBinding, restartBinding);
                cancellationToken.ThrowIfCancellationRequested();
                if (Snapshot.RestartBinding is { } boundRestart)
                {
                    Volatile.Write(ref restartSessionReleased, 1);
                    await StopBoundSessionAsync(boundRestart.BoundSession).ConfigureAwait(false);
                }

                visibleSession = await vbeDebugSessionFactory
                    .StartVisibleAsync(cancellationToken)
                    .ConfigureAwait(false);
                var generationWorkspace = buildResult.TransferGenerationOwnership();
                try
                {
                    visibleSession.AdoptGenerationWorkspace(generationWorkspace);
                }
                catch (Exception adoptionFailure)
                {
                    var completion = new DebugFailureCompletion(adoptionFailure);
                    try
                    {
                        await generationWorkspace.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception cleanupFailure)
                    {
                        completion.AddFailure("generation-adoption-cleanup", generationWorkspace.GenerationWorkspacePath,
                            DebugResourceKind.FileSystem, cleanupFailure,
                            retainedPath: generationWorkspace.GenerationWorkspacePath);
                    }
                    if (generationWorkspace is IDebugResourceOwnerEvidence { CleanupOutcome: { } outcome })
                    {
                        completion.Merge(outcome);
                    }
                    completion.Complete().ThrowWithEvidence();
                    throw;
                }
                await visibleSession
                    .OpenGeneratedWorkbookAsync(lifecycleSink, cancellationToken)
                    .ConfigureAwait(false);
                if (Snapshot.RequiresConditionalCompilationVerification)
                {
                    var openedCompilationSettings = compilationSettingsReader!.Read(
                        buildResult.WorkbookPath);
                    if (!openedCompilationSettings.VbaProjectPartSha256.Equals(
                            builtCompilationSettings!.VbaProjectPartSha256,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new DebugSetupException(
                            "The generated workbook VBA project changed between the completed debug build " +
                            "and the exact workbook opened in Excel.");
                    }
                    var hostFacts = await visibleSession
                        .GetCompilationHostFactsAsync(cancellationToken)
                        .ConfigureAwait(false);
                    var environment = compilationEnvironmentFactory!.Create(
                        builtCompilationSettings,
                        hostFacts);
                    Snapshot.SourceAdmission.VerifyConditionalCompilation(environment);
                }
                if (!Snapshot.MappedBreakpoints.IsEmpty)
                {
                    await visibleSession
                        .SetNativeBreakpointsAsync(
                            Snapshot.MappedBreakpoints,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                await visibleSession
                    .RunTargetAsync(
                        Snapshot.Target,
                        lifecycleSink,
                        cancellationToken)
                    .ConfigureAwait(false);
                await buildResult.DisposeAsync().ConfigureAwait(false);
                var runningSession = new StandaloneVbaDebugRunningSession(
                    visibleSession,
                    Snapshot.MappedBreakpoints,
                    Snapshot.Target.ModuleName,
                    Snapshot.Target.ProcedureName);
                visibleSession = null;
                return runningSession;
            }
            catch (Exception exception)
            {
                var completion = new DebugFailureCompletion(exception);
                if (visibleSession is not null)
                {
                    await CompleteSessionFailureAsync(visibleSession, completion).ConfigureAwait(false);
                }
                await CompleteBuildFailureAsync(buildResult, completion).ConfigureAwait(false);
                var outcome = completion.Complete();
                cleanupOutcome = outcome;
                if (outcome.HasCleanupFailure)
                {
                    var retainedFailure = new DebugFailureException(outcome);
                    lock (disposalGate)
                    {
                        disposal = Task.FromException(retainedFailure);
                        _ = disposal.Exception;
                        commitCleanup.TrySetException(retainedFailure);
                        _ = commitCleanup.Task.Exception;
                    }
                    throw retainedFailure;
                }
                outcome.Throw();
                throw;
            }
            finally
            {
                Volatile.Write(ref state, Consumed);
                commitCleanup.TrySetResult();
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (disposalGate)
            {
                if (disposal is null && Interlocked.CompareExchange(ref state, Disposed, Prepared) == Prepared)
                {
                    disposal = buildResult.DisposeAsync().AsTask();
                }
                return new ValueTask(disposal ?? commitCleanup.Task);
            }
        }

        private static void ValidateCommitBinding(
            DebugRestartLaunchBinding? expected,
            DebugRestartLaunchBinding? actual)
        {
            if (expected is null && actual is null)
            {
                return;
            }
            if (expected is null || actual is null ||
                !expected.HasSameIdentityAs(actual) ||
                !expected.IsBoundSessionCurrent)
            {
                throw new DebugSetupException(
                    "The prepared VBA restart launch binding is stale.");
            }
        }

        private static async Task StopBoundSessionAsync(
            IStandaloneVbaDebugRunningSession runningSession)
        {
            Exception? primary = null;
            try
            {
                await runningSession.TerminateAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                primary = exception;
            }
            var completion = new DebugFailureCompletion(primary);
            try
            {
                await runningSession.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                completion.AddFailure("restart-session-disposal", "bound Excel session",
                    DebugResourceKind.Handle, exception, runningSession.ProcessId);
            }
            var ownerOutcome = (runningSession as IDebugResourceOwnerEvidence)?.CleanupOutcome;
            if (ownerOutcome is not null) { completion.Merge(ownerOutcome); }
            foreach (var kind in new[] { DebugResourceKind.Process, DebugResourceKind.Com, DebugResourceKind.Handle })
            {
                if (ownerOutcome?.Evidence.Any(item => item.Kind == kind) != true)
                {
                    completion.AddEvidence(new("restart-session-cleanup", "bound Excel session", kind,
                        false, "The bound session owner did not supply terminal release evidence.", runningSession.ProcessId));
                }
            }
            completion.Complete().ThrowWithEvidence();
        }
    }

}

internal sealed record PreparedDebugLaunchSettings(
    string CanonicalProjectRoot,
    string DocumentName,
    string WorkbookFileName,
    string? RequestedModuleName,
    string? RequestedProcedureName,
    RestartPreparationDescriptor? RestartPreparation);

internal sealed record PreparedDebugLaunchPlanSnapshot(
    AdmittedDebugSourceSnapshot SourceAdmission,
    string GenerationWorkspacePath,
    PreparedDebugLaunchSettings LaunchSettings,
    DebugRestartLaunchBinding? RestartBinding)
{
    internal DebugSourcePosition? ActiveSource => SourceAdmission.ActiveSource;

    internal DebugTargetProcedure Target => SourceAdmission.Target;

    internal ImmutableArray<VbeBreakpoint> MappedBreakpoints =>
        SourceAdmission.MappedBreakpoints;

    internal bool RequiresConditionalCompilationVerification =>
        SourceAdmission.RequiresConditionalCompilationVerification;

    internal DebugGenerationId GenerationId => SourceAdmission.GenerationId;
}

internal interface IPreparedDebugLaunchPlan : IAsyncDisposable
{
    PreparedDebugLaunchPlanSnapshot Snapshot { get; }

    bool RestartSessionReleased { get; }

    Task<IStandaloneVbaDebugRunningSession> CommitAsync(
        DebugRestartLaunchBinding? restartBinding,
        CancellationToken cancellationToken);
}

internal sealed class StandaloneVbaDebugRunningSession : IStandaloneVbaDebugRunningSession, IDebugResourceOwnerEvidence
{
    private readonly IVbeDebugSession session;
    private readonly IReadOnlyList<VbeBreakpoint> verifiedBreakpoints;
    private readonly object disposalGate = new();
    private Task? disposal;

    public StandaloneVbaDebugRunningSession(
        IVbeDebugSession session,
        IReadOnlyList<VbeBreakpoint> verifiedBreakpoints,
        string targetModuleName,
        string targetProcedureName)
    {
        this.session = session;
        this.verifiedBreakpoints = verifiedBreakpoints;
        TargetModuleName = targetModuleName;
        TargetProcedureName = targetProcedureName;
        Completion = AwaitExitCodeAsync(session.Completion);
    }

    public Task<int> Completion { get; }

    public int ProcessId => session.ProcessId;

    public DebugFailureOutcome? CleanupOutcome =>
        (session as IDebugResourceOwnerEvidence)?.CleanupOutcome;

    public string TargetModuleName { get; }

    public string TargetProcedureName { get; }

    public IReadOnlyList<VbeBreakpoint> VerifiedBreakpoints => verifiedBreakpoints;

    public ValueTask TerminateAsync() => session.TerminateAsync();

    public ValueTask DisposeAsync()
    {
        lock (disposalGate)
        {
            return new ValueTask(disposal ??= DisposeSessionAsync());
        }
    }

    private async Task DisposeSessionAsync()
        => await session.DisposeAsync().ConfigureAwait(false);

    private static async Task<int> AwaitExitCodeAsync(Task<DebugProcessExit> completion)
        => (await completion.ConfigureAwait(false)).ExitCode;
}
