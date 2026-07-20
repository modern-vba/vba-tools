using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
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
    /// <param name="diskDocumentCache">The cache used for disk-loaded source documents.</param>
    /// <param name="cancellationToken">A cancellation token for inventory work.</param>
    /// <returns>The tracked documents that belong to the resolved project scope.</returns>
    public static Dictionary<string, VbaTrackedDocument> CreateSnapshot(
        VbaProjectResolution resolution,
        IReadOnlyDictionary<string, VbaTrackedDocument> trackedDocuments,
        IReadOnlySet<string> excludedUris,
        VbaProjectSourceDocumentCache? diskDocumentCache = null,
        CancellationToken cancellationToken = default)
        => CreateInventorySnapshot(
            resolution,
            trackedDocuments,
            excludedUris,
            diskDocumentCache,
            cancellationToken)
            .Documents;

    /// <summary>
    /// Creates a scoped document inventory and a stamp for detecting unchanged disk inputs.
    /// </summary>
    /// <param name="resolution">The project boundary resolution.</param>
    /// <param name="trackedDocuments">The current in-memory tracked documents.</param>
    /// <param name="excludedUris">Source URIs that should not be loaded from disk.</param>
    /// <param name="diskDocumentCache">The cache used for disk-loaded source documents.</param>
    /// <param name="cancellationToken">A cancellation token for inventory work.</param>
    /// <returns>The scoped inventory and its source-file stamp.</returns>
    public static VbaProjectSourceInventorySnapshot CreateInventorySnapshot(
        VbaProjectResolution resolution,
        IReadOnlyDictionary<string, VbaTrackedDocument> trackedDocuments,
        IReadOnlySet<string> excludedUris,
        VbaProjectSourceDocumentCache? diskDocumentCache = null,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, bool>? manifestBarrierOverrides = null)
    {
        var fileSystem =
            diskDocumentCache?.FileSystem
            ?? SystemVbaProjectFileSystem.Instance;
        var documents = new Dictionary<string, VbaTrackedDocument>(StringComparer.OrdinalIgnoreCase);
        var fileStates = new List<VbaProjectSourceFileState>();
        var excludedPaths = CreateLocalPathSet(excludedUris);
        var trackedDocumentsByPath = CreateTrackedDocumentPathMap(trackedDocuments.Values);
        var ownershipBoundary = new VbaProjectSourceOwnershipBoundary(
            resolution,
            fileSystem,
            manifestBarrierOverrides);
        foreach (var uri in EnumerateSourceUris(
            resolution,
            fileSystem,
            cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var localPath = VbaProjectResolver.TryGetLocalPath(uri);
            if (localPath is null
                || excludedUris.Contains(uri)
                || excludedPaths.Contains(localPath)
                || !ownershipBoundary.ContainsSource(localPath))
            {
                continue;
            }

            if (!fileSystem.TryGetSourceMetadata(localPath, out var metadata))
            {
                continue;
            }

            fileStates.Add(VbaProjectSourceFileState.From(localPath, metadata));

            if (trackedDocuments.TryGetValue(uri, out var trackedDocument))
            {
                documents[trackedDocument.Uri] = trackedDocument;
                continue;
            }

            if (trackedDocumentsByPath.TryGetValue(localPath, out trackedDocument))
            {
                documents[trackedDocument.Uri] = trackedDocument;
                continue;
            }

            documents[uri] = diskDocumentCache is null
                ? LoadDocument(uri, localPath, fileSystem, cancellationToken)
                : diskDocumentCache.GetOrLoadDocument(uri, localPath, cancellationToken);
        }

        foreach (var trackedDocument in trackedDocuments.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var localPath = VbaProjectResolver.TryGetLocalPath(
                trackedDocument.Uri);
            if (localPath is not null
                && ownershipBoundary.ContainsSource(localPath))
            {
                documents[trackedDocument.Uri] = trackedDocument;
            }
        }

        return new VbaProjectSourceInventorySnapshot(
            documents,
            fileStates);
    }

    private static IEnumerable<string> EnumerateSourceUris(
        VbaProjectResolution resolution,
        IVbaProjectFileSystem fileSystem,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(resolution.RootPath)
            || !fileSystem.DirectoryExists(resolution.RootPath))
        {
            yield break;
        }

        var searchOption = resolution.Kind == VbaProjectResolutionKind.ManifestDocument
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;
        foreach (var pattern in SourcePatterns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var path in fileSystem.EnumerateSourceFiles(
                resolution.RootPath,
                pattern,
                searchOption))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new Uri(Path.GetFullPath(path)).AbsoluteUri;
            }
        }
    }

    private static VbaTrackedDocument LoadDocument(
        string uri,
        string localPath,
        IVbaProjectFileSystem fileSystem,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var text = VbaSourceFileTextReader.Decode(
            fileSystem.ReadSourceBytes(localPath));
        var syntaxTree = VbaSyntaxTree.ParseModule(uri, text);
        return new VbaTrackedDocument(
            uri,
            text,
            syntaxTree,
            VbaSyntaxTreeParseUpdateKind.FullModule,
            SourceDocument: VbaSourceIndex.CreateDocument(uri, syntaxTree));
    }

    private static HashSet<string> CreateLocalPathSet(IEnumerable<string> uris)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var uri in uris)
        {
            var localPath = VbaProjectResolver.TryGetLocalPath(uri);
            if (localPath is not null)
            {
                paths.Add(localPath);
            }
        }

        return paths;
    }

    private static Dictionary<string, VbaTrackedDocument> CreateTrackedDocumentPathMap(
        IEnumerable<VbaTrackedDocument> trackedDocuments)
    {
        var map = new Dictionary<string, VbaTrackedDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (var trackedDocument in trackedDocuments)
        {
            var localPath = VbaProjectResolver.TryGetLocalPath(trackedDocument.Uri);
            if (localPath is not null)
            {
                map[localPath] = trackedDocument;
            }
        }

        return map;
    }

}

/// <summary>
/// Represents the metadata needed to detect changes to a known project source without enumerating the project again.
/// </summary>
internal sealed record VbaProjectSourceFileState(
    string FullPath,
    long Length,
    long LastWriteTimeUtcTicks)
{
    public static VbaProjectSourceFileState From(
        string fullPath,
        VbaProjectSourceFileMetadata metadata)
        => new(
            Path.GetFullPath(fullPath),
            metadata.Length,
            metadata.LastWriteTimeUtcTicks);
}

/// <summary>
/// Represents a scoped project source inventory and its known disk source state.
/// </summary>
/// <param name="Documents">The tracked documents that belong to the resolved project scope.</param>
/// <param name="SourceFiles">The known disk source state used by the cache fast path.</param>
internal sealed record VbaProjectSourceInventorySnapshot(
    Dictionary<string, VbaTrackedDocument> Documents,
    IReadOnlyList<VbaProjectSourceFileState> SourceFiles);
