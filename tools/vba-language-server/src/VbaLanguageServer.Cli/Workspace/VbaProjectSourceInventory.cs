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
        CancellationToken cancellationToken = default)
    {
        var documents = new Dictionary<string, VbaTrackedDocument>(StringComparer.OrdinalIgnoreCase);
        var fileStates = new List<string>();
        var excludedPaths = CreateLocalPathSet(excludedUris);
        var trackedDocumentsByPath = CreateTrackedDocumentPathMap(trackedDocuments.Values);
        foreach (var uri in EnumerateSourceUris(resolution, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var localPath = VbaProjectResolver.TryGetLocalPath(uri);
            if (localPath is null || excludedUris.Contains(uri) || excludedPaths.Contains(localPath))
            {
                continue;
            }

            var fileInfo = new FileInfo(localPath);
            if (!fileInfo.Exists)
            {
                continue;
            }

            fileStates.Add(CreateFileState(fileInfo));

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
                ? LoadDocument(uri, localPath, cancellationToken)
                : diskDocumentCache.GetOrLoadDocument(uri, localPath, cancellationToken);
        }

        foreach (var trackedDocument in trackedDocuments.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var trackedPath = VbaProjectResolver.TryGetLocalPath(trackedDocument.Uri);
            if (!excludedUris.Contains(trackedDocument.Uri)
                && (trackedPath is null || !excludedPaths.Contains(trackedPath))
                && resolution.ContainsUri(trackedDocument.Uri))
            {
                documents[trackedDocument.Uri] = trackedDocument;
            }
        }

        return new VbaProjectSourceInventorySnapshot(
            documents,
            CreateInventoryStamp(fileStates));
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

    private static VbaTrackedDocument LoadDocument(
        string uri,
        string localPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var text = VbaSourceFileTextReader.ReadAllText(localPath);
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

    private static string CreateFileState(FileInfo fileInfo)
        => string.Join(
            "|",
            Path.GetFullPath(fileInfo.FullName),
            fileInfo.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            fileInfo.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static string CreateInventoryStamp(IEnumerable<string> fileStates)
        => string.Join(
            "\n",
            fileStates.OrderBy(state => state, StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// Represents a scoped project source inventory and its disk source stamp.
/// </summary>
/// <param name="Documents">The tracked documents that belong to the resolved project scope.</param>
/// <param name="Stamp">The deterministic source-file stamp for cache invalidation.</param>
internal sealed record VbaProjectSourceInventorySnapshot(
    Dictionary<string, VbaTrackedDocument> Documents,
    string Stamp);
