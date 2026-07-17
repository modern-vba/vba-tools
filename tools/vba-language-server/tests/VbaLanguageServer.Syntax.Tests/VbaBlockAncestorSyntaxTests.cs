using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaLanguageServer.Syntax.Tests;

public sealed class VbaBlockAncestorSyntaxTests
{
    [Fact]
    public void Complete_sub_ancestor_is_accepted()
    {
        const string source = "Public Sub Main()\nEnd Sub";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.Procedure);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.True(isComplete);
    }

    [Fact]
    public void Incomplete_sub_ancestor_is_rejected()
    {
        const string source = "Public Sub Main(";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.Procedure);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.False(isComplete);
    }

    [Fact]
    public void Complete_function_ancestor_is_accepted()
    {
        const string source = "Public Function TryGet(ByVal key As String) As Boolean\nEnd Function";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.Procedure);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.True(isComplete);
    }

    [Fact]
    public void Complete_continued_function_ancestor_is_accepted()
    {
        const string source = "Public Function TryGet( _\n"
            + "    ByVal key As String) As Boolean\n"
            + "End Function";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.Procedure);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.True(isComplete);
    }

    [Theory]
    [InlineData("Public Function Ready() As Boolean Static\nEnd Function")]
    [InlineData("Public Property Get Ready() As Boolean Static\nEnd Property")]
    [InlineData("Public Property Let Ready(ByVal value As Boolean) Static\nEnd Property")]
    [InlineData("Public Property Set Ready(ByVal value As Object) Static\nEnd Property")]
    public void Complete_trailing_static_callable_ancestors_are_accepted(string source)
    {
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.Procedure);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.True(isComplete);
    }

    [Theory]
    [InlineData("Public Function Ready(ByVal ready As Boolean) As Boolean\nEnd Function")]
    [InlineData("Public Property Get Ready(ByVal ready As Boolean) As Boolean\nEnd Property")]
    public void Function_like_callable_rejects_a_parameter_with_the_result_name(string source)
    {
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.Procedure);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.False(isComplete);
    }

    [Theory]
    [InlineData("Public Property Let Item(Optional index As Long = 0, ByVal value As Long)\nEnd Property")]
    [InlineData("Public Property Set Item(ParamArray keys() As Variant, ByVal value As Object)\nEnd Property")]
    public void Property_lhs_accepts_a_required_value_after_its_optional_parameter_list(
        string source)
    {
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.Procedure);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.True(isComplete);
    }

    [Theory]
    [InlineData("Public Property Let Item(, ByVal value As Long)\nEnd Property")]
    [InlineData("Public Property Set Item(, ByVal value As Object)\nEnd Property")]
    public void Property_lhs_rejects_a_comma_without_a_parameter_list(string source)
    {
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.Procedure);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.False(isComplete);
    }

    [Theory]
    [InlineData("Public Function Values() As Long()\nEnd Function")]
    [InlineData("Public Property Get Values() As Long()\nEnd Property")]
    [InlineData("Public Property Let Values(values() As Long)\nEnd Property")]
    public void Complete_array_callable_ancestors_are_accepted(string source)
    {
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.Procedure);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.True(isComplete);
    }

    [Fact]
    public void Property_set_rejects_an_array_reference_parameter()
    {
        const string source = "Public Property Set Values(values() As Object)\nEnd Property";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.Procedure);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.False(isComplete);
    }

    [Fact]
    public void Complete_property_get_ancestor_is_accepted()
    {
        const string source = "Public Property Get Item(ByVal index As Long) As String\nEnd Property";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Class1.cls", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.Procedure);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.True(isComplete);
    }

    [Fact]
    public void Complete_property_let_ancestor_is_accepted()
    {
        const string source = "Public Property Let Item(ByVal value As String)\nEnd Property";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Class1.cls", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.Procedure);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.True(isComplete);
    }

    [Fact]
    public void Property_let_without_a_value_parameter_is_rejected()
    {
        const string source = "Public Property Let Item\nEnd Property";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Class1.cls", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.Procedure);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.False(isComplete);
    }

    [Fact]
    public void Complete_property_set_ancestor_is_accepted()
    {
        const string source = "Public Property Set Item(ByVal value As Object)\nEnd Property";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Class1.cls", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.Procedure);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.True(isComplete);
    }

    [Fact]
    public void Property_let_requires_a_non_optional_final_value_parameter()
    {
        const string source = "Public Property Let Item(Optional ByVal value As String = \"\")\nEnd Property";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Class1.cls", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.Procedure);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.False(isComplete);
    }

    [Fact]
    public void Property_set_rejects_a_paramarray_reference_parameter()
    {
        const string source = "Public Property Set Item(ParamArray value() As Variant)\nEnd Property";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Class1.cls", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.Procedure);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.False(isComplete);
    }

    [Fact]
    public void Property_set_rejects_a_value_typed_reference_parameter()
    {
        const string source = "Public Property Set Item(ByVal value As Long)\nEnd Property";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Class1.cls", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.Procedure);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.False(isComplete);
    }

    [Fact]
    public void Property_set_named_reference_type_remains_syntax_complete()
    {
        const string source = "Public Property Set Item(ByVal value As Excel.Range)\nEnd Property";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Class1.cls", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.Procedure);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.True(isComplete);
    }

    [Fact]
    public void Function_with_an_incomplete_return_type_is_rejected()
    {
        const string source = "Public Function Main() As\nEnd Function";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.Procedure);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.False(isComplete);
    }

    [Fact]
    public void Callable_with_duplicate_parameter_names_is_rejected()
    {
        const string source = "Public Sub Main(ByVal value As Long, ByRef VALUE As String)\nEnd Sub";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.Procedure);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.False(isComplete);
    }

    [Fact]
    public void Callable_with_an_unterminated_default_string_is_rejected()
    {
        const string source = "Public Sub Main(Optional value As String = \"broken)\nEnd Sub";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.Procedure);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.False(isComplete);
    }

    [Fact]
    public void Optional_named_type_parameter_remains_syntax_complete_without_type_resolution()
    {
        const string source = "Public Sub Main(Optional target As Excel.Range = 1)\nEnd Sub";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.Procedure);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.True(isComplete);
    }

    [Fact]
    public void Complete_callable_header_does_not_require_an_owner_closer()
    {
        const string source = "Public Function Main() As Boolean";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.Procedure);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.True(isComplete);
    }

    [Fact]
    public void Present_callable_closer_must_have_an_exact_shape()
    {
        const string source = "Public Function Main() As Boolean\nEnd Function garbage";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.Procedure);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.False(isComplete);
    }

    [Fact]
    public void Complete_block_if_ancestor_is_accepted()
    {
        const string source = "Public Sub Main()\n"
            + "    If Ready(Flag:=True) Then\n"
            + "    End If\n"
            + "End Sub";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.If);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.True(isComplete);
    }

    [Fact]
    public void Present_if_closer_must_have_an_exact_shape()
    {
        const string source = "Public Sub Main()\n"
            + "    If True Then\n"
            + "    End If garbage\n"
            + "End Sub";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.If);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.False(isComplete);
    }

    [Fact]
    public void Complete_if_header_does_not_require_a_closer_at_eof()
    {
        const string source = "Public Sub Main()\n"
            + "    If True Then";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.If);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.True(isComplete);
    }

    [Fact]
    public void Complete_nested_if_ancestors_are_each_accepted()
    {
        const string source = "Public Sub Main()\n"
            + "    If outerCondition Then\n"
            + "        If innerCondition Then\n"
            + "        End If\n"
            + "    End If\n"
            + "End Sub";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var blocks = tree.Module.Blocks
            .Where(candidate => candidate.Kind == VbaBlockKind.If)
            .OrderBy(candidate => candidate.OpenerRange.Start.Offset)
            .ToArray();

        Assert.Equal(2, blocks.Length);
        Assert.All(blocks, block =>
            Assert.True(VbaBlockAncestorSyntax.IsComplete(tree, block)));
    }

    [Fact]
    public void Block_if_with_a_missing_condition_is_rejected()
    {
        const string source = "Public Sub Main()\n"
            + "    If Then\n"
            + "    End If\n"
            + "End Sub";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.If);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.False(isComplete);
    }

    [Fact]
    public void Complete_elseif_and_else_branches_are_accepted()
    {
        const string source = "Public Sub Main()\n"
            + "    If first Then\n"
            + "    ElseIf second Then\n"
            + "    Else\n"
            + "    End If\n"
            + "End Sub";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.If);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.True(isComplete);
    }

    [Fact]
    public void If_ancestor_with_a_malformed_elseif_branch_is_rejected()
    {
        const string source = "Public Sub Main()\n"
            + "    If first Then\n"
            + "    ElseIf Then\n"
            + "        If True Then\n"
            + "        End If\n"
            + "    End If\n"
            + "End Sub";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.If
            && candidate.OpenerRange.Start.Line == 1);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.False(isComplete);
    }

    [Fact]
    public void If_ancestor_with_a_malformed_else_branch_is_rejected()
    {
        const string source = "Public Sub Main()\n"
            + "    If first Then\n"
            + "    Else value\n"
            + "    End If\n"
            + "End Sub";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.If);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.False(isComplete);
    }

    [Fact]
    public void Complete_continued_elseif_branch_is_accepted()
    {
        const string source = "Public Sub Main()\n"
            + "    If first Then\n"
            + "    ElseIf second _\n"
            + "        And third Then   ' keep\n"
            + "    End If\n"
            + "End Sub";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.If);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.True(isComplete);
    }

    [Fact]
    public void If_ancestor_rejects_a_branch_suffix_recovered_as_a_malformed_barrier()
    {
        const string source = "Public Sub Main()\n"
            + "    If first Then\n"
            + "    ElseIf second Then\n"
            + "    Else\n"
            + "    Else\n"
            + "    End If\n"
            + "End Sub";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.If);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.False(isComplete);
    }

    [Fact]
    public void Conditional_compilation_ancestor_context_fails_closed()
    {
        const string source = "#If VBA7 Then\n"
            + "Public Sub Main()\n"
            + "End Sub\n"
            + "#End If";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.Procedure);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.False(isComplete);
    }

    [Theory]
    [InlineData("Public Sub Main()\n    With target\n    End With\nEnd Sub")]
    [InlineData("Public Sub Main()\n    With target")]
    [InlineData("Public Sub Main()\n    With target _\n        .Parent\n    End With\nEnd Sub")]
    public void Complete_with_ancestor_with_optional_closer_is_accepted(string source)
    {
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.With);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.True(isComplete);
    }

    [Theory]
    [InlineData("Public Sub Main()\n    With\n    End With\nEnd Sub")]
    [InlineData("Public Sub Main()\n    With target +\n    End With\nEnd Sub")]
    [InlineData("Public Sub Main()\n    With target\n    End With extra\nEnd Sub")]
    public void Malformed_with_ancestor_is_rejected(string source)
    {
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.With);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.False(isComplete);
    }

    [Theory]
    [InlineData("Public Sub Main()\n    For index = 1 To 3\n    Next\nEnd Sub")]
    [InlineData("Public Sub Main()\n    For Each item In items\n    Next\nEnd Sub")]
    [InlineData("Public Sub Main()\n    For index = 1 To 3")]
    public void Complete_for_ancestor_with_optional_bare_closer_is_accepted(string source)
    {
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.For);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.True(isComplete);
    }

    [Fact]
    public void Complete_nested_for_ancestors_are_each_accepted()
    {
        const string source = "Public Sub Main()\n"
            + "    For outerIndex = 1 To 3\n"
            + "        For Each item In items\n"
            + "        Next\n"
            + "    Next\n"
            + "End Sub";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var blocks = tree.Module.Blocks
            .Where(candidate => candidate.Kind == VbaBlockKind.For)
            .OrderBy(candidate => candidate.OpenerRange.Start.Offset)
            .ToArray();

        Assert.Equal(2, blocks.Length);
        Assert.All(blocks, block =>
            Assert.True(VbaBlockAncestorSyntax.IsComplete(tree, block)));
    }

    [Theory]
    [InlineData("Public Sub Main()\n    For index = To 3\n    Next\nEnd Sub")]
    [InlineData("Public Sub Main()\n    For Each item In\n    Next\nEnd Sub")]
    [InlineData("Public Sub Main()\n    For index = 1 To 3\n    Next index\nEnd Sub")]
    [InlineData("Public Sub Main()\n    For index = 1 To 3\n    Next index, outerIndex\nEnd Sub")]
    public void Malformed_or_explicit_for_ancestor_is_rejected(string source)
    {
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.For);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.False(isComplete);
    }

    [Fact]
    public void Complete_select_ancestor_without_branches_is_accepted()
    {
        const string source = "Public Sub Main()\n"
            + "    Select Case value\n"
            + "    End Select\n"
            + "End Sub";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.Select);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.True(isComplete);
    }

    [Fact]
    public void Complete_select_ancestor_with_strict_case_branches_is_accepted()
    {
        const string source = "Public Sub Main()\n"
            + "    Select Case value\n"
            + "    Case 1 To 3, Is >= minimum\n"
            + "    Case Else\n"
            + "    End Select\n"
            + "End Sub";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.Select);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.True(isComplete);
    }

    [Fact]
    public void Select_ancestor_allows_comments_before_the_first_case()
    {
        const string source = "Public Sub Main()\n"
            + "    Select Case value\n"
            + "        ' apostrophe comment\n"
            + "        Rem comment\n"
            + "    Case 1\n"
            + "    End Select\n"
            + "End Sub";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.Select);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.True(isComplete);
    }

    [Theory]
    [InlineData(
        "Public Sub Main()\n"
        + "    Select Case value\n"
        + "        Debug.Print value\n"
        + "    Case 1\n"
        + "    End Select\n"
        + "End Sub")]
    [InlineData(
        "Public Sub Main()\n"
        + "    Select Case value\n"
        + "        Debug.Print value\n"
        + "    End Select\n"
        + "End Sub")]
    public void Select_ancestor_rejects_significant_statements_before_the_first_case(
        string source)
    {
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.Select);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.False(isComplete);
    }

    [Fact]
    public void Complete_nested_select_ancestors_are_each_accepted()
    {
        const string source = "Public Sub Main()\n"
            + "    Select Case outerValue\n"
            + "    Case 1\n"
            + "        Select Case innerValue\n"
            + "        Case Else\n"
            + "        End Select\n"
            + "    End Select\n"
            + "End Sub";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var blocks = tree.Module.Blocks
            .Where(candidate => candidate.Kind == VbaBlockKind.Select)
            .OrderBy(candidate => candidate.OpenerRange.Start.Offset)
            .ToArray();

        Assert.Equal(2, blocks.Length);
        Assert.All(blocks, block =>
            Assert.True(VbaBlockAncestorSyntax.IsComplete(tree, block)));
    }

    [Theory]
    [InlineData("Public Sub Main()\n    Select Case\n    End Select\nEnd Sub")]
    [InlineData("Public Sub Main()\n    Select Case(value)\n    End Select\nEnd Sub")]
    [InlineData("Public Sub Main()\n    Select Case value +\n    End Select\nEnd Sub")]
    [InlineData("Public Sub Main()\n    Select Case value\n    Case\n    End Select\nEnd Sub")]
    [InlineData("Public Sub Main()\n    Select Case value\n    Case 1 To\n    End Select\nEnd Sub")]
    [InlineData("Public Sub Main()\n    Select Case value\n    Case Else extra\n    End Select\nEnd Sub")]
    [InlineData(
        "Public Sub Main()\n"
        + "    Select Case value\n"
        + "    Case first, _ ' invalid continuation\n"
        + "        second\n"
        + "    End Select\n"
        + "End Sub")]
    [InlineData("Public Sub Main()\n    Select Case value\n    End Select extra\nEnd Sub")]
    [InlineData("Public Sub Main()\n    Select Case value\n    Case Else\n    Case 2\n    End Select\nEnd Sub")]
    public void Malformed_select_ancestor_is_rejected(string source)
    {
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == VbaBlockKind.Select);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.False(isComplete);
    }

    [Theory]
    [InlineData("Do\nLoop", VbaBlockKind.Do)]
    [InlineData("While True\nWend", VbaBlockKind.While)]
    [InlineData("Public Enum Mode\nEnd Enum", VbaBlockKind.Enum)]
    [InlineData("Public Type Item\nEnd Type", VbaBlockKind.Type)]
    public void Cross_kind_ancestors_are_conservatively_rejected(
        string source,
        VbaBlockKind kind)
    {
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var block = Assert.Single(tree.Module.Blocks, candidate =>
            candidate.Kind == kind);

        var isComplete = VbaBlockAncestorSyntax.IsComplete(tree, block);

        Assert.False(isComplete);
    }
}
