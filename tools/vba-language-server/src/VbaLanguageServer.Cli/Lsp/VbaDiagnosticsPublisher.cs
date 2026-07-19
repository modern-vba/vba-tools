using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.Workspace;

namespace VbaLanguageServer.Lsp;

/// <summary>
/// Publishes document diagnostics to the LSP transport.
/// </summary>
internal sealed class VbaDiagnosticsPublisher
{
    private readonly object gate = new();
    private readonly Dictionary<string, long> latestPublishRevisions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingDiagnosticsPublication> pendingPublications =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> activePublicationWorkers = new(StringComparer.OrdinalIgnoreCase);
    private readonly LspMessageTransport transport;
    private readonly VbaLanguageWorkspace workspace;
    private VbaInteractiveWorkScheduler? scheduler;

    /// <summary>
    /// Creates a diagnostics publisher.
    /// </summary>
    /// <param name="transport">The transport used to publish diagnostics.</param>
    /// <param name="workspace">The workspace that owns parsed syntax trees.</param>
    public VbaDiagnosticsPublisher(LspMessageTransport transport, VbaLanguageWorkspace workspace)
    {
        this.transport = transport;
        this.workspace = workspace;
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

        var revision = ReservePublishRevision(uri);
        EnqueuePublication(
            uri,
            revision,
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
        var revision = ReservePublishRevision(uri);
        EnqueuePublication(
            uri,
            revision,
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
        var revision = ReservePublishRevision(uri);
        EnqueuePublication(
            uri,
            revision,
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

    private long ReservePublishRevision(string uri)
    {
        lock (gate)
        {
            latestPublishRevisions.TryGetValue(uri, out var previous);
            var next = previous + 1;
            latestPublishRevisions[uri] = next;
            return next;
        }
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
        long revision,
        Func<bool> isStillPublishable,
        Func<CancellationToken, Task> publish)
    {
        VbaInteractiveWorkScheduler? interactiveScheduler;
        var startWorker = false;
        lock (gate)
        {
            interactiveScheduler = scheduler;
            pendingPublications[uri] = new PendingDiagnosticsPublication(
                revision,
                isStillPublishable,
                publish);
            startWorker = activePublicationWorkers.Add(uri);
        }

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

        if (publication is null
            || !IsLatestPublishRevision(uri, publication.Revision)
            || !publication.IsStillPublishable())
        {
            return;
        }

        await publication.Publish(cancellationToken).ConfigureAwait(false);
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
        }

        if (restart)
        {
            StartScheduledPublication(uri, interactiveScheduler);
        }
    }

    private async Task ProcessPublicationsAsync(string uri)
    {
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
                        activePublicationWorkers.Remove(uri);
                        return;
                    }
                }

                if (!IsLatestPublishRevision(uri, publication.Revision)
                    || !publication.IsStillPublishable())
                {
                    continue;
                }

                await publication.Publish(CancellationToken.None).ConfigureAwait(false);
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
            }
        }
    }

    private sealed record PendingDiagnosticsPublication(
        long Revision,
        Func<bool> IsStillPublishable,
        Func<CancellationToken, Task> Publish);
}
