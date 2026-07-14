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
        => FindMatching(target.Identity);

    public IReadOnlyList<VbaResolvedIdentifierOccurrence> FindMatching(VbaDefinitionIdentity targetIdentity)
        => documents
            .SelectMany(GetDocumentOccurrences)
            .Where(occurrence => occurrence.Definition.Identity == targetIdentity)
            .Distinct(VbaResolvedIdentifierOccurrenceComparer.Instance)
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
                if (VbaLanguageVocabulary.IsKeyword(occurrence.Name))
                {
                    continue;
                }

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

    private static bool SameUri(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private sealed class VbaResolvedIdentifierOccurrenceComparer : IEqualityComparer<VbaResolvedIdentifierOccurrence>
    {
        public static VbaResolvedIdentifierOccurrenceComparer Instance { get; } = new();

        public bool Equals(VbaResolvedIdentifierOccurrence? left, VbaResolvedIdentifierOccurrence? right)
            => ReferenceEquals(left, right)
                || (left is not null
                    && right is not null
                    && SameUri(left.Uri, right.Uri)
                    && left.Range == right.Range
                    && left.Definition.Identity == right.Definition.Identity);

        public int GetHashCode(VbaResolvedIdentifierOccurrence occurrence)
        {
            var hash = new HashCode();
            hash.Add(occurrence.Uri, StringComparer.OrdinalIgnoreCase);
            hash.Add(occurrence.Range);
            hash.Add(occurrence.Definition.Identity);
            return hash.ToHashCode();
        }
    }
}
