using VbaDev.Domain;

namespace VbaDev.App.Projects;

/// <summary>
/// Supports validated ProjectManifest mutation planning and failure recovery.
/// </summary>
public sealed class ProjectManifestEditor
{
    private readonly IProjectManifestAtomicWriter atomicWriter;

    /// <summary>
    /// Creates a project manifest mutation helper.
    /// </summary>
    /// <param name="atomicWriter">The writer used to persist recovery artifacts.</param>
    public ProjectManifestEditor(IProjectManifestAtomicWriter atomicWriter)
    {
        this.atomicWriter = atomicWriter;
    }

    /// <summary>
    /// Clones a manifest before planning a mutation.
    /// </summary>
    /// <param name="manifest">The source manifest.</param>
    /// <returns>A mutable manifest clone.</returns>
    public static ProjectManifest Clone(ProjectManifest manifest)
    {
        var documents = manifest.Documents.ToDictionary(
            pair => pair.Key,
            pair => pair.Value with
            {
                CommonModules = [.. pair.Value.CommonModules],
                References = [.. pair.Value.References]
            },
            StringComparer.OrdinalIgnoreCase);
        var commandDefaults = manifest.CommandDefaults is null
            ? null
            : new CommandDefaults(
                manifest.CommandDefaults.Test is null
                    ? null
                    : manifest.CommandDefaults.Test with { },
                manifest.CommandDefaults.ExcelAutomation is null
                    ? null
                    : manifest.CommandDefaults.ExcelAutomation with { });

        return manifest with
        {
            Documents = documents,
            CommandDefaults = commandDefaults
        };
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
    /// Writes the planned manifest as a manual recovery artifact after another commit boundary fails.
    /// </summary>
    /// <param name="projectRoot">The project root that receives the recovery artifact.</param>
    /// <param name="manifest">The complete manifest that could not be committed.</param>
    /// <param name="manifestSaveException">The commit failure reported by the owning boundary.</param>
    /// <returns>The recovery path, or a combined recovery-failure diagnostic.</returns>
    public string CreateRecoveryAfterFailedSave(
        string projectRoot,
        ProjectManifest manifest,
        Exception manifestSaveException)
        => WriteManifestRecovery(projectRoot, manifest, manifestSaveException);

    private string WriteManifestRecovery(
        string projectRoot,
        ProjectManifest manifest,
        Exception manifestSaveException)
    {
        try
        {
            return atomicWriter.CreateRecovery(projectRoot, manifest);
        }
        catch (IOException ex)
        {
            return RecoveryFailureMessage(manifestSaveException, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            return RecoveryFailureMessage(manifestSaveException, ex);
        }
        catch (ProjectManifestException ex)
        {
            return RecoveryFailureMessage(manifestSaveException, ex);
        }
    }

    private static string RecoveryFailureMessage(Exception manifestSaveException, Exception recoveryException)
        => $"Project manifest could not be saved ({manifestSaveException.Message}), and recovery file could not be written: {recoveryException.Message}";
}
