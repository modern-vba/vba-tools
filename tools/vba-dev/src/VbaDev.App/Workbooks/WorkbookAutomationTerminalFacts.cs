using System.Collections.Immutable;
using System.Runtime.InteropServices;
using VbaDev.App.Build;

namespace VbaDev.App.Workbooks;

internal interface IWorkbookAutomationLifecycleFailure
{
    WorkbookAutomationLifecycleEvidence? LifecycleEvidence { get; set; }
}

internal sealed class WorkbookAutomationStageFailureException(
    WorkbookAutomationStage stage,
    Exception error)
    : InvalidOperationException($"Workbook automation failed during {stage.Description}: {error.Message}", error),
        IWorkbookAutomationLifecycleFailure
{
    internal WorkbookAutomationStage Stage { get; } = stage;

    WorkbookAutomationLifecycleEvidence? IWorkbookAutomationLifecycleFailure.LifecycleEvidence { get; set; }
}

internal sealed record WorkbookAutomationLifecycleEvidence(
    WorkbookAutomationStage? Stage,
    bool ProcessReleaseProven,
    bool DispatcherRetired,
    bool CancellationObserved);

internal enum WorkbookAutomationFailureCategory
{
    Cancellation,
    Timeout,
    ComFailure,
    ProcessLoss,
    CleanupAfterProvedRelease,
    UnprovedProcessRelease,
    UnprovedDispatcherRetirement
}

internal sealed record WorkbookAutomationFailure(
    WorkbookAutomationFailureCategory Category,
    Exception Error,
    WorkbookAutomationStage? Stage);

/// <summary>
/// Describes terminal evidence only; commitment, exit codes, and wording belong to the caller.
/// </summary>
internal sealed record WorkbookAutomationTerminalFacts(
    ImmutableArray<WorkbookAutomationFailure> Failures,
    ImmutableArray<Exception> UnknownFailures,
    bool CancellationObserved)
{
    internal bool IsRecognized => Failures.Length > 0 && UnknownFailures.IsEmpty;

    internal WorkbookAutomationFailure? PrimaryFailure => Failures
        .OrderBy(failure => failure.Category switch
        {
            WorkbookAutomationFailureCategory.UnprovedProcessRelease => 0,
            WorkbookAutomationFailureCategory.UnprovedDispatcherRetirement => 1,
            WorkbookAutomationFailureCategory.CleanupAfterProvedRelease => 2,
            WorkbookAutomationFailureCategory.ProcessLoss => 3,
            WorkbookAutomationFailureCategory.Timeout => 4,
            WorkbookAutomationFailureCategory.ComFailure => 5,
            _ => 6
        })
        .FirstOrDefault();

    internal bool ProcessReleaseProven => !Failures.Any(failure =>
        failure.Category == WorkbookAutomationFailureCategory.UnprovedProcessRelease);

    internal bool DispatcherRetired => !Failures.Any(failure =>
        failure.Category == WorkbookAutomationFailureCategory.UnprovedDispatcherRetirement);
}

internal static partial class WorkbookAutomationFailureClassifier
{
    internal static bool TryClassify(Exception error, out WorkbookAutomationTerminalFacts facts,
        bool cancellationObserved = false)
    {
        ArgumentNullException.ThrowIfNull(error);
        var failures = ImmutableArray.CreateBuilder<WorkbookAutomationFailure>();
        var unknown = ImmutableArray.CreateBuilder<Exception>();
        Visit(error, false, null, null);
        facts = new(failures.ToImmutable(), unknown.ToImmutable(), cancellationObserved);
        return facts.IsRecognized;

        void Visit(Exception current, bool supportingCause, WorkbookAutomationLifecycleEvidence? lifecycle,
            WorkbookAutomationStage? stage)
        {
            var observedLifecycle = lifecycle is null
                ? (current as IWorkbookAutomationLifecycleFailure)?.LifecycleEvidence
                : null;
            lifecycle ??= observedLifecycle ?? (current is WorkbookAutomationReleasedProcessCleanupException
                ? new(null, true, true, false) : null);
            if (observedLifecycle is not null)
            {
                if (!observedLifecycle.ProcessReleaseProven)
                {
                    failures.Add(new(WorkbookAutomationFailureCategory.UnprovedProcessRelease, current, observedLifecycle.Stage));
                }
                if (!observedLifecycle.DispatcherRetired)
                {
                    failures.Add(new(WorkbookAutomationFailureCategory.UnprovedDispatcherRetirement, current, observedLifecycle.Stage));
                }
            }
            cancellationObserved |= lifecycle?.CancellationObserved == true;
            stage = current switch
            {
                WorkbookAutomationStageFailureException bounded => bounded.Stage,
                WorkbookAutomationTimeoutException timeout => timeout.Stage,
                WorkbookAutomationCanceledException cancelled => cancelled.Stage,
                WorkbookAutomationProcessLostException lost => lost.Stage,
                _ => lifecycle?.Stage ?? stage
            };
            var recognized = true;
            switch (current)
            {
                case WorkbookAutomationTimeoutException timeout:
                    failures.Add(new(WorkbookAutomationFailureCategory.Timeout, current, timeout.Stage));
                    break;
                case COMException:
                    failures.Add(new(WorkbookAutomationFailureCategory.ComFailure, current, stage));
                    break;
                case WorkbookAutomationProcessLostException lost:
                    failures.Add(new(WorkbookAutomationFailureCategory.ProcessLoss, current, lost.Stage));
                    break;
                case WorkbookAutomationReleasedProcessCleanupException:
                    failures.Add(new(WorkbookAutomationFailureCategory.CleanupAfterProvedRelease, current, stage));
                    break;
                case WorkbookAutomationCleanupException:
                    if (lifecycle is null)
                    {
                        failures.Add(new(WorkbookAutomationFailureCategory.UnprovedProcessRelease, current, stage));
                    }
                    else if (lifecycle.ProcessReleaseProven && lifecycle.DispatcherRetired)
                    {
                        failures.Add(new(WorkbookAutomationFailureCategory.CleanupAfterProvedRelease, current, stage));
                    }
                    break;
                case OperationCanceledException:
                    cancellationObserved = true;
                    failures.Add(new(WorkbookAutomationFailureCategory.Cancellation, current, stage));
                    break;
                default:
                    recognized = false;
                    break;
            }

            if (!recognized && !supportingCause && current is not AggregateException
                && (current.InnerException is null || current is not (InvalidOperationException or BuildCommandException)))
            {
                unknown.Add(current);
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    Visit(inner, supportingCause, lifecycle, stage);
                }
            }
            else if (current.InnerException is { } inner)
            {
                Visit(inner, supportingCause || recognized, lifecycle, stage);
            }
        }
    }
}
