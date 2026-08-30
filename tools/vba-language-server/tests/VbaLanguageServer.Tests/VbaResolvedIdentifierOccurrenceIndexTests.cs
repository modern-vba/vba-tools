using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaResolvedIdentifierOccurrenceIndexTests
{
    [Theory]
    [InlineData(
        "file:///C:/work/Identifier%20Occurrence%20Cache.bas",
        "file:///C:/work/Identifier Occurrence Cache.bas")]
    [InlineData(
        "untitled://workspace/Identifier%20Occurrence%20Cache.bas",
        "untitled://workspace/Identifier Occurrence Cache.bas")]
    public void Document_cache_uses_canonical_identity_and_preserves_source_uri(
        string sourceUri,
        string equivalentUri)
    {
        const string source =
            """
            Attribute VB_Name = "IdentifierOccurrenceCache"
            Public Sub Run()
                Run
            End Sub
            """;
        var document = VbaSourceDocumentProjector.Project(
            sourceUri,
            VbaSyntaxTree.ParseModule(sourceUri, source));
        var target = new VbaDefinitionNameTarget(
            document.Definitions.Single(definition => definition.Name == "Run"));
        var index = new VbaResolvedIdentifierOccurrenceIndex(
            [document],
            (_, _, _) => target);

        var occurrences = index.GetDocumentOccurrences(equivalentUri);
        var canonicalNames = index.GetCanonicalNamesByRange(equivalentUri);

        Assert.NotEmpty(occurrences);
        Assert.All(
            occurrences,
            occurrence => Assert.Equal(sourceUri, occurrence.Uri));
        Assert.Contains("Run", canonicalNames.Values);
    }
}
