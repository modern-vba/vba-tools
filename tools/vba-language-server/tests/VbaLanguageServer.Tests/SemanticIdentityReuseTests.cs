using VbaTools.Syntax;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class SemanticIdentityReuseTests
{
    [Fact]
    public void ConcurrentKnownReadsReuseAdmissionWhileUnknownAliasesRemainUnretained()
    {
        const string uri = "file:///C:/identity-reuse/Worker.bas";
        var tree = VbaSyntaxTree.ParseModule(uri, "Public Const Value As Long = 1\n");
        var document = VbaSourceDocumentProjector.Project(uri, tree);
        var resolver = new VbaNameResolutionService([document], null, VbaProjectReferenceCatalogSet.Empty);

        Parallel.For(0, 8, _ =>
        {
            using var work = VbaSemanticWorkObservation.Begin();
            for (var i = 0; i < 32; i++)
                Assert.Equal(uri, resolver.Resolve(uri, new VbaPosition(1, 0), null, "Value")?.Uri);
            Assert.Equal(0, work.DocumentIdentifications);
        });

        using var aliases = VbaSemanticWorkObservation.Begin();
        for (var i = 0; i < 32; i++)
            Assert.Equal(uri, resolver.Resolve("file:///C:/identity-reuse/Nested/../Worker.bas",
                new VbaPosition(1, 0), null, "Value")?.Uri);
        Assert.Equal(32, aliases.DocumentIdentifications);
    }

    [Theory]
    [InlineData("file:///C:/identity-reuse/%E5%8B%A4%E5%8B%99/Module%20One.bas", "file:///c%3A/identity-reuse/%E5%8B%A4%E5%8B%99/Nested/../Module%20One.bas", true)]
    [InlineData("file:///C:/identity-reuse/Module.bas", "file:///C:/other/Module.bas", false)]
    [InlineData("untitled:Module.bas", "untitled:module.bas", true)]
    [InlineData("file:///C:/invalid%00path/Module.bas", "file:///C:/invalid%00path/Module.bas", true)]
    [InlineData("not a URI", "not a URI", false)]
    [InlineData("file:///C:/identity-reuse/Module.bas", "", false)]
    public void ResolutionPreservesIdentityKindsAndOriginalPresentation(string sourceUri, string queryUri, bool expected)
    {
        var range = new VbaRange(new VbaPosition(0, 0), new VbaPosition(0, 5));
        var definition = new VbaSourceDefinition(
            VbaDefinitionIdentity.ForSource(sourceUri, "Value", range),
            new VbaDefinitionLocation(sourceUri, range), "Value", VbaSourceDefinitionKind.Constant,
            VbaSourceDefinitionVisibility.Private, "Module");
        var resolver = new VbaNameResolutionService(
            [new VbaSourceDocument(sourceUri, "", "Module", [definition])], null, VbaProjectReferenceCatalogSet.Empty);

        for (var i = 0; i < 8; i++)
        {
            var result = resolver.Resolve(queryUri, new VbaPosition(1, 0), null, "Value");
            Assert.Equal(expected, result is not null);
            if (expected) Assert.Equal(sourceUri, result!.Uri);
        }
    }

    [Fact]
    public void CompleteCallValidationIdentificationWorkDoesNotGrowWithRepeatedCalls()
    {
        static long Analyze(int calls)
        {
            var caller = VbaSyntaxTree.ParseModule("file:///C:/identity-reuse/Caller.bas",
                "Attribute VB_Name = \"Caller\"\nPublic Sub Run()\nDim item As Worker\n"
                + string.Concat(Enumerable.Repeat("Call item.Go(1)\n", calls)) + "End Sub\n");
            var worker = VbaSyntaxTree.ParseModule("file:///C:/identity-reuse/Worker.cls",
                "Attribute VB_Name = \"Worker\"\nPublic Sub Go(ByVal value As Long)\nEnd Sub\n");
            using var work = VbaSemanticWorkObservation.Begin();
            Assert.Empty(VbaProjectSourceAnalysis.Analyze([caller, worker]));
            return work.DocumentIdentifications;
        }

        var small = Analyze(2);
        Assert.Equal(small, Analyze(32));
        Assert.Equal(small, Analyze(128));
    }

    [Fact]
    public void RepeatedKnownDocumentResolutionDoesNotRepeatDocumentIdentification()
    {
        var documents = Enumerable.Range(0, 16).Select(index =>
        {
            var uri = $"file:///C:/identity-reuse/Module{index}.bas";
            var tree = VbaSyntaxTree.ParseModule(uri,
                $"Attribute VB_Name = \"Module{index}\"\nPrivate Const OwnValue As Long = 1\nPublic Sub Run()\n    Debug.Print OwnValue\nEnd Sub\n");
            return VbaSourceDocumentProjector.Project(uri, tree);
        }).ToArray();
        var resolver = new VbaNameResolutionService(documents, null, VbaProjectReferenceCatalogSet.Empty);
        using var work = VbaSemanticWorkObservation.Begin();

        for (var repeat = 0; repeat < 64; repeat++)
        {
            foreach (var document in documents)
            {
                var target = resolver.Resolve(document.Uri, new VbaPosition(3, 20), null, "OwnValue");
                Assert.NotNull(target);
                Assert.Equal(document.Uri, target.Uri);
            }
        }

        Assert.Equal(0, work.DocumentIdentifications);
    }
}
