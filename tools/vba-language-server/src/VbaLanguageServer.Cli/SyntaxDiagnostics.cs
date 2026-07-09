namespace VbaLanguageServer.Diagnostics;

public sealed record VbaPosition(int Line, int Character);

public sealed record VbaRange(VbaPosition Start, VbaPosition End);

public sealed record VbaSyntaxDiagnostic(
    string Code,
    string Message,
    VbaRange Range,
    string Severity = "error",
    string Source = "vba-language-server");

public static class VbaSyntaxDiagnostics
{
    public static IReadOnlyList<VbaSyntaxDiagnostic> Collect(string source, string fileName)
    {
        var lines = SplitLines(source);
        var startLine = GetCodeStartLine(lines, fileName);
        var diagnostics = new List<VbaSyntaxDiagnostic>();

        for (var index = startLine; index < lines.Length; index++)
        {
            diagnostics.AddRange(CollectLineContinuationDiagnostics(lines[index], index));
            diagnostics.AddRange(CollectStringDiagnostics(lines[index], index));
        }

        return diagnostics;
    }

    private static string[] SplitLines(string source)
        => source.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

    private static int GetCodeStartLine(string[] lines, string fileName)
    {
        if (!fileName.EndsWith(".frm", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var attributeIndex = Array.FindIndex(lines, line =>
            line.TrimStart().StartsWith("Attribute VB_Name", StringComparison.OrdinalIgnoreCase));
        return attributeIndex < 0 ? 0 : attributeIndex;
    }

    private static IEnumerable<VbaSyntaxDiagnostic> CollectLineContinuationDiagnostics(string line, int lineIndex)
    {
        var commentStart = FindApostropheCommentStart(line);
        if (commentStart < 0)
        {
            yield break;
        }

        var codePart = line[..commentStart];
        var underscoreIndex = codePart.LastIndexOf('_');
        if (underscoreIndex >= 0 && codePart.TrimEnd().EndsWith('_'))
        {
            yield return new VbaSyntaxDiagnostic(
                "syntax.invalidTrailingCommentContinuation",
                "Code line-continuation marker cannot be followed by a comment.",
                new VbaRange(
                    new VbaPosition(lineIndex, underscoreIndex),
                    new VbaPosition(lineIndex, line.Length)));
        }
    }

    private static IEnumerable<VbaSyntaxDiagnostic> CollectStringDiagnostics(string line, int lineIndex)
    {
        if (IsRemCommentLine(line))
        {
            yield break;
        }

        var inString = false;
        var stringStart = -1;
        for (var index = 0; index < line.Length; index++)
        {
            var current = line[index];
            if (!inString && current == '\'')
            {
                break;
            }

            if (current != '"')
            {
                continue;
            }

            if (inString && index + 1 < line.Length && line[index + 1] == '"')
            {
                index++;
                continue;
            }

            inString = !inString;
            if (inString)
            {
                stringStart = index;
            }
        }

        if (inString)
        {
            yield return new VbaSyntaxDiagnostic(
                "syntax.unterminatedStringLiteral",
                "String literal is missing a closing double quote.",
                new VbaRange(
                    new VbaPosition(lineIndex, stringStart),
                    new VbaPosition(lineIndex, line.Length)));
        }
    }

    private static int FindApostropheCommentStart(string line)
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

    private static bool IsRemCommentLine(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.Equals("Rem", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Rem ", StringComparison.OrdinalIgnoreCase);
    }
}
