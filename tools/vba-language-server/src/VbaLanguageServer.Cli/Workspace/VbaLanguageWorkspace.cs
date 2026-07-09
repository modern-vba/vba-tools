using VbaLanguageServer.ProjectModel;
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

public sealed class VbaLanguageWorkspace
{
    private readonly Dictionary<string, string> documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly VbaProjectReferenceCatalogCache referenceCatalogCache;

    public VbaLanguageWorkspace(VbaProjectReferenceCatalogCache referenceCatalogCache)
    {
        this.referenceCatalogCache = referenceCatalogCache;
    }

    public void UpdateDocument(string uri, string text)
    {
        documents[uri] = text;
    }

    public VbaProjectSnapshot CreateProjectSnapshot(string activeUri)
    {
        var resolution = VbaProjectResolver.Resolve(activeUri);
        var scopedDocuments = documents
            .Where(pair => resolution.ContainsUri(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        if (!scopedDocuments.ContainsKey(activeUri) && documents.TryGetValue(activeUri, out var activeText))
        {
            scopedDocuments[activeUri] = activeText;
        }

        var referenceSelection =
            resolution.Kind == VbaProjectResolutionKind.ManifestDocument
            && !string.IsNullOrEmpty(resolution.DocumentKind)
                ? VbaProjectReferenceSelection.Create(
                    resolution.DocumentKind,
                    resolution.ReferenceEntries)
                : null;
        var sourceIndex = VbaSourceIndex.Build(
            scopedDocuments,
            referenceSelection,
            referenceCatalogCache.Current);

        return new VbaProjectSnapshot(
            resolution,
            scopedDocuments,
            referenceSelection,
            sourceIndex);
    }

    public IReadOnlyList<VbaProjectSnapshot> CreateProjectSnapshots()
    {
        var snapshots = new List<VbaProjectSnapshot>();
        var seenScopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var uri in documents.Keys)
        {
            var snapshot = CreateProjectSnapshot(uri);
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
