using System.Text.Json;
using VbaDev.Cli;
using Xunit;

namespace VbaDev.Tests;

public sealed class DebugPreparationInteractionTests
{
    [Fact]
    public async Task ManagedPreparationWaitsForItsOwnContinuationBeforeReplacement()
    {
        using var error = new StringWriter();
        using var input = new VbaDevWorkbookConfirmationInput(Stream.Null, error, managed: true);
        var generationId = new string('a', 32);
        var workbookPath = Path.GetFullPath("Selected.xlsm");
        const int processId = 1234;
        const long startTicks = 638900000000000000;

        var continuation = VbaDevWorkbookConfirmationInput.ContinueDebugPreparationAsync(
            generationId, workbookPath, processId, startTicks, CancellationToken.None);

        Assert.False(continuation.IsCompleted);
        using var payload = JsonDocument.Parse(error.ToString());
        var ready = payload.RootElement;
        Assert.Equal("debugPreparationReady", ready.GetProperty("type").GetString());
        Assert.Equal("1.0", ready.GetProperty("schemaVersion").GetString());
        Assert.Equal(generationId, ready.GetProperty("generationId").GetString());
        Assert.Equal(workbookPath, ready.GetProperty("workbookPath").GetString());
        Assert.Equal(processId, ready.GetProperty("excelProcessId").GetInt32());
        Assert.Equal(startTicks, ready.GetProperty("excelProcessStartUtcTicks").GetInt64());
        var requestId = ready.GetProperty("requestId").GetString();
        Assert.Matches("^[0-9a-f]{32}$", requestId!);

        input.ObserveFrame($"prepare:{new string('0', 32)}:ready");
        input.ObserveFrame($"confirm:{requestId}:yes");
        Assert.False(continuation.IsCompleted);
        input.ObserveFrame($"prepare:{requestId}:ready");

        Assert.True(await continuation.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task CompletedContinuationCannotAuthorizeTheNextPreparation()
    {
        using var error = new StringWriter();
        using var input = new VbaDevWorkbookConfirmationInput(Stream.Null, error, managed: true);
        var first = RequestContinuation();
        var firstId = ReadRequestId(error);
        input.ObserveFrame($"prepare:{firstId}:ready");
        Assert.True(await first);
        error.GetStringBuilder().Clear();

        var next = RequestContinuation();
        var nextId = ReadRequestId(error);
        Assert.NotEqual(firstId, nextId);
        input.ObserveFrame($"prepare:{firstId}:ready");
        Assert.False(next.IsCompleted);
        input.ObserveFrame($"prepare:{nextId}:declined");
        Assert.False(await next);
    }

    [Fact]
    public async Task InputClosureRefusesPendingAndFuturePreparation()
    {
        using var error = new StringWriter();
        using var input = new VbaDevWorkbookConfirmationInput(Stream.Null, error, managed: true);
        var pending = RequestContinuation();
        var requestId = ReadRequestId(error);
        input.CompleteInput();
        input.ObserveFrame($"prepare:{requestId}:ready");

        Assert.False(await pending);
        error.GetStringBuilder().Clear();
        Assert.False(await RequestContinuation());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task CancellationDoesNotReturnSuccessfulContinuation()
    {
        using var error = new StringWriter();
        using var input = new VbaDevWorkbookConfirmationInput(Stream.Null, error, managed: true);
        using var cancellation = new CancellationTokenSource();
        var pending = RequestContinuation(cancellation.Token);
        var requestId = ReadRequestId(error);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        input.ObserveFrame($"prepare:{requestId}:ready");
        Assert.True(pending.IsCanceled);
    }

    [Fact]
    public async Task UnmanagedInputCannotSilentlyAuthorizePreparation()
    {
        using var error = new StringWriter();
        using var input = new VbaDevWorkbookConfirmationInput(Stream.Null, error);
        Assert.False(await RequestContinuation());
        Assert.Equal(string.Empty, error.ToString());
    }

    private static Task<bool> RequestContinuation(CancellationToken cancellationToken = default)
        => VbaDevWorkbookConfirmationInput.ContinueDebugPreparationAsync(
            new string('a', 32), Path.GetFullPath("Selected.xlsm"),
            1234, 638900000000000000, cancellationToken);

    private static string ReadRequestId(StringWriter error)
    {
        using var payload = JsonDocument.Parse(error.ToString());
        return payload.RootElement.GetProperty("requestId").GetString()!;
    }
}
