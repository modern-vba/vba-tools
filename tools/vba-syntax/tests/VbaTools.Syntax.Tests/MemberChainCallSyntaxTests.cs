using Xunit;

namespace VbaTools.Syntax.Tests;

public sealed class MemberChainCallSyntaxTests
{
    [Fact]
    public void ImplicitTerminalInvocationRetainsTheCompleteReceiverChain()
    {
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Caller.bas", """
            Public Sub Run()
                item.Child("A1").Activate
            End Sub
            """);

        var terminal = Assert.Single(tree.Module.ArgumentLists,
            candidate => candidate.Form == VbaCallSyntaxForm.Statement);

        Assert.Equal("item.Child(\"A1\").Activate", terminal.Callee);
        Assert.Empty(terminal.Arguments);
        Assert.NotNull(terminal.CalleeRange);
        Assert.Equal(4, terminal.CalleeRange.Start.Character);
        Assert.Equal("    item.Child(\"A1\").Activate".Length, terminal.CalleeRange.End.Character);
        Assert.Contains(tree.Module.ArgumentLists, candidate =>
            candidate.Form == VbaCallSyntaxForm.Parenthesized && candidate.Callee == "item.Child");
        Assert.DoesNotContain(tree.Module.ArgumentLists, candidate =>
            candidate.Form == VbaCallSyntaxForm.BareValueRead
            && candidate.CalleeRange?.End == terminal.CalleeRange.End);
    }

    [Fact]
    public void ExplicitTerminalArgumentsAreSeparateFromReceiverArguments()
    {
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Caller.bas", """
            Public Sub Run()
                Call item.Child("A1").Activate(1&)
            End Sub
            """);

        var terminal = Assert.Single(tree.Module.ArgumentLists,
            candidate => candidate.Callee.EndsWith(".Activate", StringComparison.Ordinal));

        Assert.Equal("item.Child(\"A1\").Activate", terminal.Callee);
        Assert.Equal(VbaCallSyntaxForm.Parenthesized, terminal.Form);
        Assert.Equal("1&", Assert.Single(terminal.Arguments).ValueText);
        var receiver = Assert.Single(tree.Module.ArgumentLists,
            candidate => candidate.Callee == "item.Child");
        Assert.Equal("\"A1\"", Assert.Single(receiver.Arguments).ValueText);
    }

    [Fact]
    public void ImplicitTerminalArgumentsAreSeparateFromReceiverArguments()
    {
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Caller.bas", """
            Public Sub Run()
                item.Child("A1").Activate 1&
            End Sub
            """);

        var terminal = Assert.Single(tree.Module.ArgumentLists,
            candidate => candidate.Form == VbaCallSyntaxForm.Statement);

        Assert.Equal("item.Child(\"A1\").Activate", terminal.Callee);
        Assert.Equal("1&", Assert.Single(terminal.Arguments).ValueText);
        var receiver = Assert.Single(tree.Module.ArgumentLists,
            candidate => candidate.Callee == "item.Child");
        Assert.Equal("\"A1\"", Assert.Single(receiver.Arguments).ValueText);
    }

    [Theory]
    [InlineData("item.Child(\"A1\").Child(\"B1\").Activate")]
    [InlineData("Call item.Child(\"A1\").Child(\"B1\").Activate")]
    public void MultipleReceiversRetainTheirOwnArgumentsAndTheTerminalStatement(string statement)
    {
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Caller.bas",
            $"Public Sub Run()\n    {statement}\nEnd Sub\n");

        var terminal = Assert.Single(tree.Module.ArgumentLists,
            candidate => candidate.Form == VbaCallSyntaxForm.Statement);

        Assert.Equal("item.Child(\"A1\").Child(\"B1\").Activate", terminal.Callee);
        Assert.Empty(terminal.Arguments);
        var firstReceiver = Assert.Single(tree.Module.ArgumentLists,
            candidate => candidate.Callee == "item.Child");
        Assert.Equal("\"A1\"", Assert.Single(firstReceiver.Arguments).ValueText);
        var secondReceiver = Assert.Single(tree.Module.ArgumentLists,
            candidate => candidate.Callee == "item.Child(\"A1\").Child");
        Assert.Equal("\"B1\"", Assert.Single(secondReceiver.Arguments).ValueText);
        Assert.DoesNotContain(tree.Module.ArgumentLists, candidate =>
            candidate.Form == VbaCallSyntaxForm.BareValueRead
            && candidate.CalleeRange?.End == terminal.CalleeRange?.End);
    }

    [Theory]
    [InlineData("item.Child(\"A1\")")]
    [InlineData("Set result = item.Child(\"A1\").Child(\"B1\")")]
    [InlineData("value = item.Child(\"A1\").Count")]
    public void StandaloneGetterAndValueChainsDoNotAcquireATerminalStatement(string statement)
    {
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Caller.bas",
            $"Public Sub Run()\n    {statement}\nEnd Sub\n");

        Assert.DoesNotContain(tree.Module.ArgumentLists,
            candidate => candidate.Form == VbaCallSyntaxForm.Statement);
        Assert.Contains(tree.Module.ArgumentLists, candidate =>
            candidate.Callee == "item.Child"
            && candidate.Form == VbaCallSyntaxForm.Parenthesized);
    }
}
