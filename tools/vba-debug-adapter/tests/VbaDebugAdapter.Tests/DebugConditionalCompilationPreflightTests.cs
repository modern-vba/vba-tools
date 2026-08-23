using VbaDebugAdapter.Debugging;
using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class DebugConditionalCompilationPreflightTests
{
    [Fact]
    public void InactiveConditionalTargetFailsBeforeNativeExecution()
    {
        const string sourceUri = "file:///C:/persistent/DebugModule.bas";
        var source = string.Join('\n',
        [
            "Attribute VB_Name = \"DebugModule\"",
            "#If VBA7 Then",
            "Public Sub ModernTarget()",
            "End Sub",
            "#Else",
            "Public Sub LegacyTarget()",
            "End Sub",
            "#End If"
        ]);
        var snapshot = new DebugSourceSnapshot(
            DebugSourceSnapshot.CurrentSchemaVersion,
            [new DebugSourceFileSnapshot("DebugModule.bas", sourceUri, source)],
            null);
        var tree = VbaSyntaxTree.ParseModule(sourceUri, source);
        var targetDeclaration = Assert.Single(
            tree.Module.CallableDeclarations,
            declaration => declaration.Name == "LegacyTarget");
        Assert.True(VbaConditionalCompilationBranchFacts.TryGetPath(
            tree,
            targetDeclaration.Range,
            requireCompleteStructure: true,
            out var targetPath));
        var request = new DebugLaunchRequest(
            new DebugTargetProcedure("DebugModule", "LegacyTarget")
            {
                ConditionalCompilationPath = targetPath
            },
            snapshot);
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
        var environment = new VbaConditionalCompilationEnvironment(
            constants,
            constants.Keys,
            supportsLongLong: true);

        var exception = Assert.Throws<DebugSetupException>(() =>
            new DebugConditionalCompilationPreflight().Validate(
                request,
                [],
                environment));

        Assert.Contains("inactive", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "actual generated workbook compilation context",
            exception.Message,
            StringComparison.Ordinal);
    }
}
