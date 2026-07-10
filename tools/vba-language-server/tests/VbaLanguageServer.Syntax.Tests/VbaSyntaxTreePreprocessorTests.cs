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
}
