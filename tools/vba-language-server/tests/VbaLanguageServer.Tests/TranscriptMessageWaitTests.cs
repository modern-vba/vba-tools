using System.Text.Json;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class TranscriptMessageWaitTests
{
    [Fact]
    public async Task A_watchdog_failure_records_cursor_and_transcript_receipt_timing()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        var observed = await Assert.ThrowsAsync<TimeoutException>(
            () => process.WaitForMessageAsync(
                0, _ => false, TimeSpan.FromMilliseconds(25)));

        Assert.Contains("cursor=raw:0", observed.Message, StringComparison.Ordinal);
        Assert.Contains("initialCheckpoint=", observed.Message, StringComparison.Ordinal);
        Assert.Contains("watchdogMs=", observed.Message, StringComparison.Ordinal);
        Assert.Contains("elapsedMs=", observed.Message, StringComparison.Ordinal);
        Assert.Contains("[transcriptRecordedAfterWaitStartMs=", observed.Message, StringComparison.Ordinal);
        Assert.Contains("\"serverInfo\"", observed.Message, StringComparison.Ordinal);
        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task A_final_snapshot_fault_is_not_hidden_by_the_watchdog()
    {
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new InvalidOperationException("transcript session failed");
        var reads = 0;
        TranscriptMessageWait.Snapshot Read()
        {
            if (++reads > 1) throw expected;
            return new(null, changed.Task);
        }

        var observed = await Record.ExceptionAsync(
            () => TranscriptMessageWait.WaitAsync(
                Read, TimeSpan.FromMilliseconds(25), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Same(expected, observed);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task A_predicate_timeout_is_not_reclassified_as_a_watchdog_failure()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();
        var expected = new TimeoutException("independent predicate failure");

        var observed = await Record.ExceptionAsync(
            () => process.WaitForMessageAsync(0, _ => throw expected));

        Assert.Same(expected, observed);
        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_replaced_by_a_recorded_message()
    {
        using var document = JsonDocument.Parse("{\"id\":1}");
        using var cancellation = new CancellationTokenSource();
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        JsonElement? recorded = null;
        var reads = 0;
        TranscriptMessageWait.Snapshot Read()
        {
            reads++;
            var observed = recorded;
            recorded = document.RootElement;
            cancellation.Cancel();
            return new(observed, changed.Task);
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => TranscriptMessageWait.WaitAsync(
                Read, TimeSpan.FromSeconds(5), cancellation.Token));

        Assert.Equal(1, reads);
        Assert.True(cancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task A_missing_message_still_times_out_after_one_final_snapshot()
    {
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        TranscriptMessageWait.Snapshot Read()
        {
            reads++;
            return new(null, changed.Task);
        }

        var observed = await Assert.ThrowsAnyAsync<TimeoutException>(
            () => TranscriptMessageWait.WaitAsync(
                Read, TimeSpan.FromMilliseconds(25), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.IsAssignableFrom<OperationCanceledException>(observed.InnerException);
        Assert.Equal(2, reads);
        Assert.False(changed.Task.IsCompleted);
    }

    [Fact]
    public async Task A_recorded_message_survives_a_delayed_change_notification_at_the_watchdog()
    {
        using var document = JsonDocument.Parse("{\"method\":\"textDocument/publishDiagnostics\"}");
        var expected = document.RootElement;
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        JsonElement? recorded = null;
        TranscriptMessageWait.Snapshot Read()
        {
            var observed = recorded;
            // Model the stdout pump committing a frame just after the reader's
            // snapshot, while the separate change notification remains delayed.
            recorded = expected;
            return new(observed, changed.Task);
        }

        try
        {
            var actual = await TranscriptMessageWait.WaitAsync(
                Read, TimeSpan.FromMilliseconds(25), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(expected.GetRawText(), actual.GetRawText());
            Assert.False(changed.Task.IsCompleted);
        }
        finally
        {
            changed.TrySetResult();
        }
    }
}
