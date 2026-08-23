using System.Collections.Immutable;
using VbaDebugAdapter.Build;
using VbaDebugAdapter.Debugging;

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

    public async Task<IStandaloneVbaDebugRunningSession> LaunchAsync(
        string vbaDevPath,
        string sessionId,
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
            sessionId,
            new VbaDevSnapshotBuildRequest(
                request.ProjectRoot,
                request.DocumentName,
                request.WorkbookFileName,
                request.SourceSnapshot),
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
            await visibleSession
                .OpenGeneratedWorkbookAsync(
                    buildResult.WorkbookPath,
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
                buildResult.SessionWorkspacePath,
                mappedBreakpoints);
        }
        catch
        {
            if (visibleSession is not null)
            {
                await TryTerminateAndDisposeAsync(visibleSession).ConfigureAwait(false);
            }
            TryDeleteSessionWorkspace(buildResult.SessionWorkspacePath);
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

    internal static void TryDeleteSessionWorkspace(string workspacePath)
    {
        try
        {
            if (Directory.Exists(workspacePath))
            {
                Directory.Delete(workspacePath, recursive: true);
            }
        }
        catch
        {
        }
    }
}

internal sealed class StandaloneVbaDebugRunningSession : IStandaloneVbaDebugRunningSession
{
    private readonly IVbeDebugSession session;
    private readonly string sessionWorkspacePath;
    private readonly IReadOnlyList<VbeBreakpoint> verifiedBreakpoints;
    private int disposed;

    public StandaloneVbaDebugRunningSession(
        IVbeDebugSession session,
        string sessionWorkspacePath,
        IReadOnlyList<VbeBreakpoint> verifiedBreakpoints)
    {
        this.session = session;
        this.sessionWorkspacePath = sessionWorkspacePath;
        this.verifiedBreakpoints = verifiedBreakpoints;
        Completion = AwaitExitCodeAsync(session.Completion);
    }

    public Task<int> Completion { get; }

    public int ProcessId => session.ProcessId;

    public IReadOnlyList<VbeBreakpoint> VerifiedBreakpoints => verifiedBreakpoints;

    public ValueTask TerminateAsync() => session.TerminateAsync();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            StandaloneVbaDebugLaunchService.TryDeleteSessionWorkspace(
                sessionWorkspacePath);
        }
    }

    private static async Task<int> AwaitExitCodeAsync(Task<DebugProcessExit> completion)
        => (await completion.ConfigureAwait(false)).ExitCode;
}
