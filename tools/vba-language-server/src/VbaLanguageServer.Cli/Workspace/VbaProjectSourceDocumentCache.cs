using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Syntax;

namespace VbaLanguageServer.Workspace;

/// <summary>
/// Caches parsed source documents loaded from disk for repeated project snapshots.
/// </summary>
internal sealed class VbaProjectSourceDocumentCache
{
    private readonly object gate = new();
    private readonly Dictionary<string, CachedDocument> documents = new(StringComparer.OrdinalIgnoreCase);

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
        var fileInfo = new FileInfo(fullPath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("Source file was not found.", fullPath);
        }

        lock (gate)
        {
            if (documents.TryGetValue(fullPath, out var cached)
                && cached.Length == fileInfo.Length
                && cached.LastWriteTimeUtc == fileInfo.LastWriteTimeUtc)
            {
                return cached.Document;
            }
        }

        var text = VbaSourceFileTextReader.ReadAllText(fullPath);
        cancellationToken.ThrowIfCancellationRequested();
        var syntaxTree = VbaSyntaxTree.ParseModule(uri, text);
        var document = new VbaTrackedDocument(
            uri,
            text,
            syntaxTree,
            VbaSyntaxTreeParseUpdateKind.FullModule,
            SourceDocument: VbaSourceIndex.CreateDocument(uri, syntaxTree));
        fileInfo.Refresh();

        lock (gate)
        {
            documents[fullPath] = new CachedDocument(
                fileInfo.Length,
                fileInfo.LastWriteTimeUtc,
                document);
        }

        return document;
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
        }
    }

    private sealed record CachedDocument(
        long Length,
        DateTime LastWriteTimeUtc,
        VbaTrackedDocument Document);
}
