namespace VbaLanguageServer.SourceModel;

internal sealed record SourceFormattingEdit(int StartOffset, int EndOffset, string NewText);

internal sealed class SourceFormattingEditCollector
{
    private readonly List<SourceFormattingEdit> edits = [];

    public IReadOnlyList<SourceFormattingEdit> Edits => edits;

    public void Replace(int startOffset, int endOffset, string newText)
        => edits.Add(new SourceFormattingEdit(startOffset, endOffset, newText));

    public string Apply(string source)
        => SourceFormatting.ApplyEdits(source, edits);
}

internal static class SourceFormatting
{
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

    public static string DetectDominantLineEnding(string source)
    {
        var crlfCount = 0;
        var lfCount = 0;
        var crCount = 0;
        for (var index = 0; index < source.Length; index++)
        {
            if (source[index] == '\r')
            {
                if (index + 1 < source.Length && source[index + 1] == '\n')
                {
                    crlfCount++;
                    index++;
                }
                else
                {
                    crCount++;
                }

                continue;
            }

            if (source[index] == '\n')
            {
                lfCount++;
            }
        }

        if (crlfCount >= lfCount && crlfCount >= crCount)
        {
            return "\r\n";
        }

        return lfCount >= crCount ? "\n" : "\r";
    }

    public static string ApplyEdits(string source, IEnumerable<SourceFormattingEdit> edits)
    {
        var orderedEdits = edits
            .OrderBy(edit => edit.StartOffset)
            .ToArray();
        ValidateEdits(source, orderedEdits);

        var formatted = source;
        foreach (var edit in orderedEdits.Reverse())
        {
            formatted = formatted[..edit.StartOffset] + edit.NewText + formatted[edit.EndOffset..];
        }

        return formatted;
    }

    private static void ValidateEdits(string source, IReadOnlyList<SourceFormattingEdit> edits)
    {
        var previousEnd = 0;
        foreach (var edit in edits)
        {
            if (edit.StartOffset < 0 || edit.EndOffset < edit.StartOffset || edit.EndOffset > source.Length)
            {
                throw new InvalidOperationException("Source formatting edit range is outside the source text.");
            }

            if (edit.StartOffset < previousEnd)
            {
                throw new InvalidOperationException("Source formatting edits must not overlap.");
            }

            previousEnd = edit.EndOffset;
        }
    }
}
