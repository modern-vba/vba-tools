using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Workbooks;
using Xunit;

namespace VbaDev.Tests;

public sealed class WorkbookAutomationTerminalFactsTests
{
    [Fact]
    public void RuntimeTimeoutKeepsCancellationObservedDuringSuccessfulCleanup()
    {
        var stage = new WorkbookAutomationStage(WorkbookAutomationStageKind.ModuleImport);
        var timeout = new WorkbookAutomationTimeoutException(stage, TimeSpan.FromSeconds(1));
        var outcome = new AutomationExcelProcessOutcome<string>(null,
            new(stage, timeout, null, null, true, true, true, null));
        var error = Assert.Throws<WorkbookAutomationTimeoutException>(() => outcome.GetReleasedResult());

        Assert.True(WorkbookAutomationFailureClassifier.TryClassify(error, out var facts));
        Assert.True(facts.CancellationObserved);
        Assert.Equal(WorkbookAutomationFailureCategory.Timeout, facts.PrimaryFailure!.Category);
    }

    [Fact]
    public void NestedComFailureInheritsTheBoundedOperationStage()
    {
        var stage = new WorkbookAutomationStage(WorkbookAutomationStageKind.ReferenceAttempt, "Library");
        var com = new System.Runtime.InteropServices.COMException("Localized detail");
        var timeout = new WorkbookAutomationTimeoutException(stage, TimeSpan.FromSeconds(1), com);

        Assert.True(WorkbookAutomationFailureClassifier.TryClassify(timeout, out var facts));
        Assert.Equal(stage, facts.Failures.Single(failure => ReferenceEquals(failure.Error, com)).Stage);
        Assert.Equal(WorkbookAutomationFailureCategory.Timeout, facts.PrimaryFailure!.Category);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownProgrammingFailureCannotBecomeRecognizedCancellation(bool nestedCancellation)
    {
        var cancellation = new OperationCanceledException();
        var defect = new NullReferenceException("Unexpected defect", nestedCancellation ? cancellation : null);
        Exception error = nestedCancellation ? defect : new AggregateException(cancellation, defect);

        Assert.False(WorkbookAutomationFailureClassifier.TryClassify(error, out var facts));
        Assert.True(facts.CancellationObserved);
        Assert.Contains(defect, facts.UnknownFailures);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void RuntimeCleanupFailureRetainsBothReleaseProofsAndCleanupTimeCancellation(
        bool processReleased, bool dispatcherRetired)
    {
        var stage = new WorkbookAutomationStage(WorkbookAutomationStageKind.WorkbookSave);
        Exception cleanup = processReleased
            ? new WorkbookAutomationReleasedProcessCleanupException("Secondary failure")
            : new WorkbookAutomationCleanupException("Uncertain process release");
        var outcome = new AutomationExcelProcessOutcome<string>("saved", new(
            stage, null, cleanup,
            dispatcherRetired ? null : new InvalidOperationException("Dispatcher failed"),
            true, processReleased, dispatcherRetired, null));
        var error = Assert.ThrowsAny<Exception>(() => outcome.GetReleasedResult());

        Assert.True(WorkbookAutomationFailureClassifier.TryClassify(error, out var facts));
        Assert.Equal(processReleased, facts.ProcessReleaseProven);
        Assert.Equal(dispatcherRetired, facts.DispatcherRetired);
        Assert.True(facts.CancellationObserved);
        Assert.Equal(stage, facts.PrimaryFailure!.Stage);
    }

    [Fact]
    public void ProvedReleaseKeepsCancellationAndProcessLossInsideSecondaryCleanup()
    {
        var stage = new WorkbookAutomationStage(WorkbookAutomationStageKind.WorkbookSave);
        var lost = new WorkbookAutomationProcessLostException(stage);
        var error = new WorkbookAutomationReleasedProcessCleanupException(
            "Translated cleanup text",
            new AggregateException(lost, new WorkbookAutomationCleanupException(
                "Earlier release uncertainty", new OperationCanceledException())));

        Assert.True(WorkbookAutomationFailureClassifier.TryClassify(error, out var facts));
        Assert.True(facts.CancellationObserved);
        Assert.True(facts.ProcessReleaseProven);
        Assert.True(facts.DispatcherRetired);
        Assert.Contains(facts.Failures, failure => ReferenceEquals(failure.Error, lost) && failure.Stage == stage);
        Assert.Contains(facts.Failures, failure => ReferenceEquals(failure.Error, error));
    }

    [Fact]
    public void TimeoutKeepsItsStageAndConcurrentCancellationEvidence()
    {
        var stage = new WorkbookAutomationStage(WorkbookAutomationStageKind.ModuleImport, "Module1");
        var timeout = new WorkbookAutomationTimeoutException(stage, TimeSpan.FromSeconds(1));
        var error = new AggregateException(new OperationCanceledException(), timeout);

        Assert.True(WorkbookAutomationFailureClassifier.TryClassify(error, out var facts));
        Assert.True(facts.CancellationObserved);
        Assert.True(facts.ProcessReleaseProven);
        Assert.True(facts.DispatcherRetired);
        Assert.Contains(facts.Failures, failure => ReferenceEquals(failure.Error, timeout) && failure.Stage == stage);
    }
}
