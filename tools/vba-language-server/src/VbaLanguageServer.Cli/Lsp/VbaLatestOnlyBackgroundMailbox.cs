namespace VbaLanguageServer.Lsp;

/// <summary>
/// Owns latest-only background admission for independent authority keys.
/// </summary>
internal sealed class VbaLatestOnlyBackgroundMailbox
{
    private readonly object gate = new();
    private readonly VbaInteractiveWorkScheduler scheduler;
    private readonly VbaInteractiveBackgroundWorkType workType;
    private readonly Dictionary<string, PendingWork> pending;
    private readonly HashSet<string> active;
    private readonly LinkedList<string> ready = new();
    private readonly Dictionary<string, LinkedListNode<string>> readyNodes;
    private readonly Dictionary<string, List<TaskCompletionSource>> authorityIdleWaiters;
    private readonly List<TaskCompletionSource> idleWaiters = [];
    private readonly Action<string>? authorityStateChanged;
    private bool stopped;

    /// <summary>
    /// Creates a mailbox over one scheduler-owned background work class.
    /// </summary>
    public VbaLatestOnlyBackgroundMailbox(
        VbaInteractiveWorkScheduler scheduler,
        VbaInteractiveBackgroundWorkType workType,
        IEqualityComparer<string>? authorityComparer = null,
        Action<string>? authorityStateChanged = null)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        var comparer = authorityComparer ?? StringComparer.OrdinalIgnoreCase;
        this.scheduler = scheduler;
        this.workType = workType;
        this.authorityStateChanged = authorityStateChanged;
        pending = new Dictionary<string, PendingWork>(comparer);
        active = new HashSet<string>(comparer);
        readyNodes = new Dictionary<string, LinkedListNode<string>>(comparer);
        authorityIdleWaiters =
            new Dictionary<string, List<TaskCompletionSource>>(comparer);
        scheduler.RegisterCapacityObserver(TryDispatchOne);
    }

    /// <summary>
    /// Replaces pending work for an authority and admits one worker when needed.
    /// </summary>
    public void Post(
        string authorityKey,
        Func<CancellationToken, Task> executeAsync,
        Action? onTerminal = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorityKey);
        ArgumentNullException.ThrowIfNull(executeAsync);
        PendingWork? superseded = null;
        var reject = false;
        lock (gate)
        {
            if (stopped)
            {
                reject = true;
            }
            else
            {
                pending.Remove(authorityKey, out superseded);
                pending[authorityKey] = new PendingWork(executeAsync, onTerminal);
                EnqueueReadyLocked(authorityKey);
            }
        }

        CompleteTerminal(superseded);
        if (reject)
        {
            CompleteTerminal(new PendingWork(executeAsync, onTerminal));
            NotifyAuthorityStateChanged(authorityKey);
            return;
        }

        TryDispatchOne();
    }

    /// <summary>
    /// Discards pending work for an authority without interrupting active execution.
    /// </summary>
    public void Discard(string authorityKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorityKey);
        PendingWork? discarded;
        IdleCompletions completions;
        lock (gate)
        {
            pending.Remove(authorityKey, out discarded);
            RemoveReadyLocked(authorityKey);
            completions = CaptureIdleCompletionsLocked(authorityKey);
        }

        CompleteTerminal(discarded);
        CompleteIdle(completions);
    }

    /// <summary>
    /// Returns whether one authority has no pending or active work.
    /// </summary>
    public bool IsIdle(string authorityKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorityKey);
        lock (gate)
        {
            return IsIdleLocked(authorityKey);
        }
    }

    /// <summary>
    /// Waits until one authority has no pending or active work.
    /// </summary>
    public Task WaitForIdleAsync(string authorityKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorityKey);
        lock (gate)
        {
            if (IsIdleLocked(authorityKey))
            {
                return Task.CompletedTask;
            }

            var waiter = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!authorityIdleWaiters.TryGetValue(authorityKey, out var waiters))
            {
                waiters = [];
                authorityIdleWaiters.Add(authorityKey, waiters);
            }

            waiters.Add(waiter);
            return waiter.Task;
        }
    }

    /// <summary>
    /// Waits until every authority has no pending or active work.
    /// </summary>
    public Task WaitForIdleAsync()
    {
        lock (gate)
        {
            if (pending.Count == 0 && active.Count == 0)
            {
                return Task.CompletedTask;
            }

            var waiter = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            idleWaiters.Add(waiter);
            return waiter.Task;
        }
    }

    /// <summary>
    /// Rejects later posts and terminalizes all work that has not started.
    /// </summary>
    public void Stop()
    {
        PendingWork[] discarded;
        string[] affectedAuthorities;
        IdleCompletions[] completions;
        lock (gate)
        {
            if (stopped)
            {
                return;
            }

            stopped = true;
            affectedAuthorities = pending.Keys
                .Concat(readyNodes.Keys)
                .Distinct(pending.Comparer)
                .ToArray();
            discarded = pending.Values.ToArray();
            pending.Clear();
            ready.Clear();
            readyNodes.Clear();
            completions = affectedAuthorities
                .Select(CaptureIdleCompletionsLocked)
                .ToArray();
            if (active.Count == 0 && idleWaiters.Count > 0)
            {
                var allWaiters = idleWaiters.ToArray();
                idleWaiters.Clear();
                completions =
                [
                    .. completions,
                    new IdleCompletions(
                        AuthorityKey: null,
                        AuthorityWaiters: null,
                        AllWaiters: allWaiters)
                ];
            }
        }

        foreach (var work in discarded)
        {
            CompleteTerminal(work);
        }

        foreach (var completion in completions)
        {
            CompleteIdle(completion);
        }
    }

    private void TryDispatchOne()
    {
        string? authorityKey;
        lock (gate)
        {
            authorityKey = TakeReadyLocked();
            if (authorityKey is not null)
            {
                active.Add(authorityKey);
            }
        }

        if (authorityKey is null)
        {
            return;
        }

        if (!scheduler.TryAdmitBackground(
                workType,
                authorityKey,
                cancellationToken => ExecuteLatestAsync(
                    authorityKey,
                    cancellationToken),
                out var admission))
        {
            var schedulerAccepting = scheduler.IsAccepting;
            PendingWork? rejected = null;
            IdleCompletions completions;
            lock (gate)
            {
                active.Remove(authorityKey);
                if (!stopped && schedulerAccepting)
                {
                    EnqueueReadyLocked(authorityKey, retryFirst: true);
                }
                else
                {
                    pending.Remove(authorityKey, out rejected);
                }

                completions = CaptureIdleCompletionsLocked(authorityKey);
            }

            CompleteTerminal(rejected);
            CompleteIdle(completions);
            scheduler.RequestCapacityPump();
            return;
        }

        _ = ObserveAdmissionAsync(authorityKey, admission.Completion);
    }

    private async Task ExecuteLatestAsync(
        string authorityKey,
        CancellationToken cancellationToken)
    {
        PendingWork? work;
        lock (gate)
        {
            pending.Remove(authorityKey, out work);
        }

        if (work is null)
        {
            return;
        }

        try
        {
            await work.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CompleteTerminal(work);
        }
    }

    private async Task ObserveAdmissionAsync(
        string authorityKey,
        Task completion)
    {
        try
        {
            await completion.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        var schedulerAccepting = scheduler.IsAccepting;
        PendingWork? rejected = null;
        IdleCompletions completions;
        lock (gate)
        {
            active.Remove(authorityKey);
            if (pending.ContainsKey(authorityKey))
            {
                if (!stopped && schedulerAccepting)
                {
                    EnqueueReadyLocked(authorityKey);
                }
                else
                {
                    pending.Remove(authorityKey, out rejected);
                }
            }

            completions = CaptureIdleCompletionsLocked(authorityKey);
        }

        CompleteTerminal(rejected);
        CompleteIdle(completions);
        TryDispatchOne();
    }

    private void EnqueueReadyLocked(
        string authorityKey,
        bool retryFirst = false)
    {
        if (active.Contains(authorityKey)
            || readyNodes.ContainsKey(authorityKey)
            || !pending.ContainsKey(authorityKey))
        {
            return;
        }

        readyNodes.Add(
            authorityKey,
            retryFirst
                ? ready.AddFirst(authorityKey)
                : ready.AddLast(authorityKey));
    }

    private string? TakeReadyLocked()
    {
        while (!stopped && ready.First is { } node)
        {
            ready.Remove(node);
            readyNodes.Remove(node.Value);
            if (!active.Contains(node.Value)
                && pending.ContainsKey(node.Value))
            {
                return node.Value;
            }
        }

        return null;
    }

    private void RemoveReadyLocked(string authorityKey)
    {
        if (!readyNodes.Remove(authorityKey, out var node))
        {
            return;
        }

        ready.Remove(node);
    }

    private bool IsIdleLocked(string authorityKey)
        => !pending.ContainsKey(authorityKey)
            && !active.Contains(authorityKey)
            && !readyNodes.ContainsKey(authorityKey);

    private IdleCompletions CaptureIdleCompletionsLocked(string authorityKey)
    {
        TaskCompletionSource[]? authorityWaiters = null;
        TaskCompletionSource[]? allWaiters = null;
        var authorityBecameIdle = IsIdleLocked(authorityKey);
        if (authorityBecameIdle
            && authorityIdleWaiters.Remove(authorityKey, out var waiters))
        {
            authorityWaiters = waiters.ToArray();
        }

        if (pending.Count == 0
            && active.Count == 0
            && idleWaiters.Count > 0)
        {
            allWaiters = idleWaiters.ToArray();
            idleWaiters.Clear();
        }

        return new IdleCompletions(
            authorityBecameIdle ? authorityKey : null,
            authorityWaiters,
            allWaiters);
    }

    private void CompleteIdle(IdleCompletions completions)
    {
        if (completions.AuthorityKey is not null)
        {
            NotifyAuthorityStateChanged(completions.AuthorityKey);
        }

        if (completions.AuthorityWaiters is not null)
        {
            foreach (var waiter in completions.AuthorityWaiters)
            {
                waiter.TrySetResult();
            }
        }

        if (completions.AllWaiters is not null)
        {
            foreach (var waiter in completions.AllWaiters)
            {
                waiter.TrySetResult();
            }
        }
    }

    private void NotifyAuthorityStateChanged(string? authorityKey)
    {
        if (authorityStateChanged is null || authorityKey is null)
        {
            return;
        }

        try
        {
            authorityStateChanged(authorityKey);
        }
        catch (Exception)
        {
        }
    }

    private static void CompleteTerminal(PendingWork? work)
    {
        if (work?.OnTerminal is null)
        {
            return;
        }

        try
        {
            work.OnTerminal();
        }
        catch (Exception)
        {
        }
    }

    private sealed record PendingWork(
        Func<CancellationToken, Task> ExecuteAsync,
        Action? OnTerminal);

    private sealed record IdleCompletions(
        string? AuthorityKey,
        TaskCompletionSource[]? AuthorityWaiters,
        TaskCompletionSource[]? AllWaiters);
}
