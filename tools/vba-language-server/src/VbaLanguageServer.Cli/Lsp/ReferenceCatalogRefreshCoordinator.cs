using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Workspace;

namespace VbaLanguageServer.Lsp;

internal sealed record ReferenceCatalogRefreshSessionMessage(
    ReferenceCatalogRefreshLogMessage Message,
    bool PublishOnce);

internal sealed record ReferenceCatalogRefreshPlan(
    string Uri,
    IReadOnlyList<VbaProjectReferenceSelectionContext> Selections,
    long Revision,
    IReadOnlyDictionary<string, long> ScopeRevisions);

internal interface IReferenceCatalogRefreshPlanObserver
{
    void AfterPlanReservedBeforePost(string uri, long revision);

    void BeforePlanCommit(string uri, long revision);
}

internal sealed class NullReferenceCatalogRefreshPlanObserver
    : IReferenceCatalogRefreshPlanObserver
{
    public static NullReferenceCatalogRefreshPlanObserver Instance { get; } = new();

    private NullReferenceCatalogRefreshPlanObserver()
    {
    }

    public void AfterPlanReservedBeforePost(string uri, long revision)
    {
    }

    public void BeforePlanCommit(string uri, long revision)
    {
    }
}

internal interface IReferenceCatalogLifecycle
{
    void ActivateProject(string uri);

    void ApplyManifestSelectionChange(string uri, string text);

    void DeactivateManifest(string uri);
}

internal interface IReferenceCatalogRuntimeLifecycle : IReferenceCatalogLifecycle
{
    void AttachScheduler(VbaInteractiveWorkScheduler scheduler);

    Task StopAsync();
}

internal sealed class VbaInteractiveReferenceCatalogMutationLane
    : IVbaProjectReferenceCatalogMutationLane
{
    private readonly VbaInteractiveWorkScheduler scheduler;

    public VbaInteractiveReferenceCatalogMutationLane(VbaInteractiveWorkScheduler scheduler)
    {
        this.scheduler = scheduler;
    }

    public async Task CommitAsync(
        string authorityKey,
        Action commit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorityKey);
        ArgumentNullException.ThrowIfNull(commit);
        cancellationToken.ThrowIfCancellationRequested();
        var admission = await scheduler.AdmitRequiredMutationAsync(
                "vba/referenceCatalogCommit",
                schedulerCancellationToken =>
                {
                    schedulerCancellationToken.ThrowIfCancellationRequested();
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        commit();
                    }

                    return Task.CompletedTask;
                },
                cancellationToken)
            .ConfigureAwait(false);
        await admission.Completion.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }
}

/// <summary>
/// Owns project-scoped reference catalog activation, trace publication, and background refresh.
/// </summary>
internal sealed class ReferenceCatalogRefreshCoordinator : IReferenceCatalogRuntimeLifecycle
{
    private static readonly TimeSpan ShutdownWaitTimeout = TimeSpan.FromSeconds(1);
    private readonly VbaProjectReferenceCatalogCache catalogCache;
    private readonly VbaProjectReferenceCatalogRefreshService refreshService;
    private readonly VbaProjectManifestWorkspace manifestWorkspace;
    private readonly LspMessageTransport transport;
    private readonly IVbaProjectReferenceCatalogLifecycleObserver lifecycleObserver;
    private readonly IReferenceCatalogRefreshPlanObserver planObserver;
    private readonly object diagnosticGate = new();
    private readonly HashSet<string> publishedDiagnostics = new(StringComparer.Ordinal);
    private readonly object lifecycleGate = new();
    private readonly object lifecyclePlanGate = new();
    private readonly Dictionary<string, ReferenceCatalogLifecycleState> lifecycleStates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SharedReferenceCatalogWork> sharedAutomaticWork =
        new(StringComparer.Ordinal);
    private readonly HashSet<SharedReferenceCatalogWork> activeAutomaticWork = [];
    private readonly HashSet<Task> backgroundTasks = [];
    private readonly Dictionary<string, long> latestLifecycleScopeRevisions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private VbaInteractiveWorkScheduler? scheduler;
    private VbaLatestOnlyBackgroundMailbox? lifecycleMailbox;
    private long lifecycleRevision;
    private long lifecyclePlanRevision;
    private bool stopping;

    /// <summary>
    /// Creates a reference catalog lifecycle coordinator.
    /// </summary>
    /// <param name="catalogCache">The current reference catalog state.</param>
    /// <param name="refreshService">The service that preloads and refreshes reference catalogs.</param>
    /// <param name="manifestWorkspace">The effective manifest authority used for selection resolution.</param>
    /// <param name="transport">The transport used to publish log messages.</param>
    public ReferenceCatalogRefreshCoordinator(
        VbaProjectReferenceCatalogCache catalogCache,
        VbaProjectReferenceCatalogRefreshService refreshService,
        VbaProjectManifestWorkspace manifestWorkspace,
        LspMessageTransport transport,
        IVbaProjectReferenceCatalogLifecycleObserver? lifecycleObserver = null,
        IReferenceCatalogRefreshPlanObserver? planObserver = null)
    {
        this.catalogCache = catalogCache;
        this.refreshService = refreshService;
        this.manifestWorkspace = manifestWorkspace;
        this.transport = transport;
        this.lifecycleObserver =
            lifecycleObserver ?? NullVbaProjectReferenceCatalogLifecycleObserver.Instance;
        this.planObserver =
            planObserver ?? NullReferenceCatalogRefreshPlanObserver.Instance;
    }

    /// <summary>
    /// Attaches the runtime-owned scheduler before automatic lifecycle work starts.
    /// </summary>
    public void AttachScheduler(VbaInteractiveWorkScheduler interactiveScheduler)
    {
        ArgumentNullException.ThrowIfNull(interactiveScheduler);
        lock (lifecyclePlanGate)
        {
            lock (lifecycleGate)
            {
                if (scheduler is not null && !ReferenceEquals(scheduler, interactiveScheduler))
                {
                    throw new InvalidOperationException(
                        "The reference catalog coordinator is already attached to another scheduler.");
                }

                if (lifecycleMailbox is not null)
                {
                    return;
                }

                if (backgroundTasks.Count > 0 || lifecycleStates.Count > 0)
                {
                    throw new InvalidOperationException(
                        "The reference catalog scheduler must be attached before lifecycle work starts.");
                }

                scheduler = interactiveScheduler;
                lifecycleMailbox = new VbaLatestOnlyBackgroundMailbox(
                    interactiveScheduler,
                    VbaInteractiveBackgroundWorkType.ReferenceCatalogRefresh,
                    StringComparer.OrdinalIgnoreCase);
            }
        }

        refreshService.AttachMutationLane(
            new VbaInteractiveReferenceCatalogMutationLane(interactiveScheduler));
    }

    /// <summary>
    /// Activates the project scope containing a source document.
    /// </summary>
    /// <param name="uri">The source document URI.</param>
    public void ActivateProject(string uri)
    {
        if (TryCreateLifecyclePlan(uri, text: "", out var plan))
        {
            ScheduleLifecycle(plan, beforePost: null);
        }
    }

    /// <summary>
    /// Applies an effective manifest reference-selection change.
    /// </summary>
    /// <param name="uri">The effective manifest URI.</param>
    /// <param name="text">The effective manifest text.</param>
    public void ApplyManifestSelectionChange(string uri, string text)
    {
        if (!TryCreateLifecyclePlan(uri, text, out var plan))
        {
            DeactivateManifest(uri);
            return;
        }

        ScheduleLifecycle(
            plan,
            () => RemoveMissingManifestScopes(uri, plan.Selections));
    }

    /// <summary>
    /// Removes lifecycle state owned by a manifest that no longer has an effective selection.
    /// </summary>
    /// <param name="uri">The manifest URI.</param>
    public void DeactivateManifest(string uri)
    {
        var scopePrefix = CreateManifestScopePrefix(uri);
        var removedFingerprints = new HashSet<string>(StringComparer.Ordinal);
        lock (lifecyclePlanGate)
        {
            VbaLatestOnlyBackgroundMailbox? mailbox;
            lock (lifecycleGate)
            {
                mailbox = lifecycleMailbox;
                foreach (var scopeKey in latestLifecycleScopeRevisions.Keys
                    .Concat(lifecycleStates.Keys)
                    .Where(key => key.StartsWith(scopePrefix, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray())
                {
                    latestLifecycleScopeRevisions.Remove(scopeKey);
                    if (lifecycleStates.Remove(scopeKey, out var state))
                    {
                        removedFingerprints.Add(state.Fingerprint);
                    }
                }
            }

            mailbox?.Discard(uri);
        }

        CancelUnusedSharedWork(removedFingerprints);
    }

    /// <summary>
    /// Waits until all automatic lifecycle work admitted so far has completed.
    /// </summary>
    public async Task WaitForIdleAsync()
    {
        while (true)
        {
            Task[] tasks;
            Task mailboxIdle;
            lock (lifecycleGate)
            {
                tasks = backgroundTasks.ToArray();
                mailboxIdle = lifecycleMailbox?.WaitForIdleAsync()
                    ?? Task.CompletedTask;
            }

            if (tasks.Length == 0 && mailboxIdle.IsCompletedSuccessfully)
            {
                return;
            }

            await Task.WhenAll(tasks.Append(mailboxIdle));
        }
    }

    /// <summary>
    /// Cancels and observes all automatic lifecycle work.
    /// </summary>
    public async Task StopAsync()
    {
        Task[] tasks;
        VbaLatestOnlyBackgroundMailbox? mailbox;
        var cancel = false;
        lock (lifecyclePlanGate)
        {
            lock (lifecycleGate)
            {
                if (!stopping)
                {
                    stopping = true;
                    cancel = true;
                    latestLifecycleScopeRevisions.Clear();
                }

                mailbox = lifecycleMailbox;
                tasks = backgroundTasks.ToArray();
            }

            mailbox?.Stop();
        }

        var cancellation = cancel
            ? lifetimeCancellation.CancelAsync()
            : Task.CompletedTask;
        var mailboxIdle = mailbox?.WaitForIdleAsync()
            ?? Task.CompletedTask;

        try
        {
            await Task.WhenAll(
                    tasks
                        .Append(cancellation)
                        .Append(mailboxIdle))
                .WaitAsync(ShutdownWaitTimeout);
        }
        catch (TimeoutException)
        {
            // Non-cooperative TypeLib COM calls remain observed in the background
            // but must not prevent the language-server process from shutting down.
            return;
        }
        catch (Exception)
        {
            // Cancellation callbacks and already-observed background tasks may fault,
            // but shutdown must still release retained lifecycle resources.
        }

        SharedReferenceCatalogWork[] retainedWork;
        lock (lifecycleGate)
        {
            retainedWork = sharedAutomaticWork.Values.ToArray();
            sharedAutomaticWork.Clear();
        }

        foreach (var sharedWork in retainedWork)
        {
            await sharedWork.DisposeCancellationWhenCompleteAsync();
        }
    }

    private void ScheduleLifecycle(
        ReferenceCatalogRefreshPlan plan,
        Action? beforePost)
    {
        lock (lifecyclePlanGate)
        {
            VbaLatestOnlyBackgroundMailbox mailbox;
            ReferenceCatalogRefreshPlan scheduledPlan;
            lock (lifecycleGate)
            {
                if (stopping)
                {
                    return;
                }

                mailbox = lifecycleMailbox
                    ?? throw new InvalidOperationException(
                        "The reference catalog scheduler must be attached before lifecycle work starts.");
                var revision = ++lifecyclePlanRevision;
                var scopeRevisions = plan.Selections
                    .Select(selection => selection.ScopeKey)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        scopeKey => scopeKey,
                        _ => revision,
                        StringComparer.OrdinalIgnoreCase);
                foreach (var scopeRevision in scopeRevisions)
                {
                    latestLifecycleScopeRevisions[scopeRevision.Key] =
                        scopeRevision.Value;
                }

                scheduledPlan = plan with
                {
                    Revision = revision,
                    ScopeRevisions = scopeRevisions
                };
            }

            planObserver.AfterPlanReservedBeforePost(
                scheduledPlan.Uri,
                scheduledPlan.Revision);
            beforePost?.Invoke();
            mailbox.Post(
                scheduledPlan.Uri,
                cancellationToken =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    planObserver.BeforePlanCommit(
                        scheduledPlan.Uri,
                        scheduledPlan.Revision);
                    ScheduleLifecycleCore(scheduledPlan);
                    return Task.CompletedTask;
                });
        }
    }

    private void ScheduleLifecycleCore(ReferenceCatalogRefreshPlan plan)
    {
        var scheduledSelections = new List<ScheduledReferenceCatalogSelection>();
        var replacedFingerprints = new HashSet<string>(StringComparer.Ordinal);
        lock (lifecycleGate)
        {
            if (stopping)
            {
                return;
            }

            foreach (var selection in plan.Selections)
            {
                if (!IsCurrentLifecycleScopeCore(
                        plan,
                        selection.ScopeKey))
                {
                    continue;
                }

                var fingerprint = CreateSelectionFingerprint(selection);
                if (lifecycleStates.TryGetValue(selection.ScopeKey, out var current)
                    && current.Fingerprint.Equals(fingerprint, StringComparison.Ordinal))
                {
                    continue;
                }

                if (current is not null)
                {
                    replacedFingerprints.Add(current.Fingerprint);
                }

                lifecycleRevision++;
                var state = new ReferenceCatalogLifecycleState(
                    fingerprint,
                    lifecycleRevision);
                lifecycleStates[selection.ScopeKey] = state;
                scheduledSelections.Add(new ScheduledReferenceCatalogSelection(
                    plan.Uri,
                    selection,
                    state));
            }
        }

        CancelUnusedSharedWork(replacedFingerprints);
        foreach (var selectionGroup in scheduledSelections.GroupBy(
            selection => selection.State.Fingerprint,
            StringComparer.Ordinal))
        {
            SharedReferenceCatalogWork? automaticWork = null;
            foreach (var selection in selectionGroup)
            {
                automaticWork ??= GetOrStartSharedAutomaticWork(selection);
            }

            foreach (var selection in selectionGroup)
            {
                StartSelectionLifecycle(selection, automaticWork);
            }
        }
    }

    private void StartSelectionLifecycle(
        ScheduledReferenceCatalogSelection selection,
        SharedReferenceCatalogWork? automaticWork)
    {
        Task task;
        lock (lifecycleGate)
        {
            if (stopping || !IsCurrentLifecycleCore(selection))
            {
                return;
            }

            task = Task.Run(
                () => RunSelectionLifecycleAsync(
                    selection,
                    automaticWork,
                    lifetimeCancellation.Token),
                CancellationToken.None);
            backgroundTasks.Add(task);
        }

        ObserveBackgroundTask(task);
    }

    private async Task RunSelectionLifecycleAsync(
        ScheduledReferenceCatalogSelection scheduledSelection,
        SharedReferenceCatalogWork? automaticWork,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!IsCurrentLifecycle(scheduledSelection))
            {
                return;
            }

            var context = scheduledSelection.Context;
            foreach (var sessionMessage in CreateReferenceSelectionTraceMessages(
                scheduledSelection.Uri,
                context))
            {
                if (!IsCurrentLifecycle(scheduledSelection))
                {
                    return;
                }

                await PublishSessionMessageAsync(sessionMessage, cancellationToken);
            }

            if (automaticWork is null)
            {
                return;
            }

            var persistedPreloadResults = await automaticWork.PersistedPreloadResults
                .WaitAsync(cancellationToken);
            if (!IsCurrentLifecycle(scheduledSelection))
            {
                return;
            }

            foreach (var result in persistedPreloadResults)
            {
                await PublishCatalogRefreshResultAsync(
                    context.DocumentName,
                    result,
                    cancellationToken);
            }

            var results = await automaticWork.Task;
            if (!IsCurrentLifecycle(scheduledSelection))
            {
                return;
            }

            foreach (var result in results.Skip(persistedPreloadResults.Count))
            {
                await PublishCatalogRefreshResultAsync(
                    context.DocumentName,
                    result,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            try
            {
                await PublishCatalogNotificationAsync(
                    $"lifecycle-failure:{scheduledSelection.Context.ScopeKey}",
                    token => transport.WriteLogMessageAsync(
                        2,
                        $"Reference catalog lifecycle failed without changing committed editor metadata: {ex.Message}",
                        token),
                    cancellationToken);
            }
            catch (Exception)
            {
                // The lifecycle task is observed even when the transport is already unavailable.
            }
        }
    }

    private SharedReferenceCatalogWork?
        GetOrStartSharedAutomaticWork(ScheduledReferenceCatalogSelection selection)
    {
        lock (lifecycleGate)
        {
            if (stopping || !IsCurrentLifecycleCore(selection))
            {
                return null;
            }

            var fingerprint = selection.State.Fingerprint;
            if (sharedAutomaticWork.TryGetValue(fingerprint, out var existing)
                && !existing.Cancellation.IsCancellationRequested)
            {
                return existing;
            }

            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                lifetimeCancellation.Token);
            var referenceNames = selection.Context.Selection.References
                .Select(reference => reference.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var dependencies = activeAutomaticWork
                .Where(work => work.ReferenceNames.Overlaps(referenceNames))
                .Select(work => work.Task)
                .ToArray();
            var persistedPreloadResults =
                new TaskCompletionSource<IReadOnlyList<VbaProjectReferenceCatalogRefreshResult>>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            var task = Task.Run(
                () => RunAutomaticWorkAsync(
                    selection.Context,
                    dependencies,
                    persistedPreloadResults,
                    cancellation.Token),
                CancellationToken.None);
            var sharedWork = new SharedReferenceCatalogWork(
                task,
                persistedPreloadResults.Task,
                cancellation,
                referenceNames);
            sharedAutomaticWork[fingerprint] = sharedWork;
            activeAutomaticWork.Add(sharedWork);
            backgroundTasks.Add(task);
            ObserveSharedAutomaticWork(fingerprint, sharedWork);
            return sharedWork;
        }
    }

    private async Task<IReadOnlyList<VbaProjectReferenceCatalogRefreshResult>> RunAutomaticWorkAsync(
        VbaProjectReferenceSelectionContext selectionContext,
        IReadOnlyList<Task<IReadOnlyList<VbaProjectReferenceCatalogRefreshResult>>> dependencies,
        TaskCompletionSource<IReadOnlyList<VbaProjectReferenceCatalogRefreshResult>>
            persistedPreloadResults,
        CancellationToken cancellationToken)
    {
        try
        {
            if (dependencies.Count > 0)
            {
                await Task.WhenAll(dependencies).WaitAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return await refreshService.RefreshAutomaticallyAsync(
                selectionContext.Selection,
                results => persistedPreloadResults.TrySetResult(results),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return [];
        }
        catch (Exception ex)
        {
            try
            {
                await WriteLogMessageOnceAsync(
                    2,
                    $"Reference catalog lifecycle failed without changing committed editor metadata: {ex.Message}",
                    $"lifecycle-failure\u001f{selectionContext.ScopeKey}\u001f{ex.Message}",
                    cancellationToken);
            }
            catch (Exception)
            {
                // The lifecycle task is observed even when the transport is already unavailable.
            }

            return [];
        }
        finally
        {
            persistedPreloadResults.TrySetResult([]);
        }
    }

    private bool IsCurrentLifecycle(ScheduledReferenceCatalogSelection selection)
    {
        lock (lifecycleGate)
        {
            return IsCurrentLifecycleCore(selection);
        }
    }

    private bool IsCurrentLifecycleCore(ScheduledReferenceCatalogSelection selection)
        => lifecycleStates.TryGetValue(selection.Context.ScopeKey, out var current)
            && current.Fingerprint.Equals(
                selection.State.Fingerprint,
                StringComparison.Ordinal)
            && current.Revision == selection.State.Revision;

    private void CancelUnusedSharedWork(IEnumerable<string> fingerprints)
    {
        var workToCancel = new List<SharedReferenceCatalogWork>();
        lock (lifecycleGate)
        {
            foreach (var fingerprint in fingerprints.Distinct(StringComparer.Ordinal))
            {
                if (lifecycleStates.Values.Any(state =>
                        state.Fingerprint.Equals(fingerprint, StringComparison.Ordinal))
                    || !sharedAutomaticWork.Remove(fingerprint, out var sharedWork))
                {
                    continue;
                }

                workToCancel.Add(sharedWork);
            }
        }

        foreach (var sharedWork in workToCancel)
        {
            sharedWork.DispatchCancellation();
            _ = sharedWork.DisposeCancellationWhenCompleteAsync();
        }
    }

    private void ObserveBackgroundTask(Task task)
        => _ = task.ContinueWith(
            completed =>
            {
                lock (lifecycleGate)
                {
                    backgroundTasks.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private void ObserveSharedAutomaticWork(
        string fingerprint,
        SharedReferenceCatalogWork sharedWork)
        => _ = sharedWork.Task.ContinueWith(
            completed =>
            {
                lock (lifecycleGate)
                {
                    backgroundTasks.Remove(completed);
                    activeAutomaticWork.Remove(sharedWork);
                    if (sharedAutomaticWork.TryGetValue(fingerprint, out var current)
                        && ReferenceEquals(current, sharedWork))
                    {
                        sharedAutomaticWork.Remove(fingerprint);
                    }
                }

                _ = sharedWork.DisposeCancellationWhenCompleteAsync();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private Task PublishSessionMessageAsync(
        ReferenceCatalogRefreshSessionMessage sessionMessage,
        CancellationToken cancellationToken)
        => sessionMessage.PublishOnce
            ? WriteLogMessageOnceAsync(sessionMessage.Message, cancellationToken)
            : PublishCatalogNotificationAsync(
                sessionMessage.Message.Key,
                token => transport.WriteLogMessageAsync(
                    sessionMessage.Message.Type,
                    sessionMessage.Message.Text,
                    token),
                cancellationToken);

    private async Task PublishCatalogRefreshResultAsync(
        string documentName,
        VbaProjectReferenceCatalogRefreshResult result,
        CancellationToken cancellationToken)
    {
        await WriteLogMessageOnceAsync(
            ReferenceCatalogRefreshOutcome.CreateDiagnosticMessage(documentName, result),
            cancellationToken);

        foreach (var message in ReferenceCatalogRefreshOutcome.CreateDiscoveryMessages(documentName, result))
        {
            await PublishCatalogNotificationAsync(
                $"{documentName}:{result.ReferenceName}:{message.Key}",
                token => transport.WriteLogMessageAsync(
                    message.Type,
                    message.Text,
                    token),
                cancellationToken);
        }
    }

    private Task WriteLogMessageOnceAsync(
        ReferenceCatalogRefreshLogMessage message,
        CancellationToken cancellationToken)
        => WriteLogMessageOnceAsync(message.Type, message.Text, message.Key, cancellationToken);

    private async Task WriteLogMessageOnceAsync(
        int type,
        string message,
        string key,
        CancellationToken cancellationToken)
        => await PublishCatalogNotificationAsync(
            key,
            token => WriteLogMessageOnceCoreAsync(type, message, key, token),
            cancellationToken);

    private async Task WriteLogMessageOnceCoreAsync(
        int type,
        string message,
        string key,
        CancellationToken cancellationToken)
    {
        lock (diagnosticGate)
        {
            if (!publishedDiagnostics.Add(key))
            {
                return;
            }
        }

        try
        {
            await transport.WriteLogMessageAsync(type, message, cancellationToken);
        }
        catch
        {
            lock (diagnosticGate)
            {
                publishedDiagnostics.Remove(key);
            }

            throw;
        }
    }

    private async Task PublishCatalogNotificationAsync(
        string authorityKey,
        Func<CancellationToken, Task> publish,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VbaInteractiveWorkScheduler interactiveScheduler;
        lock (lifecycleGate)
        {
            interactiveScheduler = scheduler
                ?? throw new InvalidOperationException(
                    "The reference catalog scheduler must be attached before publication starts.");
        }

        if (!interactiveScheduler.TryAdmitBackground(
                VbaInteractiveBackgroundWorkType.ReferenceCatalogPublication,
                authorityKey,
                async schedulerCancellationToken =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    using var linkedCancellation =
                        CancellationTokenSource.CreateLinkedTokenSource(
                            schedulerCancellationToken,
                            cancellationToken);
                    try
                    {
                        await publish(linkedCancellation.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                        when (cancellationToken.IsCancellationRequested
                            && !schedulerCancellationToken.IsCancellationRequested)
                    {
                    }
                },
                out var admission))
        {
            return;
        }

        await admission.Completion.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private IReadOnlyList<ReferenceCatalogRefreshSessionMessage> CreateReferenceSelectionTraceMessages(
        string uri,
        VbaProjectReferenceSelectionContext context)
    {
        var references = context.Selection.References.Count == 0
            ? "<none>"
            : string.Join(", ", context.Selection.References.Select(reference => reference.Name));
        var messages = new List<ReferenceCatalogRefreshSessionMessage>
        {
            CreateDirectMessage(
                3,
                $"VbaProjectReferenceSelection document={context.DocumentName} references={references} main={context.Selection.MainVbaProjectReference?.Name ?? "<none>"}",
                $"selection\u001f{uri}\u001f{context.ScopeKey}\u001f{references}")
        };
        if (context.Selection.MissingExpectedMainReference is not null)
        {
            messages.Add(CreateDirectMessage(
                2,
                $"Manifest/reference consistency warning: document '{context.DocumentName}' kind '{context.DocumentKind}' is missing expected main reference '{context.Selection.MissingExpectedMainReference}'. Host definitions will not be activated implicitly.",
                $"selection-warning\u001f{context.ScopeKey}\u001f{context.Selection.MissingExpectedMainReference}"));
        }

        foreach (var referenceName in catalogCache.Current.GetMissingCatalogReferenceNames(context.Selection))
        {
            messages.Add(CreateDirectMessage(
                3,
                $"Reference catalog availability: document '{context.DocumentName}' reference '{referenceName}' editor metadata is not currently available. The reference remains active for workbook build/test, but external editor definitions are unavailable until bundled or generated metadata is available.",
                $"availability-missing\u001f{context.ScopeKey}\u001f{referenceName}"));
        }

        AddAvailabilityMessages(messages, context.DocumentName, context.Selection);
        return messages;
    }

    private void AddAvailabilityMessages(
        ICollection<ReferenceCatalogRefreshSessionMessage> messages,
        string documentName,
        VbaProjectReferenceSelection selection)
    {
        foreach (var reference in selection.References)
        {
            var source = catalogCache.GetCatalogSource(reference.Name);
            if (source == VbaProjectReferenceCatalogSource.Unavailable)
            {
                continue;
            }

            messages.Add(new ReferenceCatalogRefreshSessionMessage(
                ReferenceCatalogRefreshOutcome.CreateAvailabilityMessage(
                    documentName,
                    reference.Name,
                    source),
                PublishOnce: true));
        }
    }

    private bool TryCreateLifecyclePlan(
        string uri,
        string text,
        out ReferenceCatalogRefreshPlan plan)
    {
        plan = default!;
        lifecycleObserver.Record(new VbaProjectReferenceCatalogLifecycleEvent(
            VbaProjectReferenceCatalogLifecycleOperation.ProjectSelectionResolve,
            ScopeKey: uri));
        if (!LanguageServerManifestResolution.TryCreateReferenceSelections(
            uri,
            text,
            manifestWorkspace,
            out var selections))
        {
            return false;
        }

        plan = new ReferenceCatalogRefreshPlan(
            uri,
            selections,
            Revision: 0,
            ScopeRevisions:
                new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase));
        return true;
    }

    private bool IsCurrentLifecycleScopeCore(
        ReferenceCatalogRefreshPlan plan,
        string scopeKey)
        => plan.ScopeRevisions.TryGetValue(scopeKey, out var planRevision)
            && latestLifecycleScopeRevisions.TryGetValue(
                scopeKey,
                out var latestRevision)
            && latestRevision == planRevision;

    private void RemoveMissingManifestScopes(
        string uri,
        IReadOnlyList<VbaProjectReferenceSelectionContext> selections)
    {
        var retainedScopes = selections
            .Select(selection => selection.ScopeKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var scopePrefix = CreateManifestScopePrefix(uri);
        var removedFingerprints = new HashSet<string>(StringComparer.Ordinal);
        lock (lifecycleGate)
        {
            foreach (var scopeKey in latestLifecycleScopeRevisions.Keys
                .Concat(lifecycleStates.Keys)
                .Where(key => key.StartsWith(scopePrefix, StringComparison.OrdinalIgnoreCase))
                .Where(key => !retainedScopes.Contains(key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray())
            {
                latestLifecycleScopeRevisions.Remove(scopeKey);
                if (lifecycleStates.Remove(scopeKey, out var state))
                {
                    removedFingerprints.Add(state.Fingerprint);
                }
            }
        }

        CancelUnusedSharedWork(removedFingerprints);
    }

    private static string CreateSelectionFingerprint(VbaProjectReferenceSelectionContext context)
        => string.Join(
            "\u001f",
            context.DocumentKind.Trim().ToUpperInvariant(),
            context.Selection.MainVbaProjectReference?.Name.Trim().ToUpperInvariant() ?? "",
            context.Selection.MissingExpectedMainReference?.Trim().ToUpperInvariant() ?? "",
            string.Join(
                "\u001e",
                context.Selection.References
                    .Select(reference => reference.Name.Trim().ToUpperInvariant())
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(name => name, StringComparer.Ordinal)));

    private static string CreateManifestScopePrefix(string uri)
    {
        var manifestIdentity = VbaProjectResolver.TryGetLocalPath(uri) ?? uri;
        return $"{Path.GetFullPath(manifestIdentity)}\u001f";
    }

    private static ReferenceCatalogRefreshSessionMessage CreateDirectMessage(
        int type,
        string text,
        string key)
        => new(new ReferenceCatalogRefreshLogMessage(type, text, key), PublishOnce: false);

    private sealed record ReferenceCatalogLifecycleState(string Fingerprint, long Revision);

    private sealed record ScheduledReferenceCatalogSelection(
        string Uri,
        VbaProjectReferenceSelectionContext Context,
        ReferenceCatalogLifecycleState State);

    private sealed class SharedReferenceCatalogWork
    {
        private readonly object cancellationGate = new();
        private Task cancellationDispatch = System.Threading.Tasks.Task.CompletedTask;
        private Task? cancellationDisposal;

        public SharedReferenceCatalogWork(
            Task<IReadOnlyList<VbaProjectReferenceCatalogRefreshResult>> task,
            Task<IReadOnlyList<VbaProjectReferenceCatalogRefreshResult>>
                persistedPreloadResults,
            CancellationTokenSource cancellation,
            IReadOnlySet<string> referenceNames)
        {
            Task = task;
            PersistedPreloadResults = persistedPreloadResults;
            Cancellation = cancellation;
            ReferenceNames = referenceNames;
        }

        public Task<IReadOnlyList<VbaProjectReferenceCatalogRefreshResult>> Task { get; }

        public Task<IReadOnlyList<VbaProjectReferenceCatalogRefreshResult>>
            PersistedPreloadResults { get; }

        public CancellationTokenSource Cancellation { get; }

        public IReadOnlySet<string> ReferenceNames { get; }

        public void DispatchCancellation()
        {
            lock (cancellationGate)
            {
                if (cancellationDisposal is not null)
                {
                    return;
                }

                if (!Cancellation.IsCancellationRequested)
                {
                    cancellationDispatch = Cancellation.CancelAsync();
                }
            }
        }

        public Task DisposeCancellationWhenCompleteAsync()
        {
            lock (cancellationGate)
            {
                cancellationDisposal ??= DisposeCancellationCoreAsync(
                    cancellationDispatch);
                return cancellationDisposal;
            }
        }

        private async Task DisposeCancellationCoreAsync(Task dispatch)
        {
            await ObserveAsync(Task);
            await ObserveAsync(dispatch);
            Cancellation.Dispose();
        }

        private static async Task ObserveAsync(Task task)
        {
            try
            {
                await task;
            }
            catch (Exception)
            {
            }
        }
    }
}
