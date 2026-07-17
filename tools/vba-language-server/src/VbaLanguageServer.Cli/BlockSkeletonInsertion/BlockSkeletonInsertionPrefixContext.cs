using VbaLanguageServer.Syntax;
using VbaLanguageServer.Workspace;

namespace VbaLanguageServer.BlockSkeletonInsertion;

internal sealed record BlockSkeletonInsertionPrefixBlock(
    VbaBlockKind Kind,
    string ExpectedTerminator,
    VbaSyntaxRange OpenerRange,
    VbaSyntaxRange StatementRange,
    string LeadingWhitespace);

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

        var prefixHeader = VbaBlockHeaderSyntax.FindAtPosition(
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
            && candidateBlock.OpenerRange.Start.Offset <= block.Range.End.Offset))
        {
            return false;
        }

        var openPath = prefixTree.Module.Blocks
            .Where(block =>
                !block.IsMalformedBarrier
                && block.CloserRange is null
                && block.Range.End.Offset == prefixText.Length
                && block.OpenerRange.Start.Offset <= candidateBlock.OpenerRange.Start.Offset)
            .OrderBy(block => block.OpenerRange.Start.Offset)
            .ThenByDescending(block => block.OpenerRange.End.Offset)
            .ToArray();
        var candidateIndexes = openPath
            .Select((block, index) => (block, index))
            .Where(item => item.block == candidateBlock)
            .Select(item => item.index)
            .ToArray();
        if (candidateIndexes.Length != 1 || candidateIndexes[0] != openPath.Length - 1)
        {
            return false;
        }

        if (openPath
            .Take(openPath.Length - 1)
            .Any(block => !VbaBlockAncestorSyntax.IsComplete(prefixTree, block)))
        {
            return false;
        }

        var source = VbaSourceText.From(prefixText);
        var path = openPath.Select(block => CreateBlock(source, block)).ToArray();
        var candidate = path[^1];
        var ancestors = path[..^1];
        if (!candidate.LeadingWhitespace.Equals(header.LeadingWhitespace, StringComparison.Ordinal)
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
            if (childIndentation.Length <= parentIndentation.Length
                || !childIndentation.StartsWith(parentIndentation, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static BlockSkeletonInsertionPrefixBlock CreateBlock(
        VbaSourceText source,
        VbaBlockSyntax block)
    {
        var firstLine = source.Lines[block.OpenerRange.Start.Line];
        var finalLine = source.Lines[block.OpenerRange.End.Line];
        var leadingWhitespaceLength = firstLine.Text
            .TakeWhile(value => value is ' ' or '\t')
            .Count();
        return new BlockSkeletonInsertionPrefixBlock(
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
            firstLine.Text[..leadingWhitespaceLength]);
    }

    private static VbaBlockKind GetStructuralKind(VbaBlockHeaderKind headerKind)
        => headerKind switch
        {
            VbaBlockHeaderKind.Sub => VbaBlockKind.Procedure,
            VbaBlockHeaderKind.If => VbaBlockKind.If,
            VbaBlockHeaderKind.With => VbaBlockKind.With,
            VbaBlockHeaderKind.For => VbaBlockKind.For,
            VbaBlockHeaderKind.ForEach => VbaBlockKind.For,
            _ => VbaBlockKind.Malformed
        };
}
