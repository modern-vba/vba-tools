using System.Text;
using System.Text.Json;
using VbaDev.Domain;

namespace VbaDev.App.Projects;

/// <summary>
/// Applies validated ProjectManifest edits and persistence policies.
/// </summary>
public sealed class ProjectManifestEditor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly UnicodeEncoding Utf16LeWithBom = new(bigEndian: false, byteOrderMark: true);

    private readonly IProjectManifestStore manifestStore;

    /// <summary>
    /// Creates a project manifest editor.
    /// </summary>
    /// <param name="manifestStore">The manifest store used to persist changes.</param>
    public ProjectManifestEditor(IProjectManifestStore manifestStore)
    {
        this.manifestStore = manifestStore;
    }

    /// <summary>
    /// Clones a manifest before planning a mutation.
    /// </summary>
    /// <param name="manifest">The source manifest.</param>
    /// <returns>A mutable manifest clone.</returns>
    public static ProjectManifest Clone(ProjectManifest manifest)
    {
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        return JsonSerializer.Deserialize<ProjectManifest>(json, JsonOptions)
            ?? throw new ProjectManifestException("Project manifest could not be cloned.");
    }

    /// <summary>
    /// Finds a document definition by manifest key, using case-insensitive fallback.
    /// </summary>
    /// <param name="manifest">The manifest to inspect.</param>
    /// <param name="documentName">The document name to find.</param>
    /// <returns>The matching project document.</returns>
    public static ProjectDocument GetDocument(ProjectManifest manifest, string documentName)
    {
        if (manifest.Documents.TryGetValue(documentName, out var document))
        {
            return document;
        }

        return manifest.Documents
            .First(item => item.Key.Equals(documentName, StringComparison.OrdinalIgnoreCase))
            .Value;
    }

    /// <summary>
    /// Saves a manifest through the configured manifest store.
    /// </summary>
    /// <param name="projectRoot">The project root containing vba-project.json.</param>
    /// <param name="manifest">The manifest to save.</param>
    public void Save(string projectRoot, ProjectManifest manifest)
        => manifestStore.Save(projectRoot, manifest);

    /// <summary>
    /// Saves a manifest, writing a recovery file when the manifest store rejects the save.
    /// </summary>
    /// <param name="projectRoot">The project root containing vba-project.json.</param>
    /// <param name="manifest">The manifest to save.</param>
    public void SaveWithRecovery(string projectRoot, ProjectManifest manifest)
    {
        try
        {
            manifestStore.Save(projectRoot, manifest);
        }
        catch (IOException ex)
        {
            throw new ProjectManifestEditException(WriteManifestRecovery(projectRoot, manifest, ex), ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new ProjectManifestEditException(WriteManifestRecovery(projectRoot, manifest, ex), ex);
        }
        catch (ProjectManifestException ex)
        {
            throw new ProjectManifestEditException(WriteManifestRecovery(projectRoot, manifest, ex), ex);
        }
    }

    private static string WriteManifestRecovery(
        string projectRoot,
        ProjectManifest manifest,
        Exception manifestSaveException)
    {
        try
        {
            Directory.CreateDirectory(projectRoot);
            var recoveryPath = Path.Combine(projectRoot, $"vba-project.failed-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json");
            var json = JsonSerializer.Serialize(manifest, JsonOptions);
            File.WriteAllText(recoveryPath, json + Environment.NewLine, Utf16LeWithBom);
            return recoveryPath;
        }
        catch (IOException ex)
        {
            return RecoveryFailureMessage(manifestSaveException, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            return RecoveryFailureMessage(manifestSaveException, ex);
        }
    }

    private static string RecoveryFailureMessage(Exception manifestSaveException, Exception recoveryException)
        => $"Project manifest could not be saved ({manifestSaveException.Message}), and recovery file could not be written: {recoveryException.Message}";
}

/// <summary>
/// Reports a failed manifest edit or recovery operation.
/// </summary>
public sealed class ProjectManifestEditException : Exception
{
    /// <summary>
    /// Creates a project manifest edit exception.
    /// </summary>
    /// <param name="message">The user-facing edit failure message.</param>
    /// <param name="innerException">The underlying edit failure.</param>
    public ProjectManifestEditException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
