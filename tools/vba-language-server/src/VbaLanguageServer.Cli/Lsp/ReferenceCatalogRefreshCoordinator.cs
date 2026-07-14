using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Workspace;

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

internal sealed record ReferenceCatalogRefreshEvent(
    string Uri,
    string DocumentName,
    VbaProjectReferenceCatalogRefreshResult Result);

internal sealed record ReferenceCatalogRefreshSessionMessage(
    ReferenceCatalogRefreshLogMessage Message,
    bool PublishOnce);

internal sealed record ReferenceCatalogRefreshPlan(
    string Uri,
    IReadOnlyList<VbaProjectReferenceSelectionContext> Selections);

/// <summary>
/// Publishes reference-selection trace messages and starts background catalog refresh work.
/// </summary>
internal sealed class ReferenceCatalogRefreshCoordinator
{
    private readonly VbaProjectReferenceCatalogCache catalogCache;
    private readonly VbaProjectReferenceCatalogRefreshService refreshService;
    private readonly LspMessageTransport transport;
    private readonly object diagnosticGate = new();
    private readonly HashSet<string> publishedDiagnostics = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a reference catalog refresh coordinator.
    /// </summary>
    /// <param name="catalogCache">The current reference catalog state.</param>
    /// <param name="refreshService">The service that preloads and refreshes reference catalogs.</param>
    /// <param name="transport">The transport used to publish log messages.</param>
    public ReferenceCatalogRefreshCoordinator(
        VbaProjectReferenceCatalogCache catalogCache,
        VbaProjectReferenceCatalogRefreshService refreshService,
        LspMessageTransport transport)
    {
        this.catalogCache = catalogCache;
        this.refreshService = refreshService;
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
        foreach (var sessionMessage in CreateReferenceSelectionTraceMessages(uri))
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
        foreach (var refreshEvent in PreloadReferenceCatalogs(uri, text))
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
        if (TryCreateBackgroundRefreshPlan(uri, text, out var plan))
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
            refreshEvents = await RefreshAsync(plan, cancellationToken);
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

    private IReadOnlyList<ReferenceCatalogRefreshSessionMessage> CreateReferenceSelectionTraceMessages(string uri)
    {
        if (!LanguageServerManifestResolution.TryCreateReferenceSelectionContext(
            uri,
            catalogCache.Current,
            out var context,
            out var error))
        {
            return error is null
                ? []
                : [CreateDirectMessage(
                    2,
                    $"Project manifest could not be resolved for reference selection: {error.Message}",
                    $"manifest-error\u001f{uri}\u001f{error.Message}")];
        }

        var messages = new List<ReferenceCatalogRefreshSessionMessage>();
        messages.AddRange(context.Messages.Select(message => CreateDirectMessage(
            message.Type,
            message.Text,
            $"selection\u001f{uri}\u001f{message.Type}\u001f{message.Text}")));

        foreach (var reference in context.ReferenceSelection?.References ?? [])
        {
            var source = catalogCache.GetCatalogSource(reference.Name);
            if (source == VbaProjectReferenceCatalogSource.Unavailable)
            {
                continue;
            }

            messages.Add(new ReferenceCatalogRefreshSessionMessage(
                ReferenceCatalogRefreshOutcome.CreateAvailabilityMessage(
                    context.Resolution.DocumentName,
                    reference.Name,
                    source),
                PublishOnce: true));
        }

        return messages;
    }

    private IReadOnlyList<ReferenceCatalogRefreshEvent> PreloadReferenceCatalogs(string uri, string text)
    {
        if (!LanguageServerManifestResolution.TryCreateReferenceSelections(uri, text, out var selections))
        {
            return [];
        }

        return selections
            .SelectMany(selectionContext => refreshService
                .PreloadPersistedCatalogs(selectionContext.Selection)
                .Select(result => new ReferenceCatalogRefreshEvent(
                    uri,
                    selectionContext.DocumentName,
                    result)))
            .ToArray();
    }

    private static bool TryCreateBackgroundRefreshPlan(
        string uri,
        string text,
        out ReferenceCatalogRefreshPlan plan)
    {
        plan = default!;
        if (!LanguageServerManifestResolution.TryCreateReferenceSelections(uri, text, out var selections))
        {
            return false;
        }

        plan = new ReferenceCatalogRefreshPlan(uri, selections);
        return true;
    }

    private async Task<IReadOnlyList<ReferenceCatalogRefreshEvent>> RefreshAsync(
        ReferenceCatalogRefreshPlan plan,
        CancellationToken cancellationToken)
    {
        var events = new List<ReferenceCatalogRefreshEvent>();
        foreach (var selectionContext in plan.Selections)
        {
            var results = await refreshService.RefreshAsync(selectionContext.Selection, cancellationToken);
            events.AddRange(results.Select(result => new ReferenceCatalogRefreshEvent(
                plan.Uri,
                selectionContext.DocumentName,
                result)));
        }

        return events;
    }

    private static ReferenceCatalogRefreshSessionMessage CreateDirectMessage(
        int type,
        string text,
        string key)
        => new(new ReferenceCatalogRefreshLogMessage(type, text, key), PublishOnce: false);

}
