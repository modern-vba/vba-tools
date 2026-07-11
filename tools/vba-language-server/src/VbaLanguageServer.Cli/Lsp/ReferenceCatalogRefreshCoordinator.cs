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
        var discovery = result.DiscoveryResult;
        if (discovery.IsFailure)
        {
            await transport.WriteLogMessageAsync(
                2,
                $"Reference catalog discovery warning: document '{documentName}' reference '{result.ReferenceName}' could not be discovered for {uri}: {discovery.ErrorMessage}",
                cancellationToken);
            return;
        }

        if (discovery.IsAmbiguous)
        {
            await transport.WriteLogMessageAsync(
                2,
                $"Reference catalog discovery warning: document '{documentName}' reference '{result.ReferenceName}' is ambiguous across {discovery.Identities.Count} TypeLib candidates; no catalog was cached.",
                cancellationToken);
            return;
        }

        var identity = discovery.Identities.SingleOrDefault();
        if (identity is not null)
        {
            await transport.WriteLogMessageAsync(
                3,
                $"Reference catalog discovery: document '{documentName}' reference '{result.ReferenceName}' resolved to TypeLib {identity.Guid} {identity.MajorVersion}.{identity.MinorVersion} LCID {identity.Lcid} at {identity.Path}.",
                cancellationToken);
        }

        if (discovery.HasUsableCatalog)
        {
            await transport.WriteLogMessageAsync(
                3,
                $"Reference catalog refresh: document '{documentName}' reference '{result.ReferenceName}' cached {discovery.Catalog!.Definitions.Count} external definitions.",
                cancellationToken);
        }
    }
}
