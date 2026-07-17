namespace VbaLanguageServer.Syntax;

/// <summary>
/// Validates the local syntax owned by a supported body-owning ancestor.
/// </summary>
public static class VbaBlockAncestorSyntax
{
    /// <summary>
    /// Determines whether a supported ancestor has a complete locally strict shape.
    /// A missing closer is allowed during editing; any present branch or closer must be exact.
    /// </summary>
    /// <param name="tree">The parsed source snapshot that owns the block.</param>
    /// <param name="block">The structured ancestor block to validate.</param>
    /// <returns>True only when the supported ancestor form is locally complete.</returns>
    public static bool IsComplete(VbaSyntaxTree tree, VbaBlockSyntax block)
        => IsCompleteCore(
            tree,
            block,
            requireCompleteConditionalStructure: true,
            selectedLeafPath: null);

    internal static bool IsComplete(
        VbaSyntaxTree tree,
        VbaBlockSyntax block,
        VbaConditionalCompilationBranchPath selectedLeafPath)
        => IsCompleteCore(
            tree,
            block,
            requireCompleteConditionalStructure: true,
            selectedLeafPath);

    internal static bool IsCompletePrefix(
        VbaSyntaxTree tree,
        VbaBlockSyntax block,
        VbaConditionalCompilationBranchPath selectedLeafPath)
        => IsCompleteCore(
            tree,
            block,
            requireCompleteConditionalStructure: false,
            selectedLeafPath);

    private static bool IsCompleteCore(
        VbaSyntaxTree tree,
        VbaBlockSyntax block,
        bool requireCompleteConditionalStructure,
        VbaConditionalCompilationBranchPath? selectedLeafPath)
    {
        if (block.IsMalformedBarrier
            || !VbaConditionalCompilationBranchFacts.TryGetPath(
                tree,
                block.OpenerRange,
                requireCompleteConditionalStructure,
                out var conditionalCompilationBranchPath)
            || selectedLeafPath is not null
                && !conditionalCompilationBranchPath.IsPrefixOf(selectedLeafPath)
            || !VbaConditionalCompilationBranchFacts.IsBlockLocal(
                tree,
                block,
                conditionalCompilationBranchPath,
                requireCompleteConditionalStructure)
            || HasCoexistingMalformedBarrier(
                tree,
                block,
                selectedLeafPath ?? conditionalCompilationBranchPath,
                requireCompleteConditionalStructure))
        {
            return false;
        }

        return block.Kind switch
        {
            VbaBlockKind.Procedure => IsCompleteProcedure(
                tree,
                block,
                requireCompleteConditionalStructure),
            VbaBlockKind.If => IsCompleteIf(
                tree,
                block,
                requireCompleteConditionalStructure),
            VbaBlockKind.With => IsCompleteWith(
                tree,
                block,
                requireCompleteConditionalStructure),
            VbaBlockKind.For => IsCompleteFor(
                tree,
                block,
                requireCompleteConditionalStructure),
            VbaBlockKind.Select => IsCompleteSelect(
                tree,
                block,
                requireCompleteConditionalStructure),
            _ => false
        };
    }

    private static bool HasCoexistingMalformedBarrier(
        VbaSyntaxTree tree,
        VbaBlockSyntax block,
        VbaConditionalCompilationBranchPath selectedLeafPath,
        bool requireCompleteConditionalStructure)
        => tree.Module.Blocks.Any(candidate =>
            candidate.IsMalformedBarrier
            && candidate.Range.Start.Offset <= block.Range.End.Offset
            && block.Range.Start.Offset <= candidate.Range.End.Offset
            && VbaConditionalCompilationBranchFacts
                .CanMalformedBarrierAffectPath(
                    tree,
                    candidate,
                    selectedLeafPath,
                    requireCompleteConditionalStructure));

    private static bool IsCompleteProcedure(
        VbaSyntaxTree tree,
        VbaBlockSyntax block,
        bool requireCompleteConditionalStructure)
    {
        var header = VbaBlockHeaderSyntax.FindCompleteCallableAncestor(
            tree,
            block.OpenerRange,
            requireCompleteConditionalStructure);
        return header is not null
            && block.ExpectedTerminator.Equals(
                header.ExpectedTerminator,
                StringComparison.OrdinalIgnoreCase)
            && HasExactCloserWhenPresent(tree, block);
    }

    private static bool IsCompleteIf(
        VbaSyntaxTree tree,
        VbaBlockSyntax block,
        bool requireCompleteConditionalStructure)
        => block.ExpectedTerminator.Equals("End If", StringComparison.OrdinalIgnoreCase)
            && VbaBlockHeaderSyntax.IsCompleteIfAncestor(
                tree,
                block.OpenerRange,
                requireCompleteConditionalStructure)
            && HasExactIfBranches(tree, block)
            && HasExactCloserWhenPresent(tree, block);

    private static bool IsCompleteWith(
        VbaSyntaxTree tree,
        VbaBlockSyntax block,
        bool requireCompleteConditionalStructure)
        => block.ExpectedTerminator.Equals("End With", StringComparison.OrdinalIgnoreCase)
            && VbaBlockHeaderSyntax.IsCompleteWithAncestor(
                tree,
                block.OpenerRange,
                requireCompleteConditionalStructure)
            && block.Branches.Count == 0
            && HasExactCloserWhenPresent(tree, block);

    private static bool IsCompleteFor(
        VbaSyntaxTree tree,
        VbaBlockSyntax block,
        bool requireCompleteConditionalStructure)
        => block.ExpectedTerminator.Equals("Next", StringComparison.OrdinalIgnoreCase)
            && VbaBlockHeaderSyntax.IsCompleteForAncestor(
                tree,
                block.OpenerRange,
                requireCompleteConditionalStructure)
            && block.Branches.Count == 0
            && HasExactCloserWhenPresent(tree, block);

    private static bool IsCompleteSelect(
        VbaSyntaxTree tree,
        VbaBlockSyntax block,
        bool requireCompleteConditionalStructure)
        => block.ExpectedTerminator.Equals(
                "End Select",
                StringComparison.OrdinalIgnoreCase)
            && VbaBlockHeaderSyntax.IsCompleteSelectCaseAncestor(
                tree,
                block.OpenerRange,
                requireCompleteConditionalStructure)
            && HasOnlyTriviaBeforeFirstSelectBranch(tree, block)
            && HasExactSelectBranches(tree, block)
            && HasExactCloserWhenPresent(tree, block);

    private static bool HasExactIfBranches(
        VbaSyntaxTree tree,
        VbaBlockSyntax block)
    {
        if (block.Branches.Count == 0
            || block.Branches[0].Kind != VbaBlockBranchKind.Then
            || !block.Branches[0].HeaderRange.Equals(block.OpenerRange))
        {
            return false;
        }

        var hasElse = false;
        for (var index = 1; index < block.Branches.Count; index++)
        {
            var branch = block.Branches[index];
            if (branch.Kind is not VbaBlockBranchKind.ElseIf and not VbaBlockBranchKind.Else
                || hasElse)
            {
                return false;
            }

            hasElse = branch.Kind == VbaBlockBranchKind.Else;
            var boundary = VbaBlockBoundarySyntax.FindAtFirstPhysicalLine(
                tree,
                branch.HeaderRange.Start.Line,
                VbaBlockKind.If,
                block.ExpectedTerminator);
            if (boundary is null
                || boundary.Role != VbaBlockBoundaryRole.Branch
                || boundary.BranchKind != branch.Kind
                || !boundary.TokenRange.Equals(branch.HeaderRange))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasExactSelectBranches(
        VbaSyntaxTree tree,
        VbaBlockSyntax block)
    {
        var hasCaseElse = false;
        foreach (var branch in block.Branches)
        {
            if (branch.Kind is not VbaBlockBranchKind.Case
                and not VbaBlockBranchKind.CaseElse
                || hasCaseElse)
            {
                return false;
            }

            hasCaseElse = branch.Kind == VbaBlockBranchKind.CaseElse;
            var boundary = VbaBlockBoundarySyntax.FindAtFirstPhysicalLine(
                tree,
                branch.HeaderRange.Start.Line,
                VbaBlockKind.Select,
                block.ExpectedTerminator);
            if (boundary is null
                || boundary.Role != VbaBlockBoundaryRole.Branch
                || boundary.BranchKind != branch.Kind
                || !boundary.TokenRange.Equals(branch.HeaderRange))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasOnlyTriviaBeforeFirstSelectBranch(
        VbaSyntaxTree tree,
        VbaBlockSyntax block)
    {
        var boundaryOffset = block.Branches.Count > 0
            ? block.Branches[0].HeaderRange.Start.Offset
            : block.CloserRange?.Start.Offset ?? block.Range.End.Offset;
        return !VbaLogicalStatementSpan
            .Build(tree.SourceText.Text.Length, tree.TokenStream.Tokens)
            .Any(statement =>
                statement.SignificantTokens.Count > 0
                && !statement.SignificantTokens[0].Text.Equals(
                    "Rem",
                    StringComparison.OrdinalIgnoreCase)
                && block.OpenerRange.End.Offset
                    <= statement.SignificantTokens[0].Range.Start.Offset
                && statement.SignificantTokens[0].Range.Start.Offset < boundaryOffset);
    }

    private static bool HasExactCloserWhenPresent(
        VbaSyntaxTree tree,
        VbaBlockSyntax block)
    {
        if (block.CloserRange is null)
        {
            return true;
        }

        var closer = VbaBlockBoundarySyntax.FindAtFirstPhysicalLine(
            tree,
            block.CloserRange.Start.Line,
            block.Kind,
            block.ExpectedTerminator);
        return closer is not null
            && closer.Role == VbaBlockBoundaryRole.Closer
            && closer.TokenRange.Equals(block.CloserRange);
    }
}
