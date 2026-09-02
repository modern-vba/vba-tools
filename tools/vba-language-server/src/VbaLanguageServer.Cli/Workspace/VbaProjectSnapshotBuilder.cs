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
        VbaIdentifiedDocument activeDocument,
        VbaProjectResolution resolution,
        IReadOnlyDictionary<VbaDocumentIdentity, VbaTrackedDocument>
            workspaceDocumentsByIdentity,
        IReadOnlySet<VbaDocumentIdentity> excludedSourceIdentities,
        IReadOnlyDictionary<VbaDocumentIdentity, bool>
            manifestBarrierOverrides,
        CancellationToken cancellationToken)
    {
        var diskCapture = diskInventory.CaptureColdSources(
            resolution,
            workspaceDocumentsByIdentity.Keys.ToArray(),
            excludedSourceIdentities,
            manifestBarrierOverrides,
            cancellationToken);
        var inventorySnapshot =
            VbaProjectSourceInventory.CreateInventorySnapshot(
                diskCapture,
                workspaceDocumentsByIdentity,
                diskDocumentCache,
                cancellationToken);
        if (!inventorySnapshot.DocumentsByIdentity.ContainsKey(
                activeDocument.Identity)
            && workspaceDocumentsByIdentity.TryGetValue(
                activeDocument.Identity,
                out var trackedActiveDocument))
        {
            inventorySnapshot.Documents[activeDocument.Uri] =
                trackedActiveDocument;
            inventorySnapshot.DocumentsByIdentity[activeDocument.Identity] =
                trackedActiveDocument;
        }

        return inventorySnapshot;
    }

    public VbaProjectSnapshot BuildSnapshot(
        VbaProjectResolution resolution,
        IReadOnlyDictionary<string, VbaTrackedDocument> scopedTrackedDocuments,
        IReadOnlyList<VbaProjectDiskSource> diskSources,
        IReadOnlyList<VbaProjectDiskSourceFailure> diskSourceFailures,
        IReadOnlySet<VbaDocumentIdentity> existingOpenSourceIdentities,
        VbaProjectReferenceCatalogSet referenceCatalogs,
        IReadOnlyDictionary<string, VbaProjectReferenceCatalogSource> referenceCatalogSources,
        VbaIntrinsicHostEventCatalog? intrinsicHostEventCatalog,
        IReadOnlyDictionary<string, VbaProjectReferenceCatalogIdentity>?
            referenceCatalogIdentities = null,
        IReadOnlyDictionary<string, string>?
            authoritativeReferencedProjectNames = null)
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
            referenceCatalogs,
            intrinsicHostEventCatalog,
            referenceCatalogSources,
            referenceCatalogIdentities,
            resolution,
            authoritativeReferencedProjectNames);

        return new VbaProjectSnapshot(
            resolution,
            scopedDocuments,
            manifestContext.ReferenceSelection,
            semanticInventory)
        {
            DiskSources = diskSources,
            DiskSourceFailures = diskSourceFailures,
            ExistingOpenSourceIdentities = existingOpenSourceIdentities
        };
    }
}
