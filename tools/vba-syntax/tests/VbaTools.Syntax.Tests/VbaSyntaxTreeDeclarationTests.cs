using VbaTools.Syntax;
using Xunit;

namespace VbaTools.Syntax.Tests;

public sealed class VbaSyntaxTreeDeclarationTests
{
    [Fact]
    public void ParserRepresentsJapaneseProcedureDeclarations()
    {
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            "Public Sub 集計()\nEnd Sub");

        var callable = Assert.Single(tree.Module.CallableDeclarations);
        Assert.Equal("集計", callable.Name);
    }

    [Theory]
    [InlineData("Public Sub Foo'comment")]
    [InlineData("Public Sub Foo: End Sub")]
    public void ParserStopsProcedureNamesAtVbaTokenBoundaries(string declarationLine)
    {
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            declarationLine + "\nEnd Sub");

        var callable = Assert.Single(tree.Module.CallableDeclarations);
        Assert.Equal("Foo", callable.Name);
        Assert.Equal(11, callable.Range.Start.Character);
        Assert.Equal(14, callable.Range.End.Character);
    }

    [Fact]
    public void ParserRepresentsJapaneseParameterDeclarations()
    {
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            "Public Sub Run(ByVal 値 As Long)\nEnd Sub");

        var parameter = Assert.Single(
            tree.Module.Declarations,
            declaration => declaration.Kind == VbaDeclarationKind.Parameter);
        Assert.Equal("値", parameter.Name);
    }

    [Fact]
    public void ParserPreservesACodePageParameterThatDotNetTreatsAsWhitespace()
    {
        const string parameterName = "\u00A0";
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            $"Public Sub Run({parameterName})\nEnd Sub");

        var callable = Assert.Single(tree.Module.CallableDeclarations);
        Assert.Equal(parameterName, Assert.Single(callable.Parameters).Name);
        Assert.Contains(tree.Module.Declarations, declaration =>
            declaration.Kind == VbaDeclarationKind.Parameter
            && declaration.Name == parameterName);
    }

    [Fact]
    public void ParameterModifiersDoNotSplitACompleteCodePageIdentifier()
    {
        const string parameterName = "ByVal\u00A0value";
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            $"Public Sub Run({parameterName} As Long)\nEnd Sub");

        var parameter = Assert.Single(
            Assert.Single(tree.Module.CallableDeclarations).Parameters);
        Assert.Equal(parameterName, parameter.Name);
        Assert.True(parameter.IsByRef);
    }

    [Fact]
    public void ArrayParameterRecognitionPreservesAnExactCodePageIdentifier()
    {
        const string parameterName = "\u00A0";
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            $"Public Sub Run({parameterName}() As Long)\nEnd Sub");

        var parameter = Assert.Single(
            Assert.Single(tree.Module.CallableDeclarations).Parameters);
        Assert.Equal(parameterName, parameter.Name);
        Assert.True(parameter.IsArray);
    }

    [Fact]
    public void ParserRejectsCompleteReservedProductionNamesForParameters()
    {
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            "Public Sub Run(ByVal CDecl As Long)\nEnd Sub");

        Assert.DoesNotContain(
            tree.Module.Declarations,
            declaration => declaration.Kind == VbaDeclarationKind.Parameter
                && declaration.Name == "CDecl");
    }

    [Fact]
    public void ParserRejectsAProcedureNameLongerThan255Characters()
    {
        var name = new string('A', 256);

        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            $"Public Sub {name}()\nEnd Sub");

        Assert.DoesNotContain(
            tree.Module.CallableDeclarations,
            declaration => declaration.Name == name);
    }

    [Fact]
    public void ReservedProcedureNameDoesNotBecomeAProcedureBodyStatement()
    {
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            "Public Sub CDecl()\nEnd Sub");

        Assert.DoesNotContain(
            tree.Module.Statements,
            statement => statement.Kind == VbaStatementKind.ProcedureBody);
        Assert.Contains(
            tree.Diagnostics,
            diagnostic => diagnostic.Code == "syntax.malformedDeclarationHeader");
    }

    [Fact]
    public void MalformedDeclarationDetectionUsesExactMsVbalWhitespace()
    {
        var unicode50Whitespace = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            "Public\u180eSub");
        var cp2IdentifierCharacter = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            "Public\u00a0Sub");
        var punctuationBoundary = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            "Public Sub(");

        Assert.Contains(
            unicode50Whitespace.Diagnostics,
            diagnostic => diagnostic.Code == "syntax.malformedDeclarationHeader");
        Assert.DoesNotContain(
            cp2IdentifierCharacter.Diagnostics,
            diagnostic => diagnostic.Code == "syntax.malformedDeclarationHeader");
        Assert.Contains(
            punctuationBoundary.Diagnostics,
            diagnostic => diagnostic.Code == "syntax.malformedDeclarationHeader");
    }

    [Theory]
    [InlineData("Public Sub Run-Now()")]
    [InlineData("Public Sub Run=Now")]
    public void ParserRejectsPunctuationAfterAValidProcedureNamePrefix(string header)
    {
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            $"{header}\nEnd Sub");

        Assert.DoesNotContain(
            tree.Module.CallableDeclarations,
            declaration => declaration.Name == "Run");
        Assert.Contains(
            tree.Diagnostics,
            diagnostic => diagnostic.Code == "syntax.malformedDeclarationHeader");
    }

    [Fact]
    public void ParserRepresentsJapaneseVariableDeclarations()
    {
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            "Public Sub Run()\n    Dim 値 As Long\nEnd Sub");

        var variable = Assert.Single(
            tree.Module.Declarations,
            declaration => declaration.Kind == VbaDeclarationKind.Variable);
        Assert.Equal("値", variable.Name);
    }

    [Fact]
    public void ModuleVariableClassificationPreservesNonWhitespaceIdentifierCharacters()
    {
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            "Public Static\u00a0Sub");

        var variable = Assert.Single(
            tree.Module.Declarations,
            declaration => declaration.Kind == VbaDeclarationKind.Variable);
        Assert.Equal("Static\u00a0Sub", variable.Name);
    }

    [Fact]
    public void WithEventsRecognitionUsesWholeIdentifierTokens()
    {
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            "Public WithEvents\u00a0source As Class1");

        var variable = Assert.Single(
            tree.Module.Declarations,
            declaration => declaration.Kind == VbaDeclarationKind.Variable);
        Assert.Equal("WithEvents\u00a0source", variable.Name);
        Assert.False(variable.IsWithEvents);
    }

    [Fact]
    public void ConstDeclarationsUseExactMsVbalWhitespaceBoundaries()
    {
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            "Const\u0019値 = 1\nConst\u00A0偽 = 2");

        Assert.Contains(tree.Module.Declarations, declaration =>
            declaration.Kind == VbaDeclarationKind.Constant && declaration.Name == "値");
        Assert.DoesNotContain(tree.Module.Declarations, declaration =>
            declaration.Kind == VbaDeclarationKind.Constant && declaration.Name == "偽");
    }

    [Fact]
    public void ParserRepresentsJapaneseEventDeclarations()
    {
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.cls",
            "Public Event 保存完了(ByVal 値 As Long)");

        var eventDeclaration = Assert.Single(
            tree.Module.Declarations,
            declaration => declaration.Kind == VbaDeclarationKind.Event);
        Assert.Equal("保存完了", eventDeclaration.Name);
    }

    [Fact]
    public void ParserRepresentsJapaneseEnumDeclarations()
    {
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            "Public Enum 状態\n"
            + "    待機中 = 0\n"
            + "End Enum");

        Assert.Contains(tree.Module.Declarations, declaration =>
            declaration.Kind == VbaDeclarationKind.Enum && declaration.Name == "状態");
    }

    [Fact]
    public void ParserRepresentsJapaneseEnumMembers()
    {
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            "Public Enum State\n"
            + "    待機中 = 0\n"
            + "End Enum");

        Assert.Contains(tree.Module.Declarations, declaration =>
            declaration.Kind == VbaDeclarationKind.EnumMember && declaration.Name == "待機中");
    }

    [Fact]
    public void ParserUsesOnlyMsVbalWhitespaceForEnumTerminators()
    {
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            "Public Enum State\n"
            + "    First = 0\n"
            + "End\u00a0Enum\n"
            + "    Second = 1\n"
            + "End\u0019Enum");

        Assert.Contains(tree.Module.Declarations, declaration =>
            declaration.Kind == VbaDeclarationKind.EnumMember && declaration.Name == "Second");
    }

    [Fact]
    public void ParserRejectsCompleteReservedProductionNamesForEnumMembers()
    {
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            "Public Enum State\n"
            + "    CDecl = 0\n"
            + "End Enum");

        Assert.DoesNotContain(tree.Module.Declarations, declaration =>
            declaration.Kind == VbaDeclarationKind.EnumMember && declaration.Name == "CDecl");
    }

    [Fact]
    public void ParserRepresentsJapaneseTypeDeclarations()
    {
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            "Public Type 顧客情報\n"
            + "    Id As Long\n"
            + "End Type");

        Assert.Contains(tree.Module.Declarations, declaration =>
            declaration.Kind == VbaDeclarationKind.Type && declaration.Name == "顧客情報");
    }

    [Fact]
    public void ParserRepresentsJapaneseTypeReferences()
    {
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            "Private currentCustomer As 顧客情報");

        var declaration = Assert.Single(
            tree.Module.Declarations,
            candidate => candidate.Kind == VbaDeclarationKind.Variable);
        Assert.Equal("顧客情報", declaration.TypeReference?.Name);
    }

    [Fact]
    public void CallableSignaturePreservesAnExactCodePageTypeName()
    {
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            "Public Function Build() As \u00a0\nEnd Function");

        var callable = Assert.Single(tree.Module.CallableDeclarations);
        Assert.Equal("\u00a0", callable.TypeReference?.Name);
        Assert.Equal("Build() As \u00a0", callable.Signature.Label);
    }

    [Fact]
    public void ParserDoesNotTreatAnUnrelatedReservedWordAsATypeReference()
    {
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            "Private value As If");

        var declaration = Assert.Single(
            tree.Module.Declarations,
            candidate => candidate.Kind == VbaDeclarationKind.Variable);
        Assert.Null(declaration.TypeReference);
    }

    [Fact]
    public void ParserDoesNotTreatAnyAsAnOrdinaryParameterTypeReference()
    {
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            "Public Sub Run(value As Any)\nEnd Sub");

        var callable = Assert.Single(tree.Module.CallableDeclarations);
        var parameter = Assert.Single(callable.Parameters);
        Assert.Equal("value", parameter.Name);
        Assert.Null(parameter.TypeReference);
    }

    [Fact]
    public void ParserRepresentsJapaneseExternalDeclareNames()
    {
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            "Public Declare PtrSafe Function 時刻取得 Lib \"kernel32\" () As Long");

        var callable = Assert.Single(tree.Module.CallableDeclarations);
        Assert.Equal("時刻取得", callable.Name);
        Assert.True(callable.IsExternal);
    }

    [Fact]
    public void ParserTreatsAnyAsAnExternalProcedureParameterTypeReference()
    {
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            "Public Declare Sub Run Lib \"sample\" (value As Any)");

        var callable = Assert.Single(tree.Module.CallableDeclarations);
        Assert.True(callable.IsExternal);
        var parameter = Assert.Single(callable.Parameters);
        Assert.Equal("Any", parameter.TypeReference?.Name);
    }

    [Fact]
    public void ParserUsesMsVbalWhitespaceWhenJoiningMultilineJapaneseDeclarations()
    {
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            "Public Sub 集計(ByVal 値 As String,\u0019_\n"
            + "    ByVal 件数 As Long)\n"
            + "End Sub");

        var declaration = Assert.Single(tree.Module.CallableDeclarations);
        Assert.Equal(["値", "件数"], declaration.Parameters.Select(parameter => parameter.Name));
    }

    [Fact]
    public void ParserDoesNotUseGenericUnicodeWhitespaceForDeclarationContinuations()
    {
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            "Public Sub Run(ByVal firstValue As String,\u000b_\n"
            + "    ByVal secondValue As Long)\n"
            + "End Sub");

        Assert.DoesNotContain(
            tree.Module.CallableDeclarations,
            declaration => declaration.Parameters.Any(parameter => parameter.Name == "secondValue"));
    }

    [Fact]
    public void Parser_treats_a_global_sub_as_a_public_callable_in_a_standard_module()
    {
        const string source = "Global Sub Run()\nEnd Sub";

        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        var callable = Assert.Single(tree.Module.CallableDeclarations);
        Assert.Equal("Run", callable.Name);
        Assert.Equal(VbaDeclarationKind.Procedure, callable.Kind);
        Assert.Equal(VbaDeclarationVisibility.Public, callable.Visibility);
        Assert.Contains(tree.Module.Statements, statement =>
            statement.Kind == VbaStatementKind.ProcedureBody
            && statement.Text.Contains("Global Sub Run", StringComparison.Ordinal));
        Assert.DoesNotContain(tree.Module.Declarations, declaration =>
            declaration.Name == "Run"
            && declaration.Kind == VbaDeclarationKind.Variable);
    }

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

    [Theory]
    [InlineData("\u180E", true)]
    [InlineData("\u00A0", false)]
    public void DocumentationCommentsUseExactMsVbalWhitespace(
        string prefix,
        bool expectedDocumentation)
    {
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            $"{prefix}'* Exact whitespace documentation.\nPublic Sub Run()\nEnd Sub");

        var declaration = Assert.Single(
            tree.Module.CallableDeclarations,
            candidate => candidate.Name == "Run");

        Assert.Equal(
            expectedDocumentation ? "Exact whitespace documentation." : null,
            declaration.Documentation);
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
