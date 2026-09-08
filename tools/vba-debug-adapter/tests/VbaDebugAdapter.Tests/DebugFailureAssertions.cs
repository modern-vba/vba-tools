using VbaDebugAdapter.Infrastructure;
using Xunit;

namespace VbaDebugAdapter.Tests;

internal static class DebugFailureAssertions
{
    public static async Task<T> ThrowsWithProvedCleanupAsync<T>(Func<Task> action) where T : Exception
        => HasProvedCleanup<T>(await Record.ExceptionAsync(action));

    public static T HasProvedCleanup<T>(Exception? exception) where T : Exception
    {
        var outcome = Assert.IsAssignableFrom<IDebugFailureEvidence>(exception).FailureOutcome;
        Assert.False(outcome.HasCleanupFailure, outcome.Describe());
        return Assert.IsType<T>(outcome.PrimaryFailure);
    }
}
