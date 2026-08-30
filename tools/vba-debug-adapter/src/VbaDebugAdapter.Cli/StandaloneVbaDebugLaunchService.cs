using System.Collections.Immutable;
using VbaDebugAdapter.Build;
using VbaDebugAdapter.Debugging;
using VbaDebugAdapter.Infrastructure;

namespace VbaDebugAdapter.Cli;

public sealed class StandaloneVbaDebugLaunchService : IStandaloneVbaDebugLaunchService
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

    public void ValidateForLaunch(StandaloneVbaDebugLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validatedSnapshot = snapshotValidator.Validate(request.SourceSnapshot);
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
            .ToArray();
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
    }

    public async Task<IStandaloneVbaDebugRunningSession> LaunchAsync(
        string vbaDevPath,
        IVbaDebugSessionWorkspaceLease workspaceLease,
        StandaloneVbaDebugLaunchRequest request,
        CancellationToken cancellationToken,
        IDebugLifecycleSink? lifecycleSink = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validatedSnapshot = snapshotValidator.Validate(request.SourceSnapshot);
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
            .ToArray();
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
        var buildResult = await workbookBuilder.BuildAsync(
            vbaDevPath,
            workspaceLease,
            new VbaDevSnapshotBuildRequest(
                request.ProjectRoot,
                request.DocumentName,
                request.WorkbookFileName,
                request.SourceSnapshot)
            {
                GenerationId = DebugGenerationId.FromValue(
                    request.RestartPreparation?.Generation.Value ?? 0)
            },
            cancellationToken).ConfigureAwait(false);
        IVbeDebugSession? visibleSession = null;
        try
        {
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
                .OpenGeneratedWorkbookAsync(
                    lifecycleSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (requiresConditionalCompilationPreflight)
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
                    mappedBreakpoints,
                    environment);
            }
            if (mappedBreakpoints.Length != 0)
            {
                await visibleSession
                    .SetNativeBreakpointsAsync(mappedBreakpoints, cancellationToken)
                    .ConfigureAwait(false);
            }
            await visibleSession
                .RunTargetAsync(
                    launchRequest.Target,
                    lifecycleSink,
                    cancellationToken)
                .ConfigureAwait(false);
            return new StandaloneVbaDebugRunningSession(
                visibleSession,
                mappedBreakpoints,
                launchRequest.Target.ModuleName,
                launchRequest.Target.ProcedureName);
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
