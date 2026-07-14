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
    private VbaSourceText(
        string text,
        IReadOnlyList<VbaSourceLine> lines,
        VbaSyntaxPosition startPosition,
        VbaSyntaxRange fullRange)
    {
        Text = text;
        Lines = lines;
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
        var lines = new List<VbaSourceLine>();
        var line = 0;
        var offset = 0;
        while (offset <= source.Length)
        {
            var startOffset = offset;
            while (offset < source.Length && source[offset] is not '\r' and not '\n')
            {
                offset++;
            }

            lines.Add(new VbaSourceLine(line, source[startOffset..offset], startOffset, offset));
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

        var startPosition = new VbaSyntaxPosition(0, 0, 0);
        var endPosition = PositionAt(source, source.Length);
        return new VbaSourceText(source, lines, startPosition, new VbaSyntaxRange(startPosition, endPosition));
    }

    /// <summary>
    /// Converts an absolute character offset to a syntax position.
    /// </summary>
    /// <param name="offset">The zero-based character offset.</param>
    /// <returns>The corresponding line, character, and offset position.</returns>
    public VbaSyntaxPosition PositionAt(int offset)
        => PositionAt(Text, offset);

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
        => source.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

    /// <summary>
    /// Splits source text into physical lines while preserving each line's text without newline characters.
    /// </summary>
    /// <param name="source">The source text to split.</param>
    /// <returns>The source lines in order.</returns>
    public static IReadOnlyList<string> SplitLogicalLines(string source)
    {
        var lines = new List<string>();
        var lineStart = 0;
        for (var index = 0; index < source.Length; index++)
        {
            if (source[index] == '\r')
            {
                lines.Add(source[lineStart..index]);
                if (index + 1 < source.Length && source[index + 1] == '\n')
                {
                    index++;
                }

                lineStart = index + 1;
                continue;
            }

            if (source[index] == '\n')
            {
                lines.Add(source[lineStart..index]);
                lineStart = index + 1;
            }
        }

        lines.Add(source[lineStart..]);
        return lines;
    }

    /// <summary>
    /// Finds identifier occurrences on a line while skipping string literals and apostrophe comments.
    /// </summary>
    /// <param name="line">The source line to scan.</param>
    /// <returns>The identifier occurrences in source order.</returns>
    public static IEnumerable<VbaIdentifierOccurrence> FindIdentifierOccurrences(string line)
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
                yield break;
            }

            if (inString || !IsIdentifierStart(current))
            {
                continue;
            }

            var start = index;
            index++;
            while (index < line.Length && IsIdentifierCharacter(line[index]))
            {
                index++;
            }

            yield return new VbaIdentifierOccurrence(line[start..index], start, index);
            index--;
        }
    }

    /// <summary>
    /// Finds the identifier that contains or immediately precedes a character position.
    /// </summary>
    /// <param name="line">The source line to inspect.</param>
    /// <param name="character">The zero-based character position.</param>
    /// <returns>The identifier occurrence, or null when the position is not on an identifier.</returns>
    public static VbaIdentifierOccurrence? GetIdentifierAt(string line, int character)
    {
        if (line.Length == 0)
        {
            return null;
        }

        var clamped = Math.Clamp(character, 0, line.Length - 1);
        if (!IsIdentifierCharacter(line[clamped]) && clamped > 0 && IsIdentifierCharacter(line[clamped - 1]))
        {
            clamped--;
        }

        if (!IsIdentifierCharacter(line[clamped]))
        {
            return null;
        }

        var start = clamped;
        while (start > 0 && IsIdentifierCharacter(line[start - 1]))
        {
            start--;
        }

        var end = clamped + 1;
        while (end < line.Length && IsIdentifierCharacter(line[end]))
        {
            end++;
        }

        return new VbaIdentifierOccurrence(line[start..end], start, end);
    }

    /// <summary>
    /// Determines whether a character position is in code rather than inside a string or apostrophe comment.
    /// </summary>
    /// <param name="line">The source line to inspect.</param>
    /// <param name="character">The zero-based character position.</param>
    /// <returns>True when the position is in code.</returns>
    public static bool IsCodePosition(string line, int character)
    {
        var inString = false;
        var clamped = Math.Clamp(character, 0, Math.Max(0, line.Length - 1));
        for (var index = 0; index <= clamped && index < line.Length; index++)
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
                if (index == clamped)
                {
                    return false;
                }

                continue;
            }

            if (!inString && current == '\'')
            {
                return false;
            }
        }

        return !inString;
    }

    /// <summary>
    /// Gets the logical statement prefix ending at a position across continued physical lines.
    /// </summary>
    /// <param name="lines">The physical source lines.</param>
    /// <param name="line">The zero-based line containing the position.</param>
    /// <param name="character">The zero-based character position.</param>
    /// <returns>The logical prefix text with continuation markers removed.</returns>
    public static string GetLogicalPrefix(string[] lines, int line, int character)
    {
        var startLine = line;
        while (startLine > 0 && HasLineContinuation(lines[startLine - 1]))
        {
            startLine--;
        }

        var parts = new List<string>();
        for (var lineIndex = startLine; lineIndex <= line; lineIndex++)
        {
            var text = lineIndex == line
                ? lines[lineIndex][..Math.Clamp(character, 0, lines[lineIndex].Length)]
                : lines[lineIndex];
            parts.Add(RemoveLineContinuation(text));
        }

        return string.Join(' ', parts);
    }

    /// <summary>
    /// Gets a full logical statement starting at a physical line.
    /// </summary>
    /// <param name="lines">The physical source lines.</param>
    /// <param name="startLine">The zero-based line where the logical statement starts.</param>
    /// <param name="endLine">The zero-based line where the logical statement ends.</param>
    /// <returns>The logical statement text with continuation markers removed.</returns>
    public static string GetLogicalStatement(string[] lines, int startLine, out int endLine)
    {
        var parts = new List<string>();
        endLine = startLine;
        while (endLine < lines.Length)
        {
            parts.Add(RemoveLineContinuation(lines[endLine]));
            if (!HasLineContinuation(lines[endLine]))
            {
                break;
            }

            endLine++;
        }

        return string.Join(' ', parts);
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

    private static VbaSyntaxPosition PositionAt(string source, int offset)
    {
        var line = 0;
        var character = 0;
        for (var index = 0; index < offset; index++)
        {
            if (source[index] == '\r')
            {
                if (index + 1 < source.Length && source[index + 1] == '\n')
                {
                    index++;
                }

                line++;
                character = 0;
                continue;
            }

            if (source[index] == '\n')
            {
                line++;
                character = 0;
                continue;
            }

            character++;
        }

        return new VbaSyntaxPosition(line, character, offset);
    }
}
