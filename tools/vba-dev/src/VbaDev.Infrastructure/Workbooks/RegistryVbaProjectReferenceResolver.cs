using Microsoft.Win32;
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

        var matches = new List<ResolvedVbaProjectReference>();
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

                if (TryParseVersion(version, out var major, out var minor))
                {
                    matches.Add(new ResolvedVbaProjectReference(description, guid, major, minor));
                }
            }
        }

        return matches
            .DistinctBy(match => (match.Guid.ToUpperInvariant(), match.Major, match.Minor))
            .ToArray();
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
