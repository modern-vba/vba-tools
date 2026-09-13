using VbaLanguageServer.Diagnostics;
using VbaTools.Syntax;
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
        if (!VbaSourceTextEditResult.TryApply(
                snapshot.SourceText,
                [new(insertionStartOffset, insertionEndOffset, plan.TextBeforeCursor + plan.TextAfterCursor)],
                out var edit))
        {
            return false;
        }

        if (originalHeader.Kind is VbaBlockHeaderKind.If
            or VbaBlockHeaderKind.With
            or VbaBlockHeaderKind.For
            or VbaBlockHeaderKind.ForEach
            or VbaBlockHeaderKind.SelectCase)
        {
            return IsSafeStructuredBlock(
                snapshot,
                originalHeader,
                plan,
                edit,
                firstFollowingContentLine,
                lineEnding);
        }

        if (!IsModuleDeclarationHeader(originalHeader.Kind))
        {
            return false;
        }

        if (firstFollowingContentLine is null
            && TryProveTrustedDeclarationTail(snapshot, originalHeader))
        {
            return true;
        }

        if (HasDisqualifyingHeaderDiagnostic(snapshot, originalHeader))
        {
            return false;
        }

        var speculativeSource = edit.After;
        var speculativeTree = VbaSyntaxTree.ParseModule(snapshot.Uri, speculativeSource.Text);
        if (speculativeTree.Module.Kind != snapshot.ModuleKind)
        {
            return false;
        }

        var speculativeHeader = VbaBlockHeaderSyntax.FindAtPosition(
            speculativeTree,
            plan.Position.Line,
            plan.Position.Character);
        if (!TryCreateTerminatorRange(edit, originalHeader, plan, lineEnding, out var insertedTerminatorRange))
        {
            return false;
        }

        var candidateBlock = speculativeHeader is null
            ? null
            : FindBlock(speculativeTree, speculativeHeader);
        if (speculativeHeader?.Kind != originalHeader.Kind
            || speculativeHeader.Range != originalHeader.Range
            || !speculativeHeader.ConditionalCompilationBranchPath.Equals(
                originalHeader.ConditionalCompilationBranchPath)
            || candidateBlock?.CloserRange != insertedTerminatorRange
            || !VbaConditionalCompilationBranchFacts.IsBlockLocal(
                speculativeTree,
                candidateBlock,
                originalHeader.ConditionalCompilationBranchPath,
                requireCompleteStructure: true))
        {
            return false;
        }

        if (firstFollowingContentLine is { } originalBoundaryLine
            && !PreservesFollowingBoundary(
                snapshot,
                originalHeader,
                speculativeTree,
                candidateBlock,
                originalBoundaryLine,
                edit))
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
            edit);
    }

    private static bool IsSafeStructuredBlock(
        VbaVersionedDocumentSnapshot snapshot,
        VbaBlockHeaderSyntax originalHeader,
        BlockSkeletonInsertionPlan plan,
        VbaSourceTextEditResult edit,
        int? firstFollowingContentLine,
        string lineEnding)
    {
        if (HasDisqualifyingHeaderDiagnostic(snapshot, originalHeader)
            || !BlockSkeletonInsertionPrefixContext.TryCreate(
                snapshot,
                originalHeader,
                edit.Replacements[0].BeforeStartOffset,
                out var prefix))
        {
            return false;
        }

        if (HasDisqualifyingAncestorDiagnostic(snapshot, prefix.Ancestors))
        {
            return false;
        }

        if (!TryNeutralizeRange(
            snapshot.SourceText,
            originalHeader.Range.Start.Offset,
            originalHeader.Range.End.Offset,
            out var controlEdit))
        {
            return false;
        }

        var controlSource = controlEdit.After;
        var controlTree = VbaSyntaxTree.ParseModule(snapshot.Uri, controlSource.Text);
        if (controlTree.Module.Kind != snapshot.ModuleKind
            || !TryFindPrefixBlocks(
                controlTree,
                prefix.Ancestors,
                prefix.Candidate.ConditionalCompilationBranchPath,
                out var controlAncestors)
            || !TryProveControlBoundary(
                controlTree,
                prefix,
                controlAncestors,
                firstFollowingContentLine,
                out var boundaryProof))
        {
            return false;
        }

        var prospectiveSource = edit.After;
        var prospectiveTree = VbaSyntaxTree.ParseModule(snapshot.Uri, prospectiveSource.Text);
        if (prospectiveTree.Module.Kind != snapshot.ModuleKind)
        {
            return false;
        }

        var prospectiveHeader = VbaBlockHeaderSyntax.FindAtPosition(
            prospectiveTree,
            plan.Position.Line,
            plan.Position.Character);
        if (!TryCreateTerminatorRange(edit, originalHeader, plan, lineEnding, out var insertedTerminatorRange))
        {
            return false;
        }

        var candidateKind = GetStructuralKind(originalHeader.Kind);
        var candidateBlock = prospectiveHeader is null
            ? null
            : FindUniqueBlock(prospectiveTree.Module.Blocks, block =>
                block.Kind == candidateKind
                && block.ExpectedTerminator.Equals(
                    originalHeader.ExpectedTerminator,
                    StringComparison.OrdinalIgnoreCase)
                && originalHeader.Range.Start.Offset <= block.OpenerRange.Start.Offset
                && block.OpenerRange.End.Offset <= originalHeader.Range.End.Offset);
        if (prospectiveHeader != originalHeader
            || candidateBlock?.CloserRange != insertedTerminatorRange
            || !VbaConditionalCompilationBranchFacts.IsBlockLocal(
                prospectiveTree,
                candidateBlock,
                originalHeader.ConditionalCompilationBranchPath,
                requireCompleteStructure: true))
        {
            return false;
        }

        if (!TryFindPrefixBlocks(
                prospectiveTree,
                prefix.Ancestors,
                prefix.Candidate.ConditionalCompilationBranchPath,
                out var prospectiveAncestors)
            || !PreservesPrefixAncestors(
                controlAncestors,
                prospectiveAncestors,
                edit)
            || !PreservesControlBoundary(
                boundaryProof,
                prospectiveTree,
                prospectiveAncestors,
                candidateBlock,
                edit))
        {
            return false;
        }

        var originalEvidence = new BlockSkeletonInsertionDiagnosticEvidence(
            snapshot.SourceText,
            snapshot.Diagnostics);
        if (!snapshot.IsOwnedByAnalysis
            && (!VbaSourceTextEditResult.TryApply(snapshot.SourceText, [], out var unchanged)
                || !BlockSkeletonInsertionDiagnosticProof.IsSafe(new(
                originalEvidence,
                new(snapshot.SourceText, VbaDiagnosticPipeline.CollectDocument(snapshot.SyntaxTree, snapshot.Uri)),
                new(snapshot.SourceText, new([], [], [])),
                unchanged))))
        {
            return false;
        }

        return BlockSkeletonInsertionDiagnosticProof.IsSafe(new(
            originalEvidence,
            new(prospectiveSource, VbaDiagnosticPipeline.CollectDocument(prospectiveTree, snapshot.Uri)),
            new(snapshot.SourceText, new(CreateAllowedDirectCascades(prefix, controlAncestors, controlSource), [], [])),
            edit,
            new(controlSource, VbaDiagnosticPipeline.CollectDocument(controlTree, snapshot.Uri))));
    }

    private static IReadOnlyList<PublishedSyntaxDiagnostic> CreateAllowedDirectCascades(
        BlockSkeletonInsertionPrefixContext prefix,
        IReadOnlyList<VbaBlockSyntax> controlAncestorBlocks,
        VbaSourceText controlSource)
    {
        var result = new List<PublishedSyntaxDiagnostic>();
        foreach (var block in prefix.Ancestors.Append(prefix.Candidate))
        {
            result.Add(new(
                "syntax.missingBlockTerminator",
                $"Block is missing '{block.ExpectedTerminator}'.",
                new(
                    new(block.StatementRange.Start.Line, block.StatementRange.Start.Character),
                    new(block.StatementRange.End.Line, block.StatementRange.End.Character))));
        }

        foreach (var block in controlAncestorBlocks)
        {
            if (block.CloserRange is null)
            {
                continue;
            }

            var firstLine = controlSource.Lines[block.CloserRange.Start.Line];
            var finalLine = controlSource.Lines[block.CloserRange.End.Line];
            result.Add(new(
                "syntax.unexpectedStatementBoundaryToken",
                $"Unexpected statement-boundary token '{block.ExpectedTerminator}'.",
                new(new(firstLine.LineNumber, 0), new(finalLine.LineNumber, finalLine.Text.Length))));
        }

        return result;
    }

    private static bool TryProveTrustedDeclarationTail(
        VbaVersionedDocumentSnapshot snapshot,
        VbaBlockHeaderSyntax header)
    {
        if (!snapshot.IsOwnedByAnalysis
            || !header.ConditionalCompilationBranchPath.IsEmpty)
        {
            return false;
        }

        var candidateBlock = FindBlock(snapshot.SyntaxTree, header);
        if (candidateBlock is null
            || candidateBlock.CloserRange is not null
            || !VbaConditionalCompilationBranchFacts.IsBlockLocal(
                snapshot.SyntaxTree,
                candidateBlock,
                header.ConditionalCompilationBranchPath,
                requireCompleteStructure: true))
        {
            return false;
        }

        var syntaxErrors = snapshot.Diagnostics.SyntaxDiagnostics
            .Where(diagnostic => IsError(diagnostic.Severity))
            .ToArray();
        return syntaxErrors.Length == 1
            && IsDirectMissingTerminator(syntaxErrors[0], header)
            && !snapshot.Diagnostics.DocumentValidationDiagnostics.Any(
                diagnostic => IsError(diagnostic.Severity));
    }

    private static bool TryCreateTerminatorRange(
        VbaSourceTextEditResult edit,
        VbaBlockHeaderSyntax header,
        BlockSkeletonInsertionPlan plan,
        string lineEnding,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out VbaSyntaxRange? range)
    {
        range = null;
        var startOffset = edit.Replacements[0].AfterStartOffset
            + plan.TextBeforeCursor.Length + lineEnding.Length + header.LeadingWhitespace.Length;
        if (!edit.After.TryGetPosition(startOffset, out var start)
            || !edit.After.TryGetPosition(startOffset + header.ExpectedTerminator.Length, out var end))
        {
            return false;
        }

        range = new(start, end);
        return true;
    }

    private static bool TryNeutralizeRange(
        VbaSourceText source,
        int startOffset,
        int endOffset,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out VbaSourceTextEditResult? edit)
    {
        edit = null;
        if (endOffset < startOffset
            || !source.TryGetPosition(startOffset, out _)
            || !source.TryGetPosition(endOffset, out _))
        {
            return false;
        }

        var characters = source.Text[startOffset..endOffset].ToCharArray();
        for (var index = 0; index < characters.Length; index++)
        {
            if (characters[index] is not '\r' and not '\n')
            {
                characters[index] = ' ';
            }
        }

        return VbaSourceTextEditResult.TryApply(
            source, [new(startOffset, endOffset, new string(characters))], out edit);
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
        VbaConditionalCompilationBranchPath selectedLeafPath,
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
            if (block is null
                || !VbaBlockAncestorSyntax.IsComplete(
                    tree,
                    block,
                    selectedLeafPath))
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
        out BlockBoundaryProof? proof)
    {
        proof = null;
        if (firstFollowingContentLine is null)
        {
            return true;
        }

        if (VbaConditionalCompilationBranchFacts.TryGetClosingBoundary(
            controlTree,
            prefix.Candidate.ConditionalCompilationBranchPath,
            firstFollowingContentLine.Value,
            out var conditionalBoundary))
        {
            proof = new ConditionalCompilationBoundaryProof(
                prefix.Candidate.ConditionalCompilationBranchPath,
                conditionalBoundary);
            return true;
        }

        var matches = new List<BlockBoundaryProof>();
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

            matches.Add(new AncestorBlockBoundaryProof(index, boundary));
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
        VbaSourceTextEditResult edit)
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
                edit))
            {
                return false;
            }
        }

        return true;
    }

    private static bool PreservesControlBoundary(
        BlockBoundaryProof? proof,
        VbaSyntaxTree prospectiveTree,
        IReadOnlyList<VbaBlockSyntax> prospectiveAncestors,
        VbaBlockSyntax candidateBlock,
        VbaSourceTextEditResult edit)
    {
        if (proof is null)
        {
            return true;
        }

        if (proof is ConditionalCompilationBoundaryProof conditionalProof)
        {
            if (!TryMapStructuralBoundary(
                    conditionalProof.Boundary.Range.Start.Offset, edit, out var position))
            {
                return false;
            }

            return VbaConditionalCompilationBranchFacts.TryGetClosingBoundary(
                    prospectiveTree,
                    conditionalProof.Path,
                    position.Line,
                    out var prospectiveConditionalBoundary)
                && prospectiveConditionalBoundary.Kind == conditionalProof.Boundary.Kind
                && PreservesStructuralRange(
                    conditionalProof.Boundary.Range,
                    prospectiveConditionalBoundary.Range,
                    edit)
                && candidateBlock.CloserRange!.End.Offset
                    <= prospectiveConditionalBoundary.Range.Start.Offset;
        }

        var ancestorProof = (AncestorBlockBoundaryProof)proof;
        if (!TryMapStructuralBoundary(
                ancestorProof.Boundary.TokenRange.Start.Offset, edit, out var boundaryPosition))
        {
            return false;
        }

        var prospectiveBoundary = VbaBlockBoundarySyntax.FindAtFirstPhysicalLine(
            prospectiveTree,
            boundaryPosition.Line,
            ancestorProof.Boundary.OwnerBlockKind,
            ancestorProof.Boundary.ExpectedTerminator);
        return prospectiveBoundary is not null
            && prospectiveBoundary.Role == ancestorProof.Boundary.Role
            && prospectiveBoundary.BranchKind == ancestorProof.Boundary.BranchKind
            && PreservesStructuralRange(
                ancestorProof.Boundary.TokenRange,
                prospectiveBoundary.TokenRange,
                edit)
            && PreservesStructuralRange(
                ancestorProof.Boundary.Range,
                prospectiveBoundary.Range,
                edit)
            && OwnsBoundary(
                prospectiveAncestors[ancestorProof.AncestorIndex],
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
        VbaSourceTextEditResult edit)
    {
        if (control.Kind != prospective.Kind
            || control.IsMalformedBarrier != prospective.IsMalformedBarrier
            || !control.ExpectedTerminator.Equals(
                prospective.ExpectedTerminator,
                StringComparison.OrdinalIgnoreCase)
            || !PreservesStructuralRange(
                control.OpenerRange,
                prospective.OpenerRange,
                edit)
            || !PreservesStructuralRange(
                control.Range,
                prospective.Range,
                edit)
            || control.Branches.Count != prospective.Branches.Count)
        {
            return false;
        }

        if (control.CloserRange is null
            ? prospective.CloserRange is not null
            : !PreservesStructuralRange(
                control.CloserRange,
                prospective.CloserRange,
                edit))
        {
            return false;
        }

        if (control.MalformedBarrierOwnerRange is null
            ? prospective.MalformedBarrierOwnerRange is not null
            : !PreservesStructuralRange(
                control.MalformedBarrierOwnerRange,
                prospective.MalformedBarrierOwnerRange,
                edit))
        {
            return false;
        }

        for (var index = 0; index < control.Branches.Count; index++)
        {
            var controlBranch = control.Branches[index];
            var prospectiveBranch = prospective.Branches[index];
            if (controlBranch.Kind != prospectiveBranch.Kind
                || !PreservesStructuralRange(
                    controlBranch.HeaderRange,
                    prospectiveBranch.HeaderRange,
                    edit)
                || !PreservesStructuralRange(
                    controlBranch.Range,
                    prospectiveBranch.Range,
                    edit))
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
        VbaBlockSyntax candidateBlock,
        int originalBoundaryLine,
        VbaSourceTextEditResult edit)
    {
        var speculativeSource = edit.After;
        var originalBoundaryOffset = edit.Before.Lines[originalBoundaryLine].StartOffset;
        if (originalBoundaryOffset < edit.Replacements[0].BeforeEndOffset
            || !TryMapStructuralBoundary(originalBoundaryOffset, edit, out var boundaryPosition))
        {
            return false;
        }

        if (VbaConditionalCompilationBranchFacts.TryGetClosingBoundary(
            snapshot.SyntaxTree,
            candidateHeader.ConditionalCompilationBranchPath,
            originalBoundaryLine,
            out var originalConditionalBoundary))
        {
            return VbaConditionalCompilationBranchFacts.TryGetClosingBoundary(
                    speculativeTree,
                    candidateHeader.ConditionalCompilationBranchPath,
                    boundaryPosition.Line,
                    out var speculativeConditionalBoundary)
                && speculativeConditionalBoundary.Kind == originalConditionalBoundary.Kind
                && PreservesStructuralRange(
                    originalConditionalBoundary.Range,
                    speculativeConditionalBoundary.Range,
                    edit)
                && candidateBlock.CloserRange!.End.Offset
                    <= speculativeConditionalBoundary.Range.Start.Offset;
        }

        var speculativeBoundaryLine = boundaryPosition.Line;
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
        if (boundaryHeader is null
            || !IsModuleDeclarationHeader(boundaryHeader.Kind)
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

        var originalBoundaryOpener = MapFollowingRangeToOriginal(
            speculativeBoundaryBlock.OpenerRange,
            edit);
        if (originalBoundaryOpener is null)
        {
            return false;
        }

        var originalBoundaryBlock = FindUniqueBlock(snapshot.SyntaxTree.Module.Blocks, block =>
            block.Kind == GetStructuralKind(boundaryHeader.Kind)
            && block.ExpectedTerminator.Equals(
                boundaryHeader.ExpectedTerminator,
                StringComparison.OrdinalIgnoreCase)
            && block.OpenerRange == originalBoundaryOpener);
        if (originalBoundaryBlock is null)
        {
            return false;
        }

        return originalBoundaryBlock.CloserRange is null
            ? speculativeBoundaryBlock.CloserRange is null
            : PreservesStructuralRange(
                originalBoundaryBlock.CloserRange,
                speculativeBoundaryBlock.CloserRange,
                edit);
    }

    private static VbaSyntaxRange? MapFollowingRangeToOriginal(
        VbaSyntaxRange range,
        VbaSourceTextEditResult edit)
    {
        if (range.Start.Offset < edit.Replacements[0].AfterEndOffset
            || range.End.Offset < range.Start.Offset)
        {
            return null;
        }

        foreach (var span in edit.UnchangedSpans)
        {
            if (span.AfterStartOffset <= range.Start.Offset && range.End.Offset <= span.AfterEndOffset
                && edit.Before.TryGetPosition(
                    span.BeforeStartOffset + (range.Start.Offset - span.AfterStartOffset), out var start)
                && edit.Before.TryGetPosition(
                    span.BeforeStartOffset + (range.End.Offset - span.AfterStartOffset), out var end))
            {
                return new(start, end);
            }
        }

        return null;
    }

    private static VbaBlockSyntax? FindBlock(
        VbaSyntaxTree tree,
        VbaBlockHeaderSyntax header)
        => FindUniqueBlock(tree.Module.Blocks, block =>
            block.Kind == GetStructuralKind(header.Kind)
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

    // An enclosing block may contain the edit; only its own endpoints must correspond.
    private static bool PreservesStructuralRange(
        VbaSyntaxRange original,
        VbaSyntaxRange? prospective,
        VbaSourceTextEditResult edit)
        => original.End.Offset >= original.Start.Offset
            && TryMapStructuralBoundary(original.Start.Offset, edit, out var start)
            && TryMapStructuralBoundary(original.End.Offset, edit, out var end)
            && prospective == new VbaSyntaxRange(start, end);

    private static bool TryMapStructuralBoundary(
        int offset,
        VbaSourceTextEditResult edit,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out VbaSyntaxPosition? position)
    {
        position = null;
        if (!edit.Before.TryGetPosition(offset, out _))
        {
            return false;
        }

        var replacement = edit.Replacements[0];
        // Preserve the following-side affinity when an insertion has a single before endpoint.
        if (offset == replacement.BeforeEndOffset)
        {
            return edit.After.TryGetPosition(replacement.AfterEndOffset, out position);
        }

        if (offset == replacement.BeforeStartOffset)
        {
            return edit.After.TryGetPosition(replacement.AfterStartOffset, out position);
        }

        foreach (var span in edit.UnchangedSpans)
        {
            if (span.BeforeStartOffset <= offset && offset <= span.BeforeEndOffset)
            {
                return edit.After.TryGetPosition(
                    span.AfterStartOffset + (offset - span.BeforeStartOffset), out position);
            }
        }

        return false;
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
        VbaSourceTextEditResult edit)
    {
        var directMissing = snapshot.Diagnostics.SyntaxDiagnostics
            .Where(diagnostic => IsError(diagnostic.Severity))
            .Where(diagnostic => IsDirectMissingTerminator(diagnostic, header))
            .ToArray();
        var derivedDirectMissingCount = snapshot.IsOwnedByAnalysis
            ? directMissing.Length
            : VbaDiagnosticPipeline
                .CollectDocument(snapshot.SyntaxTree, snapshot.Uri)
                .SyntaxDiagnostics
                .Count(diagnostic =>
                    IsError(diagnostic.Severity)
                    && IsDirectMissingTerminator(diagnostic, header));
        if (directMissing.Length != derivedDirectMissingCount)
        {
            return false;
        }

        return BlockSkeletonInsertionDiagnosticProof.IsSafe(new(
            new(snapshot.SourceText, snapshot.Diagnostics),
            new(edit.After, speculativeDiagnostics),
            new(snapshot.SourceText, new(directMissing, [], [])),
            edit));
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


    private static VbaBlockKind GetStructuralKind(VbaBlockHeaderKind headerKind)
        => headerKind switch
        {
            VbaBlockHeaderKind.Sub => VbaBlockKind.Procedure,
            VbaBlockHeaderKind.Function => VbaBlockKind.Procedure,
            VbaBlockHeaderKind.PropertyGet => VbaBlockKind.Procedure,
            VbaBlockHeaderKind.PropertyLet => VbaBlockKind.Procedure,
            VbaBlockHeaderKind.PropertySet => VbaBlockKind.Procedure,
            VbaBlockHeaderKind.If => VbaBlockKind.If,
            VbaBlockHeaderKind.With => VbaBlockKind.With,
            VbaBlockHeaderKind.For => VbaBlockKind.For,
            VbaBlockHeaderKind.ForEach => VbaBlockKind.For,
            VbaBlockHeaderKind.SelectCase => VbaBlockKind.Select,
            VbaBlockHeaderKind.Enum => VbaBlockKind.Enum,
            VbaBlockHeaderKind.Type => VbaBlockKind.Type,
            _ => VbaBlockKind.Malformed
        };

    private static bool IsModuleDeclarationHeader(VbaBlockHeaderKind headerKind)
        => headerKind is VbaBlockHeaderKind.Sub
            or VbaBlockHeaderKind.Function
            or VbaBlockHeaderKind.PropertyGet
            or VbaBlockHeaderKind.PropertyLet
            or VbaBlockHeaderKind.PropertySet
            or VbaBlockHeaderKind.Enum
            or VbaBlockHeaderKind.Type;

    private abstract record BlockBoundaryProof;

    private sealed record AncestorBlockBoundaryProof(
        int AncestorIndex,
        VbaBlockBoundarySyntax Boundary)
        : BlockBoundaryProof;

    private sealed record ConditionalCompilationBoundaryProof(
        VbaConditionalCompilationBranchPath Path,
        VbaConditionalCompilationBoundary Boundary)
        : BlockBoundaryProof;
}
