using System.Text;
using System.Text.Json.Nodes;
using VbaLanguageServer.Lsp;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Workspace;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class RenameResponseLifetimeTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancel")]
    public async Task ConfirmCaptureBeforeChallengeWriterCompletionOwnsTheLease(string delivery)
    {
        using var stream = new ChallengeGateStream();
        using var fixture = new Fixture(stream);
        using var responseCancellation = new CancellationTokenSource();
        using var original = fixture.Rename();
        var writing = fixture.Executor.ExecuteAsync(original, CancellationToken.None, responseCancellation.Token);
        try
        {
            var id = await stream.Published.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(writing.IsCompleted);
            using var confirmation = fixture.Confirm(id);
            Assert.Equal(0, fixture.Leases[0].Disposals);
            using var newer = fixture.InvalidRename("params");
            Assert.Equal(-32602, newer.Execute(CancellationToken.None).ErrorCode);
            Assert.Equal(0, fixture.Leases[0].Disposals);
            if (delivery == "cancel") responseCancellation.Cancel();
            stream.Release(delivery == "failure");
            if (delivery == "failure") await Assert.ThrowsAsync<IOException>(() => writing);
            else if (delivery == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing);
            else await writing;
            // The late writer result belongs to the original offer, not to the
            // consumed confirmation operation, even across a newer Rename.
            Assert.Equal(0, fixture.Leases[0].Disposals);
            Assert.NotNull(confirmation.Execute(CancellationToken.None).Result);
            Assert.Equal(1, fixture.Leases[0].Disposals);
            using var replay = fixture.Confirm(id);
            Assert.Equal("confirmationExpired", Reason(replay.Execute(CancellationToken.None)));
        }
        finally { stream.Release(false); _ = await Record.ExceptionAsync(() => writing); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleDeliveryFailureOrCancellationCannotDiscardANewerOffer(bool cancelWrite)
    {
        using var stream = new ChallengeGateStream();
        using var fixture = new Fixture(stream);
        using var responseCancellation = new CancellationTokenSource();
        using var original = fixture.Rename();
        var writing = fixture.Executor.ExecuteAsync(original, CancellationToken.None, responseCancellation.Token);
        try
        {
            var oldId = await stream.Published.Task.WaitAsync(TimeSpan.FromSeconds(2));
            using var newer = fixture.Rename();
            var newId = ChallengeId(newer.Execute(CancellationToken.None));
            newer.Dispose();
            Assert.NotEqual(oldId, newId);
            Assert.Equal(1, fixture.Leases[0].Disposals);
            Assert.Equal(0, fixture.Leases[1].Disposals);
            if (cancelWrite)
            {
                responseCancellation.Cancel();
                Assert.Equal(0, fixture.Leases[1].Disposals);
                // The transport owns an atomic, non-cancelable frame write.
                // Release it before awaiting cancellation of the response task.
                stream.Release(false);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing);
            }
            else
            {
                stream.Release(true);
                await Assert.ThrowsAsync<IOException>(() => writing);
            }
            Assert.Equal(0, fixture.Leases[1].Disposals);
            using var accepted = fixture.Confirm(newId);
            Assert.NotNull(accepted.Execute(CancellationToken.None).Result);
            Assert.Equal(1, fixture.Leases[1].Disposals);
        }
        finally { stream.Release(false); _ = await Record.ExceptionAsync(() => writing); }
    }

    [Fact]
    public async Task DelayedWriterSuccessCannotReviveAnInvalidatedOffer()
    {
        using var stream = new ChallengeGateStream();
        using var fixture = new Fixture(stream);
        using var original = fixture.Rename();
        var writing = fixture.Executor.ExecuteAsync(original, CancellationToken.None, CancellationToken.None);
        try
        {
            var id = await stream.Published.Task.WaitAsync(TimeSpan.FromSeconds(2));
            using var invalid = fixture.InvalidRename("name");
            Assert.Equal("invalidName", Reason(invalid.Execute(CancellationToken.None)));
            Assert.Equal(1, fixture.Leases[0].Disposals);
            stream.Release(false);
            await writing;
            using var replay = fixture.Confirm(id);
            Assert.Equal("confirmationExpired", Reason(replay.Execute(CancellationToken.None)));
        }
        finally { stream.Release(false); _ = await Record.ExceptionAsync(() => writing); }
    }

    [Theory]
    [InlineData("params", true)]
    [InlineData("name", true)]
    [InlineData("envelope", false)]
    [InlineData("cancelled", false)]
    public void OnlyARenameThatReachesCaptureInvalidatesPendingConsent(string kind, bool invalidates)
    {
        using var fixture = new Fixture(new MemoryStream());
        var id = fixture.Offer();
        using var invalid = fixture.InvalidRename(kind);
        var invalidResult = invalid.Execute(CancellationToken.None);
        Assert.NotNull(invalidResult.ErrorCode);
        Assert.Equal(invalidates ? 1 : 0, fixture.Leases[0].Disposals);
        using var confirmation = fixture.Confirm(id);
        var outcome = confirmation.Execute(CancellationToken.None);
        if (invalidates) Assert.Equal("confirmationExpired", Reason(outcome));
        else Assert.NotNull(outcome.Result);
        Assert.Single(fixture.Leases);
        Assert.Equal(1, fixture.Leases[0].Disposals);
    }

    [Theory]
    [InlineData("continue")]
    [InlineData("cancel")]
    public void ConfirmationCaptureConsumesOnceAndShutdownDoesNotOwnTheConsumedLease(string decision)
    {
        using var fixture = new Fixture(new MemoryStream());
        var id = fixture.Offer();
        using var first = fixture.Confirm(id, decision);
        using var second = fixture.Confirm(id, decision == "continue" ? "cancel" : "continue");
        Assert.Equal("confirmationExpired", Reason(second.Execute(CancellationToken.None)));
        fixture.Executor.Dispose();
        Assert.Equal(0, fixture.Leases[0].Disposals);
        var result = first.Execute(CancellationToken.None);
        Assert.Null(result.ErrorCode);
        Assert.Equal(decision == "cancel", result.Result is null);
        first.Dispose();
        Assert.Equal(1, fixture.Leases[0].Disposals);
    }

    [Fact]
    public void OldGenerationCannotOfferOrDiscardTheCurrentGeneration()
    {
        using var fixture = new Fixture(new MemoryStream());
        using var old = fixture.Rename();
        var id = fixture.Offer();
        Assert.Equal("confirmationExpired", Reason(old.Execute(CancellationToken.None)));
        old.ResponseFailure?.Invoke();
        Assert.Equal(1, fixture.Leases[0].Disposals);
        Assert.Equal(0, fixture.Leases[1].Disposals);
        using var current = fixture.Confirm(id);
        Assert.NotNull(current.Execute(CancellationToken.None).Result);
        Assert.Equal(1, fixture.Leases[1].Disposals);
    }

    [Fact]
    public void ConsumedConfirmationStillValidatesCurrentEvidenceAndRejectsWithoutAnEdit()
    {
        using var fixture = new Fixture(new MemoryStream());
        var id = fixture.Offer();
        using var confirmation = fixture.Confirm(id);
        fixture.SourceFailure = new("resourceOperationConflict", "source changed", Condition: "sourceChanged");
        var outcome = confirmation.Execute(CancellationToken.None);
        Assert.Equal("resourceOperationConflict", Reason(outcome));
        Assert.Null(outcome.Result);
        Assert.Equal(1, fixture.Leases[0].Disposals);
        using var retry = fixture.Confirm(id);
        Assert.Equal("confirmationExpired", Reason(retry.Execute(CancellationToken.None)));
    }

    [Fact]
    public async Task FailedChallengeDeliveryReleasesPendingLeaseWithoutWaitingForShutdown()
    {
        using var stream = new ChallengeGateStream();
        using var fixture = new Fixture(stream);
        using var original = fixture.Rename();
        var writing = fixture.Executor.ExecuteAsync(original, CancellationToken.None, CancellationToken.None);
        try
        {
            var id = await stream.Published.Task.WaitAsync(TimeSpan.FromSeconds(2));
            stream.Release(true);
            await Assert.ThrowsAsync<IOException>(() => writing);
            Assert.Equal(1, fixture.Leases[0].Disposals);
            using var retry = fixture.Confirm(id);
            Assert.Equal("confirmationExpired", Reason(retry.Execute(CancellationToken.None)));
        }
        finally { stream.Release(false); _ = await Record.ExceptionAsync(() => writing); }
    }

    [Fact]
    public async Task CancellationRevokesPendingBeforeTheAtomicWriteRetires()
    {
        using var stream = new ChallengeGateStream();
        using var fixture = new Fixture(stream);
        using var cancellation = new CancellationTokenSource();
        using var original = fixture.Rename();
        var writing = fixture.Executor.ExecuteAsync(original, CancellationToken.None, cancellation.Token);
        try
        {
            var id = await stream.Published.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            Assert.Equal(1, fixture.Leases[0].Disposals);
            Assert.False(writing.IsCompleted);
            using var replay = fixture.Confirm(id);
            Assert.Equal("confirmationExpired", Reason(replay.Execute(CancellationToken.None)));
            stream.Release(false);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing);
        }
        finally { stream.Release(false); _ = await Record.ExceptionAsync(() => writing); }
    }

    [Fact]
    public void ShutdownReleasesUnconsumedPendingAndRejectsOldOffers()
    {
        using var fixture = new Fixture(new MemoryStream());
        var id = fixture.Offer();
        fixture.Executor.Dispose();
        fixture.Executor.Dispose();
        Assert.Equal(1, fixture.Leases[0].Disposals);
        using var retry = fixture.Confirm(id);
        Assert.Equal("confirmationExpired", Reason(retry.Execute(CancellationToken.None)));
    }

    private static string Reason(VbaLspRequestExecution.RequestOutcome result)
        => (string)Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(result.ErrorData)["reason"]!;
    private static string ChallengeId(VbaLspRequestExecution.RequestOutcome result)
    {
        Assert.Equal("confirmationRequired", Reason(result));
        return (string)Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(result.ErrorData)["confirmationId"]!;
    }

    private sealed class Lease : IDisposable
    {
        internal int Disposals;
        public void Dispose() => Disposals++;
    }

    private sealed class Fixture : IVbaInteractiveWorkspaceCapture, IDisposable
    {
        private const string Uri = "file:///C:/work/Worker.bas";
        private readonly VbaLanguageWorkspace real = new(new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
        private readonly VbaSemanticInventory inventory;
        private int requestId;
        internal readonly List<Lease> Leases = [];
        internal VbaRenameFailure? SourceFailure;
        internal VbaLspRequestExecution Executor { get; }
        internal Fixture(Stream output)
        {
            real.OpenDocument(Uri, 1, "Attribute VB_Name = \"Worker\"\nPublic Sub Run()\nDim original As Long\nDim existing As Long\noriginal = 1\nEnd Sub");
            inventory = ((IVbaInteractiveWorkspaceCapture)real).CaptureProjectSemanticInventory(Uri);
            var capabilities = new VbaLspClientCapabilityState();
            capabilities.Update(JsonNode.Parse("""{"capabilities":{"experimental":{"vbaRenameConfirmation":{"protocolVersion":1}}}}""")!.AsObject());
            Executor = new(new LspMessageTransport(Stream.Null, output), this, clientCapabilities: capabilities);
        }
        private JsonObject RenameMessage() => new()
        {
            ["jsonrpc"] = "2.0", ["id"] = ++requestId, ["method"] = "textDocument/rename",
            ["params"] = new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = Uri },
                ["position"] = new JsonObject { ["line"] = 2, ["character"] = 5 }, ["newName"] = "existing"
            }
        };
        internal VbaLspRequestExecution.CapturedRequest Rename() => Executor.Capture(RenameMessage(), CancellationToken.None);
        internal VbaLspRequestExecution.CapturedRequest InvalidRename(string kind)
        {
            var message = RenameMessage();
            if (kind == "params") message["params"] = new JsonArray();
            if (kind == "name") message["params"]!["newName"] = "not valid";
            if (kind == "envelope") message["jsonrpc"] = "1.0";
            return Executor.Capture(message, kind == "cancelled" ? new CancellationToken(true) : CancellationToken.None);
        }
        internal VbaLspRequestExecution.CapturedRequest Confirm(string id, string decision = "continue") => Executor.Capture(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = ++requestId, ["method"] = "vba/confirmRename",
            ["params"] = new JsonObject { ["confirmationId"] = id, ["decision"] = decision }
        }, CancellationToken.None);
        internal string Offer() { using var operation = Rename(); return ChallengeId(operation.Execute(CancellationToken.None)); }
        public VbaRenameProjectSnapshotCapture CaptureRenameProjectSnapshot(string uri, CancellationToken cancellationToken = default)
        {
            var lease = new Lease(); Leases.Add(lease);
            return new(inventory, () => SourceFailure, lease);
        }
        public VbaSemanticInventory CaptureProjectSemanticInventory(string uri, CancellationToken cancellationToken = default) => inventory;
        public IReadOnlyList<VbaSemanticInventory> CaptureWorkspaceSemanticInventories(CancellationToken cancellationToken = default) => [inventory];
        public VbaVersionedDocumentSnapshot? CaptureExactDocumentSnapshot(string uri, int version, CancellationToken cancellationToken = default) => null;
        public void Dispose()
        {
            Executor.Dispose();
            real.Dispose();
            Assert.All(Leases, lease => Assert.Equal(1, lease.Disposals));
        }
    }

    private sealed class ChallengeGateStream : MemoryStream
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int gated;
        private bool fail;
        internal TaskCompletionSource<string> Published { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Release(bool failure) { fail = failure; release.TrySetResult(); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken);
            var text = Encoding.UTF8.GetString(buffer.Span);
            if (text.Contains("vbaRenameConfirmationRequired", StringComparison.Ordinal) && Interlocked.Exchange(ref gated, 1) == 0)
            {
                var json = JsonNode.Parse(text[text.IndexOf('{')..])!;
                Published.TrySetResult(json["error"]!["data"]!["confirmationId"]!.GetValue<string>());
                await release.Task.WaitAsync(cancellationToken);
                if (fail) throw new IOException("Synthetic challenge delivery failure after bytes were published.");
            }
        }
    }
}
