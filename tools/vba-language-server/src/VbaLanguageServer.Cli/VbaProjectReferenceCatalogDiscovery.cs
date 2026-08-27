using System.Diagnostics;
using VbaLanguageServer.ProjectModel;
using VbaTools.TypeLibRegistry;

namespace VbaLanguageServer.SourceModel;

/// <summary>
/// Identifies a discovered TypeLib catalog identity for a VBA project reference.
/// </summary>
/// <param name="ReferenceName">The human-visible reference name.</param>
/// <param name="Guid">The TypeLib GUID.</param>
/// <param name="MajorVersion">The TypeLib major version.</param>
/// <param name="MinorVersion">The TypeLib minor version.</param>
/// <param name="Lcid">The TypeLib locale identifier.</param>
/// <param name="Path">The registry-resolved TypeLib path.</param>
public sealed record VbaProjectReferenceCatalogIdentity(
    string ReferenceName,
    string Guid,
    int MajorVersion,
    int MinorVersion,
    int Lcid,
    string Path);

/// <summary>
/// Represents the result of discovering catalog metadata for one reference name.
/// </summary>
/// <param name="ReferenceName">The reference name being discovered.</param>
/// <param name="Identities">The matching catalog identities.</param>
/// <param name="Catalog">The discovered catalog metadata, when available.</param>
/// <param name="ErrorMessage">The discovery error message, when discovery failed.</param>
/// <param name="RequiresExternalIdentityResolution">Whether registry discovery could not select one concrete identity.</param>
public sealed record VbaProjectReferenceCatalogDiscoveryResult(
    string ReferenceName,
    IReadOnlyList<VbaProjectReferenceCatalogIdentity> Identities,
    VbaProjectReferenceCatalog? Catalog,
    string? ErrorMessage = null,
    bool RequiresExternalIdentityResolution = false)
{
    /// <summary>
    /// Gets whether discovery found more than one possible identity.
    /// </summary>
    public bool IsAmbiguous => Identities.Count > 1;

    /// <summary>
    /// Gets whether discovery failed with an error message.
    /// </summary>
    public bool IsFailure => !string.IsNullOrWhiteSpace(ErrorMessage);

    /// <summary>
    /// Gets whether discovery produced one well-formed successful identity.
    /// </summary>
    public bool IsSuccessful =>
        !IsFailure
        && Identities.Count == 1
        && VbaProjectReferenceName.AreEquivalent(
            ReferenceName,
            Identities[0].ReferenceName)
        && (Catalog is null
            || VbaProjectReferenceName.AreEquivalent(
                ReferenceName,
                Catalog.ReferenceName));

    /// <summary>
    /// Gets whether discovery produced usable catalog metadata.
    /// </summary>
    public bool HasUsableCatalog => IsSuccessful && Catalog is not null;

    /// <summary>
    /// Creates a successful discovery result.
    /// </summary>
    /// <param name="identity">The resolved catalog identity.</param>
    /// <param name="catalog">The optional catalog metadata.</param>
    /// <returns>The discovery result.</returns>
    public static VbaProjectReferenceCatalogDiscoveryResult Success(
        VbaProjectReferenceCatalogIdentity identity,
        VbaProjectReferenceCatalog? catalog = null)
        => new(identity.ReferenceName, [identity], catalog);

    /// <summary>
    /// Creates an ambiguous discovery result.
    /// </summary>
    /// <param name="referenceName">The reference name being discovered.</param>
    /// <param name="identities">The matching identities.</param>
    /// <returns>The ambiguous discovery result.</returns>
    public static VbaProjectReferenceCatalogDiscoveryResult Ambiguous(
        string referenceName,
        IReadOnlyList<VbaProjectReferenceCatalogIdentity> identities)
        => new(
            referenceName,
            identities,
            null,
            RequiresExternalIdentityResolution: true);

    /// <summary>
    /// Creates a failed discovery result.
    /// </summary>
    /// <param name="referenceName">The reference name being discovered.</param>
    /// <param name="errorMessage">The discovery error message.</param>
    /// <returns>The failed discovery result.</returns>
    public static VbaProjectReferenceCatalogDiscoveryResult Failure(string referenceName, string errorMessage)
        => new(referenceName, [], null, errorMessage);
}

/// <summary>
/// Discovers reference catalog identities and optional catalog metadata.
/// </summary>
public interface IVbaProjectReferenceCatalogDiscovery
{
    /// <summary>
    /// Discovers catalog information for one reference name.
    /// </summary>
    /// <param name="referenceName">The human-visible reference name.</param>
    /// <param name="cancellationToken">A cancellation token for discovery work.</param>
    /// <returns>The discovery result.</returns>
    Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
        string referenceName,
        CancellationToken cancellationToken = default);
}

internal interface IVbaProjectReferenceCatalogDiscoveryBatchFactory
{
    IVbaProjectReferenceCatalogDiscovery CreateBatchDiscovery();
}

internal sealed record VbaProjectReferenceCatalogIdentityKey(
    string Guid,
    int MajorVersion,
    int MinorVersion);

internal interface IVbaProjectReferenceCatalogIdentityDiscovery
{
    Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverIdentityAsync(
        string referenceName,
        VbaProjectReferenceCatalogIdentityKey identity,
        CancellationToken cancellationToken = default);
}

internal sealed record VbaProjectReferenceCatalogRefreshContext(
    string ProjectPath,
    string DocumentName,
    VbaProjectReferenceSelection Selection,
    string? ScopeKey = null,
    string? SelectionFingerprint = null,
    Func<bool>? IsCurrent = null);

internal interface IVbaProjectReferenceCatalogContextDiscoveryFactory
{
    bool UsesContextSpecificResolution => true;

    IVbaProjectReferenceCatalogDiscovery CreateContextDiscovery(
        VbaProjectReferenceCatalogRefreshContext context);
}

/// <summary>
/// Adds a file-based blocking hook around catalog discovery for language-server process tests.
/// </summary>
internal sealed class BlockingReferenceCatalogDiscoveryHook
    : IVbaProjectReferenceCatalogDiscovery,
      IVbaProjectReferenceCatalogDiscoveryBatchFactory,
      IVbaProjectReferenceCatalogIdentityDiscovery,
      IVbaProjectReferenceCatalogContextDiscoveryFactory
{
    /// <summary>
    /// The environment variable containing the file written when discovery reaches the hook.
    /// </summary>
    internal const string StartedFileEnvironmentVariable = "VBA_TOOLS_REFERENCE_CATALOG_DISCOVERY_STARTED_FILE";

    /// <summary>
    /// The environment variable containing the file that releases the blocked discovery hook.
    /// </summary>
    internal const string ReleaseFileEnvironmentVariable = "VBA_TOOLS_REFERENCE_CATALOG_DISCOVERY_RELEASE_FILE";

    private readonly IVbaProjectReferenceCatalogDiscovery inner;
    private readonly string? startedFile;
    private readonly string? releaseFile;

    bool IVbaProjectReferenceCatalogContextDiscoveryFactory.UsesContextSpecificResolution =>
        inner is IVbaProjectReferenceCatalogContextDiscoveryFactory contextFactory
        && contextFactory.UsesContextSpecificResolution;

    private BlockingReferenceCatalogDiscoveryHook(
        IVbaProjectReferenceCatalogDiscovery inner,
        string? startedFile,
        string? releaseFile)
    {
        this.inner = inner;
        this.startedFile = startedFile;
        this.releaseFile = releaseFile;
    }

    /// <summary>
    /// Wraps discovery when the test hook environment variables are configured.
    /// </summary>
    /// <param name="inner">The discovery service to wrap.</param>
    /// <returns>The original discovery service or a wrapped hook.</returns>
    public static IVbaProjectReferenceCatalogDiscovery WrapIfConfigured(IVbaProjectReferenceCatalogDiscovery inner)
    {
        var startedFile = Environment.GetEnvironmentVariable(StartedFileEnvironmentVariable);
        var releaseFile = Environment.GetEnvironmentVariable(ReleaseFileEnvironmentVariable);
        return string.IsNullOrWhiteSpace(startedFile) && string.IsNullOrWhiteSpace(releaseFile)
            ? inner
            : new BlockingReferenceCatalogDiscoveryHook(inner, startedFile, releaseFile);
    }

    /// <inheritdoc />
    public async Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
        string referenceName,
        CancellationToken cancellationToken = default)
    {
        WriteStartedFile(referenceName);
        if (!string.IsNullOrWhiteSpace(releaseFile))
        {
            while (!File.Exists(releaseFile))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
            }

            return VbaProjectReferenceCatalogDiscoveryResult.Failure(
                referenceName,
                "TypeLib discovery was released by the reference catalog test hook before metadata extraction ran.");
        }

        return await inner.DiscoverAsync(referenceName, cancellationToken);
    }

    IVbaProjectReferenceCatalogDiscovery
        IVbaProjectReferenceCatalogDiscoveryBatchFactory.CreateBatchDiscovery()
        => inner is IVbaProjectReferenceCatalogDiscoveryBatchFactory batchFactory
            ? new BlockingReferenceCatalogDiscoveryHook(
                batchFactory.CreateBatchDiscovery(),
                startedFile,
                releaseFile)
            : this;

    Task<VbaProjectReferenceCatalogDiscoveryResult>
        IVbaProjectReferenceCatalogIdentityDiscovery.DiscoverIdentityAsync(
            string referenceName,
            VbaProjectReferenceCatalogIdentityKey identity,
            CancellationToken cancellationToken)
        => inner is IVbaProjectReferenceCatalogIdentityDiscovery identityDiscovery
            ? identityDiscovery.DiscoverIdentityAsync(
                referenceName,
                identity,
                cancellationToken)
            : Task.FromResult(VbaProjectReferenceCatalogDiscoveryResult.Failure(
                referenceName,
                "The wrapped discovery cannot load a selected TypeLib identity."));

    IVbaProjectReferenceCatalogDiscovery
        IVbaProjectReferenceCatalogContextDiscoveryFactory.CreateContextDiscovery(
            VbaProjectReferenceCatalogRefreshContext context)
        => inner is IVbaProjectReferenceCatalogContextDiscoveryFactory contextFactory
            ? new BlockingReferenceCatalogDiscoveryHook(
                contextFactory.CreateContextDiscovery(context),
                startedFile,
                releaseFile)
            : this;

    private void WriteStartedFile(string referenceName)
    {
        if (string.IsNullOrWhiteSpace(startedFile))
        {
            return;
        }

        var directory = Path.GetDirectoryName(startedFile);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(startedFile, referenceName);
    }
}

/// <summary>
/// Discovers reference catalog identities from TypeLib registry entries.
/// </summary>
public sealed class TypeLibReferenceCatalogDiscovery
    : IVbaProjectReferenceCatalogDiscovery,
      IVbaProjectReferenceCatalogDiscoveryBatchFactory,
      IVbaProjectReferenceCatalogIdentityDiscovery
{
    private readonly ITypeLibRegistryCatalogReader neutralRegistryReader;
    private readonly Lazy<TypeLibRegistryCatalog>? registryCatalog;
    private readonly ITypeLibCatalogMetadataReader metadataReader;

    /// <summary>
    /// Creates a TypeLib-backed catalog discovery service from the neutral registry catalog.
    /// </summary>
    /// <param name="registryReader">The neutral registry catalog reader.</param>
    public TypeLibReferenceCatalogDiscovery(ITypeLibRegistryCatalogReader registryReader)
        : this(registryReader, new ComTypeLibCatalogMetadataReader())
    {
    }

    /// <summary>
    /// Creates a TypeLib-backed catalog discovery service from the neutral registry catalog.
    /// </summary>
    /// <param name="registryReader">The neutral registry catalog reader.</param>
    /// <param name="metadataReader">The reader used to extract TypeLib metadata.</param>
    public TypeLibReferenceCatalogDiscovery(
        ITypeLibRegistryCatalogReader registryReader,
        ITypeLibCatalogMetadataReader metadataReader)
        : this(registryReader, metadataReader, cacheSnapshot: false)
    {
    }

    private TypeLibReferenceCatalogDiscovery(
        ITypeLibRegistryCatalogReader registryReader,
        ITypeLibCatalogMetadataReader metadataReader,
        bool cacheSnapshot)
    {
        ArgumentNullException.ThrowIfNull(registryReader);
        ArgumentNullException.ThrowIfNull(metadataReader);
        neutralRegistryReader = registryReader;
        if (cacheSnapshot)
        {
            registryCatalog = new Lazy<TypeLibRegistryCatalog>(
                registryReader.Read,
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        this.metadataReader = metadataReader;
    }

    /// <summary>
    /// Discovers registry identities and generated catalog metadata matching a reference name.
    /// </summary>
    /// <param name="referenceName">The human-visible reference name.</param>
    /// <param name="cancellationToken">A cancellation token for discovery work.</param>
    /// <returns>The discovery result.</returns>
    public Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
        string referenceName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var catalog = registryCatalog?.Value ?? neutralRegistryReader.Read();
        return Task.FromResult(DiscoverFromNeutralCatalog(
            catalog,
            referenceName,
            cancellationToken));
    }

    IVbaProjectReferenceCatalogDiscovery
        IVbaProjectReferenceCatalogDiscoveryBatchFactory.CreateBatchDiscovery()
        => new TypeLibReferenceCatalogDiscovery(
            neutralRegistryReader,
            metadataReader,
            cacheSnapshot: true);

    private VbaProjectReferenceCatalogDiscoveryResult DiscoverFromNeutralCatalog(
        TypeLibRegistryCatalog catalog,
        string referenceName,
        CancellationToken cancellationToken)
    {
        if (!catalog.Complete)
        {
            return VbaProjectReferenceCatalogDiscoveryResult.Failure(
                referenceName,
                catalog.Diagnostic?.Message ?? "The TypeLib registry catalog is incomplete.");
        }

        var registeredName = catalog.Find(referenceName);
        if (registeredName is null)
        {
            return VbaProjectReferenceCatalogDiscoveryResult.Failure(
                referenceName,
                "No matching TypeLib registry entry was found.");
        }

        var candidates = registeredName.Lineages
            .Select(lineage => CreateLineageCandidate(registeredName.Name, lineage))
            .Where(candidate => candidate is not null)
            .Cast<NeutralRegistryIdentityCandidate>()
            .ToArray();
        return candidates.Length switch
        {
            0 => VbaProjectReferenceCatalogDiscoveryResult.Failure(
                registeredName.Name,
                "No well-formed TypeLib registry identity was found."),
            1 => DiscoverCatalog(
                registeredName.Name,
                candidates[0],
                cancellationToken),
            _ => VbaProjectReferenceCatalogDiscoveryResult.Ambiguous(
                registeredName.Name,
                candidates
                    .Select(candidate => CreateIdentity(candidate, candidate.Locations[0]))
                    .ToArray())
        };
    }

    Task<VbaProjectReferenceCatalogDiscoveryResult>
        IVbaProjectReferenceCatalogIdentityDiscovery.DiscoverIdentityAsync(
            string referenceName,
            VbaProjectReferenceCatalogIdentityKey identity,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var catalog = registryCatalog?.Value ?? neutralRegistryReader.Read();
        return Task.FromResult(DiscoverExactIdentity(
            catalog,
            referenceName,
            identity,
            cancellationToken));
    }

    private VbaProjectReferenceCatalogDiscoveryResult DiscoverExactIdentity(
        TypeLibRegistryCatalog catalog,
        string referenceName,
        VbaProjectReferenceCatalogIdentityKey identity,
        CancellationToken cancellationToken)
    {
        if (!catalog.Complete)
        {
            return VbaProjectReferenceCatalogDiscoveryResult.Failure(
                referenceName,
                catalog.Diagnostic?.Message ?? "The TypeLib registry catalog is incomplete.");
        }

        var registeredName = catalog.Find(referenceName);
        var matchingLineages = registeredName?.Lineages
            .Where(lineage => lineage.Guid.Equals(identity.Guid, StringComparison.Ordinal))
            .ToArray() ?? [];
        if (matchingLineages.Length != 1)
        {
            return VbaProjectReferenceCatalogDiscoveryResult.Failure(
                referenceName,
                "The externally resolved TypeLib GUID is not present in the neutral registry snapshot.");
        }

        var matchingVersions = matchingLineages[0].Versions
            .Where(version => version.Major == identity.MajorVersion
                && version.Minor == identity.MinorVersion)
            .ToArray();
        if (matchingVersions.Length != 1)
        {
            return VbaProjectReferenceCatalogDiscoveryResult.Failure(
                referenceName,
                "The externally resolved TypeLib version is not present in the neutral registry snapshot.");
        }

        var candidate = CreateLineageCandidate(
            registeredName!.Name,
            matchingLineages[0],
            matchingVersions[0]);
        return candidate is null
            ? VbaProjectReferenceCatalogDiscoveryResult.Failure(
                referenceName,
                "The externally resolved TypeLib identity has no usable registered location.")
            : DiscoverCatalog(
                registeredName.Name,
                candidate,
                cancellationToken);
    }

    private static NeutralRegistryIdentityCandidate? CreateLineageCandidate(
        string referenceName,
        TypeLibRegistryLineage lineage)
    {
        var version = lineage.Versions
            .OrderByDescending(candidate => candidate.Major)
            .ThenByDescending(candidate => candidate.Minor)
            .FirstOrDefault();
        return version is null
            ? null
            : CreateLineageCandidate(referenceName, lineage, version);
    }

    private static NeutralRegistryIdentityCandidate? CreateLineageCandidate(
        string referenceName,
        TypeLibRegistryLineage lineage,
        TypeLibRegistryVersion version)
    {
        var locations = version.Locales
            .SelectMany(
                locale => locale.Paths,
                (locale, path) => new { locale.Lcid, Path = path })
            .OrderBy(candidate => candidate.Lcid)
            .ThenBy(candidate => candidate.Path.Platform, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Path.Path, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => new NeutralRegistryCatalogLocation(
                candidate.Lcid,
                candidate.Path.Platform,
                candidate.Path.Path))
            .ToArray();
        return locations is null || locations.Length == 0
            ? null
            : new NeutralRegistryIdentityCandidate(
                referenceName,
                lineage.Guid,
                version.Major,
                version.Minor,
                locations);
    }

    private VbaProjectReferenceCatalogDiscoveryResult DiscoverCatalog(
        string referenceName,
        NeutralRegistryIdentityCandidate candidate,
        CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        foreach (var location in candidate.Locations)
        {
            var identity = CreateIdentity(candidate, location);
            var result = DiscoverCatalog(referenceName, identity, cancellationToken);
            if (result.HasUsableCatalog)
            {
                return result;
            }

            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                failures.Add(result.ErrorMessage);
            }
        }

        return VbaProjectReferenceCatalogDiscoveryResult.Failure(
            referenceName,
            failures.Count == 0
                ? "TypeLib catalog metadata could not be read from any registered location."
                : string.Join(" ", failures.Distinct(StringComparer.Ordinal)));
    }

    private static VbaProjectReferenceCatalogIdentity CreateIdentity(
        NeutralRegistryIdentityCandidate candidate,
        NeutralRegistryCatalogLocation location)
        => new(
            candidate.ReferenceName,
            candidate.Guid,
            candidate.MajorVersion,
            candidate.MinorVersion,
            location.Lcid,
            location.Path);

    private VbaProjectReferenceCatalogDiscoveryResult DiscoverCatalog(
        string referenceName,
        VbaProjectReferenceCatalogIdentity identity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var metadata = metadataReader.ReadMetadata(identity);
            var catalog = TypeLibReferenceCatalogBuilder.Build(referenceName, metadata);
            return VbaProjectReferenceCatalogDiscoveryResult.Success(identity, catalog);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return VbaProjectReferenceCatalogDiscoveryResult.Failure(
                referenceName,
                $"TypeLib catalog metadata could not be read: {ex.Message}");
        }
    }

    private sealed record NeutralRegistryIdentityCandidate(
        string ReferenceName,
        string Guid,
        int MajorVersion,
        int MinorVersion,
        IReadOnlyList<NeutralRegistryCatalogLocation> Locations);

    private sealed record NeutralRegistryCatalogLocation(
        int Lcid,
        string Platform,
        string Path);
}

/// <summary>
/// Holds the current reference catalog set and discovered identities for background refresh.
/// </summary>
public sealed class VbaProjectReferenceCatalogCache
{
    private readonly object gate = new();
    private VbaProjectReferenceCatalogSet catalogSet;
    private long version;
    private readonly Dictionary<string, VbaProjectReferenceCatalogIdentity> identities = new(VbaProjectReferenceName.Comparer);
    private readonly Dictionary<string, VbaProjectReferenceCatalogSource> catalogSources = new(VbaProjectReferenceName.Comparer);
    private readonly Dictionary<string, long> referenceChangeVersions =
        new(VbaProjectReferenceName.Comparer);
    private readonly Dictionary<string, Dictionary<string, ScopedReferenceCatalogBinding>>
        scopedBindings = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> refreshesInProgress = new(VbaProjectReferenceName.Comparer);
    private readonly Dictionary<string, SemaphoreSlim> refreshOwnership =
        new(VbaProjectReferenceName.Comparer);

    /// <summary>
    /// Creates a catalog cache with an initial catalog set.
    /// </summary>
    /// <param name="catalogSet">The initial catalog set.</param>
    public VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet catalogSet)
    {
        this.catalogSet = catalogSet;
        foreach (var referenceName in catalogSet.ReferenceNames)
        {
            catalogSources[referenceName] = VbaProjectReferenceCatalogSource.Bundled;
        }
    }

    /// <summary>
    /// Gets the current catalog set snapshot.
    /// </summary>
    public VbaProjectReferenceCatalogSet Current
    {
        get
        {
            lock (gate)
            {
                return catalogSet;
            }
        }
    }

    /// <summary>
    /// Gets a versioned snapshot of the current reference catalog set.
    /// </summary>
    public VbaProjectReferenceCatalogCacheState State
    {
        get
        {
            lock (gate)
            {
                return new VbaProjectReferenceCatalogCacheState(catalogSet, version);
            }
        }
    }

    internal VbaProjectReferenceCatalogSelectionState CaptureSelectionState(
        IReadOnlyList<VbaProjectReference> references)
        => CaptureSelectionState(references, scopeKey: null);

    internal VbaProjectReferenceCatalogSelectionState CaptureSelectionState(
        IReadOnlyList<VbaProjectReference> references,
        string? scopeKey)
    {
        lock (gate)
        {
            var selectedRevision = 0L;
            var selectedCatalogSet = catalogSet;
            var selectedSources = new Dictionary<string, VbaProjectReferenceCatalogSource>(
                VbaProjectReferenceName.Comparer);
            Dictionary<string, ScopedReferenceCatalogBinding>? scopeBindings = null;
            if (scopeKey is not null)
            {
                scopedBindings.TryGetValue(scopeKey, out scopeBindings);
            }
            for (var index = 0; index < references.Count; index++)
            {
                var referenceName = references[index].Name;
                selectedSources[referenceName] = catalogSources.TryGetValue(
                    referenceName,
                    out var catalogSource)
                    ? catalogSource
                    : VbaProjectReferenceCatalogSource.Unavailable;
                if (referenceChangeVersions.TryGetValue(
                    referenceName,
                    out var revision))
                {
                    selectedRevision = Math.Max(selectedRevision, revision);
                }

                if (scopeBindings is not null
                    && scopeBindings.TryGetValue(referenceName, out var scopedBinding))
                {
                    selectedCatalogSet = selectedCatalogSet.WithCatalog(scopedBinding.Catalog);
                    selectedSources[referenceName] = scopedBinding.Source;
                    selectedRevision = Math.Max(
                        selectedRevision,
                        scopedBinding.ChangeVersion);
                }
            }

            return new VbaProjectReferenceCatalogSelectionState(
                selectedCatalogSet,
                selectedRevision,
                selectedSources);
        }
    }

    /// <summary>
    /// Gets the discovered catalog identity snapshot keyed by reference name.
    /// </summary>
    public IReadOnlyDictionary<string, VbaProjectReferenceCatalogIdentity> Identities
    {
        get
        {
            lock (gate)
            {
                return new Dictionary<string, VbaProjectReferenceCatalogIdentity>(
                    identities,
                    VbaProjectReferenceName.Comparer);
            }
        }
    }

    /// <summary>
    /// Gets the known source for each currently available catalog.
    /// </summary>
    public IReadOnlyDictionary<string, VbaProjectReferenceCatalogSource> CatalogSources
    {
        get
        {
            lock (gate)
            {
                return new Dictionary<string, VbaProjectReferenceCatalogSource>(
                    catalogSources,
                    VbaProjectReferenceName.Comparer);
            }
        }
    }

    /// <summary>
    /// Gets the known catalog source for a reference name.
    /// </summary>
    /// <param name="referenceName">The human-visible reference name.</param>
    /// <returns>The catalog source, or <see cref="VbaProjectReferenceCatalogSource.Unavailable"/>.</returns>
    public VbaProjectReferenceCatalogSource GetCatalogSource(string referenceName)
        => GetCatalogSource(referenceName, scopeKey: null);

    internal VbaProjectReferenceCatalogSource GetCatalogSource(
        string referenceName,
        string? scopeKey)
    {
        lock (gate)
        {
            if (scopeKey is not null
                && scopedBindings.TryGetValue(scopeKey, out var scopeBindings)
                && scopeBindings.TryGetValue(referenceName, out var scopedBinding))
            {
                return scopedBinding.Source;
            }

            return catalogSources.TryGetValue(referenceName, out var source)
                ? source
                : VbaProjectReferenceCatalogSource.Unavailable;
        }
    }

    /// <summary>
    /// Determines whether a reference name already has a resolved catalog identity in memory.
    /// </summary>
    /// <param name="referenceName">The human-visible reference name.</param>
    /// <returns>True when an identity is already cached for the reference.</returns>
    public bool HasIdentity(string referenceName)
        => HasIdentity(referenceName, scopeKey: null, selectionFingerprint: null);

    internal bool HasIdentity(
        string referenceName,
        string? scopeKey,
        string? selectionFingerprint)
    {
        lock (gate)
        {
            if (scopeKey is not null
                && scopedBindings.TryGetValue(scopeKey, out var scopeBindings)
                && scopeBindings.TryGetValue(referenceName, out var scopedBinding)
                && scopedBinding.Identity is not null
                && string.Equals(
                    scopedBinding.SelectionFingerprint,
                    selectionFingerprint,
                    StringComparison.Ordinal))
            {
                return true;
            }

            return identities.ContainsKey(referenceName);
        }
    }

    /// <summary>
    /// Gets selected reference names whose generated catalog identity has not been discovered yet.
    /// </summary>
    /// <param name="selection">The active reference selection.</param>
    /// <returns>The reference names ordered for deterministic refresh work.</returns>
    public IReadOnlyList<string> TakeRefreshCandidateReferenceNames(VbaProjectReferenceSelection selection)
    {
        lock (gate)
        {
            return TakeRefreshCandidateReferenceNamesCore(selection);
        }
    }

    internal VbaProjectReferenceCatalogRefreshBatchReservation ReserveRefreshCandidateBatch(
        VbaProjectReferenceSelection selection,
        string? scopeKey = null,
        string? selectionFingerprint = null)
    {
        lock (gate)
        {
            return new VbaProjectReferenceCatalogRefreshBatchReservation(
                this,
                TakeRefreshCandidateReferenceNamesCore(
                    selection,
                    scopeKey,
                    selectionFingerprint),
                scopeKey,
                selectionFingerprint);
        }
    }

    internal async Task<VbaProjectReferenceCatalogRefreshLease> AcquireRefreshLeaseAsync(
        IReadOnlyList<VbaProjectReference> references,
        bool waitForExistingOwners,
        CancellationToken cancellationToken,
        string? scopeKey = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        KeyValuePair<string, SemaphoreSlim>[] ownership;
        lock (gate)
        {
            ownership = references
                .Select(reference => reference.Name)
                .Distinct(VbaProjectReferenceName.Comparer)
                .OrderBy(name => name, VbaProjectReferenceName.OrderingComparer)
                .Select(name =>
                {
                    var authorityKey = CreateAuthorityKey(scopeKey, name);
                    if (!refreshOwnership.TryGetValue(authorityKey, out var semaphore))
                    {
                        semaphore = new SemaphoreSlim(1, 1);
                        refreshOwnership[authorityKey] = semaphore;
                    }

                    return new KeyValuePair<string, SemaphoreSlim>(name, semaphore);
                })
                .ToArray();
        }

        var acquiredNames = new List<string>(ownership.Length);
        var acquiredSemaphores = new List<SemaphoreSlim>(ownership.Length);
        try
        {
            foreach (var (referenceName, semaphore) in ownership)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (waitForExistingOwners)
                {
                    await semaphore.WaitAsync(cancellationToken);
                }
                else if (!semaphore.Wait(0))
                {
                    continue;
                }

                acquiredNames.Add(referenceName);
                acquiredSemaphores.Add(semaphore);
            }

            return new VbaProjectReferenceCatalogRefreshLease(
                acquiredNames,
                acquiredSemaphores);
        }
        catch
        {
            foreach (var semaphore in acquiredSemaphores)
            {
                semaphore.Release();
            }

            throw;
        }
    }

    /// <summary>
    /// Stores a discovery result in the cache.
    /// </summary>
    /// <param name="result">The discovery result to store.</param>
    public void Store(VbaProjectReferenceCatalogDiscoveryResult result)
    {
        lock (gate)
        {
            refreshesInProgress.Remove(result.ReferenceName);
            StoreCore(result);
        }
    }

    internal void StoreReservedDiscoveryResult(
        string reservedReferenceName,
        VbaProjectReferenceCatalogDiscoveryResult result,
        string? scopeKey,
        string? selectionFingerprint)
    {
        lock (gate)
        {
            try
            {
                StoreCore(result, scopeKey, selectionFingerprint);
            }
            finally
            {
                refreshesInProgress.Remove(CreateAuthorityKey(scopeKey, reservedReferenceName));
            }
        }
    }

    /// <summary>
    /// Stores a current persisted catalog and marks its TypeLib identity as resolved.
    /// </summary>
    /// <param name="entry">The persisted catalog entry.</param>
    public void StorePersistedCatalog(VbaProjectReferenceCatalogPersistentEntry entry)
        => StorePersistedCatalog(
            entry,
            scopeKey: null,
            identityAuthoritative: true,
            selectionFingerprint: null);

    internal void StorePersistedCatalog(
        VbaProjectReferenceCatalogPersistentEntry entry,
        string? scopeKey,
        bool identityAuthoritative,
        string? selectionFingerprint)
    {
        lock (gate)
        {
            if (scopeKey is not null)
            {
                StoreScopedBinding(
                    scopeKey,
                    identityAuthoritative ? entry.Identity : null,
                    entry.Catalog,
                    VbaProjectReferenceCatalogSource.Persisted,
                    selectionFingerprint);
                return;
            }

            identities[entry.Identity.ReferenceName] = entry.Identity;
            catalogSet = catalogSet.WithCatalog(entry.Catalog);
            catalogSources[entry.Identity.ReferenceName] = VbaProjectReferenceCatalogSource.Persisted;
            version++;
            MarkReferenceChanged(entry.Identity.ReferenceName);
        }
    }

    /// <summary>
    /// Stores a usable stale catalog without marking its TypeLib identity as current.
    /// </summary>
    /// <param name="catalog">The stale catalog to make available to editor features.</param>
    public void StoreStaleCatalog(VbaProjectReferenceCatalog catalog)
        => StoreStaleCatalog(catalog, scopeKey: null);

    internal void StoreStaleCatalog(
        VbaProjectReferenceCatalog catalog,
        string? scopeKey)
    {
        lock (gate)
        {
            if (scopeKey is not null)
            {
                StoreScopedBinding(
                    scopeKey,
                    identity: null,
                    catalog,
                    VbaProjectReferenceCatalogSource.StalePersisted,
                    selectionFingerprint: null);
                return;
            }

            catalogSet = catalogSet.WithCatalog(catalog);
            catalogSources[catalog.ReferenceName] = VbaProjectReferenceCatalogSource.StalePersisted;
            version++;
            MarkReferenceChanged(catalog.ReferenceName);
        }
    }

    /// <summary>
    /// Releases a refresh candidate without storing discovery metadata.
    /// </summary>
    /// <param name="referenceName">The reference name whose refresh attempt ended before a result was stored.</param>
    public void ReleaseRefreshCandidate(string referenceName)
    {
        lock (gate)
        {
            refreshesInProgress.Remove(referenceName);
        }
    }

    internal void ReleaseRefreshCandidates(IEnumerable<string> referenceNames)
        => ReleaseRefreshCandidates(referenceNames, scopeKey: null);

    internal void ReleaseRefreshCandidates(
        IEnumerable<string> referenceNames,
        string? scopeKey)
    {
        lock (gate)
        {
            foreach (var referenceName in referenceNames)
            {
                refreshesInProgress.Remove(CreateAuthorityKey(scopeKey, referenceName));
            }
        }
    }

    private IReadOnlyList<string> TakeRefreshCandidateReferenceNamesCore(
        VbaProjectReferenceSelection selection,
        string? scopeKey = null,
        string? selectionFingerprint = null)
    {
        var candidateNames = selection.References
            .Where(reference => !HasIdentityCore(
                reference.Name,
                scopeKey,
                selectionFingerprint))
            .Where(reference => !refreshesInProgress.Contains(
                CreateAuthorityKey(scopeKey, reference.Name)))
            .Select(reference => reference.Name)
            .Distinct(VbaProjectReferenceName.Comparer)
            .OrderBy(name => name, VbaProjectReferenceName.OrderingComparer)
            .ToArray();
        foreach (var candidateName in candidateNames)
        {
            refreshesInProgress.Add(CreateAuthorityKey(scopeKey, candidateName));
        }

        return candidateNames;
    }

    private void StoreCore(
        VbaProjectReferenceCatalogDiscoveryResult result,
        string? scopeKey = null,
        string? selectionFingerprint = null)
    {
        if (!result.IsSuccessful)
        {
            return;
        }

        if (scopeKey is not null)
        {
            if (result.Catalog is not null)
            {
                StoreScopedBinding(
                    scopeKey,
                    result.Identities[0],
                    result.Catalog,
                    VbaProjectReferenceCatalogSource.Generated,
                    selectionFingerprint);
            }

            return;
        }

        identities[result.ReferenceName] = result.Identities[0];

        if (result.Catalog is not null)
        {
            catalogSet = catalogSet.WithCatalog(result.Catalog);
            catalogSources[result.ReferenceName] = VbaProjectReferenceCatalogSource.Generated;
            version++;
            MarkReferenceChanged(result.ReferenceName);
        }
    }

    private void MarkReferenceChanged(string referenceName)
        => referenceChangeVersions[referenceName] = version;

    private bool HasIdentityCore(
        string referenceName,
        string? scopeKey,
        string? selectionFingerprint)
        => (scopeKey is not null
                && scopedBindings.TryGetValue(scopeKey, out var scopeBindings)
                && scopeBindings.TryGetValue(referenceName, out var scopedBinding)
                && scopedBinding.Identity is not null
                && string.Equals(
                    scopedBinding.SelectionFingerprint,
                    selectionFingerprint,
                    StringComparison.Ordinal))
            || identities.ContainsKey(referenceName);

    private void StoreScopedBinding(
        string scopeKey,
        VbaProjectReferenceCatalogIdentity? identity,
        VbaProjectReferenceCatalog catalog,
        VbaProjectReferenceCatalogSource source,
        string? selectionFingerprint)
    {
        if (!scopedBindings.TryGetValue(scopeKey, out var scopeBindings))
        {
            scopeBindings = new Dictionary<string, ScopedReferenceCatalogBinding>(
                VbaProjectReferenceName.Comparer);
            scopedBindings[scopeKey] = scopeBindings;
        }

        version++;
        scopeBindings[catalog.ReferenceName] = new ScopedReferenceCatalogBinding(
            identity,
            catalog,
            source,
            version,
            selectionFingerprint);
    }

    private static string CreateAuthorityKey(string? scopeKey, string referenceName)
        => scopeKey is null
            ? referenceName
            : $"{scopeKey}\u001d{referenceName}";

    private sealed record ScopedReferenceCatalogBinding(
        VbaProjectReferenceCatalogIdentity? Identity,
        VbaProjectReferenceCatalog Catalog,
        VbaProjectReferenceCatalogSource Source,
        long ChangeVersion,
        string? SelectionFingerprint);
}

/// <summary>
/// Owns one atomically reserved batch of reference catalog refresh candidates.
/// </summary>
internal sealed class VbaProjectReferenceCatalogRefreshBatchReservation
{
    private readonly VbaProjectReferenceCatalogCache cache;
    private readonly IReadOnlyList<string> referenceNames;
    private readonly HashSet<string> remainingReferenceNames;
    private readonly string? scopeKey;
    private readonly string? selectionFingerprint;

    internal VbaProjectReferenceCatalogRefreshBatchReservation(
        VbaProjectReferenceCatalogCache cache,
        IReadOnlyList<string> referenceNames,
        string? scopeKey,
        string? selectionFingerprint)
    {
        this.cache = cache;
        this.referenceNames = referenceNames;
        this.scopeKey = scopeKey;
        this.selectionFingerprint = selectionFingerprint;
        remainingReferenceNames = new HashSet<string>(
            referenceNames,
            VbaProjectReferenceName.Comparer);
    }

    internal IReadOnlyList<string> ReferenceNames => referenceNames;

    internal void StoreDiscoveryResult(
        string referenceName,
        VbaProjectReferenceCatalogDiscoveryResult result)
    {
        if (!remainingReferenceNames.Remove(referenceName))
        {
            throw new InvalidOperationException(
                $"Reference catalog refresh reservation for '{referenceName}' is not active.");
        }

        cache.StoreReservedDiscoveryResult(
            referenceName,
            result,
            scopeKey,
            selectionFingerprint);
    }

    internal void ReleaseRemaining()
    {
        if (remainingReferenceNames.Count == 0)
        {
            return;
        }

        var referenceNamesToRelease = remainingReferenceNames.ToArray();
        remainingReferenceNames.Clear();
        cache.ReleaseRefreshCandidates(referenceNamesToRelease, scopeKey);
    }
}

/// <summary>
/// Owns selected reference refreshes across persisted preload and discovery.
/// </summary>
internal sealed class VbaProjectReferenceCatalogRefreshLease : IDisposable
{
    private readonly IReadOnlyList<SemaphoreSlim> semaphores;
    private int disposed;

    internal VbaProjectReferenceCatalogRefreshLease(
        IReadOnlyList<string> referenceNames,
        IReadOnlyList<SemaphoreSlim> semaphores)
    {
        ReferenceNames = referenceNames;
        this.semaphores = semaphores;
    }

    internal IReadOnlyList<string> ReferenceNames { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        foreach (var semaphore in semaphores)
        {
            semaphore.Release();
        }
    }
}

/// <summary>
/// Represents a versioned reference catalog cache snapshot.
/// </summary>
/// <param name="CatalogSet">The catalog set available to editor features.</param>
/// <param name="Version">The cache version that changes when the catalog set changes.</param>
public sealed record VbaProjectReferenceCatalogCacheState(
    VbaProjectReferenceCatalogSet CatalogSet,
    long Version);

internal readonly record struct VbaProjectReferenceCatalogSelectionState(
    VbaProjectReferenceCatalogSet CatalogSet,
    long Revision,
    IReadOnlyDictionary<string, VbaProjectReferenceCatalogSource> Sources);

/// <summary>
/// Identifies where the active catalog for a reference came from.
/// </summary>
public enum VbaProjectReferenceCatalogSource
{
    /// <summary>
    /// No editor metadata catalog is available for the reference.
    /// </summary>
    Unavailable,

    /// <summary>
    /// The catalog came from the bundled minimal metadata shipped with the language server.
    /// </summary>
    Bundled,

    /// <summary>
    /// The catalog came from a current persisted generated cache entry.
    /// </summary>
    Persisted,

    /// <summary>
    /// The catalog came from a stale persisted generated cache entry.
    /// </summary>
    StalePersisted,

    /// <summary>
    /// The catalog was generated from TypeLib metadata in the current session.
    /// </summary>
    Generated
}

/// <summary>
/// Identifies how a reference catalog refresh request was handled.
/// </summary>
public enum VbaProjectReferenceCatalogRefreshStatus
{
    /// <summary>
    /// The reference was refreshed through catalog discovery.
    /// </summary>
    Refreshed,

    /// <summary>
    /// The reference already had a current persisted catalog, so expensive discovery was skipped.
    /// </summary>
    SkippedValidPersistentCache,

    /// <summary>
    /// A stale persisted catalog was loaded while refresh continues in the background.
    /// </summary>
    LoadedStalePersistentCache,

    /// <summary>
    /// A persisted cache entry could not be read, but refresh can continue.
    /// </summary>
    PersistentCacheReadWarning
}

/// <summary>
/// Represents one reference catalog refresh result.
/// </summary>
/// <param name="ReferenceName">The reference name refreshed.</param>
/// <param name="DiscoveryResult">The discovery result for the reference.</param>
/// <param name="Status">How the refresh request was handled.</param>
/// <param name="Source">The best active catalog source after this result was handled.</param>
/// <param name="Phase">The refresh phase that produced the result.</param>
/// <param name="ExpensiveMetadataRan">Whether TypeLib discovery or metadata extraction was scheduled.</param>
/// <param name="Elapsed">The elapsed time spent in the phase.</param>
/// <param name="WarningMessage">A non-fatal warning associated with the result.</param>
public sealed record VbaProjectReferenceCatalogRefreshResult(
    string ReferenceName,
    VbaProjectReferenceCatalogDiscoveryResult DiscoveryResult,
    VbaProjectReferenceCatalogRefreshStatus Status = VbaProjectReferenceCatalogRefreshStatus.Refreshed,
    VbaProjectReferenceCatalogSource Source = VbaProjectReferenceCatalogSource.Unavailable,
    string Phase = "typelib-discovery",
    bool ExpensiveMetadataRan = true,
    TimeSpan Elapsed = default,
    string? WarningMessage = null);

internal interface IVbaProjectReferenceCatalogMutationLane
{
    Task CommitAsync(
        string authorityKey,
        Action commit,
        CancellationToken cancellationToken);
}

internal sealed class InlineVbaProjectReferenceCatalogMutationLane
    : IVbaProjectReferenceCatalogMutationLane
{
    public static InlineVbaProjectReferenceCatalogMutationLane Instance { get; } = new();

    private InlineVbaProjectReferenceCatalogMutationLane()
    {
    }

    public Task CommitAsync(
        string authorityKey,
        Action commit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorityKey);
        ArgumentNullException.ThrowIfNull(commit);
        cancellationToken.ThrowIfCancellationRequested();
        commit();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Refreshes missing reference catalogs for an active reference selection.
/// </summary>
public sealed class VbaProjectReferenceCatalogRefreshService
{
    private readonly VbaProjectReferenceCatalogCache cache;
    private readonly IVbaProjectReferenceCatalogDiscovery discovery;
    private readonly IVbaProjectReferenceCatalogPersistentStore? persistentStore;
    private readonly IVbaProjectReferenceCatalogRefreshWorker refreshWorker;
    private readonly IVbaProjectReferenceCatalogLifecycleObserver lifecycleObserver;
    private readonly object mutationLaneGate = new();
    private IVbaProjectReferenceCatalogMutationLane mutationLane =
        InlineVbaProjectReferenceCatalogMutationLane.Instance;

    internal bool UsesContextSpecificDiscovery =>
        discovery is IVbaProjectReferenceCatalogContextDiscoveryFactory contextFactory
        && contextFactory.UsesContextSpecificResolution;

    /// <summary>
    /// Creates a catalog refresh service.
    /// </summary>
    /// <param name="cache">The catalog cache to read and update.</param>
    /// <param name="discovery">The discovery service used for missing references.</param>
    public VbaProjectReferenceCatalogRefreshService(
        VbaProjectReferenceCatalogCache cache,
        IVbaProjectReferenceCatalogDiscovery discovery)
        : this(cache, discovery, null, LowImpactReferenceCatalogRefreshWorker.Shared)
    {
    }

    /// <summary>
    /// Creates a catalog refresh service.
    /// </summary>
    /// <param name="cache">The catalog cache to read and update.</param>
    /// <param name="discovery">The discovery service used for missing references.</param>
    /// <param name="persistentStore">The optional persistent store used across language-server sessions.</param>
    public VbaProjectReferenceCatalogRefreshService(
        VbaProjectReferenceCatalogCache cache,
        IVbaProjectReferenceCatalogDiscovery discovery,
        IVbaProjectReferenceCatalogPersistentStore? persistentStore)
        : this(cache, discovery, persistentStore, LowImpactReferenceCatalogRefreshWorker.Shared)
    {
    }

    /// <summary>
    /// Creates a catalog refresh service.
    /// </summary>
    /// <param name="cache">The catalog cache to read and update.</param>
    /// <param name="discovery">The discovery service used for missing references.</param>
    /// <param name="persistentStore">The optional persistent store used across language-server sessions.</param>
    /// <param name="refreshWorker">The worker used to schedule low-impact discovery work.</param>
    public VbaProjectReferenceCatalogRefreshService(
        VbaProjectReferenceCatalogCache cache,
        IVbaProjectReferenceCatalogDiscovery discovery,
        IVbaProjectReferenceCatalogPersistentStore? persistentStore,
        IVbaProjectReferenceCatalogRefreshWorker refreshWorker)
        : this(
            cache,
            discovery,
            persistentStore,
            refreshWorker,
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance)
    {
    }

    internal VbaProjectReferenceCatalogRefreshService(
        VbaProjectReferenceCatalogCache cache,
        IVbaProjectReferenceCatalogDiscovery discovery,
        IVbaProjectReferenceCatalogPersistentStore? persistentStore,
        IVbaProjectReferenceCatalogRefreshWorker refreshWorker,
        IVbaProjectReferenceCatalogLifecycleObserver lifecycleObserver)
    {
        this.cache = cache;
        this.discovery = discovery;
        this.persistentStore = persistentStore;
        this.refreshWorker = refreshWorker;
        this.lifecycleObserver = lifecycleObserver;
    }

    internal void AttachMutationLane(IVbaProjectReferenceCatalogMutationLane catalogMutationLane)
    {
        ArgumentNullException.ThrowIfNull(catalogMutationLane);
        lock (mutationLaneGate)
        {
            if (!ReferenceEquals(
                    mutationLane,
                    InlineVbaProjectReferenceCatalogMutationLane.Instance)
                && !ReferenceEquals(mutationLane, catalogMutationLane))
            {
                throw new InvalidOperationException(
                    "The reference catalog refresh service is already attached to another mutation lane.");
            }

            mutationLane = catalogMutationLane;
        }
    }

    /// <summary>
    /// Discovers generated catalogs for selected references that have not been resolved yet.
    /// </summary>
    /// <param name="selection">The active reference selection.</param>
    /// <param name="cancellationToken">A cancellation token for refresh work.</param>
    /// <returns>The refresh results for references that were attempted.</returns>
    public async Task<IReadOnlyList<VbaProjectReferenceCatalogRefreshResult>> RefreshAsync(
        VbaProjectReferenceSelection selection,
        CancellationToken cancellationToken = default)
    {
        lifecycleObserver.Record(new VbaProjectReferenceCatalogLifecycleEvent(
            VbaProjectReferenceCatalogLifecycleOperation.ExplicitRetry));
        return await RefreshCoreAsync(
            selection,
            waitForExistingOwners: false,
            persistedPreloadCompleted: null,
            refreshContext: null,
            cancellationToken);
    }

    internal Task<IReadOnlyList<VbaProjectReferenceCatalogRefreshResult>>
        RefreshAutomaticallyAsync(
            VbaProjectReferenceSelection selection,
            CancellationToken cancellationToken)
        => RefreshCoreAsync(
            selection,
            waitForExistingOwners: true,
            persistedPreloadCompleted: null,
            refreshContext: null,
            cancellationToken);

    internal Task<IReadOnlyList<VbaProjectReferenceCatalogRefreshResult>>
        RefreshAutomaticallyAsync(
            VbaProjectReferenceSelection selection,
            Action<IReadOnlyList<VbaProjectReferenceCatalogRefreshResult>> persistedPreloadCompleted,
            CancellationToken cancellationToken)
        => RefreshCoreAsync(
            selection,
            waitForExistingOwners: true,
            persistedPreloadCompleted,
            refreshContext: null,
            cancellationToken);

    internal Task<IReadOnlyList<VbaProjectReferenceCatalogRefreshResult>>
        RefreshAutomaticallyAsync(
            VbaProjectReferenceCatalogRefreshContext context,
            CancellationToken cancellationToken)
        => RefreshCoreAsync(
            context.Selection,
            waitForExistingOwners: true,
            persistedPreloadCompleted: null,
            context,
            cancellationToken);

    internal Task<IReadOnlyList<VbaProjectReferenceCatalogRefreshResult>>
        RefreshAutomaticallyAsync(
            VbaProjectReferenceCatalogRefreshContext context,
            Action<IReadOnlyList<VbaProjectReferenceCatalogRefreshResult>> persistedPreloadCompleted,
            CancellationToken cancellationToken)
        => RefreshCoreAsync(
            context.Selection,
            waitForExistingOwners: true,
            persistedPreloadCompleted,
            context,
            cancellationToken);

    private async Task<IReadOnlyList<VbaProjectReferenceCatalogRefreshResult>> RefreshCoreAsync(
        VbaProjectReferenceSelection selection,
        bool waitForExistingOwners,
        Action<IReadOnlyList<VbaProjectReferenceCatalogRefreshResult>>? persistedPreloadCompleted,
        VbaProjectReferenceCatalogRefreshContext? refreshContext,
        CancellationToken cancellationToken)
    {
        if (refreshContext?.IsCurrent?.Invoke() == false)
        {
            return [];
        }

        using var refreshLease = await cache.AcquireRefreshLeaseAsync(
            selection.References,
            waitForExistingOwners,
            cancellationToken,
            refreshContext?.ScopeKey);
        if (refreshLease.ReferenceNames.Count == 0)
        {
            return [];
        }

        var ownedReferenceNames = refreshLease.ReferenceNames.ToHashSet(
            VbaProjectReferenceName.Comparer);
        var ownedSelection = selection with
        {
            References = selection.References
                .Where(reference => ownedReferenceNames.Contains(reference.Name))
                .ToArray()
        };
        var results = new List<VbaProjectReferenceCatalogRefreshResult>();
        var persistedPreloadResults = await PreloadPersistedCatalogsAsync(
            ownedSelection,
            refreshContext?.ScopeKey,
            refreshContext?.SelectionFingerprint,
            refreshContext?.IsCurrent,
            cancellationToken);
        results.AddRange(persistedPreloadResults);
        persistedPreloadCompleted?.Invoke(persistedPreloadResults);
        cancellationToken.ThrowIfCancellationRequested();
        var activeDiscovery = refreshContext is not null
            && discovery is IVbaProjectReferenceCatalogContextDiscoveryFactory contextFactory
            && contextFactory.UsesContextSpecificResolution
                ? contextFactory.CreateContextDiscovery(refreshContext)
                : discovery;
        results.AddRange(await DiscoverMissingCatalogsAsync(
            ownedSelection,
            activeDiscovery,
            refreshContext?.ScopeKey,
            refreshContext?.SelectionFingerprint,
            refreshContext?.IsCurrent,
            cancellationToken));
        return results;
    }

    /// <summary>
    /// Discovers catalogs that remain unresolved after any lifecycle-owned persisted preload.
    /// </summary>
    /// <param name="selection">The active reference selection.</param>
    /// <param name="cancellationToken">A cancellation token for discovery work.</param>
    /// <returns>The discovery results for references that were attempted.</returns>
    private async Task<IReadOnlyList<VbaProjectReferenceCatalogRefreshResult>> DiscoverMissingCatalogsAsync(
        VbaProjectReferenceSelection selection,
        IVbaProjectReferenceCatalogDiscovery activeDiscovery,
        string? scopeKey,
        string? selectionFingerprint,
        Func<bool>? commitGuard,
        CancellationToken cancellationToken = default)
    {
        var results = new List<VbaProjectReferenceCatalogRefreshResult>();
        var batchDiscovery = activeDiscovery is IVbaProjectReferenceCatalogDiscoveryBatchFactory batchFactory
            ? batchFactory.CreateBatchDiscovery()
            : activeDiscovery;
        var reservation = cache.ReserveRefreshCandidateBatch(
            selection,
            scopeKey,
            selectionFingerprint);
        try
        {
            foreach (var referenceName in reservation.ReferenceNames)
            {
                VbaProjectReferenceCatalogDiscoveryResult discoveryResult;
                var sourceBeforeDiscovery = cache.GetCatalogSource(referenceName, scopeKey);
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    lifecycleObserver.Record(new VbaProjectReferenceCatalogLifecycleEvent(
                        VbaProjectReferenceCatalogLifecycleOperation.Discovery,
                        ReferenceName: referenceName));
                    discoveryResult = await refreshWorker.DiscoverAsync(
                        batchDiscovery,
                        referenceName,
                        cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    discoveryResult = VbaProjectReferenceCatalogDiscoveryResult.Failure(referenceName, ex.Message);
                }
                finally
                {
                    stopwatch.Stop();
                }

                cancellationToken.ThrowIfCancellationRequested();
                discoveryResult = ValidateDiscoveryResultReferenceName(
                    referenceName,
                    discoveryResult);
                var committed = await CommitCatalogMutationAsync(
                    CreateMutationAuthorityKey(scopeKey, referenceName),
                    () => reservation.StoreDiscoveryResult(referenceName, discoveryResult),
                    commitGuard,
                    cancellationToken);
                if (!committed)
                {
                    return results;
                }

                if (discoveryResult.HasUsableCatalog)
                {
                    lifecycleObserver.Record(new VbaProjectReferenceCatalogLifecycleEvent(
                        VbaProjectReferenceCatalogLifecycleOperation.Commit,
                        ReferenceName: referenceName));
                }

                var saveWarning = await SavePersistedCatalogAsync(
                    discoveryResult,
                    scopeKey,
                    selectionFingerprint,
                    cancellationToken);
                var source = discoveryResult.HasUsableCatalog
                    ? VbaProjectReferenceCatalogSource.Generated
                    : sourceBeforeDiscovery;
                results.Add(new VbaProjectReferenceCatalogRefreshResult(
                    referenceName,
                    discoveryResult,
                    Source: source,
                    Phase: "typelib-discovery",
                    ExpensiveMetadataRan: true,
                    Elapsed: stopwatch.Elapsed,
                    WarningMessage: saveWarning));
            }

            return results;
        }
        finally
        {
            reservation.ReleaseRemaining();
        }
    }

    /// <summary>
    /// Loads usable persisted catalogs into memory without running TypeLib discovery.
    /// </summary>
    /// <param name="selection">The active reference selection.</param>
    /// <returns>The preload results for references that had persisted cache state.</returns>
    private async Task<IReadOnlyList<VbaProjectReferenceCatalogRefreshResult>> PreloadPersistedCatalogsAsync(
        VbaProjectReferenceSelection selection,
        string? scopeKey,
        string? selectionFingerprint,
        Func<bool>? commitGuard,
        CancellationToken cancellationToken = default)
    {
        if (persistentStore is null)
        {
            return [];
        }

        var scopedPersistentStore = persistentStore
            as IVbaProjectReferenceCatalogScopedPersistentStore;
        if (scopeKey is not null
            && (selectionFingerprint is null
                || scopedPersistentStore is null))
        {
            return [];
        }

        var results = new List<VbaProjectReferenceCatalogRefreshResult>();
        foreach (var referenceName in selection.References
            .Select(reference => reference.Name)
            .Distinct(VbaProjectReferenceName.Comparer)
            .OrderBy(name => name, VbaProjectReferenceName.OrderingComparer))
        {
            if (cache.HasIdentity(referenceName, scopeKey, selectionFingerprint))
            {
                continue;
            }

            if (cache.GetCatalogSource(referenceName, scopeKey)
                == VbaProjectReferenceCatalogSource.StalePersisted)
            {
                continue;
            }

            var stopwatch = Stopwatch.StartNew();
            lifecycleObserver.Record(new VbaProjectReferenceCatalogLifecycleEvent(
                VbaProjectReferenceCatalogLifecycleOperation.PersistedPreload,
                ReferenceName: referenceName));
            var loadResult = scopeKey is null
                ? await persistentStore.LoadAsync(referenceName, cancellationToken)
                : await scopedPersistentStore!.LoadScopedAsync(
                    referenceName,
                    scopeKey,
                    selectionFingerprint!,
                    cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            stopwatch.Stop();
            if (loadResult.Entry is null && loadResult.WarningMessage is not null)
            {
                results.Add(new VbaProjectReferenceCatalogRefreshResult(
                    referenceName,
                    VbaProjectReferenceCatalogDiscoveryResult.Failure(referenceName, loadResult.WarningMessage),
                    VbaProjectReferenceCatalogRefreshStatus.PersistentCacheReadWarning,
                    cache.GetCatalogSource(referenceName, scopeKey),
                    "persistent-load",
                    ExpensiveMetadataRan: false,
                    Elapsed: stopwatch.Elapsed,
                    WarningMessage: loadResult.WarningMessage));
                continue;
            }

            if (loadResult.Entry is not null
                && loadResult.Status == VbaProjectReferenceCatalogPersistentLoadStatus.Current)
            {
                var committed = await CommitCatalogMutationAsync(
                    CreateMutationAuthorityKey(scopeKey, referenceName),
                    () => cache.StorePersistedCatalog(
                        loadResult.Entry,
                        scopeKey,
                        identityAuthoritative: true,
                        selectionFingerprint),
                    commitGuard,
                    cancellationToken);
                if (!committed)
                {
                    return results;
                }

                lifecycleObserver.Record(new VbaProjectReferenceCatalogLifecycleEvent(
                    VbaProjectReferenceCatalogLifecycleOperation.Commit,
                    ReferenceName: referenceName));
                results.Add(new VbaProjectReferenceCatalogRefreshResult(
                    referenceName,
                    VbaProjectReferenceCatalogDiscoveryResult.Success(
                        loadResult.Entry.Identity,
                        loadResult.Entry.Catalog),
                    VbaProjectReferenceCatalogRefreshStatus.SkippedValidPersistentCache,
                    VbaProjectReferenceCatalogSource.Persisted,
                    "persistent-load",
                    ExpensiveMetadataRan: false,
                    Elapsed: stopwatch.Elapsed));
                continue;
            }

            if (loadResult.Entry is not null
                && loadResult.Status == VbaProjectReferenceCatalogPersistentLoadStatus.Stale)
            {
                var committed = await CommitCatalogMutationAsync(
                    CreateMutationAuthorityKey(scopeKey, referenceName),
                    () => cache.StoreStaleCatalog(loadResult.Entry.Catalog, scopeKey),
                    commitGuard,
                    cancellationToken);
                if (!committed)
                {
                    return results;
                }

                lifecycleObserver.Record(new VbaProjectReferenceCatalogLifecycleEvent(
                    VbaProjectReferenceCatalogLifecycleOperation.Commit,
                    ReferenceName: referenceName));
                results.Add(new VbaProjectReferenceCatalogRefreshResult(
                    referenceName,
                    VbaProjectReferenceCatalogDiscoveryResult.Success(
                        loadResult.Entry.Identity,
                        loadResult.Entry.Catalog),
                    VbaProjectReferenceCatalogRefreshStatus.LoadedStalePersistentCache,
                    VbaProjectReferenceCatalogSource.StalePersisted,
                    "persistent-load",
                    ExpensiveMetadataRan: false,
                    Elapsed: stopwatch.Elapsed,
                    WarningMessage: loadResult.WarningMessage));
            }
        }

        return results;
    }

    private async Task<string?> SavePersistedCatalogAsync(
        VbaProjectReferenceCatalogDiscoveryResult discoveryResult,
        string? scopeKey,
        string? selectionFingerprint,
        CancellationToken cancellationToken)
    {
        if (persistentStore is null || !discoveryResult.HasUsableCatalog)
        {
            return null;
        }

        try
        {
            var entry = new VbaProjectReferenceCatalogPersistentEntry(
                discoveryResult.Identities[0],
                discoveryResult.Catalog!);
            if (scopeKey is null)
            {
                await persistentStore.SaveAsync(entry, cancellationToken);
            }
            else if (selectionFingerprint is not null
                && persistentStore is IVbaProjectReferenceCatalogScopedPersistentStore scopedStore)
            {
                await scopedStore.SaveScopedAsync(
                    entry,
                    scopeKey,
                    selectionFingerprint,
                    cancellationToken);
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Persisted reference catalog cache for '{discoveryResult.ReferenceName}' could not be written: {ex.Message}";
        }
    }

    private async Task<bool> CommitCatalogMutationAsync(
        string authorityKey,
        Action commit,
        Func<bool>? commitGuard,
        CancellationToken cancellationToken)
    {
        IVbaProjectReferenceCatalogMutationLane currentMutationLane;
        lock (mutationLaneGate)
        {
            currentMutationLane = mutationLane;
        }

        var committed = false;
        await currentMutationLane.CommitAsync(
            authorityKey,
            () =>
            {
                if (commitGuard?.Invoke() == false)
                {
                    return;
                }

                commit();
                committed = true;
            },
            cancellationToken);
        return committed;
    }

    private static string CreateMutationAuthorityKey(
        string? scopeKey,
        string referenceName)
        => scopeKey is null
            ? referenceName
            : $"{scopeKey}\u001d{referenceName}";

    private static VbaProjectReferenceCatalogDiscoveryResult ValidateDiscoveryResultReferenceName(
        string requestedReferenceName,
        VbaProjectReferenceCatalogDiscoveryResult result)
    {
        if (!result.IsSuccessful)
        {
            return result;
        }

        var namesMatch =
            VbaProjectReferenceName.AreEquivalent(
                result.ReferenceName,
                requestedReferenceName)
            && result.Identities.All(identity =>
                VbaProjectReferenceName.AreEquivalent(
                    identity.ReferenceName,
                    requestedReferenceName))
            && (result.Catalog is null
                || VbaProjectReferenceName.AreEquivalent(
                    result.Catalog.ReferenceName,
                    requestedReferenceName));
        return namesMatch
            ? result
            : VbaProjectReferenceCatalogDiscoveryResult.Failure(
                requestedReferenceName,
                $"Reference catalog discovery for '{requestedReferenceName}' returned metadata owned by a different reference.");
    }
}
