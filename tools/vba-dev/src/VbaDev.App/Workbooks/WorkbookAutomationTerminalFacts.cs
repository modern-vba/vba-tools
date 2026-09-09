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

internal enum WorkbookAutomationDisposition
{
    Cancelled,
    Failed
}

/// <summary>
/// Fixes one failure tree's evidence and disposition; commitment, exit codes, and wording belong to callers.
/// </summary>
internal sealed class WorkbookAutomationTerminalFacts
{
    private WorkbookAutomationTerminalFacts(
        ImmutableArray<WorkbookAutomationFailure> failures,
        ImmutableArray<Exception> unknownFailures,
        bool cancellationObserved,
        bool callerCancellationRequested)
    {
        Failures = failures;
        UnknownFailures = unknownFailures;
        CancellationObserved = cancellationObserved;
        CallerCancellationRequested = callerCancellationRequested;
        ProcessReleaseProven = !failures.Any(failure =>
            failure.Category == WorkbookAutomationFailureCategory.UnprovedProcessRelease);
        DispatcherRetired = !failures.Any(failure =>
            failure.Category == WorkbookAutomationFailureCategory.UnprovedDispatcherRetirement);
        HasUnprovedLifecycle = !ProcessReleaseProven || !DispatcherRetired;
        TypedCancellation = failures.FirstOrDefault(failure => failure.Error is WorkbookAutomationCanceledException);
        HasTrustedCancellationAuthority = TypedCancellation is not null || callerCancellationRequested;

        var primaryIndex = -1;
        var primaryPriority = int.MaxValue;
        for (var index = 0; index < failures.Length; index++)
        {
            var priority = Priority(failures[index].Category);
            if (priority < primaryPriority)
            {
                primaryIndex = index;
                primaryPriority = priority;
            }
        }
        PrimaryFailure = primaryIndex < 0 ? null : failures[primaryIndex];
        SecondaryFailures = failures.Where((_, index) => index != primaryIndex).ToImmutableArray();
        IsUntrustedCancellation = unknownFailures.IsEmpty
            && PrimaryFailure?.Category == WorkbookAutomationFailureCategory.Cancellation
            && !HasTrustedCancellationAuthority;

        Disposition = HasUnprovedLifecycle
            ? WorkbookAutomationDisposition.Failed
            : !unknownFailures.IsEmpty || PrimaryFailure is null
                ? null
                : PrimaryFailure.Category != WorkbookAutomationFailureCategory.Cancellation
                    ? WorkbookAutomationDisposition.Failed
                    : HasTrustedCancellationAuthority
                        ? WorkbookAutomationDisposition.Cancelled
                        : null;
    }

    internal ImmutableArray<WorkbookAutomationFailure> Failures { get; }
    internal ImmutableArray<Exception> UnknownFailures { get; }
    // Highest-priority known evidence can also exist in an unclassified mixed tree.
    // Disposition, not this candidate alone, authorizes a recognized command decision.
    internal WorkbookAutomationFailure? PrimaryFailure { get; }
    internal ImmutableArray<WorkbookAutomationFailure> SecondaryFailures { get; }
    internal WorkbookAutomationFailure? TypedCancellation { get; }
    internal bool CancellationObserved { get; }
    internal bool CallerCancellationRequested { get; }
    internal bool HasTrustedCancellationAuthority { get; }
    internal bool IsUntrustedCancellation { get; }
    internal bool ProcessReleaseProven { get; }
    internal bool DispatcherRetired { get; }
    internal bool HasUnprovedLifecycle { get; }
    internal WorkbookAutomationDisposition? Disposition { get; }
    internal bool IsRecognized => Disposition is not null;

    private static int Priority(WorkbookAutomationFailureCategory category)
        => category switch
        {
            WorkbookAutomationFailureCategory.UnprovedProcessRelease => 0,
            WorkbookAutomationFailureCategory.UnprovedDispatcherRetirement => 1,
            WorkbookAutomationFailureCategory.CleanupAfterProvedRelease => 2,
            WorkbookAutomationFailureCategory.ProcessLoss => 3,
            WorkbookAutomationFailureCategory.Timeout => 4,
            WorkbookAutomationFailureCategory.ComFailure => 5,
            WorkbookAutomationFailureCategory.Cancellation => 6,
            _ => throw new ArgumentOutOfRangeException(nameof(category))
        };

    internal static WorkbookAutomationTerminalFacts Analyze(Exception error, bool callerCancellationRequested = false)
    {
        ArgumentNullException.ThrowIfNull(error);
        var failures = ImmutableArray.CreateBuilder<WorkbookAutomationFailure>();
        var unknown = ImmutableArray.CreateBuilder<Exception>();
        var cancellationObserved = callerCancellationRequested;
        Visit(error, false, null, null);
        return new WorkbookAutomationTerminalFacts(
            failures.ToImmutable(), unknown.ToImmutable(), cancellationObserved, callerCancellationRequested);

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
