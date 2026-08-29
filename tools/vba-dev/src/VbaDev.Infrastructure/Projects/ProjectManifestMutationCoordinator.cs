using System.Text;
using System.Runtime.ExceptionServices;
using VbaDev.App.Projects;
using VbaDev.Domain;

namespace VbaDev.Infrastructure.Projects;

/// <summary>
/// Reloads and commits one project-manifest mutation against the latest valid state.
/// </summary>
public sealed class ProjectManifestMutationCoordinator : IProjectManifestMutationCoordinator
{
    private const string CancellationDeferredMessage =
        "Cancellation was requested after CommonModules source mutation began; "
        + "the command completed and committed the mutation to preserve project consistency.";

    private readonly IProjectManifestAtomicWriter atomicWriter;
    private readonly IProjectManifestMutationLeaseProvider leaseProvider;

    /// <summary>
    /// Creates a coordinator using the production crash-atomic writer.
    /// </summary>
    public ProjectManifestMutationCoordinator()
        : this(
            new ProjectManifestAtomicWriter(),
            new ProjectManifestMutationLeaseProvider())
    {
    }

    /// <summary>
    /// Creates a coordinator with an explicit writer boundary.
    /// </summary>
    public ProjectManifestMutationCoordinator(
        IProjectManifestAtomicWriter atomicWriter,
        IProjectManifestMutationLeaseProvider leaseProvider)
    {
        this.atomicWriter = atomicWriter;
        this.leaseProvider = leaseProvider;
    }

    /// <inheritdoc />
    public async Task<ProjectManifestMutationOutcome<TResult>> ExecuteAsync<TResult>(
        string projectRoot,
        ProjectManifestMutationCommand command,
        Func<ProjectManifestMutationSnapshot, ProjectManifestMutationPlan<TResult>> rebase,
        CancellationToken cancellationToken)
    {
        var lease = await leaseProvider.AcquireAsync(
                projectRoot,
                command,
                cancellationToken)
            .ConfigureAwait(false);
        ProjectManifestMutationPlan<TResult> plan;
        try
        {
            var snapshotBytes = ReadManifestBytes(lease.ManifestPath);
            var latestManifest = ParseManifest(snapshotBytes, lease.ManifestPath);
            plan = rebase(new ProjectManifestMutationSnapshot(
                lease.ProjectIdentity.OperationPath,
                lease.ManifestPath,
                latestManifest));
            var commitCancellationToken = plan.SourceMutationCommitted
                ? CancellationToken.None
                : cancellationToken;
            commitCancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (plan.Manifest is not null)
                {
                    atomicWriter.ReplaceExisting(
                        lease.ManifestPath,
                        snapshotBytes,
                        plan.Manifest,
                        commitCancellationToken);
                }
                else
                {
                    atomicWriter.EstablishNoOp(
                        lease.ManifestPath,
                        snapshotBytes,
                        commitCancellationToken);
                }
            }
            catch (Exception commitException) when (
                plan.CommitFailureRecovery is not null
                && (commitException is not OperationCanceledException
                    || !commitCancellationToken.IsCancellationRequested))
            {
                throw plan.CommitFailureRecovery(commitException);
            }
        }
        catch (Exception operationException)
        {
            await ReleaseAfterFailureAsync(lease, operationException)
                .ConfigureAwait(false);
            ExceptionDispatchInfo.Capture(operationException).Throw();
            throw;
        }

        ProjectManifestLeaseRelease release;
        try
        {
            release = await lease.ReleaseAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new ProjectManifestMutationException(
                "manifestMutationReleaseFailed",
                $"Project manifest mutation succeeded, but ownership release could not be proved: {lease.ManifestPath}",
                ex);
        }

        var warnings = new List<ProjectManifestMutationWarning>();
        if (plan.SourceMutationCommitted && cancellationToken.IsCancellationRequested)
        {
            warnings.Add(new ProjectManifestMutationWarning(
                "cancellationDeferred",
                CancellationDeferredMessage));
        }

        warnings.AddRange(release.Warnings);
        return new ProjectManifestMutationOutcome<TResult>(plan.Result, warnings);
    }

    private static async ValueTask ReleaseAfterFailureAsync(
        IProjectManifestMutationLease lease,
        Exception operationException)
    {
        try
        {
            _ = await lease.ReleaseAsync().ConfigureAwait(false);
        }
        catch (Exception releaseException)
        {
            throw new ProjectManifestMutationException(
                "manifestMutationReleaseFailed",
                $"Project manifest mutation failed and ownership release could not be proved: {lease.ManifestPath}",
                new AggregateException(operationException, releaseException));
        }
    }

    private static byte[] ReadManifestBytes(string manifestPath)
    {
        try
        {
            return File.ReadAllBytes(manifestPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProjectManifestException(
                $"Project manifest could not be read: {manifestPath}",
                ex);
        }
    }

    private static ProjectManifest ParseManifest(byte[] bytes, string manifestPath)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);
            var manifest = ProjectManifestReader.Parse(reader.ReadToEnd(), manifestPath);
            _ = DocumentSourceSetIsolationValidator.ResolveAndValidate(
                manifest,
                manifestPath,
                manifestPath);
            return manifest;
        }
        catch (VbaProjectManifestException ex)
        {
            throw new ProjectManifestException(ex.Message, ex);
        }
    }

}
