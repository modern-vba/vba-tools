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
    private readonly object enqueueGate = new();
    private readonly Dictionary<string, long> latestPublishRevisions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> terminalPublishRevisions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly LspMessageTransport transport;
    private readonly VbaLanguageWorkspace workspace;
    private readonly IVbaDiagnosticsPublicationObserver publicationObserver;
    private VbaInteractiveWorkScheduler? scheduler;
    private VbaLatestOnlyBackgroundMailbox? publicationMailbox;

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
            }
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
        lock (enqueueGate)
        {
            VbaLatestOnlyBackgroundMailbox? mailbox;
            lock (gate)
            {
                mailbox = publicationMailbox;
            }

            mailbox?.Stop();
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
        var revisionObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        long revision;
        lock (enqueueGate)
        {
            VbaLatestOnlyBackgroundMailbox mailbox;
            lock (gate)
            {
                mailbox = publicationMailbox
                    ?? throw new InvalidOperationException(
                        "The diagnostics scheduler must be attached before publication starts.");
                latestPublishRevisions.TryGetValue(uri, out var previous);
                revision = previous + 1;
                latestPublishRevisions[uri] = revision;
            }

            mailbox.Post(
                uri,
                async cancellationToken =>
                {
                    await revisionObserved.Task.ConfigureAwait(false);
                    if (!IsLatestPublishRevision(uri, revision)
                        || !isStillPublishable())
                    {
                        return;
                    }

                    try
                    {
                        await publish(cancellationToken).ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                },
                () => MarkPublishRevisionTerminal(uri, revision));
        }

        try
        {
            publicationObserver.AfterRevisionReserved(uri, revision);
        }
        finally
        {
            revisionObserved.TrySetResult();
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
