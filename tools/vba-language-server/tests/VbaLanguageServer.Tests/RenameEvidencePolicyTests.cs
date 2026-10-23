using System.Text.Json.Nodes;
using VbaLanguageServer.Lsp;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Workspace;
using VbaTools.Syntax;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class RenameEvidencePolicyTests
{
    public static IEnumerable<object[]> PrecedenceCases()
    {
        foreach (var confirmed in new[] { false, true })
        foreach (var duringPreflight in new[] { false, true })
        foreach (var (invalid, expected) in new[]
        {
            (1, "source"), (2, "authority"), (4, "template"), (8, "paths"),
            (15, "source"), (14, "authority"), (12, "template"), (0, "preflight")
        }) yield return [confirmed, duringPreflight, invalid, expected];
    }

    [Theory]
    [MemberData(nameof(PrecedenceCases))]
    public void OrdinaryAndConfirmedPlansUseTheSameEvidencePrecedence(
        bool confirmed, bool duringPreflight, int invalid, string expected)
    {
        var state = duringPreflight ? 0 : invalid;
        var preflights = 0;
        var preflightFailure = new VbaRenameFailure("preflight", "preflight failed");
        VbaRenameFailure? Failure(int bit, string name) => (state & bit) == 0 ? null : new(name, name);
        using var capture = new VbaRenameProjectSnapshotCapture(EmptyInventory(),
            () => Failure(1, "source"), null,
            preflightFileRenames: plan =>
            {
                preflights++;
                if (duringPreflight) state = invalid;
                return new(plan, preflightFailure);
            }, getSourceTemplateChangeFailure: () => Failure(4, "template"),
            getConfirmationAuthorityChangeFailure: () => Failure(2, "authority"));
        var paths = new VbaRenamePathDecision(false, [], [], () => Failure(8, "paths"));
        var policy = new RenameEvidencePolicy(capture, requiresTemplate: true, supportsRenameFile: false);
        var result = policy.ValidatePlan(FilePlan(), paths, retainCompletePlan: confirmed);
        Assert.Equal(expected, result.Failure?.Reason);
        Assert.Equal(duringPreflight || invalid == 0 ? 1 : 0, preflights);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void EvidenceChangesDuringSuccessfulPreflightAlsoWin(bool confirmed, bool failPreflight)
    {
        var changed = false;
        using var capture = new VbaRenameProjectSnapshotCapture(EmptyInventory(),
            () => changed ? new("source", "source changed") : null, null,
            preflightFileRenames: plan =>
            {
                changed = true;
                return new(plan, failPreflight ? new("preflight", "file failed") : null);
            });
        var result = new RenameEvidencePolicy(capture, false, false)
            .ValidatePlan(FilePlan(), null, confirmed);
        Assert.Equal("source", result.Failure?.Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyActualFinalOperationsRequireCapabilityAndConditionalTemplateProof(bool confirmed)
    {
        using var capture = new VbaRenameProjectSnapshotCapture(EmptyInventory(), () => null, null,
            getSourceTemplateChangeFailure: () => new("template", "not applicable"));
        var policy = new RenameEvidencePolicy(capture, requiresTemplate: false, supportsRenameFile: false);
        var retainedPaths = FilePlan() with { FileRenames = [] };
        Assert.Null(policy.ValidatePlan(retainedPaths, null, confirmed).Failure);
        Assert.Equal("clientCapabilityMissing", policy.ValidatePlan(FilePlan(), null, confirmed).Failure?.Reason);
    }

    [Fact]
    public void ConfirmationValidatesTheCompleteStoredPlanWithoutAcceptingChangedClosure()
    {
        var calls = 0;
        using var capture = new VbaRenameProjectSnapshotCapture(EmptyInventory(), () => null, null,
            preflightFileRenames: plan =>
            {
                calls++;
                return new(plan with { FileRenames = [.. plan.FileRenames, new("file:///C:/work/extra.bas", "file:///C:/work/Other.bas")] }, null);
            });
        var result = new RenameEvidencePolicy(capture, false, true).ValidatePlan(FilePlan(), null, true);
        Assert.Equal("renamePlanChanged", result.Failure?.Condition);
        Assert.Equal(1, calls);
        Assert.Single(result.Plan.FileRenames);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnexpectedPreflightExceptionRemainsAnInternalFailure(bool confirmed)
    {
        var original = new InvalidOperationException("unexpected preflight failure");
        using var capture = new VbaRenameProjectSnapshotCapture(EmptyInventory(), () => null, null,
            preflightFileRenames: _ => throw original);
        var policy = new RenameEvidencePolicy(capture, false, true);
        Assert.Same(original, Assert.Throws<InvalidOperationException>(() => policy.ValidatePlan(FilePlan(), null, confirmed)));
    }

    [Theory]
    [InlineData("original")]
    [InlineData("existing")]
    public void NoChangeAndSemanticRejectionStillValidateProjectAuthorityAndReleaseCapture(string newName)
    {
        const string uri = "file:///C:/work/Worker.bas";
        const string text = "Attribute VB_Name = \"Worker\"\nPublic Sub Run()\nDim original As Long\nDim existing As Long\nEnd Sub";
        using var real = new VbaLanguageWorkspace(new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
        real.OpenDocument(uri, 1, text);
        var lease = new Lease();
        var capture = new VbaRenameProjectSnapshotCapture(((IVbaInteractiveWorkspaceCapture)real).CaptureProjectSemanticInventory(uri), () => null, lease,
            getConfirmationAuthorityChangeFailure: () => new("authority", "project authority changed"));
        using var output = new MemoryStream();
        using var executor = new VbaLspRequestExecution(new LspMessageTransport(Stream.Null, output), new Workspace(capture));
        using var request = executor.Capture(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "textDocument/rename",
            ["params"] = new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = uri },
                ["position"] = new JsonObject { ["line"] = 2, ["character"] = 5 }, ["newName"] = newName
            }
        }, CancellationToken.None);
        var result = request.Execute(CancellationToken.None);
        var data = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(result.ErrorData);
        Assert.Equal("authority", data["reason"]);
        Assert.Null(result.Result);
        request.Dispose();
        Assert.Equal(1, lease.Disposals);
    }

    public static IEnumerable<object[]> ProductionCases()
    {
        foreach (var confirmed in new[] { false, true })
        foreach (var mode in new[] { "source", "authority", "paths", "all", "during-success", "during-failure", "preflight", "internal" })
            yield return [confirmed, mode];
    }

    [Theory]
    [MemberData(nameof(ProductionCases))]
    public async Task OrdinaryAndConfirmedProtocolPathsUseThePolicyAndReleaseOnce(bool confirmed, string mode)
    {
        const string uri = "file:///C:/work/Original.bas";
        const string other = "file:///C:/work/Existing.bas";
        var inventory = VbaSemanticInventory.Create(new Dictionary<string, VbaSourceDocument>
        {
            [uri] = VbaSourceDocumentProjector.Project(uri, VbaSyntaxTree.ParseModule(uri,
                "Attribute VB_Name = \"Original\"\nPublic Sub Run()\nEnd Sub")),
            [other] = VbaSourceDocumentProjector.Project(other, VbaSyntaxTree.ParseModule(other,
                "Attribute VB_Name = \"Existing\"\nPublic Sub Other()\nEnd Sub"))
        });
        var active = false;
        var changedDuring = false;
        var preflights = 0;
        VbaRenameFailure? Source() => active && (mode is "source" or "all" || changedDuring) ? new("source", "source changed") : null;
        VbaRenameFailure? Authority() => active && mode is "authority" or "all" ? new("authority", "authority changed") : null;
        VbaRenameFailure? Paths() => active && mode is "paths" or "all" ? new("paths", "paths changed") : null;
        var lease = new Lease();
        var capture = new VbaRenameProjectSnapshotCapture(inventory, Source, lease,
            preflightFileRenames: plan =>
            {
                preflights++;
                if (active && mode == "internal") throw new InvalidOperationException("unexpected file inspection failure");
                if (active && mode.StartsWith("during-", StringComparison.Ordinal)) changedDuring = true;
                return new(plan, active && mode is "preflight" or "during-failure" ? new("preflight", "file inspection failed") : null);
            }, prepareRenamePathDecision: _ => new(new(false, [], [], Paths), null),
            getConfirmationAuthorityChangeFailure: Authority);
        var capabilities = new VbaLspClientCapabilityState();
        capabilities.Update(JsonNode.Parse("""
            {"capabilities":{"workspace":{"workspaceEdit":{"documentChanges":true,"resourceOperations":["rename"]}},
            "experimental":{"vbaRenameConfirmation":{"protocolVersion":1}}}}
            """)!.AsObject());
        using var output = new MemoryStream();
        var workspace = new Workspace(capture);
        using var executor = new VbaLspRequestExecution(new LspMessageTransport(Stream.Null, output), workspace, clientCapabilities: capabilities);
        JsonObject Rename() => new()
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "textDocument/rename",
            ["params"] = new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = uri },
                ["position"] = new JsonObject { ["line"] = 0, ["character"] = 21 },
                ["newName"] = confirmed ? "Existing" : "Revised"
            }
        };
        VbaLspRequestExecution.CapturedRequest subject;
        if (confirmed)
        {
            using var offered = executor.Capture(Rename(), CancellationToken.None);
            var initial = offered.Execute(CancellationToken.None);
            var challenge = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(initial.ErrorData);
            Assert.Equal("confirmationRequired", challenge["reason"]);
            offered.Dispose();
            Assert.Equal(0, lease.Disposals);
            subject = executor.Capture(new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "vba/confirmRename",
                ["params"] = new JsonObject { ["confirmationId"] = (string)challenge["confirmationId"]!, ["decision"] = "continue" }
            }, CancellationToken.None);
        }
        else subject = executor.Capture(Rename(), CancellationToken.None);
        var before = preflights;
        active = true;
        using (subject) await executor.ExecuteAsync(subject, CancellationToken.None, CancellationToken.None);
        output.Position = 0;
        var response = Assert.IsType<JsonObject>(await new LspMessageTransport(output, Stream.Null).ReadMessageAsync(CancellationToken.None));
        var expected = mode switch { "all" or "source" or "during-success" or "during-failure" => "source", _ => mode };
        if (mode == "internal") Assert.Equal(-32603, response["error"]!["code"]!.GetValue<int>());
        else Assert.Equal(expected, response["error"]!["data"]!["reason"]!.GetValue<string>());
        Assert.Null(response["result"]);
        Assert.Equal(1, lease.Disposals);
        Assert.Equal(1, workspace.Captures);
        if (mode.StartsWith("during-", StringComparison.Ordinal) || mode is "preflight" or "internal") Assert.Equal(before + 1, preflights);
        else Assert.Equal(before, preflights);
    }

    private static VbaSemanticInventory EmptyInventory() => VbaSemanticInventory.Create(new Dictionary<string, VbaSourceDocument>());
    private static VbaRenamePlan FilePlan() => new(new(new(0, 0), new(0, 1)), new Dictionary<string, IReadOnlyList<VbaTextEdit>>())
        { FileRenames = [new("file:///C:/work/Original.bas", "file:///C:/work/Revised.bas")] };
    private sealed class Lease : IDisposable { internal int Disposals; public void Dispose() => Disposals++; }
    private sealed class Workspace(VbaRenameProjectSnapshotCapture capture) : IVbaInteractiveWorkspaceCapture
    {
        internal int Captures;
        public VbaRenameProjectSnapshotCapture CaptureRenameProjectSnapshot(string uri, CancellationToken cancellationToken = default) { Captures++; return capture; }
        public VbaSemanticInventory CaptureProjectSemanticInventory(string uri, CancellationToken cancellationToken = default) => capture.SemanticInventory;
        public IReadOnlyList<VbaSemanticInventory> CaptureWorkspaceSemanticInventories(CancellationToken cancellationToken = default) => [];
        public VbaVersionedDocumentSnapshot? CaptureExactDocumentSnapshot(string uri, int version, CancellationToken cancellationToken = default) => null;
    }
}
