using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.Syntax;

namespace VbaLanguageServer.SourceModel;

/// <summary>
/// Represents an identifier occurrence after semantic resolution has attached its definition.
/// </summary>
internal sealed record VbaResolvedIdentifierOccurrence(
    string Uri,
    VbaIdentifierOccurrence Occurrence,
    VbaRange Range,
    VbaSourceDefinition Definition);

/// <summary>
/// Finds resolved identifier occurrences for references, rename, semantic tokens, and formatting-like traversals.
/// </summary>
internal sealed class VbaResolvedIdentifierOccurrenceIndex
{
    private readonly IReadOnlyList<VbaSourceDocument> documents;
    private readonly Func<string, int, int, VbaSourceDefinition?> resolveSourceDefinition;

    public VbaResolvedIdentifierOccurrenceIndex(
        IReadOnlyList<VbaSourceDocument> documents,
        Func<string, int, int, VbaSourceDefinition?> resolveSourceDefinition)
    {
        this.documents = documents;
        this.resolveSourceDefinition = resolveSourceDefinition;
    }

    public IReadOnlyList<VbaResolvedIdentifierOccurrence> GetDocumentOccurrences(string uri)
    {
        var document = documents.FirstOrDefault(candidate => SameUri(candidate.Uri, uri));
        return document is null ? [] : GetDocumentOccurrences(document);
    }

    public IReadOnlyList<VbaResolvedIdentifierOccurrence> FindMatching(VbaSourceDefinition target)
        => documents
            .SelectMany(GetDocumentOccurrences)
            .Where(occurrence => SameDefinition(occurrence.Definition, target))
            .GroupBy(
                occurrence => $"{occurrence.Uri}:{GetRangeKey(occurrence.Range)}:{GetRangeKey(occurrence.Definition.Range)}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(occurrence => occurrence.Uri, StringComparer.OrdinalIgnoreCase)
            .ThenBy(occurrence => occurrence.Range.Start.Line)
            .ThenBy(occurrence => occurrence.Range.Start.Character)
            .ToArray();

    public static bool SameDefinition(VbaSourceDefinition left, VbaSourceDefinition right)
        => SameUri(left.Uri, right.Uri)
            && SameName(left.Name, right.Name)
            && ComparePosition(left.Range.Start, right.Range.Start) == 0
            && ComparePosition(left.Range.End, right.Range.End) == 0;

    private IReadOnlyList<VbaResolvedIdentifierOccurrence> GetDocumentOccurrences(VbaSourceDocument document)
    {
        var occurrences = new List<VbaResolvedIdentifierOccurrence>();
        var lines = VbaSourceText.SplitLines(document.Text);
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            foreach (var occurrence in VbaSourceText.FindIdentifierOccurrences(lines[lineIndex]))
            {
                var definition = resolveSourceDefinition(document.Uri, lineIndex, occurrence.Start);
                if (definition is null)
                {
                    continue;
                }

                occurrences.Add(new VbaResolvedIdentifierOccurrence(
                    document.Uri,
                    occurrence,
                    new VbaRange(
                        new VbaPosition(lineIndex, occurrence.Start),
                        new VbaPosition(lineIndex, occurrence.End)),
                    definition));
            }
        }

        return occurrences;
    }

    private static string GetRangeKey(VbaRange range)
        => $"{range.Start.Line}:{range.Start.Character}:{range.End.Line}:{range.End.Character}";

    private static bool SameUri(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool SameName(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static int ComparePosition(VbaPosition left, VbaPosition right)
    {
        var lineComparison = left.Line.CompareTo(right.Line);
        return lineComparison != 0 ? lineComparison : left.Character.CompareTo(right.Character);
    }
}
