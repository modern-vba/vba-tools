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
    private readonly Func<string, int, int, VbaSourceDefinition?> resolveSourceDefinition;
    private readonly IReadOnlyList<VbaDocumentOccurrenceCache> documentOccurrenceCaches;
    private readonly ILookup<string, VbaDocumentOccurrenceCache> documentOccurrenceCachesByUri;
    private readonly VbaCancellationSafeMemo<
        IReadOnlyDictionary<VbaDefinitionIdentity, IReadOnlyList<VbaResolvedIdentifierOccurrence>>>
        occurrencesByIdentity;

    public VbaResolvedIdentifierOccurrenceIndex(
        IReadOnlyList<VbaSourceDocument> documents,
        Func<string, int, int, VbaSourceDefinition?> resolveSourceDefinition)
    {
        this.resolveSourceDefinition = resolveSourceDefinition;
        documentOccurrenceCaches = documents
            .Select(document => new VbaDocumentOccurrenceCache(
                document.Uri,
                document,
                new VbaCancellationSafeMemo<VbaResolvedDocumentOccurrenceSet>()))
            .ToArray();
        documentOccurrenceCachesByUri = documentOccurrenceCaches.ToLookup(
            cache => cache.Uri,
            StringComparer.OrdinalIgnoreCase);
        occurrencesByIdentity = new VbaCancellationSafeMemo<
            IReadOnlyDictionary<VbaDefinitionIdentity, IReadOnlyList<VbaResolvedIdentifierOccurrence>>>();
    }

    public IReadOnlyList<VbaResolvedIdentifierOccurrence> GetDocumentOccurrences(
        string uri,
        CancellationToken cancellationToken = default)
        => GetDocumentOccurrenceSet(uri, cancellationToken)?.Occurrences ?? [];

    public IReadOnlyDictionary<VbaRange, string> GetCanonicalNamesByRange(
        string uri,
        CancellationToken cancellationToken = default)
        => GetDocumentOccurrenceSet(uri, cancellationToken)?.CanonicalNamesByRange
            ?? EmptyCanonicalNamesByRange;

    public IReadOnlyList<VbaResolvedIdentifierOccurrence> FindMatching(
        VbaSourceDefinition target,
        CancellationToken cancellationToken = default)
        => FindMatching(target.Identity, cancellationToken);

    public IReadOnlyList<VbaResolvedIdentifierOccurrence> FindMatching(
        VbaDefinitionIdentity targetIdentity,
        CancellationToken cancellationToken = default)
        => occurrencesByIdentity.Get(
                BuildOccurrenceReverseMap,
                cancellationToken)
            .TryGetValue(targetIdentity, out var matchingOccurrences)
            ? matchingOccurrences
            : [];

    private VbaResolvedDocumentOccurrenceSet? GetDocumentOccurrenceSet(
        string uri,
        CancellationToken cancellationToken)
    {
        var cache = documentOccurrenceCachesByUri[uri].FirstOrDefault();
        return cache?.Occurrences.Get(
            token => ResolveDocumentOccurrences(cache.Document, token),
            cancellationToken);
    }

    private IReadOnlyDictionary<VbaDefinitionIdentity, IReadOnlyList<VbaResolvedIdentifierOccurrence>>
        BuildOccurrenceReverseMap(CancellationToken cancellationToken)
    {
        var occurrences = new List<VbaResolvedIdentifierOccurrence>();
        foreach (var cache in documentOccurrenceCaches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            occurrences.AddRange(cache.Occurrences.Get(
                token => ResolveDocumentOccurrences(cache.Document, token),
                cancellationToken).Occurrences);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return occurrences
            .Distinct(VbaResolvedIdentifierOccurrenceComparer.Instance)
            .GroupBy(occurrence => occurrence.Definition.Identity)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<VbaResolvedIdentifierOccurrence>)group
                    .OrderBy(occurrence => occurrence.Uri, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(occurrence => occurrence.Range.Start.Line)
                    .ThenBy(occurrence => occurrence.Range.Start.Character)
                    .ToArray());
    }

    private VbaResolvedDocumentOccurrenceSet ResolveDocumentOccurrences(
        VbaSourceDocument document,
        CancellationToken cancellationToken)
    {
        var occurrences = new List<VbaResolvedIdentifierOccurrence>();
        var syntaxTree = document.SyntaxTree ?? VbaSyntaxTree.ParseModule(document.Uri, document.Text);
        foreach (var token in syntaxTree.TokenStream.Tokens.Where(token => token.Kind == VbaTokenKind.Identifier))
        {
            cancellationToken.ThrowIfCancellationRequested();
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

        cancellationToken.ThrowIfCancellationRequested();
        var resolvedOccurrences = occurrences.ToArray();
        var canonicalNamesByRange = resolvedOccurrences
            .GroupBy(occurrence => occurrence.Range)
            .Where(group => group
                .Select(occurrence => occurrence.Definition.Name)
                .Distinct(StringComparer.Ordinal)
                .Count() == 1)
            .ToDictionary(group => group.Key, group => group.First().Definition.Name);
        return new VbaResolvedDocumentOccurrenceSet(resolvedOccurrences, canonicalNamesByRange);
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

    private static IReadOnlyDictionary<VbaRange, string> EmptyCanonicalNamesByRange { get; } =
        new Dictionary<VbaRange, string>();

    private sealed record VbaDocumentOccurrenceCache(
        string Uri,
        VbaSourceDocument Document,
        VbaCancellationSafeMemo<VbaResolvedDocumentOccurrenceSet> Occurrences);

    private sealed record VbaResolvedDocumentOccurrenceSet(
        IReadOnlyList<VbaResolvedIdentifierOccurrence> Occurrences,
        IReadOnlyDictionary<VbaRange, string> CanonicalNamesByRange);
}

internal sealed class VbaCancellationSafeMemo<T>
    where T : class
{
    private readonly SemaphoreSlim buildGate = new(1, 1);
    private T? value;

    public T Get(
        Func<CancellationToken, T> create,
        CancellationToken cancellationToken)
    {
        var cached = Volatile.Read(ref value);
        if (cached is not null)
        {
            return cached;
        }

        buildGate.Wait(cancellationToken);
        try
        {
            cached = value;
            if (cached is not null)
            {
                return cached;
            }

            var created = create(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Write(ref value, created);
            return created;
        }
        finally
        {
            buildGate.Release();
        }
    }
}
