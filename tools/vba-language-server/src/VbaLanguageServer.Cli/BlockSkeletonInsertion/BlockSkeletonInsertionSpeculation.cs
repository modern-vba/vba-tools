using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.Syntax;
using VbaLanguageServer.Workspace;
using PublishedSyntaxDiagnostic = VbaLanguageServer.Diagnostics.VbaSyntaxDiagnostic;

namespace VbaLanguageServer.BlockSkeletonInsertion;

/// <summary>
/// Proves candidate ownership and diagnostic preservation against the exact prospective source.
/// </summary>
internal static class BlockSkeletonInsertionSpeculation
{
    public static bool IsSafe(
        VbaVersionedDocumentSnapshot snapshot,
        VbaBlockHeaderSyntax originalHeader,
        BlockSkeletonInsertionPlan plan,
        int insertionStartOffset,
        int insertionEndOffset,
        int? firstFollowingContentLine,
        string lineEnding)
    {
        if (HasDisqualifyingHeaderDiagnostic(snapshot, originalHeader))
        {
            return false;
        }

        var replacement = plan.TextBeforeCursor + plan.TextAfterCursor;
        var speculativeText = snapshot.Text[..insertionStartOffset]
            + replacement
            + snapshot.Text[insertionEndOffset..];
        var speculativeTree = VbaSyntaxTree.ParseModule(snapshot.Uri, speculativeText);
        if (speculativeTree.Module.Kind != snapshot.ModuleKind)
        {
            return false;
        }

        var speculativeSource = VbaSourceText.From(speculativeText);
        var speculativeHeader = VbaBlockHeaderSyntax.FindAtPosition(
            speculativeTree,
            plan.Position.Line,
            plan.Position.Character);
        var terminatorStartOffset = insertionStartOffset
            + plan.TextBeforeCursor.Length
            + lineEnding.Length
            + originalHeader.LeadingWhitespace.Length;
        var insertedTerminatorRange = new VbaSyntaxRange(
            speculativeSource.PositionAt(terminatorStartOffset),
            speculativeSource.PositionAt(
                terminatorStartOffset + originalHeader.ExpectedTerminator.Length));
        var candidateBlock = speculativeHeader is null
            ? null
            : FindBlock(speculativeTree, speculativeHeader);
        if (speculativeHeader?.Kind != VbaBlockHeaderKind.Sub
            || speculativeHeader.Range != originalHeader.Range
            || candidateBlock?.CloserRange != insertedTerminatorRange)
        {
            return false;
        }

        var replacementEndOffset = insertionStartOffset + replacement.Length;
        var delta = replacement.Length - (insertionEndOffset - insertionStartOffset);
        if (firstFollowingContentLine is { } originalBoundaryLine
            && !PreservesFollowingBoundary(
                snapshot,
                originalHeader,
                speculativeTree,
                speculativeSource,
                candidateBlock,
                originalBoundaryLine,
                insertionEndOffset,
                delta))
        {
            return false;
        }

        var speculativeDiagnostics = VbaDiagnosticPipeline.CollectDocument(
            speculativeTree,
            snapshot.Uri);
        return PreservesErrorDiagnostics(
            snapshot,
            originalHeader,
            speculativeDiagnostics,
            speculativeSource,
            insertionStartOffset,
            insertionEndOffset,
            replacementEndOffset,
            delta);
    }

    private static bool PreservesFollowingBoundary(
        VbaVersionedDocumentSnapshot snapshot,
        VbaBlockHeaderSyntax candidateHeader,
        VbaSyntaxTree speculativeTree,
        VbaSourceText speculativeSource,
        VbaBlockSyntax candidateBlock,
        int originalBoundaryLine,
        int insertionEndOffset,
        int delta)
    {
        var originalSource = VbaSourceText.From(snapshot.Text);
        var originalBoundaryOffset = originalSource.Lines[originalBoundaryLine].StartOffset;
        if (originalBoundaryOffset < insertionEndOffset)
        {
            return false;
        }

        var speculativeBoundaryLine = speculativeSource
            .PositionAt(originalBoundaryOffset + delta)
            .Line;
        var finalBoundaryLine = speculativeBoundaryLine;
        while (finalBoundaryLine + 1 < speculativeSource.Lines.Count
            && speculativeTree.TokenStream.Tokens.Any(token =>
                token.Kind == VbaTokenKind.LineContinuation
                && token.Range.Start.Line == finalBoundaryLine))
        {
            finalBoundaryLine++;
        }

        var boundaryHeader = VbaBlockHeaderSyntax.FindAtPosition(
            speculativeTree,
            finalBoundaryLine,
            speculativeSource.Lines[finalBoundaryLine].Text.Length);
        if (boundaryHeader?.Kind != VbaBlockHeaderKind.Sub
            || boundaryHeader.FirstPhysicalLine != speculativeBoundaryLine
            || !boundaryHeader.LeadingWhitespace.Equals(
                candidateHeader.LeadingWhitespace,
                StringComparison.Ordinal))
        {
            return false;
        }

        var speculativeBoundaryBlock = FindBlock(speculativeTree, boundaryHeader);
        if (speculativeBoundaryBlock is null
            || candidateBlock.CloserRange!.End.Offset > speculativeBoundaryBlock.OpenerRange.Start.Offset)
        {
            return false;
        }

        var originalBoundaryOpener = MapRangeToOriginal(
            speculativeBoundaryBlock.OpenerRange,
            originalSource,
            insertionEndOffset,
            delta);
        if (originalBoundaryOpener is null)
        {
            return false;
        }

        var originalBoundaryBlock = FindUniqueBlock(snapshot.SyntaxTree.Module.Blocks, block =>
            block.Kind == VbaBlockKind.Procedure
            && block.ExpectedTerminator.Equals("End Sub", StringComparison.OrdinalIgnoreCase)
            && block.OpenerRange == originalBoundaryOpener);
        if (originalBoundaryBlock is null)
        {
            return false;
        }

        return originalBoundaryBlock.CloserRange is null
            ? speculativeBoundaryBlock.CloserRange is null
            : speculativeBoundaryBlock.CloserRange == ShiftRange(
                originalBoundaryBlock.CloserRange,
                speculativeSource,
                insertionEndOffset,
                delta);
    }

    private static VbaSyntaxRange? MapRangeToOriginal(
        VbaSyntaxRange range,
        VbaSourceText originalSource,
        int insertionEndOffset,
        int delta)
    {
        var startOffset = range.Start.Offset - delta;
        var endOffset = range.End.Offset - delta;
        if (startOffset < insertionEndOffset || endOffset < startOffset)
        {
            return null;
        }

        return new VbaSyntaxRange(
            originalSource.PositionAt(startOffset),
            originalSource.PositionAt(endOffset));
    }

    private static VbaBlockSyntax? FindBlock(
        VbaSyntaxTree tree,
        VbaBlockHeaderSyntax header)
        => FindUniqueBlock(tree.Module.Blocks, block =>
            block.Kind == VbaBlockKind.Procedure
            && block.ExpectedTerminator.Equals(
                header.ExpectedTerminator,
                StringComparison.OrdinalIgnoreCase)
            && header.Range.Start.Offset <= block.OpenerRange.Start.Offset
            && block.OpenerRange.End.Offset <= header.Range.End.Offset);

    private static VbaBlockSyntax? FindUniqueBlock(
        IEnumerable<VbaBlockSyntax> blocks,
        Func<VbaBlockSyntax, bool> predicate)
    {
        var matches = blocks.Where(predicate).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static VbaSyntaxRange ShiftRange(
        VbaSyntaxRange range,
        VbaSourceText speculativeSource,
        int insertionEndOffset,
        int delta)
    {
        var startOffset = range.Start.Offset >= insertionEndOffset
            ? range.Start.Offset + delta
            : range.Start.Offset;
        var endOffset = range.End.Offset >= insertionEndOffset
            ? range.End.Offset + delta
            : range.End.Offset;
        return new VbaSyntaxRange(
            speculativeSource.PositionAt(startOffset),
            speculativeSource.PositionAt(endOffset));
    }

    private static bool HasDisqualifyingHeaderDiagnostic(
        VbaVersionedDocumentSnapshot snapshot,
        VbaBlockHeaderSyntax header)
        => snapshot.Diagnostics.SyntaxDiagnostics.Any(diagnostic =>
                IsError(diagnostic.Severity)
                && Overlaps(diagnostic.Range, header.Range)
                && !IsDirectMissingTerminator(diagnostic, header))
            || snapshot.Diagnostics.DocumentValidationDiagnostics.Any(diagnostic =>
                IsError(diagnostic.Severity)
                && Overlaps(diagnostic.Range, header.Range));

    private static bool PreservesErrorDiagnostics(
        VbaVersionedDocumentSnapshot snapshot,
        VbaBlockHeaderSyntax header,
        VbaDiagnosticPipelineResult speculativeDiagnostics,
        VbaSourceText speculativeSource,
        int insertionStartOffset,
        int insertionEndOffset,
        int replacementEndOffset,
        int delta)
    {
        var directMissing = snapshot.Diagnostics.SyntaxDiagnostics
            .Where(diagnostic => IsError(diagnostic.Severity))
            .Where(diagnostic => IsDirectMissingTerminator(diagnostic, header))
            .ToArray();
        if (directMissing.Length != 1)
        {
            return false;
        }

        var originalSource = VbaSourceText.From(snapshot.Text);
        var expected = new Dictionary<DiagnosticFingerprint, int>();
        foreach (var diagnostic in snapshot.Diagnostics.SyntaxDiagnostics
            .Where(diagnostic => IsError(diagnostic.Severity))
            .Where(diagnostic => !IsDirectMissingTerminator(diagnostic, header)))
        {
            Add(expected, CreateFingerprint("syntax", diagnostic, originalSource));
        }

        foreach (var diagnostic in snapshot.Diagnostics.DocumentValidationDiagnostics
            .Where(diagnostic => IsError(diagnostic.Severity)))
        {
            Add(expected, CreateFingerprint("validation", diagnostic, originalSource));
        }

        var actual = new Dictionary<DiagnosticFingerprint, int>();
        foreach (var diagnostic in speculativeDiagnostics.SyntaxDiagnostics
            .Where(diagnostic => IsError(diagnostic.Severity)))
        {
            if (!TryCreateNormalizedFingerprint(
                "syntax",
                diagnostic,
                speculativeSource,
                insertionStartOffset,
                insertionEndOffset,
                replacementEndOffset,
                delta,
                out var fingerprint))
            {
                return false;
            }

            Add(actual, fingerprint);
        }

        foreach (var diagnostic in speculativeDiagnostics.DocumentValidationDiagnostics
            .Where(diagnostic => IsError(diagnostic.Severity)))
        {
            if (!TryCreateNormalizedFingerprint(
                "validation",
                diagnostic,
                speculativeSource,
                insertionStartOffset,
                insertionEndOffset,
                replacementEndOffset,
                delta,
                out var fingerprint))
            {
                return false;
            }

            Add(actual, fingerprint);
        }

        return expected.Count == actual.Count
            && expected.All(pair => actual.TryGetValue(pair.Key, out var count)
                && count == pair.Value);
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
        int speculativeStartOffset,
        int speculativeEndOffset,
        int insertionStartOffset,
        int insertionEndOffset,
        int replacementEndOffset,
        int delta,
        out int originalStartOffset,
        out int originalEndOffset)
    {
        if (speculativeEndOffset < speculativeStartOffset)
        {
            originalStartOffset = 0;
            originalEndOffset = 0;
            return false;
        }

        if (speculativeEndOffset <= insertionStartOffset)
        {
            originalStartOffset = speculativeStartOffset;
            originalEndOffset = speculativeEndOffset;
            return true;
        }

        if (speculativeStartOffset >= replacementEndOffset)
        {
            originalStartOffset = speculativeStartOffset - delta;
            originalEndOffset = speculativeEndOffset - delta;
            return originalStartOffset >= insertionEndOffset;
        }

        originalStartOffset = 0;
        originalEndOffset = 0;
        return false;
    }

    private static bool IsDirectMissingTerminator(
        PublishedSyntaxDiagnostic diagnostic,
        VbaBlockHeaderSyntax header)
        => diagnostic.Code.Equals("syntax.missingBlockTerminator", StringComparison.Ordinal)
            && diagnostic.Message.Equals(
                $"Block is missing '{header.ExpectedTerminator}'.",
                StringComparison.Ordinal)
            && diagnostic.Range.Start.Line == header.Range.Start.Line
            && diagnostic.Range.Start.Character == header.Range.Start.Character
            && diagnostic.Range.End.Line == header.Range.End.Line
            && diagnostic.Range.End.Character == header.Range.End.Character;

    private static bool IsError(string severity)
        => severity.Equals("error", StringComparison.OrdinalIgnoreCase);

    private static bool Overlaps(VbaRange diagnostic, VbaSyntaxRange header)
        => Compare(
                diagnostic.Start.Line,
                diagnostic.Start.Character,
                header.End.Line,
                header.End.Character) < 0
            && Compare(
                header.Start.Line,
                header.Start.Character,
                diagnostic.End.Line,
                diagnostic.End.Character) < 0;

    private static int Compare(
        int leftLine,
        int leftCharacter,
        int rightLine,
        int rightCharacter)
        => leftLine != rightLine
            ? leftLine.CompareTo(rightLine)
            : leftCharacter.CompareTo(rightCharacter);

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
