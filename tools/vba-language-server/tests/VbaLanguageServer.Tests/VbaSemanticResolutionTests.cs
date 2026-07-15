using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaSemanticResolutionTests
{
    [Fact]
    public void BundledFunctionKindsDoNotDependOnReturnTypeMetadata()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub UseFunctions()",
            "    MsgBox(",
            "    Dim app As Excel.Application",
            "    app.Run(",
            "End Sub"
        ]);
        var index = VbaSourceIndex.Build(
            new Dictionary<string, string> { [uri] = text },
            VbaProjectReferenceSelection.Create(
                ProjectDocument.ExcelKind,
                [
                    new VbaProjectReference("Visual Basic For Applications"),
                    new VbaProjectReference("Microsoft Excel 16.0 Object Library")
                ]),
            VbaProjectReferenceCatalogSet.CreateBundled());

        Assert.Equal(
            "Function MsgBox(Prompt, Buttons, Title)",
            index.GetSignatureHelp(uri, 2, "    MsgBox(".Length)?.Signature.Label);
        Assert.Equal(
            "Function Run(Macro, [Arg1])",
            index.GetSignatureHelp(uri, 4, "    app.Run(".Length)?.Signature.Label);
    }

    [Fact]
    public void ResolvesTypedReferenceMembersAndMissingMetadataFailsClosed()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "Public Sub Run()",
            "    Dim app As Excel.Application",
            "    value = app.",
            "    app.Run(",
            "    Dim dict As Scripting.Dictionary",
            "    dict.",
            "    Dim unknown As MissingType",
            "    unknown.",
            "End Sub"
        ]);
        var index = BuildIndex(uri, text);

        var appCompletionLabels = index.GetCompletionDefinitions(uri, 4, "    value = app.".Length)
            .Select(definition => definition.Name)
            .ToArray();
        Assert.Contains("Run", appCompletionLabels);
        Assert.Contains("Workbooks", appCompletionLabels);
        Assert.DoesNotContain("Dictionary", appCompletionLabels);

        var runDefinition = index.ResolveSourceDefinition(uri, 5, "    app.".Length);
        Assert.Equal("Microsoft Excel 16.0 Object Library", runDefinition?.ModuleName);
        Assert.Equal("Application", runDefinition?.ParentTypeName);
        Assert.Equal("Function Run(Macro, [Arg1])", index.GetSignatureHelp(uri, 5, "    app.Run(".Length)?.Signature.Label);

        var dictionaryCompletionLabels = index.GetCompletionDefinitions(uri, 7, "    dict.".Length)
            .Select(definition => definition.Name)
            .ToArray();
        Assert.Contains("Exists", dictionaryCompletionLabels);
        Assert.Empty(index.GetCompletionDefinitions(uri, 9, "    unknown.".Length));
    }

    [Fact]
    public void SourceTypesOutrankReferencesUnlessTypeAnnotationIsReferenceQualified()
    {
        const string workerUri = "file:///C:/work/Worker.bas";
        const string applicationUri = "file:///C:/work/Application.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "Public Sub Run()",
            "    Dim sourceApp As Application",
            "    sourceApp.",
            "    Dim excelApp As Excel.Application",
            "    excelApp.",
            "End Sub"
        ]);
        var sourceApplicationText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Application\"",
            "Public Function SourceOnly() As String",
            "End Function"
        ]);
        var index = BuildIndex(
            new Dictionary<string, string>
            {
                [workerUri] = workerText,
                [applicationUri] = sourceApplicationText
            });

        var sourceLabels = index.GetCompletionDefinitions(workerUri, 4, "    sourceApp.".Length)
            .Select(definition => definition.Name)
            .ToArray();
        Assert.Contains("SourceOnly", sourceLabels);
        Assert.DoesNotContain("Run", sourceLabels);

        var referenceLabels = index.GetCompletionDefinitions(workerUri, 6, "    excelApp.".Length)
            .Select(definition => definition.Name)
            .ToArray();
        Assert.Contains("Run", referenceLabels);
        Assert.DoesNotContain("SourceOnly", referenceLabels);
    }

    [Fact]
    public void ResolvesMemberChainsContinuationsAndNestedWithReceivers()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "Public Sub Run()",
            "    Dim app As Excel.Application",
            "    app.Workbooks.",
            "    app _",
            "        .Run(",
            "    app.Run( _",
            "        ",
            "    With app",
            "        With .Workbooks",
            "            .Open(",
            "        End With",
            "    End With",
            "    With app _",
            "        .Workbooks",
            "        .Open(",
            "    End With",
            "End Sub"
        ]);
        var index = BuildIndex(uri, text);

        var workbookLabels = index.GetCompletionDefinitions(uri, 4, "    app.Workbooks.".Length)
            .Select(definition => definition.Name)
            .ToArray();
        Assert.Contains("Open", workbookLabels);

        Assert.Equal("Function Run(Macro, [Arg1])", index.GetSignatureHelp(uri, 6, "        .Run(".Length)?.Signature.Label);
        Assert.Equal("Function Run(Macro, [Arg1])", index.GetSignatureHelp(uri, 8, "        ".Length)?.Signature.Label);
        Assert.Equal("Function Open(FileName) As Workbook", index.GetSignatureHelp(uri, 11, "            .Open(".Length)?.Signature.Label);
        Assert.Equal("Function Open(FileName) As Workbook", index.GetSignatureHelp(uri, 16, "        .Open(".Length)?.Signature.Label);
    }

    [Fact]
    public void MemberAndTypeCompletionUseSourceTypeContext()
    {
        const string workerUri = "file:///C:/work/Worker.bas";
        const string rangeBoundsUri = "file:///C:/work/WorksheetRangeBounds.cls";
        const string helperUri = "file:///C:/work/Helper.bas";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "Public Sub Run()",
            "    Dim bare As ",
            "    Dim typed As WorksheetRan",
            "    Dim range_obj As WorksheetRangeBounds",
            "    aaaa = range_obj.",
            "    aaaa = range_obj.Col",
            "    aaaa = range_obj.Column ",
            "    aaaa = range_obj. ",
            "End Sub"
        ]);
        var rangeBoundsText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"WorksheetRangeBounds\"",
            "Public Property Get Column() As Long",
            "End Property",
            "Public Property Get ColumnCount() As Long",
            "End Property"
        ]);
        var helperText = string.Join('\n', [
            "Attribute VB_Name = \"Helper\"",
            "Public Function BuildValue() As String",
            "End Function"
        ]);
        var index = BuildIndex(
            new Dictionary<string, string>
            {
                [workerUri] = workerText,
                [rangeBoundsUri] = rangeBoundsText,
                [helperUri] = helperText
            });

        var dotCompletion = index.GetCompletionResult(workerUri, 6, "    aaaa = range_obj.".Length);
        var dotLabels = dotCompletion.Definitions.Select(definition => definition.Name).ToArray();
        Assert.All(dotCompletion.Candidates, candidate =>
            Assert.Equal(VbaCompletionCandidateKind.Definition, candidate.Kind));
        Assert.Contains("Column", dotLabels);
        Assert.Contains("ColumnCount", dotLabels);
        Assert.DoesNotContain("BuildValue", dotLabels);

        var partialCompletion = index.GetCompletionResult(workerUri, 7, "    aaaa = range_obj.Col".Length);
        var partialLabels = partialCompletion.Definitions.Select(definition => definition.Name).ToArray();
        Assert.All(partialCompletion.Candidates, candidate =>
            Assert.Equal(VbaCompletionCandidateKind.Definition, candidate.Kind));
        Assert.Contains("Column", partialLabels);
        Assert.Contains("ColumnCount", partialLabels);
        Assert.DoesNotContain("BuildValue", partialLabels);

        var completedMemberCompletion = index.GetCompletionResult(workerUri, 8, "    aaaa = range_obj.Column ".Length);
        Assert.Empty(completedMemberCompletion.Candidates);

        var spacedDotCompletion = index.GetCompletionResult(workerUri, 9, "    aaaa = range_obj. ".Length);
        Assert.Empty(spacedDotCompletion.Candidates);

        var bareTypeCompletion = index.GetCompletionResult(workerUri, 3, "    Dim bare As ".Length);
        Assert.Contains(bareTypeCompletion.Candidates, candidate =>
            candidate.Kind == VbaCompletionCandidateKind.LanguageVocabulary
            && candidate.Label == "String");
        Assert.Contains(
            bareTypeCompletion.Definitions,
            definition => definition.Name == "WorksheetRangeBounds" && definition.Kind == VbaSourceDefinitionKind.Class);

        var typeCompletion = index.GetCompletionResult(workerUri, 4, "    Dim typed As WorksheetRan".Length);
        Assert.Contains(typeCompletion.Candidates, candidate =>
            candidate.Kind == VbaCompletionCandidateKind.LanguageVocabulary
            && candidate.Label == "String");
        Assert.Contains(
            typeCompletion.Definitions,
            definition => definition.Name == "WorksheetRangeBounds" && definition.Kind == VbaSourceDefinitionKind.Class);
    }

    [Fact]
    public void MemberCompletionUsesPublicMembersOfGlobalVariableTypeFromOtherModule()
    {
        const string workerUri = "file:///C:/work/Mod_Search.bas";
        const string commonUri = "file:///C:/work/common-modules/Lib_Common.bas";
        const string worksheetServiceUri = "file:///C:/work/common-modules/IWorksheetService.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Mod_Search\"",
            "Option Explicit",
            "Public Sub Run()",
            "    WsSrv.",
            "End Sub"
        ]);
        var commonText = string.Join('\n', [
            "Attribute VB_Name = \"Lib_Common\"",
            "Option Explicit",
            "Public WsSrv As IWorksheetService"
        ]);
        var worksheetServiceText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"IWorksheetService\"",
            "Private Sub Class_Initialize()",
            "End Sub",
            "Public Function Find() As Object",
            "End Function",
            "Public Sub ClearRange()",
            "End Sub",
            "Public Sub SetRangeColor()",
            "End Sub"
        ]);
        var index = BuildIndex(
            new Dictionary<string, string>
            {
                [workerUri] = workerText,
                [commonUri] = commonText,
                [worksheetServiceUri] = worksheetServiceText
            });

        var labels = index.GetCompletionDefinitions(workerUri, 3, "    WsSrv.".Length)
            .Select(definition => definition.Name)
            .ToArray();

        Assert.Contains("Find", labels);
        Assert.Contains("ClearRange", labels);
        Assert.Contains("SetRangeColor", labels);
        Assert.DoesNotContain("Class_Initialize", labels);
    }

    [Fact]
    public void CompletionStaysSilentInsideApostropheComments()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    ' Call Build",
            "    Dim value As String",
            "End Sub"
        ]);
        var index = BuildIndex(uri, text);

        var commentCompletion = index.GetCompletionResult(uri, 2, "    ' Call B".Length);
        var codeCompletion = index.GetCompletionResult(uri, 3, "    Dim value As ".Length);

        Assert.Empty(commentCompletion.Candidates);
        Assert.Contains(codeCompletion.Candidates, candidate =>
            candidate.Kind == VbaCompletionCandidateKind.LanguageVocabulary
            && candidate.Label == "String");
        Assert.NotEmpty(codeCompletion.Definitions);
    }

    [Fact]
    public void CompletionStaysSilentInsideStringLiterals()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    value = \"Call Build\"",
            "    Dim value As String",
            "End Sub"
        ]);
        var index = BuildIndex(uri, text);

        var stringCompletion = index.GetCompletionResult(uri, 2, "    value = \"Call B".Length);
        var codeCompletion = index.GetCompletionResult(uri, 3, "    Dim value As ".Length);

        Assert.Empty(stringCompletion.Candidates);
        Assert.Contains(codeCompletion.Candidates, candidate =>
            candidate.Kind == VbaCompletionCandidateKind.LanguageVocabulary
            && candidate.Label == "String");
        Assert.NotEmpty(codeCompletion.Definitions);
    }

    [Fact]
    public void CompletionStaysSilentInsideDocumentationComments()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "'* Calls Build",
            "Public Sub Run()",
            "    Dim value As String",
            "End Sub"
        ]);
        var index = BuildIndex(uri, text);

        var documentationCompletion = index.GetCompletionResult(uri, 1, "'* Calls B".Length);
        var codeCompletion = index.GetCompletionResult(uri, 3, "    Dim value As ".Length);

        Assert.Empty(documentationCompletion.Candidates);
        Assert.Contains(codeCompletion.Candidates, candidate =>
            candidate.Kind == VbaCompletionCandidateKind.LanguageVocabulary
            && candidate.Label == "String");
        Assert.NotEmpty(codeCompletion.Definitions);
    }

    [Fact]
    public void CompletionStaysSilentInsideRemComments()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    Rem Call Build",
            "    Dim value As String",
            "End Sub"
        ]);
        var index = BuildIndex(uri, text);

        var remCompletion = index.GetCompletionResult(uri, 2, "    Rem Call B".Length);
        var codeCompletion = index.GetCompletionResult(uri, 3, "    Dim value As ".Length);

        Assert.Empty(remCompletion.Candidates);
        Assert.Contains(codeCompletion.Candidates, candidate =>
            candidate.Kind == VbaCompletionCandidateKind.LanguageVocabulary
            && candidate.Label == "String");
        Assert.NotEmpty(codeCompletion.Definitions);
    }

    [Fact]
    public void CompletionStaysSilentImmediatelyAfterLineContinuationMarker()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    BuildValue _",
            "        1",
            "    Dim value As String",
            "End Sub",
            "Public Sub BuildValue(ByVal argument As Long)",
            "End Sub"
        ]);
        var index = BuildIndex(uri, text);

        var continuationCompletion = index.GetCompletionResult(uri, 2, "    BuildValue _".Length);
        var codeCompletion = index.GetCompletionResult(uri, 4, "    Dim value As ".Length);

        Assert.Empty(continuationCompletion.Candidates);
        Assert.Contains(codeCompletion.Candidates, candidate =>
            candidate.Kind == VbaCompletionCandidateKind.LanguageVocabulary
            && candidate.Label == "String");
        Assert.NotEmpty(codeCompletion.Definitions);
    }

    [Fact]
    public void CompletionContinuesAfterIdentifierUnderscoreWithoutPrecedingSpace()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    value_",
            "End Sub"
        ]);
        var index = BuildIndex(uri, text);

        var completion = index.GetCompletionResult(uri, 2, "    value_".Length);

        Assert.NotEmpty(completion.Candidates);
    }

    [Fact]
    public void SignatureHelpUsesActiveNamedArgumentWhenParameterNameMatches()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Function ReadValue(ByVal Key As String, ByVal Fallback As String) As String",
            "End Function",
            "Public Sub Run()",
            "    ReadValue(Fallback:=",
            "End Sub"
        ]);
        var index = BuildIndex(uri, text);

        var signatureHelp = index.GetSignatureHelp(uri, 4, "    ReadValue(Fallback:=".Length);

        Assert.Equal("Function ReadValue(Key As String, Fallback As String) As String", signatureHelp?.Signature.Label);
        Assert.Equal(1, signatureHelp?.ActiveParameter);
    }

    [Theory]
    [InlineData("    example_var = ExampleFunc(", 1)]
    [InlineData("    example_var = ExampleFunc(Arg", 1)]
    [InlineData("    example_var = ExampleFunc(Arg2", 1)]
    [InlineData("    example_var = ExampleFunc(Arg2:", 1)]
    [InlineData("    example_var = ExampleFunc(Arg2:=", 1)]
    [InlineData("    example_var = ExampleFunc(Arg2:=Tr", 1)]
    public void SignatureHelpTracksCompleteNamedArgumentAcrossCursorPositions(
        string cursorPrefix,
        int expectedParameter)
    {
        const string uri = "file:///C:/work/Worker.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub ExampleSub()",
            "    Dim example_var As String",
            "    example_var = ExampleFunc(Arg2:=True)",
            "End Sub",
            "Public Function ExampleFunc(ByRef Arg1 As Long, Optional Arg2 As Boolean = False) As String",
            "End Function"
        ]);
        var index = BuildIndex(uri, text);

        var signatureHelp = index.GetSignatureHelp(uri, 3, cursorPrefix.Length);

        Assert.Equal(expectedParameter, signatureHelp?.ActiveParameter);
    }

    [Fact]
    public void SignatureHelpEndsAfterCompleteCallClosingParenthesis()
    {
        const string uri = "file:///C:/work/Worker.bas";
        const string callLine = "    example_var = ExampleFunc(Arg2:=True)";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub ExampleSub()",
            "    Dim example_var As String",
            callLine,
            "End Sub",
            "Public Function ExampleFunc(ByRef Arg1 As Long, Optional Arg2 As Boolean = False) As String",
            "End Function"
        ]);
        var index = BuildIndex(uri, text);

        var signatureHelp = index.GetSignatureHelp(uri, 3, callLine.Length);

        Assert.Null(signatureHelp);
    }

    [Theory]
    [InlineData(7, "    example_var = ExampleFunc(1,")]
    [InlineData(7, "    example_var = ExampleFunc(1, ")]
    [InlineData(7, "    example_var = ExampleFunc(1, Arg")]
    [InlineData(7, "    example_var = ExampleFunc(1, Arg3:")]
    [InlineData(7, "    example_var = ExampleFunc(1, Arg3:=Tr")]
    [InlineData(8, "    ExampleSub 1,")]
    [InlineData(8, "    ExampleSub 1, ")]
    [InlineData(8, "    ExampleSub 1, Arg")]
    [InlineData(8, "    ExampleSub 1, Arg3:")]
    [InlineData(8, "    ExampleSub 1, Arg3:=Tr")]
    public void SignatureHelpTracksThirdNamedArgumentAfterPositionalArgument(
        int line,
        string cursorPrefix)
    {
        const string uri = "file:///C:/work/Worker.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Function ExampleFunc(ByRef Arg1 As Long, Optional Arg2 As Boolean = False, Optional Arg3 As Boolean = False) As String",
            "End Function",
            "Public Sub ExampleSub(ByRef Arg1 As Long, Optional Arg2 As Boolean = False, Optional Arg3 As Boolean = False)",
            "End Sub",
            "Public Sub Run()",
            "    Dim example_var As String",
            "    example_var = ExampleFunc(1, Arg3:=True)",
            "    ExampleSub 1, Arg3:=True",
            "End Sub"
        ]);
        var index = BuildIndex(uri, text);

        var signatureHelp = index.GetSignatureHelp(uri, line, cursorPrefix.Length);

        Assert.Equal(2, signatureHelp?.ActiveParameter);
    }

    [Fact]
    public void SignatureHelpEndsAfterParenthesizedAndStatementFormCallsComplete()
    {
        const string uri = "file:///C:/work/Worker.bas";
        const string functionCall = "    example_var = ExampleFunc(1, Arg3:=True)";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Function ExampleFunc(ByRef Arg1 As Long, Optional Arg2 As Boolean = False, Optional Arg3 As Boolean = False) As String",
            "End Function",
            "Public Sub ExampleSub(ByRef Arg1 As Long, Optional Arg2 As Boolean = False, Optional Arg3 As Boolean = False)",
            "End Sub",
            "Public Sub Run()",
            "    Dim example_var As String",
            functionCall,
            "    ExampleSub 1, Arg3:=True",
            "End Sub"
        ]);
        var index = BuildIndex(uri, text);

        var afterFunctionCall = index.GetSignatureHelp(uri, 7, functionCall.Length);
        var afterStatementCall = index.GetSignatureHelp(uri, 9, 0);

        Assert.Null(afterFunctionCall);
        Assert.Null(afterStatementCall);
    }

    [Fact]
    public void SignatureHelpFormatsSourceOptionalParametersAndTracksArgumentForms()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Function ExampleFunc(ByVal Arg1 As String, Optional Arg2 As Long, Optional ByVal Arg3 As Variant) As String",
            "End Function",
            "Public Sub Run()",
            "    value = ExampleFunc(",
            "    value = ExampleFunc(\"a\", ",
            "    value = ExampleFunc(Arg2:=",
            "    value = ExampleFunc(,, ",
            "    value = ExampleFunc( _",
            "        \"a\", _",
            "        ",
            "End Sub"
        ]);
        var index = BuildIndex(uri, text);

        var opening = index.GetSignatureHelp(uri, 4, "    value = ExampleFunc(".Length);
        var positional = index.GetSignatureHelp(uri, 5, "    value = ExampleFunc(\"a\", ".Length);
        var named = index.GetSignatureHelp(uri, 6, "    value = ExampleFunc(Arg2:=".Length);
        var omitted = index.GetSignatureHelp(uri, 7, "    value = ExampleFunc(,, ".Length);
        var continued = index.GetSignatureHelp(uri, 10, "        ".Length);

        Assert.Equal(
            "Function ExampleFunc(Arg1 As String, [ByRef Arg2 As Long], [Arg3 As Variant]) As String",
            opening?.Signature.Label);
        Assert.Equal(["Arg1", "Arg2", "Arg3"], opening!.Signature.Parameters.Select(parameter => parameter.Name).ToArray());
        Assert.Equal(0, opening.ActiveParameter);
        Assert.Equal(1, positional?.ActiveParameter);
        Assert.Equal(1, named?.ActiveParameter);
        Assert.Equal(2, omitted?.ActiveParameter);
        Assert.Equal(1, continued?.ActiveParameter);
    }

    [Fact]
    public void SignatureHelpFormatsRichSourceCallableSignatures()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub ExampleSub(ByRef Values() As String, ByVal Fallback As String, Optional RetryCount As Long, ParamArray Rest() As Variant)",
            "End Sub",
            "Public Function ExampleFunc(ByVal Key As String, Optional Fallback As Variant) As String",
            "End Function",
            "Friend Property Get DisplayName(Optional Name As String) As String",
            "End Property",
            "Public Event Saved(ByVal Name As String, Optional RetryCount As Long)",
            "Public Sub Run()",
            "    ExampleSub(",
            "    result = ExampleFunc(",
            "    value = DisplayName(",
            "    RaiseEvent Saved(",
            "End Sub"
        ]);
        var index = BuildIndex(uri, text);

        var subHelp = index.GetSignatureHelp(uri, 9, "    ExampleSub(".Length);
        var functionHelp = index.GetSignatureHelp(uri, 10, "    result = ExampleFunc(".Length);
        var propertyHelp = index.GetSignatureHelp(uri, 11, "    value = DisplayName(".Length);
        var eventHelp = index.GetSignatureHelp(uri, 12, "    RaiseEvent Saved(".Length);

        Assert.Equal(
            "Sub ExampleSub(ByRef Values() As String, Fallback As String, [ByRef RetryCount As Long], ParamArray Rest() As Variant)",
            subHelp?.Signature.Label);
        Assert.Equal(
            [
                "ByRef Values() As String",
                "Fallback As String",
                "[ByRef RetryCount As Long]",
                "ParamArray Rest() As Variant"
            ],
            subHelp!.Signature.Parameters.Select(parameter => parameter.Label).ToArray());
        Assert.Equal(
            "Function ExampleFunc(Key As String, [ByRef Fallback As Variant]) As String",
            functionHelp?.Signature.Label);
        Assert.Equal(
            "Property DisplayName([ByRef Name As String]) As String",
            propertyHelp?.Signature.Label);
        Assert.Equal(
            "Event Saved(Name As String, [ByRef RetryCount As Long])",
            eventHelp?.Signature.Label);
    }

    [Fact]
    public void SignatureHelpRejectsWriteOnlyPropertyCallTargets()
    {
        const string uri = "file:///C:/work/Worker.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Public Property Let WriteOnly(ByVal AssignedValue As Long)",
            "End Property",
            "Public Sub Run()",
            "    Dim result As Long",
            "    result = WriteOnly(",
            "End Sub"
        ]);
        var index = BuildIndex(uri, text);

        var signatureHelp = index.GetSignatureHelp(
            uri,
            6,
            "    result = WriteOnly(".Length);

        Assert.Null(signatureHelp);
    }

    [Theory]
    [InlineData("Let", "Long", "    Item() = 42", "    Item(")]
    [InlineData("Set", "Object", "    Set Item() = Nothing", "    Set Item(")]
    public void SignatureHelpForIndexedWriteOnlyPropertyAssignmentExcludesTheRhsParameter(
        string accessorKind,
        string valueType,
        string assignment,
        string callPrefix)
    {
        const string uri = "file:///C:/work/Worker.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            $"Public Property {accessorKind} Item(ByVal Index As Long, ByVal AssignedValue As {valueType})",
            "End Property",
            "Public Sub Run()",
            assignment,
            "End Sub"
        ]);
        var index = BuildIndex(uri, text);

        var signatureHelp = Assert.IsType<VbaSignatureHelp>(index.GetSignatureHelp(
            uri,
            5,
            callPrefix.Length));

        Assert.Equal("Property Item(Index As Long)", signatureHelp.Signature.Label);
        Assert.Equal("Index", Assert.Single(signatureHelp.Signature.Parameters).Name);
        Assert.Equal(0, signatureHelp.ActiveParameter);
    }

    [Fact]
    public void SignatureHelpReturnsSourceMemberCallableSignaturesAndStaysSilentForNonCallables()
    {
        const string workerUri = "file:///C:/work/Worker.bas";
        const string helperUri = "file:///C:/work/HelperClass.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    Dim helper As HelperClass",
            "    helper.BuildValue(",
            "    Dim value As String",
            "    value(",
            "End Sub"
        ]);
        var helperText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"HelperClass\"",
            "Public Function BuildValue(ByVal Arg1 As String, Optional Arg2 As Long) As String",
            "End Function"
        ]);
        var index = BuildIndex(new Dictionary<string, string>
        {
            [workerUri] = workerText,
            [helperUri] = helperText
        });

        var sourceMember = index.GetSignatureHelp(workerUri, 3, "    helper.BuildValue(".Length);
        var nonCallable = index.GetSignatureHelp(workerUri, 5, "    value(".Length);

        Assert.Equal(
            "Function BuildValue(Arg1 As String, [ByRef Arg2 As Long]) As String",
            sourceMember?.Signature.Label);
        Assert.Null(nonCallable);
    }

    [Fact]
    public void SignatureHelpBracketsReferenceOptionalParametersOnlyWhenMetadataIsAvailable()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    Dim generated As GeneratedType",
            "    generated.OptionalMethod(",
            "    generated.PlainMethod(",
            "End Sub"
        ]);
        var selection = VbaProjectReferenceSelection.Create(
            ProjectDocument.ExcelKind,
            [new VbaProjectReference("Generated Library")]);
        var catalog = new VbaProjectReferenceCatalog(
            "Generated Library",
            ["Generated"],
            [
                new VbaProjectReferenceDefinition(
                    "Generated Library",
                    "GeneratedType",
                    VbaSourceDefinitionKind.Class),
                new VbaProjectReferenceDefinition(
                    "Generated Library",
                    "OptionalMethod",
                    VbaSourceDefinitionKind.Procedure,
                    Signature: new VbaCallableSignature(
                        "OptionalMethod(Required, OptionalValue)",
                        [
                            new VbaCallableParameter("Required"),
                            new VbaCallableParameter("OptionalValue", IsOptional: true)
                        ],
                        CallableKind: VbaCallableKind.Sub),
                    ParentTypeName: "GeneratedType"),
                new VbaProjectReferenceDefinition(
                    "Generated Library",
                    "PlainMethod",
                    VbaSourceDefinitionKind.Procedure,
                    Signature: new VbaCallableSignature(
                        "PlainMethod(Required, OptionalValue)",
                        [
                            new VbaCallableParameter("Required"),
                            new VbaCallableParameter("OptionalValue")
                        ]),
                    ParentTypeName: "GeneratedType")
            ]);
        var index = VbaSourceIndex.Build(
            new Dictionary<string, string> { [uri] = text },
            selection,
            VbaProjectReferenceCatalogSet.Empty.WithCatalog(catalog));

        var optionalSignature = index.GetSignatureHelp(uri, 3, "    generated.OptionalMethod(".Length);
        var plainSignature = index.GetSignatureHelp(uri, 4, "    generated.PlainMethod(".Length);

        Assert.Equal("Sub OptionalMethod(Required, [OptionalValue])", optionalSignature?.Signature.Label);
        Assert.Equal("PlainMethod(Required, OptionalValue)", plainSignature?.Signature.Label);
    }

    [Fact]
    public void SignatureHelpFormatsRichReferenceCatalogCallablesWithoutGuessingMissingMetadata()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    Dim generated As GeneratedType",
            "    generated.RichMethod(",
            "    generated.GeneratedName(",
            "    generated.Changed(",
            "    generated.FallbackMethod(",
            "End Sub"
        ]);
        var selection = VbaProjectReferenceSelection.Create(
            ProjectDocument.ExcelKind,
            [new VbaProjectReference("Generated Library")]);
        var catalog = new VbaProjectReferenceCatalog(
            "Generated Library",
            ["Generated"],
            [
                new VbaProjectReferenceDefinition(
                    "Generated Library",
                    "GeneratedType",
                    VbaSourceDefinitionKind.Class),
                new VbaProjectReferenceDefinition(
                    "Generated Library",
                    "RichMethod",
                    VbaSourceDefinitionKind.Procedure,
                    Signature: new VbaCallableSignature(
                        "RichMethod(Required, ByValValue, OptionalCount, Rest)",
                        [
                            new VbaCallableParameter(
                                "Required",
                                TypeReference: new VbaTypeReference("Variant"),
                                IsByRef: true),
                            new VbaCallableParameter(
                                "ByValValue",
                                TypeReference: new VbaTypeReference("String"),
                                IsByRef: false),
                            new VbaCallableParameter(
                                "OptionalCount",
                                IsOptional: true,
                                TypeReference: new VbaTypeReference("Long"),
                                IsByRef: true),
                            new VbaCallableParameter(
                                "Rest",
                                TypeReference: new VbaTypeReference("Variant"),
                                IsParamArray: true,
                                IsArray: true)
                        ],
                        CallableKind: VbaCallableKind.Function),
                    ParentTypeName: "GeneratedType",
                    TypeReference: new VbaTypeReference("String")),
                new VbaProjectReferenceDefinition(
                    "Generated Library",
                    "GeneratedName",
                    VbaSourceDefinitionKind.Property,
                    Signature: new VbaCallableSignature(
                        "GeneratedName(Fallback)",
                        [
                            new VbaCallableParameter(
                                "Fallback",
                                IsOptional: true,
                                TypeReference: new VbaTypeReference("String"),
                                IsByRef: false)
                        ],
                        CallableKind: VbaCallableKind.Property),
                    ParentTypeName: "GeneratedType",
                    TypeReference: new VbaTypeReference("String")),
                new VbaProjectReferenceDefinition(
                    "Generated Library",
                    "Changed",
                    VbaSourceDefinitionKind.Event,
                    Signature: new VbaCallableSignature(
                        "Changed(Target)",
                        [
                            new VbaCallableParameter(
                                "Target",
                                TypeReference: new VbaTypeReference("Object"),
                                IsByRef: true)
                        ],
                        CallableKind: VbaCallableKind.Event),
                    ParentTypeName: "GeneratedType"),
                new VbaProjectReferenceDefinition(
                    "Generated Library",
                    "FallbackMethod",
                    VbaSourceDefinitionKind.Procedure,
                    Signature: new VbaCallableSignature(
                        "FallbackMethod(Value)",
                        [new VbaCallableParameter("Value")],
                        CallableKind: VbaCallableKind.Sub),
                    ParentTypeName: "GeneratedType")
            ]);
        var index = VbaSourceIndex.Build(
            new Dictionary<string, string> { [uri] = text },
            selection,
            VbaProjectReferenceCatalogSet.Empty.WithCatalog(catalog));

        var richSignature = index.GetSignatureHelp(uri, 3, "    generated.RichMethod(".Length);
        var propertySignature = index.GetSignatureHelp(uri, 4, "    generated.GeneratedName(".Length);
        var eventSignature = index.GetSignatureHelp(uri, 5, "    generated.Changed(".Length);
        var fallbackSignature = index.GetSignatureHelp(uri, 6, "    generated.FallbackMethod(".Length);

        Assert.Equal(
            "Function RichMethod(ByRef Required As Variant, ByValValue As String, [ByRef OptionalCount As Long], ParamArray Rest() As Variant) As String",
            richSignature?.Signature.Label);
        Assert.Equal(
            [
                "ByRef Required As Variant",
                "ByValValue As String",
                "[ByRef OptionalCount As Long]",
                "ParamArray Rest() As Variant"
            ],
            richSignature!.Signature.Parameters.Select(parameter => parameter.Label).ToArray());
        Assert.Equal(
            "Property GeneratedName([Fallback As String]) As String",
            propertySignature?.Signature.Label);
        Assert.Equal("Event Changed(ByRef Target As Object)", eventSignature?.Signature.Label);
        Assert.Equal("Sub FallbackMethod(Value)", fallbackSignature?.Signature.Label);
    }

    [Fact]
    public void ReferenceEventKindOverridesForwardedProcedureCallableKind()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    Dim publisher As GeneratedPublisher",
            "    publisher.Changed(",
            "End Sub"
        ]);
        var catalog = new VbaProjectReferenceCatalog(
            "Generated Library",
            ["Generated"],
            [
                new VbaProjectReferenceDefinition(
                    "Generated Library",
                    "GeneratedPublisher",
                    VbaSourceDefinitionKind.Class),
                new VbaProjectReferenceDefinition(
                    "Generated Library",
                    "Changed",
                    VbaSourceDefinitionKind.Event,
                    Signature: new VbaCallableSignature(
                        "Changed(Target)",
                        [new VbaCallableParameter("Target")],
                        CallableKind: VbaCallableKind.Function),
                    ParentTypeName: "GeneratedPublisher")
            ]);
        var index = VbaSourceIndex.Build(
            new Dictionary<string, string> { [uri] = text },
            VbaProjectReferenceSelection.Create(
                ProjectDocument.ExcelKind,
                [new VbaProjectReference("Generated Library")]),
            VbaProjectReferenceCatalogSet.Empty.WithCatalog(catalog));

        Assert.Equal(
            "Event Changed(Target)",
            index.GetSignatureHelp(uri, 3, "    publisher.Changed(".Length)?.Signature.Label);
    }

    [Fact]
    public void SignatureHelpSupportsStatementFormCallsOnlyAtStatementLevel()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub ExampleSub(ByVal Arg1 As String, Optional Arg2 As Long)",
            "End Sub",
            "Public Function ExampleFunc(ByVal Arg1 As String, Optional Arg2 As Long) As String",
            "End Function",
            "Public Sub Run()",
            "    ExampleSub ",
            "    Worker.ExampleSub ",
            "    ExampleFunc ",
            "    ExampleSub \"a\", ",
            "    ExampleSub Arg2:=",
            "    value = ExampleFunc ",
            "    If ExampleFunc Then",
            "    Dim localValue As String",
            "End Sub"
        ]);
        var index = BuildIndex(uri, text);

        var statementSub = index.GetSignatureHelp(uri, 6, "    ExampleSub ".Length);
        var qualifiedSub = index.GetSignatureHelp(uri, 7, "    Worker.ExampleSub ".Length);
        var discardedFunction = index.GetSignatureHelp(uri, 8, "    ExampleFunc ".Length);
        var positional = index.GetSignatureHelp(uri, 9, "    ExampleSub \"a\", ".Length);
        var named = index.GetSignatureHelp(uri, 10, "    ExampleSub Arg2:=".Length);
        var assignment = index.GetSignatureHelp(uri, 11, "    value = ExampleFunc ".Length);
        var ifExpression = index.GetSignatureHelp(uri, 12, "    If ExampleFunc ".Length);
        var declaration = index.GetSignatureHelp(uri, 13, "    Dim localValue As ".Length);

        Assert.Equal("Sub ExampleSub(Arg1 As String, [ByRef Arg2 As Long])", statementSub?.Signature.Label);
        Assert.Equal("Sub ExampleSub(Arg1 As String, [ByRef Arg2 As Long])", qualifiedSub?.Signature.Label);
        Assert.Equal(
            "Function ExampleFunc(Arg1 As String, [ByRef Arg2 As Long]) As String",
            discardedFunction?.Signature.Label);
        Assert.Equal(0, statementSub?.ActiveParameter);
        Assert.Equal(1, positional?.ActiveParameter);
        Assert.Equal(1, named?.ActiveParameter);
        Assert.Null(assignment);
        Assert.Null(ifExpression);
        Assert.Null(declaration);
    }

    [Fact]
    public void SignatureHelpIncludesArrayParametersAndLaterParametersInOrder()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Function Search(ByRef Values() As String, ByVal Fallback As String) As Long",
            "End Function",
            "Public Sub Run()",
            "    Search(",
            "End Sub"
        ]);
        var index = BuildIndex(uri, text);

        var signatureHelp = index.GetSignatureHelp(uri, 4, "    Search(".Length);

        Assert.Equal(
            "Function Search(ByRef Values() As String, Fallback As String) As Long",
            signatureHelp?.Signature.Label);
        Assert.Equal(["Values", "Fallback"], signatureHelp!.Signature.Parameters.Select(parameter => parameter.Name).ToArray());
    }

    private static VbaSourceIndex BuildIndex(string uri, string text)
        => BuildIndex(new Dictionary<string, string> { [uri] = text });

    private static VbaSourceIndex BuildIndex(IReadOnlyDictionary<string, string> sourceDocuments)
        => VbaSourceIndex.Build(
            sourceDocuments,
            VbaProjectReferenceSelection.Create(
                ProjectDocument.ExcelKind,
                [
                    new VbaProjectReference("Microsoft Excel 16.0 Object Library"),
                    new VbaProjectReference("Microsoft Scripting Runtime")
                ]),
            VbaProjectReferenceCatalogSet.CreateBundled());
}
