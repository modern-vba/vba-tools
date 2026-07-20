using System.Collections.Concurrent;
using VbaLanguageServer.Lsp;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaLatestOnlyBackgroundMailboxTests
{
    [Fact]
    public async Task Execution_start_takes_the_latest_pending_work()
    {
        await using var scheduler = new VbaInteractiveWorkScheduler(
            options: new VbaInteractiveWorkSchedulerOptions(
                CoalesceSupersededMutations: true,
                EnableConcurrentReads: true,
                MaxConcurrentReads: 1,
                MaxConcurrentBulkReads: 1));
        var blockerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocker = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = scheduler.AdmitRequest(
            requestId: null,
            "textDocument/hover",
            _ => new object(),
            async (_, cancellationToken) =>
            {
                blockerStarted.TrySetResult();
                await releaseBlocker.Task.WaitAsync(cancellationToken);
            });
        await blockerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var executed = new ConcurrentQueue<string>();
        var terminalCount = 0;
        var mailbox = new VbaLatestOnlyBackgroundMailbox(
            scheduler,
            VbaInteractiveBackgroundWorkType.DiagnosticsPublication);

        mailbox.Post(
            "file:///C:/work/Worker.bas",
            _ =>
            {
                executed.Enqueue("first");
                return Task.CompletedTask;
            },
            () => Interlocked.Increment(ref terminalCount));
        mailbox.Post(
            "file:///C:/work/Worker.bas",
            _ =>
            {
                executed.Enqueue("latest");
                return Task.CompletedTask;
            },
            () => Interlocked.Increment(ref terminalCount));

        releaseBlocker.TrySetResult();
        await blocker.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await mailbox.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["latest"], executed);
        Assert.Equal(2, terminalCount);
        mailbox.Stop();
        await scheduler.StopAsync(VbaInteractiveStopReason.Complete);
    }

    [Fact]
    public async Task Posting_while_active_restarts_the_authority_after_completion()
    {
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var executed = new ConcurrentQueue<string>();
        var terminalCount = 0;
        var mailbox = new VbaLatestOnlyBackgroundMailbox(
            scheduler,
            VbaInteractiveBackgroundWorkType.DiagnosticsPublication);

        mailbox.Post(
            "worker",
            async cancellationToken =>
            {
                executed.Enqueue("first");
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
            },
            () => Interlocked.Increment(ref terminalCount));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        mailbox.Post(
            "worker",
            _ =>
            {
                executed.Enqueue("second");
                return Task.CompletedTask;
            },
            () => Interlocked.Increment(ref terminalCount));

        releaseFirst.TrySetResult();
        await mailbox.WaitForIdleAsync("worker")
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["first", "second"], executed);
        Assert.Equal(2, terminalCount);
        mailbox.Stop();
        await scheduler.StopAsync(VbaInteractiveStopReason.Complete);
    }

    [Fact]
    public async Task Capacity_retry_preserves_ready_authority_order()
    {
        await using var scheduler = new VbaInteractiveWorkScheduler(
            options: new VbaInteractiveWorkSchedulerOptions(
                CoalesceSupersededMutations: true,
                MaxOwnedWork: 1));
        var blockerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocker = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = scheduler.AdmitMutation(async cancellationToken =>
        {
            blockerStarted.TrySetResult();
            await releaseBlocker.Task.WaitAsync(cancellationToken);
        });
        await blockerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var executed = new ConcurrentQueue<string>();
        var mailbox = new VbaLatestOnlyBackgroundMailbox(
            scheduler,
            VbaInteractiveBackgroundWorkType.ReferenceCatalogRefresh);

        mailbox.Post(
            "first",
            _ =>
            {
                executed.Enqueue("first");
                return Task.CompletedTask;
            });
        mailbox.Post(
            "second",
            _ =>
            {
                executed.Enqueue("second");
                return Task.CompletedTask;
            });

        releaseBlocker.TrySetResult();
        await blocker.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await mailbox.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["first", "second"], executed);
        mailbox.Stop();
        await scheduler.StopAsync(VbaInteractiveStopReason.Complete);
    }

    [Fact]
    public async Task Discard_and_stop_terminalize_each_pending_item_once()
    {
        await using var scheduler = new VbaInteractiveWorkScheduler(
            options: new VbaInteractiveWorkSchedulerOptions(
                CoalesceSupersededMutations: true,
                MaxOwnedWork: 1));
        var blockerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocker = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = scheduler.AdmitMutation(async cancellationToken =>
        {
            blockerStarted.TrySetResult();
            await releaseBlocker.Task.WaitAsync(cancellationToken);
        });
        await blockerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var executionCount = 0;
        var discardedTerminalCount = 0;
        var stoppedTerminalCount = 0;
        var rejectedTerminalCount = 0;
        var mailbox = new VbaLatestOnlyBackgroundMailbox(
            scheduler,
            VbaInteractiveBackgroundWorkType.DiagnosticsPublication);

        mailbox.Post(
            "discarded",
            _ =>
            {
                Interlocked.Increment(ref executionCount);
                return Task.CompletedTask;
            },
            () => Interlocked.Increment(ref discardedTerminalCount));
        mailbox.Discard("discarded");
        mailbox.Post(
            "stopped",
            _ =>
            {
                Interlocked.Increment(ref executionCount);
                return Task.CompletedTask;
            },
            () => Interlocked.Increment(ref stoppedTerminalCount));
        mailbox.Stop();
        mailbox.Post(
            "rejected",
            _ =>
            {
                Interlocked.Increment(ref executionCount);
                return Task.CompletedTask;
            },
            () => Interlocked.Increment(ref rejectedTerminalCount));

        await mailbox.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        releaseBlocker.TrySetResult();
        await blocker.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, executionCount);
        Assert.Equal(1, discardedTerminalCount);
        Assert.Equal(1, stoppedTerminalCount);
        Assert.Equal(1, rejectedTerminalCount);
        await scheduler.StopAsync(VbaInteractiveStopReason.Complete);
    }
}
