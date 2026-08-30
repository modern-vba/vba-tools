using VbaLanguageServer.ProjectModel;

namespace VbaLanguageServer.Workspace;

/// <summary>
/// Applies open-buffer priority and parsed projections to captured disk facts.
/// </summary>
internal static class VbaProjectSourceInventory
{
    public static VbaProjectSourceInventorySnapshot CreateInventorySnapshot(
        VbaProjectDiskColdSourceCapture diskCapture,
        IReadOnlyDictionary<string, VbaTrackedDocument> trackedDocuments,
        VbaProjectSourceDocumentCache diskDocumentCache,
        CancellationToken cancellationToken = default)
    {
        var documents = new Dictionary<string, VbaTrackedDocument>(
            StringComparer.OrdinalIgnoreCase);
        var trackedDocumentsByIdentity =
            CreateTrackedDocumentIdentityMap(trackedDocuments.Values);
        foreach (var source in diskCapture.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (VbaProjectIdentityModel.TryIdentifyDocument(
                    source.Uri,
                    out var sourceIdentity)
                && trackedDocumentsByIdentity.TryGetValue(
                    sourceIdentity,
                    out var trackedDocument))
            {
                documents[trackedDocument.Uri] = trackedDocument;
                continue;
            }

            documents[source.Uri] =
                diskDocumentCache.GetOrCreateDocument(
                    source,
                    cancellationToken);
        }

        foreach (var trackedDocument in trackedDocuments.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (VbaProjectIdentityModel.TryIdentifyDocument(
                    trackedDocument.Uri,
                    out var identity)
                && identity.IsLocalFile
                && diskCapture.OwnedCandidateSourcePaths.Contains(
                    identity.CanonicalValue))
            {
                documents[trackedDocument.Uri] = trackedDocument;
            }
        }

        return new VbaProjectSourceInventorySnapshot(
            documents,
            diskCapture.Sources,
            diskCapture.Failures,
            diskCapture.ExistingCandidateSourcePaths);
    }

    private static Dictionary<VbaDocumentIdentity, VbaTrackedDocument>
        CreateTrackedDocumentIdentityMap(
            IEnumerable<VbaTrackedDocument> trackedDocuments)
    {
        var map = new Dictionary<
            VbaDocumentIdentity,
            VbaTrackedDocument>();
        foreach (var trackedDocument in trackedDocuments)
        {
            if (VbaProjectIdentityModel.TryIdentifyDocument(
                    trackedDocument.Uri,
                    out var identity))
            {
                map[identity] = trackedDocument;
            }
        }

        return map;
    }
}

/// <summary>
/// Represents projected source documents and the disk facts that produced them.
/// </summary>
internal sealed record VbaProjectSourceInventorySnapshot(
    Dictionary<string, VbaTrackedDocument> Documents,
    IReadOnlyList<VbaProjectDiskSource> DiskSources,
    IReadOnlyList<VbaProjectDiskSourceFailure> Failures,
    IReadOnlySet<string> ExistingOpenSourcePaths);
