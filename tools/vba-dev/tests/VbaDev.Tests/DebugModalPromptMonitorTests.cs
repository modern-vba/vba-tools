using VbaDev.App.Debugging;
using VbaDev.Infrastructure.Debugging;
using Xunit;

namespace VbaDev.Tests;

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
        var observation = monitor.Capture(inputWait);
        var operation = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var processCompletion = new TaskCompletionSource<DebugProcessExit>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new RecordingDebugInputWaitSink();

        var observed = monitor.ObserveAsync(
            observation,
            operation.Task,
            processCompletion.Task,
            sink,
            CancellationToken.None);

        Assert.Equal([inputWait], sink.InputWaits);
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
        var observation = monitor.Capture(inputWait);
        var sink = new RecordingDebugInputWaitSink();

        var result = await monitor.ObserveAsync(
            observation,
            operation.Task,
            new TaskCompletionSource<DebugProcessExit>().Task,
            sink,
            CancellationToken.None);

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
        var observation = monitor.Capture(inputWait);
        var processCompletion = new TaskCompletionSource<DebugProcessExit>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var inputWaits = new List<DebugInputWait>();
        var sink = new CallbackDebugInputWaitSink(wait =>
        {
            inputWaits.Add(wait);
            processCompletion.TrySetResult(new DebugProcessExit(0));
        });

        await monitor.ObserveUntilProcessExitAsync(
            observation,
            processCompletion.Task,
            sink,
            CancellationToken.None);

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
        var observation = monitor.Capture(inputWait);
        var operation = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            monitor.ObserveAsync(
                    observation,
                    operation.Task,
                    Task.FromResult(new DebugProcessExit(-1)),
                    new RecordingDebugInputWaitSink(),
                    cancellation.Token)
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
        var observation = monitor.Capture(inputWait);
        var operation = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var exception = await Assert.ThrowsAsync<DebugSetupException>(() =>
            monitor.ObserveAsync(
                    observation,
                    operation.Task,
                    Task.FromResult(new DebugProcessExit(9)),
                    new RecordingDebugInputWaitSink(),
                    CancellationToken.None)
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
        var observation = monitor.Capture(inputWait);
        var operation = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var processCompletion = new TaskCompletionSource<DebugProcessExit>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new RecordingDebugInputWaitSink();
        using var cancellation = new CancellationTokenSource();

        var observed = monitor.ObserveAsync(
            observation,
            operation.Task,
            processCompletion.Task,
            sink,
            cancellation.Token);
        Assert.Equal([inputWait], sink.InputWaits);

        cancellation.Cancel();
        processCompletion.TrySetResult(new DebugProcessExit(-1));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            observed.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
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
