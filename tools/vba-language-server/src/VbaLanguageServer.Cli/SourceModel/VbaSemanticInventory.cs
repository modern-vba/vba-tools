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
    private readonly VbaSourceIndex compatibilityIndex;
    private readonly IReadOnlyList<VbaSourceDocument> sourceDocuments;
    private readonly VbaResolutionPolicy resolutionPolicy = new();
    private readonly VbaResolvedIdentifierOccurrenceIndex resolvedOccurrences;
    private readonly object semanticTokenCacheGate = new();
    private readonly Dictionary<string, IReadOnlyList<VbaSemanticToken>> semanticTokenCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<int>> semanticTokenDataCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<string, IReadOnlyList<VbaSourceDefinition>> definitionsByDocument;
    private readonly IReadOnlyList<VbaSourceDefinition> workspaceSymbolDefinitions;

    private VbaSemanticInventory(
        VbaSourceIndex compatibilityIndex,
        IReadOnlyList<VbaSourceDocument> sourceDocuments,
        IReadOnlyDictionary<string, IReadOnlyList<VbaSourceDefinition>> definitionsByDocument,
        IReadOnlyDictionary<string, IReadOnlyList<VbaSourceDefinition>> definitionsByNormalizedName,
        IReadOnlyDictionary<string, IReadOnlyList<VbaSourceDefinition>> definitionsByModule,
        IReadOnlyDictionary<string, IReadOnlyList<VbaSourceDefinition>> definitionsByType,
        IReadOnlyDictionary<string, IReadOnlyList<VbaSourceDefinition>> definitionsByParentType,
        IReadOnlyDictionary<string, IReadOnlyList<VbaSourceDefinition>> definitionsByQualifier,
        IReadOnlyDictionary<string, IReadOnlyList<VbaSourceDefinition>> definitionsByCallableIdentity,
        IReadOnlyList<VbaSourceDefinition> workspaceSymbolDefinitions)
    {
        this.compatibilityIndex = compatibilityIndex;
        this.sourceDocuments = sourceDocuments;
        this.definitionsByDocument = definitionsByDocument;
        DefinitionsByNormalizedName = definitionsByNormalizedName;
        DefinitionsByModule = definitionsByModule;
        DefinitionsByType = definitionsByType;
        DefinitionsByParentType = definitionsByParentType;
        DefinitionsByQualifier = definitionsByQualifier;
        DefinitionsByCallableIdentity = definitionsByCallableIdentity;
        this.workspaceSymbolDefinitions = workspaceSymbolDefinitions;
        resolvedOccurrences = new VbaResolvedIdentifierOccurrenceIndex(
            sourceDocuments,
            compatibilityIndex.ResolveSourceDefinition);
    }

    /// <summary>
    /// Gets definitions grouped by normalized definition name.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<VbaSourceDefinition>> DefinitionsByNormalizedName { get; }

    /// <summary>
    /// Gets definitions grouped by declaring module or reference root.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<VbaSourceDefinition>> DefinitionsByModule { get; }

    /// <summary>
    /// Gets type-like definitions grouped by normalized type name.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<VbaSourceDefinition>> DefinitionsByType { get; }

    /// <summary>
    /// Gets member definitions grouped by normalized parent type name.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<VbaSourceDefinition>> DefinitionsByParentType { get; }

    /// <summary>
    /// Gets definitions grouped by their qualification root.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<VbaSourceDefinition>> DefinitionsByQualifier { get; }

    /// <summary>
    /// Gets callable definitions grouped by module, parent type, name, and callable label.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<VbaSourceDefinition>> DefinitionsByCallableIdentity { get; }

    /// <summary>
    /// Creates a semantic inventory from an existing compatibility index and projected source documents.
    /// </summary>
    public static VbaSemanticInventory Create(
        VbaSourceIndex compatibilityIndex,
        IReadOnlyDictionary<string, VbaSourceDocument> sourceDocuments)
    {
        var allDefinitions = sourceDocuments.Values
            .SelectMany(document => document.Definitions)
            .ToArray();
        return new VbaSemanticInventory(
            compatibilityIndex,
            sourceDocuments.Values.ToArray(),
            sourceDocuments.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<VbaSourceDefinition>)pair.Value.Definitions,
                StringComparer.OrdinalIgnoreCase),
            GroupDefinitions(
                allDefinitions,
                definition => Normalize(definition.Name)),
            GroupDefinitions(
                allDefinitions,
                definition => Normalize(definition.ModuleName)),
            GroupDefinitions(
                allDefinitions.Where(IsTypeDefinition),
                definition => Normalize(definition.Name)),
            GroupDefinitions(
                allDefinitions.Where(definition => !string.IsNullOrWhiteSpace(definition.ParentTypeName)),
                definition => Normalize(definition.ParentTypeName!)),
            GroupDefinitions(
                allDefinitions,
                definition => Normalize(definition.TypeReference?.Qualifier ?? definition.ModuleName)),
            GroupDefinitions(
                allDefinitions.Where(definition => definition.Signature is not null),
                CreateCallableIdentityKey),
            allDefinitions
                .Where(definition => definition.Visibility != VbaSourceDefinitionVisibility.Local)
                .Where(definition => !VbaProjectReferenceCatalogSet.IsExternalDefinition(definition))
                .ToArray());
    }

    /// <summary>
    /// Gets definitions declared in a document.
    /// </summary>
    public IReadOnlyList<VbaSourceDefinition> GetDocumentDefinitions(string uri)
        => definitionsByDocument.TryGetValue(uri, out var definitions)
            ? definitions
            : Array.Empty<VbaSourceDefinition>();

    /// <summary>
    /// Searches workspace symbols across indexed source documents.
    /// </summary>
    public IReadOnlyList<VbaWorkspaceSymbol> GetWorkspaceSymbols(string query)
    {
        var normalizedQuery = query ?? "";
        return workspaceSymbolDefinitions
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
        => compatibilityIndex.GetCompletionResult(uri, line, character);

    public VbaDefinitionLocation? ResolveDefinition(string uri, int line, int character)
        => compatibilityIndex.ResolveDefinition(uri, line, character);

    public IReadOnlyList<VbaDefinitionLocation> FindReferences(string uri, int line, int character)
    {
        var target = ResolveSourceDefinition(uri, line, character);
        if (target is null)
        {
            return [];
        }

        return resolvedOccurrences.FindMatching(target)
            .Select(occurrence => new VbaDefinitionLocation(occurrence.Uri, occurrence.Range))
            .GroupBy(reference => $"{reference.Uri}:{GetRangeKey(reference.Range)}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(reference => reference.Uri, StringComparer.OrdinalIgnoreCase)
            .ThenBy(reference => reference.Range.Start.Line)
            .ThenBy(reference => reference.Range.Start.Character)
            .ToArray();
    }

    public VbaSourceDefinition? ResolveSourceDefinition(string uri, int line, int character)
        => compatibilityIndex.ResolveSourceDefinition(uri, line, character);

    public VbaSignatureHelp? GetSignatureHelp(string uri, int line, int character)
        => compatibilityIndex.GetSignatureHelp(uri, line, character);

    public VbaRange? PrepareRename(string uri, int line, int character)
        => compatibilityIndex.PrepareRename(uri, line, character);

    public VbaRenamePlan? CreateRenamePlan(
        string uri,
        int line,
        int character,
        string newName)
    {
        if (!IsIdentifierName(newName))
        {
            return null;
        }

        var target = ResolveSourceDefinition(uri, line, character);
        if (target is null || !resolutionPolicy.IsRenameTarget(target))
        {
            return null;
        }

        var changes = resolvedOccurrences.FindMatching(target)
            .GroupBy(occurrence => occurrence.Uri, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<VbaTextEdit>)group
                    .Select(occurrence => new VbaTextEdit(occurrence.Range, newName))
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

        return changes.Count == 0 ? null : new VbaRenamePlan(target.Range, changes);
    }

    public VbaTextEdit? FormatDocument(string uri, VbaIndentationStyle indentationStyle)
        => compatibilityIndex.FormatDocument(uri, indentationStyle);

    public IReadOnlyList<int> GetSemanticTokenData(string uri)
    {
        lock (semanticTokenCacheGate)
        {
            if (semanticTokenDataCache.TryGetValue(uri, out var cachedData))
            {
                return cachedData;
            }
        }

        var data = VbaSemanticTokenBuilder.GetSemanticTokenData(GetSemanticTokens(uri));
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

    internal IReadOnlyList<VbaSemanticToken> GetSemanticTokens(string uri)
    {
        lock (semanticTokenCacheGate)
        {
            if (semanticTokenCache.TryGetValue(uri, out var cachedTokens))
            {
                return cachedTokens;
            }
        }

        var tokens = VbaSemanticTokenBuilder.GetSemanticTokens(
            sourceDocuments,
            uri,
            resolvedOccurrences.GetDocumentOccurrences(uri));
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

    private static IReadOnlyDictionary<string, IReadOnlyList<VbaSourceDefinition>> GroupDefinitions(
        IEnumerable<VbaSourceDefinition> definitions,
        Func<VbaSourceDefinition, string> getKey)
        => definitions
            .GroupBy(getKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<VbaSourceDefinition>)group.ToArray(),
                StringComparer.OrdinalIgnoreCase);

    private static bool IsTypeDefinition(VbaSourceDefinition definition)
        => definition.Kind is VbaSourceDefinitionKind.Class
            or VbaSourceDefinitionKind.Enum
            or VbaSourceDefinitionKind.Form
            or VbaSourceDefinitionKind.Type;

    private static string CreateCallableIdentityKey(VbaSourceDefinition definition)
        => string.Join(
            "|",
            Normalize(definition.ModuleName),
            Normalize(definition.ParentTypeName ?? ""),
            Normalize(definition.Name),
            Normalize(definition.Signature?.Label ?? ""));

    private static string Normalize(string value)
        => value.Trim().ToUpperInvariant();

    private static string GetRangeKey(VbaRange range)
        => $"{range.Start.Line}:{range.Start.Character}:{range.End.Line}:{range.End.Character}";

    private static bool IsIdentifierName(string value)
        => Regex.IsMatch(
            value,
            "^[A-Za-z_][A-Za-z0-9_]*$",
            RegexOptions.CultureInvariant);
}
