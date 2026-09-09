using System.Collections.Immutable;
using VbaTools.Syntax;
using VbaTools.Semantics;

namespace VbaDev.App.Workbooks;

internal sealed record VbaSourceDiagnostic(
    string SourceUri,
    string Code,
    string Message,
    string Severity,
    VbaRange Range,
    IReadOnlyList<VbaDiagnosticDetail>? Details = null);

internal sealed record VbaSourceAnalysisFailure(string Scope, string? SourceUri, string Message);

/// <summary>Retains source findings from the exact trees admitted for an invocation.</summary>
internal sealed class VbaSourceAnalysisReport
{
    private VbaSourceAnalysisReport(
        ImmutableArray<VbaSourceDiagnostic> diagnostics,
        ImmutableArray<VbaSourceAnalysisFailure> failures)
    {
        Diagnostics = diagnostics;
        Failures = failures;
    }

    internal ImmutableArray<VbaSourceDiagnostic> Diagnostics { get; }
    internal ImmutableArray<VbaSourceAnalysisFailure> Failures { get; }
    internal bool Complete => Failures.IsEmpty;

    internal bool HasErrors => Diagnostics.Any(diagnostic =>
        diagnostic.Severity.Equals("error", StringComparison.OrdinalIgnoreCase));

    internal sealed class Builder
    {
        private readonly ImmutableArray<VbaSourceDiagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<VbaSourceDiagnostic>();
        private readonly ImmutableArray<VbaSourceAnalysisFailure>.Builder failures = ImmutableArray.CreateBuilder<VbaSourceAnalysisFailure>();

        private readonly ImmutableArray<VbaSyntaxTree>.Builder syntaxTrees = ImmutableArray.CreateBuilder<VbaSyntaxTree>();

        internal void Add(VbaSyntaxTree syntax)
        {
            syntaxTrees.Add(syntax);
            var uri = syntax.Uri;
            foreach (var diagnostic in syntax.Diagnostics)
            {
                diagnostics.Add(new(uri, diagnostic.Code, diagnostic.Message,
                    diagnostic.Severity, ToRange(diagnostic.Range)));
            }
            foreach (var diagnostic in VbaDocumentValidationDiagnostics.Collect(syntax))
            {
                diagnostics.Add(new(uri, diagnostic.Code, diagnostic.Message,
                    diagnostic.Severity, ToRange(diagnostic.Range)));
            }
        }

        internal ImmutableArray<VbaSyntaxTree> CapturedSyntaxTrees => syntaxTrees.ToImmutable();

        internal void AnalyzeProjectSources(CancellationToken cancellationToken, VbaProjectSemanticInputs? inputs = null)
        {
            var sourceInventoryComplete = !failures.Any(failure => failure.Scope == "project"
                || !syntaxTrees.Any(tree => tree.Uri.Equals(failure.SourceUri, StringComparison.OrdinalIgnoreCase)));
            foreach (var diagnostic in VbaProjectSourceAnalysis.Analyze(CapturedSyntaxTrees,
                inputs ?? VbaProjectSemanticInputs.Empty, cancellationToken,
                sourceInventoryComplete))
            {
                diagnostics.Add(new(diagnostic.SourceUri, diagnostic.Code, diagnostic.Message,
                    diagnostic.Severity, diagnostic.Range, diagnostic.Details));
            }
        }

        private static VbaRange ToRange(VbaSyntaxRange range)
            => new(new(range.Start.Line, range.Start.Character), new(range.End.Line, range.End.Character));

        internal void FailSource(string path, Exception error)
            => failures.Add(new("source", new Uri(path).AbsoluteUri, FailureMessage(error)));

        internal void FailProject(Exception error)
            => failures.Add(new("project", null, FailureMessage(error)));

        private static string FailureMessage(Exception error)
            => string.IsNullOrWhiteSpace(error.Message)
                ? "Source analysis failed without an error message."
                : error.Message;

        internal VbaSourceAnalysisReport ToReport()
            => new(diagnostics.ToImmutable(), failures.ToImmutable());
    }
}

internal sealed class VbaSourceAnalysisException(VbaSourceAnalysisReport report, Exception? operationalFailure = null)
    : InvalidOperationException("VBA source analysis found errors or could not complete before workbook generation.", operationalFailure)
{
    internal VbaSourceAnalysisReport Report { get; } = report;
    internal Exception? OperationalFailure { get; } = operationalFailure;
}
