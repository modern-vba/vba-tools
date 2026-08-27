using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class SourceInterfaceCallableContractLanguageServerProcessTests
{
    [Fact]
    public async Task Indexed_Property_parameter_keeps_ordinary_passing_while_value_passing_is_normalized()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });

        const string interfaceUri = "file:///C:/work/IIndexed.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "IIndexed"
            Public Property Let Item(ByRef index As Long, ByVal assigned As String)
            End Property
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Indexed.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Indexed"
            Implements IIndexed
            Private Property Let IIndexed_Item(ByVal itemIndex As Long, ByRef rhs As String)
            End Property
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));

        var notification = await process.WaitForDiagnosticsAsync(implementationUri);
        var diagnostic = Assert.Single(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleInterfaceMemberSignature");
        Assert.Equal(
            "Interface member 'IIndexed_Item' signature does not match any required Property Let contract.",
            diagnostic.GetProperty("message").GetString());
        var related = Assert.Single(
            diagnostic.GetProperty("relatedInformation").EnumerateArray());
        Assert.Equal(interfaceUri, related
            .GetProperty("location")
            .GetProperty("uri")
            .GetString());
        Assert.Equal(
            "Required contract: Property Let IIndexed_Item(ByRef index As Long, assigned As String). "
                + "Mismatches: parameter 1 passing: expected ByRef, found ByVal.",
            related.GetProperty("message").GetString());

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Indexed_Property_signature_help_tracks_the_physical_parameter_position()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/IIndexed.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "IIndexed"
            Public Property Let Item(ByVal index As Long, ByVal assigned As String)
            End Property
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Indexed.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Indexed"
            Implements IIndexed
            Private Property Let IIndexed_Item(ByVal itemIndex As Long, ByVal rhs As String)
            End Property
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));
        await process.WaitForDiagnosticsAsync(implementationUri);

        var firstParameterResponse = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            implementationUri,
            implementationText,
            "Long");
        Assert.Equal(
            0,
            firstParameterResponse
                .GetProperty("result")
                .GetProperty("activeParameter")
                .GetInt32());

        var response = await SendPositionRequestAsync(
            process,
            3,
            "textDocument/signatureHelp",
            implementationUri,
            implementationText,
            "rhs");
        var result = response.GetProperty("result");
        Assert.Equal(1, result.GetProperty("activeParameter").GetInt32());

        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task Optional_defaults_compare_their_evaluated_constant_values()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });

        const string interfaceUri = "file:///C:/work/IWorker.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "IWorker"
            Public Sub Run(Optional ByVal count As Long = 1 + 1)
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Worker.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Worker"
            Implements IWorker
            Private Sub IWorker_Run(Optional ByVal amount As Long = 3)
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));

        var notification = await process.WaitForDiagnosticsAsync(implementationUri);
        var diagnostic = Assert.Single(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleInterfaceMemberSignature");
        Assert.Equal(
            "Interface member 'IWorker_Run' signature does not match any required Sub contract.",
            diagnostic.GetProperty("message").GetString());
        var related = Assert.Single(
            diagnostic.GetProperty("relatedInformation").EnumerateArray());
        Assert.Equal(
            "Required contract: Sub IWorker_Run([count As Long]). "
                + "Mismatches: parameter 1 default: expected 2, found 3.",
            related.GetProperty("message").GetString());

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Optional_string_defaults_compare_their_evaluated_constant_values()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });

        const string interfaceUri = "file:///C:/work/IWorker.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "IWorker"
            Public Sub Run(Optional ByVal text As String = "a")
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Worker.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Worker"
            Implements IWorker
            Private Sub IWorker_Run(Optional ByVal value As String = "b")
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));

        var notification = await process.WaitForDiagnosticsAsync(implementationUri);
        var diagnostic = Assert.Single(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleInterfaceMemberSignature");
        var related = Assert.Single(
            diagnostic.GetProperty("relatedInformation").EnumerateArray());
        Assert.Equal(
            "Required contract: Sub IWorker_Run([text As String]). "
                + "Mismatches: parameter 1 default: expected \"a\", found \"b\".",
            related.GetProperty("message").GetString());

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Optional_floating_defaults_compare_their_evaluated_constant_values()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });

        const string interfaceUri = "file:///C:/work/IWorker.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "IWorker"
            Public Sub Run(Optional ByVal ratio As Double = 1.5)
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Worker.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Worker"
            Implements IWorker
            Private Sub IWorker_Run(Optional ByVal actual As Double = 2.5)
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));

        var notification = await process.WaitForDiagnosticsAsync(implementationUri);
        var diagnostic = Assert.Single(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleInterfaceMemberSignature");
        var related = Assert.Single(
            diagnostic.GetProperty("relatedInformation").EnumerateArray());
        Assert.Equal(
            "Required contract: Sub IWorker_Run([ratio As Double]). "
                + "Mismatches: parameter 1 default: expected 1.5, found 2.5.",
            related.GetProperty("message").GetString());

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Equivalent_Optional_default_spellings_fulfill_the_same_contract()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/IWorker.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "IWorker"
            Public Sub Run(Optional ByVal count As Long = 1 + 1)
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Worker.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Worker"
            Implements IWorker
            Private Sub IWorker_Run(Optional ByVal amount As Long = &H2)
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));

        var notification = await process.WaitForDiagnosticsAsync(implementationUri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            IsInterfaceFulfillmentDiagnostic);

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Equivalent_integral_and_floating_Optional_defaults_fulfill_the_same_contract()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/IWorker.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "IWorker"
            Public Sub Run(Optional ByVal ratio As Double = 1)
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Worker.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Worker"
            Implements IWorker
            Private Sub IWorker_Run(Optional ByVal actual As Double = 1#)
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));

        var notification = await process.WaitForDiagnosticsAsync(implementationUri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            IsInterfaceFulfillmentDiagnostic);

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Unevaluable_Optional_default_evidence_suppresses_a_conclusive_mismatch()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/IWorker.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "IWorker"
            Public Sub Run(Optional ByVal count As Long = MissingDefault)
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Worker.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Worker"
            Implements IWorker
            Private Sub IWorker_Run(Optional ByVal amount As Long = 3)
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));

        var notification = await process.WaitForDiagnosticsAsync(implementationUri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            IsInterfaceFulfillmentDiagnostic);

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Empty_Optional_default_evidence_suppresses_a_conclusive_mismatch()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/IWorker.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "IWorker"
            Public Sub Run(Optional ByVal value As Variant = Empty)
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Worker.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Worker"
            Implements IWorker
            Private Sub IWorker_Run(Optional ByVal actual As Variant = 1)
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));

        var notification = await process.WaitForDiagnosticsAsync(implementationUri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            IsInterfaceFulfillmentDiagnostic);

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Function_result_array_shape_participates_in_contract_fulfillment()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });

        const string interfaceUri = "file:///C:/work/IArray.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "IArray"
            Public Function Values() As Long()
            End Function
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/ArrayProvider.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ArrayProvider"
            Implements IArray
            Private Function IArray_Values() As Long
            End Function
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));

        var notification = await process.WaitForDiagnosticsAsync(implementationUri);
        var diagnostic = Assert.Single(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleInterfaceMemberSignature");
        Assert.Equal(
            "Interface member 'IArray_Values' signature does not match any required Function contract.",
            diagnostic.GetProperty("message").GetString());
        var related = Assert.Single(
            diagnostic.GetProperty("relatedInformation").EnumerateArray());
        Assert.Equal(
            "Required contract: Function IArray_Values() As Long(). "
                + "Mismatches: return array shape: expected array, found scalar.",
            related.GetProperty("message").GetString());

        await process.ShutdownAsync(2);
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

    private static bool IsInterfaceFulfillmentDiagnostic(System.Text.Json.JsonElement diagnostic)
        => diagnostic.GetProperty("code").GetString() is
            "validation.interfaceMemberNotImplemented"
                or "validation.interfaceMemberKindMismatch"
                or "validation.incompatibleInterfaceMemberSignature"
                or "validation.interfaceMemberContractNotFullyImplemented";

    private static Task<System.Text.Json.JsonElement> SendPositionRequestAsync(
        LanguageServerProcessHarness process,
        int id,
        string method,
        string uri,
        string text,
        string needle)
    {
        var characterOffset = text.IndexOf(needle, StringComparison.Ordinal);
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
