using System.Runtime.ExceptionServices;
using VbaDev.App.Workbooks;

namespace VbaDev.Infrastructure.Workbooks;

/// <summary>
/// Keeps operation, cancellation, process release, and STA retirement evidence
/// separate so the scenario owner can apply its own commitment policy.
/// </summary>
internal sealed record AutomationExcelProcessEvidence(
    WorkbookAutomationStage? LastOperationStage,
    Exception? OperationFailure,
    Exception? CleanupFailure,
    Exception? DispatcherFailure,
    bool CancellationRequestedDuringCleanup,
    bool ProcessReleaseVerified,
    bool DispatcherRetired,
    string? IsolationDiagnostics,
    bool DispatcherCreated = true);

/// <summary>
/// Withholds the operation value until runtime release and terminal failures
/// have been checked. Cleanup-time cancellation is evidence, not a decision.
/// </summary>
internal sealed class AutomationExcelProcessOutcome<TResult>(
    TResult? value,
    AutomationExcelProcessEvidence evidence)
{
    internal AutomationExcelProcessEvidence Evidence { get; } = evidence;

    internal TResult GetReleasedResult()
    {
        var operationError = Evidence.OperationFailure;
        var cleanupError = Evidence.CleanupFailure;
        if (Evidence.DispatcherFailure is { } dispatcherError)
        {
            cleanupError = cleanupError is null
                ? dispatcherError
                : !Evidence.ProcessReleaseVerified
                    ? new WorkbookAutomationCleanupException(
                        "Workbook automation cleanup and STA dispatcher disposal both failed, and owned Excel process release could not be proved.",
                        new AggregateException(cleanupError, dispatcherError))
                    : new WorkbookAutomationReleasedProcessCleanupException(
                        "Workbook automation cleanup and STA dispatcher disposal both failed after owned Excel process release was verified.",
                        new AggregateException(cleanupError, dispatcherError));
        }

        if (!Evidence.DispatcherRetired)
        {
            var failures = new[] { operationError, cleanupError }
                .OfType<Exception>()
                .ToArray();
            throw new WorkbookAutomationCleanupException(
                !Evidence.ProcessReleaseVerified
                    ? "The owned Excel process could not be verified as released during process cleanup."
                    : "The owned Excel process was released, but STA dispatcher retirement could not be proved.",
                failures.Length == 1 ? failures[0] : new AggregateException(failures));
        }

        if (cleanupError is not null)
        {
            if (operationError is null)
            {
                ExceptionDispatchInfo.Capture(cleanupError).Throw();
            }

            var combined = new AggregateException(operationError!, cleanupError);
            if (!Evidence.ProcessReleaseVerified)
            {
                throw new WorkbookAutomationCleanupException(
                    $"{operationError!.Message} The owned Excel process could not be verified as released during process cleanup.",
                    combined);
            }

            throw new WorkbookAutomationReleasedProcessCleanupException(
                $"{operationError!.Message} Workbook automation cleanup also failed after owned Excel process release was verified.",
                combined);
        }

        if (operationError is not null)
        {
            ExceptionDispatchInfo.Capture(operationError).Throw();
        }

        if (!Evidence.ProcessReleaseVerified)
        {
            throw new WorkbookAutomationCleanupException(
                "The owned Excel process release could not be proved.");
        }

        return value!;
    }
}
