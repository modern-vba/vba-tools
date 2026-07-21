using System.Collections.Immutable;
using VbaDev.App.Debugging;
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
    }

    [Fact]
    public void AnUnrecognizedProcedureAttributeBeforeTheRequestedLineFailsClosed()
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

        var error = Assert.Throws<DebugSetupException>(() => new BreakpointSourceMapper().Map(
            snapshot,
            new DebugSourceBreakpoint(sourcePath, EditorLine: 4)));

        Assert.Contains("Attribute", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot map", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
