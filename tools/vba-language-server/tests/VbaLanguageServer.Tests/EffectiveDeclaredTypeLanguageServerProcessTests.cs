using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class EffectiveDeclaredTypeLanguageServerProcessTests
{
    [Fact]
    public async Task One_owners_effective_types_drive_calls_Events_and_Implements_signatures()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();
        const string ownerUri = "file:///C:/work/TypeOwner.cls";
        const string ownerText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "TypeOwner"
            DefLng R
            Public Event Ready(rhs)
            Public Function Result(rhs)
            End Function
            """;
        await process.SendNotificationAsync("textDocument/didOpen", new
        {
            textDocument = new { uri = ownerUri, languageId = "vba", version = 1, text = ownerText }
        });
        await process.WaitForDiagnosticsAsync(ownerUri);
        const string consumerUri = "file:///C:/work/TypeConsumer.cls";
        var consumerLines = new[]
        {
            "VERSION 1.0 CLASS", "Attribute VB_Name = \"TypeConsumer\"", "DefStr R",
            "Implements TypeOwner", "Private WithEvents source As TypeOwner",
            "Private Sub source_Ready(ByRef rhs As Long)", "End Sub",
            "Private Function TypeOwner_Result(ByRef rhs As Long) As Long", "End Function",
            "Private Sub CallSource()", "    Dim value As String",
            "    value = source.Result(value)", "End Sub"
        };
        await process.SendNotificationAsync("textDocument/didOpen", new
        {
            textDocument = new { uri = consumerUri, languageId = "vba", version = 1, text = string.Join('\n', consumerLines) }
        });
        var notification = await process.WaitForDiagnosticsAsync(consumerUri);
        var codes = notification.GetProperty("params").GetProperty("diagnostics").EnumerateArray()
            .Select(diagnostic => diagnostic.GetProperty("code").GetString()).ToArray();
        Assert.Contains("validation.incompatibleCallArgumentList", codes);
        Assert.DoesNotContain("validation.incompatibleEventHandlerSignature", codes);
        Assert.DoesNotContain(codes, code => code?.Contains("interface", StringComparison.OrdinalIgnoreCase) == true);

        var requestId = 2;
        foreach (var (line, prefix) in new[]
        {
            (5, "Private Sub source_Ready("),
            (7, "Private Function TypeOwner_Result("),
            (11, "    value = source.Result(")
        })
        {
            var response = await process.SendRequestAsync(requestId++, "textDocument/signatureHelp", new
            {
                textDocument = new { uri = consumerUri }, position = new { line, character = prefix.Length }
            });
            var signature = Assert.Single(response.GetProperty("result").GetProperty("signatures").EnumerateArray());
            var label = signature.GetProperty("label").GetString()!;
            Assert.Contains("rhs As Long", label);
            if (line != 5)
            {
                Assert.EndsWith("As Long", label);
            }
            Assert.DoesNotContain("DefType", label);
        }
        await process.ShutdownAsync(requestId);
    }

    [Theory]
    [InlineData("DefLng R", "rhs As String", "", "String", "Long")]
    [InlineData("DefLng R", "rhs$", "As String", "String", "String")]
    [InlineData("", "rhs", "", "Variant", "Variant")]
    [InlineData("DefLng R", "rhs As MissingType", "As MissingType", "MissingType", "MissingType")]
    [InlineData("#If FEATURE Then\nDefLng R\n#End If", "rhs", "", null, null)]
    public async Task Signature_help_preserves_explicit_types_and_indeterminate_omissions(
        string directives, string parameter, string resultClause, string? expectedParameter, string? expectedResult)
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();
        const string uri = "file:///C:/work/TypePresentation.bas";
        var lines = new List<string> { "Attribute VB_Name = \"TypePresentation\"" };
        if (directives.Length != 0) { lines.AddRange(directives.Split('\n')); }
        lines.AddRange([
            $"Public Function Result({parameter}) {resultClause}".TrimEnd(),
            "End Function", "Public Sub Caller()", "    Dim value As String",
            "    value = Result(value)", "End Sub"
        ]);
        await process.SendNotificationAsync("textDocument/didOpen", new
        {
            textDocument = new { uri, languageId = "vba", version = 1, text = string.Join('\n', lines) }
        });
        await process.WaitForDiagnosticsAsync(uri);
        var response = await process.SendRequestAsync(2, "textDocument/signatureHelp", new
        {
            textDocument = new { uri },
            position = new { line = lines.Count - 2, character = "    value = Result(".Length }
        });
        var label = Assert.Single(response.GetProperty("result").GetProperty("signatures").EnumerateArray())
            .GetProperty("label").GetString()!;
        if (expectedParameter is null) { Assert.DoesNotContain("As Variant", label); }
        else { Assert.Contains($"rhs As {expectedParameter}", label); }
        if (expectedResult is not null) { Assert.EndsWith($"As {expectedResult}", label); }
        Assert.DoesNotContain("DefType", label, StringComparison.OrdinalIgnoreCase);
        await process.ShutdownAsync(3);
    }

    [Theory]
    [InlineData(2, "Public r", "rhs")]
    [InlineData(4, "    Dim r", "result")]
    public async Task Variable_hover_displays_the_same_effective_type(int line, string prefix, string name)
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();
        const string uri = "file:///C:/work/EffectiveVariables.bas";
        const string text = """
            Attribute VB_Name = "EffectiveVariables"
            DefLng R
            Public rhs
            Public Sub Caller()
                Dim result
            End Sub
            """;
        await process.SendNotificationAsync("textDocument/didOpen", new
        {
            textDocument = new { uri, languageId = "vba", version = 1, text }
        });
        await process.WaitForDiagnosticsAsync(uri);
        var hover = await process.SendRequestAsync(2, "textDocument/hover", new
        {
            textDocument = new { uri },
            position = new { line, character = prefix.Length }
        });
        var label = hover.GetProperty("result").GetProperty("contents").GetProperty("value").GetString();
        Assert.Contains($"{name} As Long", label, StringComparison.Ordinal);
        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Event_contracts_use_the_declaring_modules_effective_parameter_type()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();
        const string emitterUri = "file:///C:/work/Emitter.cls";
        const string emitterText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Emitter"
            DefLng R
            Public Event Ready(rhs)
            """;
        await process.SendNotificationAsync("textDocument/didOpen", new
        {
            textDocument = new { uri = emitterUri, languageId = "vba", version = 1, text = emitterText }
        });
        await process.WaitForDiagnosticsAsync(emitterUri);
        const string receiverUri = "file:///C:/work/Receiver.cls";
        const string receiverText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Receiver"
            DefStr R
            Private WithEvents source As Emitter
            Private Sub source_Ready(ByRef rhs As Long)
            End Sub
            """;
        await process.SendNotificationAsync("textDocument/didOpen", new
        {
            textDocument = new { uri = receiverUri, languageId = "vba", version = 1, text = receiverText }
        });
        var diagnostics = await process.WaitForDiagnosticsAsync(receiverUri);
        Assert.DoesNotContain(diagnostics.GetProperty("params").GetProperty("diagnostics").EnumerateArray(),
            diagnostic => diagnostic.GetProperty("code").GetString() == "validation.incompatibleEventHandlerSignature");
        var signature = await process.SendRequestAsync(2, "textDocument/signatureHelp", new
        {
            textDocument = new { uri = receiverUri },
            position = new { line = 4, character = "Private Sub source_Ready(ByRef rhs".Length }
        });
        var label = Assert.Single(signature.GetProperty("result").GetProperty("signatures").EnumerateArray())
            .GetProperty("label").GetString();
        Assert.Contains("rhs As Long", label, StringComparison.Ordinal);
        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Ordinary_ByRef_calls_compare_the_same_effective_parameter_type()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();
        const string uri = "file:///C:/work/EffectiveArguments.bas";
        const string text = """
            Attribute VB_Name = "EffectiveArguments"
            DefLng R
            Public Sub ReadValue(rhs)
            End Sub
            Public Sub Caller()
                Dim value As String
                ReadValue value
            End Sub
            """;
        await process.SendNotificationAsync("textDocument/didOpen", new
        {
            textDocument = new { uri, languageId = "vba", version = 1, text }
        });
        var diagnostics = await process.WaitForDiagnosticsAsync(uri);
        Assert.Contains(diagnostics.GetProperty("params").GetProperty("diagnostics").EnumerateArray(),
            diagnostic => diagnostic.GetProperty("code").GetString() == "validation.incompatibleCallArgumentList");
        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Module_DefType_types_ordinary_parameters_and_returns_in_editor_signatures()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();
        const string uri = "file:///C:/work/EffectiveTypes.bas";
        var lines = new[]
        {
            "Attribute VB_Name = \"EffectiveTypes\"",
            "DefLng R",
            "Public Function Result(rhs)",
            "End Function",
            "Public Sub Caller()",
            "    Dim value As Long",
            "    value = Result(value)",
            "End Sub"
        };
        await process.SendNotificationAsync("textDocument/didOpen", new
        {
            textDocument = new { uri, languageId = "vba", version = 1, text = string.Join('\n', lines) }
        });
        await process.WaitForDiagnosticsAsync(uri);

        var signature = await process.SendRequestAsync(2, "textDocument/signatureHelp", new
        {
            textDocument = new { uri },
            position = new { line = 6, character = "    value = Result(".Length }
        });
        var displayed = Assert.Single(signature.GetProperty("result").GetProperty("signatures").EnumerateArray());
        Assert.Equal("Function Result(ByRef rhs As Long) As Long", displayed.GetProperty("label").GetString());
        var hover = await process.SendRequestAsync(3, "textDocument/hover", new
        {
            textDocument = new { uri },
            position = new { line = 2, character = "Public Function Re".Length }
        });
        var hoverText = hover.GetProperty("result").GetProperty("contents").GetProperty("value").GetString();
        Assert.Contains("Function Result(ByRef rhs As Long) As Long", hoverText, StringComparison.Ordinal);
        Assert.DoesNotContain("DefType", hoverText, StringComparison.OrdinalIgnoreCase);
        await process.ShutdownAsync(4);
    }
}
