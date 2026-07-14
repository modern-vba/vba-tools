using System.Text;
using System.Text.Json;
using VbaDev.App.Projects;
using VbaDev.Domain;

namespace VbaDev.Infrastructure.Projects;

/// <summary>
/// Loads and saves project manifests as JSON files on disk.
/// </summary>
public sealed class JsonProjectManifestStore : IProjectManifestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly UnicodeEncoding Utf16LeWithBom = new(bigEndian: false, byteOrderMark: true);

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
            return ProjectManifestReader.Parse(json, manifestPath);
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
        try
        {
            ProjectManifestValidator.Validate(manifest, ProjectManifest.ManifestFileName);
        }
        catch (VbaProjectManifestException ex)
        {
            throw new ProjectManifestException(ex.Message, ex);
        }
        Directory.CreateDirectory(projectRoot);
        var manifestPath = Path.Combine(projectRoot, ProjectManifest.ManifestFileName);
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        File.WriteAllText(manifestPath, json + Environment.NewLine, Utf16LeWithBom);
    }

}
