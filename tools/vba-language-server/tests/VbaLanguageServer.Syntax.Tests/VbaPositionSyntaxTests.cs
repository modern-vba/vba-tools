using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaLanguageServer.Syntax.Tests;

public sealed class VbaPositionSyntaxTests
{
    [Fact]
    public void PositionSyntaxClassifiesNonCodeRegionsAndInvalidPositions()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "#If VBA7 Then",
            "Public Sub Run()",
            "    value = \"text\"",
            "    ' comment",
            "    Rem documentation",
            "    Rem first: app.Workbooks.",
            "End Sub"
        ]);
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        Assert.Equal(VbaPositionRegion.Preprocessor, tree.GetPositionSyntax(1, 5).Region);
        Assert.Equal(VbaPositionRegion.String, tree.GetPositionSyntax(3, "    value = \"te".Length).Region);
        Assert.Equal(VbaPositionRegion.Comment, tree.GetPositionSyntax(4, "    ' comm".Length).Region);
        Assert.Equal(VbaPositionRegion.Comment, tree.GetPositionSyntax(4, "    ' comment".Length).Region);
        Assert.Equal(VbaPositionRegion.Comment, tree.GetPositionSyntax(5, "    Rem doc".Length).Region);
        Assert.Equal(VbaPositionRegion.Comment, tree.GetPositionSyntax(6, "    Rem first: app.Work".Length).Region);
        Assert.Equal(VbaCompletionSyntaxKind.None, tree.GetPositionSyntax(6, "    Rem first: app.Workbooks.".Length).CompletionKind);
        Assert.Equal(VbaPositionRegion.Outside, tree.GetPositionSyntax(-1, 0).Region);
        Assert.Equal(VbaPositionRegion.Outside, tree.GetPositionSyntax(3, 100).Region);

        var formTree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Dialog.frm",
            string.Join('\n', [
                "VERSION 5.00",
                "Begin VB.Form Dialog",
                "  Caption = \"Designer caption\"",
                "End",
                "Attribute VB_Name = \"Dialog\""
            ]));
        Assert.Equal(VbaPositionRegion.Designer, formTree.GetPositionSyntax(2, 4).Region);
    }

    [Fact]
    public void PositionSyntaxReturnsMemberAndTypeFactsWithoutSourceFragments()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    Dim blank As ",
            "    Dim app As New Excel.Application",
            "    app.Workbooks.",
            "    app.Work",
            "    app.Workbooks ",
            "End Sub"
        ]);
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        var blankType = tree.GetPositionSyntax(2, "    Dim blank As ".Length);
        var qualifiedType = tree.GetPositionSyntax(3, "    Dim app As New Excel.Application".Length);
        var missingMember = tree.GetPositionSyntax(4, "    app.Workbooks.".Length);
        var partialMember = tree.GetPositionSyntax(5, "    app.Work".Length);
        var completedMember = tree.GetPositionSyntax(6, "    app.Workbooks ".Length);

        Assert.Equal(VbaCompletionSyntaxKind.TypeName, blankType.CompletionKind);
        Assert.True(blankType.TypeReference?.IsIncomplete);
        Assert.Equal("Excel", qualifiedType.TypeReference?.Qualifier?.Name);
        Assert.Equal("Application", qualifiedType.TypeReference?.Name?.Name);
        Assert.True(qualifiedType.TypeReference?.IsNew);
        Assert.Equal(VbaCompletionSyntaxKind.Member, missingMember.CompletionKind);
        Assert.Equal(["app", "Workbooks"], missingMember.MemberAccess!.Segments.Select(segment => segment.Name));
        Assert.Equal(2, missingMember.MemberAccess.TargetSegmentIndex);
        Assert.True(missingMember.MemberAccess.IsIncomplete);
        Assert.Equal(1, partialMember.MemberAccess?.TargetSegmentIndex);
        Assert.Equal(VbaCompletionSyntaxKind.Member, partialMember.CompletionKind);
        Assert.True(completedMember.MemberAccess?.HasTrailingWhitespace);
        Assert.Equal(VbaCompletionSyntaxKind.None, completedMember.CompletionKind);
    }

    [Fact]
    public void PositionSyntaxTracksNestedAndStatementFormCallArguments()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    ReadValue(\"a,b\", Fallback:=",
            "    Outer(Inner(",
            "    ExampleSub \"a,b\", ",
            "End Sub"
        ]);
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        var named = tree.GetPositionSyntax(2, "    ReadValue(\"a,b\", Fallback:=".Length).CallSite;
        var nested = tree.GetPositionSyntax(3, "    Outer(Inner(".Length).CallSite;
        var statement = tree.GetPositionSyntax(4, "    ExampleSub \"a,b\", ".Length).CallSite;

        Assert.Equal(VbaCallSyntaxForm.Parenthesized, named?.Form);
        Assert.Equal("ReadValue", named?.Callee.Target?.Name);
        Assert.Equal(1, named?.ActiveArgumentIndex);
        Assert.Equal("Fallback", named?.ActiveNamedArgument);
        Assert.Equal("Inner", nested?.Callee.Target?.Name);
        Assert.Equal(VbaCallSyntaxForm.Statement, statement?.Form);
        Assert.Equal(1, statement?.ActiveArgumentIndex);
    }

    [Fact]
    public void PositionSyntaxUsesCompleteNamedArgumentWhileCursorIsInsideItsName()
    {
        const string callLine = "    value = ExampleFunc(Arg2:=True)";
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            callLine,
            "End Sub"
        ]);
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        var position = tree.GetPositionSyntax(
            2,
            "    value = ExampleFunc(Arg".Length).CallSite;

        Assert.Equal("Arg2", position?.ActiveNamedArgument);
    }

    [Fact]
    public void PositionSyntaxUsesCompleteStatementNamedArgumentWhileCursorIsInsideItsName()
    {
        const string callLine = "    ExampleSub Arg2:=True";
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            callLine,
            "End Sub"
        ]);
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        var position = tree.GetPositionSyntax(2, "    ExampleSub Arg".Length).CallSite;

        Assert.Equal("Arg2", position?.ActiveNamedArgument);
    }

    [Fact]
    public void CompleteAndPositionCallSyntaxAgreeOnArgumentKindsAndNames()
    {
        const string callLine = "    Example(1, Arg2:=\"x\", , Arg4:=Nested(5, Name:=6),)";
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            callLine,
            "End Sub"
        ]);
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        var complete = Assert.Single(
            tree.Module.ArgumentLists,
            argumentList => argumentList.Callee == "Example");
        var position = tree.GetPositionSyntax(2, callLine.LastIndexOf(')')).CallSite;

        Assert.NotNull(position);
        Assert.Equal(VbaCallSyntaxForm.Parenthesized, position.Form);
        Assert.Equal(complete.Arguments.Count, position.Arguments.Count);
        Assert.Equal(
            complete.Arguments.Select(argument => argument.Name),
            position.Arguments.Select(argument => argument.Name));
        Assert.Equal(
            complete.Arguments.Select(argument => argument.Kind == VbaArgumentKind.Omitted),
            position.Arguments.Select(argument => argument.IsOmitted));
        Assert.Equal(complete.Arguments.Count - 1, position.ActiveArgumentIndex);
    }

    [Fact]
    public void CompleteAndPositionCallSyntaxPreserveCallsInsideArrayBounds()
    {
        const string declarationLine = "    ReDim values(CalculateSize(1, Minimum:=2))";
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            declarationLine,
            "End Sub"
        ]);
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        var complete = Assert.Single(
            tree.Module.ArgumentLists,
            argumentList => argumentList.Callee == "CalculateSize");
        Assert.DoesNotContain(
            tree.Module.ArgumentLists,
            argumentList => argumentList.Callee == "values");

        var innerCloseCharacter = declarationLine.LastIndexOf(')') - 1;
        var position = tree.GetPositionSyntax(2, innerCloseCharacter).CallSite;

        Assert.NotNull(position);
        Assert.Equal(VbaCallSyntaxForm.Parenthesized, position.Form);
        Assert.Equal(complete.Callee, position.Callee.Target?.Name);
        Assert.Equal(complete.Arguments.Count, position.Arguments.Count);
        Assert.Equal(
            complete.Arguments.Select(argument => argument.Name),
            position.Arguments.Select(argument => argument.Name));
        Assert.Equal(
            complete.Arguments.Select(argument => argument.Kind == VbaArgumentKind.Omitted),
            position.Arguments.Select(argument => argument.IsOmitted));
    }

    [Fact]
    public void CompleteAndPositionCallSyntaxPreserveEmptyCallsInsideArrayBounds()
    {
        const string declarationLine = "    ReDim values(CalculateSize())";
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            declarationLine,
            "End Sub"
        ]);
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        var complete = Assert.Single(
            tree.Module.ArgumentLists,
            argumentList => argumentList.Callee == "CalculateSize");
        Assert.Empty(complete.Arguments);
        Assert.DoesNotContain(
            tree.Module.ArgumentLists,
            argumentList => argumentList.Callee == "values");

        var innerCloseCharacter = declarationLine.LastIndexOf(')') - 1;
        var position = tree.GetPositionSyntax(2, innerCloseCharacter).CallSite;

        Assert.NotNull(position);
        Assert.Equal(VbaCallSyntaxForm.Parenthesized, position.Form);
        Assert.Equal(complete.Callee, position.Callee.Target?.Name);
    }

    [Fact]
    public void PositionSyntaxPreservesNestedWithScopeOrderAcrossContinuations()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
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
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        var nested = tree.GetPositionSyntax(4, "            .Open(".Length);
        var continued = tree.GetPositionSyntax(9, "        .Open(".Length);

        Assert.Equal(2, nested.EnclosingWithScopes.Count);
        Assert.Equal("app", nested.EnclosingWithScopes[0].Receiver?.Target?.Name);
        Assert.True(nested.EnclosingWithScopes[1].Receiver?.IsLeadingDot);
        Assert.Equal("Workbooks", nested.EnclosingWithScopes[1].Receiver?.Target?.Name);
        Assert.Single(continued.EnclosingWithScopes);
        Assert.Equal(["app", "Workbooks"], continued.EnclosingWithScopes[0].Receiver!.Segments.Select(segment => segment.Name));
    }

    [Fact]
    public void IncrementalTreeBuildsPositionFactsFromUpdatedOffsets()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var previous = VbaSyntaxTree.ParseModule(uri, string.Join("\r\n", [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    app.",
            "End Sub"
        ]));
        var updatedText = string.Join("\r\n", [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    Dim value As String",
            "    app.Work",
            "End Sub"
        ]);

        var updated = VbaSyntaxTree.ParseOrUpdate(uri, updatedText, previous).SyntaxTree;
        var position = updated.GetPositionSyntax(3, "    app.Work".Length);

        Assert.Equal(VbaCompletionSyntaxKind.Member, position.CompletionKind);
        Assert.Equal("Work", position.MemberAccess?.Target?.Name);
        Assert.Equal(3, position.MemberAccess?.Target?.Range.Start.Line);
    }
}
