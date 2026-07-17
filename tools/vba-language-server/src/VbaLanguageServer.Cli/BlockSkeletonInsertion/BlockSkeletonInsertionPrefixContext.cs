using VbaLanguageServer.Syntax;
using VbaLanguageServer.Workspace;

namespace VbaLanguageServer.BlockSkeletonInsertion;

internal sealed record BlockSkeletonInsertionPrefixBlock(
    VbaBlockKind Kind,
    string ExpectedTerminator,
    VbaSyntaxRange OpenerRange,
    VbaSyntaxRange StatementRange,
    string LeadingWhitespace,
    VbaConditionalCompilationBranchPath ConditionalCompilationBranchPath);

internal sealed record BlockSkeletonInsertionPrefixContext(
    BlockSkeletonInsertionPrefixBlock Candidate,
    IReadOnlyList<BlockSkeletonInsertionPrefixBlock> Ancestors)
{
    public static bool TryCreate(
        VbaVersionedDocumentSnapshot snapshot,
        VbaBlockHeaderSyntax header,
        int headerEndOffset,
        out BlockSkeletonInsertionPrefixContext context)
    {
        context = default!;
        if (headerEndOffset != header.Range.End.Offset
            || headerEndOffset < 0
            || headerEndOffset > snapshot.Text.Length)
        {
            return false;
        }

        var prefixText = snapshot.Text[..headerEndOffset];
        var prefixTree = VbaSyntaxTree.ParseModule(snapshot.Uri, prefixText);
        if (prefixTree.Module.Kind != snapshot.ModuleKind)
        {
            return false;
        }

        var prefixHeader = VbaBlockHeaderSyntax.FindAtPrefixPosition(
            prefixTree,
            header.FinalPhysicalLine,
            header.Range.End.Character);
        if (prefixHeader != header)
        {
            return false;
        }

        var expectedKind = GetStructuralKind(header.Kind);
        var candidateMatches = prefixTree.Module.Blocks
            .Where(block =>
                block.Kind == expectedKind
                && block.CloserRange is null
                && block.ExpectedTerminator.Equals(
                    header.ExpectedTerminator,
                    StringComparison.OrdinalIgnoreCase)
                && header.Range.Start.Offset <= block.OpenerRange.Start.Offset
                && block.OpenerRange.End.Offset <= header.Range.End.Offset)
            .Take(2)
            .ToArray();
        if (candidateMatches.Length != 1)
        {
            return false;
        }

        var candidateBlock = candidateMatches[0];
        if (prefixTree.Module.Blocks.Any(block =>
            block.IsMalformedBarrier
            && block.Range.Start.Offset < candidateBlock.OpenerRange.Start.Offset
            && candidateBlock.OpenerRange.Start.Offset <= block.Range.End.Offset
            && VbaConditionalCompilationBranchFacts
                .CanMalformedBarrierAffectPath(
                    prefixTree,
                    block,
                    header.ConditionalCompilationBranchPath,
                    requireCompleteStructure: false)))
        {
            return false;
        }

        var openBlocks = prefixTree.Module.Blocks
            .Where(block =>
                !block.IsMalformedBarrier
                && block.CloserRange is null
                && block.Range.End.Offset == prefixText.Length
                && block.OpenerRange.Start.Offset <= candidateBlock.OpenerRange.Start.Offset)
            .ToArray();
        var openPath = new List<VbaBlockSyntax>(openBlocks.Length);
        foreach (var block in openBlocks)
        {
            if (!VbaConditionalCompilationBranchFacts.TryGetPath(
                prefixTree,
                block.OpenerRange,
                requireCompleteStructure: false,
                out var blockPath))
            {
                return false;
            }

            if (blockPath.IsPrefixOf(header.ConditionalCompilationBranchPath))
            {
                openPath.Add(block);
                continue;
            }

            if (VbaConditionalCompilationBranchFacts.CanCoexist(
                blockPath,
                header.ConditionalCompilationBranchPath))
            {
                return false;
            }
        }

        var orderedOpenPath = openPath
            .OrderBy(block => block.OpenerRange.Start.Offset)
            .ThenByDescending(block => block.OpenerRange.End.Offset)
            .ToArray();
        var candidateIndexes = orderedOpenPath
            .Select((block, index) => (block, index))
            .Where(item => item.block == candidateBlock)
            .Select(item => item.index)
            .ToArray();
        if (candidateIndexes.Length != 1
            || candidateIndexes[0] != orderedOpenPath.Length - 1)
        {
            return false;
        }

        if (orderedOpenPath
            .Take(orderedOpenPath.Length - 1)
            .Any(block => !VbaBlockAncestorSyntax.IsCompletePrefix(
                prefixTree,
                block,
                header.ConditionalCompilationBranchPath)))
        {
            return false;
        }

        var source = VbaSourceText.From(prefixText);
        var path = new List<BlockSkeletonInsertionPrefixBlock>(orderedOpenPath.Length);
        foreach (var block in orderedOpenPath)
        {
            if (!TryCreateBlock(prefixTree, source, block, out var prefixBlock))
            {
                return false;
            }

            path.Add(prefixBlock);
        }

        var candidate = path[^1];
        var ancestors = path.Take(path.Count - 1).ToArray();
        if (!candidate.LeadingWhitespace.Equals(header.LeadingWhitespace, StringComparison.Ordinal)
            || !candidate.ConditionalCompilationBranchPath.Equals(
                header.ConditionalCompilationBranchPath)
            || !HasEligibleAncestry(header.Kind, ancestors, candidate))
        {
            return false;
        }

        context = new BlockSkeletonInsertionPrefixContext(candidate, ancestors);
        return true;
    }

    private static bool HasEligibleAncestry(
        VbaBlockHeaderKind headerKind,
        IReadOnlyList<BlockSkeletonInsertionPrefixBlock> ancestors,
        BlockSkeletonInsertionPrefixBlock candidate)
    {
        if (headerKind == VbaBlockHeaderKind.Sub)
        {
            return ancestors.Count == 0;
        }

        if (headerKind is not VbaBlockHeaderKind.If
            and not VbaBlockHeaderKind.With
            and not VbaBlockHeaderKind.For
            and not VbaBlockHeaderKind.ForEach
            and not VbaBlockHeaderKind.SelectCase
            || ancestors.Count == 0
            || ancestors[0].Kind != VbaBlockKind.Procedure
            || ancestors.Count(block => block.Kind == VbaBlockKind.Procedure) != 1
            || ancestors.Any(block => block.Kind is VbaBlockKind.Enum
                or VbaBlockKind.Type
                or VbaBlockKind.Malformed))
        {
            return false;
        }

        var path = ancestors.Append(candidate).ToArray();
        for (var index = 1; index < path.Length; index++)
        {
            var parentIndentation = path[index - 1].LeadingWhitespace;
            var childIndentation = path[index].LeadingWhitespace;
            if (!path[index - 1].ConditionalCompilationBranchPath.IsPrefixOf(
                    path[index].ConditionalCompilationBranchPath)
                || childIndentation.Length <= parentIndentation.Length
                || !childIndentation.StartsWith(parentIndentation, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryCreateBlock(
        VbaSyntaxTree tree,
        VbaSourceText source,
        VbaBlockSyntax block,
        out BlockSkeletonInsertionPrefixBlock prefixBlock)
    {
        prefixBlock = default!;
        var firstLine = source.Lines[block.OpenerRange.Start.Line];
        var finalLine = source.Lines[block.OpenerRange.End.Line];
        var leadingWhitespaceLength = firstLine.Text
            .TakeWhile(value => value is ' ' or '\t')
            .Count();
        if (!VbaConditionalCompilationBranchFacts.TryGetPath(
            tree,
            block.OpenerRange,
            requireCompleteStructure: false,
            out var conditionalCompilationBranchPath))
        {
            return false;
        }

        prefixBlock = new BlockSkeletonInsertionPrefixBlock(
            block.Kind,
            block.ExpectedTerminator,
            block.OpenerRange,
            new VbaSyntaxRange(
                new VbaSyntaxPosition(
                    firstLine.LineNumber,
                    0,
                    firstLine.StartOffset),
                new VbaSyntaxPosition(
                    finalLine.LineNumber,
                    finalLine.Text.Length,
                    finalLine.EndOffset)),
            firstLine.Text[..leadingWhitespaceLength],
            conditionalCompilationBranchPath);
        return true;
    }

    private static VbaBlockKind GetStructuralKind(VbaBlockHeaderKind headerKind)
        => headerKind switch
        {
            VbaBlockHeaderKind.Sub => VbaBlockKind.Procedure,
            VbaBlockHeaderKind.If => VbaBlockKind.If,
            VbaBlockHeaderKind.With => VbaBlockKind.With,
            VbaBlockHeaderKind.For => VbaBlockKind.For,
            VbaBlockHeaderKind.ForEach => VbaBlockKind.For,
            VbaBlockHeaderKind.SelectCase => VbaBlockKind.Select,
            _ => VbaBlockKind.Malformed
        };
}
