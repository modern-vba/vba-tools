using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class RenameDeclarationRelationshipLanguageServerProcessTests
{
    [Fact]
    public async Task Function_name_completion_excludes_a_conflicting_result_local()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();
        const string ownerUri = "file:///C:/work/Owner.cls";
        const string ownerText = "VERSION 1.0 CLASS\nAttribute VB_Name = \"Owner\"\nPublic Function Result() As Long\nEnd Function";
        await process.SendNotificationAsync("textDocument/didOpen", new
        {
            textDocument = new { uri = ownerUri, languageId = "vba", version = 1, text = ownerText }
        });
        await process.WaitForDiagnosticsAsync(ownerUri);
        const string uri = "file:///C:/work/Implementer.cls";
        const string text = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Implementer"
            Implements Owner
            Private Function Owner_() As Long
                Dim Owner_Result As Long
            End Function
            """;
        await process.SendNotificationAsync("textDocument/didOpen", new
        {
            textDocument = new { uri, languageId = "vba", version = 1, text }
        });
        await process.WaitForDiagnosticsAsync(uri);
        var response = await process.SendRequestAsync(2, "textDocument/completion", new
        {
            textDocument = new { uri }, position = new { line = 3, character = "Private Function Owner_".Length }
        });
        Assert.Empty(response.GetProperty("result").EnumerateArray());
        await process.ShutdownAsync(3);
    }

    [Theory]
    [InlineData("DefLng R", "Public Sub Run(rhs)\n    Debug.Print rhs\nEnd Sub", "rhs", "value", "resolutionChanged")]
    [InlineData("DefLng R", "Public Function Result()\n    Result = 1\nEnd Function", "Result", "Value", "resolutionChanged")]
    [InlineData("DefLng R", "Public Property Get Result()\n    Result = 1\nEnd Property", "Result", "Value", "resolutionChanged")]
    [InlineData("DefLng R", "Public rhs", "rhs", "result", null)]
    [InlineData("DefLng R", "Public rhs As Long", "rhs", "value", null)]
    [InlineData("", "Public rhs", "rhs", "value", null)]
    [InlineData("#If FEATURE Then\nDefLng R\n#End If", "Public rhs", "rhs", "value", "analysisIncomplete")]
    [InlineData("DefLng R", "Public unrelated As MissingType\nPublic rhs", "rhs", "result", null)]
    public async Task Rename_preserves_each_affected_type_slot_and_leaves_unrelated_unknowns_alone(
        string directives, string declaration, string original, string newName, string? failureReason)
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();
        const string uri = "file:///C:/work/TypeRenameCases.cls";
        var text = "VERSION 1.0 CLASS\nAttribute VB_Name = \"TypeRenameCases\"\n"
            + directives + "\n" + declaration;
        await process.SendNotificationAsync("textDocument/didOpen", new
        {
            textDocument = new { uri, languageId = "vba", version = 1, text }
        });
        await process.WaitForDiagnosticsAsync(uri);
        var offset = text.IndexOf(original, StringComparison.Ordinal);
        var before = text[..offset];
        var response = await process.SendRequestAsync(2, "textDocument/rename", new
        {
            textDocument = new { uri },
            position = new { line = before.Count(character => character == '\n'), character = offset - before.LastIndexOf('\n') - 1 },
            newName
        });
        if (failureReason is not null)
        {
            Assert.True(response.TryGetProperty("error", out var error), response.ToString());
            Assert.Equal(failureReason, error.GetProperty("data").GetProperty("reason").GetString());
            Assert.False(response.TryGetProperty("result", out _));
        }
        else
        {
            Assert.False(response.TryGetProperty("error", out _), response.ToString());
            var edits = response.GetProperty("result").GetProperty("changes").GetProperty(uri).EnumerateArray().ToArray();
            Assert.NotEmpty(edits);
            Assert.All(edits, edit => Assert.Equal(newName, edit.GetProperty("newText").GetString()));
        }
        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Renaming_a_type_preserves_the_identity_of_its_typed_declarations()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();
        const string uri = "file:///C:/work/TypeIdentityRename.bas";
        const string text = """
            Attribute VB_Name = "TypeIdentityRename"
            Public Type Record
                Value As Long
            End Type
            Public state As Record
            Public Function ReadRecord(rhs As Record) As Record
                ReadRecord = rhs
            End Function
            """;
        await process.SendNotificationAsync("textDocument/didOpen", new
        {
            textDocument = new { uri, languageId = "vba", version = 1, text }
        });
        await process.WaitForDiagnosticsAsync(uri);
        var response = await process.SendRequestAsync(2, "textDocument/rename", new
        {
            textDocument = new { uri }, position = new { line = 1, character = "Public Type ".Length },
            newName = "Payload"
        });
        Assert.False(response.TryGetProperty("error", out _), response.ToString());
        var edits = response.GetProperty("result").GetProperty("changes").GetProperty(uri).EnumerateArray().ToArray();
        Assert.Equal(4, edits.Length);
        Assert.All(edits, edit => Assert.Equal("Payload", edit.GetProperty("newText").GetString()));
        await process.ShutdownAsync(3);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Different_Enum_members_require_preserved_bindings_even_without_a_declaration_collision(bool qualified)
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();
        const string uri = "file:///C:/work/EnumRename.bas";
        var text = """
            Attribute VB_Name = "EnumRename"
            Public Enum FirstState
                Ready = 1
            End Enum
            Public Enum SecondState
                Waiting = 2
            End Enum
            Public Sub UseEnums()
                Debug.Print FirstState.Ready
                Debug.Print SecondState.Waiting
            End Sub
            """;
        if (!qualified)
        {
            text = text.Replace("FirstState.Ready", "Ready", StringComparison.Ordinal);
        }
        await process.SendNotificationAsync("textDocument/didOpen", new
        {
            textDocument = new { uri, languageId = "vba", version = 1, text }
        });
        await process.WaitForDiagnosticsAsync(uri);
        var response = await process.SendRequestAsync(2, "textDocument/rename", new
        {
            textDocument = new { uri }, position = new { line = 5, character = 4 }, newName = "Ready"
        });

        if (qualified)
        {
            Assert.False(response.TryGetProperty("error", out _), response.ToString());
            var edits = response.GetProperty("result").GetProperty("changes").GetProperty(uri).EnumerateArray().ToArray();
            Assert.Equal(2, edits.Length);
            Assert.All(edits, edit => Assert.Equal("Ready", edit.GetProperty("newText").GetString()));
        }
        else
        {
            Assert.True(response.TryGetProperty("error", out var error), response.ToString());
            Assert.Equal("resolutionChanged", error.GetProperty("data").GetProperty("reason").GetString());
            Assert.False(response.TryGetProperty("result", out _));
        }
        await process.ShutdownAsync(3);
    }

    [Theory]
    [InlineData("Public Enum First\n    Ready = 1\n    Waiting = 2\nEnd Enum")]
    [InlineData("Public Ready As Long\nPublic Enum Second\n    Waiting = 2\nEnd Enum")]
    [InlineData("Private Const Ready As Long = 1\nPublic Enum Second\n    Waiting = 2\nEnd Enum")]
    public async Task Same_Enum_and_module_value_collisions_are_rejected_before_binding_proof(string declarations)
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();
        const string uri = "file:///C:/work/EnumCollision.bas";
        var text = "Attribute VB_Name = \"EnumCollision\"\n" + declarations;
        await process.SendNotificationAsync("textDocument/didOpen", new
        {
            textDocument = new { uri, languageId = "vba", version = 1, text }
        });
        await process.WaitForDiagnosticsAsync(uri);
        var offset = text.IndexOf("Waiting", StringComparison.Ordinal);
        var before = text[..offset];
        var response = await process.SendRequestAsync(2, "textDocument/rename", new
        {
            textDocument = new { uri },
            position = new { line = before.Count(character => character == '\n'), character = offset - before.LastIndexOf('\n') - 1 },
            newName = "Ready"
        });
        Assert.True(response.TryGetProperty("error", out var error), response.ToString());
        Assert.Equal("sameScopeCollision", error.GetProperty("data").GetProperty("reason").GetString());
        Assert.False(response.TryGetProperty("result", out _));
        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Rename_rejects_a_changed_DefType_variable_type()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();
        const string uri = "file:///C:/work/RenameTypes.bas";
        const string text = """
            Attribute VB_Name = "RenameTypes"
            DefLng R
            Public Sub Run()
                Dim rhs
                rhs = 1
            End Sub
            """;
        await process.SendNotificationAsync("textDocument/didOpen", new
        {
            textDocument = new { uri, languageId = "vba", version = 1, text }
        });
        await process.WaitForDiagnosticsAsync(uri);

        var response = await process.SendRequestAsync(2, "textDocument/rename", new
        {
            textDocument = new { uri }, position = new { line = 3, character = "    Dim ".Length },
            newName = "value"
        });

        Assert.True(response.TryGetProperty("error", out var error));
        Assert.Equal("resolutionChanged", error.GetProperty("data").GetProperty("reason").GetString());
        Assert.Contains("type", error.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(response.TryGetProperty("result", out _));
        await process.ShutdownAsync(3);
    }
}
