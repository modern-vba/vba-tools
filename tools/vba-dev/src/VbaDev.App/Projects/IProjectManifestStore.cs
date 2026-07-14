using VbaDev.Domain;

namespace VbaDev.App.Projects;

/// <summary>
/// Loads and saves project manifests for workbook-backed projects.
/// </summary>
public interface IProjectManifestStore
{
    /// <summary>
    /// Loads a project manifest from disk.
    /// </summary>
    /// <param name="manifestPath">The absolute or relative manifest path.</param>
    /// <returns>The parsed project manifest.</returns>
    ProjectManifest Load(string manifestPath);

    /// <summary>
    /// Saves a project manifest to the standard manifest path under a project root.
    /// </summary>
    /// <param name="projectRoot">The project root where vba-project.json should be written.</param>
    /// <param name="manifest">The manifest to save.</param>
    void Save(string projectRoot, ProjectManifest manifest);
}
