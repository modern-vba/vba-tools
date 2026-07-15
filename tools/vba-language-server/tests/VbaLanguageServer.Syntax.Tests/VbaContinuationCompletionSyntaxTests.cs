using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaLanguageServer.Syntax.Tests;

public sealed class VbaContinuationCompletionSyntaxTests
{
    [Fact]
    public void VisibilityModifiersRespectStandardAndObjectModuleGrammar()
    {
        AssertDeclarationContinuation(
            "file:///C:/work/Worker.bas",
            "Public ",
            "Const", "Declare", "Enum", "Function", "Static", "Sub", "Type");
        AssertDeclarationContinuation(
            "file:///C:/work/Worker.bas",
            "Private ",
            "Const", "Declare", "Enum", "Function", "Static", "Sub", "Type");
        AssertDeclarationContinuation(
            "file:///C:/work/Worker.cls",
            "Public ",
            "Event", "Function", "Property", "Static", "Sub", "WithEvents");
        AssertDeclarationContinuation(
            "file:///C:/work/Worker.cls",
            "Private ",
            "Const", "Declare", "Enum", "Function", "Property", "Static", "Sub", "Type", "WithEvents");
        AssertDeclarationContinuation(
            "file:///C:/work/Worker.cls",
            "Friend ",
            "Function", "Property", "Static", "Sub");
    }

    [Fact]
    public void StaticModifiersRespectModuleCallableKinds()
    {
        AssertDeclarationContinuation(
            "file:///C:/work/Worker.bas",
            "Static ",
            "Function", "Sub");
        AssertDeclarationContinuation(
            "file:///C:/work/Worker.bas",
            "Public Static ",
            "Function", "Sub");
        AssertDeclarationContinuation(
            "file:///C:/work/Worker.cls",
            "Static ",
            "Function", "Property", "Sub");
        AssertDeclarationContinuation(
            "file:///C:/work/Worker.cls",
            "Public Static ",
            "Function", "Property", "Sub");
    }

    [Theory]
    [InlineData("file:///C:/work/Worker.cls", "Property ")]
    [InlineData("file:///C:/work/Worker.cls", "Public Property ")]
    [InlineData("file:///C:/work/Dialog.frm", "Private Property ")]
    public void ObjectPropertyDeclarationsExposeOnlyAccessorKinds(
        string uri,
        string prefix)
    {
        AssertDeclarationContinuation(uri, prefix, "Get", "Let", "Set");
    }

    [Theory]
    [InlineData("file:///C:/work/Worker.bas", "Declare ", new[] { "Function", "PtrSafe", "Sub" })]
    [InlineData("file:///C:/work/Worker.bas", "Public Declare ", new[] { "Function", "PtrSafe", "Sub" })]
    [InlineData("file:///C:/work/Worker.bas", "Declare PtrSafe ", new[] { "Function", "Sub" })]
    [InlineData("file:///C:/work/Worker.cls", "Private Declare ", new[] { "Function", "PtrSafe", "Sub" })]
    [InlineData("file:///C:/work/Worker.cls", "Private Declare PtrSafe ", new[] { "Function", "Sub" })]
    public void DeclareContinuationPreservesOptionalPtrSafePosition(
        string uri,
        string prefix,
        string[] expected)
    {
        AssertDeclarationContinuation(uri, prefix, expected);
    }

    [Fact]
    public void OptionStatementOffersOnlyLegalFirstTransitions()
    {
        AssertDeclarationContinuation(
            "file:///C:/work/Worker.bas",
            "Option ",
            "Base", "Compare", "Explicit", "Private");
    }

    [Theory]
    [InlineData("Option Base ", new[] { "0", "1" })]
    [InlineData("Option Compare ", new[] { "Binary", "Text" })]
    [InlineData("Option Private ", new[] { "Module" })]
    public void OptionStatementOffersOnlyStateSpecificTransitions(
        string prefix,
        string[] expected)
    {
        AssertDeclarationContinuation("file:///C:/work/Worker.bas", prefix, expected);
    }

    [Fact]
    public void ClassImplementsStatementExpectsOnlyAnImplementableType()
    {
        const string uri = "file:///C:/work/Worker.cls";
        var source = ModuleSource(uri, "Implements ");
        var position = VbaSyntaxTree
            .ParseModule(uri, source)
            .GetPositionSyntax(
                source.Count(character => character == '\n'),
                "Implements ".Length);

        Assert.Equal(VbaCompletionExpectation.ImplementsType, position.CompletionExpectation);
        Assert.Equal(VbaPositionTypeReferenceContext.Implements, position.TypeReference?.Context);
    }

    [Fact]
    public void PartialDeclarationContinuationKeepsCanonicalWordAndReplacementRange()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var source = ModuleSource(uri, "Public Sta");
        var tree = VbaSyntaxTree.ParseModule(uri, source);

        var position = tree.GetPositionSyntax(1, "Public Sta".Length);

        Assert.Equal(VbaCompletionExpectation.SyntaxWord, position.CompletionExpectation);
        Assert.Equal(["Static"], position.SyntaxWords);
        Assert.Equal("Sta", Slice(source, position.CompletionReplacementRange!));
    }

    [Fact]
    public void ExplicitLineContinuationPreservesDeclarationSyntaxWords()
    {
        const string uri = "file:///C:/work/Worker.bas";
        const string continuation = "    Sta";
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public _",
            continuation
        ]);
        var tree = VbaSyntaxTree.ParseModule(uri, source);

        var position = tree.GetPositionSyntax(2, continuation.Length);

        Assert.Equal(VbaCompletionExpectation.SyntaxWord, position.CompletionExpectation);
        Assert.Equal(["Static"], position.SyntaxWords);
        Assert.Equal("Sta", Slice(source, position.CompletionReplacementRange!));
    }

    [Theory]
    [InlineData("Public")]
    [InlineData("Public Function")]
    [InlineData("Public Static")]
    [InlineData("Public Property Get")]
    public void CompletedDeclarationWordsWaitForASeparator(string declaration)
    {
        const string uri = "file:///C:/work/Worker.cls";
        var source = ModuleSource(uri, declaration);
        var tree = VbaSyntaxTree.ParseModule(uri, source);

        var position = tree.GetPositionSyntax(
            source.Count(character => character == '\n'),
            declaration.Length);

        Assert.Equal(VbaCompletionExpectation.None, position.CompletionExpectation);
        Assert.Empty(position.StarterWords);
        Assert.Empty(position.SyntaxWords);
    }

    [Theory]
    [InlineData("    Call ")]
    [InlineData("    Call Ex")]
    [InlineData("    Call service.")]
    [InlineData("    Call service.Ru")]
    public void CallStatementExpectsACallableName(string statement)
    {
        var position = ProcedurePosition(statement);

        Assert.Equal(VbaCompletionExpectation.CallableName, position.CompletionExpectation);
        Assert.Empty(position.SyntaxWords);
    }

    [Theory]
    [InlineData("    Debug.", new[] { "Assert", "Print" })]
    [InlineData("    Debug.A", new[] { "Assert" })]
    [InlineData("    Debug.P", new[] { "Print" })]
    public void DebugMemberContinuationOffersOnlyDebugStatements(
        string statement,
        string[] expected)
    {
        var position = ProcedurePosition(statement);

        Assert.Equal(VbaCompletionExpectation.SyntaxWord, position.CompletionExpectation);
        Assert.Equal(expected, position.SyntaxWords);
    }

    [Theory]
    [InlineData("    Debug.Print ")]
    [InlineData("    Debug.Assert ")]
    [InlineData("    For index = 1 To ")]
    [InlineData("    For index = 1 To maximum Step ")]
    public void StatementContinuationSlotsExpectExpressionValues(string statement)
    {
        var position = ProcedurePosition(statement);

        Assert.Equal(VbaCompletionExpectation.ExpressionValue, position.CompletionExpectation);
        Assert.Empty(position.SyntaxWords);
    }

    [Theory]
    [InlineData("    TypeOf value Is ")]
    [InlineData("    If TypeOf value Is ")]
    public void TypeOfIsContinuationExpectsATypeName(string statement)
    {
        var position = ProcedurePosition(statement);

        Assert.Equal(VbaCompletionExpectation.TypeName, position.CompletionExpectation);
        Assert.Empty(position.SyntaxWords);
    }

    [Fact]
    public void QualifiedTypeOfContinuationKeepsTheTypeQualifierContext()
    {
        var position = ProcedurePosition("    If TypeOf value Is Excel.");

        Assert.Equal(VbaCompletionExpectation.TypeName, position.CompletionExpectation);
        Assert.Equal("Excel", position.TypeReference?.Qualifier?.Name);
        Assert.Null(position.TypeReference?.Name);
    }

    [Theory]
    [InlineData("If ")]
    [InlineData("ElseIf ")]
    [InlineData("While ")]
    [InlineData("Do While ")]
    [InlineData("Do Until ")]
    [InlineData("Loop While ")]
    [InlineData("Loop Until ")]
    [InlineData("For Each item In ")]
    [InlineData("Select Case ")]
    [InlineData("With ")]
    public void StatementExpressionStartsExpectValues(string expressionPrefix)
    {
        var position = ExpressionStatementPosition(expressionPrefix);

        Assert.Equal(VbaCompletionExpectation.ExpressionValue, position.CompletionExpectation);
    }

    [Theory]
    [InlineData("If ")]
    [InlineData("ElseIf ")]
    [InlineData("While ")]
    [InlineData("Do While ")]
    [InlineData("Do Until ")]
    [InlineData("Loop While ")]
    [InlineData("Loop Until ")]
    [InlineData("For Each item In ")]
    [InlineData("Select Case ")]
    [InlineData("With ")]
    public void PartialStatementExpressionsKeepExpectingValues(string expressionPrefix)
    {
        var position = ExpressionStatementPosition(expressionPrefix + "val");

        Assert.Equal(VbaCompletionExpectation.ExpressionValue, position.CompletionExpectation);
    }

    [Theory]
    [InlineData("While ")]
    [InlineData("Do While ")]
    [InlineData("Do Until ")]
    [InlineData("Loop While ")]
    [InlineData("Loop Until ")]
    [InlineData("For Each item In ")]
    [InlineData("Select Case ")]
    [InlineData("With ")]
    public void CompletedStatementExpressionsWithTrailingWhitespaceFailClosed(
        string expressionPrefix)
    {
        var position = ExpressionStatementPosition(expressionPrefix + "value ");

        Assert.Equal(VbaCompletionExpectation.None, position.CompletionExpectation);
    }

    [Fact]
    public void NestedCallParenthesisKeepsCallArgumentExpectation()
    {
        var position = ExpressionStatementPosition("If IsReady(");

        Assert.Equal(VbaCompletionExpectation.CallArgument, position.CompletionExpectation);
    }

    [Theory]
    [InlineData("Case ")]
    [InlineData("Case 1 To ")]
    [InlineData("Case 1,")]
    [InlineData("Case 1, ")]
    [InlineData("Case E")]
    public void CaseHeaderExpressionSlotsPreferExpressionValues(string caseHeader)
    {
        var position = SelectCasePosition(caseHeader);

        Assert.Equal(VbaCompletionExpectation.ExpressionValue, position.CompletionExpectation);
        Assert.Empty(position.ContextualStatements);
    }

    [Theory]
    [InlineData("Case ")]
    [InlineData("Case E")]
    [InlineData("Case El")]
    public void CaseValueSlotAlsoOffersTheElseAlternative(string caseHeader)
    {
        var position = SelectCasePosition(caseHeader);

        Assert.Equal(VbaCompletionExpectation.ExpressionValue, position.CompletionExpectation);
        Assert.Equal(["Else"], position.SupplementalSyntaxWords);
    }

    [Theory]
    [InlineData("Case 1 ")]
    [InlineData("Case Else")]
    [InlineData("Case Else ")]
    public void CompletedCaseHeaderFailsClosed(string caseHeader)
    {
        var position = SelectCasePosition(caseHeader);

        Assert.Equal(VbaCompletionExpectation.None, position.CompletionExpectation);
        Assert.Empty(position.SupplementalSyntaxWords);
    }

    [Theory]
    [InlineData("Ca", new[] { "Case", "Case Else" })]
    public void SelectBodyBranchPrefixesRemainContextual(
        string branchPrefix,
        string[] expectedStatements)
    {
        var position = SelectCasePosition(branchPrefix);

        Assert.Equal(VbaCompletionExpectation.ContextualStatement, position.CompletionExpectation);
        Assert.Equal(expectedStatements, position.ContextualStatements);
    }

    [Fact]
    public void ModuleStarterWordsRespectStandardAndObjectModuleKinds()
    {
        AssertModuleStarterWords(
            "file:///C:/work/Worker.bas",
            "",
            "Const", "Declare", "Dim", "Enum", "Function", "Global", "Option",
            "Private", "Public", "Static", "Sub", "Type");
        AssertModuleStarterWords(
            "file:///C:/work/Worker.cls",
            "",
            "Const", "Dim", "Event", "Friend", "Function", "Implements", "Option", "Private",
            "Property", "Public", "Static", "Sub", "WithEvents");
        AssertModuleStarterWords(
            "file:///C:/work/Dialog.frm",
            "",
            "Const", "Dim", "Event", "Friend", "Function", "Option", "Private",
            "Property", "Public", "Static", "Sub", "WithEvents");
    }

    [Fact]
    public void ModuleStarterWordsStopOfferingOptionAfterAProcedure()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "End Sub",
            ""
        ]);
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        var position = tree.GetPositionSyntax(3, 0);

        Assert.Equal(VbaCompletionExpectation.ModuleDeclaration, position.CompletionExpectation);
        Assert.DoesNotContain("Option", position.StarterWords);
        Assert.Contains("Public", position.StarterWords);
    }

    [Theory]
    [InlineData("file:///C:/work/Worker.bas", "Pub", "Public")]
    [InlineData("file:///C:/work/Worker.cls", "Pro", "Property")]
    [InlineData("file:///C:/work/Dialog.frm", "Fri", "Friend")]
    public void PartialLegalModuleStartersExposeOnlyMatchingWords(
        string uri,
        string partial,
        string expected)
    {
        AssertModuleStarterWords(uri, partial, expected);
    }

    [Theory]
    [InlineData("file:///C:/work/Worker.bas", "Ev")]
    [InlineData("file:///C:/work/Worker.bas", "Fri")]
    [InlineData("file:///C:/work/Worker.bas", "Pro")]
    [InlineData("file:///C:/work/Worker.bas", "Wi")]
    [InlineData("file:///C:/work/Worker.cls", "Ty")]
    [InlineData("file:///C:/work/Worker.cls", "En")]
    [InlineData("file:///C:/work/Worker.cls", "De")]
    public void IllegalModuleStartersFailClosedForTheModuleKind(
        string uri,
        string partial)
    {
        var position = ModulePosition(uri, partial);

        Assert.Equal(VbaCompletionExpectation.None, position.CompletionExpectation);
        Assert.Empty(position.StarterWords);
    }

    [Theory]
    [InlineData("Public Banana ")]
    [InlineData("Public Function ")]
    [InlineData("Public Sub ")]
    public void InvalidOrNameSlotsAfterDeclarationsFailClosed(string declaration)
    {
        const string uri = "file:///C:/work/Worker.bas";
        var source = ModuleSource(uri, declaration);
        var tree = VbaSyntaxTree.ParseModule(uri, source);
        var position = tree.GetPositionSyntax(1, declaration.Length);

        Assert.Equal(VbaCompletionExpectation.None, position.CompletionExpectation);
        Assert.Empty(position.SyntaxWords);
    }

    [Theory]
    [InlineData("file:///C:/work/Worker.bas", "Friend ")]
    [InlineData("file:///C:/work/Worker.bas", "Property ")]
    [InlineData("file:///C:/work/Worker.cls", "Declare ")]
    [InlineData("file:///C:/work/Worker.cls", "Public Declare ")]
    [InlineData("file:///C:/work/Worker.cls", "Public Property Get ")]
    public void ModuleSpecificInvalidDeclarationContinuationsFailClosed(
        string uri,
        string declaration)
    {
        var source = ModuleSource(uri, declaration);
        var position = VbaSyntaxTree
            .ParseModule(uri, source)
            .GetPositionSyntax(source.Count(character => character == '\n'), declaration.Length);

        Assert.Equal(VbaCompletionExpectation.None, position.CompletionExpectation);
        Assert.Empty(position.SyntaxWords);
    }

    [Theory]
    [InlineData("    Public ")]
    [InlineData("    Call Existing ")]
    [InlineData("    Debug.Print value ")]
    public void InvalidOrCompletedProcedureStatementsFailClosed(string statement)
    {
        var position = ProcedurePosition(statement);

        Assert.Equal(VbaCompletionExpectation.None, position.CompletionExpectation);
        Assert.Empty(position.SyntaxWords);
    }

    private static void AssertDeclarationContinuation(
        string uri,
        string declaration,
        params string[] expected)
    {
        var source = ModuleSource(uri, declaration);
        var tree = VbaSyntaxTree.ParseModule(uri, source);
        var position = tree.GetPositionSyntax(
            source.Count(character => character == '\n'),
            declaration.Length);

        Assert.Equal(VbaCompletionExpectation.SyntaxWord, position.CompletionExpectation);
        Assert.Equal(expected, position.SyntaxWords);
        Assert.Empty(position.ContextualStatements);
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

    private static VbaPositionSyntax ExpressionStatementPosition(string statementText)
    {
        var statement = "    " + statementText;
        IReadOnlyList<string> body = statementText.StartsWith("ElseIf", StringComparison.Ordinal)
            ? ["    If ready Then", statement, "    End If"]
            : statementText.StartsWith("Loop", StringComparison.Ordinal)
                ? ["    Do", statement]
                : statementText.StartsWith("While", StringComparison.Ordinal)
                    ? [statement, "    Wend"]
                    : statementText.StartsWith("Do", StringComparison.Ordinal)
                        ? [statement, "    Loop"]
                        : statementText.StartsWith("For", StringComparison.Ordinal)
                            ? [statement, "    Next"]
                            : statementText.StartsWith("Select", StringComparison.Ordinal)
                                ? [statement, "    End Select"]
                                : statementText.StartsWith("With", StringComparison.Ordinal)
                                    ? [statement, "    End With"]
                                    : [statement];
        var lines = new List<string>
        {
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()"
        };
        lines.AddRange(body);
        lines.Add("End Sub");
        var line = lines.IndexOf(statement);
        var source = string.Join('\n', lines);
        return VbaSyntaxTree
            .ParseModule("file:///C:/work/Worker.bas", source)
            .GetPositionSyntax(line, statement.Length);
    }

    private static VbaPositionSyntax SelectCasePosition(string statementText)
    {
        var statement = "        " + statementText;
        var lines = new[]
        {
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    Select Case selector",
            statement,
            "    End Select",
            "End Sub"
        };
        return VbaSyntaxTree
            .ParseModule("file:///C:/work/Worker.bas", string.Join('\n', lines))
            .GetPositionSyntax(3, statement.Length);
    }

    private static void AssertModuleStarterWords(
        string uri,
        string partial,
        params string[] expected)
    {
        var position = ModulePosition(uri, partial);

        Assert.Equal(VbaCompletionExpectation.ModuleDeclaration, position.CompletionExpectation);
        Assert.Equal(expected, position.StarterWords);
    }

    private static VbaPositionSyntax ModulePosition(string uri, string declaration)
    {
        var source = ModuleSource(uri, declaration);
        return VbaSyntaxTree
            .ParseModule(uri, source)
            .GetPositionSyntax(source.Count(character => character == '\n'), declaration.Length);
    }

    private static string ModuleSource(string uri, string declaration)
        => uri.EndsWith(".bas", StringComparison.OrdinalIgnoreCase)
            ? string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                declaration
            ])
            : string.Join('\n', [
                uri.EndsWith(".cls", StringComparison.OrdinalIgnoreCase)
                    ? "VERSION 1.0 CLASS"
                    : "VERSION 5.00",
                "Attribute VB_Name = \"Worker\"",
                declaration
            ]);

    private static string Slice(string source, VbaSyntaxRange range)
        => source[range.Start.Offset..range.End.Offset];
}
