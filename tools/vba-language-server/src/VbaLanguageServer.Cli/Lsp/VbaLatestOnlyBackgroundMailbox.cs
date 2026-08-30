using VbaLanguageServer.ProjectModel;

namespace VbaLanguageServer.Lsp;

/// <summary>
/// Owns latest-only background admission for independent authority keys.
/// </summary>
internal sealed class VbaLatestOnlyBackgroundMailbox
{
    private readonly object gate = new();
    private readonly VbaInteractiveWorkScheduler scheduler;
    private readonly VbaInteractiveBackgroundWorkType workType;
    private readonly Dictionary<AuthorityIdentity, PendingWork> pending;
    private readonly HashSet<AuthorityIdentity> active;
    private readonly LinkedList<AuthorityIdentity> ready = new();
    private readonly Dictionary<
        AuthorityIdentity,
        LinkedListNode<AuthorityIdentity>> readyNodes;
    private readonly Dictionary<
        AuthorityIdentity,
        List<TaskCompletionSource>> authorityIdleWaiters;
    private readonly List<TaskCompletionSource> idleWaiters = [];
    private readonly Action<string>? authorityStateChanged;
    private readonly Action<VbaDocumentIdentity>?
        documentAuthorityStateChanged;
    private bool stopped;

    /// <summary>
    /// Creates a mailbox over one scheduler-owned background work class.
    /// </summary>
    public VbaLatestOnlyBackgroundMailbox(
        VbaInteractiveWorkScheduler scheduler,
        VbaInteractiveBackgroundWorkType workType,
        IEqualityComparer<string>? authorityComparer = null,
        Action<string>? authorityStateChanged = null,
        Action<VbaDocumentIdentity>?
            documentAuthorityStateChanged = null)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        var comparer = authorityComparer ?? StringComparer.OrdinalIgnoreCase;
        this.scheduler = scheduler;
        this.workType = workType;
        this.authorityStateChanged = authorityStateChanged;
        this.documentAuthorityStateChanged =
            documentAuthorityStateChanged;
        var authorityIdentityComparer =
            new AuthorityIdentityComparer(comparer);
        pending = new Dictionary<AuthorityIdentity, PendingWork>(
            authorityIdentityComparer);
        active = new HashSet<AuthorityIdentity>(authorityIdentityComparer);
        readyNodes = new Dictionary<
            AuthorityIdentity,
            LinkedListNode<AuthorityIdentity>>(authorityIdentityComparer);
        authorityIdleWaiters =
            new Dictionary<
                AuthorityIdentity,
                List<TaskCompletionSource>>(authorityIdentityComparer);
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
        Post(
            AuthorityIdentity.FromText(authorityKey),
            executeAsync,
            onTerminal);
    }

    public void Post(
        VbaDocumentIdentity authority,
        Func<CancellationToken, Task> executeAsync,
        Action? onTerminal = null)
        => Post(
            AuthorityIdentity.FromDocument(authority),
            executeAsync,
            onTerminal);

    private void Post(
        AuthorityIdentity authority,
        Func<CancellationToken, Task> executeAsync,
        Action? onTerminal)
    {
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
                pending.Remove(authority, out superseded);
                pending[authority] = new PendingWork(executeAsync, onTerminal);
                EnqueueReadyLocked(authority);
            }
        }

        CompleteTerminal(superseded);
        if (reject)
        {
            CompleteTerminal(new PendingWork(executeAsync, onTerminal));
            NotifyAuthorityStateChanged(authority);
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
        Discard(AuthorityIdentity.FromText(authorityKey));
    }

    public void Discard(VbaDocumentIdentity authority)
        => Discard(AuthorityIdentity.FromDocument(authority));

    private void Discard(AuthorityIdentity authority)
    {
        PendingWork? discarded;
        IdleCompletions completions;
        lock (gate)
        {
            pending.Remove(authority, out discarded);
            RemoveReadyLocked(authority);
            completions = CaptureIdleCompletionsLocked(authority);
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
        return IsIdle(AuthorityIdentity.FromText(authorityKey));
    }

    public bool IsIdle(VbaDocumentIdentity authority)
        => IsIdle(AuthorityIdentity.FromDocument(authority));

    private bool IsIdle(AuthorityIdentity authority)
    {
        lock (gate)
        {
            return IsIdleLocked(authority);
        }
    }

    /// <summary>
    /// Waits until one authority has no pending or active work.
    /// </summary>
    public Task WaitForIdleAsync(string authorityKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorityKey);
        return WaitForIdleAsync(AuthorityIdentity.FromText(authorityKey));
    }

    public Task WaitForIdleAsync(VbaDocumentIdentity authority)
        => WaitForIdleAsync(AuthorityIdentity.FromDocument(authority));

    private Task WaitForIdleAsync(AuthorityIdentity authority)
    {
        lock (gate)
        {
            if (IsIdleLocked(authority))
            {
                return Task.CompletedTask;
            }

            var waiter = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!authorityIdleWaiters.TryGetValue(authority, out var waiters))
            {
                waiters = [];
                authorityIdleWaiters.Add(authority, waiters);
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
        AuthorityIdentity[] affectedAuthorities;
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
        AuthorityIdentity? authority;
        lock (gate)
        {
            authority = TakeReadyLocked();
            if (authority is { } readyAuthority)
            {
                active.Add(readyAuthority);
            }
        }

        if (authority is not { } authorityKey)
        {
            return;
        }

        var admitted = authorityKey.TryGetDocument(out var documentAuthority)
            ? scheduler.TryAdmitBackground(
                workType,
                documentAuthority,
                cancellationToken => ExecuteLatestAsync(
                    authorityKey,
                    cancellationToken),
                out var admission)
            : scheduler.TryAdmitBackground(
                workType,
                authorityKey.Text,
                cancellationToken => ExecuteLatestAsync(
                    authorityKey,
                    cancellationToken),
                out admission);
        if (!admitted)
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
        AuthorityIdentity authorityKey,
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
        AuthorityIdentity authorityKey,
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
        AuthorityIdentity authorityKey,
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

    private AuthorityIdentity? TakeReadyLocked()
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

    private void RemoveReadyLocked(AuthorityIdentity authorityKey)
    {
        if (!readyNodes.Remove(authorityKey, out var node))
        {
            return;
        }

        ready.Remove(node);
    }

    private bool IsIdleLocked(AuthorityIdentity authorityKey)
        => !pending.ContainsKey(authorityKey)
            && !active.Contains(authorityKey)
            && !readyNodes.ContainsKey(authorityKey);

    private IdleCompletions CaptureIdleCompletionsLocked(
        AuthorityIdentity authorityKey)
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

    private void NotifyAuthorityStateChanged(
        AuthorityIdentity? authority)
    {
        if (authority is not { } authorityKey)
        {
            return;
        }

        try
        {
            if (authorityKey.TryGetText(out var textAuthority))
            {
                authorityStateChanged?.Invoke(textAuthority);
            }
            else if (authorityKey.TryGetDocument(
                out var documentAuthority))
            {
                documentAuthorityStateChanged?.Invoke(
                    documentAuthority);
            }
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
        AuthorityIdentity? AuthorityKey,
        TaskCompletionSource[]? AuthorityWaiters,
        TaskCompletionSource[]? AllWaiters);

    private readonly struct AuthorityIdentity
    {
        private readonly string? text;
        private readonly VbaDocumentIdentity document;

        private AuthorityIdentity(
            string? text,
            VbaDocumentIdentity document)
        {
            this.text = text;
            this.document = document;
        }

        internal string Text
            => text
                ?? throw new InvalidOperationException(
                    "A document authority has no text key.");

        internal static AuthorityIdentity FromText(string text)
            => new(text, default);

        internal static AuthorityIdentity FromDocument(
            VbaDocumentIdentity document)
            => new(text: null, document);

        internal bool TryGetText(out string value)
        {
            value = text ?? "";
            return text is not null;
        }

        internal bool TryGetDocument(
            out VbaDocumentIdentity value)
        {
            value = document;
            return text is null;
        }
    }

    private sealed class AuthorityIdentityComparer(
        IEqualityComparer<string> textComparer)
        : IEqualityComparer<AuthorityIdentity>
    {
        public bool Equals(AuthorityIdentity left, AuthorityIdentity right)
            => left.TryGetText(out var leftText)
                ? right.TryGetText(out var rightText)
                    && textComparer.Equals(leftText, rightText)
                : !right.TryGetText(out _)
                    && left.TryGetDocument(out var leftDocument)
                    && right.TryGetDocument(out var rightDocument)
                    && leftDocument == rightDocument;

        public int GetHashCode(AuthorityIdentity identity)
            => identity.TryGetText(out var text)
                ? HashCode.Combine(0, textComparer.GetHashCode(text))
                : identity.TryGetDocument(out var document)
                    ? HashCode.Combine(1, document.GetHashCode())
                    : 0;
    }
}
