using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Syntax;

namespace VbaLanguageServer.Workspace;

/// <summary>
/// Caches parsed source documents loaded from disk for repeated project snapshots.
/// </summary>
internal sealed class VbaProjectSourceDocumentCache
{
    private const int MaxStableReadAttempts = 3;
    private readonly object gate = new();
    private readonly IVbaProjectFileSystem fileSystem;
    private readonly Dictionary<string, CachedDocument> documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> activeLoads =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> invalidationGenerations =
        new(StringComparer.OrdinalIgnoreCase);

    public VbaProjectSourceDocumentCache()
        : this(SystemVbaProjectFileSystem.Instance)
    {
    }

    internal VbaProjectSourceDocumentCache(IVbaProjectFileSystem fileSystem)
    {
        this.fileSystem = fileSystem;
    }

    internal IVbaProjectFileSystem FileSystem => fileSystem;

    public int Count
    {
        get
        {
            lock (gate)
            {
                return documents.Count;
            }
        }
    }

    /// <summary>
    /// Gets a cached disk document or reloads it when the file identity has changed.
    /// </summary>
    /// <param name="uri">The canonical disk URI used for the source document.</param>
    /// <param name="localPath">The local filesystem path to load.</param>
    /// <param name="cancellationToken">A cancellation token for cache work.</param>
    /// <returns>The tracked source document loaded from disk.</returns>
    public VbaTrackedDocument GetOrLoadDocument(
        string uri,
        string localPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(localPath);
        if (!fileSystem.TryGetSourceMetadata(fullPath, out var metadata))
        {
            throw new FileNotFoundException("Source file was not found.", fullPath);
        }

        long capturedInvalidationGeneration;
        lock (gate)
        {
            if (documents.TryGetValue(fullPath, out var cached)
                && cached.Metadata == metadata)
            {
                return cached.Document;
            }

            activeLoads.TryGetValue(fullPath, out var activeLoadCount);
            activeLoads[fullPath] = activeLoadCount + 1;
            invalidationGenerations.TryGetValue(
                fullPath,
                out capturedInvalidationGeneration);
        }

        try
        {
            for (var attempt = 0;
                attempt < MaxStableReadAttempts;
                attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (attempt > 0)
                {
                    lock (gate)
                    {
                        if (documents.TryGetValue(
                                fullPath,
                                out var retriedCached)
                            && retriedCached.Metadata == metadata)
                        {
                            return retriedCached.Document;
                        }
                    }
                }

                var sourceBytes = fileSystem.ReadSourceBytes(fullPath);
                cancellationToken.ThrowIfCancellationRequested();
                if (!fileSystem.TryGetSourceMetadata(
                        fullPath,
                        out var loadedMetadata))
                {
                    throw new FileNotFoundException(
                        "Source file was removed while it was being read.",
                        fullPath);
                }

                if (loadedMetadata != metadata)
                {
                    metadata = loadedMetadata;
                    continue;
                }

                var text = VbaSourceFileTextReader.Decode(sourceBytes);
                var syntaxTree = VbaSyntaxTree.ParseModule(uri, text);
                var document = new VbaTrackedDocument(
                    uri,
                    text,
                    syntaxTree,
                    VbaSyntaxTreeParseUpdateKind.FullModule,
                    SourceDocument: VbaSourceIndex.CreateDocument(
                        uri,
                        syntaxTree));
                lock (gate)
                {
                    if (documents.TryGetValue(
                            fullPath,
                            out var concurrentlyCached)
                        && concurrentlyCached.Metadata == loadedMetadata)
                    {
                        return concurrentlyCached.Document;
                    }

                    invalidationGenerations.TryGetValue(
                        fullPath,
                        out var currentInvalidationGeneration);
                    if (currentInvalidationGeneration
                        == capturedInvalidationGeneration)
                    {
                        documents[fullPath] = new CachedDocument(
                            loadedMetadata,
                            document);
                    }
                }

                return document;
            }

            throw new IOException(
                $"Source file changed repeatedly while it was being read: {fullPath}");
        }
        finally
        {
            lock (gate)
            {
                var remainingLoadCount = activeLoads[fullPath] - 1;
                if (remainingLoadCount == 0)
                {
                    activeLoads.Remove(fullPath);
                    invalidationGenerations.Remove(fullPath);
                }
                else
                {
                    activeLoads[fullPath] = remainingLoadCount;
                }
            }
        }
    }

    /// <summary>
    /// Invalidates a cached disk document after an explicit watcher event.
    /// </summary>
    /// <param name="localPath">The local source path.</param>
    public void Invalidate(string localPath)
    {
        var fullPath = Path.GetFullPath(localPath);
        lock (gate)
        {
            documents.Remove(fullPath);
            if (activeLoads.ContainsKey(fullPath))
            {
                invalidationGenerations.TryGetValue(
                    fullPath,
                    out var previousGeneration);
                invalidationGenerations[fullPath] =
                    previousGeneration + 1;
            }
            else
            {
                invalidationGenerations.Remove(fullPath);
            }
        }
    }

    private sealed record CachedDocument(
        VbaProjectSourceFileMetadata Metadata,
        VbaTrackedDocument Document);
}
