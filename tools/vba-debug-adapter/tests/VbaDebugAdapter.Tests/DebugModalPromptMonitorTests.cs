using VbaDebugAdapter.Debugging;
using VbaDebugAdapter.Infrastructure;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class DebugModalPromptMonitorTests
{
    [Fact]
    public async Task ANewModalWindowForTheExactOwnedProcessReportsInputAndDoesNotCompleteTheOperation()
    {
        var windowApi = new SequenceDebugModalWindowApi(
            [new HashSet<nint> { 100 }, new HashSet<nint> { 100 }, new HashSet<nint> { 100, 200 }]);
        var monitor = new DebugModalPromptMonitor(windowApi);
        var inputWait = new DebugInputWait(
            DebugInputWaitKind.Excel,
            DebugInputWaitPhase.WorkbookOpen,
            31415);
        var operation = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var processCompletion = new TaskCompletionSource<DebugProcessExit>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var inputWaits = new List<DebugInputWait>();
        var inputReported = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new CallbackDebugInputWaitSink(wait =>
        {
            inputWaits.Add(wait);
            inputReported.TrySetResult();
        });

        await using var phase = monitor.BeginPhase(inputWait, processCompletion.Task, sink);
        var observed = phase.ObserveOperationAsync(() => operation.Task, CancellationToken.None);

        await inputReported.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal([inputWait], inputWaits);
        Assert.False(observed.IsCompleted);
        Assert.All(windowApi.ProcessIds, processId => Assert.Equal(31415, processId));

        operation.TrySetResult(42);
        Assert.Equal(42, await observed);
    }

    [Fact]
    public async Task APreExistingModalWindowIsNotReportedBeforeTheOperationCompletes()
    {
        var operation = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var windowApi = new SequenceDebugModalWindowApi(
            [new HashSet<nint> { 100 }, new HashSet<nint> { 100 }],
            onWait: () => operation.TrySetResult(7));
        var monitor = new DebugModalPromptMonitor(windowApi);
        var inputWait = new DebugInputWait(
            DebugInputWaitKind.Vbe,
            DebugInputWaitPhase.TargetStart,
            27182);
        var sink = new RecordingDebugInputWaitSink();

        await using var phase = monitor.BeginPhase(inputWait, new TaskCompletionSource<DebugProcessExit>().Task, sink);
        var result = await phase.ObserveOperationAsync(() => operation.Task, CancellationToken.None);

        Assert.Equal(7, result);
        Assert.Empty(sink.InputWaits);
    }

    [Fact]
    public async Task AModalShownLongAfterTheNativeRunCommandReturnsIsObservedUntilProcessExit()
    {
        var snapshots = new List<IReadOnlySet<nint>>
        {
            new HashSet<nint>()
        };
        snapshots.AddRange(Enumerable.Range(0, 8).Select(_ =>
            (IReadOnlySet<nint>)new HashSet<nint>()));
        snapshots.Add(new HashSet<nint> { 200 });
        var windowApi = new SequenceDebugModalWindowApi(snapshots);
        var monitor = new DebugModalPromptMonitor(windowApi);
        var inputWait = new DebugInputWait(
            DebugInputWaitKind.ExcelOrVbe,
            DebugInputWaitPhase.TargetStart,
            27183);
        var processCompletion = new TaskCompletionSource<DebugProcessExit>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var inputWaits = new List<DebugInputWait>();
        var sink = new CallbackDebugInputWaitSink(wait =>
        {
            inputWaits.Add(wait);
            processCompletion.TrySetResult(new DebugProcessExit(0));
        });

        await using var phase = monitor.BeginPhase(inputWait, processCompletion.Task, sink);
        await phase.Completion;

        Assert.Equal([inputWait], inputWaits);
        Assert.True(windowApi.ProcessIds.Count > 5);
    }

    [Fact]
    public async Task ACompletedOwnedProcessDoesNotLeaveACancelledObservationWaitingForTheOperation()
    {
        var monitor = new DebugModalPromptMonitor(
            new SequenceDebugModalWindowApi([new HashSet<nint>()]));
        var inputWait = new DebugInputWait(
            DebugInputWaitKind.Excel,
            DebugInputWaitPhase.WorkbookOpen,
            16180);
        var operation = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await using var phase = monitor.BeginPhase(inputWait,
            Task.FromResult(new DebugProcessExit(-1)), new RecordingDebugInputWaitSink());
        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            phase.ObserveOperationAsync(() => operation.Task, cancellation.Token)
                .WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task AnOwnedProcessExitEndsObservationWhenTheOperationNeverReturns()
    {
        var monitor = new DebugModalPromptMonitor(
            new SequenceDebugModalWindowApi([new HashSet<nint>()]));
        var inputWait = new DebugInputWait(
            DebugInputWaitKind.Vbe,
            DebugInputWaitPhase.TargetStart,
            27182);
        var operation = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var phase = monitor.BeginPhase(inputWait,
            Task.FromResult(new DebugProcessExit(9)), new RecordingDebugInputWaitSink());
        var exception = await Assert.ThrowsAsync<DebugSetupException>(() =>
            phase.ObserveOperationAsync(() => operation.Task, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.Contains("27182", exception.Message, StringComparison.Ordinal);
        Assert.Contains("9", exception.Message, StringComparison.Ordinal);
        Assert.Contains("target start", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StopAfterAReportedModalDoesNotWaitForTheBlockedOperation()
    {
        var windowApi = new SequenceDebugModalWindowApi(
            [new HashSet<nint>(), new HashSet<nint> { 200 }]);
        var monitor = new DebugModalPromptMonitor(windowApi);
        var inputWait = new DebugInputWait(
            DebugInputWaitKind.Vbe,
            DebugInputWaitPhase.TargetStart,
            31415);
        var operation = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var processCompletion = new TaskCompletionSource<DebugProcessExit>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new RecordingDebugInputWaitSink();
        using var cancellation = new CancellationTokenSource();

        await using var phase = monitor.BeginPhase(inputWait, processCompletion.Task, sink);
        var observed = phase.ObserveOperationAsync(() => operation.Task, cancellation.Token);
        Assert.Equal([inputWait], sink.InputWaits);

        cancellation.Cancel();
        processCompletion.TrySetResult(new DebugProcessExit(-1));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            observed.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task ReturningFromRunRetainsNotificationsAndTheNextPhaseTakesAFreshBaseline()
    {
        var windows = new ControlledDebugModalWindowApi();
        var monitor = new DebugModalPromptMonitor(windows);
        var notifications = System.Threading.Channels.Channel.CreateUnbounded<DebugInputWait>();
        var sink = new CallbackDebugInputWaitSink(wait => notifications.Writer.TryWrite(wait));
        var process = new TaskCompletionSource<DebugProcessExit>();
        var openWait = new DebugInputWait(DebugInputWaitKind.ExcelOrVbe, DebugInputWaitPhase.WorkbookOpen, 42);
        await using (var open = monitor.BeginPhase(openWait, process.Task, sink))
        {
            await open.ObserveOperationAsync(() => Task.FromResult(0), CancellationToken.None);
        }
        _ = windows.Show(100);
        var runWait = openWait with { Phase = DebugInputWaitPhase.TargetStart };
        await using var run = monitor.BeginPhase(runWait, process.Task, sink);
        var operation = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedRun = run.ObserveOperationAsync(() => operation.Task, CancellationToken.None);
        await windows.Show(100, 200).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(runWait, await notifications.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));

        operation.SetResult(7);
        Assert.Equal(7, await observedRun);
        await windows.Show(100, 200).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(notifications.Reader.TryRead(out _));
        await windows.Show(100, 200, 300).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(runWait, await notifications.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.False(notifications.Reader.TryRead(out _));
        Assert.False(run.Completion.IsCompleted);
    }

    private sealed class ControlledDebugModalWindowApi : IDebugModalWindowApi
    {
        private readonly System.Threading.Channels.Channel<bool> ticks =
            System.Threading.Channels.Channel.CreateUnbounded<bool>();
        private WindowSnapshot windows = new(new HashSet<nint>());
        private WindowSnapshot? observed;

        public Task Show(params nint[] handles)
        {
            var snapshot = new WindowSnapshot(new HashSet<nint>(handles));
            Volatile.Write(ref windows, snapshot);
            ticks.Writer.TryWrite(true);
            return snapshot.Seen.Task;
        }

        public IReadOnlySet<nint> CaptureVisibleModalWindows(int processId)
        {
            observed = Volatile.Read(ref windows);
            return observed.Handles;
        }

        public async Task WaitForNextObservationAsync(CancellationToken cancellationToken)
        {
            observed?.Seen.TrySetResult();
            _ = await ticks.Reader.ReadAsync(cancellationToken);
        }

        private sealed record WindowSnapshot(IReadOnlySet<nint> Handles)
        {
            public TaskCompletionSource Seen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private sealed class SequenceDebugModalWindowApi(
        IEnumerable<IReadOnlySet<nint>> snapshots,
        Action? onWait = null) : IDebugModalWindowApi
    {
        private readonly Queue<IReadOnlySet<nint>> remaining = new(snapshots);
        private IReadOnlySet<nint> last = new HashSet<nint>();

        public System.Collections.Concurrent.ConcurrentQueue<int> ProcessIds { get; } = [];

        public IReadOnlySet<nint> CaptureVisibleModalWindows(int processId)
        {
            ProcessIds.Enqueue(processId);
            if (remaining.Count != 0)
            {
                last = remaining.Dequeue();
            }

            return last;
        }

        public async Task WaitForNextObservationAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onWait?.Invoke();
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private sealed class CallbackDebugInputWaitSink(Action<DebugInputWait> callback)
        : IDebugInputWaitSink
    {
        public ValueTask InputRequiredAsync(
            DebugInputWait inputWait,
            CancellationToken cancellationToken)
        {
            callback(inputWait);
            return ValueTask.CompletedTask;
        }
    }
}
