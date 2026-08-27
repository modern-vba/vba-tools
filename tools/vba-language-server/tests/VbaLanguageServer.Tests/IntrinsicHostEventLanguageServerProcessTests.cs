using System.Text.Json;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class IntrinsicHostEventLanguageServerProcessTests
{
    [Theory]
    [InlineData("current")]
    [InlineData("lastKnownGood")]
    public async Task Intrinsic_handler_suffix_hover_uses_associated_projected_Event(
        string authority)
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
                "vba/hostClassProjectionSnapshot",
                CreateSnapshotNotification(
                    projectRoot,
                    sourceTemplate,
                    authority,
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

    [Theory]
    [InlineData("current")]
    [InlineData("lastKnownGood")]
    public async Task Intrinsic_handler_declaration_uses_one_host_signature(
        string authority)
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
                "vba/hostClassProjectionSnapshot",
                CreateSnapshotNotification(
                    projectRoot,
                    sourceTemplate,
                    authority,
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

    [Theory]
    [InlineData("current", true)]
    [InlineData("lastKnownGood", false)]
    public async Task Intrinsic_Function_kind_diagnostic_requires_current_authority(
        string authority,
        bool expectDiagnostic)
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
                "vba/hostClassProjectionSnapshot",
                CreateSnapshotNotification(
                    projectRoot,
                    sourceTemplate,
                    authority,
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

            var notification = await process.WaitForDiagnosticsAsync(uri);
            var diagnostics = notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray()
                .Where(candidate => candidate.GetProperty("code").GetString()
                    == "validation.eventHandlerMustBeSub")
                .ToArray();
            if (expectDiagnostic)
            {
                var diagnostic = Assert.Single(diagnostics);
                Assert.Equal(
                    "Event handlers must be declared as Sub procedures.",
                    diagnostic.GetProperty("message").GetString());
            }
            else
            {
                Assert.Empty(diagnostics);
            }

            await process.ShutdownAsync(2);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("current", true)]
    [InlineData("lastKnownGood", false)]
    public async Task Intrinsic_Sub_signature_diagnostic_requires_current_authority(
        string authority,
        bool expectDiagnostic)
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
                "vba/hostClassProjectionSnapshot",
                CreateSnapshotNotification(
                    projectRoot,
                    sourceTemplate,
                    authority,
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

            var notification = await process.WaitForDiagnosticsAsync(uri);
            var diagnostics = notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray()
                .Where(candidate => candidate.GetProperty("code").GetString()
                    == "validation.incompatibleEventHandlerSignature")
                .ToArray();
            if (expectDiagnostic)
            {
                var diagnostic = Assert.Single(diagnostics);
                Assert.Equal(
                    "Event handler signature does not match any available Event signature.\n"
                        + "Expected signature: Event Change(ByVal Value As Long).\n"
                        + "Mismatches: parameter 1 type: expected Long, found Boolean.",
                    diagnostic.GetProperty("message").GetString());
                Assert.False(diagnostic.TryGetProperty(
                    "relatedInformation",
                    out _));
            }
            else
            {
                Assert.Empty(diagnostics);
            }

            await process.ShutdownAsync(2);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("current")]
    [InlineData("lastKnownGood")]
    public async Task External_WithEvents_handler_uses_projected_host_Event(
        string authority)
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
                "vba/hostClassProjectionSnapshot",
                CreateSnapshotNotification(
                    projectRoot,
                    sourceTemplate,
                    authority,
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
                "vba/hostClassProjectionSnapshot",
                CreateSnapshotNotification(
                    projectRoot,
                    sourceTemplate,
                    "current",
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
    public async Task Intrinsic_handler_rename_follows_snapshot_authority_and_loss()
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
                "vba/hostClassProjectionSnapshot",
                CreateSnapshotNotification(
                    projectRoot,
                    sourceTemplate,
                    "current",
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

            await process.SendNotificationAsync(
                "vba/hostClassProjectionSnapshot",
                CreateSnapshotNotification(
                    projectRoot,
                    sourceTemplate,
                    "lastKnownGood",
                    "Initialize",
                    [],
                    "Occurs when the form is initialized.",
                    revision: 2));
            await FenceSnapshotAsync(process, uri, text, version: 3);
            var retainedRename = await process.SendRequestAsync(
                4,
                "textDocument/rename",
                new
                {
                    textDocument = new { uri },
                    position,
                    newName = "RenamedHandler"
                });
            Assert.Equal(
                "analysisIncomplete",
                retainedRename
                    .GetProperty("error")
                    .GetProperty("data")
                    .GetProperty("reason")
                    .GetString());

            await process.SendNotificationAsync(
                "vba/hostClassProjectionSnapshot",
                CreateClearedSnapshotNotification(
                    projectRoot,
                    sourceTemplate,
                    revision: 3));
            await FenceSnapshotAsync(process, uri, text, version: 4);
            var ordinaryRename = await process.SendRequestAsync(
                5,
                "textDocument/rename",
                new
                {
                    textDocument = new { uri },
                    position,
                    newName = "RenamedHandler"
                });
            var edit = Assert.Single(
                ordinaryRename
                    .GetProperty("result")
                    .GetProperty("changes")
                    .GetProperty(uri)
                    .EnumerateArray());
            Assert.Equal("RenamedHandler", edit.GetProperty("newText").GetString());

            await process.ShutdownAsync(6);
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
                "vba/hostClassProjectionSnapshot",
                CreateSnapshotNotification(
                    projectRoot,
                    sourceTemplate,
                    "current",
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

    private static object CreateSnapshotNotification(
        string projectRoot,
        string sourceTemplate,
        string authority,
        string eventName,
        object[] parameters,
        string documentation,
        bool authoringAvailable = true,
        bool existingHandlerRecognizable = true,
        int revision = 1)
        => new
        {
            schemaVersion = 1,
            revision,
            project = Path.GetFullPath(projectRoot),
            document = "Book1",
            sourceTemplate = Path.GetFullPath(sourceTemplate),
            state = "present",
            classEnumerationComplete = true,
            classes = new object[]
            {
                new
                {
                    identity = new { name = "Dialog", kind = "form" },
                    authority,
                    projection = new
                    {
                        intrinsicEventSourceName = "UserForm",
                        events = new object[]
                        {
                            new
                            {
                                name = eventName,
                                parameters,
                                documentation,
                                authoringAvailable,
                                existingHandlerRecognizable
                            }
                        }
                    }
                }
            }
        };

    private static object CreateClearedSnapshotNotification(
        string projectRoot,
        string sourceTemplate,
        int revision)
        => new
        {
            schemaVersion = 1,
            revision,
            project = Path.GetFullPath(projectRoot),
            document = "Book1",
            sourceTemplate = Path.GetFullPath(sourceTemplate),
            state = "cleared"
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
