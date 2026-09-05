using VbaDev.Infrastructure.FileSystem;
using System.Text;
using VbaDev.App.Projects;
using VbaDev.Domain;

namespace VbaDev.Infrastructure.Projects;

/// <summary>
/// Loads and saves project manifests as JSON files on disk.
/// </summary>
public sealed class JsonProjectManifestStore : IProjectManifestStore
{
    private readonly IProjectManifestAtomicWriter atomicWriter;

    /// <summary>
    /// Creates a manifest store with the production crash-atomic writer.
    /// </summary>
    public JsonProjectManifestStore()
        : this(new ProjectManifestAtomicWriter())
    {
    }

    /// <summary>
    /// Creates a manifest store with an explicit crash-atomic writer.
    /// </summary>
    public JsonProjectManifestStore(IProjectManifestAtomicWriter atomicWriter)
    {
        this.atomicWriter = atomicWriter;
    }

    /// <summary>
    /// Loads and validates a project manifest JSON file.
    /// </summary>
    /// <param name="manifestPath">The manifest path to read.</param>
    /// <returns>The parsed and validated project manifest.</returns>
    public ProjectManifest Load(string manifestPath)
    {
        try
        {
            using var stream = File.OpenRead(manifestPath);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var json = reader.ReadToEnd();
            var manifest = ProjectManifestReader.Parse(json, manifestPath);
            _ = DocumentSourceSetIsolationValidator.ResolveAndValidate(
                manifest,
                manifestPath,
                manifestPath,
                new FileSystemPathIdentityResolver());
            return manifest;
        }
        catch (VbaProjectManifestException ex)
        {
            throw new ProjectManifestException(ex.Message, ex);
        }
        catch (IOException ex)
        {
            throw new ProjectManifestException($"Project manifest could not be read: {manifestPath}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new ProjectManifestException($"Project manifest could not be read: {manifestPath}", ex);
        }
    }

    /// <summary>
    /// Writes a validated project manifest to vba-project.json under a project root.
    /// </summary>
    /// <param name="projectRoot">The project root directory.</param>
    /// <param name="manifest">The manifest to save.</param>
    public void Save(string projectRoot, ProjectManifest manifest)
    {
        var manifestPath = Path.Combine(projectRoot, ProjectManifest.ManifestFileName);
        atomicWriter.Save(manifestPath, manifest);
    }

}
