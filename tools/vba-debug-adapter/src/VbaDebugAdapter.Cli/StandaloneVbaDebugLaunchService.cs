using System.Collections.Immutable;
using VbaDebugAdapter.Build;
using VbaDebugAdapter.Debugging;
using VbaDebugAdapter.Infrastructure;

namespace VbaDebugAdapter.Cli;

internal sealed class StandaloneVbaDebugLaunchService : IStandaloneVbaDebugLaunchService
{
    private readonly TransportedDebugSourceSnapshotValidator snapshotValidator;
    private readonly IVbaDebugWorkbookBuilder workbookBuilder;
    private readonly IVbeDebugSessionFactory vbeDebugSessionFactory;
    private readonly DebugLaunchRequestResolver launchRequestResolver = new();
    private readonly IBreakpointSourceMapper breakpointSourceMapper;
    private readonly IDebugCompilationSettingsReader? compilationSettingsReader;
    private readonly DebugCompilationEnvironmentFactory? compilationEnvironmentFactory;
    private readonly DebugConditionalCompilationPreflight? conditionalCompilationPreflight;

    public StandaloneVbaDebugLaunchService(
        TransportedDebugSourceSnapshotValidator snapshotValidator,
        IVbaDebugWorkbookBuilder workbookBuilder,
        IVbeDebugSessionFactory vbeDebugSessionFactory,
        IBreakpointSourceMapper? breakpointSourceMapper = null,
        IDebugCompilationSettingsReader? compilationSettingsReader = null,
        DebugCompilationEnvironmentFactory? compilationEnvironmentFactory = null,
        DebugConditionalCompilationPreflight? conditionalCompilationPreflight = null)
    {
        this.snapshotValidator = snapshotValidator
            ?? throw new ArgumentNullException(nameof(snapshotValidator));
        this.workbookBuilder = workbookBuilder
            ?? throw new ArgumentNullException(nameof(workbookBuilder));
        this.vbeDebugSessionFactory = vbeDebugSessionFactory
            ?? throw new ArgumentNullException(nameof(vbeDebugSessionFactory));
        this.breakpointSourceMapper = breakpointSourceMapper ?? new BreakpointSourceMapper();
        this.compilationSettingsReader = compilationSettingsReader;
        this.compilationEnvironmentFactory = compilationEnvironmentFactory;
        this.conditionalCompilationPreflight = conditionalCompilationPreflight;
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
        cancellationToken.ThrowIfCancellationRequested();
        var frozenTransport = Freeze(request.SourceSnapshot);
        var validatedSnapshot = snapshotValidator.Validate(frozenTransport);
        var sourceSnapshot = new DebugSourceSnapshot(
            validatedSnapshot.SchemaVersion,
            validatedSnapshot.Sources
                .Where(source => source.Text is not null)
                .Select(source => new DebugSourceFileSnapshot(
                    source.RelativePath,
                    source.SourceUri!,
                    source.Text!))
                .ToImmutableArray(),
            validatedSnapshot.ActiveSource is null
                ? null
                : new DebugSourcePosition(
                    validatedSnapshot.ActiveSource.SourceUri,
                    validatedSnapshot.ActiveSource.Line,
                    validatedSnapshot.ActiveSource.Character))
        {
            Breakpoints = validatedSnapshot.Breakpoints
                .Select(breakpoint => new DebugSourceBreakpoint(
                    breakpoint.SourceUri,
                    breakpoint.Line))
                .ToImmutableArray()
        };
        var launchRequest = launchRequestResolver.Resolve(
            sourceSnapshot,
            request.ModuleName,
            request.ProcedureName);
        var mappedBreakpoints = sourceSnapshot.Breakpoints
            .Select(breakpoint => breakpointSourceMapper.Map(sourceSnapshot, breakpoint))
            .ToImmutableArray();
        var requiresConditionalCompilationPreflight =
            launchRequest.Target.ConditionalCompilationPath.Branches.Count != 0 ||
            mappedBreakpoints.Any(breakpoint =>
                breakpoint.ConditionalCompilationPath.Branches.Count != 0);
        if (requiresConditionalCompilationPreflight &&
            (compilationSettingsReader is null ||
             compilationEnvironmentFactory is null ||
             conditionalCompilationPreflight is null))
        {
            throw new DebugSetupException(
                "Conditional-compilation debug participants require generated-workbook and " +
                "visible Excel/VBE compiler context services.");
        }
        var canonicalProjectRoot = CanonicalizeProjectRoot(request.ProjectRoot);
        ValidateRestartBinding(
            request,
            canonicalProjectRoot,
            launchRequest.Target,
            workspaceLease.SessionId,
            restartBinding);
        var generationId = DebugGenerationId.FromValue(
            request.RestartPreparation?.Generation.Value ?? 0);
        VbaDevSnapshotBuildResult? buildResult = null;
        try
        {
            buildResult = await workbookBuilder.BuildAsync(
                vbaDevPath,
                workspaceLease,
                new VbaDevSnapshotBuildRequest(
                    canonicalProjectRoot,
                    request.DocumentName,
                    request.WorkbookFileName,
                    frozenTransport)
                {
                    GenerationId = generationId
                },
                cancellationToken).ConfigureAwait(false);
            if (buildResult.GenerationId != generationId)
            {
                throw new DebugSetupException(
                    "The prepared debug generation does not match the requested launch generation.");
            }
            if (lifecycleSink is not null)
            {
                foreach (var output in buildResult.Output)
                {
                    await lifecycleSink
                        .WriteAsync(new DebugLifecycleMessage(output), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            var builtCompilationSettings = requiresConditionalCompilationPreflight
                ? compilationSettingsReader!.Read(buildResult.WorkbookPath)
                : null;
            var snapshot = new PreparedDebugLaunchPlanSnapshot(
                frozenTransport,
                sourceSnapshot.ActiveSource,
                launchRequest.Target,
                mappedBreakpoints,
                requiresConditionalCompilationPreflight,
                generationId,
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
                launchRequest,
                builtCompilationSettings,
                vbeDebugSessionFactory,
                compilationSettingsReader,
                compilationEnvironmentFactory,
                conditionalCompilationPreflight,
                lifecycleSink);
            buildResult = null;
            return plan;
        }
        catch
        {
            if (buildResult is not null)
            {
                await TryDisposeBuildResultAsync(buildResult).ConfigureAwait(false);
            }
            throw;
        }
    }

    private static TransportedDebugSourceSnapshot Freeze(
        TransportedDebugSourceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new TransportedDebugSourceSnapshot(
            snapshot.SchemaVersion,
            snapshot.Sources.Select(source => source with { }).ToImmutableArray())
        {
            ActiveSource = snapshot.ActiveSource is null
                ? null
                : snapshot.ActiveSource with { },
            Breakpoints = snapshot.Breakpoints
                .Select(breakpoint => breakpoint with { })
                .ToImmutableArray()
        };
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

    private static void ValidateRestartBinding(
        StandaloneVbaDebugLaunchRequest request,
        string canonicalProjectRoot,
        DebugTargetProcedure target,
        DebugSessionId workspaceSessionId,
        DebugRestartLaunchBinding? restartBinding)
    {
        if (restartBinding is null)
        {
            return;
        }

        var descriptor = request.RestartPreparation;
        if (descriptor is null ||
            restartBinding.SessionId != workspaceSessionId ||
            descriptor.Id != restartBinding.PreparationId ||
            descriptor.Generation != restartBinding.Generation ||
            !canonicalProjectRoot.Equals(
                restartBinding.CanonicalProjectRoot,
                StringComparison.OrdinalIgnoreCase) ||
            !request.DocumentName.Equals(
                restartBinding.DocumentName,
                StringComparison.OrdinalIgnoreCase) ||
            !request.WorkbookFileName.Equals(
                restartBinding.WorkbookFileName,
                StringComparison.OrdinalIgnoreCase) ||
            !target.ModuleName.Equals(
                restartBinding.TargetModuleName,
                StringComparison.OrdinalIgnoreCase) ||
            !target.ProcedureName.Equals(
                restartBinding.TargetProcedureName,
                StringComparison.OrdinalIgnoreCase) ||
            (restartBinding.RequestedModuleName is not null &&
             !restartBinding.RequestedModuleName.Equals(
                 restartBinding.TargetModuleName,
                 StringComparison.OrdinalIgnoreCase)) ||
            (restartBinding.RequestedProcedureName is not null &&
             !restartBinding.RequestedProcedureName.Equals(
                 restartBinding.TargetProcedureName,
                 StringComparison.OrdinalIgnoreCase)) ||
            !restartBinding.BoundSession.TargetModuleName.Equals(
                restartBinding.TargetModuleName,
                StringComparison.OrdinalIgnoreCase) ||
            !restartBinding.BoundSession.TargetProcedureName.Equals(
                restartBinding.TargetProcedureName,
                StringComparison.OrdinalIgnoreCase) ||
            restartBinding.DapRequestSequence < 0 ||
            restartBinding.BoundSession.Completion.IsCompleted)
        {
            throw new DebugSetupException(
                "The fresh VBA restart launch does not match its bound session, target, or request identity.");
        }
    }

    private static async Task TryTerminateAndDisposeAsync(IVbeDebugSession session)
    {
        try
        {
            await session.TerminateAsync().ConfigureAwait(false);
        }
        catch
        {
        }
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static async Task TryDisposeBuildResultAsync(
        VbaDevSnapshotBuildResult buildResult)
    {
        try
        {
            await buildResult.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private sealed class PreparedDebugLaunchPlan : IPreparedDebugLaunchPlan
    {
        private const int Prepared = 0;
        private const int Committing = 1;
        private const int Disposed = 2;
        private const int Consumed = 3;

        private readonly VbaDevSnapshotBuildResult buildResult;
        private readonly DebugLaunchRequest launchRequest;
        private readonly DebugCompilationSettings? builtCompilationSettings;
        private readonly IVbeDebugSessionFactory vbeDebugSessionFactory;
        private readonly IDebugCompilationSettingsReader? compilationSettingsReader;
        private readonly DebugCompilationEnvironmentFactory? compilationEnvironmentFactory;
        private readonly DebugConditionalCompilationPreflight? conditionalCompilationPreflight;
        private readonly IDebugLifecycleSink? lifecycleSink;
        private int state;
        private int restartSessionReleased;

        public PreparedDebugLaunchPlanSnapshot Snapshot { get; }

        public bool RestartSessionReleased => Volatile.Read(ref restartSessionReleased) != 0;

        internal PreparedDebugLaunchPlan(
            PreparedDebugLaunchPlanSnapshot snapshot,
            VbaDevSnapshotBuildResult buildResult,
            DebugLaunchRequest launchRequest,
            DebugCompilationSettings? builtCompilationSettings,
            IVbeDebugSessionFactory vbeDebugSessionFactory,
            IDebugCompilationSettingsReader? compilationSettingsReader,
            DebugCompilationEnvironmentFactory? compilationEnvironmentFactory,
            DebugConditionalCompilationPreflight? conditionalCompilationPreflight,
            IDebugLifecycleSink? lifecycleSink)
        {
            Snapshot = snapshot;
            this.buildResult = buildResult;
            this.launchRequest = launchRequest;
            this.builtCompilationSettings = builtCompilationSettings;
            this.vbeDebugSessionFactory = vbeDebugSessionFactory;
            this.compilationSettingsReader = compilationSettingsReader;
            this.compilationEnvironmentFactory = compilationEnvironmentFactory;
            this.conditionalCompilationPreflight = conditionalCompilationPreflight;
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
                IVbaDebugGenerationWorkspace? generationWorkspace =
                    buildResult.TransferGenerationOwnership();
                try
                {
                    visibleSession.AdoptGenerationWorkspace(generationWorkspace);
                    generationWorkspace = null;
                }
                finally
                {
                    if (generationWorkspace is not null)
                    {
                        await generationWorkspace.DisposeAsync().ConfigureAwait(false);
                    }
                }
                await visibleSession
                    .OpenGeneratedWorkbookAsync(lifecycleSink, cancellationToken)
                    .ConfigureAwait(false);
                if (Snapshot.RequiresConditionalCompilationPreflight)
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
                    conditionalCompilationPreflight!.Validate(
                        launchRequest,
                        Snapshot.MappedBreakpoints,
                        environment);
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
                var runningSession = new StandaloneVbaDebugRunningSession(
                    visibleSession,
                    Snapshot.MappedBreakpoints,
                    Snapshot.Target.ModuleName,
                    Snapshot.Target.ProcedureName);
                visibleSession = null;
                return runningSession;
            }
            catch
            {
                if (visibleSession is not null)
                {
                    await TryTerminateAndDisposeAsync(visibleSession).ConfigureAwait(false);
                }
                await TryDisposeBuildResultAsync(buildResult).ConfigureAwait(false);
                throw;
            }
            finally
            {
                Volatile.Write(ref state, Consumed);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.CompareExchange(ref state, Disposed, Prepared) == Prepared)
            {
                await buildResult.DisposeAsync().ConfigureAwait(false);
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
            try
            {
                await runningSession.TerminateAsync().ConfigureAwait(false);
            }
            finally
            {
                await runningSession.DisposeAsync().ConfigureAwait(false);
            }
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
    TransportedDebugSourceSnapshot SourceInventory,
    DebugSourcePosition? ActiveSource,
    DebugTargetProcedure Target,
    ImmutableArray<VbeBreakpoint> MappedBreakpoints,
    bool RequiresConditionalCompilationPreflight,
    DebugGenerationId GenerationId,
    string GenerationWorkspacePath,
    PreparedDebugLaunchSettings LaunchSettings,
    DebugRestartLaunchBinding? RestartBinding);

internal sealed record DebugRestartLaunchBinding(
    DebugSessionId SessionId,
    IStandaloneVbaDebugRunningSession BoundSession,
    string CanonicalProjectRoot,
    string DocumentName,
    string WorkbookFileName,
    string TargetModuleName,
    string TargetProcedureName,
    string? RequestedModuleName,
    string? RequestedProcedureName,
    DebugRestartPreparationId PreparationId,
    DebugRestartGeneration Generation,
    int DapRequestSequence)
{
    public bool IsBoundSessionCurrent =>
        !BoundSession.Completion.IsCompleted &&
        BoundSession.TargetModuleName.Equals(
            TargetModuleName,
            StringComparison.OrdinalIgnoreCase) &&
        BoundSession.TargetProcedureName.Equals(
            TargetProcedureName,
            StringComparison.OrdinalIgnoreCase);

    public bool HasSameIdentityAs(DebugRestartLaunchBinding other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return ReferenceEquals(BoundSession, other.BoundSession) &&
               SessionId == other.SessionId &&
               CanonicalProjectRoot.Equals(
                   other.CanonicalProjectRoot,
                   StringComparison.OrdinalIgnoreCase) &&
               DocumentName.Equals(
                   other.DocumentName,
                   StringComparison.OrdinalIgnoreCase) &&
               WorkbookFileName.Equals(
                   other.WorkbookFileName,
                   StringComparison.OrdinalIgnoreCase) &&
               TargetModuleName.Equals(
                   other.TargetModuleName,
                   StringComparison.OrdinalIgnoreCase) &&
               TargetProcedureName.Equals(
                   other.TargetProcedureName,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   RequestedModuleName,
                   other.RequestedModuleName,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   RequestedProcedureName,
                   other.RequestedProcedureName,
                   StringComparison.OrdinalIgnoreCase) &&
               PreparationId == other.PreparationId &&
               Generation == other.Generation &&
               DapRequestSequence == other.DapRequestSequence;
    }
}

internal interface IPreparedDebugLaunchPlan : IAsyncDisposable
{
    PreparedDebugLaunchPlanSnapshot Snapshot { get; }

    bool RestartSessionReleased { get; }

    Task<IStandaloneVbaDebugRunningSession> CommitAsync(
        DebugRestartLaunchBinding? restartBinding,
        CancellationToken cancellationToken);
}

internal sealed class StandaloneVbaDebugRunningSession : IStandaloneVbaDebugRunningSession
{
    private readonly IVbeDebugSession session;
    private readonly IReadOnlyList<VbeBreakpoint> verifiedBreakpoints;
    private int disposed;

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

    public string TargetModuleName { get; }

    public string TargetProcedureName { get; }

    public IReadOnlyList<VbeBreakpoint> VerifiedBreakpoints => verifiedBreakpoints;

    public ValueTask TerminateAsync() => session.TerminateAsync();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        await session.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task<int> AwaitExitCodeAsync(Task<DebugProcessExit> completion)
        => (await completion.ConfigureAwait(false)).ExitCode;
}
