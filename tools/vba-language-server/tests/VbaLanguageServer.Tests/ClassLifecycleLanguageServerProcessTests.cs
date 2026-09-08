using System.Text.Json;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class ClassLifecycleLanguageServerProcessTests
{
    [Fact]
    public async Task Both_stages_share_trigger_paths_and_emit_only_name_edits()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();
        var request = 2;
        foreach (var fragment in new[] { "", "cLa", "Class_", "cLaSs_iN", "CLASS_tEr" })
        {
            var uri = "file:///C:/work/Worker" + request + ".cls";
            var declaration = "Public Static Sub " + fragment;
            await OpenAsync(process, uri, declaration);
            var explicitResult = await CompleteAsync(process, request++, uri, 0, declaration.Length);
            var continuation = await CompleteAsync(process, request++, uri, 0, declaration.Length, 3);
            Assert.Equal(explicitResult.GetRawText(), continuation.GetRawText());
            if (fragment.Length == 0 || fragment == "Class_")
            {
                var triggered = await CompleteAsync(process, request++, uri, 0, declaration.Length,
                    2, fragment.Length == 0 ? " " : "_");
                Assert.Equal(explicitResult.GetRawText(), triggered.GetRawText());
            }

            var items = explicitResult.EnumerateArray().ToArray();
            if (!fragment.Contains('_'))
            {
                var item = Assert.Single(items);
                Assert.Equal("Class_", item.GetProperty("label").GetString());
                Assert.Equal("Class Lifecycle", item.GetProperty("detail").GetString());
                Assert.True(item.GetProperty("data").GetProperty("retriggerCompletion").GetBoolean());
                Assert.False(item.TryGetProperty("documentation", out _));
                AssertEdit(item, "Class_", "Public Static Sub ".Length, declaration.Length);
            }
            else
            {
                var names = fragment == "Class_" ? new[] { "Initialize", "Terminate" }
                    : fragment == "cLaSs_iN" ? ["Initialize"] : new[] { "Terminate" };
                Assert.Equal(names.Select(name => "Class_" + name),
                    items.Select(item => item.GetProperty("label").GetString()));
                foreach (var item in items)
                {
                    var name = item.GetProperty("label").GetString()!["Class_".Length..];
                    Assert.Equal("Lifecycle Handler", item.GetProperty("detail").GetString());
                    Assert.Equal(name, item.GetProperty("filterText").GetString());
                    AssertEdit(item, name, "Public Static Sub Class_".Length, declaration.Length);
                    var documentation = item.GetProperty("documentation").GetProperty("value").GetString();
                    Assert.Contains("Sub Class_" + name + "()", documentation);
                    Assert.Contains(name == "Initialize" ? "created" : "not guaranteed", documentation);
                    if (name == "Terminate")
                    {
                        Assert.Contains("abnormally", documentation);
                    }
                }
            }
        }
        await process.ShutdownAsync(request);
    }

    [Theory]
    [InlineData("bas", "Private Sub Class_")]
    [InlineData("frm", "Private Sub Class_")]
    [InlineData("cls", "Private Function Class_")]
    [InlineData("cls", "Private Property Get Class_")]
    [InlineData("cls", "Private Property Let Class_")]
    [InlineData("cls", "Private Property Set Class_")]
    [InlineData("cls", "Private Sub Run()\nClass_")]
    public async Task Underscore_and_explicit_requests_exclude_other_contexts(
        string extension,
        string text)
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();
        var uri = "file:///C:/work/Worker." + extension;
        await OpenAsync(process, uri, text);
        var line = text.Count(character => character == '\n');
        var column = text.Length - text.LastIndexOf('\n') - 1;
        Assert.Empty((await CompleteAsync(process, 2, uri, line, column, 2, "_")).EnumerateArray());
        Assert.DoesNotContain((await CompleteAsync(process, 3, uri, line, column)).EnumerateArray(),
            item => item.GetProperty("label").GetString() is "Class_" or "Class_Initialize" or "Class_Terminate");
        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task Collision_and_conditional_results_are_recomputed_from_current_document()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();
        const string uri = "file:///C:/work/Worker.cls";
        var request = 2;
        var version = 1;
        var cases = new (string Source, string[] Labels)[]
        {
            ("Private class_initialize As Long\nPrivate Sub Class_|", ["Class_Terminate"]),
            ("Private class_initialize As Long\nPrivate CLASS_TERMINATE As Long\nPrivate Sub |", []),
            ("Private Sub class_initialize|()\nEnd Sub", ["Class_Initialize"]),
            ("#If VBA7 Then\nPrivate Sub Class_Initialize()\nEnd Sub\nPrivate Sub Class_|\nEnd Sub\n#End If",
                ["Class_Initialize", "Class_Terminate"]),
            ("Private Sub Class_Initialize()\nEnd Sub\n#If VBA7 Then\nPrivate Sub Class_|\nEnd Sub\n#End If",
                ["Class_Terminate"])
        };
        foreach (var (source, labels) in cases)
        {
            var marker = source.IndexOf('|');
            var text = source.Remove(marker, 1);
            if (version == 1)
            {
                await OpenAsync(process, uri, text);
            }
            else
            {
                await process.SendNotificationAsync("textDocument/didChange", new
                {
                    textDocument = new { uri, version },
                    contentChanges = new[] { new { text } }
                });
                await process.WaitForDiagnosticsAsync(uri);
            }
            version++;
            var prefix = source[..marker];
            var items = (await CompleteAsync(process, request++, uri,
                prefix.Count(character => character == '\n'), marker - prefix.LastIndexOf('\n') - 1))
                .EnumerateArray().ToArray();
            Assert.Equal(labels, items.Select(item => item.GetProperty("label").GetString()));
            Assert.All(items, item => Assert.Equal("Lifecycle Handler", item.GetProperty("detail").GetString()));
        }
        await process.ShutdownAsync(request);
    }

    [Fact]
    public async Task Space_preserves_ordinary_expression_completion_and_underscore_stays_scoped()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();
        const string uri = "file:///C:/work/Worker.cls";
        const string text = "Private Sub Run()\nDim value As Long\nvalue = ";
        await OpenAsync(process, uri, text);
        var ordinary = await CompleteAsync(process, 2, uri, 2, "value = ".Length);
        var space = await CompleteAsync(process, 3, uri, 2, "value = ".Length, 2, " ");
        Assert.Equal(ordinary.GetRawText(), space.GetRawText());
        Assert.Contains(ordinary.EnumerateArray(), item => item.GetProperty("label").GetString() == "value");
        Assert.DoesNotContain(ordinary.EnumerateArray(), item => item.GetProperty("label").GetString() == "Class_");
        Assert.Empty((await CompleteAsync(process, 4, uri, 2, "value = ".Length, 2, "_")).EnumerateArray());
        await process.ShutdownAsync(5);
    }

    private static void AssertEdit(JsonElement item, string name, int start, int end)
    {
        Assert.False(item.TryGetProperty("command", out _));
        Assert.False(item.TryGetProperty("additionalTextEdits", out _));
        Assert.False(item.TryGetProperty("insertTextFormat", out var format) && format.GetInt32() == 2);
        var edit = item.GetProperty("textEdit");
        Assert.Equal(name, edit.GetProperty("newText").GetString());
        var range = edit.GetProperty("range");
        Assert.Equal(0, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(0, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(start, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(end, range.GetProperty("end").GetProperty("character").GetInt32());
    }

    private static async Task OpenAsync(LanguageServerProcessHarness process, string uri, string text)
    {
        await process.SendNotificationAsync("textDocument/didOpen", new
        {
            textDocument = new { uri, languageId = "vba", version = 1, text }
        });
        await process.WaitForDiagnosticsAsync(uri);
    }

    private static async Task<JsonElement> CompleteAsync(
        LanguageServerProcessHarness process, int id, string uri, int line, int character,
        int triggerKind = 1, string? triggerCharacter = null)
    {
        var response = await process.SendRequestAsync(id, "textDocument/completion", new
        {
            textDocument = new { uri },
            position = new { line, character },
            context = new { triggerKind, triggerCharacter }
        });
        Assert.False(response.TryGetProperty("error", out var error), error.ToString());
        return response.GetProperty("result");
    }
}
