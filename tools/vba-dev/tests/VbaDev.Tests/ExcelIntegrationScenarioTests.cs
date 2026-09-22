using Xunit;

namespace VbaDev.Tests;

public sealed class ExcelIntegrationScenarioTests
{
    [Fact]
    public async Task BodyFailureWaitsForBaselineBeforePreservingOriginalException()
    {
        var failure = new InvalidOperationException("The scenario failed.");
        var baselineRestored = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var quitRequested = false;
        var scenario = ExcelIntegrationScenario.RunAsync(
            () => ThrowBodyFailure(failure),
            () => quitRequested = true,
            () => baselineRestored.Task);

        try
        {
            Assert.True(quitRequested);
            Assert.False(scenario.IsCompleted);
        }
        finally
        {
            baselineRestored.TrySetResult();
        }

        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() => scenario);
        Assert.Same(failure, observed);
        Assert.Contains(nameof(ThrowBodyFailure), observed.StackTrace, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuitFailureStillWaitsForBaselineBeforeReportingFailure()
    {
        var failure = new InvalidOperationException("Excel did not acknowledge Quit.");
        var baselineRestored = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var scenario = ExcelIntegrationScenario.RunAsync(
            () => Task.CompletedTask,
            () => throw failure,
            () => baselineRestored.Task);

        try
        {
            Assert.False(scenario.IsCompleted);
        }
        finally
        {
            baselineRestored.TrySetResult();
        }

        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() => scenario);
        Assert.Same(failure, observed);
    }

    [Fact]
    public async Task BodyAndCleanupFailuresRemainAvailableInOriginalOrder()
    {
        var bodyFailure = new InvalidOperationException("The scenario failed.");
        var quitFailure = new InvalidOperationException("Excel did not acknowledge Quit.");
        var baselineFailure = new InvalidOperationException("The Excel process set changed.");

        var observed = await Assert.ThrowsAsync<AggregateException>(() =>
            ExcelIntegrationScenario.RunAsync(
                () => throw bodyFailure,
                () => throw quitFailure,
                () => throw baselineFailure));

        Assert.Collection(
            observed.InnerExceptions,
            failure => Assert.Same(bodyFailure, failure),
            failure => Assert.Same(quitFailure, failure),
            failure => Assert.Same(baselineFailure, failure));
    }

    [Fact]
    public async Task SuccessfulBodyWaitsForBaselineBeforeCompleting()
    {
        var baselineRestored = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var quitRequested = false;
        var scenario = ExcelIntegrationScenario.RunAsync(
            () => Task.CompletedTask,
            () => quitRequested = true,
            () => baselineRestored.Task);

        try
        {
            Assert.True(quitRequested);
            Assert.False(scenario.IsCompleted);
        }
        finally
        {
            baselineRestored.TrySetResult();
        }

        await scenario;
    }

    [Fact]
    public async Task BaselineMismatchFailsAnOtherwiseSuccessfulScenario()
    {
        var failure = new InvalidOperationException("The Excel process set changed.");

        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExcelIntegrationScenario.RunAsync(
                () => Task.CompletedTask,
                () => { },
                () => Task.FromException(failure)));

        Assert.Same(failure, observed);
    }

    private static Task ThrowBodyFailure(Exception failure) => throw failure;
}
