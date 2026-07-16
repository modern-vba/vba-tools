using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.Syntax;
using VbaLanguageServer.Workspace;
using PublishedSyntaxDiagnostic = VbaLanguageServer.Diagnostics.VbaSyntaxDiagnostic;

namespace VbaLanguageServer.BlockSkeletonInsertion;

internal static class BlockSkeletonInsertionDiagnosticProof
{
    public static bool IsSafe(
        VbaVersionedDocumentSnapshot snapshot,
        VbaDiagnosticPipelineResult controlDiagnostics,
        VbaSourceText controlSource,
        VbaDiagnosticPipelineResult prospectiveDiagnostics,
        VbaSourceText prospectiveSource,
        BlockSkeletonInsertionPrefixContext prefix,
        IReadOnlyList<VbaBlockSyntax> controlAncestorBlocks,
        int insertionStartOffset,
        int insertionEndOffset,
        int replacementEndOffset,
        int delta)
    {
        var originalSource = VbaSourceText.From(snapshot.Text);
        var original = CreateErrorMultiset(snapshot.Diagnostics, originalSource);
        var derivedOriginal = CreateErrorMultiset(
            VbaDiagnosticPipeline.CollectDocument(snapshot.SyntaxTree, snapshot.Uri),
            originalSource);
        if (!MultisetsEqual(original, derivedOriginal))
        {
            return false;
        }

        var control = CreateErrorMultiset(controlDiagnostics, controlSource);
        var remainingOriginal = new Dictionary<DiagnosticFingerprint, int>(original);
        foreach (var pair in control)
        {
            if (!remainingOriginal.TryGetValue(pair.Key, out var count)
                || count < pair.Value)
            {
                return false;
            }

            if (count == pair.Value)
            {
                remainingOriginal.Remove(pair.Key);
            }
            else
            {
                remainingOriginal[pair.Key] = count - pair.Value;
            }
        }

        var allowedDirectCascades = CreateAllowedDirectCascadeMultiset(
            prefix,
            controlAncestorBlocks,
            controlSource);
        foreach (var pair in remainingOriginal)
        {
            if (!allowedDirectCascades.TryGetValue(pair.Key, out var allowedCount)
                || pair.Value > allowedCount)
            {
                return false;
            }
        }

        var prospective = new Dictionary<DiagnosticFingerprint, int>();
        foreach (var diagnostic in prospectiveDiagnostics.SyntaxDiagnostics
            .Where(diagnostic => IsError(diagnostic.Severity)))
        {
            if (!TryCreateNormalizedFingerprint(
                "syntax",
                diagnostic,
                prospectiveSource,
                insertionStartOffset,
                insertionEndOffset,
                replacementEndOffset,
                delta,
                out var fingerprint))
            {
                return false;
            }

            Add(prospective, fingerprint);
        }

        foreach (var diagnostic in prospectiveDiagnostics.DocumentValidationDiagnostics
            .Where(diagnostic => IsError(diagnostic.Severity)))
        {
            if (!TryCreateNormalizedFingerprint(
                "validation",
                diagnostic,
                prospectiveSource,
                insertionStartOffset,
                insertionEndOffset,
                replacementEndOffset,
                delta,
                out var fingerprint))
            {
                return false;
            }

            Add(prospective, fingerprint);
        }

        return MultisetsEqual(control, prospective);
    }

    private static bool MultisetsEqual(
        IReadOnlyDictionary<DiagnosticFingerprint, int> left,
        IReadOnlyDictionary<DiagnosticFingerprint, int> right)
        => left.Count == right.Count
            && left.All(pair => right.TryGetValue(pair.Key, out var count)
                && count == pair.Value);

    private static Dictionary<DiagnosticFingerprint, int> CreateErrorMultiset(
        VbaDiagnosticPipelineResult diagnostics,
        VbaSourceText source)
    {
        var result = new Dictionary<DiagnosticFingerprint, int>();
        foreach (var diagnostic in diagnostics.SyntaxDiagnostics
            .Where(diagnostic => IsError(diagnostic.Severity)))
        {
            Add(result, CreateFingerprint("syntax", diagnostic, source));
        }

        foreach (var diagnostic in diagnostics.DocumentValidationDiagnostics
            .Where(diagnostic => IsError(diagnostic.Severity)))
        {
            Add(result, CreateFingerprint("validation", diagnostic, source));
        }

        return result;
    }

    private static Dictionary<DiagnosticFingerprint, int> CreateAllowedDirectCascadeMultiset(
        BlockSkeletonInsertionPrefixContext prefix,
        IReadOnlyList<VbaBlockSyntax> controlAncestorBlocks,
        VbaSourceText controlSource)
    {
        var result = new Dictionary<DiagnosticFingerprint, int>();
        foreach (var block in prefix.Ancestors.Append(prefix.Candidate))
        {
            Add(result, new DiagnosticFingerprint(
                "syntax",
                "vba-language-server",
                "error",
                "syntax.missingBlockTerminator",
                $"Block is missing '{block.ExpectedTerminator}'.",
                block.StatementRange.Start.Offset,
                block.StatementRange.End.Offset));
        }

        foreach (var block in controlAncestorBlocks)
        {
            if (block.CloserRange is null)
            {
                continue;
            }

            var range = ToFullStatementRange(controlSource, block.CloserRange);
            Add(result, new DiagnosticFingerprint(
                "syntax",
                "vba-language-server",
                "error",
                "syntax.unexpectedStatementBoundaryToken",
                $"Unexpected statement-boundary token '{block.ExpectedTerminator}'.",
                range.Start.Offset,
                range.End.Offset));
        }

        return result;
    }

    private static VbaSyntaxRange ToFullStatementRange(
        VbaSourceText source,
        VbaSyntaxRange tokenRange)
    {
        var firstLine = source.Lines[tokenRange.Start.Line];
        var finalLine = source.Lines[tokenRange.End.Line];
        return new VbaSyntaxRange(
            new VbaSyntaxPosition(firstLine.LineNumber, 0, firstLine.StartOffset),
            new VbaSyntaxPosition(
                finalLine.LineNumber,
                finalLine.Text.Length,
                finalLine.EndOffset));
    }

    private static DiagnosticFingerprint CreateFingerprint(
        string category,
        PublishedSyntaxDiagnostic diagnostic,
        VbaSourceText source)
        => new(
            category,
            diagnostic.Source,
            diagnostic.Severity,
            diagnostic.Code,
            diagnostic.Message,
            ToOffset(source, diagnostic.Range.Start),
            ToOffset(source, diagnostic.Range.End));

    private static DiagnosticFingerprint CreateFingerprint(
        string category,
        VbaValidationDiagnostic diagnostic,
        VbaSourceText source)
        => new(
            category,
            diagnostic.Source,
            diagnostic.Severity,
            diagnostic.Code,
            diagnostic.Message,
            ToOffset(source, diagnostic.Range.Start),
            ToOffset(source, diagnostic.Range.End));

    private static bool TryCreateNormalizedFingerprint(
        string category,
        PublishedSyntaxDiagnostic diagnostic,
        VbaSourceText source,
        int insertionStartOffset,
        int insertionEndOffset,
        int replacementEndOffset,
        int delta,
        out DiagnosticFingerprint fingerprint)
        => TryCreateNormalizedFingerprint(
            category,
            diagnostic.Source,
            diagnostic.Severity,
            diagnostic.Code,
            diagnostic.Message,
            diagnostic.Range,
            source,
            insertionStartOffset,
            insertionEndOffset,
            replacementEndOffset,
            delta,
            out fingerprint);

    private static bool TryCreateNormalizedFingerprint(
        string category,
        VbaValidationDiagnostic diagnostic,
        VbaSourceText source,
        int insertionStartOffset,
        int insertionEndOffset,
        int replacementEndOffset,
        int delta,
        out DiagnosticFingerprint fingerprint)
        => TryCreateNormalizedFingerprint(
            category,
            diagnostic.Source,
            diagnostic.Severity,
            diagnostic.Code,
            diagnostic.Message,
            diagnostic.Range,
            source,
            insertionStartOffset,
            insertionEndOffset,
            replacementEndOffset,
            delta,
            out fingerprint);

    private static bool TryCreateNormalizedFingerprint(
        string category,
        string sourceName,
        string severity,
        string code,
        string message,
        VbaRange range,
        VbaSourceText source,
        int insertionStartOffset,
        int insertionEndOffset,
        int replacementEndOffset,
        int delta,
        out DiagnosticFingerprint fingerprint)
    {
        fingerprint = default!;
        var startOffset = ToOffset(source, range.Start);
        var endOffset = ToOffset(source, range.End);
        if (!TryMapRangeToOriginal(
            startOffset,
            endOffset,
            insertionStartOffset,
            insertionEndOffset,
            replacementEndOffset,
            delta,
            out var originalStart,
            out var originalEnd))
        {
            return false;
        }

        fingerprint = new DiagnosticFingerprint(
            category,
            sourceName,
            severity,
            code,
            message,
            originalStart,
            originalEnd);
        return true;
    }

    private static bool TryMapRangeToOriginal(
        int prospectiveStartOffset,
        int prospectiveEndOffset,
        int insertionStartOffset,
        int insertionEndOffset,
        int replacementEndOffset,
        int delta,
        out int originalStartOffset,
        out int originalEndOffset)
    {
        if (prospectiveEndOffset < prospectiveStartOffset)
        {
            originalStartOffset = 0;
            originalEndOffset = 0;
            return false;
        }

        if (prospectiveEndOffset <= insertionStartOffset)
        {
            originalStartOffset = prospectiveStartOffset;
            originalEndOffset = prospectiveEndOffset;
            return true;
        }

        if (prospectiveStartOffset >= replacementEndOffset)
        {
            originalStartOffset = prospectiveStartOffset - delta;
            originalEndOffset = prospectiveEndOffset - delta;
            return originalStartOffset >= insertionEndOffset;
        }

        originalStartOffset = 0;
        originalEndOffset = 0;
        return false;
    }

    private static bool IsError(string severity)
        => severity.Equals("error", StringComparison.OrdinalIgnoreCase);

    private static int ToOffset(VbaSourceText source, VbaPosition position)
        => source.Lines[position.Line].StartOffset + position.Character;

    private static void Add(
        IDictionary<DiagnosticFingerprint, int> counts,
        DiagnosticFingerprint fingerprint)
        => counts[fingerprint] = counts.TryGetValue(fingerprint, out var count)
            ? count + 1
            : 1;

    private sealed record DiagnosticFingerprint(
        string Category,
        string Source,
        string Severity,
        string Code,
        string Message,
        int StartOffset,
        int EndOffset);
}
