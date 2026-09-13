using Xunit;

namespace VbaTools.Syntax.Tests;

public sealed class VbaIndentationDetectionTests
{
    [Theory]
    [InlineData("Worker.bas", "", 2)]
    [InlineData("Worker.cls", "VERSION 1.0 CLASS\nBEGIN\n  MultiUse = -1\nEND\n", 4)]
    [InlineData("Worker.cls", "VERSION 1.0 CLASS\nBEGIN\n  MultiUse = -1\nEND\n", 2)]
    [InlineData("Worker.cls", "VERSION 1.0 CLASS\nBEGIN\n  MultiUse = -1\n", 4)]
    [InlineData("Worker.frm", "VERSION 5.00\nBegin VB.UserForm Worker\n  Caption = \"Dialog\"\n  Begin VB.CommandButton Button\n    Caption = \"Run\"\n  End\nEnd\n", 3)]
    public void Export_records_are_excluded_and_nested_code_supplies_one_indentation_unit(
        string fileName, string header, int width)
    {
        var unit = new string(' ', width);
        var source = header + string.Join('\n', [
            " Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "Public Sub Run()",
            " Attribute Run.VB_Description = \"Run\"",
            unit + "If True Then",
            unit + unit + "Debug.Print \"BEGIN END Attribute\"",
            unit + "End If",
            "End Sub"
        ]);
        var tree = VbaSyntaxTree.ParseModule(fileName, source);

        var detected = VbaIndentationDetection.Detect(
            tree, VbaIndentationStyle.FromEditorOptions(false, 8));

        Assert.Equal(VbaIndentationStyle.FromEditorOptions(true, width), detected);
        Assert.Equal(source, tree.Text);
    }

    [Fact]
    public void Labels_and_conditional_directives_do_not_override_code_indentation()
    {
        var tree = VbaSyntaxTree.ParseModule("Worker.bas", string.Join('\n', [
            "Public Sub Run()",
            "  Retry:",
            "  #If Win64 Then",
            "    Debug.Print 1",
            "  #Else",
            "    Debug.Print 2",
            "  #End If",
            "End Sub"
        ]));

        var detected = VbaIndentationDetection.Detect(
            tree, VbaIndentationStyle.FromEditorOptions(true, 8));

        Assert.Equal(VbaIndentationStyle.FromEditorOptions(true, 4), detected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("VERSION 1.0 CLASS\nBEGIN\n  MultiUse = -1\nEND")]
    [InlineData("Option Explicit\nPublic Sub Run()\nDebug.Print 1\nEnd Sub")]
    [InlineData("Public Sub Run()\n    Debug.Print 1")]
    [InlineData("Public Sub Run()\n \tDebug.Print 1\nEnd Sub")]
    [InlineData("Public Sub Run()\n\t Debug.Print 1\nEnd Sub")]
    [InlineData("Public Sub Run()\n  If True Then\n   Debug.Print 1\n  End If\nEnd Sub")]
    public void Insufficient_or_ambiguous_code_evidence_preserves_configured_defaults(string source)
    {
        var tree = VbaSyntaxTree.ParseModule("Worker.cls", source);
        var configured = VbaIndentationStyle.FromEditorOptions(true, 6);

        var detected = VbaIndentationDetection.Detect(tree, configured);

        Assert.Equal(configured, detected);
    }

    [Fact]
    public void Comments_blanks_and_continuation_alignment_do_not_supply_indentation_evidence()
    {
        var tree = VbaSyntaxTree.ParseModule("Worker.bas", string.Join('\n', [
            "Public Sub Run()",
            "  ' visually aligned prose",
            "  Rem more prose",
            "  ",
            "    Debug.Print 1 + _",
            "                2",
            "End Sub"
        ]));

        var detected = VbaIndentationDetection.Detect(
            tree, VbaIndentationStyle.FromEditorOptions(true, 8));

        Assert.Equal(VbaIndentationStyle.FromEditorOptions(true, 4), detected);
    }

    [Fact]
    public void Tabs_in_code_select_tabs_without_guessing_their_visual_width()
    {
        var tree = VbaSyntaxTree.ParseModule("Worker.cls", string.Join('\n', [
            "VERSION 1.0 CLASS",
            "BEGIN",
            "  MultiUse = -1  'True",
            "END",
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "\tIf True Then",
            "\t\tDebug.Print 1",
            "\tEnd If",
            "End Sub"
        ]));

        var detected = VbaIndentationDetection.Detect(
            tree, VbaIndentationStyle.FromEditorOptions(true, 6));

        Assert.Equal(VbaIndentationStyle.FromEditorOptions(false, 6), detected);
    }

    [Fact]
    public void Conflicting_code_widths_fall_back_to_the_configured_style()
    {
        var tree = VbaSyntaxTree.ParseModule("Worker.bas", string.Join('\n', [
            "Public Sub Run()",
            "  Debug.Print 1",
            "    Debug.Print 2",
            "End Sub"
        ]));
        var configured = VbaIndentationStyle.FromEditorOptions(false, 8);

        var detected = VbaIndentationDetection.Detect(tree, configured);

        Assert.Equal(configured, detected);
    }
}
