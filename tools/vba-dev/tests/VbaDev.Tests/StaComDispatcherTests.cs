using VbaDev.Infrastructure.Debugging;
using Xunit;

namespace VbaDev.Tests;

public sealed class StaComDispatcherTests
{
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
