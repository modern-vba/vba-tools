using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.Versioning;
using VbaLanguageServer.ProjectModel;

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
public sealed record VbaProjectReferenceCatalogDiscoveryResult(
    string ReferenceName,
    IReadOnlyList<VbaProjectReferenceCatalogIdentity> Identities,
    VbaProjectReferenceCatalog? Catalog,
    string? ErrorMessage = null)
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
    /// Gets whether discovery produced usable catalog metadata.
    /// </summary>
    public bool HasUsableCatalog => Catalog is not null;

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
        => new(referenceName, identities, null);

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

/// <summary>
/// Represents one TypeLib registry entry that may satisfy a VBA project reference.
/// </summary>
/// <param name="ReferenceName">The human-visible TypeLib description.</param>
/// <param name="Guid">The TypeLib GUID.</param>
/// <param name="MajorVersion">The TypeLib major version.</param>
/// <param name="MinorVersion">The TypeLib minor version.</param>
/// <param name="Lcid">The TypeLib locale identifier.</param>
/// <param name="Path">The registry-resolved TypeLib path.</param>
public sealed record TypeLibRegistryEntry(
    string ReferenceName,
    string Guid,
    int MajorVersion,
    int MinorVersion,
    int Lcid,
    string Path);

/// <summary>
/// Reads TypeLib registry entries from the host machine.
/// </summary>
public interface ITypeLibRegistryReader
{
    /// <summary>
    /// Reads TypeLib entries available to VBA references.
    /// </summary>
    /// <returns>The discovered TypeLib registry entries.</returns>
    IReadOnlyList<TypeLibRegistryEntry> ReadTypeLibraries();
}

/// <summary>
/// Discovers reference catalog identities from TypeLib registry entries.
/// </summary>
public sealed class TypeLibReferenceCatalogDiscovery : IVbaProjectReferenceCatalogDiscovery
{
    private readonly ITypeLibRegistryReader registryReader;
    private readonly ITypeLibCatalogMetadataReader metadataReader;

    /// <summary>
    /// Creates a TypeLib-backed catalog discovery service.
    /// </summary>
    /// <param name="registryReader">The registry reader used to enumerate TypeLib entries.</param>
    public TypeLibReferenceCatalogDiscovery(ITypeLibRegistryReader registryReader)
        : this(registryReader, new ComTypeLibCatalogMetadataReader())
    {
    }

    /// <summary>
    /// Creates a TypeLib-backed catalog discovery service.
    /// </summary>
    /// <param name="registryReader">The registry reader used to enumerate TypeLib entries.</param>
    /// <param name="metadataReader">The reader used to extract TypeLib metadata.</param>
    public TypeLibReferenceCatalogDiscovery(
        ITypeLibRegistryReader registryReader,
        ITypeLibCatalogMetadataReader metadataReader)
    {
        this.registryReader = registryReader;
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
        var matches = registryReader
            .ReadTypeLibraries()
            .Where(entry => entry.ReferenceName.Equals(referenceName, StringComparison.OrdinalIgnoreCase))
            .Select(entry => new VbaProjectReferenceCatalogIdentity(
                entry.ReferenceName,
                entry.Guid,
                entry.MajorVersion,
                entry.MinorVersion,
                entry.Lcid,
                entry.Path))
            .DistinctBy(identity => (
                identity.Guid.ToUpperInvariant(),
                identity.MajorVersion,
                identity.MinorVersion,
                identity.Lcid,
                identity.Path.ToUpperInvariant()))
            .ToArray();

        var result = matches.Length switch
        {
            0 => VbaProjectReferenceCatalogDiscoveryResult.Failure(
                referenceName,
                "No matching TypeLib registry entry was found."),
            1 => DiscoverCatalog(referenceName, matches[0], cancellationToken),
            _ => VbaProjectReferenceCatalogDiscoveryResult.Ambiguous(referenceName, matches)
        };
        return Task.FromResult(result);
    }

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
}

/// <summary>
/// Reads TypeLib entries from the Windows registry.
/// </summary>
public sealed class RegistryTypeLibRegistryReader : ITypeLibRegistryReader
{
    /// <summary>
    /// Reads TypeLib entries, returning an empty list on non-Windows platforms.
    /// </summary>
    /// <returns>The TypeLib registry entries available on the host.</returns>
    public IReadOnlyList<TypeLibRegistryEntry> ReadTypeLibraries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        return ReadWindowsTypeLibraries();
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<TypeLibRegistryEntry> ReadWindowsTypeLibraries()
    {
        using var typeLibRoot = Registry.ClassesRoot.OpenSubKey("TypeLib");
        if (typeLibRoot is null)
        {
            return [];
        }

        var entries = new List<TypeLibRegistryEntry>();
        foreach (var guid in typeLibRoot.GetSubKeyNames())
        {
            using var guidKey = typeLibRoot.OpenSubKey(guid);
            if (guidKey is null)
            {
                continue;
            }

            foreach (var version in guidKey.GetSubKeyNames())
            {
                using var versionKey = guidKey.OpenSubKey(version);
                if (versionKey is null)
                {
                    continue;
                }

                var description = versionKey.GetValue(null) as string;
                if (string.IsNullOrWhiteSpace(description) || !TryParseVersion(version, out var major, out var minor))
                {
                    continue;
                }

                foreach (var lcid in versionKey.GetSubKeyNames())
                {
                    if (!int.TryParse(lcid, out var lcidValue))
                    {
                        continue;
                    }

                    using var lcidKey = versionKey.OpenSubKey(lcid);
                    var path = ReadTypeLibPath(lcidKey);
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }

                    entries.Add(new TypeLibRegistryEntry(
                        description,
                        guid,
                        major,
                        minor,
                        lcidValue,
                        path));
                }
            }
        }

        return entries;
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadTypeLibPath(RegistryKey? lcidKey)
    {
        if (lcidKey is null)
        {
            return null;
        }

        foreach (var platform in new[] { "win64", "win32" })
        {
            using var platformKey = lcidKey.OpenSubKey(platform);
            var rawPath = platformKey?.GetValue(null) as string;
            if (!string.IsNullOrWhiteSpace(rawPath))
            {
                return Environment.ExpandEnvironmentVariables(rawPath);
            }
        }

        return null;
    }

    private static bool TryParseVersion(string version, out int major, out int minor)
    {
        var parts = version.Split('.', 2);
        if (parts.Length == 2 &&
            int.TryParse(parts[0], out major) &&
            int.TryParse(parts[1], out minor))
        {
            return true;
        }

        major = 0;
        minor = 0;
        return false;
    }
}

/// <summary>
/// Holds the current reference catalog set and discovered identities for background refresh.
/// </summary>
public sealed class VbaProjectReferenceCatalogCache
{
    private readonly object gate = new();
    private VbaProjectReferenceCatalogSet catalogSet;
    private long version;
    private readonly Dictionary<string, VbaProjectReferenceCatalogIdentity> identities = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VbaProjectReferenceCatalogSource> catalogSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> refreshesInProgress = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// Gets the discovered catalog identity snapshot keyed by reference name.
    /// </summary>
    public IReadOnlyDictionary<string, VbaProjectReferenceCatalogIdentity> Identities
    {
        get
        {
            lock (gate)
            {
                return new Dictionary<string, VbaProjectReferenceCatalogIdentity>(identities, StringComparer.OrdinalIgnoreCase);
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
                    StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// Gets the known catalog source for a reference name.
    /// </summary>
    /// <param name="referenceName">The human-visible reference name.</param>
    /// <returns>The catalog source, or <see cref="VbaProjectReferenceCatalogSource.Unavailable"/>.</returns>
    public VbaProjectReferenceCatalogSource GetCatalogSource(string referenceName)
    {
        lock (gate)
        {
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
    {
        lock (gate)
        {
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
            var candidateNames = selection.References
                .Where(reference => !identities.ContainsKey(reference.Name))
                .Where(reference => !refreshesInProgress.Contains(reference.Name))
                .Select(reference => reference.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var candidateName in candidateNames)
            {
                refreshesInProgress.Add(candidateName);
            }

            return candidateNames;
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

            if (result.Identities.Count == 1)
            {
                identities[result.ReferenceName] = result.Identities[0];
            }

            if (result.Catalog is not null)
            {
                catalogSet = catalogSet.WithCatalog(result.Catalog);
                catalogSources[result.ReferenceName] = VbaProjectReferenceCatalogSource.Generated;
                version++;
            }
        }
    }

    /// <summary>
    /// Stores a current persisted catalog and marks its TypeLib identity as resolved.
    /// </summary>
    /// <param name="entry">The persisted catalog entry.</param>
    public void StorePersistedCatalog(VbaProjectReferenceCatalogPersistentEntry entry)
    {
        lock (gate)
        {
            identities[entry.Identity.ReferenceName] = entry.Identity;
            catalogSet = catalogSet.WithCatalog(entry.Catalog);
            catalogSources[entry.Identity.ReferenceName] = VbaProjectReferenceCatalogSource.Persisted;
            version++;
        }
    }

    /// <summary>
    /// Stores a usable stale catalog without marking its TypeLib identity as current.
    /// </summary>
    /// <param name="catalog">The stale catalog to make available to editor features.</param>
    public void StoreStaleCatalog(VbaProjectReferenceCatalog catalog)
    {
        lock (gate)
        {
            catalogSet = catalogSet.WithCatalog(catalog);
            catalogSources[catalog.ReferenceName] = VbaProjectReferenceCatalogSource.StalePersisted;
            version++;
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
}

/// <summary>
/// Represents a versioned reference catalog cache snapshot.
/// </summary>
/// <param name="CatalogSet">The catalog set available to editor features.</param>
/// <param name="Version">The cache version that changes when the catalog set changes.</param>
public sealed record VbaProjectReferenceCatalogCacheState(
    VbaProjectReferenceCatalogSet CatalogSet,
    long Version);

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

/// <summary>
/// Refreshes missing reference catalogs for an active reference selection.
/// </summary>
public sealed class VbaProjectReferenceCatalogRefreshService
{
    private readonly VbaProjectReferenceCatalogCache cache;
    private readonly IVbaProjectReferenceCatalogDiscovery discovery;
    private readonly VbaProjectReferenceCatalogPersistentStore? persistentStore;
    private readonly IVbaProjectReferenceCatalogRefreshWorker refreshWorker;

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
        VbaProjectReferenceCatalogPersistentStore? persistentStore)
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
        VbaProjectReferenceCatalogPersistentStore? persistentStore,
        IVbaProjectReferenceCatalogRefreshWorker refreshWorker)
    {
        this.cache = cache;
        this.discovery = discovery;
        this.persistentStore = persistentStore;
        this.refreshWorker = refreshWorker;
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
        var results = new List<VbaProjectReferenceCatalogRefreshResult>();
        results.AddRange(PreloadPersistedCatalogs(selection));
        var refreshReferenceNames = cache.TakeRefreshCandidateReferenceNames(selection);
        foreach (var referenceName in refreshReferenceNames)
        {
            VbaProjectReferenceCatalogDiscoveryResult discoveryResult;
            var sourceBeforeDiscovery = cache.GetCatalogSource(referenceName);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                discoveryResult = await refreshWorker.DiscoverAsync(discovery, referenceName, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                cache.ReleaseRefreshCandidate(referenceName);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                discoveryResult = VbaProjectReferenceCatalogDiscoveryResult.Failure(referenceName, ex.Message);
            }
            finally
            {
                stopwatch.Stop();
            }

            cache.Store(discoveryResult);
            var saveWarning = SavePersistedCatalog(discoveryResult);
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

    private IReadOnlyList<VbaProjectReferenceCatalogRefreshResult> PreloadPersistedCatalogs(
        VbaProjectReferenceSelection selection)
    {
        if (persistentStore is null)
        {
            return [];
        }

        var results = new List<VbaProjectReferenceCatalogRefreshResult>();
        foreach (var referenceName in selection.References
            .Select(reference => reference.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            if (cache.HasIdentity(referenceName))
            {
                continue;
            }

            var stopwatch = Stopwatch.StartNew();
            var loadResult = persistentStore.Load(referenceName);
            stopwatch.Stop();
            if (loadResult.Entry is null && loadResult.WarningMessage is not null)
            {
                results.Add(new VbaProjectReferenceCatalogRefreshResult(
                    referenceName,
                    VbaProjectReferenceCatalogDiscoveryResult.Failure(referenceName, loadResult.WarningMessage),
                    VbaProjectReferenceCatalogRefreshStatus.PersistentCacheReadWarning,
                    cache.GetCatalogSource(referenceName),
                    "persistent-load",
                    ExpensiveMetadataRan: false,
                    Elapsed: stopwatch.Elapsed,
                    WarningMessage: loadResult.WarningMessage));
                continue;
            }

            if (loadResult.Entry is not null
                && loadResult.Status == VbaProjectReferenceCatalogPersistentLoadStatus.Current)
            {
                cache.StorePersistedCatalog(loadResult.Entry);
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
                cache.StoreStaleCatalog(loadResult.Entry.Catalog);
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

    private string? SavePersistedCatalog(VbaProjectReferenceCatalogDiscoveryResult discoveryResult)
    {
        if (persistentStore is null || discoveryResult.Identities.Count != 1 || discoveryResult.Catalog is null)
        {
            return null;
        }

        try
        {
            persistentStore.Save(new VbaProjectReferenceCatalogPersistentEntry(
                discoveryResult.Identities[0],
                discoveryResult.Catalog));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Persisted reference catalog cache for '{discoveryResult.ReferenceName}' could not be written: {ex.Message}";
        }
    }
}
