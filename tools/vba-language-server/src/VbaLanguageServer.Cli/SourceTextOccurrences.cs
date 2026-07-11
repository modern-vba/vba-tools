namespace VbaLanguageServer.SourceModel;

internal sealed record IdentifierAtPosition(string Name, int Start, int End);

internal static class SourceTextOccurrences
{
    public static string[] SplitLines(string source)
        => source.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

    public static IEnumerable<IdentifierAtPosition> FindIdentifierOccurrences(string line)
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

            yield return new IdentifierAtPosition(line[start..index], start, index);
            index--;
        }
    }

    public static IdentifierAtPosition? GetIdentifierAt(string line, int character)
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

        return new IdentifierAtPosition(line[start..end], start, end);
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

    public static bool IsIdentifierStart(char value)
        => char.IsAsciiLetter(value) || value == '_';

    public static bool IsIdentifierCharacter(char value)
        => char.IsAsciiLetterOrDigit(value) || value == '_';
}
