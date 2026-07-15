using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaLanguageServer.Syntax.Tests;

public sealed class VbaContextualCompletionSyntaxTests
{
    [Fact]
    public void EndPrefixOffersOnlyTheInnermostCanonicalCloser()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    Select Case value",
            "        If ready Then",
            "            End ",
            "        End If",
            "    End Select",
            "End Sub"
        ]);
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        var position = tree.GetPositionSyntax(4, "            End ".Length);

        Assert.Equal(VbaCompletionExpectation.ContextualStatement, position.CompletionExpectation);
        Assert.Equal(["End If"], position.ContextualStatements);
        Assert.Equal("End ", Slice(source, position.CompletionReplacementRange!));
    }

    [Fact]
    public void SelectBodyOffersCanonicalBranchesAndCloser()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    Select Case value",
            "        ",
            "    End Select",
            "End Sub"
        ]);
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        var position = tree.GetPositionSyntax(3, 8);

        Assert.Equal(VbaCompletionExpectation.ContextualStatement, position.CompletionExpectation);
        Assert.Equal(["Case", "Case Else", "End Select"], position.ContextualStatements);
    }

    [Fact]
    public void CaseElseIsTerminalForContextualBranchCandidates()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    Select Case value",
            "        Case 1",
            "            value = 1",
            "        Case Else",
            "            ",
            "    End Select",
            "End Sub"
        ]);
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        var position = tree.GetPositionSyntax(6, 12);

        Assert.Equal(VbaBlockBranchKind.CaseElse, position.EnclosingBlocks[^1].ActiveBranch);
        Assert.Equal(["End Select"], position.ContextualStatements);
    }

    [Fact]
    public void PartialCaseElseKeepsExpressionAndElseAlternatives()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    Select Case value",
            "        Case El",
            "    End Select",
            "End Sub"
        ]);
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        var position = tree.GetPositionSyntax(3, "        Case El".Length);

        Assert.Equal(VbaCompletionExpectation.ExpressionValue, position.CompletionExpectation);
        Assert.Empty(position.ContextualStatements);
        Assert.Equal(["Else"], position.SupplementalSyntaxWords);
        Assert.Equal("El", Slice(source, position.CompletionReplacementRange!));
    }

    [Theory]
    [InlineData("Enum", "End Enum")]
    [InlineData("Type", "End Type")]
    public void ModuleBlockEndPrefixRemainsContextual(
        string blockKind,
        string expectedTerminator)
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            $"Public {blockKind} Example{blockKind}",
            "    Value",
            "    End",
            $"End {blockKind}"
        ]);
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        var position = tree.GetPositionSyntax(3, "    End".Length);

        Assert.Equal(VbaCompletionExpectation.ContextualStatement, position.CompletionExpectation);
        Assert.Equal([expectedTerminator], position.ContextualStatements);
    }

    [Theory]
    [InlineData("    If (")]
    [InlineData("    If (ready And ")]
    [InlineData("    While (")]
    [InlineData("    While (ready + ")]
    public void ParenthesizedConditionSlotsExpectExpressionValues(string condition)
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            condition,
            "End Sub"
        ]);
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        var position = tree.GetPositionSyntax(2, condition.Length);

        Assert.Equal(VbaCompletionExpectation.ExpressionValue, position.CompletionExpectation);
    }

    private static string Slice(string source, VbaSyntaxRange range)
        => source[range.Start.Offset..range.End.Offset];
}
