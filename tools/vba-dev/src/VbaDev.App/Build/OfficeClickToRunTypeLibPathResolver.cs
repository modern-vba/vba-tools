using VbaDev.App.References;
using VbaDev.App.Workbooks;
using VbaTools.Semantics;

namespace VbaDev.App.Build;

internal static class OfficeClickToRunTypeLibPathResolver
{
    internal static bool MayApply(string observedPath)
        => ParseVirtualPath(observedPath).Applicable;

    internal static OfficeClickToRunTypeLibPathResolution Resolve(
        OfficeClickToRunTypeLibEvidenceSnapshot evidence,
        ResolvedVbaProjectReference reference,
        string observedPath)
    {
        var virtualPath = ParseVirtualPath(observedPath);
        if (!virtualPath.Applicable)
        {
            return OfficeClickToRunTypeLibPathResolution.NotApplicable;
        }

        if (virtualPath.RelativeSegments is null)
        {
            return Failure($"Observed Office virtual TypeLib path '{observedPath}' is malformed or escapes its supported Windows virtual root.");
        }

        if (!evidence.Complete)
        {
            return Failure("The Office Click-to-Run evidence snapshot is incomplete. "
                + (string.IsNullOrWhiteSpace(evidence.Diagnostic) ? "Repair the Office Click-to-Run installation." : evidence.Diagnostic));
        }

        var candidates = new List<OfficeClickToRunTypeLibPathCandidate>();
        var problems = new List<string>();
        foreach (var installation in evidence.Installations)
        {
            var relevant = installation.Registrations.Where(registration =>
                    VbaReferenceName.Comparer.Equals(registration.ReferenceName, reference.Name)
                    && registration.Major == reference.Major
                    && registration.Minor == reference.Minor)
                .ToArray();
            foreach (var registration in relevant)
            {
                if (!Guid.TryParse(registration.Guid, out var registeredGuid)
                    || registeredGuid != Guid.Parse(reference.Guid))
                {
                    problems.Add($"Click-to-Run registration for '{reference.Name}' did not match GUID '{reference.Guid}'.");
                    continue;
                }
                if (registration.Lcid < 0)
                {
                    problems.Add($"Click-to-Run registration for '{reference.Name}' exposes invalid LCID {registration.Lcid}.");
                    continue;
                }

                var registeredPath = NormalizeWindowsAbsolutePath(registration.VirtualPath);
                if (registeredPath is null
                    || !string.Equals(registeredPath, virtualPath.NormalizedPath, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"Click-to-Run registration for '{reference.Name}' did not exactly match observed alias '{observedPath}'.");
                    continue;
                }

                if (!TrySelectVfsRoot(installation.Platform, registration.Platform, virtualPath.VirtualRoot, out var vfsRoot))
                {
                    problems.Add($"Click-to-Run platform '{installation.Platform}' and TypeLib view '{registration.Platform}' "
                        + $"do not match virtual root '{virtualPath.VirtualRoot}'.");
                    continue;
                }

                if (!TryCreatePhysicalPath(installation.InstallationPath, vfsRoot, virtualPath.RelativeSegments!,
                        out var physicalPath, out var pathProblem))
                {
                    problems.Add(pathProblem);
                    continue;
                }

                candidates.Add(new(physicalPath, registration.Lcid));
            }
        }

        if (candidates.Count != 1)
        {
            var countDescription = candidates.Count == 0 ? "no" : candidates.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var diagnostic = $"Office Click-to-Run evidence produced {countDescription} authoritative physical TypeLib candidate(s) "
                + $"for '{reference.Name}' ({reference.Guid}, {reference.Major}.{reference.Minor}) and observed alias '{observedPath}'.";
            if (problems.Count > 0)
            {
                diagnostic += " " + string.Join(" ", problems.Distinct(StringComparer.Ordinal));
            }
            return Failure(diagnostic);
        }

        return new(true, candidates[0], null);
    }

    private static OfficeClickToRunTypeLibPathResolution Failure(string diagnostic)
        => new(true, null, diagnostic);

    private static VirtualPath ParseVirtualPath(string path)
    {
        var normalized = NormalizeWindowsAbsolutePath(path);
        if (normalized is null)
        {
            var looksApplicable = path.Replace('/', '\\').Contains("\\Windows\\System32", StringComparison.OrdinalIgnoreCase)
                || path.Replace('/', '\\').Contains("\\Windows\\SysWOW64", StringComparison.OrdinalIgnoreCase);
            return new(looksApplicable, null, null, null);
        }

        foreach (var virtualRoot in new[] { "System32", "SysWOW64" })
        {
            var prefix = normalized[..3] + "Windows\\" + virtualRoot + "\\";
            if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = normalized[prefix.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            return relative.Length == 0
                ? new(true, normalized, virtualRoot, null)
                : new(true, normalized, virtualRoot, relative);
        }

        return new(false, normalized, null, null);
    }

    private static string? NormalizeWindowsAbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalized = path.Trim().Replace('/', '\\');
        if (normalized.Length < 4 || !char.IsAsciiLetter(normalized[0]) || normalized[1] != ':' || normalized[2] != '\\')
        {
            return null;
        }

        var segments = normalized[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(IsInvalidWindowsPathSegment))
        {
            return null;
        }

        return char.ToUpperInvariant(normalized[0]) + ":\\" + string.Join("\\", segments);
    }

    private static bool IsInvalidWindowsPathSegment(string segment)
        => segment is "." or ".."
            || segment.EndsWith(' ')
            || segment.EndsWith('.')
            || segment.Any(character => character < ' '
                || character is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*');

    private static bool TrySelectVfsRoot(string installationPlatform, string registrationPlatform,
        string? virtualRoot, out string vfsRoot)
    {
        vfsRoot = (installationPlatform.Trim(), registrationPlatform.Trim(), virtualRoot) switch
        {
            (var installation, var registration, "System32")
                when installation.Equals("x64", StringComparison.OrdinalIgnoreCase)
                     && registration.Equals("win64", StringComparison.OrdinalIgnoreCase) => "System",
            (var installation, var registration, "System32")
                when installation.Equals("x86", StringComparison.OrdinalIgnoreCase)
                     && registration.Equals("win32", StringComparison.OrdinalIgnoreCase) => "SystemX86",
            (var installation, var registration, "SysWOW64")
                when installation.Equals("x86", StringComparison.OrdinalIgnoreCase)
                     && registration.Equals("win32", StringComparison.OrdinalIgnoreCase) => "SystemX86",
            _ => string.Empty
        };
        return vfsRoot.Length > 0;
    }

    private static bool TryCreatePhysicalPath(string installationPath, string vfsRoot,
        IReadOnlyList<string> relativeSegments,
        out string physicalPath, out string diagnostic)
    {
        physicalPath = string.Empty;
        diagnostic = string.Empty;
        if (string.IsNullOrWhiteSpace(installationPath) || !Path.IsPathFullyQualified(installationPath))
        {
            diagnostic = $"Office Click-to-Run installation path '{installationPath}' is not a rooted path.";
            return false;
        }

        string installationRoot;
        try
        {
            installationRoot = Path.GetFullPath(installationPath);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            diagnostic = $"Office Click-to-Run installation path '{installationPath}' is invalid: {error.Message}";
            return false;
        }

        if (!Directory.Exists(installationRoot))
        {
            diagnostic = $"Office Click-to-Run installation path '{installationRoot}' does not exist or is not accessible as a directory.";
            return false;
        }

        var candidateRoot = Path.GetFullPath(Path.Combine(installationRoot, "root", "vfs", vfsRoot));
        physicalPath = Path.GetFullPath(Path.Combine([candidateRoot, .. relativeSegments]));
        var relativeToInstallation = Path.GetRelativePath(installationRoot, physicalPath);
        if (Path.IsPathFullyQualified(relativeToInstallation)
            || relativeToInstallation.Equals("..", StringComparison.Ordinal)
            || relativeToInstallation.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            diagnostic = $"Derived Click-to-Run TypeLib path '{physicalPath}' escapes installation root '{installationRoot}'.";
            physicalPath = string.Empty;
            return false;
        }

        return true;
    }

    private sealed record VirtualPath(
        bool Applicable,
        string? NormalizedPath,
        string? VirtualRoot,
        IReadOnlyList<string>? RelativeSegments);
}

internal sealed record OfficeClickToRunTypeLibPathResolution(
    bool Applicable,
    OfficeClickToRunTypeLibPathCandidate? Candidate,
    string? Diagnostic)
{
    internal static OfficeClickToRunTypeLibPathResolution NotApplicable { get; } = new(false, null, null);
}

internal sealed record OfficeClickToRunTypeLibPathCandidate(string Path, int Lcid);
