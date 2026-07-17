using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaLanguageServer.Syntax.Tests;

public sealed class VbaConditionalCompilationBranchFactsTests
{
    [Fact]
    public void Branch_paths_are_immutable_structural_values()
    {
        var identity = new VbaConditionalCompilationBranchIdentity(10, 20);
        var source = new[] { identity };
        var path = new VbaConditionalCompilationBranchPath(source);
        var equal = new VbaConditionalCompilationBranchPath([identity]);

        source[0] = new VbaConditionalCompilationBranchIdentity(30, 40);

        Assert.Equal(identity, Assert.Single(path.Branches));
        Assert.Equal(path, equal);
        Assert.True(path == equal);
        Assert.False(path != equal);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<VbaConditionalCompilationBranchIdentity>)path.Branches)[0] =
                source[0]);
    }

    [Fact]
    public void Coexistence_distinguishes_sibling_branches_from_independent_blocks()
    {
        var outerIf = new VbaConditionalCompilationBranchIdentity(10, 10);
        var outerElse = new VbaConditionalCompilationBranchIdentity(10, 20);
        var nestedIf = new VbaConditionalCompilationBranchIdentity(30, 30);
        var independentIf = new VbaConditionalCompilationBranchIdentity(40, 40);
        var root = new VbaConditionalCompilationBranchPath([]);
        var selected = new VbaConditionalCompilationBranchPath([outerIf]);
        var sibling = new VbaConditionalCompilationBranchPath([outerElse]);
        var nested = new VbaConditionalCompilationBranchPath([outerIf, nestedIf]);
        var independent = new VbaConditionalCompilationBranchPath([independentIf]);

        Assert.True(VbaConditionalCompilationBranchFacts.CanCoexist(root, selected));
        Assert.True(VbaConditionalCompilationBranchFacts.CanCoexist(selected, nested));
        Assert.False(VbaConditionalCompilationBranchFacts.CanCoexist(selected, sibling));
        Assert.True(VbaConditionalCompilationBranchFacts.CanCoexist(
            selected,
            independent));
    }

    [Fact]
    public void Block_locality_rejects_a_block_range_crossing_its_branch_envelope()
    {
        const string source = "#If VBA7 Then\n"
            + "Public Sub Run()\n"
            + "End Sub\n"
            + "#Else\n"
            + "#End If";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var block = Assert.Single(
            tree.Module.Blocks,
            candidate => candidate.Kind == VbaBlockKind.Procedure);
        Assert.True(VbaConditionalCompilationBranchFacts.TryGetPath(
            tree,
            block.OpenerRange,
            requireCompleteStructure: true,
            out var path));
        Assert.True(VbaConditionalCompilationBranchFacts.IsBlockLocal(
            tree,
            block,
            path,
            requireCompleteStructure: true));

        var crossing = block with
        {
            Range = new VbaSyntaxRange(block.Range.Start, tree.SourceText.FullRange.End)
        };

        Assert.False(VbaConditionalCompilationBranchFacts.IsBlockLocal(
            tree,
            crossing,
            path,
            requireCompleteStructure: true));
    }

    [Fact]
    public void Block_locality_rejects_a_runtime_branch_range_crossing_its_branch_envelope()
    {
        const string source = "#If VBA7 Then\n"
            + "Public Sub Run()\n"
            + "    If True Then\n"
            + "    Else\n"
            + "    End If\n"
            + "End Sub\n"
            + "#Else\n"
            + "#End If";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var block = Assert.Single(
            tree.Module.Blocks,
            candidate => candidate.Kind == VbaBlockKind.If);
        Assert.True(VbaConditionalCompilationBranchFacts.TryGetPath(
            tree,
            block.OpenerRange,
            requireCompleteStructure: true,
            out var path));
        Assert.True(VbaConditionalCompilationBranchFacts.IsBlockLocal(
            tree,
            block,
            path,
            requireCompleteStructure: true));

        var branches = block.Branches
            .Select((branch, index) => index == 0
                ? branch with
                {
                    Range = new VbaSyntaxRange(
                        branch.Range.Start,
                        tree.SourceText.FullRange.End)
                }
                : branch)
            .ToArray();
        var crossing = block with { Branches = branches };

        Assert.False(VbaConditionalCompilationBranchFacts.IsBlockLocal(
            tree,
            crossing,
            path,
            requireCompleteStructure: true));
    }

    [Fact]
    public void Header_discovery_fails_closed_beyond_the_supported_conditional_depth()
    {
        const int depth = 129;
        const string header = "Public Sub Run()";
        var lines = Enumerable.Repeat("#If True Then", depth)
            .Append(header)
            .Concat(Enumerable.Repeat("#End If", depth));
        var source = string.Join('\n', lines);
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var result = VbaBlockHeaderSyntax.FindAtPosition(
            tree,
            line: depth,
            character: header.Length);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("#If INNER Then\n#End If")]
    [InlineData("#Const Feature = True")]
    public void Nonclosing_directives_are_not_branch_boundaries(string directiveText)
    {
        const string header = "Public Sub Run()";
        var source = "#If OUTER Then\n"
            + $"{header}\n"
            + $"{directiveText}\n"
            + "#End If";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var block = Assert.Single(
            tree.Module.Blocks,
            candidate => candidate.Kind == VbaBlockKind.Procedure);
        Assert.True(VbaConditionalCompilationBranchFacts.TryGetPath(
            tree,
            block.OpenerRange,
            requireCompleteStructure: true,
            out var path));

        var result = VbaConditionalCompilationBranchFacts.TryGetClosingBoundary(
            tree,
            path,
            line: 2,
            out _);

        Assert.False(result);
    }
}
