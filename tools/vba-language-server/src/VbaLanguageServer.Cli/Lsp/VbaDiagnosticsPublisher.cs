using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.ProjectModel;
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
    private readonly object enqueueGate = new();
    private readonly Dictionary<string, long> latestPublishRevisions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> terminalPublishRevisions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly LspMessageTransport transport;
    private readonly VbaLanguageWorkspace workspace;
    private readonly IVbaDiagnosticsPublicationObserver publicationObserver;
    private VbaInteractiveWorkScheduler? scheduler;
    private VbaLatestOnlyBackgroundMailbox? publicationMailbox;
    private bool diskSourceDiagnosticsAttached;

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
        var attachDiskSourceDiagnostics = false;
        lock (enqueueGate)
        {
            lock (gate)
            {
                if (scheduler is not null && !ReferenceEquals(scheduler, interactiveScheduler))
                {
                    throw new InvalidOperationException(
                        "The diagnostics publisher is already attached to another scheduler.");
                }

                if (publicationMailbox is not null)
                {
                    return;
                }

                scheduler = interactiveScheduler;
                publicationMailbox = new VbaLatestOnlyBackgroundMailbox(
                    interactiveScheduler,
                    VbaInteractiveBackgroundWorkType.DiagnosticsPublication,
                    StringComparer.OrdinalIgnoreCase,
                    CompleteTerminalRevisionState);
                diskSourceDiagnosticsAttached = true;
                attachDiskSourceDiagnostics = true;
            }
        }

        if (attachDiskSourceDiagnostics)
        {
            workspace.DiskSourceDiagnosticsChanged +=
                OnDiskSourceDiagnosticsChanged;
        }
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
            if (EnqueueDiskSourceFailure(uri, cancellationToken))
            {
                return Task.CompletedTask;
            }

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
    /// Publishes the latest document and project diagnostics for every tracked
    /// source in the project containing the active URI.
    /// </summary>
    public Task PublishProjectDiagnosticsAsync(
        string activeUri,
        CancellationToken cancellationToken)
    {
        var diskSourceFailureEnqueued = EnqueueDiskSourceFailure(
            activeUri,
            cancellationToken);
        var snapshots = workspace.GetProjectDiagnosticsSnapshots(
            activeUri,
            cancellationToken);
        if (snapshots is null)
        {
            return Task.CompletedTask;
        }

        if (snapshots.Count == 0)
        {
            return diskSourceFailureEnqueued
                ? Task.CompletedTask
                : PublishTrackedDiagnosticsAsync(activeUri, cancellationToken);
        }

        var batchOwnership = snapshots[0];
        EnqueuePublications(
            snapshots
                .Select(snapshot => new DiagnosticsPublication(
                    snapshot.Analysis.Uri,
                    () => workspace.AreLatestDiagnosticsSnapshots(
                        snapshot.ProjectOwnership,
                        snapshot.ProjectSnapshotOwnership),
                    publicationCancellationToken =>
                        PublishDiagnosticsAsync(
                            snapshot,
                            publicationCancellationToken)))
                .ToArray(),
            () => workspace.AreLatestDiagnosticsSnapshots(
                batchOwnership.ProjectOwnership,
                batchOwnership.ProjectSnapshotOwnership));

        return Task.CompletedTask;
    }

    private bool EnqueueDiskSourceFailure(
        string uri,
        CancellationToken cancellationToken)
    {
        var failure = workspace.GetDiskSourceFailure(uri, cancellationToken);
        if (failure is null)
        {
            return false;
        }

        EnqueuePublication(
            failure.Uri,
            () => workspace.IsCurrentDiskSourceFailure(failure),
            publicationCancellationToken =>
                PublishDiskSourceFailureAsync(
                    failure,
                    publicationCancellationToken));
        return true;
    }

    private Task PublishDiskSourceFailureAsync(
        VbaProjectDiskSourceFailure failure,
        CancellationToken cancellationToken)
        => transport.WriteNotificationAsync(
            "textDocument/publishDiagnostics",
            new
            {
                uri = failure.Uri,
                diagnostics = new object[]
                {
                    new
                    {
                        range = new
                        {
                            start = new { line = 0, character = 0 },
                            end = new { line = 0, character = 1 }
                        },
                        severity = 1,
                        code = "invalid-disk-source-encoding",
                        source = "vba-language-server",
                        message = failure.DiagnosticMessage
                    }
                }
            },
            cancellationToken);

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

    void IVbaProjectDiskReconciliationDiagnostics.EnqueueProjectDiagnostics(
        string uri,
        CancellationToken cancellationToken)
        => _ = PublishProjectDiagnosticsAsync(uri, cancellationToken);

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
        VbaLatestOnlyBackgroundMailbox mailbox;
        lock (gate)
        {
            mailbox = publicationMailbox
                ?? throw new InvalidOperationException(
                    "The diagnostics scheduler must be attached before publication starts.");
        }

        return mailbox.WaitForIdleAsync(uri);
    }

    /// <summary>
    /// Stops pending diagnostics before the runtime-owned scheduler stops.
    /// </summary>
    internal void Stop()
    {
        var detachDiskSourceDiagnostics = false;
        lock (enqueueGate)
        {
            VbaLatestOnlyBackgroundMailbox? mailbox;
            lock (gate)
            {
                mailbox = publicationMailbox;
                if (diskSourceDiagnosticsAttached)
                {
                    diskSourceDiagnosticsAttached = false;
                    detachDiskSourceDiagnostics = true;
                }
            }

            mailbox?.Stop();
        }

        if (detachDiskSourceDiagnostics)
        {
            workspace.DiskSourceDiagnosticsChanged -=
                OnDiskSourceDiagnosticsChanged;
        }
    }

    private void OnDiskSourceDiagnosticsChanged(string uri)
    {
        if (workspace.GetDiskSourceFailure(uri, CancellationToken.None) is null)
        {
            _ = PublishEmptyDiagnosticsAsync(
                uri,
                CancellationToken.None);
        }

        _ = PublishProjectDiagnosticsAsync(
            uri,
            CancellationToken.None);
    }

    private Task PublishDiagnosticsAsync(
        VbaDocumentDiagnosticsSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var analysis = snapshot.Analysis;
        var diagnostics = analysis.Diagnostics with
        {
            ProjectValidationDiagnostics = snapshot.ProjectValidationDiagnostics
        };
        object parameters = snapshot.ClientVersion is { } version
            ? new
            {
                uri = analysis.Uri,
                version,
                diagnostics = VbaLspFeatureProjection.CreateDiagnostics(
                    diagnostics.Diagnostics)
            }
            : new
            {
                uri = analysis.Uri,
                diagnostics = VbaLspFeatureProjection.CreateDiagnostics(
                    diagnostics.Diagnostics)
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
        => EnqueuePublications(
        [
            new DiagnosticsPublication(uri, isStillPublishable, publish)
        ]);

    private void EnqueuePublications(
        IReadOnlyList<DiagnosticsPublication> publications,
        Func<bool>? isBatchStillPublishable = null)
    {
        var reservations = new List<DiagnosticsPublicationReservation>(
            publications.Count);
        lock (enqueueGate)
        {
            if (isBatchStillPublishable is not null
                && !isBatchStillPublishable())
            {
                return;
            }

            VbaLatestOnlyBackgroundMailbox mailbox;
            lock (gate)
            {
                mailbox = publicationMailbox
                    ?? throw new InvalidOperationException(
                        "The diagnostics scheduler must be attached before publication starts.");
                foreach (var publication in publications)
                {
                    latestPublishRevisions.TryGetValue(
                        publication.Uri,
                        out var previous);
                    var revision = previous + 1;
                    latestPublishRevisions[publication.Uri] = revision;
                    reservations.Add(new DiagnosticsPublicationReservation(
                        publication,
                        revision,
                        new TaskCompletionSource(
                            TaskCreationOptions.RunContinuationsAsynchronously)));
                }
            }

            foreach (var reservation in reservations)
            {
                mailbox.Post(
                    reservation.Publication.Uri,
                    async cancellationToken =>
                    {
                        await reservation.RevisionObserved.Task.ConfigureAwait(false);
                        if (!IsLatestPublishRevision(
                                reservation.Publication.Uri,
                                reservation.Revision)
                            || !reservation.Publication.IsStillPublishable())
                        {
                            return;
                        }

                        try
                        {
                            await reservation.Publication.Publish(cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (IOException)
                        {
                        }
                        catch (ObjectDisposedException)
                        {
                        }
                    },
                    () => MarkPublishRevisionTerminal(
                        reservation.Publication.Uri,
                        reservation.Revision));
            }
        }

        try
        {
            foreach (var reservation in reservations)
            {
                publicationObserver.AfterRevisionReserved(
                    reservation.Publication.Uri,
                    reservation.Revision);
            }
        }
        finally
        {
            foreach (var reservation in reservations)
            {
                reservation.RevisionObserved.TrySetResult();
            }
        }
    }

    private sealed record DiagnosticsPublication(
        string Uri,
        Func<bool> IsStillPublishable,
        Func<CancellationToken, Task> Publish);

    private sealed record DiagnosticsPublicationReservation(
        DiagnosticsPublication Publication,
        long Revision,
        TaskCompletionSource RevisionObserved);

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
        }
    }

    private void CompleteTerminalRevisionState(string uri)
    {
        VbaLatestOnlyBackgroundMailbox? mailbox;
        lock (gate)
        {
            mailbox = publicationMailbox;
        }

        if (mailbox is null || !mailbox.IsIdle(uri))
        {
            return;
        }

        lock (gate)
        {
            if (!mailbox.IsIdle(uri))
            {
                return;
            }

            latestPublishRevisions.TryGetValue(uri, out var latestRevision);
            terminalPublishRevisions.TryGetValue(uri, out var terminalRevision);
            if (terminalRevision < latestRevision)
            {
                return;
            }

            latestPublishRevisions.Remove(uri);
            terminalPublishRevisions.Remove(uri);
        }
    }
}
