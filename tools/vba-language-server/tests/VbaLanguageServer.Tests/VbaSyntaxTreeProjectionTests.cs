using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaSyntaxTreeProjectionTests
{
    [Fact]
    public void ParserReportsModuleMemberUpdateForSafeCallableBodyEdit()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var original = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Function BuildValue() As String",
            "    BuildValue = \"old\"",
            "End Function",
            "",
            "Public Sub Run()",
            "    BuildValue",
            "End Sub"
        ]);
        var updated = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Function BuildValue() As String",
            "    BuildValue = \"new\"",
            "End Function",
            "",
            "Public Sub Run()",
            "    BuildValue",
            "End Sub"
        ]);
        var previousSyntaxTree = VbaSyntaxTree.ParseModule(uri, original);

        var result = VbaSyntaxTree.ParseOrUpdate(uri, updated, previousSyntaxTree);

        Assert.Equal(VbaSyntaxTreeParseUpdateKind.ModuleMember, result.UpdateKind);
        Assert.Contains(result.SyntaxTree.Module.CallableDeclarations, declaration => declaration.Name == "BuildValue");
        Assert.Contains(result.SyntaxTree.Module.CallableDeclarations, declaration => declaration.Name == "Run");
    }

    [Fact]
    public void ParserFallsBackToFullModuleRebuildWhenMemberRecoveryIsRequired()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var original = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Function BuildValue() As String",
            "    BuildValue = \"old\"",
            "End Function",
            "",
            "Public Sub Run()",
            "    BuildValue",
            "End Sub"
        ]);
        var malformed = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Function () As String",
            "    BuildValue = \"new\"",
            "End Function",
            "",
            "Public Sub Run()",
            "    BuildValue",
            "End Sub"
        ]);
        var previousSyntaxTree = VbaSyntaxTree.ParseModule(uri, original);

        var result = VbaSyntaxTree.ParseOrUpdate(uri, malformed, previousSyntaxTree);

        Assert.Equal(VbaSyntaxTreeParseUpdateKind.FullModule, result.UpdateKind);
        Assert.DoesNotContain(result.SyntaxTree.Module.CallableDeclarations, declaration => declaration.Name == "BuildValue");
        Assert.Contains(result.SyntaxTree.Module.CallableDeclarations, declaration => declaration.Name == "Run");
    }

    [Fact]
    public void ParserReadsModuleClassAndFormIdentityFromAttributeOrFileName()
    {
        var standardModule = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            "Attribute VB_Name = \"WorkerModule\"\nOption Explicit\n");
        var classModule = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Customer.cls",
            "VERSION 1.0 CLASS\nAttribute VB_Name = \"CustomerRecord\"\nOption Explicit\n");
        var formModule = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Dialog.frm",
            string.Join('\n', [
                "VERSION 5.00",
                "Begin VB.Form Dialog",
                "  Caption = \"Designer caption\"",
                "End",
                "Attribute VB_Name = \"DialogView\"",
                "Option Explicit"
            ]));
        var fallback = VbaSyntaxTree.ParseModule(
            "file:///C:/work/FallbackName.bas",
            "Option Explicit\n");

        Assert.Equal("WorkerModule", standardModule.Module.Identity.Name);
        Assert.Equal(VbaModuleKind.StandardModule, standardModule.Module.Kind);
        Assert.Equal("CustomerRecord", classModule.Module.Identity.Name);
        Assert.Equal(VbaModuleKind.ClassModule, classModule.Module.Kind);
        Assert.Equal("DialogView", formModule.Module.Identity.Name);
        Assert.Equal(VbaModuleKind.FormModule, formModule.Module.Kind);
        Assert.Equal("FallbackName", fallback.Module.Identity.Name);
    }

    [Fact]
    public void ParserReadsCallableDeclarationsAndFailsClosedForMalformedHeaders()
    {
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "'* Reads a value.",
                "'* @param Key lookup key",
                "Public Function ReadValue(ByVal Key As String) As String",
                "End Function",
                "Private Sub SaveValue()",
                "End Sub",
                "Friend Property Get DisplayName() As String",
                "End Property",
                "Public Function () As String",
                "End Function"
            ]));

        Assert.Collection(
            tree.Module.CallableDeclarations,
            readValue =>
            {
                Assert.Equal("ReadValue", readValue.Name);
                Assert.Equal(VbaDeclarationKind.Procedure, readValue.Kind);
                Assert.Equal(VbaDeclarationVisibility.Public, readValue.Visibility);
                Assert.Equal(3, readValue.Range.Start.Line);
                Assert.Equal("Public Function ".Length, readValue.Range.Start.Character);
                Assert.Equal("ReadValue(Key) As String", readValue.Signature.Label);
                var parameter = Assert.Single(readValue.Signature.Parameters);
                Assert.Equal("Key", parameter.Name);
                Assert.Equal("lookup key", parameter.Documentation);
            },
            saveValue =>
            {
                Assert.Equal("SaveValue", saveValue.Name);
                Assert.Equal(VbaDeclarationVisibility.Private, saveValue.Visibility);
            },
            displayName =>
            {
                Assert.Equal("DisplayName", displayName.Name);
                Assert.Equal(VbaDeclarationKind.Property, displayName.Kind);
            });
    }

    [Fact]
    public void SourceIndexProjectsArrayCallableParametersAsParameterDefinitions()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run(ByRef Values() As String, ByVal Fallback As String)",
            "End Sub"
        ]);
        var index = VbaSourceIndex.Build(new Dictionary<string, string> { [uri] = source });

        var definitions = index.GetDocumentDefinitions(uri);

        var run = Assert.Single(definitions, definition =>
            definition.Name == "Run"
            && definition.Kind == VbaSourceDefinitionKind.Procedure);
        Assert.Equal("Run(Values, Fallback)", run.Signature?.Label);
        Assert.Equal(["Values", "Fallback"], run.Signature!.Parameters.Select(parameter => parameter.Name).ToArray());

        Assert.Contains(definitions, definition =>
            definition.Name == "Values"
            && definition.Kind == VbaSourceDefinitionKind.Parameter
            && definition.ParentProcedureName == "Run"
            && definition.TypeReference?.Name == "String");
        Assert.Contains(definitions, definition =>
            definition.Name == "Fallback"
            && definition.Kind == VbaSourceDefinitionKind.Parameter
            && definition.ParentProcedureName == "Run"
            && definition.TypeReference?.Name == "String");
    }

    [Fact]
    public void ParserReadsClassAndFormCallableDeclarationsAfterExportHeaders()
    {
        var classModule = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Customer.cls",
            string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"Customer\"",
                "Option Explicit",
                "Public Property Get DisplayName() As String",
                "End Property",
                "Private Sub Class_Initialize()",
                "End Sub"
            ]));
        var formModule = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Dialog.frm",
            string.Join('\n', [
                "VERSION 5.00",
                "Begin VB.Form Dialog",
                "  Caption = \"Designer caption\"",
                "End",
                "Attribute VB_Name = \"Dialog\"",
                "Option Explicit",
                "Private Sub CommandButton1_Click()",
                "End Sub"
            ]));

        Assert.Collection(
            classModule.Module.CallableDeclarations,
            displayName =>
            {
                Assert.Equal("DisplayName", displayName.Name);
                Assert.Equal(VbaDeclarationKind.Property, displayName.Kind);
                Assert.Equal(VbaDeclarationVisibility.Public, displayName.Visibility);
                Assert.Equal(3, displayName.LineIndex);
            },
            initialize =>
            {
                Assert.Equal("Class_Initialize", initialize.Name);
                Assert.Equal(VbaDeclarationKind.Procedure, initialize.Kind);
                Assert.Equal(VbaDeclarationVisibility.Private, initialize.Visibility);
                Assert.Equal(5, initialize.LineIndex);
            });
        var formCallable = Assert.Single(formModule.Module.CallableDeclarations);
        Assert.Equal("CommandButton1_Click", formCallable.Name);
        Assert.Equal(VbaDeclarationKind.Procedure, formCallable.Kind);
        Assert.Equal(VbaDeclarationVisibility.Private, formCallable.Visibility);
        Assert.Equal(6, formCallable.LineIndex);
    }

    [Fact]
    public void ParserRepresentsDeclarationsDocumentationAndLocalScopeInSyntaxModel()
    {
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Option Explicit",
                "'* Event documentation.",
                "Public Event Saved(ByVal Name As String)",
                "Public Enum Status",
                "    StatusReady = 1",
                "    StatusDone",
                "End Enum",
                "Public Type CustomerRecord",
                "    Id As Long",
                "    Name As String",
                "End Type",
                "'* Limit documentation.",
                "Private Const MaxCount As Long = 10",
                "Dim moduleValue As String",
                "'* @brief Reads a value.",
                "'* @param Key lookup key",
                "'* @return selected value",
                "Public Function ReadValue(ByVal Key As String) As String",
                "    Dim localCount As Long",
                "    ReadValue = Key",
                "End Function"
            ]));

        var declarations = tree.Module.Declarations;
        Assert.Contains(declarations, declaration =>
            declaration.Name == "Saved"
            && declaration.Kind == VbaDeclarationKind.Event
            && declaration.Documentation == "Event documentation.");
        Assert.Contains(declarations, declaration =>
            declaration.Name == "Name"
            && declaration.Kind == VbaDeclarationKind.Parameter
            && declaration.Visibility == VbaDeclarationVisibility.Local);
        Assert.Contains(declarations, declaration =>
            declaration.Name == "Status"
            && declaration.Kind == VbaDeclarationKind.Enum
            && declaration.Visibility == VbaDeclarationVisibility.Public);
        Assert.Contains(declarations, declaration =>
            declaration.Name == "StatusReady"
            && declaration.Kind == VbaDeclarationKind.EnumMember
            && declaration.Visibility == VbaDeclarationVisibility.Public);
        Assert.Contains(declarations, declaration =>
            declaration.Name == "CustomerRecord"
            && declaration.Kind == VbaDeclarationKind.Type);
        Assert.Contains(declarations, declaration =>
            declaration.Name == "Id"
            && declaration.Kind == VbaDeclarationKind.TypeMember);
        Assert.Contains(declarations, declaration =>
            declaration.Name == "MaxCount"
            && declaration.Kind == VbaDeclarationKind.Constant
            && declaration.Visibility == VbaDeclarationVisibility.Private
            && declaration.Documentation == "Limit documentation.");
        Assert.Contains(declarations, declaration =>
            declaration.Name == "moduleValue"
            && declaration.Kind == VbaDeclarationKind.Variable
            && declaration.Visibility == VbaDeclarationVisibility.Private);
        var readValue = Assert.Single(declarations, declaration => declaration.Name == "ReadValue");
        Assert.Equal(VbaDeclarationKind.Procedure, readValue.Kind);
        Assert.Equal("Reads a value.\n\n@return selected value", readValue.Signature?.Documentation);
        var key = Assert.Single(declarations, declaration => declaration.Name == "Key");
        Assert.Equal("ReadValue", key.ParentProcedureName);
        Assert.Equal("lookup key", key.Documentation);
        var local = Assert.Single(declarations, declaration => declaration.Name == "localCount");
        Assert.Equal("ReadValue", local.ParentProcedureName);
        Assert.Equal(VbaDeclarationVisibility.Local, local.Visibility);
    }

    [Fact]
    public void ParserExcludesFormDesignerBlockFromSyntaxDeclarations()
    {
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Dialog.frm",
            string.Join('\n', [
                "VERSION 5.00",
                "Begin VB.Form Dialog",
                "  Caption = \"Designer caption\"",
                "End",
                "Attribute VB_Name = \"Dialog\"",
                "Option Explicit",
                "Private Sub CommandButton1_Click()",
                "End Sub"
            ]));

        Assert.Contains(tree.Module.Declarations, declaration => declaration.Name == "CommandButton1_Click");
        Assert.DoesNotContain(tree.Module.Declarations, declaration => declaration.Name == "Caption");
    }

    [Fact]
    public void SourceIndexProjectsSyntaxTreeDeclarationsForEditorFeatures()
    {
        const string uri = "file:///C:/work/Compat.bas";
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Compat\"",
            "Option Explicit",
            "global const BufferSize As Long = 260",
            "global SharedName As String",
            "Private Declare PtrSafe Function GetTickCount Lib \"kernel32\" Alias \"GetTickCount\" () As Long",
            "Public Static Function BuildValue(Optional ByVal Prefix As String = \"x\") As String",
            "    BuildValue = Prefix & SharedName",
            "End Function"
        ]);
        var syntaxTree = VbaSyntaxTree.ParseModule(uri, source);
        var index = VbaSourceIndex.BuildFromSyntaxTrees(new Dictionary<string, VbaSyntaxTree> { [uri] = syntaxTree });

        Assert.Contains(syntaxTree.Module.Declarations, declaration =>
            declaration.Name == "BufferSize"
            && declaration.Kind == VbaDeclarationKind.Constant
            && declaration.Visibility == VbaDeclarationVisibility.Public
            && declaration.TypeReference?.Name == "Long");
        Assert.Contains(syntaxTree.Module.Declarations, declaration =>
            declaration.Name == "SharedName"
            && declaration.Kind == VbaDeclarationKind.Variable
            && declaration.Visibility == VbaDeclarationVisibility.Public
            && declaration.TypeReference?.Name == "String");
        Assert.Contains(syntaxTree.Module.Declarations, declaration =>
            declaration.Name == "GetTickCount"
            && declaration.Kind == VbaDeclarationKind.Procedure
            && declaration.Visibility == VbaDeclarationVisibility.Private);

        var buildValue = Assert.Single(index.GetDocumentDefinitions(uri), definition => definition.Name == "BuildValue");
        Assert.Equal("BuildValue([Prefix]) As String", buildValue.Signature?.Label);
        Assert.Contains(index.GetWorkspaceSymbols("shared"), symbol => symbol.Name == "SharedName");
        Assert.Contains(index.GetSemanticTokens(uri), token =>
            token.Text == "SharedName"
            && token.TokenType == "field"
            && token.TokenModifiers.Contains("declaration"));

        var edit = Assert.IsType<VbaTextEdit>(index.FormatDocument(uri, tabSize: 4));
        Assert.Contains("Global Const BufferSize As Long = 260", edit.NewText);
        Assert.Contains("Global SharedName As String", edit.NewText);
    }
}
