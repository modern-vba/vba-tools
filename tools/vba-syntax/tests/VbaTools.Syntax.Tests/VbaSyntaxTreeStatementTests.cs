using VbaTools.Syntax;
using Xunit;

namespace VbaTools.Syntax.Tests;

public sealed class VbaSyntaxTreeStatementTests
{
    [Fact]
    public void ParserRepresentsStatementAndBlockSyntaxNodes()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    If ready Then",
            "        value = 1",
            "    End If",
            "    With Application",
            "        .Run",
            "    End With",
            "    Select Case value",
            "        Case 1",
            "            Call DoWork",
            "    End Select",
            "    For i = 1 To 3",
            "    Next",
            "    Do",
            "    Loop",
            "End Sub"
        ]);

        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        Assert.Contains(tree.Module.Statements, statement => statement.Kind == VbaStatementKind.ProcedureBody && statement.Text.Contains("Public Sub Run"));
        Assert.Contains(tree.Module.Statements, statement => statement.Kind == VbaStatementKind.IfBlock && statement.Text.TrimStart().StartsWith("If ready", StringComparison.Ordinal));
        Assert.Contains(tree.Module.Statements, statement => statement.Kind == VbaStatementKind.WithBlock);
        Assert.Contains(tree.Module.Statements, statement => statement.Kind == VbaStatementKind.SelectBlock);
        Assert.Contains(tree.Module.Statements, statement => statement.Kind == VbaStatementKind.ForBlock);
        Assert.Contains(tree.Module.Statements, statement => statement.Kind == VbaStatementKind.DoLoopBlock);
        Assert.Contains(tree.Module.Statements, statement => statement.Kind == VbaStatementKind.Assignment && statement.Text.Trim() == "value = 1");
        Assert.Contains(tree.Module.Statements, statement => statement.Kind == VbaStatementKind.Call && statement.Text.Trim() == "Call DoWork");
        Assert.Empty(tree.Diagnostics);
    }

    [Fact]
    public void ParserClassifiesAJapaneseIdentifierAssignment()
    {
        var source = string.Join('\n', [
            "Public Sub Run()",
            "    集計結果 = 1",
            "End Sub"
        ]);

        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        Assert.Contains(
            tree.Module.Statements,
            statement => statement.Kind == VbaStatementKind.Assignment
                && statement.Text.Trim() == "集計結果 = 1");
    }

    [Fact]
    public void ParserClassifiesACallToAJapaneseIdentifier()
    {
        var source = string.Join('\n', [
            "Public Sub Run()",
            "    集計 1",
            "End Sub"
        ]);

        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        Assert.Contains(
            tree.Module.Statements,
            statement => statement.Kind == VbaStatementKind.Call
                && statement.Text.Trim() == "集計 1");
    }

    [Fact]
    public void ParserDoesNotTreatACompleteCp2IdentifierAsBlank()
    {
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            "Public Sub Run()\n\u00A0\nEnd Sub");

        Assert.Contains(
            tree.Module.Statements,
            statement => statement.Kind == VbaStatementKind.Call
                && statement.Text == "\u00A0");
    }

    [Fact]
    public void ParserClassifiesANonReservedContextualWordAsABareCallTarget()
    {
        var source = string.Join('\n', [
            "Public Sub Caller()",
            "    Object",
            "End Sub",
            "Public Sub Object()",
            "End Sub"
        ]);

        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        Assert.Contains(
            tree.Module.Statements,
            statement => statement.Kind == VbaStatementKind.Call
                && statement.Text.Trim() == "Object");
    }

    [Fact]
    public void Cp2IdentifierCharactersDoNotCreateKeywordBlockBoundaries()
    {
        var source = string.Join('\n', [
            "Public Sub Run()",
            "    If\u00a0Then",
            "End Sub"
        ]);

        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        Assert.DoesNotContain(tree.Module.Statements, statement => statement.Kind == VbaStatementKind.IfBlock);
        Assert.DoesNotContain(tree.Module.Blocks, block => block.Kind == VbaBlockKind.If);
        Assert.DoesNotContain(
            tree.Diagnostics,
            diagnostic => diagnostic.Message.Contains("End If", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ParserReportsRecoveryDiagnosticsAndKeepsMalformedStatementRanges()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Function () As String",
            "    value = \"unterminated",
            "    ReadValue _ ' bad continuation",
            "    @",
            "Public Sub Run()",
            "    If ready Then",
            "        MissingIdentifier"
        ]);

        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        Assert.Contains(tree.Diagnostics, diagnostic => diagnostic.Code == "syntax.malformedDeclarationHeader");
        Assert.Contains(tree.Diagnostics, diagnostic => diagnostic.Code == "syntax.unterminatedStringLiteral");
        Assert.Contains(tree.Diagnostics, diagnostic => diagnostic.Code == "syntax.invalidTrailingCommentContinuation");
        Assert.Contains(tree.Diagnostics, diagnostic => diagnostic.Code == "syntax.unexpectedStatementBoundaryToken");
        Assert.Contains(tree.Diagnostics, diagnostic =>
            diagnostic.Code == "syntax.missingBlockTerminator"
            && diagnostic.Message.Contains("End If", StringComparison.Ordinal));
        Assert.Contains(tree.Diagnostics, diagnostic =>
            diagnostic.Code == "syntax.missingBlockTerminator"
            && diagnostic.Message.Contains("End Sub", StringComparison.Ordinal));
        Assert.DoesNotContain(tree.Diagnostics, diagnostic => diagnostic.Code.Contains("unresolved", StringComparison.OrdinalIgnoreCase));

        var malformed = Assert.Single(tree.Module.Statements, statement => statement.Kind == VbaStatementKind.Malformed && statement.Text.Trim() == "@");
        Assert.Equal(4, malformed.Range.Start.Line);
        Assert.True(malformed.Range.End.Character > malformed.Range.Start.Character);
    }

    [Fact]
    public void ParserUsesExactMsVbalWhitespaceAtARemCommentBoundary()
    {
        var wscTree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Wsc.bas",
            "Public Sub Run()\n    Rem\u0019\"comment\nEnd Sub");
        var codePageTree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/CodePage.bas",
            "Public Sub Run()\n    Rem\u00A0\"unterminated\nEnd Sub");

        Assert.DoesNotContain(
            wscTree.Diagnostics,
            diagnostic => diagnostic.Code == "syntax.unterminatedStringLiteral");
        Assert.Contains(
            codePageTree.Diagnostics,
            diagnostic => diagnostic.Code == "syntax.unterminatedStringLiteral");
    }

    [Fact]
    public void TrailingCommentContinuationUsesExactMsVbalWhitespace()
    {
        var wscTree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Wsc.bas",
            "Public Sub Run()\n    ReadValue _\u0019'comment\nEnd Sub");
        var codePageTree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/CodePage.bas",
            "Public Sub Run()\n    ReadValue _\u00A0'comment\nEnd Sub");

        Assert.Contains(
            wscTree.Diagnostics,
            diagnostic => diagnostic.Code == "syntax.invalidTrailingCommentContinuation");
        Assert.DoesNotContain(
            codePageTree.Diagnostics,
            diagnostic => diagnostic.Code == "syntax.invalidTrailingCommentContinuation");
    }

    [Fact]
    public void RaiseEventDoesNotTreatAReservedIdentifierAsAnEventName()
    {
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            "Public Sub Run()\n    RaiseEvent CDecl value\nEnd Sub");

        Assert.DoesNotContain(
            tree.Diagnostics,
            diagnostic => diagnostic.Code == "syntax.raiseEventArgumentListRequiresParentheses");
    }

    [Fact]
    public void RaiseEventDoesNotTreatAMixedCodePageCandidateAsAnEventName()
    {
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            "Public Sub Run()\n    RaiseEvent 亜ㄱ value\nEnd Sub");

        Assert.DoesNotContain(
            tree.Diagnostics,
            diagnostic => diagnostic.Code == "syntax.raiseEventArgumentListRequiresParentheses");
    }

    [Fact]
    public void RaiseEventDoesNotTreatGenericUnicodeWhitespaceAsLayout()
    {
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            "Public Sub Run()\n\u000bRaiseEvent Changed value\nEnd Sub");

        Assert.DoesNotContain(
            tree.Diagnostics,
            diagnostic => diagnostic.Code == "syntax.raiseEventArgumentListRequiresParentheses");
    }

    [Fact]
    public void FormattingInputExposesBlockBranchAndContinuationDepths()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "If ready Then",
            "value = 1 _",
            "+ 2",
            "Else",
            "value = 3",
            "End If",
            "End Sub"
        ]);
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        var formattingInput = VbaFormattingInput.FromSyntaxTree(tree);

        Assert.True(formattingInput.CanApplyIndentation);
        Assert.Equal("End Sub", formattingInput.Lines[1].BlockTransition.OpenTerminator);
        Assert.Equal(0, formattingInput.Lines[1].IndentationDepth);
        Assert.Equal("End If", formattingInput.Lines[2].BlockTransition.OpenTerminator);
        Assert.Equal(1, formattingInput.Lines[2].IndentationDepth);
        Assert.True(formattingInput.Lines[4].IsContinuationLine);
        Assert.Equal(3, formattingInput.Lines[4].IndentationDepth);
        Assert.Equal("End If", formattingInput.Lines[5].BlockTransition.BranchTerminator);
        Assert.Equal(1, formattingInput.Lines[5].IndentationDepth);
        Assert.Equal("End If", formattingInput.Lines[7].BlockTransition.CloseTerminator);
        Assert.Equal(1, formattingInput.Lines[7].IndentationDepth);
    }
}
