using VbaDebugAdapter.Infrastructure;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class WindowsDebugProcessJobTests
{
    [Fact]
    public void FailedJobConfigurationRetainsItsCauseAndNativeHandleReleaseEvidence()
    {
        var original = new IOException("Job configuration failed.");

        var failure = Record.Exception(() => WindowsDebugProcessJob.Create(_ => throw original));

        var outcome = Assert.IsAssignableFrom<IDebugFailureEvidence>(failure).FailureOutcome;
        Assert.Same(original, outcome.PrimaryFailure);
        Assert.Contains(nameof(FailedJobConfigurationRetainsItsCauseAndNativeHandleReleaseEvidence),
            outcome.PrimaryFailure!.StackTrace!);
        Assert.False(outcome.HasCleanupFailure);
        Assert.False(outcome.HasUnprovedRelease);
        Assert.Contains(outcome.Evidence, item => item.Kind == DebugResourceKind.Handle && item.Released);
    }
}
