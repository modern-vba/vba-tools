using System.Text.Json;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class SourceTextRenameLanguageServerProcessTests
{
    public static TheoryData<string, bool, bool> SourceCases
    {
        get
        {
            var cases = new TheoryData<string, bool, bool>();
            foreach (var newlineStyle in new[] { "crlf", "lf", "cr", "mixed" })
            foreach (var trailingNewline in new[] { false, true })
            foreach (var unicodePrefix in new[] { false, true })
            {
                cases.Add(newlineStyle, trailingNewline, unicodePrefix);
            }
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(SourceCases))]
    public async Task Rename_preserves_newlines_and_resolved_occurrences(
        string newlineStyle, bool trailingNewline, bool unicodePrefix)
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();
        const string uri = "file:///C:/source-text-rename-tests/Worker.bas";
        var usePrefix = unicodePrefix ? "    Debug.Print \"日本語😀\" : " : "    ";
        // The astral character occupies two UTF-16 units before the identifier.
        var useCharacter = unicodePrefix ? 26 : 4;
        var source = CreateSource(new[]
        {
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    Dim item As Long",
            usePrefix + "item = 1",
            "End Sub"
        }, newlineStyle, trailingNewline);
        await process.SendNotificationAsync("textDocument/didOpen", new
        {
            textDocument = new { uri, languageId = "vba", version = 1, text = source }
        });

        var preparation = await process.SendRequestAsync(2, "textDocument/prepareRename", new
        {
            textDocument = new { uri }, position = new { line = 2, character = 8 }
        });
        Assert.False(preparation.TryGetProperty("error", out _), preparation.ToString());
        Assert.Equal("item", preparation.GetProperty("result").GetProperty("placeholder").GetString());
        Assert.Equal((2, 8, 2, 12), ReadRange(preparation.GetProperty("result").GetProperty("range")));

        var rename = await process.SendRequestAsync(3, "textDocument/rename", new
        {
            textDocument = new { uri },
            position = new { line = 2, character = 8 },
            newName = "count"
        });
        Assert.False(rename.TryGetProperty("error", out _), rename.ToString());
        var changes = rename.GetProperty("result").GetProperty("changes");
        Assert.Equal(uri, Assert.Single(changes.EnumerateObject()).Name);
        var edits = changes.GetProperty(uri).EnumerateArray().ToArray();
        Assert.Equal(new[] { (2, 8, 2, 12), (3, useCharacter, 3, useCharacter + 4) },
            edits.Select(edit => ReadRange(edit.GetProperty("range"))).ToArray());
        Assert.All(edits, edit => Assert.Equal("count", edit.GetProperty("newText").GetString()));

        // The fixture's two exact occurrences are an independent offset oracle.
        var offsets = new[]
        {
            source.IndexOf("item", StringComparison.Ordinal),
            source.LastIndexOf("item", StringComparison.Ordinal)
        };
        var updated = source;
        for (var index = edits.Length - 1; index >= 0; index--)
        {
            updated = updated.Remove(offsets[index], "item".Length)
                .Insert(offsets[index], edits[index].GetProperty("newText").GetString()!);
        }
        Assert.Equal(CreateSource(new[]
        {
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    Dim count As Long",
            usePrefix + "count = 1",
            "End Sub"
        }, newlineStyle, trailingNewline), updated);

        await process.SendNotificationAsync("textDocument/didChange", new
        {
            textDocument = new { uri, version = 2 },
            contentChanges = new[] { new { text = updated } }
        });
        var references = await process.SendRequestAsync(4, "textDocument/references", new
        {
            textDocument = new { uri },
            position = new { line = 2, character = 8 },
            context = new { includeDeclaration = true }
        });
        Assert.False(references.TryGetProperty("error", out _), references.ToString());
        var locations = references.GetProperty("result").EnumerateArray().ToArray();
        Assert.All(locations, location => Assert.Equal(uri, location.GetProperty("uri").GetString()));
        Assert.Equal(new[] { (2, 8, 2, 13), (3, useCharacter, 3, useCharacter + 5) },
            locations.Select(location => ReadRange(location.GetProperty("range"))).ToArray());

        var semanticTokens = await process.SendRequestAsync(5, "textDocument/semanticTokens/full", new
        {
            textDocument = new { uri }
        });
        Assert.False(semanticTokens.TryGetProperty("error", out _), semanticTokens.ToString());
        var data = semanticTokens.GetProperty("result").GetProperty("data")
            .EnumerateArray().Select(value => value.GetInt32()).ToArray();
        Assert.Equal(0, data.Length % 5);
        var occurrences = new List<(int Line, int Character, int Length)>();
        var line = 0;
        var character = 0;
        for (var index = 0; index < data.Length; index += 5)
        {
            line += data[index];
            character = data[index] == 0 ? character + data[index + 1] : data[index + 1];
            if ((line == 2 && character == 8) || (line == 3 && character == useCharacter))
            {
                occurrences.Add((line, character, data[index + 2]));
            }
        }
        Assert.Equal(new[] { (2, 8, 5), (3, useCharacter, 5) }, occurrences);
        await process.ShutdownAsync(6);
    }

    [Fact]
    public async Task Interface_member_Rename_rejects_changed_WithEvents_association_when_handler_segments_overlap()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/source-text-rename-tests/IContract.cls";
        var documents = new[]
        {
            (Uri: interfaceUri, Text: """
                VERSION 1.0 CLASS
                Attribute VB_Name = "IContract"
                Public Sub X_Changed()
                End Sub
                """),
            (Uri: "file:///C:/source-text-rename-tests/Publisher.cls", Text: """
                VERSION 1.0 CLASS
                Attribute VB_Name = "Publisher"
                Public Event Changed()
                """),
            (Uri: "file:///C:/source-text-rename-tests/Worker.cls", Text: """
                VERSION 1.0 CLASS
                Attribute VB_Name = "Worker"
                Implements IContract
                Private WithEvents IContract_X As Publisher
                Private Sub IContract_X_Changed()
                End Sub
                """)
        };
        foreach (var document in documents)
        {
            await process.SendNotificationAsync("textDocument/didOpen", new
            {
                textDocument = new
                {
                    uri = document.Uri, languageId = "vba", version = 1, text = document.Text
                }
            });
        }

        var preparation = await process.SendRequestAsync(2, "textDocument/prepareRename", new
        {
            textDocument = new { uri = interfaceUri }, position = new { line = 2, character = 11 }
        });
        Assert.False(preparation.TryGetProperty("error", out _), preparation.ToString());
        Assert.Equal("X_Changed", preparation.GetProperty("result").GetProperty("placeholder").GetString());
        Assert.Equal((2, 11, 2, 20), ReadRange(preparation.GetProperty("result").GetProperty("range")));

        // Replacing X_Changed also covers the boundary between IContract_X and Changed.
        var rename = await process.SendRequestAsync(3, "textDocument/rename", new
        {
            textDocument = new { uri = interfaceUri },
            position = new { line = 2, character = 11 },
            newName = "Updated"
        });
        Assert.False(rename.TryGetProperty("result", out _));
        var error = rename.GetProperty("error");
        Assert.Equal(-32803, error.GetProperty("code").GetInt32());
        Assert.Equal("resolutionChanged", error.GetProperty("data").GetProperty("reason").GetString());

        await process.ShutdownAsync(4);
    }

    private static string CreateSource(string[] lines, string newlineStyle, bool trailingNewline)
    {
        string[] lineEndings = newlineStyle switch
        {
            "crlf" => ["\r\n"],
            "lf" => ["\n"],
            "cr" => ["\r"],
            "mixed" => ["\r\n", "\r", "\n"],
            _ => throw new ArgumentException("Unknown fixture newline style.", nameof(newlineStyle))
        };
        return string.Concat(lines.Select((line, index) => line
            + (index < lines.Length - 1 || trailingNewline
                ? lineEndings[index % lineEndings.Length]
                : "")));
    }

    private static (int StartLine, int StartCharacter, int EndLine, int EndCharacter) ReadRange(JsonElement range)
        => (range.GetProperty("start").GetProperty("line").GetInt32(),
            range.GetProperty("start").GetProperty("character").GetInt32(),
            range.GetProperty("end").GetProperty("line").GetInt32(),
            range.GetProperty("end").GetProperty("character").GetInt32());
}
