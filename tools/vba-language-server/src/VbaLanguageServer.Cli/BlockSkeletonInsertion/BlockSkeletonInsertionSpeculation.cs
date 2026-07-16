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
        if (originalHeader.Kind == VbaBlockHeaderKind.If)
        {
            return IsSafeIf(
                snapshot,
                originalHeader,
                plan,
                insertionStartOffset,
                insertionEndOffset,
                firstFollowingContentLine,
                lineEnding);
        }

        if (originalHeader.Kind != VbaBlockHeaderKind.Sub)
        {
            return false;
        }

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

    private static bool IsSafeIf(
        VbaVersionedDocumentSnapshot snapshot,
        VbaBlockHeaderSyntax originalHeader,
        BlockSkeletonInsertionPlan plan,
        int insertionStartOffset,
        int insertionEndOffset,
        int? firstFollowingContentLine,
        string lineEnding)
    {
        if (HasDisqualifyingHeaderDiagnostic(snapshot, originalHeader)
            || !BlockSkeletonInsertionPrefixContext.TryCreate(
                snapshot,
                originalHeader,
                insertionStartOffset,
                out var prefix))
        {
            return false;
        }

        if (HasDisqualifyingAncestorDiagnostic(snapshot, prefix.Ancestors))
        {
            return false;
        }

        var controlText = NeutralizeRange(
            snapshot.Text,
            originalHeader.Range.Start.Offset,
            originalHeader.Range.End.Offset);
        var controlTree = VbaSyntaxTree.ParseModule(snapshot.Uri, controlText);
        if (controlTree.Module.Kind != snapshot.ModuleKind
            || !TryFindPrefixBlocks(controlTree, prefix.Ancestors, out var controlAncestors)
            || !TryProveControlBoundary(
                controlTree,
                prefix,
                controlAncestors,
                firstFollowingContentLine,
                out var boundaryProof))
        {
            return false;
        }

        var replacement = plan.TextBeforeCursor + plan.TextAfterCursor;
        var prospectiveText = snapshot.Text[..insertionStartOffset]
            + replacement
            + snapshot.Text[insertionEndOffset..];
        var prospectiveTree = VbaSyntaxTree.ParseModule(snapshot.Uri, prospectiveText);
        if (prospectiveTree.Module.Kind != snapshot.ModuleKind)
        {
            return false;
        }

        var prospectiveSource = VbaSourceText.From(prospectiveText);
        var prospectiveHeader = VbaBlockHeaderSyntax.FindAtPosition(
            prospectiveTree,
            plan.Position.Line,
            plan.Position.Character);
        var terminatorStartOffset = insertionStartOffset
            + plan.TextBeforeCursor.Length
            + lineEnding.Length
            + originalHeader.LeadingWhitespace.Length;
        var insertedTerminatorRange = new VbaSyntaxRange(
            prospectiveSource.PositionAt(terminatorStartOffset),
            prospectiveSource.PositionAt(
                terminatorStartOffset + originalHeader.ExpectedTerminator.Length));
        var candidateBlock = prospectiveHeader is null
            ? null
            : FindUniqueBlock(prospectiveTree.Module.Blocks, block =>
                block.Kind == VbaBlockKind.If
                && block.ExpectedTerminator.Equals(
                    originalHeader.ExpectedTerminator,
                    StringComparison.OrdinalIgnoreCase)
                && originalHeader.Range.Start.Offset <= block.OpenerRange.Start.Offset
                && block.OpenerRange.End.Offset <= originalHeader.Range.End.Offset);
        if (prospectiveHeader != originalHeader
            || candidateBlock?.CloserRange != insertedTerminatorRange)
        {
            return false;
        }

        var replacementEndOffset = insertionStartOffset + replacement.Length;
        var delta = replacement.Length - (insertionEndOffset - insertionStartOffset);
        if (!TryFindPrefixBlocks(prospectiveTree, prefix.Ancestors, out var prospectiveAncestors)
            || !PreservesPrefixAncestors(
                controlAncestors,
                prospectiveAncestors,
                prospectiveSource,
                insertionEndOffset,
                delta)
            || !PreservesControlBoundary(
                boundaryProof,
                prospectiveTree,
                prospectiveSource,
                prospectiveAncestors,
                candidateBlock,
                insertionEndOffset,
                delta))
        {
            return false;
        }

        var controlSource = VbaSourceText.From(controlText);
        return BlockSkeletonInsertionDiagnosticProof.IsSafe(
            snapshot,
            VbaDiagnosticPipeline.CollectDocument(controlTree, snapshot.Uri),
            controlSource,
            VbaDiagnosticPipeline.CollectDocument(prospectiveTree, snapshot.Uri),
            prospectiveSource,
            prefix,
            controlAncestors,
            insertionStartOffset,
            insertionEndOffset,
            replacementEndOffset,
            delta);
    }

    private static string NeutralizeRange(string text, int startOffset, int endOffset)
    {
        var characters = text.ToCharArray();
        for (var index = startOffset; index < endOffset; index++)
        {
            if (characters[index] is not '\r' and not '\n')
            {
                characters[index] = ' ';
            }
        }

        return new string(characters);
    }

    private static bool HasDisqualifyingAncestorDiagnostic(
        VbaVersionedDocumentSnapshot snapshot,
        IReadOnlyList<BlockSkeletonInsertionPrefixBlock> ancestors)
        => ancestors.Any(ancestor =>
            snapshot.Diagnostics.SyntaxDiagnostics.Any(diagnostic =>
                IsError(diagnostic.Severity)
                && Overlaps(diagnostic.Range, ancestor.StatementRange)
                && !IsDirectMissingTerminator(diagnostic, ancestor))
            || snapshot.Diagnostics.DocumentValidationDiagnostics.Any(diagnostic =>
                IsError(diagnostic.Severity)
                && Overlaps(diagnostic.Range, ancestor.StatementRange)));

    private static bool IsDirectMissingTerminator(
        PublishedSyntaxDiagnostic diagnostic,
        BlockSkeletonInsertionPrefixBlock block)
        => diagnostic.Code.Equals("syntax.missingBlockTerminator", StringComparison.Ordinal)
            && diagnostic.Message.Equals(
                $"Block is missing '{block.ExpectedTerminator}'.",
                StringComparison.Ordinal)
            && diagnostic.Range.Start.Line == block.StatementRange.Start.Line
            && diagnostic.Range.Start.Character == block.StatementRange.Start.Character
            && diagnostic.Range.End.Line == block.StatementRange.End.Line
            && diagnostic.Range.End.Character == block.StatementRange.End.Character;

    private static bool TryFindPrefixBlocks(
        VbaSyntaxTree tree,
        IReadOnlyList<BlockSkeletonInsertionPrefixBlock> prefixBlocks,
        out IReadOnlyList<VbaBlockSyntax> blocks)
    {
        var result = new List<VbaBlockSyntax>(prefixBlocks.Count);
        foreach (var prefixBlock in prefixBlocks)
        {
            var block = FindUniqueBlock(tree.Module.Blocks, candidate =>
                candidate.Kind == prefixBlock.Kind
                && candidate.ExpectedTerminator.Equals(
                    prefixBlock.ExpectedTerminator,
                    StringComparison.OrdinalIgnoreCase)
                && candidate.OpenerRange == prefixBlock.OpenerRange);
            if (block is null || !VbaBlockAncestorSyntax.IsComplete(tree, block))
            {
                blocks = Array.Empty<VbaBlockSyntax>();
                return false;
            }

            result.Add(block);
        }

        blocks = result;
        return true;
    }

    private static bool TryProveControlBoundary(
        VbaSyntaxTree controlTree,
        BlockSkeletonInsertionPrefixContext prefix,
        IReadOnlyList<VbaBlockSyntax> controlAncestors,
        int? firstFollowingContentLine,
        out IfBoundaryProof? proof)
    {
        proof = null;
        if (firstFollowingContentLine is null)
        {
            return true;
        }

        var matches = new List<IfBoundaryProof>();
        for (var index = 0; index < prefix.Ancestors.Count; index++)
        {
            var ancestor = prefix.Ancestors[index];
            var boundary = VbaBlockBoundarySyntax.FindAtFirstPhysicalLine(
                controlTree,
                firstFollowingContentLine.Value,
                ancestor.Kind,
                ancestor.ExpectedTerminator);
            if (boundary is null
                || !boundary.LeadingWhitespace.Equals(
                    ancestor.LeadingWhitespace,
                    StringComparison.Ordinal)
                || !OwnsBoundary(controlAncestors[index], boundary))
            {
                continue;
            }

            matches.Add(new IfBoundaryProof(index, boundary));
        }

        if (matches.Count != 1)
        {
            return false;
        }

        proof = matches[0];
        return true;
    }

    private static bool PreservesPrefixAncestors(
        IReadOnlyList<VbaBlockSyntax> controlAncestors,
        IReadOnlyList<VbaBlockSyntax> prospectiveAncestors,
        VbaSourceText prospectiveSource,
        int insertionEndOffset,
        int delta)
    {
        if (controlAncestors.Count != prospectiveAncestors.Count)
        {
            return false;
        }

        for (var index = 0; index < controlAncestors.Count; index++)
        {
            if (!PreservesBlock(
                controlAncestors[index],
                prospectiveAncestors[index],
                prospectiveSource,
                insertionEndOffset,
                delta))
            {
                return false;
            }
        }

        return true;
    }

    private static bool PreservesControlBoundary(
        IfBoundaryProof? proof,
        VbaSyntaxTree prospectiveTree,
        VbaSourceText prospectiveSource,
        IReadOnlyList<VbaBlockSyntax> prospectiveAncestors,
        VbaBlockSyntax candidateBlock,
        int insertionEndOffset,
        int delta)
    {
        if (proof is null)
        {
            return true;
        }

        var prospectiveLine = prospectiveSource
            .PositionAt(proof.Boundary.TokenRange.Start.Offset + delta)
            .Line;
        var prospectiveBoundary = VbaBlockBoundarySyntax.FindAtFirstPhysicalLine(
            prospectiveTree,
            prospectiveLine,
            proof.Boundary.OwnerBlockKind,
            proof.Boundary.ExpectedTerminator);
        return prospectiveBoundary is not null
            && prospectiveBoundary.Role == proof.Boundary.Role
            && prospectiveBoundary.BranchKind == proof.Boundary.BranchKind
            && prospectiveBoundary.TokenRange == ShiftRange(
                proof.Boundary.TokenRange,
                prospectiveSource,
                insertionEndOffset,
                delta)
            && prospectiveBoundary.Range == ShiftRange(
                proof.Boundary.Range,
                prospectiveSource,
                insertionEndOffset,
                delta)
            && OwnsBoundary(
                prospectiveAncestors[proof.AncestorIndex],
                prospectiveBoundary)
            && candidateBlock.CloserRange!.End.Offset
                <= prospectiveBoundary.TokenRange.Start.Offset;
    }

    private static bool OwnsBoundary(
        VbaBlockSyntax owner,
        VbaBlockBoundarySyntax boundary)
        => boundary.Role == VbaBlockBoundaryRole.Closer
            ? owner.CloserRange == boundary.TokenRange
            : boundary.BranchKind is { } branchKind
                && owner.Branches.Any(branch =>
                    branch.Kind == branchKind
                    && branch.HeaderRange == boundary.TokenRange);

    private static bool PreservesBlock(
        VbaBlockSyntax control,
        VbaBlockSyntax prospective,
        VbaSourceText prospectiveSource,
        int insertionEndOffset,
        int delta)
    {
        if (control.Kind != prospective.Kind
            || control.IsMalformedBarrier != prospective.IsMalformedBarrier
            || !control.ExpectedTerminator.Equals(
                prospective.ExpectedTerminator,
                StringComparison.OrdinalIgnoreCase)
            || prospective.OpenerRange != ShiftRange(
                control.OpenerRange,
                prospectiveSource,
                insertionEndOffset,
                delta)
            || prospective.Range != ShiftRange(
                control.Range,
                prospectiveSource,
                insertionEndOffset,
                delta)
            || control.Branches.Count != prospective.Branches.Count)
        {
            return false;
        }

        if (control.CloserRange is null
            ? prospective.CloserRange is not null
            : prospective.CloserRange != ShiftRange(
                control.CloserRange,
                prospectiveSource,
                insertionEndOffset,
                delta))
        {
            return false;
        }

        for (var index = 0; index < control.Branches.Count; index++)
        {
            var controlBranch = control.Branches[index];
            var prospectiveBranch = prospective.Branches[index];
            if (controlBranch.Kind != prospectiveBranch.Kind
                || prospectiveBranch.HeaderRange != ShiftRange(
                    controlBranch.HeaderRange,
                    prospectiveSource,
                    insertionEndOffset,
                    delta)
                || prospectiveBranch.Range != ShiftRange(
                    controlBranch.Range,
                    prospectiveSource,
                    insertionEndOffset,
                    delta))
            {
                return false;
            }
        }

        return true;
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

    private sealed record IfBoundaryProof(
        int AncestorIndex,
        VbaBlockBoundarySyntax Boundary);
}
