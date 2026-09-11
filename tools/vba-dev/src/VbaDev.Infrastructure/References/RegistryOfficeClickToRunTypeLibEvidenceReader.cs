using System.Globalization;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;
using VbaDev.App.References;

namespace VbaDev.Infrastructure.References;

/// <summary>Reads Office Click-to-Run installation and virtual TypeLib registration evidence.</summary>
public sealed class RegistryOfficeClickToRunTypeLibEvidenceReader : IOfficeClickToRunTypeLibEvidenceReader
{
    private readonly IOfficeClickToRunRegistryRootProvider rootProvider;
    private readonly Func<bool> isSupportedPlatform;
    private readonly Func<bool> is64BitOperatingSystem;

    /// <summary>Creates a reader backed by the machine-scoped Office Click-to-Run registry.</summary>
    public RegistryOfficeClickToRunTypeLibEvidenceReader()
        : this(CreateRootProvider(), OperatingSystem.IsWindows, () => Environment.Is64BitOperatingSystem)
    {
    }

    internal RegistryOfficeClickToRunTypeLibEvidenceReader(
        IOfficeClickToRunRegistryRootProvider rootProvider,
        Func<bool> isSupportedPlatform)
        : this(rootProvider, isSupportedPlatform, () => true)
    {
    }

    internal RegistryOfficeClickToRunTypeLibEvidenceReader(
        IOfficeClickToRunRegistryRootProvider rootProvider,
        Func<bool> isSupportedPlatform,
        Func<bool> is64BitOperatingSystem)
    {
        this.rootProvider = rootProvider ?? throw new ArgumentNullException(nameof(rootProvider));
        this.isSupportedPlatform = isSupportedPlatform ?? throw new ArgumentNullException(nameof(isSupportedPlatform));
        this.is64BitOperatingSystem = is64BitOperatingSystem ?? throw new ArgumentNullException(nameof(is64BitOperatingSystem));
    }

    /// <inheritdoc />
    public OfficeClickToRunTypeLibEvidenceSnapshot Read()
    {
        if (!isSupportedPlatform())
        {
            return OfficeClickToRunTypeLibEvidenceSnapshot.Empty;
        }

        try
        {
            using var root = rootProvider.OpenRoot();
            if (root is null)
            {
                return OfficeClickToRunTypeLibEvidenceSnapshot.Empty;
            }

            using var configuration = root.OpenSubKey("Configuration");
            if (configuration is null)
            {
                return Incomplete("The Office Click-to-Run Configuration registry key could not be opened.");
            }

            if (!TryReadString(configuration, "InstallationPath", out var installationPath)
                || !TryReadString(configuration, "Platform", out var platform))
            {
                return Incomplete("Office Click-to-Run Configuration did not expose complete InstallationPath and Platform evidence.");
            }
            var is64Bit = is64BitOperatingSystem();
            var useWow6432Node = platform.Equals("x86", StringComparison.OrdinalIgnoreCase) && is64Bit;
            if (!platform.Equals("x64", StringComparison.OrdinalIgnoreCase)
                && !platform.Equals("x86", StringComparison.OrdinalIgnoreCase))
            {
                return Incomplete($"Office Click-to-Run Configuration exposes unsupported Platform '{platform}'.");
            }
            if (platform.Equals("x64", StringComparison.OrdinalIgnoreCase) && !is64Bit)
            {
                return Incomplete("Office Click-to-Run Configuration exposes x64 on a non-x64 operating system.");
            }

            using var registry = root.OpenSubKey("REGISTRY");
            using var machine = registry?.OpenSubKey("MACHINE");
            using var software = machine?.OpenSubKey("Software");
            using var classes = software?.OpenSubKey("Classes");
            if (classes is null)
            {
                return Incomplete("The scoped Office Click-to-Run virtual Classes registry could not be opened.");
            }

            var registrations = new List<OfficeClickToRunTypeLibRegistrationEvidence>();
            var malformed = 0;
            if (useWow6432Node)
            {
                using var wow6432Node = classes.OpenSubKey("Wow6432Node");
                using var typeLib = wow6432Node?.OpenSubKey("TypeLib");
                if (typeLib is null)
                {
                    return Incomplete("The architecture-applicable Office Click-to-Run Wow6432Node\\TypeLib registry could not be opened.");
                }
                ReadRegistrations(typeLib, registrations, ref malformed);
            }
            else
            {
                using var typeLib = classes.OpenSubKey("TypeLib");
                if (typeLib is null)
                {
                    return Incomplete("The architecture-applicable Office Click-to-Run TypeLib registry could not be opened.");
                }
                ReadRegistrations(typeLib, registrations, ref malformed);
            }

            var installation = new OfficeClickToRunInstallationEvidence(
                installationPath,
                platform,
                registrations
                    .OrderBy(item => item.ReferenceName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.Guid, StringComparer.Ordinal)
                    .ThenBy(item => item.Major)
                    .ThenBy(item => item.Minor)
                    .ThenBy(item => item.Lcid)
                    .ThenBy(item => item.Platform, StringComparer.Ordinal)
                    .ThenBy(item => item.VirtualPath, StringComparer.OrdinalIgnoreCase)
                    .ToArray());
            return malformed == 0
                ? new(true, [installation], null)
                : new(false, [installation], $"Office Click-to-Run virtual TypeLib evidence contained {malformed} malformed registration item(s).");
        }
        catch (Exception error) when (IsRegistryAccessFailure(error))
        {
            return Incomplete($"Office Click-to-Run registry evidence could not be read completely: {error.Message}");
        }
    }

    private static void ReadRegistrations(
        IOfficeClickToRunRegistryKey typeLibRoot,
        ICollection<OfficeClickToRunTypeLibRegistrationEvidence> registrations,
        ref int malformed)
    {
        foreach (var guidName in typeLibRoot.GetSubKeyNames())
        {
            using var guidKey = typeLibRoot.OpenSubKey(guidName);
            if (guidKey is null || !Guid.TryParse(guidName, out var guid))
            {
                malformed++;
                continue;
            }

            foreach (var versionName in guidKey.GetSubKeyNames())
            {
                using var versionKey = guidKey.OpenSubKey(versionName);
                if (versionKey is null
                    || !TryParseVersion(versionName, out var major, out var minor)
                    || !TryReadString(versionKey, null, out var referenceName))
                {
                    malformed++;
                    continue;
                }

                foreach (var lcidName in versionKey.GetSubKeyNames())
                {
                    if (lcidName.Equals("FLAGS", StringComparison.OrdinalIgnoreCase)
                        || lcidName.Equals("HELPDIR", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    using var lcidKey = versionKey.OpenSubKey(lcidName);
                    if (lcidKey is null || !TryParseLcid(lcidName, out var lcid))
                    {
                        malformed++;
                        continue;
                    }

                    foreach (var platformName in lcidKey.GetSubKeyNames())
                    {
                        if (!platformName.Equals("win32", StringComparison.OrdinalIgnoreCase)
                            && !platformName.Equals("win64", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        using var platformKey = lcidKey.OpenSubKey(platformName);
                        if (platformKey is null || !TryReadString(platformKey, null, out var virtualPath))
                        {
                            malformed++;
                            continue;
                        }

                        registrations.Add(new(referenceName, guid.ToString("D"), major, minor, lcid,
                            platformName.Trim().ToLowerInvariant(), virtualPath));
                    }
                }
            }
        }
    }

    private static bool TryReadString(IOfficeClickToRunRegistryKey key, string? name, out string value)
    {
        value = key.GetValue(name) as string ?? string.Empty;
        value = value.Trim();
        return value.Length > 0;
    }

    private static bool TryParseVersion(string value, out int major, out int minor)
    {
        var parts = value.Trim().Split('.');
        if (parts.Length == 2
            && ushort.TryParse(parts[0], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var parsedMajor)
            && ushort.TryParse(parts[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var parsedMinor))
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
        if (uint.TryParse(value.Trim(), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var parsed)
            && parsed <= int.MaxValue)
        {
            lcid = (int)parsed;
            return true;
        }

        lcid = 0;
        return false;
    }

    private static OfficeClickToRunTypeLibEvidenceSnapshot Incomplete(string diagnostic)
        => new(false, [], diagnostic);

    private static bool IsRegistryAccessFailure(Exception error)
        => error is UnauthorizedAccessException
            or SecurityException
            or IOException
            or ObjectDisposedException;

    private static IOfficeClickToRunRegistryRootProvider CreateRootProvider()
        => OperatingSystem.IsWindows()
            ? new WindowsOfficeClickToRunRegistryRootProvider()
            : new UnsupportedOfficeClickToRunRegistryRootProvider();
}

internal interface IOfficeClickToRunRegistryRootProvider
{
    IOfficeClickToRunRegistryKey? OpenRoot();
}

internal interface IOfficeClickToRunRegistryKey : IDisposable
{
    IReadOnlyList<string> GetSubKeyNames();
    object? GetValue(string? name);
    IOfficeClickToRunRegistryKey? OpenSubKey(string name);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsOfficeClickToRunRegistryRootProvider : IOfficeClickToRunRegistryRootProvider
{
    public IOfficeClickToRunRegistryKey? OpenRoot()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,
            Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32);
        var root = machine.OpenSubKey(@"SOFTWARE\Microsoft\Office\ClickToRun");
        return root is null ? null : new WindowsOfficeClickToRunRegistryKey(root);
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsOfficeClickToRunRegistryKey(RegistryKey key) : IOfficeClickToRunRegistryKey
{
    public IReadOnlyList<string> GetSubKeyNames()
        => key.GetSubKeyNames();

    public object? GetValue(string? name)
        => key.GetValue(name);

    public IOfficeClickToRunRegistryKey? OpenSubKey(string name)
    {
        var child = key.OpenSubKey(name);
        return child is null ? null : new WindowsOfficeClickToRunRegistryKey(child);
    }

    public void Dispose()
        => key.Dispose();
}

internal sealed class UnsupportedOfficeClickToRunRegistryRootProvider : IOfficeClickToRunRegistryRootProvider
{
    public IOfficeClickToRunRegistryKey? OpenRoot()
        => null;
}
