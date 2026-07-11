using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.Parsing;
using VbaLanguageServer.SourceModel;

namespace VbaLanguageServer.Workspace;

public sealed record VbaProjectReferenceSelectionContext(
    string DocumentName,
    VbaProjectReferenceSelection Selection);

public sealed record VbaProjectSnapshot(
    VbaProjectResolution Resolution,
    IReadOnlyDictionary<string, string> SourceDocuments,
    VbaProjectReferenceSelection? ReferenceSelection,
    VbaSourceIndex SourceIndex);

public sealed record VbaTrackedDocument(
    string Uri,
    string Text,
    VbaModuleSyntaxTree SyntaxTree,
    VbaModuleParseUpdateKind LastParseUpdateKind);

public sealed class VbaLanguageWorkspace
{
    private readonly object gate = new();
    private readonly Dictionary<string, VbaTrackedDocument> documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly VbaProjectReferenceCatalogCache referenceCatalogCache;

    public VbaLanguageWorkspace(VbaProjectReferenceCatalogCache referenceCatalogCache)
    {
        this.referenceCatalogCache = referenceCatalogCache;
    }

    public VbaModuleParseUpdateKind UpdateDocument(
        string uri,
        string text,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            documents.TryGetValue(uri, out var previousDocument);
            var parseResult = VbaModuleParser.ParseOrUpdate(uri, text, previousDocument?.SyntaxTree);
            documents[uri] = new VbaTrackedDocument(
                uri,
                text,
                parseResult.SyntaxTree,
                parseResult.UpdateKind);
            return parseResult.UpdateKind;
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

    public VbaLanguageServer.Syntax.VbaSyntaxTree? GetDocumentSyntaxTree(
        string uri,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return documents.TryGetValue(uri, out var document)
                ? document.SyntaxTree.CoreSyntaxTree
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
        var scopedTrackedDocuments = documentSnapshot
            .Where(pair => resolution.ContainsUri(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        if (!scopedTrackedDocuments.ContainsKey(activeUri)
            && documentSnapshot.TryGetValue(activeUri, out var activeDocument))
        {
            scopedTrackedDocuments[activeUri] = activeDocument;
        }

        var scopedDocuments = scopedTrackedDocuments
            .ToDictionary(pair => pair.Key, pair => pair.Value.Text, StringComparer.OrdinalIgnoreCase);
        var scopedSyntaxTrees = scopedTrackedDocuments
            .ToDictionary(pair => pair.Key, pair => pair.Value.SyntaxTree, StringComparer.OrdinalIgnoreCase);
        var referenceSelection =
            resolution.Kind == VbaProjectResolutionKind.ManifestDocument
            && !string.IsNullOrEmpty(resolution.DocumentKind)
                ? VbaProjectReferenceSelection.Create(
                    resolution.DocumentKind,
                    resolution.ReferenceEntries)
                : null;
        var sourceIndex = VbaSourceIndex.BuildFromSyntaxTrees(
            scopedSyntaxTrees,
            referenceSelection,
            referenceCatalogCache.Current);

        return new VbaProjectSnapshot(
            resolution,
            scopedDocuments,
            referenceSelection,
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

    public static bool TryCreateReferenceSelections(
        string uri,
        string text,
        out IReadOnlyList<VbaProjectReferenceSelectionContext> selections)
    {
        selections = [];
        if (IsProjectManifestUri(uri))
        {
            try
            {
                var manifest = ProjectManifestReader.Parse(text, uri);
                selections = manifest.Documents
                    .Select(document => new VbaProjectReferenceSelectionContext(
                        document.Key,
                        VbaProjectReferenceSelection.Create(
                            document.Value.Kind,
                            document.Value.References ?? [])))
                    .ToArray();
                return selections.Count > 0;
            }
            catch (ProjectManifestException)
            {
                return false;
            }
        }

        VbaProjectResolution resolution;
        try
        {
            resolution = VbaProjectResolver.Resolve(uri);
        }
        catch (ProjectManifestException)
        {
            return false;
        }

        if (resolution.Kind != VbaProjectResolutionKind.ManifestDocument
            || string.IsNullOrEmpty(resolution.DocumentName)
            || string.IsNullOrEmpty(resolution.DocumentKind))
        {
            return false;
        }

        selections =
        [
            new VbaProjectReferenceSelectionContext(
                resolution.DocumentName,
                VbaProjectReferenceSelection.Create(
                    resolution.DocumentKind,
                    resolution.ReferenceEntries))
        ];
        return true;
    }

    private static bool IsProjectManifestUri(string uri)
    {
        var localPath = VbaProjectResolver.TryGetLocalPath(uri);
        return localPath is not null
            && Path.GetFileName(localPath).Equals("project.json", StringComparison.OrdinalIgnoreCase);
    }
}
