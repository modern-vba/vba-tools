using VbaDevTools.Domain;

namespace VbaDevTools.App.Projects;

public sealed record ResolvedProject(
    string ProjectRoot,
    string ManifestPath,
    ProjectManifest Manifest,
    string? CommonModulesRepositoryPath)
{
    public string ResolvePath(string path)
    {
        var normalizedPath = path.Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.IsPathRooted(normalizedPath) ? normalizedPath : Path.Combine(ProjectRoot, normalizedPath));
    }
}
