using System.Text.Json.Nodes;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class ConfirmationRenameLanguageServerProcessTests
{
    [Fact]
    public async Task Confirmation_with_positional_parameters_is_invalid_params_without_an_internal_error()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync(new
        {
            experimental = new { vbaRenameConfirmation = new { protocolVersion = 1 } }
        });
        var response = await process.SendRequestAsync(2, "vba/confirmRename", new object?[]
        {
            new { confirmationId = "unissued", decision = "cancel" }, null
        });
        Assert.Equal(-32602, response.GetProperty("error").GetProperty("code").GetInt32());
        await process.ShutdownAsync(3);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("continue")]
    public async Task A_new_rename_supersedes_pending_consent_and_each_decision_is_consumed_once(string decision)
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync(new
        {
            experimental = new { vbaRenameConfirmation = new { protocolVersion = 1 } }
        });
        const string uri = "file:///C:/work/OneUseRename.bas";
        const string text = "Attribute VB_Name = \"OneUseRename\"\nPublic Sub Run()\n"
            + "    Dim original As Long\n    Dim existing As Long\n    original = 1\nEnd Sub";
        await process.SendNotificationAsync("textDocument/didOpen", new
        {
            textDocument = new { uri, languageId = "vba", version = 1, text }
        });
        await process.WaitForDiagnosticsAsync(uri);
        async Task<string> RequestChallenge(int id)
        {
            var response = await process.SendRequestAsync(id, "textDocument/rename", new
            {
                textDocument = new { uri }, position = new { line = 2, character = 8 }, newName = "existing"
            });
            return response.GetProperty("error").GetProperty("data").GetProperty("confirmationId").GetString()!;
        }
        var first = await RequestChallenge(2);
        var current = await RequestChallenge(3);
        Assert.NotEqual(first, current);
        var staleCancel = await process.SendRequestAsync(4, "vba/confirmRename", new
        {
            confirmationId = first, decision = "cancel"
        });
        Assert.Equal("confirmationExpired", staleCancel.GetProperty("error").GetProperty("data").GetProperty("reason").GetString());
        var consumed = await process.SendRequestAsync(5, "vba/confirmRename", new
        {
            confirmationId = current, decision
        });
        Assert.False(consumed.TryGetProperty("error", out _), consumed.ToString());
        Assert.Equal(decision == "cancel" ? System.Text.Json.JsonValueKind.Null : System.Text.Json.JsonValueKind.Object,
            consumed.GetProperty("result").ValueKind);
        var replay = await process.SendRequestAsync(6, "vba/confirmRename", new
        {
            confirmationId = current, decision = "continue"
        });
        Assert.Equal("confirmationExpired", replay.GetProperty("error").GetProperty("data").GetProperty("reason").GetString());
        await process.ShutdownAsync(7);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Containing_project_collision_requires_confirmation_bound_to_the_original_template(bool changeTemplate)
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-confirm-project-name-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "vba-project.json"), """
                {"schemaVersion":1,"projectName":"ManifestLabel","primaryDocument":"Book1",
                 "documents":{"Book1":{"kind":"excel","sourcePath":"src","templatePath":"Book1.xlsm",
                   "binPath":"bin/Book1.xlsm","publishPath":"publish/Book1.xlsm","commonModules":[],"references":[]}}}
                """);
            var template = Path.Combine(projectRoot, "Book1.xlsm");
            File.WriteAllBytes(template, VbaProjectIdentityWorkbookFixture.Create("bILLINGmODULE", 1252));
            var sourcePath = Path.Combine(projectRoot, "src", "SourceUnit.bas");
            const string text = "Attribute VB_Name = \"InvoiceModule\"\nPublic Sub Run()\nEnd Sub";
            File.WriteAllText(sourcePath, text);
            var uri = new Uri(sourcePath).AbsoluteUri;
            await using var process = await LanguageServerProcessHarness.StartAsync();
            await process.InitializeAsync(new
            {
                experimental = new { vbaRenameConfirmation = new { protocolVersion = 1 } }
            });
            await process.SendNotificationAsync("textDocument/didOpen", new
            {
                textDocument = new { uri, languageId = "vba", version = 1, text }
            });
            await process.WaitForDiagnosticsAsync(uri);
            var response = await process.SendRequestAsync(2, "textDocument/rename", new
            {
                textDocument = new { uri }, position = new { line = 0, character = 21 }, newName = "BillingModule"
            });
            Assert.True(response.TryGetProperty("error", out var error), response.ToString());
            var challenge = error.GetProperty("data");
            Assert.True(challenge.TryGetProperty("confirmationId", out var confirmationId), error.ToString());
            var conflict = Assert.Single(challenge.GetProperty("conflicts").EnumerateArray());
            Assert.Equal("containingProject", conflict.GetProperty("collisionKind").GetString());
            Assert.Equal("bILLINGmODULE", conflict.GetProperty("name").GetString());
            Assert.Equal(new Uri(template).AbsoluteUri, conflict.GetProperty("uri").GetString());
            if (changeTemplate)
            {
                File.WriteAllBytes(template, VbaProjectIdentityWorkbookFixture.Create("DifferentProject", 1252));
            }
            var confirmed = await process.SendRequestAsync(3, "vba/confirmRename", new
            {
                confirmationId = confirmationId.GetString(), decision = "continue"
            });
            if (changeTemplate)
            {
                Assert.True(confirmed.TryGetProperty("error", out var staleError), confirmed.ToString());
                Assert.Equal("analysisIncomplete", staleError.GetProperty("data").GetProperty("reason").GetString());
                Assert.False(confirmed.TryGetProperty("result", out _));
            }
            else
            {
                Assert.False(confirmed.TryGetProperty("error", out _), confirmed.ToString());
                var edits = confirmed.GetProperty("result").GetProperty("changes").GetProperty(uri);
                Assert.Equal("BillingModule", Assert.Single(edits.EnumerateArray()).GetProperty("newText").GetString());
            }
            Assert.Equal(text, File.ReadAllText(sourcePath));
            await process.ShutdownAsync(4);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("true")]
    [InlineData("{\"protocolVersion\":2}")]
    public async Task Unknown_confirmation_capability_keeps_initialization_and_strict_rename_available(string capability)
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        var initialization = await process.InitializeAsync(new JsonObject
        {
            ["experimental"] = new JsonObject { ["vbaRenameConfirmation"] = JsonNode.Parse(capability) }
        });
        Assert.False(initialization.TryGetProperty("error", out _), initialization.ToString());
        const string uri = "file:///C:/work/StrictConfirmationFallback.bas";
        const string text = "Attribute VB_Name = \"StrictConfirmationFallback\"\n"
            + "Public Sub Run()\n    Dim original As Long\n    Dim existing As Long\n"
            + "    original = 1\nEnd Sub";
        await process.SendNotificationAsync("textDocument/didOpen", new
        {
            textDocument = new { uri, languageId = "vba", version = 1, text }
        });
        await process.WaitForDiagnosticsAsync(uri);
        var response = await process.SendRequestAsync(2, "textDocument/rename", new
        {
            textDocument = new { uri }, position = new { line = 2, character = 8 }, newName = "existing"
        });
        var failure = response.GetProperty("error").GetProperty("data");
        Assert.Equal("sameScopeCollision", failure.GetProperty("reason").GetString());
        Assert.False(failure.TryGetProperty("confirmationId", out _));
        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Confirmation_rejects_a_participating_source_revision_change_even_when_text_returns_to_the_capture()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync(new
        {
            experimental = new { vbaRenameConfirmation = new { protocolVersion = 1 } }
        });
        const string uri = "file:///C:/work/ConfirmationRevision.bas";
        const string text = "Attribute VB_Name = \"ConfirmationRevision\"\n"
            + "Public Sub Run()\n    Dim original As Long\n    Dim existing As Long\n"
            + "    original = 1\nEnd Sub";
        await process.SendNotificationAsync("textDocument/didOpen", new
        {
            textDocument = new { uri, languageId = "vba", version = 1, text }
        });
        await process.WaitForDiagnosticsAsync(uri);
        var response = await process.SendRequestAsync(2, "textDocument/rename", new
        {
            textDocument = new { uri }, position = new { line = 2, character = 8 }, newName = "existing"
        });
        var id = response.GetProperty("error").GetProperty("data").GetProperty("confirmationId").GetString();
        await process.SendNotificationAsync("textDocument/didChange", new
        {
            textDocument = new { uri, version = 2 }, contentChanges = new[] { new { text = text + "\n' changed" } }
        });
        await process.SendNotificationAsync("textDocument/didChange", new
        {
            textDocument = new { uri, version = 3 }, contentChanges = new[] { new { text } }
        });
        var confirmed = await process.SendRequestAsync(3, "vba/confirmRename", new
        {
            confirmationId = id, decision = "continue"
        });
        Assert.True(confirmed.TryGetProperty("error", out var error), confirmed.ToString());
        Assert.Equal("sourceChanged", error.GetProperty("data").GetProperty("condition").GetString());
        Assert.False(confirmed.TryGetProperty("result", out _));
        var replay = await process.SendRequestAsync(4, "vba/confirmRename", new
        {
            confirmationId = id, decision = "continue"
        });
        Assert.Equal("confirmationExpired", replay.GetProperty("error").GetProperty("data").GetProperty("reason").GetString());
        await process.ShutdownAsync(5);
    }

    [Fact]
    public async Task Confirmed_collision_changes_only_the_original_declaration_and_references()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync(new
        {
            experimental = new { vbaRenameConfirmation = new { protocolVersion = 1 } }
        });
        const string uri = "file:///C:/work/ConfirmedRename.bas";
        const string text = """
            Attribute VB_Name = "ConfirmedRename"
            Public Sub Run()
                Dim original As Long
                Dim existing As Long
                original = 1
                existing = 2
            End Sub
            """;
        await process.SendNotificationAsync("textDocument/didOpen", new
        {
            textDocument = new { uri, languageId = "vba", version = 1, text }
        });
        await process.WaitForDiagnosticsAsync(uri);
        var response = await process.SendRequestAsync(2, "textDocument/rename", new
        {
            textDocument = new { uri },
            position = new { line = 2, character = "    Dim ".Length },
            newName = "existing"
        });
        Assert.False(response.TryGetProperty("result", out _), response.ToString());
        var data = response.GetProperty("error").GetProperty("data");
        Assert.True(data.TryGetProperty("kind", out var kind), data.ToString());
        Assert.Equal("vbaRenameConfirmationRequired", kind.GetString());
        Assert.Equal(1, data.GetProperty("protocolVersion").GetInt32());
        Assert.Equal("original", data.GetProperty("originalName").GetString());
        Assert.Equal("existing", data.GetProperty("newName").GetString());
        Assert.NotEmpty(data.GetProperty("conflicts").EnumerateArray());
        Assert.NotEmpty(data.GetProperty("concerns").EnumerateArray());

        var confirmed = await process.SendRequestAsync(3, "vba/confirmRename", new
        {
            confirmationId = data.GetProperty("confirmationId").GetString(),
            decision = "continue"
        });
        Assert.False(confirmed.TryGetProperty("error", out _), confirmed.ToString());
        var edits = confirmed.GetProperty("result").GetProperty("changes")
            .GetProperty(uri).EnumerateArray().ToArray();
        Assert.Equal(new[] { 2, 4 }, edits.Select(edit => edit.GetProperty("range")
            .GetProperty("start").GetProperty("line").GetInt32()).Order().ToArray());
        Assert.All(edits, edit => Assert.Equal("existing", edit.GetProperty("newText").GetString()));
        await process.SendNotificationAsync("textDocument/didChange", new
        {
            textDocument = new { uri, version = 2 },
            contentChanges = new[] { new { text = text.Replace("original", "existing", StringComparison.Ordinal) } }
        });
        var diagnostics = await process.WaitForDiagnosticsMatchingAsync(
            uri,
            values => values.EnumerateArray().Any(diagnostic =>
                diagnostic.GetProperty("code").GetString() == "validation.duplicateDeclaration"),
            "the existing duplicate-declaration diagnostic after confirmed Rename");
        var duplicateLines = diagnostics.GetProperty("params").GetProperty("diagnostics")
            .EnumerateArray()
            .Where(diagnostic => diagnostic.GetProperty("code").GetString() == "validation.duplicateDeclaration")
            .Select(diagnostic => diagnostic.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32())
            .ToArray();
        Assert.Contains(2, duplicateLines);
        Assert.Contains(3, duplicateLines);
        await process.ShutdownAsync(4);
    }
}
