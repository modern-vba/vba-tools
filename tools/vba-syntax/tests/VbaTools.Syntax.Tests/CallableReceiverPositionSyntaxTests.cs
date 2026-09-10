using VbaTools.Syntax;
using Xunit;

namespace VbaTools.Syntax.Tests;

public sealed class CallableReceiverPositionSyntaxTests
{
    [Fact]
    public void Terminal_member_retains_the_explicit_receiver_across_a_balanced_call()
    {
        const string source = "Public Sub Run()\n    item.Child(\"A1\").Activate\nEnd Sub";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        var position = tree.GetPositionSyntax(1, "    item.Child(\"A1\").Activ".Length);

        var access = Assert.IsType<VbaMemberAccessSyntax>(position.MemberAccess);
        Assert.Equal(["item", "Child", "Activate"], access.Segments.Select(segment => segment.Name));
        Assert.Equal(2, access.TargetSegmentIndex);
        Assert.False(access.IsLeadingDot);
        Assert.False(access.IsIncomplete);
        Assert.Equal(
            [(1, 4, 21, 1, 8, 25), (1, 9, 26, 1, 14, 31), (1, 21, 38, 1, 29, 46)],
            access.Segments.Select(segment => (
                segment.Range.Start.Line,
                segment.Range.Start.Character,
                segment.Range.Start.Offset,
                segment.Range.End.Line,
                segment.Range.End.Character,
                segment.Range.End.Offset)));
        Assert.Equal(new VbaSyntaxPosition(1, 4, 21), access.Range.Start);
        Assert.Equal(new VbaSyntaxPosition(1, 29, 46), access.Range.End);
    }

    [Fact]
    public void Trailing_dot_retains_the_called_receiver_without_argument_identifiers()
    {
        const string line = "    item.Child(key).";
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            $"Public Sub Run()\n{line}\nEnd Sub");

        var position = tree.GetPositionSyntax(1, line.Length);

        var access = Assert.IsType<VbaMemberAccessSyntax>(position.MemberAccess);
        Assert.Equal(["item", "Child"], access.Segments.Select(segment => segment.Name));
        Assert.Equal(2, access.TargetSegmentIndex);
        Assert.Null(access.Target);
        Assert.False(access.IsLeadingDot);
        Assert.True(access.IsIncomplete);
        Assert.Equal(4, access.Range.Start.Character);
        Assert.Equal(line.Length, access.Range.End.Character);
    }

    [Theory]
    [InlineData("item.Child(key).Activate", false)]
    [InlineData(".Child(key).Activate", true)]
    [InlineData("Call item.Child(key).Activate", false)]
    [InlineData("Call .Child(key).Activate", true)]
    public void A_with_scope_supplies_only_a_genuinely_implicit_receiver(string expression, bool leadingDot)
    {
        var line = $"        {expression}";
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            $"Public Sub Run()\n    With other\n{line}\n    End With\nEnd Sub");

        var position = tree.GetPositionSyntax(2, line.Length - 1);

        Assert.Single(position.EnclosingWithScopes);
        var access = Assert.IsType<VbaMemberAccessSyntax>(position.MemberAccess);
        Assert.Equal(leadingDot, access.IsLeadingDot);
        Assert.Equal(
            leadingDot ? ["Child", "Activate"] : new[] { "item", "Child", "Activate" },
            access.Segments.Select(segment => segment.Name));
        Assert.Equal(access.Segments.Count - 1, access.TargetSegmentIndex);
        Assert.Equal("Activate", access.Target?.Name);
    }

    [Fact]
    public void An_identifier_inside_receiver_arguments_retains_its_own_position_context()
    {
        const string line = "    item.Child(key).Activate";
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            $"Public Sub Run()\n{line}\nEnd Sub");

        var position = tree.GetPositionSyntax(1, line.IndexOf("key", StringComparison.Ordinal) + 1);

        Assert.Equal("key", position.Identifier?.Name);
        Assert.Null(position.MemberAccess);
        var call = Assert.IsType<VbaCallSiteSyntax>(position.CallSite);
        Assert.Equal(["item", "Child"], call.Callee.Segments.Select(segment => segment.Name));
        Assert.Equal(0, call.ActiveArgumentIndex);
        Assert.Equal("Child", call.Callee.Target?.Name);
    }

    [Fact]
    public void A_terminal_call_site_projects_only_receiver_members_across_continued_arguments()
    {
        const string terminalLine = "        Other:=\"A.B\").Activate(";
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            $"Public Sub Run()\n    Call item.Child(Key:=Normalize(other.Name), _\n{terminalLine}\nEnd Sub");

        var position = tree.GetPositionSyntax(2, terminalLine.Length);

        var call = Assert.IsType<VbaCallSiteSyntax>(position.CallSite);
        Assert.Equal(VbaCallSyntaxForm.Parenthesized, call.Form);
        Assert.Equal(["item", "Child", "Activate"], call.Callee.Segments.Select(segment => segment.Name));
        Assert.False(call.Callee.IsLeadingDot);
        Assert.Equal(2, call.Callee.TargetSegmentIndex);
        Assert.Equal(0, call.ActiveArgumentIndex);
        Assert.Equal(
            [(1, 9, 13), (1, 14, 19), (2, 22, 30)],
            call.Callee.Segments.Select(segment => (
                segment.Range.Start.Line,
                segment.Range.Start.Character,
                segment.Range.End.Character)));
    }

    [Fact]
    public void An_unprojected_explicit_receiver_does_not_become_a_with_receiver()
    {
        const string line = "        Values()(0).Activate";
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            $"Public Sub Run()\n    With other\n{line}\n    End With\nEnd Sub");

        var position = tree.GetPositionSyntax(2, line.Length - 1);

        Assert.Single(position.EnclosingWithScopes);
        Assert.Equal("Activate", position.Identifier?.Name);
        Assert.Null(position.MemberAccess);
    }
}
