using VbaDebugAdapter.Debugging;

namespace VbaDebugAdapter.Infrastructure;

internal sealed class DebugModalPromptPhase : IDebugModalPromptPhase
{
    private readonly IDebugModalWindowApi windowApi;
    private readonly DebugModalPromptObservation observation;
    private readonly Task<DebugProcessExit> processCompletion;
    private readonly IDebugInputWaitSink inputWaitSink;
    private readonly CancellationTokenSource stopping = new();
    private int disposed;

    public DebugModalPromptPhase(
        IDebugModalWindowApi windowApi,
        DebugInputWait inputWait,
        Task<DebugProcessExit> processCompletion,
        IDebugInputWaitSink inputWaitSink)
    {
        this.windowApi = windowApi;
        this.processCompletion = processCompletion;
        this.inputWaitSink = inputWaitSink;
        observation = new DebugModalPromptObservation(inputWait,
            windowApi.CaptureVisibleModalWindows(inputWait.ProcessId));
        Completion = ObserveAsync();
        ObserveFailure(Completion);
    }

    public Task Completion { get; }

    public async Task<T> ObserveOperationAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var running = operation();
        ObserveFailure(running);
        _ = await Task.WhenAny(running, Completion, processCompletion).WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (Completion.IsFaulted) { await Completion.ConfigureAwait(false); }
        if (!running.IsCompleted && processCompletion.IsCompleted)
        {
            var exit = await processCompletion.ConfigureAwait(false);
            var phase = observation.InputWait.Phase == DebugInputWaitPhase.WorkbookOpen
                ? "workbook open" : "target start";
            throw new DebugSetupException(
                $"Owned Excel process {observation.InputWait.ProcessId} exited with code " +
                $"{exit.ExitCode} before the {phase} operation completed.");
        }
        return await running.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            stopping.Cancel();
        }
        try { await Completion.ConfigureAwait(false); }
        finally { stopping.Dispose(); }
    }

    private async Task ObserveAsync()
    {
        var token = stopping.Token;
        try
        {
            while (!processCompletion.IsCompleted)
            {
                token.ThrowIfCancellationRequested();
                var windows = windowApi.CaptureVisibleModalWindows(observation.InputWait.ProcessId);
                if (observation.TryMarkNewModalWindows(windows))
                {
                    var notification = inputWaitSink.InputRequiredAsync(observation.InputWait, token).AsTask();
                    ObserveFailure(notification);
                    await notification.WaitAsync(token).ConfigureAwait(false);
                }
                var next = windowApi.WaitForNextObservationAsync(token);
                ObserveFailure(next);
                _ = await Task.WhenAny(processCompletion, next).WaitAsync(token).ConfigureAwait(false);
                if (next.IsCompleted) { await next.ConfigureAwait(false); }
            }
            _ = await processCompletion.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private static void ObserveFailure(Task task)
        => _ = task.ContinueWith(static completed => _ = completed.Exception,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
}
