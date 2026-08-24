using VbaDev.Domain;

namespace VbaDev.App.Projects;

/// <summary>
/// Establishes crash-atomic project-manifest commit and no-op boundaries.
/// </summary>
public interface IProjectManifestAtomicWriter
{
    /// <summary>
    /// Atomically creates or replaces a complete validated manifest.
    /// </summary>
    void Save(string manifestPath, ProjectManifest manifest);

    /// <summary>
    /// Atomically replaces an existing manifest only when its exact source bytes remain current.
    /// </summary>
    void ReplaceExisting(
        string manifestPath,
        ReadOnlyMemory<byte> expectedRawBytes,
        ProjectManifest manifest,
        CancellationToken cancellationToken);

    /// <summary>
    /// Establishes a trusted no-op only when the exact rebased bytes remain current.
    /// </summary>
    void EstablishNoOp(
        string manifestPath,
        ReadOnlyMemory<byte> expectedRawBytes,
        CancellationToken cancellationToken);

    /// <summary>
    /// Atomically creates a uniquely named manual recovery manifest without overwrite.
    /// </summary>
    /// <returns>The committed absolute recovery-artifact path.</returns>
    string CreateRecovery(string projectRoot, ProjectManifest manifest);
}
