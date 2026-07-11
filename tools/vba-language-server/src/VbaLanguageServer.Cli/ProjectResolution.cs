namespace VbaLanguageServer.ProjectModel;

/// <summary>
/// Identifies whether a source file belongs to a manifest document or an ad-hoc project.
/// </summary>
public enum VbaProjectResolutionKind
{
    /// <summary>
    /// The source file is under a manifest document source set.
    /// </summary>
    ManifestDocument,

    /// <summary>
    /// The source file is resolved as an ad-hoc folder-scoped project.
    /// </summary>
    AdHoc
}

/// <summary>
/// Describes the project boundary and reference selection for an active source document.
/// </summary>
/// <param name="Kind">The project resolution kind.</param>
/// <param name="RootPath">The source root path for the resolved project.</param>
/// <param name="ManifestPath">The manifest path for manifest-backed projects.</param>
/// <param name="DocumentName">The manifest document name for manifest-backed projects.</param>
/// <param name="DocumentKind">The manifest document kind for manifest-backed projects.</param>
/// <param name="References">The manifest references active for the resolved document.</param>
public sealed record VbaProjectResolution(
    VbaProjectResolutionKind Kind,
    string RootPath,
    string? ManifestPath = null,
    string? DocumentName = null,
    string? DocumentKind = null,
    IReadOnlyList<VbaProjectReference>? References = null)
{
    /// <summary>
    /// Gets the active manifest reference entries, or an empty list for ad-hoc projects.
    /// </summary>
    public IReadOnlyList<VbaProjectReference> ReferenceEntries => References ?? [];

    /// <summary>
    /// Determines whether a URI belongs to this resolved project boundary.
    /// </summary>
    /// <param name="uri">The document URI to test.</param>
    /// <returns>True when the URI belongs to this project.</returns>
    public bool ContainsUri(string uri)
    {
        var localPath = VbaProjectResolver.TryGetLocalPath(uri);
        if (localPath is null)
        {
            return false;
        }

        return Kind == VbaProjectResolutionKind.ManifestDocument
            ? VbaProjectResolver.IsPathUnder(localPath, RootPath)
            : VbaProjectResolver.SameDirectory(localPath, RootPath);
    }
}

/// <summary>
/// Resolves source document URIs to manifest-backed or ad-hoc VBA project boundaries.
/// </summary>
public static class VbaProjectResolver
{
    /// <summary>
    /// Resolves the project boundary for an active document URI.
    /// </summary>
    /// <param name="activeUri">The active document URI.</param>
    /// <returns>The resolved project boundary.</returns>
    public static VbaProjectResolution Resolve(string activeUri)
    {
        var activePath = TryGetLocalPath(activeUri);
        if (activePath is null)
        {
            return new VbaProjectResolution(VbaProjectResolutionKind.AdHoc, "");
        }

        var activeDirectory = Path.GetDirectoryName(activePath) ?? Directory.GetCurrentDirectory();
        for (var directory = new DirectoryInfo(activeDirectory); directory is not null; directory = directory.Parent)
        {
            var manifestPath = Path.Combine(directory.FullName, "project.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            var manifest = ProjectManifestReader.Parse(File.ReadAllText(manifestPath), manifestPath);
            foreach (var (documentName, document) in manifest.Documents)
            {
                var sourceRoot = Path.GetFullPath(Path.Combine(directory.FullName, document.SourcePath));
                if (IsPathUnder(activePath, sourceRoot))
                {
                    return new VbaProjectResolution(
                        VbaProjectResolutionKind.ManifestDocument,
                        sourceRoot,
                        manifestPath,
                        documentName,
                        document.Kind,
                        document.References ?? []);
                }
            }

            return new VbaProjectResolution(VbaProjectResolutionKind.AdHoc, activeDirectory);
        }

        return new VbaProjectResolution(VbaProjectResolutionKind.AdHoc, activeDirectory);
    }

    /// <summary>
    /// Converts a file URI to a local filesystem path.
    /// </summary>
    /// <param name="uri">The URI to convert.</param>
    /// <returns>The local path, or null when the URI is invalid or non-file.</returns>
    public static string? TryGetLocalPath(string uri)
    {
        try
        {
            var parsed = new Uri(uri);
            return parsed.IsFile ? Path.GetFullPath(parsed.LocalPath) : null;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Determines whether a candidate path is under a root path.
    /// </summary>
    /// <param name="candidatePath">The path to test.</param>
    /// <param name="rootPath">The expected root path.</param>
    /// <returns>True when the candidate is inside the root directory.</returns>
    public static bool IsPathUnder(string candidatePath, string rootPath)
    {
        var candidate = Path.GetFullPath(candidatePath);
        var root = EnsureTrailingSeparator(Path.GetFullPath(rootPath));
        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines whether a candidate path is in a directory.
    /// </summary>
    /// <param name="candidatePath">The path whose parent directory should be checked.</param>
    /// <param name="directoryPath">The expected directory path.</param>
    /// <returns>True when both normalized directories are the same.</returns>
    public static bool SameDirectory(string candidatePath, string directoryPath)
    {
        var candidateDirectory = Path.GetDirectoryName(Path.GetFullPath(candidatePath)) ?? "";
        return string.Equals(
            TrimTrailingSeparator(candidateDirectory),
            TrimTrailingSeparator(Path.GetFullPath(directoryPath)),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        var trimmed = TrimTrailingSeparator(path);
        return $"{trimmed}{Path.DirectorySeparatorChar}";
    }

    private static string TrimTrailingSeparator(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
