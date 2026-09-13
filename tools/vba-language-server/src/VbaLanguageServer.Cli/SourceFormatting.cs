using System.Text.RegularExpressions;
using VbaLanguageServer.Diagnostics;
using VbaTools.Syntax;

namespace VbaLanguageServer.SourceModel;

/// <summary>
/// Chooses source formatting output conventions.
/// </summary>
internal static class SourceFormatting
{
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

}

/// <summary>
/// Formats VBA source text for casing and indentation while preserving semantics.
/// </summary>
internal sealed class VbaSourceFormatter
{
    private readonly VbaSemanticResolution semanticResolution;
    private readonly VbaResolvedIdentifierOccurrenceIndex resolvedOccurrences;

    /// <summary>
    /// Creates a source formatter.
    /// </summary>
    /// <param name="semanticResolution">The semantic resolver used for canonical casing decisions.</param>
    /// <param name="resolvedOccurrences">The snapshot-scoped resolved occurrence cache.</param>
    public VbaSourceFormatter(
        VbaSemanticResolution semanticResolution,
        VbaResolvedIdentifierOccurrenceIndex resolvedOccurrences)
    {
        this.semanticResolution = semanticResolution;
        this.resolvedOccurrences = resolvedOccurrences;
    }

    /// <summary>
    /// Formats a source document and returns a whole-document replacement edit when needed.
    /// </summary>
    /// <param name="document">The source document to format.</param>
    /// <param name="indentationStyle">The resolved editor indentation style.</param>
    /// <returns>The formatting edit, or null when no changes are required.</returns>
    public VbaTextEdit? FormatDocument(
        VbaSourceDocument document,
        VbaIndentationStyle indentationStyle,
        CancellationToken cancellationToken = default)
    {
        var result = FormatText(document, indentationStyle, cancellationToken);
        if (result.Replacements.Count == 0)
        {
            return null;
        }

        var range = result.Before.FullRange;
        return new VbaTextEdit(
            new VbaRange(
                new VbaPosition(range.Start.Line, range.Start.Character),
                new VbaPosition(range.End.Line, range.End.Character)),
            result.After.Text);
    }

    private VbaSourceTextEditResult FormatText(
        VbaSourceDocument document,
        VbaIndentationStyle indentationStyle,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var declarationRanges = document.Definitions
            .Select(definition => GetRangeKey(definition.Range))
            .ToHashSet(StringComparer.Ordinal);
        var syntaxTree = document.SyntaxTree ?? VbaSyntaxTree.ParseModule(document.Uri, document.Text);
        var sourceText = syntaxTree.SourceText;
        var formattingInput = VbaFormattingInput.FromSyntaxTree(syntaxTree);
        var indentationFormatting = VbaIndentationFormatting.FromInput(formattingInput);
        var canonicalNamesByRange = resolvedOccurrences.GetCanonicalNamesByRange(
            document.Uri,
            cancellationToken);
        var formattedLines = new List<string>(formattingInput.Lines.Count);

        foreach (var formattingLine in formattingInput.Lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = formattingLine.Text;
            if (formattingLine.IsClassMetadata)
            {
                formattedLines.Add(line);
                continue;
            }

            var casedLine = FormatLineCasing(
                line,
                document,
                formattingLine.LineNumber,
                declarationRanges,
                canonicalNamesByRange);
            formattedLines.Add(indentationFormatting.Apply(formattingLine, casedLine, indentationStyle));
        }

        var formattedText = string.Join(SourceFormatting.DetectDominantLineEnding(sourceText.Text), formattedLines);
        var edits = new List<VbaSourceTextEdit>();
        if (!string.Equals(formattedText, sourceText.Text, StringComparison.Ordinal))
        {
            edits.Add(new VbaSourceTextEdit(0, sourceText.Text.Length, formattedText));
        }

        return ApplyFormattingEdits(sourceText, edits);
    }

    private string FormatLineCasing(
        string line,
        VbaSourceDocument document,
        int lineIndex,
        IReadOnlySet<string> declarationRanges,
        IReadOnlyDictionary<VbaRange, string> canonicalNamesByRange)
    {
        var lineParts = VbaLexicalFacts.SplitCodeAndComment(line);
        var codePart = lineParts.CodePart;

        codePart = Regex.Replace(
            codePart,
            "^(?<leading>" + VbaIdentifier.RegexWhitespace + "*)"
                + "Attribute(?<separator>" + VbaIdentifier.RegexWhitespace + "+)"
                + "VB_Name(?=$|" + VbaIdentifier.RegexWhitespace + "|=)",
            match => match.Groups["leading"].Value
                + "Attribute"
                + match.Groups["separator"].Value
                + "VB_Name",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        var edits = new List<VbaSourceTextEdit>();
        foreach (var occurrence in VbaLexicalFacts.FindCodeIdentifierOccurrences(codePart))
        {
            var canonicalName = semanticResolution.GetCanonicalFormattingName(
                occurrence,
                document,
                lineIndex,
                declarationRanges,
                canonicalNamesByRange);
            if (canonicalName is not null
                && !string.Equals(occurrence.Name, canonicalName, StringComparison.Ordinal))
            {
                edits.Add(new VbaSourceTextEdit(occurrence.Start, occurrence.End, canonicalName));
            }
        }

        return ApplyFormattingEdits(VbaSourceText.From(codePart), edits).After.Text + lineParts.CommentPart;
    }

    private static VbaSourceTextEditResult ApplyFormattingEdits(
        VbaSourceText sourceText,
        IEnumerable<VbaSourceTextEdit> edits)
        => VbaSourceTextEditResult.TryApply(sourceText, edits, out var result)
            ? result
            : throw new InvalidOperationException("Source formatting produced invalid source edits.");

    private static string GetRangeKey(VbaRange range)
        => $"{range.Start.Line}:{range.Start.Character}:{range.End.Line}:{range.End.Character}";
}
