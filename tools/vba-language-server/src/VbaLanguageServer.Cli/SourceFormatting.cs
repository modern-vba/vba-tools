using System.Text.RegularExpressions;
using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.Syntax;

namespace VbaLanguageServer.SourceModel;

/// <summary>
/// Represents a source replacement used while constructing a formatted document.
/// </summary>
/// <param name="StartOffset">The inclusive source offset where replacement starts.</param>
/// <param name="EndOffset">The exclusive source offset where replacement ends.</param>
/// <param name="NewText">The replacement text.</param>
internal sealed record SourceFormattingEdit(int StartOffset, int EndOffset, string NewText);

/// <summary>
/// Collects non-overlapping source formatting edits and applies them to source text.
/// </summary>
internal sealed class SourceFormattingEditCollector
{
    private readonly List<SourceFormattingEdit> edits = [];

    /// <summary>
    /// Gets the collected formatting edits.
    /// </summary>
    public IReadOnlyList<SourceFormattingEdit> Edits => edits;

    /// <summary>
    /// Adds a replacement edit.
    /// </summary>
    /// <param name="startOffset">The inclusive source offset where replacement starts.</param>
    /// <param name="endOffset">The exclusive source offset where replacement ends.</param>
    /// <param name="newText">The replacement text.</param>
    public void Replace(int startOffset, int endOffset, string newText)
        => edits.Add(new SourceFormattingEdit(startOffset, endOffset, newText));

    /// <summary>
    /// Applies the collected edits to source text.
    /// </summary>
    /// <param name="source">The source text to edit.</param>
    /// <returns>The edited source text.</returns>
    public string Apply(string source)
        => SourceFormatting.ApplyEdits(source, edits);
}

/// <summary>
/// Provides low-level source formatting text helpers.
/// </summary>
internal static class SourceFormatting
{
    /// <summary>
    /// Splits source text into physical lines without newline characters.
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
    /// Detects the dominant line ending used by source text.
    /// </summary>
    /// <param name="source">The source text to inspect.</param>
    /// <returns>The line ending to preserve during formatting.</returns>
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

    /// <summary>
    /// Applies non-overlapping replacement edits to source text.
    /// </summary>
    /// <param name="source">The source text to edit.</param>
    /// <param name="edits">The replacement edits to apply.</param>
    /// <returns>The edited source text.</returns>
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

/// <summary>
/// Formats VBA source text for casing and indentation while preserving semantics.
/// </summary>
internal sealed class VbaSourceFormatter
{
    private readonly VbaSemanticResolution semanticResolution;

    /// <summary>
    /// Creates a source formatter.
    /// </summary>
    /// <param name="semanticResolution">The semantic resolver used for canonical casing decisions.</param>
    public VbaSourceFormatter(VbaSemanticResolution semanticResolution)
    {
        this.semanticResolution = semanticResolution;
    }

    /// <summary>
    /// Formats a source document and returns a whole-document replacement edit when needed.
    /// </summary>
    /// <param name="document">The source document to format.</param>
    /// <param name="tabSize">The number of spaces per indentation level.</param>
    /// <returns>The formatting edit, or null when no changes are required.</returns>
    public VbaTextEdit? FormatDocument(VbaSourceDocument document, int tabSize)
    {
        var formattedText = FormatText(document, Math.Max(1, tabSize));
        if (string.Equals(formattedText, document.Text, StringComparison.Ordinal))
        {
            return null;
        }

        var lines = VbaSourceText.SplitLines(document.Text);
        return new VbaTextEdit(
            new VbaRange(
                new VbaPosition(0, 0),
                new VbaPosition(Math.Max(0, lines.Length - 1), lines.Length == 0 ? 0 : lines[^1].Length)),
            formattedText);
    }

    private string FormatText(VbaSourceDocument document, int tabSize)
    {
        var declarationRanges = document.Definitions
            .Select(definition => GetRangeKey(definition.Range))
            .ToHashSet(StringComparer.Ordinal);
        var syntaxTree = document.SyntaxTree ?? VbaSyntaxTree.ParseModule(document.Uri, document.Text);
        var formDesignerRange = syntaxTree.Module.FormDesignerBlock?.Range;
        var lines = SourceFormatting.SplitLogicalLines(document.Text);
        var indentationDepths = CreateIndentationDepths(lines, formDesignerRange);
        var formattedLines = new List<string>(lines.Count);

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            if (IsLineInRange(formDesignerRange, lineIndex))
            {
                formattedLines.Add(line);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                formattedLines.Add("");
                continue;
            }

            var casedLine = FormatLineCasing(line, document, lineIndex, declarationRanges);
            if (indentationDepths is null)
            {
                formattedLines.Add(casedLine);
                continue;
            }

            var trimmed = casedLine.TrimStart();
            formattedLines.Add($"{new string(' ', tabSize * indentationDepths[lineIndex])}{trimmed}");
        }

        var formattedText = string.Join(SourceFormatting.DetectDominantLineEnding(document.Text), formattedLines);
        var edits = new SourceFormattingEditCollector();
        if (!string.Equals(formattedText, document.Text, StringComparison.Ordinal))
        {
            edits.Replace(0, document.Text.Length, formattedText);
        }

        return edits.Apply(document.Text);
    }

    private string FormatLineCasing(
        string line,
        VbaSourceDocument document,
        int lineIndex,
        IReadOnlySet<string> declarationRanges)
    {
        var commentStart = VbaSourceText.FindApostropheCommentStart(line);
        var codePart = commentStart < 0 ? line : line[..commentStart];
        var commentPart = commentStart < 0 ? "" : line[commentStart..];

        codePart = Regex.Replace(
            codePart,
            "^\\s*Attribute\\s+VB_Name",
            match => match.Value[..^"Attribute VB_Name".Length] + "Attribute VB_Name",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        var edits = new SourceFormattingEditCollector();
        foreach (var occurrence in VbaSourceText.FindIdentifierOccurrences(codePart))
        {
            var canonicalName = semanticResolution.GetCanonicalFormattingName(
                codePart,
                occurrence,
                document,
                lineIndex,
                declarationRanges);
            if (canonicalName is not null
                && !string.Equals(occurrence.Name, canonicalName, StringComparison.Ordinal))
            {
                edits.Replace(occurrence.Start, occurrence.End, canonicalName);
            }
        }

        return edits.Apply(codePart) + commentPart;
    }

    private static int[]? CreateIndentationDepths(
        IReadOnlyList<string> lines,
        VbaSyntaxRange? formDesignerRange)
    {
        var depths = new int[lines.Count];
        var blockStack = new Stack<string>();
        var inContinuation = false;
        var continuationDepth = 0;

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            if (IsLineInRange(formDesignerRange, lineIndex))
            {
                continue;
            }

            var codePart = VbaSourceText.StripApostropheComment(lines[lineIndex]);
            var trimmed = codePart.TrimStart();
            if (string.IsNullOrWhiteSpace(trimmed)
                || IsFormattingIgnoredCodeLine(trimmed))
            {
                depths[lineIndex] = inContinuation ? continuationDepth : blockStack.Count;
                inContinuation = VbaSourceText.HasLineContinuation(codePart);
                continue;
            }

            if (inContinuation)
            {
                depths[lineIndex] = continuationDepth;
                inContinuation = VbaSourceText.HasLineContinuation(codePart);
                continue;
            }

            var closeTerminator = GetIndentationCloseTerminator(trimmed);
            var branchTerminator = GetIndentationBranchTerminator(trimmed);
            if (closeTerminator is not null)
            {
                if (blockStack.Count == 0
                    || !blockStack.Peek().Equals(closeTerminator, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                blockStack.Pop();
                depths[lineIndex] = blockStack.Count;
            }
            else if (branchTerminator is not null)
            {
                if (blockStack.Count == 0
                    || !blockStack.Peek().Equals(branchTerminator, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                depths[lineIndex] = Math.Max(0, blockStack.Count - 1);
            }
            else
            {
                depths[lineIndex] = blockStack.Count;
            }

            var openTerminator = GetIndentationOpenTerminator(trimmed);
            if (openTerminator is not null)
            {
                blockStack.Push(openTerminator);
            }

            if (VbaSourceText.HasLineContinuation(codePart))
            {
                inContinuation = true;
                continuationDepth = depths[lineIndex] + 1;
            }
        }

        return blockStack.Count == 0 && !inContinuation ? depths : null;
    }

    private static bool IsLineInRange(VbaSyntaxRange? range, int line)
        => range is not null
            && line >= range.Start.Line
            && line <= range.End.Line
            && (line != range.End.Line || range.End.Character > 0);

    private static bool IsFormattingIgnoredCodeLine(string trimmedLine)
        => Regex.IsMatch(trimmedLine, "^Attribute\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || Regex.IsMatch(trimmedLine, "^Option\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || trimmedLine.StartsWith("#", StringComparison.Ordinal);

    private static string? GetIndentationOpenTerminator(string trimmedLine)
    {
        if (Regex.IsMatch(
            trimmedLine,
            "^((Public|Private|Friend)\\s+)?(Static\\s+)?Sub\\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "End Sub";
        }

        if (Regex.IsMatch(
            trimmedLine,
            "^((Public|Private|Friend)\\s+)?(Static\\s+)?Function\\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "End Function";
        }

        if (Regex.IsMatch(
            trimmedLine,
            "^((Public|Private|Friend)\\s+)?(Static\\s+)?Property\\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "End Property";
        }

        if (Regex.IsMatch(trimmedLine, "^If\\b.*\\bThen\\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "End If";
        }

        if (Regex.IsMatch(trimmedLine, "^Select\\s+Case\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "End Select";
        }

        if (Regex.IsMatch(trimmedLine, "^With\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "End With";
        }

        if (Regex.IsMatch(trimmedLine, "^For\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            && !Regex.IsMatch(trimmedLine, ":\\s*Next\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "Next";
        }

        if (Regex.IsMatch(trimmedLine, "^Do\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            && !Regex.IsMatch(trimmedLine, ":\\s*Loop\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "Loop";
        }

        if (Regex.IsMatch(trimmedLine, "^While\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "Wend";
        }

        if (Regex.IsMatch(trimmedLine, "^((Public|Private|Friend)\\s+)?Enum\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "End Enum";
        }

        if (Regex.IsMatch(trimmedLine, "^((Public|Private|Friend)\\s+)?Type\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "End Type";
        }

        return null;
    }

    private static string? GetIndentationCloseTerminator(string trimmedLine)
    {
        if (Regex.IsMatch(trimmedLine, "^End\\s+Sub\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "End Sub";
        }

        if (Regex.IsMatch(trimmedLine, "^End\\s+Function\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "End Function";
        }

        if (Regex.IsMatch(trimmedLine, "^End\\s+Property\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "End Property";
        }

        if (Regex.IsMatch(trimmedLine, "^End\\s+If\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "End If";
        }

        if (Regex.IsMatch(trimmedLine, "^End\\s+Select\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "End Select";
        }

        if (Regex.IsMatch(trimmedLine, "^End\\s+With\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "End With";
        }

        if (Regex.IsMatch(trimmedLine, "^Next\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "Next";
        }

        if (Regex.IsMatch(trimmedLine, "^Loop\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "Loop";
        }

        if (Regex.IsMatch(trimmedLine, "^Wend\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "Wend";
        }

        if (Regex.IsMatch(trimmedLine, "^End\\s+Enum\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "End Enum";
        }

        if (Regex.IsMatch(trimmedLine, "^End\\s+Type\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "End Type";
        }

        return null;
    }

    private static string? GetIndentationBranchTerminator(string trimmedLine)
    {
        if (Regex.IsMatch(trimmedLine, "^(Else|ElseIf)\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "End If";
        }

        if (Regex.IsMatch(trimmedLine, "^Case\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "End Select";
        }

        return null;
    }

    private static string GetRangeKey(VbaRange range)
        => $"{range.Start.Line}:{range.Start.Character}:{range.End.Line}:{range.End.Character}";
}
