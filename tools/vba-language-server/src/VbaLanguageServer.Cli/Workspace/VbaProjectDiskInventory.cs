using System.Security.Cryptography;
using System.Text;
using VbaLanguageServer.ProjectModel;

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
    VbaDocumentIdentity DocumentIdentity,
    string Uri,
    string FullPath,
    string Text,
    VbaProjectSourceFileMetadata Metadata,
    VbaProjectDiskContentIdentity ContentIdentity,
    string RawContentDigest);

/// <summary>
/// Represents one closed source that could not be decoded without substitution.
/// </summary>
internal sealed record VbaProjectDiskSourceFailure(
    VbaDocumentIdentity DocumentIdentity,
    string Uri,
    string FullPath,
    VbaProjectSourceFileMetadata Metadata,
    string DiagnosticMessage);

/// <summary>
/// Represents one disk source from the immutable project snapshot used as a scan baseline.
/// </summary>
internal sealed record VbaProjectDiskKnownSource(
    VbaDocumentIdentity DocumentIdentity,
    string Uri,
    string FullPath,
    string Text,
    VbaProjectDiskContentIdentity ContentIdentity);

/// <summary>
/// Represents syntax-free disk facts captured for cold snapshot materialization.
/// </summary>
internal sealed record VbaProjectDiskColdSourceCapture(
    IReadOnlyList<VbaProjectDiskSource> Sources,
    IReadOnlyList<VbaProjectDiskSourceFailure> Failures,
    IReadOnlySet<VbaDocumentIdentity> OwnedCandidateSourceIdentities)
{
    public IReadOnlySet<VbaDocumentIdentity> ExistingCandidateSourceIdentities
    { get; init; } = new HashSet<VbaDocumentIdentity>();
}

/// <summary>
/// Represents the optional disk manifest captured with one project scan.
/// </summary>
internal sealed record VbaProjectDiskManifest(
    VbaDocumentIdentity DocumentIdentity,
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
    public IReadOnlyList<VbaProjectDiskSourceFailure> Failures { get; init; } = [];

    /// <summary>
    /// Gets source paths that still exist below the scanned root but are now
    /// owned by a descendant project manifest.
    /// </summary>
    public IReadOnlySet<VbaDocumentIdentity> ExistingNonOwnedSourceIdentities
    { get; init; } = new HashSet<VbaDocumentIdentity>();

    public IReadOnlyList<VbaProjectDiskManifest>
        ObservedManifestBarriers
    { get; init; } = [];

    public IReadOnlyList<VbaDocumentIdentity>
        MissingObservedManifestBarrierIdentities
    { get; init; } = [];
}

/// <summary>
/// Represents the project ownership facts needed for one disk observation.
/// </summary>
internal sealed record VbaProjectDiskProjectScope(
    VbaProjectAuthorityIdentity? AuthorityIdentity,
    VbaProjectResolutionKind Kind,
    string RootPath);

/// <summary>
/// Represents one ordered manifest probe needed for a disk observation.
/// </summary>
internal sealed record VbaProjectDiskManifestProbe(
    VbaDocumentIdentity DocumentIdentity,
    bool ExistedInBaseline);

/// <summary>
/// Represents one captured manifest-barrier override used during disk ownership checks.
/// </summary>
internal sealed record VbaProjectDiskManifestBarrierOverride(
    VbaDocumentIdentity DocumentIdentity,
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
        IReadOnlyList<VbaDocumentIdentity> observedManifestBarrierIdentities)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(manifestCandidates);
        ArgumentNullException.ThrowIfNull(barrierOverrides);
        ArgumentNullException.ThrowIfNull(observedManifestBarrierIdentities);
        Project = project;
        ManifestCandidates = Array.AsReadOnly(manifestCandidates.ToArray());
        BarrierOverrides = Array.AsReadOnly(barrierOverrides.ToArray());
        ObservedManifestBarrierIdentities = Array.AsReadOnly(
            observedManifestBarrierIdentities.ToArray());
    }

    public VbaProjectDiskProjectScope Project { get; }

    public IReadOnlyList<VbaProjectDiskManifestProbe> ManifestCandidates
    { get; }

    public IReadOnlyList<VbaProjectDiskManifestBarrierOverride> BarrierOverrides
    { get; }

    public IReadOnlyList<VbaDocumentIdentity>
        ObservedManifestBarrierIdentities
    { get; }

    public IReadOnlyList<VbaDocumentIdentity> OpenSourceIdentities
    { get; init; } = [];
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
/// Projects protocol and manifest path facts onto the shared document identity
/// model before they cross a disk-cache or reconciliation boundary.
/// </summary>
internal static class VbaProjectDiskIdentityProjection
{
    internal static VbaDocumentIdentity[] CaptureDocuments(
        IEnumerable<string> uris)
        => CaptureIdentifiedDocuments(uris)
            .Select(document => document.Identity)
            .ToArray();

    internal static VbaIdentifiedDocument[] CaptureIdentifiedDocuments(
        IEnumerable<string> uris)
        => uris
            .Select(uri =>
                VbaProjectIdentityModel.TryIdentifyDocument(
                    uri,
                    out var identity)
                        ? new VbaIdentifiedDocument(identity, uri)
                        : null)
            .OfType<VbaIdentifiedDocument>()
            .DistinctBy(document => document.Identity)
            .ToArray();
}

/// <summary>
/// Owns project disk enumeration, source identity, stable reads, decoding,
/// nested-manifest ownership, and manifest probes.
/// </summary>
internal interface IVbaProjectDiskInventory : IVbaProjectDiskObservationSource
{
    bool ContainsSource(
        VbaProjectResolution resolution,
        VbaDocumentIdentity sourceIdentity,
        IReadOnlyDictionary<VbaDocumentIdentity, bool>
            manifestBarrierOverrides);

    VbaProjectDiskColdSourceCapture CaptureColdSources(
        VbaProjectResolution resolution,
        IReadOnlyCollection<VbaDocumentIdentity> candidateSourceIdentities,
        IReadOnlySet<VbaDocumentIdentity> excludedSourceIdentities,
        IReadOnlyDictionary<VbaDocumentIdentity, bool>
            manifestBarrierOverrides,
        CancellationToken cancellationToken);

    VbaProjectDiskSource? CaptureWatchedSource(
        VbaProjectResolution resolution,
        VbaDocumentIdentity sourceIdentity,
        IReadOnlyDictionary<VbaDocumentIdentity, bool>
            manifestBarrierOverrides,
        out VbaProjectDiskSourceFailure? failure,
        CancellationToken cancellationToken);

    void InvalidateSource(VbaDocumentIdentity documentIdentity);
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
    private readonly DiskSourceDecoding sourceDecoding;
    private readonly Dictionary<VbaDocumentIdentity, CachedSource> sourceCache =
        new();
    private readonly Dictionary<VbaDocumentIdentity, int> activeLoads =
        new();
    private readonly Dictionary<VbaDocumentIdentity, long> publicationGenerations =
        new();

    public VbaFileSystemProjectDiskInventory()
        : this(
            SystemVbaProjectFileSystem.Instance,
            DiskSourceDecoding.ForCurrentProcess)
    {
    }

    internal VbaFileSystemProjectDiskInventory(
        IVbaProjectFileSystem fileSystem)
        : this(fileSystem, DiskSourceDecoding.ForCurrentProcess)
    {
    }

    internal VbaFileSystemProjectDiskInventory(
        IVbaProjectFileSystem fileSystem,
        DiskSourceDecoding sourceDecoding)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(sourceDecoding);
        this.fileSystem = fileSystem;
        this.sourceDecoding = sourceDecoding;
    }

    public bool ContainsSource(
        VbaProjectResolution resolution,
        VbaDocumentIdentity sourceIdentity,
        IReadOnlyDictionary<VbaDocumentIdentity, bool>
            manifestBarrierOverrides)
    {
        return sourceIdentity.IsLocalFile
            && new SourceOwnership(
                resolution,
                fileSystem,
                manifestBarrierOverrides).ContainsSource(
                    sourceIdentity.CanonicalValue);
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
        IReadOnlyCollection<VbaDocumentIdentity> candidateSourceIdentities,
        IReadOnlySet<VbaDocumentIdentity> excludedSourceIdentities,
        IReadOnlyDictionary<VbaDocumentIdentity, bool>
            manifestBarrierOverrides,
        CancellationToken cancellationToken)
    {
        var excludedPaths = CreateLocalPathSet(excludedSourceIdentities);
        var candidatePaths = CreateLocalPathSet(candidateSourceIdentities);
        var ownership = new SourceOwnership(
            resolution,
            fileSystem,
            manifestBarrierOverrides);
        var sources = new List<VbaProjectDiskSource>();
        var failures = new List<VbaProjectDiskSourceFailure>();
        var existingCandidateIdentities =
            new HashSet<VbaDocumentIdentity>();
        foreach (var path in EnumerateSourcePaths(
            resolution,
            cancellationToken))
        {
            if (!ownership.ContainsSource(path)
                || excludedPaths.Contains(path))
            {
                continue;
            }

            if (candidatePaths.Contains(path))
            {
                existingCandidateIdentities.Add(
                    IdentifyLocalDocument(path));
                continue;
            }

            if (TryCaptureSource(
                path,
                forceStableRead: false,
                cancellationToken,
                out var source,
                out var failure))
            {
                sources.Add(source);
            }
            else if (failure is not null)
            {
                failures.Add(failure);
            }
        }

        var ownedCandidateIdentities = candidatePaths
            .Where(ownership.ContainsSource)
            .Select(IdentifyLocalDocument)
            .ToHashSet();
        return new VbaProjectDiskColdSourceCapture(
            sources.ToArray(),
            failures.ToArray(),
            ownedCandidateIdentities)
        {
            ExistingCandidateSourceIdentities =
                existingCandidateIdentities
        };
    }

    public VbaProjectDiskSource? CaptureWatchedSource(
        VbaProjectResolution resolution,
        VbaDocumentIdentity sourceIdentity,
        IReadOnlyDictionary<VbaDocumentIdentity, bool>
            manifestBarrierOverrides,
        out VbaProjectDiskSourceFailure? failure,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!sourceIdentity.IsLocalFile)
        {
            failure = null;
            return null;
        }

        var fullPath = sourceIdentity.CanonicalValue;
        var ownership = new SourceOwnership(
            resolution,
            fileSystem,
            manifestBarrierOverrides);
        if (!ownership.ContainsSource(fullPath))
        {
            failure = null;
            return null;
        }

        InvalidateSource(sourceIdentity);
        return TryCaptureSource(
            fullPath,
            forceStableRead: true,
            cancellationToken,
            out var source,
            out failure)
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

    public void InvalidateSource(VbaDocumentIdentity documentIdentity)
    {
        lock (gate)
        {
            sourceCache.Remove(documentIdentity);
            if (activeLoads.ContainsKey(documentIdentity))
            {
                publicationGenerations.TryGetValue(
                    documentIdentity,
                    out var previousGeneration);
                publicationGenerations[documentIdentity] =
                    previousGeneration + 1;
            }
            else
            {
                publicationGenerations.Remove(documentIdentity);
            }
        }
    }

    private static PreparedDiskObservation PrepareObservation(
        VbaProjectDiskObservationRequest request)
    {
        var manifestBarrierOverrides =
            new Dictionary<VbaDocumentIdentity, bool>();
        foreach (var barrierOverride in request.BarrierOverrides)
        {
            manifestBarrierOverrides[barrierOverride.DocumentIdentity] =
                barrierOverride.IsBarrier;
        }

        return new PreparedDiskObservation(
            request.Project,
            request.ManifestCandidates.ToArray(),
            manifestBarrierOverrides,
            request.ObservedManifestBarrierIdentities.ToArray(),
            CreateLocalPathSet(request.OpenSourceIdentities));
    }

    private VbaProjectDiskObservation ObserveReconciliation(
        PreparedDiskObservation observation,
        CancellationToken cancellationToken)
    {
        var sources = new List<VbaProjectDiskSource>();
        var failures = new List<VbaProjectDiskSourceFailure>();
        var existingNonOwnedSourceIdentities =
            new HashSet<VbaDocumentIdentity>();
        var observedManifestBarrierPaths = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var missingObservedManifestBarrierIdentities =
            new List<VbaDocumentIdentity>();
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
                    existingNonOwnedSourceIdentities.Add(
                        IdentifyLocalDocument(fullPath));
                    continue;
                }

                if (observation.OpenSourcePaths.Contains(fullPath))
                {
                    continue;
                }

                if (TryCaptureSource(
                    fullPath,
                    forceStableRead: true,
                    cancellationToken,
                    out var source,
                    out var failure))
                {
                    sources.Add(source);
                }
                else if (failure is not null)
                {
                    failures.Add(failure);
                }
            }

            observedManifestBarrierPaths.UnionWith(
                ownership.ObservedManifestBarrierPaths);
        }

        foreach (var identity in observation.ObservedManifestBarrierIdentities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (identity.IsLocalFile)
            {
                var fullPath = identity.CanonicalValue;
                if (fileSystem.FileExists(fullPath))
                {
                    observedManifestBarrierPaths.Add(fullPath);
                }
                else
                {
                    missingObservedManifestBarrierIdentities.Add(identity);
                }
            }
        }

        VbaProjectDiskManifest? manifest = null;
        foreach (var candidate in observation.ManifestCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!candidate.DocumentIdentity.IsLocalFile)
            {
                continue;
            }

            var manifestPath = candidate.DocumentIdentity.CanonicalValue;
            if (IsKnownInvalidBarrier(observation, candidate.DocumentIdentity)
                || !fileSystem.FileExists(manifestPath))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(manifestPath);
            cancellationToken.ThrowIfCancellationRequested();
            manifest = new VbaProjectDiskManifest(
                IdentifyLocalDocument(fullPath),
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
                    IdentifyLocalDocument(path),
                    new Uri(path).AbsoluteUri,
                    path,
                    fileSystem.ReadManifestText(path)));
        }

        return new VbaProjectDiskObservation(sources, manifest)
        {
            Failures = failures.ToArray(),
            ExistingNonOwnedSourceIdentities =
                existingNonOwnedSourceIdentities,
            ObservedManifestBarriers = observedManifestBarriers.ToArray(),
            MissingObservedManifestBarrierIdentities =
                missingObservedManifestBarrierIdentities
        };
    }

    private IEnumerable<string> EnumerateSourcePaths(
        VbaProjectResolution resolution,
        CancellationToken cancellationToken)
        => EnumerateSourcePaths(
            new VbaProjectDiskProjectScope(
                IdentifyOptionalAuthority(resolution),
                resolution.Kind,
                resolution.RootPath),
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
        out VbaProjectDiskSource source,
        out VbaProjectDiskSourceFailure? failure)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(localPath);
        var documentIdentity = IdentifyLocalDocument(fullPath);
        if (!fileSystem.TryGetSourceMetadata(fullPath, out var metadata))
        {
            source = null!;
            failure = null;
            return false;
        }

        long capturedPublicationGeneration;
        lock (gate)
        {
            if (!forceStableRead
                && sourceCache.TryGetValue(documentIdentity, out var cached)
                && cached.Metadata == metadata)
            {
                source = CreateSource(fullPath, cached);
                failure = null;
                return true;
            }

            activeLoads.TryGetValue(documentIdentity, out var activeLoadCount);
            activeLoads[documentIdentity] = activeLoadCount + 1;
            publicationGenerations.TryGetValue(
                documentIdentity,
                out var previousPublicationGeneration);
            capturedPublicationGeneration =
                previousPublicationGeneration + 1;
            publicationGenerations[documentIdentity] =
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
                                documentIdentity,
                                out var retriedCached)
                            && retriedCached.Metadata == metadata)
                        {
                            source = CreateSource(
                                fullPath,
                                retriedCached);
                            failure = null;
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
                    failure = null;
                    return false;
                }
                catch (DirectoryNotFoundException)
                {
                    source = null!;
                    failure = null;
                    return false;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (!fileSystem.TryGetSourceMetadata(
                    fullPath,
                    out var loadedMetadata))
                {
                    source = null!;
                    failure = null;
                    return false;
                }

                if (loadedMetadata != metadata)
                {
                    metadata = loadedMetadata;
                    continue;
                }

                string text;
                try
                {
                    text = sourceDecoding.Decode(sourceBytes, fullPath);
                }
                catch (DiskSourceDecodingException error)
                {
                    lock (gate)
                    {
                        publicationGenerations.TryGetValue(
                            documentIdentity,
                            out var currentPublicationGeneration);
                        if (currentPublicationGeneration
                            == capturedPublicationGeneration)
                        {
                            sourceCache.Remove(documentIdentity);
                        }
                    }

                    source = null!;
                    failure = new VbaProjectDiskSourceFailure(
                        documentIdentity,
                        new Uri(fullPath).AbsoluteUri,
                        fullPath,
                        loadedMetadata,
                        error.Message);
                    return false;
                }

                CachedSource loaded;
                var rawContentDigest = Convert.ToHexString(
                    SHA256.HashData(sourceBytes));
                lock (gate)
                {
                    var identity =
                        sourceCache.TryGetValue(
                            documentIdentity,
                            out var existing)
                        && existing.Text.Equals(
                            text,
                            StringComparison.Ordinal)
                            ? existing.ContentIdentity
                            : VbaProjectDiskContentIdentity.FromText(text);
                    loaded = new CachedSource(
                        loadedMetadata,
                        text,
                        identity,
                        rawContentDigest);
                    publicationGenerations.TryGetValue(
                        documentIdentity,
                        out var currentPublicationGeneration);
                    if (currentPublicationGeneration
                        == capturedPublicationGeneration)
                    {
                        sourceCache[documentIdentity] = loaded;
                    }
                }

                source = CreateSource(fullPath, loaded);
                failure = null;
                return true;
            }

            throw new IOException(
                $"Source file changed repeatedly while it was being read: {fullPath}");
        }
        finally
        {
            lock (gate)
            {
                var remainingLoadCount = activeLoads[documentIdentity] - 1;
                if (remainingLoadCount == 0)
                {
                    activeLoads.Remove(documentIdentity);
                    publicationGenerations.Remove(documentIdentity);
                }
                else
                {
                    activeLoads[documentIdentity] = remainingLoadCount;
                }
            }
        }
    }

    private static VbaProjectDiskSource CreateSource(
        string fullPath,
        CachedSource cached)
        => new(
            IdentifyLocalDocument(fullPath),
            new Uri(fullPath).AbsoluteUri,
            fullPath,
            cached.Text,
            cached.Metadata,
            cached.ContentIdentity,
            cached.RawContentDigest);

    private static VbaDocumentIdentity IdentifyLocalDocument(string fullPath)
        => VbaProjectIdentityModel.TryIdentifyLocalDocumentPath(
            fullPath,
            out var identity)
                ? identity
                : throw new InvalidOperationException(
                    $"The disk source path has no document identity: {fullPath}");

    private static VbaProjectAuthorityIdentity? IdentifyOptionalAuthority(
        VbaProjectResolution resolution)
        => VbaProjectIdentityModel.TryIdentifyAuthority(
            resolution,
            out var identity)
                ? identity
                : null;

    private static HashSet<string> CreateLocalPathSet(
        IEnumerable<VbaDocumentIdentity> identities)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var identity in identities)
        {
            if (identity.IsLocalFile)
            {
                paths.Add(identity.CanonicalValue);
            }
        }

        return paths;
    }

    private static bool IsKnownInvalidBarrier(
        PreparedDiskObservation observation,
        VbaDocumentIdentity manifestIdentity)
    {
        var candidate = observation.ManifestCandidates.FirstOrDefault(
            candidate => candidate.DocumentIdentity == manifestIdentity);
        return candidate?.ExistedInBaseline == true
            && observation.ManifestBarrierOverrides.TryGetValue(
                manifestIdentity,
                out var isBarrier)
            && !isBarrier;
    }

    private sealed record PreparedDiskObservation(
        VbaProjectDiskProjectScope Project,
        IReadOnlyList<VbaProjectDiskManifestProbe> ManifestCandidates,
        IReadOnlyDictionary<VbaDocumentIdentity, bool>
            ManifestBarrierOverrides,
        IReadOnlyList<VbaDocumentIdentity> ObservedManifestBarrierIdentities,
        IReadOnlySet<string> OpenSourcePaths);

    private sealed record CachedSource(
        VbaProjectSourceFileMetadata Metadata,
        string Text,
        VbaProjectDiskContentIdentity ContentIdentity,
        string RawContentDigest);

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
        private readonly VbaProjectAuthorityIdentity? authorityIdentity;
        private readonly IReadOnlyDictionary<VbaDocumentIdentity, bool>
            manifestBarrierOverrides;
        private readonly Dictionary<string, bool> ownedDirectories =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> observedManifestBarrierPaths =
            new(StringComparer.OrdinalIgnoreCase);

        public SourceOwnership(
            VbaProjectResolution resolution,
            IVbaProjectFileSystem fileSystem,
            IReadOnlyDictionary<VbaDocumentIdentity, bool>?
                manifestBarrierOverrides = null)
            : this(
                new VbaProjectDiskProjectScope(
                    IdentifyOptionalAuthority(resolution),
                    resolution.Kind,
                    resolution.RootPath),
                fileSystem,
                manifestBarrierOverrides)
        {
        }

        public SourceOwnership(
            VbaProjectDiskProjectScope project,
            IVbaProjectFileSystem fileSystem,
            IReadOnlyDictionary<VbaDocumentIdentity, bool>?
                manifestBarrierOverrides = null)
        {
            kind = project.Kind;
            this.fileSystem = fileSystem;
            rootPath = NormalizePath(project.RootPath);
            authorityIdentity = project.AuthorityIdentity;
            this.manifestBarrierOverrides =
                manifestBarrierOverrides
                ?? new Dictionary<VbaDocumentIdentity, bool>();
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
            var candidateManifestIdentity =
                IdentifyLocalDocument(candidateManifestPath);
            var hasOverride =
                manifestBarrierOverrides.TryGetValue(
                    candidateManifestIdentity,
                    out var barrierOverride);
            var hasManifestBarrier = hasOverride
                    ? barrierOverride
                    : fileSystem.FileExists(candidateManifestPath);
            var isAuthorityManifest = authorityIdentity?.UsesManifest(
                candidateManifestIdentity) == true;
            if (!hasOverride
                && hasManifestBarrier
                && !isAuthorityManifest)
            {
                observedManifestBarrierPaths.Add(
                    Path.GetFullPath(candidateManifestPath));
            }

            isOwned = parentOwned
                && (isAuthorityManifest
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
