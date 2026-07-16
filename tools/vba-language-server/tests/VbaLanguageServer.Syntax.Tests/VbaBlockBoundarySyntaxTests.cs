using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaLanguageServer.Syntax.Tests;

public sealed class VbaBlockBoundarySyntaxTests
{
    [Theory]
    [InlineData("    Else   ' keep", VbaBlockBranchKind.Else, 0)]
    [InlineData("    ElseIf ready Then", VbaBlockBranchKind.ElseIf, 0)]
    [InlineData("    ElseIf IsReady(value, Flag:=True) Then", VbaBlockBranchKind.ElseIf, 0)]
    [InlineData("    ElseIf TypeOf target Is Object Then", VbaBlockBranchKind.ElseIf, 0)]
    [InlineData("    ElseIf first _\n        And second Then   ' keep", VbaBlockBranchKind.ElseIf, 1)]
    public void Strict_if_branches_preserve_logical_range_and_indentation(
        string source,
        VbaBlockBranchKind expectedBranch,
        int finalLine)
    {
        var lines = source.Split('\n');
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var boundary = VbaBlockBoundarySyntax.FindAtFirstPhysicalLine(
            tree,
            firstPhysicalLine: 0,
            VbaBlockKind.If,
            expectedTerminator: "End If");

        Assert.NotNull(boundary);
        Assert.Equal(VbaBlockBoundaryRole.Branch, boundary.Role);
        Assert.Equal(expectedBranch, boundary.BranchKind);
        Assert.Equal(VbaBlockKind.If, boundary.OwnerBlockKind);
        Assert.Equal("End If", boundary.ExpectedTerminator);
        Assert.Equal(0, boundary.FirstPhysicalLine);
        Assert.Equal(finalLine, boundary.FinalPhysicalLine);
        Assert.Equal("    ", boundary.LeadingWhitespace);
        Assert.Equal(lines[finalLine].Length, boundary.Range.End.Character);
    }

    [Fact]
    public void Strict_if_closer_requires_the_exact_terminator_shape()
    {
        const string source = "    End If   ' keep";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var boundary = VbaBlockBoundarySyntax.FindAtFirstPhysicalLine(
            tree,
            firstPhysicalLine: 0,
            VbaBlockKind.If,
            expectedTerminator: "End If");

        Assert.NotNull(boundary);
        Assert.Equal(VbaBlockBoundaryRole.Closer, boundary.Role);
        Assert.Null(boundary.BranchKind);
    }

    [Fact]
    public void Leading_member_elseif_requires_an_enclosing_with_block()
    {
        const string outside = "ElseIf .Enabled Then";
        const string inside = "Public Sub Main()\n"
            + "    With target\n"
            + "        If True Then\n"
            + "        ElseIf .Enabled Then";
        var outsideTree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", outside);
        var insideTree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", inside);

        Assert.Null(VbaBlockBoundarySyntax.FindAtFirstPhysicalLine(
            outsideTree,
            0,
            VbaBlockKind.If,
            "End If"));
        Assert.NotNull(VbaBlockBoundarySyntax.FindAtFirstPhysicalLine(
            insideTree,
            3,
            VbaBlockKind.If,
            "End If"));
    }

    [Theory]
    [InlineData("ElseIf Then")]
    [InlineData("ElseIf ready And Then")]
    [InlineData("Else value")]
    [InlineData("End If garbage")]
    [InlineData("Else:")]
    [InlineData("label: Else")]
    [InlineData("Else Rem not-an-apostrophe-comment")]
    [InlineData("#Else")]
    public void Malformed_prefixed_and_preprocessor_boundaries_are_rejected(string source)
    {
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var boundary = VbaBlockBoundarySyntax.FindAtFirstPhysicalLine(
            tree,
            firstPhysicalLine: 0,
            VbaBlockKind.If,
            expectedTerminator: "End If");

        Assert.Null(boundary);
    }
}
