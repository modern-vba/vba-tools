using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaLanguageServer.Syntax.Tests;

public sealed class VbaSyntaxTreePreprocessorTests
{
    [Fact]
    public void ParserRepresentsPreprocessorDirectivesBranchesAndBodies()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "#Const UseExcel = True",
            "#If UseExcel Then",
            "Public Sub ExcelOnly()",
            "End Sub",
            "#ElseIf UseWord Then",
            "Public Sub WordOnly()",
            "End Sub",
            "#Else",
            "Public Sub Fallback()",
            "End Sub",
            "#End If"
        ]);

        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        Assert.Contains(tree.Module.PreprocessorDirectives, directive =>
            directive.Kind == VbaPreprocessorDirectiveKind.Const
            && directive.Text == "#Const UseExcel = True");
        var block = Assert.Single(tree.Module.PreprocessorBlocks);
        Assert.Collection(
            block.Branches,
            branch =>
            {
                Assert.Equal(VbaPreprocessorDirectiveKind.If, branch.Directive.Kind);
                Assert.Contains("ExcelOnly", branch.BodyText);
            },
            branch =>
            {
                Assert.Equal(VbaPreprocessorDirectiveKind.ElseIf, branch.Directive.Kind);
                Assert.Contains("WordOnly", branch.BodyText);
            },
            branch =>
            {
                Assert.Equal(VbaPreprocessorDirectiveKind.Else, branch.Directive.Kind);
                Assert.Contains("Fallback", branch.BodyText);
            });
        Assert.Equal(VbaPreprocessorDirectiveKind.EndIf, block.EndDirective?.Kind);
        Assert.Empty(tree.Diagnostics);
    }

    [Fact]
    public void ParserReportsMalformedPreprocessorNestingWithoutDroppingBranchBodies()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "#If UseExcel Then",
            "Public Sub ExcelOnly()",
            "End Sub",
            "#Else",
            "Public Sub Fallback()",
            "End Sub"
        ]);

        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        var block = Assert.Single(tree.Module.PreprocessorBlocks);
        Assert.Null(block.EndDirective);
        Assert.Contains(block.Branches, branch => branch.BodyText.Contains("ExcelOnly", StringComparison.Ordinal));
        Assert.Contains(block.Branches, branch => branch.BodyText.Contains("Fallback", StringComparison.Ordinal));
        var diagnostic = Assert.Single(tree.Diagnostics, diagnostic => diagnostic.Code == "syntax.malformedPreprocessorNesting");
        Assert.Equal("Preprocessor block is missing '#End If'.", diagnostic.Message);
    }

    [Fact]
    public void ParserReportsADuplicateElseBranch()
    {
        var source = string.Join('\n', [
            "#If VBA7 Then",
            "#Else",
            "#Else",
            "#End If"
        ]);

        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        Assert.Contains(
            tree.Diagnostics,
            diagnostic => diagnostic.Code == "syntax.malformedPreprocessorNesting"
                && diagnostic.Message.Contains("duplicate '#Else'", StringComparison.Ordinal));
    }

    [Fact]
    public void ParserReportsAnElseIfBranchAfterElse()
    {
        var source = string.Join('\n', [
            "#If VBA7 Then",
            "#Else",
            "#ElseIf Win64 Then",
            "#End If"
        ]);

        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        Assert.Contains(
            tree.Diagnostics,
            diagnostic => diagnostic.Code == "syntax.malformedPreprocessorNesting"
                && diagnostic.Message.Contains("cannot follow '#Else'", StringComparison.Ordinal));
    }

    [Fact]
    public void ParserReportsAMalformedConditionalDirectivePrefix()
    {
        const string source = "#Ifx VBA7 Then\nPublic Sub Run()";

        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        Assert.Contains(
            tree.Diagnostics,
            diagnostic => diagnostic.Code == "syntax.malformedPreprocessorDirective"
                && diagnostic.Range.Start.Line == 0);
    }

    [Fact]
    public void ParserReportsAConditionalIfWithoutThen()
    {
        const string source = "#If VBA7\n#End If";

        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        Assert.Contains(
            tree.Diagnostics,
            diagnostic => diagnostic.Code == "syntax.malformedPreprocessorDirective"
                && diagnostic.Range.Start.Line == 0);
    }

    [Fact]
    public void ParserAcceptsConditionalDirectiveCommentsAndCompactEndIf()
    {
        var source = string.Join('\n', [
            "#If VBA7 Then ' modern branch",
            "#ElseIf Win64 Then ' native branch",
            "#Else ' fallback branch",
            "#EndIf ' conditional end"
        ]);

        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        Assert.DoesNotContain(
            tree.Diagnostics,
            diagnostic => diagnostic.Code.StartsWith(
                "syntax.malformedPreprocessor",
                StringComparison.Ordinal));
        Assert.Equal(
            VbaPreprocessorDirectiveKind.EndIf,
            Assert.Single(tree.Module.PreprocessorBlocks).EndDirective?.Kind);
    }

    [Theory]
    [InlineData("#If &H10& = 16 Then")]
    [InlineData("#If 1% = 1 Then")]
    public void ParserPreservesAdjacentConditionalExpressionTokens(string directive)
    {
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            $"{directive}\n#End If");

        Assert.DoesNotContain(
            tree.Diagnostics,
            diagnostic => diagnostic.Code.StartsWith(
                "syntax.malformedPreprocessor",
                StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void ParserTreatsAContinuedConditionalDirectiveAsOneAtomicRange(
        string lineEnding)
    {
        var source = string.Join(lineEnding, [
            "#If VBA7 _",
            "    And Win64 Then",
            "Public Sub Candidate()",
            "",
            "#End If"
        ]);

        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            source);

        var directive = Assert.Single(
            tree.Module.PreprocessorDirectives,
            candidate => candidate.Kind == VbaPreprocessorDirectiveKind.If);
        var token = Assert.Single(
            tree.TokenStream.Tokens,
            candidate => candidate.Kind == VbaTokenKind.PreprocessorDirective
                && candidate.Range.Start.Line == 0);
        Assert.Equal(1, directive.Range.End.Line);
        Assert.Equal(directive.Range, token.Range);
        Assert.DoesNotContain(
            tree.Module.Statements,
            statement => statement.Range.Start.Line == 1);
        Assert.DoesNotContain(
            tree.Diagnostics,
            diagnostic => diagnostic.Code.StartsWith(
                "syntax.malformedPreprocessor",
                StringComparison.Ordinal));
    }

    [Fact]
    public void ParserDoesNotReinterpretAContinuedDirectiveBodyAsVbaCode()
    {
        const string source = "#If VBA7 _\n"
            + "Public Sub Hidden()\n"
            + "#End If";

        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            source);

        Assert.Empty(tree.Module.CallableDeclarations);
        Assert.DoesNotContain(
            tree.Module.Blocks,
            block => block.Kind == VbaBlockKind.Procedure);
        Assert.Contains(
            tree.Diagnostics,
            diagnostic => diagnostic.Code == "syntax.malformedPreprocessorDirective");
    }
}
