using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;

namespace VbaLanguageServer.Workspace;

/// <summary>
/// Assembles immutable project snapshots from workspace state, disk inventory, and reference catalogs.
/// </summary>
internal sealed class VbaProjectSnapshotBuilder
{
    private readonly IVbaProjectDiskInventory diskInventory;
    private readonly VbaProjectSourceDocumentCache diskDocumentCache;

    public VbaProjectSnapshotBuilder(
        IVbaProjectDiskInventory diskInventory,
        VbaProjectSourceDocumentCache diskDocumentCache)
    {
        this.diskInventory = diskInventory;
        this.diskDocumentCache = diskDocumentCache;
    }

    public VbaProjectSourceInventorySnapshot CreateInventorySnapshot(
        string activeUri,
        VbaProjectResolution resolution,
        IReadOnlyDictionary<string, VbaTrackedDocument> workspaceDocuments,
        IReadOnlySet<string> excludedSourceUris,
        IReadOnlyDictionary<string, bool> manifestBarrierOverrides,
        CancellationToken cancellationToken)
    {
        var diskCapture = diskInventory.CaptureColdSources(
            resolution,
            workspaceDocuments.Keys.ToArray(),
            excludedSourceUris,
            manifestBarrierOverrides,
            cancellationToken);
        var inventorySnapshot =
            VbaProjectSourceInventory.CreateInventorySnapshot(
                diskCapture,
                workspaceDocuments,
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
                pair => pair.Value.SourceDocument ?? VbaSourceDocumentProjector.Project(pair.Value.Uri, pair.Value.SyntaxTree),
                StringComparer.OrdinalIgnoreCase);
        var manifestContext = LanguageServerManifestResolution.Create(
            resolution,
            referenceCatalogs);
        var semanticInventory = VbaSemanticInventory.Create(
            scopedSourceDocuments,
            manifestContext.ReferenceSelection,
            referenceCatalogs);

        return new VbaProjectSnapshot(
            resolution,
            scopedDocuments,
            manifestContext.ReferenceSelection,
            semanticInventory);
    }
}
