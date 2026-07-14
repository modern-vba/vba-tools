using VbaLanguageServer.SourceModel;

namespace VbaLanguageServer.Lsp;

/// <summary>
/// Describes reference-catalog work caused by one document change.
/// </summary>
/// <param name="Uri">The changed document URI.</param>
/// <param name="Text">The changed document text.</param>
/// <param name="PreloadPersistedCatalogs">Whether persisted catalogs should be loaded before feature requests continue.</param>
/// <param name="PublishReferenceTrace">Whether reference-selection trace should be published after diagnostics.</param>
/// <param name="RefreshInBackground">Whether background type-library discovery should be started.</param>
internal sealed record ReferenceCatalogDocumentChange(
    string Uri,
    string Text,
    bool PreloadPersistedCatalogs,
    bool PublishReferenceTrace,
    bool RefreshInBackground);

/// <summary>
/// Publishes reference-selection trace messages and starts background catalog refresh work.
/// </summary>
internal sealed class ReferenceCatalogRefreshCoordinator
{
    private readonly ReferenceCatalogRefreshSession refreshSession;
    private readonly LspMessageTransport transport;
    private readonly object diagnosticGate = new();
    private readonly HashSet<string> publishedDiagnostics = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a reference catalog refresh coordinator.
    /// </summary>
    /// <param name="catalogAvailability">The current catalog availability module.</param>
    /// <param name="transport">The transport used to publish log messages.</param>
    public ReferenceCatalogRefreshCoordinator(
        VbaProjectReferenceCatalogAvailability catalogAvailability,
        LspMessageTransport transport)
    {
        refreshSession = new ReferenceCatalogRefreshSession(catalogAvailability);
        this.transport = transport;
    }

    /// <summary>
    /// Applies reference-catalog work for a document change in the required order.
    /// </summary>
    /// <param name="change">The catalog-affecting document change.</param>
    /// <param name="publishDiagnosticsAsync">Diagnostics work that must run after preload and before trace.</param>
    /// <param name="cancellationToken">A cancellation token for foreground work.</param>
    public async Task ApplyDocumentChangeAsync(
        ReferenceCatalogDocumentChange change,
        Func<CancellationToken, Task> publishDiagnosticsAsync,
        CancellationToken cancellationToken)
    {
        if (change.PreloadPersistedCatalogs)
        {
            await PreloadReferenceCatalogsAsync(change.Uri, change.Text, cancellationToken);
        }

        await publishDiagnosticsAsync(cancellationToken);

        if (change.PublishReferenceTrace)
        {
            await PublishReferenceSelectionTraceAsync(change.Uri, cancellationToken);
        }

        if (change.RefreshInBackground)
        {
            RefreshReferenceCatalogsInBackground(change.Uri, change.Text, cancellationToken);
        }
    }

    /// <summary>
    /// Applies reference-catalog work caused by a project manifest change.
    /// </summary>
    /// <param name="uri">The manifest URI.</param>
    /// <param name="text">The manifest text.</param>
    /// <param name="documentUris">The tracked source document URIs that need refreshed trace.</param>
    /// <param name="cancellationToken">A cancellation token for foreground work.</param>
    public async Task ApplyProjectManifestChangeAsync(
        string uri,
        string text,
        IReadOnlyList<string> documentUris,
        CancellationToken cancellationToken)
    {
        RefreshReferenceCatalogsInBackground(uri, text, cancellationToken);
        foreach (var documentUri in documentUris)
        {
            await PublishReferenceSelectionTraceAsync(documentUri, cancellationToken);
        }
    }

    /// <summary>
    /// Publishes trace and warning messages for the reference selection that applies to a URI.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="cancellationToken">A cancellation token for message publication.</param>
    public async Task PublishReferenceSelectionTraceAsync(string uri, CancellationToken cancellationToken)
    {
        foreach (var sessionMessage in refreshSession.CreateReferenceSelectionTraceMessages(uri))
        {
            if (sessionMessage.PublishOnce)
            {
                await WriteLogMessageOnceAsync(sessionMessage.Message, cancellationToken);
                continue;
            }

            await transport.WriteLogMessageAsync(
                sessionMessage.Message.Type,
                sessionMessage.Message.Text,
                cancellationToken);
        }
    }

    /// <summary>
    /// Loads persisted catalogs for selections affected by a document before editor requests continue.
    /// </summary>
    /// <param name="uri">The changed document URI.</param>
    /// <param name="text">The changed document text.</param>
    /// <param name="cancellationToken">A cancellation token for preload work.</param>
    public async Task PreloadReferenceCatalogsAsync(string uri, string text, CancellationToken cancellationToken)
    {
        foreach (var refreshEvent in refreshSession.PreloadReferenceCatalogs(uri, text))
        {
            await PublishCatalogRefreshResultAsync(refreshEvent, cancellationToken);
        }
    }

    /// <summary>
    /// Starts background catalog refresh for reference selections affected by a document text change.
    /// </summary>
    /// <param name="uri">The changed document URI.</param>
    /// <param name="text">The changed document text.</param>
    /// <param name="cancellationToken">A cancellation token for refresh work.</param>
    public void RefreshReferenceCatalogsInBackground(string uri, string text, CancellationToken cancellationToken)
    {
        if (refreshSession.TryCreateBackgroundRefreshPlan(uri, text, out var plan))
        {
            _ = RefreshReferenceCatalogsInBackgroundAsync(plan, cancellationToken);
        }
    }

    private async Task RefreshReferenceCatalogsInBackgroundAsync(
        ReferenceCatalogRefreshPlan plan,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ReferenceCatalogRefreshEvent> refreshEvents;
        try
        {
            refreshEvents = await refreshSession.RefreshAsync(plan, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        foreach (var refreshEvent in refreshEvents)
        {
            await PublishCatalogRefreshResultAsync(refreshEvent, cancellationToken);
        }
    }

    private Task PublishCatalogRefreshResultAsync(
        ReferenceCatalogRefreshEvent refreshEvent,
        CancellationToken cancellationToken)
        => PublishCatalogRefreshResultAsync(
            refreshEvent.Uri,
            refreshEvent.DocumentName,
            refreshEvent.Result,
            cancellationToken);

    private async Task PublishCatalogRefreshResultAsync(
        string uri,
        string documentName,
        VbaProjectReferenceCatalogRefreshResult result,
        CancellationToken cancellationToken)
    {
        await PublishCatalogRefreshDiagnosticAsync(documentName, result, cancellationToken);

        foreach (var message in ReferenceCatalogRefreshOutcome.CreateDiscoveryMessages(documentName, result))
        {
            await transport.WriteLogMessageAsync(
                message.Type,
                message.Text,
                cancellationToken);
        }
    }

    private async Task PublishCatalogRefreshDiagnosticAsync(
        string documentName,
        VbaProjectReferenceCatalogRefreshResult result,
        CancellationToken cancellationToken)
    {
        await WriteLogMessageOnceAsync(
            ReferenceCatalogRefreshOutcome.CreateDiagnosticMessage(documentName, result),
            cancellationToken);
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

        await transport.WriteLogMessageAsync(type, message, cancellationToken);
    }

}
