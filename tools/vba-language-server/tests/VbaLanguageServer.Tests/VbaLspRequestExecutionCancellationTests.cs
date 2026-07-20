using System.Text.Json.Nodes;
using VbaLanguageServer.Lsp;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Workspace;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaLspRequestExecutionCancellationTests
{
    [Fact]
    public async Task Every_supported_interactive_feature_captures_only_its_declared_workspace_view()
    {
        await using var output = new MemoryStream();
        var transport = new LspMessageTransport(Stream.Null, output);
        var cases = new[]
        {
            ("textDocument/completion", CreatePositionParameters(), CaptureKind.Project),
            ("textDocument/documentSymbol", CreateTextDocumentParameters(), CaptureKind.Project),
            ("textDocument/definition", CreatePositionParameters(), CaptureKind.Project),
            ("textDocument/references", CreatePositionParameters(), CaptureKind.Project),
            ("workspace/symbol", new JsonObject { ["query"] = "" }, CaptureKind.Workspace),
            ("textDocument/hover", CreatePositionParameters(), CaptureKind.Project),
            ("textDocument/signatureHelp", CreatePositionParameters(), CaptureKind.Project),
            ("textDocument/prepareRename", CreatePositionParameters(), CaptureKind.Project),
            ("textDocument/rename", CreateRenameParameters(), CaptureKind.Project),
            ("textDocument/formatting", CreateFormattingParameters(), CaptureKind.Project),
            ("vba/blockSkeletonInsertion", CreateBlockSkeletonParameters(), CaptureKind.ExactDocument),
            ("textDocument/semanticTokens/full", CreateTextDocumentParameters(), CaptureKind.Project)
        };

        foreach (var (method, parameters, expectedCapture) in cases)
        {
            var workspace = new RecordingInteractiveWorkspaceCapture();
            var executor = new VbaLspRequestExecution(transport, workspace);
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = method,
                ["params"] = parameters
            };

            var captured = executor.Capture(request, CancellationToken.None);

            Assert.Equal(expectedCapture == CaptureKind.Project ? 1 : 0, workspace.ProjectCaptureCount);
            Assert.Equal(expectedCapture == CaptureKind.Workspace ? 1 : 0, workspace.WorkspaceCaptureCount);
            Assert.Equal(expectedCapture == CaptureKind.ExactDocument ? 1 : 0, workspace.ExactDocumentCaptureCount);
            Assert.True(captured.UseExecutionGate);

            var projectCaptures = workspace.ProjectCaptureCount;
            var workspaceCaptures = workspace.WorkspaceCaptureCount;
            var exactDocumentCaptures = workspace.ExactDocumentCaptureCount;

            captured.Execute(CancellationToken.None);

            Assert.Equal(projectCaptures, workspace.ProjectCaptureCount);
            Assert.Equal(workspaceCaptures, workspace.WorkspaceCaptureCount);
            Assert.Equal(exactDocumentCaptures, workspace.ExactDocumentCaptureCount);
        }
    }

    [Fact]
    public void Block_skeleton_uses_committed_exact_analysis_without_rebuild_or_project_capture()
    {
        const string uri = "file:///C:/work/Worker.bas";
        const string text =
            "Attribute VB_Name = \"Worker\"\n"
            + "Public Function BuildValue() As String\n"
            + "    ";
        var analysisObserver = new CountingDocumentAnalysisBuildObserver();
        var projectObserver = new CountingProjectSnapshotBuildObserver();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            analysisObserver,
            projectObserver);
        workspace.OpenDocument(uri, version: 1, text);
        var baselineAnalysisBuilds = analysisObserver.BuildCount;
        using var output = new MemoryStream();
        var executor = new VbaLspRequestExecution(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        var parameters = CreateBlockSkeletonParameters();
        parameters["documentUri"] = uri;
        parameters["position"]!["line"] = 1;
        parameters["position"]!["character"] =
            "Public Function BuildValue() As String".Length;
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "vba/blockSkeletonInsertion",
            ["params"] = parameters
        };

        var captured = executor.Capture(request, CancellationToken.None);
        var outcome = captured.Execute(CancellationToken.None);

        Assert.Null(outcome.ErrorCode);
        Assert.NotNull(outcome.Result);
        Assert.Equal(baselineAnalysisBuilds, analysisObserver.BuildCount);
        Assert.Equal(0, projectObserver.BuildCount);
    }

    [Fact]
    public async Task Executor_returns_request_cancelled_for_a_request_cancelled_before_execution()
    {
        await using var output = new MemoryStream();
        var transport = new LspMessageTransport(Stream.Null, output);
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.CreateBundled());
        var executor = new VbaLspRequestExecution(
            transport,
            new VbaLanguageWorkspace(catalogCache));
        using var requestCancellation = new CancellationTokenSource();
        requestCancellation.Cancel();
        var request = JsonNode.Parse(
            """
            {
              "jsonrpc": "2.0",
              "id": 7,
              "method": "test/unknown"
            }
            """)!.AsObject();

        var capturedRequest = executor.Capture(
            request,
            requestCancellation.Token);
        await executor.ExecuteAsync(
            capturedRequest,
            requestCancellation.Token,
            CancellationToken.None);

        output.Position = 0;
        var responseReader = new LspMessageTransport(output, Stream.Null);
        var response = Assert.IsType<JsonObject>(
            await responseReader.ReadMessageAsync(CancellationToken.None));
        Assert.Equal(-32800, response["error"]!["code"]!.GetValue<int>());
    }

    private static JsonObject CreateTextDocumentParameters()
        => new()
        {
            ["textDocument"] = new JsonObject
            {
                ["uri"] = "file:///C:/work/Worker.bas"
            }
        };

    private static JsonObject CreatePositionParameters()
    {
        var parameters = CreateTextDocumentParameters();
        parameters["position"] = new JsonObject
        {
            ["line"] = 1,
            ["character"] = 0
        };
        return parameters;
    }

    private static JsonObject CreateRenameParameters()
    {
        var parameters = CreatePositionParameters();
        parameters["newName"] = "Renamed";
        return parameters;
    }

    private static JsonObject CreateFormattingParameters()
    {
        var parameters = CreateTextDocumentParameters();
        parameters["options"] = new JsonObject
        {
            ["tabSize"] = 4,
            ["insertSpaces"] = true
        };
        return parameters;
    }

    private static JsonObject CreateBlockSkeletonParameters()
        => new()
        {
            ["documentUri"] = "file:///C:/work/Worker.bas",
            ["documentVersion"] = 1,
            ["position"] = new JsonObject
            {
                ["line"] = 1,
                ["character"] = 0
            },
            ["options"] = new JsonObject
            {
                ["tabSize"] = 4,
                ["insertSpaces"] = true
            }
        };

    private enum CaptureKind
    {
        Project,
        Workspace,
        ExactDocument
    }

    private sealed class RecordingInteractiveWorkspaceCapture
        : IVbaInteractiveWorkspaceCapture
    {
        private static readonly VbaSemanticInventory EmptyInventory =
            VbaSemanticInventory.Create(
                new Dictionary<string, VbaSourceDocument>(
                    StringComparer.OrdinalIgnoreCase));

        public int ProjectCaptureCount { get; private set; }

        public int WorkspaceCaptureCount { get; private set; }

        public int ExactDocumentCaptureCount { get; private set; }

        public VbaSemanticInventory CaptureProjectSemanticInventory(
            string activeUri,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProjectCaptureCount++;
            return EmptyInventory;
        }

        public IReadOnlyList<VbaSemanticInventory> CaptureWorkspaceSemanticInventories(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WorkspaceCaptureCount++;
            return [EmptyInventory];
        }

        public VbaVersionedDocumentSnapshot? CaptureExactDocumentSnapshot(
            string uri,
            int expectedVersion,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExactDocumentCaptureCount++;
            return null;
        }
    }

    private sealed class CountingDocumentAnalysisBuildObserver
        : IVbaDocumentAnalysisBuildObserver
    {
        public int BuildCount { get; private set; }

        public void BeforeBuild(
            VbaDocumentAnalysisBuildContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BuildCount++;
        }
    }

    private sealed class CountingProjectSnapshotBuildObserver
        : IVbaProjectSnapshotBuildObserver
    {
        public int BuildCount { get; private set; }

        public void BeforeStore(
            long workspaceVersion,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BuildCount++;
        }
    }
}
