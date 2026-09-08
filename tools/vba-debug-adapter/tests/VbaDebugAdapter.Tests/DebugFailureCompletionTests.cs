using VbaDebugAdapter.Infrastructure;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class DebugFailureCompletionTests
{
    [Fact]
    public void MergingASelfCausalOwnerOutcomeRetainsOneCauseWithoutReopeningIt()
    {
        var primary = new InvalidOperationException("Original debug failure.");
        var rejected = Assert.Throws<ExistingExcelProcessOwnershipRejectedException>(
            (Action)(() => throw new ExistingExcelProcessOwnershipRejectedException()));
        var completion = new DebugFailureCompletion(primary);

        completion.Merge(rejected.FailureOutcome);
        completion.AddFailure("ownership", "Excel process", DebugResourceKind.Process, rejected);
        var outcome = completion.Complete();

        Assert.Same(primary, outcome.PrimaryFailure);
        Assert.Same(rejected, Assert.Single(outcome.CleanupFailures).Exception);
        Assert.Single(outcome.Evidence, evidence => evidence.Kind == DebugResourceKind.Process && evidence.Released);
        Assert.Same(outcome, completion.Complete());
    }
}
