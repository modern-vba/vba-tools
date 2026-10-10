using System.Text.Json;
using VbaDebugAdapter.Debugging;
using VbaDebugAdapter.Infrastructure;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class DebugWorkbookConfirmationGateTests
{
    [Fact]
    public async Task OnlyThePublishedGenerationBoundReplyAllowsReplacement()
    {
        using var gate = new DebugWorkbookConfirmationGate(
            DebugSessionId.Parse("0123456789abcdef0123456789abcdef"));
        DebugWorkbookConfirmationRequest? warning = null;
        var confirmation = gate.RequestAsync(DebugGenerationId.FromValue(4),
            @"C:\Projects\Book\src\Book\Book.xlsm", "Replace live VBA code?",
            (request, _) => { warning = request; return Task.CompletedTask; },
            CancellationToken.None);

        Assert.NotNull(warning);
        Assert.False(confirmation.IsCompleted);
        Assert.Equal(4, warning.GenerationId);
        Assert.Equal("1.0", warning.SchemaVersion);
        Assert.True(gate.TryComplete(Reply(warning, true)));
        Assert.True(await confirmation);
        Assert.False(gate.TryComplete(Reply(warning, true)));
    }

    private static JsonElement Reply(DebugWorkbookConfirmationRequest warning, bool accepted)
        => JsonSerializer.SerializeToElement(new
        {
            requestId = warning.RequestId, sessionId = warning.SessionId,
            generationId = warning.GenerationId, accepted
        });

    [Fact]
    public async Task MalformedGenerationReplyDoesNotThrowOrConsumeThePendingWarning()
    {
        using var gate = new DebugWorkbookConfirmationGate(
            DebugSessionId.Parse("0123456789abcdef0123456789abcdef"));
        DebugWorkbookConfirmationRequest? warning = null;
        var confirmation = gate.RequestAsync(DebugGenerationId.FromValue(4),
            @"C:\Projects\Book\src\Book\Book.xlsm", "Replace live VBA code?",
            (request, _) => { warning = request; return Task.CompletedTask; },
            CancellationToken.None);
        Assert.NotNull(warning);
        var malformed = JsonSerializer.SerializeToElement(new
        {
            requestId = warning.RequestId, sessionId = warning.SessionId,
            generationId = "4", accepted = true
        });

        Assert.False(gate.TryComplete(malformed));
        Assert.False(confirmation.IsCompleted);
        Assert.True(gate.TryComplete(Reply(warning, false)));
        Assert.False(await confirmation);
    }

    [Theory]
    [InlineData("requestId")]
    [InlineData("sessionId")]
    [InlineData("generationId")]
    [InlineData("accepted")]
    [InlineData("unknown")]
    public async Task StaleOrMalformedRepliesCannotAuthorizeReplacement(string invalidField)
    {
        using var gate = new DebugWorkbookConfirmationGate(
            DebugSessionId.Parse("0123456789abcdef0123456789abcdef"));
        DebugWorkbookConfirmationRequest? warning = null;
        var confirmation = gate.RequestAsync(DebugGenerationId.FromValue(4),
            @"C:\Projects\Book\src\Book\Book.xlsm", "Replace live VBA code?",
            (request, _) => { warning = request; return Task.CompletedTask; }, CancellationToken.None);
        Assert.NotNull(warning);
        var fields = new Dictionary<string, object>
        {
            ["requestId"] = warning.RequestId, ["sessionId"] = warning.SessionId,
            ["generationId"] = 4, ["accepted"] = true
        };
        fields[invalidField] = invalidField == "generationId" ? 3 : "not a bound reply";

        Assert.False(gate.TryComplete(JsonSerializer.SerializeToElement(fields)));
        Assert.False(confirmation.IsCompleted);
        Assert.True(gate.TryComplete(Reply(warning, false)));
        Assert.False(await confirmation);
    }

    [Fact]
    public async Task CancellationRetiresTheNonceAndRejectsALateYes()
    {
        using var cancellation = new CancellationTokenSource();
        using var gate = new DebugWorkbookConfirmationGate(
            DebugSessionId.Parse("0123456789abcdef0123456789abcdef"));
        DebugWorkbookConfirmationRequest? warning = null;
        var confirmation = gate.RequestAsync(DebugGenerationId.Initial,
            @"C:\Projects\Book\src\Book\Book.xlsm", "Replace?",
            (request, _) => { warning = request; return Task.CompletedTask; }, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => confirmation);
        Assert.NotNull(warning);
        Assert.False(gate.TryComplete(Reply(warning, true)));
    }

    [Fact]
    public async Task DisposalDeclinesPendingConfirmationInsteadOfApprovingIt()
    {
        var gate = new DebugWorkbookConfirmationGate(
            DebugSessionId.Parse("0123456789abcdef0123456789abcdef"));
        DebugWorkbookConfirmationRequest? warning = null;
        var confirmation = gate.RequestAsync(DebugGenerationId.Initial,
            @"C:\Projects\Book\src\Book\Book.xlsm", "Replace?",
            (request, _) => { warning = request; return Task.CompletedTask; }, CancellationToken.None);
        gate.Dispose();

        Assert.False(await confirmation);
        Assert.NotNull(warning);
        Assert.False(gate.TryComplete(Reply(warning, true)));
    }
}
