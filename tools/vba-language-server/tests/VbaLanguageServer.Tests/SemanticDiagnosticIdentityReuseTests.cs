using VbaTools.Syntax;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class SemanticDiagnosticIdentityReuseTests
{
    [Fact]
    public void RepeatedInvalidCallsKeepEveryFindingWithoutRepeatingDocumentIdentification()
    {
        static long Analyze(int calls)
        {
            const string callerUri = "file:///C:/identity-reuse/%E5%8B%A4%E5%8B%99/Caller.bas";
            const string workerUri = "file:///C:/identity-reuse/%E5%8B%A4%E5%8B%99/Worker.cls";
            var caller = VbaSyntaxTree.ParseModule(callerUri,
                "Attribute VB_Name = \"Caller\"\nPublic Sub Run()\nDim item As Worker\n"
                + string.Concat(Enumerable.Repeat("Call item.Go()\n", calls)) + "End Sub\n");
            var worker = VbaSyntaxTree.ParseModule(workerUri,
                "Attribute VB_Name = \"Worker\"\nPublic Sub Go(ByVal value As Long)\nEnd Sub\n");
            using var work = VbaSemanticWorkObservation.Begin();

            var diagnostics = VbaProjectSourceAnalysis.Analyze([caller, worker]);

            Assert.Equal(calls, diagnostics.Count);
            for (var index = 0; index < calls; index++)
            {
                var diagnostic = diagnostics[index];
                Assert.Equal(callerUri, diagnostic.SourceUri);
                Assert.Equal("validation.incompatibleCallArgumentList", diagnostic.Code);
                Assert.Equal("error", diagnostic.Severity);
                Assert.Equal(new VbaRange(new VbaPosition(index + 3, 10),
                    new VbaPosition(index + 3, 12)), diagnostic.Range);
                Assert.NotNull(diagnostic.Details);
                Assert.Contains(diagnostic.Details, detail => detail.RelatedMessage.Contains(
                    "parameter 'value': required argument is missing.", StringComparison.Ordinal));
            }

            return work.DocumentIdentifications;
        }

        var small = Analyze(2);
        Assert.Equal(small, Analyze(32));
        Assert.Equal(small, Analyze(128));
    }
}
