using System.Text.Json;
using VbaDev.App.Cli;
using VbaDev.App.Workbooks;
using VbaTools.Syntax;
using Xunit;

namespace VbaDev.Tests;

public sealed class VbaSourceAnalysisReportTests
{
    private const string SourceUri = "file:///C:/diagnostic-fixtures/Severity.bas";
    private static readonly VbaSyntaxRange Range = new(new(0, 0, 0), new(0, 1, 1));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RenderingAnAbsentOrEmptyReportPreservesEmptySuccessErrorOutput(bool hasReport)
    {
        var report = hasReport ? new VbaSourceAnalysisReport.Builder().ToReport() : null;

        Assert.Empty(VbaSourceAnalysisOutput.Render(report));
    }

    [Fact]
    public void ReportPolicyRetainsSyntheticNonErrorDiagnosticsWithoutPromotingTheirSeverity()
    {
        var report = CreateReport(
            new("policy.warning", "Retained warning.", Range, "warning"),
            new("policy.information", "Retained information.", Range, "information"),
            new("policy.hint", "Retained hint.", Range, "hint"));

        Assert.True(report.Complete);
        Assert.False(report.HasErrors);
        Assert.Empty(report.Failures);
        Assert.Equal(new[] { "warning", "information", "hint" },
            report.Diagnostics.Select(diagnostic => diagnostic.Severity));
        var output = VbaSourceAnalysisOutput.Render(report);
        Assert.Single(output.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using var document = JsonDocument.Parse(output);
        var payload = document.RootElement;
        Assert.Equal("sourceAnalysis", payload.GetProperty("type").GetString());
        Assert.Equal("3.0", payload.GetProperty("schemaVersion").GetString());
        Assert.True(payload.GetProperty("complete").GetBoolean());
        Assert.Empty(payload.GetProperty("failures").EnumerateArray());
        Assert.Equal(
            new[]
            {
                new SerializedDiagnostic("diagnostic", "vba-dev", SourceUri,
                    "policy.warning", "Retained warning.", "warning", 0, 0, 0, 1),
                new SerializedDiagnostic("diagnostic", "vba-dev", SourceUri,
                    "policy.information", "Retained information.", "information", 0, 0, 0, 1),
                new SerializedDiagnostic("diagnostic", "vba-dev", SourceUri,
                    "policy.hint", "Retained hint.", "hint", 0, 0, 0, 1)
            },
            payload.GetProperty("diagnostics").EnumerateArray().Select(ReadDiagnostic));
    }

    [Fact]
    public void ReportPolicyRecognizesAnErrorAmongSyntheticNonErrorDiagnostics()
    {
        var report = CreateReport(
            new("policy.warning", "Retained warning.", Range, "warning"),
            new("policy.error", "Blocking error.", Range, "Error"),
            new("policy.information", "Retained information.", Range, "information"));

        Assert.True(report.Complete);
        Assert.True(report.HasErrors);
        Assert.Empty(report.Failures);
        Assert.Equal(new[] { "warning", "Error", "information" },
            report.Diagnostics.Select(diagnostic => diagnostic.Severity));
    }

    private static VbaSourceAnalysisReport CreateReport(params VbaSyntaxDiagnostic[] diagnostics)
    {
        var cleanTree = VbaSyntaxTree.ParseModule(SourceUri,
            "Attribute VB_Name = \"Severity\"\nPublic Sub Run()\nEnd Sub\n");
        Assert.Empty(cleanTree.Diagnostics);
        // These values characterize report policy, not severities currently emitted by the parser.
        var tree = cleanTree with { Diagnostics = diagnostics };
        var builder = new VbaSourceAnalysisReport.Builder();
        builder.Add(tree);
        return builder.ToReport();
    }

    private static SerializedDiagnostic ReadDiagnostic(JsonElement diagnostic)
    {
        var range = diagnostic.GetProperty("range");
        var start = range.GetProperty("start");
        var end = range.GetProperty("end");
        return new(
            diagnostic.GetProperty("type").GetString()!,
            diagnostic.GetProperty("owner").GetString()!,
            diagnostic.GetProperty("uri").GetString()!,
            diagnostic.GetProperty("code").GetString()!,
            diagnostic.GetProperty("message").GetString()!,
            diagnostic.GetProperty("severity").GetString()!,
            start.GetProperty("line").GetInt32(), start.GetProperty("character").GetInt32(),
            end.GetProperty("line").GetInt32(), end.GetProperty("character").GetInt32());
    }

    private sealed record SerializedDiagnostic(
        string Type, string Owner, string Uri, string Code, string Message, string Severity,
        int StartLine, int StartCharacter, int EndLine, int EndCharacter);
}
