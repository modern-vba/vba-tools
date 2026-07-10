using Microsoft.Win32;
using System.Runtime.Versioning;
using VbaDev.App.Workbooks;

namespace VbaDev.Infrastructure.Workbooks;

public sealed class RegistryVbaProjectReferenceResolver : IVbaProjectReferenceResolver
{
    public IReadOnlyList<ResolvedVbaProjectReference> Resolve(string referenceName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        using var typeLibRoot = Registry.ClassesRoot.OpenSubKey("TypeLib");
        if (typeLibRoot is null)
        {
            return [];
        }

        var matches = new List<RegistryTypeLibMatch>();
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
                var description = versionKey?.GetValue(null) as string;
                if (!referenceName.Equals(description, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (versionKey is not null && TryParseVersion(version, out var major, out var minor))
                {
                    matches.Add(new RegistryTypeLibMatch(
                        new ResolvedVbaProjectReference(description, guid, major, minor),
                        GetRegisteredTypeLibPaths(versionKey)));
                }
            }
        }

        return SelectUsableMatches(matches)
            .Select(match => match.Reference)
            .DistinctBy(match => (match.Guid.ToUpperInvariant(), match.Major, match.Minor))
            .ToArray();
    }

    private static IReadOnlyList<RegistryTypeLibMatch> SelectUsableMatches(IReadOnlyList<RegistryTypeLibMatch> matches)
    {
        if (matches.Count <= 1)
        {
            return matches;
        }

        var preferredPlatform = Environment.Is64BitProcess ? "win64" : "win32";
        var preferredPlatformMatches = matches
            .Where(match => match.Paths.Any(path =>
                path.Platform.Equals(preferredPlatform, StringComparison.OrdinalIgnoreCase) &&
                IsUsableTypeLibPath(path.Path)))
            .ToArray();
        if (preferredPlatformMatches.Length > 0)
        {
            return SelectLatestVersionPerGuid(preferredPlatformMatches);
        }

        var usablePathMatches = matches
            .Where(match => match.Paths.Any(path => IsUsableTypeLibPath(path.Path)))
            .ToArray();
        if (usablePathMatches.Length > 0)
        {
            return SelectLatestVersionPerGuid(usablePathMatches);
        }

        return SelectLatestVersionPerGuid(matches);
    }

    private static IReadOnlyList<RegistryTypeLibMatch> SelectLatestVersionPerGuid(IEnumerable<RegistryTypeLibMatch> matches)
        => matches
            .GroupBy(match => match.Reference.Guid, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(match => match.Reference.Major)
                .ThenByDescending(match => match.Reference.Minor)
                .First())
            .ToArray();

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<RegisteredTypeLibPath> GetRegisteredTypeLibPaths(RegistryKey versionKey)
    {
        var paths = new List<RegisteredTypeLibPath>();
        foreach (var lcid in versionKey.GetSubKeyNames())
        {
            using var lcidKey = versionKey.OpenSubKey(lcid);
            if (lcidKey is null)
            {
                continue;
            }

            foreach (var platform in lcidKey.GetSubKeyNames())
            {
                using var platformKey = lcidKey.OpenSubKey(platform);
                var path = platformKey?.GetValue(null) as string;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    paths.Add(new RegisteredTypeLibPath(platform, path));
                }
            }
        }

        return paths;
    }

    private static bool IsUsableTypeLibPath(string path)
    {
        var expandedPath = Environment.ExpandEnvironmentVariables(path.Trim());
        if (string.Equals(Path.GetExtension(expandedPath), ".exd", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return File.Exists(expandedPath);
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

    private sealed record RegistryTypeLibMatch(
        ResolvedVbaProjectReference Reference,
        IReadOnlyList<RegisteredTypeLibPath> Paths);

    private sealed record RegisteredTypeLibPath(string Platform, string Path);
}
