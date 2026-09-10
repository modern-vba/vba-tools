using Xunit;

namespace VbaTools.Syntax.Tests;

public sealed class ReceiverBoundaryCallSyntaxTests
{
    [Fact]
    public void A_continued_receiver_keeps_the_terminal_statement_and_its_arguments()
    {
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Caller.bas", """
            Public Sub Run()
                item.Child("A1") _
                    .Activate 1&
            End Sub
            """);

        var terminal = Assert.Single(tree.Module.ArgumentLists,
            candidate => candidate.Form == VbaCallSyntaxForm.Statement);

        Assert.Equal("item.Child(\"A1\").Activate", terminal.Callee);
        Assert.Equal("1&", Assert.Single(terminal.Arguments).ValueText);
        Assert.Equal(1, terminal.CalleeRange!.Start.Line);
        Assert.Equal(4, terminal.CalleeRange.Start.Character);
        Assert.Equal(2, terminal.CalleeRange.End.Line);
        Assert.Equal(17, terminal.CalleeRange.End.Character);
        var receiver = Assert.Single(tree.Module.ArgumentLists,
            candidate => candidate.Callee == "item.Child");
        Assert.Equal("\"A1\"", Assert.Single(receiver.Arguments).ValueText);
        Assert.DoesNotContain(tree.Module.ArgumentLists, candidate =>
            candidate.Form == VbaCallSyntaxForm.BareValueRead
            && candidate.CalleeRange?.End == terminal.CalleeRange.End);
    }

    [Fact]
    public void Whitespace_separated_parentheses_remain_a_statement_argument()
    {
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Caller.bas", """
            Public Sub Run()
                Work (item).Activate
            End Sub
            """);

        var statement = Assert.Single(tree.Module.ArgumentLists,
            candidate => candidate.Form == VbaCallSyntaxForm.Statement);

        Assert.Equal("Work", statement.Callee);
        Assert.Equal("(item).Activate", Assert.Single(statement.Arguments).ValueText);
        Assert.DoesNotContain(tree.Module.ArgumentLists, candidate =>
            candidate.Callee == "Work(item).Activate");
    }

    [Theory]
    [InlineData("Set item.Child(\"A1\").Value = other")]
    [InlineData("Let item.Child(\"A1\").Value = other")]
    [InlineData("item.Child(\"A1\").Value = other")]
    [InlineData("Set item . Child(\"A1\") . Value = other")]
    public void An_unindexed_assignment_preserves_the_terminal_property_after_a_called_receiver(string statement)
    {
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Caller.bas",
            $"Public Sub Run()\n    {statement}\nEnd Sub");

        var assignment = Assert.Single(tree.Module.ArgumentLists,
            candidate => candidate.Form == VbaCallSyntaxForm.PropertyAssignment);

        Assert.Equal("item.Child(\"A1\").Value", assignment.Callee);
        Assert.Empty(assignment.Arguments);
        var receiver = Assert.Single(tree.Module.ArgumentLists,
            candidate => candidate.Callee == "item.Child");
        Assert.Equal(VbaCallSyntaxForm.Parenthesized, receiver.Form);
        Assert.Equal("\"A1\"", Assert.Single(receiver.Arguments).ValueText);
        Assert.DoesNotContain(tree.Module.ArgumentLists, candidate =>
            candidate.Form == VbaCallSyntaxForm.BareValueRead
            && candidate.CalleeRange?.End == assignment.CalleeRange?.End);
    }

    [Fact]
    public void An_assignment_retains_unindexed_intermediate_value_reads()
    {
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Caller.bas", """
            Public Sub Run()
                Set item.Child("A1").Value.Other = target
            End Sub
            """);

        var assignment = Assert.Single(tree.Module.ArgumentLists,
            candidate => candidate.Form == VbaCallSyntaxForm.PropertyAssignment);

        Assert.Equal("item.Child(\"A1\").Value.Other", assignment.Callee);
        Assert.Contains(tree.Module.ArgumentLists, candidate =>
            candidate.Form == VbaCallSyntaxForm.BareValueRead && candidate.Callee == ".Value");
        Assert.DoesNotContain(tree.Module.ArgumentLists, candidate =>
            candidate.Form == VbaCallSyntaxForm.BareValueRead
            && candidate.CalleeRange?.End == assignment.CalleeRange?.End);
    }
}
