using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Workspace;

namespace VbaLanguageServer.Lsp;

internal sealed class ReferenceCatalogRefreshCoordinator
{
    private readonly VbaProjectReferenceCatalogCache referenceCatalogCache;
    private readonly VbaProjectReferenceCatalogRefreshService catalogRefreshService;
    private readonly LspMessageTransport transport;

    public ReferenceCatalogRefreshCoordinator(
        VbaProjectReferenceCatalogCache referenceCatalogCache,
        VbaProjectReferenceCatalogRefreshService catalogRefreshService,
        LspMessageTransport transport)
    {
        this.referenceCatalogCache = referenceCatalogCache;
        this.catalogRefreshService = catalogRefreshService;
        this.transport = transport;
    }

    public async Task PublishReferenceSelectionTraceAsync(string uri, CancellationToken cancellationToken)
    {
        VbaProjectResolution resolution;
        try
        {
            resolution = VbaProjectResolver.Resolve(uri);
        }
        catch (ProjectManifestException ex)
        {
            await transport.WriteLogMessageAsync(
                2,
                $"Project manifest could not be resolved for reference selection: {ex.Message}",
                cancellationToken);
            return;
        }

        if (resolution.Kind != VbaProjectResolutionKind.ManifestDocument
            || string.IsNullOrEmpty(resolution.DocumentName)
            || string.IsNullOrEmpty(resolution.DocumentKind))
        {
            return;
        }

        var selection = VbaProjectReferenceSelection.Create(
            resolution.DocumentKind,
            resolution.ReferenceEntries);
        var references = selection.References.Count == 0
            ? "<none>"
            : string.Join(", ", selection.References.Select(reference => reference.Name));
        await transport.WriteLogMessageAsync(
            3,
            $"VbaProjectReferenceSelection document={resolution.DocumentName} references={references} main={selection.MainVbaProjectReference?.Name ?? "<none>"}",
            cancellationToken);

        if (selection.MissingExpectedMainReference is not null)
        {
            await transport.WriteLogMessageAsync(
                2,
                $"Manifest/reference consistency warning: document '{resolution.DocumentName}' kind '{resolution.DocumentKind}' is missing expected main reference '{selection.MissingExpectedMainReference}'. Host definitions will not be activated implicitly.",
                cancellationToken);
        }

        foreach (var referenceName in referenceCatalogCache.Current.GetMissingCatalogReferenceNames(selection))
        {
            await transport.WriteLogMessageAsync(
                2,
                $"Reference catalog availability warning: document '{resolution.DocumentName}' reference '{referenceName}' has no bundled or cached VbaProjectReferenceCatalog metadata. The reference remains active, but external definitions are unavailable.",
                cancellationToken);
        }
    }

    public void RefreshReferenceCatalogsInBackground(string uri, string text, CancellationToken cancellationToken)
    {
        if (VbaLanguageWorkspace.TryCreateReferenceSelections(uri, text, out var selections))
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
