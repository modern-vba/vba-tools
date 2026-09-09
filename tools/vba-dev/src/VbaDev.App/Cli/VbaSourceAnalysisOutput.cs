using System.Text.Json;
using VbaDev.App.Workbooks;

namespace VbaDev.App.Cli;

/// <summary>Projects captured source findings onto the public tool output contract.</summary>
internal static class VbaSourceAnalysisOutput
{
    internal static string Render(VbaSourceAnalysisReport? report)
    {
        if (report is null || report.Diagnostics.IsEmpty && report.Failures.IsEmpty)
        {
            return string.Empty;
        }
        return JsonSerializer.Serialize(new
        {
            type = "sourceAnalysis",
            schemaVersion = "2.0",
            complete = report.Complete,
            diagnostics = report.Diagnostics.Select(diagnostic => new
            {
                type = "diagnostic",
                owner = "vba-dev",
                uri = diagnostic.SourceUri,
                code = diagnostic.Code,
                message = diagnostic.Message,
                severity = diagnostic.Severity,
                range = new
                {
                    start = new { line = diagnostic.Range.Start.Line, character = diagnostic.Range.Start.Character },
                    end = new { line = diagnostic.Range.End.Line, character = diagnostic.Range.End.Character }
                }
            }),
            failures = report.Failures.Select(failure => new
            {
                scope = failure.Scope,
                uri = failure.SourceUri,
                message = failure.Message
            })
        }) + Environment.NewLine;
    }
}
