using VbaTools.Semantics;
using VbaTools.Syntax;
using Xunit;

namespace VbaDev.Tests;

public sealed class SemanticInputAnalysisTests
{
    [Fact]
    public void SharedAnalysisPreservesEscapedJapaneseDocumentIdentityAndSourceShadowing()
    {
        const string currentUri =
            "file:///c%3A/projects/%E6%9B%B8%E5%BC%8F%E3%81%A7%E3%83%86%E3%82%AD%E3%82%B9%E3%83%88%E6%8A%BD%E5%87%BA/Worker.cls";
        var current = VbaSyntaxTree.ParseModule(currentUri,
            "VERSION 1.0 CLASS\nAttribute VB_Name = \"Worker\"\nPrivate Sub LocalOnly()\nEnd Sub\nPrivate Sub Shadowed()\nEnd Sub\nPublic Sub Run()\n    Dim worker As Worker\n    Call worker.LocalOnly(Unknown:=False)\n    Call worker.Shadowed(Unknown:=False)\n    Call CatalogOnly(Unknown:=False)\nEnd Sub\n");
        const string referenceName = "Identity Boundary Library";
        var acceptingParameter = new VbaCallableParameter(
            "Unknown",
            TypeReference: new VbaTypeReference("Boolean"),
            IsOptional: true);
        var catalog = new VbaProjectReferenceCatalog(
            referenceName,
            ["IdentityBoundary"],
            [
                new VbaProjectReferenceDefinition(
                    referenceName,
                    "Worker",
                    VbaSourceDefinitionKind.Class),
                CreateAcceptingCatalogMember(
                    referenceName,
                    "LocalOnly",
                    acceptingParameter),
                CreateAcceptingCatalogMember(
                    referenceName,
                    "Shadowed",
                    acceptingParameter),
                new VbaProjectReferenceDefinition(
                    referenceName,
                    "CatalogOnly",
                    VbaSourceDefinitionKind.Procedure,
                    Signature: new VbaCallableSignature(
                        "Sub CatalogOnly()",
                        [],
                        CallableKind: VbaCallableKind.Sub,
                        SupportsNamedArguments: true),
                    GlobalExposure: ReferenceDefinitionGlobalExposure.LibraryGlobal,
                    IsCallableMetadataComplete: true)
            ]);
        var inputs = VbaProjectSemanticInputs.Capture(
            VbaReferenceSelection.Capture([referenceName], null),
            VbaProjectReferenceCatalogSet.Empty.WithCatalog(catalog));

        var diagnostics = VbaProjectSourceAnalysis.Analyze(
            [current],
            inputs);

        Assert.Equal(currentUri, current.Uri);
        Assert.Equal(
            [8, 9, 10],
            diagnostics.Select(diagnostic => diagnostic.Range.Start.Line));
        Assert.All(diagnostics, diagnostic =>
        {
            Assert.Equal(currentUri, diagnostic.SourceUri);
            Assert.Equal("validation.incompatibleCallArgumentList", diagnostic.Code);
        });
        Assert.Contains(
            diagnostics[1].Details!,
            detail => detail.Location?.Uri == currentUri);
        Assert.Contains(
            diagnostics[2].Details!,
            detail => detail.Location is null
                && detail.FallbackText.Contains(
                    "CatalogOnly",
                    StringComparison.Ordinal));
    }

    private static VbaProjectReferenceDefinition CreateAcceptingCatalogMember(
        string referenceName,
        string name,
        VbaCallableParameter parameter)
        => new(
            referenceName,
            name,
            VbaSourceDefinitionKind.Procedure,
            Signature: new VbaCallableSignature(
                $"Sub {name}([Unknown As Boolean])",
                [parameter],
                CallableKind: VbaCallableKind.Sub,
                SupportsNamedArguments: true),
            ParentTypeName: "Worker",
            IsCallableMetadataComplete: true);

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
