using VbaDev.Infrastructure.Debugging;
using Xunit;

namespace VbaDev.Tests;

public sealed class StaComDispatcherTests
{
    [Fact]
    public async Task EveryDisposalRequestWaitsForRunningAndQueuedWork()
    {
        using var releaseWork = new ManualResetEventSlim();
        using var releaseQueuedWork = new ManualResetEventSlim();
        var workStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedWorkStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var dispatcher = new StaComDispatcherFactory().Create();
        var runningWork = dispatcher.InvokeAsync(() =>
        {
            workStarted.TrySetResult();
            if (!releaseWork.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The test did not release the dispatcher operation.");
            }

            return 17;
        }, CancellationToken.None);
        var queuedWork = dispatcher.InvokeAsync(() =>
        {
            queuedWorkStarted.TrySetResult();
            if (!releaseQueuedWork.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The test did not release the queued dispatcher operation.");
            }

            return 42;
        }, CancellationToken.None);
        Task firstDisposal = Task.CompletedTask;
        Task secondDisposal = Task.CompletedTask;

        try
        {
            await workStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            firstDisposal = dispatcher.DisposeAsync().AsTask();
            secondDisposal = dispatcher.DisposeAsync().AsTask();

            Assert.False(firstDisposal.IsCompleted);
            Assert.False(secondDisposal.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                dispatcher.InvokeAsync(() => 99, CancellationToken.None));

            releaseWork.Set();
            await queuedWorkStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(firstDisposal.IsCompleted);
            Assert.False(secondDisposal.IsCompleted);
        }
        finally
        {
            releaseWork.Set();
            releaseQueuedWork.Set();
            await Task.WhenAll(runningWork, queuedWork, firstDisposal, secondDisposal)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(17, await runningWork);
        Assert.Equal(42, await queuedWork);
        Assert.True(firstDisposal.IsCompletedSuccessfully);
        Assert.True(secondDisposal.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task InvokeAsyncRunsEveryOperationOnOneDedicatedStaThread()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var callerThreadId = Environment.CurrentManagedThreadId;
        await using var dispatcher = new StaComDispatcherFactory().Create();

        var first = await dispatcher.InvokeAsync(
            () => (Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState()),
            CancellationToken.None);
        var second = await dispatcher.InvokeAsync(
            () => (Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState()),
            CancellationToken.None);

        Assert.NotEqual(callerThreadId, first.CurrentManagedThreadId);
        Assert.Equal(first.CurrentManagedThreadId, second.CurrentManagedThreadId);
        Assert.Equal(ApartmentState.STA, first.Item2);
        Assert.Equal(ApartmentState.STA, second.Item2);
    }
}
