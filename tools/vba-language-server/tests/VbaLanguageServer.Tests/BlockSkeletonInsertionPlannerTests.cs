using VbaLanguageServer.BlockSkeletonInsertion;
using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Syntax;
using VbaLanguageServer.Workspace;
using Xunit;
using PublishedSyntaxDiagnostic = VbaLanguageServer.Diagnostics.VbaSyntaxDiagnostic;

namespace VbaLanguageServer.Tests;

public sealed class BlockSkeletonInsertionPlannerTests
{
    [Fact]
    public void Planner_inserts_a_with_skeleton_inside_a_callable_body()
    {
        const string uri = "file:///C:/work/With.bas";
        const string header = "    With target.Parent";
        const string text = "Public Sub Main()\n    With target.Parent\n    \nEnd Sub";
        var snapshot = CreateSnapshot(uri, version: 10, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(1, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 2));

        Assert.NotNull(plan);
        Assert.Equal("\n      ", plan.TextBeforeCursor);
        Assert.Equal("\n    End With", plan.TextAfterCursor);
        Assert.Equal(
            "Public Sub Main()\n    With target.Parent\n      \n    End With\nEnd Sub",
            ApplyPlan(text, VbaSourceText.From(text), plan));
    }

    [Theory]
    [InlineData("New Class1")]
    [InlineData("New Project1.Class1")]
    [InlineData("target!Field")]
    [InlineData("Date.Value")]
    [InlineData("String(2, \"x\")!Value")]
    [InlineData("Strings.Len(value)")]
    [InlineData("VBA.Date.Value")]
    [InlineData("VBA.Array(1)")]
    [InlineData("VBA.String(2, \"x\")")]
    public void Planner_accepts_strict_with_receiver_forms(string receiver)
    {
        const string uri = "file:///C:/work/WithReceiver.bas";
        var header = $"    With {receiver}";
        var text = $"Public Sub Main()\n{header}\n    \nEnd Sub";
        var snapshot = CreateSnapshot(uri, version: 10, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(1, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 2));

        Assert.NotNull(plan);
        Assert.Equal("\n    End With", plan.TextAfterCursor);
    }

    [Theory]
    [InlineData("CStr(value)")]
    [InlineData("VBA.Constants.vbCrLf")]
    [InlineData("VbCompareMethod.vbBinaryCompare")]
    [InlineData("VBA.ColorConstants.vbRed")]
    [InlineData("Err.Number")]
    [InlineData("VBA")]
    [InlineData("VBA.Strings")]
    [InlineData("VBA.DateTime.Timer.Value")]
    [InlineData("VBA.DateTime.Timer!Field")]
    [InlineData("Strings.Unknown")]
    [InlineData("VBA.Strings.Unknown")]
    [InlineData("VbDayOfWeek.Unknown")]
    [InlineData("Err.Unknown")]
    [InlineData("VBA.Err.Unknown")]
    [InlineData("Global.Unknown")]
    [InlineData("VBA.Unknown")]
    [InlineData("VBA.String(1)")]
    [InlineData("VBA.CVar()")]
    public void Planner_rejects_known_non_receiver_with_expressions(string receiver)
    {
        const string uri = "file:///C:/work/InvalidWithReceiver.bas";
        var header = $"    With {receiver}";
        var text = $"Public Sub Main()\n{header}\n    \nEnd Sub";
        var snapshot = CreateSnapshot(uri, version: 10, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(1, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 2));

        Assert.Null(plan);
    }

    [Fact]
    public void Planner_keeps_a_shallow_end_with_with_the_ancestor_of_a_fresh_nested_with()
    {
        const string uri = "file:///C:/work/NestedWith.bas";
        const string header = "        With .Font";
        const string text = "Public Sub Main()\n"
            + "    With target\n"
            + "        With .Font\n"
            + "        \n"
            + "    End With\n"
            + "End Sub";
        var snapshot = CreateSnapshot(uri, version: 11, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(2, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 2));

        Assert.NotNull(plan);
        Assert.Equal("\n          ", plan.TextBeforeCursor);
        Assert.Equal("\n        End With", plan.TextAfterCursor);
        var prospectiveText = ApplyPlan(text, VbaSourceText.From(text), plan);
        Assert.Equal(
            "Public Sub Main()\n"
                + "    With target\n"
                + "        With .Font\n"
                + "          \n"
                + "        End With\n"
                + "    End With\n"
                + "End Sub",
            prospectiveText);
        Assert.True(CountErrors(snapshot.Diagnostics) > 0);
        Assert.Equal(
            0,
            CountErrors(VbaDiagnosticPipeline.CollectDocument(
                VbaSyntaxTree.ParseModule(uri, prospectiveText),
                uri)));
    }

    [Theory]
    [InlineData("    Else")]
    [InlineData("    ElseIf OtherReady() Then")]
    [InlineData("    End If")]
    public void Planner_preserves_a_proven_if_ancestor_boundary_for_a_with_candidate(
        string boundary)
    {
        const string uri = "file:///C:/work/WithIfBoundary.bas";
        const string header = "        With target";
        var outerCloser = boundary.Equals("    End If", StringComparison.Ordinal)
            ? string.Empty
            : "    End If\n";
        var text = "Public Sub Main()\n"
            + "    If Ready() Then\n"
            + $"{header}\n"
            + "        \n"
            + $"{boundary}\n"
            + outerCloser
            + "End Sub";
        var snapshot = CreateSnapshot(uri, version: 12, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(2, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 2));

        Assert.NotNull(plan);
        Assert.Contains("        End With\n" + boundary, ApplyPlan(text, VbaSourceText.From(text), plan));
    }

    [Fact]
    public void Planner_preserves_a_leading_member_if_ancestor_for_a_nested_with_candidate()
    {
        const string uri = "file:///C:/work/NestedWithMemberIf.bas";
        const string header = "            With .Font";
        var text = "Public Sub Main()\n"
            + "    With target\n"
            + "        If .Enabled Then\n"
            + $"{header}\n"
            + "            \n"
            + "        End If\n"
            + "    End With\n"
            + "End Sub";
        var snapshot = CreateSnapshot(uri, version: 13, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(3, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 2));

        Assert.NotNull(plan);
        Assert.Contains(
            "            End With\n        End If",
            ApplyPlan(text, VbaSourceText.From(text), plan));
    }

    [Theory]
    [InlineData("        .Value = 1")]
    [InlineData("        Debug.Print 1")]
    [InlineData("        ' existing comment")]
    [InlineData("        Rem existing comment")]
    [InlineData("    End With")]
    [InlineData("      End With")]
    public void Planner_rejects_body_owned_candidate_closed_and_ambiguous_with_context(
        string followingLine)
    {
        const string uri = "file:///C:/work/OwnedWithContext.bas";
        const string header = "    With target";
        var text = "Public Sub Main()\n"
            + $"{header}\n"
            + "    \n"
            + $"{followingLine}\n"
            + "End Sub";
        var snapshot = CreateSnapshot(uri, version: 13, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(1, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 2));

        Assert.Null(plan);
    }

    [Theory]
    [InlineData(
        "Public Sub Main()\n    With target\n    \n\nEnd Sub",
        "Public Sub Main()\n    With target\n      \n    End With\n\nEnd Sub")]
    [InlineData(
        "Public Sub Main()\r\n    With target\r\n        \r\n\r\nEnd Sub",
        "Public Sub Main()\r\n    With target\r\n      \r\n    End With\r\n\r\nEnd Sub")]
    public void Planner_preserves_existing_blank_lines_after_a_with_terminator(
        string text,
        string expectedText)
    {
        const string uri = "file:///C:/work/WithBlankLines.bas";
        const string header = "    With target";
        var snapshot = CreateSnapshot(uri, version: 14, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(1, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 2));

        Assert.NotNull(plan);
        Assert.Equal(expectedText, ApplyPlan(text, VbaSourceText.From(text), plan));
    }

    [Fact]
    public void Planner_preserves_continued_with_line_endings_and_first_line_indentation()
    {
        var lines = new[]
        {
            "Public Sub Main()",
            "  With Worksheets( _",
            "        \"Sheet1\")   ' keep",
            "      ",
            "End Sub"
        };
        var text = string.Join("\r\n", lines);
        var snapshot = CreateSnapshot(
            "file:///C:/work/ContinuedWith.bas",
            version: 15,
            text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(2, lines[2].Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 2));

        Assert.NotNull(plan);
        Assert.Equal("\r\n    ", plan.TextBeforeCursor);
        Assert.Equal("\r\n  End With", plan.TextAfterCursor);
    }

    [Fact]
    public void Planner_rejects_a_with_candidate_inside_a_malformed_with_ancestor()
    {
        const string uri = "file:///C:/work/MalformedWithAncestor.bas";
        const string header = "        With child";
        const string text = "Public Sub Main()\n"
            + "    With target +\n"
            + "        With child\n"
            + "        \n"
            + "    End With\n"
            + "End Sub";
        var snapshot = CreateSnapshot(uri, version: 16, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(2, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 2));

        Assert.Null(plan);
    }

    [Fact]
    public void Planner_inserts_a_block_if_before_its_callable_terminator()
    {
        const string uri = "file:///C:/work/If.bas";
        const string header = "    If True Then";
        const string text = "Public Sub Main()\n    If True Then\n    \nEnd Sub";
        var snapshot = CreateSnapshot(uri, version: 6, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(1, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 2));

        Assert.NotNull(plan);
        Assert.Equal("\n      ", plan.TextBeforeCursor);
        Assert.Equal("\n    End If", plan.TextAfterCursor);
        Assert.Equal(
            "Public Sub Main()\n    If True Then\n      \n    End If\nEnd Sub",
            ApplyPlan(text, VbaSourceText.From(text), plan));
    }

    [Theory]
    [InlineData("Public Function Ready() As Boolean", "End Function")]
    [InlineData("Public Function Ready() As Boolean Static", "End Function")]
    [InlineData("Public Function Values() As Long()", "End Function")]
    [InlineData("Public Property Get Ready() As Boolean", "End Property")]
    [InlineData("Public Property Let Ready(ByVal value As Boolean)", "End Property")]
    [InlineData("Public Property Let Ready(Optional index As Long = 0, ByVal value As Boolean) Static", "End Property")]
    [InlineData("Public Property Set Ready(ByVal value As Object)", "End Property")]
    public void Planner_inserts_a_block_if_inside_other_body_owning_callables(
        string callableHeader,
        string callableTerminator)
    {
        const string uri = "file:///C:/work/CallableIf.bas";
        const string header = "    If True Then";
        var text = $"{callableHeader}\n{header}\n    \n{callableTerminator}";
        var snapshot = CreateSnapshot(uri, version: 6, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(1, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 2));

        Assert.NotNull(plan);
        Assert.Equal("\n      ", plan.TextBeforeCursor);
        Assert.Equal("\n    End If", plan.TextAfterCursor);
    }

    [Fact]
    public void Planner_keeps_a_shallow_end_if_with_the_ancestor_of_a_fresh_nested_if()
    {
        const string uri = "file:///C:/work/NestedIf.bas";
        const string header = "        If True Then";
        const string text = "Public Sub Example()\n"
            + "    If True Then\n"
            + "        If True Then\n"
            + "        \n"
            + "    End If\n"
            + "End Sub";
        var snapshot = CreateSnapshot(uri, version: 9, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(2, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 2));

        Assert.NotNull(plan);
        Assert.Equal("\n          ", plan.TextBeforeCursor);
        Assert.Equal("\n        End If", plan.TextAfterCursor);
        var prospectiveText = ApplyPlan(text, VbaSourceText.From(text), plan);
        Assert.Equal(
            "Public Sub Example()\n"
                + "    If True Then\n"
                + "        If True Then\n"
                + "          \n"
                + "        End If\n"
                + "    End If\n"
                + "End Sub",
            prospectiveText);
        Assert.True(CountErrors(snapshot.Diagnostics) > 0);
        Assert.Equal(
            0,
            CountErrors(VbaDiagnosticPipeline.CollectDocument(
                VbaSyntaxTree.ParseModule(uri, prospectiveText),
                uri)));
    }

    [Theory]
    [InlineData("    Else")]
    [InlineData("    ElseIf OtherReady() Then")]
    public void Planner_preserves_a_proven_ancestor_if_branch(string boundary)
    {
        const string uri = "file:///C:/work/AncestorBranch.bas";
        const string header = "        If Ready() Then";
        var text = "Public Sub Main()\n"
            + "    If True Then\n"
            + $"{header}\n"
            + "        \n"
            + $"{boundary}\n"
            + "    End If\n"
            + "End Sub";
        var snapshot = CreateSnapshot(uri, version: 4, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(2, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 2));

        Assert.NotNull(plan);
        Assert.Contains("        End If\n" + boundary, ApplyPlan(text, VbaSourceText.From(text), plan));
    }

    [Fact]
    public void Planner_allows_an_if_at_eof_while_preserving_the_callable_missing_terminator()
    {
        const string uri = "file:///C:/work/IfAtEof.bas";
        const string header = "    If Ready() Then";
        const string text = "Public Sub Main()\n    If Ready() Then\n        ";
        var snapshot = CreateSnapshot(uri, version: 3, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(1, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.NotNull(plan);
        var prospectiveText = ApplyPlan(text, VbaSourceText.From(text), plan);
        var diagnostics = VbaDiagnosticPipeline.CollectDocument(
            VbaSyntaxTree.ParseModule(uri, prospectiveText),
            uri);
        Assert.Contains(
            diagnostics.SyntaxDiagnostics,
            diagnostic => diagnostic.Message == "Block is missing 'End Sub'.");
        Assert.DoesNotContain(
            diagnostics.SyntaxDiagnostics,
            diagnostic => diagnostic.Message == "Block is missing 'End If'.");
    }

    [Theory]
    [InlineData("        Else")]
    [InlineData("        ElseIf Ready() Then")]
    [InlineData("        End If")]
    [InlineData("          Debug.Print 1")]
    [InlineData("        ' existing comment")]
    [InlineData("        Rem existing comment")]
    [InlineData("      End If")]
    public void Planner_rejects_candidate_owned_body_and_ambiguous_if_context(string followingLine)
    {
        const string uri = "file:///C:/work/OwnedIfContext.bas";
        const string header = "        If True Then";
        var text = "Public Sub Main()\n"
            + "    If True Then\n"
            + $"{header}\n"
            + "        \n"
            + $"{followingLine}\n"
            + "    End If\n"
            + "End Sub";
        var snapshot = CreateSnapshot(uri, version: 8, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(2, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 2));

        Assert.Null(plan);
    }

    [Theory]
    [InlineData(
        "Public Sub Main()\n    If True Then\n    \n\nEnd Sub",
        "Public Sub Main()\n    If True Then\n      \n    End If\n\nEnd Sub")]
    [InlineData(
        "Public Sub Main()\r\n    If True Then\r\n        \r\n\r\nEnd Sub",
        "Public Sub Main()\r\n    If True Then\r\n      \r\n    End If\r\n\r\nEnd Sub")]
    public void Planner_preserves_existing_blank_lines_after_an_if_terminator(
        string text,
        string expectedText)
    {
        const string uri = "file:///C:/work/IfBlankLines.bas";
        const string header = "    If True Then";
        var snapshot = CreateSnapshot(uri, version: 5, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(1, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 2));

        Assert.NotNull(plan);
        Assert.Equal(expectedText, ApplyPlan(text, VbaSourceText.From(text), plan));
    }

    [Theory]
    [InlineData(
        "Public Sub Main()\n\tIf True Then\n\t\nEnd Sub",
        1,
        13,
        false,
        8,
        "\n\t\t",
        "\n\tEnd If")]
    [InlineData(
        "Public Sub Main()\r\n  If first _\r\n      And second Then\r\n      \r\nEnd Sub",
        2,
        21,
        true,
        2,
        "\r\n    ",
        "\r\n  End If")]
    public void Planner_preserves_if_line_endings_tabs_and_first_line_indentation(
        string text,
        int line,
        int character,
        bool insertSpaces,
        int indentSize,
        string expectedBeforeCursor,
        string expectedAfterCursor)
    {
        const string uri = "file:///C:/work/StyledIf.bas";
        var snapshot = CreateSnapshot(uri, version: 5, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(line, character),
            VbaIndentationStyle.FromEditorOptions(insertSpaces, indentSize));

        Assert.NotNull(plan);
        Assert.Equal(expectedBeforeCursor, plan.TextBeforeCursor);
        Assert.Equal(expectedAfterCursor, plan.TextAfterCursor);
    }

    [Fact]
    public void Planner_rejects_a_visual_only_tabs_and_spaces_ancestry_match()
    {
        const string uri = "file:///C:/work/AmbiguousWhitespace.bas";
        const string header = "        If True Then";
        const string text = "Public Sub Main()\n"
            + "\tIf True Then\n"
            + "        If True Then\n"
            + "        \n"
            + "\tEnd If\n"
            + "End Sub";
        var snapshot = CreateSnapshot(uri, version: 2, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(2, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.Null(plan);
    }

    [Fact]
    public void Planner_rejects_same_indentation_for_nested_if_ownership()
    {
        const string uri = "file:///C:/work/SameIndentNestedIf.bas";
        const string header = "    If False Then";
        const string text = "Public Sub Main()\n"
            + "    If True Then\n"
            + "    If False Then\n"
            + "    \n"
            + "    End If\n"
            + "End Sub";
        var snapshot = CreateSnapshot(uri, version: 2, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(2, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.Null(plan);
    }

    [Theory]
    [InlineData("    Else")]
    [InlineData("    ElseIf OtherReady() Then")]
    [InlineData("    End If")]
    public void Planner_rejects_an_outer_if_boundary_that_skips_an_intervening_if(
        string outerBoundary)
    {
        const string uri = "file:///C:/work/SkippedIfAncestor.bas";
        const string header = "            If CandidateReady() Then";
        var text = "Public Sub Main()\n"
            + "    If OuterReady() Then\n"
            + "        If MiddleReady() Then\n"
            + $"{header}\n"
            + "            \n"
            + $"{outerBoundary}\n"
            + "    End If\n"
            + "End Sub";
        var snapshot = CreateSnapshot(uri, version: 2, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(3, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.Null(plan);
    }

    [Fact]
    public void Planner_fails_closed_for_an_if_in_a_conditional_compilation_document()
    {
        const string uri = "file:///C:/work/ConditionalIf.bas";
        const string header = "    If True Then";
        const string text = "#If VBA7 Then\n"
            + "Public Sub Main()\n"
            + "    If True Then\n"
            + "    \n"
            + "End Sub\n"
            + "#End If";
        var snapshot = CreateSnapshot(uri, version: 2, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(2, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.Null(plan);
    }

    [Theory]
    [InlineData("    If 1.5% Then")]
    [InlineData("    If &H100000000& Then")]
    [InlineData("    If 32768% Then")]
    [InlineData("    If +1 Then")]
    [InlineData("    If TypeOf target Is Object.Member Then")]
    [InlineData("    If String(1) Then")]
    [InlineData("    If Date(1) Then")]
    [InlineData("    If text$.Length Then")]
    [InlineData("    If TypeOf count% Is Widget Then")]
    public void Planner_rejects_invalid_executable_if_conditions(string header)
    {
        const string uri = "file:///C:/work/InvalidNumericIf.bas";
        var text = $"Public Sub Main()\n{header}\n    \nEnd Sub";
        var snapshot = CreateSnapshot(uri, version: 2, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(1, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.Null(plan);
    }

    [Theory]
    [InlineData("Public Sub Main(", "End Sub")]
    [InlineData("Public Sub Main(value As Long, value As Long)", "End Sub")]
    [InlineData("Public Function Main() As", "End Function")]
    [InlineData("Public Function Main(ByVal main As Long) As Boolean", "End Function")]
    [InlineData("Public Sub Main(Optional value As String = \"unterminated)", "End Sub")]
    [InlineData("Public Property Set Main(ByVal value As Long)", "End Property")]
    [InlineData("Public Property Let Main(, ByVal value As Long)", "End Property")]
    public void Planner_rejects_an_if_owned_by_a_malformed_callable_header(
        string callableHeader,
        string callableTerminator)
    {
        const string uri = "file:///C:/work/MalformedCallableIf.bas";
        const string header = "    If True Then";
        var text = $"{callableHeader}\n{header}\n    \n{callableTerminator}";
        var snapshot = CreateSnapshot(uri, version: 2, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(1, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.Null(plan);
    }

    [Theory]
    [InlineData("    With", "    End With")]
    [InlineData("    If Then", "    End If")]
    [InlineData("    If .Enabled Then", "    End If")]
    [InlineData("    For index = To 3", "    Next")]
    [InlineData("    Select Case", "    End Select")]
    public void Planner_rejects_an_if_inside_a_malformed_structural_ancestor(
        string ancestorHeader,
        string ancestorTerminator)
    {
        const string uri = "file:///C:/work/MalformedAncestorIf.bas";
        const string header = "        If True Then";
        var text = "Public Sub Main()\n"
            + $"{ancestorHeader}\n"
            + $"{header}\n"
            + "        \n"
            + $"{ancestorTerminator}\n"
            + "End Sub";
        var snapshot = CreateSnapshot(uri, version: 2, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(2, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.Null(plan);
    }

    [Fact]
    public void Planner_rejects_an_if_inside_an_ancestor_with_a_malformed_branch()
    {
        const string uri = "file:///C:/work/MalformedAncestorBranchIf.bas";
        const string header = "        If True Then";
        const string text = "Public Sub Main()\n"
            + "    If Ready() Then\n"
            + "    ElseIf Then\n"
            + "        If True Then\n"
            + "        \n"
            + "    End If\n"
            + "End Sub";
        var snapshot = CreateSnapshot(uri, version: 2, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(3, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.Null(plan);
    }

    [Fact]
    public void Planner_rejects_a_later_malformed_ancestor_branch_after_a_proven_boundary()
    {
        const string uri = "file:///C:/work/LaterMalformedAncestorBranchIf.bas";
        const string header = "        If True Then";
        const string text = "Public Sub Main()\n"
            + "    If Ready() Then\n"
            + "        If True Then\n"
            + "        \n"
            + "    ElseIf OtherReady() Then\n"
            + "    ElseIf Then\n"
            + "    End If\n"
            + "End Sub";
        var snapshot = CreateSnapshot(uri, version: 2, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(2, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.Null(plan);
    }

    [Fact]
    public void Planner_rejects_a_later_malformed_barrier_inside_an_ancestor()
    {
        const string uri = "file:///C:/work/LaterMalformedAncestorBarrierIf.bas";
        const string header = "        If True Then";
        const string text = "Public Sub Main()\n"
            + "    If Ready() Then\n"
            + "        If True Then\n"
            + "        \n"
            + "    ElseIf OtherReady() Then\n"
            + "    Else\n"
            + "    Else\n"
            + "    End If\n"
            + "End Sub";
        var snapshot = CreateSnapshot(uri, version: 2, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(2, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.Null(plan);
    }

    [Fact]
    public void Planner_rejects_skipping_an_unclosed_intervening_block()
    {
        const string uri = "file:///C:/work/SkippedBoundary.bas";
        const string header = "            If True Then";
        const string text = "Public Sub Main()\n"
            + "    If True Then\n"
            + "        With target\n"
            + "            If True Then\n"
            + "            \n"
            + "    End If\n"
            + "End Sub";
        var snapshot = CreateSnapshot(uri, version: 2, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(3, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.Null(plan);
    }

    [Fact]
    public void Planner_accepts_a_leading_member_if_inside_a_strict_with_ancestor()
    {
        const string uri = "file:///C:/work/WithMemberIf.bas";
        const string header = "        If .Enabled Then";
        const string text = "Public Sub Main()\n"
            + "    With target\n"
            + "        If .Enabled Then\n"
            + "        \n"
            + "    End With\n"
            + "End Sub";
        var snapshot = CreateSnapshot(uri, version: 7, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(2, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.NotNull(plan);
        Assert.Equal("\n            ", plan.TextBeforeCursor);
        Assert.Equal("\n        End If", plan.TextAfterCursor);
    }

    [Theory]
    [InlineData("    Select Case value", "    End Select")]
    public void Planner_fails_closed_for_an_if_inside_an_unsupported_cross_kind_ancestor(
        string ancestorHeader,
        string ancestorTerminator)
    {
        const string uri = "file:///C:/work/CrossKindAncestorIf.bas";
        const string header = "        If True Then";
        var text = "Public Sub Main()\n"
            + $"{ancestorHeader}\n"
            + $"{header}\n"
            + "        \n"
            + $"{ancestorTerminator}\n"
            + "End Sub";
        var snapshot = CreateSnapshot(uri, version: 7, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(2, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.Null(plan);
    }

    [Fact]
    public void Planner_accepts_an_if_inside_a_strict_for_ancestor()
    {
        const string uri = "file:///C:/work/ForAncestorIf.bas";
        const string header = "        If True Then";
        const string text = "Public Sub Main()\n"
            + "    For index = 1 To 3\n"
            + "        If True Then\n"
            + "        \n"
            + "    Next\n"
            + "End Sub";
        var snapshot = CreateSnapshot(uri, version: 7, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(2, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.NotNull(plan);
        Assert.Equal("\n            ", plan.TextBeforeCursor);
        Assert.Equal("\n        End If", plan.TextAfterCursor);
    }

    [Fact]
    public void Planner_preserves_unrelated_downstream_errors_for_an_if_candidate()
    {
        const string uri = "file:///C:/work/IfUnrelatedErrors.bas";
        const string header = "    If Ready() Then";
        const string text = "Public Sub Main()\n"
            + "    If Ready() Then\n"
            + "    \n"
            + "End Sub\n"
            + "\n"
            + "Public Sub Broken(value As Long, value As Long)\n"
            + "    value = \"unterminated\n"
            + "End Sub";
        var snapshot = CreateSnapshot(uri, version: 5, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(1, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.NotNull(plan);
        var prospectiveDiagnostics = VbaDiagnosticPipeline.CollectDocument(
            VbaSyntaxTree.ParseModule(uri, ApplyPlan(text, VbaSourceText.From(text), plan)),
            uri);
        Assert.Contains(
            prospectiveDiagnostics.SyntaxDiagnostics,
            diagnostic => diagnostic.Code == "syntax.unterminatedStringLiteral");
        Assert.Contains(
            prospectiveDiagnostics.DocumentValidationDiagnostics,
            diagnostic => diagnostic.Code == "validation.duplicateCallableParameterName");
    }

    [Fact]
    public void Planner_rejects_an_injected_error_overlapping_an_if_header()
    {
        const string uri = "file:///C:/work/IfOverlappingError.bas";
        const string header = "    If True Then";
        const string text = "Public Sub Main()\n    If True Then\n    \nEnd Sub";
        var snapshot = CreateSnapshot(uri, version: 5, text);
        snapshot = snapshot with
        {
            Diagnostics = snapshot.Diagnostics with
            {
                SyntaxDiagnostics = snapshot.Diagnostics.SyntaxDiagnostics
                    .Append(new PublishedSyntaxDiagnostic(
                        "test.error",
                        "An error overlapping the If candidate.",
                        new VbaRange(
                            new VbaPosition(1, 4),
                            new VbaPosition(1, header.Length))))
                    .ToArray()
            }
        };

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(1, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.Null(plan);
    }

    [Fact]
    public void Planner_ignores_non_error_diagnostics_overlapping_an_if_header()
    {
        const string uri = "file:///C:/work/IfNonErrors.bas";
        const string header = "    If True Then";
        const string text = "Public Sub Main()\n    If True Then\n    \nEnd Sub";
        var snapshot = CreateSnapshot(uri, version: 5, text);
        var range = new VbaRange(new VbaPosition(1, 4), new VbaPosition(1, header.Length));
        snapshot = snapshot with
        {
            Diagnostics = snapshot.Diagnostics with
            {
                SyntaxDiagnostics = snapshot.Diagnostics.SyntaxDiagnostics
                    .Append(new PublishedSyntaxDiagnostic(
                        "test.warning",
                        "A warning overlapping the If candidate.",
                        range,
                        Severity: "warning"))
                    .ToArray(),
                DocumentValidationDiagnostics = snapshot.Diagnostics.DocumentValidationDiagnostics
                    .Append(new VbaValidationDiagnostic(
                        "test.information",
                        "Information overlapping the If candidate.",
                        range,
                        Severity: "information"))
                    .ToArray()
            }
        };

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(1, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.NotNull(plan);
    }

    [Theory]
    [InlineData("vba-language-server")]
    [InlineData("another-diagnostic-source")]
    public void Planner_rejects_an_unproven_duplicate_of_a_direct_if_recovery_diagnostic(
        string duplicateSource)
    {
        const string uri = "file:///C:/work/IfDuplicateRecovery.bas";
        const string header = "    If True Then";
        const string text = "Public Sub Main()\n    If True Then\n    \nEnd Sub";
        var snapshot = CreateSnapshot(uri, version: 5, text);
        var direct = Assert.Single(snapshot.Diagnostics.SyntaxDiagnostics, diagnostic =>
            diagnostic.Code == "syntax.missingBlockTerminator"
            && diagnostic.Message == "Block is missing 'End If'.");
        snapshot = snapshot with
        {
            Diagnostics = snapshot.Diagnostics with
            {
                SyntaxDiagnostics = snapshot.Diagnostics.SyntaxDiagnostics
                    .Append(direct with { Source = duplicateSource })
                    .ToArray()
            }
        };

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(1, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.Null(plan);
    }

    [Fact]
    public void Planner_rejects_an_injected_candidate_recovery_diagnostic_not_derived_from_the_snapshot_tree()
    {
        const string uri = "file:///C:/work/InjectedCandidateRecovery.bas";
        const string header = "        If True Then";
        const string text = "Public Sub Main()\n"
            + "    If True Then\n"
            + "        If True Then\n"
            + "        \n"
            + "    End If\n"
            + "End Sub";
        var snapshot = CreateSnapshot(uri, version: 5, text);
        snapshot = snapshot with
        {
            Diagnostics = snapshot.Diagnostics with
            {
                SyntaxDiagnostics = snapshot.Diagnostics.SyntaxDiagnostics
                    .Append(new PublishedSyntaxDiagnostic(
                        "syntax.missingBlockTerminator",
                        "Block is missing 'End If'.",
                        new VbaRange(
                            new VbaPosition(2, 0),
                            new VbaPosition(2, header.Length))))
                    .ToArray()
            }
        };

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(2, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.Null(plan);
    }

    [Theory]
    [InlineData("    For index = 1 To 3")]
    [InlineData("    For Each item In items")]
    [InlineData("    For Each item In values(0)")]
    [InlineData("    For Each item In vbCrLf.Value")]
    public void Planner_inserts_a_bare_next_for_a_complete_for_header(string header)
    {
        const string uri = "file:///C:/work/For.bas";
        var text = $"Public Sub Main()\n{header}\n    \nEnd Sub";
        var snapshot = CreateSnapshot(uri, version: 40, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(1, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 2));

        Assert.NotNull(plan);
        Assert.Equal("\n      ", plan.TextBeforeCursor);
        Assert.Equal("\n    Next", plan.TextAfterCursor);
        Assert.Equal(
            $"Public Sub Main()\n{header}\n      \n    Next\nEnd Sub",
            ApplyPlan(text, VbaSourceText.From(text), plan));
    }

    [Fact]
    public void Planner_preserves_continued_for_each_line_endings_tabs_and_first_line_indentation()
    {
        var lines = new[]
        {
            "Public Sub Main()",
            "    Dim source As Object",
            "\tFor Each item _",
            "        In source.Items   ' keep",
            "\t\t",
            "End Sub"
        };
        var text = string.Join("\r\n", lines);
        var snapshot = CreateSnapshot(
            "file:///C:/work/ContinuedForEach.bas",
            version: 41,
            text);

        var intermediate = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(2, lines[2].Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: false, indentSize: 8));
        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(3, lines[3].Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: false, indentSize: 8));

        Assert.Null(intermediate);
        Assert.NotNull(plan);
        Assert.Equal("\r\n\t\t", plan.TextBeforeCursor);
        Assert.Equal("\r\n\tNext", plan.TextAfterCursor);
    }

    [Theory]
    [InlineData("        Debug.Print item")]
    [InlineData("        ' existing comment")]
    [InlineData("        Rem existing comment")]
    [InlineData("    Next")]
    [InlineData("    Next index")]
    [InlineData("      Next")]
    public void Planner_rejects_body_owned_candidate_closed_and_ambiguous_for_context(
        string followingLine)
    {
        const string uri = "file:///C:/work/OwnedForContext.bas";
        const string header = "    For index = 1 To 3";
        var text = "Public Sub Main()\n"
            + $"{header}\n"
            + "    \n"
            + $"{followingLine}\n"
            + "End Sub";
        var snapshot = CreateSnapshot(uri, version: 42, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(1, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 2));

        Assert.Null(plan);
    }

    [Fact]
    public void Planner_keeps_a_shallow_next_with_the_ancestor_of_a_fresh_nested_for()
    {
        const string uri = "file:///C:/work/NestedFor.bas";
        const string header = "        For innerIndex = 1 To 3";
        const string text = "Public Sub Main()\n"
            + "    For Each outerItem In outerItems\n"
            + "        For innerIndex = 1 To 3\n"
            + "        \n"
            + "    Next\n"
            + "End Sub";
        var snapshot = CreateSnapshot(uri, version: 43, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(2, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 2));

        Assert.NotNull(plan);
        Assert.Equal("\n          ", plan.TextBeforeCursor);
        Assert.Equal("\n        Next", plan.TextAfterCursor);
        Assert.Equal(
            "Public Sub Main()\n"
                + "    For Each outerItem In outerItems\n"
                + "        For innerIndex = 1 To 3\n"
                + "          \n"
                + "        Next\n"
                + "    Next\n"
                + "End Sub",
            ApplyPlan(text, VbaSourceText.From(text), plan));
    }

    [Theory]
    [InlineData("    Else")]
    [InlineData("    ElseIf OtherReady() Then")]
    [InlineData("    End If")]
    public void Planner_preserves_a_proven_if_ancestor_boundary_for_a_for_candidate(
        string boundary)
    {
        const string uri = "file:///C:/work/ForIfBoundary.bas";
        const string header = "        For Each item In items";
        var outerCloser = boundary.Equals("    End If", StringComparison.Ordinal)
            ? string.Empty
            : "    End If\n";
        var text = "Public Sub Main()\n"
            + "    If Ready() Then\n"
            + $"{header}\n"
            + "        \n"
            + $"{boundary}\n"
            + outerCloser
            + "End Sub";
        var snapshot = CreateSnapshot(uri, version: 44, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(2, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 2));

        Assert.NotNull(plan);
        Assert.Contains("        Next\n" + boundary, ApplyPlan(text, VbaSourceText.From(text), plan));
    }

    [Fact]
    public void Planner_rejects_same_indentation_for_nested_for_ownership()
    {
        const string uri = "file:///C:/work/SameIndentNestedFor.bas";
        const string header = "    For innerIndex = 1 To 3";
        const string text = "Public Sub Main()\n"
            + "    For outerIndex = 1 To 3\n"
            + "    For innerIndex = 1 To 3\n"
            + "    \n"
            + "    Next\n"
            + "End Sub";
        var snapshot = CreateSnapshot(uri, version: 45, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(2, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 2));

        Assert.Null(plan);
    }

    [Theory]
    [InlineData("    For index = 1 To")]
    [InlineData("    For Each item In")]
    [InlineData("    For Each item In 1")]
    [InlineData("    For Each item In Empty")]
    [InlineData("    For Each item In source.Value + 1")]
    [InlineData("    For Each item In source.Count&")]
    [InlineData("    Do")]
    [InlineData("    Do While Ready()")]
    [InlineData("    Do Until Ready()")]
    [InlineData("    Loop")]
    [InlineData("    Loop While Ready()")]
    [InlineData("    Loop Until Ready()")]
    [InlineData("    While Ready()")]
    [InlineData("    Wend")]
    public void Planner_rejects_incomplete_ineligible_and_excluded_loop_headers(string header)
    {
        const string uri = "file:///C:/work/ExcludedLoops.bas";
        var text = $"Public Sub Main()\n{header}\n    \nEnd Sub";
        var snapshot = CreateSnapshot(uri, version: 46, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(1, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 2));

        Assert.Null(plan);
    }

    [Fact]
    public void Planner_rejects_an_injected_error_overlapping_a_for_header()
    {
        const string uri = "file:///C:/work/ForOverlappingError.bas";
        const string header = "    For index = 1 To 3";
        const string text = "Public Sub Main()\n    For index = 1 To 3\n    \nEnd Sub";
        var snapshot = CreateSnapshot(uri, version: 47, text);
        snapshot = snapshot with
        {
            Diagnostics = snapshot.Diagnostics with
            {
                SyntaxDiagnostics = snapshot.Diagnostics.SyntaxDiagnostics
                    .Append(new PublishedSyntaxDiagnostic(
                        "test.error",
                        "An error overlapping the For candidate.",
                        new VbaRange(
                            new VbaPosition(1, 4),
                            new VbaPosition(1, header.Length))))
                    .ToArray()
            }
        };

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(1, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 2));

        Assert.Null(plan);
    }

    [Fact]
    public void Planner_inserts_before_a_same_level_sub_and_preserves_existing_blank_lines()
    {
        const string uri = "file:///C:/work/Boundary.bas";
        const string header = "Public Sub First()";
        const string text = "Public Sub First()\n    \n\nPublic Sub Second()\nEnd Sub";
        var snapshot = CreateSnapshot(uri, version: 5, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(0, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.Contains(
            snapshot.Diagnostics.SyntaxDiagnostics,
            diagnostic => diagnostic.Code == "syntax.missingBlockTerminator"
                && diagnostic.Message == "Block is missing 'End Sub'.");
        Assert.NotNull(plan);
        Assert.Equal("\n    ", plan.TextBeforeCursor);
        Assert.Equal("\nEnd Sub", plan.TextAfterCursor);
        Assert.Equal(
            "Public Sub First()\n    \nEnd Sub\n\nPublic Sub Second()\nEnd Sub",
            ApplyPlan(text, VbaSourceText.From(text), plan));
    }

    [Theory]
    [InlineData(
        "Public Sub First()\n    \n",
        "Public Sub First()\n    \nEnd Sub\n")]
    [InlineData(
        "Public Sub First()\r\n    \r\n\r\nPublic Sub Second()\r\nEnd Sub",
        "Public Sub First()\r\n    \r\nEnd Sub\r\n\r\nPublic Sub Second()\r\nEnd Sub")]
    [InlineData(
        "Public Sub First()\n    \n\nPublic Sub Second( _\n    )\nEnd Sub",
        "Public Sub First()\n    \nEnd Sub\n\nPublic Sub Second( _\n    )\nEnd Sub")]
    [InlineData(
        "Public Sub First()\n    \n\nPublic Sub Second() ' comment ending in _\nEnd Sub",
        "Public Sub First()\n    \nEnd Sub\n\nPublic Sub Second() ' comment ending in _\nEnd Sub")]
    [InlineData(
        "Public Sub First()\n    \n\nPublic Sub Second()",
        "Public Sub First()\n    \nEnd Sub\n\nPublic Sub Second()")]
    [InlineData(
        "Public Sub First()\n    \n\nPublic Function Second() As Long\nEnd Function",
        "Public Sub First()\n    \nEnd Sub\n\nPublic Function Second() As Long\nEnd Function")]
    [InlineData(
        "Public Sub First()\n    \n\nPublic Property Get Second() As Long\nEnd Property",
        "Public Sub First()\n    \nEnd Sub\n\nPublic Property Get Second() As Long\nEnd Property")]
    public void Planner_preserves_blank_to_eof_and_complete_same_level_sub_boundaries(
        string text,
        string expectedText)
    {
        const string uri = "file:///C:/work/SafeBoundary.bas";
        const string header = "Public Sub First()";
        var snapshot = CreateSnapshot(uri, version: 5, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(0, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.NotNull(plan);
        Assert.Equal(expectedText, ApplyPlan(text, VbaSourceText.From(text), plan));
    }

    [Fact]
    public void Planner_preserves_unrelated_downstream_errors()
    {
        const string uri = "file:///C:/work/UnrelatedErrors.bas";
        const string header = "Public Sub First()";
        const string text = "Public Sub First()\n"
            + "    \n"
            + "\n"
            + "Public Sub Second()\n"
            + "End Sub\n"
            + "\n"
            + "Public Sub Third(value As Long, value As Long)\n"
            + "    value = \"unterminated\n"
            + "End Sub";
        var snapshot = CreateSnapshot(uri, version: 5, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(0, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.Contains(
            snapshot.Diagnostics.SyntaxDiagnostics,
            diagnostic => diagnostic.Code == "syntax.unterminatedStringLiteral");
        Assert.Contains(
            snapshot.Diagnostics.DocumentValidationDiagnostics,
            diagnostic => diagnostic.Code == "validation.duplicateCallableParameterName");
        Assert.NotNull(plan);
        var speculativeText = ApplyPlan(text, VbaSourceText.From(text), plan);
        var speculativeDiagnostics = VbaDiagnosticPipeline.CollectDocument(
            VbaSyntaxTree.ParseModule(uri, speculativeText),
            uri);
        Assert.DoesNotContain(
            speculativeDiagnostics.SyntaxDiagnostics,
            diagnostic => diagnostic.Code == "syntax.missingBlockTerminator");
        Assert.Contains(
            speculativeDiagnostics.SyntaxDiagnostics,
            diagnostic => diagnostic.Code == "syntax.unterminatedStringLiteral");
        Assert.Contains(
            speculativeDiagnostics.DocumentValidationDiagnostics,
            diagnostic => diagnostic.Code == "validation.duplicateCallableParameterName");
        Assert.Equal(
            CountErrors(snapshot.Diagnostics) - 1,
            CountErrors(speculativeDiagnostics));
    }

    [Fact]
    public void Planner_ignores_overlapping_warning_and_information_diagnostics()
    {
        const string uri = "file:///C:/work/NonErrors.bas";
        const string header = "Public Sub First()";
        const string text = "Public Sub First()\n    \n\nPublic Sub Second()\nEnd Sub";
        var snapshot = CreateSnapshot(uri, version: 5, text);
        var headerRange = new VbaRange(
            new VbaPosition(0, 0),
            new VbaPosition(0, header.Length));
        snapshot = snapshot with
        {
            Diagnostics = snapshot.Diagnostics with
            {
                SyntaxDiagnostics = snapshot.Diagnostics.SyntaxDiagnostics
                    .Append(new PublishedSyntaxDiagnostic(
                        "test.warning",
                        "A warning overlapping the candidate.",
                        headerRange,
                        Severity: "warning"))
                    .ToArray(),
                DocumentValidationDiagnostics = snapshot.Diagnostics.DocumentValidationDiagnostics
                    .Append(new VbaValidationDiagnostic(
                        "test.information",
                        "Information overlapping the candidate.",
                        headerRange,
                        Severity: "information"))
                    .ToArray()
            }
        };

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(0, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.NotNull(plan);
    }

    [Fact]
    public void Planner_rejects_an_injected_error_overlapping_the_header()
    {
        const string uri = "file:///C:/work/OverlappingError.bas";
        const string header = "Public Sub First()";
        const string text = "Public Sub First()\n    \n\nPublic Sub Second()\nEnd Sub";
        var snapshot = CreateSnapshot(uri, version: 5, text);
        snapshot = snapshot with
        {
            Diagnostics = snapshot.Diagnostics with
            {
                SyntaxDiagnostics = snapshot.Diagnostics.SyntaxDiagnostics
                    .Append(new PublishedSyntaxDiagnostic(
                        "test.error",
                        "An error overlapping the candidate.",
                        new VbaRange(
                            new VbaPosition(0, 0),
                            new VbaPosition(0, header.Length))))
                    .ToArray()
            }
        };

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(0, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.Null(plan);
    }

    [Theory]
    [InlineData(
        "  Public Sub Run()\n    ",
        0,
        18,
        true,
        2,
        "\n    ",
        "\n  End Sub")]
    [InlineData(
        "\tPublic Sub Run()\r\n\t",
        0,
        17,
        false,
        8,
        "\r\n\t\t",
        "\r\n\tEnd Sub")]
    [InlineData(
        "    Public Sub Run( _\r\n        )\r\n        ",
        1,
        9,
        true,
        2,
        "\r\n      ",
        "\r\n    End Sub")]
    public void Planner_preserves_line_endings_and_resolved_first_line_indentation(
        string text,
        int line,
        int character,
        bool insertSpaces,
        int indentSize,
        string expectedBeforeCursor,
        string expectedAfterCursor)
    {
        const string uri = "file:///C:/work/Planner.bas";
        var snapshot = CreateSnapshot(uri, version: 5, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(line, character),
            VbaIndentationStyle.FromEditorOptions(insertSpaces, indentSize));

        Assert.NotNull(plan);
        Assert.Equal(5, plan.DocumentVersion);
        Assert.Equal(new BlockSkeletonInsertionPosition(line, character), plan.Position);
        Assert.Equal(expectedBeforeCursor, plan.TextBeforeCursor);
        Assert.Equal(expectedAfterCursor, plan.TextAfterCursor);
    }

    [Theory]
    [InlineData(
        "file:///C:/work/Module1.bas",
        "Public Function Build() As String",
        "\nEnd Function")]
    [InlineData(
        "file:///C:/work/Worker.cls",
        "Public Property Get Value() As Long",
        "\nEnd Property")]
    [InlineData(
        "file:///C:/work/Module1.bas",
        "Public Property Get Value() As Long",
        "\nEnd Property")]
    [InlineData(
        "file:///C:/work/Worker.cls",
        "Public Property Let Value(ByVal assignedValue As Long)",
        "\nEnd Property")]
    [InlineData(
        "file:///C:/work/Worker.cls",
        "Public Property Set Value(ByVal assignedValue As Object)",
        "\nEnd Property")]
    public void Planner_inserts_eligible_function_and_property_skeletons_at_eof(
        string uri,
        string header,
        string expectedAfterCursor)
    {
        var snapshot = CreateSnapshot(uri, version: 30, text: $"{header}\n    ");

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(0, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 2));

        Assert.NotNull(plan);
        Assert.Equal("\n  ", plan.TextBeforeCursor);
        Assert.Equal(expectedAfterCursor, plan.TextAfterCursor);
    }

    [Fact]
    public void Planner_preserves_tabs_crlf_and_first_line_indentation_for_a_continued_function()
    {
        string[] lines =
        [
            "\tPrivate Function Build( _",
            "        ByVal value As Long) As String   ' keep",
            "\t\t"
        ];
        var text = string.Join("\r\n", lines);
        var snapshot = CreateSnapshot(
            "file:///C:/work/Module1.bas",
            version: 31,
            text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(1, lines[1].Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: false, indentSize: 8));

        Assert.NotNull(plan);
        Assert.Equal("\r\n\t\t", plan.TextBeforeCursor);
        Assert.Equal("\r\n\tEnd Function", plan.TextAfterCursor);
    }

    [Theory]
    [InlineData(
        "Public Function First() As Long",
        "Public Property Get Second() As Long",
        "End Property",
        "\nEnd Function")]
    [InlineData(
        "Public Property Get First() As Long",
        "Public Property Let Second(ByVal assignedValue As Long)",
        "End Property",
        "\nEnd Property")]
    [InlineData(
        "Public Property Let First(ByVal assignedValue As Long)",
        "Public Property Set Second(ByVal assignedValue As Object)",
        "End Property",
        "\nEnd Property")]
    [InlineData(
        "Public Property Set First(ByVal assignedValue As Object)",
        "Public Sub Second()",
        "End Sub",
        "\nEnd Property")]
    public void Planner_preserves_complete_same_level_callable_boundaries(
        string header,
        string boundaryHeader,
        string boundaryTerminator,
        string expectedAfterCursor)
    {
        var text = $"{header}\n    \n\n{boundaryHeader}\n{boundaryTerminator}";
        var snapshot = CreateSnapshot(
            "file:///C:/work/Worker.cls",
            version: 32,
            text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(0, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.NotNull(plan);
        Assert.Equal(expectedAfterCursor, plan.TextAfterCursor);
    }

    [Theory]
    [InlineData(
        "Public Sub Existing()\nEnd Sub\nPublic Function Build() As Long\n    ",
        2,
        "Public Function Build() As Long",
        "\nEnd Function")]
    [InlineData(
        "Public Property Get Existing() As Long\nEnd Property\nPublic Property Let Existing(ByVal assignedValue As Long)\n    ",
        2,
        "Public Property Let Existing(ByVal assignedValue As Long)",
        "\nEnd Property")]
    public void Planner_allows_callable_skeletons_after_a_finished_callable(
        string text,
        int line,
        string header,
        string expectedAfterCursor)
    {
        var snapshot = CreateSnapshot(
            "file:///C:/work/Worker.cls",
            version: 36,
            text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(line, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.NotNull(plan);
        Assert.Equal(expectedAfterCursor, plan.TextAfterCursor);
    }

    [Theory]
    [InlineData(
        "file:///C:/work/Worker.cls",
        "Public Event Changed(ByVal value As Long)")]
    [InlineData(
        "file:///C:/work/Module1.bas",
        "Public Declare Sub Run Lib \"library\" ()")]
    [InlineData(
        "file:///C:/work/Module1.bas",
        "Private Declare PtrSafe Function Read Lib \"library\" () As Long")]
    [InlineData(
        "file:///C:/work/Module1.bas",
        "Friend Function Build() As String")]
    [InlineData(
        "file:///C:/work/Worker.cls",
        "Global Function Build() As String")]
    [InlineData(
        "file:///C:/work/Worker.cls",
        "Public Property Let Value()")]
    [InlineData(
        "file:///C:/work/Worker.cls",
        "Public Property Set Value(ByVal assignedValue As Long)")]
    [InlineData(
        "file:///C:/work/Module1.bas",
        "Public Function Build() As Long _")]
    [InlineData(
        "file:///C:/work/Worker.cls",
        "Public Property Get Value() As Long _")]
    public void Planner_rejects_excluded_illegal_and_incomplete_callable_headers(
        string uri,
        string header)
    {
        var snapshot = CreateSnapshot(uri, version: 33, text: $"{header}\n    ");

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(0, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.Null(plan);
    }

    [Theory]
    [InlineData(
        "file:///C:/work/Module1.bas",
        "Public Function Build() As String\n    \n    Debug.Print 1")]
    [InlineData(
        "file:///C:/work/Worker.cls",
        "Public Property Get Value() As Long\n    \n    ' existing comment")]
    [InlineData(
        "file:///C:/work/Worker.cls",
        "Public Property Let Value(ByVal assignedValue As Long)\n    \nEnd Property")]
    [InlineData(
        "file:///C:/work/Worker.cls",
        "Public Property Set Value(ByVal assignedValue As Object)\n    \n\nPublic Event Changed()")]
    public void Planner_rejects_owned_or_unproven_callable_post_header_context(
        string uri,
        string text)
    {
        var header = text.Split('\n')[0];
        var snapshot = CreateSnapshot(uri, version: 34, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(0, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.Null(plan);
    }

    [Theory]
    [InlineData(
        "file:///C:/work/FunctionDuplicateRecovery.bas",
        "Public Function First() As Long",
        "Public Sub Second()",
        "End Sub",
        "End Function")]
    [InlineData(
        "file:///C:/work/PropertyDuplicateRecovery.cls",
        "Public Property Get First() As Long",
        "Public Function Second() As Long",
        "End Function",
        "End Property")]
    public void Planner_rejects_an_unproven_duplicate_of_a_direct_callable_recovery_diagnostic(
        string uri,
        string header,
        string boundaryHeader,
        string boundaryTerminator,
        string expectedTerminator)
    {
        var text = $"{header}\n    \n\n{boundaryHeader}\n{boundaryTerminator}";
        var snapshot = CreateSnapshot(uri, version: 35, text);
        var direct = Assert.Single(snapshot.Diagnostics.SyntaxDiagnostics, diagnostic =>
            diagnostic.Code == "syntax.missingBlockTerminator"
            && diagnostic.Message == $"Block is missing '{expectedTerminator}'.");
        snapshot = snapshot with
        {
            Diagnostics = snapshot.Diagnostics with
            {
                SyntaxDiagnostics = snapshot.Diagnostics.SyntaxDiagnostics
                    .Append(direct)
                    .ToArray()
            }
        };

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(0, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.Null(plan);
    }

    [Fact]
    public void Planner_rejects_a_validation_error_overlapping_the_header()
    {
        const string uri = "file:///C:/work/Invalid.bas";
        const string header = "Public Sub Run(value As Long, value As Long)";
        var snapshot = CreateSnapshot(uri, version: 5, text: $"{header}\n    ");

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(0, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.Contains(
            snapshot.Diagnostics.DocumentValidationDiagnostics,
            diagnostic => diagnostic.Code == "validation.duplicateCallableParameterName");
        Assert.Null(plan);
    }

    [Fact]
    public void Planner_rejects_a_snapshot_whose_module_kind_does_not_match_its_tree()
    {
        const string uri = "file:///C:/work/Inconsistent.bas";
        const string header = "Public Sub Run()";
        var snapshot = CreateSnapshot(uri, version: 5, text: $"{header}\n    ") with
        {
            ModuleKind = VbaModuleKind.ClassModule
        };

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(0, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.Null(plan);
    }

    [Theory]
    [InlineData("file:///C:/work/GlobalModule.bas", true)]
    [InlineData("file:///C:/work/GlobalClass.cls", false)]
    public void Planner_allows_global_sub_only_in_a_standard_module(
        string uri,
        bool expectedPlan)
    {
        const string header = "Global Sub Run()";
        var snapshot = CreateSnapshot(uri, version: 5, text: $"{header}\n    ");

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(0, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.Equal(expectedPlan, plan is not null);
    }

    [Theory]
    [InlineData("Public Sub Run()")]
    [InlineData("Public Sub Run()\n    Debug.Print 1")]
    [InlineData("Public Sub Run()\n    ' existing comment")]
    [InlineData("Public Sub Run()\n    Rem existing comment")]
    [InlineData("Public Sub Run()\nEnd Sub")]
    [InlineData("Public Sub Run()\n    \n\n    Public Sub Nested()\n    End Sub")]
    [InlineData("Public Sub Run()\n    \n\nPublic Sub Broken(")]
    [InlineData("Public Sub Run()\n    \n\n#Const Enabled = True")]
    [InlineData("Public Sub Run()\n    \n\nPublic value As Long")]
    [InlineData("  Public Sub Run()\n      \n\n\t\tPublic Sub DifferentWhitespace()\n\t\tEnd Sub")]
    public void Planner_rejects_body_owned_or_unproven_post_header_context(string text)
    {
        const string uri = "file:///C:/work/NotEof.bas";
        const string header = "Public Sub Run()";
        var snapshot = CreateSnapshot(uri, version: 5, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(0, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.Null(plan);
    }

    [Theory]
    [InlineData("file:///C:/work/State.bas", "Public Enum State", "\n  ", "\nEnd Enum")]
    [InlineData("file:///C:/work/Record.bas", "  Private Type Record", "\n    ", "\n  End Type")]
    [InlineData("file:///C:/work/State.cls", "Public Enum State", "\n  ", "\nEnd Enum")]
    [InlineData("file:///C:/work/State.cls", "Private Enum State", "\n  ", "\nEnd Enum")]
    [InlineData("file:///C:/work/Record.cls", "Private Type Record", "\n  ", "\nEnd Type")]
    public void Planner_inserts_eligible_enum_and_type_skeletons_at_eof(
        string uri,
        string header,
        string expectedBeforeCursor,
        string expectedAfterCursor)
    {
        var snapshot = CreateSnapshot(uri, version: 17, text: $"{header}\n    ");

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(0, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 2));

        Assert.NotNull(plan);
        Assert.Equal(expectedBeforeCursor, plan.TextBeforeCursor);
        Assert.Equal(expectedAfterCursor, plan.TextAfterCursor);
    }

    [Fact]
    public void Planner_uses_first_line_tabs_and_crlf_for_a_continued_private_type()
    {
        string[] lines =
        [
            "\tPrivate _",
            "        Type _",
            "    Record   ' keep",
            "\t\t"
        ];
        var text = string.Join("\r\n", lines);
        var snapshot = CreateSnapshot("file:///C:/work/Record.cls", version: 18, text);

        var intermediate = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(1, lines[1].Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: false, indentSize: 8));
        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(2, lines[2].Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: false, indentSize: 8));

        Assert.Null(intermediate);
        Assert.NotNull(plan);
        Assert.Equal("\r\n\t\t", plan.TextBeforeCursor);
        Assert.Equal("\r\n\tEnd Type", plan.TextAfterCursor);
    }

    [Fact]
    public void Planner_preserves_blank_lines_and_a_same_level_module_declaration_boundary()
    {
        const string uri = "file:///C:/work/Declarations.bas";
        const string header = "Public Enum State";
        const string text = "Public Enum State\n    \n\nPrivate Type Record\nEnd Type";
        var snapshot = CreateSnapshot(uri, version: 19, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(0, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.NotNull(plan);
        Assert.Equal(
            "Public Enum State\n    \nEnd Enum\n\nPrivate Type Record\nEnd Type",
            ApplyPlan(text, VbaSourceText.From(text), plan));
    }

    [Fact]
    public void Planner_preserves_a_same_level_sub_boundary_after_a_type_declaration()
    {
        const string uri = "file:///C:/work/TypeBeforeSub.bas";
        const string header = "Private Type Record";
        const string text = "Private Type Record\n    \n\nPublic Sub Run()\nEnd Sub";
        var snapshot = CreateSnapshot(uri, version: 20, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(0, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.NotNull(plan);
        Assert.Equal(
            "Private Type Record\n    \nEnd Type\n\nPublic Sub Run()\nEnd Sub",
            ApplyPlan(text, VbaSourceText.From(text), plan));
    }

    [Fact]
    public void Planner_preserves_existing_blank_lines_after_a_declaration_skeleton_at_eof()
    {
        const string uri = "file:///C:/work/BlankEnum.bas";
        const string header = "Public Enum State";
        const string text = "Public Enum State\n    \n\n";
        var snapshot = CreateSnapshot(uri, version: 25, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(0, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.NotNull(plan);
        Assert.Equal(
            "Public Enum State\n    \nEnd Enum\n\n",
            ApplyPlan(text, VbaSourceText.From(text), plan));
    }

    [Fact]
    public void Planner_rejects_a_module_declaration_boundary_with_different_indentation()
    {
        const string uri = "file:///C:/work/MismatchedDeclarationBoundary.bas";
        const string header = "  Public Enum State";
        const string text = "  Public Enum State\n    \n\nPrivate Type Record\nEnd Type";
        var snapshot = CreateSnapshot(uri, version: 26, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(0, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.Null(plan);
    }

    [Theory]
    [InlineData("Public Sub Main()\nEnd Sub\nPublic Enum State\n    ", 2, "Public Enum State")]
    [InlineData("Public Property Get Value() As Long\nEnd Property\nPrivate Type Record\n    ", 2, "Private Type Record")]
    public void Planner_rejects_module_declarations_after_a_callable_body(
        string text,
        int line,
        string header)
    {
        var snapshot = CreateSnapshot(
            "file:///C:/work/DeclarationAfterCallable.bas",
            version: 27,
            text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(line, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.Null(plan);
    }

    [Theory]
    [InlineData("file:///C:/work/Record.cls", "Public Type Record")]
    [InlineData("file:///C:/work/Record.cls", "Type Record")]
    [InlineData("file:///C:/work/Friend.bas", "Friend Enum State")]
    [InlineData("file:///C:/work/GlobalEnum.bas", "Global Enum State")]
    [InlineData("file:///C:/work/Global.bas", "Global Type Record")]
    public void Planner_rejects_enum_and_type_headers_illegal_for_the_module_kind(
        string uri,
        string header)
    {
        var snapshot = CreateSnapshot(uri, version: 21, text: $"{header}\n    ");

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(0, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.Null(plan);
    }

    [Theory]
    [InlineData("Public Enum State", true)]
    [InlineData("Private Enum State", true)]
    [InlineData("Private Type Record", true)]
    [InlineData("Type Record", false)]
    [InlineData("Public Type Record", false)]
    public void Planner_obeys_form_module_enum_and_type_visibility(
        string header,
        bool expectedPlan)
    {
        var lines = new[]
        {
            "VERSION 5.00",
            "Begin VB.Form Dialog",
            "End",
            "Attribute VB_Name = \"Dialog\"",
            header,
            "    "
        };
        var snapshot = CreateSnapshot(
            "file:///C:/work/Dialog.frm",
            version: 24,
            string.Join('\n', lines));

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(4, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.Equal(expectedPlan, plan is not null);
    }

    [Theory]
    [InlineData("Public Enum State\n    \n    Ready")]
    [InlineData("Public Enum State\n    \n    ' existing comment")]
    [InlineData("Public Enum State\n    \nEnd Enum")]
    [InlineData("Private Type Record\n    \n    Value As Long")]
    [InlineData("Private Type Record\n    \n    Rem existing comment")]
    [InlineData("Private Type Record\n    \nEnd Type")]
    [InlineData("Public Enum State\n    \n\nPublic value As Long")]
    public void Planner_rejects_owned_or_unproven_enum_and_type_post_header_context(string text)
    {
        const string uri = "file:///C:/work/OwnedDeclarationContext.bas";
        var header = text.Split('\n')[0];
        var snapshot = CreateSnapshot(uri, version: 22, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(0, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.Null(plan);
    }

    [Fact]
    public void Planner_rejects_an_error_overlapping_an_enum_header()
    {
        const string uri = "file:///C:/work/InvalidEnum.bas";
        const string header = "Public Enum State";
        var snapshot = CreateSnapshot(uri, version: 23, text: $"{header}\n    ");
        snapshot = snapshot with
        {
            Diagnostics = snapshot.Diagnostics with
            {
                SyntaxDiagnostics = snapshot.Diagnostics.SyntaxDiagnostics
                    .Append(new PublishedSyntaxDiagnostic(
                        "test.error",
                        "An error overlapping the Enum candidate.",
                        new VbaRange(
                            new VbaPosition(0, 0),
                            new VbaPosition(0, header.Length))))
                    .ToArray()
            }
        };

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(0, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.Null(plan);
    }

    private static VbaVersionedDocumentSnapshot CreateSnapshot(
        string uri,
        int version,
        string text)
    {
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
        workspace.OpenDocument(uri, version, text);
        return Assert.IsType<VbaVersionedDocumentSnapshot>(
            workspace.GetDocumentSnapshot(uri, version));
    }

    private static string ApplyPlan(
        string text,
        VbaSourceText source,
        BlockSkeletonInsertionPlan plan)
    {
        var line = source.Lines[plan.Position.Line];
        var startOffset = line.StartOffset + plan.Position.Character;
        var endOffset = startOffset;
        if (text.AsSpan(endOffset).StartsWith("\r\n", StringComparison.Ordinal))
        {
            endOffset += 2;
        }
        else
        {
            endOffset++;
        }

        while (endOffset < text.Length && text[endOffset] is ' ' or '\t')
        {
            endOffset++;
        }

        return text[..startOffset]
            + plan.TextBeforeCursor
            + plan.TextAfterCursor
            + text[endOffset..];
    }

    private static int CountErrors(VbaDiagnosticPipelineResult diagnostics)
        => diagnostics.SyntaxDiagnostics.Count(diagnostic =>
                diagnostic.Severity.Equals("error", StringComparison.OrdinalIgnoreCase))
            + diagnostics.DocumentValidationDiagnostics.Count(diagnostic =>
                diagnostic.Severity.Equals("error", StringComparison.OrdinalIgnoreCase));
}
