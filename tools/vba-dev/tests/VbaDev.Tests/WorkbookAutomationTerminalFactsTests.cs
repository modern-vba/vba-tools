using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Workbooks;
using Xunit;

namespace VbaDev.Tests;

public sealed class WorkbookAutomationTerminalFactsTests
{
    [Theory]
    [InlineData("typed-cancellation")]
    [InlineData("caller-requested-cancellation")]
    [InlineData("plain-untrusted-cancellation")]
    [InlineData("nested-untrusted-cancellation")]
    [InlineData("lifecycle-observation-is-not-authority")]
    [InlineData("lifecycle-observation-without-cancellation")]
    [InlineData("unknown-only")]
    [InlineData("unknown-with-requested-caller")]
    [InlineData("unknown-with-com")]
    [InlineData("unknown-with-typed-cancellation")]
    [InlineData("unknown-with-plain-cancellation")]
    [InlineData("unknown-with-caller-cancellation")]
    [InlineData("unknown-with-unproved-process")]
    [InlineData("unknown-with-unproved-dispatcher")]
    [InlineData("direct-com")]
    [InlineData("nested-com")]
    [InlineData("aggregate-com-first-tie")]
    [InlineData("typed-cancellation-supporting-cause")]
    [InlineData("typed-cancellation-unproved-process")]
    [InlineData("typed-cancellation-unproved-dispatcher")]
    [InlineData("one-error-retains-two-proof-occurrences")]
    [InlineData("proved-subtree-supersedes-earlier-uncertainty")]
    [InlineData("proved-subtree-does-not-release-independent-sibling")]
    [InlineData("equal-timeout-priority-keeps-first")]
    [InlineData("priority-cancellation")]
    [InlineData("priority-com")]
    [InlineData("priority-timeout")]
    [InlineData("priority-process-loss")]
    [InlineData("priority-released-cleanup")]
    [InlineData("priority-dispatcher")]
    [InlineData("priority-process-release")]
    [InlineData("analysis-is-immutable-without-a-global-cache")]
    public void AnalysisConformsToTheNeutralTerminalContract(string caseName)
    {
        var scenario = CreateConformanceCase(caseName);

        var facts = WorkbookAutomationTerminalFacts.Analyze(scenario.Error, scenario.CallerCancellationRequested);

        AssertConformance(scenario, facts);
        if (scenario.MutateLifecycleEvidence is not null)
        {
            var primary = facts.PrimaryFailure;
            scenario.MutateLifecycleEvidence();
            AssertConformance(scenario, facts);
            Assert.Same(primary, facts.PrimaryFailure);
            Assert.NotNull(scenario.AfterMutation);
            var refreshed = WorkbookAutomationTerminalFacts.Analyze(
                scenario.Error, scenario.CallerCancellationRequested);
            Assert.NotSame(facts, refreshed);
            AssertConformance(scenario.AfterMutation!, refreshed);
        }
    }

    private static void AssertConformance(TerminalConformanceCase expected, WorkbookAutomationTerminalFacts actual)
    {
        Assert.Equal(expected.Disposition, actual.Disposition);
        Assert.Equal(expected.Disposition is not null, actual.IsRecognized);
        Assert.Equal(expected.CancellationObserved, actual.CancellationObserved);
        Assert.Equal(expected.CallerCancellationRequested, actual.CallerCancellationRequested);
        Assert.Equal(expected.HasTrustedCancellationAuthority, actual.HasTrustedCancellationAuthority);
        Assert.Equal(expected.IsUntrustedCancellation, actual.IsUntrustedCancellation);
        Assert.Equal(expected.ProcessReleaseProven, actual.ProcessReleaseProven);
        Assert.Equal(expected.DispatcherRetired, actual.DispatcherRetired);
        Assert.Equal(!expected.ProcessReleaseProven || !expected.DispatcherRetired, actual.HasUnprovedLifecycle);
        Assert.Same(expected.TypedCancellation, actual.TypedCancellation?.Error);
        Assert.Equal(expected.Failures.Length, actual.Failures.Length);
        for (var index = 0; index < expected.Failures.Length; index++)
        {
            Assert.Equal(expected.Failures[index].Category, actual.Failures[index].Category);
            Assert.Same(expected.Failures[index].Error, actual.Failures[index].Error);
            Assert.Equal(expected.Failures[index].Stage, actual.Failures[index].Stage);
        }
        if (expected.PrimaryIndex is int primaryIndex)
        {
            Assert.Same(actual.Failures[primaryIndex], actual.PrimaryFailure);
        }
        else
        {
            Assert.Null(actual.PrimaryFailure);
        }
        var secondaryIndices = Enumerable.Range(0, expected.Failures.Length)
            .Where(index => index != expected.PrimaryIndex).ToArray();
        Assert.Equal(secondaryIndices.Length, actual.SecondaryFailures.Length);
        for (var index = 0; index < secondaryIndices.Length; index++)
        {
            Assert.Same(actual.Failures[secondaryIndices[index]], actual.SecondaryFailures[index]);
        }
        Assert.Equal(expected.UnknownFailures.Length, actual.UnknownFailures.Length);
        for (var index = 0; index < expected.UnknownFailures.Length; index++)
        {
            Assert.Same(expected.UnknownFailures[index], actual.UnknownFailures[index]);
        }
    }

    private static TerminalConformanceCase CreateConformanceCase(string caseName)
    {
        var importStage = new WorkbookAutomationStage(WorkbookAutomationStageKind.ModuleImport, "Source.bas");
        var saveStage = new WorkbookAutomationStage(WorkbookAutomationStageKind.WorkbookSave, "Book.xlsm");
        var typed = new WorkbookAutomationCanceledException(importStage, CancellationToken.None);
        var plain = new OperationCanceledException("Untrusted cancellation");
        var com = new System.Runtime.InteropServices.COMException("COM evidence");
        var unknown = new NullReferenceException("Independent unknown defect");
        var cancellation = new ExpectedFailure(WorkbookAutomationFailureCategory.Cancellation, typed, importStage);

        switch (caseName)
        {
            case "typed-cancellation":
                return new(typed, WorkbookAutomationDisposition.Cancelled, [cancellation], 0, [], true, true,
                    TypedCancellation: typed);
            case "caller-requested-cancellation":
                return new(plain, WorkbookAutomationDisposition.Cancelled,
                    [new(WorkbookAutomationFailureCategory.Cancellation, plain, null)], 0, [], true, true,
                    CallerCancellationRequested: true);
            case "plain-untrusted-cancellation":
                return new(plain, null,
                    [new(WorkbookAutomationFailureCategory.Cancellation, plain, null)], 0, [], true, false,
                    IsUntrustedCancellation: true);
            case "nested-untrusted-cancellation":
            case "lifecycle-observation-is-not-authority":
            {
                var bounded = new WorkbookAutomationStageFailureException(importStage,
                    new InvalidOperationException("Transparent context", plain));
                if (caseName == "lifecycle-observation-is-not-authority")
                {
                    ((IWorkbookAutomationLifecycleFailure)bounded).LifecycleEvidence =
                        new(importStage, true, true, true);
                }
                return new(bounded, null,
                    [new(WorkbookAutomationFailureCategory.Cancellation, plain, importStage)], 0, [], true, false,
                    IsUntrustedCancellation: true);
            }
            case "unknown-only":
                return new(unknown, null, [], null, [unknown], false, false);
            case "lifecycle-observation-without-cancellation":
            {
                var bounded = new WorkbookAutomationStageFailureException(importStage, unknown);
                ((IWorkbookAutomationLifecycleFailure)bounded).LifecycleEvidence = new(importStage, true, true, true);
                return new(bounded, null, [], null, [unknown], true, false);
            }
            case "unknown-with-requested-caller":
                return new(unknown, null, [], null, [unknown], true, true, CallerCancellationRequested: true);
            case "unknown-with-com":
            {
                var secondUnknown = new ArgumentException("Later independent unknown defect");
                return new(new AggregateException(unknown, new AggregateException(com, secondUnknown)), null,
                    [new(WorkbookAutomationFailureCategory.ComFailure, com, null)],
                    0, [unknown, secondUnknown], false, false);
            }
            case "unknown-with-typed-cancellation":
                return new(new AggregateException(typed, unknown), null, [cancellation], 0, [unknown], true, true,
                    TypedCancellation: typed);
            case "unknown-with-plain-cancellation":
            case "unknown-with-caller-cancellation":
            {
                var requested = caseName == "unknown-with-caller-cancellation";
                return new(new AggregateException(plain, unknown), null,
                    [new(WorkbookAutomationFailureCategory.Cancellation, plain, null)], 0, [unknown], true, requested,
                    CallerCancellationRequested: requested);
            }
            case "unknown-with-unproved-process":
            case "unknown-with-unproved-dispatcher":
            {
                var processReleased = caseName == "unknown-with-unproved-dispatcher";
                var cleanup = CreateCleanup(saveStage, processReleased, !processReleased);
                return new(new AggregateException(plain, unknown, cleanup), WorkbookAutomationDisposition.Failed,
                    [new(WorkbookAutomationFailureCategory.Cancellation, plain, null),
                        new(processReleased ? WorkbookAutomationFailureCategory.UnprovedDispatcherRetirement
                            : WorkbookAutomationFailureCategory.UnprovedProcessRelease, cleanup, saveStage)],
                    1, [unknown], true, false,
                    ProcessReleaseProven: processReleased, DispatcherRetired: !processReleased);
            }
            case "direct-com":
                return new(com, WorkbookAutomationDisposition.Failed,
                    [new(WorkbookAutomationFailureCategory.ComFailure, com, null)], 0, [], false, false);
            case "nested-com":
                return new(new WorkbookAutomationStageFailureException(importStage,
                        new InvalidOperationException("Transparent context", com)), WorkbookAutomationDisposition.Failed,
                    [new(WorkbookAutomationFailureCategory.ComFailure, com, importStage)], 0, [], false, false);
            case "aggregate-com-first-tie":
            {
                var second = new System.Runtime.InteropServices.COMException("Second COM evidence");
                return new(new AggregateException(new WorkbookAutomationStageFailureException(importStage, com),
                        new WorkbookAutomationStageFailureException(saveStage, second)), WorkbookAutomationDisposition.Failed,
                    [new(WorkbookAutomationFailureCategory.ComFailure, com, importStage),
                        new(WorkbookAutomationFailureCategory.ComFailure, second, saveStage)], 0, [], false, false);
            }
            case "typed-cancellation-supporting-cause":
            {
                var supported = new WorkbookAutomationCanceledException(importStage, CancellationToken.None, unknown);
                return new(supported, WorkbookAutomationDisposition.Cancelled,
                    [new(WorkbookAutomationFailureCategory.Cancellation, supported, importStage)], 0, [], true, true,
                    TypedCancellation: supported);
            }
            case "typed-cancellation-unproved-process":
            case "typed-cancellation-unproved-dispatcher":
            {
                var processReleased = caseName == "typed-cancellation-unproved-dispatcher";
                ((IWorkbookAutomationLifecycleFailure)typed).LifecycleEvidence =
                    new(importStage, processReleased, !processReleased, false);
                return new(typed, WorkbookAutomationDisposition.Failed,
                    [new(processReleased ? WorkbookAutomationFailureCategory.UnprovedDispatcherRetirement
                            : WorkbookAutomationFailureCategory.UnprovedProcessRelease, typed, importStage), cancellation],
                    0, [], true, true, TypedCancellation: typed,
                    ProcessReleaseProven: processReleased, DispatcherRetired: !processReleased);
            }
            case "one-error-retains-two-proof-occurrences":
            {
                var cleanup = CreateCleanup(saveStage, false, false);
                return new(new AggregateException(com, cleanup, plain), WorkbookAutomationDisposition.Failed,
                    [new(WorkbookAutomationFailureCategory.ComFailure, com, null),
                        new(WorkbookAutomationFailureCategory.UnprovedProcessRelease, cleanup, saveStage),
                        new(WorkbookAutomationFailureCategory.UnprovedDispatcherRetirement, cleanup, saveStage),
                        new(WorkbookAutomationFailureCategory.Cancellation, plain, null)],
                    1, [], true, false, ProcessReleaseProven: false, DispatcherRetired: false);
            }
            case "proved-subtree-supersedes-earlier-uncertainty":
            case "proved-subtree-does-not-release-independent-sibling":
            {
                var earlier = CreateCleanup(importStage, false, false);
                var released = new WorkbookAutomationReleasedProcessCleanupException("Final release proved", earlier);
                ((IWorkbookAutomationLifecycleFailure)released).LifecycleEvidence = new(saveStage, true, true, false);
                ExpectedFailure[] provedFailures =
                    [new(WorkbookAutomationFailureCategory.CleanupAfterProvedRelease, released, saveStage),
                        new(WorkbookAutomationFailureCategory.CleanupAfterProvedRelease, earlier, saveStage)];
                if (caseName == "proved-subtree-supersedes-earlier-uncertainty")
                {
                    return new(released, WorkbookAutomationDisposition.Failed, provedFailures, 0, [], false, false);
                }
                var sibling = CreateCleanup(importStage, false, false);
                return new(new AggregateException(released, sibling), WorkbookAutomationDisposition.Failed,
                    [.. provedFailures,
                        new(WorkbookAutomationFailureCategory.UnprovedProcessRelease, sibling, importStage),
                        new(WorkbookAutomationFailureCategory.UnprovedDispatcherRetirement, sibling, importStage)],
                    2, [], false, false, ProcessReleaseProven: false, DispatcherRetired: false);
            }
            case "equal-timeout-priority-keeps-first":
            {
                var first = new WorkbookAutomationTimeoutException(importStage, TimeSpan.FromSeconds(1));
                var second = new WorkbookAutomationTimeoutException(saveStage, TimeSpan.FromSeconds(2));
                return new(new AggregateException(first, com, second), WorkbookAutomationDisposition.Failed,
                    [new(WorkbookAutomationFailureCategory.Timeout, first, importStage),
                        new(WorkbookAutomationFailureCategory.ComFailure, com, null),
                        new(WorkbookAutomationFailureCategory.Timeout, second, saveStage)], 0, [], false, false);
            }
            case "analysis-is-immutable-without-a-global-cache":
            {
                var lifecycle = (IWorkbookAutomationLifecycleFailure)typed;
                lifecycle.LifecycleEvidence = new(saveStage, false, false, false);
                return new(typed, WorkbookAutomationDisposition.Failed,
                    [new(WorkbookAutomationFailureCategory.UnprovedProcessRelease, typed, saveStage),
                        new(WorkbookAutomationFailureCategory.UnprovedDispatcherRetirement, typed, saveStage), cancellation],
                    0, [], true, true, TypedCancellation: typed, ProcessReleaseProven: false, DispatcherRetired: false,
                    MutateLifecycleEvidence: () => lifecycle.LifecycleEvidence = new(importStage, true, true, false),
                    AfterMutation: new(typed, WorkbookAutomationDisposition.Cancelled, [cancellation], 0, [], true, true,
                        TypedCancellation: typed));
            }
        }

        var evidenceCount = caseName switch
        {
            "priority-cancellation" => 1,
            "priority-com" => 2,
            "priority-timeout" => 3,
            "priority-process-loss" => 4,
            "priority-released-cleanup" => 5,
            "priority-dispatcher" => 6,
            "priority-process-release" => 7,
            _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, null)
        };
        var timeout = new WorkbookAutomationTimeoutException(importStage, TimeSpan.FromSeconds(1));
        var lost = new WorkbookAutomationProcessLostException(importStage);
        var releasedCleanup = new WorkbookAutomationReleasedProcessCleanupException("Released cleanup evidence");
        var dispatcher = CreateCleanup(importStage, true, false);
        var unproved = CreateCleanup(importStage, false, true);
        ExpectedFailure[] priorityEvidence =
        [
            cancellation,
            new(WorkbookAutomationFailureCategory.ComFailure, com, importStage),
            new(WorkbookAutomationFailureCategory.Timeout, timeout, importStage),
            new(WorkbookAutomationFailureCategory.ProcessLoss, lost, importStage),
            new(WorkbookAutomationFailureCategory.CleanupAfterProvedRelease, releasedCleanup, importStage),
            new(WorkbookAutomationFailureCategory.UnprovedDispatcherRetirement, dispatcher, importStage),
            new(WorkbookAutomationFailureCategory.UnprovedProcessRelease, unproved, importStage)
        ];
        var expectedFailures = priorityEvidence.Take(evidenceCount).ToArray();
        return new(new WorkbookAutomationStageFailureException(importStage,
                new AggregateException(expectedFailures.Select(failure => failure.Error))),
            evidenceCount == 1 ? WorkbookAutomationDisposition.Cancelled : WorkbookAutomationDisposition.Failed,
            expectedFailures, evidenceCount - 1, [], true, true, TypedCancellation: typed,
            ProcessReleaseProven: evidenceCount != 7, DispatcherRetired: evidenceCount < 6);
    }

    private static WorkbookAutomationCleanupException CreateCleanup(
        WorkbookAutomationStage stage, bool processReleased, bool dispatcherRetired)
    {
        var cleanup = new WorkbookAutomationCleanupException("Lifecycle evidence");
        ((IWorkbookAutomationLifecycleFailure)cleanup).LifecycleEvidence = new(stage, processReleased, dispatcherRetired, false);
        return cleanup;
    }

    private sealed record ExpectedFailure(
        WorkbookAutomationFailureCategory Category, Exception Error, WorkbookAutomationStage? Stage);

    private sealed record TerminalConformanceCase(
        Exception Error,
        WorkbookAutomationDisposition? Disposition,
        ExpectedFailure[] Failures,
        int? PrimaryIndex,
        Exception[] UnknownFailures,
        bool CancellationObserved,
        bool HasTrustedCancellationAuthority,
        bool CallerCancellationRequested = false,
        Exception? TypedCancellation = null,
        bool ProcessReleaseProven = true,
        bool DispatcherRetired = true,
        bool IsUntrustedCancellation = false,
        Action? MutateLifecycleEvidence = null,
        TerminalConformanceCase? AfterMutation = null);

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void IndependentUnknownDefectCannotHideUnprovedLifecycleEvidence(
        bool processReleased, bool dispatcherRetired)
    {
        var stage = new WorkbookAutomationStage(WorkbookAutomationStageKind.WorkbookSave);
        var cleanup = new WorkbookAutomationCleanupException("Lifecycle release was not proved");
        ((IWorkbookAutomationLifecycleFailure)cleanup).LifecycleEvidence =
            new(stage, processReleased, dispatcherRetired, true);
        var unknown = new NullReferenceException("Independent defect");
        var error = new AggregateException(new OperationCanceledException(), unknown, cleanup);

        var facts = WorkbookAutomationTerminalFacts.Analyze(error);
        Assert.True(facts.IsRecognized);
        Assert.Equal(processReleased ? WorkbookAutomationFailureCategory.UnprovedDispatcherRetirement
            : WorkbookAutomationFailureCategory.UnprovedProcessRelease, facts.PrimaryFailure!.Category);
        Assert.Equal(processReleased, facts.ProcessReleaseProven);
        Assert.Equal(dispatcherRetired, facts.DispatcherRetired);
        Assert.Contains(unknown, facts.UnknownFailures);
        Assert.True(facts.CancellationObserved);
    }

    [Fact]
    public void RuntimeTimeoutKeepsCancellationObservedDuringSuccessfulCleanup()
    {
        var stage = new WorkbookAutomationStage(WorkbookAutomationStageKind.ModuleImport);
        var timeout = new WorkbookAutomationTimeoutException(stage, TimeSpan.FromSeconds(1));
        var outcome = new AutomationExcelProcessOutcome<string>(null,
            new(stage, timeout, null, null, true, true, true, null));
        var error = Assert.Throws<WorkbookAutomationTimeoutException>(() => outcome.GetReleasedResult());

        var facts = WorkbookAutomationTerminalFacts.Analyze(error);
        Assert.True(facts.IsRecognized);
        Assert.True(facts.CancellationObserved);
        Assert.Equal(WorkbookAutomationFailureCategory.Timeout, facts.PrimaryFailure!.Category);
    }

    [Fact]
    public void NestedComFailureInheritsTheBoundedOperationStage()
    {
        var stage = new WorkbookAutomationStage(WorkbookAutomationStageKind.ReferenceAttempt, "Library");
        var com = new System.Runtime.InteropServices.COMException("Localized detail");
        var timeout = new WorkbookAutomationTimeoutException(stage, TimeSpan.FromSeconds(1), com);

        var facts = WorkbookAutomationTerminalFacts.Analyze(timeout);
        Assert.True(facts.IsRecognized);
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

        var facts = WorkbookAutomationTerminalFacts.Analyze(error);
        Assert.False(facts.IsRecognized);
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

        var facts = WorkbookAutomationTerminalFacts.Analyze(error);
        Assert.True(facts.IsRecognized);
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

        var facts = WorkbookAutomationTerminalFacts.Analyze(error);
        Assert.True(facts.IsRecognized);
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

        var facts = WorkbookAutomationTerminalFacts.Analyze(error);
        Assert.True(facts.IsRecognized);
        Assert.True(facts.CancellationObserved);
        Assert.True(facts.ProcessReleaseProven);
        Assert.True(facts.DispatcherRetired);
        Assert.Contains(facts.Failures, failure => ReferenceEquals(failure.Error, timeout) && failure.Stage == stage);
    }
}
