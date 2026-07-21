using VbaDev.App.Projects;

namespace VbaDev.App.Debugging;

/// <summary>
/// Identifies the explicit standard-module procedure selected for a VBE debug launch.
/// </summary>
/// <param name="ModuleName">The VBA module identity.</param>
/// <param name="ProcedureName">The VBA procedure name.</param>
public sealed record DebugTargetProcedure(string ModuleName, string ProcedureName)
{
    /// <summary>
    /// Gets the exact structural conditional-compilation path containing the procedure declaration.
    /// </summary>
    public VbaLanguageServer.Syntax.VbaConditionalCompilationBranchPath ConditionalCompilationPath
    {
        get;
        init;
    } = VbaLanguageServer.Syntax.VbaConditionalCompilationBranchPath.Root;
}

/// <summary>
/// Contains the resolved workbook document and target for one debug launch.
/// </summary>
/// <param name="Context">The resolved workbook-backed document context.</param>
/// <param name="Target">The resolved procedure target.</param>
/// <param name="SourceSnapshot">The immutable saved source state used to resolve the target.</param>
public sealed record DebugLaunchRequest(
    ResolvedProjectContext Context,
    DebugTargetProcedure Target,
    DebugSourceSnapshot SourceSnapshot)
{
    /// <summary>
    /// Gets the breakpoint participation decision frozen for this launch.
    /// </summary>
    public DebugBreakpointPlan BreakpointPlan { get; init; } =
        new(SourceSnapshot.Breakpoints, []);
}

/// <summary>
/// Contains the non-fatal output produced by a completed debug build.
/// </summary>
/// <param name="Output">The build output lines to report to the debug client.</param>
public sealed record DebugWorkbookBuildResult(IReadOnlyList<string> Output);

/// <summary>
/// Contains the final exit code of the owned debug Excel process.
/// </summary>
/// <param name="ExitCode">The process exit code.</param>
public sealed record DebugProcessExit(int ExitCode);

/// <summary>
/// Represents one lifecycle message emitted while launching or monitoring Excel.
/// </summary>
/// <param name="Output">The text to display in the debug client.</param>
public sealed record DebugLifecycleMessage(string Output);

/// <summary>
/// Identifies the native UI that currently requires user input.
/// </summary>
public enum DebugInputWaitKind
{
    Excel,
    Vbe,
    ExcelOrVbe
}

/// <summary>
/// Identifies the launch operation blocked by native user input.
/// </summary>
public enum DebugInputWaitPhase
{
    WorkbookOpen,
    TargetStart
}

/// <summary>
/// Reports a detected modal prompt owned by the exact debug Excel process.
/// </summary>
/// <param name="Kind">The native UI that owns the prompt.</param>
/// <param name="Phase">The launch phase waiting for the prompt.</param>
/// <param name="ProcessId">The exact owned Excel process identifier.</param>
public sealed record DebugInputWait(
    DebugInputWaitKind Kind,
    DebugInputWaitPhase Phase,
    int ProcessId)
{
    /// <summary>
    /// Creates the user-facing lifecycle message for this wait state.
    /// </summary>
    public DebugLifecycleMessage ToLifecycleMessage()
    {
        var owner = Kind switch
        {
            DebugInputWaitKind.Excel => "Excel",
            DebugInputWaitKind.Vbe => "the VBE",
            _ => "Excel/VBE"
        };
        var operation = Phase == DebugInputWaitPhase.WorkbookOpen
            ? "opening the generated workbook"
            : "starting the debug target";
        return new DebugLifecycleMessage(
            $"Owned Excel process {ProcessId} is waiting for {owner} input while {operation}. " +
            "Respond to the visible prompt or stop debugging.");
    }
}

/// <summary>
/// Builds the manifest-selected document before visible Excel starts.
/// </summary>
public interface IDebugWorkbookBuilder
{
    /// <summary>
    /// Builds the selected document from the immutable saved source snapshot.
    /// </summary>
    Task<DebugWorkbookBuildResult> BuildAsync(
        ResolvedProjectContext context,
        DebugSourceSnapshot sourceSnapshot,
        CancellationToken cancellationToken);
}

/// <summary>
/// Starts an owned visible Excel process for VBE automation.
/// </summary>
public interface IVbeDebugSessionFactory
{
    /// <summary>
    /// Starts visible Excel and establishes exact process ownership before returning.
    /// </summary>
    Task<IVbeDebugSession> StartVisibleAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Represents one owned visible Excel/VBE automation session.
/// </summary>
public interface IVbeDebugSession : IAsyncDisposable
{
    /// <summary>
    /// Gets the exact owned Excel process identifier.
    /// </summary>
    int ProcessId { get; }

    /// <summary>
    /// Gets a task that completes when the owned Excel process exits.
    /// </summary>
    Task<DebugProcessExit> Completion { get; }

    /// <summary>
    /// Reads the actual Excel, VBE, operating-system, and process-architecture facts
    /// that determine the host compiler constants.
    /// </summary>
    Task<DebugCompilationHostFacts> GetCompilationHostFactsAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// Opens the exact generated workbook before native debug commands are prepared and reports native input waits.
    /// </summary>
    Task OpenGeneratedWorkbookAsync(
        string workbookPath,
        IDebugInputWaitSink? inputWaitSink,
        CancellationToken cancellationToken);

    /// <summary>
    /// Verifies all generated source maps before setting the participating native breakpoints.
    /// </summary>
    Task SetNativeBreakpointsAsync(
        IReadOnlyList<VbeBreakpoint> breakpoints,
        CancellationToken cancellationToken);

    /// <summary>
    /// Establishes the target VBE command context, invokes the native Run command, and reports native input waits.
    /// </summary>
    Task RunTargetAsync(
        DebugTargetProcedure target,
        IDebugInputWaitSink? inputWaitSink,
        CancellationToken cancellationToken);

    /// <summary>
    /// Terminates the owned Excel process. The operation is idempotent and cannot be cancelled by the caller.
    /// </summary>
    ValueTask TerminateAsync();
}

/// <summary>
/// Receives notification that native Excel/VBE input is required.
/// </summary>
public interface IDebugInputWaitSink
{
    /// <summary>
    /// Reports that the exact owned Excel process has displayed a modal prompt.
    /// </summary>
    ValueTask InputRequiredAsync(
        DebugInputWait inputWait,
        CancellationToken cancellationToken);
}

/// <summary>
/// Receives user-visible debug lifecycle output without depending on a transport.
/// </summary>
public interface IDebugLaunchEventSink : IDebugInputWaitSink
{
    /// <summary>
    /// Writes one lifecycle message.
    /// </summary>
    ValueTask WriteAsync(DebugLifecycleMessage message, CancellationToken cancellationToken);

    /// <inheritdoc />
    ValueTask IDebugInputWaitSink.InputRequiredAsync(
        DebugInputWait inputWait,
        CancellationToken cancellationToken)
        => WriteAsync(inputWait.ToLifecycleMessage(), cancellationToken);

    /// <summary>
    /// Reports that an exact source mapping has been transferred through the native VBE command.
    /// </summary>
    ValueTask BreakpointVerifiedAsync(
        VbeBreakpoint breakpoint,
        CancellationToken cancellationToken);
}

/// <summary>
/// Preserves the lifecycle-only sink contract for zero-breakpoint callers.
/// </summary>
public interface IDebugLifecycleSink : IDebugLaunchEventSink
{
    /// <inheritdoc />
    ValueTask IDebugLaunchEventSink.BreakpointVerifiedAsync(
        VbeBreakpoint breakpoint,
        CancellationToken cancellationToken)
        => ValueTask.CompletedTask;
}

/// <summary>
/// Coordinates the build and visible Excel setup phases of a VBE debug launch.
/// </summary>
public sealed class DebugLaunchCoordinator
{
    private readonly IDebugWorkbookBuilder workbookBuilder;
    private readonly IVbeDebugSessionFactory vbeDebugSessionFactory;
    private readonly IBreakpointSourceMapper breakpointSourceMapper;
    private readonly IDebugCompilationSettingsReader? compilationSettingsReader;
    private readonly DebugCompilationEnvironmentFactory? compilationEnvironmentFactory;
    private readonly DebugConditionalCompilationPreflight? conditionalCompilationPreflight;
    private int activeLaunch;

    /// <summary>
    /// Creates a debug launch coordinator over workbook build and visible Excel ports.
    /// </summary>
    public DebugLaunchCoordinator(
        IDebugWorkbookBuilder workbookBuilder,
        IVbeDebugSessionFactory vbeDebugSessionFactory)
        : this(workbookBuilder, vbeDebugSessionFactory, new BreakpointSourceMapper())
    {
    }

    /// <summary>
    /// Creates a debug launch coordinator over workbook build, visible Excel, and source-map ports.
    /// </summary>
    public DebugLaunchCoordinator(
        IDebugWorkbookBuilder workbookBuilder,
        IVbeDebugSessionFactory vbeDebugSessionFactory,
        IBreakpointSourceMapper breakpointSourceMapper)
    {
        this.workbookBuilder = workbookBuilder;
        this.vbeDebugSessionFactory = vbeDebugSessionFactory;
        this.breakpointSourceMapper = breakpointSourceMapper;
    }

    /// <summary>
    /// Creates a debug launch coordinator that can prove actual conditional-compilation branches.
    /// </summary>
    public DebugLaunchCoordinator(
        IDebugWorkbookBuilder workbookBuilder,
        IVbeDebugSessionFactory vbeDebugSessionFactory,
        IBreakpointSourceMapper breakpointSourceMapper,
        IDebugCompilationSettingsReader compilationSettingsReader,
        DebugCompilationEnvironmentFactory compilationEnvironmentFactory,
        DebugConditionalCompilationPreflight conditionalCompilationPreflight)
        : this(workbookBuilder, vbeDebugSessionFactory, breakpointSourceMapper)
    {
        this.compilationSettingsReader = compilationSettingsReader
            ?? throw new ArgumentNullException(nameof(compilationSettingsReader));
        this.compilationEnvironmentFactory = compilationEnvironmentFactory
            ?? throw new ArgumentNullException(nameof(compilationEnvironmentFactory));
        this.conditionalCompilationPreflight = conditionalCompilationPreflight
            ?? throw new ArgumentNullException(nameof(conditionalCompilationPreflight));
    }

    /// <summary>
    /// Builds and starts one explicit VBE debug target.
    /// </summary>
    public async Task<DebugRunningSession> LaunchAsync(
        DebugLaunchRequest request,
        IDebugLaunchEventSink eventSink,
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref activeLaunch, 1, 0) != 0)
        {
            throw new DebugLaunchBusyException();
        }

        try
        {
            ValidateBreakpointPlan(request.BreakpointPlan);
            var mappedBreakpoints = request.BreakpointPlan.Participating.IsDefaultOrEmpty
                ? []
                : request.BreakpointPlan.Participating
                    .Select(sourceBreakpoint => breakpointSourceMapper.Map(
                        request.SourceSnapshot,
                        sourceBreakpoint))
                    .ToArray();
            var requiresConditionalCompilationPreflight =
                request.Target.ConditionalCompilationPath.Branches.Count != 0
                || mappedBreakpoints.Any(breakpoint =>
                    breakpoint.ConditionalCompilationPath.Branches.Count != 0);

            var buildResult = await workbookBuilder
                .BuildAsync(request.Context, request.SourceSnapshot, cancellationToken)
                .ConfigureAwait(false);
            foreach (var output in buildResult.Output)
            {
                await eventSink
                    .WriteAsync(new DebugLifecycleMessage(output), cancellationToken)
                    .ConfigureAwait(false);
            }

            DebugCompilationSettings? builtCompilationSettings = null;
            if (requiresConditionalCompilationPreflight)
            {
                EnsureConditionalCompilationServices();
                builtCompilationSettings = compilationSettingsReader!.Read(
                    request.Context.BinDocumentPath);
            }

            var session = await vbeDebugSessionFactory.StartVisibleAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await session.OpenGeneratedWorkbookAsync(
                    request.Context.BinDocumentPath,
                    eventSink,
                    cancellationToken).ConfigureAwait(false);

                if (requiresConditionalCompilationPreflight)
                {
                    var openedCompilationSettings = compilationSettingsReader!.Read(
                        request.Context.BinDocumentPath);
                    if (!openedCompilationSettings.VbaProjectPartSha256.Equals(
                        builtCompilationSettings!.VbaProjectPartSha256,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        throw new DebugSetupException(
                            "The generated workbook VBA project changed between the completed debug build " +
                            "and the exact workbook opened in Excel. Conditional-compilation source " +
                            "identities cannot be proved.");
                    }

                    var hostFacts = await session
                        .GetCompilationHostFactsAsync(cancellationToken)
                        .ConfigureAwait(false);
                    var environment = compilationEnvironmentFactory!.Create(
                        builtCompilationSettings,
                        hostFacts);
                    conditionalCompilationPreflight!.Validate(
                        request,
                        mappedBreakpoints,
                        environment);
                }

                if (mappedBreakpoints.Length != 0)
                {
                    await session.SetNativeBreakpointsAsync(
                        mappedBreakpoints,
                        cancellationToken).ConfigureAwait(false);
                    foreach (var vbeBreakpoint in mappedBreakpoints)
                    {
                        await eventSink.BreakpointVerifiedAsync(
                            vbeBreakpoint,
                            cancellationToken).ConfigureAwait(false);
                    }
                }

                await session.RunTargetAsync(
                    request.Target,
                    eventSink,
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await session.TerminateAsync().ConfigureAwait(false);
                await session.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            return new DebugRunningSession(session, ReleaseLaunch);
        }
        catch
        {
            ReleaseLaunch();
            throw;
        }
    }

    private static void ValidateBreakpointPlan(DebugBreakpointPlan breakpointPlan)
    {
        if (!breakpointPlan.Unsupported.IsDefaultOrEmpty)
        {
            var unsupported = breakpointPlan.Unsupported.Select(item =>
                $"{item.Kind}: {item.Description}");
            throw new DebugSetupException(
                "VBA debug launch contains unsupported breakpoint participation: " +
                string.Join("; ", unsupported));
        }

        if (breakpointPlan.Participating.IsDefaultOrEmpty)
        {
            return;
        }

        var linesBySource = new Dictionary<string, HashSet<int>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var breakpoint in breakpointPlan.Participating)
        {
            if (!linesBySource.TryGetValue(breakpoint.SourcePath, out var lines))
            {
                lines = [];
                linesBySource.Add(breakpoint.SourcePath, lines);
            }

            if (!lines.Add(breakpoint.EditorLine))
            {
                throw new DebugSetupException(
                    $"VBA debug launch contains a duplicate participating breakpoint at " +
                    $"'{breakpoint.SourcePath}:{breakpoint.EditorLine + 1}'.");
            }
        }
    }

    private void EnsureConditionalCompilationServices()
    {
        if (compilationSettingsReader is null
            || compilationEnvironmentFactory is null
            || conditionalCompilationPreflight is null)
        {
            throw new DebugSetupException(
                "Conditional-compilation debug participants require actual generated-workbook and " +
                "Excel/VBE compiler context, but those services are not configured.");
        }
    }

    private void ReleaseLaunch() => Interlocked.Exchange(ref activeLaunch, 0);
}

/// <summary>
/// Exposes the owned process lifetime after launch setup has completed.
/// </summary>
public sealed class DebugRunningSession : IAsyncDisposable
{
    private readonly IVbeDebugSession session;

    internal DebugRunningSession(IVbeDebugSession session, Action releaseLaunch)
    {
        this.session = session;
        Completion = CompleteAndDisposeAsync(session, releaseLaunch);
    }

    /// <summary>
    /// Gets the owned Excel process identifier.
    /// </summary>
    public int ProcessId => session.ProcessId;

    /// <summary>
    /// Gets a task that completes after Excel exits and session resources are released.
    /// </summary>
    public Task<DebugProcessExit> Completion { get; }

    /// <summary>
    /// Force-terminates the owned Excel process.
    /// </summary>
    public ValueTask TerminateAsync() => session.TerminateAsync();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await session.TerminateAsync().ConfigureAwait(false);
        await Completion.ConfigureAwait(false);
    }

    private static async Task<DebugProcessExit> CompleteAndDisposeAsync(
        IVbeDebugSession session,
        Action releaseLaunch)
    {
        try
        {
            return await session.Completion.ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                releaseLaunch();
            }
        }
    }
}
