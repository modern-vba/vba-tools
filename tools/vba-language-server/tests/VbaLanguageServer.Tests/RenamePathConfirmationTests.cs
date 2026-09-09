using System.Text.Json;
using System.Text.Json.Nodes;
using VbaLanguageServer.Lsp;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Workspace;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class RenamePathConfirmationTests
{
    [Theory]
    [InlineData("missing-sidecar", "sidecarMissing")]
    [InlineData("mismatched-sidecar", "sidecarReferenceConflict")]
    [InlineData("wrong-designer", "designerIdentityConflict")]
    [InlineData("malformed-designer", "designerStructureMalformed")]
    public async Task Destination_collision_does_not_bypass_original_form_source_unit_failures(
        string invalidEvidence,
        string expectedCondition)
    {
        var sourceRoot = Directory.CreateTempSubdirectory("vba-ls-confirmation-form-preflight-").FullName;
        try
        {
            var sourcePath = Path.Combine(sourceRoot, "Dialog.frm");
            var sidecarPath = Path.Combine(sourceRoot, "Dialog.frx");
            var destinationPath = Path.Combine(sourceRoot, "DialogView.frx");
            var designerBody = invalidEvidence switch
            {
                "mismatched-sidecar" => "Begin VB.Form Dialog\n  Picture = \"Dialog.frx\":0000\n  MouseIcon = \"Other.frx\":0004\nEnd",
                "wrong-designer" => "Begin VB.Form OtherDialog\nEnd",
                "malformed-designer" => "Begin VB.Form Dialog\nBeginProperty\nEndProperty\nEnd",
                _ => "Begin VB.Form Dialog\n  OleObjectBlob = \"Dialog.frx\":0000\nEnd"
            };
            var sourceText = "VERSION 5.00\n" + designerBody + "\nAttribute VB_Name = \"Dialog\"\n";
            File.WriteAllText(sourcePath, sourceText);
            if (invalidEvidence != "missing-sidecar")
            {
                File.WriteAllBytes(sidecarPath, [0x01, 0x02, 0x03, 0x04, 0x05]);
            }
            byte[] destinationBytes = [0x91, 0x92, 0x93];
            File.WriteAllBytes(destinationPath, destinationBytes);
            var sourceUri = new Uri(sourcePath).AbsoluteUri;
            await using var server = await LanguageServerProcessHarness.StartAsync();
            await InitializeConfirmationClientAsync(server);
            await OpenDocumentAsync(server, sourceUri, sourceText);

            var requested = await server.SendRequestAsync(2, "textDocument/rename", new
            {
                textDocument = new { uri = sourceUri },
                position = new
                {
                    line = sourceText[..sourceText.IndexOf("Attribute VB_Name", StringComparison.Ordinal)]
                        .Count(character => character == '\n'),
                    character = "Attribute VB_Name = \"".Length
                },
                newName = "DialogView"
            });

            Assert.False(requested.TryGetProperty("result", out _), requested.ToString());
            var error = requested.GetProperty("error");
            Assert.Equal(-32803, error.GetProperty("code").GetInt32());
            var data = error.GetProperty("data");
            Assert.False(data.TryGetProperty("confirmationId", out _), requested.ToString());
            Assert.Equal(expectedCondition, data.GetProperty("condition").GetString());
            Assert.Equal(sourceText, File.ReadAllText(sourcePath));
            Assert.Equal(destinationBytes, File.ReadAllBytes(destinationPath));
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("removed")]
    public async Task Retained_form_confirmation_revalidates_the_original_sidecar(string change)
    {
        var sourceRoot = Directory.CreateTempSubdirectory("vba-ls-confirmation-original-sidecar-").FullName;
        try
        {
            var sourcePath = Path.Combine(sourceRoot, "Dialog.frm");
            var sidecarPath = Path.Combine(sourceRoot, "Dialog.frx");
            var destinationPath = Path.Combine(sourceRoot, "DialogView.frx");
            const string sourceText = "VERSION 5.00\nBegin VB.Form Dialog\n"
                + "  OleObjectBlob = \"Dialog.frx\":0000\nEnd\nAttribute VB_Name = \"Dialog\"\n";
            File.WriteAllText(sourcePath, sourceText);
            File.WriteAllBytes(sidecarPath, [0x01, 0x02, 0x03]);
            byte[] destinationBytes = [0x91, 0x92, 0x93];
            File.WriteAllBytes(destinationPath, destinationBytes);
            var sourceUri = new Uri(sourcePath).AbsoluteUri;
            await using var server = await LanguageServerProcessHarness.StartAsync();
            await InitializeConfirmationClientAsync(server);
            await OpenDocumentAsync(server, sourceUri, sourceText);
            var challenge = await RequestChallengeAsync(server, sourceUri, 4, "DialogView");
            Assert.Equal(new[] { sourcePath, sidecarPath },
                challenge.GetProperty("retainedPaths").EnumerateArray().Select(value => value.GetString()));

            if (change == "removed")
            {
                File.Delete(sidecarPath);
            }
            else
            {
                File.WriteAllBytes(sidecarPath, [0x03, 0x02, 0x01]);
            }
            var confirmed = await ConfirmAsync(server, challenge);

            Assert.False(confirmed.TryGetProperty("result", out _), confirmed.ToString());
            Assert.Equal(-32803, confirmed.GetProperty("error").GetProperty("code").GetInt32());
            Assert.Equal(sourceText, File.ReadAllText(sourcePath));
            Assert.Equal(destinationBytes, File.ReadAllBytes(destinationPath));
            Assert.Equal(change != "removed", File.Exists(sidecarPath));
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
        }
    }

    [Fact]
    public void Intrinsic_catalog_change_during_confirmation_aborts_the_captured_semantics()
    {
        var sourceRoot = Directory.CreateTempSubdirectory("vba-ls-confirmation-catalog-change-").FullName;
        try
        {
            var sourcePath = Path.Combine(sourceRoot, "Dialog.frm");
            const string sourceText = "VERSION 5.00\nBegin VB.Form Dialog\nEnd\n"
                + "Attribute VB_Name = \"Dialog\"\nPrivate original As Long\nPrivate existing As Long\n"
                + "Private Sub UserForm_Initialize()\n    original = existing\nEnd Sub\n";
            File.WriteAllText(sourcePath, sourceText);
            var sourceUri = new Uri(sourcePath).AbsoluteUri;
            using var workspace = new VbaLanguageWorkspace(new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()));
            VbaIntrinsicHostEventCatalog Catalog(string eventName)
                => new(VbaIntrinsicHostEventSourceKind.UserForm, "UserForm",
                    [new VbaIntrinsicHostEvent(new VbaIntrinsicHostEventIdentity("UserForm", eventName),
                        new VbaIntrinsicHostEventSignature([], null), true, true)]);
            Assert.True(workspace.TryApplyIntrinsicHostEventCatalog(
                new VbaIntrinsicHostEventCatalogUpdate(1, Catalog("Initialize"))));
            workspace.OpenDocument(sourceUri, 1, sourceText);
            using var output = new MemoryStream();
            var capabilities = new VbaLspClientCapabilityState();
            capabilities.Update(JsonSerializer.SerializeToNode(new
            {
                capabilities = new
                {
                    experimental = new { vbaRenameConfirmation = new { protocolVersion = 1 } },
                    workspace = new { workspaceEdit = new { documentChanges = true, resourceOperations = new[] { "rename" } } }
                }
            })!.AsObject());
            using var executor = new VbaLspRequestExecution(new LspMessageTransport(Stream.Null, output),
                workspace, clientCapabilities: capabilities);
            var requested = executor.Capture(JsonSerializer.SerializeToNode(new
            {
                jsonrpc = "2.0", id = 1, method = "textDocument/rename",
                @params = new
                {
                    textDocument = new { uri = sourceUri },
                    position = new { line = 4, character = "Private ".Length },
                    newName = "existing"
                }
            })!.AsObject(), CancellationToken.None).Execute(CancellationToken.None);
            var challenge = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(requested.ErrorData);
            Assert.True(challenge.TryGetValue("kind", out var kind),
                requested.ErrorMessage + "\n" + JsonSerializer.Serialize(challenge));
            Assert.Equal("vbaRenameConfirmationRequired", kind);
            Assert.True(workspace.TryApplyIntrinsicHostEventCatalog(
                new VbaIntrinsicHostEventCatalogUpdate(2, Catalog("Activate"))));

            var confirmed = executor.Capture(JsonSerializer.SerializeToNode(new
            {
                jsonrpc = "2.0", id = 2, method = "vba/confirmRename",
                @params = new { confirmationId = challenge["confirmationId"], decision = "continue" }
            })!.AsObject(), CancellationToken.None).Execute(CancellationToken.None);

            Assert.Equal(-32803, confirmed.ErrorCode);
            Assert.Null(confirmed.Result);
            Assert.Equal(sourceText, File.ReadAllText(sourcePath));
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("removed")]
    [InlineData("appeared")]
    public async Task Destination_changes_during_confirmation_abort_the_captured_path_decision(string change)
    {
        var sourceRoot = Directory.CreateTempSubdirectory("vba-ls-confirmation-destination-change-").FullName;
        try
        {
            var sourcePath = Path.Combine(sourceRoot, "InvoiceModule.bas");
            var destinationPath = Path.Combine(sourceRoot, "BillingModule.bas");
            const string sourceText = "Attribute VB_Name = \"InvoiceModule\"\n";
            File.WriteAllText(sourcePath, sourceText);
            if (change == "appeared")
            {
                File.WriteAllText(Path.Combine(sourceRoot, "Existing.bas"),
                    "Attribute VB_Name = \"BillingModule\"\n");
            }
            else
            {
                File.WriteAllText(destinationPath, "Attribute VB_Name = \"UnrelatedModule\"\n");
            }
            var sourceUri = new Uri(sourcePath).AbsoluteUri;
            await using var server = await LanguageServerProcessHarness.StartAsync();
            await InitializeConfirmationClientAsync(server);
            await OpenDocumentAsync(server, sourceUri, sourceText);
            var challenge = await RequestChallengeAsync(server, sourceUri, 0, "BillingModule");
            Assert.Equal(change == "appeared" ? 0 : 1,
                challenge.GetProperty("retainedPaths").GetArrayLength());

            if (change == "removed")
            {
                File.Delete(destinationPath);
            }
            else
            {
                File.WriteAllText(destinationPath, "Attribute VB_Name = \"ChangedDestination\"\n");
            }
            var confirmed = await ConfirmAsync(server, challenge);

            Assert.False(confirmed.TryGetProperty("result", out _), confirmed.ToString());
            Assert.Equal(-32803, confirmed.GetProperty("error").GetProperty("code").GetInt32());
            Assert.Equal(sourceText, File.ReadAllText(sourcePath));
            Assert.Equal(change != "removed", File.Exists(destinationPath));
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(".bas")]
    [InlineData(".frm")]
    public async Task Retained_paths_require_no_resource_operation_client_capability(string sourceExtension)
    {
        var sourceRoot = Directory.CreateTempSubdirectory("vba-ls-retained-path-capability-").FullName;
        try
        {
            var sourcePath = Path.Combine(sourceRoot, "InvoiceModule" + sourceExtension);
            var destinationPath = Path.Combine(sourceRoot, "BillingModule" + sourceExtension);
            var sourceText = sourceExtension == ".frm"
                ? "VERSION 5.00\nBegin VB.Form InvoiceModule\nEnd\nAttribute VB_Name = \"InvoiceModule\"\n"
                : "Attribute VB_Name = \"InvoiceModule\"\n";
            File.WriteAllText(sourcePath, sourceText);
            File.WriteAllText(destinationPath, sourceExtension == ".frm"
                ? "VERSION 5.00\nBegin VB.Form Existing\nEnd\nAttribute VB_Name = \"Existing\"\n"
                : "Attribute VB_Name = \"Existing\"\n");
            var sourceUri = new Uri(sourcePath).AbsoluteUri;
            await using var server = await LanguageServerProcessHarness.StartAsync();
            await InitializeConfirmationClientAsync(server, supportsResourceOperations: false);
            await OpenDocumentAsync(server, sourceUri, sourceText);

            var challenge = await RequestChallengeAsync(server, sourceUri,
                sourceExtension == ".frm" ? 3 : 0, "BillingModule");
            var confirmed = await ConfirmAsync(server, challenge);

            Assert.False(confirmed.TryGetProperty("error", out var error), error.ToString());
            var edit = confirmed.GetProperty("result");
            Assert.False(edit.TryGetProperty("documentChanges", out _));
            Assert.Equal(sourceUri, Assert.Single(edit.GetProperty("changes").EnumerateObject()).Name);
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(".frm")]
    [InlineData(".frx")]
    public async Task Either_form_destination_collision_retains_the_complete_source_unit_and_resource_references(
        string conflictingExtension)
    {
        var sourceRoot = Directory.CreateTempSubdirectory("vba-ls-retained-form-pair-").FullName;
        try
        {
            var formPath = Path.Combine(sourceRoot, "Dialog.frm");
            var sidecarPath = Path.Combine(sourceRoot, "Dialog.frx");
            var destinationPath = Path.Combine(sourceRoot, "DialogView" + conflictingExtension);
            var sourceText = string.Join('\n',
            [
                "VERSION 5.00",
                "Begin VB.Form Dialog",
                "  Caption = \"Dialog\"",
                "  OleObjectBlob = \"Dialog.frx\":0000",
                "  Begin VB.CommandButton DialogButton",
                "    Picture = \"Dialog.frx\":0004",
                "  End",
                "End",
                "Attribute VB_Name = \"Dialog\"",
                "Private Sub UserForm_Initialize()",
                "End Sub",
                ""
            ]);
            byte[] sidecarBytes = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06];
            File.WriteAllText(formPath, sourceText);
            File.WriteAllBytes(sidecarPath, sidecarBytes);
            if (conflictingExtension == ".frm")
            {
                File.WriteAllText(destinationPath,
                    "VERSION 5.00\nBegin VB.Form Existing\nEnd\nAttribute VB_Name = \"Existing\"\n");
            }
            else
            {
                File.WriteAllBytes(destinationPath, [0x91, 0x92, 0x93]);
            }
            var destinationBytes = File.ReadAllBytes(destinationPath);
            var formUri = new Uri(formPath).AbsoluteUri;
            await using var server = await LanguageServerProcessHarness.StartAsync();
            await server.InitializeAsync(new
            {
                experimental = new { vbaRenameConfirmation = new { protocolVersion = 1 } },
                workspace = new
                {
                    workspaceEdit = new
                    {
                        documentChanges = true,
                        resourceOperations = new[] { "rename" }
                    }
                }
            });
            await server.SendNotificationAsync("textDocument/didOpen", new
            {
                textDocument = new { uri = formUri, languageId = "vba", version = 1, text = sourceText }
            });

            var requested = await server.SendRequestAsync(2, "textDocument/rename", new
            {
                textDocument = new { uri = formUri },
                position = new { line = 8, character = "Attribute VB_Name = \"".Length },
                newName = "DialogView"
            });

            Assert.False(requested.TryGetProperty("result", out _));
            var challenge = requested.GetProperty("error").GetProperty("data");
            Assert.True(challenge.TryGetProperty("kind", out var kind), requested.ToString());
            Assert.Equal("vbaRenameConfirmationRequired", kind.GetString());
            Assert.Equal(new[] { formPath, sidecarPath },
                challenge.GetProperty("retainedPaths").EnumerateArray().Select(value => value.GetString()));

            var confirmed = await server.SendRequestAsync(3, "vba/confirmRename", new
            {
                confirmationId = challenge.GetProperty("confirmationId").GetString(),
                decision = "continue"
            });

            Assert.False(confirmed.TryGetProperty("error", out var confirmationError), confirmationError.ToString());
            var edit = confirmed.GetProperty("result");
            Assert.False(edit.TryGetProperty("documentChanges", out _), edit.ToString());
            var changedDocument = Assert.Single(edit.GetProperty("changes").EnumerateObject());
            Assert.Equal(formUri, changedDocument.Name);
            var updatedText = ApplyTextEdits(sourceText, changedDocument.Value);
            Assert.Equal(sourceText
                .Replace("Begin VB.Form Dialog\n", "Begin VB.Form DialogView\n", StringComparison.Ordinal)
                .Replace("Attribute VB_Name = \"Dialog\"", "Attribute VB_Name = \"DialogView\"", StringComparison.Ordinal),
                updatedText);
            Assert.Equal(sidecarBytes, File.ReadAllBytes(sidecarPath));
            Assert.Equal(destinationBytes, File.ReadAllBytes(destinationPath));
            Assert.Equal(sourceText, File.ReadAllText(formPath));
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Destination_only_module_collision_confirms_retaining_the_original_filename()
    {
        var sourceRoot = Directory.CreateTempSubdirectory("vba-ls-retained-module-path-").FullName;
        try
        {
            var sourcePath = Path.Combine(sourceRoot, "InvoiceModule.bas");
            var destinationPath = Path.Combine(sourceRoot, "BillingModule.bas");
            const string sourceText = "Attribute VB_Name = \"InvoiceModule\"\n";
            const string destinationText = "Attribute VB_Name = \"UnrelatedModule\"\n";
            File.WriteAllText(sourcePath, sourceText);
            File.WriteAllText(destinationPath, destinationText);
            var sourceUri = new Uri(sourcePath).AbsoluteUri;
            await using var server = await LanguageServerProcessHarness.StartAsync();
            await server.InitializeAsync(new
            {
                experimental = new { vbaRenameConfirmation = new { protocolVersion = 1 } },
                workspace = new
                {
                    workspaceEdit = new
                    {
                        documentChanges = true,
                        resourceOperations = new[] { "rename" }
                    }
                }
            });
            await server.SendNotificationAsync("textDocument/didOpen", new
            {
                textDocument = new { uri = sourceUri, languageId = "vba", version = 1, text = sourceText }
            });

            var requested = await server.SendRequestAsync(2, "textDocument/rename", new
            {
                textDocument = new { uri = sourceUri },
                position = new { line = 0, character = "Attribute VB_Name = \"".Length },
                newName = "BillingModule"
            });

            Assert.False(requested.TryGetProperty("result", out _));
            var error = requested.GetProperty("error");
            Assert.Equal(-32803, error.GetProperty("code").GetInt32());
            var challenge = error.GetProperty("data");
            Assert.True(challenge.TryGetProperty("kind", out var challengeKind), requested.ToString());
            Assert.Equal("vbaRenameConfirmationRequired", challengeKind.GetString());
            Assert.Equal("InvoiceModule", challenge.GetProperty("originalName").GetString());
            Assert.Equal("BillingModule", challenge.GetProperty("newName").GetString());
            Assert.Equal(sourcePath, Assert.Single(challenge.GetProperty("retainedPaths").EnumerateArray()).GetString());
            Assert.NotEmpty(challenge.GetProperty("conflicts").EnumerateArray());

            var confirmed = await server.SendRequestAsync(3, "vba/confirmRename", new
            {
                confirmationId = challenge.GetProperty("confirmationId").GetString(),
                decision = "continue"
            });

            Assert.False(confirmed.TryGetProperty("error", out var confirmationError), confirmationError.ToString());
            var edit = confirmed.GetProperty("result");
            Assert.False(edit.TryGetProperty("documentChanges", out _));
            var changedDocument = Assert.Single(edit.GetProperty("changes").EnumerateObject());
            Assert.Equal(sourceUri, changedDocument.Name);
            var textEdit = Assert.Single(changedDocument.Value.EnumerateArray());
            Assert.Equal("BillingModule", textEdit.GetProperty("newText").GetString());
            Assert.Equal(0, textEdit.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
            Assert.Equal("Attribute VB_Name = \"".Length,
                textEdit.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
            Assert.Equal(sourceText, File.ReadAllText(sourcePath));
            Assert.Equal(destinationText, File.ReadAllText(destinationPath));
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
        }
    }

    private static string ApplyTextEdits(string text, JsonElement edits)
    {
        var lines = text.Split('\n');
        int Offset(JsonElement position)
            => lines.Take(position.GetProperty("line").GetInt32()).Sum(line => line.Length + 1)
                + position.GetProperty("character").GetInt32();

        foreach (var edit in edits.EnumerateArray()
            .OrderByDescending(edit => Offset(edit.GetProperty("range").GetProperty("start"))))
        {
            var range = edit.GetProperty("range");
            var start = Offset(range.GetProperty("start"));
            var end = Offset(range.GetProperty("end"));
            text = text[..start] + edit.GetProperty("newText").GetString() + text[end..];
        }
        return text;
    }

    private static Task<JsonElement> InitializeConfirmationClientAsync(
        LanguageServerProcessHarness server,
        bool supportsResourceOperations = true)
        => server.InitializeAsync(new
        {
            experimental = new { vbaRenameConfirmation = new { protocolVersion = 1 } },
            workspace = new
            {
                workspaceEdit = new
                {
                    documentChanges = supportsResourceOperations,
                    resourceOperations = supportsResourceOperations ? new[] { "rename" } : []
                }
            }
        });

    private static Task OpenDocumentAsync(
        LanguageServerProcessHarness server, string uri, string text)
        => server.SendNotificationAsync("textDocument/didOpen", new
        {
            textDocument = new { uri, languageId = "vba", version = 1, text }
        });

    private static async Task<JsonElement> RequestChallengeAsync(
        LanguageServerProcessHarness server, string uri, int declarationLine, string newName)
    {
        var response = await server.SendRequestAsync(2, "textDocument/rename", new
        {
            textDocument = new { uri },
            position = new { line = declarationLine, character = "Attribute VB_Name = \"".Length },
            newName
        });
        Assert.True(response.TryGetProperty("error", out var error), response.ToString());
        var challenge = error.GetProperty("data");
        Assert.True(challenge.TryGetProperty("kind", out var kind), response.ToString());
        Assert.Equal("vbaRenameConfirmationRequired", kind.GetString());
        return challenge;
    }

    private static Task<JsonElement> ConfirmAsync(LanguageServerProcessHarness server, JsonElement challenge)
        => server.SendRequestAsync(3, "vba/confirmRename", new
        {
            confirmationId = challenge.GetProperty("confirmationId").GetString(),
            decision = "continue"
        });
}
