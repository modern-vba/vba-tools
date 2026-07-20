using System.Security.Cryptography;
using System.Text;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.Syntax;

namespace VbaLanguageServer.Workspace;

/// <summary>
/// Identifies decoded disk content without exposing how that identity is calculated.
/// </summary>
internal sealed class VbaProjectDiskContentIdentity
    : IEquatable<VbaProjectDiskContentIdentity>
{
    private readonly string digest;

    private VbaProjectDiskContentIdentity(string digest)
    {
        this.digest = digest;
    }

    public bool Equals(VbaProjectDiskContentIdentity? other)
        => other is not null
            && digest.Equals(other.digest, StringComparison.Ordinal);

    public override bool Equals(object? obj)
        => obj is VbaProjectDiskContentIdentity other
            && Equals(other);

    public override int GetHashCode()
        => StringComparer.Ordinal.GetHashCode(digest);

    internal static VbaProjectDiskContentIdentity FromText(string text)
        => new(Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(text))));
}

/// <summary>
/// Represents one syntax-free disk source fact captured by the project inventory.
/// </summary>
internal sealed record VbaProjectDiskSource(
    string Uri,
    string FullPath,
    string Text,
    VbaProjectSourceFileMetadata Metadata,
    VbaProjectDiskContentIdentity ContentIdentity);

/// <summary>
/// Represents one disk source from the immutable project snapshot used as a scan baseline.
/// </summary>
internal sealed record VbaProjectDiskKnownSource(
    string Uri,
    string FullPath,
    string Text,
    VbaProjectDiskContentIdentity ContentIdentity);

/// <summary>
/// Represents syntax-free disk facts captured for cold snapshot materialization.
/// </summary>
internal sealed record VbaProjectDiskColdSourceCapture(
    IReadOnlyList<VbaProjectDiskSource> Sources,
    IReadOnlySet<string> OwnedCandidateSourcePaths);

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
/// Represents syntax-free disk facts captured for one project reconciliation pass.
/// </summary>
internal sealed record VbaProjectDiskObservation(
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
/// Represents the project ownership facts needed for one disk observation.
/// </summary>
internal sealed record VbaProjectDiskProjectScope(
    VbaProjectResolutionKind Kind,
    string RootPath,
    string? OwningManifestPath);

/// <summary>
/// Represents one ordered manifest probe needed for a disk observation.
/// </summary>
internal sealed record VbaProjectDiskManifestProbe(
    string Uri,
    bool ExistedInBaseline);

/// <summary>
/// Represents one captured manifest-barrier override used during disk ownership checks.
/// </summary>
internal sealed record VbaProjectDiskManifestBarrierOverride(
    string Path,
    bool IsBarrier);

/// <summary>
/// Contains only the disk facts needed for one reconciliation observation.
/// </summary>
internal sealed class VbaProjectDiskObservationRequest
{
    public VbaProjectDiskObservationRequest(
        VbaProjectDiskProjectScope project,
        IReadOnlyList<VbaProjectDiskManifestProbe> manifestCandidates,
        IReadOnlyList<VbaProjectDiskManifestBarrierOverride> barrierOverrides,
        IReadOnlyList<string> observedManifestBarrierUris)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(manifestCandidates);
        ArgumentNullException.ThrowIfNull(barrierOverrides);
        ArgumentNullException.ThrowIfNull(observedManifestBarrierUris);
        Project = project;
        ManifestCandidates = Array.AsReadOnly(manifestCandidates.ToArray());
        BarrierOverrides = Array.AsReadOnly(barrierOverrides.ToArray());
        ObservedManifestBarrierUris = Array.AsReadOnly(
            observedManifestBarrierUris.ToArray());
    }

    public VbaProjectDiskProjectScope Project { get; }

    public IReadOnlyList<VbaProjectDiskManifestProbe> ManifestCandidates
        { get; }

    public IReadOnlyList<VbaProjectDiskManifestBarrierOverride> BarrierOverrides
        { get; }

    public IReadOnlyList<string> ObservedManifestBarrierUris { get; }
}

/// <summary>
/// Observes syntax-free project facts for background reconciliation.
/// </summary>
internal interface IVbaProjectDiskObservationSource
{
    Task<VbaProjectDiskObservation> ObserveReconciliationAsync(
        VbaProjectDiskObservationRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Owns project disk enumeration, source identity, stable reads, decoding,
/// nested-manifest ownership, and manifest probes.
/// </summary>
internal interface IVbaProjectDiskInventory : IVbaProjectDiskObservationSource
{
    VbaProjectDiskColdSourceCapture CaptureColdSources(
        VbaProjectResolution resolution,
        IReadOnlyCollection<string> candidateSourceUris,
        IReadOnlySet<string> excludedSourceUris,
        IReadOnlyDictionary<string, bool> manifestBarrierOverrides,
        CancellationToken cancellationToken);

    VbaProjectDiskSource? CaptureWatchedSource(
        VbaProjectResolution resolution,
        string sourceUri,
        IReadOnlyDictionary<string, bool> manifestBarrierOverrides,
        CancellationToken cancellationToken);

    void InvalidateSource(string localPath);
}

/// <summary>
/// Captures project disk facts through one shared filesystem adapter.
/// </summary>
internal sealed class VbaFileSystemProjectDiskInventory
    : IVbaProjectDiskInventory
{
    private const int MaxStableReadAttempts = 3;
    private static readonly string[] SourcePatterns = ["*.bas", "*.cls", "*.frm"];
    private readonly object gate = new();
    private readonly IVbaProjectFileSystem fileSystem;
    private readonly Dictionary<string, CachedSource> sourceCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> activeLoads =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> publicationGenerations =
        new(StringComparer.OrdinalIgnoreCase);

    public VbaFileSystemProjectDiskInventory()
        : this(SystemVbaProjectFileSystem.Instance)
    {
    }

    internal VbaFileSystemProjectDiskInventory(
        IVbaProjectFileSystem fileSystem)
    {
        this.fileSystem = fileSystem;
    }

    internal int Count
    {
        get
        {
            lock (gate)
            {
                return sourceCache.Count;
            }
        }
    }

    public VbaProjectDiskColdSourceCapture CaptureColdSources(
        VbaProjectResolution resolution,
        IReadOnlyCollection<string> candidateSourceUris,
        IReadOnlySet<string> excludedSourceUris,
        IReadOnlyDictionary<string, bool> manifestBarrierOverrides,
        CancellationToken cancellationToken)
    {
        var excludedPaths = CreateLocalPathSet(excludedSourceUris);
        var ownership = new SourceOwnership(
            resolution,
            fileSystem,
            manifestBarrierOverrides);
        var sources = EnumerateSourcePaths(resolution, cancellationToken)
            .Where(ownership.ContainsSource)
            .Where(path => !excludedPaths.Contains(path))
            .Select(
                path => TryCaptureSource(
                    path,
                    forceStableRead: false,
                    cancellationToken,
                    out var source)
                        ? source
                        : null)
            .Where(source => source is not null)
            .Select(source => source!)
            .ToArray();
        var ownedCandidatePaths = candidateSourceUris
            .Select(VbaProjectResolver.TryGetLocalPath)
            .Where(path => path is not null)
            .Select(path => Path.GetFullPath(path!))
            .Where(ownership.ContainsSource)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new VbaProjectDiskColdSourceCapture(
            sources,
            ownedCandidatePaths);
    }

    public VbaProjectDiskSource? CaptureWatchedSource(
        VbaProjectResolution resolution,
        string sourceUri,
        IReadOnlyDictionary<string, bool> manifestBarrierOverrides,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var localPath = VbaProjectResolver.TryGetLocalPath(sourceUri);
        if (localPath is null)
        {
            return null;
        }

        var fullPath = Path.GetFullPath(localPath);
        var ownership = new SourceOwnership(
            resolution,
            fileSystem,
            manifestBarrierOverrides);
        if (!ownership.ContainsSource(fullPath))
        {
            return null;
        }

        InvalidateSource(fullPath);
        return TryCaptureSource(
            fullPath,
            forceStableRead: true,
            cancellationToken,
            out var source)
                ? source
                : null;
    }

    public Task<VbaProjectDiskObservation> ObserveReconciliationAsync(
        VbaProjectDiskObservationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var observation = PrepareObservation(request);
        return Task.Run(
            () => ObserveReconciliation(observation, cancellationToken),
            cancellationToken);
    }

    public void InvalidateSource(string localPath)
    {
        var fullPath = Path.GetFullPath(localPath);
        lock (gate)
        {
            sourceCache.Remove(fullPath);
            if (activeLoads.ContainsKey(fullPath))
            {
                publicationGenerations.TryGetValue(
                    fullPath,
                    out var previousGeneration);
                publicationGenerations[fullPath] =
                    previousGeneration + 1;
            }
            else
            {
                publicationGenerations.Remove(fullPath);
            }
        }
    }

    private static PreparedDiskObservation PrepareObservation(
        VbaProjectDiskObservationRequest request)
    {
        var manifestBarrierOverrides = new Dictionary<string, bool>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var barrierOverride in request.BarrierOverrides)
        {
            var path = string.IsNullOrWhiteSpace(barrierOverride.Path)
                ? barrierOverride.Path
                : Path.GetFullPath(barrierOverride.Path);
            manifestBarrierOverrides[path] = barrierOverride.IsBarrier;
        }

        return new PreparedDiskObservation(
            request.Project,
            request.ManifestCandidates.ToArray(),
            manifestBarrierOverrides,
            request.ObservedManifestBarrierUris.ToArray());
    }

    private VbaProjectDiskObservation ObserveReconciliation(
        PreparedDiskObservation observation,
        CancellationToken cancellationToken)
    {
        var sources = new List<VbaProjectDiskSource>();
        var existingNonOwnedSourcePaths = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var observedManifestBarrierPaths = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var missingObservedManifestBarrierUris =
            new List<string>();
        if (!string.IsNullOrWhiteSpace(observation.Project.RootPath)
            && fileSystem.DirectoryExists(observation.Project.RootPath))
        {
            var ownership = new SourceOwnership(
                observation.Project,
                fileSystem,
                observation.ManifestBarrierOverrides);
            foreach (var fullPath in EnumerateSourcePaths(
                observation.Project,
                cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ownership.ContainsSource(fullPath))
                {
                    existingNonOwnedSourcePaths.Add(fullPath);
                    continue;
                }

                if (TryCaptureSource(
                    fullPath,
                    forceStableRead: true,
                    cancellationToken,
                    out var source))
                {
                    sources.Add(source);
                }
            }

            observedManifestBarrierPaths.UnionWith(
                ownership.ObservedManifestBarrierPaths);
        }

        foreach (var uri in observation.ObservedManifestBarrierUris)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = VbaProjectResolver.TryGetLocalPath(uri);
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
                        uri);
                }
            }
        }

        VbaProjectDiskManifest? manifest = null;
        foreach (var candidate in observation.ManifestCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifestPath = VbaProjectResolver.TryGetLocalPath(candidate.Uri);
            if (manifestPath is null
                || IsKnownInvalidBarrier(observation, manifestPath)
                || !fileSystem.FileExists(manifestPath))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(manifestPath);
            cancellationToken.ThrowIfCancellationRequested();
            manifest = new VbaProjectDiskManifest(
                new Uri(fullPath).AbsoluteUri,
                fullPath,
                fileSystem.ReadManifestText(fullPath));
            break;
        }

        var observedManifestBarriers =
            new List<VbaProjectDiskManifest>();
        foreach (var path in observedManifestBarrierPaths.OrderBy(
            path => path,
            StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!fileSystem.FileExists(path))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            observedManifestBarriers.Add(
                new VbaProjectDiskManifest(
                    new Uri(path).AbsoluteUri,
                    path,
                    fileSystem.ReadManifestText(path)));
        }

        return new VbaProjectDiskObservation(sources, manifest)
        {
            ExistingNonOwnedSourcePaths = existingNonOwnedSourcePaths,
            ObservedManifestBarriers = observedManifestBarriers.ToArray(),
            MissingObservedManifestBarrierUris =
                missingObservedManifestBarrierUris
        };
    }

    private IEnumerable<string> EnumerateSourcePaths(
        VbaProjectResolution resolution,
        CancellationToken cancellationToken)
        => EnumerateSourcePaths(
            new VbaProjectDiskProjectScope(
                resolution.Kind,
                resolution.RootPath,
                resolution.ManifestPath),
            cancellationToken);

    private IEnumerable<string> EnumerateSourcePaths(
        VbaProjectDiskProjectScope project,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(project.RootPath)
            || !fileSystem.DirectoryExists(project.RootPath))
        {
            return [];
        }

        var searchOption =
            project.Kind == VbaProjectResolutionKind.ManifestDocument
                ? SearchOption.AllDirectories
                : SearchOption.TopDirectoryOnly;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pattern in SourcePatterns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var path in fileSystem.EnumerateSourceFiles(
                project.RootPath,
                pattern,
                searchOption))
            {
                cancellationToken.ThrowIfCancellationRequested();
                paths.Add(Path.GetFullPath(path));
            }
        }

        return paths.OrderBy(
            path => path,
            StringComparer.OrdinalIgnoreCase);
    }

    private bool TryCaptureSource(
        string localPath,
        bool forceStableRead,
        CancellationToken cancellationToken,
        out VbaProjectDiskSource source)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(localPath);
        if (!fileSystem.TryGetSourceMetadata(fullPath, out var metadata))
        {
            source = null!;
            return false;
        }

        long capturedPublicationGeneration;
        lock (gate)
        {
            if (!forceStableRead
                && sourceCache.TryGetValue(fullPath, out var cached)
                && cached.Metadata == metadata)
            {
                source = CreateSource(fullPath, cached);
                return true;
            }

            activeLoads.TryGetValue(fullPath, out var activeLoadCount);
            activeLoads[fullPath] = activeLoadCount + 1;
            publicationGenerations.TryGetValue(
                fullPath,
                out var previousPublicationGeneration);
            capturedPublicationGeneration =
                previousPublicationGeneration + 1;
            publicationGenerations[fullPath] =
                capturedPublicationGeneration;
        }

        try
        {
            for (var attempt = 0;
                attempt < MaxStableReadAttempts;
                attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!forceStableRead && attempt > 0)
                {
                    lock (gate)
                    {
                        if (sourceCache.TryGetValue(
                                fullPath,
                                out var retriedCached)
                            && retriedCached.Metadata == metadata)
                        {
                            source = CreateSource(
                                fullPath,
                                retriedCached);
                            return true;
                        }
                    }
                }

                byte[] sourceBytes;
                try
                {
                    sourceBytes = fileSystem.ReadSourceBytes(fullPath);
                }
                catch (FileNotFoundException)
                {
                    source = null!;
                    return false;
                }
                catch (DirectoryNotFoundException)
                {
                    source = null!;
                    return false;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (!fileSystem.TryGetSourceMetadata(
                    fullPath,
                    out var loadedMetadata))
                {
                    source = null!;
                    return false;
                }

                if (loadedMetadata != metadata)
                {
                    metadata = loadedMetadata;
                    continue;
                }

                var text = VbaSourceFileTextReader.Decode(sourceBytes);
                CachedSource loaded;
                lock (gate)
                {
                    var identity =
                        sourceCache.TryGetValue(
                            fullPath,
                            out var existing)
                        && existing.Text.Equals(
                            text,
                            StringComparison.Ordinal)
                            ? existing.ContentIdentity
                            : VbaProjectDiskContentIdentity.FromText(text);
                    loaded = new CachedSource(
                        loadedMetadata,
                        text,
                        identity);
                    publicationGenerations.TryGetValue(
                        fullPath,
                        out var currentPublicationGeneration);
                    if (currentPublicationGeneration
                        == capturedPublicationGeneration)
                    {
                        sourceCache[fullPath] = loaded;
                    }
                }

                source = CreateSource(fullPath, loaded);
                return true;
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
                    publicationGenerations.Remove(fullPath);
                }
                else
                {
                    activeLoads[fullPath] = remainingLoadCount;
                }
            }
        }
    }

    private static VbaProjectDiskSource CreateSource(
        string fullPath,
        CachedSource cached)
        => new(
            new Uri(fullPath).AbsoluteUri,
            fullPath,
            cached.Text,
            cached.Metadata,
            cached.ContentIdentity);

    private static HashSet<string> CreateLocalPathSet(
        IEnumerable<string> uris)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var uri in uris)
        {
            var localPath = VbaProjectResolver.TryGetLocalPath(uri);
            if (localPath is not null)
            {
                paths.Add(Path.GetFullPath(localPath));
            }
        }

        return paths;
    }

    private static bool IsKnownInvalidBarrier(
        PreparedDiskObservation observation,
        string manifestPath)
    {
        var fullManifestPath = Path.GetFullPath(manifestPath);
        var candidate = observation.ManifestCandidates.FirstOrDefault(
            candidate =>
                VbaProjectResolver.TryGetLocalPath(candidate.Uri)
                    is { } candidatePath
                && Path.GetFullPath(candidatePath).Equals(
                    fullManifestPath,
                    StringComparison.OrdinalIgnoreCase));
        return candidate?.ExistedInBaseline == true
            && observation.ManifestBarrierOverrides.TryGetValue(
                fullManifestPath,
                out var isBarrier)
            && !isBarrier;
    }

    private sealed record PreparedDiskObservation(
        VbaProjectDiskProjectScope Project,
        IReadOnlyList<VbaProjectDiskManifestProbe> ManifestCandidates,
        IReadOnlyDictionary<string, bool> ManifestBarrierOverrides,
        IReadOnlyList<string> ObservedManifestBarrierUris);

    private sealed record CachedSource(
        VbaProjectSourceFileMetadata Metadata,
        string Text,
        VbaProjectDiskContentIdentity ContentIdentity);

    /// <summary>
    /// Determines whether exported sources remain owned by one resolved project
    /// rather than a descendant project manifest.
    /// </summary>
    private sealed class SourceOwnership
    {
        private const string ManifestFileName = "vba-project.json";
        private readonly VbaProjectResolutionKind kind;
        private readonly IVbaProjectFileSystem fileSystem;
        private readonly string rootPath;
        private readonly string? authorityManifestPath;
        private readonly IReadOnlyDictionary<string, bool>
            manifestBarrierOverrides;
        private readonly Dictionary<string, bool> ownedDirectories =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> observedManifestBarrierPaths =
            new(StringComparer.OrdinalIgnoreCase);

        public SourceOwnership(
            VbaProjectResolution resolution,
            IVbaProjectFileSystem fileSystem,
            IReadOnlyDictionary<string, bool>? manifestBarrierOverrides = null)
            : this(
                new VbaProjectDiskProjectScope(
                    resolution.Kind,
                    resolution.RootPath,
                    resolution.ManifestPath),
                fileSystem,
                manifestBarrierOverrides)
        {
        }

        public SourceOwnership(
            VbaProjectDiskProjectScope project,
            IVbaProjectFileSystem fileSystem,
            IReadOnlyDictionary<string, bool>? manifestBarrierOverrides = null)
        {
            kind = project.Kind;
            this.fileSystem = fileSystem;
            rootPath = NormalizePath(project.RootPath);
            authorityManifestPath = string.IsNullOrWhiteSpace(
                    project.OwningManifestPath)
                ? null
                : NormalizePath(project.OwningManifestPath);
            this.manifestBarrierOverrides =
                manifestBarrierOverrides
                ?? new Dictionary<string, bool>(
                    StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyCollection<string> ObservedManifestBarrierPaths
            => observedManifestBarrierPaths;

        public bool ContainsSource(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(rootPath)
                || string.IsNullOrWhiteSpace(sourcePath))
            {
                return false;
            }

            var fullPath = Path.GetFullPath(sourcePath);
            if (kind == VbaProjectResolutionKind.AdHoc)
            {
                return VbaProjectResolver.SameDirectory(
                    fullPath,
                    rootPath);
            }

            if (!VbaProjectResolver.IsPathUnder(fullPath, rootPath))
            {
                return false;
            }

            var sourceDirectory = Path.GetDirectoryName(fullPath);
            return sourceDirectory is not null
                && IsOwnedDirectory(sourceDirectory);
        }

        private bool IsOwnedDirectory(string directoryPath)
        {
            var fullDirectoryPath = NormalizePath(directoryPath);
            if (ownedDirectories.TryGetValue(
                    fullDirectoryPath,
                    out var isOwned))
            {
                return isOwned;
            }

            if (!SamePath(fullDirectoryPath, rootPath)
                && !VbaProjectResolver.IsPathUnder(
                    fullDirectoryPath,
                    rootPath))
            {
                ownedDirectories[fullDirectoryPath] = false;
                return false;
            }

            var parentOwned = SamePath(fullDirectoryPath, rootPath);
            if (!parentOwned)
            {
                var parentPath = Path.GetDirectoryName(fullDirectoryPath);
                parentOwned = parentPath is not null
                    && IsOwnedDirectory(parentPath);
            }

            var candidateManifestPath = Path.Combine(
                fullDirectoryPath,
                ManifestFileName);
            var hasOverride =
                manifestBarrierOverrides.TryGetValue(
                    candidateManifestPath,
                    out var barrierOverride);
            var hasManifestBarrier = hasOverride
                    ? barrierOverride
                    : fileSystem.FileExists(candidateManifestPath);
            if (!hasOverride
                && hasManifestBarrier
                && !SamePath(
                    candidateManifestPath,
                    authorityManifestPath))
            {
                observedManifestBarrierPaths.Add(
                    Path.GetFullPath(candidateManifestPath));
            }

            isOwned = parentOwned
                && (SamePath(candidateManifestPath, authorityManifestPath)
                    || !hasManifestBarrier);
            ownedDirectories[fullDirectoryPath] = isOwned;
            return isOwned;
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "";
            }

            var fullPath = Path.GetFullPath(path);
            var pathRoot = Path.GetPathRoot(fullPath);
            return pathRoot is not null
                && fullPath.Equals(
                    pathRoot,
                    StringComparison.OrdinalIgnoreCase)
                    ? pathRoot
                    : fullPath.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar);
        }

        private static bool SamePath(string left, string? right)
            => right is not null
                && left.Equals(
                    right,
                    StringComparison.OrdinalIgnoreCase);
    }
}
