using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaCancellationSafeMemoTests
{
    [Fact]
    public void Cancelled_build_does_not_poison_a_later_retry()
    {
        var memo = new VbaCancellationSafeMemo<string>();
        using var cancellation = new CancellationTokenSource();
        var buildCount = 0;

        Assert.Throws<OperationCanceledException>(() =>
            memo.Get(
                token =>
                {
                    Interlocked.Increment(ref buildCount);
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                    return "cancelled";
                },
                cancellation.Token));

        var result = memo.Get(
            _ =>
            {
                Interlocked.Increment(ref buildCount);
                return "usable";
            },
            CancellationToken.None);

        Assert.Equal("usable", result);
        Assert.Equal(2, buildCount);
    }

    [Fact]
    public async Task Cancelled_waiter_does_not_cancel_or_replace_the_shared_build()
    {
        var memo = new VbaCancellationSafeMemo<string>();
        using var firstBuildEntered = new ManualResetEventSlim();
        using var releaseFirstBuild = new ManualResetEventSlim();
        var first = Task.Run(() =>
            memo.Get(
                _ =>
                {
                    firstBuildEntered.Set();
                    releaseFirstBuild.Wait();
                    return "shared";
                },
                CancellationToken.None));
        Assert.True(firstBuildEntered.Wait(TimeSpan.FromSeconds(5)));
        using var waiterCancellation = new CancellationTokenSource();
        var waiter = Task.Run(() =>
            memo.Get(_ => "replacement", waiterCancellation.Token));

        await waiterCancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        releaseFirstBuild.Set();
        Assert.Equal("shared", await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(
            "shared",
            memo.Get(_ => "replacement", CancellationToken.None));
    }
}
