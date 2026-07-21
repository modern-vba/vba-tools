using System.Collections.Immutable;
using VbaDev.App.Debugging;
using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaDev.Tests;

public sealed class BreakpointSourceMapperTests
{
    [Fact]
    public void AnExecutableStandardModuleLineMapsExactlyAfterExportOnlyAttributesAreRemoved()
    {
        var sourcePath = Path.GetFullPath(Path.Combine("DebugProject", "DebugModule.bas"));
        var source = string.Join('\n',
        [
            "Attribute VB_Name = \"DebugModule\"",
            "Attribute VB_Description = \"Debug module\"",
            "Option Explicit",
            "",
            "Public Sub RunTarget()",
            "    value = 1",
            "End Sub"
        ]);
        var snapshot = new DebugSourceSnapshot(
            DebugSourceSnapshot.CurrentSchemaVersion,
            ImmutableArray.Create(new DebugSourceFileSnapshot(sourcePath, source)),
            null);
        var requested = new DebugSourceBreakpoint(sourcePath, EditorLine: 5);

        var mapped = new BreakpointSourceMapper().Map(snapshot, requested);

        Assert.Equal(requested, mapped.Source);
        Assert.Equal("DebugModule", mapped.ModuleName);
        Assert.Equal(4, mapped.VbideLine);
        Assert.Equal("    value = 1", mapped.ExpectedCodeLine);
        Assert.Equal(VbaModuleKind.StandardModule, mapped.SourceMap.ModuleKind);
        Assert.Equal(
            [
                "Option Explicit",
                "",
                "Public Sub RunTarget()",
                "    value = 1",
                "End Sub"
            ],
            mapped.SourceMap.CodeLines.ToArray());
    }

    [Fact]
    public void AProcedureAttributeIsExcludedByTheSharedSyntaxProjection()
    {
        var sourcePath = Path.GetFullPath(Path.Combine("DebugProject", "DebugModule.bas"));
        var source = string.Join('\n',
        [
            "Attribute VB_Name = \"DebugModule\"",
            "Public Sub RunTarget()",
            "Attribute RunTarget.VB_Description = \"export-only metadata\"",
            "    Debug.Print \"same text\"",
            "    Debug.Print \"same text\"",
            "    Debug.Print \"same text\"",
            "End Sub"
        ]);
        var snapshot = new DebugSourceSnapshot(
            DebugSourceSnapshot.CurrentSchemaVersion,
            ImmutableArray.Create(new DebugSourceFileSnapshot(sourcePath, source)),
            null);

        var mapped = new BreakpointSourceMapper().Map(
            snapshot,
            new DebugSourceBreakpoint(sourcePath, EditorLine: 4));

        Assert.Equal("DebugModule", mapped.ModuleName);
        Assert.Equal(3, mapped.VbideLine);
        Assert.Equal("    Debug.Print \"same text\"", mapped.ExpectedCodeLine);
        Assert.Equal(5, mapped.SourceMap.CodeLines.Length);
    }

    [Theory]
    [InlineData("Worker.cls", 8, "Worker", 3, VbaModuleKind.ClassModule)]
    [InlineData("Dialog.frm", 8, "Dialog", 4, VbaModuleKind.FormModule)]
    public void ObjectModuleExecutableLinesMapAfterExportOnlyContent(
        string fileName,
        int editorLine,
        string moduleName,
        int vbideLine,
        VbaModuleKind moduleKind)
    {
        var sourcePath = Path.GetFullPath(Path.Combine("DebugProject", fileName));
        var source = Path.GetExtension(fileName).Equals(".cls", StringComparison.OrdinalIgnoreCase)
            ? string.Join('\n',
            [
                "VERSION 1.0 CLASS",
                "BEGIN",
                "  MultiUse = -1  'True",
                "END",
                "Attribute VB_Name = \"Worker\"",
                "Attribute VB_Exposed = False",
                "Option Explicit",
                "Public Sub Run()",
                "    Debug.Print \"class\"",
                "End Sub"
            ])
            : string.Join('\n',
            [
                "VERSION 5.00",
                "Begin VB.Form Dialog",
                "  Caption = \"Dialog\"",
                "End",
                "Attribute VB_Name = \"Dialog\"",
                "Attribute VB_PredeclaredId = True",
                "Option Explicit",
                "Private Sub Run()",
                "    Debug.Print \"form\"",
                "End Sub"
            ]);

        var mapped = new BreakpointSourceMapper().Map(
            Snapshot(sourcePath, source),
            new DebugSourceBreakpoint(sourcePath, editorLine));

        Assert.Equal(moduleName, mapped.ModuleName);
        Assert.Equal(vbideLine, mapped.VbideLine);
        Assert.Equal(moduleKind, mapped.SourceMap.ModuleKind);
        var formLineOffset = moduleKind == VbaModuleKind.FormModule ? 1 : 0;
        Assert.Equal(4 + formLineOffset, mapped.SourceMap.CodeLines.Length);
        if (moduleKind == VbaModuleKind.FormModule)
        {
            Assert.Equal(string.Empty, mapped.SourceMap.CodeLines[0]);
        }

        Assert.Equal("Option Explicit", mapped.SourceMap.CodeLines[formLineOffset]);
    }

    [Theory]
    [InlineData(2, "blank")]
    [InlineData(3, "comment")]
    [InlineData(5, "declaration")]
    [InlineData(7, "continuation")]
    [InlineData(8, "label")]
    public void NonExecutablePhysicalLocationsFailWithSpecificInvalidBreakpointErrors(
        int editorLine,
        string expectedReason)
    {
        var sourcePath = Path.GetFullPath(Path.Combine("DebugProject", "Worker.bas"));
        var source = string.Join('\n',
        [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "",
            "    ' comment",
            "    Dim first As Long: first = 1",
            "    Dim second As Long: Const third = 3",
            "    first = 1 _",
            "        + 2",
            "OnlyLabel:",
            "End Sub"
        ]);

        var error = Assert.Throws<DebugSetupException>(() => new BreakpointSourceMapper().Map(
            Snapshot(sourcePath, source),
            new DebugSourceBreakpoint(sourcePath, editorLine)));

        Assert.Contains("invalid breakpoint", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(expectedReason, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BreakpointOnContinuationTailWithColonSeparatedExecutableFailsToPreventNativeVbeRelocation()
    {
        var sourcePath = Path.GetFullPath(Path.Combine("DebugProject", "Worker.bas"));
        var source = string.Join('\n',
        [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    value = 1 _",
            "        + 2: Debug.Print value",
            "End Sub"
        ]);

        var error = Assert.Throws<DebugSetupException>(() => new BreakpointSourceMapper().Map(
            Snapshot(sourcePath, source),
            new DebugSourceBreakpoint(sourcePath, EditorLine: 3)));

        Assert.Contains("invalid breakpoint", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("non-executable continuation", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not relocated", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(4, 4)]
    [InlineData(6, 6)]
    public void ExecutableColonAndContinuationHeadLinesKeepTheirExactPhysicalIdentity(
        int editorLine,
        int vbideLine)
    {
        var sourcePath = Path.GetFullPath(Path.Combine("DebugProject", "Worker.bas"));
        var source = string.Join('\n',
        [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "",
            "    ' comment",
            "    Dim first As Long: first = 1",
            "    Dim second As Long: Const third = 3",
            "    first = 1 _",
            "        + 2",
            "End Sub"
        ]);

        var mapped = new BreakpointSourceMapper().Map(
            Snapshot(sourcePath, source),
            new DebugSourceBreakpoint(sourcePath, editorLine));

        Assert.Equal(editorLine, mapped.Source.EditorLine);
        Assert.Equal(vbideLine, mapped.VbideLine);
    }

    [Theory]
    [InlineData(2, 2, "10 Debug.Print \"numbered\"")]
    [InlineData(4, 4, "    Case 1")]
    [InlineData(6, 6, "    Case Else")]
    public void NumberedAndSelectCaseBreakpointsMapToTheirExactPhysicalLines(
        int editorLine,
        int vbideLine,
        string expectedCodeLine)
    {
        var sourcePath = Path.GetFullPath(Path.Combine("DebugProject", "Worker.bas"));
        var source = string.Join('\n',
        [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "10 Debug.Print \"numbered\"",
            "    Select Case 1",
            "    Case 1",
            "        Debug.Print \"one\"",
            "    Case Else",
            "        Debug.Print \"other\"",
            "    End Select",
            "End Sub"
        ]);

        var mapped = new BreakpointSourceMapper().Map(
            Snapshot(sourcePath, source),
            new DebugSourceBreakpoint(sourcePath, editorLine));

        Assert.Equal(editorLine, mapped.Source.EditorLine);
        Assert.Equal(vbideLine, mapped.VbideLine);
        Assert.Equal(expectedCodeLine, mapped.ExpectedCodeLine);
    }

    [Fact]
    public void ConditionalBreakpointCarriesTheExactStructuralBranchPath()
    {
        var sourcePath = Path.GetFullPath(Path.Combine("DebugProject", "Worker.bas"));
        var source = string.Join('\n',
        [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "#If VBA7 Then",
            "    Debug.Print \"modern\"",
            "#Else",
            "    Debug.Print \"legacy\"",
            "#End If",
            "End Sub"
        ]);
        var snapshot = Snapshot(sourcePath, source);

        var modern = new BreakpointSourceMapper().Map(
            snapshot,
            new DebugSourceBreakpoint(sourcePath, EditorLine: 3));
        var legacy = new BreakpointSourceMapper().Map(
            snapshot,
            new DebugSourceBreakpoint(sourcePath, EditorLine: 5));

        Assert.NotNull(modern.ConditionalCompilationPath);
        Assert.NotNull(legacy.ConditionalCompilationPath);
        Assert.Single(modern.ConditionalCompilationPath.Branches);
        Assert.Single(legacy.ConditionalCompilationPath.Branches);
        Assert.NotEqual(
            modern.ConditionalCompilationPath.Branches[0],
            legacy.ConditionalCompilationPath.Branches[0]);
    }

    [Fact]
    public void MalformedConditionalStructureFailsInsteadOfDroppingActivationIdentity()
    {
        var sourcePath = Path.GetFullPath(Path.Combine("DebugProject", "Worker.bas"));
        var source = string.Join('\n',
        [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "#If VBA7 Then",
            "    Debug.Print \"unclosed\"",
            "End Sub"
        ]);

        var error = Assert.Throws<DebugSetupException>(() => new BreakpointSourceMapper().Map(
            Snapshot(sourcePath, source),
            new DebugSourceBreakpoint(sourcePath, EditorLine: 3)));

        Assert.Contains("conditional-compilation", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not relocated", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrDuplicateExportedModuleIdentityFailsClosed(bool duplicate)
    {
        var sourcePath = Path.GetFullPath(Path.Combine("DebugProject", "Worker.bas"));
        var sourceLines = new List<string>();
        if (duplicate)
        {
            sourceLines.Add("Attribute VB_Name = \"Worker\"");
            sourceLines.Add("Attribute VB_Name = \"Other\"");
        }

        sourceLines.AddRange(
        [
            "Public Sub Run()",
            "    Debug.Print \"run\"",
            "End Sub"
        ]);
        var editorLine = duplicate ? 3 : 1;

        var error = Assert.Throws<DebugSetupException>(() => new BreakpointSourceMapper().Map(
            Snapshot(sourcePath, string.Join('\n', sourceLines)),
            new DebugSourceBreakpoint(sourcePath, editorLine)));

        Assert.Contains("module identity", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Other.bas")]
    [InlineData("Other.cls")]
    [InlineData("Other.frm")]
    public void CaseInsensitiveModuleIdentityCollisionAcrossSnapshotFailsClosed(
        string conflictingFileName)
    {
        var sourcePath = Path.GetFullPath(Path.Combine("DebugProject", "Worker.bas"));
        var conflictingPath = Path.GetFullPath(Path.Combine("DebugProject", conflictingFileName));
        var snapshot = new DebugSourceSnapshot(
            DebugSourceSnapshot.CurrentSchemaVersion,
            ImmutableArray.Create(
                new DebugSourceFileSnapshot(
                    sourcePath,
                    CreateExportedSource("Worker.bas", "Worker")),
                new DebugSourceFileSnapshot(
                    conflictingPath,
                    CreateExportedSource(conflictingFileName, "worker"))),
            null);

        var error = Assert.Throws<DebugSetupException>(() => new BreakpointSourceMapper().Map(
            snapshot,
            new DebugSourceBreakpoint(sourcePath, EditorLine: 2)));

        Assert.Contains("invalid breakpoint", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("module identity", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ambiguous", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateExportedSource(string fileName, string moduleName)
        => Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".bas" => string.Join('\n',
            [
                $"Attribute VB_Name = \"{moduleName}\"",
                "Public Sub Run()",
                "    Debug.Print \"run\"",
                "End Sub"
            ]),
            ".cls" => string.Join('\n',
            [
                "VERSION 1.0 CLASS",
                "BEGIN",
                "  MultiUse = -1  'True",
                "END",
                $"Attribute VB_Name = \"{moduleName}\"",
                "Public Sub Run()",
                "    Debug.Print \"run\"",
                "End Sub"
            ]),
            ".frm" => string.Join('\n',
            [
                "VERSION 5.00",
                $"Begin VB.Form {moduleName}",
                "End",
                $"Attribute VB_Name = \"{moduleName}\"",
                "Public Sub Run()",
                "    Debug.Print \"run\"",
                "End Sub"
            ]),
            _ => throw new ArgumentOutOfRangeException(nameof(fileName), fileName, null)
        };

    private static DebugSourceSnapshot Snapshot(string sourcePath, string source)
        => new(
            DebugSourceSnapshot.CurrentSchemaVersion,
            ImmutableArray.Create(new DebugSourceFileSnapshot(sourcePath, source)),
            null);
}
