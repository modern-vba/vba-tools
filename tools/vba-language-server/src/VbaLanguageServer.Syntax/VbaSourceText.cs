namespace VbaLanguageServer.Syntax;

public sealed record VbaIdentifierOccurrence(string Name, int Start, int End);

public static class VbaSourceText
{
    public static string[] SplitLines(string source)
        => source.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

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

    public static string StripApostropheComment(string line)
    {
        var commentStart = FindApostropheCommentStart(line);
        return commentStart < 0 ? line : line[..commentStart];
    }

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

    public static bool HasLineContinuation(string line)
        => line.TrimEnd().EndsWith("_", StringComparison.Ordinal);

    public static string RemoveLineContinuation(string line)
    {
        var trimmed = line.TrimEnd();
        return trimmed.EndsWith("_", StringComparison.Ordinal)
            ? trimmed[..^1]
            : line;
    }

    public static bool IsIdentifierStart(char value)
        => char.IsAsciiLetter(value) || value == '_';

    public static bool IsIdentifierCharacter(char value)
        => char.IsAsciiLetterOrDigit(value) || value == '_';
}
