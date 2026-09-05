using VbaDebugAdapter.Build;
using VbaDebugAdapter.Debugging;
using VbaDebugAdapter.Infrastructure;
using VbaTools.Syntax;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class DebugSourceAdmissionConditionalCompilationTests
{
    [Fact]
    public void DeferredProofAcceptsAnActiveBreakpointAndRejectsTheInactiveSiblingInRequestOrder()
    {
        const string sourceUri = "file:///C:/persistent/DebugModule.bas";
        var source = CreateConditionalSource();
        var environment = CreateWindows64Vba7Environment();
        var admission = new DebugSourceAdmission(932);
        var active = admission.Admit(
            Snapshot(sourceUri, source, [new(sourceUri, Line: 5)]),
            "DebugModule",
            "RunTarget",
            DebugGenerationId.Initial);
        var activeAndInactive = admission.Admit(
            Snapshot(
                sourceUri,
                source,
                [new(sourceUri, Line: 5), new(sourceUri, Line: 9)]),
            "DebugModule",
            "RunTarget",
            DebugGenerationId.FromValue(1));

        active.VerifyConditionalCompilation(environment);
        var error = Assert.Throws<DebugSetupException>(() =>
            activeAndInactive.VerifyConditionalCompilation(environment));

        Assert.Contains("invalid breakpoint", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("inactive", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(":10'", error.Message, StringComparison.Ordinal);
        Assert.Contains("actual generated workbook compilation context", error.Message);
        Assert.Contains("not relocated", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeferredProofRejectsAnInactiveTargetBeforeNativeExecution()
    {
        const string sourceUri = "file:///C:/persistent/DebugModule.bas";
        var admitted = new DebugSourceAdmission(932).Admit(
            Snapshot(sourceUri, CreateConditionalSource(), []),
            "DebugModule",
            "LegacyTarget",
            DebugGenerationId.Initial);

        var error = Assert.Throws<DebugSetupException>(() =>
            admitted.VerifyConditionalCompilation(CreateWindows64Vba7Environment()));

        Assert.Contains("target", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("inactive", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("actual generated workbook compilation context", error.Message);
    }

    [Theory]
    [InlineData("1^", true)]
    [InlineData("&H1^", true)]
    [InlineData("1^", false)]
    [InlineData("&H1^", false)]
    public void DeferredProofRejectsLongLongConditionsInAVerifiedX86Context(
        string literal,
        bool vba7)
    {
        const string sourceUri = "file:///C:/persistent/DebugModule.bas";
        var source = string.Join('\n',
        [
            "Attribute VB_Name = \"DebugModule\"",
            $"#If {literal} Then",
            "Public Sub LongLongTarget()",
            "End Sub",
            "#End If"
        ]);
        var admitted = new DebugSourceAdmission(932).Admit(
            Snapshot(sourceUri, source, []),
            "DebugModule",
            "LongLongTarget",
            DebugGenerationId.Initial);
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
            admitted.VerifyConditionalCompilation(environment));

        Assert.False(environment.SupportsLongLong);
        Assert.Contains("target", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("could not be proved", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "syntax.conditionalCompilationUnsupportedExpression",
            error.Message,
            StringComparison.Ordinal);
    }

    private static TransportedDebugSourceSnapshot Snapshot(
        string sourceUri,
        string source,
        IReadOnlyList<TransportedDebugSourceBreakpoint> breakpoints)
        => new(
            2,
            [
                new TransportedDebugSource(
                    "DebugModule.bas",
                    sourceUri,
                    "utf8bom",
                    Convert.ToBase64String(DebugSnapshotTestEncoding.Utf8BomBytes(source)))
            ])
        {
            Breakpoints = breakpoints
        };

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
}
