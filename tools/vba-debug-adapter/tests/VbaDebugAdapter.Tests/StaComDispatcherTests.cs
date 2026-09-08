using VbaDebugAdapter.Infrastructure;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class StaComDispatcherTests
{
    [Fact]
    public async Task ConcurrentDisposalWaitsForTheOwnedWorkerBeforeReportingRelease()
    {
        var dispatcher = new StaComDispatcher();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocation = dispatcher.InvokeAsync(() =>
        {
            entered.SetResult();
            release.Task.GetAwaiter().GetResult();
            return true;
        }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var first = dispatcher.DisposeAsync().AsTask();
        var second = dispatcher.DisposeAsync().AsTask();
        try
        {
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.False(dispatcher.ReleaseVerified);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(first, second, invocation).WaitAsync(TimeSpan.FromSeconds(2));
        }

        Assert.True(dispatcher.ReleaseVerified);
        await dispatcher.DisposeAsync();
    }

    [Fact]
    public async Task CancelledInvocationPreservesTheOriginalCancellationAndItsStack()
    {
        await using var dispatcher = new StaComDispatcher();
        using var cancellation = new CancellationTokenSource();
        var original = new OperationCanceledException("Original COM operation cancellation.", cancellation.Token);
        var invocation = dispatcher.InvokeAsync(() =>
        {
            cancellation.Cancel();
            return ThrowOriginalCancellation(original);
        }, cancellation.Token);

        var observed = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);

        Assert.Same(original, observed);
        Assert.Contains(nameof(ThrowOriginalCancellation), observed.StackTrace);
        Assert.True(invocation.IsCanceled);
    }

    private static bool ThrowOriginalCancellation(OperationCanceledException cancellation)
        => throw cancellation;
}
