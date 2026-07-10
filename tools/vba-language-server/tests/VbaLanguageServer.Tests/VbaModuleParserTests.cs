using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.Parsing;
using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaModuleParserTests
{
    [Fact]
    public void ParserReportsModuleMemberUpdateForSafeCallableBodyEdit()
    {
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
        var previousSyntaxTree = VbaModuleParser.Parse("file:///C:/work/Worker.bas", original);

        var result = VbaModuleParser.ParseOrUpdate("file:///C:/work/Worker.bas", updated, previousSyntaxTree);

        Assert.Equal(VbaModuleParseUpdateKind.ModuleMember, result.UpdateKind);
        Assert.Contains(result.SyntaxTree.CallableDeclarations, declaration => declaration.Name == "BuildValue");
        Assert.Contains(result.SyntaxTree.CallableDeclarations, declaration => declaration.Name == "Run");
    }

    [Fact]
    public void ParserFallsBackToFullModuleRebuildWhenMemberRecoveryIsRequired()
    {
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
        var previousSyntaxTree = VbaModuleParser.Parse("file:///C:/work/Worker.bas", original);

        var result = VbaModuleParser.ParseOrUpdate("file:///C:/work/Worker.bas", malformed, previousSyntaxTree);

        Assert.Equal(VbaModuleParseUpdateKind.FullModule, result.UpdateKind);
        Assert.DoesNotContain(result.SyntaxTree.CallableDeclarations, declaration => declaration.Name == "BuildValue");
        Assert.Contains(result.SyntaxTree.CallableDeclarations, declaration => declaration.Name == "Run");
    }

    [Fact]
    public void ParserReadsModuleClassAndFormIdentityFromAttributeOrFileName()
    {
        var standardModule = VbaModuleParser.Parse(
            "file:///C:/work/Worker.bas",
            "Attribute VB_Name = \"WorkerModule\"\nOption Explicit\n");
        var classModule = VbaModuleParser.Parse(
            "file:///C:/work/Customer.cls",
            "VERSION 1.0 CLASS\nAttribute VB_Name = \"CustomerRecord\"\nOption Explicit\n");
        var formModule = VbaModuleParser.Parse(
            "file:///C:/work/Dialog.frm",
            string.Join('\n', [
                "VERSION 5.00",
                "Begin VB.Form Dialog",
                "  Caption = \"Designer caption\"",
                "End",
                "Attribute VB_Name = \"DialogView\"",
                "Option Explicit"
            ]));
        var fallback = VbaModuleParser.Parse(
            "file:///C:/work/FallbackName.bas",
            "Option Explicit\n");

        Assert.Equal("WorkerModule", standardModule.Identity.Name);
        Assert.Equal(VbaSourceDefinitionKind.Module, standardModule.Identity.Kind);
        Assert.Equal("CustomerRecord", classModule.Identity.Name);
        Assert.Equal(VbaSourceDefinitionKind.Class, classModule.Identity.Kind);
        Assert.Equal("DialogView", formModule.Identity.Name);
        Assert.Equal(VbaSourceDefinitionKind.Form, formModule.Identity.Kind);
        Assert.Equal("FallbackName", fallback.Identity.Name);
    }

    [Fact]
    public void ParserReadsCallableDeclarationsAndFailsClosedForMalformedHeaders()
    {
        var module = VbaModuleParser.Parse(
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
            module.CallableDeclarations,
            readValue =>
            {
                Assert.Equal("ReadValue", readValue.Name);
                Assert.Equal(VbaSourceDefinitionKind.Procedure, readValue.Kind);
                Assert.Equal(VbaSourceDefinitionVisibility.Public, readValue.Visibility);
                Assert.Equal(new VbaRange(new VbaPosition(3, "Public Function ".Length), new VbaPosition(3, "Public Function ReadValue".Length)), readValue.Range);
                Assert.Equal("ReadValue(Key) As String", readValue.Signature.Label);
                var parameter = Assert.Single(readValue.Signature.Parameters);
                Assert.Equal("Key", parameter.Name);
                Assert.Equal("lookup key", parameter.Documentation);
            },
            saveValue =>
            {
                Assert.Equal("SaveValue", saveValue.Name);
                Assert.Equal(VbaSourceDefinitionVisibility.Private, saveValue.Visibility);
            },
            displayName =>
            {
                Assert.Equal("DisplayName", displayName.Name);
                Assert.Equal(VbaSourceDefinitionKind.Property, displayName.Kind);
            });
    }

    [Fact]
    public void ParserReadsClassAndFormCallableDeclarationsAfterExportHeaders()
    {
        var classModule = VbaModuleParser.Parse(
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
        var formModule = VbaModuleParser.Parse(
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
            classModule.CallableDeclarations,
            displayName =>
            {
                Assert.Equal("DisplayName", displayName.Name);
                Assert.Equal(VbaSourceDefinitionKind.Property, displayName.Kind);
                Assert.Equal(VbaSourceDefinitionVisibility.Public, displayName.Visibility);
                Assert.Equal(3, displayName.LineIndex);
            },
            initialize =>
            {
                Assert.Equal("Class_Initialize", initialize.Name);
                Assert.Equal(VbaSourceDefinitionKind.Procedure, initialize.Kind);
                Assert.Equal(VbaSourceDefinitionVisibility.Private, initialize.Visibility);
                Assert.Equal(5, initialize.LineIndex);
            });
        var formCallable = Assert.Single(formModule.CallableDeclarations);
        Assert.Equal("CommandButton1_Click", formCallable.Name);
        Assert.Equal(VbaSourceDefinitionKind.Procedure, formCallable.Kind);
        Assert.Equal(VbaSourceDefinitionVisibility.Private, formCallable.Visibility);
        Assert.Equal(6, formCallable.LineIndex);
    }

    [Fact]
    public void ParserRepresentsDeclarationsDocumentationAndLocalScopeInAstModel()
    {
        var module = VbaModuleParser.Parse(
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

        var declarations = module.Declarations;
        Assert.Contains(declarations, declaration =>
            declaration.Name == "Saved"
            && declaration.Kind == VbaSourceDefinitionKind.Event
            && declaration.Documentation == "Event documentation.");
        Assert.Contains(declarations, declaration =>
            declaration.Name == "Name"
            && declaration.Kind == VbaSourceDefinitionKind.Parameter
            && declaration.Visibility == VbaSourceDefinitionVisibility.Local);
        Assert.Contains(declarations, declaration =>
            declaration.Name == "Status"
            && declaration.Kind == VbaSourceDefinitionKind.Enum
            && declaration.Visibility == VbaSourceDefinitionVisibility.Public);
        Assert.Contains(declarations, declaration =>
            declaration.Name == "StatusReady"
            && declaration.Kind == VbaSourceDefinitionKind.EnumMember
            && declaration.Visibility == VbaSourceDefinitionVisibility.Public);
        Assert.Contains(declarations, declaration =>
            declaration.Name == "CustomerRecord"
            && declaration.Kind == VbaSourceDefinitionKind.Type);
        Assert.Contains(declarations, declaration =>
            declaration.Name == "Id"
            && declaration.Kind == VbaSourceDefinitionKind.TypeMember);
        Assert.Contains(declarations, declaration =>
            declaration.Name == "MaxCount"
            && declaration.Kind == VbaSourceDefinitionKind.Constant
            && declaration.Visibility == VbaSourceDefinitionVisibility.Private
            && declaration.Documentation == "Limit documentation.");
        Assert.Contains(declarations, declaration =>
            declaration.Name == "moduleValue"
            && declaration.Kind == VbaSourceDefinitionKind.Variable
            && declaration.Visibility == VbaSourceDefinitionVisibility.Private);
        var readValue = Assert.Single(declarations, declaration => declaration.Name == "ReadValue");
        Assert.Equal(VbaSourceDefinitionKind.Procedure, readValue.Kind);
        Assert.Equal("Reads a value.\n\n@return selected value", readValue.Signature?.Documentation);
        var key = Assert.Single(declarations, declaration => declaration.Name == "Key");
        Assert.Equal("ReadValue", key.ParentProcedureName);
        Assert.Equal("lookup key", key.Documentation);
        var local = Assert.Single(declarations, declaration => declaration.Name == "localCount");
        Assert.Equal("ReadValue", local.ParentProcedureName);
        Assert.Equal(VbaSourceDefinitionVisibility.Local, local.Visibility);
    }

    [Fact]
    public void ParserExcludesFormDesignerBlockFromAstDeclarations()
    {
        var module = VbaModuleParser.Parse(
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

        Assert.Contains(module.Declarations, declaration => declaration.Name == "CommandButton1_Click");
        Assert.DoesNotContain(module.Declarations, declaration => declaration.Name == "Caption");
    }

    [Fact]
    public void ParserDerivesSourceDefinitionsAndSignaturesFromVbaSyntaxTreeDeclarations()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "Public Declare PtrSafe Function GetTickCount Lib \"kernel32\" () As Long",
            "Private Const MaxCount As Long = 10, DefaultName = \"fallback\"",
            "Dim firstValue As New Collection, implicitValue",
            "Public Static Function Build(ByVal Key As String) As String",
            "    Dim localCount As Long, implicitLocal",
            "End Function"
        ]);

        var module = VbaModuleParser.Parse(uri, source);
        var index = VbaSourceIndex.Build(new Dictionary<string, string> { [uri] = source });

        Assert.Contains(module.Members, member => member.Name == "GetTickCount");
        Assert.Contains(module.Declarations, declaration =>
            declaration.Name == "DefaultName"
            && declaration.Kind == VbaSourceDefinitionKind.Constant
            && declaration.TypeReference is null);
        Assert.Contains(module.Declarations, declaration =>
            declaration.Name == "firstValue"
            && declaration.Kind == VbaSourceDefinitionKind.Variable
            && declaration.TypeReference?.Name == "Collection");
        Assert.Contains(module.Declarations, declaration =>
            declaration.Name == "implicitValue"
            && declaration.Kind == VbaSourceDefinitionKind.Variable
            && declaration.TypeReference is null);
        Assert.Contains(module.Declarations, declaration =>
            declaration.Name == "implicitLocal"
            && declaration.Visibility == VbaSourceDefinitionVisibility.Local
            && declaration.ParentProcedureName == "Build");

        var buildDefinition = Assert.Single(index.GetDocumentDefinitions(uri), definition => definition.Name == "Build");
        Assert.Equal("Build(Key) As String", buildDefinition.Signature?.Label);
        Assert.Contains(index.GetDocumentDefinitions(uri), definition => definition.Name == "GetTickCount");
    }

    [Fact]
    public void ParserKeepsRepresentativeVbaDeclarationsOnTheVbaSyntaxTreePath()
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

        var module = VbaModuleParser.Parse(uri, source);
        var index = VbaSourceIndex.Build(new Dictionary<string, string> { [uri] = source });

        Assert.Contains(module.Declarations, declaration =>
            declaration.Name == "BufferSize"
            && declaration.Kind == VbaSourceDefinitionKind.Constant
            && declaration.Visibility == VbaSourceDefinitionVisibility.Public
            && declaration.TypeReference?.Name == "Long");
        Assert.Contains(module.Declarations, declaration =>
            declaration.Name == "SharedName"
            && declaration.Kind == VbaSourceDefinitionKind.Variable
            && declaration.Visibility == VbaSourceDefinitionVisibility.Public
            && declaration.TypeReference?.Name == "String");
        Assert.Contains(module.Declarations, declaration =>
            declaration.Name == "GetTickCount"
            && declaration.Kind == VbaSourceDefinitionKind.Procedure
            && declaration.Visibility == VbaSourceDefinitionVisibility.Private);

        var buildValue = Assert.Single(index.GetDocumentDefinitions(uri), definition => definition.Name == "BuildValue");
        Assert.Equal("BuildValue(Prefix) As String", buildValue.Signature?.Label);
        Assert.Contains(index.GetWorkspaceSymbols("shared"), symbol => symbol.Name == "SharedName");
        Assert.Contains(index.GetSemanticTokens(uri), token =>
            token.Text == "SharedName"
            && token.TokenType == "variable"
            && token.TokenModifiers.Contains("declaration"));
        Assert.Contains("Global Const BufferSize As Long = 260", index.FormatDocument(uri, tabSize: 4)?.NewText);
        Assert.Contains("Global SharedName As String", index.FormatDocument(uri, tabSize: 4)?.NewText);
    }
}
