using VbaLanguageServer.ProjectModel;

namespace VbaLanguageServer.Workspace;

/// <summary>
/// Represents one disk source captured by a reconciliation scan.
/// </summary>
internal sealed record VbaProjectDiskSource(
    string Uri,
    string FullPath,
    string Text);

/// <summary>
/// Represents one disk source from the immutable project snapshot used as a scan baseline.
/// </summary>
internal sealed record VbaProjectDiskKnownSource(
    string Uri,
    string FullPath,
    string Text);

/// <summary>
/// Represents the optional disk manifest captured with one project scan.
/// </summary>
internal sealed record VbaProjectDiskManifest(
    string Uri,
    string FullPath,
    string Text);

/// <summary>
/// Represents the last accepted disk-manifest content for one candidate path.
/// </summary>
internal sealed record VbaProjectDiskManifestBaseline(
    bool Exists,
    string? Text);

/// <summary>
/// Represents one manifest path that may own an activated source URI.
/// </summary>
internal sealed record VbaProjectDiskManifestCandidate(
    string Uri,
    long CapturedRevision,
    VbaProjectDiskManifestBaseline Baseline)
{
    public bool HasOpenOverlay { get; init; }

    public string? OpenOverlayText { get; init; }

    public string? EffectiveManifestText { get; init; }
}

/// <summary>
/// Represents all disk inputs captured for one project reconciliation pass.
/// </summary>
internal sealed record VbaProjectDiskScopeScan(
    IReadOnlyList<VbaProjectDiskSource> Sources,
    VbaProjectDiskManifest? Manifest)
{
    /// <summary>
    /// Gets source paths that still exist below the scanned root but are now
    /// owned by a descendant project manifest.
    /// </summary>
    public IReadOnlySet<string> ExistingNonOwnedSourcePaths { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<VbaProjectDiskManifest>
        ObservedManifestBarriers { get; init; } = [];

    public IReadOnlyList<string>
        MissingObservedManifestBarrierUris { get; init; } = [];
}

/// <summary>
/// Represents an activated project scope captured before background disk work starts.
/// </summary>
internal sealed record VbaProjectDiskReconciliationScope(
    string AuthorityKey,
    string ActiveUri,
    VbaProjectResolution Resolution,
    long CapturedWorkspaceRevision,
    IReadOnlyList<VbaProjectDiskManifestCandidate> ManifestCandidates,
    IReadOnlyList<VbaProjectDiskKnownSource> KnownSources)
{
    /// <summary>
    /// Gets the manifest-barrier snapshot that owns this scan.
    /// </summary>
    public VbaProjectManifestBarrierSnapshot ManifestBarriers { get; init; } =
        new(
            Revision: 0,
            new Dictionary<string, bool>(
                StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Gets the structural incarnation of the captured reconciliation authority.
    /// </summary>
    public long AuthorityGeneration { get; init; }

    public IReadOnlyList<VbaProjectDiskManifestCandidate>
        ObservedManifestBarrierCandidates { get; init; } = [];

    public IReadOnlyList<string> OpenSourceUris { get; init; } = [];

    public IReadOnlyList<string> OpenDocumentUris { get; init; } = [];
}

/// <summary>
/// Owns one reconciliation scope capture and its source-revision watermark.
/// </summary>
internal sealed class VbaProjectDiskReconciliationCapture : IDisposable
{
    private IDisposable? revisionCapture;

    public VbaProjectDiskReconciliationCapture(
        IReadOnlyList<VbaProjectDiskReconciliationScope> scopes,
        IDisposable revisionCapture)
    {
        Scopes = scopes;
        this.revisionCapture = revisionCapture;
    }

    public IReadOnlyList<VbaProjectDiskReconciliationScope> Scopes { get; }

    public void Dispose()
        => Interlocked.Exchange(ref revisionCapture, null)?.Dispose();
}

/// <summary>
/// Isolates project disk enumeration and reads from warm interactive workspace requests.
/// </summary>
internal interface IVbaProjectDiskSourceBoundary
{
    Task<VbaProjectDiskScopeScan> ScanAsync(
        VbaProjectDiskReconciliationScope scope,
        CancellationToken cancellationToken);
}

/// <summary>
/// Reads VBA source files for one already-activated project scope.
/// </summary>
internal sealed class VbaFileSystemProjectDiskSourceBoundary
    : IVbaProjectDiskSourceBoundary
{
    private static readonly string[] SourcePatterns = ["*.bas", "*.cls", "*.frm"];
    private readonly IVbaProjectFileSystem fileSystem;

    public VbaFileSystemProjectDiskSourceBoundary()
        : this(SystemVbaProjectFileSystem.Instance)
    {
    }

    internal VbaFileSystemProjectDiskSourceBoundary(
        IVbaProjectFileSystem fileSystem)
    {
        this.fileSystem = fileSystem;
    }

    public Task<VbaProjectDiskScopeScan> ScanAsync(
        VbaProjectDiskReconciliationScope scope,
        CancellationToken cancellationToken)
        => Task.Run(
            () => Scan(scope, cancellationToken),
            cancellationToken);

    private VbaProjectDiskScopeScan Scan(
        VbaProjectDiskReconciliationScope scope,
        CancellationToken cancellationToken)
    {
        var sources = new List<VbaProjectDiskSource>();
        var existingNonOwnedSourcePaths = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var observedManifestBarrierPaths = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var missingObservedManifestBarrierUris =
            new List<string>();
        if (!string.IsNullOrWhiteSpace(scope.Resolution.RootPath)
            && fileSystem.DirectoryExists(scope.Resolution.RootPath))
        {
            var ownershipBoundary = new VbaProjectSourceOwnershipBoundary(
                scope.Resolution,
                fileSystem,
                scope.ManifestBarriers.Overrides);
            var searchOption =
                scope.Resolution.Kind == VbaProjectResolutionKind.ManifestDocument
                    ? SearchOption.AllDirectories
                    : SearchOption.TopDirectoryOnly;
            foreach (var pattern in SourcePatterns)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var path in fileSystem.EnumerateSourceFiles(
                    scope.Resolution.RootPath,
                    pattern,
                    searchOption))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fullPath = Path.GetFullPath(path);
                    if (!ownershipBoundary.ContainsSource(fullPath))
                    {
                        existingNonOwnedSourcePaths.Add(fullPath);
                        continue;
                    }

                    sources.Add(new VbaProjectDiskSource(
                        new Uri(fullPath).AbsoluteUri,
                        fullPath,
                        VbaSourceFileTextReader.Decode(
                            fileSystem.ReadSourceBytes(fullPath))));
                }
            }

            observedManifestBarrierPaths.UnionWith(
                ownershipBoundary.ObservedManifestBarrierPaths);
        }

        foreach (var candidate in
            scope.ObservedManifestBarrierCandidates)
        {
            var path =
                VbaProjectResolver.TryGetLocalPath(candidate.Uri);
            if (path is not null)
            {
                var fullPath = Path.GetFullPath(path);
                if (fileSystem.FileExists(fullPath))
                {
                    observedManifestBarrierPaths.Add(fullPath);
                }
                else
                {
                    missingObservedManifestBarrierUris.Add(
                        candidate.Uri);
                }
            }
        }

        VbaProjectDiskManifest? manifest = null;
        foreach (var candidate in scope.ManifestCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifestPath = VbaProjectResolver.TryGetLocalPath(candidate.Uri);
            if (manifestPath is null
                || IsKnownInvalidBarrier(scope, manifestPath)
                || !fileSystem.FileExists(manifestPath))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(manifestPath);
            manifest = new VbaProjectDiskManifest(
                new Uri(fullPath).AbsoluteUri,
                fullPath,
                fileSystem.ReadManifestText(fullPath));
            break;
        }

        var observedManifestBarriers =
            observedManifestBarrierPaths
                .Where(fileSystem.FileExists)
                .OrderBy(
                    path => path,
                    StringComparer.OrdinalIgnoreCase)
                .Select(
                    path => new VbaProjectDiskManifest(
                        new Uri(path).AbsoluteUri,
                        path,
                        fileSystem.ReadManifestText(path)))
                .ToArray();
        return new VbaProjectDiskScopeScan(sources, manifest)
        {
            ExistingNonOwnedSourcePaths = existingNonOwnedSourcePaths,
            ObservedManifestBarriers = observedManifestBarriers,
            MissingObservedManifestBarrierUris =
                missingObservedManifestBarrierUris
        };
    }

    private static bool IsKnownInvalidBarrier(
        VbaProjectDiskReconciliationScope scope,
        string manifestPath)
    {
        var fullManifestPath = Path.GetFullPath(manifestPath);
        var candidate = scope.ManifestCandidates.FirstOrDefault(
            candidate =>
                VbaProjectResolver.TryGetLocalPath(candidate.Uri)
                    is { } candidatePath
                && Path.GetFullPath(candidatePath).Equals(
                    fullManifestPath,
                    StringComparison.OrdinalIgnoreCase));
        return candidate?.Baseline.Exists == true
            && scope.ManifestBarriers.Overrides.TryGetValue(
                fullManifestPath,
                out var isBarrier)
            && !isBarrier;
    }
}
