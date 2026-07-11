using Microsoft.Win32;
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

    /// <summary>
    /// Creates a TypeLib-backed catalog discovery service.
    /// </summary>
    /// <param name="registryReader">The registry reader used to enumerate TypeLib entries.</param>
    public TypeLibReferenceCatalogDiscovery(ITypeLibRegistryReader registryReader)
    {
        this.registryReader = registryReader;
    }

    /// <summary>
    /// Discovers registry identities matching a reference name.
    /// </summary>
    /// <param name="referenceName">The human-visible reference name.</param>
    /// <param name="cancellationToken">A cancellation token for discovery work.</param>
    /// <returns>The discovery result.</returns>
    public Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
        string referenceName,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
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

            return matches.Length switch
            {
                0 => VbaProjectReferenceCatalogDiscoveryResult.Failure(
                    referenceName,
                    "No matching TypeLib registry entry was found."),
                1 => VbaProjectReferenceCatalogDiscoveryResult.Success(matches[0]),
                _ => VbaProjectReferenceCatalogDiscoveryResult.Ambiguous(referenceName, matches)
            };
        }, cancellationToken);
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
    private readonly Dictionary<string, VbaProjectReferenceCatalogIdentity> identities = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates a catalog cache with an initial catalog set.
    /// </summary>
    /// <param name="catalogSet">The initial catalog set.</param>
    public VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet catalogSet)
    {
        this.catalogSet = catalogSet;
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
    /// Stores a discovery result in the cache.
    /// </summary>
    /// <param name="result">The discovery result to store.</param>
    public void Store(VbaProjectReferenceCatalogDiscoveryResult result)
    {
        lock (gate)
        {
            if (result.Identities.Count == 1)
            {
                identities[result.ReferenceName] = result.Identities[0];
            }

            if (result.Catalog is not null)
            {
                catalogSet = catalogSet.WithCatalog(result.Catalog);
            }
        }
    }
}

/// <summary>
/// Represents one reference catalog refresh result.
/// </summary>
/// <param name="ReferenceName">The reference name refreshed.</param>
/// <param name="DiscoveryResult">The discovery result for the reference.</param>
public sealed record VbaProjectReferenceCatalogRefreshResult(
    string ReferenceName,
    VbaProjectReferenceCatalogDiscoveryResult DiscoveryResult);

/// <summary>
/// Refreshes missing reference catalogs for an active reference selection.
/// </summary>
public sealed class VbaProjectReferenceCatalogRefreshService
{
    private readonly VbaProjectReferenceCatalogCache cache;
    private readonly IVbaProjectReferenceCatalogDiscovery discovery;

    /// <summary>
    /// Creates a catalog refresh service.
    /// </summary>
    /// <param name="cache">The catalog cache to read and update.</param>
    /// <param name="discovery">The discovery service used for missing references.</param>
    public VbaProjectReferenceCatalogRefreshService(
        VbaProjectReferenceCatalogCache cache,
        IVbaProjectReferenceCatalogDiscovery discovery)
    {
        this.cache = cache;
        this.discovery = discovery;
    }

    /// <summary>
    /// Discovers catalogs for selected references that are missing from the current cache.
    /// </summary>
    /// <param name="selection">The active reference selection.</param>
    /// <param name="cancellationToken">A cancellation token for refresh work.</param>
    /// <returns>The refresh results for references that were attempted.</returns>
    public async Task<IReadOnlyList<VbaProjectReferenceCatalogRefreshResult>> RefreshAsync(
        VbaProjectReferenceSelection selection,
        CancellationToken cancellationToken = default)
    {
        var missingReferenceNames = cache.Current.GetMissingCatalogReferenceNames(selection);
        var results = new List<VbaProjectReferenceCatalogRefreshResult>();
        foreach (var referenceName in missingReferenceNames)
        {
            VbaProjectReferenceCatalogDiscoveryResult discoveryResult;
            try
            {
                discoveryResult = await discovery.DiscoverAsync(referenceName, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                discoveryResult = VbaProjectReferenceCatalogDiscoveryResult.Failure(referenceName, ex.Message);
            }

            cache.Store(discoveryResult);
            results.Add(new VbaProjectReferenceCatalogRefreshResult(referenceName, discoveryResult));
        }

        return results;
    }
}
