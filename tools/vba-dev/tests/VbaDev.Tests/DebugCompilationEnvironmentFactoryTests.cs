using VbaDev.App.Debugging;
using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaDev.Tests;

public sealed class DebugCompilationEnvironmentFactoryTests
{
    [Fact]
    public void ActualWindows64HostAndProjectConstantsCreateTheEvaluationEnvironment()
    {
        var settings = Settings(
            VbaProjectSystemKind.Win64,
            new Dictionary<string, short> { ["Feature"] = 2 });
        var host = HostFacts(
            DebugExcelProcessArchitecture.X64,
            new DebugCompilerBuiltInConstants(
                Vba6: true,
                Vba7: true,
                Win16: false,
                Win32: true,
                Win64: true,
                Mac: false));
        var source = "#If VBA6 And VBA7 And Win32 And Win64 And Not Mac And Feature = 2 Then\n"
            + "Public Sub Enabled()\n"
            + "End Sub\n"
            + "#Else\n"
            + "Public Sub Disabled()\n"
            + "End Sub\n"
            + "#End If";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var environment = new DebugCompilationEnvironmentFactory().Create(settings, host);
        var evaluation = VbaConditionalCompilationEvaluator.Evaluate(tree, environment);
        Assert.True(evaluation.Succeeded);
        var enabled = Assert.Single(
            tree.Module.CallableDeclarations,
            declaration => declaration.Name == "Enabled");
        Assert.True(VbaConditionalCompilationBranchFacts.TryGetPath(
            tree,
            enabled.Range,
            requireCompleteStructure: true,
            out var enabledPath));
        Assert.True(evaluation.IsActive(enabledPath));
    }

    [Theory]
    [InlineData("1^")]
    [InlineData("&H1^")]
    public void VerifiedWindows64Vba7HostSupportsLongLongLiterals(string literal)
    {
        var settings = Settings(VbaProjectSystemKind.Win64, []);
        var host = HostFacts(
            DebugExcelProcessArchitecture.X64,
            new DebugCompilerBuiltInConstants(true, true, false, true, true, false));
        var source = $"#If {literal} Then\n"
            + "Public Sub Enabled()\n"
            + "End Sub\n"
            + "#End If";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var environment = new DebugCompilationEnvironmentFactory().Create(settings, host);
        var evaluation = VbaConditionalCompilationEvaluator.Evaluate(tree, environment);

        Assert.True(environment.SupportsLongLong);
        Assert.True(evaluation.Succeeded);
        var enabled = Assert.Single(tree.Module.CallableDeclarations);
        Assert.True(VbaConditionalCompilationBranchFacts.TryGetPath(
            tree,
            enabled.Range,
            requireCompleteStructure: true,
            out var enabledPath));
        Assert.True(evaluation.IsActive(enabledPath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void VerifiedWindows32HostDoesNotSupportLongLong(bool vba7)
    {
        var settings = Settings(VbaProjectSystemKind.Win32, []);
        var host = HostFacts(
            DebugExcelProcessArchitecture.X86,
            new DebugCompilerBuiltInConstants(true, vba7, false, true, false, false));

        var environment = new DebugCompilationEnvironmentFactory().Create(settings, host);

        Assert.False(environment.SupportsLongLong);
    }

    [Fact]
    public void WorkbookSystemKindMustMatchTheExactExcelProcessArchitecture()
    {
        var settings = Settings(VbaProjectSystemKind.Win32, []);
        var host = HostFacts(
            DebugExcelProcessArchitecture.X64,
            new DebugCompilerBuiltInConstants(true, true, false, true, true, false));

        var error = Assert.Throws<DebugSetupException>(() =>
            new DebugCompilationEnvironmentFactory().Create(settings, host));

        Assert.Contains("system kind", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("architecture", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(DebugCompilationHostFactsStatus.Unknown)]
    [InlineData(DebugCompilationHostFactsStatus.Mismatch)]
    public void UnprovedHostFactsFailClosed(DebugCompilationHostFactsStatus status)
    {
        var host = new DebugCompilationHostFacts(
            "16.0",
            "7.01",
            "Windows (64-bit) NT 10.00",
            DebugExcelProcessArchitecture.X64,
            status,
            BuiltInConstants: null,
            "unproved host");

        var error = Assert.Throws<DebugSetupException>(() =>
            new DebugCompilationEnvironmentFactory().Create(
                Settings(VbaProjectSystemKind.Win64, []),
                host));

        Assert.Contains("unproved host", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProjectConstantCannotOverrideACompilerBuiltIn()
    {
        var settings = Settings(
            VbaProjectSystemKind.Win64,
            new Dictionary<string, short> { ["vBa7"] = 0 });
        var host = HostFacts(
            DebugExcelProcessArchitecture.X64,
            new DebugCompilerBuiltInConstants(true, true, false, true, true, false));

        var error = Assert.Throws<DebugSetupException>(() =>
            new DebugCompilationEnvironmentFactory().Create(settings, host));

        Assert.Contains("built-in", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("VBA7", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static DebugCompilationSettings Settings(
        VbaProjectSystemKind systemKind,
        IEnumerable<KeyValuePair<string, short>> constants)
        => new(systemKind, 1252, constants, new string('A', 64));

    private static DebugCompilationHostFacts HostFacts(
        DebugExcelProcessArchitecture architecture,
        DebugCompilerBuiltInConstants constants)
        => new(
            "16.0",
            "7.01",
            architecture == DebugExcelProcessArchitecture.X86
                ? "Windows (32-bit) NT 10.00"
                : "Windows (64-bit) NT 10.00",
            architecture,
            DebugCompilationHostFactsStatus.Verified,
            constants,
            UnavailableReason: null);
}
