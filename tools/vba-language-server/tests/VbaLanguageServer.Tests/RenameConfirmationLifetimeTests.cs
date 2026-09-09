using System.Text.Json.Nodes;
using VbaLanguageServer.Lsp;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Workspace;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class RenameConfirmationLifetimeTests
{
    [Fact]
    public void Cancellation_after_planning_releases_the_unpublished_confirmation_evidence()
    {
        const string uri = "file:///C:/work/ConfirmationLifetime.bas";
        using var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
        workspace.OpenDocument(uri, 1,
            "Attribute VB_Name = \"ConfirmationLifetime\"\nPublic Sub Run()\n"
            + "    Dim original As Long\n    Dim existing As Long\n    original = 1\nEnd Sub");
        var capabilities = new VbaLspClientCapabilityState();
        capabilities.Update(JsonNode.Parse("""
            {"capabilities":{"experimental":{"vbaRenameConfirmation":{"protocolVersion":1}}}}
            """)!.AsObject());
        using var output = new MemoryStream();
        using var executor = new VbaLspRequestExecution(
            new LspMessageTransport(Stream.Null, output), workspace, clientCapabilities: capabilities);
        using var cancellation = new CancellationTokenSource();
        using var captured = executor.Capture(JsonNode.Parse("""
            {"jsonrpc":"2.0","id":1,"method":"textDocument/rename","params":{
              "textDocument":{"uri":"file:///C:/work/ConfirmationLifetime.bas"},
              "position":{"line":2,"character":8},"newName":"existing"}}
            """)!.AsObject(), cancellation.Token);

        var outcome = captured.Execute(cancellation.Token);
        var challenge = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(outcome.ErrorData);
        Assert.Equal("vbaRenameConfirmationRequired", challenge["kind"]);
        cancellation.Cancel();
        captured.Dispose();

        Assert.Equal(0, workspace.RetainedRenameSourceRevisionCount);
        using var replay = executor.Capture(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "vba/confirmRename",
            ["params"] = new JsonObject
            {
                ["confirmationId"] = (string)challenge["confirmationId"]!, ["decision"] = "continue"
            }
        }, CancellationToken.None);
        var replayOutcome = replay.Execute(CancellationToken.None);
        var failure = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(replayOutcome.ErrorData);
        Assert.Equal("confirmationExpired", failure["reason"]);
    }
}
