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
/// <param name="LastMemberUpdate">The last safe ModuleMember update plan.</param>
/// <param name="SourceDocument">The projected source document, when already available.</param>
public sealed record VbaTrackedDocument(
    string Uri,
    string Text,
    VbaSyntaxTree SyntaxTree,
    VbaSyntaxTreeParseUpdateKind LastParseUpdateKind,
    VbaModuleMemberIncrementalUpdate? LastMemberUpdate = null,
    VbaSourceDocument? SourceDocument = null);

/// <summary>
/// Maintains open document text and creates project snapshots for language-server features.
/// </summary>
public sealed class VbaLanguageWorkspace
{
    private readonly object gate = new();
    private readonly Dictionary<string, WorkspaceDocumentState> documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> excludedSourceUris = new(StringComparer.OrdinalIgnoreCase);
    private readonly VbaProjectSourceDocumentCache diskDocumentCache = new();
    private readonly VbaProjectSnapshotProvider snapshotProvider;
    private long workspaceVersion;

    /// <summary>
    /// Creates a language workspace.
    /// </summary>
    /// <param name="referenceCatalogCache">The reference catalog cache used when building source indexes.</param>
    public VbaLanguageWorkspace(VbaProjectReferenceCatalogCache referenceCatalogCache)
    {
        ManifestWorkspace = new VbaProjectManifestWorkspace();
        snapshotProvider = new VbaProjectSnapshotProvider(
            referenceCatalogCache,
            diskDocumentCache,
            ManifestWorkspace);
    }

    /// <summary>
    /// Gets the focused manifest authority shared by snapshots, trace resolution, and lifecycle work.
    /// </summary>
    internal VbaProjectManifestWorkspace ManifestWorkspace { get; }

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
            var existing = GetDocumentState(uri);
            var version = existing?.Authority == WorkspaceDocumentAuthority.OpenBuffer
                ? (existing.Version ?? 0) + 1
                : 0;
            RemoveExcludedSourceIdentity(uri);
            return StoreDocument(uri, text, WorkspaceDocumentAuthority.OpenBuffer, version);
        }
    }

    /// <summary>
    /// Opens a versioned client document and makes its text authoritative over disk state.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="version">The client document version.</param>
    /// <param name="text">The complete document text.</param>
    /// <param name="cancellationToken">A cancellation token for the update.</param>
    /// <returns>The parse update kind for the opened source.</returns>
    public VbaSyntaxTreeParseUpdateKind OpenDocument(
        string uri,
        int version,
        string text,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            RemoveExcludedSourceIdentity(uri);
            return StoreDocument(uri, text, WorkspaceDocumentAuthority.OpenBuffer, version);
        }
    }

    /// <summary>
    /// Applies a client document change only when its version is newer than the open buffer.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="version">The client document version.</param>
    /// <param name="text">The complete document text.</param>
    /// <param name="cancellationToken">A cancellation token for the update.</param>
    /// <returns>The parse update kind, or null when the change was stale or the document was not open.</returns>
    public VbaSyntaxTreeParseUpdateKind? ChangeDocument(
        string uri,
        int version,
        string text,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var existing = GetDocumentState(uri);
            if (existing?.Authority != WorkspaceDocumentAuthority.OpenBuffer
                || version <= existing.Version)
            {
                return null;
            }

            return StoreDocument(
                existing.Document.Uri,
                text,
                WorkspaceDocumentAuthority.OpenBuffer,
                version);
        }
    }

    /// <summary>
    /// Reloads a watched disk source unless an open client buffer is authoritative.
    /// </summary>
    /// <param name="uri">The watched source URI.</param>
    /// <param name="text">The complete disk source text.</param>
    /// <param name="cancellationToken">A cancellation token for the reload.</param>
    /// <returns>True when disk text became the tracked source; false when an open buffer was preserved.</returns>
    public bool ReloadSourceDocument(
        string uri,
        string text,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InvalidateDiskDocument(uri);
        lock (gate)
        {
            var exclusionRemoved = RemoveExcludedSourceIdentity(uri);
            var existing = GetDocumentState(uri);
            if (existing?.Authority == WorkspaceDocumentAuthority.OpenBuffer)
            {
                if (exclusionRemoved)
                {
                    MarkWorkspaceChanged();
                }

                return false;
            }

            StoreDocument(uri, text, WorkspaceDocumentAuthority.DiskWatcher, version: null);
            return true;
        }
    }

    /// <summary>
    /// Closes an open client buffer so later snapshots can fall back to disk state.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="cancellationToken">A cancellation token for the close.</param>
    /// <returns>True when an open buffer was removed.</returns>
    public bool CloseDocument(string uri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InvalidateDiskDocument(uri);
        lock (gate)
        {
            var key = FindDocumentKey(uri);
            if (key is null
                || documents[key].Authority != WorkspaceDocumentAuthority.OpenBuffer)
            {
                return false;
            }

            documents.Remove(key);
            MarkWorkspaceChanged();
            return true;
        }
    }

    /// <summary>
    /// Excludes a deleted disk source while preserving an equivalent open client buffer.
    /// </summary>
    /// <param name="uri">The deleted source URI.</param>
    /// <param name="cancellationToken">A cancellation token for the deletion.</param>
    /// <returns>True when no open buffer remains and diagnostics should be cleared.</returns>
    public bool DeleteSourceDocument(string uri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InvalidateDiskDocument(uri);
        lock (gate)
        {
            var exclusionAdded = AddExcludedSourceIdentity(uri);
            var key = FindDocumentKey(uri);
            if (key is not null
                && documents[key].Authority == WorkspaceDocumentAuthority.OpenBuffer)
            {
                if (exclusionAdded)
                {
                    MarkWorkspaceChanged();
                }

                return false;
            }

            var removed = key is not null && documents.Remove(key);
            if (exclusionAdded || removed)
            {
                MarkWorkspaceChanged();
            }

            return true;
        }
    }

    /// <summary>
    /// Removes any tracked document without excluding it from future disk inventory.
    /// </summary>
    /// <param name="uri">The document URI to remove.</param>
    /// <param name="cancellationToken">A cancellation token for the removal.</param>
    /// <returns>True when a tracked document was removed.</returns>
    public bool RemoveDocument(string uri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var key = FindDocumentKey(uri);
            var removed = key is not null && documents.Remove(key);
            if (removed)
            {
                MarkWorkspaceChanged();
            }

            return removed;
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
            var document = GetDocumentState(uri)?.Document;
            return document is not null
                ? document.SyntaxTree
                : null;
        }
    }

    /// <summary>
    /// Gets the effective tracked text for a source identity.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="cancellationToken">A cancellation token for the lookup.</param>
    /// <returns>The tracked text, or null when the source is not tracked.</returns>
    public string? GetDocumentText(string uri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return GetDocumentState(uri)?.Document.Text;
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
            return documents.Values
                .Select(state => state.Document.Uri)
                .ToArray();
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
        var workspaceState = CopyWorkspaceState();
        return snapshotProvider.CreateProjectSnapshot(
            activeUri,
            workspaceState,
            cancellationToken);
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

    private VbaWorkspaceSnapshotState CopyWorkspaceState()
    {
        lock (gate)
        {
            return new VbaWorkspaceSnapshotState(
                documents.Values
                    .Where(state => state.Authority == WorkspaceDocumentAuthority.OpenBuffer)
                    .ToDictionary(
                        state => state.Document.Uri,
                        state => state.Document,
                        StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(excludedSourceUris, StringComparer.OrdinalIgnoreCase),
                workspaceVersion);
        }
    }

    private VbaSyntaxTreeParseUpdateKind StoreDocument(
        string uri,
        string text,
        WorkspaceDocumentAuthority authority,
        int? version)
    {
        var existingKey = FindDocumentKey(uri);
        var previousDocument = existingKey is null
            ? null
            : documents[existingKey].Document;
        var parseResult = VbaSyntaxTree.ParseOrUpdate(uri, text, previousDocument?.SyntaxTree);
        if (existingKey is not null)
        {
            documents.Remove(existingKey);
        }

        var syntaxTree = parseResult.SyntaxTree;
        documents[uri] = new WorkspaceDocumentState(
            new VbaTrackedDocument(
                uri,
                text,
                syntaxTree,
                parseResult.UpdateKind,
                parseResult.MemberUpdate,
                VbaSourceIndex.CreateDocument(uri, syntaxTree)),
            authority,
            version);
        MarkWorkspaceChanged();
        return parseResult.UpdateKind;
    }

    private WorkspaceDocumentState? GetDocumentState(string uri)
    {
        var key = FindDocumentKey(uri);
        return key is null ? null : documents[key];
    }

    private string? FindDocumentKey(string uri)
    {
        if (documents.ContainsKey(uri))
        {
            return uri;
        }

        return documents.Keys.FirstOrDefault(candidate => SameDocumentIdentity(candidate, uri));
    }

    private bool AddExcludedSourceIdentity(string uri)
    {
        if (excludedSourceUris.Any(candidate => SameDocumentIdentity(candidate, uri)))
        {
            return false;
        }

        return excludedSourceUris.Add(uri);
    }

    private bool RemoveExcludedSourceIdentity(string uri)
        => excludedSourceUris.RemoveWhere(candidate => SameDocumentIdentity(candidate, uri)) > 0;

    private static bool SameDocumentIdentity(string leftUri, string rightUri)
    {
        if (leftUri.Equals(rightUri, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var leftPath = VbaProjectResolver.TryGetLocalPath(leftUri);
        var rightPath = VbaProjectResolver.TryGetLocalPath(rightUri);
        return leftPath is not null
            && rightPath is not null
            && leftPath.Equals(rightPath, StringComparison.OrdinalIgnoreCase);
    }

    private void InvalidateDiskDocument(string uri)
    {
        var localPath = VbaProjectResolver.TryGetLocalPath(uri);
        if (localPath is not null)
        {
            diskDocumentCache.Invalidate(localPath);
        }
    }

    private void MarkWorkspaceChanged()
    {
        workspaceVersion++;
        snapshotProvider.Invalidate();
    }

    private enum WorkspaceDocumentAuthority
    {
        OpenBuffer,
        DiskWatcher
    }

    private sealed record WorkspaceDocumentState(
        VbaTrackedDocument Document,
        WorkspaceDocumentAuthority Authority,
        int? Version);
}
