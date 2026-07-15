using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaCompletionCandidateResolutionTests
{
    private const string MainUri = "file:///C:/work/Main.bas";
    private const string LegacyReferenceName = "Legacy Library";

    [Fact]
    public void OperatorWhitespaceDoesNotChangeCandidatesAndCompletedExpressionIsClosed()
    {
        var afterOperator = Complete(BasicExpressionSource("    value = ReadValue +|"));
        var afterOperatorSpace = Complete(BasicExpressionSource("    value = ReadValue + |"));
        var afterCompletedCall = Complete(BasicExpressionSource("    value = ReadValue() |"));

        Assert.NotEmpty(afterOperator.Candidates);
        Assert.Equal(afterOperator.Candidates, afterOperatorSpace.Candidates);
        Assert.Empty(afterCompletedCall.Candidates);
    }

    [Fact]
    public void ExpressionAndAssignmentContextsFilterReadableAndWritableDefinitions()
    {
        var expression = Complete(PropertyContextSource("    result = |"), LegacyReference());
        var assignment = Complete(PropertyContextSource("    M| = 1"), LegacyReference());
        var universe = new[]
        {
            "FixedValue",
            "LegacyValue",
            "MutableValue",
            "ReadOnly",
            "ReadValue",
            "ReadWrite",
            "RunTask",
            "WriteOnly"
        };

        Assert.Equal(
            ["FixedValue", "MutableValue", "ReadOnly", "ReadValue", "ReadWrite"],
            RelevantDefinitionLabels(expression, universe));
        Assert.Equal(
            ["MutableValue", "ReadWrite", "WriteOnly"],
            RelevantDefinitionLabels(assignment, universe));
    }

    [Fact]
    public void MemberPropertiesRespectReadableWritableAndFailClosedUnknownCapabilities()
    {
        const string itemUri = "file:///C:/work/Item.cls";
        var sources = new Dictionary<string, string>
        {
            [itemUri] = ItemClassSource()
        };
        var expression = Complete(
            MemberPropertySource("    result = item.|"),
            LegacyReference(),
            sources);
        var assignment = Complete(
            MemberPropertySource("    item.| = 1"),
            LegacyReference(),
            sources);
        var legacyExpression = Complete(
            MemberPropertySource("    result = legacy.|"),
            LegacyReference(),
            sources);
        var legacyAssignment = Complete(
            MemberPropertySource("    legacy.| = 1"),
            LegacyReference(),
            sources);
        var sourceMembers = new[] { "ReadMember", "ReadWriteMember", "WriteMember" };

        Assert.Equal(
            ["ReadMember", "ReadWriteMember"],
            RelevantDefinitionLabels(expression, sourceMembers));
        Assert.Equal(
            ["ReadWriteMember", "WriteMember"],
            RelevantDefinitionLabels(assignment, sourceMembers));
        Assert.Empty(DefinitionLabels(legacyExpression));
        Assert.Empty(DefinitionLabels(legacyAssignment));
    }

    [Fact]
    public void StatementMemberCompletionKeepsReadablePropertiesAvailableForChaining()
    {
        const string itemUri = "file:///C:/work/Item.cls";
        var result = Complete(
            MemberPropertySource("    item.|"),
            LegacyReference(),
            new Dictionary<string, string>
            {
                [itemUri] = ItemClassSource()
            });

        Assert.Equal(
            ["ReadMember", "ReadWriteMember", "WriteMember"],
            RelevantDefinitionLabels(
                result,
                ["ReadMember", "ReadWriteMember", "WriteMember"]));
    }

    [Fact]
    public void WriteOnlyPropertyCannotSupplyCallArguments()
    {
        var result = Complete(PropertyContextSource(
            "    result = WriteOnly(|)"));

        Assert.Empty(result.Candidates);
    }

    [Theory]
    [InlineData("Let", "Long", "    Item(|) = 42")]
    [InlineData("Set", "Object", "    Set Item(|) = Nothing")]
    public void IndexedWriteOnlyPropertyAssignmentOffersIndexArguments(
        string accessorKind,
        string valueType,
        string assignment)
    {
        var result = Complete(string.Join('\n', [
            "Attribute VB_Name = \"Main\"",
            $"Public Property {accessorKind} Item(ByVal Index As Long, ByVal AssignedValue As {valueType})",
            "End Property",
            "Public Sub Probe()",
            "    Dim LocalIndex As Long",
            assignment,
            "End Sub"
        ]));

        Assert.Contains(result.Candidates, candidate =>
            candidate.Kind == VbaCompletionCandidateKind.Definition
            && candidate.Label == "LocalIndex");
        AssertNamedCandidates(result, "Index");
        Assert.DoesNotContain(result.Candidates, candidate =>
            candidate.Kind == VbaCompletionCandidateKind.NamedArgument
            && candidate.Label == "AssignedValue");
    }

    [Theory]
    [InlineData("    result = Item(|) = 1")]
    [InlineData("    If Item(|) = 1 Then")]
    public void IndexedWriteOnlyPropertyValueExpressionDoesNotOfferArguments(string statement)
    {
        var result = Complete(string.Join('\n', [
            "Attribute VB_Name = \"Main\"",
            "Public Property Let Item(ByVal Index As Long, ByVal AssignedValue As Long)",
            "End Property",
            "Public Sub Probe()",
            "    Dim result As Boolean",
            statement,
            "End Sub"
        ]));

        Assert.Empty(result.Candidates);
    }

    [Theory]
    [InlineData("Private Values(10) As Long", "Public Sub Probe()", "")]
    [InlineData("", "Public Sub Probe()", "    Dim Values() As Long")]
    [InlineData("", "Public Sub Probe(ByRef Values() As Long)", "")]
    public void ExplicitArraysOfferIndexExpressionCandidates(
        string moduleDeclaration,
        string procedureDeclaration,
        string localDeclaration)
    {
        var result = Complete(string.Join('\n', [
            "Attribute VB_Name = \"Main\"",
            moduleDeclaration,
            procedureDeclaration,
            localDeclaration,
            "    Dim LocalIndex As Long",
            "    Dim result As Long",
            "    result = Values(|)",
            "End Sub"
        ]));

        Assert.Contains(result.Candidates, candidate =>
            candidate.Kind == VbaCompletionCandidateKind.Definition
            && candidate.Label == "LocalIndex");
        Assert.DoesNotContain(result.Candidates, candidate =>
            candidate.Kind == VbaCompletionCandidateKind.NamedArgument);
    }

    [Fact]
    public void ScalarParenthesizedAccessDoesNotOfferIndexExpressionCandidates()
    {
        var result = Complete(string.Join('\n', [
            "Attribute VB_Name = \"Main\"",
            "Public Sub Probe()",
            "    Dim Value As Long",
            "    Dim LocalIndex As Long",
            "    Dim result As Long",
            "    result = Value(|)",
            "End Sub"
        ]));

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void TypeAndNewContextsSeparateVisibleAndCreatableTypes()
    {
        const string classUri = "file:///C:/work/Worker.cls";
        const string formUri = "file:///C:/work/Dialog.frm";
        var sources = new Dictionary<string, string>
        {
            [classUri] = string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"Worker\"",
                "Option Explicit"
            ]),
            [formUri] = string.Join('\n', [
                "VERSION 5.00",
                "Begin VB.Form Dialog",
                "End",
                "Attribute VB_Name = \"Dialog\"",
                "Option Explicit"
            ])
        };
        var typeName = Complete(TypeContextSource("    Dim value As |"), TypeReference(), sources);
        var creatable = Complete(TypeContextSource("    Set value = New |"), TypeReference(), sources);
        var universe = new[] { "Dialog", "IWidget", "Long", "Point", "State", "Widget", "Worker" };

        Assert.Equal(universe, RelevantLabels(typeName, universe));
        Assert.Equal(["Dialog", "Widget", "Worker"], RelevantLabels(creatable, universe));
    }

    [Fact]
    public void QualifiedTypeContextsOnlyUseTheAddressedReference()
    {
        var typeName = Complete(
            TypeContextSource("    Dim value As Legacy.|"),
            TypeReference());
        var creatable = Complete(
            TypeContextSource("    Set value = New Legacy.|"),
            TypeReference());
        var universe = new[] { "IWidget", "Long", "Point", "State", "Widget" };

        Assert.Equal(["IWidget", "Widget"], RelevantLabels(typeName, universe));
        Assert.Equal(["Widget"], RelevantLabels(creatable, universe));
    }

    [Fact]
    public void ImplementsOffersOnlyOtherSourceAndReferenceClasses()
    {
        const string workerUri = "file:///C:/work/Worker.cls";
        const string contractUri = "file:///C:/work/Contract.cls";
        const string formUri = "file:///C:/work/Dialog.frm";
        const string moduleUri = "file:///C:/work/Helpers.bas";
        var source = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Implements |"
        ]);
        var additionalSources = new Dictionary<string, string>
        {
            [contractUri] = string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"Contract\""
            ]),
            [formUri] = string.Join('\n', [
                "VERSION 5.00",
                "Attribute VB_Name = \"Dialog\""
            ]),
            [moduleUri] = string.Join('\n', [
                "Attribute VB_Name = \"Helpers\"",
                "Public Enum State",
                "    Ready",
                "End Enum",
                "Public Type Point",
                "    X As Long",
                "End Type"
            ])
        };
        var reference = CreateReference(
            new VbaProjectReferenceDefinition(
                LegacyReferenceName,
                "IWidget",
                VbaSourceDefinitionKind.Class),
            new VbaProjectReferenceDefinition(
                LegacyReferenceName,
                "Widget",
                VbaSourceDefinitionKind.Class,
                IsCreatable: true),
            new VbaProjectReferenceDefinition(
                LegacyReferenceName,
                "ReferenceState",
                VbaSourceDefinitionKind.Enum),
            new VbaProjectReferenceDefinition(
                LegacyReferenceName,
                "ReferencePoint",
                VbaSourceDefinitionKind.Type));

        var result = Complete(
            source,
            reference,
            additionalSources,
            workerUri);
        var universe = new[]
        {
            "Contract",
            "Dialog",
            "Helpers",
            "IWidget",
            "Point",
            "ReferencePoint",
            "ReferenceState",
            "State",
            "String",
            "Widget",
            "Worker"
        };

        Assert.Equal(
            ["Contract", "IWidget", "Widget"],
            RelevantLabels(result, universe));
    }

    [Fact]
    public void QualifiedImplementsOnlyUsesTheAddressedReference()
    {
        const string workerUri = "file:///C:/work/Worker.cls";
        var result = Complete(
            string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"Worker\"",
                "Implements Legacy.|"
            ]),
            TypeReference(),
            mainUri: workerUri);

        Assert.Equal(
            ["IWidget", "Widget"],
            RelevantLabels(result, ["IWidget", "Long", "Widget"]));
    }

    [Theory]
    [InlineData("    result = ExampleFunc(1, |)")]
    [InlineData("    ExampleSub 1, |")]
    public void PositionalArgumentsOfferOnlyLaterParametersByName(string callLine)
    {
        var result = Complete(CallContextSource(callLine));

        AssertNamedCandidates(result, "Arg2", "Arg3");
    }

    [Theory]
    [InlineData("    result = ExampleFunc(Arg2:=True, |)")]
    [InlineData("    ExampleSub Arg2:=True, |")]
    public void NamedArgumentsDisablePositionsButLeaveUnusedNames(string callLine)
    {
        var result = Complete(CallContextSource(callLine));

        AssertNamedCandidates(result, "Arg1", "Arg3");
        Assert.All(result.Candidates, candidate =>
            Assert.Equal(VbaCompletionCandidateKind.NamedArgument, candidate.Kind));
    }

    [Theory]
    [InlineData("    result = ExampleFunc(Arg1:=1, Arg1:=|)")]
    [InlineData("    ExampleSub Arg2:=True, 1, Arg3:=|")]
    public void InvalidPriorArgumentsSuppressNamedArgumentValueCandidates(string callLine)
    {
        Assert.Empty(Complete(CallContextSource(callLine)).Candidates);
    }

    [Theory]
    [InlineData("    ExampleSub Arg2:=True, 1 + |")]
    [InlineData("    result = ExampleFunc(1, True, False, 1 + |)")]
    public void InvalidCallArgumentExpressionContinuationRemainsClosed(string callLine)
    {
        var result = Complete(CallContextSource(callLine));

        Assert.Empty(result.Candidates);
    }

    [Theory]
    [InlineData("    result = ExampleFunc(1, True, False, New |)")]
    [InlineData("    result = ExampleFunc(Bad:=New |)")]
    [InlineData("    result = ExampleFunc(Arg1:=1, Arg1:=New |)")]
    public void InvalidCallArgumentCreatableTypeContinuationRemainsClosed(string callLine)
    {
        var result = Complete(CallContextSource(callLine), TypeReference());

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void ValidCallArgumentCreatableTypeContinuationRetainsTypes()
    {
        var result = Complete(
            CallContextSource("    result = ExampleFunc(New |)"),
            TypeReference());

        Assert.Contains(result.Candidates, candidate => candidate.Label == "Widget");
    }

    [Fact]
    public void ValidCallArgumentTypeOfContinuationRetainsTypes()
    {
        var result = Complete(CallContextSource(
            "    result = ExampleFunc(TypeOf LocalValue Is |)"));

        Assert.Contains(result.Candidates, candidate => candidate.Label == "String");
        Assert.DoesNotContain(result.Candidates, candidate => candidate.Label == "LocalValue");
    }

    [Fact]
    public void ExhaustedCallArgumentMemberContinuationRemainsClosed()
    {
        const string thingUri = "file:///C:/work/Thing.cls";
        var result = Complete(
            MemberCallSource("    result = thing.Transform(1, 2, thing.|)"),
            additionalSources: new Dictionary<string, string>
            {
                [thingUri] = ThingClassSource()
            });

        Assert.Empty(result.Candidates);
    }

    [Theory]
    [InlineData("    result = CollectValues(\"prefix\", 1, |)")]
    [InlineData("    Collect \"prefix\", 1, |")]
    public void ParamArrayKeepsPositionalExpressionsButIsNeverNamed(string callLine)
    {
        var result = Complete(CallContextSource(callLine));

        Assert.DoesNotContain(result.Candidates, candidate =>
            candidate.Kind == VbaCompletionCandidateKind.NamedArgument);
        Assert.Contains(result.Candidates, candidate =>
            candidate.Kind == VbaCompletionCandidateKind.Definition
            && candidate.Label == "LocalValue");
    }

    [Theory]
    [InlineData("    result = ExampleFunc(1, True, False, |)")]
    [InlineData("    ExampleSub 1, True, False, |")]
    public void ExhaustedCallsHaveNoCandidates(string callLine)
    {
        Assert.Empty(Complete(CallContextSource(callLine)).Candidates);
    }

    [Fact]
    public void UnresolvedCallOffersPositionalExpressionsOnly()
    {
        var result = Complete(CallContextSource("    result = Missing(|)"));

        Assert.DoesNotContain(result.Candidates, candidate =>
            candidate.Kind == VbaCompletionCandidateKind.NamedArgument);
        Assert.Contains(result.Candidates, candidate =>
            candidate.Kind == VbaCompletionCandidateKind.Definition
            && candidate.Label == "LocalValue");
        Assert.Contains(result.Candidates, candidate =>
            candidate.Kind == VbaCompletionCandidateKind.LanguageVocabulary
            && candidate.Label == "True");
    }

    [Fact]
    public void RaiseEventArgumentsOfferValuesButNeverNamedParameters()
    {
        var result = Complete(CallContextSource("    RaiseEvent Saved(|)"));

        Assert.DoesNotContain(result.Candidates, candidate =>
            candidate.Kind == VbaCompletionCandidateKind.NamedArgument);
        Assert.Contains(result.Candidates, candidate =>
            candidate.Kind == VbaCompletionCandidateKind.Definition
            && candidate.Label == "LocalValue");
    }

    [Fact]
    public void RaiseEventNameCompletionUsesOnlyEventsFromTheCurrentModule()
    {
        const string otherUri = "file:///C:/work/Other.cls";
        var result = Complete(
            EventAndLabelSource("    RaiseEvent |"),
            additionalSources: new Dictionary<string, string>
            {
                [otherUri] = string.Join('\n', [
                    "VERSION 1.0 CLASS",
                    "Attribute VB_Name = \"Other\"",
                    "Public Event ForeignEvent()"
                ])
            });

        Assert.Equal(["Saved", "Updated"], DefinitionLabels(result));
        Assert.All(result.Candidates, candidate =>
            Assert.Equal(VbaCompletionCandidateKind.Definition, candidate.Kind));
    }

    [Theory]
    [InlineData("    GoTo |", new[] { "100", "StartHere" })]
    [InlineData("    Resume |", new[] { "100", "Next", "StartHere" })]
    [InlineData("    On Error GoTo |", new[] { "0", "100", "StartHere" })]
    [InlineData("    On Error Resume |", new[] { "Next" })]
    public void LabelCompletionUsesCurrentProcedureAndSyntaxDestinations(
        string statement,
        string[] expected)
    {
        var result = Complete(EventAndLabelSource(statement));

        Assert.Equal(expected, result.Candidates.Select(candidate => candidate.Label));
        Assert.All(result.Candidates, candidate =>
            Assert.Equal(VbaCompletionCandidateKind.Label, candidate.Kind));
        Assert.DoesNotContain(result.Candidates, candidate => candidate.Label == "OtherLabel");
    }

    [Fact]
    public void InnermostBlockAndElseBranchControlContextualStatements()
    {
        var nested = Complete(string.Join('\n', [
            "Attribute VB_Name = \"Main\"",
            "Public Sub Run()",
            "    If True Then",
            "        For index = 1 To 2",
            "            |",
            "        Next",
            "    End If",
            "End Sub"
        ]));
        var elseBranch = Complete(string.Join('\n', [
            "Attribute VB_Name = \"Main\"",
            "Public Sub Run()",
            "    If True Then",
            "    Else",
            "        |",
            "    End If",
            "End Sub"
        ]));

        Assert.Equal(["Next"], ContextualLabels(nested));
        Assert.Equal(["End If"], ContextualLabels(elseBranch));
    }

    [Fact]
    public void EndPrefixOnlyOffersTheInnermostCloserWithAWholePrefixEdit()
    {
        var result = Complete(string.Join('\n', [
            "Attribute VB_Name = \"Main\"",
            "Public Sub Run()",
            "    If True Then",
            "        End |",
            "    End If",
            "End Sub"
        ]));
        var candidate = Assert.Single(result.Candidates);

        Assert.Equal("End If", candidate.Label);
        Assert.Equal(VbaCompletionCandidateKind.ContextualStatement, candidate.Kind);
        Assert.Equal("End If", candidate.TextEdit?.NewText);
        Assert.Equal(8, candidate.TextEdit?.Range.Start.Character);
        Assert.Equal(12, candidate.TextEdit?.Range.End.Character);
    }

    [Fact]
    public void SelectCaseCombinesExpressionAndElseAlternativesAndCaseElseIsTerminal()
    {
        var partial = Complete(string.Join('\n', [
            "Attribute VB_Name = \"Main\"",
            "Public Sub Run()",
            "    Dim Elapsed As Long",
            "    Select Case 1",
            "        Case E|",
            "    End Select",
            "End Sub"
        ]));
        var terminal = Complete(string.Join('\n', [
            "Attribute VB_Name = \"Main\"",
            "Public Sub Run()",
            "    Select Case 1",
            "        Case Else",
            "            |",
            "    End Select",
            "End Sub"
        ]));
        var elseCandidate = Assert.Single(partial.Candidates, candidate => candidate.Label == "Else");
        var valueCandidate = Assert.Single(partial.Candidates, candidate => candidate.Label == "Elapsed");

        Assert.Equal("Else", elseCandidate.TextEdit?.NewText);
        Assert.Equal(13, elseCandidate.TextEdit?.Range.Start.Character);
        Assert.Equal(14, elseCandidate.TextEdit?.Range.End.Character);
        Assert.Equal("Elapsed", valueCandidate.TextEdit?.NewText);
        Assert.Equal(elseCandidate.TextEdit?.Range, valueCandidate.TextEdit?.Range);
        Assert.Equal(["End Select"], ContextualLabels(terminal));
    }

    [Fact]
    public void CaseCommaWhitespaceDoesNotChangeExpressionCandidates()
    {
        static string Source(string caseLine)
            => string.Join('\n', [
                "Attribute VB_Name = \"Main\"",
                "Public Sub Run()",
                "    Dim LocalValue As Long",
                "    Select Case LocalValue",
                caseLine,
                "    End Select",
                "End Sub"
            ]);

        var afterComma = Complete(Source("        Case 1,|"));
        var afterCommaSpace = Complete(Source("        Case 1, |"));

        Assert.NotEmpty(afterComma.Candidates);
        Assert.Equal(afterComma.Candidates, afterCommaSpace.Candidates);
        Assert.Contains(afterComma.Candidates, candidate => candidate.Label == "LocalValue");
    }

    [Fact]
    public void OnSelectorCombinesExpressionAndErrorThenRequiresADispatchWord()
    {
        var selector = Complete(string.Join('\n', [
            "Attribute VB_Name = \"Main\"",
            "Public Sub Run()",
            "    Dim selector As Long",
            "    On |",
            "End Sub"
        ]));
        var dispatch = Complete(string.Join('\n', [
            "Attribute VB_Name = \"Main\"",
            "Public Sub Run()",
            "    Dim selector As Long",
            "    On selector |",
            "End Sub"
        ]));

        Assert.Contains(selector.Candidates, candidate => candidate.Label == "selector");
        Assert.Contains(selector.Candidates, candidate => candidate.Label == "Error");
        Assert.Equal(["GoSub", "GoTo"], dispatch.Candidates.Select(candidate => candidate.Label));
    }

    [Theory]
    [InlineData("    If (|")]
    [InlineData("    If (True And |")]
    [InlineData("    While (|")]
    public void GroupedConditionsOfferExpressionCandidates(string statement)
    {
        var result = Complete(string.Join('\n', [
            "Attribute VB_Name = \"Main\"",
            "Public Sub Run()",
            "    Dim LocalValue As Boolean",
            statement,
            "End Sub"
        ]));

        Assert.Contains(result.Candidates, candidate =>
            candidate.Kind == VbaCompletionCandidateKind.Definition
            && candidate.Label == "LocalValue");
        Assert.Contains(result.Candidates, candidate =>
            candidate.Kind == VbaCompletionCandidateKind.LanguageVocabulary
            && candidate.Label == "True");
    }

    [Fact]
    public void IntrinsicKeywordCallsUseResolvedArgumentAvailability()
    {
        var intrinsics = IntrinsicKeywordSources();
        var stringCall = Complete(
            IntrinsicKeywordCallSource("    result = String(|)"),
            additionalSources: intrinsics);
        var dateCall = Complete(
            IntrinsicKeywordCallSource("    result = Date(|)"),
            additionalSources: intrinsics);

        Assert.Contains(stringCall.Candidates, candidate => candidate.Label == "LocalValue");
        Assert.Contains(stringCall.Candidates, candidate => candidate.Label == "Number");
        Assert.Contains(stringCall.Candidates, candidate => candidate.Label == "Character");
        Assert.Empty(dateCall.Candidates);
    }

    [Theory]
    [InlineData("    result = String(1, 2, 3 + |)")]
    [InlineData("    result = Date(1 + |)")]
    public void IntrinsicKeywordCallOperandsCannotBypassResolvedArity(string statement)
    {
        var result = Complete(
            IntrinsicKeywordCallSource(statement),
            additionalSources: IntrinsicKeywordSources());

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void ExpressionVocabularyKeepsMeInsideObjectModules()
    {
        var standard = Complete(string.Join('\n', [
            "Attribute VB_Name = \"Main\"",
            "Public Sub Run()",
            "    If | Then",
            "End Sub"
        ]));
        var objectModule = Complete(
            string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"Worker\"",
                "Public Sub Run()",
                "    If | Then",
                "End Sub"
            ]),
            mainUri: "file:///C:/work/Worker.cls");

        Assert.Contains(standard.Candidates, candidate => candidate.Label == "Empty");
        Assert.Contains(standard.Candidates, candidate => candidate.Label == "Null");
        Assert.DoesNotContain(standard.Candidates, candidate => candidate.Label == "Me");
        Assert.Contains(objectModule.Candidates, candidate => candidate.Label == "Empty");
        Assert.Contains(objectModule.Candidates, candidate => candidate.Label == "Null");
        Assert.Contains(objectModule.Candidates, candidate => candidate.Label == "Me");
    }

    [Theory]
    [InlineData("    Debug.Print |")]
    [InlineData("    Debug.Assert |")]
    [InlineData("    For index = 1 To |")]
    [InlineData("    For index = 1 To 10 Step |")]
    public void StatementExpressionContinuationsOfferReadableValues(string statement)
    {
        var result = Complete(string.Join('\n', [
            "Attribute VB_Name = \"Main\"",
            "Public Sub Run()",
            "    Dim index As Long",
            "    Dim LocalValue As Long",
            statement,
            "End Sub"
        ]));

        Assert.Contains(result.Candidates, candidate => candidate.Label == "LocalValue");
        Assert.Contains(result.Candidates, candidate => candidate.Label == "True");
    }

    [Theory]
    [InlineData("    Debug.|", new[] { "Assert", "Print" })]
    [InlineData("    Debug.A|", new[] { "Assert" })]
    [InlineData("    Debug.P|", new[] { "Print" })]
    public void DebugMemberContinuationOffersOnlyDebugSyntax(
        string statement,
        string[] expected)
    {
        var result = Complete(string.Join('\n', [
            "Attribute VB_Name = \"Main\"",
            "Public Sub Run()",
            statement,
            "End Sub"
        ]));

        Assert.Equal(expected, result.Candidates.Select(candidate => candidate.Label));
        Assert.All(result.Candidates, candidate =>
            Assert.Equal(VbaCompletionCandidateKind.LanguageVocabulary, candidate.Kind));
    }

    [Theory]
    [InlineData("    Dim value As |")]
    [InlineData("    Dim value As N|")]
    public void VariableTypeAnnotationOffersNewAlongsideTypes(string statement)
    {
        var result = Complete(string.Join('\n', [
            "Attribute VB_Name = \"Main\"",
            "Public Sub Run()",
            statement,
            "End Sub"
        ]));

        Assert.Contains(result.Candidates, candidate => candidate.Label == "Long");
        Assert.Contains(result.Candidates, candidate =>
            candidate.Kind == VbaCompletionCandidateKind.LanguageVocabulary
            && candidate.Label == "New");
    }

    [Fact]
    public void TypeOfIsContinuationOffersOnlyTypeCandidates()
    {
        var result = Complete(string.Join('\n', [
            "Attribute VB_Name = \"Main\"",
            "Public Sub Run()",
            "    Dim value As Object",
            "    If TypeOf value Is |",
            "End Sub"
        ]));

        Assert.Contains(result.Candidates, candidate => candidate.Label == "String");
        Assert.Contains(result.Candidates, candidate => candidate.Label == "Long");
        Assert.DoesNotContain(result.Candidates, candidate => candidate.Label == "value");
    }

    [Fact]
    public void CallContinuationOffersOnlyCallableDefinitions()
    {
        var unqualified = Complete(PropertyContextSource("    Call |"));
        const string thingUri = "file:///C:/work/Thing.cls";
        var member = Complete(
            MemberCallSource("    Call thing.|"),
            additionalSources: new Dictionary<string, string>
            {
                [thingUri] = ThingClassSource()
            });
        var unqualifiedUniverse = new[]
        {
            "FixedValue", "MutableValue", "ReadOnly", "ReadValue", "ReadWrite", "RunTask", "WriteOnly"
        };

        Assert.Equal(
            ["ReadValue", "RunTask"],
            RelevantDefinitionLabels(unqualified, unqualifiedUniverse));
        Assert.All(unqualified.Candidates, candidate =>
            Assert.Equal(VbaCompletionCandidateKind.Definition, candidate.Kind));
        Assert.Equal(
            ["Transform"],
            RelevantDefinitionLabels(member, ["ReadMember", "Transform", "WriteMember"]));
    }

    [Fact]
    public void DeclarationContinuationProjectsOnlySyntaxOwnedWords()
    {
        var result = Complete(string.Join('\n', [
            "Attribute VB_Name = \"Main\"",
            "Public |"
        ]));

        Assert.NotEmpty(result.Candidates);
        Assert.All(result.Candidates, candidate =>
            Assert.Equal(VbaCompletionCandidateKind.LanguageVocabulary, candidate.Kind));
        Assert.Contains(result.Candidates, candidate => candidate.Label == "Sub");
        Assert.DoesNotContain(result.Candidates, candidate => candidate.Label == "If");
        Assert.DoesNotContain(result.Candidates, candidate => candidate.Label == "String");
    }

    [Theory]
    [InlineData("Enum", "End Enum")]
    [InlineData("Type", "End Type")]
    public void ModuleBlockTerminatorReplacesIncompleteEndStatement(
        string blockKind,
        string expectedTerminator)
    {
        var result = Complete(string.Join('\n', [
            "Attribute VB_Name = \"Main\"",
            $"Public {blockKind} Example{blockKind}",
            "    Value",
            "    End|",
            $"End {blockKind}"
        ]));
        var candidate = Assert.Single(result.Candidates);

        Assert.Equal(expectedTerminator, candidate.Label);
        Assert.Equal(VbaCompletionCandidateKind.ContextualStatement, candidate.Kind);
        Assert.Equal(expectedTerminator, candidate.TextEdit?.NewText);
        Assert.Equal(4, candidate.TextEdit?.Range.Start.Character);
        Assert.Equal(7, candidate.TextEdit?.Range.End.Character);
    }

    [Fact]
    public void MemberCalleeDoesNotHideCallArgumentsAndNamedValueUsesMemberCompletion()
    {
        const string thingUri = "file:///C:/work/Thing.cls";
        var sources = new Dictionary<string, string>
        {
            [thingUri] = ThingClassSource()
        };
        var callArguments = Complete(
            MemberCallSource("    result = thing.Transform(|)"),
            additionalSources: sources);
        var namedMemberValue = Complete(
            MemberCallSource("    result = thing.Transform(Arg:=thing.|)"),
            additionalSources: sources);
        var memberUniverse = new[] { "ReadMember", "Transform", "WriteMember" };

        AssertNamedCandidates(callArguments, "Arg", "OptionalArg");
        Assert.Contains(callArguments.Candidates, candidate =>
            candidate.Kind == VbaCompletionCandidateKind.Definition
            && candidate.Label == "thing");
        Assert.Contains(callArguments.Candidates, candidate =>
            candidate.Kind == VbaCompletionCandidateKind.LanguageVocabulary
            && candidate.Label == "True");
        Assert.Equal(
            ["ReadMember", "Transform"],
            RelevantDefinitionLabels(namedMemberValue, memberUniverse));
        Assert.DoesNotContain(namedMemberValue.Candidates, candidate =>
            candidate.Kind == VbaCompletionCandidateKind.NamedArgument);
    }

    [Fact]
    public void FunctionAndPropertyGetExposeOnlyTheirOwnResultAssignmentTargets()
    {
        var functionResult = Complete(ResultTargetSource(
            "Public Function Calculate() As Long",
            "    Calc| = 1",
            "End Function"));
        var propertyGetResult = Complete(ResultTargetSource(
            "Public Property Get CurrentValue() As Long",
            "    Current| = 1",
            "End Property"));
        var propertyLetResult = Complete(ResultTargetSource(
            "Public Property Let CurrentValue(ByVal AssignedValue As Long)",
            "    Current| = 1",
            "End Property"));
        var propertySetResult = Complete(ResultTargetSource(
            "Public Property Set CurrentValue(ByVal AssignedValue As Object)",
            "    Current| = Nothing",
            "End Property"));

        Assert.Contains(functionResult.Candidates, candidate => candidate.Label == "Calculate");
        Assert.Contains(propertyGetResult.Candidates, candidate => candidate.Label == "CurrentValue");
        Assert.DoesNotContain(propertyLetResult.Candidates, candidate => candidate.Label == "CurrentValue");
        Assert.DoesNotContain(propertySetResult.Candidates, candidate => candidate.Label == "CurrentValue");
    }

    private static VbaCompletionResult Complete(
        string markedSource,
        ReferenceFixture? reference = null,
        IReadOnlyDictionary<string, string>? additionalSources = null,
        string mainUri = MainUri)
    {
        var markerOffset = markedSource.IndexOf('|');
        Assert.True(markerOffset >= 0, "The source must contain one completion marker.");
        Assert.Equal(markerOffset, markedSource.LastIndexOf('|'));
        var prefix = markedSource[..markerOffset];
        var line = prefix.Count(character => character == '\n');
        var lineStart = prefix.LastIndexOf('\n');
        var character = markerOffset - lineStart - 1;
        var source = markedSource.Remove(markerOffset, 1);
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [mainUri] = source
        };
        if (additionalSources is not null)
        {
            foreach (var entry in additionalSources)
            {
                sources.Add(entry.Key, entry.Value);
            }
        }

        var index = VbaSourceIndex.Build(
            sources,
            reference?.Selection,
            reference?.Catalogs);
        return index.GetCompletionResult(mainUri, line, character);
    }

    private static string BasicExpressionSource(string statement)
        => string.Join('\n', [
            "Attribute VB_Name = \"Main\"",
            "Private Function ReadValue() As Long",
            "End Function",
            "Public Sub Run()",
            "    Dim value As Long",
            statement,
            "End Sub"
        ]);

    private static string IntrinsicKeywordCallSource(string statement)
        => string.Join('\n', [
            "Attribute VB_Name = \"Main\"",
            "Public Sub Run()",
            "    Dim result As Variant",
            "    Dim LocalValue As Long",
            statement,
            "End Sub"
        ]);

    private static IReadOnlyDictionary<string, string> IntrinsicKeywordSources()
        => new Dictionary<string, string>
        {
            ["file:///C:/work/Intrinsics.bas"] = string.Join('\n', [
                "Attribute VB_Name = \"Intrinsics\"",
                "Public Function String(ByVal Number As Long, ByVal Character As Variant) As String",
                "End Function",
                "Public Function Date() As Date",
                "End Function"
            ])
        };

    private static string PropertyContextSource(string statement)
        => string.Join('\n', [
            "Attribute VB_Name = \"Main\"",
            "Private Const FixedValue As Long = 1",
            "Private MutableValue As Long",
            "Public Function ReadValue() As Long",
            "End Function",
            "Public Sub RunTask()",
            "End Sub",
            "Public Property Get ReadOnly() As Long",
            "End Property",
            "Public Property Let WriteOnly(ByVal AssignedValue As Long)",
            "End Property",
            "Public Property Get ReadWrite() As Long",
            "End Property",
            "Public Property Let ReadWrite(ByVal AssignedValue As Long)",
            "End Property",
            "Public Sub Probe()",
            "    Dim result As Long",
            statement,
            "End Sub"
        ]);

    private static string MemberPropertySource(string statement)
        => string.Join('\n', [
            "Attribute VB_Name = \"Main\"",
            "Public Sub Probe()",
            "    Dim item As Item",
            "    Dim legacy As LegacyItem",
            "    Dim result As Long",
            statement,
            "End Sub"
        ]);

    private static string ItemClassSource()
        => string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Item\"",
            "Public Property Get ReadMember() As Long",
            "End Property",
            "Public Property Let WriteMember(ByVal AssignedValue As Long)",
            "End Property",
            "Public Property Get ReadWriteMember() As Long",
            "End Property",
            "Public Property Let ReadWriteMember(ByVal AssignedValue As Long)",
            "End Property"
        ]);

    private static string TypeContextSource(string statement)
        => string.Join('\n', [
            "Attribute VB_Name = \"Main\"",
            "Public Enum State",
            "    Ready",
            "End Enum",
            "Public Type Point",
            "    X As Long",
            "End Type",
            "Public Sub Probe()",
            "    Dim value As Variant",
            statement,
            "End Sub"
        ]);

    private static string CallContextSource(string callLine)
        => string.Join('\n', [
            "Attribute VB_Name = \"Main\"",
            "Public Function ExampleFunc(ByVal Arg1 As Long, Optional ByVal Arg2 As Boolean = False, Optional ByVal Arg3 As Boolean = False) As String",
            "End Function",
            "Public Sub ExampleSub(ByVal Arg1 As Long, Optional ByVal Arg2 As Boolean = False, Optional ByVal Arg3 As Boolean = False)",
            "End Sub",
            "Public Function CollectValues(Optional ByVal Prefix As String, ParamArray Values() As Variant) As Long",
            "End Function",
            "Public Sub Collect(Optional ByVal Prefix As String, ParamArray Values() As Variant)",
            "End Sub",
            "Public Event Saved(ByVal Arg1 As Long, Optional ByVal Arg2 As Boolean)",
            "Public Sub Probe()",
            "    Dim result As Variant",
            "    Dim LocalValue As Long",
            callLine,
            "End Sub"
        ]);

    private static string EventAndLabelSource(string statement)
        => string.Join('\n', [
            "Attribute VB_Name = \"Main\"",
            "Public Event Saved()",
            "Public Event Updated(ByVal Value As Long)",
            "Public Sub Probe()",
            "StartHere:",
            "100",
            statement,
            "End Sub",
            "Public Sub Other()",
            "OtherLabel:",
            "End Sub"
        ]);

    private static string ThingClassSource()
        => string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Thing\"",
            "Public Function Transform(ByVal Arg As Long, Optional ByVal OptionalArg As Long = 0) As Long",
            "End Function",
            "Public Property Get ReadMember() As Long",
            "End Property",
            "Public Property Let WriteMember(ByVal AssignedValue As Long)",
            "End Property"
        ]);

    private static string MemberCallSource(string callLine)
        => string.Join('\n', [
            "Attribute VB_Name = \"Main\"",
            "Public Sub Probe()",
            "    Dim thing As Thing",
            "    Dim result As Long",
            callLine,
            "End Sub"
        ]);

    private static string ResultTargetSource(
        string declaration,
        string statement,
        string terminator)
        => string.Join('\n', [
            "Attribute VB_Name = \"Main\"",
            declaration,
            statement,
            terminator
        ]);

    private static string[] DefinitionLabels(VbaCompletionResult result)
        => result.Candidates
            .Where(candidate => candidate.Kind == VbaCompletionCandidateKind.Definition)
            .Select(candidate => candidate.Label)
            .ToArray();

    private static string[] RelevantDefinitionLabels(
        VbaCompletionResult result,
        IReadOnlyCollection<string> universe)
        => result.Candidates
            .Where(candidate => candidate.Kind == VbaCompletionCandidateKind.Definition)
            .Where(candidate => universe.Contains(candidate.Label, StringComparer.OrdinalIgnoreCase))
            .Select(candidate => candidate.Label)
            .ToArray();

    private static string[] RelevantLabels(
        VbaCompletionResult result,
        IReadOnlyCollection<string> universe)
        => result.Candidates
            .Where(candidate => universe.Contains(candidate.Label, StringComparer.OrdinalIgnoreCase))
            .Select(candidate => candidate.Label)
            .ToArray();

    private static string[] ContextualLabels(VbaCompletionResult result)
        => result.Candidates
            .Where(candidate => candidate.Kind == VbaCompletionCandidateKind.ContextualStatement)
            .Select(candidate => candidate.Label)
            .ToArray();

    private static void AssertNamedCandidates(
        VbaCompletionResult result,
        params string[] expectedNames)
    {
        var candidates = result.Candidates
            .Where(candidate => candidate.Kind == VbaCompletionCandidateKind.NamedArgument)
            .ToArray();

        Assert.Equal(expectedNames, candidates.Select(candidate => candidate.Label));
        foreach (var candidate in candidates)
        {
            Assert.Equal(candidate.Label + ":=", candidate.InsertText);
            Assert.Equal(candidate.Label, candidate.FilterText);
            Assert.Null(candidate.Definition);
        }
    }

    private static ReferenceFixture LegacyReference()
        => CreateReference(
            new VbaProjectReferenceDefinition(
                LegacyReferenceName,
                "LegacyValue",
                VbaSourceDefinitionKind.Property),
            new VbaProjectReferenceDefinition(
                LegacyReferenceName,
                "LegacyItem",
                VbaSourceDefinitionKind.Class),
            new VbaProjectReferenceDefinition(
                LegacyReferenceName,
                "LegacyMember",
                VbaSourceDefinitionKind.Property,
                ParentTypeName: "LegacyItem"));

    private static ReferenceFixture TypeReference()
        => CreateReference(
            new VbaProjectReferenceDefinition(
                LegacyReferenceName,
                "IWidget",
                VbaSourceDefinitionKind.Class),
            new VbaProjectReferenceDefinition(
                LegacyReferenceName,
                "Widget",
                VbaSourceDefinitionKind.Class,
                IsCreatable: true));

    private static ReferenceFixture CreateReference(
        params VbaProjectReferenceDefinition[] definitions)
    {
        var selection = VbaProjectReferenceSelection.Create(
            ProjectDocument.ExcelKind,
            [new VbaProjectReference(LegacyReferenceName)]);
        var catalogs = VbaProjectReferenceCatalogSet.Empty.WithCatalog(
            new VbaProjectReferenceCatalog(
                LegacyReferenceName,
                ["Legacy"],
                definitions));
        return new ReferenceFixture(selection, catalogs);
    }

    private sealed record ReferenceFixture(
        VbaProjectReferenceSelection Selection,
        VbaProjectReferenceCatalogSet Catalogs);
}
