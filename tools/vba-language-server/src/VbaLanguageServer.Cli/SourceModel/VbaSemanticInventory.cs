using System.Text.RegularExpressions;
using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.Syntax;

namespace VbaLanguageServer.SourceModel;

/// <summary>
/// Owns project-scope semantic lookup structures shaped around editor query patterns.
/// </summary>
public sealed class VbaSemanticInventory
{
    private readonly IReadOnlyList<VbaSourceDocument> sourceDocuments;
    private readonly VbaNameCandidateInventory definitionCandidates;
    private readonly VbaResolutionPolicy resolutionPolicy = new();
    private readonly VbaSemanticResolution semanticResolution;
    private readonly VbaResolvedIdentifierOccurrenceIndex resolvedOccurrences;
    private readonly VbaSourceFormatter sourceFormatter;
    private readonly object semanticTokenCacheGate = new();
    private readonly Dictionary<string, IReadOnlyList<VbaSemanticToken>> semanticTokenCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<int>> semanticTokenDataCache =
        new(StringComparer.OrdinalIgnoreCase);

    private VbaSemanticInventory(
        IReadOnlyList<VbaSourceDocument> sourceDocuments,
        VbaNameCandidateInventory definitionCandidates)
    {
        this.sourceDocuments = sourceDocuments;
        this.definitionCandidates = definitionCandidates;
        semanticResolution = new VbaSemanticResolution(
            definitionCandidates,
            resolutionPolicy);
        resolvedOccurrences = new VbaResolvedIdentifierOccurrenceIndex(
            sourceDocuments,
            semanticResolution.ResolveSourceDefinition);
        sourceFormatter = new VbaSourceFormatter(
            semanticResolution,
            resolvedOccurrences);
    }

    /// <summary>
    /// Creates a semantic inventory from projected source documents and active reference metadata.
    /// </summary>
    public static VbaSemanticInventory Create(
        IReadOnlyDictionary<string, VbaSourceDocument> sourceDocuments,
        VbaProjectReferenceSelection? referenceSelection = null,
        VbaProjectReferenceCatalogSet? referenceCatalogs = null)
    {
        var documents = FreezeList(
            sourceDocuments.Values.Select(CaptureDocument));
        var capturedReferenceSelection = CaptureReferenceSelection(referenceSelection);
        var catalogs = referenceCatalogs ?? VbaProjectReferenceCatalogSet.Empty;
        var activeReferenceDefinitions = FreezeList(
            catalogs
                .GetActiveDefinitions(capturedReferenceSelection)
                .Select(CaptureDefinition));
        var definitionCandidates = new VbaNameCandidateInventory(
            documents,
            capturedReferenceSelection,
            catalogs,
            activeReferenceDefinitions);
        return new VbaSemanticInventory(
            documents,
            definitionCandidates);
    }

    /// <summary>
    /// Gets definitions declared in a document.
    /// </summary>
    public IReadOnlyList<VbaSourceDefinition> GetDocumentDefinitions(string uri)
        => definitionCandidates.GetDocumentDefinitions(uri);

    /// <summary>
    /// Searches workspace symbols across indexed source documents.
    /// </summary>
    public IReadOnlyList<VbaWorkspaceSymbol> GetWorkspaceSymbols(string query)
    {
        var normalizedQuery = query ?? "";
        return definitionCandidates.GetWorkspaceSymbolDefinitions()
            .Where(definition => string.IsNullOrWhiteSpace(normalizedQuery)
                || definition.Name.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            .Select(definition => new VbaWorkspaceSymbol(
                definition.Name,
                definition.Kind,
                definition.Uri,
                definition.Range))
            .OrderBy(symbol => symbol.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(symbol => symbol.Uri, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public VbaCompletionResult GetCompletionResult(string uri, int line, int character)
        => semanticResolution.GetCompletionResult(uri, line, character);

    public VbaDefinitionLocation? ResolveDefinition(string uri, int line, int character)
    {
        var definition = ResolveSourceDefinition(uri, line, character);
        return definition is null
            || definition.Identity.Origin == VbaDefinitionOrigin.ProjectReference
                ? null
                : definition.Location;
    }

    public IReadOnlyList<VbaDefinitionLocation> FindReferences(
        string uri,
        int line,
        int character,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = ResolveSourceDefinition(uri, line, character);
        if (target is null)
        {
            return [];
        }

        var references = resolvedOccurrences.FindMatching(target, cancellationToken)
            .Select(occurrence => new VbaDefinitionLocation(occurrence.Uri, occurrence.Range))
            .GroupBy(reference => $"{reference.Uri}:{GetRangeKey(reference.Range)}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(reference => reference.Uri, StringComparer.OrdinalIgnoreCase)
            .ThenBy(reference => reference.Range.Start.Line)
            .ThenBy(reference => reference.Range.Start.Character)
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return references;
    }

    public VbaSourceDefinition? ResolveSourceDefinition(string uri, int line, int character)
        => semanticResolution.ResolveSourceDefinition(uri, line, character);

    public VbaSignatureHelp? GetSignatureHelp(string uri, int line, int character)
        => semanticResolution.GetSignatureHelp(uri, line, character);

    public VbaRange? PrepareRename(string uri, int line, int character)
    {
        var target = ResolveSourceDefinition(uri, line, character);
        return target is null || !resolutionPolicy.IsRenameTarget(target)
            ? null
            : target.Range;
    }

    public VbaRenamePlan? CreateRenamePlan(
        string uri,
        int line,
        int character,
        string newName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsIdentifierName(newName))
        {
            return null;
        }

        var target = ResolveSourceDefinition(uri, line, character);
        if (target is null || !resolutionPolicy.IsRenameTarget(target))
        {
            return null;
        }

        var changes = resolvedOccurrences.FindMatching(target, cancellationToken)
            .GroupBy(occurrence => occurrence.Uri, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<VbaTextEdit>)group
                    .Select(occurrence => new VbaTextEdit(occurrence.Range, newName))
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

        cancellationToken.ThrowIfCancellationRequested();
        return changes.Count == 0 ? null : new VbaRenamePlan(target.Range, changes);
    }

    public VbaTextEdit? FormatDocument(
        string uri,
        VbaIndentationStyle indentationStyle,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var document = definitionCandidates.FindDocument(uri);
        return document is null
            ? null
            : sourceFormatter.FormatDocument(
                document,
                indentationStyle,
                cancellationToken);
    }

    public IReadOnlyList<int> GetSemanticTokenData(
        string uri,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (semanticTokenCacheGate)
        {
            if (semanticTokenDataCache.TryGetValue(uri, out var cachedData))
            {
                return cachedData;
            }
        }

        var data = FreezeList(
            VbaSemanticTokenBuilder.GetSemanticTokenData(
                GetSemanticTokens(uri, cancellationToken),
                cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        lock (semanticTokenCacheGate)
        {
            if (semanticTokenDataCache.TryGetValue(uri, out var cachedData))
            {
                return cachedData;
            }

            semanticTokenDataCache[uri] = data;
            return data;
        }
    }

    internal IReadOnlyList<VbaSemanticToken> GetSemanticTokens(
        string uri,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (semanticTokenCacheGate)
        {
            if (semanticTokenCache.TryGetValue(uri, out var cachedTokens))
            {
                return cachedTokens;
            }
        }

        var tokens = FreezeList(
            VbaSemanticTokenBuilder.GetSemanticTokens(
                    sourceDocuments,
                    uri,
                    resolvedOccurrences.GetDocumentOccurrences(uri, cancellationToken),
                    cancellationToken)
                .Select(token => token with
                {
                    TokenModifiers = FreezeList(token.TokenModifiers)
                }));
        cancellationToken.ThrowIfCancellationRequested();
        lock (semanticTokenCacheGate)
        {
            if (semanticTokenCache.TryGetValue(uri, out var cachedTokens))
            {
                return cachedTokens;
            }

            semanticTokenCache[uri] = tokens;
            return tokens;
        }
    }

    private static string GetRangeKey(VbaRange range)
        => $"{range.Start.Line}:{range.Start.Character}:{range.End.Line}:{range.End.Character}";

    private static VbaSourceDocument CaptureDocument(VbaSourceDocument document)
        => new(
            document.Uri,
            document.Text,
            document.ModuleName,
            FreezeList(document.Definitions.Select(CaptureDefinition)),
            document.SyntaxTree);

    internal static VbaSourceDefinition CaptureDefinition(VbaSourceDefinition definition)
        => definition.Signature is null
            ? definition
            : definition with
            {
                Signature = definition.Signature with
                {
                    Parameters = FreezeList(definition.Signature.Parameters)
                }
            };

    private static VbaProjectReferenceSelection? CaptureReferenceSelection(
        VbaProjectReferenceSelection? referenceSelection)
        => referenceSelection is null
            ? null
            : referenceSelection with
            {
                References = FreezeList(referenceSelection.References)
            };

    private static IReadOnlyList<T> FreezeList<T>(IEnumerable<T> values)
        => Array.AsReadOnly(values.ToArray());

    private static bool IsIdentifierName(string value)
        => Regex.IsMatch(
            value,
            "^[A-Za-z_][A-Za-z0-9_]*$",
            RegexOptions.CultureInvariant);
}
