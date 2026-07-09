using Microsoft.Win32;
using System.Runtime.Versioning;
using VbaLanguageServer.ProjectModel;

namespace VbaLanguageServer.SourceModel;

public sealed record VbaProjectReferenceCatalogIdentity(
    string ReferenceName,
    string Guid,
    int MajorVersion,
    int MinorVersion,
    int Lcid,
    string Path);

public sealed record VbaProjectReferenceCatalogDiscoveryResult(
    string ReferenceName,
    IReadOnlyList<VbaProjectReferenceCatalogIdentity> Identities,
    VbaProjectReferenceCatalog? Catalog,
    string? ErrorMessage = null)
{
    public bool IsAmbiguous => Identities.Count > 1;

    public bool IsFailure => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool HasUsableCatalog => Catalog is not null;

    public static VbaProjectReferenceCatalogDiscoveryResult Success(
        VbaProjectReferenceCatalogIdentity identity,
        VbaProjectReferenceCatalog? catalog = null)
        => new(identity.ReferenceName, [identity], catalog);

    public static VbaProjectReferenceCatalogDiscoveryResult Ambiguous(
        string referenceName,
        IReadOnlyList<VbaProjectReferenceCatalogIdentity> identities)
        => new(referenceName, identities, null);

    public static VbaProjectReferenceCatalogDiscoveryResult Failure(string referenceName, string errorMessage)
        => new(referenceName, [], null, errorMessage);
}

public interface IVbaProjectReferenceCatalogDiscovery
{
    Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
        string referenceName,
        CancellationToken cancellationToken = default);
}

public sealed record TypeLibRegistryEntry(
    string ReferenceName,
    string Guid,
    int MajorVersion,
    int MinorVersion,
    int Lcid,
    string Path);

public interface ITypeLibRegistryReader
{
    IReadOnlyList<TypeLibRegistryEntry> ReadTypeLibraries();
}

public sealed class TypeLibReferenceCatalogDiscovery : IVbaProjectReferenceCatalogDiscovery
{
    private readonly ITypeLibRegistryReader registryReader;

    public TypeLibReferenceCatalogDiscovery(ITypeLibRegistryReader registryReader)
    {
        this.registryReader = registryReader;
    }

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

public sealed class RegistryTypeLibRegistryReader : ITypeLibRegistryReader
{
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

public sealed class VbaProjectReferenceCatalogCache
{
    private readonly object gate = new();
    private VbaProjectReferenceCatalogSet catalogSet;
    private readonly Dictionary<string, VbaProjectReferenceCatalogIdentity> identities = new(StringComparer.OrdinalIgnoreCase);

    public VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet catalogSet)
    {
        this.catalogSet = catalogSet;
    }

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

public sealed record VbaProjectReferenceCatalogRefreshResult(
    string ReferenceName,
    VbaProjectReferenceCatalogDiscoveryResult DiscoveryResult);

public sealed class VbaProjectReferenceCatalogRefreshService
{
    private readonly VbaProjectReferenceCatalogCache cache;
    private readonly IVbaProjectReferenceCatalogDiscovery discovery;

    public VbaProjectReferenceCatalogRefreshService(
        VbaProjectReferenceCatalogCache cache,
        IVbaProjectReferenceCatalogDiscovery discovery)
    {
        this.cache = cache;
        this.discovery = discovery;
    }

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
