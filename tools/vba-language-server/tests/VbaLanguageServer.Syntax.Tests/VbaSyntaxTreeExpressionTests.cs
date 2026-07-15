using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaLanguageServer.Syntax.Tests;

public sealed class VbaSyntaxTreeExpressionTests
{
    [Fact]
    public void ParserEmitsExpressionAndCompletionContexts()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    Application.ActiveWorkbook _",
            "        .Worksheets(1).Range(\"A1\").",
            "    value = Application.WorksheetFunction.Sum( _",
            "        1, _",
            "        2)",
            "    With Application.ActiveWorkbook _",
            "        .Worksheets(1)",
            "        .Range(\"A1\").Find( _",
            "            What:=\"x\", _",
            "            LookAt:=xlWhole)",
            "    End With",
            "End Sub"
        ]);

        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        Assert.Contains(tree.Module.CompletionContexts, context => context.Kind == VbaCompletionContextKind.Statement);
        Assert.Contains(tree.Module.CompletionContexts, context => context.Kind == VbaCompletionContextKind.Expression && context.Text.Contains("WorksheetFunction", StringComparison.Ordinal));
        Assert.Contains(tree.Module.CompletionContexts, context => context.Kind == VbaCompletionContextKind.MemberAccess && context.IsContinued && context.Text.Contains(".Worksheets", StringComparison.Ordinal));
        Assert.Contains(tree.Module.CompletionContexts, context => context.Kind == VbaCompletionContextKind.ArgumentList && context.IsContinued && context.Text.Contains("Sum", StringComparison.Ordinal));
        Assert.Contains(tree.Module.CompletionContexts, context => context.Kind == VbaCompletionContextKind.WithReceiver && context.IsContinued && context.Text.Contains("ActiveWorkbook", StringComparison.Ordinal));

        var findCall = Assert.Single(tree.Module.ArgumentLists, argumentList => argumentList.Callee.EndsWith(".Find", StringComparison.Ordinal));
        Assert.True(findCall.IsContinued);
        Assert.Collection(
            findCall.Arguments,
            argument => Assert.Equal("What:=\"x\"", argument.Text.Trim()),
            argument => Assert.Equal("LookAt:=xlWhole", argument.Text.Trim()));
        Assert.Equal(1, findCall.GetActiveArgumentIndex(new VbaSyntaxPosition(11, 20, PositionOffset(source, 11, 20))));
        Assert.DoesNotContain(tree.Diagnostics, diagnostic => diagnostic.Code.Contains("unresolved", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ParserModelsCallArgumentKinds()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    Example(1, Arg2:=\"x\", , Arg4:=Nested(5, Name:=6),)",
            "End Sub"
        ]);

        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        var exampleCall = Assert.Single(tree.Module.ArgumentLists, argumentList => argumentList.Callee == "Example");
        Assert.Collection(
            exampleCall.Arguments,
            argument =>
            {
                Assert.Equal(VbaArgumentKind.Positional, argument.Kind);
                Assert.Equal("1", argument.Text);
                Assert.Null(argument.Name);
                Assert.Equal("1", argument.ValueText);
                Assert.Equal(argument.Range, argument.ValueRange);
            },
            argument =>
            {
                Assert.Equal(VbaArgumentKind.Named, argument.Kind);
                Assert.Equal("Arg2:=\"x\"", argument.Text);
                Assert.Equal("Arg2", argument.Name);
                Assert.Equal("\"x\"", argument.ValueText);
                Assert.NotNull(argument.NameRange);
                Assert.NotNull(argument.ValueRange);
            },
            argument =>
            {
                Assert.Equal(VbaArgumentKind.Omitted, argument.Kind);
                Assert.Equal("", argument.Text);
                Assert.Null(argument.Name);
                Assert.Null(argument.ValueText);
                Assert.True(argument.Range.End.Offset > argument.Range.Start.Offset);
            },
            argument =>
            {
                Assert.Equal(VbaArgumentKind.Named, argument.Kind);
                Assert.Equal("Arg4", argument.Name);
                Assert.Equal("Nested(5, Name:=6)", argument.ValueText);
            },
            argument =>
            {
                Assert.Equal(VbaArgumentKind.Omitted, argument.Kind);
                Assert.True(argument.Range.End.Offset > argument.Range.Start.Offset);
            });

        var nestedCall = Assert.Single(tree.Module.ArgumentLists, argumentList => argumentList.Callee == "Nested");
        Assert.Collection(
            nestedCall.Arguments,
            argument => Assert.Equal(VbaArgumentKind.Positional, argument.Kind),
            argument =>
            {
                Assert.Equal(VbaArgumentKind.Named, argument.Kind);
                Assert.Equal("Name", argument.Name);
            });
    }

    [Fact]
    public void ParserModelsStatementFormCallArgumentKinds()
    {
        const string callLine = "    ExampleSub 1, Arg2:=\"x\", , Arg4:=Nested(5, Name:=6)";
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            callLine,
            "End Sub"
        ]);

        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        var call = Assert.Single(
            tree.Module.ArgumentLists,
            argumentList => argumentList.Callee == "ExampleSub");
        Assert.Collection(
            call.Arguments,
            argument => Assert.Equal(VbaArgumentKind.Positional, argument.Kind),
            argument =>
            {
                Assert.Equal(VbaArgumentKind.Named, argument.Kind);
                Assert.Equal("Arg2", argument.Name);
                Assert.Equal("\"x\"", argument.ValueText);
            },
            argument => Assert.Equal(VbaArgumentKind.Omitted, argument.Kind),
            argument =>
            {
                Assert.Equal(VbaArgumentKind.Named, argument.Kind);
                Assert.Equal("Arg4", argument.Name);
                Assert.Equal("Nested(5, Name:=6)", argument.ValueText);
            });
        Assert.Equal(
            callLine.IndexOf("1", StringComparison.Ordinal),
            call.Arguments[0].Range.Start.Character);
    }

    [Theory]
    [InlineData("Public Sub Run(ByVal value As String)", "Run")]
    [InlineData("Public Function Build(ByVal value As String) As String", "Build")]
    [InlineData("Public Property Get DisplayName(ByVal value As String) As String", "DisplayName")]
    [InlineData("Public Event Saved(ByVal value As String)", "Saved")]
    [InlineData("Public Declare PtrSafe Function GetTickCount Lib \"kernel32\" (ByVal value As Long) As Long", "GetTickCount")]
    [InlineData("Dim values(1 To 3) As String", "values")]
    [InlineData("Private moduleValues(1 To 3) As String", "moduleValues")]
    [InlineData("ReDim Preserve values(1 To 3)", "values")]
    [InlineData("Rem Example(Arg1:=1)", "Example")]
    public void ParserDoesNotModelCallableParametersOrArrayBoundsAsCallArguments(
        string declarationLine,
        string excludedCallee)
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            declarationLine
        ]);

        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);
        var openCharacter = declarationLine.IndexOf('(');
        var position = tree.GetPositionSyntax(1, openCharacter + 1);

        Assert.DoesNotContain(
            tree.Module.ArgumentLists,
            argumentList => argumentList.Callee.Equals(excludedCallee, StringComparison.OrdinalIgnoreCase));
        Assert.Null(position.CallSite);
    }

    [Fact]
    public void ParserDoesNotModelUserDefinedTypeFieldAsStatementCall()
    {
        const string fieldLine = "    DisplayName As String";
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Type CustomerRecord",
            fieldLine,
            "End Type"
        ]);

        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);
        var position = tree.GetPositionSyntax(2, fieldLine.Length);

        Assert.Empty(tree.Module.ArgumentLists);
        Assert.Null(position.CallSite);
    }

    private static int PositionOffset(string source, int line, int character)
    {
        var currentLine = 0;
        var currentCharacter = 0;
        for (var offset = 0; offset < source.Length; offset++)
        {
            if (currentLine == line && currentCharacter == character)
            {
                return offset;
            }

            if (source[offset] == '\n')
            {
                currentLine++;
                currentCharacter = 0;
                continue;
            }

            currentCharacter++;
        }

        return source.Length;
    }
}
