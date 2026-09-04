using System.Text.Json;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class IntrinsicHostEventLanguageServerProcessTests
{
    [Fact]
    public async Task Environment_catalog_does_not_own_source_form_module_rename()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-host-unassociated-form-rename-").FullName;
        try
        {
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            Directory.CreateDirectory(sourceRoot);
            var sourceTemplate = Path.Combine(sourceRoot, "Book1.xlsm");
            var templateBytes = VbaProjectIdentityWorkbookFixture.Create(
                "ContainingProject",
                1252);
            File.WriteAllBytes(sourceTemplate, templateBytes);
            WriteProjectManifest(projectRoot);
            var sourcePath = Path.Combine(sourceRoot, "Dialog.frm");
            var sidecarPath = Path.Combine(sourceRoot, "Dialog.frx");
            var uri = new Uri(sourcePath).AbsoluteUri;
            var text = string.Join('\n', [
                "VERSION 5.00",
                "Begin VB.Form Dialog",
                "End",
                "Attribute VB_Name = \"Dialog\""
            ]);
            File.WriteAllText(sourcePath, text);
            File.WriteAllBytes(sidecarPath, [0x01, 0x02, 0x03]);

            await using var process = await LanguageServerProcessHarness.StartAsync();
            await process.InitializeAsync(new
            {
                workspace = new
                {
                    workspaceEdit = new
                    {
                        documentChanges = true,
                        resourceOperations = new[] { "rename" }
                    }
                }
            });
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.WaitForDiagnosticsAsync(uri);
            await process.SendNotificationAsync(
                "vba/intrinsicHostEventCatalog",
                CreateCatalogNotification(
                    "Initialize",
                    [],
                    "Initializes the form."));
            await FenceSnapshotAsync(process, uri, text, version: 2);

            var rename = await process.SendRequestAsync(
                2,
                "textDocument/rename",
                new
                {
                    textDocument = new { uri },
                    position = new
                    {
                        line = 3,
                        character = "Attribute VB_Name = \"".Length
                    },
                    newName = "DialogView"
                });

            Assert.False(
                rename.TryGetProperty("error", out var error),
                error.ToString());
            var documentChanges = rename
                .GetProperty("result")
                .GetProperty("documentChanges")
                .EnumerateArray()
                .ToArray();
            Assert.Equal(3, documentChanges.Length);
            Assert.Equal(uri, documentChanges[1].GetProperty("oldUri").GetString());
            Assert.Equal(
                new Uri(Path.Combine(sourceRoot, "DialogView.frm")).AbsoluteUri,
                documentChanges[1].GetProperty("newUri").GetString());
            Assert.Equal(
                new Uri(sidecarPath).AbsoluteUri,
                documentChanges[2].GetProperty("oldUri").GetString());
            Assert.Equal(
                new Uri(Path.Combine(sourceRoot, "DialogView.frx")).AbsoluteUri,
                documentChanges[2].GetProperty("newUri").GetString());

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Manifest_form_rename_fails_closed_when_its_sidecar_is_displaced()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-displaced-form-sidecar-rename-").FullName;
        try
        {
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            var displacedRoot = Path.Combine(sourceRoot, "displaced");
            Directory.CreateDirectory(displacedRoot);
            var sourceTemplate = Path.Combine(sourceRoot, "Book1.xlsm");
            var templateBytes = VbaProjectIdentityWorkbookFixture.Create(
                "ContainingProject",
                1252);
            File.WriteAllBytes(sourceTemplate, templateBytes);
            WriteProjectManifest(projectRoot);
            var sourcePath = Path.Combine(sourceRoot, "Dialog.frm");
            var displacedSidecarPath = Path.Combine(displacedRoot, "Dialog.frx");
            var uri = new Uri(sourcePath).AbsoluteUri;
            var text = string.Join('\n', [
                "VERSION 5.00",
                "Begin VB.Form Dialog",
                "   Picture = \"Dialog.frx\":0000",
                "End",
                "Attribute VB_Name = \"Dialog\""
            ]);
            File.WriteAllText(sourcePath, text);
            File.WriteAllBytes(displacedSidecarPath, [0x01, 0x02, 0x03]);

            await using var process = await LanguageServerProcessHarness.StartAsync();
            await process.InitializeAsync(new
            {
                workspace = new
                {
                    workspaceEdit = new
                    {
                        documentChanges = true,
                        resourceOperations = new[] { "rename" }
                    }
                }
            });
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.WaitForDiagnosticsAsync(uri);
            await process.SendNotificationAsync(
                "vba/intrinsicHostEventCatalog",
                CreateCatalogNotification(
                    "Initialize",
                    [],
                    "Initializes the form."));
            await FenceSnapshotAsync(process, uri, text, version: 2);

            var rename = await process.SendRequestAsync(
                2,
                "textDocument/rename",
                new
                {
                    textDocument = new { uri },
                    position = new
                    {
                        line = 4,
                        character = "Attribute VB_Name = \"".Length
                    },
                    newName = "DialogView"
                });

            Assert.False(rename.TryGetProperty("result", out _));
            var data = rename.GetProperty("error").GetProperty("data");
            Assert.Equal(
                "resourceOperationConflict",
                data.GetProperty("reason").GetString());
            Assert.Equal("sidecarConflict", data.GetProperty("condition").GetString());
            Assert.Equal(
                displacedSidecarPath,
                data.GetProperty("path").GetString(),
                ignoreCase: true);
            Assert.Contains(
                "beside the form",
                data.GetProperty("guidance").GetString(),
                StringComparison.OrdinalIgnoreCase);

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Manifest_deliberate_basename_form_rename_fails_when_its_sidecar_is_multiply_identified()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-duplicate-form-sidecar-rename-").FullName;
        try
        {
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            var duplicateRoot = Path.Combine(sourceRoot, "duplicate");
            Directory.CreateDirectory(duplicateRoot);
            var sourceTemplate = Path.Combine(sourceRoot, "Book1.xlsm");
            var templateBytes = VbaProjectIdentityWorkbookFixture.Create(
                "ContainingProject",
                1252);
            File.WriteAllBytes(sourceTemplate, templateBytes);
            WriteProjectManifest(projectRoot);
            var sourcePath = Path.Combine(sourceRoot, "LegacyDialog.frm");
            var sidecarPath = Path.Combine(sourceRoot, "LegacyDialog.frx");
            var duplicateSidecarPath = Path.Combine(
                duplicateRoot,
                "LegacyDialog.frx");
            var uri = new Uri(sourcePath).AbsoluteUri;
            var text = string.Join('\n', [
                "VERSION 5.00",
                "Begin VB.Form Dialog",
                "   Picture = \"LegacyDialog.frx\":0000",
                "End",
                "Attribute VB_Name = \"Dialog\""
            ]);
            File.WriteAllText(sourcePath, text);
            File.WriteAllBytes(sidecarPath, [0x01, 0x02, 0x03]);
            File.WriteAllBytes(duplicateSidecarPath, [0x04, 0x05, 0x06]);

            await using var process = await LanguageServerProcessHarness.StartAsync();
            await process.InitializeAsync();
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.WaitForDiagnosticsAsync(uri);
            await process.SendNotificationAsync(
                "vba/intrinsicHostEventCatalog",
                CreateCatalogNotification(
                    "Initialize",
                    [],
                    "Initializes the form."));
            await FenceSnapshotAsync(process, uri, text, version: 2);

            var rename = await process.SendRequestAsync(
                2,
                "textDocument/rename",
                new
                {
                    textDocument = new { uri },
                    position = new
                    {
                        line = 4,
                        character = "Attribute VB_Name = \"".Length
                    },
                    newName = "DialogView"
                });

            Assert.False(rename.TryGetProperty("result", out _));
            var data = rename.GetProperty("error").GetProperty("data");
            Assert.Equal(
                "resourceOperationConflict",
                data.GetProperty("reason").GetString());
            Assert.Equal("sidecarConflict", data.GetProperty("condition").GetString());
            Assert.Equal(
                duplicateSidecarPath,
                data.GetProperty("path").GetString(),
                ignoreCase: true);

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Environment_catalog_does_not_bind_an_ordinary_class_module()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-host-owned-document-rename-").FullName;
        try
        {
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            Directory.CreateDirectory(sourceRoot);
            var sourceTemplate = Path.Combine(sourceRoot, "Book1.xlsm");
            var templateBytes = VbaProjectIdentityWorkbookFixture.Create(
                "ContainingProject",
                1252);
            File.WriteAllBytes(sourceTemplate, templateBytes);
            WriteProjectManifest(projectRoot);
            var sourcePath = Path.Combine(sourceRoot, "Sheet1.cls");
            var uri = new Uri(sourcePath).AbsoluteUri;
            var text = string.Join('\n', [
                "VERSION 1.0 CLASS",
                "BEGIN",
                "  MultiUse = -1",
                "END",
                "Attribute VB_Name = \"Sheet1\""
            ]);
            File.WriteAllText(sourcePath, text);

            await using var process = await LanguageServerProcessHarness.StartAsync();
            await process.InitializeAsync(new
            {
                workspace = new
                {
                    workspaceEdit = new
                    {
                        documentChanges = true,
                        resourceOperations = new[] { "rename" }
                    }
                }
            });
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.WaitForDiagnosticsAsync(uri);
            await process.SendNotificationAsync(
                "vba/intrinsicHostEventCatalog",
                CreateCatalogNotification(
                    "Initialize",
                    [],
                    "Initializes the form."));
            await FenceSnapshotAsync(process, uri, text, version: 2);

            var rename = await process.SendRequestAsync(
                2,
                "textDocument/rename",
                new
                {
                    textDocument = new { uri },
                    position = new
                    {
                        line = 4,
                        character = "Attribute VB_Name = \"".Length
                    },
                    newName = "InvoiceSheet"
                });

            Assert.False(
                rename.TryGetProperty("error", out var error),
                error.ToString());
            var documentChanges = rename
                .GetProperty("result")
                .GetProperty("documentChanges")
                .EnumerateArray()
                .ToArray();
            Assert.Equal(2, documentChanges.Length);
            Assert.True(documentChanges[0].TryGetProperty("textDocument", out _));
            Assert.Equal(uri, documentChanges[1].GetProperty("oldUri").GetString());
            Assert.Equal(
                new Uri(Path.Combine(sourceRoot, "InvoiceSheet.cls")).AbsoluteUri,
                documentChanges[1].GetProperty("newUri").GetString());

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Empty_Sub_declaration_name_offers_only_the_intrinsic_host_prefix()
    {
        var completion = await GetIntrinsicCompletionAsync("Private Sub ");
        var item = Assert.Single(completion.GetProperty("result").EnumerateArray());
        Assert.Equal("UserForm_", item.GetProperty("label").GetString());
        Assert.Equal("Host Events", item.GetProperty("detail").GetString());
        Assert.True(item
            .GetProperty("data")
            .GetProperty("retriggerCompletion")
            .GetBoolean());
        Assert.False(item.TryGetProperty("command", out _));
        var textEdit = item.GetProperty("textEdit");
        Assert.Equal("UserForm_", textEdit.GetProperty("newText").GetString());
        Assert.Equal(
            (4, 12, 4, 12),
            (
                textEdit.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32(),
                textEdit.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32(),
                textEdit.GetProperty("range").GetProperty("end").GetProperty("line").GetInt32(),
                textEdit.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32()));
        Assert.DoesNotContain("(", textEdit.GetProperty("newText").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exact_intrinsic_host_prefix_offers_the_Event_name_and_replaces_only_the_suffix()
    {
        var completion = await GetIntrinsicCompletionAsync("Private Sub UserForm_");
        var item = Assert.Single(completion.GetProperty("result").EnumerateArray());
        Assert.Equal("UserForm_Initialize", item.GetProperty("label").GetString());
        Assert.Equal("Event", item.GetProperty("detail").GetString());
        Assert.Equal("Initialize", item.GetProperty("filterText").GetString());
        var textEdit = item.GetProperty("textEdit");
        Assert.Equal("Initialize", textEdit.GetProperty("newText").GetString());
        Assert.Equal(
            (4, 21, 4, 21),
            (
                textEdit.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32(),
                textEdit.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32(),
                textEdit.GetProperty("range").GetProperty("end").GetProperty("line").GetInt32(),
                textEdit.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32()));
        Assert.DoesNotContain("(", textEdit.GetProperty("newText").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Intrinsic_host_member_completion_projects_handler_signature_and_documentation()
    {
        var completion = await GetIntrinsicCompletionAsync(
            "Private Sub UserForm_");
        var item = Assert.Single(completion.GetProperty("result").EnumerateArray());
        var documentation = item
            .GetProperty("documentation")
            .GetProperty("value")
            .GetString();
        Assert.Contains(
            "UserForm_Initialize()",
            documentation,
            StringComparison.Ordinal);
        Assert.Contains(
            "Occurs when the form is initialized.",
            documentation,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_authoring_host_Event_is_not_offered_for_a_new_declaration()
    {
        var completion = await GetIntrinsicCompletionAsync(
            "Private Sub ",
            authoringAvailable: false);
        Assert.Empty(completion.GetProperty("result").EnumerateArray());
    }

    [Fact]
    public async Task Intrinsic_handler_suffix_hover_uses_the_current_catalog_Event()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-intrinsic-host-event-").FullName;
        try
        {
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            Directory.CreateDirectory(sourceRoot);
            var sourceTemplate = Path.Combine(sourceRoot, "Book1.xlsm");
            WriteProjectManifest(projectRoot);

            var sourcePath = Path.Combine(sourceRoot, "Dialog.frm");
            var uri = new Uri(sourcePath).AbsoluteUri;
            var text = string.Join('\n', [
                "VERSION 5.00",
                "Begin VB.Form Dialog",
                "End",
                "Attribute VB_Name = \"Dialog\"",
                "Private Sub UserForm_Initialize()",
                "End Sub"
            ]);
            File.WriteAllText(sourcePath, text);

            await using var process = await LanguageServerProcessHarness.StartAsync();
            await process.InitializeAsync();
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.WaitForDiagnosticsAsync(uri);

            await process.SendNotificationAsync(
                "vba/intrinsicHostEventCatalog",
                CreateCatalogNotification(
                    "Initialize",
                    [],
                    "Occurs when the form is initialized."));
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri, version = 2 },
                    contentChanges = new[] { new { text } }
                });
            await process.WaitForDiagnosticsAsync(uri);

            var hover = await SendPositionRequestAsync(
                process,
                2,
                "textDocument/hover",
                uri,
                text,
                "UserForm_Initialize",
                "UserForm_".Length);
            var value = hover
                .GetProperty("result")
                .GetProperty("contents")
                .GetProperty("value")
                .GetString();
            Assert.Contains("Event Initialize()", value, StringComparison.Ordinal);
            Assert.Contains(
                "Occurs when the form is initialized.",
                value,
                StringComparison.Ordinal);

            var definition = await SendPositionRequestAsync(
                process,
                3,
                "textDocument/definition",
                uri,
                text,
                "UserForm_Initialize",
                "UserForm_".Length);
            Assert.Equal(
                JsonValueKind.Null,
                definition.GetProperty("result").ValueKind);
            Assert.DoesNotContain(
                "vba-reference://",
                definition.GetRawText(),
                StringComparison.Ordinal);

            await process.ShutdownAsync(4);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Intrinsic_handler_declaration_uses_one_catalog_signature()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-intrinsic-host-signature-").FullName;
        try
        {
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            Directory.CreateDirectory(sourceRoot);
            var sourceTemplate = Path.Combine(sourceRoot, "Book1.xlsm");
            WriteProjectManifest(projectRoot);

            var sourcePath = Path.Combine(sourceRoot, "Dialog.frm");
            var uri = new Uri(sourcePath).AbsoluteUri;
            var text = string.Join('\n', [
                "VERSION 5.00",
                "Begin VB.Form Dialog",
                "End",
                "Attribute VB_Name = \"Dialog\"",
                "Private Sub UserForm_Change(ByVal DifferentName As Long)",
                "End Sub"
            ]);
            File.WriteAllText(sourcePath, text);

            await using var process = await LanguageServerProcessHarness.StartAsync();
            await process.InitializeAsync();
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.WaitForDiagnosticsAsync(uri);
            await process.SendNotificationAsync(
                "vba/intrinsicHostEventCatalog",
                CreateCatalogNotification(
                    "Change",
                    [
                        new
                        {
                            name = "Value",
                            type = new { kind = "intrinsic", name = "Long" },
                            passing = "byVal",
                            arrayShape = "scalar",
                            optional = false,
                            paramArray = false
                        }
                    ],
                    "Occurs when the form changes."));
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri, version = 2 },
                    contentChanges = new[] { new { text } }
                });
            await process.WaitForDiagnosticsAsync(uri);

            var response = await SendPositionRequestAsync(
                process,
                2,
                "textDocument/signatureHelp",
                uri,
                text,
                "DifferentName");
            var result = response.GetProperty("result");
            var signature = Assert.Single(
                result.GetProperty("signatures").EnumerateArray());
            Assert.Equal(
                "UserForm_Change(ByVal Value As Long)",
                signature.GetProperty("label").GetString());
            Assert.Equal(0, result.GetProperty("activeParameter").GetInt32());

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Current_catalog_diagnoses_an_intrinsic_Function_handler()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-intrinsic-host-kind-").FullName;
        try
        {
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            Directory.CreateDirectory(sourceRoot);
            var sourceTemplate = Path.Combine(sourceRoot, "Book1.xlsm");
            WriteProjectManifest(projectRoot);

            var sourcePath = Path.Combine(sourceRoot, "Dialog.frm");
            var uri = new Uri(sourcePath).AbsoluteUri;
            var text = string.Join('\n', [
                "VERSION 5.00",
                "Begin VB.Form Dialog",
                "End",
                "Attribute VB_Name = \"Dialog\"",
                "Private Function UserForm_Change(ByVal Value As Long) As Boolean",
                "End Function"
            ]);
            File.WriteAllText(sourcePath, text);

            await using var process = await LanguageServerProcessHarness.StartAsync();
            await process.InitializeAsync();
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.WaitForDiagnosticsAsync(uri);
            await process.SendNotificationAsync(
                "vba/intrinsicHostEventCatalog",
                CreateCatalogNotification(
                    "Change",
                    [
                        new
                        {
                            name = "Value",
                            type = new { kind = "intrinsic", name = "Long" },
                            passing = "byVal",
                            arrayShape = "scalar",
                            optional = false,
                            paramArray = false
                        }
                    ],
                    "Occurs when the form changes."));
            var diagnosticsCheckpoint = process.TranscriptCheckpoint;
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri, version = 2 },
                    contentChanges = new[] { new { text } }
                });

            var notification = await process.WaitForDiagnosticsMatchingAsync(
                uri,
                diagnostics => diagnostics.EnumerateArray().Any(candidate =>
                    candidate.GetProperty("code").GetString()
                        == "validation.eventHandlerMustBeSub"),
                "validation.eventHandlerMustBeSub",
                afterCheckpoint: diagnosticsCheckpoint);
            var diagnostics = notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray()
                .Where(candidate => candidate.GetProperty("code").GetString()
                    == "validation.eventHandlerMustBeSub")
                .ToArray();
            var diagnostic = Assert.Single(diagnostics);
            Assert.Equal(
                "Event handlers must be declared as Sub procedures.",
                diagnostic.GetProperty("message").GetString());

            await process.ShutdownAsync(2);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Current_catalog_diagnoses_an_intrinsic_Sub_signature()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-intrinsic-host-compatibility-").FullName;
        try
        {
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            Directory.CreateDirectory(sourceRoot);
            var sourceTemplate = Path.Combine(sourceRoot, "Book1.xlsm");
            WriteProjectManifest(projectRoot);

            var sourcePath = Path.Combine(sourceRoot, "Dialog.frm");
            var uri = new Uri(sourcePath).AbsoluteUri;
            var text = string.Join('\n', [
                "VERSION 5.00",
                "Begin VB.Form Dialog",
                "End",
                "Attribute VB_Name = \"Dialog\"",
                "Private Sub UserForm_Change(ByVal Value As Boolean)",
                "End Sub"
            ]);
            File.WriteAllText(sourcePath, text);

            await using var process = await LanguageServerProcessHarness.StartAsync();
            await process.InitializeAsync();
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.WaitForDiagnosticsAsync(uri);
            await process.SendNotificationAsync(
                "vba/intrinsicHostEventCatalog",
                CreateCatalogNotification(
                    "Change",
                    [
                        new
                        {
                            name = "Value",
                            type = new { kind = "intrinsic", name = "Long" },
                            passing = "byVal",
                            arrayShape = "scalar",
                            optional = false,
                            paramArray = false
                        }
                    ],
                    "Occurs when the form changes."));
            var diagnosticsCheckpoint = process.TranscriptCheckpoint;
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri, version = 2 },
                    contentChanges = new[] { new { text } }
                });

            var notification = await process.WaitForDiagnosticsMatchingAsync(
                uri,
                diagnostics => diagnostics.EnumerateArray().Any(candidate =>
                    candidate.GetProperty("code").GetString()
                        == "validation.incompatibleEventHandlerSignature"),
                "validation.incompatibleEventHandlerSignature",
                afterCheckpoint: diagnosticsCheckpoint);
            var diagnostics = notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray()
                .Where(candidate => candidate.GetProperty("code").GetString()
                    == "validation.incompatibleEventHandlerSignature")
                .ToArray();
            var diagnostic = Assert.Single(diagnostics);
            Assert.Equal(
                "Event handler signature does not match any available Event signature.\n"
                    + "Expected signature: Event Change(ByVal Value As Long).\n"
                    + "Mismatches: parameter 1 type: expected Long, found Boolean.",
                diagnostic.GetProperty("message").GetString());
            Assert.False(diagnostic.TryGetProperty(
                "relatedInformation",
                out _));

            await process.ShutdownAsync(2);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task External_WithEvents_handler_uses_the_catalog_Event()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-external-host-event-").FullName;
        try
        {
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            Directory.CreateDirectory(sourceRoot);
            var sourceTemplate = Path.Combine(sourceRoot, "Book1.xlsm");
            WriteProjectManifest(projectRoot);

            var formPath = Path.Combine(sourceRoot, "Dialog.frm");
            var formUri = new Uri(formPath).AbsoluteUri;
            var formText = string.Join('\n', [
                "VERSION 5.00",
                "Begin VB.Form Dialog",
                "End",
                "Attribute VB_Name = \"Dialog\""
            ]);
            File.WriteAllText(formPath, formText);
            var workerPath = Path.Combine(sourceRoot, "Worker.cls");
            var workerUri = new Uri(workerPath).AbsoluteUri;
            var workerText = string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"Worker\"",
                "Private WithEvents dialog As Dialog",
                "Private Sub dialog_Initialize()",
                "End Sub"
            ]);
            File.WriteAllText(workerPath, workerText);

            await using var process = await LanguageServerProcessHarness.StartAsync();
            await process.InitializeAsync();
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(formUri, formText));
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(workerUri, workerText));
            await process.WaitForDiagnosticsAsync(workerUri);
            await process.SendNotificationAsync(
                "vba/intrinsicHostEventCatalog",
                CreateCatalogNotification(
                    "Initialize",
                    [],
                    "Occurs when the form is initialized."));
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri = workerUri, version = 2 },
                    contentChanges = new[] { new { text = workerText } }
                });
            await process.WaitForDiagnosticsAsync(workerUri);

            var hover = await SendPositionRequestAsync(
                process,
                2,
                "textDocument/hover",
                workerUri,
                workerText,
                "dialog_Initialize",
                "dialog_".Length);
            var value = hover
                .GetProperty("result")
                .GetProperty("contents")
                .GetProperty("value")
                .GetString();
            Assert.Contains("Event Initialize()", value, StringComparison.Ordinal);
            Assert.Contains(
                "Occurs when the form is initialized.",
                value,
                StringComparison.Ordinal);

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Intrinsic_handler_references_preserve_an_underscored_Event_name()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-intrinsic-host-references-").FullName;
        try
        {
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            Directory.CreateDirectory(sourceRoot);
            var sourceTemplate = Path.Combine(sourceRoot, "Book1.xlsm");
            WriteProjectManifest(projectRoot);

            var sourcePath = Path.Combine(sourceRoot, "Dialog.frm");
            var uri = new Uri(sourcePath).AbsoluteUri;
            var text = string.Join('\n', [
                "VERSION 5.00",
                "Begin VB.Form Dialog",
                "End",
                "Attribute VB_Name = \"Dialog\"",
                "Private Sub UserForm_Before_Update()",
                "End Sub"
            ]);
            File.WriteAllText(sourcePath, text);

            await using var process = await LanguageServerProcessHarness.StartAsync();
            await process.InitializeAsync();
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.WaitForDiagnosticsAsync(uri);
            await process.SendNotificationAsync(
                "vba/intrinsicHostEventCatalog",
                CreateCatalogNotification(
                    "Before_Update",
                    [],
                    "Occurs before the form updates."));
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri, version = 2 },
                    contentChanges = new[] { new { text } }
                });
            await process.WaitForDiagnosticsAsync(uri);

            var response = await SendPositionRequestAsync(
                process,
                2,
                "textDocument/references",
                uri,
                text,
                "UserForm_Before_Update",
                "UserForm_".Length);
            var reference = Assert.Single(
                response.GetProperty("result").EnumerateArray());
            var range = reference.GetProperty("range");
            Assert.Equal(uri, reference.GetProperty("uri").GetString());
            Assert.Equal(
                (
                    4,
                    "Private Sub UserForm_".Length,
                    4,
                    "Private Sub UserForm_Before_Update".Length),
                (
                    range.GetProperty("start").GetProperty("line").GetInt32(),
                    range.GetProperty("start").GetProperty("character").GetInt32(),
                    range.GetProperty("end").GetProperty("line").GetInt32(),
                    range.GetProperty("end").GetProperty("character").GetInt32()));

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Current_catalog_intrinsic_handler_is_not_a_Rename_target()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-intrinsic-host-rename-").FullName;
        try
        {
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            Directory.CreateDirectory(sourceRoot);
            var sourceTemplate = Path.Combine(sourceRoot, "Book1.xlsm");
            WriteProjectManifest(projectRoot);

            var sourcePath = Path.Combine(sourceRoot, "Dialog.frm");
            var uri = new Uri(sourcePath).AbsoluteUri;
            var text = string.Join('\n', [
                "VERSION 5.00",
                "Begin VB.Form Dialog",
                "End",
                "Attribute VB_Name = \"Dialog\"",
                "Private Sub UserForm_Initialize()",
                "End Sub"
            ]);
            File.WriteAllText(sourcePath, text);

            await using var process = await LanguageServerProcessHarness.StartAsync();
            await process.InitializeAsync();
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.WaitForDiagnosticsAsync(uri);
            await process.SendNotificationAsync(
                "vba/intrinsicHostEventCatalog",
                CreateCatalogNotification(
                    "Initialize",
                    [],
                    "Occurs when the form is initialized."));
            await FenceSnapshotAsync(process, uri, text, version: 2);

            var position = new
            {
                line = 4,
                character = "Private Sub UserForm_".Length
            };
            var prepareCurrent = await process.SendRequestAsync(
                2,
                "textDocument/prepareRename",
                new { textDocument = new { uri }, position });
            Assert.Equal(
                JsonValueKind.Null,
                prepareCurrent.GetProperty("result").ValueKind);
            var currentRename = await process.SendRequestAsync(
                3,
                "textDocument/rename",
                new
                {
                    textDocument = new { uri },
                    position,
                    newName = "userForm_Initialize"
                });
            Assert.Equal(
                "notRenameTarget",
                currentRename
                    .GetProperty("error")
                    .GetProperty("data")
                    .GetProperty("reason")
                    .GetString());

            await process.ShutdownAsync(4);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Guarded_source_and_host_Event_alternatives_all_appear_in_hover()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-guarded-host-hover-").FullName;
        try
        {
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            Directory.CreateDirectory(sourceRoot);
            var sourceTemplate = Path.Combine(sourceRoot, "Book1.xlsm");
            WriteProjectManifest(projectRoot);

            var formPath = Path.Combine(sourceRoot, "Dialog.frm");
            var formUri = new Uri(formPath).AbsoluteUri;
            var formText = string.Join('\n', [
                "VERSION 5.00",
                "Begin VB.Form Dialog",
                "End",
                "Attribute VB_Name = \"Dialog\"",
                "#If VBA7 Then",
                "Public Event Changed(ByVal Value As Long)",
                "#Else",
                "Public Event Changed(ByVal Value As String)",
                "#End If"
            ]);
            File.WriteAllText(formPath, formText);
            var workerPath = Path.Combine(sourceRoot, "Worker.cls");
            var workerUri = new Uri(workerPath).AbsoluteUri;
            var workerText = string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"Worker\"",
                "Private WithEvents dialog As Dialog",
                "Private Sub dialog_Changed(ByVal Enabled As Boolean)",
                "End Sub"
            ]);
            File.WriteAllText(workerPath, workerText);

            await using var process = await LanguageServerProcessHarness.StartAsync();
            await process.InitializeAsync();
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(formUri, formText));
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(workerUri, workerText));
            await process.WaitForDiagnosticsAsync(workerUri);
            await process.SendNotificationAsync(
                "vba/intrinsicHostEventCatalog",
                CreateCatalogNotification(
                    "Changed",
                    [
                        new
                        {
                            name = "Enabled",
                            type = new { kind = "intrinsic", name = "Boolean" },
                            passing = "byVal",
                            arrayShape = "scalar",
                            optional = false,
                            paramArray = false
                        }
                    ],
                    "Built-in Changed Event."));
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri = workerUri, version = 2 },
                    contentChanges = new[] { new { text = workerText } }
                });
            await process.WaitForDiagnosticsAsync(workerUri);

            var hover = await SendPositionRequestAsync(
                process,
                2,
                "textDocument/hover",
                workerUri,
                workerText,
                "dialog_Changed",
                "dialog_".Length);
            var value = hover
                .GetProperty("result")
                .GetProperty("contents")
                .GetProperty("value")
                .GetString();

            Assert.Contains("Event Changed(Value As Long) [#If]", value, StringComparison.Ordinal);
            Assert.Contains("Event Changed(Value As String) [#If]", value, StringComparison.Ordinal);
            Assert.Contains(
                "Event Changed(ByVal Enabled As Boolean) [#If]",
                value,
                StringComparison.Ordinal);
            Assert.Contains("Built-in Changed Event.", value, StringComparison.Ordinal);

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    private static async Task<JsonElement> GetIntrinsicCompletionAsync(
        string declarationLine,
        bool authoringAvailable = true)
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-intrinsic-host-completion-").FullName;
        try
        {
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            Directory.CreateDirectory(sourceRoot);
            WriteProjectManifest(projectRoot);

            var sourcePath = Path.Combine(sourceRoot, "Dialog.frm");
            var uri = new Uri(sourcePath).AbsoluteUri;
            var text = string.Join('\n', [
                "VERSION 5.00",
                "Begin VB.Form Dialog",
                "End",
                "Attribute VB_Name = \"Dialog\"",
                declarationLine
            ]);
            File.WriteAllText(sourcePath, text);

            await using var process = await LanguageServerProcessHarness.StartAsync();
            await process.InitializeAsync();
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.WaitForDiagnosticsAsync(uri);
            await process.SendNotificationAsync(
                "vba/intrinsicHostEventCatalog",
                CreateCatalogNotification(
                    "Initialize",
                    [],
                    "Occurs when the form is initialized.",
                    authoringAvailable));
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri, version = 2 },
                    contentChanges = new[] { new { text } }
                });
            await process.WaitForDiagnosticsAsync(uri);

            var completion = await process.SendRequestAsync(
                2,
                "textDocument/completion",
                new
                {
                    textDocument = new { uri },
                    position = new { line = 4, character = declarationLine.Length }
                });
            await process.ShutdownAsync(3);
            return completion.Clone();
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    private static object CreateOpenDocument(string uri, string text)
        => new
        {
            textDocument = new
            {
                uri,
                languageId = "vba",
                version = 1,
                text
            }
        };

    private static object CreateCatalogNotification(
        string eventName,
        object[] parameters,
        string documentation,
        bool authoringAvailable = true,
        bool existingHandlerRecognizable = true,
        int revision = 1)
        => new
        {
            schemaVersion = "1.0",
            revision,
            catalog = new
            {
                sourceKind = "userForm",
                intrinsicEventSourceName = "UserForm",
                events = new object[]
                {
                    new
                    {
                        identity = new
                        {
                            sourceName = "UserForm",
                            name = eventName
                        },
                        signature = new
                        {
                            parameters,
                            documentation
                        },
                        authoringAvailable,
                        existingHandlerRecognizable
                    }
                }
            }
        };

    private static async Task FenceSnapshotAsync(
        LanguageServerProcessHarness process,
        string uri,
        string text,
        int version)
    {
        await process.SendNotificationAsync(
            "textDocument/didChange",
            new
            {
                textDocument = new { uri, version },
                contentChanges = new[] { new { text } }
            });
        await process.WaitForDiagnosticsAsync(uri);
    }

    private static void WriteProjectManifest(string projectRoot)
    {
        var manifest = new
        {
            schemaVersion = 1,
            projectName = "IntrinsicHostEventProject",
            primaryDocument = "Book1",
            documents = new Dictionary<string, object>
            {
                ["Book1"] = new
                {
                    kind = "excel",
                    sourcePath = "src/Book1",
                    templatePath = "src/Book1/Book1.xlsm",
                    binPath = "bin/Book1/Book1.xlsm",
                    publishPath = "publish/Book1/Book1.xlsm",
                    commonModules = Array.Empty<object>(),
                    references = Array.Empty<object>()
                }
            }
        };
        File.WriteAllText(
            Path.Combine(projectRoot, "vba-project.json"),
            JsonSerializer.Serialize(manifest));
    }

    private static Task<JsonElement> SendPositionRequestAsync(
        LanguageServerProcessHarness process,
        int id,
        string method,
        string uri,
        string text,
        string needle,
        int offset = 0)
    {
        var characterOffset = text.IndexOf(needle, StringComparison.Ordinal) + offset;
        var prefix = text[..characterOffset];
        var line = prefix.Count(character => character == '\n');
        var lineStart = prefix.LastIndexOf('\n');
        var character = lineStart < 0
            ? characterOffset
            : characterOffset - lineStart - 1;
        return process.SendRequestAsync(
            id,
            method,
            new
            {
                textDocument = new { uri },
                position = new { line, character }
            });
    }
}
