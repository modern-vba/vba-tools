namespace VbaLanguageServer.ProjectModel;

public enum VbaProjectResolutionKind
{
    ManifestDocument,
    AdHoc
}

public sealed record VbaProjectResolution(
    VbaProjectResolutionKind Kind,
    string RootPath,
    string? ManifestPath = null,
    string? DocumentName = null)
{
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

public static class VbaProjectResolver
{
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
                        documentName);
                }
            }

            return new VbaProjectResolution(VbaProjectResolutionKind.AdHoc, activeDirectory);
        }

        return new VbaProjectResolution(VbaProjectResolutionKind.AdHoc, activeDirectory);
    }

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

    public static bool IsPathUnder(string candidatePath, string rootPath)
    {
        var candidate = Path.GetFullPath(candidatePath);
        var root = EnsureTrailingSeparator(Path.GetFullPath(rootPath));
        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

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
