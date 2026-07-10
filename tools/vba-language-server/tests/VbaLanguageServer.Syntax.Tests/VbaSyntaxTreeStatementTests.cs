using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaLanguageServer.Syntax.Tests;

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
}
