using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.Workspace;

namespace VbaLanguageServer.Lsp;

internal interface IVbaDiagnosticsPublicationObserver
{
    void AfterRevisionReserved(string uri, long revision);
}

internal sealed class NullVbaDiagnosticsPublicationObserver
    : IVbaDiagnosticsPublicationObserver
{
    public static NullVbaDiagnosticsPublicationObserver Instance { get; } = new();

    private NullVbaDiagnosticsPublicationObserver()
    {
    }

    public void AfterRevisionReserved(string uri, long revision)
    {
    }
}

/// <summary>
/// Publishes document diagnostics to the LSP transport.
/// </summary>
internal sealed class VbaDiagnosticsPublisher
    : IVbaProjectDiskReconciliationDiagnostics
{
    private readonly object gate = new();
    private readonly Dictionary<string, long> latestPublishRevisions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> terminalPublishRevisions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingDiagnosticsPublication> pendingPublications =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> activePublicationWorkers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<TaskCompletionSource>>
        idleWaiters = new(StringComparer.OrdinalIgnoreCase);
    private readonly LspMessageTransport transport;
    private readonly VbaLanguageWorkspace workspace;
    private readonly IVbaDiagnosticsPublicationObserver publicationObserver;
    private VbaInteractiveWorkScheduler? scheduler;

    /// <summary>
    /// Creates a diagnostics publisher.
    /// </summary>
    /// <param name="transport">The transport used to publish diagnostics.</param>
    /// <param name="workspace">The workspace that owns parsed syntax trees.</param>
    public VbaDiagnosticsPublisher(
        LspMessageTransport transport,
        VbaLanguageWorkspace workspace,
        IVbaDiagnosticsPublicationObserver? publicationObserver = null)
    {
        this.transport = transport;
        this.workspace = workspace;
        this.publicationObserver = publicationObserver
            ?? NullVbaDiagnosticsPublicationObserver.Instance;
    }

    internal int RetainedRevisionStateCount
    {
        get
        {
            lock (gate)
            {
                return latestPublishRevisions.Keys
                    .Concat(terminalPublishRevisions.Keys)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
            }
        }
    }

    /// <summary>
    /// Attaches the runtime-owned bounded scheduler before document work is admitted.
    /// </summary>
    public void AttachScheduler(VbaInteractiveWorkScheduler interactiveScheduler)
    {
        ArgumentNullException.ThrowIfNull(interactiveScheduler);
        lock (gate)
        {
            if (scheduler is not null && !ReferenceEquals(scheduler, interactiveScheduler))
            {
                throw new InvalidOperationException(
                    "The diagnostics publisher is already attached to another scheduler.");
            }

            if (activePublicationWorkers.Count > 0)
            {
                throw new InvalidOperationException(
                    "The diagnostics scheduler must be attached before publication starts.");
            }

            scheduler = interactiveScheduler;
        }

        interactiveScheduler.RegisterCapacityObserver(
            RetryOneScheduledPublication);
    }

    /// <summary>
    /// Publishes diagnostics for a tracked document, or clears diagnostics when it is no longer parsed.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="cancellationToken">A cancellation token for transport work.</param>
    public Task PublishTrackedDiagnosticsAsync(string uri, CancellationToken cancellationToken)
    {
        var snapshot = workspace.GetDocumentDiagnosticsSnapshot(uri, cancellationToken);
        if (snapshot is null)
        {
            return PublishEmptyDiagnosticsAsync(uri, cancellationToken);
        }

        EnqueuePublication(
            uri,
            () => workspace.IsLatestDiagnosticsSnapshot(
                snapshot.Analysis.Uri,
                snapshot.ClientVersion,
                snapshot.LifecycleEpoch,
                snapshot.ReservationToken),
            cancellationToken => PublishDiagnosticsAsync(snapshot, cancellationToken));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Clears diagnostics for a document.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="cancellationToken">A cancellation token for transport work.</param>
    public Task PublishEmptyDiagnosticsAsync(string uri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnqueuePublication(
            uri,
            () => true,
            publicationCancellationToken => transport.WriteNotificationAsync(
                "textDocument/publishDiagnostics",
                new
                {
                    uri,
                    diagnostics = Array.Empty<object>()
                },
                publicationCancellationToken));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Publishes or clears the validation diagnostic for a project manifest.
    /// </summary>
    /// <param name="uri">The project manifest URI.</param>
    /// <param name="error">The current validation error, or null to clear it.</param>
    /// <param name="cancellationToken">A cancellation token for transport work.</param>
    public Task PublishManifestValidationDiagnosticAsync(
        string uri,
        VbaProjectManifestException? error,
        CancellationToken cancellationToken)
    {
        object[] diagnostics = error is null
            ? []
            :
            [
                new
                {
                    range = new
                    {
                        start = new { line = 0, character = 0 },
                        end = new { line = 0, character = 1 }
                    },
                    severity = 1,
                    code = "invalid-project-manifest",
                    source = "vba-language-server",
                    message = error.Message
                }
            ];
        cancellationToken.ThrowIfCancellationRequested();
        EnqueuePublication(
            uri,
            () => true,
            publicationCancellationToken => transport.WriteNotificationAsync(
                "textDocument/publishDiagnostics",
                new
                {
                    uri,
                    diagnostics
                },
                publicationCancellationToken));
        return Task.CompletedTask;
    }

    void IVbaProjectDiskReconciliationDiagnostics.EnqueueTrackedDiagnostics(
        string uri,
        CancellationToken cancellationToken)
        => _ = PublishTrackedDiagnosticsAsync(uri, cancellationToken);

    void IVbaProjectDiskReconciliationDiagnostics.EnqueueEmptyDiagnostics(
        string uri,
        CancellationToken cancellationToken)
        => _ = PublishEmptyDiagnosticsAsync(uri, cancellationToken);

    /// <summary>
    /// Waits until the latest diagnostics revision for one URI is terminal and
    /// no publication for that URI is pending or active.
    /// </summary>
    internal Task WaitForIdleAsync(string uri)
    {
        lock (gate)
        {
            if (IsTerminalIdleLocked(uri))
            {
                return Task.CompletedTask;
            }

            var waiter = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!idleWaiters.TryGetValue(uri, out var waiters))
            {
                waiters = [];
                idleWaiters.Add(uri, waiters);
            }

            waiters.Add(waiter);
            return waiter.Task;
        }
    }

    private Task PublishDiagnosticsAsync(
        VbaDocumentDiagnosticsSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var analysis = snapshot.Analysis;
        object parameters = snapshot.ClientVersion is { } version
            ? new
            {
                uri = analysis.Uri,
                version,
                diagnostics = VbaLspFeatureProjection.CreateDiagnostics(
                    analysis.Diagnostics.Diagnostics)
            }
            : new
            {
                uri = analysis.Uri,
                diagnostics = VbaLspFeatureProjection.CreateDiagnostics(
                    analysis.Diagnostics.Diagnostics)
            };
        return transport.WriteNotificationAsync(
            "textDocument/publishDiagnostics",
            parameters,
            cancellationToken);
    }

    private bool IsLatestPublishRevision(string uri, long revision)
    {
        lock (gate)
        {
            return latestPublishRevisions.TryGetValue(uri, out var latest)
                && latest == revision;
        }
    }

    private void EnqueuePublication(
        string uri,
        Func<bool> isStillPublishable,
        Func<CancellationToken, Task> publish)
    {
        VbaInteractiveWorkScheduler? interactiveScheduler;
        long revision;
        var startWorker = false;
        lock (gate)
        {
            latestPublishRevisions.TryGetValue(uri, out var previous);
            revision = previous + 1;
            latestPublishRevisions[uri] = revision;
            interactiveScheduler = scheduler;
            pendingPublications[uri] = new PendingDiagnosticsPublication(
                revision,
                isStillPublishable,
                publish);
            startWorker = activePublicationWorkers.Add(uri);
        }

        try
        {
            publicationObserver.AfterRevisionReserved(uri, revision);
        }
        finally
        {
            DispatchPublicationWorker(
                uri,
                interactiveScheduler,
                startWorker);
        }
    }

    private void DispatchPublicationWorker(
        string uri,
        VbaInteractiveWorkScheduler? interactiveScheduler,
        bool startWorker)
    {
        if (interactiveScheduler is not null)
        {
            if (startWorker)
            {
                StartScheduledPublication(uri, interactiveScheduler);
            }

            return;
        }

        if (startWorker)
        {
            _ = ProcessPublicationsAsync(uri);
        }
    }

    private void RetryOneScheduledPublication()
    {
        VbaInteractiveWorkScheduler? interactiveScheduler;
        string? uri;
        lock (gate)
        {
            interactiveScheduler = scheduler;
            uri = interactiveScheduler is not null && interactiveScheduler.IsAccepting
                ? pendingPublications.Keys.FirstOrDefault(
                    pendingUri => !activePublicationWorkers.Contains(pendingUri))
                : null;
            if (uri is not null)
            {
                activePublicationWorkers.Add(uri);
            }
        }

        if (interactiveScheduler is not null && uri is not null)
        {
            StartScheduledPublication(uri, interactiveScheduler);
        }
    }

    private void StartScheduledPublication(
        string uri,
        VbaInteractiveWorkScheduler interactiveScheduler)
    {
        if (!interactiveScheduler.TryAdmitBackground(
                VbaInteractiveBackgroundWorkType.DiagnosticsPublication,
                uri,
                cancellationToken => PublishOneScheduledPublicationAsync(
                    uri,
                    cancellationToken),
                out var admission))
        {
            lock (gate)
            {
                activePublicationWorkers.Remove(uri);
                CompleteIdleWaitersLocked(uri);
            }

            interactiveScheduler.RequestCapacityPump();
            return;
        }

        _ = ObserveScheduledPublicationAsync(
            uri,
            interactiveScheduler,
            admission.Completion);
    }

    private async Task PublishOneScheduledPublicationAsync(
        string uri,
        CancellationToken cancellationToken)
    {
        PendingDiagnosticsPublication? publication;
        lock (gate)
        {
            pendingPublications.Remove(uri, out publication);
        }

        if (publication is null)
        {
            return;
        }

        try
        {
            if (!IsLatestPublishRevision(uri, publication.Revision)
                || !publication.IsStillPublishable())
            {
                return;
            }

            await publication.Publish(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            MarkPublishRevisionTerminal(uri, publication.Revision);
        }
    }

    private async Task ObserveScheduledPublicationAsync(
        string uri,
        VbaInteractiveWorkScheduler interactiveScheduler,
        Task completion)
    {
        try
        {
            await completion.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        var restart = false;
        lock (gate)
        {
            activePublicationWorkers.Remove(uri);
            restart = interactiveScheduler.IsAccepting
                && pendingPublications.ContainsKey(uri)
                && activePublicationWorkers.Add(uri);
            CompleteIdleWaitersLocked(uri);
        }

        if (restart)
        {
            StartScheduledPublication(uri, interactiveScheduler);
        }
    }

    private async Task ProcessPublicationsAsync(string uri)
    {
        var restart = false;
        try
        {
            while (true)
            {
                await Task.Yield();
                PendingDiagnosticsPublication publication;
                lock (gate)
                {
                    if (!pendingPublications.Remove(uri, out publication!))
                    {
                        return;
                    }
                }

                try
                {
                    if (!IsLatestPublishRevision(uri, publication.Revision)
                        || !publication.IsStillPublishable())
                    {
                        continue;
                    }

                    await publication.Publish(CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    MarkPublishRevisionTerminal(uri, publication.Revision);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            lock (gate)
            {
                activePublicationWorkers.Remove(uri);
                restart = scheduler is null
                    && pendingPublications.ContainsKey(uri)
                    && activePublicationWorkers.Add(uri);
                CompleteIdleWaitersLocked(uri);
            }

            if (restart)
            {
                _ = ProcessPublicationsAsync(uri);
            }
        }
    }

    private void MarkPublishRevisionTerminal(
        string uri,
        long revision)
    {
        lock (gate)
        {
            terminalPublishRevisions.TryGetValue(
                uri,
                out var previousTerminalRevision);
            if (revision > previousTerminalRevision)
            {
                terminalPublishRevisions[uri] = revision;
            }

            CompleteIdleWaitersLocked(uri);
        }
    }

    private bool IsTerminalIdleLocked(string uri)
    {
        latestPublishRevisions.TryGetValue(uri, out var latestRevision);
        terminalPublishRevisions.TryGetValue(uri, out var terminalRevision);
        return !pendingPublications.ContainsKey(uri)
            && !activePublicationWorkers.Contains(uri)
            && terminalRevision >= latestRevision;
    }

    private void CompleteIdleWaitersLocked(string uri)
    {
        if (!IsTerminalIdleLocked(uri))
        {
            return;
        }

        idleWaiters.Remove(uri, out var waiters);
        latestPublishRevisions.Remove(uri);
        terminalPublishRevisions.Remove(uri);
        if (waiters is null)
        {
            return;
        }

        foreach (var waiter in waiters)
        {
            waiter.TrySetResult();
        }
    }

    private sealed record PendingDiagnosticsPublication(
        long Revision,
        Func<bool> IsStillPublishable,
        Func<CancellationToken, Task> Publish);
}
