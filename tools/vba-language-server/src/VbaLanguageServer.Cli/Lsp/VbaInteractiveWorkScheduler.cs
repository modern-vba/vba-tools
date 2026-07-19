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

internal sealed class VbaInteractiveWorkQueueFullException : InvalidOperationException
{
    public VbaInteractiveWorkQueueFullException(int maximumOwnedWork)
        : base($"The interactive work scheduler already owns its limit of {maximumOwnedWork} work items.")
    {
        MaximumOwnedWork = maximumOwnedWork;
    }

    public int MaximumOwnedWork { get; }
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

internal enum VbaInteractiveWorkClass
{
    LatencyCritical,
    Normal,
    Bulk,
    Background
}

internal enum VbaInteractiveBackgroundWorkType
{
    DiagnosticsPublication,
    Reconciliation,
    ReferenceCatalogRefresh,
    ReferenceCatalogPublication
}

internal readonly record struct VbaInteractiveReadPolicy(
    VbaInteractiveWorkClass WorkClass,
    bool Concurrent)
{
    public static VbaInteractiveReadPolicy ForMethod(string method)
        => method switch
        {
            "vba/blockSkeletonInsertion"
                or "textDocument/completion"
                or "textDocument/hover"
                or "textDocument/signatureHelp"
                => new(VbaInteractiveWorkClass.LatencyCritical, Concurrent: true),
            "textDocument/definition"
                or "textDocument/documentSymbol"
                or "textDocument/prepareRename"
                or "textDocument/semanticTokens/full"
                or "workspace/symbol"
                => new(VbaInteractiveWorkClass.Normal, Concurrent: true),
            "textDocument/references"
                or "textDocument/rename"
                or "textDocument/formatting"
                => new(VbaInteractiveWorkClass.Bulk, Concurrent: true),
            "textDocument/diagnostic"
                or "workspace/diagnostic"
                or "vba/reconcile"
                or "vba/referenceCatalogRefresh"
                or "vba/referenceCatalogPublication"
                => new(VbaInteractiveWorkClass.Background, Concurrent: true),
            _ => new(VbaInteractiveWorkClass.Normal, Concurrent: false)
        };
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
    bool CoalesceSupersededMutations,
    bool EnableConcurrentReads = true,
    int MaxConcurrentReads = 4,
    int MaxConcurrentBulkReads = 1,
    int MaxOwnedWork = 1024)
{
    internal const string SerialWorkerEnvironmentVariable =
        "VBA_TOOLS_INTERACTIVE_SERIAL_WORKER";

    public static VbaInteractiveWorkSchedulerOptions Default { get; } = new(
        CoalesceSupersededMutations: true);

    public static VbaInteractiveWorkSchedulerOptions Serial { get; } = new(
        CoalesceSupersededMutations: true,
        EnableConcurrentReads: false,
        MaxConcurrentReads: 1,
        MaxConcurrentBulkReads: 1);

    public static VbaInteractiveWorkSchedulerOptions CreateFromEnvironment()
    {
        var configured = Environment.GetEnvironmentVariable(
            SerialWorkerEnvironmentVariable);
        return string.Equals(configured, "1", StringComparison.Ordinal)
            || string.Equals(configured, "true", StringComparison.OrdinalIgnoreCase)
                ? Serial
                : Default;
    }
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
/// Admits interactive language-server work through one ordered mutation lane and bounded read execution.
/// </summary>
internal sealed class VbaInteractiveWorkScheduler : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly object timingGate = new();
    private readonly Channel<ScheduledWork> workQueue;
    private readonly Queue<ScheduledWork> bufferedWork = [];
    private readonly Dictionary<VbaLspRequestId, VbaInteractiveWorkCancellationOwner>
        requestCancellations = [];
    private readonly HashSet<VbaInteractiveWorkCancellationOwner> activeCancellations = [];
    private readonly List<ActiveRead> activeReads = [];
    private readonly List<ScheduledWork> pendingReads = [];
    private readonly List<Action> capacityObservers = [];
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
    private int ownedWorkCount;
    private int requiredAdmissionWaiterCount;
    private int nextCapacityObserverIndex;
    private bool capacityPumpActive;
    private bool capacityPumpRequested;
    private TaskCompletionSource capacityChanged =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

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
        if (this.options.MaxConcurrentReads <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                this.options.MaxConcurrentReads,
                "Maximum concurrent reads must be positive.");
        }

        if (this.options.MaxConcurrentBulkReads <= 0
            || this.options.MaxConcurrentBulkReads > this.options.MaxConcurrentReads)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                this.options.MaxConcurrentBulkReads,
                "Maximum concurrent bulk reads must be positive and no greater than total read concurrency.");
        }

        if (this.options.MaxOwnedWork <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                this.options.MaxOwnedWork,
                "Maximum owned work must be positive.");
        }

        workQueue = Channel.CreateBounded<ScheduledWork>(
            new BoundedChannelOptions(this.options.MaxOwnedWork)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
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
            captureRead: null,
            coalescingKey: null,
            requestId: null,
            (cancellationToken, _) => executeAsync(cancellationToken),
            advancesReadFence: true);

    /// <summary>
    /// Waits for bounded capacity before admitting a correctness-bearing internal mutation.
    /// </summary>
    public Task<VbaInteractiveWorkAdmission> AdmitRequiredMutationAsync(
        string method,
        Func<CancellationToken, Task> executeAsync,
        CancellationToken cancellationToken)
        => AdmitRequiredAsync(
            VbaInteractiveWorkKind.Mutation,
            method,
            executeAsync,
            advancesReadFence: true,
            cancellationToken);

    /// <summary>
    /// Waits for bounded capacity before admitting a correctness-bearing ordered barrier.
    /// </summary>
    public Task<VbaInteractiveWorkAdmission> AdmitRequiredBarrierAsync(
        string method,
        Func<CancellationToken, Task> executeAsync,
        CancellationToken cancellationToken)
        => AdmitRequiredAsync(
            VbaInteractiveWorkKind.Control,
            method,
            executeAsync,
            advancesReadFence: false,
            cancellationToken);

    private async Task<VbaInteractiveWorkAdmission> AdmitRequiredAsync(
        VbaInteractiveWorkKind kind,
        string method,
        Func<CancellationToken, Task> executeAsync,
        bool advancesReadFence,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(executeAsync);
        var registeredWaiter = false;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return Admit(
                        kind,
                        method,
                        captureRead: null,
                        coalescingKey: null,
                        requestId: null,
                        (schedulerCancellationToken, _) =>
                            executeAsync(schedulerCancellationToken),
                        advancesReadFence,
                        usesReservedCapacity: true);
                }
                catch (VbaInteractiveWorkQueueFullException)
                {
                    if (!registeredWaiter)
                    {
                        lock (gate)
                        {
                            ObjectDisposedException.ThrowIf(!accepting, this);
                            requiredAdmissionWaiterCount++;
                            registeredWaiter = true;
                        }
                    }
                }

                await WaitForOwnedCapacityAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            if (registeredWaiter)
            {
                ReleaseRequiredAdmissionWaiter();
            }
        }
    }

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
            captureRead: null,
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
            captureRead: null,
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
            captureRead: null,
            coalescingKey: null,
            requestId,
            executeAsync,
            advancesReadFence: false);

    /// <summary>
    /// Captures immutable read state on the ordered lane before executing the read concurrently.
    /// </summary>
    public VbaInteractiveWorkAdmission AdmitRequest<TSnapshot>(
        VbaLspRequestId? requestId,
        string method,
        Func<CancellationToken, TSnapshot> capture,
        Func<TSnapshot, CancellationToken, Task> executeAsync)
        => AdmitRequest(
            requestId,
            method,
            capture,
            (snapshot, cancellationToken, _) => executeAsync(snapshot, cancellationToken));

    /// <summary>
    /// Captures immutable read state and explicitly releases request-id ownership after choosing a response.
    /// </summary>
    public VbaInteractiveWorkAdmission AdmitRequest<TSnapshot>(
        VbaLspRequestId? requestId,
        string method,
        Func<CancellationToken, TSnapshot> capture,
        Func<TSnapshot, CancellationToken, Action, Task> executeAsync)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(executeAsync);
        return Admit(
            VbaInteractiveWorkKind.Request,
            method,
            cancellationToken =>
            {
                var snapshot = capture(cancellationToken);
                return new VbaInteractiveCapturedRead(
                    (executeCancellationToken, releaseCancellationOwnership) =>
                        executeAsync(
                            snapshot,
                            executeCancellationToken,
                            releaseCancellationOwnership));
            },
            coalescingKey: null,
            requestId,
            static (_, _) => throw new InvalidOperationException(
                "Captured reads must execute through their immutable snapshot."),
            advancesReadFence: false);
    }

    /// <summary>
    /// Admits one internally classified background operation without exposing scheduler priority.
    /// </summary>
    public VbaInteractiveWorkAdmission AdmitBackground(
        VbaInteractiveBackgroundWorkType workType,
        string authorityKey,
        Func<CancellationToken, Task> executeAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorityKey);
        ArgumentNullException.ThrowIfNull(executeAsync);
        var coalescingKey =
            workType == VbaInteractiveBackgroundWorkType.ReferenceCatalogPublication
                ? null
                : $"{workType}:{authorityKey}";
        return Admit(
            VbaInteractiveWorkKind.Request,
            GetBackgroundMethod(workType),
            cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new VbaInteractiveCapturedRead(
                    (executeCancellationToken, _) =>
                        executeAsync(executeCancellationToken));
            },
            coalescingKey,
            requestId: null,
            static (_, _) => throw new InvalidOperationException(
                "Background work must execute through the scheduler read lane."),
            advancesReadFence: false);
    }

    /// <summary>
    /// Attempts to admit one internally classified background operation without exceeding the owned-work bound.
    /// </summary>
    public bool TryAdmitBackground(
        VbaInteractiveBackgroundWorkType workType,
        string authorityKey,
        Func<CancellationToken, Task> executeAsync,
        out VbaInteractiveWorkAdmission admission)
    {
        try
        {
            admission = AdmitBackground(workType, authorityKey, executeAsync);
            return true;
        }
        catch (VbaInteractiveWorkQueueFullException)
        {
            admission = default;
            return false;
        }
        catch (ObjectDisposedException)
        {
            admission = default;
            return false;
        }
    }

    /// <summary>
    /// Registers a lightweight producer callback that may retry one deferred admission after capacity returns.
    /// </summary>
    public void RegisterCapacityObserver(Action observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(!accepting, this);
            capacityObservers.Add(observer);
        }
    }

    /// <summary>
    /// Requests a level-triggered retry pass over deferred background producers.
    /// </summary>
    public void RequestCapacityPump()
    {
        var runPump = false;
        lock (gate)
        {
            if (capacityPumpActive)
            {
                capacityPumpRequested = true;
                return;
            }

            if (CanPumpCapacityObserversLocked())
            {
                capacityPumpActive = true;
                runPump = true;
            }
        }

        if (runPump)
        {
            PumpCapacityObservers();
        }
    }

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
                capacityChanged.TrySetResult();
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
        Func<CancellationToken, VbaInteractiveCapturedRead>? captureRead,
        string? coalescingKey,
        VbaLspRequestId? requestId,
        Func<CancellationToken, Action, Task> executeAsync,
        bool advancesReadFence,
        bool usesReservedCapacity = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(executeAsync);

        var admissionStarted = Stopwatch.GetTimestamp();
        VbaInteractiveWorkAdmission admission;
        VbaInteractiveWorkAdmissionTiming timing;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(!accepting, this);
            if (requestId is { } duplicateRequestId
                && requestCancellations.ContainsKey(duplicateRequestId))
            {
                throw new VbaDuplicateRequestIdException(duplicateRequestId);
            }

            if (ownedWorkCount >= options.MaxOwnedWork
                || !usesReservedCapacity
                    && requiredAdmissionWaiterCount > 0
                    && ownedWorkCount >= options.MaxOwnedWork - 1)
            {
                throw new VbaInteractiveWorkQueueFullException(options.MaxOwnedWork);
            }

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
            ownedWorkCount++;
            var admittedAt = Stopwatch.GetTimestamp();
            Action releaseCancellationOwnership = requestId is { } ownedId
                ? () => ReleaseCancellationOwnership(ownedId, cancellation)
                : static () => { };
            var work = new ScheduledWork(
                inputSequence,
                latestMutationSequence,
                kind,
                method,
                kind == VbaInteractiveWorkKind.Request
                    ? VbaInteractiveReadPolicy.ForMethod(method)
                    : null,
                captureRead,
                coalescingKey,
                requestId,
                admittedAt,
                _ => executeAsync(
                    cancellation.Token,
                    releaseCancellationOwnership),
                releaseCancellationOwnership,
                cancellation,
                completion);
            if (!workQueue.Writer.TryWrite(work))
            {
                if (requestId is { } rejectedRequestId)
                {
                    requestCancellations.Remove(rejectedRequestId);
                }

                activeCancellations.Remove(cancellation);
                ownedWorkCount--;
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
        try
        {
            while (true)
            {
                await ObserveCompletedReadsAsync();
                ScheduledWork? work;
                if (pendingReads.Count > 0)
                {
                    await CollectQueuedReadRunAsync();
                    if (TryDequeueEligibleRead(out var pendingRead))
                    {
                        await DispatchReadAsync(pendingRead);
                        continue;
                    }

                    if (bufferedWork.Count == 0)
                    {
                        await WaitForReadOpportunityOrQueuedWorkAsync();
                        continue;
                    }

                    work = await ReadNextWorkAsync();
                }
                else
                {
                    work = await ReadNextWorkAsync();
                }

                if (work is null)
                {
                    return;
                }

                work = await CoalesceQueuedWorkAsync(work);
                if (work.Kind == VbaInteractiveWorkKind.Request
                    && options.EnableConcurrentReads
                    && work.ReadPolicy is { Concurrent: true }
                    && work.CaptureRead is not null)
                {
                    await AddPendingReadAsync(work);
                    await CollectQueuedReadRunAsync();
                    continue;
                }

                if (work.Kind == VbaInteractiveWorkKind.Request
                    && work.CaptureRead is not null)
                {
                    await ObserveAllReadsAsync();
                    if (await CaptureReadAsync(work))
                    {
                        await ExecuteCapturedWorkAsync(work);
                    }

                    continue;
                }

                if (work.Kind == VbaInteractiveWorkKind.Control)
                {
                    await ObserveAllReadsAsync();
                }

                await ExecuteWorkAsync(work);
            }
        }
        finally
        {
            await ObserveAllReadsAsync();
        }
    }

    private async Task CollectQueuedReadRunAsync()
    {
        while (TryReadAlreadyQueuedWork(out var work))
        {
            if (work.Kind == VbaInteractiveWorkKind.Request
                && options.EnableConcurrentReads
                && work.ReadPolicy is { Concurrent: true }
                && work.CaptureRead is not null)
            {
                await AddPendingReadAsync(work);
                continue;
            }

            bufferedWork.Enqueue(work);
            return;
        }
    }

    private async Task AddPendingReadAsync(ScheduledWork work)
    {
        if (work.ReadPolicy is
            {
                WorkClass: VbaInteractiveWorkClass.Background
            }
            && work.CoalescingKey is { } workKey)
        {
            for (var index = pendingReads.Count - 1; index >= 0; index--)
            {
                var pending = pendingReads[index];
                if (pending.ReadPolicy is not
                    {
                        WorkClass: VbaInteractiveWorkClass.Background
                    }
                    || pending.CoalescingKey is not { } pendingKey
                    || !pending.Method.Equals(work.Method, StringComparison.Ordinal)
                    || !pendingKey.Equals(workKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                pendingReads.RemoveAt(index);
                await CompleteSupersededWorkAsync(pending);
            }
        }

        if (await CaptureReadAsync(work))
        {
            pendingReads.Add(work);
        }
    }

    private bool TryDequeueEligibleRead(out ScheduledWork work)
    {
        work = default!;
        if (activeReads.Count >= options.MaxConcurrentReads)
        {
            return false;
        }

        var pendingIndex = -1;
        var bestScore = int.MinValue;
        var activeNonLatencyCriticalReads = ActiveNonLatencyCriticalReadCount();
        var maximumNonLatencyCriticalReads =
            Math.Max(1, options.MaxConcurrentReads - 1);
        for (var index = 0; index < pendingReads.Count; index++)
        {
            var pending = pendingReads[index];
            var workClass = pending.ReadPolicy!.Value.WorkClass;
            if (workClass != VbaInteractiveWorkClass.LatencyCritical
                && activeNonLatencyCriticalReads >= maximumNonLatencyCriticalReads)
            {
                continue;
            }

            if (workClass == VbaInteractiveWorkClass.Bulk
                && ActiveBulkReadCount() >= options.MaxConcurrentBulkReads)
            {
                continue;
            }

            var score = BasePriority(workClass) + pending.FairnessAge * 50;
            if (score < bestScore)
            {
                continue;
            }

            if (score == bestScore
                && pendingIndex >= 0
                && pending.InputSequence > pendingReads[pendingIndex].InputSequence)
            {
                continue;
            }

            pendingIndex = index;
            bestScore = score;
        }

        if (pendingIndex < 0)
        {
            return false;
        }

        work = pendingReads[pendingIndex];
        pendingReads.RemoveAt(pendingIndex);
        var selectedPriority = BasePriority(work.ReadPolicy!.Value.WorkClass);
        foreach (var pending in pendingReads)
        {
            if (BasePriority(pending.ReadPolicy!.Value.WorkClass) < selectedPriority)
            {
                pending.FairnessAge++;
            }
        }

        return true;
    }

    private static int BasePriority(VbaInteractiveWorkClass workClass)
        => workClass switch
        {
            VbaInteractiveWorkClass.LatencyCritical => 300,
            VbaInteractiveWorkClass.Normal => 200,
            VbaInteractiveWorkClass.Bulk => 100,
            VbaInteractiveWorkClass.Background => 0,
            _ => throw new ArgumentOutOfRangeException(nameof(workClass), workClass, null)
        };

    private static string GetBackgroundMethod(VbaInteractiveBackgroundWorkType workType)
        => workType switch
        {
            VbaInteractiveBackgroundWorkType.DiagnosticsPublication
                => "textDocument/diagnostic",
            VbaInteractiveBackgroundWorkType.Reconciliation
                => "vba/reconcile",
            VbaInteractiveBackgroundWorkType.ReferenceCatalogRefresh
                => "vba/referenceCatalogRefresh",
            VbaInteractiveBackgroundWorkType.ReferenceCatalogPublication
                => "vba/referenceCatalogPublication",
            _ => throw new ArgumentOutOfRangeException(nameof(workType), workType, null)
        };

    private async Task DispatchReadAsync(ScheduledWork work)
    {
        var execution = ExecuteCapturedWorkAsync(work);
        if (!execution.IsCompleted)
        {
            activeReads.Add(new ActiveRead(execution, work.ReadPolicy!.Value.WorkClass));
            return;
        }

        await execution;
    }

    private Task ExecuteCapturedWorkAsync(ScheduledWork work)
        => ExecuteWorkAsync(
            work,
            cancellationToken =>
            {
                var captured = work.CapturedRead
                    ?? throw new InvalidOperationException(
                        "The ordered lane must capture immutable read state before execution.");
                return captured.ExecuteAsync(
                    cancellationToken,
                    work.ReleaseCancellationOwnership);
            });

    private async Task<bool> CaptureReadAsync(ScheduledWork work)
    {
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

            work.CapturedRead = work.CaptureRead!(work.Cancellation.Token);
            return true;
        }
        catch (OperationCanceledException exception)
            when (work.Cancellation.IsCancellationRequested)
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

        await CompleteUnexecutedWorkAsync(work, cancelled, failure);
        return false;
    }

    private async Task WaitForReadOpportunityOrQueuedWorkAsync()
    {
        if (activeReads.Count == 0)
        {
            throw new InvalidOperationException("No read can make progress under the configured limits.");
        }

        var readCompletion = Task.WhenAny(activeReads.Select(read => read.Execution));
        if (bufferedWork.Count > 0 || workQueue.Reader.Completion.IsCompleted)
        {
            await readCompletion;
            return;
        }

        var queuedWork = workQueue.Reader.WaitToReadAsync().AsTask();
        var completed = await Task.WhenAny(readCompletion, queuedWork);
        if (ReferenceEquals(completed, queuedWork)
            && !await queuedWork)
        {
            await readCompletion;
        }
    }

    private async Task ObserveCompletedReadsAsync()
    {
        for (var index = activeReads.Count - 1; index >= 0; index--)
        {
            var read = activeReads[index];
            if (!read.Execution.IsCompleted)
            {
                continue;
            }

            activeReads.RemoveAt(index);
            await read.Execution;
        }
    }

    private async Task ObserveAllReadsAsync()
    {
        while (activeReads.Count > 0)
        {
            await Task.WhenAny(activeReads.Select(read => read.Execution));
            await ObserveCompletedReadsAsync();
        }
    }

    private int ActiveBulkReadCount()
        => activeReads.Count(read => read.WorkClass == VbaInteractiveWorkClass.Bulk);

    private int ActiveNonLatencyCriticalReadCount()
        => activeReads.Count(
            read => read.WorkClass != VbaInteractiveWorkClass.LatencyCritical);

    private Task ExecuteWorkAsync(ScheduledWork work)
        => ExecuteWorkAsync(work, work.ExecuteAsync);

    private async Task ExecuteWorkAsync(
        ScheduledWork work,
        Func<CancellationToken, Task> executeAsync)
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

            await executeAsync(work.Cancellation.Token);
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

        ReleaseOwnedWork();
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
        var cancelled = work.Cancellation.IsCancellationRequested
            || IsAbortRequested();
        await CompleteUnexecutedWorkAsync(
            work,
            cancelled,
            failure: null);
    }

    private async Task CompleteUnexecutedWorkAsync(
        ScheduledWork work,
        bool cancelled,
        Exception? failure)
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
            Cancelled: cancelled,
            Faulted: failure is not null && !cancelled));
        if (work.RequestId is { } requestId)
        {
            ReleaseCancellationOwnership(requestId, work.Cancellation);
        }

        await work.Cancellation.DisposeAsync(
            ReleaseActiveCancellation(work.Cancellation));
        ReleaseOwnedWork();
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

    private static bool CanCoalesce(ScheduledWork current, ScheduledWork next)
    {
        if (current.Kind != next.Kind
            || !current.Method.Equals(next.Method, StringComparison.Ordinal)
            || current.CoalescingKey is not { } currentKey
            || next.CoalescingKey is not { } nextKey
            || !currentKey.Equals(nextKey, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return current.Kind == VbaInteractiveWorkKind.Mutation
            || current.Kind == VbaInteractiveWorkKind.Request
                && current.ReadPolicy is
                {
                    WorkClass: VbaInteractiveWorkClass.Background
                };
    }

    private void RecordAdmission(VbaInteractiveWorkAdmissionTiming timing)
    {
        try
        {
            lock (timingGate)
            {
                timingSink.RecordAdmission(timing);
            }
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
            lock (timingGate)
            {
                timingSink.RecordCompletion(timing);
            }
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

    private bool IsAbortRequested()
    {
        lock (gate)
        {
            return stopReason == VbaInteractiveStopReason.Abort;
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

    private void ReleaseOwnedWork()
    {
        TaskCompletionSource releasedCapacity;
        lock (gate)
        {
            ownedWorkCount--;
            releasedCapacity = capacityChanged;
            capacityChanged = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        releasedCapacity.TrySetResult();
        RequestCapacityPump();
    }

    private Task WaitForOwnedCapacityAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(!accepting, this);
            return ownedWorkCount < options.MaxOwnedWork
                ? Task.CompletedTask
                : capacityChanged.Task.WaitAsync(cancellationToken);
        }
    }

    private void ReleaseRequiredAdmissionWaiter()
    {
        lock (gate)
        {
            requiredAdmissionWaiterCount--;
        }

        RequestCapacityPump();
    }

    private bool CanPumpCapacityObserversLocked()
        => accepting
            && requiredAdmissionWaiterCount == 0
            && ownedWorkCount < options.MaxOwnedWork
            && capacityObservers.Count > 0;

    private void PumpCapacityObservers()
    {
        var attemptedObservers = 0;
        while (true)
        {
            Action observer;
            lock (gate)
            {
                if (!CanPumpCapacityObserversLocked())
                {
                    capacityPumpActive = false;
                    capacityPumpRequested = false;
                    return;
                }

                if (attemptedObservers >= capacityObservers.Count)
                {
                    if (!capacityPumpRequested)
                    {
                        capacityPumpActive = false;
                        return;
                    }

                    capacityPumpRequested = false;
                    attemptedObservers = 0;
                }

                if (nextCapacityObserverIndex >= capacityObservers.Count)
                {
                    nextCapacityObserverIndex = 0;
                }

                observer = capacityObservers[nextCapacityObserverIndex];
                nextCapacityObserverIndex =
                    (nextCapacityObserverIndex + 1) % capacityObservers.Count;
                attemptedObservers++;
            }

            try
            {
                observer();
            }
            catch (Exception)
            {
            }
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
        VbaInteractiveReadPolicy? ReadPolicy,
        Func<CancellationToken, VbaInteractiveCapturedRead>? CaptureRead,
        string? CoalescingKey,
        VbaLspRequestId? RequestId,
        long AdmittedAt,
        Func<CancellationToken, Task> ExecuteAsync,
        Action ReleaseCancellationOwnership,
        VbaInteractiveWorkCancellationOwner Cancellation,
        TaskCompletionSource Completion)
    {
        public int FairnessAge { get; set; }

        public VbaInteractiveCapturedRead? CapturedRead { get; set; }
    }

    private sealed record ActiveRead(
        Task Execution,
        VbaInteractiveWorkClass WorkClass);

    private sealed record VbaInteractiveCapturedRead(
        Func<CancellationToken, Action, Task> ExecuteAsync);
}
