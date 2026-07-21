using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaLanguageServer.Syntax.Tests;

public sealed class VbaCodeModuleProjectionTests
{
    [Fact]
    public void StandardModuleProjectionExcludesModuleAndProcedureAttributes()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Attribute VB_Description = \"Projection fixture\"",
            "Option Explicit",
            "Public Property Get Item() As Long",
            "Attribute Item.VB_UserMemId = 0",
            "    Item = value",
            "End Property"
        ]);

        var projection = CreateProjection("Worker.bas", source);

        Assert.Equal("Worker", projection.ModuleName);
        Assert.Equal(VbaModuleKind.StandardModule, projection.ModuleKind);
        Assert.Collection(
            projection.Lines,
            line => AssertExcluded(line, 0, VbaCodeModuleLineRole.ExportAttribute),
            line => AssertExcluded(line, 1, VbaCodeModuleLineRole.ExportAttribute),
            line => AssertProjected(line, 2, 1),
            line => AssertProjected(line, 3, 2),
            line => AssertExcluded(line, 4, VbaCodeModuleLineRole.ExportAttribute),
            line => AssertProjected(line, 5, 3),
            line => AssertProjected(line, 6, 4));
    }

    [Fact]
    public void ClassModuleProjectionExcludesClassMetadataAndAttributes()
    {
        var source = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "BEGIN",
            "  MultiUse = -1  'True",
            "END",
            "Attribute VB_Name = \"WorkerClass\"",
            "Attribute VB_Exposed = False",
            "Option Explicit",
            "Public Sub Run()",
            "    value = 1",
            "End Sub"
        ]);

        var projection = CreateProjection("WorkerClass.cls", source);

        Assert.Equal("WorkerClass", projection.ModuleName);
        Assert.Equal(VbaModuleKind.ClassModule, projection.ModuleKind);
        Assert.Collection(
            projection.Lines,
            line => AssertExcluded(line, 0, VbaCodeModuleLineRole.ClassMetadata),
            line => AssertExcluded(line, 1, VbaCodeModuleLineRole.ClassMetadata),
            line => AssertExcluded(line, 2, VbaCodeModuleLineRole.ClassMetadata),
            line => AssertExcluded(line, 3, VbaCodeModuleLineRole.ClassMetadata),
            line => AssertExcluded(line, 4, VbaCodeModuleLineRole.ExportAttribute),
            line => AssertExcluded(line, 5, VbaCodeModuleLineRole.ExportAttribute),
            line => AssertProjected(line, 6, 1),
            line => AssertProjected(line, 7, 2),
            line => AssertProjected(line, 8, 3),
            line => AssertProjected(line, 9, 4));
    }

    [Fact]
    public void FormModuleProjectionExcludesDesignerContentAndAttributes()
    {
        var source = string.Join('\n', [
            "VERSION 5.00",
            "Begin VB.Form Dialog",
            "  Caption = \"Projection fixture\"",
            "End",
            "Attribute VB_Name = \"Dialog\"",
            "Attribute VB_PredeclaredId = True",
            "Option Explicit",
            "Private Sub Run()",
            "    Debug.Print \"run\"",
            "End Sub"
        ]);

        var projection = CreateProjection("Dialog.frm", source);

        Assert.Equal("Dialog", projection.ModuleName);
        Assert.Equal(VbaModuleKind.FormModule, projection.ModuleKind);
        Assert.Collection(
            projection.Lines,
            line => AssertExcluded(line, 0, VbaCodeModuleLineRole.FormDesigner),
            line => AssertExcluded(line, 1, VbaCodeModuleLineRole.FormDesigner),
            line => AssertExcluded(line, 2, VbaCodeModuleLineRole.FormDesigner),
            line => AssertExcluded(line, 3, VbaCodeModuleLineRole.FormDesigner),
            line => AssertExcluded(line, 4, VbaCodeModuleLineRole.ExportAttribute),
            line => AssertExcluded(line, 5, VbaCodeModuleLineRole.ExportAttribute),
            line => AssertProjected(line, 6, 2),
            line => AssertProjected(line, 7, 3),
            line => AssertProjected(line, 8, 4),
            line => AssertProjected(line, 9, 5));
        Assert.Equal(
            ["", "Option Explicit", "Private Sub Run()", "    Debug.Print \"run\"", "End Sub"],
            projection.CodeModuleLines);
    }

    [Fact]
    public void PhysicalLineExecutionFactsDistinguishInvalidLocationsFromExecutableCode()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "",
            "' comment",
            "Rem comment",
            "Public Sub Run()",
            "    Dim value As Long",
            "    Const LocalValue = 1",
            "    value = LocalValue",
            "End Sub"
        ]);

        var projection = CreateProjection("Worker.bas", source);

        Assert.Equal(VbaPhysicalLineExecutionKind.DeclarationOnly, Line(projection, 1).ExecutionKind);
        Assert.Equal(VbaPhysicalLineExecutionKind.Blank, Line(projection, 2).ExecutionKind);
        Assert.Equal(VbaPhysicalLineExecutionKind.Comment, Line(projection, 3).ExecutionKind);
        Assert.Equal(VbaPhysicalLineExecutionKind.Comment, Line(projection, 4).ExecutionKind);
        Assert.Equal(VbaPhysicalLineExecutionKind.ProcedureBoundary, Line(projection, 5).ExecutionKind);
        Assert.Equal(VbaPhysicalLineExecutionKind.DeclarationOnly, Line(projection, 6).ExecutionKind);
        Assert.Equal(VbaPhysicalLineExecutionKind.DeclarationOnly, Line(projection, 7).ExecutionKind);
        Assert.Equal(VbaPhysicalLineExecutionKind.ExecutableCandidate, Line(projection, 8).ExecutionKind);
        Assert.Equal(VbaPhysicalLineExecutionKind.ProcedureBoundary, Line(projection, 9).ExecutionKind);
    }

    [Fact]
    public void ContinuedStatementsKeepTheHeadExecutableAndRejectContinuationLines()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    value = 1 _",
            "        + 2",
            "    Call DoWork( _",
            "        value)",
            "End Sub"
        ]);

        var projection = CreateProjection("Worker.bas", source);

        Assert.Equal(VbaPhysicalLineExecutionKind.ExecutableCandidate, Line(projection, 2).ExecutionKind);
        Assert.Equal(VbaPhysicalLineExecutionKind.Continuation, Line(projection, 3).ExecutionKind);
        Assert.Equal(VbaPhysicalLineExecutionKind.ExecutableCandidate, Line(projection, 4).ExecutionKind);
        Assert.Equal(VbaPhysicalLineExecutionKind.Continuation, Line(projection, 5).ExecutionKind);
        Assert.Equal(2, Line(projection, 2).CodeModuleLine);
        Assert.Equal(3, Line(projection, 3).CodeModuleLine);
    }

    [Fact]
    public void ContinuationTailWithColonSeparatedExecutableStaysContinuationToPreventNativeVbeRelocation()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    value = 1 _",
            "        + 2: Debug.Print value",
            "End Sub"
        ]);

        var projection = CreateProjection("Worker.bas", source);

        Assert.Equal(VbaPhysicalLineExecutionKind.Continuation, Line(projection, 3).ExecutionKind);
        Assert.Equal(3, Line(projection, 3).CodeModuleLine);
    }

    [Fact]
    public void ColonSeparatedStatementsRetainOnePhysicalLineClassification()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    Dim value As Long: value = 1",
            "    value = 2: Debug.Print value",
            "OnlyLabel:",
            "    Dim first As Long: Const second = 2",
            "End Sub"
        ]);

        var projection = CreateProjection("Worker.bas", source);

        Assert.Equal(VbaPhysicalLineExecutionKind.ExecutableCandidate, Line(projection, 2).ExecutionKind);
        Assert.Equal(VbaPhysicalLineExecutionKind.ExecutableCandidate, Line(projection, 3).ExecutionKind);
        Assert.Equal(VbaPhysicalLineExecutionKind.LabelOnly, Line(projection, 4).ExecutionKind);
        Assert.Equal(VbaPhysicalLineExecutionKind.DeclarationOnly, Line(projection, 5).ExecutionKind);
        Assert.Equal(2, Line(projection, 2).CodeModuleLine);
        Assert.Equal(3, Line(projection, 3).CodeModuleLine);
    }

    [Fact]
    public void ModuleLevelTypeAndEnumMembersAreNeverExecutableCandidates()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Enum RunState",
            "    Ready = 1",
            "End Enum",
            "Private Type WorkItem",
            "    Name As String",
            "End Type",
            "Public Sub Run()",
            "    Ready = 2",
            "End Sub"
        ]);

        var projection = CreateProjection("Worker.bas", source);

        Assert.Equal(VbaPhysicalLineExecutionKind.DeclarationOnly, Line(projection, 1).ExecutionKind);
        Assert.Equal(VbaPhysicalLineExecutionKind.DeclarationOnly, Line(projection, 2).ExecutionKind);
        Assert.Equal(VbaPhysicalLineExecutionKind.DeclarationOnly, Line(projection, 3).ExecutionKind);
        Assert.Equal(VbaPhysicalLineExecutionKind.DeclarationOnly, Line(projection, 4).ExecutionKind);
        Assert.Equal(VbaPhysicalLineExecutionKind.DeclarationOnly, Line(projection, 5).ExecutionKind);
        Assert.Equal(VbaPhysicalLineExecutionKind.DeclarationOnly, Line(projection, 6).ExecutionKind);
        Assert.Equal(VbaPhysicalLineExecutionKind.ExecutableCandidate, Line(projection, 8).ExecutionKind);
    }

    [Fact]
    public void KeywordLedProcedureStatementsRemainExecutableCandidates()
    {
        var source = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "BEGIN",
            "  MultiUse = -1  'True",
            "END",
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    Me.RunNext",
            "    Get #1, , value",
            "End Sub"
        ]);

        var projection = CreateProjection("Worker.cls", source);

        Assert.Equal(VbaPhysicalLineExecutionKind.ExecutableCandidate, Line(projection, 6).ExecutionKind);
        Assert.Equal(VbaPhysicalLineExecutionKind.ExecutableCandidate, Line(projection, 7).ExecutionKind);
    }

    [Fact]
    public void NumberedAndSelectCaseStatementsPreserveExactExecutableClassification()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "10 Debug.Print \"numbered\"",
            "11 Dim value As Long",
            "12",
            "    Select Case value",
            "    Case 1",
            "        Print #1, value",
            "    Case Else",
            "        Open \"output.txt\" For Output As #1",
            "        Close #1",
            "        Put #1, , value",
            "    End Select",
            " 13 Debug.Print \"not first column\"",
            "End Sub"
        ]);

        var projection = CreateProjection("Worker.bas", source);

        Assert.Equal(VbaPhysicalLineExecutionKind.ExecutableCandidate, Line(projection, 2).ExecutionKind);
        Assert.Equal(VbaPhysicalLineExecutionKind.DeclarationOnly, Line(projection, 3).ExecutionKind);
        Assert.Equal(VbaPhysicalLineExecutionKind.LabelOnly, Line(projection, 4).ExecutionKind);
        Assert.Equal(VbaPhysicalLineExecutionKind.ExecutableCandidate, Line(projection, 6).ExecutionKind);
        Assert.Equal(VbaPhysicalLineExecutionKind.ExecutableCandidate, Line(projection, 7).ExecutionKind);
        Assert.Equal(VbaPhysicalLineExecutionKind.ExecutableCandidate, Line(projection, 8).ExecutionKind);
        Assert.Equal(VbaPhysicalLineExecutionKind.ExecutableCandidate, Line(projection, 9).ExecutionKind);
        Assert.Equal(VbaPhysicalLineExecutionKind.ExecutableCandidate, Line(projection, 10).ExecutionKind);
        Assert.Equal(VbaPhysicalLineExecutionKind.ExecutableCandidate, Line(projection, 11).ExecutionKind);
        Assert.Equal(VbaPhysicalLineExecutionKind.Unknown, Line(projection, 13).ExecutionKind);
    }

    [Fact]
    public void TerminalNewlineIsNotProjectedButThePrecedingRealBlankLineIsPreserved()
    {
        var source = string.Join("\r\n", [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    Debug.Print \"run\"",
            "End Sub",
            "",
            ""
        ]);

        var projection = CreateProjection("Worker.bas", source);
        var realBlankLine = Line(projection, 4);
        var terminalNewline = Line(projection, 5);

        Assert.Equal(VbaCodeModuleLineRole.Code, realBlankLine.Role);
        Assert.Equal(4, realBlankLine.CodeModuleLine);
        Assert.Equal(VbaPhysicalLineExecutionKind.Blank, realBlankLine.ExecutionKind);
        Assert.Equal(VbaCodeModuleLineRole.TerminalNewline, terminalNewline.Role);
        Assert.Null(terminalNewline.CodeModuleLine);
        Assert.Equal(VbaPhysicalLineExecutionKind.Blank, terminalNewline.ExecutionKind);
    }

    [Fact]
    public void FormProjectionAddsTheVbideLeadingBlankAndExcludesTheTerminalNewline()
    {
        var source = string.Join("\r\n", [
            "VERSION 5.00",
            "Begin VB.UserForm Dialog",
            "End",
            "Attribute VB_Name = \"Dialog\"",
            "Attribute VB_PredeclaredId = True",
            "Option Explicit",
            "",
            "Public Sub Run()",
            "    Debug.Print \"run\"",
            "End Sub",
            "",
            ""
        ]);

        var projection = CreateProjection("Dialog.frm", source);
        var terminalNewline = Line(projection, 11);

        Assert.Equal(VbaCodeModuleLineRole.TerminalNewline, terminalNewline.Role);
        Assert.Null(terminalNewline.CodeModuleLine);
        Assert.Equal(string.Empty, terminalNewline.Text);
        Assert.Equal(VbaPhysicalLineExecutionKind.Blank, terminalNewline.ExecutionKind);
        Assert.Equal(2, Line(projection, 5).CodeModuleLine);
        Assert.Equal(5, Line(projection, 8).CodeModuleLine);
        Assert.Equal(
            [
                "",
                "Option Explicit",
                "",
                "Public Sub Run()",
                "    Debug.Print \"run\"",
                "End Sub",
                ""
            ],
            projection.CodeModuleLines);
    }

    [Fact]
    public void ConditionalBranchMembershipIsPreservedWithoutGuessingTheActiveBranch()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "#If Win64 Then",
            "    Debug.Print \"Win64\"",
            "#Else",
            "    Debug.Print \"fallback\"",
            "#End If",
            "End Sub"
        ]);

        var projection = CreateProjection("Worker.bas", source);
        var win64 = Line(projection, 3);
        var fallback = Line(projection, 5);

        Assert.Equal(VbaPhysicalLineExecutionKind.Directive, Line(projection, 2).ExecutionKind);
        Assert.Equal(VbaPhysicalLineExecutionKind.ExecutableCandidate, win64.ExecutionKind);
        Assert.Equal(VbaPhysicalLineExecutionKind.ExecutableCandidate, fallback.ExecutionKind);
        Assert.NotNull(win64.ConditionalCompilationPath);
        Assert.NotNull(fallback.ConditionalCompilationPath);
        Assert.Single(win64.ConditionalCompilationPath.Branches);
        Assert.Single(fallback.ConditionalCompilationPath.Branches);
        Assert.NotEqual(win64.ConditionalCompilationPath, fallback.ConditionalCompilationPath);
    }

    private static VbaCodeModuleProjection CreateProjection(string fileName, string source)
    {
        var tree = VbaSyntaxTree.ParseModule($"file:///C:/work/{fileName}", source);
        return VbaCodeModuleProjection.Create(tree);
    }

    private static VbaCodeModuleLineProjection Line(
        VbaCodeModuleProjection projection,
        int sourceLine)
        => Assert.Single(projection.Lines, line => line.SourceLine == sourceLine);

    private static void AssertExcluded(
        VbaCodeModuleLineProjection line,
        int sourceLine,
        VbaCodeModuleLineRole role)
    {
        Assert.Equal(sourceLine, line.SourceLine);
        Assert.Null(line.CodeModuleLine);
        Assert.Equal(role, line.Role);
    }

    private static void AssertProjected(
        VbaCodeModuleLineProjection line,
        int sourceLine,
        int codeModuleLine)
    {
        Assert.Equal(sourceLine, line.SourceLine);
        Assert.Equal(codeModuleLine, line.CodeModuleLine);
        Assert.Equal(VbaCodeModuleLineRole.Code, line.Role);
    }
}
