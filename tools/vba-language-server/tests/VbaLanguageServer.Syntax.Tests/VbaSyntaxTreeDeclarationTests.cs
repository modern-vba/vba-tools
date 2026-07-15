using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaLanguageServer.Syntax.Tests;

public sealed class VbaSyntaxTreeDeclarationTests
{
    [Fact]
    public void ParserRepresentsModuleMembersDeclarationsAndCallableSignatures()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "Public Declare PtrSafe Function GetTickCount Lib \"kernel32\" () As Long",
            "Private Const MaxCount As Long = 10, DefaultName = \"fallback\"",
            "Dim firstValue As New Collection, implicitValue",
            "'* Event documentation.",
            "Public Event Saved(ByVal Name As String)",
            "Public Enum Status",
            "    StatusReady = 1",
            "End Enum",
            "Public Type CustomerRecord",
            "    Id As Long",
            "End Type",
            "Public Static Function Build(ByVal Key As String) As String",
            "    Dim localCount As Long, implicitLocal",
            "End Function",
            "Friend Static Property Get DisplayName() As String",
            "End Property"
        ]);

        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        Assert.Contains(tree.Module.Members, member =>
            member.Name == "GetTickCount"
            && member.Kind == VbaDeclarationKind.Procedure
            && member.IsExternal);
        Assert.Contains(tree.Module.Declarations, declaration =>
            declaration.Name == "MaxCount"
            && declaration.Kind == VbaDeclarationKind.Constant
            && declaration.TypeReference?.Name == "Long");
        Assert.Contains(tree.Module.Declarations, declaration =>
            declaration.Name == "DefaultName"
            && declaration.Kind == VbaDeclarationKind.Constant
            && declaration.TypeReference is null);
        Assert.Contains(tree.Module.Declarations, declaration =>
            declaration.Name == "firstValue"
            && declaration.Kind == VbaDeclarationKind.Variable
            && declaration.TypeReference is { Name: "Collection", IsNew: true });
        Assert.Contains(tree.Module.Declarations, declaration =>
            declaration.Name == "implicitValue"
            && declaration.Kind == VbaDeclarationKind.Variable
            && declaration.TypeReference is null);
        Assert.Contains(tree.Module.Declarations, declaration =>
            declaration.Name == "Saved"
            && declaration.Kind == VbaDeclarationKind.Event
            && declaration.Documentation == "Event documentation.");
        Assert.Contains(tree.Module.Declarations, declaration =>
            declaration.Name == "Status"
            && declaration.Kind == VbaDeclarationKind.Enum);
        Assert.Contains(tree.Module.Declarations, declaration =>
            declaration.Name == "StatusReady"
            && declaration.Kind == VbaDeclarationKind.EnumMember);
        Assert.Contains(tree.Module.Declarations, declaration =>
            declaration.Name == "CustomerRecord"
            && declaration.Kind == VbaDeclarationKind.Type);
        Assert.Contains(tree.Module.Declarations, declaration =>
            declaration.Name == "Id"
            && declaration.Kind == VbaDeclarationKind.TypeMember);

        var build = Assert.Single(tree.Module.CallableDeclarations, declaration => declaration.Name == "Build");
        Assert.True(build.IsStatic);
        Assert.Equal("Build(Key) As String", build.Signature.Label);
        Assert.Contains(tree.Module.Declarations, declaration =>
            declaration.Name == "implicitLocal"
            && declaration.Visibility == VbaDeclarationVisibility.Local
            && declaration.ParentProcedureName == "Build"
            && declaration.TypeReference is null);
        Assert.Contains(tree.Module.CallableDeclarations, declaration =>
            declaration.Name == "DisplayName"
            && declaration.Kind == VbaDeclarationKind.Property
            && declaration.IsStatic);
    }

    [Fact]
    public void ParserReadsCallableArrayParametersWithoutStoppingAtArrayParentheses()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Declare PtrSafe Function ReadFile Lib \"kernel32\" (ByRef Buffer() As Byte, ByVal Count As Long) As Long",
            "Public Event Saved(ByRef ChangedNames() As String, ByVal Count As Long)",
            "Public Sub Run(ByRef Values() As String, ByVal Destination As String)",
            "End Sub",
            "Public Function Build( _",
            "    ByRef SourceNames() As String, _",
            "    ByVal Fallback As String _",
            ") As Long",
            "End Function"
        ]);

        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        var readFile = Assert.Single(tree.Module.CallableDeclarations, declaration => declaration.Name == "ReadFile");
        Assert.Equal("ReadFile(Buffer, Count) As Long", readFile.Signature.Label);
        Assert.Equal(["Buffer", "Count"], readFile.Signature.Parameters.Select(parameter => parameter.Name).ToArray());

        Assert.Contains(tree.Module.Declarations, declaration =>
            declaration.Name == "Saved"
            && declaration.Kind == VbaDeclarationKind.Event);
        Assert.Contains(tree.Module.Declarations, declaration =>
            declaration.Name == "ChangedNames"
            && declaration.Kind == VbaDeclarationKind.Parameter
            && declaration.Range.Start.Line == 2
            && declaration.TypeReference?.Name == "String");
        Assert.Contains(tree.Module.Declarations, declaration =>
            declaration.Name == "Count"
            && declaration.Kind == VbaDeclarationKind.Parameter
            && declaration.Range.Start.Line == 2
            && declaration.TypeReference?.Name == "Long");

        var run = Assert.Single(tree.Module.CallableDeclarations, declaration => declaration.Name == "Run");
        Assert.Equal("Run(Values, Destination)", run.Signature.Label);
        Assert.Equal(["Values", "Destination"], run.Signature.Parameters.Select(parameter => parameter.Name).ToArray());

        var build = Assert.Single(tree.Module.CallableDeclarations, declaration => declaration.Name == "Build");
        Assert.Equal("Build(SourceNames, Fallback) As Long", build.Signature.Label);
        Assert.Equal("Long", build.TypeReference?.Name);
        Assert.Equal(["SourceNames", "Fallback"], build.Signature.Parameters.Select(parameter => parameter.Name).ToArray());
    }

    [Fact]
    public void ParserPreservesPropertyGetLetAndSetAccessorKinds()
    {
        var source = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Public Property Get Value() As Variant",
            "End Property",
            "Public Property Let Value(ByVal AssignedValue As Variant)",
            "End Property",
            "Public Property Set Owner(ByVal AssignedOwner As Object)",
            "End Property"
        ]);

        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.cls", source);

        Assert.Contains(tree.Module.CallableDeclarations, declaration =>
            declaration.Name == "Value"
            && declaration.PropertyAccessorKind == VbaPropertyAccessorKind.Get);
        Assert.Contains(tree.Module.CallableDeclarations, declaration =>
            declaration.Name == "Value"
            && declaration.PropertyAccessorKind == VbaPropertyAccessorKind.Let);
        Assert.Contains(tree.Module.CallableDeclarations, declaration =>
            declaration.Name == "Owner"
            && declaration.PropertyAccessorKind == VbaPropertyAccessorKind.Set);
        Assert.Contains(tree.Module.Declarations, declaration =>
            declaration.Name == "Value"
            && declaration.PropertyAccessorKind == VbaPropertyAccessorKind.Get);
        Assert.Contains(tree.Module.Declarations, declaration =>
            declaration.Name == "Value"
            && declaration.PropertyAccessorKind == VbaPropertyAccessorKind.Let);
        Assert.Contains(tree.Module.Declarations, declaration =>
            declaration.Name == "Owner"
            && declaration.PropertyAccessorKind == VbaPropertyAccessorKind.Set);
    }
}
