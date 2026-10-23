using System.Text.Json;
using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class ContractDeclarationSignatureHelpTests
{
    public static IEnumerable<object?[]> EditingCases()
    {
        foreach (var origin in new[] { "WithEvents", "Implements" })
        {
            yield return [origin, "(|)", 0];
            yield return [origin, "(ByVal first As Long|,)", 0];
            yield return [origin, "(ByVal first As Long,|)", 1];
            yield return [origin, "(first, second,|)", null];
            yield return [origin, "(ByRef values(1, 2) As Long,|)", 1];
            yield return [origin, "(Optional first As String = \"a,b\",|)", 1];
            yield return [origin, "(ByVal first As Long, _\n    |)", 1];
            yield return [origin, "(ByVal first As Long, |", 1];
        }
    }

    [Theory]
    [MemberData(nameof(EditingCases))]
    public void Contract_slots_follow_delimiters_not_written_parameter_count(
        string origin, string markedParameters, int? expected)
    {
        var (documents, text, line, character) = CreateSources(origin, markedParameters);
        var inventory = VbaSemanticInventoryFixture.Create(documents);
        var help = inventory.GetSignatureHelp(WorkerUri, line, character);
        Assert.NotNull(help);
        Assert.Equal(expected, help.ActiveParameter);
        Assert.All(help.Signatures, variant => Assert.Equal(expected, variant.ActiveParameter));
    }

    [Theory]
    [InlineData("Function")]
    [InlineData("Property Get")]
    public void Event_repair_guidance_does_not_require_the_correct_declaration_kind(string kind)
    {
        var (documents, _, line, character) = CreateSources("WithEvents", "(|)", kind);
        var help = VbaSemanticInventoryFixture.Create(documents)
            .GetSignatureHelp(WorkerUri, line, character);
        Assert.NotNull(help);
        Assert.Equal(0, help.ActiveParameter);
    }

    [Theory]
    [InlineData("WithEvents", "(Optional first As String = \"a|,b\")")]
    [InlineData("WithEvents", "(first, ' |comment")]
    [InlineData("Implements", "(first, ' |comment")]
    [InlineData("Implements", "()|")]
    public void Strings_comments_and_positions_after_the_declaration_have_no_help(
        string origin, string parameters)
    {
        var (documents, _, line, character) = CreateSources(origin, parameters);
        Assert.Null(VbaSemanticInventoryFixture.Create(documents)
            .GetSignatureHelp(WorkerUri, line, character));
    }

    [Fact]
    public void A_call_after_a_declaration_on_the_same_line_uses_call_site_resolution()
    {
        var (documents, _, line, character) = CreateSources(
            "WithEvents", "(): Echo(|)");
        documents["file:///C:/work/Calls.bas"] =
            "Attribute VB_Name = \"Calls\"\nPublic Sub Echo(ByVal value As Long)\nEnd Sub";
        var help = VbaSemanticInventoryFixture.Create(documents)
            .GetSignatureHelp(WorkerUri, line, character);
        Assert.NotNull(help);
        Assert.Contains("Echo", help.Signature.Label);
    }

    [Theory]
    [InlineData("Private Sub Class_Initialize(|)")]
    [InlineData("Private Sub publisher_Unrelated(|)")]
    public void Names_without_a_contract_do_not_receive_declaration_guidance(string marked)
    {
        var text = "Attribute VB_Name = \"Worker\"\n" + marked + "\nEnd Sub";
        var character = marked.IndexOf('|');
        text = text.Replace("|", "");
        var inventory = VbaSemanticInventoryFixture.Create(
            new Dictionary<string, string> { [WorkerUri] = text });
        Assert.Null(inventory.GetSignatureHelp(WorkerUri, 1, character));
    }

    [Theory]
    [InlineData("WithEvents", false)]
    [InlineData("WithEvents", true)]
    [InlineData("Implements", false)]
    [InlineData("Implements", true)]
    [InlineData("Accessor", false)]
    [InlineData("Accessor", true)]
    public async Task Declaration_editing_reaches_the_protocol_with_capability_aware_nulls(
        string origin, bool supportsNull)
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync(new
        {
            textDocument = new
            {
                signatureHelp = new
                {
                    signatureInformation = new
                    {
                        activeParameterSupport = true,
                        noActiveParameterSupport = supportsNull
                    }
                }
            }
        });
        var (documents, _, line, character) = CreateSources(origin, "(first, second,|)");
        foreach (var (uri, source) in documents)
        {
            await process.SendNotificationAsync("textDocument/didOpen", new
            {
                textDocument = new { uri, languageId = "vba", version = 1, text = source }
            });
            await process.WaitForDiagnosticsAsync(uri);
        }
        var response = await process.SendRequestAsync(2, "textDocument/signatureHelp", new
        {
            textDocument = new { uri = WorkerUri }, position = new { line, character }
        });
        var result = response.GetProperty("result");
        var signature = Assert.Single(result.GetProperty("signatures").EnumerateArray());
        if (supportsNull)
            Assert.Equal(JsonValueKind.Null, signature.GetProperty("activeParameter").ValueKind);
        else
            Assert.False(signature.TryGetProperty("activeParameter", out _));
        await process.ShutdownAsync(3);
    }

    private const string WorkerUri = "file:///C:/work/Worker.cls";

    private static (Dictionary<string, string> Documents, string Text, int Line, int Character)
        CreateSources(string origin, string parameters, string kind = "Sub")
    {
        var contract = origin == "WithEvents"
            ? "Public Event Changed(ByVal first As Long, ByVal second As String)"
            : origin == "Accessor"
                ? "Public Value As Long"
                : "Public Sub Changed(ByVal first As Long, ByVal second As String)\nEnd Sub";
        var context = origin == "WithEvents"
            ? "Private WithEvents publisher As Contract"
            : "Implements Contract";
        var name = origin == "WithEvents" ? "publisher_Changed"
            : origin == "Accessor" ? "Contract_Value" : "Contract_Changed";
        if (origin == "Accessor") kind = "Property Let";
        var marked = $"Attribute VB_Name = \"Worker\"\n{context}\nPrivate {kind} {name}{parameters}\nEnd {(kind.StartsWith("Property") ? "Property" : kind)}";
        var offset = marked.IndexOf('|');
        var line = marked[..offset].Count(character => character == '\n');
        var character = offset - marked.LastIndexOf('\n', offset) - 1;
        var text = marked.Remove(offset, 1);
        return (new Dictionary<string, string>
        {
            ["file:///C:/work/Contract.cls"] = "Attribute VB_Name = \"Contract\"\n" + contract,
            [WorkerUri] = text
        }, text, line, character);
    }
}
