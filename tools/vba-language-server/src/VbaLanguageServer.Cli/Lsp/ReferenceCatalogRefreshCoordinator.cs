using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Workspace;

namespace VbaLanguageServer.Lsp;

internal sealed record ReferenceCatalogRefreshSessionMessage(
    ReferenceCatalogRefreshLogMessage Message,
    bool PublishOnce);

internal sealed record ReferenceCatalogRefreshPlan(
    string Uri,
    IReadOnlyList<VbaProjectReferenceSelectionContext> Selections);

internal interface IReferenceCatalogLifecycle
{
    void ActivateProject(string uri);

    void ApplyManifestSelectionChange(string uri, string text);

    void DeactivateManifest(string uri);
}

/// <summary>
/// Owns project-scoped reference catalog activation, trace publication, and background refresh.
/// </summary>
internal sealed class ReferenceCatalogRefreshCoordinator : IReferenceCatalogLifecycle
{
    private static readonly TimeSpan ShutdownWaitTimeout = TimeSpan.FromSeconds(1);
    private readonly VbaProjectReferenceCatalogCache catalogCache;
    private readonly VbaProjectReferenceCatalogRefreshService refreshService;
    private readonly VbaProjectManifestWorkspace manifestWorkspace;
    private readonly LspMessageTransport transport;
    private readonly IVbaProjectReferenceCatalogLifecycleObserver lifecycleObserver;
    private readonly object diagnosticGate = new();
    private readonly HashSet<string> publishedDiagnostics = new(StringComparer.Ordinal);
    private readonly object lifecycleGate = new();
    private readonly Dictionary<string, ReferenceCatalogLifecycleState> lifecycleStates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SharedReferenceCatalogWork> sharedAutomaticWork =
        new(StringComparer.Ordinal);
    private readonly HashSet<SharedReferenceCatalogWork> activeAutomaticWork = [];
    private readonly HashSet<Task> backgroundTasks = [];
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private long lifecycleRevision;
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
        IVbaProjectReferenceCatalogLifecycleObserver? lifecycleObserver = null)
    {
        this.catalogCache = catalogCache;
        this.refreshService = refreshService;
        this.manifestWorkspace = manifestWorkspace;
        this.transport = transport;
        this.lifecycleObserver =
            lifecycleObserver ?? NullVbaProjectReferenceCatalogLifecycleObserver.Instance;
    }

    /// <summary>
    /// Activates the project scope containing a source document.
    /// </summary>
    /// <param name="uri">The source document URI.</param>
    public void ActivateProject(string uri)
    {
        if (TryCreateLifecyclePlan(uri, text: "", out var plan))
        {
            ScheduleLifecycle(plan);
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

        RemoveMissingManifestScopes(uri, plan.Selections);
        ScheduleLifecycle(plan);
    }

    /// <summary>
    /// Removes lifecycle state owned by a manifest that no longer has an effective selection.
    /// </summary>
    /// <param name="uri">The manifest URI.</param>
    public void DeactivateManifest(string uri)
    {
        var scopePrefix = CreateManifestScopePrefix(uri);
        var removedFingerprints = new HashSet<string>(StringComparer.Ordinal);
        lock (lifecycleGate)
        {
            foreach (var scopeKey in lifecycleStates.Keys
                .Where(key => key.StartsWith(scopePrefix, StringComparison.OrdinalIgnoreCase))
                .ToArray())
            {
                removedFingerprints.Add(lifecycleStates[scopeKey].Fingerprint);
                lifecycleStates.Remove(scopeKey);
            }
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
            lock (lifecycleGate)
            {
                tasks = backgroundTasks.ToArray();
            }

            if (tasks.Length == 0)
            {
                return;
            }

            await Task.WhenAll(tasks);
        }
    }

    /// <summary>
    /// Cancels and observes all automatic lifecycle work.
    /// </summary>
    public async Task StopAsync()
    {
        Task[] tasks;
        var cancel = false;
        lock (lifecycleGate)
        {
            if (!stopping)
            {
                stopping = true;
                cancel = true;
            }

            tasks = backgroundTasks.ToArray();
        }

        if (cancel)
        {
            lifetimeCancellation.Cancel();
        }

        try
        {
            await Task.WhenAll(tasks).WaitAsync(ShutdownWaitTimeout);
        }
        catch (TimeoutException)
        {
            // Non-cooperative TypeLib COM calls remain observed in the background
            // but must not prevent the language-server process from shutting down.
            return;
        }

        SharedReferenceCatalogWork[] retainedWork;
        lock (lifecycleGate)
        {
            retainedWork = sharedAutomaticWork.Values.ToArray();
            sharedAutomaticWork.Clear();
        }

        foreach (var sharedWork in retainedWork)
        {
            sharedWork.DisposeCancellation();
        }
    }

    private void ScheduleLifecycle(ReferenceCatalogRefreshPlan plan)
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
                await transport.WriteLogMessageAsync(
                    2,
                    $"Reference catalog lifecycle failed without changing committed editor metadata: {ex.Message}",
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
            sharedWork.Cancel();
            sharedWork.DisposeCancellationWhenComplete();
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

                sharedWork.DisposeCancellation();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private Task PublishSessionMessageAsync(
        ReferenceCatalogRefreshSessionMessage sessionMessage,
        CancellationToken cancellationToken)
        => sessionMessage.PublishOnce
            ? WriteLogMessageOnceAsync(sessionMessage.Message, cancellationToken)
            : transport.WriteLogMessageAsync(
                sessionMessage.Message.Type,
                sessionMessage.Message.Text,
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
            await transport.WriteLogMessageAsync(
                message.Type,
                message.Text,
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

        plan = new ReferenceCatalogRefreshPlan(uri, selections);
        return true;
    }

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
            foreach (var scopeKey in lifecycleStates.Keys
                .Where(key => key.StartsWith(scopePrefix, StringComparison.OrdinalIgnoreCase))
                .Where(key => !retainedScopes.Contains(key))
                .ToArray())
            {
                removedFingerprints.Add(lifecycleStates[scopeKey].Fingerprint);
                lifecycleStates.Remove(scopeKey);
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
        private int cancellationDisposed;

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

        public void Cancel()
        {
            try
            {
                Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void DisposeCancellationWhenComplete()
        {
            if (Task.IsCompleted)
            {
                DisposeCancellation();
                return;
            }

            _ = Task.ContinueWith(
                _ => DisposeCancellation(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public void DisposeCancellation()
        {
            if (Interlocked.Exchange(ref cancellationDisposed, 1) == 0)
            {
                Cancellation.Dispose();
            }
        }
    }
}
