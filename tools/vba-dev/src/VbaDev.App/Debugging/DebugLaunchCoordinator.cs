using VbaDev.App.Projects;

namespace VbaDev.App.Debugging;

/// <summary>
/// Identifies the explicit standard-module procedure selected for a VBE debug launch.
/// </summary>
/// <param name="ModuleName">The VBA module identity.</param>
/// <param name="ProcedureName">The VBA procedure name.</param>
public sealed record DebugTargetProcedure(string ModuleName, string ProcedureName);

/// <summary>
/// Contains the resolved workbook document and target for one debug launch.
/// </summary>
/// <param name="Context">The resolved workbook-backed document context.</param>
/// <param name="Target">The resolved procedure target.</param>
/// <param name="SourceSnapshot">The immutable saved source state used to resolve the target.</param>
public sealed record DebugLaunchRequest(
    ResolvedProjectContext Context,
    DebugTargetProcedure Target,
    DebugSourceSnapshot SourceSnapshot);

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
/// Builds the manifest-selected document before visible Excel starts.
/// </summary>
public interface IDebugWorkbookBuilder
{
    /// <summary>
    /// Builds the selected document and returns non-fatal build output.
    /// </summary>
    Task<DebugWorkbookBuildResult> BuildAsync(
        ResolvedProjectContext context,
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
    /// Opens the exact generated workbook before native debug commands are prepared.
    /// </summary>
    Task OpenGeneratedWorkbookAsync(
        string workbookPath,
        CancellationToken cancellationToken);

    /// <summary>
    /// Establishes the exact VBE command context and sets one native breakpoint.
    /// </summary>
    Task SetNativeBreakpointAsync(
        VbeBreakpoint breakpoint,
        CancellationToken cancellationToken);

    /// <summary>
    /// Establishes the target VBE command context and invokes the native Run command.
    /// </summary>
    Task RunTargetAsync(
        DebugTargetProcedure target,
        CancellationToken cancellationToken);

    /// <summary>
    /// Terminates the owned Excel process. The operation is idempotent and cannot be cancelled by the caller.
    /// </summary>
    ValueTask TerminateAsync();
}

/// <summary>
/// Receives user-visible debug lifecycle output without depending on a transport.
/// </summary>
public interface IDebugLaunchEventSink
{
    /// <summary>
    /// Writes one lifecycle message.
    /// </summary>
    ValueTask WriteAsync(DebugLifecycleMessage message, CancellationToken cancellationToken);

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
            var buildResult = await workbookBuilder
                .BuildAsync(request.Context, cancellationToken)
                .ConfigureAwait(false);
            foreach (var output in buildResult.Output)
            {
                await eventSink
                    .WriteAsync(new DebugLifecycleMessage(output), cancellationToken)
                    .ConfigureAwait(false);
            }

            var session = await vbeDebugSessionFactory.StartVisibleAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await session.OpenGeneratedWorkbookAsync(
                    request.Context.BinDocumentPath,
                    cancellationToken).ConfigureAwait(false);

                if (!request.SourceSnapshot.Breakpoints.IsDefaultOrEmpty)
                {
                    foreach (var sourceBreakpoint in request.SourceSnapshot.Breakpoints)
                    {
                        var vbeBreakpoint = breakpointSourceMapper.Map(
                            request.SourceSnapshot,
                            sourceBreakpoint);
                        await session.SetNativeBreakpointAsync(
                            vbeBreakpoint,
                            cancellationToken).ConfigureAwait(false);
                        await eventSink.BreakpointVerifiedAsync(
                            vbeBreakpoint,
                            cancellationToken).ConfigureAwait(false);
                    }
                }

                await session.RunTargetAsync(request.Target, cancellationToken).ConfigureAwait(false);
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
