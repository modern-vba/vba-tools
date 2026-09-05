using VbaDebugAdapter.Build;
using VbaDebugAdapter.Debugging;
using VbaDebugAdapter.Infrastructure;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class DebugSourceAdmissionTargetTests
{
    [Fact]
    public void ExplicitTargetResolvesFromTheTransportedSourceInsteadOfPersistentDisk()
    {
        const string sourceUri =
            "file:///C:/definitely-missing-vba-debug-source/nested/Module1.bas";
        var snapshot = new TransportedDebugSourceSnapshot(
            2,
            [
                TextSource(
                    "nested/Module1.bas",
                    sourceUri,
                    "Attribute VB_Name = \"Module1\"\r\n" +
                    "Public Sub Run()\r\nEnd Sub\r\n")
            ]);

        var admitted = Admit(snapshot, "Module1", "Run");

        Assert.Equal("Module1", admitted.Target.ModuleName);
        Assert.Equal("Run", admitted.Target.ProcedureName);
        Assert.Empty(admitted.Target.ConditionalCompilationPath.Branches);
        Assert.Equal(DebugGenerationId.Initial, admitted.GenerationId);
        Assert.Equal(1, admitted.BuildSources.Count);
    }

    [Fact]
    public void ExplicitTargetAcceptsCaseInsensitiveExportedSourceExtensions()
    {
        var snapshot = new TransportedDebugSourceSnapshot(
            2,
            [
                TextSource(
                    "MODULE1.BAS",
                    "file:///C:/persistent/MODULE1.BAS",
                    "Attribute VB_Name = \"Module1\"\r\n" +
                    "Public Sub Run()\r\nEnd Sub\r\n")
            ]);

        var admitted = Admit(snapshot, "Module1", "Run");

        Assert.Equal("Module1", admitted.Target.ModuleName);
    }

    [Fact]
    public void ExplicitTargetPreservesACodePageIdentifierThatDotNetTreatsAsWhitespace()
    {
        const string moduleName = "\u00A0";
        const string procedureName = "集計";
        var snapshot = new TransportedDebugSourceSnapshot(
            2,
            [
                TextSource(
                    "CodePage.bas",
                    "file:///C:/persistent/CodePage.bas",
                    $"Attribute VB_Name = \"{moduleName}\"\r\n" +
                    $"Public Sub {procedureName}()\r\nEnd Sub\r\n")
            ]);

        var admitted = Admit(snapshot, moduleName, procedureName);

        Assert.Equal(moduleName, admitted.Target.ModuleName);
        Assert.Equal(procedureName, admitted.Target.ProcedureName);
    }

    [Fact]
    public void ExplicitTargetPreservesACodePageProcedureThatDotNetTreatsAsWhitespace()
    {
        const string procedureName = "\u00A0";
        var snapshot = Snapshot(
            ".bas",
            "Attribute VB_Name = \"DebugModule\"\r\n" +
            $"Public Sub {procedureName}()\r\nEnd Sub\r\n");

        var admitted = Admit(snapshot, "DebugModule", procedureName);

        Assert.Equal(procedureName, admitted.Target.ProcedureName);
    }

    [Fact]
    public void ExplicitTargetRejectsNamesOutsideTheSharedIdentifierAuthority()
    {
        var snapshot = Snapshot(
            ".bas",
            "Attribute VB_Name = \"DebugModule\"\r\n" +
            "Public Sub RunTarget()\r\nEnd Sub\r\n");
        var invalidNames = new[]
        {
            "Bad Name",
            "CDecl",
            "Name$",
            "亜ㄱ"
        };

        foreach (var invalidName in invalidNames)
        {
            var moduleError = Assert.Throws<DebugSetupException>(() =>
                Admit(snapshot, invalidName, "RunTarget"));
            var procedureError = Assert.Throws<DebugSetupException>(() =>
                Admit(snapshot, "DebugModule", invalidName));

            Assert.Contains("IDENTIFIER", moduleError.Message, StringComparison.Ordinal);
            Assert.Contains("IDENTIFIER", procedureError.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ExplicitEmptyTargetIsInvalidInsteadOfMeaningOmitted()
    {
        var snapshot = Snapshot(
            ".bas",
            "Attribute VB_Name = \"DebugModule\"\r\n" +
            "Public Sub RunTarget()\r\nEnd Sub\r\n");

        var error = Assert.Throws<DebugSetupException>(() =>
            Admit(snapshot, "", ""));

        Assert.Contains("IDENTIFIER", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ImplicitPublicSubInAnOptionPrivateStandardModuleIsEligible()
    {
        var snapshot = Snapshot(
            ".bas",
            "Attribute VB_Name = \"DebugModule\"\r\n" +
            "Option Private Module\r\n\r\n" +
            "Sub RunTarget()\r\nEnd Sub\r\n");

        var admitted = Admit(snapshot, "debugmodule", "runtarget");

        Assert.Equal(new DebugTargetProcedure("DebugModule", "RunTarget"), admitted.Target);
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
            Admit(snapshot, "DebugModule", "RunTarget"));

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
            Admit(snapshot, "DebugModule", "RunTarget"));

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
            Admit(snapshot, "DebugModule", "RunTarget"));

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
            Admit(snapshot, "DebugModule", "RunTarget"));

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
            Admit(snapshot, "DebugModule", "RunTarget"));

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
        var snapshot = new TransportedDebugSourceSnapshot(
            2,
            [TextSource("DebugModule.bas", sourceUri, source)])
        {
            ActiveSource = new TransportedDebugSourcePosition(
                sourceUri,
                Line: 3,
                Character: 4)
        };

        var admitted = Admit(snapshot, moduleName: null, procedureName: null);

        Assert.Equal(
            new DebugTargetProcedure("DebugModule", "CapturedTarget"),
            admitted.Target);
        Assert.Equal(
            new DebugSourcePosition(sourceUri, Line: 3, Character: 4),
            admitted.ActiveSource);
    }

    [Fact]
    public void ActivePositionRejectsAnAmbiguousModuleIdentity()
    {
        const string activeUri = "file:///C:/persistent/DebugModule.bas";
        var snapshot = new TransportedDebugSourceSnapshot(
            2,
            [
                TextSource(
                    "Alpha.bas",
                    "file:///C:/persistent/Alpha.bas",
                    "Attribute VB_Name = \"DebugModule\"\r\n" +
                    "Public Sub OtherTarget()\r\nEnd Sub\r\n"),
                TextSource(
                    "DebugModule.bas",
                    activeUri,
                    "Attribute VB_Name = \"DebugModule\"\r\n" +
                    "Public Sub RunTarget()\r\n" +
                    "    Debug.Print \"ready\"\r\nEnd Sub\r\n")
            ])
        {
            ActiveSource = new TransportedDebugSourcePosition(
                activeUri,
                Line: 2,
                Character: 4)
        };

        var error = Assert.Throws<DebugSetupException>(() =>
            Admit(snapshot, moduleName: null, procedureName: null));

        Assert.Contains("module", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ambiguous", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static AdmittedDebugSourceSnapshot Admit(
        TransportedDebugSourceSnapshot snapshot,
        string? moduleName,
        string? procedureName)
        => new DebugSourceAdmission(932).Admit(
            snapshot,
            moduleName,
            procedureName,
            DebugGenerationId.Initial);

    private static TransportedDebugSourceSnapshot Snapshot(
        string extension,
        string source)
    {
        var relativePath = $"DebugModule{extension}";
        return new TransportedDebugSourceSnapshot(
            2,
            [
                TextSource(
                    relativePath,
                    $"file:///C:/persistent/{relativePath}",
                    source)
            ]);
    }

    private static TransportedDebugSource TextSource(
        string relativePath,
        string sourceUri,
        string text)
        => new(
            relativePath,
            sourceUri,
            "utf8bom",
            Convert.ToBase64String(DebugSnapshotTestEncoding.Utf8BomBytes(text)));
}
