using System.Runtime.InteropServices;
using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Workbooks;
using Xunit;

namespace VbaDev.Tests;

public sealed class ReleasedAutomationCleanupPolicyTests
{
    [Fact]
    public void ComOnlyCleanupPreservesThePrimaryFailureAfterVerifiedRelease()
    {
        var result = ReleasedAutomationCleanupPolicy.CanPreservePrimaryFailure(
            CreateTerminalFailure(),
            new COMException("The RPC server is unavailable."));

        Assert.True(result);
    }

    [Fact]
    public void InvalidComObjectOnlyCleanupPreservesThePrimaryFailureAfterVerifiedRelease()
    {
        var result = ReleasedAutomationCleanupPolicy.CanPreservePrimaryFailure(
            CreateTerminalFailure(),
            new InvalidComObjectException("The COM object has been released."));

        Assert.True(result);
    }

    [Fact]
    public void ReleasedCleanupWrapperWithComLeafPreservesThePrimaryFailure()
    {
        var cleanupError = new WorkbookAutomationReleasedProcessCleanupException(
            "Cooperative cleanup failed after release.",
            new COMException("The RPC server is unavailable."));

        var result = ReleasedAutomationCleanupPolicy.CanPreservePrimaryFailure(
            CreateTerminalFailure(),
            cleanupError);

        Assert.True(result);
    }

    [Fact]
    public void ReleasedCleanupWrapperIsAProofClassificationBoundary()
    {
        var historicalProofFailure = new WorkbookAutomationCleanupException(
            "A prior cleanup attempt could not prove release.");
        var releasedCleanup = new WorkbookAutomationReleasedProcessCleanupException(
            "A later exact cleanup proved release.",
            historicalProofFailure);

        var containsProofFailure =
            WorkbookAutomationFailureClassifier.ContainsCleanupProofFailure(
                releasedCleanup);

        Assert.False(containsProofFailure);
    }

    [Fact]
    public void ArbitraryWrapperWithComLeafDoesNotPreserveThePrimaryFailure()
    {
        var cleanupError = new InvalidOperationException(
            "Unexpected cleanup defect.",
            new COMException("The RPC server is unavailable."));

        var result = ReleasedAutomationCleanupPolicy.CanPreservePrimaryFailure(
            CreateTerminalFailure(),
            cleanupError);

        Assert.False(result);
    }

    [Fact]
    public void MissingMemberOnlyCleanupPreservesThePrimaryFailureAfterVerifiedRelease()
    {
        var result = ReleasedAutomationCleanupPolicy.CanPreservePrimaryFailure(
            CreateTerminalFailure(),
            new MissingMemberException(
                "The released Excel process no longer exposes workbook.Close."));

        Assert.True(result);
    }

    [Fact]
    public void MixedComAndNonComCleanupLeavesDoNotPreserveThePrimaryFailure()
    {
        var cleanupError = new AggregateException(
            new COMException("The RPC server is unavailable."),
            new InvalidOperationException("Unexpected cleanup defect."));

        var result = ReleasedAutomationCleanupPolicy.CanPreservePrimaryFailure(
            CreateTerminalFailure(),
            cleanupError);

        Assert.False(result);
    }

    private static WorkbookAutomationTimeoutException CreateTerminalFailure()
        => new(
            new WorkbookAutomationStage(WorkbookAutomationStageKind.WorkbookSave),
            TimeSpan.FromSeconds(1));
}
