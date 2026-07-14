using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaLanguageServer.Syntax.Tests;

public sealed class VbaSyntaxTreeIncrementalTests
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
        var previous = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", original);

        var result = VbaSyntaxTree.ParseOrUpdate("file:///C:/work/Worker.bas", updated, previous);

        Assert.Equal(VbaSyntaxTreeParseUpdateKind.ModuleMember, result.UpdateKind);
        Assert.NotNull(result.MemberUpdate);
        Assert.Equal("BuildValue", result.MemberUpdate.PreviousMember.Name);
        Assert.Equal("BuildValue", result.MemberUpdate.CurrentMember.Name);
        Assert.Equal(2, result.MemberUpdate.PreviousStartLine);
        Assert.Equal(2, result.MemberUpdate.CurrentStartLine);
        Assert.Contains(result.SyntaxTree.Module.CallableDeclarations, declaration => declaration.Name == "BuildValue");
        Assert.Contains(result.SyntaxTree.Module.CallableDeclarations, declaration => declaration.Name == "Run");
    }

    [Fact]
    public void ParserFallsBackToFullModuleForBoundaryAndRecoveryCases()
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
        var boundaryChanged = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Function BuildValueRenamed() As String",
            "    BuildValueRenamed = \"new\"",
            "End Function",
            "",
            "Public Sub Run()",
            "    BuildValue",
            "End Sub"
        ]);
        var malformed = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Function BuildValue() As String",
            "    BuildValue = \"unterminated",
            "",
            "Public Sub Run()",
            "    BuildValue",
            "End Sub"
        ]);
        var previous = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", original);

        var boundaryResult = VbaSyntaxTree.ParseOrUpdate("file:///C:/work/Worker.bas", boundaryChanged, previous);
        var recoveryResult = VbaSyntaxTree.ParseOrUpdate("file:///C:/work/Worker.bas", malformed, previous);

        Assert.Equal(VbaSyntaxTreeParseUpdateKind.FullModule, boundaryResult.UpdateKind);
        Assert.Null(boundaryResult.MemberUpdate);
        Assert.Equal(VbaSyntaxTreeParseUpdateKind.FullModule, recoveryResult.UpdateKind);
        Assert.Null(recoveryResult.MemberUpdate);
        Assert.Contains(recoveryResult.SyntaxTree.Diagnostics, diagnostic => diagnostic.Code == "syntax.unterminatedStringLiteral");
    }
}
