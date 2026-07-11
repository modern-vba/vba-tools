using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.Syntax;

namespace VbaLanguageServer.Workspace;

/// <summary>
/// Creates source document inventories for manifest-backed and ad-hoc VBA project scopes.
/// </summary>
internal static class VbaProjectSourceInventory
{
    private static readonly string[] SourcePatterns = ["*.bas", "*.cls", "*.frm"];

    /// <summary>
    /// Creates a scoped document snapshot from disk source files and tracked open documents.
    /// </summary>
    /// <param name="resolution">The project boundary resolution.</param>
    /// <param name="trackedDocuments">The current in-memory tracked documents.</param>
    /// <param name="excludedUris">Source URIs that should not be loaded from disk.</param>
    /// <param name="cancellationToken">A cancellation token for inventory work.</param>
    /// <returns>The tracked documents that belong to the resolved project scope.</returns>
    public static Dictionary<string, VbaTrackedDocument> CreateSnapshot(
        VbaProjectResolution resolution,
        IReadOnlyDictionary<string, VbaTrackedDocument> trackedDocuments,
        IReadOnlySet<string> excludedUris,
        CancellationToken cancellationToken = default)
    {
        var documents = new Dictionary<string, VbaTrackedDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (var uri in EnumerateSourceUris(resolution, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (excludedUris.Contains(uri))
            {
                continue;
            }

            if (trackedDocuments.TryGetValue(uri, out var trackedDocument))
            {
                documents[uri] = trackedDocument;
                continue;
            }

            var localPath = VbaProjectResolver.TryGetLocalPath(uri);
            if (localPath is null || !File.Exists(localPath))
            {
                continue;
            }

            var text = File.ReadAllText(localPath);
            documents[uri] = new VbaTrackedDocument(
                uri,
                text,
                VbaSyntaxTree.ParseModule(uri, text),
                VbaSyntaxTreeParseUpdateKind.FullModule);
        }

        foreach (var trackedDocument in trackedDocuments.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!excludedUris.Contains(trackedDocument.Uri) && resolution.ContainsUri(trackedDocument.Uri))
            {
                documents[trackedDocument.Uri] = trackedDocument;
            }
        }

        return documents;
    }

    private static IEnumerable<string> EnumerateSourceUris(
        VbaProjectResolution resolution,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(resolution.RootPath) || !Directory.Exists(resolution.RootPath))
        {
            yield break;
        }

        var searchOption = resolution.Kind == VbaProjectResolutionKind.ManifestDocument
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;
        foreach (var pattern in SourcePatterns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var path in Directory.EnumerateFiles(resolution.RootPath, pattern, searchOption))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new Uri(Path.GetFullPath(path)).AbsoluteUri;
            }
        }
    }
}
