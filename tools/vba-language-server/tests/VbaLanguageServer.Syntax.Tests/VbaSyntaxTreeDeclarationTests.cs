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
}
