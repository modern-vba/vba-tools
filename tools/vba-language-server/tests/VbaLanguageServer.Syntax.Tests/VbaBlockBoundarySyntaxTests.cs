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
    public void Strict_select_case_expression_list_branch_is_accepted()
    {
        const string source =
            "    Case Is >= minimum, 1 To maximum, Match(value, Flag:=True)";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var boundary = VbaBlockBoundarySyntax.FindAtFirstPhysicalLine(
            tree,
            firstPhysicalLine: 0,
            VbaBlockKind.Select,
            expectedTerminator: "End Select");

        Assert.NotNull(boundary);
        Assert.Equal(VbaBlockBoundaryRole.Branch, boundary.Role);
        Assert.Equal(VbaBlockBranchKind.Case, boundary.BranchKind);
        Assert.Equal(VbaBlockKind.Select, boundary.OwnerBlockKind);
    }

    [Theory]
    [InlineData("    Case value", VbaBlockBranchKind.Case, 0)]
    [InlineData("    Case(1)", VbaBlockBranchKind.Case, 0)]
    [InlineData("    Case-1", VbaBlockBranchKind.Case, 0)]
    [InlineData("    Case.5", VbaBlockBranchKind.Case, 0)]
    [InlineData("    Case>1", VbaBlockBranchKind.Case, 0)]
    [InlineData("    Case source.To", VbaBlockBranchKind.Case, 0)]
    [InlineData("    Case 1.To 2", VbaBlockBranchKind.Case, 0)]
    [InlineData("    Case 1!To 2", VbaBlockBranchKind.Case, 0)]
    [InlineData("    Case 1! To 2", VbaBlockBranchKind.Case, 0)]
    [InlineData("    Case 1.!To 2", VbaBlockBranchKind.Case, 0)]
    [InlineData("    Case 1E3!To 2", VbaBlockBranchKind.Case, 0)]
    [InlineData("    Case 1.E3!To 2", VbaBlockBranchKind.Case, 0)]
    [InlineData("    Case source.To To maximum", VbaBlockBranchKind.Case, 0)]
    [InlineData("    Case source!To", VbaBlockBranchKind.Case, 0)]
    [InlineData("    Case source!To To maximum", VbaBlockBranchKind.Case, 0)]
    [InlineData("    Case >= minimum", VbaBlockBranchKind.Case, 0)]
    [InlineData("    Case Is >< excluded", VbaBlockBranchKind.Case, 0)]
    [InlineData("    Case Is < > excluded", VbaBlockBranchKind.Case, 0)]
    [InlineData("    Case Is > = minimum", VbaBlockBranchKind.Case, 0)]
    [InlineData("    Case = _\n        > excluded", VbaBlockBranchKind.Case, 1)]
    [InlineData("    Case value > < other", VbaBlockBranchKind.Case, 0)]
    [InlineData("    Case 1 To (upper = > lower)", VbaBlockBranchKind.Case, 0)]
    [InlineData("    Case Else   ' keep", VbaBlockBranchKind.CaseElse, 0)]
    [InlineData("    Case first, _\n        second   ' keep", VbaBlockBranchKind.Case, 1)]
    public void Strict_select_branches_preserve_logical_range_and_indentation(
        string source,
        VbaBlockBranchKind expectedBranch,
        int finalLine)
    {
        var lines = source.Split('\n');
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var boundary = VbaBlockBoundarySyntax.FindAtFirstPhysicalLine(
            tree,
            firstPhysicalLine: 0,
            VbaBlockKind.Select,
            expectedTerminator: "End Select");

        Assert.NotNull(boundary);
        Assert.Equal(VbaBlockBoundaryRole.Branch, boundary.Role);
        Assert.Equal(expectedBranch, boundary.BranchKind);
        Assert.Equal(0, boundary.FirstPhysicalLine);
        Assert.Equal(finalLine, boundary.FinalPhysicalLine);
        Assert.Equal("    ", boundary.LeadingWhitespace);
        Assert.Equal(lines[finalLine].Length, boundary.Range.End.Character);
    }

    [Theory]
    [InlineData(".To")]
    [InlineData("!To")]
    public void Leading_member_select_ranges_require_an_enclosing_with_block(
        string member)
    {
        var branch = $"Case {member} To maximum";
        var inside = "Public Sub Main()\n"
            + "    With target\n"
            + "        Select Case value\n"
            + $"        {branch}";
        var outsideTree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Module1.bas",
            branch);
        var insideTree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Module1.bas",
            inside);

        Assert.Null(VbaBlockBoundarySyntax.FindAtFirstPhysicalLine(
            outsideTree,
            0,
            VbaBlockKind.Select,
            "End Select"));
        Assert.NotNull(VbaBlockBoundarySyntax.FindAtFirstPhysicalLine(
            insideTree,
            3,
            VbaBlockKind.Select,
            "End Select"));
    }

    [Fact]
    public void Dictionary_access_followed_by_an_extra_expression_is_rejected()
    {
        const string outside = "Case source!To 2";
        const string inside = "Public Sub Main()\n"
            + "    With target\n"
            + "        Select Case value\n"
            + "        Case .Value!To 2";
        var outsideTree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Module1.bas",
            outside);
        var insideTree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Module1.bas",
            inside);

        Assert.Null(VbaBlockBoundarySyntax.FindAtFirstPhysicalLine(
            outsideTree,
            0,
            VbaBlockKind.Select,
            "End Select"));
        Assert.Null(VbaBlockBoundarySyntax.FindAtFirstPhysicalLine(
            insideTree,
            3,
            VbaBlockKind.Select,
            "End Select"));
    }

    [Fact]
    public void Strict_select_closer_requires_the_exact_terminator_shape()
    {
        const string source = "    End Select   ' keep";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var boundary = VbaBlockBoundarySyntax.FindAtFirstPhysicalLine(
            tree,
            firstPhysicalLine: 0,
            VbaBlockKind.Select,
            expectedTerminator: "End Select");

        Assert.NotNull(boundary);
        Assert.Equal(VbaBlockBoundaryRole.Closer, boundary.Role);
        Assert.Null(boundary.BranchKind);
    }

    [Theory]
    [InlineData("Case")]
    [InlineData("Case 1 To")]
    [InlineData("Case Is >")]
    [InlineData("Case source!To 2")]
    [InlineData("Case 1,")]
    [InlineData("Case 1,,2")]
    [InlineData("Case 1 To 2 To 3")]
    [InlineData("Case Else extra")]
    [InlineData("Case first, _ ' invalid continuation\n    second")]
    [InlineData("Case 1:")]
    [InlineData("label: Case 1")]
    [InlineData("End Select garbage")]
    [InlineData("#Case 1")]
    public void Unsupported_and_malformed_select_boundaries_are_rejected(string source)
    {
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var boundary = VbaBlockBoundarySyntax.FindAtFirstPhysicalLine(
            tree,
            firstPhysicalLine: 0,
            VbaBlockKind.Select,
            expectedTerminator: "End Select");

        Assert.Null(boundary);
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
