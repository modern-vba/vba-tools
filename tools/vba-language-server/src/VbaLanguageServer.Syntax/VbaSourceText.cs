namespace VbaLanguageServer.Syntax;

/// <summary>
/// Represents an identifier occurrence found on one physical source line.
/// </summary>
/// <param name="Name">The identifier text.</param>
/// <param name="Start">The inclusive character start on the line.</param>
/// <param name="End">The exclusive character end on the line.</param>
public sealed record VbaIdentifierOccurrence(string Name, int Start, int End);

/// <summary>
/// Represents one physical source line with absolute offsets.
/// </summary>
/// <param name="LineNumber">The zero-based physical line number.</param>
/// <param name="Text">The line text without newline characters.</param>
/// <param name="StartOffset">The inclusive source offset where the line starts.</param>
/// <param name="EndOffset">The exclusive source offset where the line text ends.</param>
public sealed record VbaSourceLine(
    int LineNumber,
    string Text,
    int StartOffset,
    int EndOffset);

/// <summary>
/// Provides a source-range-aware model and helpers for VBA source text.
/// </summary>
public sealed class VbaSourceText
{
    private readonly VbaSourceLine[] indexedLines;
    private readonly bool[] blankLines;

    private VbaSourceText(
        string text,
        VbaSourceLine[] lines,
        bool[] blankLines,
        VbaSyntaxPosition startPosition,
        VbaSyntaxRange fullRange)
    {
        Text = text;
        indexedLines = lines;
        this.blankLines = blankLines;
        Lines = Array.AsReadOnly(lines);
        StartPosition = startPosition;
        FullRange = fullRange;
    }

    /// <summary>
    /// Gets the complete source text.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// Gets physical source lines with absolute offsets.
    /// </summary>
    public IReadOnlyList<VbaSourceLine> Lines { get; }

    /// <summary>
    /// Gets the start position of the source document.
    /// </summary>
    public VbaSyntaxPosition StartPosition { get; }

    /// <summary>
    /// Gets the full source range.
    /// </summary>
    public VbaSyntaxRange FullRange { get; }

    /// <summary>
    /// Gets whether the source text contains no characters.
    /// </summary>
    public bool IsEmpty => Text.Length == 0;

    /// <summary>
    /// Creates a source text model that tracks line boundaries and the full source range.
    /// </summary>
    /// <param name="source">The source text to index.</param>
    /// <returns>The indexed source text.</returns>
    public static VbaSourceText From(string source)
    {
        const int maximumCachedSpaceCount = 128;
        var spacesByLength = new string?[maximumCachedSpaceCount + 1];
        var lines = new List<VbaSourceLine>(
            Math.Max(1, source.Length / 32));
        var blankLines = new List<bool>(lines.Capacity);
        var line = 0;
        var offset = 0;
        while (offset <= source.Length)
        {
            var startOffset = offset;
            var remainingSource = source.AsSpan(offset);
            var relativeNewLineOffset = remainingSource.IndexOfAny('\r', '\n');
            var lineLength = relativeNewLineOffset < 0
                ? remainingSource.Length
                : relativeNewLineOffset;
            var lineSpan = remainingSource[..lineLength];
            var containsOnlySpaces = lineSpan.IndexOfAnyExcept(' ') < 0;
            offset += lineLength;

            var length = offset - startOffset;
            var lineText = containsOnlySpaces && length <= maximumCachedSpaceCount
                ? spacesByLength[length] ??= new string(' ', length)
                : source[startOffset..offset];
            lines.Add(new VbaSourceLine(line, lineText, startOffset, offset));
            blankLines.Add(containsOnlySpaces || lineSpan.Trim().IsEmpty);
            if (offset >= source.Length)
            {
                break;
            }

            if (source[offset] == '\r' && offset + 1 < source.Length && source[offset + 1] == '\n')
            {
                offset += 2;
            }
            else
            {
                offset++;
            }

            line++;
        }

        var indexedLines = lines.ToArray();
        var startPosition = new VbaSyntaxPosition(0, 0, 0);
        var lastLine = indexedLines[^1];
        var endPosition = new VbaSyntaxPosition(lastLine.LineNumber, lastLine.Text.Length, source.Length);
        return new VbaSourceText(
            source,
            indexedLines,
            blankLines.ToArray(),
            startPosition,
            new VbaSyntaxRange(startPosition, endPosition));
    }

    internal static VbaSourceText Update(
        string source,
        VbaSourceText previous)
    {
        if (source.Length != previous.Text.Length)
        {
            return From(source);
        }

        var firstDifference = 0;
        while (firstDifference < source.Length
            && source[firstDifference] == previous.Text[firstDifference])
        {
            firstDifference++;
        }

        if (firstDifference == source.Length)
        {
            return previous;
        }

        var lastDifference = source.Length - 1;
        while (lastDifference > firstDifference
            && source[lastDifference] == previous.Text[lastDifference])
        {
            lastDifference--;
        }

        var changedLineIndex = previous.PositionAt(firstDifference).Line;
        var changedLine = previous.indexedLines[changedLineIndex];
        if (lastDifference >= changedLine.EndOffset
            || source.AsSpan(
                    firstDifference,
                    lastDifference - firstDifference + 1)
                .IndexOfAny('\r', '\n') >= 0)
        {
            return From(source);
        }

        var updatedLines = (VbaSourceLine[])previous.indexedLines.Clone();
        var updatedBlankLines = (bool[])previous.blankLines.Clone();
        updatedLines[changedLineIndex] = new VbaSourceLine(
            changedLine.LineNumber,
            source[changedLine.StartOffset..changedLine.EndOffset],
            changedLine.StartOffset,
            changedLine.EndOffset);
        updatedBlankLines[changedLineIndex] = source
            .AsSpan(changedLine.StartOffset, changedLine.EndOffset - changedLine.StartOffset)
            .Trim()
            .IsEmpty;
        return new VbaSourceText(
            source,
            updatedLines,
            updatedBlankLines,
            previous.StartPosition,
            previous.FullRange);
    }

    /// <summary>
    /// Gets whether one indexed physical line contains only whitespace.
    /// </summary>
    internal bool IsBlankLine(int lineIndex)
        => blankLines[lineIndex];

    /// <summary>
    /// Converts an absolute character offset to a syntax position.
    /// </summary>
    /// <param name="offset">The zero-based character offset.</param>
    /// <returns>The corresponding line, character, and offset position.</returns>
    public VbaSyntaxPosition PositionAt(int offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, Text.Length);

        var low = 0;
        var high = indexedLines.Length - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (indexedLines[middle].StartOffset <= offset)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        var line = indexedLines[Math.Max(0, high)];
        var character = Math.Min(offset - line.StartOffset, line.Text.Length);
        return new VbaSyntaxPosition(line.LineNumber, character, offset);
    }

    /// <summary>
    /// Builds a syntax range for a span on a single source line.
    /// </summary>
    /// <param name="line">The source line that contains the span.</param>
    /// <param name="startCharacter">The zero-based start character on the line.</param>
    /// <param name="endCharacter">The zero-based end character on the line.</param>
    /// <returns>The range that covers the requested line span.</returns>
    public VbaSyntaxRange RangeForLine(VbaSourceLine line, int startCharacter, int endCharacter)
        => new(
            new VbaSyntaxPosition(line.LineNumber, startCharacter, line.StartOffset + startCharacter),
            new VbaSyntaxPosition(line.LineNumber, endCharacter, line.StartOffset + endCharacter));

    /// <summary>
    /// Splits source text into physical lines after normalizing CRLF and CR newlines to LF.
    /// </summary>
    /// <param name="source">The source text to split.</param>
    /// <returns>The physical source lines.</returns>
    public static string[] SplitLines(string source)
        => From(source).Lines.Select(line => line.Text).ToArray();

    /// <summary>
    /// Splits source text into physical lines while preserving each line's text without newline characters.
    /// </summary>
    /// <param name="source">The source text to split.</param>
    /// <returns>The source lines in order.</returns>
    public static IReadOnlyList<string> SplitLogicalLines(string source)
        => From(source).Lines.Select(line => line.Text).ToArray();

    /// <summary>
    /// Advances a source position by one source character, treating CRLF as one atomic newline.
    /// </summary>
    /// <param name="position">The current indexed source position.</param>
    /// <returns>The next indexed source position.</returns>
    internal VbaSyntaxPosition Advance(VbaSyntaxPosition position)
    {
        if (position.Offset >= Text.Length)
        {
            return FullRange.End;
        }

        var width = Text[position.Offset] == '\r'
            && position.Offset + 1 < Text.Length
            && Text[position.Offset + 1] == '\n'
                ? 2
                : 1;
        return PositionAt(position.Offset + width);
    }

    /// <summary>
    /// Removes the apostrophe comment portion of a line while respecting string literals.
    /// </summary>
    /// <param name="line">The source line to strip.</param>
    /// <returns>The code portion before the apostrophe comment, or the original line when no comment exists.</returns>
    public static string StripApostropheComment(string line)
    {
        var commentStart = FindApostropheCommentStart(line);
        return commentStart < 0 ? line : line[..commentStart];
    }

    /// <summary>
    /// Finds the start of an apostrophe comment outside string literals.
    /// </summary>
    /// <param name="line">The source line to inspect.</param>
    /// <returns>The comment start index, or -1 when no apostrophe comment starts on the line.</returns>
    public static int FindApostropheCommentStart(string line)
    {
        var inString = false;
        for (var index = 0; index < line.Length; index++)
        {
            var current = line[index];
            if (current == '"' && inString && index + 1 < line.Length && line[index + 1] == '"')
            {
                index++;
                continue;
            }

            if (current == '"')
            {
                inString = !inString;
                continue;
            }

            if (!inString && current == '\'')
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Determines whether a line ends with a VBA line-continuation marker.
    /// </summary>
    /// <param name="line">The source line to inspect.</param>
    /// <returns>True when the trimmed line ends with an underscore.</returns>
    public static bool HasLineContinuation(string line)
        => line.TrimEnd().EndsWith("_", StringComparison.Ordinal);

    /// <summary>
    /// Removes a trailing VBA line-continuation marker from one line.
    /// </summary>
    /// <param name="line">The source line to transform.</param>
    /// <returns>The line without the continuation marker, or the original line when none is present.</returns>
    public static string RemoveLineContinuation(string line)
    {
        var trimmed = line.TrimEnd();
        return trimmed.EndsWith("_", StringComparison.Ordinal)
            ? trimmed[..^1]
            : line;
    }

    /// <summary>
    /// Determines whether a character can start a VBA identifier.
    /// </summary>
    /// <param name="value">The character to inspect.</param>
    /// <returns>True for ASCII letters and underscore.</returns>
    public static bool IsIdentifierStart(char value)
        => char.IsAsciiLetter(value) || value == '_';

    /// <summary>
    /// Determines whether a character can continue a VBA identifier.
    /// </summary>
    /// <param name="value">The character to inspect.</param>
    /// <returns>True for ASCII letters, digits, and underscore.</returns>
    public static bool IsIdentifierCharacter(char value)
        => char.IsAsciiLetterOrDigit(value) || value == '_';

}
