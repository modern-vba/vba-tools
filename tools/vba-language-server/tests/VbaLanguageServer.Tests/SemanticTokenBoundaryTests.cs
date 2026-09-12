using VbaLanguageServer.SourceModel;
using VbaTools.Syntax;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class SemanticTokenBoundaryTests
{
    [Fact]
    public void MalformedPreviousCallAndCommentDoNotCaptureLaterContinuedOrInvalidCalls()
    {
        const string callerUri = "file:///C:/token-boundaries/Caller.bas";
        const string libraryUri = "file:///C:/token-boundaries/Library.bas";
        var caller = VbaSyntaxTree.ParseModule(callerUri,
            "Attribute VB_Name = \"Caller\"\nPublic Sub Run()\n"
            + "Call Accept(\n' ) : Then Else _\n\n"
            + "Call Accept( _\n    1&) ' ) : Then Else\nCall Accept()\nEnd Sub\n");
        var library = VbaSyntaxTree.ParseModule(libraryUri,
            "Attribute VB_Name = \"Library\"\nPublic Sub Accept(ByVal value As Long)\nEnd Sub\n");
        var inventory = VbaSemanticInventory.Create(new Dictionary<string, VbaSourceDocument>
        {
            [callerUri] = VbaSourceDocumentProjector.Project(callerUri, caller),
            [libraryUri] = VbaSourceDocumentProjector.Project(libraryUri, library)
        }, referenceCatalogs: VbaProjectReferenceCatalogSet.Empty);
        var expectedDeclaration = new VbaDefinitionLocation(libraryUri,
            new VbaRange(new VbaPosition(1, 11), new VbaPosition(1, 17)));
        var expectedCallRange = new VbaRange(new VbaPosition(7, 5), new VbaPosition(7, 11));

        var diagnostic = Assert.Single(VbaProjectSourceAnalysis.Analyze([caller, library]));

        Assert.Equal(callerUri, diagnostic.SourceUri);
        Assert.Equal("validation.incompatibleCallArgumentList", diagnostic.Code);
        Assert.Equal("error", diagnostic.Severity);
        Assert.Equal(expectedCallRange, diagnostic.Range);
        var detail = Assert.Single(diagnostic.Details!);
        Assert.Equal(new VbaDiagnosticLocation(libraryUri, expectedDeclaration.Range), detail.Location);
        Assert.Contains("parameter 'value': required argument is missing.", detail.RelatedMessage);
        var editorDiagnostic = Assert.Single(inventory.GetProjectValidationDiagnostics(callerUri));
        Assert.Equal("validation.incompatibleCallArgumentList", editorDiagnostic.Code);
        Assert.Equal("error", editorDiagnostic.Severity);
        Assert.Equal(expectedCallRange, editorDiagnostic.Range);
        Assert.Equal(expectedDeclaration, inventory.ResolveDefinition(callerUri, 5, 8));
        Assert.Equal(expectedDeclaration, inventory.ResolveDefinition(callerUri, 7, 8));
        var signature = Assert.IsType<VbaSignatureHelp>(inventory.GetSignatureHelp(callerUri, 6, 4));
        Assert.Equal("Sub Accept(value As Long)", signature.Signature.Label);
        Assert.Equal(0, signature.ActiveParameter);
    }

    [Fact]
    public void LiteralPunctuationAndAstralTextPreserveCallResultsAndUtf16Locations()
    {
        const string callerUri = "file:///C:/token-boundaries/%E5%8B%A4%E5%8B%99/Caller.bas";
        const string libraryUri = "file:///C:/token-boundaries/%E5%8B%A4%E5%8B%99/Library.bas";
        var caller = VbaSyntaxTree.ParseModule(callerUri,
            "Attribute VB_Name = \"Caller\"\nPublic Sub Run()\nDim marker As String\n"
            + "marker = \"😀: Then Else\" : Call RecordValue(\"x:y\", #1/2/2026#)\n"
            + "marker = \"😀: Then Else\" : Call RecordValue()\nEnd Sub\n");
        var library = VbaSyntaxTree.ParseModule(libraryUri,
            "Attribute VB_Name = \"Library\"\n"
            + "Public Sub RecordValue(ByVal text As String, ByVal day As Date)\nEnd Sub\n");
        var inventory = VbaSemanticInventory.Create(new Dictionary<string, VbaSourceDocument>
        {
            [callerUri] = VbaSourceDocumentProjector.Project(callerUri, caller),
            [libraryUri] = VbaSourceDocumentProjector.Project(libraryUri, library)
        }, referenceCatalogs: VbaProjectReferenceCatalogSet.Empty);
        var expectedDeclaration = new VbaDefinitionLocation(libraryUri,
            new VbaRange(new VbaPosition(1, 11), new VbaPosition(1, 22)));
        // The astral character occupies two UTF-16 units before the real statement separator.
        var expectedCallRange = new VbaRange(new VbaPosition(4, 32), new VbaPosition(4, 43));

        var diagnostic = Assert.Single(VbaProjectSourceAnalysis.Analyze([caller, library]));

        Assert.Equal(callerUri, diagnostic.SourceUri);
        Assert.Equal("validation.incompatibleCallArgumentList", diagnostic.Code);
        Assert.Equal("error", diagnostic.Severity);
        Assert.Equal(expectedCallRange, diagnostic.Range);
        var detail = Assert.Single(diagnostic.Details!);
        Assert.Equal(new VbaDiagnosticLocation(libraryUri, expectedDeclaration.Range), detail.Location);
        Assert.Contains("parameter 'day': required argument is missing.", detail.RelatedMessage);
        var editorDiagnostic = Assert.Single(inventory.GetProjectValidationDiagnostics(callerUri));
        Assert.Equal("validation.incompatibleCallArgumentList", editorDiagnostic.Code);
        Assert.Equal("error", editorDiagnostic.Severity);
        Assert.Equal(expectedCallRange, editorDiagnostic.Range);
        Assert.Equal(new VbaDiagnosticLocation(libraryUri, expectedDeclaration.Range),
            Assert.Single(editorDiagnostic.Details!).Location);
        Assert.Empty(inventory.GetProjectValidationDiagnostics(libraryUri));
        Assert.Equal(expectedDeclaration, inventory.ResolveDefinition(callerUri, 3, 40));
        Assert.Equal(expectedDeclaration, inventory.ResolveDefinition(callerUri, 4, 40));
        var signature = Assert.IsType<VbaSignatureHelp>(inventory.GetSignatureHelp(callerUri, 3, 51));
        Assert.Equal("Sub RecordValue(text As String, day As Date)", signature.Signature.Label);
        Assert.Equal(1, signature.ActiveParameter);
    }
}
