using VbaLanguageServer.Diagnostics;
using VbaTools.Syntax;
using PublishedSyntaxDiagnostic = VbaLanguageServer.Diagnostics.VbaSyntaxDiagnostic;

namespace VbaLanguageServer.BlockSkeletonInsertion;

// Without control, allowances are exact removals. With control, they bound original-minus-control.
internal sealed record BlockSkeletonInsertionDiagnosticProofCase(
    BlockSkeletonInsertionDiagnosticEvidence Original,
    BlockSkeletonInsertionDiagnosticEvidence Prospective,
    BlockSkeletonInsertionDiagnosticEvidence AllowedRemovals,
    BlockSkeletonInsertionDiagnosticReplacement Replacement,
    BlockSkeletonInsertionDiagnosticEvidence? Control = null);

internal sealed record BlockSkeletonInsertionDiagnosticReplacement(
    int StartOffset,
    int EndOffset,
    int ProspectiveEndOffset);

internal sealed class BlockSkeletonInsertionDiagnosticEvidence
{
    public BlockSkeletonInsertionDiagnosticEvidence(
        VbaSourceText source,
        VbaDiagnosticPipelineResult diagnostics)
    {
        Source = source;
        // Project diagnostics are intentionally outside this document-local proof.
        SyntaxDiagnostics = Array.AsReadOnly(diagnostics.SyntaxDiagnostics.ToArray());
        DocumentValidationDiagnostics = Array.AsReadOnly(
            diagnostics.DocumentValidationDiagnostics.ToArray());
    }

    public VbaSourceText Source { get; }

    public IReadOnlyList<PublishedSyntaxDiagnostic> SyntaxDiagnostics { get; }

    public IReadOnlyList<VbaValidationDiagnostic> DocumentValidationDiagnostics { get; }
}
