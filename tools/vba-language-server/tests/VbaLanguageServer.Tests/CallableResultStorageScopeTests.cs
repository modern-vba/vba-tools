using VbaLanguageServer.SourceModel;
using VbaTools.Syntax;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class CallableResultStorageScopeTests
{
    [Theory]
    [InlineData("Let", "Long", "")]
    [InlineData("Set", "Object", "Set ")]
    public void SetterBodyDoesNotOwnTheGetterResultVariable(
        string accessor, string type, string assignmentKeyword)
    {
        const string uri = "file:///C:/work/ResultStorageScope.cls";
        var source = $$"""
            VERSION 1.0 CLASS
            Attribute VB_Name = "ResultStorageScope"
            Public Property Get Item(ByVal index As Long) As {{type}}
                {{assignmentKeyword}}Item = {{(accessor == "Set" ? "Nothing" : "1&")}}
            End Property
            Public Property {{accessor}} Item(ByVal index As Long, ByVal value As {{type}})
                Dim current As {{type}}
                {{assignmentKeyword}}current = Item
            End Property
            """;
        var inventory = VbaSemanticInventory.Create(
            new Dictionary<string, VbaSourceDocument>(StringComparer.OrdinalIgnoreCase)
            {
                [uri] = VbaSourceDocumentProjector.Project(uri, VbaSyntaxTree.ParseModule(uri, source))
            });

        var diagnostic = Assert.Single(inventory.GetProjectValidationDiagnostics(uri));

        Assert.Equal("validation.incompatibleCallArgumentList", diagnostic.Code);
        Assert.Equal(7, diagnostic.Range.Start.Line);
        Assert.Equal($"    {assignmentKeyword}current = ".Length, diagnostic.Range.Start.Character);
        Assert.NotNull(diagnostic.Details);
        Assert.Contains(diagnostic.Details,
            detail => detail.RelatedMessage.Contains("parameter 'index': required argument is missing.",
                StringComparison.Ordinal));
    }
}
