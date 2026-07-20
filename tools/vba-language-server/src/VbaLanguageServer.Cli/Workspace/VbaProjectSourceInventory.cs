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
        var trackedDocumentsByPath =
            CreateTrackedDocumentPathMap(trackedDocuments.Values);
        foreach (var source in diskCapture.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (trackedDocuments.TryGetValue(
                    source.Uri,
                    out var trackedDocument)
                || trackedDocumentsByPath.TryGetValue(
                    source.FullPath,
                    out trackedDocument))
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
            var localPath = VbaProjectResolver.TryGetLocalPath(
                trackedDocument.Uri);
            if (localPath is not null
                && diskCapture.OwnedCandidateSourcePaths.Contains(
                    Path.GetFullPath(localPath)))
            {
                documents[trackedDocument.Uri] = trackedDocument;
            }
        }

        return new VbaProjectSourceInventorySnapshot(
            documents,
            diskCapture.Sources);
    }

    private static Dictionary<string, VbaTrackedDocument>
        CreateTrackedDocumentPathMap(
            IEnumerable<VbaTrackedDocument> trackedDocuments)
    {
        var map = new Dictionary<string, VbaTrackedDocument>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var trackedDocument in trackedDocuments)
        {
            var localPath = VbaProjectResolver.TryGetLocalPath(
                trackedDocument.Uri);
            if (localPath is not null)
            {
                map[Path.GetFullPath(localPath)] = trackedDocument;
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
    IReadOnlyList<VbaProjectDiskSource> DiskSources);
