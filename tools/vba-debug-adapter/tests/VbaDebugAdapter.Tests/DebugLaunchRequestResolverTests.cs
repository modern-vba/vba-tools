using VbaDebugAdapter.Debugging;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class DebugLaunchRequestResolverTests
{
    [Fact]
    public void ExplicitTargetResolvesFromTheTransportedSourceInsteadOfPersistentDisk()
    {
        const string sourceUri = "file:///C:/persistent/Module1.bas";
        var snapshot = new DebugSourceSnapshot(
            DebugSourceSnapshot.CurrentSchemaVersion,
            [
                new DebugSourceFileSnapshot(
                    "nested/Module1.bas",
                    sourceUri,
                    "Attribute VB_Name = \"Module1\"\r\nPublic Sub Run()\r\nEnd Sub\r\n")
            ],
            ActiveSource: null);

        var request = new DebugLaunchRequestResolver().Resolve(
            snapshot,
            "Module1",
            "Run");

        Assert.Equal("Module1", request.Target.ModuleName);
        Assert.Equal("Run", request.Target.ProcedureName);
        Assert.Same(snapshot, request.SourceSnapshot);
        Assert.Empty(request.Target.ConditionalCompilationPath.Branches);
    }

    [Fact]
    public void ExplicitTargetAcceptsCaseInsensitiveExportedSourceExtensions()
    {
        var snapshot = new DebugSourceSnapshot(
            DebugSourceSnapshot.CurrentSchemaVersion,
            [
                new DebugSourceFileSnapshot(
                    "MODULE1.BAS",
                    "file:///C:/persistent/MODULE1.BAS",
                    "Attribute VB_Name = \"Module1\"\r\nPublic Sub Run()\r\nEnd Sub\r\n")
            ],
            ActiveSource: null);

        var request = new DebugLaunchRequestResolver().Resolve(
            snapshot,
            "Module1",
            "Run");

        Assert.Equal("Module1", request.Target.ModuleName);
    }

    [Fact]
    public void ImplicitPublicSubInAnOptionPrivateStandardModuleIsEligible()
    {
        const string sourceUri = "file:///C:/persistent/DebugModule.bas";
        var snapshot = new DebugSourceSnapshot(
            DebugSourceSnapshot.CurrentSchemaVersion,
            [
                new DebugSourceFileSnapshot(
                    "DebugModule.bas",
                    sourceUri,
                    "Attribute VB_Name = \"DebugModule\"\r\n" +
                    "Option Private Module\r\n\r\n" +
                    "Sub RunTarget()\r\nEnd Sub\r\n")
            ],
            ActiveSource: null);

        var request = new DebugLaunchRequestResolver().Resolve(
            snapshot,
            "debugmodule",
            "runtarget");

        Assert.Equal(new DebugTargetProcedure("DebugModule", "RunTarget"), request.Target);
    }

    [Fact]
    public void FunctionIsRejectedBeforeBuild()
    {
        var snapshot = Snapshot(
            ".bas",
            "Attribute VB_Name = \"DebugModule\"\r\n" +
            "Public Function RunTarget() As Long\r\n" +
            "    RunTarget = 1\r\nEnd Function\r\n");

        var error = Assert.Throws<DebugSetupException>(() =>
            new DebugLaunchRequestResolver().Resolve(
                snapshot,
                "DebugModule",
                "RunTarget"));

        Assert.Contains("Sub", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PrivateSubIsRejectedBeforeBuild()
    {
        var snapshot = Snapshot(
            ".bas",
            "Attribute VB_Name = \"DebugModule\"\r\n" +
            "Private Sub RunTarget()\r\nEnd Sub\r\n");

        var error = Assert.Throws<DebugSetupException>(() =>
            new DebugLaunchRequestResolver().Resolve(
                snapshot,
                "DebugModule",
                "RunTarget"));

        Assert.Contains("public", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParameterizedSubIsRejectedBeforeBuild()
    {
        var snapshot = Snapshot(
            ".bas",
            "Attribute VB_Name = \"DebugModule\"\r\n" +
            "Public Sub RunTarget(ByVal value As Long)\r\nEnd Sub\r\n");

        var error = Assert.Throws<DebugSetupException>(() =>
            new DebugLaunchRequestResolver().Resolve(
                snapshot,
                "DebugModule",
                "RunTarget"));

        Assert.Contains("parameterless", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExternalDeclareSubIsRejectedBeforeBuild()
    {
        var snapshot = Snapshot(
            ".bas",
            "Attribute VB_Name = \"DebugModule\"\r\n" +
            "Public Declare PtrSafe Sub RunTarget Lib \"kernel32\" ()\r\n");

        var error = Assert.Throws<DebugSetupException>(() =>
            new DebugLaunchRequestResolver().Resolve(
                snapshot,
                "DebugModule",
                "RunTarget"));

        Assert.Contains("Declare", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClassModuleSubIsRejectedBeforeBuild()
    {
        var snapshot = Snapshot(
            ".cls",
            "VERSION 1.0 CLASS\r\n" +
            "Attribute VB_Name = \"DebugModule\"\r\n" +
            "Public Sub RunTarget()\r\nEnd Sub\r\n");

        var error = Assert.Throws<DebugSetupException>(() =>
            new DebugLaunchRequestResolver().Resolve(
                snapshot,
                "DebugModule",
                "RunTarget"));

        Assert.Contains("standard module", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ActivePositionResolvesFromTheTransportedText()
    {
        const string sourceUri = "file:///C:/persistent/DebugModule.bas";
        const string source =
            "Attribute VB_Name = \"DebugModule\"\r\n\r\n" +
            "Public Sub CapturedTarget()\r\n" +
            "    Debug.Print \"captured\"\r\n" +
            "End Sub\r\n";
        var snapshot = new DebugSourceSnapshot(
            DebugSourceSnapshot.CurrentSchemaVersion,
            [new DebugSourceFileSnapshot("DebugModule.bas", sourceUri, source)],
            new DebugSourcePosition(sourceUri, Line: 3, Character: 4));

        var request = new DebugLaunchRequestResolver().Resolve(
            snapshot,
            moduleName: null,
            procedureName: null);

        Assert.Equal(new DebugTargetProcedure("DebugModule", "CapturedTarget"), request.Target);
        Assert.Equal(source, Assert.Single(request.SourceSnapshot.Sources).Text);
    }

    [Fact]
    public void ActivePositionRejectsAnAmbiguousModuleIdentity()
    {
        const string activeUri = "file:///C:/persistent/DebugModule.bas";
        var snapshot = new DebugSourceSnapshot(
            DebugSourceSnapshot.CurrentSchemaVersion,
            [
                new DebugSourceFileSnapshot(
                    "Alpha.bas",
                    "file:///C:/persistent/Alpha.bas",
                    "Attribute VB_Name = \"DebugModule\"\r\n" +
                    "Public Sub OtherTarget()\r\nEnd Sub\r\n"),
                new DebugSourceFileSnapshot(
                    "DebugModule.bas",
                    activeUri,
                    "Attribute VB_Name = \"DebugModule\"\r\n" +
                    "Public Sub RunTarget()\r\n" +
                    "    Debug.Print \"ready\"\r\nEnd Sub\r\n")
            ],
            new DebugSourcePosition(activeUri, Line: 2, Character: 4));

        var error = Assert.Throws<DebugSetupException>(() =>
            new DebugLaunchRequestResolver().Resolve(
                snapshot,
                moduleName: null,
                procedureName: null));

        Assert.Contains("module", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ambiguous", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static DebugSourceSnapshot Snapshot(string extension, string source)
    {
        var relativePath = $"DebugModule{extension}";
        return new DebugSourceSnapshot(
            DebugSourceSnapshot.CurrentSchemaVersion,
            [
                new DebugSourceFileSnapshot(
                    relativePath,
                    $"file:///C:/persistent/{relativePath}",
                    source)
            ],
            ActiveSource: null);
    }
}
