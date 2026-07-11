namespace VbaLanguageServer.Syntax;

/// <summary>
/// Represents an identifier occurrence found on one physical source line.
/// </summary>
/// <param name="Name">The identifier text.</param>
/// <param name="Start">The inclusive character start on the line.</param>
/// <param name="End">The exclusive character end on the line.</param>
public sealed record VbaIdentifierOccurrence(string Name, int Start, int End);

/// <summary>
/// Provides line, identifier, comment, and continuation helpers for VBA source text.
/// </summary>
public static class VbaSourceText
{
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
}
