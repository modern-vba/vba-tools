using VbaLanguageServer.ProjectModel;

namespace VbaLanguageServer.Workspace;

/// <summary>
/// Applies open-buffer priority and parsed projections to captured disk facts.
/// </summary>
internal static class VbaProjectSourceInventory
{
    public static VbaProjectSourceInventorySnapshot CreateInventorySnapshot(
        VbaProjectDiskColdSourceCapture diskCapture,
        IReadOnlyDictionary<VbaDocumentIdentity, VbaTrackedDocument>
            trackedDocumentsByIdentity,
        VbaProjectSourceDocumentCache diskDocumentCache,
        CancellationToken cancellationToken = default)
    {
        var documents = new Dictionary<string, VbaTrackedDocument>(
            StringComparer.OrdinalIgnoreCase);
        var documentsByIdentity = new Dictionary<
            VbaDocumentIdentity,
            VbaTrackedDocument>();
        foreach (var source in diskCapture.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (trackedDocumentsByIdentity.TryGetValue(
                    source.DocumentIdentity,
                    out var trackedDocument))
            {
                documents[trackedDocument.Uri] = trackedDocument;
                documentsByIdentity[source.DocumentIdentity] =
                    trackedDocument;
                continue;
            }

            var diskDocument = diskDocumentCache.GetOrCreateDocument(
                source,
                cancellationToken);
            documents[source.Uri] = diskDocument;
            documentsByIdentity[source.DocumentIdentity] = diskDocument;
        }

        foreach (var (identity, trackedDocument) in
            trackedDocumentsByIdentity)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (diskCapture.OwnedCandidateSourceIdentities.Contains(
                    identity))
            {
                documents[trackedDocument.Uri] = trackedDocument;
                documentsByIdentity[identity] = trackedDocument;
            }
        }

        return new VbaProjectSourceInventorySnapshot(
            documents,
            documentsByIdentity,
            diskCapture.Sources,
            diskCapture.Failures,
            diskCapture.ExistingCandidateSourceIdentities);
    }
}

/// <summary>
/// Represents projected source documents and the disk facts that produced them.
/// </summary>
internal sealed record VbaProjectSourceInventorySnapshot(
    Dictionary<string, VbaTrackedDocument> Documents,
    Dictionary<VbaDocumentIdentity, VbaTrackedDocument> DocumentsByIdentity,
    IReadOnlyList<VbaProjectDiskSource> DiskSources,
    IReadOnlyList<VbaProjectDiskSourceFailure> Failures,
    IReadOnlySet<VbaDocumentIdentity> ExistingOpenSourceIdentities);
