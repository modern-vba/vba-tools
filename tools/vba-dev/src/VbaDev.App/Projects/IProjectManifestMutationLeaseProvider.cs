using VbaDev.Domain;

namespace VbaDev.App.Projects;

/// <summary>
/// Acquires filesystem-scoped ownership of one canonical project-manifest mutation window.
/// </summary>
public interface IProjectManifestMutationLeaseProvider
{
    /// <summary>
    /// Waits boundedly and cancellably for exclusive ownership of one physical project root.
    /// </summary>
    ValueTask<IProjectManifestMutationLease> AcquireAsync(
        string projectRoot,
        ProjectManifestMutationCommand command,
        CancellationToken cancellationToken);
}

/// <summary>
/// Holds one acquired project-manifest mutation lease until release is proved.
/// </summary>
public interface IProjectManifestMutationLease
{
    /// <summary>Gets the physical project-root identity owned by this lease.</summary>
    FileSystemPathIdentity ProjectIdentity { get; }

    /// <summary>Gets the operation-safe canonical manifest path.</summary>
    string ManifestPath { get; }

    /// <summary>
    /// Releases the owner handle without observing later command cancellation and classifies marker cleanup.
    /// </summary>
    ValueTask<ProjectManifestLeaseRelease> ReleaseAsync();
}

/// <summary>
/// Reports post-release non-fatal cleanup warnings.
/// </summary>
/// <param name="Warnings">Warnings established only after owner-handle release was proved.</param>
public sealed record ProjectManifestLeaseRelease(
    IReadOnlyList<ProjectManifestMutationWarning> Warnings);
