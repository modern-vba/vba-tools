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
        var syntaxTree = document.SyntaxTree ?? VbaSyntaxTree.ParseModule(document.Uri, document.Text);
        foreach (var token in syntaxTree.TokenStream.Tokens.Where(token => token.Kind == VbaTokenKind.Identifier))
        {
            var positionSyntax = syntaxTree.GetPositionSyntax(
                token.Range.Start.Line,
                token.Range.Start.Character);
            if (positionSyntax.Region != VbaPositionRegion.Code)
            {
                continue;
            }

            var definition = resolveSourceDefinition(
                document.Uri,
                token.Range.Start.Line,
                token.Range.Start.Character);
            if (definition is null)
            {
                continue;
            }

            var occurrence = new VbaIdentifierOccurrence(
                token.Text,
                token.Range.Start.Character,
                token.Range.End.Character);
            occurrences.Add(new VbaResolvedIdentifierOccurrence(
                document.Uri,
                occurrence,
                new VbaRange(
                    new VbaPosition(token.Range.Start.Line, token.Range.Start.Character),
                    new VbaPosition(token.Range.End.Line, token.Range.End.Character)),
                definition));
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
