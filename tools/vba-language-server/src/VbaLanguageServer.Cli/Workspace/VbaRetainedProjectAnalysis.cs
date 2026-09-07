using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;

namespace VbaLanguageServer.Workspace;

internal sealed record VbaRetainedAnalysisLimits(
    int MaximumEntries = 4,
    long MaximumBytes = 1024L * 1024 * 1024);

/// <summary>
/// Completed analysis data without a snapshot, document lifecycle, or publication lease.
/// Eligibility is proved against newly captured content and semantic inputs before use.
/// </summary>
internal sealed class VbaRetainedProjectAnalysis
{
    public VbaRetainedProjectAnalysis(
        VbaProjectSnapshotIdentity identity,
        VbaProjectResolution resolution,
        long referenceCatalogRevision,
        long intrinsicHostEventCatalogRevision,
        IReadOnlyDictionary<VbaDocumentIdentity, VbaTrackedDocument> documents,
        VbaSemanticInventory inventory)
    {
        Identity = identity;
        Resolution = resolution with
        {
            References = resolution.ReferenceEntries.ToArray(),
            CommonModules = resolution.InstalledCommonModuleEntries.ToArray()
        };
        ReferenceCatalogRevision = referenceCatalogRevision;
        IntrinsicHostEventCatalogRevision = intrinsicHostEventCatalogRevision;
        Documents = new Dictionary<VbaDocumentIdentity, VbaTrackedDocument>(documents);
        Inventory = inventory.CreateForRetainedAnalysis();
        var inventoryBytes = Inventory.EstimateRetainedAnalysisBytes();
        var documentBytes = Documents.Sum(pair => 4096L + pair.Value.Text.Length * 2L);
        EstimatedBytes = inventoryBytes > long.MaxValue - documentBytes
            ? long.MaxValue : inventoryBytes + documentBytes;
    }

    public VbaProjectResolution Resolution { get; }
    private VbaProjectSnapshotIdentity Identity { get; }
    public long ReferenceCatalogRevision { get; }
    public long IntrinsicHostEventCatalogRevision { get; }
    public IReadOnlyDictionary<VbaDocumentIdentity, VbaTrackedDocument> Documents { get; }
    public VbaSemanticInventory Inventory { get; }
    public long EstimatedBytes { get; }

    public string? GetReuseFailure(
        VbaProjectSnapshotIdentity identity,
        VbaProjectResolution resolution,
        long referenceCatalogRevision,
        long intrinsicHostEventCatalogRevision,
        VbaProjectSourceInventorySnapshot sources)
    {
        if (ReferenceCatalogRevision != referenceCatalogRevision)
        {
            return "referenceCatalog";
        }

        if (IntrinsicHostEventCatalogRevision != intrinsicHostEventCatalogRevision)
        {
            return "intrinsicHostEvents";
        }

        if (!Identity.Equals(identity)
            || !Resolution.ReferenceEntries.SequenceEqual(resolution.ReferenceEntries)
            || !Resolution.InstalledCommonModuleEntries.SequenceEqual(resolution.InstalledCommonModuleEntries))
        {
            return "projectResolution";
        }

        if (sources.Failures.Count > 0)
        {
            return "sourceFailure";
        }

        if (Documents.Count != sources.DocumentsByIdentity.Count)
        {
            return "sourceMembership";
        }

        foreach (var (documentIdentity, previous) in Documents)
        {
            if (!sources.DocumentsByIdentity.TryGetValue(documentIdentity, out var current))
            {
                return "sourceMembership";
            }

            if (!previous.Text.Equals(current.Text, StringComparison.Ordinal))
            {
                return "sourceContent";
            }
        }

        return null;
    }
}
