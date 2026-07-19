using VbaLanguageServer.Lsp;
using Xunit;
using Xunit.Abstractions;

namespace VbaLanguageServer.Tests;

public sealed class VbaInteractiveWorkSchedulerTests
{
    private readonly ITestOutputHelper output;

    public VbaInteractiveWorkSchedulerTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact]
    public async Task Scheduler_admits_later_read_while_blocked_and_preserves_one_execution_lane()
    {
        var firstStarted = CreateSignal();
        var releaseFirst = CreateSignal();
        var secondStarted = CreateSignal();
        await using var scheduler = new VbaInteractiveWorkScheduler();

        var first = scheduler.AdmitMutation(async cancellationToken =>
        {
            firstStarted.TrySetResult();
            await releaseFirst.Task.WaitAsync(cancellationToken);
        });
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = scheduler.AdmitRequest(
            requestId: null,
            cancellationToken =>
            {
                secondStarted.TrySetResult();
                return Task.CompletedTask;
            });

        Assert.Equal(1, first.InputSequence);
        Assert.Equal(2, second.InputSequence);
        Assert.Equal(first.InputSequence, second.ReadFence);
        Assert.False(secondStarted.Task.IsCompleted);

        releaseFirst.TrySetResult();
        await Task.WhenAll(first.Completion, second.Completion)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(secondStarted.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Scheduler_cancels_matching_request_without_waiting_for_the_serial_lane()
    {
        var requestStarted = CreateSignal();
        var cancellationObserved = CreateSignal();
        var laterMutationStarted = CreateSignal();
        var requestId = new VbaLspRequestId(VbaLspRequestIdKind.Number, "7");
        await using var scheduler = new VbaInteractiveWorkScheduler();

        var request = scheduler.AdmitRequest(
            requestId,
            async cancellationToken =>
            {
                requestStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved.TrySetResult();
                    throw;
                }
            });
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var laterMutation = scheduler.AdmitMutation(cancellationToken =>
        {
            laterMutationStarted.TrySetResult();
            return Task.CompletedTask;
        });

        Assert.True(scheduler.TryCancel(requestId));
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => request.Completion);
        await laterMutation.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(laterMutationStarted.Task.IsCompletedSuccessfully);
        Assert.False(scheduler.TryCancel(requestId));
    }

    [Fact]
    public async Task Scheduler_records_admission_queue_and_execution_time_separately()
    {
        var timingSink = new RecordingTimingSink();
        await using var scheduler = new VbaInteractiveWorkScheduler(timingSink);

        var admission = scheduler.AdmitMutation(
            "textDocument/didChange",
            _ => Task.CompletedTask);
        await admission.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        var admitted = Assert.Single(timingSink.Admitted);
        var completed = Assert.Single(timingSink.Completed);
        Assert.Equal(admission.InputSequence, admitted.InputSequence);
        Assert.Equal("textDocument/didChange", admitted.Method);
        Assert.True(admitted.AdmissionTime >= TimeSpan.Zero);
        Assert.Equal(admitted.InputSequence, completed.InputSequence);
        Assert.True(completed.QueueTime >= TimeSpan.Zero);
        Assert.True(completed.ExecutionTime >= TimeSpan.Zero);
    }

    [Fact]
    public async Task Scheduler_abort_cancels_in_flight_work_and_rejects_later_admission()
    {
        var requestStarted = CreateSignal();
        var cancellationObserved = CreateSignal();
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var request = scheduler.AdmitRequest(
            new VbaLspRequestId(VbaLspRequestIdKind.String, "blocked"),
            async cancellationToken =>
            {
                requestStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved.TrySetResult();
                    throw;
                }
            });
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stop = scheduler.StopAsync(VbaInteractiveStopReason.Abort);

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => request.Completion);
        Assert.Throws<ObjectDisposedException>(
            () => scheduler.AdmitMutation(_ => Task.CompletedTask));
        Assert.Same(stop, scheduler.StopAsync(VbaInteractiveStopReason.Abort));
    }

    [Fact]
    public async Task Cancellation_owner_waits_for_dispatch_before_disposal()
    {
        var callbackStarted = CreateSignal();
        using var releaseCallback = new ManualResetEventSlim();
        var owner = new VbaInteractiveWorkCancellationOwner();
        using var registration = owner.Token.Register(() =>
        {
            callbackStarted.TrySetResult();
            releaseCallback.Wait();
        });

        Assert.True(owner.TryCancel());
        await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposal = owner.DisposeAsync(Task.CompletedTask);

        try
        {
            Assert.False(disposal.IsCompleted);
        }
        finally
        {
            releaseCallback.Set();
        }

        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(owner.TryCancel());
    }

    [Fact]
    public async Task Scheduler_abort_waits_for_cancellation_dispatch_before_stopping()
    {
        var requestStarted = CreateSignal();
        var finishRequest = CreateSignal();
        var callbackStarted = CreateSignal();
        using var releaseCallback = new ManualResetEventSlim();
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var request = scheduler.AdmitRequest(
            requestId: null,
            async cancellationToken =>
            {
                using var registration = cancellationToken.Register(() =>
                {
                    callbackStarted.TrySetResult();
                    releaseCallback.Wait();
                });
                requestStarted.TrySetResult();
                await finishRequest.Task;
            });
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stop = scheduler.StopAsync(VbaInteractiveStopReason.Abort);
        await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        finishRequest.TrySetResult();

        try
        {
            Assert.False(stop.IsCompleted);
        }
        finally
        {
            releaseCallback.Set();
        }

        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        await request.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Explicit_cancellation_receives_an_input_sequence_without_entering_the_lane()
    {
        var requestStarted = CreateSignal();
        var timingSink = new RecordingTimingSink();
        var requestId = new VbaLspRequestId(VbaLspRequestIdKind.Number, "9");
        await using var scheduler = new VbaInteractiveWorkScheduler(timingSink);
        var request = scheduler.AdmitRequest(
            requestId,
            "textDocument/hover",
            async cancellationToken =>
            {
                requestStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(scheduler.TryCancel(requestId));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => request.Completion);

        var cancellation = Assert.Single(
            timingSink.Admitted,
            timing => timing.Kind == VbaInteractiveWorkKind.Control);
        Assert.Equal(request.InputSequence + 1, cancellation.InputSequence);
        Assert.Equal("$/cancelRequest", cancellation.Method);
        Assert.Equal(request.ReadFence, cancellation.ReadFence);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task Release_benchmark_records_scheduler_phases_and_bounds_mutation_admission_p95()
    {
        const int warmupCount = 64;
        const int measuredCount = 512;
        var timingSink = new RecordingTimingSink();
        await using var scheduler = new VbaInteractiveWorkScheduler(timingSink);

        var warmup = Enumerable.Range(0, warmupCount)
            .Select(_ => scheduler.AdmitMutation(
                "textDocument/didChange",
                _ => Task.CompletedTask))
            .ToArray();
        await Task.WhenAll(warmup.Select(admission => admission.Completion));
        timingSink.Admitted.Clear();
        timingSink.Completed.Clear();

        var measured = Enumerable.Range(0, measuredCount)
            .Select(_ => scheduler.AdmitMutation(
                "textDocument/didChange",
                _ => Task.CompletedTask))
            .ToArray();
        await Task.WhenAll(measured.Select(admission => admission.Completion));

        var admissionP95 = Percentile95(
            timingSink.Admitted.Select(timing => timing.AdmissionTime));
        var queueP95 = Percentile95(
            timingSink.Completed.Select(timing => timing.QueueTime));
        var executionP95 = Percentile95(
            timingSink.Completed.Select(timing => timing.ExecutionTime));
        output.WriteLine(
            "scheduler p95: admission={0:F6}ms queue={1:F6}ms execution={2:F6}ms",
            admissionP95.TotalMilliseconds,
            queueP95.TotalMilliseconds,
            executionP95.TotalMilliseconds);

        Assert.Equal(measuredCount, timingSink.Admitted.Count);
        Assert.Equal(measuredCount, timingSink.Completed.Count);
        Assert.True(
            admissionP95 <= TimeSpan.FromMilliseconds(2),
            $"Mutation admission p95 was {admissionP95.TotalMilliseconds:F6} ms.");
    }

    [Fact]
    public async Task Scheduler_complete_drains_owned_work_in_order_and_rejects_later_admission()
    {
        var firstStarted = CreateSignal();
        var releaseFirst = CreateSignal();
        var executionOrder = new List<string>();
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var first = scheduler.AdmitMutation(async cancellationToken =>
        {
            executionOrder.Add("first");
            firstStarted.TrySetResult();
            await releaseFirst.Task.WaitAsync(cancellationToken);
        });
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = scheduler.AdmitMutation(_ =>
        {
            executionOrder.Add("second");
            return Task.CompletedTask;
        });

        var stop = scheduler.StopAsync(VbaInteractiveStopReason.Complete);

        Assert.Throws<ObjectDisposedException>(
            () => scheduler.AdmitMutation(_ => Task.CompletedTask));
        Assert.False(second.Completion.IsCompleted);
        releaseFirst.TrySetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(first.Completion, second.Completion);
        Assert.Equal(["first", "second"], executionOrder);
        Assert.Same(stop, scheduler.StopAsync(VbaInteractiveStopReason.Complete));
    }

    [Fact]
    public async Task Scheduler_escalates_complete_to_abort_and_skips_queued_work()
    {
        var firstStarted = CreateSignal();
        var cancellationObserved = CreateSignal();
        var queuedMutationInvoked = false;
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var first = scheduler.AdmitRequest(
            requestId: null,
            async cancellationToken =>
            {
                firstStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved.TrySetResult();
                    throw;
                }
            });
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queuedMutation = scheduler.AdmitMutation(_ =>
        {
            queuedMutationInvoked = true;
            return Task.CompletedTask;
        });

        var complete = scheduler.StopAsync(VbaInteractiveStopReason.Complete);
        var abort = scheduler.StopAsync(VbaInteractiveStopReason.Abort);

        Assert.Same(complete, abort);
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await abort.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => first.Completion);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => queuedMutation.Completion);
        Assert.False(queuedMutationInvoked);
    }

    [Fact]
    public async Task Request_id_can_be_reused_after_its_previous_owner_reaches_terminal_completion()
    {
        var requestId = new VbaLspRequestId(VbaLspRequestIdKind.String, "reused");
        var secondStarted = CreateSignal();
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var first = scheduler.AdmitRequest(
            requestId,
            _ => Task.CompletedTask);
        await first.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        var second = scheduler.AdmitRequest(
            requestId,
            async cancellationToken =>
            {
                secondStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(scheduler.TryCancel(requestId));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => second.Completion);
        Assert.False(scheduler.TryCancel(requestId));
    }

    [Fact]
    public async Task Request_id_can_be_reused_after_terminal_ownership_is_released_before_write_completion()
    {
        var requestId = new VbaLspRequestId(VbaLspRequestIdKind.Number, "42");
        var terminalOwnershipReleased = CreateSignal();
        var finishFirstWrite = CreateSignal();
        var secondStarted = CreateSignal();
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var first = scheduler.AdmitRequest(
            requestId,
            "textDocument/hover",
            async (cancellationToken, releaseCancellationOwnership) =>
            {
                releaseCancellationOwnership();
                terminalOwnershipReleased.TrySetResult();
                await finishFirstWrite.Task.WaitAsync(cancellationToken);
            });
        await terminalOwnershipReleased.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = scheduler.AdmitRequest(
            requestId,
            "textDocument/hover",
            _ =>
            {
                secondStarted.TrySetResult();
                return Task.CompletedTask;
            });

        finishFirstWrite.TrySetResult();
        await Task.WhenAll(first.Completion, second.Completion)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(secondStarted.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Cancellation_after_id_reuse_targets_only_the_new_generation()
    {
        var requestId = new VbaLspRequestId(VbaLspRequestIdKind.Number, "42");
        var terminalOwnershipReleased = CreateSignal();
        var finishFirstWrite = CreateSignal();
        var previousGenerationCancelled = false;
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var first = scheduler.AdmitRequest(
            requestId,
            "textDocument/hover",
            async (cancellationToken, releaseCancellationOwnership) =>
            {
                using var registration = cancellationToken.Register(
                    () => previousGenerationCancelled = true);
                releaseCancellationOwnership();
                terminalOwnershipReleased.TrySetResult();
                await finishFirstWrite.Task.WaitAsync(cancellationToken);
            });
        await terminalOwnershipReleased.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = scheduler.AdmitRequest(
            requestId,
            "textDocument/hover",
            cancellationToken =>
                Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));

        Assert.True(scheduler.TryCancel(requestId));
        finishFirstWrite.TrySetResult();

        await first.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => second.Completion);
        Assert.False(previousGenerationCancelled);
    }

    [Fact]
    public async Task Scheduler_observes_and_reports_work_failures()
    {
        var failures = new List<VbaInteractiveWorkFailure>();
        await using var scheduler = new VbaInteractiveWorkScheduler(
            failureSink: failures.Add);

        var admission = scheduler.AdmitMutation(
            "textDocument/didChange",
            _ => throw new InvalidOperationException("mutation failed"));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => admission.Completion);
        var reported = Assert.Single(failures);
        Assert.Same(failure, reported.Exception);
        Assert.Equal(admission.InputSequence, reported.InputSequence);
        Assert.Equal("textDocument/didChange", reported.Method);
    }

    [Fact]
    public async Task Scheduler_failure_aborts_and_skips_queued_work()
    {
        var firstStarted = CreateSignal();
        var releaseFirst = CreateSignal();
        var queuedMutationInvoked = false;
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var first = scheduler.AdmitMutation(
            "textDocument/didChange",
            async _ =>
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task;
                throw new InvalidOperationException("mutation failed");
            });
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queuedMutation = scheduler.AdmitMutation(_ =>
        {
            queuedMutationInvoked = true;
            return Task.CompletedTask;
        });

        releaseFirst.TrySetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => first.Completion);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => queuedMutation.Completion);
        Assert.False(queuedMutationInvoked);
    }

    [Fact]
    public async Task Scheduler_reports_failure_before_waiting_for_abort_dispatch()
    {
        var callbackStarted = CreateSignal();
        var failureReported = CreateSignal();
        using var releaseCallback = new ManualResetEventSlim();
        await using var scheduler = new VbaInteractiveWorkScheduler(
            failureSink: _ => failureReported.TrySetResult());
        var failed = scheduler.AdmitMutation(cancellationToken =>
        {
            _ = cancellationToken.Register(() =>
            {
                callbackStarted.TrySetResult();
                releaseCallback.Wait();
            });
            throw new InvalidOperationException("mutation failed");
        });
        await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            Assert.True(failureReported.Task.IsCompletedSuccessfully);
            Assert.False(scheduler.IsAccepting);
        }
        finally
        {
            releaseCallback.Set();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => failed.Completion);
    }

    [Fact]
    public async Task Scheduler_abort_does_not_invoke_work_that_never_started()
    {
        var firstStarted = CreateSignal();
        var queuedMutationInvoked = false;
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var first = scheduler.AdmitRequest(
            requestId: null,
            async cancellationToken =>
            {
                firstStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queuedMutation = scheduler.AdmitMutation(_ =>
        {
            queuedMutationInvoked = true;
            return Task.CompletedTask;
        });

        await scheduler.StopAsync(VbaInteractiveStopReason.Abort)
            .WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => first.Completion);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => queuedMutation.Completion);
        Assert.False(queuedMutationInvoked);
    }

    [Fact]
    public async Task Slow_cancellation_callback_does_not_hold_the_admission_lock()
    {
        var requestStarted = CreateSignal();
        var callbackStarted = CreateSignal();
        using var releaseCallback = new ManualResetEventSlim();
        var requestId = new VbaLspRequestId(VbaLspRequestIdKind.String, "slow-callback");
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var request = scheduler.AdmitRequest(
            requestId,
            async cancellationToken =>
            {
                using var registration = cancellationToken.Register(() =>
                {
                    callbackStarted.TrySetResult();
                    releaseCallback.Wait();
                });
                requestStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cancel = Task.Run(() => scheduler.TryCancel(requestId));
        await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            var laterAdmission = await Task.Run(
                    () => scheduler.AdmitMutation(_ => Task.CompletedTask))
                .WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(laterAdmission.InputSequence > request.InputSequence);
        }
        finally
        {
            releaseCallback.Set();
        }

        Assert.True(await cancel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => request.Completion);
    }

    [Fact]
    public async Task Non_mutating_barrier_preserves_order_without_advancing_the_read_fence()
    {
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var mutation = scheduler.AdmitMutation(
            "textDocument/didOpen",
            _ => Task.CompletedTask);
        var barrier = scheduler.AdmitBarrier(
            "initialized",
            _ => Task.CompletedTask);
        var read = scheduler.AdmitRequest(
            requestId: null,
            "textDocument/hover",
            _ => Task.CompletedTask);

        await Task.WhenAll(
            mutation.Completion,
            barrier.Completion,
            read.Completion);
        Assert.Equal(mutation.InputSequence, mutation.ReadFence);
        Assert.Equal(mutation.InputSequence, barrier.ReadFence);
        Assert.Equal(mutation.InputSequence, read.ReadFence);
        Assert.True(mutation.InputSequence < barrier.InputSequence);
        Assert.True(barrier.InputSequence < read.InputSequence);
    }

    [Fact]
    public async Task Coalescible_mutations_for_the_same_key_execute_only_the_latest_before_a_read_fence()
    {
        var executed = new List<string>();
        await using var scheduler = new VbaInteractiveWorkScheduler();

        var versionTwo = scheduler.AdmitCoalescibleMutation(
            "textDocument/didChange",
            "file:///C:/work/Module.bas",
            _ =>
            {
                executed.Add("v2");
                return Task.CompletedTask;
            });
        var versionThree = scheduler.AdmitCoalescibleMutation(
            "textDocument/didChange",
            "file:///C:/work/Module.bas",
            _ =>
            {
                executed.Add("v3");
                return Task.CompletedTask;
            });

        await Task.WhenAll(versionTwo.Completion, versionThree.Completion)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(versionTwo.InputSequence + 1, versionThree.InputSequence);
        Assert.Equal(versionThree.InputSequence, versionThree.ReadFence);
        Assert.Equal(["v3"], executed);
    }

    [Fact]
    public async Task Coalescible_mutations_do_not_cross_a_read_fence()
    {
        var executed = new List<string>();
        await using var scheduler = new VbaInteractiveWorkScheduler();

        var versionTwo = scheduler.AdmitCoalescibleMutation(
            "textDocument/didChange",
            "file:///C:/work/Module.bas",
            _ =>
            {
                executed.Add("v2");
                return Task.CompletedTask;
            });
        var read = scheduler.AdmitRequest(
            requestId: null,
            "textDocument/hover",
            _ =>
            {
                executed.Add("read");
                return Task.CompletedTask;
            });
        var versionThree = scheduler.AdmitCoalescibleMutation(
            "textDocument/didChange",
            "file:///C:/work/Module.bas",
            _ =>
            {
                executed.Add("v3");
                return Task.CompletedTask;
            });

        await Task.WhenAll(versionTwo.Completion, read.Completion, versionThree.Completion)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["v2", "read", "v3"], executed);
    }

    [Fact]
    public async Task Coalescible_mutations_do_not_cross_document_keys()
    {
        var executed = new List<string>();
        await using var scheduler = new VbaInteractiveWorkScheduler();

        var moduleA2 = scheduler.AdmitCoalescibleMutation(
            "textDocument/didChange",
            "file:///C:/work/ModuleA.bas",
            _ =>
            {
                executed.Add("a2");
                return Task.CompletedTask;
            });
        var moduleB2 = scheduler.AdmitCoalescibleMutation(
            "textDocument/didChange",
            "file:///C:/work/ModuleB.bas",
            _ =>
            {
                executed.Add("b2");
                return Task.CompletedTask;
            });
        var moduleA3 = scheduler.AdmitCoalescibleMutation(
            "textDocument/didChange",
            "file:///C:/work/ModuleA.bas",
            _ =>
            {
                executed.Add("a3");
                return Task.CompletedTask;
            });

        await Task.WhenAll(moduleA2.Completion, moduleB2.Completion, moduleA3.Completion)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["a2", "b2", "a3"], executed);
    }

    [Fact]
    public async Task Coalescing_can_be_disabled_without_changing_admission_order()
    {
        var executed = new List<string>();
        await using var scheduler = new VbaInteractiveWorkScheduler(
            options: new VbaInteractiveWorkSchedulerOptions(
                CoalesceSupersededMutations: false));

        var versionTwo = scheduler.AdmitCoalescibleMutation(
            "textDocument/didChange",
            "file:///C:/work/Module.bas",
            _ =>
            {
                executed.Add("v2");
                return Task.CompletedTask;
            });
        var versionThree = scheduler.AdmitCoalescibleMutation(
            "textDocument/didChange",
            "file:///C:/work/Module.bas",
            _ =>
            {
                executed.Add("v3");
                return Task.CompletedTask;
            });

        await Task.WhenAll(versionTwo.Completion, versionThree.Completion)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["v2", "v3"], executed);
        Assert.Equal(versionTwo.InputSequence + 1, versionThree.InputSequence);
    }

    [Fact]
    public async Task Randomized_mutation_and_read_sequences_match_the_non_coalescing_reference()
    {
        for (var seed = 0; seed < 16; seed++)
        {
            var operations = CreateRandomizedOperations(seed);

            var reference = await RunObservableScenarioAsync(
                operations,
                coalesceSupersededMutations: false);
            var coalesced = await RunObservableScenarioAsync(
                operations,
                coalesceSupersededMutations: true);

            Assert.Equal(reference.Reads, coalesced.Reads);
            Assert.Equal(reference.FinalState, coalesced.FinalState);
            Assert.Equal(operations.Count, coalesced.CompletedCount);
            Assert.True(coalesced.ExecutedMutationCount <= reference.ExecutedMutationCount);
        }
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task Release_burst_coalescing_reports_typing_latency_and_superseded_work()
    {
        const int acceptedChangeCount = 256;
        var release = CreateSignal();
        var timingSink = new RecordingTimingSink();
        var executedAnalysisCount = 0;
        await using var scheduler = new VbaInteractiveWorkScheduler(timingSink);
        var blocker = scheduler.AdmitBarrier(
            "test/block",
            cancellationToken => release.Task.WaitAsync(cancellationToken));
        var beforeAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true);

        var admissions = Enumerable.Range(0, acceptedChangeCount)
            .Select(index => scheduler.AdmitCoalescibleMutation(
                "textDocument/didChange",
                "file:///C:/work/Burst.bas",
                _ =>
                {
                    executedAnalysisCount++;
                    return Task.CompletedTask;
                }))
            .ToArray();

        release.TrySetResult();
        await Task.WhenAll(admissions.Select(admission => admission.Completion).Append(blocker.Completion))
            .WaitAsync(TimeSpan.FromSeconds(5));
        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - beforeAllocatedBytes;

        var didChangeCompletions = timingSink.Completed
            .Where(timing => timing.Method == "textDocument/didChange")
            .ToArray();
        var supersededCount = acceptedChangeCount - executedAnalysisCount;
        var queueP95 = Percentile95(didChangeCompletions.Select(timing => timing.QueueTime));
        var commitLatencyP95 = Percentile95(
            didChangeCompletions
                .Where(timing => timing.ExecutionTime > TimeSpan.Zero)
                .Select(timing => timing.ExecutionTime));
        output.WriteLine(
            "burst coalescing: acceptedChanges={0} analysisBuilds={1} superseded={2} queueDelayP95={3:F6}ms commitLatencyP95={4:F6}ms allocatedBytes={5}",
            acceptedChangeCount,
            executedAnalysisCount,
            supersededCount,
            queueP95.TotalMilliseconds,
            commitLatencyP95.TotalMilliseconds,
            allocatedBytes);

        Assert.Equal(acceptedChangeCount, didChangeCompletions.Length);
        Assert.Equal(1, executedAnalysisCount);
        Assert.Equal(acceptedChangeCount - 1, supersededCount);
        Assert.True(allocatedBytes >= 0);
    }

    private static TaskCompletionSource CreateSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static IReadOnlyList<ScenarioOperation> CreateRandomizedOperations(int seed)
    {
        var random = new Random(seed);
        var operations = new List<ScenarioOperation>();
        var value = 0;
        for (var index = 0; index < 48; index++)
        {
            if (index % 7 == 6)
            {
                operations.Add(new ScenarioOperation(
                    ScenarioOperationKind.Read,
                    Key: "read",
                    Value: 0));
                continue;
            }

            var key = random.Next(2) == 0 ? "A" : "B";
            operations.Add(new ScenarioOperation(
                ScenarioOperationKind.Mutation,
                key,
                ++value));
        }

        operations.Add(new ScenarioOperation(
            ScenarioOperationKind.Read,
            Key: "read",
            Value: 0));
        return operations;
    }

    private static async Task<ScenarioResult> RunObservableScenarioAsync(
        IReadOnlyList<ScenarioOperation> operations,
        bool coalesceSupersededMutations)
    {
        var release = CreateSignal();
        var state = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["A"] = 0,
            ["B"] = 0
        };
        var reads = new List<string>();
        var executedMutationCount = 0;
        var admissions = new List<VbaInteractiveWorkAdmission>();
        await using var scheduler = new VbaInteractiveWorkScheduler(
            options: new VbaInteractiveWorkSchedulerOptions(
                coalesceSupersededMutations));
        var blocker = scheduler.AdmitBarrier(
            "test/block",
            cancellationToken => release.Task.WaitAsync(cancellationToken));

        foreach (var operation in operations)
        {
            if (operation.Kind == ScenarioOperationKind.Mutation)
            {
                var key = operation.Key;
                var value = operation.Value;
                admissions.Add(scheduler.AdmitCoalescibleMutation(
                    "textDocument/didChange",
                    key,
                    _ =>
                    {
                        executedMutationCount++;
                        state[key] = value;
                        return Task.CompletedTask;
                    }));
            }
            else
            {
                admissions.Add(scheduler.AdmitRequest(
                    requestId: null,
                    "textDocument/hover",
                    _ =>
                    {
                        reads.Add($"A={state["A"]};B={state["B"]}");
                        return Task.CompletedTask;
                    }));
            }
        }

        release.TrySetResult();
        await Task.WhenAll(admissions.Select(admission => admission.Completion).Append(blocker.Completion))
            .WaitAsync(TimeSpan.FromSeconds(5));

        return new ScenarioResult(
            reads,
            $"A={state["A"]};B={state["B"]}",
            executedMutationCount,
            admissions.Count);
    }

    private static TimeSpan Percentile95(IEnumerable<TimeSpan> values)
    {
        var ordered = values.Order().ToArray();
        var index = (int)Math.Ceiling(ordered.Length * 0.95) - 1;
        return ordered[Math.Max(0, index)];
    }

    private sealed class RecordingTimingSink : IVbaInteractiveWorkTimingSink
    {
        public List<VbaInteractiveWorkAdmissionTiming> Admitted { get; } = [];

        public List<VbaInteractiveWorkCompletionTiming> Completed { get; } = [];

        public void RecordAdmission(VbaInteractiveWorkAdmissionTiming timing)
            => Admitted.Add(timing);

        public void RecordCompletion(VbaInteractiveWorkCompletionTiming timing)
            => Completed.Add(timing);
    }

    private enum ScenarioOperationKind
    {
        Mutation,
        Read
    }

    private sealed record ScenarioOperation(
        ScenarioOperationKind Kind,
        string Key,
        int Value);

    private sealed record ScenarioResult(
        IReadOnlyList<string> Reads,
        string FinalState,
        int ExecutedMutationCount,
        int CompletedCount);
}
