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
    /// Creates the narrow EOF Sub insertion plan admitted by the first production slice.
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
        if (header?.Kind != VbaBlockHeaderKind.Sub
            || HasDisqualifyingDiagnostics(snapshot, header)
            || !TryGetPostNativeEofLineEnding(snapshot.Text, position, out var lineEnding))
        {
            return null;
        }

        var bodyIndentation = header.LeadingWhitespace
            + indentationStyle.CreateLeadingWhitespace(1);
        return new BlockSkeletonInsertionPlan(
            snapshot.Version,
            position,
            lineEnding + bodyIndentation,
            lineEnding + header.LeadingWhitespace + header.ExpectedTerminator);
    }

    private static bool HasDisqualifyingDiagnostics(
        VbaVersionedDocumentSnapshot snapshot,
        VbaBlockHeaderSyntax header)
        => snapshot.Diagnostics.SyntaxDiagnostics.Any(diagnostic =>
                IsError(diagnostic.Severity)
                && Overlaps(diagnostic.Range, header.Range)
                && !(diagnostic.Code.Equals(
                        "syntax.missingBlockTerminator",
                        StringComparison.Ordinal)
                    && diagnostic.Message.Contains(
                        header.ExpectedTerminator,
                        StringComparison.OrdinalIgnoreCase)))
            || snapshot.Diagnostics.DocumentValidationDiagnostics.Any(diagnostic =>
                IsError(diagnostic.Severity)
                && Overlaps(diagnostic.Range, header.Range));

    private static bool IsError(string severity)
        => severity.Equals("error", StringComparison.OrdinalIgnoreCase);

    private static bool Overlaps(VbaRange diagnostic, VbaSyntaxRange header)
        => Compare(
                diagnostic.Start.Line,
                diagnostic.Start.Character,
                header.End.Line,
                header.End.Character) <= 0
            && Compare(
                header.Start.Line,
                header.Start.Character,
                diagnostic.End.Line,
                diagnostic.End.Character) <= 0;

    private static int Compare(
        int leftLine,
        int leftCharacter,
        int rightLine,
        int rightCharacter)
        => leftLine != rightLine
            ? leftLine.CompareTo(rightLine)
            : leftCharacter.CompareTo(rightCharacter);

    private static bool TryGetPostNativeEofLineEnding(
        string text,
        BlockSkeletonInsertionPosition position,
        out string lineEnding)
    {
        lineEnding = string.Empty;
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
        lineEnding = suffix.StartsWith("\r\n", StringComparison.Ordinal)
            ? "\r\n"
            : suffix.StartsWith("\n", StringComparison.Ordinal)
                ? "\n"
                : suffix.StartsWith("\r", StringComparison.Ordinal)
                    ? "\r"
                    : string.Empty;
        return lineEnding.Length > 0
            && suffix[lineEnding.Length..].All(value => value is ' ' or '\t');
    }
}
