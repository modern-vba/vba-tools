using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;

namespace VbaLanguageServer.Workspace;

/// <summary>
/// Assembles immutable project snapshots from workspace state, disk inventory, and reference catalogs.
/// </summary>
internal sealed class VbaProjectSnapshotBuilder
{
    private readonly VbaProjectSourceDocumentCache diskDocumentCache;

    public VbaProjectSnapshotBuilder(VbaProjectSourceDocumentCache diskDocumentCache)
    {
        this.diskDocumentCache = diskDocumentCache;
    }

    public VbaProjectSourceInventorySnapshot CreateInventorySnapshot(
        string activeUri,
        VbaProjectResolution resolution,
        IReadOnlyDictionary<string, VbaTrackedDocument> workspaceDocuments,
        IReadOnlySet<string> excludedSourceUris,
        CancellationToken cancellationToken)
    {
        var inventorySnapshot = VbaProjectSourceInventory.CreateInventorySnapshot(
            resolution,
            workspaceDocuments,
            excludedSourceUris,
            diskDocumentCache,
            cancellationToken);
        if (!inventorySnapshot.Documents.ContainsKey(activeUri)
            && workspaceDocuments.TryGetValue(activeUri, out var activeDocument))
        {
            inventorySnapshot.Documents[activeUri] = activeDocument;
        }

        return inventorySnapshot;
    }

    public VbaProjectSnapshot BuildSnapshot(
        VbaProjectResolution resolution,
        IReadOnlyDictionary<string, VbaTrackedDocument> scopedTrackedDocuments,
        VbaProjectReferenceCatalogSet referenceCatalogs)
    {
        var scopedDocuments = scopedTrackedDocuments
            .ToDictionary(pair => pair.Key, pair => pair.Value.Text, StringComparer.OrdinalIgnoreCase);
        var scopedSourceDocuments = scopedTrackedDocuments
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value.SourceDocument ?? VbaSourceIndex.CreateDocument(pair.Value.Uri, pair.Value.SyntaxTree),
                StringComparer.OrdinalIgnoreCase);
        var manifestContext = LanguageServerManifestResolution.Create(
            resolution,
            referenceCatalogs);
        var sourceIndex = VbaSourceIndex.BuildFromSourceDocuments(
            scopedSourceDocuments,
            manifestContext.ReferenceSelection,
            referenceCatalogs);
        var semanticInventory = VbaSemanticInventory.Create(
            sourceIndex,
            scopedSourceDocuments);

        return new VbaProjectSnapshot(
            resolution,
            scopedDocuments,
            manifestContext.ReferenceSelection,
            sourceIndex,
            semanticInventory);
    }
}
