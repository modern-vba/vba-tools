using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Syntax;

namespace VbaLanguageServer.Workspace;

public sealed record VbaProjectSnapshot(
    VbaProjectResolution Resolution,
    IReadOnlyDictionary<string, string> SourceDocuments,
    VbaProjectReferenceSelection? ReferenceSelection,
    VbaSourceIndex SourceIndex);

public sealed record VbaTrackedDocument(
    string Uri,
    string Text,
    VbaSyntaxTree SyntaxTree,
    VbaSyntaxTreeParseUpdateKind LastParseUpdateKind);

public sealed class VbaLanguageWorkspace
{
    private readonly object gate = new();
    private readonly Dictionary<string, VbaTrackedDocument> documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> excludedSourceUris = new(StringComparer.OrdinalIgnoreCase);
    private readonly VbaProjectReferenceCatalogCache referenceCatalogCache;

    public VbaLanguageWorkspace(VbaProjectReferenceCatalogCache referenceCatalogCache)
    {
        this.referenceCatalogCache = referenceCatalogCache;
    }

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
                parseResult.UpdateKind);
            return parseResult.UpdateKind;
        }
    }

    public bool RemoveSourceDocument(string uri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            excludedSourceUris.Add(uri);
            return documents.Remove(uri);
        }
    }

    public bool RemoveDocument(string uri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return documents.Remove(uri);
        }
    }

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

    public IReadOnlyList<string> GetDocumentUris(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return documents.Keys.ToArray();
        }
    }

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
            cancellationToken);

        if (!scopedTrackedDocuments.ContainsKey(activeUri)
            && documentSnapshot.TryGetValue(activeUri, out var activeDocument))
        {
            scopedTrackedDocuments[activeUri] = activeDocument;
        }

        var scopedDocuments = scopedTrackedDocuments
            .ToDictionary(pair => pair.Key, pair => pair.Value.Text, StringComparer.OrdinalIgnoreCase);
        var scopedSyntaxTrees = scopedTrackedDocuments
            .ToDictionary(pair => pair.Key, pair => pair.Value.SyntaxTree, StringComparer.OrdinalIgnoreCase);
        var manifestContext = LanguageServerManifestResolution.Create(
            resolution,
            referenceCatalogCache.Current);
        var sourceIndex = VbaSourceIndex.BuildFromSyntaxTrees(
            scopedSyntaxTrees,
            manifestContext.ReferenceSelection,
            referenceCatalogCache.Current);

        return new VbaProjectSnapshot(
            resolution,
            scopedDocuments,
            manifestContext.ReferenceSelection,
            sourceIndex);
    }

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
