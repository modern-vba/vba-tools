using System.Runtime.ExceptionServices;
using VbaDev.App.FileSystem;
using VbaDev.App.Workbooks;

namespace VbaDev.App.Build;

internal sealed class WorkbookProjectIdentityProbe(
    IExactFileSystemObjectOwnershipFactory ownershipFactory,
    IWorkbookGenerationAutomation automation) : IWorkbookProjectIdentityProbe
{
    public async Task<WorkbookProjectIdentity> ReadAsync(CapturedWorkbookTemplate template,
        IReadOnlyList<string> requiredReferenceNames, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var staging = template.CreateStage(ownershipFactory,
            Path.Combine(Path.GetTempPath(), $"vba-dev-project-identity-{Guid.NewGuid():N}"),
            Path.GetFileName(template.SourcePath), createDirectory: true);
        WorkbookProjectIdentity? identity = null;
        Exception? operationError = null;
        try
        {
            identity = await automation.RunAsync(staging.Path, WorkbookAutomationTimeouts.Default,
                async (session, token) => new WorkbookProjectIdentity(
                    await session.GetProjectNameAsync(token).ConfigureAwait(false),
                    await session.GetReferenceIdentitiesAsync(requiredReferenceNames, token).ConfigureAwait(false)), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            var facts = WorkbookAutomationTerminalFacts.Analyze(error, cancellationToken.IsCancellationRequested);
            if (facts.HasUnprovedLifecycle)
            {
                staging.ReleaseWithoutCleanup();
                var failure = new WorkbookAutomationCleanupException(
                    $"The project identity probe could not prove its owned lifecycle. Its workbook copy was retained at '{staging.Path}'; verify process and STA release before removing it.", error);
                ((IWorkbookAutomationLifecycleFailure)failure).LifecycleEvidence = new(null,
                    facts.ProcessReleaseProven, facts.DispatcherRetired,
                    facts.CancellationObserved || cancellationToken.IsCancellationRequested);
                throw failure;
            }
            operationError = error;
        }

        try { staging.Dispose(); }
        catch (Exception cleanupError)
        {
            throw new WorkbookAutomationReleasedProcessCleanupException(
                $"The project identity probe finished, but its workbook copy could not be removed: '{staging.Path}'. {cleanupError.Message}",
                operationError is null ? cleanupError : new AggregateException(operationError, cleanupError));
        }
        if (operationError is not null) ExceptionDispatchInfo.Capture(operationError).Throw();
        return identity!;
    }
}
