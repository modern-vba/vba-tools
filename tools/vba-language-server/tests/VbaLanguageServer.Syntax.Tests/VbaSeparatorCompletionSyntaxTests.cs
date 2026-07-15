using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaLanguageServer.Syntax.Tests;

public sealed class VbaSeparatorCompletionSyntaxTests
{
    private const string PlainContext = "plain";
    private const string WhileContext = "while";
    private const string WithContext = "with";
    private const string ForContext = "for";
    private const string DoContext = "do";
    private const string LoopContext = "loop";
    private const string SelectContext = "select";

    [Theory]
    [InlineData("    Dim value As", PlainContext, VbaCompletionExpectation.TypeName, null)]
    [InlineData("    Dim value As New", PlainContext, VbaCompletionExpectation.CreatableType, null)]
    [InlineData("    If", PlainContext, VbaCompletionExpectation.ExpressionValue, null)]
    [InlineData("    While", WhileContext, VbaCompletionExpectation.ExpressionValue, null)]
    [InlineData("    With", WithContext, VbaCompletionExpectation.ExpressionValue, null)]
    [InlineData("    On", PlainContext, VbaCompletionExpectation.ExpressionValue, null)]
    [InlineData("    Set", PlainContext, VbaCompletionExpectation.AssignmentTarget, null)]
    [InlineData("    Let", PlainContext, VbaCompletionExpectation.AssignmentTarget, null)]
    [InlineData("    For", ForContext, VbaCompletionExpectation.AssignmentTarget, null)]
    [InlineData("    For Each", ForContext, VbaCompletionExpectation.AssignmentTarget, null)]
    [InlineData("    RaiseEvent", PlainContext, VbaCompletionExpectation.EventName, null)]
    [InlineData("    For index = 1 To", ForContext, VbaCompletionExpectation.ExpressionValue, null)]
    [InlineData("    For index = 1 To maximum Step", ForContext, VbaCompletionExpectation.ExpressionValue, null)]
    [InlineData("        Case 1 To", SelectContext, VbaCompletionExpectation.ExpressionValue, null)]
    [InlineData("    Do While", DoContext, VbaCompletionExpectation.ExpressionValue, null)]
    [InlineData("    Do Until", DoContext, VbaCompletionExpectation.ExpressionValue, null)]
    [InlineData("    Loop While", LoopContext, VbaCompletionExpectation.ExpressionValue, null)]
    [InlineData("    Loop Until", LoopContext, VbaCompletionExpectation.ExpressionValue, null)]
    [InlineData("    If TypeOf value Is", PlainContext, VbaCompletionExpectation.TypeName, null)]
    [InlineData("    Call", PlainContext, VbaCompletionExpectation.CallableName, null)]
    [InlineData("    Select", PlainContext, VbaCompletionExpectation.SyntaxWord, "Case")]
    [InlineData("    Exit", PlainContext, VbaCompletionExpectation.SyntaxWord, "Sub")]
    public void CompletedGrammarMarkerRequiresWhitespaceBeforeTheNextSlot(
        string statement,
        string context,
        VbaCompletionExpectation expectedAfterWhitespace,
        string? expectedSyntaxWord)
    {
        var withoutWhitespace = ProcedurePosition(statement, context);
        var afterWhitespace = ProcedurePosition(statement + " ", context);

        Assert.Equal(
            (VbaCompletionExpectation.None, expectedAfterWhitespace),
            (withoutWhitespace.CompletionExpectation, afterWhitespace.CompletionExpectation));
        if (expectedSyntaxWord is not null)
        {
            Assert.Equal([expectedSyntaxWord], afterWhitespace.SyntaxWords);
        }
    }

    [Theory]
    [InlineData("And")]
    [InlineData("Or")]
    [InlineData("Xor")]
    [InlineData("Eqv")]
    [InlineData("Imp")]
    [InlineData("Mod")]
    [InlineData("Like")]
    [InlineData("Is")]
    [InlineData("Not")]
    public void CompletedWordOperatorRequiresWhitespaceBeforeTheNextOperand(string wordOperator)
    {
        var statement = wordOperator.Equals("Not", StringComparison.Ordinal)
            ? "    If Not"
            : $"    If value {wordOperator}";

        var withoutWhitespace = ProcedurePosition(statement, PlainContext);
        var afterWhitespace = ProcedurePosition(statement + " ", PlainContext);

        Assert.Equal(
            (VbaCompletionExpectation.None, VbaCompletionExpectation.ExpressionValue),
            (withoutWhitespace.CompletionExpectation, afterWhitespace.CompletionExpectation));
    }

    [Theory]
    [InlineData("    If ready And (")]
    [InlineData("    If Not (")]
    public void GroupingParenthesisAfterWordOperatorExpectsAnExpressionValue(string statement)
    {
        var position = ProcedurePosition(statement, PlainContext);

        Assert.Equal(VbaCompletionExpectation.ExpressionValue, position.CompletionExpectation);
    }

    [Theory]
    [InlineData("    ExampleSub 1, (", VbaCompletionExpectation.ExpressionValue)]
    [InlineData("    ExampleSub Arg2:=(", VbaCompletionExpectation.NamedArgumentValue)]
    [InlineData("    Foo((", VbaCompletionExpectation.ExpressionValue)]
    public void GroupedCallArgumentPreservesItsValueContext(
        string statement,
        VbaCompletionExpectation expected)
    {
        var position = ProcedurePosition(statement, PlainContext);

        Assert.Equal(expected, position.CompletionExpectation);
    }

    [Theory]
    [InlineData("    If (", PlainContext)]
    [InlineData("    While (", WhileContext)]
    [InlineData("    With (", WithContext)]
    [InlineData("        Case (", SelectContext)]
    [InlineData("    Debug.Assert (", PlainContext)]
    [InlineData("    Debug.Print (", PlainContext)]
    public void GroupingParenthesisPreservesTheStatementExpressionContext(
        string statement,
        string context)
    {
        var position = ProcedurePosition(statement, context);

        Assert.Equal(VbaCompletionExpectation.ExpressionValue, position.CompletionExpectation);
    }

    [Theory]
    [InlineData("    If TypeOf")]
    [InlineData("    callback = AddressOf")]
    [InlineData("    callback = AddressOf Callb")]
    [InlineData("    callback = AddressOf Module.Callb")]
    public void UnsupportedSpecialOperandContextsFailClosed(string statement)
    {
        var withoutWhitespace = ProcedurePosition(statement, PlainContext);
        var afterWhitespace = ProcedurePosition(statement + " ", PlainContext);

        Assert.Equal(VbaCompletionExpectation.None, withoutWhitespace.CompletionExpectation);
        Assert.Equal(VbaCompletionExpectation.None, afterWhitespace.CompletionExpectation);
    }

    [Theory]
    [InlineData("    If TypeOf value ")]
    [InlineData("    If TypeOf value I")]
    [InlineData("    If TypeOf Factory() ")]
    public void CompletedTypeOfOperandExpectsOnlyTheIsSyntaxWord(string statement)
    {
        var position = ProcedurePosition(statement, PlainContext);

        Assert.Equal(VbaCompletionExpectation.SyntaxWord, position.CompletionExpectation);
        Assert.Equal(["Is"], position.SyntaxWords);
    }

    [Fact]
    public void CompletedTypeOfConditionLeavesTheSpecialOperandContext()
    {
        const string statement = "    If TypeOf value Is Worker Then ";
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            statement,
            "    End If",
            "End Sub"
        ]);

        var position = VbaSyntaxTree
            .ParseModule("file:///C:/work/Worker.bas", source)
            .GetPositionSyntax(2, statement.Length);

        Assert.Equal(VbaCompletionExpectation.ProcedureStatement, position.CompletionExpectation);
    }

    [Fact]
    public void AddressOfInAnEarlierCallArgumentDoesNotBlockTheActiveArgument()
    {
        const string statement = "    Register AddressOf Callback, ";
        var position = ProcedurePosition(statement, PlainContext);

        Assert.Equal(VbaCompletionExpectation.CallArgument, position.CompletionExpectation);
    }

    [Theory]
    [InlineData("    result = Factory(AddressOf Callback) + ")]
    [InlineData("    Consume Factory(AddressOf Callback) + ")]
    public void CompletedAddressOfSubexpressionDoesNotBlockFollowingOperand(string statement)
    {
        var position = ProcedurePosition(statement, PlainContext);

        Assert.Equal(VbaCompletionExpectation.ExpressionValue, position.CompletionExpectation);
    }

    [Theory]
    [InlineData("    If TypeOf Factory(I")]
    [InlineData("    If TypeOf Factory(value, I")]
    [InlineData("    Register TypeOf value, I")]
    public void TypeOfTransitionDoesNotLeakIntoTheActiveCallArgument(string statement)
    {
        var position = ProcedurePosition(statement, PlainContext);

        Assert.Equal(VbaCompletionExpectation.CallArgument, position.CompletionExpectation);
        Assert.Empty(position.SyntaxWords);
    }

    [Theory]
    [InlineData("    Do", DoContext)]
    [InlineData("    Loop", LoopContext)]
    public void OptionalLoopConditionWordsOpenOnlyAfterTheSeparator(
        string statement,
        string context)
    {
        var withoutWhitespace = ProcedurePosition(statement, context);
        var afterWhitespace = ProcedurePosition(statement + " ", context);

        Assert.Equal(VbaCompletionExpectation.None, withoutWhitespace.CompletionExpectation);
        Assert.Equal(VbaCompletionExpectation.SyntaxWord, afterWhitespace.CompletionExpectation);
        Assert.Equal(["While", "Until"], afterWhitespace.SyntaxWords);
    }

    [Theory]
    [InlineData("    For ")]
    [InlineData("    For E")]
    public void ForTargetSlotAlsoOffersTheEachAlternative(string statement)
    {
        var position = ProcedurePosition(statement, ForContext);

        Assert.Equal(VbaCompletionExpectation.AssignmentTarget, position.CompletionExpectation);
        Assert.Equal(["Each"], position.SupplementalSyntaxWords);
    }

    [Theory]
    [InlineData("    Dim value As ", new[] { "New" })]
    [InlineData("    Dim value As N", new[] { "New" })]
    [InlineData("    Dim value As Str", new string[0])]
    public void TypeAnnotationSlotOffersNewAsAnAlternative(
        string statement,
        string[] supplementalWords)
    {
        var position = ProcedurePosition(statement, PlainContext);

        Assert.Equal(VbaCompletionExpectation.TypeName, position.CompletionExpectation);
        Assert.Equal(supplementalWords, position.SupplementalSyntaxWords);
    }

    [Theory]
    [InlineData("    For index = val")]
    [InlineData("    For index = 1 To max")]
    public void PartialForBoundExpressionsKeepExpectingValues(string statement)
    {
        var position = ProcedurePosition(statement, ForContext);

        Assert.Equal(VbaCompletionExpectation.ExpressionValue, position.CompletionExpectation);
        Assert.Empty(position.SyntaxWords);
    }

    [Theory]
    [InlineData("    Set target ")]
    [InlineData("    Let target ")]
    public void CompletedExplicitAssignmentTargetFailsClosedUntilTheEqualsOperator(
        string statement)
    {
        var position = ProcedurePosition(statement, PlainContext);

        Assert.Equal(VbaCompletionExpectation.None, position.CompletionExpectation);
    }

    [Theory]
    [InlineData("    result = value oth")]
    [InlineData("    result = Factory() oth")]
    public void AdjacentExpressionValueWithoutAnOperatorFailsClosed(string statement)
    {
        var position = ProcedurePosition(statement, PlainContext);

        Assert.Equal(VbaCompletionExpectation.None, position.CompletionExpectation);
    }

    private static VbaPositionSyntax ProcedurePosition(string statement, string context)
    {
        IReadOnlyList<string> body = context switch
        {
            PlainContext => [statement],
            WhileContext => [statement, "    Wend"],
            WithContext => [statement, "    End With"],
            ForContext => [statement, "    Next"],
            DoContext => [statement, "    Loop"],
            LoopContext => ["    Do", statement],
            SelectContext => ["    Select Case selector", statement, "    End Select"],
            _ => throw new ArgumentOutOfRangeException(nameof(context), context, null)
        };
        var lines = new List<string>
        {
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()"
        };
        lines.AddRange(body);
        lines.Add("End Sub");
        var line = lines.IndexOf(statement);

        return VbaSyntaxTree
            .ParseModule("file:///C:/work/Worker.bas", string.Join('\n', lines))
            .GetPositionSyntax(line, statement.Length);
    }
}
