namespace VbaLanguageServer.Syntax;

/// <summary>
/// Validates the local syntax owned by a Procedure or block If ancestor.
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
    {
        if (block.IsMalformedBarrier
            || tree.Module.Blocks.Any(candidate =>
                candidate.IsMalformedBarrier
                && candidate.Range.Start.Offset <= block.Range.End.Offset
                && block.Range.Start.Offset <= candidate.Range.End.Offset))
        {
            return false;
        }

        return block.Kind switch
        {
            VbaBlockKind.Procedure => IsCompleteProcedure(tree, block),
            VbaBlockKind.If => IsCompleteIf(tree, block),
            _ => false
        };
    }

    private static bool IsCompleteProcedure(
        VbaSyntaxTree tree,
        VbaBlockSyntax block)
    {
        var header = VbaBlockHeaderSyntax.FindCompleteCallableAncestor(
            tree,
            block.OpenerRange);
        return header is not null
            && block.ExpectedTerminator.Equals(
                header.ExpectedTerminator,
                StringComparison.OrdinalIgnoreCase)
            && HasExactCloserWhenPresent(tree, block);
    }

    private static bool IsCompleteIf(
        VbaSyntaxTree tree,
        VbaBlockSyntax block)
        => block.ExpectedTerminator.Equals("End If", StringComparison.OrdinalIgnoreCase)
            && VbaBlockHeaderSyntax.IsCompleteIfAncestor(tree, block.OpenerRange)
            && HasExactIfBranches(tree, block)
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
