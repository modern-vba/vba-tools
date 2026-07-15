using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaLanguageServer.Syntax.Tests;

public sealed class VbaTriggerCompletionSyntaxTests
{
    [Fact]
    public void ColonDistinguishesIncompleteUnparenthesizedNamedArgumentFromStatementSeparator()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    Dim value As Long",
            "    ExampleSub Arg2:",
            "    value = 1:",
            "End Sub"
        ]);
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        var namedArgument = tree.GetPositionSyntax(3, "    ExampleSub Arg2:".Length);
        var statementSeparator = tree.GetPositionSyntax(4, "    value = 1:".Length);

        Assert.Equal(VbaCompletionExpectation.None, namedArgument.CompletionExpectation);
        Assert.Equal(VbaCompletionExpectation.ProcedureStatement, statementSeparator.CompletionExpectation);
    }

    [Theory]
    [InlineData("    Debug.Print value;")]
    [InlineData("    Debug.Print value; ")]
    [InlineData("    Debug.Print value,")]
    [InlineData("    Debug.Print value, ")]
    public void DebugPrintSeparatorsKeepTheExpressionValueSlotOpen(string statement)
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    Dim value As Long",
            statement,
            "End Sub"
        ]);
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        var position = tree.GetPositionSyntax(3, statement.Length);

        Assert.Equal(VbaCompletionExpectation.ExpressionValue, position.CompletionExpectation);
    }

    [Theory]
    [InlineData("    Select ")]
    [InlineData("    Select C")]
    public void SelectStatementExpectsTheCaseSyntaxWord(string statement)
    {
        var position = ProcedurePosition(statement);

        Assert.Equal(VbaCompletionExpectation.SyntaxWord, position.CompletionExpectation);
        Assert.Equal(["Case"], position.SyntaxWords);
    }

    [Theory]
    [InlineData("    For Each item ")]
    [InlineData("    For Each item I")]
    public void ForEachTargetExpectsTheInSyntaxWord(string statement)
    {
        var position = ProcedurePosition(statement);

        Assert.Equal(VbaCompletionExpectation.SyntaxWord, position.CompletionExpectation);
        Assert.Equal(["In"], position.SyntaxWords);
    }

    [Theory]
    [InlineData("    For index = 1 ", "To")]
    [InlineData("    For index = 1 T", "To")]
    [InlineData("    For index = Factory() ", "To")]
    [InlineData("    For index = 1 To maximum ", "Step")]
    [InlineData("    For index = 1 To maximum S", "Step")]
    public void ForHeaderOffersItsRequiredAndOptionalTransitionWords(
        string statement,
        string expected)
    {
        var position = ProcedurePosition(statement);

        Assert.Equal(VbaCompletionExpectation.SyntaxWord, position.CompletionExpectation);
        Assert.Equal([expected], position.SyntaxWords);
    }

    [Theory]
    [InlineData("    If ready ")]
    [InlineData("    If ready T")]
    [InlineData("    If TypeOf value Is Foo ")]
    [InlineData("    ElseIf ready ")]
    [InlineData("    ElseIf ready T")]
    public void ConditionalHeaderExpectsTheThenSyntaxWord(string statement)
    {
        var position = ConditionalHeaderPosition(statement);

        Assert.Equal(VbaCompletionExpectation.SyntaxWord, position.CompletionExpectation);
        Assert.Equal(["Then"], position.SyntaxWords);
    }

    [Theory]
    [InlineData("    If service.T")]
    [InlineData("    ElseIf service.T")]
    public void ConditionalMemberPrefixIsNotReclassifiedAsThen(string statement)
    {
        var position = ConditionalHeaderPosition(statement);

        Assert.Equal(VbaCompletionExpectation.ExpressionValue, position.CompletionExpectation);
        Assert.Empty(position.SyntaxWords);
        Assert.NotNull(position.MemberAccess);
    }

    [Theory]
    [InlineData("    If ready Then")]
    [InlineData("    ElseIf ready Then")]
    public void CompletedThenRequiresWhitespaceBeforeStatementCompletion(string statement)
    {
        var position = ConditionalHeaderPosition(statement);

        Assert.Equal(VbaCompletionExpectation.None, position.CompletionExpectation);
    }

    [Theory]
    [InlineData("    If ready Then ")]
    [InlineData("    ElseIf ready Then ")]
    public void ThenWhitespaceOpensStatementCompletion(string statement)
    {
        var position = ConditionalHeaderPosition(statement);

        Assert.Equal(VbaCompletionExpectation.ProcedureStatement, position.CompletionExpectation);
    }

    [Fact]
    public void PartialSingleLineThenBodyKeepsStatementCompletionOpen()
    {
        var position = ConditionalHeaderPosition("    If ready Then Ex");

        Assert.Equal(VbaCompletionExpectation.ProcedureStatement, position.CompletionExpectation);
    }

    [Fact]
    public void PartialSingleLineElseBodyKeepsStatementCompletionOpen()
    {
        var position = ConditionalHeaderPosition("    If ready Then Foo Else Ba");

        Assert.Equal(VbaCompletionExpectation.ProcedureStatement, position.CompletionExpectation);
    }

    [Fact]
    public void ColonAfterThenDoesNotLeakASingleLineIfBlockToTheNextLine()
    {
        const string nextStatement = "    Ba";
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    If ready Then: Foo",
            nextStatement,
            "End Sub"
        ]);

        var position = VbaSyntaxTree
            .ParseModule("file:///C:/work/Worker.bas", source)
            .GetPositionSyntax(3, nextStatement.Length);

        Assert.Equal(VbaCompletionExpectation.ProcedureStatement, position.CompletionExpectation);
        Assert.DoesNotContain(position.EnclosingBlocks, block => block.Block.Kind == VbaBlockKind.If);
        Assert.Equal(["End Sub"], position.ContextualStatements);
    }

    [Theory]
    [InlineData("    Select Case")]
    [InlineData("    For Each item In")]
    [InlineData("    Exit Sub")]
    public void CompletedRequiredSyntaxWordWaitsForASeparator(string statement)
    {
        var position = ProcedurePosition(statement);

        Assert.Equal(VbaCompletionExpectation.None, position.CompletionExpectation);
    }

    private static VbaPositionSyntax ConditionalHeaderPosition(string statement)
    {
        var isElseIf = statement.TrimStart().StartsWith("ElseIf", StringComparison.OrdinalIgnoreCase);
        var lines = new List<string>
        {
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()"
        };
        if (isElseIf)
        {
            lines.Add("    If True Then");
        }

        var line = lines.Count;
        lines.Add(statement);
        lines.Add("    End If");
        lines.Add("End Sub");
        return VbaSyntaxTree
            .ParseModule("file:///C:/work/Worker.bas", string.Join('\n', lines))
            .GetPositionSyntax(line, statement.Length);
    }

    [Theory]
    [InlineData("    On Error ", new[] { "GoTo", "Resume" })]
    [InlineData("    On Error G", new[] { "GoTo" })]
    [InlineData("    On Error R", new[] { "Resume" })]
    public void OnErrorExpectsRecoverySyntaxWords(string statement, string[] expected)
    {
        var position = ProcedurePosition(statement);

        Assert.Equal(VbaCompletionExpectation.SyntaxWord, position.CompletionExpectation);
        Assert.Equal(expected, position.SyntaxWords);
    }

    [Fact]
    public void CompletedOnErrorKeywordFailsClosedUntilTheNextSeparator()
    {
        var position = ProcedurePosition("    On Error");

        Assert.Equal(VbaCompletionExpectation.None, position.CompletionExpectation);
        Assert.Empty(position.SupplementalSyntaxWords);
    }

    [Theory]
    [InlineData("    On ")]
    [InlineData("    On E")]
    public void OnSelectorCombinesExpressionAndErrorAlternatives(string statement)
    {
        var position = ProcedurePosition(statement);

        Assert.Equal(VbaCompletionExpectation.ExpressionValue, position.CompletionExpectation);
        Assert.Equal(["Error"], position.SupplementalSyntaxWords);
    }

    [Theory]
    [InlineData("    On selector ", new[] { "GoTo", "GoSub" })]
    [InlineData("    On selector G", new[] { "GoTo", "GoSub" })]
    public void CompletedOnSelectorExpectsDispatchSyntaxWords(
        string statement,
        string[] expected)
    {
        var position = ProcedurePosition(statement);

        Assert.Equal(VbaCompletionExpectation.SyntaxWord, position.CompletionExpectation);
        Assert.Equal(expected, position.SyntaxWords);
    }

    [Fact]
    public void OnSelectorMemberPrefixIsNotReclassifiedAsDispatchWord()
    {
        var position = ProcedurePosition("    On selector.Go");

        Assert.Equal(VbaCompletionExpectation.ExpressionValue, position.CompletionExpectation);
        Assert.Empty(position.SyntaxWords);
        Assert.NotNull(position.MemberAccess);
    }

    [Theory]
    [InlineData("    GoTo")]
    [InlineData("    On Error GoTo")]
    [InlineData("    On selector GoTo")]
    [InlineData("    On Error Resume")]
    public void LabelMarkerRequiresWhitespaceBeforeDestinationCompletion(string statement)
    {
        var position = ProcedurePosition(statement);

        Assert.Equal(VbaCompletionExpectation.None, position.CompletionExpectation);
    }

    [Theory]
    [InlineData("    GoTo ")]
    [InlineData("    On Error GoTo ")]
    [InlineData("    On selector GoTo ")]
    [InlineData("    On Error Resume ")]
    public void LabelMarkerWhitespaceOpensDestinationCompletion(string statement)
    {
        var position = ProcedurePosition(statement);

        Assert.Equal(VbaCompletionExpectation.LabelName, position.CompletionExpectation);
    }

    [Fact]
    public void ExitOffersOnlyWordsValidForTheEnclosingBlocksAndCallable()
    {
        var sub = PositionAtMarker(string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    Exit |",
            "End Sub"
        ]));
        var function = PositionAtMarker(string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Function Build() As Long",
            "    Exit |",
            "End Function"
        ]));
        var property = PositionAtMarker(string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Public Property Get Value() As Long",
            "    Exit |",
            "End Property"
        ]), "file:///C:/work/Worker.cls");
        var nested = PositionAtMarker(string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    Do While True",
            "        For index = 1 To 2",
            "            Exit |",
            "        Next",
            "    Loop",
            "End Sub"
        ]));

        Assert.Equal(["Sub"], sub.SyntaxWords);
        Assert.Equal(["Function"], function.SyntaxWords);
        Assert.Equal(["Property"], property.SyntaxWords);
        Assert.Equal(["For", "Do", "Sub"], nested.SyntaxWords);
        Assert.All([sub, function, property, nested], position =>
            Assert.Equal(VbaCompletionExpectation.SyntaxWord, position.CompletionExpectation));
    }

    private static VbaPositionSyntax PositionAtMarker(
        string markedSource,
        string uri = "file:///C:/work/Worker.bas")
    {
        var markerOffset = markedSource.IndexOf('|');
        var source = markedSource.Remove(markerOffset, 1);
        var line = source[..markerOffset].Count(character => character == '\n');
        var lineStart = source.LastIndexOf('\n', Math.Max(0, markerOffset - 1));
        var character = markerOffset - (lineStart + 1);
        return VbaSyntaxTree.ParseModule(uri, source).GetPositionSyntax(line, character);
    }

    private static VbaPositionSyntax ProcedurePosition(string statement)
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            statement,
            "End Sub"
        ]);
        return VbaSyntaxTree
            .ParseModule("file:///C:/work/Worker.bas", source)
            .GetPositionSyntax(2, statement.Length);
    }
}
