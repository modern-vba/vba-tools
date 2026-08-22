using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace VbaTools.TypeLibRegistry;

public sealed class RegistryTypeLibRegistryCatalogReader : ITypeLibRegistryCatalogReader
{
    private const string IncompleteCode = "registryCatalogIncomplete";
    private const string MalformedCode = "malformedRegistrationsSkipped";

    private readonly ITypeLibRegistryRootProvider rootProvider;
    private readonly Func<bool> isSupportedPlatform;

    public RegistryTypeLibRegistryCatalogReader()
        : this(CreateRootProvider(), OperatingSystem.IsWindows)
    {
    }

    internal RegistryTypeLibRegistryCatalogReader(
        ITypeLibRegistryRootProvider rootProvider,
        Func<bool> isSupportedPlatform)
    {
        ArgumentNullException.ThrowIfNull(rootProvider);
        ArgumentNullException.ThrowIfNull(isSupportedPlatform);
        this.rootProvider = rootProvider;
        this.isSupportedPlatform = isSupportedPlatform;
    }

    public TypeLibRegistryCatalog Read()
    {
        if (!isSupportedPlatform())
        {
            return Incomplete(
                "The merged HKEY_CLASSES_ROOT\\TypeLib catalog is available only on Windows.");
        }

        try
        {
            using var typeLibRoot = rootProvider.OpenTypeLibRoot();
            if (typeLibRoot is null)
            {
                return Incomplete("HKEY_CLASSES_ROOT\\TypeLib could not be opened.");
            }

            return Read(typeLibRoot);
        }
        catch (Exception exception) when (IsRegistryAccessFailure(exception))
        {
            return Incomplete(
                $"HKEY_CLASSES_ROOT\\TypeLib enumeration did not complete: {exception.Message}");
        }
    }

    private static TypeLibRegistryCatalog Read(ITypeLibRegistryKey typeLibRoot)
    {
        var names = new Dictionary<string, NameBuilder>(StringComparer.OrdinalIgnoreCase);
        var malformedCount = 0;

        foreach (var guidKeyName in typeLibRoot.GetSubKeyNames())
        {
            using var guidKey = typeLibRoot.OpenSubKey(guidKeyName);
            if (guidKey is null)
            {
                return Incomplete(
                    "HKEY_CLASSES_ROOT\\TypeLib enumeration did not complete because an enumerated library key could not be opened.");
            }

            foreach (var versionKeyName in guidKey.GetSubKeyNames())
            {
                using var versionKey = OpenSubKeyOrNull(guidKey, versionKeyName);
                if (versionKey is null)
                {
                    malformedCount++;
                    continue;
                }

                if (!TryReadNonEmptyString(versionKey, out var registeredName))
                {
                    malformedCount++;
                    continue;
                }

                var name = GetOrAddName(names, registeredName);
                if (!TryParseGuid(guidKeyName, out var canonicalGuid)
                    || !TryParseVersion(versionKeyName, out var major, out var minor))
                {
                    malformedCount++;
                    continue;
                }

                var locales = ReadLocales(versionKey, out var malformedLocaleMetadata);
                if (locales.Count == 0 || malformedLocaleMetadata)
                {
                    malformedCount++;
                }

                if (locales.Count > 0)
                {
                    name.Add(canonicalGuid, major, minor, locales);
                }
            }
        }

        var warnings = CreateWarnings(malformedCount);
        return new TypeLibRegistryCatalog(
            complete: true,
            names.Values
                .Select(name => name.Build())
                .OrderBy(name => name.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(name => name.Name, StringComparer.Ordinal)
                .ToArray(),
            warnings,
            diagnostic: null);
    }

    private static IReadOnlyList<TypeLibRegistryLocale> ReadLocales(
        ITypeLibRegistryKey versionKey,
        out bool malformedMetadata)
    {
        malformedMetadata = false;
        var locales = new List<TypeLibRegistryLocale>();
        var localeKeyNames = GetSubKeyNamesOrNull(versionKey);
        if (localeKeyNames is null)
        {
            malformedMetadata = true;
            return [];
        }

        foreach (var localeKeyName in localeKeyNames)
        {
            if (!TryParseLcid(localeKeyName, out var lcid))
            {
                if (!IsKnownVersionMetadataKey(localeKeyName))
                {
                    malformedMetadata = true;
                }

                continue;
            }

            using var localeKey = OpenSubKeyOrNull(versionKey, localeKeyName);
            if (localeKey is null)
            {
                malformedMetadata = true;
                continue;
            }

            var paths = new List<TypeLibRegistryPath>();
            var platformKeyNames = GetSubKeyNamesOrNull(localeKey);
            if (platformKeyNames is null)
            {
                malformedMetadata = true;
                continue;
            }

            foreach (var platformKeyName in platformKeyNames)
            {
                if (!IsRegisteredPlatform(platformKeyName))
                {
                    continue;
                }

                using var platformKey = OpenSubKeyOrNull(localeKey, platformKeyName);
                if (platformKey is null
                    || !TryReadNonEmptyString(platformKey, out var registeredPath))
                {
                    malformedMetadata = true;
                    continue;
                }

                paths.Add(new TypeLibRegistryPath(
                    platformKeyName.Trim().ToLowerInvariant(),
                    registeredPath));
            }

            if (paths.Count == 0)
            {
                malformedMetadata = true;
                continue;
            }

            locales.Add(new TypeLibRegistryLocale(
                lcid,
                paths
                    .OrderBy(path => path.Platform, StringComparer.Ordinal)
                    .ThenBy(path => path.Path, StringComparer.Ordinal)
                    .ToArray()));
        }

        return locales
            .OrderBy(locale => locale.Lcid)
            .ToArray();
    }

    private static IReadOnlyList<string>? GetSubKeyNamesOrNull(ITypeLibRegistryKey key)
    {
        try
        {
            return key.GetSubKeyNames();
        }
        catch (Exception exception) when (IsRegistryAccessFailure(exception))
        {
            return null;
        }
    }

    private static ITypeLibRegistryKey? OpenSubKeyOrNull(
        ITypeLibRegistryKey key,
        string name)
    {
        try
        {
            return key.OpenSubKey(name);
        }
        catch (Exception exception) when (IsRegistryAccessFailure(exception))
        {
            return null;
        }
    }

    private static NameBuilder GetOrAddName(
        IDictionary<string, NameBuilder> names,
        string registeredName)
    {
        if (names.TryGetValue(registeredName, out var existing))
        {
            existing.ConsiderSpelling(registeredName);
            return existing;
        }

        var created = new NameBuilder(registeredName);
        names.Add(registeredName, created);
        return created;
    }

    private static bool TryReadNonEmptyString(
        ITypeLibRegistryKey key,
        out string value)
    {
        try
        {
            value = key.GetDefaultValue() as string ?? string.Empty;
            value = value.Trim();
            return value.Length > 0;
        }
        catch (Exception exception) when (IsRegistryAccessFailure(exception))
        {
            value = string.Empty;
            return false;
        }
    }

    private static bool TryParseGuid(string value, out string canonicalGuid)
    {
        if (Guid.TryParse(value.Trim(), out var guid))
        {
            canonicalGuid = guid.ToString("D").ToLowerInvariant();
            return true;
        }

        canonicalGuid = string.Empty;
        return false;
    }

    private static bool TryParseVersion(
        string value,
        out int major,
        out int minor)
    {
        var parts = value.Trim().Split('.');
        if (parts.Length == 2
            && ushort.TryParse(
                parts[0],
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out var parsedMajor)
            && ushort.TryParse(
                parts[1],
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out var parsedMinor))
        {
            major = parsedMajor;
            minor = parsedMinor;
            return true;
        }

        major = 0;
        minor = 0;
        return false;
    }

    private static bool TryParseLcid(string value, out int lcid)
    {
        if (uint.TryParse(
                value.Trim(),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out var parsed)
            && parsed <= int.MaxValue)
        {
            lcid = (int)parsed;
            return true;
        }

        lcid = 0;
        return false;
    }

    private static bool IsRegisteredPlatform(string value)
        => string.Equals(value.Trim(), "win32", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value.Trim(), "win64", StringComparison.OrdinalIgnoreCase);

    private static bool IsKnownVersionMetadataKey(string value)
        => string.Equals(value.Trim(), "FLAGS", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value.Trim(), "HELPDIR", StringComparison.OrdinalIgnoreCase);

    private static bool IsRegistryAccessFailure(Exception exception)
        => exception is UnauthorizedAccessException
            or System.Security.SecurityException
            or IOException
            or InvalidOperationException
            or ObjectDisposedException
            or PlatformNotSupportedException;

    private static ITypeLibRegistryRootProvider CreateRootProvider()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsTypeLibRegistryRootProvider();
        }

        return new UnsupportedTypeLibRegistryRootProvider();
    }

    private static TypeLibRegistryCatalog Incomplete(string message)
        => new(
            complete: false,
            names: [],
            warnings: [],
            diagnostic: new TypeLibRegistryCatalogDiagnostic(IncompleteCode, message));

    private static IReadOnlyList<TypeLibRegistryCatalogWarning> CreateWarnings(int malformedCount)
    {
        if (malformedCount == 0)
        {
            return [];
        }

        var noun = malformedCount == 1 ? "registration" : "registrations";
        return
        [
            new TypeLibRegistryCatalogWarning(
                MalformedCode,
                $"Skipped {malformedCount} malformed TypeLib {noun}.",
                malformedCount)
        ];
    }

    private sealed class NameBuilder(string initialSpelling)
    {
        private readonly Dictionary<string, LineageBuilder> lineages =
            new(StringComparer.Ordinal);

        private string spelling = initialSpelling;

        public void ConsiderSpelling(string candidate)
        {
            if (string.CompareOrdinal(candidate, spelling) < 0)
            {
                spelling = candidate;
            }
        }

        public void Add(
            string guid,
            int major,
            int minor,
            IReadOnlyList<TypeLibRegistryLocale> locales)
        {
            if (!lineages.TryGetValue(guid, out var lineage))
            {
                lineage = new LineageBuilder(guid);
                lineages.Add(guid, lineage);
            }

            lineage.Add(major, minor, locales);
        }

        public TypeLibRegistryCatalogName Build()
            => new(
                spelling,
                lineages.Values
                    .Select(lineage => lineage.Build())
                    .OrderBy(lineage => lineage.Guid, StringComparer.Ordinal)
                    .ToArray());
    }

    private sealed class LineageBuilder(string guid)
    {
        private readonly List<TypeLibRegistryVersion> versions = [];

        public void Add(
            int major,
            int minor,
            IReadOnlyList<TypeLibRegistryLocale> locales)
            => versions.Add(new TypeLibRegistryVersion(major, minor, locales));

        public TypeLibRegistryLineage Build()
            => new(
                guid,
                versions
                    .OrderByDescending(version => version.Major)
                    .ThenByDescending(version => version.Minor)
                    .ToArray());
    }
}

internal interface ITypeLibRegistryRootProvider
{
    ITypeLibRegistryKey? OpenTypeLibRoot();
}

internal interface ITypeLibRegistryKey : IDisposable
{
    IReadOnlyList<string> GetSubKeyNames();

    ITypeLibRegistryKey? OpenSubKey(string name);

    object? GetDefaultValue();
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsTypeLibRegistryRootProvider : ITypeLibRegistryRootProvider
{
    public ITypeLibRegistryKey? OpenTypeLibRoot()
    {
        var key = Registry.ClassesRoot.OpenSubKey("TypeLib", writable: false);
        return key is null ? null : new WindowsTypeLibRegistryKey(key);
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsTypeLibRegistryKey(RegistryKey key) : ITypeLibRegistryKey
{
    public IReadOnlyList<string> GetSubKeyNames() => key.GetSubKeyNames();

    public ITypeLibRegistryKey? OpenSubKey(string name)
    {
        var subKey = key.OpenSubKey(name, writable: false);
        return subKey is null ? null : new WindowsTypeLibRegistryKey(subKey);
    }

    public object? GetDefaultValue() => key.GetValue(null);

    public void Dispose() => key.Dispose();
}

internal sealed class UnsupportedTypeLibRegistryRootProvider : ITypeLibRegistryRootProvider
{
    public ITypeLibRegistryKey? OpenTypeLibRoot() => null;
}
