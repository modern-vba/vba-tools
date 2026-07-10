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
