using System.Text.Json;
using VbaDev.App.Workbooks;
using VbaTools.Semantics;

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
            schemaVersion = "3.0",
            complete = report.Complete,
            diagnostics = report.Diagnostics.Select(ProjectDiagnostic),
            failures = report.Failures.Select(failure => new
            {
                scope = failure.Scope,
                uri = failure.SourceUri,
                message = failure.Message,
                phase = failure.Phase,
                exceptionType = failure.Exception?.GetType().FullName,
                exception = failure.ExceptionDetails
            })
        }) + Environment.NewLine;
    }

    private static object ProjectDiagnostic(VbaSourceDiagnostic diagnostic)
    {
        var presentation = VbaDiagnosticPresentation.Create(diagnostic.Message, diagnostic.Details,
            supportsRelatedInformation: true);
        var result = new Dictionary<string, object?>
        {
            ["type"] = "diagnostic", ["owner"] = "vba-dev", ["uri"] = diagnostic.SourceUri,
            ["code"] = diagnostic.Code, ["message"] = presentation.Message,
            ["severity"] = diagnostic.Severity, ["range"] = ProjectRange(diagnostic.Range)
        };
        if (!presentation.RelatedInformation.IsEmpty)
        {
            result["relatedInformation"] = presentation.RelatedInformation.Select(related => new
            {
                location = new { uri = related.Location.Uri, range = ProjectRange(related.Location.Range) },
                message = related.Message
            });
        }
        return result;
    }

    private static object ProjectRange(VbaRange range) => new
    {
        start = new { line = range.Start.Line, character = range.Start.Character },
        end = new { line = range.End.Line, character = range.End.Character }
    };
}
