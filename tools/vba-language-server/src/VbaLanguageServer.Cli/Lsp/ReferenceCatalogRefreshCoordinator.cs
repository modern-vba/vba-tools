using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Workspace;

namespace VbaLanguageServer.Lsp;

/// <summary>
/// Publishes reference-selection trace messages and starts background catalog refresh work.
/// </summary>
internal sealed class ReferenceCatalogRefreshCoordinator
{
    private readonly VbaProjectReferenceCatalogCache referenceCatalogCache;
    private readonly VbaProjectReferenceCatalogRefreshService catalogRefreshService;
    private readonly LspMessageTransport transport;
    private readonly object diagnosticGate = new();
    private readonly HashSet<string> publishedDiagnostics = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a reference catalog refresh coordinator.
    /// </summary>
    /// <param name="referenceCatalogCache">The current reference catalog cache.</param>
    /// <param name="catalogRefreshService">The refresh service for missing catalogs.</param>
    /// <param name="transport">The transport used to publish log messages.</param>
    public ReferenceCatalogRefreshCoordinator(
        VbaProjectReferenceCatalogCache referenceCatalogCache,
        VbaProjectReferenceCatalogRefreshService catalogRefreshService,
        LspMessageTransport transport)
    {
        this.referenceCatalogCache = referenceCatalogCache;
        this.catalogRefreshService = catalogRefreshService;
        this.transport = transport;
    }

    /// <summary>
    /// Publishes trace and warning messages for the reference selection that applies to a URI.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="cancellationToken">A cancellation token for message publication.</param>
    public async Task PublishReferenceSelectionTraceAsync(string uri, CancellationToken cancellationToken)
    {
        if (!LanguageServerManifestResolution.TryCreateReferenceSelectionContext(
            uri,
            referenceCatalogCache.Current,
            out var context,
            out var error))
        {
            if (error is not null)
            {
                await transport.WriteLogMessageAsync(
                    2,
                    $"Project manifest could not be resolved for reference selection: {error.Message}",
                    cancellationToken);
            }

            return;
        }

        foreach (var message in context.Messages)
        {
            await transport.WriteLogMessageAsync(
                message.Type,
                message.Text,
                cancellationToken);
        }

        foreach (var reference in context.ReferenceSelection?.References ?? [])
        {
            var source = referenceCatalogCache.GetCatalogSource(reference.Name);
            if (source == VbaProjectReferenceCatalogSource.Unavailable)
            {
                continue;
            }

            await WriteLogMessageOnceAsync(
                ReferenceCatalogRefreshOutcome.CreateAvailabilityMessage(
                    context.Resolution.DocumentName,
                    reference.Name,
                    source),
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
        if (!LanguageServerManifestResolution.TryCreateReferenceSelections(uri, text, out var selections))
        {
            return;
        }

        foreach (var selectionContext in selections)
        {
            foreach (var result in catalogRefreshService.PreloadPersistedCatalogs(selectionContext.Selection))
            {
                await PublishCatalogRefreshResultAsync(
                    uri,
                    selectionContext.DocumentName,
                    result,
                    cancellationToken);
            }
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
        if (LanguageServerManifestResolution.TryCreateReferenceSelections(uri, text, out var selections))
        {
            _ = RefreshReferenceCatalogsInBackgroundAsync(uri, selections, cancellationToken);
        }
    }

    private async Task RefreshReferenceCatalogsInBackgroundAsync(
        string uri,
        IReadOnlyList<VbaProjectReferenceSelectionContext> selections,
        CancellationToken cancellationToken)
    {
        foreach (var selectionContext in selections)
        {
            IReadOnlyList<VbaProjectReferenceCatalogRefreshResult> results;
            try
            {
                results = await catalogRefreshService.RefreshAsync(selectionContext.Selection, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            foreach (var result in results)
            {
                await PublishCatalogRefreshResultAsync(
                    uri,
                    selectionContext.DocumentName,
                    result,
                    cancellationToken);
            }
        }
    }

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
