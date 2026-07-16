using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.Syntax;
using VbaLanguageServer.Workspace;

namespace VbaLanguageServer.BlockSkeletonInsertion;

/// <summary>
/// Represents the original physical header-end position carried by an insertion request.
/// </summary>
public sealed record BlockSkeletonInsertionPosition(int Line, int Character);

/// <summary>
/// Contains literal replacement text around the one body cursor.
/// </summary>
public sealed record BlockSkeletonInsertionPlan(
    int DocumentVersion,
    BlockSkeletonInsertionPosition Position,
    string TextBeforeCursor,
    string TextAfterCursor);

/// <summary>
/// Plans fail-closed block skeleton insertion from one immutable document snapshot.
/// </summary>
public static class BlockSkeletonInsertionPlanner
{
    /// <summary>
    /// Creates a narrow insertion plan at EOF or a structurally proven boundary.
    /// </summary>
    public static BlockSkeletonInsertionPlan? CreatePlan(
        VbaVersionedDocumentSnapshot snapshot,
        BlockSkeletonInsertionPosition position,
        VbaIndentationStyle indentationStyle)
    {
        if (snapshot.ModuleKind != snapshot.SyntaxTree.Module.Kind)
        {
            return null;
        }

        var header = VbaBlockHeaderSyntax.FindAtPosition(
            snapshot.SyntaxTree,
            position.Line,
            position.Character);
        if (header is null
            || !TryGetPostNativeContext(snapshot.Text, position, out var context))
        {
            return null;
        }

        var bodyIndentation = header.LeadingWhitespace
            + indentationStyle.CreateLeadingWhitespace(1);
        var plan = new BlockSkeletonInsertionPlan(
            snapshot.Version,
            position,
            context.LineEnding + bodyIndentation,
            context.LineEnding + header.LeadingWhitespace + header.ExpectedTerminator);
        return BlockSkeletonInsertionSpeculation.IsSafe(
            snapshot,
            header,
            plan,
            context.InsertionStartOffset,
            context.InsertionEndOffset,
            context.FirstFollowingContentLine,
            context.LineEnding)
                ? plan
                : null;
    }

    private static bool TryGetPostNativeContext(
        string text,
        BlockSkeletonInsertionPosition position,
        out PostNativeContext context)
    {
        context = default!;
        var source = VbaSourceText.From(text);
        if (position.Line < 0
            || position.Line >= source.Lines.Count
            || position.Character < 0
            || position.Character != source.Lines[position.Line].Text.Length)
        {
            return false;
        }

        var positionOffset = source.Lines[position.Line].StartOffset + position.Character;
        var suffix = text[positionOffset..];
        var lineEnding = suffix.StartsWith("\r\n", StringComparison.Ordinal)
            ? "\r\n"
            : suffix.StartsWith("\n", StringComparison.Ordinal)
                ? "\n"
                : suffix.StartsWith("\r", StringComparison.Ordinal)
                    ? "\r"
                    : string.Empty;
        if (lineEnding.Length == 0)
        {
            return false;
        }

        var insertionEndOffset = positionOffset + lineEnding.Length;
        while (insertionEndOffset < text.Length
            && text[insertionEndOffset] is ' ' or '\t')
        {
            insertionEndOffset++;
        }

        if (insertionEndOffset < text.Length
            && text[insertionEndOffset] is not '\r' and not '\n')
        {
            return false;
        }

        int? firstFollowingContentLine = null;
        for (var lineIndex = position.Line + 2; lineIndex < source.Lines.Count; lineIndex++)
        {
            var lineText = source.Lines[lineIndex].Text;
            if (lineText.All(value => value is ' ' or '\t'))
            {
                continue;
            }

            firstFollowingContentLine = lineIndex;
            break;
        }

        context = new PostNativeContext(
            lineEnding,
            positionOffset,
            insertionEndOffset,
            firstFollowingContentLine);
        return true;
    }

    private sealed record PostNativeContext(
        string LineEnding,
        int InsertionStartOffset,
        int InsertionEndOffset,
        int? FirstFollowingContentLine);
}
