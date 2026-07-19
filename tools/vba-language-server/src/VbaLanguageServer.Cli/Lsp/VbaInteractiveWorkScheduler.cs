using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace VbaLanguageServer.Lsp;

internal enum VbaLspRequestIdKind
{
    Number,
    String
}

internal readonly record struct VbaLspRequestId(
    VbaLspRequestIdKind Kind,
    string Value)
{
    public static bool TryCreate(JsonNode? node, out VbaLspRequestId requestId)
    {
        requestId = default;
        if (node is not JsonValue value)
        {
            return false;
        }

        if (value.GetValueKind() == JsonValueKind.String
            && value.TryGetValue<string>(out var stringValue))
        {
            requestId = new VbaLspRequestId(VbaLspRequestIdKind.String, stringValue);
            return true;
        }

        if (value.GetValueKind() == JsonValueKind.Number)
        {
            requestId = new VbaLspRequestId(
                VbaLspRequestIdKind.Number,
                value.ToJsonString());
            return true;
        }

        return false;
    }

    public override string ToString()
        => Kind == VbaLspRequestIdKind.String
            ? $"string:{Value}"
            : $"number:{Value}";
}

internal sealed class VbaDuplicateRequestIdException : InvalidOperationException
{
    public VbaDuplicateRequestIdException(VbaLspRequestId requestId)
        : base($"Request id '{requestId}' already has an active cancellation owner.")
    {
        RequestId = requestId;
    }

    public VbaLspRequestId RequestId { get; }
}

internal readonly record struct VbaInteractiveWorkAdmission(
    long InputSequence,
    long ReadFence,
    Task Completion);

internal enum VbaInteractiveWorkKind
{
    Mutation,
    Request,
    Control
}

internal enum VbaInteractiveStopReason
{
    Complete,
    Abort
}

internal readonly record struct VbaInteractiveWorkAdmissionTiming(
    long InputSequence,
    long ReadFence,
    VbaInteractiveWorkKind Kind,
    string Method,
    VbaLspRequestId? RequestId,
    TimeSpan AdmissionTime);

internal readonly record struct VbaInteractiveWorkCompletionTiming(
    long InputSequence,
    long ReadFence,
    VbaInteractiveWorkKind Kind,
    string Method,
    VbaLspRequestId? RequestId,
    TimeSpan QueueTime,
    TimeSpan ExecutionTime,
    bool Cancelled,
    bool Faulted);

internal readonly record struct VbaInteractiveWorkFailure(
    long InputSequence,
    VbaInteractiveWorkKind Kind,
    string Method,
    VbaLspRequestId? RequestId,
    Exception Exception);

internal interface IVbaInteractiveWorkTimingSink
{
    void RecordAdmission(VbaInteractiveWorkAdmissionTiming timing);

    void RecordCompletion(VbaInteractiveWorkCompletionTiming timing);
}

internal sealed class NullVbaInteractiveWorkTimingSink : IVbaInteractiveWorkTimingSink
{
    public static NullVbaInteractiveWorkTimingSink Instance { get; } = new();

    private NullVbaInteractiveWorkTimingSink()
    {
    }

    public void RecordAdmission(VbaInteractiveWorkAdmissionTiming timing)
    {
    }

    public void RecordCompletion(VbaInteractiveWorkCompletionTiming timing)
    {
    }
}

internal sealed record VbaInteractiveWorkSchedulerOptions(
    bool CoalesceSupersededMutations)
{
    public static VbaInteractiveWorkSchedulerOptions Default { get; } = new(
        CoalesceSupersededMutations: true);
}

internal sealed class VbaInteractiveWorkCancellationOwner
{
    private readonly object gate = new();
    private readonly CancellationTokenSource cancellation = new();
    private Task cancellationDispatch = Task.CompletedTask;
    private Task? disposal;

    public CancellationToken Token => cancellation.Token;

    public bool IsCancellationRequested => cancellation.IsCancellationRequested;

    public bool TryCancel()
        => TryCancel(out _);

    public bool TryCancel(out Task dispatch)
    {
        lock (gate)
        {
            if (disposal is not null)
            {
                dispatch = Task.CompletedTask;
                return false;
            }

            if (!cancellation.IsCancellationRequested)
            {
                cancellationDispatch = cancellation.CancelAsync();
            }

            dispatch = cancellationDispatch;
            return true;
        }
    }

    public Task DisposeAsync(Task schedulerCancellationDispatch)
    {
        lock (gate)
        {
            disposal ??= DisposeCoreAsync(
                cancellationDispatch,
                schedulerCancellationDispatch);
            return disposal;
        }
    }

    public void DisposeUnadmitted()
    {
        lock (gate)
        {
            if (disposal is not null)
            {
                return;
            }

            cancellation.Dispose();
            disposal = Task.CompletedTask;
        }
    }

    private async Task DisposeCoreAsync(
        Task requestCancellationDispatch,
        Task schedulerCancellationDispatch)
    {
        await ObserveCancellationDispatchAsync(requestCancellationDispatch);
        await ObserveCancellationDispatchAsync(schedulerCancellationDispatch);
        cancellation.Dispose();
    }

    private static async Task ObserveCancellationDispatchAsync(Task dispatch)
    {
        try
        {
            await dispatch;
        }
        catch (Exception)
        {
        }
    }
}

/// <summary>
/// Admits interactive language-server work while executing it through one ordered lane.
/// </summary>
internal sealed class VbaInteractiveWorkScheduler : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Channel<ScheduledWork> workQueue = Channel.CreateUnbounded<ScheduledWork>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    private readonly Queue<ScheduledWork> bufferedWork = [];
    private readonly Dictionary<VbaLspRequestId, VbaInteractiveWorkCancellationOwner>
        requestCancellations = [];
    private readonly HashSet<VbaInteractiveWorkCancellationOwner> activeCancellations = [];
    private readonly IVbaInteractiveWorkTimingSink timingSink;
    private readonly Action<VbaInteractiveWorkFailure> failureSink;
    private readonly VbaInteractiveWorkSchedulerOptions options;
    private readonly Task worker;
    private Task abortCancellationDispatch = Task.CompletedTask;
    private Task? stopTask;
    private VbaInteractiveStopReason? stopReason;
    private bool abortCancellationStarted;
    private bool stopped;
    private bool accepting = true;
    private long nextInputSequence;
    private long latestMutationSequence;

    /// <summary>
    /// Creates a scheduler with one compatibility-mode execution lane.
    /// </summary>
    public VbaInteractiveWorkScheduler(
        IVbaInteractiveWorkTimingSink? timingSink = null,
        Action<VbaInteractiveWorkFailure>? failureSink = null,
        VbaInteractiveWorkSchedulerOptions? options = null)
    {
        this.timingSink = timingSink ?? NullVbaInteractiveWorkTimingSink.Instance;
        this.failureSink = failureSink ?? (static _ => { });
        this.options = options ?? VbaInteractiveWorkSchedulerOptions.Default;
        worker = ProcessQueueAsync();
    }

    /// <summary>
    /// Gets whether the scheduler still accepts newly read input.
    /// </summary>
    public bool IsAccepting
    {
        get
        {
            lock (gate)
            {
                return accepting;
            }
        }
    }

    /// <summary>
    /// Admits a workspace mutation without waiting for its execution.
    /// </summary>
    public VbaInteractiveWorkAdmission AdmitMutation(
        Func<CancellationToken, Task> executeAsync)
        => AdmitMutation("<mutation>", executeAsync);

    /// <summary>
    /// Admits a named workspace mutation without waiting for its execution.
    /// </summary>
    public VbaInteractiveWorkAdmission AdmitMutation(
        string method,
        Func<CancellationToken, Task> executeAsync)
        => Admit(
            VbaInteractiveWorkKind.Mutation,
            method,
            coalescingKey: null,
            requestId: null,
            (cancellationToken, _) => executeAsync(cancellationToken),
            advancesReadFence: true);

    /// <summary>
    /// Admits a mutation that may be superseded by a later queued mutation with the same key before any read fence.
    /// </summary>
    public VbaInteractiveWorkAdmission AdmitCoalescibleMutation(
        string method,
        string coalescingKey,
        Func<CancellationToken, Task> executeAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(coalescingKey);
        return Admit(
            VbaInteractiveWorkKind.Mutation,
            method,
            coalescingKey,
            requestId: null,
            (cancellationToken, _) => executeAsync(cancellationToken),
            advancesReadFence: true);
    }

    /// <summary>
    /// Admits ordered non-mutating work without advancing the read fence.
    /// </summary>
    public VbaInteractiveWorkAdmission AdmitBarrier(
        string method,
        Func<CancellationToken, Task> executeAsync)
        => Admit(
            VbaInteractiveWorkKind.Control,
            method,
            coalescingKey: null,
            requestId: null,
            (cancellationToken, _) => executeAsync(cancellationToken),
            advancesReadFence: false);

    /// <summary>
    /// Admits a request without waiting for its execution.
    /// </summary>
    public VbaInteractiveWorkAdmission AdmitRequest(
        VbaLspRequestId? requestId,
        Func<CancellationToken, Task> executeAsync)
        => AdmitRequest(requestId, "<request>", executeAsync);

    /// <summary>
    /// Admits a named request without waiting for its execution.
    /// </summary>
    public VbaInteractiveWorkAdmission AdmitRequest(
        VbaLspRequestId? requestId,
        string method,
        Func<CancellationToken, Task> executeAsync)
        => AdmitRequest(
            requestId,
            method,
            (cancellationToken, _) => executeAsync(cancellationToken));

    /// <summary>
    /// Admits a request that explicitly releases cancellation ownership after choosing its terminal response.
    /// </summary>
    public VbaInteractiveWorkAdmission AdmitRequest(
        VbaLspRequestId? requestId,
        string method,
        Func<CancellationToken, Action, Task> executeAsync)
        => Admit(
            VbaInteractiveWorkKind.Request,
            method,
            coalescingKey: null,
            requestId,
            executeAsync,
            advancesReadFence: false);

    /// <summary>
    /// Cancels the queued or executing request that currently owns the supplied identifier.
    /// </summary>
    public bool TryCancel(VbaLspRequestId requestId)
    {
        var admissionStarted = Stopwatch.GetTimestamp();
        VbaInteractiveWorkCancellationOwner? owner;
        long inputSequence;
        long readFence;
        lock (gate)
        {
            inputSequence = ++nextInputSequence;
            readFence = latestMutationSequence;
            requestCancellations.TryGetValue(requestId, out owner);
        }

        var cancelled = owner?.TryCancel() == true;
        var completedAt = Stopwatch.GetTimestamp();
        var admission = new VbaInteractiveWorkAdmissionTiming(
            inputSequence,
            readFence,
            VbaInteractiveWorkKind.Control,
            "$/cancelRequest",
            requestId,
            Stopwatch.GetElapsedTime(admissionStarted, completedAt));
        var completion = new VbaInteractiveWorkCompletionTiming(
            inputSequence,
            readFence,
            VbaInteractiveWorkKind.Control,
            "$/cancelRequest",
            requestId,
            TimeSpan.Zero,
            TimeSpan.Zero,
            Cancelled: false,
            Faulted: false);
        RecordAdmission(admission);
        RecordCompletion(completion);
        return cancelled;
    }

    /// <summary>
    /// Stops admission and either drains or cancels work already owned by the scheduler.
    /// </summary>
    public Task StopAsync(VbaInteractiveStopReason reason)
    {
        lock (gate)
        {
            if (stopTask is null)
            {
                accepting = false;
                stopReason = reason;
                if (reason == VbaInteractiveStopReason.Abort)
                {
                    BeginAbortLocked();
                }

                workQueue.Writer.TryComplete();
                stopTask = StopCoreAsync();
            }
            else if (reason == VbaInteractiveStopReason.Abort
                && stopReason == VbaInteractiveStopReason.Complete
                && !stopped)
            {
                stopReason = reason;
                BeginAbortLocked();
            }

            return stopTask;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
        => new(StopAsync(VbaInteractiveStopReason.Abort));

    private VbaInteractiveWorkAdmission Admit(
        VbaInteractiveWorkKind kind,
        string method,
        string? coalescingKey,
        VbaLspRequestId? requestId,
        Func<CancellationToken, Action, Task> executeAsync,
        bool advancesReadFence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(executeAsync);

        var admissionStarted = Stopwatch.GetTimestamp();
        VbaInteractiveWorkAdmission admission;
        VbaInteractiveWorkAdmissionTiming timing;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(!accepting, this);
            var inputSequence = ++nextInputSequence;
            if (advancesReadFence)
            {
                latestMutationSequence = inputSequence;
            }

            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _ = completion.Task.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously
                    | TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            var cancellation = new VbaInteractiveWorkCancellationOwner();
            if (requestId is { } ownedRequestId
                && !requestCancellations.TryAdd(ownedRequestId, cancellation))
            {
                cancellation.DisposeUnadmitted();
                throw new VbaDuplicateRequestIdException(ownedRequestId);
            }

            activeCancellations.Add(cancellation);
            var admittedAt = Stopwatch.GetTimestamp();
            Action releaseCancellationOwnership = requestId is { } ownedId
                ? () => ReleaseCancellationOwnership(ownedId, cancellation)
                : static () => { };
            var work = new ScheduledWork(
                inputSequence,
                latestMutationSequence,
                kind,
                method,
                coalescingKey,
                requestId,
                admittedAt,
                _ => executeAsync(
                    cancellation.Token,
                    releaseCancellationOwnership),
                cancellation,
                completion);
            if (!workQueue.Writer.TryWrite(work))
            {
                if (requestId is { } rejectedRequestId)
                {
                    requestCancellations.Remove(rejectedRequestId);
                }

                activeCancellations.Remove(cancellation);
                cancellation.DisposeUnadmitted();
                throw new InvalidOperationException("The interactive work scheduler is no longer accepting work.");
            }

            admission = new VbaInteractiveWorkAdmission(
                inputSequence,
                latestMutationSequence,
                completion.Task);
            timing = new VbaInteractiveWorkAdmissionTiming(
                inputSequence,
                latestMutationSequence,
                kind,
                method,
                requestId,
                Stopwatch.GetElapsedTime(admissionStarted, admittedAt));
        }

        RecordAdmission(timing);
        return admission;
    }

    private async Task ProcessQueueAsync()
    {
        while (true)
        {
            var work = await ReadNextWorkAsync();
            if (work is null)
            {
                return;
            }

            work = await CoalesceQueuedWorkAsync(work);
            await ExecuteWorkAsync(work);
        }
    }

    private async Task ExecuteWorkAsync(ScheduledWork work)
    {
            var executionStarted = Stopwatch.GetTimestamp();
            Exception? failure = null;
            var cancelled = false;
            try
            {
                var abortDispatch = GetAbortCancellationDispatch();
                if (abortDispatch is not null)
                {
                    await ObserveCancellationDispatchAsync(abortDispatch);
                    work.Cancellation.Token.ThrowIfCancellationRequested();
                }

                await work.ExecuteAsync(work.Cancellation.Token);
            }
            catch (OperationCanceledException exception) when (work.Cancellation.IsCancellationRequested)
            {
                failure = exception;
                cancelled = true;
            }
            catch (Exception exception)
            {
                failure = exception;
                _ = StopAsync(VbaInteractiveStopReason.Abort);
                RecordFailure(new VbaInteractiveWorkFailure(
                    work.InputSequence,
                    work.Kind,
                    work.Method,
                    work.RequestId,
                    exception));
            }
            finally
            {
                var completedAt = Stopwatch.GetTimestamp();
                RecordCompletion(new VbaInteractiveWorkCompletionTiming(
                    work.InputSequence,
                    work.ReadFence,
                    work.Kind,
                    work.Method,
                    work.RequestId,
                    Stopwatch.GetElapsedTime(work.AdmittedAt, executionStarted),
                    Stopwatch.GetElapsedTime(executionStarted, completedAt),
                    cancelled,
                    failure is not null && !cancelled));
                if (work.RequestId is { } requestId)
                {
                    ReleaseCancellationOwnership(requestId, work.Cancellation);
                }

                await work.Cancellation.DisposeAsync(
                    ReleaseActiveCancellation(work.Cancellation));
            }

            if (cancelled)
            {
                work.Completion.TrySetCanceled();
            }
            else if (failure is not null)
            {
                work.Completion.TrySetException(failure);
            }
            else
            {
                work.Completion.TrySetResult();
            }
    }

    private async Task<ScheduledWork?> ReadNextWorkAsync()
    {
        if (bufferedWork.TryDequeue(out var buffered))
        {
            return buffered;
        }

        while (await workQueue.Reader.WaitToReadAsync())
        {
            if (workQueue.Reader.TryRead(out var work))
            {
                return work;
            }
        }

        return null;
    }

    private async Task<ScheduledWork> CoalesceQueuedWorkAsync(ScheduledWork work)
    {
        if (!options.CoalesceSupersededMutations
            || work.CoalescingKey is null)
        {
            return work;
        }

        var current = work;
        while (TryReadAlreadyQueuedWork(out var next))
        {
            if (!CanCoalesce(current, next))
            {
                bufferedWork.Enqueue(next);
                return current;
            }

            await CompleteSupersededWorkAsync(current);
            current = next;
        }

        return current;
    }

    private bool TryReadAlreadyQueuedWork(out ScheduledWork work)
    {
        if (bufferedWork.TryDequeue(out work!))
        {
            return true;
        }

        return workQueue.Reader.TryRead(out work!);
    }

    private async Task CompleteSupersededWorkAsync(ScheduledWork work)
    {
        var completedAt = Stopwatch.GetTimestamp();
        RecordCompletion(new VbaInteractiveWorkCompletionTiming(
            work.InputSequence,
            work.ReadFence,
            work.Kind,
            work.Method,
            work.RequestId,
            Stopwatch.GetElapsedTime(work.AdmittedAt, completedAt),
            TimeSpan.Zero,
            Cancelled: false,
            Faulted: false));
        if (work.RequestId is { } requestId)
        {
            ReleaseCancellationOwnership(requestId, work.Cancellation);
        }

        await work.Cancellation.DisposeAsync(
            ReleaseActiveCancellation(work.Cancellation));
        work.Completion.TrySetResult();
    }

    private static bool CanCoalesce(ScheduledWork current, ScheduledWork next)
        => current.Kind == VbaInteractiveWorkKind.Mutation
            && next.Kind == VbaInteractiveWorkKind.Mutation
            && current.Method.Equals(next.Method, StringComparison.Ordinal)
            && current.CoalescingKey is { } currentKey
            && next.CoalescingKey is { } nextKey
            && currentKey.Equals(nextKey, StringComparison.OrdinalIgnoreCase);

    private void RecordAdmission(VbaInteractiveWorkAdmissionTiming timing)
    {
        try
        {
            timingSink.RecordAdmission(timing);
        }
        catch (Exception)
        {
        }
    }

    private void ReleaseCancellationOwnership(
        VbaLspRequestId requestId,
        VbaInteractiveWorkCancellationOwner owner)
    {
        lock (gate)
        {
            if (requestCancellations.TryGetValue(requestId, out var currentOwner)
                && ReferenceEquals(currentOwner, owner))
            {
                requestCancellations.Remove(requestId);
            }
        }
    }

    private void RecordCompletion(VbaInteractiveWorkCompletionTiming timing)
    {
        try
        {
            timingSink.RecordCompletion(timing);
        }
        catch (Exception)
        {
        }
    }

    private void RecordFailure(VbaInteractiveWorkFailure failure)
    {
        try
        {
            failureSink(failure);
        }
        catch (Exception)
        {
        }
    }

    private void BeginAbortLocked()
    {
        if (!abortCancellationStarted)
        {
            abortCancellationStarted = true;
            abortCancellationDispatch = DispatchCancellationAsync(
                activeCancellations.ToArray());
        }
    }

    private Task? GetAbortCancellationDispatch()
    {
        lock (gate)
        {
            return stopReason == VbaInteractiveStopReason.Abort
                ? abortCancellationDispatch
                : null;
        }
    }

    private Task ReleaseActiveCancellation(
        VbaInteractiveWorkCancellationOwner owner)
    {
        lock (gate)
        {
            activeCancellations.Remove(owner);
            return abortCancellationDispatch;
        }
    }

    private static async Task DispatchCancellationAsync(
        IReadOnlyCollection<VbaInteractiveWorkCancellationOwner> owners)
    {
        var dispatches = new List<Task>(owners.Count);
        foreach (var owner in owners)
        {
            if (owner.TryCancel(out var dispatch))
            {
                dispatches.Add(dispatch);
            }
        }

        try
        {
            await Task.WhenAll(dispatches);
        }
        catch (Exception)
        {
        }
    }

    private static async Task ObserveCancellationDispatchAsync(Task dispatch)
    {
        try
        {
            await dispatch;
        }
        catch (Exception)
        {
        }
    }

    private async Task StopCoreAsync()
    {
        await Task.Yield();
        try
        {
            await worker;
        }
        finally
        {
            Task cancellationDispatch;
            lock (gate)
            {
                stopped = true;
                cancellationDispatch = abortCancellationDispatch;
            }

            await ObserveCancellationDispatchAsync(cancellationDispatch);
        }
    }

    private sealed record ScheduledWork(
        long InputSequence,
        long ReadFence,
        VbaInteractiveWorkKind Kind,
        string Method,
        string? CoalescingKey,
        VbaLspRequestId? RequestId,
        long AdmittedAt,
        Func<CancellationToken, Task> ExecuteAsync,
        VbaInteractiveWorkCancellationOwner Cancellation,
        TaskCompletionSource Completion);
}
