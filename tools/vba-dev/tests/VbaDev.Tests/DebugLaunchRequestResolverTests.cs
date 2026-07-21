using System.Collections.Immutable;
using VbaDev.App.Debugging;
using VbaDev.App.Projects;
using VbaDev.Domain;
using Xunit;

namespace VbaDev.Tests;

public sealed class DebugLaunchRequestResolverTests
{
    [Fact]
    public void ExplicitImplicitPublicSubInOptionPrivateModuleIsEligible()
    {
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp.CreateDirectory("DebugProject"));
        Directory.CreateDirectory(context.DocumentSourceSetPath);
        var sourcePath = Path.Combine(context.DocumentSourceSetPath, "DebugModule.bas");
        var source =
            """
            Attribute VB_Name = "DebugModule"
            Option Private Module

            Sub RunTarget()
            End Sub
            """;
        File.WriteAllText(sourcePath, source);
        var snapshot = new DebugSourceSnapshot(
            SchemaVersion: 1,
            Sources: [new DebugSourceFileSnapshot(sourcePath, source)],
            ActiveSource: null);

        var request = new DebugLaunchRequestResolver().Resolve(
            context,
            snapshot,
            moduleName: "debugmodule",
            procedureName: "runtarget");

        Assert.Same(snapshot, request.SourceSnapshot);
        Assert.Equal("DebugModule", request.Target.ModuleName);
        Assert.Equal("RunTarget", request.Target.ProcedureName);
    }

    [Fact]
    public void FunctionIsRejectedBeforeBuild()
    {
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp.CreateDirectory("DebugProject"));
        Directory.CreateDirectory(context.DocumentSourceSetPath);
        var sourcePath = Path.Combine(context.DocumentSourceSetPath, "DebugModule.bas");
        var source =
            """
            Attribute VB_Name = "DebugModule"

            Public Function RunTarget() As Long
                RunTarget = 1
            End Function
            """;
        File.WriteAllText(sourcePath, source);
        var snapshot = new DebugSourceSnapshot(
            SchemaVersion: 1,
            Sources: [new DebugSourceFileSnapshot(sourcePath, source)],
            ActiveSource: null);

        var error = Assert.Throws<DebugSetupException>(() =>
            new DebugLaunchRequestResolver().Resolve(
                context,
                snapshot,
                moduleName: "DebugModule",
                procedureName: "RunTarget"));

        Assert.Contains("Sub", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PrivateSubIsRejectedBeforeBuild()
    {
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp.CreateDirectory("DebugProject"));
        Directory.CreateDirectory(context.DocumentSourceSetPath);
        var sourcePath = Path.Combine(context.DocumentSourceSetPath, "DebugModule.bas");
        var source =
            """
            Attribute VB_Name = "DebugModule"

            Private Sub RunTarget()
            End Sub
            """;
        File.WriteAllText(sourcePath, source);
        var snapshot = new DebugSourceSnapshot(
            SchemaVersion: 1,
            Sources: [new DebugSourceFileSnapshot(sourcePath, source)],
            ActiveSource: null);

        var error = Assert.Throws<DebugSetupException>(() =>
            new DebugLaunchRequestResolver().Resolve(
                context,
                snapshot,
                moduleName: "DebugModule",
                procedureName: "RunTarget"));

        Assert.Contains("public", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Friend")]
    [InlineData("Global")]
    public void NonPublicVisibilityKeywordIsRejectedBeforeBuild(string visibilityKeyword)
    {
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp.CreateDirectory("DebugProject"));
        Directory.CreateDirectory(context.DocumentSourceSetPath);
        var sourcePath = Path.Combine(context.DocumentSourceSetPath, "DebugModule.bas");
        var source =
            $"""
            Attribute VB_Name = "DebugModule"

            {visibilityKeyword} Sub RunTarget()
            End Sub
            """;
        File.WriteAllText(sourcePath, source);
        var snapshot = new DebugSourceSnapshot(
            SchemaVersion: 1,
            Sources: [new DebugSourceFileSnapshot(sourcePath, source)],
            ActiveSource: null);

        var error = Assert.Throws<DebugSetupException>(() =>
            new DebugLaunchRequestResolver().Resolve(
                context,
                snapshot,
                moduleName: "DebugModule",
                procedureName: "RunTarget"));

        Assert.Contains("public", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParameterizedSubIsRejectedBeforeBuild()
    {
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp.CreateDirectory("DebugProject"));
        Directory.CreateDirectory(context.DocumentSourceSetPath);
        var sourcePath = Path.Combine(context.DocumentSourceSetPath, "DebugModule.bas");
        var source =
            """
            Attribute VB_Name = "DebugModule"

            Public Sub RunTarget(ByVal value As Long)
            End Sub
            """;
        File.WriteAllText(sourcePath, source);
        var snapshot = new DebugSourceSnapshot(
            SchemaVersion: 1,
            Sources: [new DebugSourceFileSnapshot(sourcePath, source)],
            ActiveSource: null);

        var error = Assert.Throws<DebugSetupException>(() =>
            new DebugLaunchRequestResolver().Resolve(
                context,
                snapshot,
                moduleName: "DebugModule",
                procedureName: "RunTarget"));

        Assert.Contains("parameterless", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExternalDeclareSubIsRejectedBeforeBuild()
    {
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp.CreateDirectory("DebugProject"));
        Directory.CreateDirectory(context.DocumentSourceSetPath);
        var sourcePath = Path.Combine(context.DocumentSourceSetPath, "DebugModule.bas");
        var source =
            """
            Attribute VB_Name = "DebugModule"

            Public Declare PtrSafe Sub RunTarget Lib "kernel32" ()
            """;
        File.WriteAllText(sourcePath, source);
        var snapshot = new DebugSourceSnapshot(
            SchemaVersion: 1,
            Sources: [new DebugSourceFileSnapshot(sourcePath, source)],
            ActiveSource: null);

        var error = Assert.Throws<DebugSetupException>(() =>
            new DebugLaunchRequestResolver().Resolve(
                context,
                snapshot,
                moduleName: "DebugModule",
                procedureName: "RunTarget"));

        Assert.Contains("Declare", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClassModuleSubIsRejectedBeforeBuild()
    {
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp.CreateDirectory("DebugProject"));
        Directory.CreateDirectory(context.DocumentSourceSetPath);
        var sourcePath = Path.Combine(context.DocumentSourceSetPath, "DebugClass.cls");
        var source =
            """
            VERSION 1.0 CLASS
            Attribute VB_Name = "DebugClass"

            Public Sub RunTarget()
            End Sub
            """;
        File.WriteAllText(sourcePath, source);
        var snapshot = new DebugSourceSnapshot(
            SchemaVersion: 1,
            Sources: [new DebugSourceFileSnapshot(sourcePath, source)],
            ActiveSource: null);

        var error = Assert.Throws<DebugSetupException>(() =>
            new DebugLaunchRequestResolver().Resolve(
                context,
                snapshot,
                moduleName: "DebugClass",
                procedureName: "RunTarget"));

        Assert.Contains("standard module", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ActivePositionResolvesFromPostSaveSnapshotTextInsteadOfDisk()
    {
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp.CreateDirectory("DebugProject"));
        Directory.CreateDirectory(context.DocumentSourceSetPath);
        var sourcePath = Path.Combine(context.DocumentSourceSetPath, "DebugModule.bas");
        File.WriteAllText(
            sourcePath,
            """
            Attribute VB_Name = "DebugModule"

            Public Sub OldTarget()
            End Sub
            """);
        var savedSnapshotText =
            """
            Attribute VB_Name = "DebugModule"

            Public Sub SavedTarget()
                Debug.Print "saved"
            End Sub
            """;
        var snapshot = new DebugSourceSnapshot(
            SchemaVersion: 1,
            Sources: [new DebugSourceFileSnapshot(sourcePath, savedSnapshotText)],
            ActiveSource: new DebugSourcePosition(sourcePath, Line: 3, Character: 4));

        var request = new DebugLaunchRequestResolver().Resolve(
            context,
            snapshot,
            moduleName: null,
            procedureName: null);
        File.WriteAllText(sourcePath, "Attribute VB_Name = \"ChangedAfterCapture\"");

        Assert.Equal("DebugModule", request.Target.ModuleName);
        Assert.Equal("SavedTarget", request.Target.ProcedureName);
        Assert.Equal(savedSnapshotText, Assert.Single(request.SourceSnapshot!.Sources).Text);
    }

    [Fact]
    public void ActivePositionIncludesClosingLineEndButExcludesTheFollowingBlankLine()
    {
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp.CreateDirectory("DebugProject"));
        Directory.CreateDirectory(context.DocumentSourceSetPath);
        var sourcePath = Path.Combine(context.DocumentSourceSetPath, "DebugModule.bas");
        var source = string.Join('\n',
        [
            "Attribute VB_Name = \"DebugModule\"",
            "",
            "Public Sub RunTarget()",
            "End Sub",
            "",
            "' after target"
        ]);
        File.WriteAllText(sourcePath, source);

        var closingLineSnapshot = new DebugSourceSnapshot(
            SchemaVersion: 1,
            Sources: [new DebugSourceFileSnapshot(sourcePath, source)],
            ActiveSource: new DebugSourcePosition(sourcePath, Line: 3, Character: "End Sub".Length));
        var request = new DebugLaunchRequestResolver().Resolve(
            context,
            closingLineSnapshot,
            moduleName: null,
            procedureName: null);

        Assert.Equal("RunTarget", request.Target.ProcedureName);

        var followingBlankLineSnapshot = closingLineSnapshot with
        {
            ActiveSource = new DebugSourcePosition(sourcePath, Line: 4, Character: 0)
        };
        var error = Assert.Throws<DebugSetupException>(() =>
            new DebugLaunchRequestResolver().Resolve(
                context,
                followingBlankLineSnapshot,
                moduleName: null,
                procedureName: null));

        Assert.Contains("not inside a procedure", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SourceOutsideSelectedDocumentIsRejected()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("DebugProject");
        var context = CreateContext(root);
        Directory.CreateDirectory(context.DocumentSourceSetPath);
        var sourcePath = Path.Combine(root, "OtherDocument", "DebugModule.bas");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        var source =
            """
            Attribute VB_Name = "DebugModule"

            Public Sub RunTarget()
            End Sub
            """;
        File.WriteAllText(sourcePath, source);
        var snapshot = new DebugSourceSnapshot(
            SchemaVersion: 1,
            Sources: [new DebugSourceFileSnapshot(sourcePath, source)],
            ActiveSource: null);

        var error = Assert.Throws<DebugSetupException>(() =>
            new DebugLaunchRequestResolver().Resolve(
                context,
                snapshot,
                moduleName: "DebugModule",
                procedureName: "RunTarget"));

        Assert.Contains("outside", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(context.DocumentSourceSetPath, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicateSnapshotSourcePathIsRejectedAsInventoryError()
    {
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp.CreateDirectory("DebugProject"));
        Directory.CreateDirectory(context.DocumentSourceSetPath);
        var sourcePath = Path.Combine(context.DocumentSourceSetPath, "DebugModule.bas");
        var source =
            """
            Attribute VB_Name = "DebugModule"

            Public Sub RunTarget()
            End Sub
            """;
        File.WriteAllText(sourcePath, source);
        var snapshot = new DebugSourceSnapshot(
            SchemaVersion: 1,
            Sources:
            [
                new DebugSourceFileSnapshot(sourcePath, source),
                new DebugSourceFileSnapshot(sourcePath.ToUpperInvariant(), source)
            ],
            ActiveSource: null);

        var error = Assert.Throws<DebugSetupException>(() =>
            new DebugLaunchRequestResolver().Resolve(
                context,
                snapshot,
                moduleName: "DebugModule",
                procedureName: "RunTarget"));

        Assert.Contains("duplicate", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(sourcePath, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingSelectedDocumentSourceIsRejected()
    {
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp.CreateDirectory("DebugProject"));
        Directory.CreateDirectory(context.DocumentSourceSetPath);
        var sourcePath = Path.Combine(context.DocumentSourceSetPath, "DebugModule.bas");
        var missingPath = Path.Combine(context.DocumentSourceSetPath, "Helper.bas");
        var source =
            """
            Attribute VB_Name = "DebugModule"

            Public Sub RunTarget()
            End Sub
            """;
        File.WriteAllText(sourcePath, source);
        File.WriteAllText(
            missingPath,
            """
            Attribute VB_Name = "Helper"
            """);
        var snapshot = new DebugSourceSnapshot(
            SchemaVersion: 1,
            Sources: [new DebugSourceFileSnapshot(sourcePath, source)],
            ActiveSource: null);

        var error = Assert.Throws<DebugSetupException>(() =>
            new DebugLaunchRequestResolver().Resolve(
                context,
                snapshot,
                moduleName: "DebugModule",
                procedureName: "RunTarget"));

        Assert.Contains("missing", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(missingPath, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnsupportedSnapshotSchemaVersionIsRejected()
    {
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp.CreateDirectory("DebugProject"));
        Directory.CreateDirectory(context.DocumentSourceSetPath);
        var sourcePath = Path.Combine(context.DocumentSourceSetPath, "DebugModule.bas");
        var source =
            """
            Attribute VB_Name = "DebugModule"

            Public Sub RunTarget()
            End Sub
            """;
        File.WriteAllText(sourcePath, source);
        var snapshot = new DebugSourceSnapshot(
            SchemaVersion: 2,
            Sources: [new DebugSourceFileSnapshot(sourcePath, source)],
            ActiveSource: null);

        var error = Assert.Throws<DebugSetupException>(() =>
            new DebugLaunchRequestResolver().Resolve(
                context,
                snapshot,
                moduleName: "DebugModule",
                procedureName: "RunTarget"));

        Assert.Contains("schemaVersion", error.Message, StringComparison.Ordinal);
        Assert.Contains("1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotSourceNotPresentOnDiskIsRejected()
    {
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp.CreateDirectory("DebugProject"));
        Directory.CreateDirectory(context.DocumentSourceSetPath);
        var sourcePath = Path.Combine(context.DocumentSourceSetPath, "DebugModule.bas");
        var source =
            """
            Attribute VB_Name = "DebugModule"

            Public Sub RunTarget()
            End Sub
            """;
        var snapshot = new DebugSourceSnapshot(
            SchemaVersion: 1,
            Sources: [new DebugSourceFileSnapshot(sourcePath, source)],
            ActiveSource: null);

        var error = Assert.Throws<DebugSetupException>(() =>
            new DebugLaunchRequestResolver().Resolve(
                context,
                snapshot,
                moduleName: "DebugModule",
                procedureName: "RunTarget"));

        Assert.Contains("not present", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(sourcePath, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SnapshotSourcesMustUseCanonicalPathOrder()
    {
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp.CreateDirectory("DebugProject"));
        Directory.CreateDirectory(context.DocumentSourceSetPath);
        var firstPath = Path.Combine(context.DocumentSourceSetPath, "Alpha.bas");
        var secondPath = Path.Combine(context.DocumentSourceSetPath, "DebugModule.bas");
        var firstSource = "Attribute VB_Name = \"Alpha\"";
        var secondSource =
            """
            Attribute VB_Name = "DebugModule"

            Public Sub RunTarget()
            End Sub
            """;
        File.WriteAllText(firstPath, firstSource);
        File.WriteAllText(secondPath, secondSource);
        var snapshot = new DebugSourceSnapshot(
            SchemaVersion: 1,
            Sources:
            [
                new DebugSourceFileSnapshot(secondPath, secondSource),
                new DebugSourceFileSnapshot(firstPath, firstSource)
            ],
            ActiveSource: null);

        var error = Assert.Throws<DebugSetupException>(() =>
            new DebugLaunchRequestResolver().Resolve(
                context,
                snapshot,
                moduleName: "DebugModule",
                procedureName: "RunTarget"));

        Assert.Contains("canonical path order", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SnapshotSourcesUseOrdinalWireOrderAcrossPunctuationAndCase()
    {
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp.CreateDirectory("DebugProject"));
        Directory.CreateDirectory(context.DocumentSourceSetPath);
        var digitPath = Path.Combine(context.DocumentSourceSetPath, "A0.bas");
        var underscorePath = Path.Combine(context.DocumentSourceSetPath, "A_B.bas");
        var lowerCasePath = Path.Combine(context.DocumentSourceSetPath, "aZ.bas");
        var digitSource =
            """
            Attribute VB_Name = "A0"

            Public Sub RunTarget()
            End Sub
            """;
        var underscoreSource = "Attribute VB_Name = \"A_B\"";
        var lowerCaseSource = "Attribute VB_Name = \"aZ\"";
        File.WriteAllText(digitPath, digitSource);
        File.WriteAllText(underscorePath, underscoreSource);
        File.WriteAllText(lowerCasePath, lowerCaseSource);
        var snapshot = new DebugSourceSnapshot(
            SchemaVersion: 1,
            Sources:
            [
                new DebugSourceFileSnapshot(digitPath, digitSource),
                new DebugSourceFileSnapshot(underscorePath, underscoreSource),
                new DebugSourceFileSnapshot(lowerCasePath, lowerCaseSource)
            ],
            ActiveSource: null);

        var request = new DebugLaunchRequestResolver().Resolve(
            context,
            snapshot,
            moduleName: "A0",
            procedureName: "RunTarget");

        Assert.Equal("A0", request.Target.ModuleName);
        Assert.Equal("RunTarget", request.Target.ProcedureName);
    }

    [Fact]
    public void ActivePositionRejectsDuplicateModuleIdentity()
    {
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp.CreateDirectory("DebugProject"));
        Directory.CreateDirectory(context.DocumentSourceSetPath);
        var firstPath = Path.Combine(context.DocumentSourceSetPath, "Alpha.bas");
        var activePath = Path.Combine(context.DocumentSourceSetPath, "DebugModule.bas");
        var firstSource =
            """
            Attribute VB_Name = "DebugModule"

            Public Sub OtherTarget()
            End Sub
            """;
        var activeSource =
            """
            Attribute VB_Name = "DebugModule"

            Public Sub RunTarget()
                Debug.Print "ready"
            End Sub
            """;
        File.WriteAllText(firstPath, firstSource);
        File.WriteAllText(activePath, activeSource);
        var snapshot = new DebugSourceSnapshot(
            SchemaVersion: 1,
            Sources:
            [
                new DebugSourceFileSnapshot(firstPath, firstSource),
                new DebugSourceFileSnapshot(activePath, activeSource)
            ],
            ActiveSource: new DebugSourcePosition(activePath, Line: 3, Character: 4));

        var error = Assert.Throws<DebugSetupException>(() =>
            new DebugLaunchRequestResolver().Resolve(
                context,
                snapshot,
                moduleName: null,
                procedureName: null));

        Assert.Contains("module", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ambiguous", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ActivePositionRejectsDuplicateProcedureIdentity()
    {
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp.CreateDirectory("DebugProject"));
        Directory.CreateDirectory(context.DocumentSourceSetPath);
        var sourcePath = Path.Combine(context.DocumentSourceSetPath, "DebugModule.bas");
        var source =
            """
            Attribute VB_Name = "DebugModule"

            Public Sub RunTarget()
                Debug.Print "first"
            End Sub

            Public Sub RunTarget()
                Debug.Print "second"
            End Sub
            """;
        File.WriteAllText(sourcePath, source);
        var snapshot = new DebugSourceSnapshot(
            SchemaVersion: 1,
            Sources: [new DebugSourceFileSnapshot(sourcePath, source)],
            ActiveSource: new DebugSourcePosition(sourcePath, Line: 3, Character: 4));

        var error = Assert.Throws<DebugSetupException>(() =>
            new DebugLaunchRequestResolver().Resolve(
                context,
                snapshot,
                moduleName: null,
                procedureName: null));

        Assert.Contains("procedure", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ambiguous", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(
        ".bas",
        "Attribute VB_Name = \"DebugModule\"\nPublic Property Get RunTarget() As Long\nEnd Property",
        "Sub")]
    [InlineData(
        ".frm",
        "VERSION 5.00\nBegin VB.Form DebugModule\nEnd\nAttribute VB_Name = \"DebugModule\"\nPublic Sub RunTarget()\nEnd Sub",
        "standard module")]
    [InlineData(
        ".bas",
        "Attribute VB_Name = \"DebugModule\"\nPublic Event RunTarget()",
        "not found")]
    public void PropertyFormAndEventTargetsAreRejected(
        string extension,
        string source,
        string expectedMessage)
    {
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp.CreateDirectory("DebugProject"));
        Directory.CreateDirectory(context.DocumentSourceSetPath);
        var sourcePath = Path.Combine(context.DocumentSourceSetPath, $"DebugModule{extension}");
        File.WriteAllText(sourcePath, source);
        var snapshot = new DebugSourceSnapshot(
            SchemaVersion: 1,
            Sources: [new DebugSourceFileSnapshot(sourcePath, source)],
            ActiveSource: null);

        var error = Assert.Throws<DebugSetupException>(() =>
            new DebugLaunchRequestResolver().Resolve(
                context,
                snapshot,
                moduleName: "DebugModule",
                procedureName: "RunTarget"));

        Assert.Contains(expectedMessage, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicateFlatSourceFileIdentityIsRejectedBeforeTargetResolution()
    {
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp.CreateDirectory("DebugProject"));
        var firstDirectory = Path.Combine(context.DocumentSourceSetPath, "One");
        var secondDirectory = Path.Combine(context.DocumentSourceSetPath, "Two");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        var firstPath = Path.Combine(firstDirectory, "DebugModule.bas");
        var secondPath = Path.Combine(secondDirectory, "DebugModule.bas");
        var firstSource =
            """
            Attribute VB_Name = "FirstModule"

            Public Sub RunTarget()
            End Sub
            """;
        var secondSource = "Attribute VB_Name = \"SecondModule\"";
        File.WriteAllText(firstPath, firstSource);
        File.WriteAllText(secondPath, secondSource);
        var snapshot = new DebugSourceSnapshot(
            SchemaVersion: 1,
            Sources:
            [
                new DebugSourceFileSnapshot(firstPath, firstSource),
                new DebugSourceFileSnapshot(secondPath, secondSource)
            ],
            ActiveSource: null);

        var error = Assert.Throws<DebugSetupException>(() =>
            new DebugLaunchRequestResolver().Resolve(
                context,
                snapshot,
                moduleName: "FirstModule",
                procedureName: "RunTarget"));

        Assert.Contains("duplicate", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DebugModule.bas", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RelativeActiveSourcePathIsRejected()
    {
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp.CreateDirectory("DebugProject"));
        Directory.CreateDirectory(context.DocumentSourceSetPath);
        var sourcePath = Path.Combine(context.DocumentSourceSetPath, "DebugModule.bas");
        var source =
            """
            Attribute VB_Name = "DebugModule"

            Public Sub RunTarget()
                Debug.Print "ready"
            End Sub
            """;
        File.WriteAllText(sourcePath, source);
        var snapshot = new DebugSourceSnapshot(
            SchemaVersion: 1,
            Sources: [new DebugSourceFileSnapshot(sourcePath, source)],
            ActiveSource: new DebugSourcePosition("DebugModule.bas", Line: 3, Character: 4));

        var error = Assert.Throws<DebugSetupException>(() =>
            new DebugLaunchRequestResolver().Resolve(
                context,
                snapshot,
                moduleName: null,
                procedureName: null));

        Assert.Contains("absolute", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ResolvedProjectContext CreateContext(string root)
    {
        var document = ProjectDocument.CreateExcel("Book1");
        var manifest = new ProjectManifest(
            ProjectManifest.CurrentSchemaVersion,
            "DebugProject",
            "Book1",
            new Dictionary<string, ProjectDocument>(StringComparer.OrdinalIgnoreCase)
            {
                ["Book1"] = document
            });

        return new ResolvedProjectContext(
            root,
            Path.Combine(root, ProjectManifest.ManifestFileName),
            manifest,
            "Book1",
            document,
            Path.Combine(root, "src", "Book1"),
            Path.Combine(root, "src", "Book1", "Book1.xlsm"),
            Path.Combine(root, "bin", "Book1.xlsm"),
            Path.Combine(root, "publish", "Book1.xlsm"),
            null);
    }
}
