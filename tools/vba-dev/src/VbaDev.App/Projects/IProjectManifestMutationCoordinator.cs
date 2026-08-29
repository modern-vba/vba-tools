using VbaDev.Domain;

namespace VbaDev.App.Projects;

/// <summary>
/// Serializes and commits one rebased mutation of a project manifest.
/// </summary>
public interface IProjectManifestMutationCoordinator
{
    /// <summary>
    /// Reloads the latest manifest, invokes one operation-specific rebase, and establishes
    /// either an atomic commit or a trusted complete no-op result.
    /// </summary>
    /// <typeparam name="TResult">The operation-specific result produced by the rebase.</typeparam>
    /// <param name="projectRoot">The resolved project root selected by the invocation.</param>
    /// <param name="command">The stable argument-free command name used for ownership metadata.</param>
    /// <param name="rebase">The operation-specific mutation to apply to the latest manifest.</param>
    /// <param name="cancellationToken">The cooperative pre-success cancellation token.</param>
    /// <returns>The complete established result and any non-fatal cleanup warnings.</returns>
    Task<ProjectManifestMutationOutcome<TResult>> ExecuteAsync<TResult>(
        string projectRoot,
        ProjectManifestMutationCommand command,
        Func<ProjectManifestMutationSnapshot, ProjectManifestMutationPlan<TResult>> rebase,
        CancellationToken cancellationToken);
}

/// <summary>
/// Supplies the latest validated manifest selected inside a mutation window.
/// </summary>
/// <param name="ProjectRoot">The operation-safe project root used by the coordinator.</param>
/// <param name="ManifestPath">The operation-safe canonical manifest path.</param>
/// <param name="Manifest">The latest validated manifest.</param>
public sealed record ProjectManifestMutationSnapshot(
    string ProjectRoot,
    string ManifestPath,
    ProjectManifest Manifest);

/// <summary>
/// Describes either one complete manifest replacement or a trusted no-op result.
/// </summary>
/// <typeparam name="TResult">The operation-specific result value.</typeparam>
/// <param name="Result">The exhaustive result established at the success boundary.</param>
/// <param name="Manifest">The complete replacement manifest, or null for a no-op.</param>
/// <param name="SourceMutationCommitted">
/// Whether a consistency-critical source mutation has crossed its commitment boundary.
/// </param>
/// <param name="CommitFailureRecovery">
/// An optional recovery boundary that converts a commit failure while the lease is still owned.
/// </param>
public sealed record ProjectManifestMutationPlan<TResult>(
    TResult Result,
    ProjectManifest? Manifest,
    bool SourceMutationCommitted,
    Func<Exception, Exception>? CommitFailureRecovery)
{
    /// <summary>
    /// Creates a plan that commits a complete updated manifest.
    /// </summary>
    public static ProjectManifestMutationPlan<TResult> Commit(
        ProjectManifest manifest,
        TResult result,
        bool sourceMutationCommitted = false,
        Func<Exception, Exception>? commitFailureRecovery = null)
        => new(result, manifest, sourceMutationCommitted, commitFailureRecovery);

    /// <summary>
    /// Creates a plan whose complete result requires no manifest replacement.
    /// </summary>
    public static ProjectManifestMutationPlan<TResult> NoOp(
        TResult result,
        bool sourceMutationCommitted = false,
        Func<Exception, Exception>? commitFailureRecovery = null)
        => new(result, Manifest: null, sourceMutationCommitted, commitFailureRecovery);
}

/// <summary>
/// Reports a completed mutation result after owned cleanup has been classified.
/// </summary>
/// <typeparam name="TResult">The operation-specific result value.</typeparam>
/// <param name="Result">The exhaustive operation result.</param>
/// <param name="Warnings">Structured non-fatal warnings established after success.</param>
public sealed record ProjectManifestMutationOutcome<TResult>(
    TResult Result,
    IReadOnlyList<ProjectManifestMutationWarning> Warnings);

/// <summary>
/// Describes a non-fatal condition that does not weaken result or commit trust.
/// </summary>
/// <param name="Code">The stable warning code.</param>
/// <param name="Message">The human-readable warning message.</param>
public sealed record ProjectManifestMutationWarning(string Code, string Message);

/// <summary>
/// Identifies the bounded stable command names allowed in mutation ownership metadata.
/// </summary>
public enum ProjectManifestMutationCommand
{
    /// <summary>One CommonModules add mutation.</summary>
    CommonModuleAdd,

    /// <summary>One CommonModules update mutation.</summary>
    CommonModuleUpdate,

    /// <summary>One reference-add mutation.</summary>
    ReferenceAdd,

    /// <summary>One reference-remove mutation.</summary>
    ReferenceRemove
}

/// <summary>
/// Reports a mutation conflict or ownership failure with a stable diagnostic code.
/// </summary>
public sealed class ProjectManifestMutationException : Exception
{
    /// <summary>
    /// Creates a coded manifest-mutation failure.
    /// </summary>
    public ProjectManifestMutationException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>
    /// Creates a coded manifest-mutation failure that preserves its underlying cause.
    /// </summary>
    public ProjectManifestMutationException(
        string code,
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>
    /// Gets the stable diagnostic code.
    /// </summary>
    public string Code { get; }
}
