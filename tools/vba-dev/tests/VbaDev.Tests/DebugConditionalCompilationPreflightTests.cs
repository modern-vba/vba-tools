using System.Collections.Immutable;
using VbaDev.App.Debugging;
using VbaDev.App.Projects;
using VbaDev.Domain;
using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaDev.Tests;

public sealed class DebugConditionalCompilationPreflightTests
{
    [Fact]
    public void ActiveBreakpointPassesAndInactiveSiblingFailsWithoutRelocation()
    {
        var sourcePath = Path.GetFullPath(Path.Combine("DebugProject", "DebugModule.bas"));
        var source = CreateConditionalSource();
        var activeSource = new DebugSourceBreakpoint(sourcePath, EditorLine: 5);
        var inactiveSource = new DebugSourceBreakpoint(sourcePath, EditorLine: 9);
        var snapshot = Snapshot(sourcePath, source, [activeSource, inactiveSource]);
        var mapper = new BreakpointSourceMapper();
        var active = mapper.Map(snapshot, activeSource);
        var inactive = mapper.Map(snapshot, inactiveSource);
        var request = Request(snapshot);
        var preflight = new DebugConditionalCompilationPreflight();
        var environment = CreateWindows64Vba7Environment();

        preflight.Validate(request, [active], environment);
        var error = Assert.Throws<DebugSetupException>(() =>
            preflight.Validate(request, [active, inactive], environment));

        Assert.Contains("invalid breakpoint", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("inactive", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($":{inactiveSource.EditorLine + 1}'", error.Message, StringComparison.Ordinal);
        Assert.Contains("actual generated workbook compilation context", error.Message);
        Assert.Contains("not relocated", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InactiveConditionalTargetFailsBeforeNativeExecution()
    {
        var sourcePath = Path.GetFullPath(Path.Combine("DebugProject", "DebugModule.bas"));
        var source = CreateConditionalSource();
        var snapshot = Snapshot(sourcePath, source, []);
        var tree = VbaSyntaxTree.ParseModule(new Uri(sourcePath).AbsoluteUri, source);
        var targetDeclaration = Assert.Single(
            tree.Module.CallableDeclarations,
            declaration => declaration.Name == "LegacyTarget");
        Assert.True(VbaConditionalCompilationBranchFacts.TryGetPath(
            tree,
            targetDeclaration.Range,
            requireCompleteStructure: true,
            out var targetPath));
        var request = Request(snapshot) with
        {
            Target = new DebugTargetProcedure("DebugModule", "LegacyTarget")
            {
                ConditionalCompilationPath = targetPath
            }
        };

        var error = Assert.Throws<DebugSetupException>(() =>
            new DebugConditionalCompilationPreflight().Validate(
                request,
                [],
                CreateWindows64Vba7Environment()));

        Assert.Contains("target", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("inactive", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("actual generated workbook compilation context", error.Message);
    }

    [Theory]
    [InlineData("1^", true)]
    [InlineData("&H1^", true)]
    [InlineData("1^", false)]
    [InlineData("&H1^", false)]
    public void LongLongConditionalTargetFailsPreflightInVerifiedX86Context(
        string literal,
        bool vba7)
    {
        var sourcePath = Path.GetFullPath(Path.Combine("DebugProject", "DebugModule.bas"));
        var source = string.Join('\n',
        [
            "Attribute VB_Name = \"DebugModule\"",
            $"#If {literal} Then",
            "Public Sub LongLongTarget()",
            "End Sub",
            "#End If"
        ]);
        var snapshot = Snapshot(sourcePath, source, []);
        var tree = VbaSyntaxTree.ParseModule(new Uri(sourcePath).AbsoluteUri, source);
        var targetDeclaration = Assert.Single(tree.Module.CallableDeclarations);
        Assert.True(VbaConditionalCompilationBranchFacts.TryGetPath(
            tree,
            targetDeclaration.Range,
            requireCompleteStructure: true,
            out var targetPath));
        var request = Request(snapshot) with
        {
            Target = new DebugTargetProcedure("DebugModule", "LongLongTarget")
            {
                ConditionalCompilationPath = targetPath
            }
        };
        var environment = new DebugCompilationEnvironmentFactory().Create(
            new DebugCompilationSettings(
                VbaProjectSystemKind.Win32,
                1252,
                [],
                new string('A', 64)),
            new DebugCompilationHostFacts(
                "16.0",
                "7.01",
                "Windows (64-bit) NT 10.00",
                DebugExcelProcessArchitecture.X86,
                DebugCompilationHostFactsStatus.Verified,
                new DebugCompilerBuiltInConstants(true, vba7, false, true, false, false),
                UnavailableReason: null));

        var error = Assert.Throws<DebugSetupException>(() =>
            new DebugConditionalCompilationPreflight().Validate(request, [], environment));

        Assert.False(environment.SupportsLongLong);
        Assert.Contains("target", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("could not be proved", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "syntax.conditionalCompilationUnsupportedExpression",
            error.Message,
            StringComparison.Ordinal);
    }

    private static VbaConditionalCompilationEnvironment CreateWindows64Vba7Environment()
    {
        var constants = new Dictionary<string, VbaConditionalCompilationValue>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["VBA6"] = VbaConditionalCompilationValue.FromBoolean(true),
            ["VBA7"] = VbaConditionalCompilationValue.FromBoolean(true),
            ["Win16"] = VbaConditionalCompilationValue.FromBoolean(false),
            ["Win32"] = VbaConditionalCompilationValue.FromBoolean(true),
            ["Win64"] = VbaConditionalCompilationValue.FromBoolean(true),
            ["Mac"] = VbaConditionalCompilationValue.FromBoolean(false)
        };
        return new VbaConditionalCompilationEnvironment(
            constants,
            constants.Keys,
            supportsLongLong: true);
    }

    private static string CreateConditionalSource()
        => string.Join('\n',
        [
            "Attribute VB_Name = \"DebugModule\"",
            "Public Sub RunTarget()",
            "End Sub",
            "#If VBA7 Then",
            "Public Sub ModernTarget()",
            "    Debug.Print \"modern\"",
            "End Sub",
            "#Else",
            "Public Sub LegacyTarget()",
            "    Debug.Print \"legacy\"",
            "End Sub",
            "#End If"
        ]);

    private static DebugSourceSnapshot Snapshot(
        string sourcePath,
        string source,
        ImmutableArray<DebugSourceBreakpoint> breakpoints)
        => new(
            DebugSourceSnapshot.CurrentSchemaVersion,
            [new DebugSourceFileSnapshot(sourcePath, source)],
            null)
        {
            Breakpoints = breakpoints
        };

    private static DebugLaunchRequest Request(DebugSourceSnapshot snapshot)
        => new(
            CreateContext(),
            new DebugTargetProcedure("DebugModule", "RunTarget"),
            snapshot);

    private static ResolvedProjectContext CreateContext()
    {
        var root = Path.GetFullPath(Path.Combine("DebugProject", Guid.NewGuid().ToString("N")));
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
