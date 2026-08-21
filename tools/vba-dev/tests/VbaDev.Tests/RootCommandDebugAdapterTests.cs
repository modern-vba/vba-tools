using VbaDev.Cli;
using VbaDev.Composition;
using Xunit;

namespace VbaDev.Tests;

public sealed class RootCommandDebugAdapterTests
{
    [Fact]
    public async Task AdvertisedStdioEntryPointRunsThroughTheRootCommandGraph()
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var invocationCount = 0;
        var observedCancellationToken = CancellationToken.None;
        var application = VbaDevCommandLine.Create(
            ToolingCompositionRoot.CreateApplicationComposition(temp.Path),
            debugAdapterRunner: token =>
            {
                invocationCount++;
                observedCancellationToken = token;
                return Task.FromResult(0);
            });

        var result = await application.RunAsync(
            ["debug-adapter", "--stdio"],
            cancellation.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Empty(result.StandardError);
        Assert.Equal(1, invocationCount);
        Assert.True(observedCancellationToken.CanBeCanceled);
    }

    [Theory]
    [MemberData(nameof(InvalidStdioArguments))]
    public async Task StdioEntryPointRejectsMissingValuedOrTrailingTransportArguments(string[] args)
    {
        using var temp = TempDirectory.Create();
        var invocationCount = 0;
        var application = VbaDevCommandLine.Create(
            ToolingCompositionRoot.CreateApplicationComposition(temp.Path),
            debugAdapterRunner: _ =>
            {
                invocationCount++;
                return Task.FromResult(0);
            });

        var result = await application.RunAsync(args);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(0, invocationCount);
    }

    [Fact]
    public async Task InvocationCancellationReachesTheAsynchronousCommandAction()
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var application = VbaDevCommandLine.Create(
            ToolingCompositionRoot.CreateApplicationComposition(temp.Path),
            debugAdapterRunner: async token =>
            {
                started.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return 0;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    cancellationObserved.SetResult();
                    throw;
                }
            });

        var invocation = application.RunAsync(
            ["debug-adapter", "--stdio"],
            cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await invocation;
        Assert.Equal(130, result.ExitCode);
    }

    public static TheoryData<string[]> InvalidStdioArguments()
        =>
        [
            ["debug-adapter"],
            ["debug-adapter", "--stdio=false"],
            ["debug-adapter", "--stdio=true"],
            ["debug-adapter", "--stdio", "extra"]
        ];
}
