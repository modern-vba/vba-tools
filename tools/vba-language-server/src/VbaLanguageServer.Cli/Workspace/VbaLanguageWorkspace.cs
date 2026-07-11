using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Syntax;

namespace VbaLanguageServer.Workspace;

/// <summary>
/// Represents an immutable snapshot of one resolved VBA project scope.
/// </summary>
/// <param name="Resolution">The project boundary resolution.</param>
/// <param name="SourceDocuments">The source text documents included in the scope, keyed by URI.</param>
/// <param name="ReferenceSelection">The active reference selection for the scope.</param>
/// <param name="SourceIndex">The source index built from the scoped documents and reference catalogs.</param>
public sealed record VbaProjectSnapshot(
    VbaProjectResolution Resolution,
    IReadOnlyDictionary<string, string> SourceDocuments,
    VbaProjectReferenceSelection? ReferenceSelection,
    VbaSourceIndex SourceIndex);

/// <summary>
/// Represents one document tracked in workspace memory.
/// </summary>
/// <param name="Uri">The document URI.</param>
/// <param name="Text">The latest document text.</param>
/// <param name="SyntaxTree">The latest parsed syntax tree.</param>
/// <param name="LastParseUpdateKind">The last parse update granularity.</param>
/// <param name="SourceDocument">The projected source document, when already available.</param>
public sealed record VbaTrackedDocument(
    string Uri,
    string Text,
    VbaSyntaxTree SyntaxTree,
    VbaSyntaxTreeParseUpdateKind LastParseUpdateKind,
    VbaSourceDocument? SourceDocument = null);

/// <summary>
/// Maintains open document text and creates project snapshots for language-server features.
/// </summary>
public sealed class VbaLanguageWorkspace
{
    private readonly object gate = new();
    private readonly Dictionary<string, VbaTrackedDocument> documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> excludedSourceUris = new(StringComparer.OrdinalIgnoreCase);
    private readonly VbaProjectReferenceCatalogCache referenceCatalogCache;
    private readonly VbaProjectSourceDocumentCache diskDocumentCache = new();

    /// <summary>
    /// Creates a language workspace.
    /// </summary>
    /// <param name="referenceCatalogCache">The reference catalog cache used when building source indexes.</param>
    public VbaLanguageWorkspace(VbaProjectReferenceCatalogCache referenceCatalogCache)
    {
        this.referenceCatalogCache = referenceCatalogCache;
    }

    /// <summary>
    /// Updates or adds an open document and parses its latest source text.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="text">The latest source text.</param>
    /// <param name="cancellationToken">A cancellation token for the update.</param>
    /// <returns>The parse update kind for the new syntax tree.</returns>
    public VbaSyntaxTreeParseUpdateKind UpdateDocument(
        string uri,
        string text,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            documents.TryGetValue(uri, out var previousDocument);
            var parseResult = VbaSyntaxTree.ParseOrUpdate(uri, text, previousDocument?.SyntaxTree);
            excludedSourceUris.Remove(uri);
            documents[uri] = new VbaTrackedDocument(
                uri,
                text,
                parseResult.SyntaxTree,
                parseResult.UpdateKind,
                VbaSourceIndex.CreateDocument(uri, parseResult.SyntaxTree));
            return parseResult.UpdateKind;
        }
    }

    /// <summary>
    /// Removes a source document and excludes it from disk inventory snapshots until reopened.
    /// </summary>
    /// <param name="uri">The document URI to remove.</param>
    /// <param name="cancellationToken">A cancellation token for the removal.</param>
    /// <returns>True when an open tracked document was removed.</returns>
    public bool RemoveSourceDocument(string uri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            excludedSourceUris.Add(uri);
            return documents.Remove(uri);
        }
    }

    /// <summary>
    /// Removes a tracked document without excluding it from future disk inventory.
    /// </summary>
    /// <param name="uri">The document URI to remove.</param>
    /// <param name="cancellationToken">A cancellation token for the removal.</param>
    /// <returns>True when an open tracked document was removed.</returns>
    public bool RemoveDocument(string uri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return documents.Remove(uri);
        }
    }

    /// <summary>
    /// Gets the latest syntax tree for a tracked document.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="cancellationToken">A cancellation token for the lookup.</param>
    /// <returns>The syntax tree, or null when the document is not tracked.</returns>
    public VbaSyntaxTree? GetDocumentSyntaxTree(
        string uri,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return documents.TryGetValue(uri, out var document)
                ? document.SyntaxTree
                : null;
        }
    }

    /// <summary>
    /// Gets the URIs of currently tracked documents.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token for the lookup.</param>
    /// <returns>The tracked document URIs.</returns>
    public IReadOnlyList<string> GetDocumentUris(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return documents.Keys.ToArray();
        }
    }

    /// <summary>
    /// Creates a project snapshot for the scope containing an active document.
    /// </summary>
    /// <param name="activeUri">The active document URI.</param>
    /// <param name="cancellationToken">A cancellation token for snapshot creation.</param>
    /// <returns>The resolved project snapshot.</returns>
    public VbaProjectSnapshot CreateProjectSnapshot(
        string activeUri,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolution = VbaProjectResolver.Resolve(activeUri);
        var documentSnapshot = CopyDocuments();
        var excludedSnapshot = CopyExcludedSourceUris();
        var scopedTrackedDocuments = VbaProjectSourceInventory.CreateSnapshot(
            resolution,
            documentSnapshot,
            excludedSnapshot,
            diskDocumentCache,
            cancellationToken);

        if (!scopedTrackedDocuments.ContainsKey(activeUri)
            && documentSnapshot.TryGetValue(activeUri, out var activeDocument))
        {
            scopedTrackedDocuments[activeUri] = activeDocument;
        }

        var scopedDocuments = scopedTrackedDocuments
            .ToDictionary(pair => pair.Key, pair => pair.Value.Text, StringComparer.OrdinalIgnoreCase);
        var scopedSourceDocuments = scopedTrackedDocuments
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value.SourceDocument ?? VbaSourceIndex.CreateDocument(pair.Value.Uri, pair.Value.SyntaxTree),
                StringComparer.OrdinalIgnoreCase);
        var manifestContext = LanguageServerManifestResolution.Create(
            resolution,
            referenceCatalogCache.Current);
        var sourceIndex = VbaSourceIndex.BuildFromSourceDocuments(
            scopedSourceDocuments,
            manifestContext.ReferenceSelection,
            referenceCatalogCache.Current);

        return new VbaProjectSnapshot(
            resolution,
            scopedDocuments,
            manifestContext.ReferenceSelection,
            sourceIndex);
    }

    /// <summary>
    /// Creates distinct project snapshots for all currently tracked document scopes.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token for snapshot creation.</param>
    /// <returns>The distinct project snapshots.</returns>
    public IReadOnlyList<VbaProjectSnapshot> CreateProjectSnapshots(CancellationToken cancellationToken = default)
    {
        var snapshots = new List<VbaProjectSnapshot>();
        var seenScopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var uri in GetDocumentUris(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = CreateProjectSnapshot(uri, cancellationToken);
            var scopeKey = string.Join(
                "|",
                snapshot.SourceDocuments.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase));
            if (seenScopes.Add(scopeKey))
            {
                snapshots.Add(snapshot);
            }
        }

        return snapshots;
    }

    private IReadOnlyDictionary<string, VbaTrackedDocument> CopyDocuments()
    {
        lock (gate)
        {
            return new Dictionary<string, VbaTrackedDocument>(documents, StringComparer.OrdinalIgnoreCase);
        }
    }

    private IReadOnlySet<string> CopyExcludedSourceUris()
    {
        lock (gate)
        {
            return new HashSet<string>(excludedSourceUris, StringComparer.OrdinalIgnoreCase);
        }
    }

}
