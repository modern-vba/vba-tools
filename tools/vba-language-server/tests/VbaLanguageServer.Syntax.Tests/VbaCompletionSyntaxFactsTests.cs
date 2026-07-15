using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaLanguageServer.Syntax.Tests;

public sealed class VbaCompletionSyntaxFactsTests
{
    [Fact]
    public void ParserBuildsNestedBlocksBranchesAndDeclarationBlocks()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    If ready Then",
            "        Select Case value",
            "            Case 1",
            "                For index = 1 To 2",
            "                    Do",
            "                        While ready",
            "                            With Application",
            "                            End With",
            "                        Wend",
            "                    Loop",
            "                Next",
            "            Case Else",
            "        End Select",
            "    ElseIf waiting Then",
            "    Else",
            "    End If",
            "    If ready Then value = 1",
            "End Sub",
            "Public Enum Choice",
            "    First",
            "End Enum",
            "Private Type Payload",
            "    Value As Long",
            "End Type"
        ]);

        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        Assert.Equal(9, tree.Module.Blocks.Count(block => !block.IsMalformedBarrier));
        Assert.DoesNotContain(
            tree.Module.Blocks,
            block => block.OpenerRange.Start.Line == 18);
        var ifBlock = Assert.Single(tree.Module.Blocks, block => block.Kind == VbaBlockKind.If);
        Assert.Equal("End If", ifBlock.ExpectedTerminator);
        Assert.Equal(2, ifBlock.OpenerRange.Start.Line);
        Assert.Equal(17, ifBlock.CloserRange?.Start.Line);
        Assert.Equal(
            [VbaBlockBranchKind.Then, VbaBlockBranchKind.ElseIf, VbaBlockBranchKind.Else],
            ifBlock.Branches.Select(branch => branch.Kind));
        var select = Assert.Single(tree.Module.Blocks, block => block.Kind == VbaBlockKind.Select);
        Assert.Equal(2, select.Branches.Count);
        Assert.Equal(
            [VbaBlockBranchKind.Case, VbaBlockBranchKind.CaseElse],
            select.Branches.Select(branch => branch.Kind));
        Assert.Contains(tree.Module.Blocks, block => block.Kind == VbaBlockKind.While && block.ExpectedTerminator == "Wend");
        Assert.Contains(tree.Module.Blocks, block => block.Kind == VbaBlockKind.Enum && block.ExpectedTerminator == "End Enum");
        Assert.Contains(tree.Module.Blocks, block => block.Kind == VbaBlockKind.Type && block.ExpectedTerminator == "End Type");
    }

    [Fact]
    public void ParserRespectsColonParenthesesAndLineContinuationWhenBuildingBlocks()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    For index = 1 To 2: Next",
            "    If IsReady(Arg:=True) _",
            "        Then",
            "    End If",
            "End Sub"
        ]);

        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        var forBlock = Assert.Single(tree.Module.Blocks, block => block.Kind == VbaBlockKind.For);
        Assert.Equal(2, forBlock.OpenerRange.Start.Line);
        Assert.Equal(2, forBlock.CloserRange?.Start.Line);
        var ifBlock = Assert.Single(tree.Module.Blocks, block => block.Kind == VbaBlockKind.If);
        Assert.Equal(3, ifBlock.OpenerRange.Start.Line);
        Assert.Equal(4, ifBlock.OpenerRange.End.Line);
        Assert.False(ifBlock.IsMalformedBarrier);
    }

    [Fact]
    public void ParserExtractsOnlyCallableOwnedIdentifierAndNumericLabels()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "ModuleLabel:",
            "Public Sub FirstProcedure()",
            "StartHere:",
            "100 value = 1",
            "If:",
            "    ' CommentLabel:",
            "    value = \"StringLabel:\"",
            "#If VBA7 Then",
            "PreprocessorLabel:",
            "#End If",
            "End Sub",
            "Public Sub SecondProcedure()",
            "OtherLabel:",
            "End Sub"
        ]);

        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        Assert.Equal(["StartHere", "100", "OtherLabel"], tree.Module.LineLabels.Select(label => label.Name));
        Assert.DoesNotContain(tree.Module.LineLabels, label => label.Name == "If");
        Assert.Equal("FirstProcedure", tree.Module.LineLabels[0].ProcedureName);
        Assert.True(tree.Module.LineLabels[1].IsNumeric);
        Assert.Equal("SecondProcedure", tree.Module.LineLabels[^1].ProcedureName);

        var form = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Dialog.frm",
            string.Join('\n', [
                "VERSION 5.00",
                "DesignerLabel:",
                "Attribute VB_Name = \"Dialog\"",
                "Public Sub Run()",
                "CodeLabel:",
                "End Sub"
            ]));
        Assert.Equal("CodeLabel", Assert.Single(form.Module.LineLabels).Name);
    }

    [Fact]
    public void PositionSyntaxDescribesAllSupportedLabelDestinationForms()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "First:",
            "    GoTo Fir",
            "    GoSub ",
            "    Resume ",
            "    On selector GoTo First, Sec",
            "    On selector GoSub ",
            "    On Error GoTo ",
            "    On Error Resume ",
            "    GoTo First ",
            "End Sub"
        ]);
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        var goTo = tree.GetPositionSyntax(3, "    GoTo Fir".Length);
        Assert.Equal(VbaLabelReferenceKind.GoTo, goTo.LabelReference?.Kind);
        Assert.Equal(VbaCompletionExpectation.LabelName, goTo.CompletionExpectation);
        Assert.Equal("Fir", Slice(source, goTo.LabelReference!.ReplacementRange));

        Assert.Equal(VbaLabelReferenceKind.GoSub, tree.GetPositionSyntax(4, "    GoSub ".Length).LabelReference?.Kind);
        var resume = tree.GetPositionSyntax(5, "    Resume ".Length).LabelReference!;
        Assert.True(resume.AllowsProcedureLabels);
        Assert.Equal(["Next"], resume.SyntaxCandidates);

        var onGoTo = tree.GetPositionSyntax(6, "    On selector GoTo First, Sec".Length).LabelReference!;
        Assert.Equal(VbaLabelReferenceKind.OnGoTo, onGoTo.Kind);
        Assert.Equal(1, onGoTo.DestinationIndex);
        Assert.Equal("Sec", Slice(source, onGoTo.ReplacementRange));
        Assert.Equal(VbaLabelReferenceKind.OnGoSub, tree.GetPositionSyntax(7, "    On selector GoSub ".Length).LabelReference?.Kind);

        var onError = tree.GetPositionSyntax(8, "    On Error GoTo ".Length).LabelReference!;
        Assert.Equal(VbaLabelReferenceKind.OnErrorGoTo, onError.Kind);
        Assert.Equal(["0"], onError.SyntaxCandidates);
        var onErrorResume = tree.GetPositionSyntax(9, "    On Error Resume ".Length).LabelReference!;
        Assert.Equal(VbaLabelReferenceKind.OnErrorResume, onErrorResume.Kind);
        Assert.False(onErrorResume.AllowsProcedureLabels);
        Assert.Equal(["Next"], onErrorResume.SyntaxCandidates);

        var completed = tree.GetPositionSyntax(10, "    GoTo First ".Length);
        Assert.False(completed.LabelReference?.IsIncomplete);
        Assert.Equal(VbaCompletionExpectation.None, completed.CompletionExpectation);
    }

    [Fact]
    public void PositionSyntaxExposesEnclosingBlockBranchAndEndReplacementRange()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    If ready Then",
            "    ElseIf waiting Then",
            "        End ",
            "    End If",
            "End Sub"
        ]);
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        var position = tree.GetPositionSyntax(4, "        End ".Length);

        Assert.Equal([VbaBlockKind.Procedure, VbaBlockKind.If], position.EnclosingBlocks.Select(block => block.Block.Kind));
        Assert.Equal(VbaBlockBranchKind.ElseIf, position.EnclosingBlocks[^1].ActiveBranch);
        Assert.Equal("End If", position.EnclosingBlocks[^1].Block.ExpectedTerminator);
        Assert.Equal(VbaCompletionExpectation.ContextualStatement, position.CompletionExpectation);
        Assert.Equal(["End If"], position.ContextualStatements);
        Assert.Equal("End ", Slice(source, position.CompletionReplacementRange!));
    }

    [Fact]
    public void MismatchedCloserCreatesFailClosedCompletionBarrier()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    If ready Then",
            "    Next",
            "    ",
            "    End If",
            "End Sub"
        ]);
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        var barrier = Assert.Single(tree.Module.Blocks, block => block.IsMalformedBarrier);
        Assert.Equal(3, barrier.Range.Start.Line);
        Assert.Equal(6, barrier.Range.End.Line);
        var position = tree.GetPositionSyntax(4, 4);
        Assert.Contains(position.EnclosingBlocks, block => block.Block.IsMalformedBarrier);
        Assert.Equal(VbaCompletionExpectation.None, position.CompletionExpectation);
    }

    [Fact]
    public void CompletionFallbackRejectsCompletedUnknownAndUnmodelledPrefixes()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "nonsense ",
            "Pub",
            "Public Sub Run()",
            "    mystery ",
            "    myst",
            "    Dim ",
            "    Else ",
            "End Sub"
        ]);
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        Assert.Equal(VbaCompletionExpectation.None, tree.GetPositionSyntax(1, "nonsense ".Length).CompletionExpectation);
        Assert.Equal(VbaCompletionExpectation.ModuleDeclaration, tree.GetPositionSyntax(2, "Pub".Length).CompletionExpectation);
        Assert.Equal(VbaCompletionExpectation.CallArgument, tree.GetPositionSyntax(4, "    mystery ".Length).CompletionExpectation);
        Assert.Equal(VbaCompletionExpectation.ProcedureStatement, tree.GetPositionSyntax(5, "    myst".Length).CompletionExpectation);
        Assert.Equal(VbaCompletionExpectation.None, tree.GetPositionSyntax(6, "    Dim ".Length).CompletionExpectation);
        Assert.Equal(VbaCompletionExpectation.None, tree.GetPositionSyntax(7, "    Else ".Length).CompletionExpectation);
    }

    [Fact]
    public void StaticStatementVocabularyExcludesBlockOwnedWords()
    {
        var blockOwnedWords = new[] { "Case", "Else", "ElseIf", "End", "Loop", "Next", "Wend" };

        Assert.All(blockOwnedWords, word =>
            Assert.DoesNotContain(word, VbaLanguageVocabulary.ProcedureStatementWords));
    }

    [Fact]
    public void IncrementalParseMergesAndShiftsBlocksAndLabels()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var previous = VbaSyntaxTree.ParseModule(uri, string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "StartHere:",
            "    If ready Then",
            "    End If",
            "End Sub"
        ]));
        var updatedText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    value = 1",
            "StartHere:",
            "    If ready Then",
            "    End If",
            "End Sub"
        ]);

        var result = VbaSyntaxTree.ParseOrUpdate(uri, updatedText, previous);

        Assert.Equal(VbaSyntaxTreeParseUpdateKind.ModuleMember, result.UpdateKind);
        Assert.Equal(3, Assert.Single(result.SyntaxTree.Module.LineLabels).Range.Start.Line);
        var ifBlock = Assert.Single(result.SyntaxTree.Module.Blocks, block => block.Kind == VbaBlockKind.If);
        Assert.Equal(4, ifBlock.OpenerRange.Start.Line);
        Assert.Equal(5, ifBlock.CloserRange?.Start.Line);
        Assert.Equal([VbaBlockKind.Procedure, VbaBlockKind.If],
            result.SyntaxTree.GetPositionSyntax(4, "    If ready Then".Length).EnclosingBlocks.Select(block => block.Block.Kind));
    }

    private static string Slice(string source, VbaSyntaxRange range)
        => source[range.Start.Offset..range.End.Offset];
}
