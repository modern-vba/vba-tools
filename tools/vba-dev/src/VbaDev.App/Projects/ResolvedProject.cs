using VbaDev.Domain;

namespace VbaDev.App.Projects;

/// <summary>
/// Contains a loaded project manifest and project-root-relative path resolution.
/// </summary>
/// <param name="ProjectRoot">The absolute project root directory.</param>
/// <param name="ManifestPath">The absolute path to project.json.</param>
/// <param name="Manifest">The loaded project manifest.</param>
/// <param name="CommonModulesRepositoryPath">The resolved CommonModulesRepository path, when configured.</param>
public sealed record ResolvedProject(
    string ProjectRoot,
    string ManifestPath,
    ProjectManifest Manifest,
    string? CommonModulesRepositoryPath)
{
    /// <summary>
    /// Resolves a manifest path against the project root.
    /// </summary>
    /// <param name="path">The manifest path, using either slash style or an absolute path.</param>
    /// <returns>The absolute local filesystem path.</returns>
    public string ResolvePath(string path)
    {
        var normalizedPath = path.Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.IsPathRooted(normalizedPath) ? normalizedPath : Path.Combine(ProjectRoot, normalizedPath));
    }
}
