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
    VbaSourceDefinition Definition,
    VbaDefinitionIdentity DefinitionIdentity);

/// <summary>
/// Finds resolved identifier occurrences for references, rename, semantic tokens, and formatting-like traversals.
/// </summary>
internal sealed class VbaResolvedIdentifierOccurrenceIndex
{
    private readonly IReadOnlyList<VbaSourceDocument> documents;
    private readonly VbaResolutionTable resolutionTable;

    public VbaResolvedIdentifierOccurrenceIndex(
        IReadOnlyList<VbaSourceDocument> documents,
        VbaResolutionTable resolutionTable)
    {
        this.documents = documents;
        this.resolutionTable = resolutionTable;
    }

    public IReadOnlyList<VbaResolvedIdentifierOccurrence> GetDocumentOccurrences(string uri)
    {
        var document = documents.FirstOrDefault(candidate => SameUri(candidate.Uri, uri));
        return document is null ? [] : GetDocumentOccurrences(document);
    }

    public IReadOnlyList<VbaResolvedIdentifierOccurrence> FindMatching(VbaSourceDefinition target)
    {
        var targetIdentity = resolutionTable.GetIdentity(target);
        return FindMatching(targetIdentity);
    }

    public IReadOnlyList<VbaResolvedIdentifierOccurrence> FindMatching(VbaDefinitionIdentity targetIdentity)
        => documents
            .SelectMany(GetDocumentOccurrences)
            .Where(occurrence => resolutionTable.SameIdentity(occurrence.DefinitionIdentity, targetIdentity))
            .GroupBy(
                occurrence => $"{occurrence.Uri}:{GetRangeKey(occurrence.Range)}:{GetRangeKey(occurrence.DefinitionIdentity.Range)}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(occurrence => occurrence.Uri, StringComparer.OrdinalIgnoreCase)
            .ThenBy(occurrence => occurrence.Range.Start.Line)
            .ThenBy(occurrence => occurrence.Range.Start.Character)
            .ToArray();

    private IReadOnlyList<VbaResolvedIdentifierOccurrence> GetDocumentOccurrences(VbaSourceDocument document)
    {
        var occurrences = new List<VbaResolvedIdentifierOccurrence>();
        var lines = VbaSourceText.SplitLines(document.Text);
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            foreach (var occurrence in VbaSourceText.FindIdentifierOccurrences(lines[lineIndex]))
            {
                var definition = resolutionTable.ResolveSourceDefinition(document.Uri, lineIndex, occurrence.Start);
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
                    definition,
                    resolutionTable.GetIdentity(definition)));
            }
        }

        return occurrences;
    }

    private static string GetRangeKey(VbaRange range)
        => $"{range.Start.Line}:{range.Start.Character}:{range.End.Line}:{range.End.Character}";

    private static bool SameUri(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

}
