using VbaTools.Semantics;
using VbaTools.Syntax;
using Xunit;

namespace VbaDev.Tests;

public sealed class SemanticInputAnalysisTests
{
    [Fact]
    public void SharedAnalysisKeepsTheCapturedIntrinsicHostEventContract()
    {
        const string uri = "file:///C:/semantic-inputs/Dialog.frm";
        var syntax = VbaSyntaxTree.ParseModule(uri,
            "VERSION 5.00\nBegin VB.Form Dialog\nEnd\nAttribute VB_Name = \"Dialog\"\nPrivate Function UserForm_Initialize() As Long\nEnd Function\n");
        var parameters = new List<VbaHostEventParameter>();
        var events = new List<VbaIntrinsicHostEvent>
        {
            new(new("UserForm", "Initialize"), new(parameters, "Initializes the form."), true, true)
        };
        var inputs = VbaProjectSemanticInputs.Capture(null, VbaProjectReferenceCatalogSet.Empty,
            new(VbaIntrinsicHostEventSourceKind.UserForm, "UserForm", events));
        events.Clear();

        var diagnostics = VbaProjectSourceAnalysis.Analyze([syntax], inputs);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("validation.eventHandlerMustBeSub", diagnostic.Code);
        Assert.Equal("Event handlers must be declared as Sub procedures.", diagnostic.Message);
        Assert.Equal(new VbaRange(new(4, 8), new(4, 16)), diagnostic.Range);
        Assert.Empty(VbaProjectSourceAnalysis.Analyze([syntax], VbaProjectSemanticInputs.Empty));
    }

    [Fact]
    public void SharedAnalysisUsesTheCapturedSelectedExternalCallable()
    {
        const string uri = "file:///C:/semantic-inputs/Caller.bas";
        var syntax = VbaSyntaxTree.ParseModule(uri,
            "Attribute VB_Name = \"Caller\"\nPublic Sub Run()\n    AcceptValue 1, 2\nEnd Sub\n");
        var names = new List<string> { "Example Library" };
        var parameters = new List<VbaCallableParameter> { new("value", TypeReference: new("Long"), IsByRef: false) };
        var definitions = new List<VbaProjectReferenceDefinition>
        {
            new("Example Library", "AcceptValue", VbaSourceDefinitionKind.Procedure,
                Signature: new("Sub AcceptValue(ByVal value As Long)", parameters,
                    CallableKind: VbaCallableKind.Sub, SupportsNamedArguments: true),
                GlobalExposure: ReferenceDefinitionGlobalExposure.LibraryGlobal)
        };
        var catalog = VbaProjectReferenceCatalogSet.Empty.WithCatalog(new("Example Library", ["Example"], definitions));
        var inputs = VbaProjectSemanticInputs.Capture(VbaReferenceSelection.Capture(names, null), catalog);
        names.Clear();
        definitions.Clear();
        parameters.Clear();

        var diagnostics = VbaProjectSourceAnalysis.Analyze([syntax], inputs);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(uri, diagnostic.SourceUri);
        Assert.Equal("validation.incompatibleCallArgumentList", diagnostic.Code);
        Assert.Equal("No available callable signature accepts this argument list.", diagnostic.Message);
        var detail = Assert.Single(diagnostic.Details!);
        Assert.Null(detail.Location);
        Assert.Contains("AcceptValue", detail.FallbackText, StringComparison.Ordinal);
        Assert.Empty(VbaProjectSourceAnalysis.Analyze([syntax], VbaProjectSemanticInputs.Empty));
    }
}
