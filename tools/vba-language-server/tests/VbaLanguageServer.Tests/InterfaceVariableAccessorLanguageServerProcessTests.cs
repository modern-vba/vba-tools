using System.Text.Json;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class InterfaceVariableAccessorLanguageServerProcessTests
{
    [Fact]
    public async Task Long_public_variable_requires_a_missing_Property_Let()
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

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            Public Value As Long
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            Implements ISettings
            Private Property Get ISettings_Value() As Long
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
                == "validation.interfaceMemberNotImplemented");

        Assert.Equal(
            "Interface member 'ISettings_Value' requires a Property Let implementation.",
            diagnostic.GetProperty("message").GetString());
        Assert.Equal(2, diagnostic.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(11, diagnostic.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(20, diagnostic.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32());

        var related = Assert.Single(
            diagnostic.GetProperty("relatedInformation").EnumerateArray());
        Assert.Equal(interfaceUri, related.GetProperty("location").GetProperty("uri").GetString());
        Assert.Equal(
            "Required contract: Property Let ISettings_Value(ByVal AssignedValue As Long).",
            related.GetProperty("message").GetString());

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Interface_DefInt_determines_an_untyped_public_variable_contract()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/INumericSettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "INumericSettings"
            DefInt V
            Public Value
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/NumericSettings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "NumericSettings"
            Implements INumericSettings
            Private Property Get INumericSettings_Value() As Integer
            End Property
            Private Property Let INumericSettings_Value(ByVal AssignedValue As Integer)
            End Property
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
            diagnostic => diagnostic.GetProperty("code").GetString()?.StartsWith(
                "validation.interfaceMember",
                StringComparison.Ordinal) == true);

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task DefType_after_external_Declare_remains_module_level()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/INumericSettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "INumericSettings"
            Private Declare Function Noise Lib "kernel32" (): DefInt V
            Public Value
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/NumericSettings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "NumericSettings"
            Implements INumericSettings
            Private Declare Function INumericSettings_Noise Lib "kernel32" ()
            Private Property Get INumericSettings_Value() As Integer
            End Property
            Private Property Let INumericSettings_Value(ByVal rhs As Integer)
            End Property
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
    public async Task Procedure_body_DefType_does_not_change_an_interface_variable_contract()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            Public Sub Noise()
                DefLng V
            End Sub
            Public Value
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            Implements ISettings
            Private Sub ISettings_Noise()
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));

        var notification = await process.WaitForDiagnosticsAsync(implementationUri);
        Assert.Equal(
            [
                "Interface member 'ISettings_Value' requires a Property Get implementation.\nRequired contract: Property Get ISettings_Value() As Variant.",
                "Interface member 'ISettings_Value' requires a Property Let implementation.\nRequired contract: Property Let ISettings_Value(ByVal AssignedValue As Variant).",
                "Interface member 'ISettings_Value' requires a Property Set implementation.\nRequired contract: Property Set ISettings_Value(ByVal AssignedValue As Variant)."
            ],
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray()
                .Where(IsInterfaceFulfillmentDiagnostic)
                .Select(diagnostic => diagnostic.GetProperty("message").GetString()));

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task User_defined_type_body_DefType_does_not_change_an_interface_variable_contract()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            Private Type Noise
                DefLng V
            End Type
            Public Value
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            Implements ISettings
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));

        var notification = await process.WaitForDiagnosticsAsync(implementationUri);
        Assert.Equal(
            [
                "Interface member 'ISettings_Value' requires a Property Get implementation.\nRequired contract: Property Get ISettings_Value() As Variant.",
                "Interface member 'ISettings_Value' requires a Property Let implementation.\nRequired contract: Property Let ISettings_Value(ByVal AssignedValue As Variant).",
                "Interface member 'ISettings_Value' requires a Property Set implementation.\nRequired contract: Property Set ISettings_Value(ByVal AssignedValue As Variant)."
            ],
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray()
                .Where(IsInterfaceFulfillmentDiagnostic)
                .Select(diagnostic => diagnostic.GetProperty("message").GetString()));

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Procedure_body_Implements_does_not_create_an_interface_relationship()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            Public Value As Long
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            Private Sub Noise()
                Implements ISettings
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
    public async Task User_defined_type_body_Implements_does_not_create_an_interface_relationship()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            Public Value As Long
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            Private Type Noise
                Implements ISettings
            End Type
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
    public async Task Fixed_length_String_public_variable_contributes_no_accessor_contract()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/IFixedSettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "IFixedSettings"
            Public Code As String * 10
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/FixedSettings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "FixedSettings"
            Implements IFixedSettings
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
            diagnostic => diagnostic.GetProperty("code").GetString()?.StartsWith(
                "validation.interfaceMember",
                StringComparison.Ordinal) == true);

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Derived_accessor_suffix_definition_returns_the_owning_public_variable()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            Public Value As Long
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            Implements ISettings
            Private Property Get ISettings_Value() As Long
            End Property
            Private Property Let ISettings_Value(ByVal rhs As Long)
            End Property
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));
        await process.WaitForDiagnosticsAsync(implementationUri);

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/definition",
            implementationUri,
            implementationText,
            "ISettings_Value",
            "ISettings_".Length);
        var location = response.GetProperty("result");
        Assert.Equal(interfaceUri, location.GetProperty("uri").GetString());
        Assert.Equal(
            2,
            location.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(
            7,
            location.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Derived_accessor_definition_returns_every_case_variant_in_the_variable_family()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            #If FIRST_CONFIGURATION Then
            Public Value As Long
            #Else
            Public value As String
            #End If
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            Implements ISettings
            Private Property Get ISettings_Value() As Long
            End Property
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));
        await process.WaitForDiagnosticsAsync(implementationUri);

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/definition",
            implementationUri,
            implementationText,
            "ISettings_Value",
            "ISettings_".Length);
        var result = response.GetProperty("result");
        Assert.Equal(JsonValueKind.Array, result.ValueKind);
        var locations = result.EnumerateArray().ToArray();
        Assert.Equal(2, locations.Length);
        Assert.Equal(
            [3, 5],
            locations.Select(location => location
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32()));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Accessor_definition_returns_variable_variants_that_do_not_derive_that_accessor_kind()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            #If VALUE_TYPE Then
            Public Value As Long
            #Else
            Public Value As Object
            #End If
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            Implements ISettings
            Private Property Let ISettings_Value(ByVal rhs As Long)
            End Property
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));
        await process.WaitForDiagnosticsAsync(implementationUri);

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/definition",
            implementationUri,
            implementationText,
            "ISettings_Value",
            "ISettings_".Length);
        var locations = response
            .GetProperty("result")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(
            [3, 5],
            locations.Select(location => location
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32()));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Accessor_definition_returns_invalid_variable_variants_in_the_owning_family()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            #If VALUE_TYPE Then
            Public Value As Long
            #Else
            Public Value(0 To 1) As Long
            #End If
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            Implements ISettings
            Private Property Let ISettings_Value(ByVal rhs As Long)
            End Property
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));
        await process.WaitForDiagnosticsAsync(implementationUri);

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/definition",
            implementationUri,
            implementationText,
            "ISettings_Value",
            "ISettings_".Length);
        var locations = response
            .GetProperty("result")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(
            [3, 5],
            locations.Select(location => location
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32()));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Derived_Property_Let_signature_help_uses_AssignedValue_contract()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            Public Value As Long
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            Implements ISettings
            Private Property Get ISettings_Value() As Long
            End Property
            Private Property Let ISettings_Value(ByRef rhs As Long)
            End Property
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));
        await process.WaitForDiagnosticsAsync(implementationUri);

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            implementationUri,
            implementationText,
            "rhs");
        var result = response.GetProperty("result");
        var signature = Assert.Single(
            result.GetProperty("signatures").EnumerateArray());
        Assert.Equal(
            "Property Let ISettings_Value(ByVal AssignedValue As Long)",
            signature.GetProperty("label").GetString());
        Assert.Equal(
            "ByVal AssignedValue As Long",
            Assert.Single(signature.GetProperty("parameters").EnumerateArray())
                .GetProperty("label")
                .GetString());
        Assert.Equal(0, result.GetProperty("activeParameter").GetInt32());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Conditional_variable_variants_remain_separate_in_accessor_signature_help()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            #If FIRST_CONFIGURATION Then
            Public Value As Long
            #Else
            Public value As String
            #End If
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            Implements ISettings
            Private Property Let ISettings_Value(ByRef rhs As Long)
            End Property
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));
        await process.WaitForDiagnosticsAsync(implementationUri);

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            implementationUri,
            implementationText,
            "rhs");
        var result = response.GetProperty("result");
        Assert.Equal(
            [
                "Property Let ISettings_Value(ByVal AssignedValue As Long) [#If]",
                "Property Let ISettings_value(ByVal AssignedValue As String) [#If]"
            ],
            result
                .GetProperty("signatures")
                .EnumerateArray()
                .Select(signature => signature.GetProperty("label").GetString()));
        Assert.Equal(0, result.GetProperty("activeSignature").GetInt32());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Conditional_DefType_directives_apply_to_their_physical_variable_variants()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            #If FIRST_CONFIGURATION Then
            DefInt V
            Public Value
            #Else
            DefStr V
            Public Value
            #End If
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            Implements ISettings
            Private Property Let ISettings_Value(ByRef rhs As Variant)
            End Property
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));
        await process.WaitForDiagnosticsAsync(implementationUri);

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            implementationUri,
            implementationText,
            "rhs");
        Assert.Equal(
            [
                "Property Let ISettings_Value(ByVal AssignedValue As Integer) [#If]",
                "Property Let ISettings_Value(ByVal AssignedValue As String) [#If]"
            ],
            response
                .GetProperty("result")
                .GetProperty("signatures")
                .EnumerateArray()
                .Select(signature => signature.GetProperty("label").GetString()));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Conditional_Implements_relationships_each_contribute_accessor_signature_help_provenance()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            Public Value As Long
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            #If FIRST_CONFIGURATION Then
            Implements ISettings
            #Else
            Implements ISettings
            #End If
            Private Property Let ISettings_Value(ByRef rhs As Long)
            End Property
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));
        await process.WaitForDiagnosticsAsync(implementationUri);

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            implementationUri,
            implementationText,
            "rhs");
        var result = response.GetProperty("result");
        Assert.Equal(
            [
                "Property Let ISettings_Value(ByVal AssignedValue As Long) [#If]",
                "Property Let ISettings_Value(ByVal AssignedValue As Long) [#If]"
            ],
            result
                .GetProperty("signatures")
                .EnumerateArray()
                .Select(signature => signature.GetProperty("label").GetString()));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Incompatible_Property_Get_return_reports_the_required_contract()
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

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            Public Value As Long
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            Implements ISettings
            Private Property Get ISettings_Value() As String
            End Property
            Private Property Let ISettings_Value(ByVal rhs As Long)
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
            "Interface member 'ISettings_Value' signature does not match any required Property Get contract.",
            diagnostic.GetProperty("message").GetString());
        Assert.Equal(3, diagnostic.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(21, diagnostic.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(48, diagnostic.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32());

        var related = Assert.Single(
            diagnostic.GetProperty("relatedInformation").EnumerateArray());
        Assert.Equal(interfaceUri, related.GetProperty("location").GetProperty("uri").GetString());
        Assert.Equal(
            "Required contract: Property Get ISettings_Value() As Long. Mismatches: return type: expected Long, found String.",
            related.GetProperty("message").GetString());

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Wrong_kind_Sub_suppresses_missing_accessor_diagnostics()
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

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            Public Value As Variant
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            Implements ISettings
            Private Sub ISettings_Value()
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));

        var notification = await process.WaitForDiagnosticsAsync(implementationUri);
        var interfaceDiagnostics = notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .Where(candidate => candidate.GetProperty("code").GetString()?.StartsWith(
                "validation.interfaceMember",
                StringComparison.Ordinal) == true)
            .ToArray();
        var diagnostic = Assert.Single(interfaceDiagnostics);
        Assert.Equal(
            "validation.interfaceMemberKindMismatch",
            diagnostic.GetProperty("code").GetString());
        Assert.Equal(
            "Interface member 'ISettings_Value' requires Property Get, Property Let, or Property Set, not Sub.",
            diagnostic.GetProperty("message").GetString());
        Assert.Equal(8, diagnostic.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(11, diagnostic.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32());

        var related = diagnostic
            .GetProperty("relatedInformation")
            .EnumerateArray()
            .Select(item => item.GetProperty("message").GetString()!)
            .ToArray();
        Assert.Equal(
            [
                "Required contract: Property Get ISettings_Value() As Variant.",
                "Required contract: Property Let ISettings_Value(ByVal AssignedValue As Variant).",
                "Required contract: Property Set ISettings_Value(ByVal AssignedValue As Variant)."
            ],
            related);

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Wrong_kind_diagnostic_uses_the_first_physical_contract_name_casing()
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

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            #If VALUE_TYPE Then
            Public value As Long
            #Else
            Public Value As Object
            #End If
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            Implements ISettings
            Private Sub ISettings_value()
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
                == "validation.interfaceMemberKindMismatch");
        Assert.Equal(
            "Interface member 'ISettings_value' requires Property Get, Property Let, or Property Set, not Sub.",
            diagnostic.GetProperty("message").GetString());

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Continued_Property_Set_header_reports_one_wrong_kind_diagnostic()
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

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            Public Value As Long
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            Implements ISettings
            Private Property _
                Set ISettings_Value(ByVal rhs As Object)
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
            IsInterfaceFulfillmentDiagnostic);
        Assert.Equal(
            "validation.interfaceMemberKindMismatch",
            diagnostic.GetProperty("code").GetString());
        Assert.Equal(
            "Interface member 'ISettings_Value' requires Property Get or Property Let, not Property Set.",
            diagnostic.GetProperty("message").GetString());
        var range = diagnostic.GetProperty("range");
        Assert.Equal(3, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(8, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(4, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(7, range.GetProperty("end").GetProperty("character").GetInt32());

        var related = diagnostic
            .GetProperty("relatedInformation")
            .EnumerateArray()
            .Select(item => item.GetProperty("message").GetString()!)
            .ToArray();
        Assert.Equal(
            [
                "Required contract: Property Get ISettings_Value() As Long.",
                "Required contract: Property Let ISettings_Value(ByVal AssignedValue As Long)."
            ],
            related);

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Conditional_contract_variants_report_partial_Get_coverage()
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

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            #If Win64 Then
            Public Value As Long
            #Else
            Public Value As String
            #End If
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            Implements ISettings
            Private Property Get ISettings_Value() As Long
            End Property
            #If Win64 Then
            Private Property Let ISettings_Value(ByVal rhs As Long)
            End Property
            #Else
            Private Property Let ISettings_Value(ByVal rhs As String)
            End Property
            #End If
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));

        var notification = await process.WaitForDiagnosticsAsync(implementationUri);
        var fulfillmentDiagnostics = notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .Where(IsInterfaceFulfillmentDiagnostic)
            .ToArray();
        var diagnostic = Assert.Single(fulfillmentDiagnostics);
        Assert.Equal(
            "validation.interfaceMemberContractNotFullyImplemented",
            diagnostic.GetProperty("code").GetString());
        Assert.Equal(
            "Interface member 'ISettings_Value' does not implement every required Property Get contract.",
            diagnostic.GetProperty("message").GetString());
        Assert.Equal(11, diagnostic.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(20, diagnostic.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32());

        var related = Assert.Single(
            diagnostic.GetProperty("relatedInformation").EnumerateArray());
        Assert.Equal(interfaceUri, related.GetProperty("location").GetProperty("uri").GetString());
        Assert.Equal(5, related.GetProperty("location").GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(
            "Required contract: Property Get ISettings_Value() As String [#If].",
            related.GetProperty("message").GetString());

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Conditional_contract_and_implementation_variants_compare_as_a_Cartesian_product()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            #If Win64 Then
            Public Value As Long
            #Else
            Public Value As String
            #End If
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            Implements ISettings
            #If Win64 Then
            Private Property Get ISettings_Value() As String
            End Property
            Private Property Let ISettings_Value(ByVal rhs As String)
            End Property
            #Else
            Private Property Get ISettings_Value() As Long
            End Property
            Private Property Let ISettings_Value(ByVal rhs As Long)
            End Property
            #End If
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
    public async Task Property_Let_parameter_count_uses_shared_mismatch_grammar()
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

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            Public Value As Long
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            Implements ISettings
            Private Property Let ISettings_Value(ByVal index As Long, ByVal rhs As Long)
            End Property
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));

        var notification = await process.WaitForDiagnosticsAsync(implementationUri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleInterfaceMemberSignature");
        var related = Assert.Single(
            diagnostic.GetProperty("relatedInformation").EnumerateArray());
        Assert.Equal(
            "Required contract: Property Let ISettings_Value(ByVal AssignedValue As Long). Mismatches: parameter count: expected 1, found 2.",
            related.GetProperty("message").GetString());

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Property_Let_maps_the_final_value_after_an_index_parameter()
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

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            Public Value As Long
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            Implements ISettings
            Private Property Let ISettings_Value(ByVal index As Long, ByVal rhs As String)
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
        var related = Assert.Single(
            diagnostic.GetProperty("relatedInformation").EnumerateArray());
        Assert.Equal(
            "Required contract: Property Let ISettings_Value(ByVal AssignedValue As Long). Mismatches: parameter count: expected 1, found 2; value parameter type: expected Long, found String.",
            related.GetProperty("message").GetString());

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Optional_Property_Let_value_is_conclusively_incompatible()
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

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            Public Value As Variant
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            Implements ISettings
            Private Property Let ISettings_Value(Optional ByVal rhs As Variant)
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
        var related = Assert.Single(
            diagnostic.GetProperty("relatedInformation").EnumerateArray());
        Assert.Equal(
            "Required contract: Property Let ISettings_Value(ByVal AssignedValue As Variant). Mismatches: value parameter role: expected required, found Optional.",
            related.GetProperty("message").GetString());

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Property_Let_value_mismatch_reasons_use_shared_stable_order()
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

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            Public Value As Long
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            Implements ISettings
            Private Property Let ISettings_Value(ParamArray rhs() As String)
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
        var related = Assert.Single(
            diagnostic.GetProperty("relatedInformation").EnumerateArray());
        Assert.Equal(
            "Required contract: Property Let ISettings_Value(ByVal AssignedValue As Long). Mismatches: value parameter type: expected Long, found String; value parameter array shape: expected scalar, found array; value parameter role: expected required, found ParamArray.",
            related.GetProperty("message").GetString());

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Property_Get_requires_the_exact_qualified_type_identity()
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

        const string firstUri = "file:///C:/work/First.bas";
        const string firstText = """
            Attribute VB_Name = "First"
            Public Type Payload
                Value As Long
            End Type
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(firstUri, firstText));
        await process.WaitForDiagnosticsAsync(firstUri);

        const string secondUri = "file:///C:/work/Second.bas";
        const string secondText = """
            Attribute VB_Name = "Second"
            Public Type Payload
                Value As Long
            End Type
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(secondUri, secondText));
        await process.WaitForDiagnosticsAsync(secondUri);

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            Public Value As First.Payload
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            Implements ISettings
            Private Property Get ISettings_Value() As Second.Payload
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
        var related = Assert.Single(
            diagnostic.GetProperty("relatedInformation").EnumerateArray());
        Assert.Equal(
            "Required contract: Property Get ISettings_Value() As First.Payload. Mismatches: return type: expected First.Payload, found Second.Payload.",
            related.GetProperty("message").GetString());

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Conclusive_parameter_count_mismatch_survives_indeterminate_type_evidence()
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

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            #If Win64 Then
            Public Value As MissingOne
            #Else
            Public Value As MissingTwo
            #End If
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            Implements ISettings
            Private Property Get ISettings_Value(ByVal index As Long) As MissingImplementation
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
        var related = diagnostic
            .GetProperty("relatedInformation")
            .EnumerateArray()
            .Select(item => item.GetProperty("message").GetString()!)
            .ToArray();
        Assert.Equal(
            [
                "Required contract: Property Get ISettings_Value() As MissingOne [#If]. Mismatches: parameter count: expected 0, found 1.",
                "Required contract: Property Get ISettings_Value() As MissingTwo [#If]. Mismatches: parameter count: expected 0, found 1."
            ],
            related);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.interfaceMemberContractNotFullyImplemented");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Unresolved_contract_type_suppresses_a_conclusive_type_mismatch()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            Public Value As MissingType
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            Implements ISettings
            Private Property Get ISettings_Value() As Long
            End Property
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
            candidate => candidate.GetProperty("code").GetString() is
                "validation.interfaceMemberNotImplemented"
                    or "validation.interfaceMemberKindMismatch"
                    or "validation.incompatibleInterfaceMemberSignature"
                    or "validation.interfaceMemberContractNotFullyImplemented");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Incomplete_conditional_ownership_suppresses_interface_fulfillment_diagnostics()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            Public Value As Long
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            #If Win64 Then
            Implements ISettings
            Private Property Get ISettings_Value() As String
            End Property
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
    public async Task Incomplete_interface_contract_ownership_suppresses_fulfillment_diagnostics()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            #If Win64 Then
            Public Value As Long
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            Implements ISettings
            Private Property Get ISettings_Value() As String
            End Property
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
    public async Task Incomplete_implementation_ownership_suppresses_only_its_accessor_kind()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            Public Value As Long
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            Implements ISettings
            #If Win64 Then
            Private Property Get ISettings_Value() As String
            End Property
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));

        var notification = await process.WaitForDiagnosticsAsync(implementationUri);
        var fulfillmentDiagnostics = notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .Where(IsInterfaceFulfillmentDiagnostic)
            .ToArray();
        var missing = Assert.Single(fulfillmentDiagnostics);
        Assert.Equal(
            "validation.interfaceMemberNotImplemented",
            missing.GetProperty("code").GetString());
        Assert.Contains(
            "Property Let",
            missing.GetProperty("message").GetString());

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Incomplete_wrong_kind_evidence_does_not_hide_conclusive_contract_failures()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            Public Value As Long
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            Implements ISettings
            Private Property Get ISettings_Value() As String
            End Property
            #If Win64 Then
            Private Sub ISettings_Value()
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));

        var notification = await process.WaitForDiagnosticsAsync(implementationUri);
        var diagnosticCodes = notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .Select(diagnostic => diagnostic.GetProperty("code").GetString())
            .ToArray();
        Assert.Contains(
            "validation.incompatibleInterfaceMemberSignature",
            diagnosticCodes);
        Assert.Contains(
            "validation.interfaceMemberNotImplemented",
            diagnosticCodes);
        Assert.DoesNotContain(
            "validation.interfaceMemberKindMismatch",
            diagnosticCodes);

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Signature_mismatch_uses_two_line_fallback_without_related_information()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            Public Value As Long
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            Implements ISettings
            Private Property Get ISettings_Value() As String
            End Property
            Private Property Let ISettings_Value(ByVal rhs As Long)
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
            "Interface member 'ISettings_Value' signature does not match any required Property Get contract.\n"
                + "Expected signature: Property Get ISettings_Value() As Long.\n"
                + "Mismatches: return type: expected Long, found String.",
            diagnostic.GetProperty("message").GetString());
        Assert.False(diagnostic.TryGetProperty("relatedInformation", out _));

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Signature_mismatch_range_stops_before_colon_terminated_Property_body()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            Public Value As Long
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            Implements ISettings
            Private Property Get ISettings_Value$( _
            ): ISettings_Value = CStr(1)
            End Property
            Private Property Let ISettings_Value(ByVal rhs As Long)
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
        var range = diagnostic.GetProperty("range");
        Assert.Equal(3, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(21, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(4, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(1, range.GetProperty("end").GetProperty("character").GetInt32());

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Signature_mismatch_range_excludes_trailing_Static()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            Public Value As Long
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            Implements ISettings
            Private Property Get ISettings_Value() As String Static
            End Property
            Private Property Let ISettings_Value(ByVal rhs As Long)
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
        var range = diagnostic.GetProperty("range");
        Assert.Equal(3, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(21, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(3, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(48, range.GetProperty("end").GetProperty("character").GetInt32());

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Comma_separated_public_variables_keep_declarator_local_types()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            Public first, second As String
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            Implements ISettings
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));

        var notification = await process.WaitForDiagnosticsAsync(implementationUri);
        var messages = notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .Where(IsInterfaceFulfillmentDiagnostic)
            .Select(diagnostic => diagnostic.GetProperty("message").GetString()!)
            .ToArray();
        Assert.Equal(
            [
                "Interface member 'ISettings_first' requires a Property Get implementation.\nRequired contract: Property Get ISettings_first() As Variant.",
                "Interface member 'ISettings_first' requires a Property Let implementation.\nRequired contract: Property Let ISettings_first(ByVal AssignedValue As Variant).",
                "Interface member 'ISettings_first' requires a Property Set implementation.\nRequired contract: Property Set ISettings_first(ByVal AssignedValue As Variant).",
                "Interface member 'ISettings_second' requires a Property Get implementation.\nRequired contract: Property Get ISettings_second() As String.",
                "Interface member 'ISettings_second' requires a Property Let implementation.\nRequired contract: Property Let ISettings_second(ByVal AssignedValue As String)."
            ],
            messages);

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Type_declaration_character_determines_the_public_variable_contract()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            Public Count%
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            Implements ISettings
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));

        var notification = await process.WaitForDiagnosticsAsync(implementationUri);
        var messages = notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .Where(IsInterfaceFulfillmentDiagnostic)
            .Select(diagnostic => diagnostic.GetProperty("message").GetString()!)
            .ToArray();
        Assert.Equal(
            [
                "Interface member 'ISettings_Count' requires a Property Get implementation.\nRequired contract: Property Get ISettings_Count() As Integer.",
                "Interface member 'ISettings_Count' requires a Property Let implementation.\nRequired contract: Property Let ISettings_Count(ByVal AssignedValue As Integer)."
            ],
            messages);

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Implementing_class_DefType_does_not_change_the_interface_contract()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            Public Value
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            DefLng A-Z
            Implements ISettings
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));

        var notification = await process.WaitForDiagnosticsAsync(implementationUri);
        var messages = notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .Where(IsInterfaceFulfillmentDiagnostic)
            .Select(diagnostic => diagnostic.GetProperty("message").GetString()!)
            .ToArray();
        Assert.Equal(
            [
                "Interface member 'ISettings_Value' requires a Property Get implementation.\nRequired contract: Property Get ISettings_Value() As Variant.",
                "Interface member 'ISettings_Value' requires a Property Let implementation.\nRequired contract: Property Let ISettings_Value(ByVal AssignedValue As Variant).",
                "Interface member 'ISettings_Value' requires a Property Set implementation.\nRequired contract: Property Set ISettings_Value(ByVal AssignedValue As Variant)."
            ],
            messages);

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Implementing_class_DefType_applies_only_to_its_implicit_signature_types()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            Public Value As Long
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            DefLng I, R
            Implements ISettings
            Private Property Get ISettings_Value()
            End Property
            Private Property Let ISettings_Value(rhs)
            End Property
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
    public async Task Conditional_implementation_DefType_applies_to_the_same_Property_Get_variant()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            Public Value As Long
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            Implements ISettings
            #If Win64 Then
            DefLng I
            Private Property Get ISettings_Value()
            End Property
            #End If
            Private Property Let ISettings_Value(ByVal rhs As Long)
            End Property
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
    public async Task Conditional_implementation_DefType_applies_to_the_same_Property_Let_value_variant()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            Public Value As Long
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            Implements ISettings
            Private Property Get ISettings_Value() As Long
            End Property
            #If Win64 Then
            DefLng R
            Private Property Let ISettings_Value(ByVal rhs)
            End Property
            #End If
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
    public async Task Object_public_variable_requires_Get_and_Set_contracts()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            Public Target As Object
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            Implements ISettings
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));

        var notification = await process.WaitForDiagnosticsAsync(implementationUri);
        var messages = notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .Where(IsInterfaceFulfillmentDiagnostic)
            .Select(diagnostic => diagnostic.GetProperty("message").GetString()!)
            .ToArray();
        Assert.Equal(
            [
                "Interface member 'ISettings_Target' requires a Property Get implementation.\nRequired contract: Property Get ISettings_Target() As Object.",
                "Interface member 'ISettings_Target' requires a Property Set implementation.\nRequired contract: Property Set ISettings_Target(ByVal AssignedValue As Object)."
            ],
            messages);

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Resolved_class_public_variable_requires_Get_and_Set_contracts()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string classUri = "file:///C:/work/Widget.cls";
        const string classText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Widget"
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(classUri, classText));
        await process.WaitForDiagnosticsAsync(classUri);

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            Public Target As Widget
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            Implements ISettings
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));

        var notification = await process.WaitForDiagnosticsAsync(implementationUri);
        var messages = notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .Where(IsInterfaceFulfillmentDiagnostic)
            .Select(diagnostic => diagnostic.GetProperty("message").GetString()!)
            .ToArray();
        Assert.Equal(
            [
                "Interface member 'ISettings_Target' requires a Property Get implementation.\nRequired contract: Property Get ISettings_Target() As Widget.",
                "Interface member 'ISettings_Target' requires a Property Set implementation.\nRequired contract: Property Set ISettings_Target(ByVal AssignedValue As Widget)."
            ],
            messages);

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Resolved_Enum_public_variable_requires_Get_and_Let_contracts()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string enumUri = "file:///C:/work/Contracts.bas";
        const string enumText = """
            Attribute VB_Name = "Contracts"
            Public Enum Mode
                Automatic = 1
            End Enum
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(enumUri, enumText));
        await process.WaitForDiagnosticsAsync(enumUri);

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            Public Current As Mode
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            Implements ISettings
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));

        var notification = await process.WaitForDiagnosticsAsync(implementationUri);
        var messages = notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .Where(IsInterfaceFulfillmentDiagnostic)
            .Select(diagnostic => diagnostic.GetProperty("message").GetString()!)
            .ToArray();
        Assert.Equal(
            [
                "Interface member 'ISettings_Current' requires a Property Get implementation.\nRequired contract: Property Get ISettings_Current() As Mode.",
                "Interface member 'ISettings_Current' requires a Property Let implementation.\nRequired contract: Property Let ISettings_Current(ByVal AssignedValue As Mode)."
            ],
            messages);

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Public_array_contributes_no_accessor_contract()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            Public Values() As Long
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            Implements ISettings
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
    public async Task As_New_unresolved_type_contributes_only_the_Get_contract()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/ISettings.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ISettings"
            Public Target As New MissingType
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Settings.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Settings"
            Implements ISettings
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));

        var notification = await process.WaitForDiagnosticsAsync(implementationUri);
        var messages = notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .Where(IsInterfaceFulfillmentDiagnostic)
            .Select(diagnostic => diagnostic.GetProperty("message").GetString()!)
            .ToArray();
        Assert.Equal(
            [
                "Interface member 'ISettings_Target' requires a Property Get implementation.\nRequired contract: Property Get ISettings_Target() As MissingType."
            ],
            messages);

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

    private static bool IsInterfaceFulfillmentDiagnostic(JsonElement diagnostic)
        => diagnostic.GetProperty("code").GetString() is
            "validation.interfaceMemberNotImplemented"
                or "validation.interfaceMemberKindMismatch"
                or "validation.incompatibleInterfaceMemberSignature"
                or "validation.interfaceMemberContractNotFullyImplemented";

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
