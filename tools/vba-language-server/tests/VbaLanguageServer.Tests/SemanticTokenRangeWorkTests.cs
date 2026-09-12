using VbaTools.Syntax;
using Xunit;
using Xunit.Abstractions;

namespace VbaLanguageServer.Tests;

public sealed class SemanticTokenRangeWorkTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void CompleteSmallCallsDoNotRepeatedlyExamineUnrelatedModulePrefixes(int documentCount)
    {
        static long Analyze(int documentCount, int callsPerDocument)
        {
            var trees = Enumerable.Range(0, documentCount).Select(index =>
                VbaSyntaxTree.ParseModule($"file:///C:/token-ranges/Module{index}.bas",
                    $"Attribute VB_Name = \"Module{index}\"\nPrivate Sub Target()\nEnd Sub\nPublic Sub Run()\n"
                    + string.Concat(Enumerable.Repeat("Call Target()\n", callsPerDocument))
                    + "End Sub\n")).ToArray();
            using var work = VbaSemanticWorkObservation.Begin();
            Assert.Empty(VbaProjectSourceAnalysis.Analyze(trees));
            return work.LogicalContextTokensExamined + work.TokenIndexPreparationTokens;
        }

        var small = Analyze(documentCount, 16);
        var large = Analyze(documentCount, 64);
        output.WriteLine($"Context work: documents={documentCount}, small={small}, large={large}");
        Assert.True(small > 0, "The probe must observe real complete-call token work.");
        Assert.True(large <= small * 6,
            $"Four times the small calls should not cause full-prefix rescans: small={small}, large={large}.");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void CompleteArrayArgumentsDoNotRepeatedlyFilterUnrelatedModuleTokens(int documentCount)
    {
        static long Analyze(int documentCount, int callsPerDocument)
        {
            var trees = Enumerable.Range(0, documentCount).Select(index =>
                VbaSyntaxTree.ParseModule($"file:///C:/token-ranges/Module{index}.bas",
                    $"Attribute VB_Name = \"Module{index}\"\nPrivate Sub Target(ByRef value As Long)\nEnd Sub\nPublic Sub Run()\nDim values(0 To 1) As Long\n"
                    + string.Concat(Enumerable.Repeat("Call Target(values(0))\n", callsPerDocument))
                    + "End Sub\n")).ToArray();
            using var work = VbaSemanticWorkObservation.Begin();
            Assert.Empty(VbaProjectSourceAnalysis.Analyze(trees));
            return work.ArgumentRangeTokensExamined;
        }

        var small = Analyze(documentCount, 16);
        var large = Analyze(documentCount, 64);
        output.WriteLine($"Argument work: documents={documentCount}, small={small}, large={large}");
        Assert.True(small > 0, "The probe must observe actual argument-type range selection.");
        Assert.True(large <= small * 6,
            $"Four times the arguments should not cause whole-module filtering: small={small}, large={large}.");
    }

    [Fact]
    public void ConcentratingTheSameCallsInOneModuleDoesNotMultiplyUnrelatedTokenWork()
    {
        static long Analyze(int documentCount)
        {
            var trees = Enumerable.Range(0, documentCount).Select(index =>
                VbaSyntaxTree.ParseModule($"file:///C:/token-ranges/Module{index}.bas",
                    $"Attribute VB_Name = \"Module{index}\"\nPrivate Sub Target(ByRef value As Long)\nEnd Sub\nPublic Sub Run()\nDim values(0 To 1) As Long\n"
                    + string.Concat(Enumerable.Repeat("Call Target(values(0))\n", 256 / documentCount))
                    + "End Sub\n")).ToArray();
            using var work = VbaSemanticWorkObservation.Begin();
            Assert.Empty(VbaProjectSourceAnalysis.Analyze(trees));
            return work.LogicalContextTokensExamined + work.ArgumentRangeTokensExamined
                + work.TokenIndexPreparationTokens;
        }

        var split = Analyze(16);
        var concentrated = Analyze(1);
        output.WriteLine($"Same 256 calls: split={split}, concentrated={concentrated}");
        Assert.InRange(concentrated, 1, split * 2);
    }
}
