using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text;
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
    public async Task Rename_fails_when_a_participating_source_changes_after_capture()
    {
        const string uri = "file:///C:/work/Worker.bas";
        const string text =
            "Attribute VB_Name = \"Worker\"\n"
            + "Public Function BuildValue() As Long\n"
            + "    BuildValue = 1\n"
            + "End Function";
        await using var output = new MemoryStream();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()));
        workspace.OpenDocument(uri, version: 1, text);
        var executor = new VbaLspRequestExecution(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        var parameters = CreateRenameParameters();
        parameters["textDocument"]!["uri"] = uri;
        parameters["position"]!["line"] = 1;
        parameters["position"]!["character"] = "Public Function ".Length;
        parameters["newName"] = "CreateValue";
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "textDocument/rename",
            ["params"] = parameters
        };

        var captured = executor.Capture(request, CancellationToken.None);
        Assert.True(workspace.ChangeDocument(
            uri,
            version: 2,
            text + "\n' changed while Rename was planning"));

        var outcome = captured.Execute(CancellationToken.None);
        var data = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            outcome.ErrorData);

        Assert.Equal(-32803, outcome.ErrorCode);
        Assert.Equal("resourceOperationConflict", data["reason"]);
        Assert.Equal("sourceChanged", data["condition"]);
        Assert.Null(outcome.Result);
        Assert.Equal(0, workspace.RetainedSourceRevisionCount);
        Assert.Equal(0, workspace.RetainedRenameSourceRevisionCount);
    }

    [Theory]
    [InlineData("ContainingProject")]
    [InlineData("BillingModule")]
    public async Task Module_rename_fails_when_the_captured_source_template_changes_before_the_final_fence(
        string capturedProjectName)
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-rename-template-fence-").FullName;
        try
        {
            const string documentName = "Book1";
            var sourceRoot = Directory.CreateDirectory(Path.Combine(
                projectRoot,
                "src",
                documentName)).FullName;
            var sourcePath = Path.Combine(sourceRoot, "SourceUnit.bas");
            var templatePath = Path.Combine(projectRoot, "Book1.xlsm");
            const string text = "Attribute VB_Name = \"InvoiceModule\"";
            WriteManifest(projectRoot, documentName, "src/Book1");
            File.WriteAllText(sourcePath, text);
            File.WriteAllBytes(
                templatePath,
                VbaProjectIdentityWorkbookFixture.Create(
                    capturedProjectName,
                    1252));
            var uri = new Uri(sourcePath).AbsoluteUri;
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()));
            workspace.OpenDocument(uri, version: 1, text);
            await using var output = new MemoryStream();
            var executor = new VbaLspRequestExecution(
                new LspMessageTransport(Stream.Null, output),
                workspace);
            var parameters = CreateRenameParameters();
            parameters["textDocument"]!["uri"] = uri;
            parameters["position"]!["line"] = 0;
            parameters["position"]!["character"] =
                "Attribute VB_Name = \"".Length;
            parameters["newName"] = "BillingModule";
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "textDocument/rename",
                ["params"] = parameters
            };

            var captured = executor.Capture(request, CancellationToken.None);
            File.WriteAllBytes(
                templatePath,
                VbaProjectIdentityWorkbookFixture.Create(
                    "ChangedProject",
                    1252));

            var outcome = captured.Execute(CancellationToken.None);
            var data = Assert.IsAssignableFrom<
                IReadOnlyDictionary<string, object?>>(outcome.ErrorData);

            Assert.Equal(-32803, outcome.ErrorCode);
            Assert.Equal("analysisIncomplete", data["reason"]);
            Assert.Equal("sourceTemplateChanged", data["condition"]);
            Assert.Equal(
                templatePath,
                Assert.IsType<string>(data["path"]),
                ignoreCase: true);
            Assert.Null(outcome.Result);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task File_following_module_rename_reports_the_changed_source_path_and_repair_guidance()
    {
        var sourceRoot = Directory.CreateTempSubdirectory(
            "vba-ls-rename-source-change-").FullName;
        try
        {
            var sourcePath = Path.Combine(sourceRoot, "InvoiceModule.bas");
            const string text =
                "Attribute VB_Name = \"InvoiceModule\"\n"
                + "Public Sub Run()\n"
                + "End Sub";
            File.WriteAllText(sourcePath, text);
            var uri = new Uri(sourcePath).AbsoluteUri;
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()));
            workspace.OpenDocument(uri, version: 1, text);
            await using var output = new MemoryStream();
            var clientCapabilities = new VbaLspClientCapabilityState();
            clientCapabilities.Update(new JsonObject
            {
                ["capabilities"] = new JsonObject
                {
                    ["workspace"] = new JsonObject
                    {
                        ["workspaceEdit"] = new JsonObject
                        {
                            ["documentChanges"] = true,
                            ["resourceOperations"] = new JsonArray("rename")
                        }
                    }
                }
            });
            var executor = new VbaLspRequestExecution(
                new LspMessageTransport(Stream.Null, output),
                workspace,
                clientCapabilities: clientCapabilities);
            var parameters = CreateRenameParameters();
            parameters["textDocument"]!["uri"] = uri;
            parameters["position"]!["line"] = 0;
            parameters["position"]!["character"] =
                "Attribute VB_Name = \"".Length;
            parameters["newName"] = "BillingModule";
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "textDocument/rename",
                ["params"] = parameters
            };

            var captured = executor.Capture(request, CancellationToken.None);
            Assert.True(workspace.ChangeDocument(
                uri,
                version: 2,
                text + "\n' changed while Rename was planning"));

            var outcome = captured.Execute(CancellationToken.None);
            var data = Assert.IsAssignableFrom<
                IReadOnlyDictionary<string, object?>>(outcome.ErrorData);

            Assert.Equal(-32803, outcome.ErrorCode);
            Assert.Equal("resourceOperationConflict", data["reason"]);
            Assert.Equal("sourceChanged", data["condition"]);
            Assert.Equal(sourcePath, Assert.IsType<string>(data["path"]), ignoreCase: true);
            Assert.Contains("retry", Assert.IsType<string>(data["guidance"]), StringComparison.OrdinalIgnoreCase);
            Assert.Null(outcome.Result);
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(true, "sourceChanged")]
    [InlineData(false, "sidecarConflict")]
    public async Task Deliberate_basename_form_rename_fences_unchanged_source_unit_bytes(
        bool changeForm,
        string expectedCondition)
    {
        var sourceRoot = Directory.CreateTempSubdirectory(
            "vba-ls-rename-form-unit-change-").FullName;
        try
        {
            var sourcePath = Path.Combine(sourceRoot, "LegacyDialog.frm");
            var sidecarPath = Path.Combine(sourceRoot, "LegacyDialog.frx");
            var text = string.Join('\n', [
                "VERSION 5.00",
                "Begin VB.UserForm Dialog",
                "   Picture = \"LegacyDialog.frx\":0000",
                "End",
                "Attribute VB_Name = \"Dialog\""
            ]);
            File.WriteAllText(sourcePath, text);
            File.WriteAllBytes(sidecarPath, [0x01, 0x02, 0x03]);
            var uri = new Uri(sourcePath).AbsoluteUri;
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()));
            workspace.OpenDocument(uri, version: 1, text);
            await using var output = new MemoryStream();
            var executor = new VbaLspRequestExecution(
                new LspMessageTransport(Stream.Null, output),
                workspace);
            var parameters = CreateRenameParameters();
            parameters["textDocument"]!["uri"] = uri;
            parameters["position"]!["line"] = 4;
            parameters["position"]!["character"] =
                "Attribute VB_Name = \"".Length;
            parameters["newName"] = "DialogView";
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "textDocument/rename",
                ["params"] = parameters
            };

            var captured = executor.Capture(request, CancellationToken.None);
            if (changeForm)
            {
                File.WriteAllText(
                    sourcePath,
                    text + "\n' changed on disk while Rename was planning");
            }
            else
            {
                File.WriteAllBytes(sidecarPath, [0x09, 0x08, 0x07, 0x06]);
            }

            var outcome = captured.Execute(CancellationToken.None);
            var data = Assert.IsAssignableFrom<
                IReadOnlyDictionary<string, object?>>(outcome.ErrorData);

            Assert.Equal(-32803, outcome.ErrorCode);
            Assert.Equal("resourceOperationConflict", data["reason"]);
            Assert.Equal(expectedCondition, data["condition"]);
            Assert.Equal(
                changeForm ? sourcePath : sidecarPath,
                Assert.IsType<string>(data["path"]),
                ignoreCase: true);
            Assert.Null(outcome.Result);
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task File_following_form_rename_reports_sidecar_conflict_when_frx_appears_after_request_capture()
    {
        var sourceRoot = Directory.CreateTempSubdirectory(
            "vba-ls-rename-sidecar-appeared-").FullName;
        try
        {
            var sourcePath = Path.Combine(sourceRoot, "Dialog.frm");
            var sidecarPath = Path.Combine(sourceRoot, "Dialog.frx");
            var text = string.Join('\n', [
                "VERSION 5.00",
                "Begin VB.Form Dialog",
                "End",
                "Attribute VB_Name = \"Dialog\""
            ]);
            File.WriteAllText(sourcePath, text);
            var uri = new Uri(sourcePath).AbsoluteUri;
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()));
            workspace.OpenDocument(uri, version: 1, text);
            await using var output = new MemoryStream();
            var clientCapabilities = new VbaLspClientCapabilityState();
            clientCapabilities.Update(new JsonObject
            {
                ["capabilities"] = new JsonObject
                {
                    ["workspace"] = new JsonObject
                    {
                        ["workspaceEdit"] = new JsonObject
                        {
                            ["documentChanges"] = true,
                            ["resourceOperations"] = new JsonArray("rename")
                        }
                    }
                }
            });
            var executor = new VbaLspRequestExecution(
                new LspMessageTransport(Stream.Null, output),
                workspace,
                clientCapabilities: clientCapabilities);
            var parameters = CreateRenameParameters();
            parameters["textDocument"]!["uri"] = uri;
            parameters["position"]!["line"] = 3;
            parameters["position"]!["character"] =
                "Attribute VB_Name = \"".Length;
            parameters["newName"] = "DialogView";
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "textDocument/rename",
                ["params"] = parameters
            };

            var captured = executor.Capture(request, CancellationToken.None);
            File.WriteAllBytes(sidecarPath, [0x01, 0x02, 0x03]);

            var outcome = captured.Execute(CancellationToken.None);
            var data = Assert.IsAssignableFrom<
                IReadOnlyDictionary<string, object?>>(outcome.ErrorData);

            Assert.Equal(-32803, outcome.ErrorCode);
            Assert.Equal("resourceOperationConflict", data["reason"]);
            Assert.Equal("sidecarConflict", data["condition"]);
            Assert.Equal(
                sidecarPath,
                Assert.IsType<string>(data["path"]),
                ignoreCase: true);
            Assert.Contains(
                "retry",
                Assert.IsType<string>(data["guidance"]),
                StringComparison.OrdinalIgnoreCase);
            Assert.Null(outcome.Result);
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Form_rename_rejects_a_case_variant_sidecar_that_appears_after_capture_on_a_case_sensitive_file_system()
    {
        var sourceRoot = Directory.CreateTempSubdirectory(
            "vba-ls-rename-case-variant-sidecar-race-").FullName;
        try
        {
            var sourcePath = Path.Combine(sourceRoot, "Dialog.frm");
            var appearingSidecarPath = Path.Combine(
                sourceRoot,
                "DIALOG.FRX");
            var text = string.Join('\n', [
                "VERSION 5.00",
                "Begin VB.Form Dialog",
                "   Picture = \"Dialog.frx\":0000",
                "End",
                "Attribute VB_Name = \"Dialog\""
            ]);
            File.WriteAllText(sourcePath, text);
            var uri = new Uri(sourcePath).AbsoluteUri;
            var fileSystem = new AppearingCaseVariantSidecarFileSystem(
                appearingSidecarPath);
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()),
                NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
                NullVbaDocumentAnalysisBuildObserver.Instance,
                NullVbaProjectSnapshotBuildObserver.Instance,
                fileSystem);
            workspace.OpenDocument(uri, version: 1, text);
            await using var output = new MemoryStream();
            var clientCapabilities = new VbaLspClientCapabilityState();
            clientCapabilities.Update(new JsonObject
            {
                ["capabilities"] = new JsonObject
                {
                    ["workspace"] = new JsonObject
                    {
                        ["workspaceEdit"] = new JsonObject
                        {
                            ["documentChanges"] = true,
                            ["resourceOperations"] = new JsonArray("rename")
                        }
                    }
                }
            });
            var executor = new VbaLspRequestExecution(
                new LspMessageTransport(Stream.Null, output),
                workspace,
                clientCapabilities: clientCapabilities);
            var parameters = CreateRenameParameters();
            parameters["textDocument"]!["uri"] = uri;
            parameters["position"]!["line"] = 4;
            parameters["position"]!["character"] =
                "Attribute VB_Name = \"".Length;
            parameters["newName"] = "DialogView";
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "textDocument/rename",
                ["params"] = parameters
            };

            var captured = executor.Capture(request, CancellationToken.None);
            fileSystem.RevealSidecar();

            var outcome = captured.Execute(CancellationToken.None);
            var data = Assert.IsAssignableFrom<
                IReadOnlyDictionary<string, object?>>(outcome.ErrorData);

            Assert.Equal(-32803, outcome.ErrorCode);
            Assert.Equal("resourceOperationConflict", data["reason"]);
            Assert.Equal("sidecarConflict", data["condition"]);
            Assert.Equal(
                appearingSidecarPath,
                Assert.IsType<string>(data["path"]),
                ignoreCase: false);
            Assert.Null(outcome.Result);
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Form_rename_rejects_a_request_start_case_variant_sidecar_that_disappears_before_preflight_on_a_case_sensitive_file_system()
    {
        var sourceRoot = Directory.CreateTempSubdirectory(
            "vba-ls-rename-disappearing-case-variant-sidecar-").FullName;
        try
        {
            var sourcePath = Path.Combine(sourceRoot, "Dialog.frm");
            var sidecarPath = Path.Combine(sourceRoot, "Dialog.frx");
            var disappearingSidecarPath = Path.Combine(
                sourceRoot,
                "DIALOG.FRX");
            var text = string.Join('\n', [
                "VERSION 5.00",
                "Begin VB.Form Dialog",
                "   Picture = \"Dialog.frx\":0000",
                "End",
                "Attribute VB_Name = \"Dialog\""
            ]);
            File.WriteAllText(sourcePath, text);
            var uri = new Uri(sourcePath).AbsoluteUri;
            var fileSystem =
                new DisappearingCaseVariantSidecarFileSystem(
                    sidecarPath,
                    disappearingSidecarPath);
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()),
                NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
                NullVbaDocumentAnalysisBuildObserver.Instance,
                NullVbaProjectSnapshotBuildObserver.Instance,
                fileSystem);
            workspace.OpenDocument(uri, version: 1, text);
            await using var output = new MemoryStream();
            var clientCapabilities = new VbaLspClientCapabilityState();
            clientCapabilities.Update(new JsonObject
            {
                ["capabilities"] = new JsonObject
                {
                    ["workspace"] = new JsonObject
                    {
                        ["workspaceEdit"] = new JsonObject
                        {
                            ["documentChanges"] = true,
                            ["resourceOperations"] = new JsonArray("rename")
                        }
                    }
                }
            });
            var executor = new VbaLspRequestExecution(
                new LspMessageTransport(Stream.Null, output),
                workspace,
                clientCapabilities: clientCapabilities);
            var parameters = CreateRenameParameters();
            parameters["textDocument"]!["uri"] = uri;
            parameters["position"]!["line"] = 4;
            parameters["position"]!["character"] =
                "Attribute VB_Name = \"".Length;
            parameters["newName"] = "DialogView";
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "textDocument/rename",
                ["params"] = parameters
            };

            var captured = executor.Capture(request, CancellationToken.None);
            fileSystem.HideCaseVariant();

            var outcome = captured.Execute(CancellationToken.None);
            var data = Assert.IsAssignableFrom<
                IReadOnlyDictionary<string, object?>>(outcome.ErrorData);

            Assert.Equal(-32803, outcome.ErrorCode);
            Assert.Equal("resourceOperationConflict", data["reason"]);
            Assert.Equal("sidecarConflict", data["condition"]);
            Assert.Equal(
                disappearingSidecarPath,
                Assert.IsType<string>(data["path"]),
                ignoreCase: false);
            Assert.Null(outcome.Result);
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task File_following_form_rename_reports_sidecar_conflict_when_frx_becomes_unreadable()
    {
        var sourceRoot = Directory.CreateTempSubdirectory(
            "vba-ls-rename-sidecar-unreadable-").FullName;
        try
        {
            var sourcePath = Path.Combine(sourceRoot, "Dialog.frm");
            var sidecarPath = Path.Combine(sourceRoot, "Dialog.frx");
            var text = string.Join('\n', [
                "VERSION 5.00",
                "Begin VB.Form Dialog",
                "End",
                "Attribute VB_Name = \"Dialog\""
            ]);
            File.WriteAllText(sourcePath, text);
            File.WriteAllBytes(sidecarPath, [0x01, 0x02, 0x03]);
            var uri = new Uri(sourcePath).AbsoluteUri;
            var fileSystem = new UnreadableSecondSidecarReadFileSystem(
                sidecarPath);
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()),
                NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
                NullVbaDocumentAnalysisBuildObserver.Instance,
                NullVbaProjectSnapshotBuildObserver.Instance,
                fileSystem);
            workspace.OpenDocument(uri, version: 1, text);
            await using var output = new MemoryStream();
            var clientCapabilities = new VbaLspClientCapabilityState();
            clientCapabilities.Update(new JsonObject
            {
                ["capabilities"] = new JsonObject
                {
                    ["workspace"] = new JsonObject
                    {
                        ["workspaceEdit"] = new JsonObject
                        {
                            ["documentChanges"] = true,
                            ["resourceOperations"] = new JsonArray("rename")
                        }
                    }
                }
            });
            var executor = new VbaLspRequestExecution(
                new LspMessageTransport(Stream.Null, output),
                workspace,
                clientCapabilities: clientCapabilities);
            var parameters = CreateRenameParameters();
            parameters["textDocument"]!["uri"] = uri;
            parameters["position"]!["line"] = 3;
            parameters["position"]!["character"] =
                "Attribute VB_Name = \"".Length;
            parameters["newName"] = "DialogView";
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "textDocument/rename",
                ["params"] = parameters
            };

            var captured = executor.Capture(request, CancellationToken.None);
            var outcome = captured.Execute(CancellationToken.None);
            var data = Assert.IsAssignableFrom<
                IReadOnlyDictionary<string, object?>>(outcome.ErrorData);

            Assert.Equal(-32803, outcome.ErrorCode);
            Assert.Equal("resourceOperationConflict", data["reason"]);
            Assert.Equal("sidecarConflict", data["condition"]);
            Assert.Equal(
                sidecarPath,
                Assert.IsType<string>(data["path"]),
                ignoreCase: true);
            Assert.Contains(
                "retry",
                Assert.IsType<string>(data["guidance"]),
                StringComparison.OrdinalIgnoreCase);
            Assert.Null(outcome.Result);
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task File_following_rename_rechecks_source_revision_after_preflight()
    {
        var sourceRoot = Directory.CreateTempSubdirectory(
            "vba-ls-rename-final-source-fence-").FullName;
        BlockingSecondSourceReadFileSystem? fileSystem = null;
        try
        {
            var sourcePath = Path.Combine(sourceRoot, "InvoiceModule.bas");
            const string text =
                "Attribute VB_Name = \"InvoiceModule\"\n"
                + "Public Sub Run()\n"
                + "End Sub";
            File.WriteAllText(sourcePath, text);
            var uri = new Uri(sourcePath).AbsoluteUri;
            fileSystem = new BlockingSecondSourceReadFileSystem(sourcePath);
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()),
                NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
                NullVbaDocumentAnalysisBuildObserver.Instance,
                NullVbaProjectSnapshotBuildObserver.Instance,
                fileSystem);
            workspace.OpenDocument(uri, version: 1, text);
            await using var output = new MemoryStream();
            var clientCapabilities = new VbaLspClientCapabilityState();
            clientCapabilities.Update(new JsonObject
            {
                ["capabilities"] = new JsonObject
                {
                    ["workspace"] = new JsonObject
                    {
                        ["workspaceEdit"] = new JsonObject
                        {
                            ["documentChanges"] = true,
                            ["resourceOperations"] = new JsonArray("rename")
                        }
                    }
                }
            });
            var executor = new VbaLspRequestExecution(
                new LspMessageTransport(Stream.Null, output),
                workspace,
                clientCapabilities: clientCapabilities);
            var parameters = CreateRenameParameters();
            parameters["textDocument"]!["uri"] = uri;
            parameters["position"]!["line"] = 0;
            parameters["position"]!["character"] =
                "Attribute VB_Name = \"".Length;
            parameters["newName"] = "BillingModule";
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "textDocument/rename",
                ["params"] = parameters
            };

            var captured = executor.Capture(request, CancellationToken.None);
            var execution = Task.Run(
                () => captured.Execute(CancellationToken.None));
            await fileSystem.SecondReadStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(workspace.ChangeDocument(
                uri,
                version: 2,
                text + "\n' changed during preflight"));
            fileSystem.ReleaseSecondRead();

            var outcome = await execution.WaitAsync(TimeSpan.FromSeconds(5));
            var data = Assert.IsAssignableFrom<
                IReadOnlyDictionary<string, object?>>(outcome.ErrorData);
            Assert.Equal(-32803, outcome.ErrorCode);
            Assert.Equal("resourceOperationConflict", data["reason"]);
            Assert.Equal("sourceChanged", data["condition"]);
            Assert.Null(outcome.Result);
        }
        finally
        {
            fileSystem?.ReleaseSecondRead();
            Directory.Delete(sourceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Case_only_file_following_rename_rejects_a_distinct_case_variant_destination_peer()
    {
        var sourceRoot = Directory.CreateTempSubdirectory(
            "vba-ls-rename-case-variant-peer-").FullName;
        try
        {
            var sourcePath = Path.Combine(sourceRoot, "Dialog.bas");
            var destinationPeerPath = Path.Combine(sourceRoot, "dialog.bas");
            const string text = "Attribute VB_Name = \"Dialog\"";
            File.WriteAllText(sourcePath, text);
            var uri = new Uri(sourcePath).AbsoluteUri;
            var fileSystem = new CaseSensitiveDestinationPeerFileSystem(
                destinationPeerPath);
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()),
                NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
                NullVbaDocumentAnalysisBuildObserver.Instance,
                NullVbaProjectSnapshotBuildObserver.Instance,
                fileSystem);
            workspace.OpenDocument(uri, version: 1, text);
            await using var output = new MemoryStream();
            var clientCapabilities = new VbaLspClientCapabilityState();
            clientCapabilities.Update(new JsonObject
            {
                ["capabilities"] = new JsonObject
                {
                    ["workspace"] = new JsonObject
                    {
                        ["workspaceEdit"] = new JsonObject
                        {
                            ["documentChanges"] = true,
                            ["resourceOperations"] = new JsonArray("rename")
                        }
                    }
                }
            });
            var executor = new VbaLspRequestExecution(
                new LspMessageTransport(Stream.Null, output),
                workspace,
                clientCapabilities: clientCapabilities);
            var parameters = CreateRenameParameters();
            parameters["textDocument"]!["uri"] = uri;
            parameters["position"]!["line"] = 0;
            parameters["position"]!["character"] =
                "Attribute VB_Name = \"".Length;
            parameters["newName"] = "dialog";
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "textDocument/rename",
                ["params"] = parameters
            };

            var captured = executor.Capture(request, CancellationToken.None);
            var outcome = captured.Execute(CancellationToken.None);
            var data = Assert.IsAssignableFrom<
                IReadOnlyDictionary<string, object?>>(outcome.ErrorData);

            Assert.Equal(-32803, outcome.ErrorCode);
            Assert.Equal("resourceOperationConflict", data["reason"]);
            Assert.Equal("destinationExists", data["condition"]);
            Assert.Equal(
                destinationPeerPath,
                Assert.IsType<string>(data["path"]),
                ignoreCase: false);
            Assert.Null(outcome.Result);
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Rename_rejects_a_cold_module_that_changes_between_semantic_capture_and_file_evidence_capture()
    {
        var sourceRoot = Directory.CreateTempSubdirectory(
            "vba-ls-rename-cold-evidence-race-").FullName;
        try
        {
            var modulePath = Path.Combine(sourceRoot, "InvoiceModule.bas");
            const string originalModuleText =
                "Attribute VB_Name = \"InvoiceModule\"\n"
                + "Public Sub Run()\n"
                + "End Sub";
            const string changedModuleText =
                "Attribute VB_Name = \"CurrentModule\"\n"
                + "Public Sub Run()\n"
                + "End Sub";
            Assert.Equal(originalModuleText.Length, changedModuleText.Length);
            File.WriteAllText(modulePath, originalModuleText);
            var consumerPath = Path.Combine(sourceRoot, "Consumer.bas");
            const string consumerText =
                "Attribute VB_Name = \"Consumer\"\n"
                + "Public Sub Execute()\n"
                + "    InvoiceModule.Run\n"
                + "End Sub";
            File.WriteAllText(consumerPath, consumerText);
            var consumerUri = new Uri(consumerPath).AbsoluteUri;
            var fileSystem = new ChangingColdSourceFileSystem(
                modulePath,
                originalModuleText,
                changedModuleText);
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()),
                NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
                NullVbaDocumentAnalysisBuildObserver.Instance,
                NullVbaProjectSnapshotBuildObserver.Instance,
                fileSystem);
            workspace.OpenDocument(consumerUri, version: 1, consumerText);
            await using var output = new MemoryStream();
            var clientCapabilities = new VbaLspClientCapabilityState();
            clientCapabilities.Update(new JsonObject
            {
                ["capabilities"] = new JsonObject
                {
                    ["workspace"] = new JsonObject
                    {
                        ["workspaceEdit"] = new JsonObject
                        {
                            ["documentChanges"] = true,
                            ["resourceOperations"] = new JsonArray("rename")
                        }
                    }
                }
            });
            var executor = new VbaLspRequestExecution(
                new LspMessageTransport(Stream.Null, output),
                workspace,
                clientCapabilities: clientCapabilities);
            var parameters = CreateRenameParameters();
            parameters["textDocument"]!["uri"] = consumerUri;
            parameters["position"]!["line"] = 2;
            parameters["position"]!["character"] = 4;
            parameters["newName"] = "BillingModule";
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "textDocument/rename",
                ["params"] = parameters
            };

            var captured = executor.Capture(request, CancellationToken.None);
            var outcome = captured.Execute(CancellationToken.None);
            var data = Assert.IsAssignableFrom<
                IReadOnlyDictionary<string, object?>>(outcome.ErrorData);

            Assert.Equal(-32803, outcome.ErrorCode);
            Assert.Equal("resourceOperationConflict", data["reason"]);
            Assert.Equal("sourceChanged", data["condition"]);
            Assert.Equal(
                modulePath,
                Assert.IsType<string>(data["path"]),
                ignoreCase: true);
            Assert.Contains(
                "retry",
                Assert.IsType<string>(data["guidance"]),
                StringComparison.OrdinalIgnoreCase);
            Assert.Null(outcome.Result);
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Rename_fence_observes_a_pending_newer_source_analysis()
    {
        const string uri = "file:///C:/work/Worker.bas";
        const string text =
            "Attribute VB_Name = \"Worker\"\n"
            + "Public Function BuildValue() As Long\n"
            + "    BuildValue = 1\n"
            + "End Function";
        var observer = new BlockingNextDocumentAnalysisBuildObserver();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            observer);
        workspace.OpenDocument(uri, version: 1, text);
        await using var output = new MemoryStream();
        var executor = new VbaLspRequestExecution(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        var parameters = CreateRenameParameters();
        parameters["textDocument"]!["uri"] = uri;
        parameters["position"]!["line"] = 1;
        parameters["position"]!["character"] = "Public Function ".Length;
        parameters["newName"] = "CreateValue";
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "textDocument/rename",
            ["params"] = parameters
        };

        var captured = executor.Capture(request, CancellationToken.None);
        observer.BlockNextBuild();
        var changeTask = Task.Run(() => workspace.ChangeDocument(
            uri,
            version: 2,
            text + "\n' pending change"));
        await observer.BuildStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(10));
        try
        {
            var outcome = captured.Execute(CancellationToken.None);
            var data = Assert.IsAssignableFrom<
                IReadOnlyDictionary<string, object?>>(outcome.ErrorData);

            Assert.Equal(-32803, outcome.ErrorCode);
            Assert.Equal("resourceOperationConflict", data["reason"]);
            Assert.Equal("sourceChanged", data["condition"]);
            Assert.Null(outcome.Result);
            Assert.Equal(0, workspace.RetainedRenameSourceRevisionCount);
        }
        finally
        {
            observer.ReleaseBuild();
        }

        Assert.True(await changeTask);
    }

    [Fact]
    public async Task Rename_ignores_source_changes_outside_the_captured_project()
    {
        const string uri = "file:///C:/work/Worker.bas";
        const string text =
            "Attribute VB_Name = \"Worker\"\n"
            + "Public Function BuildValue() As Long\n"
            + "    BuildValue = 1\n"
            + "End Function";
        const string unrelatedUri = "file:///C:/unrelated/Other.bas";
        const string unrelatedText =
            "Attribute VB_Name = \"Other\"\n"
            + "Public Sub Run()\n"
            + "End Sub";
        await using var output = new MemoryStream();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()));
        workspace.OpenDocument(uri, version: 1, text);
        workspace.OpenDocument(unrelatedUri, version: 1, unrelatedText);
        var executor = new VbaLspRequestExecution(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        var parameters = CreateRenameParameters();
        parameters["textDocument"]!["uri"] = uri;
        parameters["position"]!["line"] = 1;
        parameters["position"]!["character"] = "Public Function ".Length;
        parameters["newName"] = "CreateValue";
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "textDocument/rename",
            ["params"] = parameters
        };

        var captured = executor.Capture(request, CancellationToken.None);
        Assert.True(workspace.ChangeDocument(
            unrelatedUri,
            version: 2,
            unrelatedText + "\n' unrelated change"));

        var outcome = captured.Execute(CancellationToken.None);

        Assert.Null(outcome.ErrorCode);
        Assert.NotNull(outcome.Result);
        Assert.Equal(0, workspace.RetainedSourceRevisionCount);
        Assert.Equal(0, workspace.RetainedRenameSourceRevisionCount);
    }

    [Fact]
    public async Task Rename_ignores_source_changes_owned_by_a_nested_project()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-rename-nested-boundary-").FullName;
        try
        {
            var outerSourceRoot = Path.Combine(projectRoot, "src");
            var nestedProjectRoot = Path.Combine(
                outerSourceRoot,
                "NestedProject");
            var nestedSourceRoot = Path.Combine(nestedProjectRoot, "src");
            Directory.CreateDirectory(nestedSourceRoot);
            WriteManifest(projectRoot, "OuterBook", "src");
            WriteManifest(nestedProjectRoot, "NestedBook", "src");

            var outerPath = Path.Combine(outerSourceRoot, "Outer.bas");
            var nestedPath = Path.Combine(nestedSourceRoot, "Nested.bas");
            const string outerText =
                "Attribute VB_Name = \"Outer\"\n"
                + "Public Function BuildValue() As Long\n"
                + "    BuildValue = 1\n"
                + "End Function";
            const string nestedText =
                "Attribute VB_Name = \"Nested\"\n"
                + "Public Sub RunNested()\n"
                + "End Sub";
            File.WriteAllText(outerPath, outerText);
            File.WriteAllText(nestedPath, nestedText);
            var outerUri = new Uri(outerPath).AbsoluteUri;
            var nestedUri = new Uri(nestedPath).AbsoluteUri;
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()));
            workspace.OpenDocument(outerUri, version: 1, outerText);
            workspace.OpenDocument(nestedUri, version: 1, nestedText);
            await using var output = new MemoryStream();
            var executor = new VbaLspRequestExecution(
                new LspMessageTransport(Stream.Null, output),
                workspace);
            var parameters = CreateRenameParameters();
            parameters["textDocument"]!["uri"] = outerUri;
            parameters["position"]!["line"] = 1;
            parameters["position"]!["character"] =
                "Public Function ".Length;
            parameters["newName"] = "CreateValue";
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "textDocument/rename",
                ["params"] = parameters
            };

            var captured = executor.Capture(request, CancellationToken.None);
            Assert.True(workspace.ChangeDocument(
                nestedUri,
                version: 2,
                nestedText + "\n' nested-project change"));

            var outcome = captured.Execute(CancellationToken.None);

            Assert.Null(outcome.ErrorCode);
            Assert.NotNull(outcome.Result);
            Assert.Equal(0, workspace.RetainedSourceRevisionCount);
            Assert.Equal(0, workspace.RetainedRenameSourceRevisionCount);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Rename_reports_first_cold_source_decode_failure_as_incomplete_analysis()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-rename-cold-failure-").FullName;
        try
        {
            var activePath = Path.Combine(projectRoot, "Worker.bas");
            var unreadablePath = Path.Combine(projectRoot, "Unreadable.bas");
            const string activeText =
                "Attribute VB_Name = \"Worker\"\n"
                + "Public Function BuildValue() As Long\n"
                + "    BuildValue = 1\n"
                + "End Function";
            File.WriteAllText(activePath, activeText);
            File.WriteAllBytes(unreadablePath, [0xC3, 0x28]);
            var activeUri = new Uri(activePath).AbsoluteUri;
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()),
                new DiskSourceDecoding(
                    supportsLegacyFallback: false,
                    activeCodePage: 65001));
            workspace.OpenDocument(activeUri, version: 1, activeText);
            await using var output = new MemoryStream();
            var executor = new VbaLspRequestExecution(
                new LspMessageTransport(Stream.Null, output),
                workspace);
            var parameters = CreateRenameParameters();
            parameters["textDocument"]!["uri"] = activeUri;
            parameters["position"]!["line"] = 1;
            parameters["position"]!["character"] =
                "Public Function ".Length;
            parameters["newName"] = "CreateValue";
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "textDocument/rename",
                ["params"] = parameters
            };

            var captured = executor.Capture(request, CancellationToken.None);
            var outcome = captured.Execute(CancellationToken.None);
            var data = Assert.IsAssignableFrom<
                IReadOnlyDictionary<string, object?>>(outcome.ErrorData);

            Assert.Equal(-32803, outcome.ErrorCode);
            Assert.Equal("analysisIncomplete", data["reason"]);
            Assert.Null(outcome.Result);
            Assert.Equal(0, workspace.RetainedRenameSourceRevisionCount);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Rename_allows_an_exact_no_op_before_cold_source_completeness()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-rename-no-op-before-cold-failure-").FullName;
        try
        {
            var activePath = Path.Combine(projectRoot, "Worker.bas");
            var unreadablePath = Path.Combine(projectRoot, "Unreadable.bas");
            const string activeText =
                "Attribute VB_Name = \"Worker\"\n"
                + "Public Function BuildValue() As Long\n"
                + "    BuildValue = 1\n"
                + "End Function";
            File.WriteAllText(activePath, activeText);
            File.WriteAllBytes(unreadablePath, [0xC3, 0x28]);
            var activeUri = new Uri(activePath).AbsoluteUri;
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()),
                new DiskSourceDecoding(
                    supportsLegacyFallback: false,
                    activeCodePage: 65001));
            workspace.OpenDocument(activeUri, version: 1, activeText);
            await using var output = new MemoryStream();
            var executor = new VbaLspRequestExecution(
                new LspMessageTransport(Stream.Null, output),
                workspace);
            var parameters = CreateRenameParameters();
            parameters["textDocument"]!["uri"] = activeUri;
            parameters["position"]!["line"] = 1;
            parameters["position"]!["character"] =
                "Public Function ".Length;
            parameters["newName"] = "BuildValue";
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "textDocument/rename",
                ["params"] = parameters
            };

            var captured = executor.Capture(request, CancellationToken.None);
            var outcome = captured.Execute(CancellationToken.None);

            Assert.Null(outcome.ErrorCode);
            Assert.Null(outcome.Result);
            Assert.Equal(0, workspace.RetainedRenameSourceRevisionCount);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Rename_fence_does_not_retain_changes_without_an_active_capture()
    {
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()));

        for (var index = 0; index < 16; index++)
        {
            var uri = $"file:///C:/work/Module{index}.bas";
            workspace.OpenDocument(
                uri,
                version: 1,
                $"Attribute VB_Name = \"Module{index}\"\n"
                    + $"Public Sub Run{index}()\n"
                    + "End Sub");
        }

        Assert.Equal(0, workspace.RetainedRenameSourceRevisionCount);
    }

    [Fact]
    public async Task Rename_rejects_invalid_name_before_cold_source_completeness()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-rename-invalid-before-cold-failure-").FullName;
        try
        {
            var activePath = Path.Combine(projectRoot, "Worker.bas");
            var unreadablePath = Path.Combine(projectRoot, "Unreadable.bas");
            const string activeText =
                "Attribute VB_Name = \"Worker\"\n"
                + "Public Function BuildValue() As Long\n"
                + "    BuildValue = 1\n"
                + "End Function";
            File.WriteAllText(activePath, activeText);
            File.WriteAllBytes(unreadablePath, [0xC3, 0x28]);
            var activeUri = new Uri(activePath).AbsoluteUri;
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()),
                new DiskSourceDecoding(
                    supportsLegacyFallback: false,
                    activeCodePage: 65001));
            workspace.OpenDocument(activeUri, version: 1, activeText);
            await using var output = new MemoryStream();
            var executor = new VbaLspRequestExecution(
                new LspMessageTransport(Stream.Null, output),
                workspace);
            var parameters = CreateRenameParameters();
            parameters["textDocument"]!["uri"] = activeUri;
            parameters["position"]!["line"] = 1;
            parameters["position"]!["character"] =
                "Public Function ".Length;
            parameters["newName"] = " Bad";
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "textDocument/rename",
                ["params"] = parameters
            };

            var captured = executor.Capture(request, CancellationToken.None);
            var outcome = captured.Execute(CancellationToken.None);
            var data = Assert.IsAssignableFrom<
                IReadOnlyDictionary<string, object?>>(outcome.ErrorData);

            Assert.Equal(-32803, outcome.ErrorCode);
            Assert.Equal("invalidName", data["reason"]);
            Assert.Null(outcome.Result);
            Assert.Equal(0, workspace.RetainedRenameSourceRevisionCount);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Rename_uses_unsaved_buffer_text_without_writing_the_disk_source()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-rename-buffer-").FullName;
        try
        {
            var sourcePath = Path.Combine(projectRoot, "Worker.bas");
            const string diskText =
                "Attribute VB_Name = \"Worker\"\n"
                + "Public Function DiskValue() As Long\n"
                + "End Function";
            const string bufferText =
                "Attribute VB_Name = \"Worker\"\n"
                + "Public Function BufferValue() As Long\n"
                + "    BufferValue = 1\n"
                + "End Function";
            File.WriteAllText(sourcePath, diskText);
            var uri = new Uri(sourcePath).AbsoluteUri;
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()));
            workspace.OpenDocument(uri, version: 1, bufferText);
            await using var output = new MemoryStream();
            var executor = new VbaLspRequestExecution(
                new LspMessageTransport(Stream.Null, output),
                workspace);
            var parameters = CreateRenameParameters();
            parameters["textDocument"]!["uri"] = uri;
            parameters["position"]!["line"] = 1;
            parameters["position"]!["character"] =
                "Public Function ".Length;
            parameters["newName"] = "RenamedValue";
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "textDocument/rename",
                ["params"] = parameters
            };

            var captured = executor.Capture(request, CancellationToken.None);
            var outcome = captured.Execute(CancellationToken.None);
            var result = JsonSerializer.SerializeToNode(outcome.Result)!
                .AsObject();
            var edits = result["changes"]![uri]!.AsArray();

            Assert.Null(outcome.ErrorCode);
            Assert.Equal(2, edits.Count);
            Assert.All(edits, edit => Assert.Equal(
                "RenamedValue",
                edit!["newText"]!.GetValue<string>()));
            Assert.Equal(diskText, File.ReadAllText(sourcePath));
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
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

    [Fact]
    public async Task Scheduler_abort_releases_an_undispatched_rename_capture()
    {
        const string uri = "file:///C:/work/Worker.bas";
        const string text =
            "Attribute VB_Name = \"Worker\"\n"
            + "Public Function BuildValue() As Long\n"
            + "    BuildValue = 1\n"
            + "End Function";
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()));
        workspace.OpenDocument(uri, version: 1, text);
        await using var output = new MemoryStream();
        var executor = new VbaLspRequestExecution(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        await using var scheduler = new VbaInteractiveWorkScheduler(
            options: new VbaInteractiveWorkSchedulerOptions(
                CoalesceSupersededMutations: true,
                MaxConcurrentReads: 1,
                MaxConcurrentBulkReads: 1));
        var blockerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = scheduler.AdmitRequest(
            requestId: null,
            method: "textDocument/rename",
            capture: static _ => true,
            async (_, cancellationToken) =>
            {
                blockerStarted.TrySetResult();
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);
            });
        await blockerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var parameters = CreateRenameParameters();
        parameters["textDocument"]!["uri"] = uri;
        parameters["position"]!["line"] = 1;
        parameters["position"]!["character"] = "Public Function ".Length;
        parameters["newName"] = "CreateValue";
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "textDocument/rename",
            ["params"] = parameters
        };
        var renameCaptured = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var rename = scheduler.AdmitRequest(
            new VbaLspRequestId(VbaLspRequestIdKind.Number, "1"),
            "textDocument/rename",
            cancellationToken =>
            {
                var captured = executor.Capture(request, cancellationToken);
                renameCaptured.TrySetResult();
                return captured;
            },
            (captured, cancellationToken, releaseCancellationOwnership) =>
                executor.ExecuteAsync(
                    captured,
                    cancellationToken,
                    CancellationToken.None,
                    releaseCancellationOwnership));
        await renameCaptured.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(workspace.ChangeDocument(
            uri,
            version: 2,
            text + "\n' retained until capture release"));
        Assert.Equal(1, workspace.RetainedRenameSourceRevisionCount);

        await scheduler.StopAsync(VbaInteractiveStopReason.Abort)
            .WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => blocker.Completion);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => rename.Completion);
        Assert.Equal(0, workspace.RetainedSourceRevisionCount);
        Assert.Equal(0, workspace.RetainedRenameSourceRevisionCount);
    }

    private static JsonObject CreateTextDocumentParameters()
        => new()
        {
            ["textDocument"] = new JsonObject
            {
                ["uri"] = "file:///C:/work/Worker.bas"
            }
        };

    private static void WriteManifest(
        string projectRoot,
        string documentName,
        string sourcePath)
        => File.WriteAllText(
            Path.Combine(projectRoot, "vba-project.json"),
            $$"""
            {
              "schemaVersion": 1,
              "projectName": "{{documentName}}Project",
              "primaryDocument": "{{documentName}}",
              "documents": {
                "{{documentName}}": {
                  "kind": "excel",
                  "sourcePath": "{{sourcePath}}",
                  "templatePath": "{{documentName}}.xlsm",
                  "binPath": "bin/{{documentName}}.xlsm",
                  "publishPath": "publish/{{documentName}}.xlsm",
                  "commonModules": [],
                  "references": []
                }
              }
            }
            """);

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

    private sealed class BlockingSecondSourceReadFileSystem(
        string sourcePath)
        : IVbaProjectFileSystem
    {
        private readonly string sourcePath = Path.GetFullPath(sourcePath);
        private readonly TaskCompletionSource releaseSecondRead = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int sourceReadCount;

        public TaskCompletionSource SecondReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool FileExists(string path)
            => File.Exists(path);

        public bool DirectoryExists(string path)
            => Directory.Exists(path);

        public IEnumerable<string> EnumerateSourceFiles(
            string rootPath,
            string searchPattern,
            SearchOption searchOption)
            => Directory.EnumerateFiles(rootPath, searchPattern, searchOption);

        public bool TryGetSourceMetadata(
            string path,
            out VbaProjectSourceFileMetadata metadata)
        {
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists)
            {
                metadata = default;
                return false;
            }

            metadata = new VbaProjectSourceFileMetadata(
                fileInfo.Length,
                fileInfo.LastWriteTimeUtc.Ticks);
            return true;
        }

        public string ReadManifestText(string path)
            => File.ReadAllText(path);

        public byte[] ReadSourceBytes(string path)
        {
            if (Path.GetFullPath(path).Equals(
                    sourcePath,
                    StringComparison.OrdinalIgnoreCase)
                && Interlocked.Increment(ref sourceReadCount) == 2)
            {
                SecondReadStarted.TrySetResult();
                releaseSecondRead.Task.GetAwaiter().GetResult();
            }

            return File.ReadAllBytes(path);
        }

        public void ReleaseSecondRead()
            => releaseSecondRead.TrySetResult();
    }

    private sealed class CaseSensitiveDestinationPeerFileSystem(
        string destinationPeerPath)
        : IVbaProjectFileSystem
    {
        private readonly string destinationPeerPath =
            Path.GetFullPath(destinationPeerPath);

        public bool FileExists(string path)
            => File.Exists(path);

        public bool DirectoryExists(string path)
            => Directory.Exists(path);

        public IEnumerable<string> EnumerateSourceFiles(
            string rootPath,
            string searchPattern,
            SearchOption searchOption)
            => Directory
                .EnumerateFiles(rootPath, searchPattern, searchOption)
                .Append(destinationPeerPath);

        public bool TryGetSourceMetadata(
            string path,
            out VbaProjectSourceFileMetadata metadata)
        {
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists)
            {
                metadata = default;
                return false;
            }

            metadata = new VbaProjectSourceFileMetadata(
                fileInfo.Length,
                fileInfo.LastWriteTimeUtc.Ticks);
            return true;
        }

        public string ReadManifestText(string path)
            => File.ReadAllText(path);

        public byte[] ReadSourceBytes(string path)
            => File.ReadAllBytes(path);

        public bool PathsReferToSameEntry(string left, string right)
            => Path.GetFullPath(left).Equals(
                Path.GetFullPath(right),
                StringComparison.Ordinal);
    }

    private sealed class AppearingCaseVariantSidecarFileSystem(
        string sidecarPath)
        : IVbaProjectFileSystem
    {
        private readonly string sidecarPath = Path.GetFullPath(sidecarPath);
        private bool sidecarVisible;

        public bool FileExists(string path)
            => IsSidecar(path)
                ? sidecarVisible
                : File.Exists(path);

        public bool DirectoryExists(string path)
            => Directory.Exists(path);

        public IEnumerable<string> EnumerateSourceFiles(
            string rootPath,
            string searchPattern,
            SearchOption searchOption)
        {
            var files = Directory.EnumerateFiles(
                rootPath,
                searchPattern,
                searchOption);
            if (!sidecarVisible
                || searchPattern != "*"
                    && !Path.GetFileName(sidecarPath).Equals(
                        searchPattern,
                        StringComparison.Ordinal))
            {
                return files;
            }

            return files.Append(sidecarPath);
        }

        public bool TryGetSourceMetadata(
            string path,
            out VbaProjectSourceFileMetadata metadata)
        {
            if (IsSidecar(path) && sidecarVisible)
            {
                metadata = new VbaProjectSourceFileMetadata(
                    Length: 3,
                    LastWriteTimeUtcTicks: 1);
                return true;
            }

            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists)
            {
                metadata = default;
                return false;
            }

            metadata = new VbaProjectSourceFileMetadata(
                fileInfo.Length,
                fileInfo.LastWriteTimeUtc.Ticks);
            return true;
        }

        public string ReadManifestText(string path)
            => File.ReadAllText(path);

        public byte[] ReadSourceBytes(string path)
            => IsSidecar(path) && sidecarVisible
                ? [0x01, 0x02, 0x03]
                : File.ReadAllBytes(path);

        public bool PathsReferToSameEntry(string left, string right)
            => Path.GetFullPath(left).Equals(
                Path.GetFullPath(right),
                StringComparison.Ordinal);

        public void RevealSidecar()
            => sidecarVisible = true;

        private bool IsSidecar(string path)
            => Path.GetFullPath(path).Equals(
                sidecarPath,
                StringComparison.Ordinal);
    }

    private sealed class DisappearingCaseVariantSidecarFileSystem(
        string sidecarPath,
        string caseVariantSidecarPath)
        : IVbaProjectFileSystem
    {
        private readonly string sidecarPath = Path.GetFullPath(sidecarPath);
        private readonly string caseVariantSidecarPath = Path.GetFullPath(
            caseVariantSidecarPath);
        private bool caseVariantVisible = true;

        public bool FileExists(string path)
            => IsSidecar(path)
                || IsCaseVariantSidecar(path) && caseVariantVisible
                || File.Exists(path);

        public bool DirectoryExists(string path)
            => Directory.Exists(path);

        public IEnumerable<string> EnumerateSourceFiles(
            string rootPath,
            string searchPattern,
            SearchOption searchOption)
        {
            var files = Directory.EnumerateFiles(
                rootPath,
                searchPattern,
                searchOption);
            if (searchPattern != "*")
            {
                return files;
            }

            var sidecars = new List<string> { sidecarPath };
            if (caseVariantVisible)
            {
                sidecars.Add(caseVariantSidecarPath);
            }

            return files.Concat(sidecars);
        }

        public bool TryGetSourceMetadata(
            string path,
            out VbaProjectSourceFileMetadata metadata)
        {
            if (IsSidecar(path)
                || IsCaseVariantSidecar(path) && caseVariantVisible)
            {
                metadata = new VbaProjectSourceFileMetadata(
                    Length: 3,
                    LastWriteTimeUtcTicks: 1);
                return true;
            }

            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists)
            {
                metadata = default;
                return false;
            }

            metadata = new VbaProjectSourceFileMetadata(
                fileInfo.Length,
                fileInfo.LastWriteTimeUtc.Ticks);
            return true;
        }

        public string ReadManifestText(string path)
            => File.ReadAllText(path);

        public byte[] ReadSourceBytes(string path)
            => IsSidecar(path)
                ? [0x01, 0x02, 0x03]
                : IsCaseVariantSidecar(path) && caseVariantVisible
                    ? [0x04, 0x05, 0x06]
                    : File.ReadAllBytes(path);

        public bool PathsReferToSameEntry(string left, string right)
            => Path.GetFullPath(left).Equals(
                Path.GetFullPath(right),
                StringComparison.Ordinal);

        public void HideCaseVariant()
            => caseVariantVisible = false;

        private bool IsSidecar(string path)
            => Path.GetFullPath(path).Equals(
                sidecarPath,
                StringComparison.Ordinal);

        private bool IsCaseVariantSidecar(string path)
            => Path.GetFullPath(path).Equals(
                caseVariantSidecarPath,
                StringComparison.Ordinal);
    }

    private sealed class UnreadableSecondSidecarReadFileSystem(
        string sidecarPath)
        : IVbaProjectFileSystem
    {
        private readonly string sidecarPath = Path.GetFullPath(sidecarPath);
        private int sidecarReadCount;

        public bool FileExists(string path)
            => File.Exists(path);

        public bool DirectoryExists(string path)
            => Directory.Exists(path);

        public IEnumerable<string> EnumerateSourceFiles(
            string rootPath,
            string searchPattern,
            SearchOption searchOption)
            => Directory.EnumerateFiles(rootPath, searchPattern, searchOption);

        public bool TryGetSourceMetadata(
            string path,
            out VbaProjectSourceFileMetadata metadata)
        {
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists)
            {
                metadata = default;
                return false;
            }

            metadata = new VbaProjectSourceFileMetadata(
                fileInfo.Length,
                fileInfo.LastWriteTimeUtc.Ticks);
            return true;
        }

        public string ReadManifestText(string path)
            => File.ReadAllText(path);

        public byte[] ReadSourceBytes(string path)
        {
            if (Path.GetFullPath(path).Equals(
                    sidecarPath,
                    StringComparison.OrdinalIgnoreCase)
                && Interlocked.Increment(ref sidecarReadCount) == 2)
            {
                throw new UnauthorizedAccessException(
                    "The form sidecar became unreadable.");
            }

            return File.ReadAllBytes(path);
        }
    }

    private sealed class ChangingColdSourceFileSystem(
        string modulePath,
        string originalModuleText,
        string changedModuleText)
        : IVbaProjectFileSystem
    {
        private readonly byte[] changedModuleBytes =
            Encoding.UTF8.GetBytes(changedModuleText);
        private readonly string modulePath = Path.GetFullPath(modulePath);
        private readonly byte[] originalModuleBytes =
            Encoding.UTF8.GetBytes(originalModuleText);
        private int moduleReadCount;

        public bool FileExists(string path)
            => File.Exists(path);

        public bool DirectoryExists(string path)
            => Directory.Exists(path);

        public IEnumerable<string> EnumerateSourceFiles(
            string rootPath,
            string searchPattern,
            SearchOption searchOption)
            => Directory.EnumerateFiles(rootPath, searchPattern, searchOption);

        public bool TryGetSourceMetadata(
            string path,
            out VbaProjectSourceFileMetadata metadata)
        {
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists)
            {
                metadata = default;
                return false;
            }

            metadata = new VbaProjectSourceFileMetadata(
                fileInfo.Length,
                fileInfo.LastWriteTimeUtc.Ticks);
            return true;
        }

        public string ReadManifestText(string path)
            => File.ReadAllText(path);

        public byte[] ReadSourceBytes(string path)
        {
            if (!Path.GetFullPath(path).Equals(
                    modulePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return File.ReadAllBytes(path);
            }

            return Interlocked.Increment(ref moduleReadCount) == 1
                ? originalModuleBytes.ToArray()
                : changedModuleBytes.ToArray();
        }
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

    private sealed class BlockingNextDocumentAnalysisBuildObserver
        : IVbaDocumentAnalysisBuildObserver
    {
        private readonly TaskCompletionSource releaseBuild = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int blockNext;

        public TaskCompletionSource BuildStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void BlockNextBuild()
            => Interlocked.Exchange(ref blockNext, 1);

        public void ReleaseBuild()
            => releaseBuild.TrySetResult();

        public void BeforeBuild(
            VbaDocumentAnalysisBuildContext context,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref blockNext, 0) == 0)
            {
                return;
            }

            BuildStarted.TrySetResult();
            releaseBuild.Task.Wait(cancellationToken);
        }
    }
}
