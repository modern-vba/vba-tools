using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class CallablePresentationSemanticTests
{
    [Fact]
    public void CatalogCompletionDetailUsesTheSameStructuredSignatureAsSignatureHelp()
    {
        const string uri = "file:///C:/work/Worker.bas";
        const string reference = "Visual Basic For Applications";
        var catalogs = VbaProjectReferenceCatalogSet.Empty.WithCatalog(new(
            reference, ["VBA"],
            [new(reference, "MsgBox", VbaSourceDefinitionKind.Procedure,
                Signature: new("legacy label is not structural evidence",
                    [new("Prompt", TypeReference: new("String"), IsByRef: false),
                     new("format", IsOptional: true, TypeReference: new("String"), IsByRef: true)
                         { DefaultExpression = "\"hidden\"" }],
                    CallableKind: VbaCallableKind.Function, SupportsNamedArguments: true),
                TypeReference: new("Long"),
                GlobalExposure: ReferenceDefinitionGlobalExposure.LibraryGlobal)]));
        var inventory = VbaSemanticInventoryFixture.Create(new Dictionary<string, string>
        {
            [uri] = """
                Attribute VB_Name = "Worker"
                Public Sub Run()
                    value = MsgBox(
                End Sub
                """
        }, referenceCatalogs: catalogs);

        var candidate = Assert.Single(inventory.GetCompletionResult(uri, 2, "    value = ".Length)
            .Candidates, candidate => candidate.Label == "MsgBox");
        var help = Assert.IsType<VbaSignatureHelp>(inventory.GetSignatureHelp(
            uri, 2, "    value = MsgBox(".Length));

        Assert.Equal(help.Signature.Label, candidate.Definition!.DeclarationLabel);
        Assert.Equal("Function MsgBox(Prompt As String, [ByRef format As String]) As Long", help.Signature.Label);
        Assert.Equal("MsgBox", candidate.Label);
        Assert.Equal(0, help.ActiveParameter);
    }

    [Fact]
    public void HostHandlerSignatureUsesOptionalBracketsAndOnlyKnownByRef()
    {
        const string uri = "file:///C:/work/Dialog.frm";
        var sources = VbaSemanticInventoryFixture.ProjectSourceDocuments(new Dictionary<string, string>
        {
            [uri] = """
                VERSION 5.00
                Begin VB.UserForm Dialog
                End
                Attribute VB_Name = "Dialog"
                Private Sub UserForm_Changed(ByVal values() As String, ByRef cancel As Boolean)
                End Sub
                """
        });
        var catalog = new VbaIntrinsicHostEventCatalog(
            VbaIntrinsicHostEventSourceKind.UserForm, "UserForm",
            [new VbaIntrinsicHostEvent(new("UserForm", "Changed"), new(
                [new("values", new VbaIntrinsicHostEventParameterType("String"),
                    VbaHostEventParameterPassing.ByVal, VbaHostEventParameterArrayShape.Array,
                    Optional: true, ParamArray: false),
                 new("cancel", new VbaIntrinsicHostEventParameterType("Boolean"),
                    VbaHostEventParameterPassing.ByRef, VbaHostEventParameterArrayShape.Scalar,
                    Optional: false, ParamArray: false)], "Change event"), true, true)]);
        var inventory = VbaSemanticInventory.Create(sources, intrinsicHostEventCatalog: catalog);

        var help = Assert.IsType<VbaSignatureHelp>(inventory.GetSignatureHelp(
            uri, 4, "Private Sub UserForm_Changed(ByVal ".Length));

        Assert.Equal("UserForm_Changed([values() As String], ByRef cancel As Boolean)",
            help.Signature.Label);
        Assert.Equal("[values() As String]", help.Signature.Parameters[0].Label);
        Assert.False(help.Signature.Parameters[0].IsByRef);
        Assert.True(help.Signature.Parameters[1].IsByRef);
        Assert.Equal(0, help.ActiveParameter);
    }

    [Fact]
    public void DerivedSetterContractOmitsByValAndRetainsAssignedValueSemantics()
    {
        const string contractUri = "file:///C:/work/ISettings.cls";
        const string implementationUri = "file:///C:/work/Settings.cls";
        var inventory = VbaSemanticInventoryFixture.Create(new Dictionary<string, string>
        {
            [contractUri] = """
                VERSION 1.0 CLASS
                Attribute VB_Name = "ISettings"
                Public Value As Long
                """,
            [implementationUri] = """
                VERSION 1.0 CLASS
                Attribute VB_Name = "Settings"
                Implements ISettings
                Private Property Let ISettings_Value(ByRef rhs As Long)
                End Property
                """
        });

        var help = Assert.IsType<VbaSignatureHelp>(inventory.GetSignatureHelp(
            implementationUri, 3, "Private Property Let ISettings_Value(ByRef ".Length));

        Assert.Equal("Property Let ISettings_Value(AssignedValue As Long)", help.Signature.Label);
        var parameter = Assert.Single(help.Signature.Parameters);
        Assert.Equal("AssignedValue As Long", parameter.Label);
        Assert.Equal("AssignedValue", parameter.Name);
        Assert.False(parameter.IsByRef);
        Assert.Equal(0, help.ActiveParameter);
    }
}
