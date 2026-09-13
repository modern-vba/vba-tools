using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class BundledMsgBoxTests
{
    private const string Uri = "file:///C:/work/Example.bas";

    [Theory]
    [InlineData("MsgBox(\"hello\", Buttons:=4)")]
    [InlineData("MsgBox(\"hello\")")]
    [InlineData("MsgBox(\"hello\", HelpFile:=\"help.chm\", Context:=1)")]
    [InlineData("MsgBox(\"hello\", 4)")]
    [InlineData("MsgBox(\"hello\", Title:=\"Confirmation\")")]
    [InlineData("MsgBox(\"hello\", , \"Confirmation\")")]
    [InlineData("MsgBox(\"hello\", 4, \"Confirmation\", \"help.chm\", 1)")]
    public void Optional_arguments_can_be_omitted(string call)
    {
        var inventory = CreateInventory(call);

        Assert.DoesNotContain(inventory.GetProjectValidationDiagnostics(Uri), diagnostic =>
            diagnostic.Code == "validation.incompatibleCallArgumentList");
    }

    [Theory]
    [InlineData("MsgBox()", "parameter 'Prompt': required argument is missing")]
    [InlineData("MsgBox(Buttons:=4)", "parameter 'Prompt': required argument is missing")]
    [InlineData("MsgBox(\"hello\", Message:=\"unknown\")", "no parameter named 'Message'")]
    [InlineData("MsgBox(\"hello\", 4, \"Confirmation\", \"help.chm\", 1, 2)", "no parameter accepts this argument")]
    public void Invalid_arguments_still_produce_call_diagnostics(string call, string reason)
    {
        var inventory = CreateInventory(call);

        var diagnostic = Assert.Single(inventory.GetProjectValidationDiagnostics(Uri));

        Assert.Equal("validation.incompatibleCallArgumentList", diagnostic.Code);
        Assert.NotNull(diagnostic.Details);
        Assert.Contains(diagnostic.Details, detail =>
            detail.RelatedMessage.Contains(reason, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Server_accepts_omitted_Title_and_shows_optional_parameters()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync(
            enableProjectDiagnosticsSynchronization: true);
        await process.InitializeAsync();
        var checkpoint = process.CaptureProjectDiagnosticsCheckpoint();
        await process.SendNotificationAsync("textDocument/didOpen", new
        {
            textDocument = new
            {
                uri = Uri,
                languageId = "vba",
                version = 1,
                text = CreateSource("MsgBox(\"hello\", Buttons:=4)")
            }
        });

        var notification = await process.WaitForProjectDiagnosticsSettledAsync(Uri, 1, checkpoint);

        Assert.DoesNotContain(
            notification.GetProperty("params").GetProperty("diagnostics").EnumerateArray(),
            diagnostic => diagnostic.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        var response = await process.SendRequestAsync(2, "textDocument/signatureHelp", new
        {
            textDocument = new { uri = Uri },
            position = new { line = 3, character = "    result = MsgBox(\"hello\", Buttons:=".Length }
        });
        var result = response.GetProperty("result");
        var signature = Assert.Single(result.GetProperty("signatures").EnumerateArray());

        Assert.Equal(
            "Function MsgBox(Prompt, [Buttons], [Title], [HelpFile], [Context])",
            signature.GetProperty("label").GetString());
        Assert.Equal(1, result.GetProperty("activeParameter").GetInt32());

        await process.ShutdownAsync(3);
    }

    private static VbaSemanticInventory CreateInventory(string call)
        => VbaSemanticInventoryFixture.Create(
            new Dictionary<string, string> { [Uri] = CreateSource(call) },
            referenceCatalogs: VbaProjectReferenceCatalogSet.CreateBundled());

    private static string CreateSource(string call)
        => string.Join('\n', [
            "Attribute VB_Name = \"Example\"",
            "Public Sub Run()",
            "    Dim result As Variant",
            $"    result = {call}",
            "End Sub"
        ]);
}
