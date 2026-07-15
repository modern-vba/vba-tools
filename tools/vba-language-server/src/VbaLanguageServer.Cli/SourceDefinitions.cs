using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.Syntax;

namespace VbaLanguageServer.SourceModel;

/// <summary>
/// Identifies the editor-facing kind of a source definition.
/// </summary>
public enum VbaSourceDefinitionKind
{
    /// <summary>
    /// A standard module definition.
    /// </summary>
    Module,

    /// <summary>
    /// A class module definition.
    /// </summary>
    Class,

    /// <summary>
    /// A form module definition.
    /// </summary>
    Form,

    /// <summary>
    /// A Sub or Function procedure definition.
    /// </summary>
    Procedure,

    /// <summary>
    /// A property procedure definition.
    /// </summary>
    Property,

    /// <summary>
    /// A constant definition.
    /// </summary>
    Constant,

    /// <summary>
    /// A variable or field definition.
    /// </summary>
    Variable,

    /// <summary>
    /// A callable parameter definition.
    /// </summary>
    Parameter,

    /// <summary>
    /// An enum type definition.
    /// </summary>
    Enum,

    /// <summary>
    /// An enum member definition.
    /// </summary>
    EnumMember,

    /// <summary>
    /// A user-defined type definition.
    /// </summary>
    Type,

    /// <summary>
    /// A member of a user-defined type.
    /// </summary>
    TypeMember,

    /// <summary>
    /// An event definition.
    /// </summary>
    Event
}

/// <summary>
/// Represents the visibility scope of a source definition.
/// </summary>
public enum VbaSourceDefinitionVisibility
{
    /// <summary>
    /// Visible outside the declaring module.
    /// </summary>
    Public,

    /// <summary>
    /// Visible only inside the declaring module.
    /// </summary>
    Private,

    /// <summary>
    /// Visible only inside the declaring procedure.
    /// </summary>
    Local
}

/// <summary>
/// Represents a resolved or parsed type annotation used by semantic features.
/// </summary>
/// <param name="Name">The type name.</param>
/// <param name="Qualifier">The optional module or reference qualifier.</param>
public sealed record VbaTypeReference(string Name, string? Qualifier = null);

/// <summary>
/// Identifies where a definition originates without relying on its editor presentation.
/// </summary>
public enum VbaDefinitionOrigin
{
    /// <summary>
    /// The default value, which is not a valid definition origin.
    /// </summary>
    Unknown,

    /// <summary>
    /// A declaration parsed from VBA source text.
    /// </summary>
    Source,

    /// <summary>
    /// A definition projected from an active VBA project reference catalog.
    /// </summary>
    ProjectReference
}

/// <summary>
/// Identifies a source or project-reference definition independently from its display location.
/// </summary>
public readonly struct VbaDefinitionIdentity : IEquatable<VbaDefinitionIdentity>
{
    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

    private VbaDefinitionIdentity(
        VbaDefinitionOrigin origin,
        string name,
        string? sourceUri,
        VbaRange? declarationRange,
        string? referenceName,
        string? parentTypeName,
        VbaSourceDefinitionKind? kind)
    {
        Origin = origin;
        Name = name;
        SourceUri = sourceUri;
        DeclarationRange = declarationRange;
        ReferenceName = referenceName;
        ParentTypeName = parentTypeName;
        Kind = kind;
    }

    /// <summary>
    /// Gets the definition origin.
    /// </summary>
    public VbaDefinitionOrigin Origin { get; }

    /// <summary>
    /// Gets the definition name.
    /// </summary>
    public string? Name { get; }

    /// <summary>
    /// Gets the source URI for a source definition.
    /// </summary>
    public string? SourceUri { get; }

    /// <summary>
    /// Gets the declaration range for a source definition.
    /// </summary>
    public VbaRange? DeclarationRange { get; }

    /// <summary>
    /// Gets the reference name for a project-reference definition.
    /// </summary>
    public string? ReferenceName { get; }

    /// <summary>
    /// Gets the containing type name for a project-reference member.
    /// </summary>
    public string? ParentTypeName { get; }

    /// <summary>
    /// Gets the definition kind for a project-reference definition.
    /// </summary>
    public VbaSourceDefinitionKind? Kind { get; }

    /// <summary>
    /// Creates an identity for a source declaration.
    /// </summary>
    public static VbaDefinitionIdentity ForSource(string uri, string name, VbaRange declarationRange)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(declarationRange);
        return new VbaDefinitionIdentity(
            VbaDefinitionOrigin.Source,
            name,
            uri,
            declarationRange,
            null,
            null,
            null);
    }

    /// <summary>
    /// Creates an identity for a project-reference definition.
    /// </summary>
    public static VbaDefinitionIdentity ForProjectReference(
        string referenceName,
        string? parentTypeName,
        VbaSourceDefinitionKind kind,
        string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(referenceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (parentTypeName is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(parentTypeName);
        }

        return new VbaDefinitionIdentity(
            VbaDefinitionOrigin.ProjectReference,
            name,
            null,
            null,
            referenceName,
            parentTypeName,
            kind);
    }

    /// <inheritdoc />
    public bool Equals(VbaDefinitionIdentity other)
    {
        if (Origin != other.Origin || !NameComparer.Equals(Name, other.Name))
        {
            return false;
        }

        return Origin switch
        {
            VbaDefinitionOrigin.Source =>
                NameComparer.Equals(SourceUri, other.SourceUri)
                && Equals(DeclarationRange, other.DeclarationRange),
            VbaDefinitionOrigin.ProjectReference =>
                NameComparer.Equals(ReferenceName, other.ReferenceName)
                && NameComparer.Equals(ParentTypeName, other.ParentTypeName)
                && Kind == other.Kind,
            _ => true
        };
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
        => obj is VbaDefinitionIdentity other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Origin);
        hash.Add(Name, NameComparer);
        switch (Origin)
        {
            case VbaDefinitionOrigin.Source:
                hash.Add(SourceUri, NameComparer);
                hash.Add(DeclarationRange);
                break;
            case VbaDefinitionOrigin.ProjectReference:
                hash.Add(ReferenceName, NameComparer);
                hash.Add(ParentTypeName, NameComparer);
                hash.Add(Kind);
                break;
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// Determines whether two definition identities are equal.
    /// </summary>
    public static bool operator ==(VbaDefinitionIdentity left, VbaDefinitionIdentity right)
        => left.Equals(right);

    /// <summary>
    /// Determines whether two definition identities differ.
    /// </summary>
    public static bool operator !=(VbaDefinitionIdentity left, VbaDefinitionIdentity right)
        => !left.Equals(right);
}

/// <summary>
/// Represents one source-defined or reference-catalog definition used by editor features.
/// </summary>
/// <param name="Identity">The logical identity used for definition equality.</param>
/// <param name="Location">The editor-facing definition location.</param>
/// <param name="Name">The definition name.</param>
/// <param name="Kind">The editor-facing definition kind.</param>
/// <param name="Visibility">The definition visibility.</param>
/// <param name="ModuleName">The module or reference root that owns the definition.</param>
/// <param name="ParentProcedureName">The containing procedure for local definitions.</param>
/// <param name="ParentProcedureRange">The containing procedure range for local definitions.</param>
/// <param name="Documentation">The documentation text shown by hover.</param>
/// <param name="Signature">The callable signature, when the definition is callable.</param>
/// <param name="ParentTypeName">The containing enum or user-defined type name for members.</param>
/// <param name="TypeReference">The explicit result or variable type reference.</param>
/// <param name="IsWithEvents">Whether the definition declares WithEvents.</param>
/// <param name="DeclarationLabel">The editor-facing declaration summary for hover display.</param>
/// <param name="PropertyAccess">The supported property operations, or Unknown when unavailable.</param>
public sealed record VbaSourceDefinition(
    VbaDefinitionIdentity Identity,
    VbaDefinitionLocation Location,
    string Name,
    VbaSourceDefinitionKind Kind,
    VbaSourceDefinitionVisibility Visibility,
    string ModuleName,
    string? ParentProcedureName = null,
    VbaRange? ParentProcedureRange = null,
    string? Documentation = null,
    VbaCallableSignature? Signature = null,
    string? ParentTypeName = null,
    VbaTypeReference? TypeReference = null,
    bool IsWithEvents = false,
    string? DeclarationLabel = null,
    VbaPropertyAccess PropertyAccess = VbaPropertyAccess.Unknown)
{
    /// <summary>
    /// Gets the editor-facing definition URI.
    /// </summary>
    public string Uri => Location.Uri;

    /// <summary>
    /// Gets the editor-facing definition range.
    /// </summary>
    public VbaRange Range => Location.Range;
}

/// <summary>
/// Represents one callable parameter in editor-facing signature metadata.
/// </summary>
/// <param name="Name">The parameter name.</param>
/// <param name="Documentation">The parameter documentation text.</param>
/// <param name="IsOptional">Whether the parameter is optional when the source metadata provides it.</param>
/// <param name="DisplayLabel">The displayed parameter segment in the containing signature label.</param>
/// <param name="TypeReference">The parameter type reference, when supplied by the source or catalog.</param>
/// <param name="IsByRef">Whether the parameter is known to be passed ByRef. Null means the metadata is unavailable.</param>
/// <param name="IsParamArray">Whether the parameter is declared ParamArray.</param>
/// <param name="IsArray">Whether the parameter name carries a VBA array marker.</param>
public sealed record VbaCallableParameter(
    string Name,
    string? Documentation = null,
    bool IsOptional = false,
    string? DisplayLabel = null,
    VbaTypeReference? TypeReference = null,
    bool? IsByRef = null,
    bool IsParamArray = false,
    bool IsArray = false)
{
    /// <summary>
    /// Gets the parameter segment shown inside its callable signature.
    /// </summary>
    [JsonIgnore]
    public string Label => DisplayLabel ?? Name;
}

/// <summary>
/// Identifies the declared callable form without inferring it from return metadata.
/// </summary>
public enum VbaCallableKind
{
    /// <summary>
    /// A Sub procedure that does not return a value.
    /// </summary>
    Sub,

    /// <summary>
    /// A Function procedure that returns a value.
    /// </summary>
    Function,

    /// <summary>
    /// A Property accessor exposed as one callable property.
    /// </summary>
    Property,

    /// <summary>
    /// An Event declaration.
    /// </summary>
    Event
}

/// <summary>
/// Represents callable signature metadata used by hover and signature help.
/// </summary>
/// <param name="Label">The full signature label.</param>
/// <param name="Parameters">The ordered parameter metadata.</param>
/// <param name="Documentation">The callable documentation retained for semantic consumers but omitted from LSP Signature Help.</param>
/// <param name="CallableKind">The explicit callable kind when supplied by source or catalog metadata.</param>
public sealed record VbaCallableSignature(
    string Label,
    IReadOnlyList<VbaCallableParameter> Parameters,
    string? Documentation = null,
    VbaCallableKind? CallableKind = null);

/// <summary>
/// Represents the signature help result for a call site.
/// </summary>
/// <param name="Signature">The callable signature to show.</param>
/// <param name="ActiveParameter">The zero-based active parameter index.</param>
public sealed record VbaSignatureHelp(VbaCallableSignature Signature, int ActiveParameter);

/// <summary>
/// Identifies which fixed VBA vocabulary set can be appended to completion definitions.
/// </summary>
public enum VbaCompletionVocabularyKind
{
    /// <summary>
    /// No fixed VBA vocabulary is valid in the completion context.
    /// </summary>
    None,

    /// <summary>
    /// Fixed VBA type names are valid in the completion context.
    /// </summary>
    TypeName,

    /// <summary>
    /// General fixed VBA language keywords are valid in the completion context.
    /// </summary>
    Keyword
}

/// <summary>
/// Represents completion definitions and the fixed language vocabulary that should be added.
/// </summary>
/// <param name="Definitions">The completion candidate definitions.</param>
/// <param name="VocabularyKind">The fixed VBA vocabulary set valid in this completion context.</param>
public sealed record VbaCompletionResult(
    IReadOnlyList<VbaSourceDefinition> Definitions,
    VbaCompletionVocabularyKind VocabularyKind);

/// <summary>
/// Represents one parsed source document in the source index.
/// </summary>
/// <param name="Uri">The document URI.</param>
/// <param name="Text">The complete source text.</param>
/// <param name="ModuleName">The parsed module identity.</param>
/// <param name="Definitions">The definitions declared by the document.</param>
/// <param name="SyntaxTree">The parsed syntax tree for features that need structured syntax.</param>
public sealed record VbaSourceDocument(
    string Uri,
    string Text,
    string ModuleName,
    IReadOnlyList<VbaSourceDefinition> Definitions,
    VbaSyntaxTree? SyntaxTree = null);

/// <summary>
/// Represents a definition or reference location.
/// </summary>
/// <param name="Uri">The document URI.</param>
/// <param name="Range">The source range.</param>
public sealed record VbaDefinitionLocation(string Uri, VbaRange Range);

/// <summary>
/// Represents one text edit in LSP-compatible coordinates.
/// </summary>
/// <param name="Range">The source range to replace.</param>
/// <param name="NewText">The replacement text.</param>
public sealed record VbaTextEdit(VbaRange Range, string NewText);

/// <summary>
/// Represents a validated rename operation and its resulting edits.
/// </summary>
/// <param name="TargetRange">The range that should be highlighted for prepareRename.</param>
/// <param name="Changes">The source edits keyed by document URI.</param>
public sealed record VbaRenamePlan(
    VbaRange TargetRange,
    IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>> Changes);

/// <summary>
/// Represents a workspace symbol projected from a source definition.
/// </summary>
/// <param name="Name">The symbol name.</param>
/// <param name="Kind">The symbol definition kind.</param>
/// <param name="Uri">The owning document URI.</param>
/// <param name="Range">The symbol source range.</param>
public sealed record VbaWorkspaceSymbol(
    string Name,
    VbaSourceDefinitionKind Kind,
    string Uri,
    VbaRange Range);

/// <summary>
/// Represents one semantic token before LSP delta encoding.
/// </summary>
/// <param name="Range">The source range covered by the token.</param>
/// <param name="Text">The source text covered by the token.</param>
/// <param name="TokenType">The semantic token type name.</param>
/// <param name="TokenModifiers">The semantic token modifier names.</param>
public sealed record VbaSemanticToken(
    VbaRange Range,
    string Text,
    string TokenType,
    IReadOnlyList<string> TokenModifiers);

/// <summary>
/// Indexes parsed VBA source documents and serves editor-intelligence queries over their definitions.
/// </summary>
public sealed class VbaSourceIndex
{
    /// <summary>
    /// Gets the semantic token legend types advertised to LSP clients.
    /// </summary>
    public static readonly IReadOnlyList<string> SemanticTokenTypes = [
        "namespace",
        "type",
        "class",
        "enum",
        "interface",
        "struct",
        "typeParameter",
        "parameter",
        "variable",
        "property",
        "field",
        "enumMember",
        "event",
        "function",
        "method"
    ];

    /// <summary>
    /// Gets the semantic token legend modifiers advertised to LSP clients.
    /// </summary>
    public static readonly IReadOnlyList<string> SemanticTokenModifiers = [
        "declaration",
        "definition",
        "readonly",
        "static",
        "deprecated",
        "abstract",
        "async",
        "modification",
        "documentation",
        "defaultLibrary"
    ];

    /// <summary>
    /// Gets the fixed VBA language vocabulary used by completion and formatting features.
    /// </summary>
    public static readonly IReadOnlyList<string> LanguageVocabulary = VbaLanguageVocabulary.Keywords;

    /// <summary>
    /// Gets the fixed VBA type names used by type annotation completion.
    /// </summary>
    public static readonly IReadOnlyList<string> TypeVocabulary = VbaLanguageVocabulary.TypeNames;

    private readonly IReadOnlyList<VbaSourceDocument> documents;
    private readonly VbaSemanticResolution semanticResolution;
    private readonly VbaResolutionPolicy resolutionPolicy;
    private readonly VbaResolvedIdentifierOccurrenceIndex resolvedOccurrences;
    private readonly VbaSourceFormatter sourceFormatter;
    private readonly object semanticTokenCacheGate = new();
    private readonly Dictionary<string, IReadOnlyList<VbaSemanticToken>> semanticTokenCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<int>> semanticTokenDataCache = new(StringComparer.OrdinalIgnoreCase);

    private VbaSourceIndex(
        IReadOnlyList<VbaSourceDocument> documents,
        VbaProjectReferenceSelection? referenceSelection,
        VbaProjectReferenceCatalogSet referenceCatalogs)
    {
        this.documents = documents;
        resolutionPolicy = new VbaResolutionPolicy();
        semanticResolution = new VbaSemanticResolution(documents, referenceSelection, referenceCatalogs, resolutionPolicy);
        resolvedOccurrences = new VbaResolvedIdentifierOccurrenceIndex(
            documents,
            semanticResolution.ResolveSourceDefinition);
        sourceFormatter = new VbaSourceFormatter(semanticResolution, resolvedOccurrences);
    }

    /// <summary>
    /// Builds a source index from raw source text documents.
    /// </summary>
    /// <param name="sourceDocuments">The source text keyed by document URI.</param>
    /// <param name="referenceSelection">The active VBA project reference selection.</param>
    /// <param name="referenceCatalogs">The available reference catalogs.</param>
    /// <returns>The built source index.</returns>
    public static VbaSourceIndex Build(
        IReadOnlyDictionary<string, string> sourceDocuments,
        VbaProjectReferenceSelection? referenceSelection = null,
        VbaProjectReferenceCatalogSet? referenceCatalogs = null)
    {
        var parsedDocuments = sourceDocuments
            .Select(entry => ParseDocument(entry.Key, entry.Value))
            .ToArray();
        return new VbaSourceIndex(
            parsedDocuments,
            referenceSelection,
            referenceCatalogs ?? VbaProjectReferenceCatalogSet.Empty);
    }

    /// <summary>
    /// Builds a source index from already parsed syntax trees.
    /// </summary>
    /// <param name="sourceDocuments">The syntax trees keyed by document URI.</param>
    /// <param name="referenceSelection">The active VBA project reference selection.</param>
    /// <param name="referenceCatalogs">The available reference catalogs.</param>
    /// <returns>The built source index.</returns>
    public static VbaSourceIndex BuildFromSyntaxTrees(
        IReadOnlyDictionary<string, VbaSyntaxTree> sourceDocuments,
        VbaProjectReferenceSelection? referenceSelection = null,
        VbaProjectReferenceCatalogSet? referenceCatalogs = null)
    {
        var parsedDocuments = sourceDocuments
            .Select(entry => CreateDocument(entry.Key, entry.Value))
            .ToArray();
        return new VbaSourceIndex(
            parsedDocuments,
            referenceSelection,
            referenceCatalogs ?? VbaProjectReferenceCatalogSet.Empty);
    }

    /// <summary>
    /// Builds a source index from already parsed and projected source documents.
    /// </summary>
    /// <param name="sourceDocuments">The projected source documents keyed by document URI.</param>
    /// <param name="referenceSelection">The active VBA project reference selection.</param>
    /// <param name="referenceCatalogs">The available reference catalogs.</param>
    /// <returns>The built source index.</returns>
    public static VbaSourceIndex BuildFromSourceDocuments(
        IReadOnlyDictionary<string, VbaSourceDocument> sourceDocuments,
        VbaProjectReferenceSelection? referenceSelection = null,
        VbaProjectReferenceCatalogSet? referenceCatalogs = null)
    {
        return new VbaSourceIndex(
            sourceDocuments.Values.ToArray(),
            referenceSelection,
            referenceCatalogs ?? VbaProjectReferenceCatalogSet.Empty);
    }

    /// <summary>
    /// Gets definitions declared in a document.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <returns>The document definitions, or an empty list when the document is not indexed.</returns>
    public IReadOnlyList<VbaSourceDefinition> GetDocumentDefinitions(string uri)
        => documents
            .FirstOrDefault(document => SameUri(document.Uri, uri))
            ?.Definitions
            ?? Array.Empty<VbaSourceDefinition>();

    /// <summary>
    /// Searches workspace symbols across indexed source documents.
    /// </summary>
    /// <param name="query">The optional symbol name substring.</param>
    /// <returns>The matching non-local source symbols.</returns>
    public IReadOnlyList<VbaWorkspaceSymbol> GetWorkspaceSymbols(string query)
    {
        var normalizedQuery = query ?? "";
        return documents
            .SelectMany(document => document.Definitions)
            .Where(definition => definition.Visibility != VbaSourceDefinitionVisibility.Local)
            .Where(definition => !VbaProjectReferenceCatalogSet.IsExternalDefinition(definition))
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

    /// <summary>
    /// Finds source references to the definition at a document position.
    /// </summary>
    /// <param name="uri">The document URI containing the position.</param>
    /// <param name="line">The zero-based line.</param>
    /// <param name="character">The zero-based character.</param>
    /// <returns>The matching reference locations across indexed source documents.</returns>
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

    /// <summary>
    /// Gets semantic tokens for one document.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <returns>The semantic tokens before LSP encoding.</returns>
    public IReadOnlyList<VbaSemanticToken> GetSemanticTokens(string uri)
    {
        lock (semanticTokenCacheGate)
        {
            if (semanticTokenCache.TryGetValue(uri, out var cachedTokens))
            {
                return cachedTokens;
            }
        }

        var tokens = VbaSemanticTokenBuilder.GetSemanticTokens(
            documents,
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

    /// <summary>
    /// Gets LSP delta-encoded semantic token data for one document.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <returns>The encoded semantic token integer data.</returns>
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

    /// <summary>
    /// Gets completion definitions visible at a document position.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="line">The zero-based line.</param>
    /// <param name="character">The zero-based character.</param>
    /// <returns>The completion candidate definitions.</returns>
    public IReadOnlyList<VbaSourceDefinition> GetCompletionDefinitions(string uri, int line, int character)
        => GetCompletionResult(uri, line, character).Definitions;

    /// <summary>
    /// Gets completion definitions and context metadata for a document position.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="line">The zero-based line.</param>
    /// <param name="character">The zero-based character.</param>
    /// <returns>The completion result for the position.</returns>
    public VbaCompletionResult GetCompletionResult(string uri, int line, int character)
        => semanticResolution.GetCompletionResult(uri, line, character);

    /// <summary>
    /// Resolves the definition location for the reference at a document position.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="line">The zero-based line.</param>
    /// <param name="character">The zero-based character.</param>
    /// <returns>The definition location, or null when the reference is unresolved or ambiguous.</returns>
    public VbaDefinitionLocation? ResolveDefinition(string uri, int line, int character)
    {
        var definition = ResolveSourceDefinition(uri, line, character);
        return definition?.Location;
    }

    /// <summary>
    /// Resolves the source definition for the reference at a document position.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="line">The zero-based line.</param>
    /// <param name="character">The zero-based character.</param>
    /// <returns>The resolved definition, or null when unresolved or ambiguous.</returns>
    public VbaSourceDefinition? ResolveSourceDefinition(string uri, int line, int character)
        => semanticResolution.ResolveSourceDefinition(uri, line, character);

    /// <summary>
    /// Gets signature help for the call site at a document position.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="line">The zero-based line.</param>
    /// <param name="character">The zero-based character.</param>
    /// <returns>The signature help result, or null when no callable target resolves.</returns>
    public VbaSignatureHelp? GetSignatureHelp(string uri, int line, int character)
        => semanticResolution.GetSignatureHelp(uri, line, character);

    /// <summary>
    /// Gets the rename target range when the symbol at a document position can be renamed.
    /// </summary>
    /// <param name="uri">The document URI containing the rename position.</param>
    /// <param name="line">The zero-based line.</param>
    /// <param name="character">The zero-based character.</param>
    /// <returns>The rename target range, or null when rename is unavailable.</returns>
    public VbaRange? PrepareRename(string uri, int line, int character)
    {
        var target = ResolveSourceDefinition(uri, line, character);
        return target is null || !resolutionPolicy.IsRenameTarget(target)
            ? null
            : target.Range;
    }

    /// <summary>
    /// Creates workspace edits for renaming the source definition at a document position.
    /// </summary>
    /// <param name="uri">The document URI containing the rename position.</param>
    /// <param name="line">The zero-based line.</param>
    /// <param name="character">The zero-based character.</param>
    /// <param name="newName">The requested new identifier name.</param>
    /// <returns>The edits keyed by URI, or null when the rename is not valid.</returns>
    public IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>>? CreateRenameChanges(
        string uri,
        int line,
        int character,
        string newName)
        => CreateRenamePlan(uri, line, character, newName)?.Changes;

    /// <summary>
    /// Creates a validated rename plan for the source definition at a document position.
    /// </summary>
    /// <param name="uri">The document URI containing the rename position.</param>
    /// <param name="line">The zero-based line.</param>
    /// <param name="character">The zero-based character.</param>
    /// <param name="newName">The requested new identifier name.</param>
    /// <returns>The rename plan, or null when rename is not valid.</returns>
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

    /// <summary>
    /// Formats a document and returns a whole-document replacement edit when formatting changes text.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="tabSize">The number of spaces used per indentation level.</param>
    /// <returns>The formatting edit, or null when the document is unknown or already formatted.</returns>
    public VbaTextEdit? FormatDocument(string uri, int tabSize)
    {
        var document = documents.FirstOrDefault(candidate => SameUri(candidate.Uri, uri));
        return document is null ? null : sourceFormatter.FormatDocument(document, tabSize);
    }

    private static string GetRangeKey(VbaRange range)
        => $"{range.Start.Line}:{range.Start.Character}:{range.End.Line}:{range.End.Character}";

    private static VbaSourceDocument ParseDocument(string uri, string text)
    {
        var syntaxTree = VbaSyntaxTree.ParseModule(uri, text);
        return CreateDocument(uri, syntaxTree);
    }

    internal static VbaSourceDocument CreateDocument(string uri, VbaSyntaxTree syntaxTree)
    {
        var definitions = new List<VbaSourceDefinition>();
        var moduleDefinition = CreateModuleDefinition(uri, syntaxTree.Module);
        definitions.Add(moduleDefinition);
        definitions.AddRange(syntaxTree.Module.Declarations.Select(declaration =>
            CreateSourceDefinition(uri, moduleDefinition.Name, declaration)));

        return new VbaSourceDocument(uri, syntaxTree.Text, moduleDefinition.Name, definitions, syntaxTree);
    }

    private static VbaSourceDefinition CreateModuleDefinition(string uri, VbaModuleSyntax module)
    {
        var range = MapRange(module.Identity.Range);
        return new VbaSourceDefinition(
            VbaDefinitionIdentity.ForSource(uri, module.Identity.Name, range),
            new VbaDefinitionLocation(uri, range),
            module.Identity.Name,
            MapModuleKind(module.Kind),
            VbaSourceDefinitionVisibility.Public,
            module.Identity.Name);
    }

    private static VbaSourceDefinition CreateSourceDefinition(
        string uri,
        string moduleName,
        VbaDeclarationSyntax declaration)
    {
        var range = MapRange(declaration.Range);
        return new VbaSourceDefinition(
            Identity: VbaDefinitionIdentity.ForSource(uri, declaration.Name, range),
            Location: new VbaDefinitionLocation(uri, range),
            Name: declaration.Name,
            Kind: MapDeclarationKind(declaration.Kind),
            Visibility: MapVisibility(declaration.Visibility),
            ModuleName: moduleName,
            ParentProcedureName: declaration.ParentProcedureName,
            ParentProcedureRange: declaration.ParentProcedureRange is null ? null : MapRange(declaration.ParentProcedureRange),
            Documentation: declaration.Documentation,
            Signature: declaration.Signature is null ? null : MapSignature(declaration),
            ParentTypeName: declaration.ParentTypeName,
            TypeReference: declaration.TypeReference is null ? null : MapTypeReference(declaration.TypeReference),
            IsWithEvents: declaration.IsWithEvents,
            DeclarationLabel: declaration.DeclarationLabel,
            PropertyAccess: MapPropertyAccess(declaration.PropertyAccessorKind));
    }

    private static VbaSourceDefinitionKind MapModuleKind(VbaModuleKind kind)
        => kind switch
        {
            VbaModuleKind.ClassModule => VbaSourceDefinitionKind.Class,
            VbaModuleKind.FormModule => VbaSourceDefinitionKind.Form,
            _ => VbaSourceDefinitionKind.Module
        };

    private static VbaSourceDefinitionKind MapDeclarationKind(VbaDeclarationKind kind)
        => kind switch
        {
            VbaDeclarationKind.Procedure => VbaSourceDefinitionKind.Procedure,
            VbaDeclarationKind.Property => VbaSourceDefinitionKind.Property,
            VbaDeclarationKind.Constant => VbaSourceDefinitionKind.Constant,
            VbaDeclarationKind.Variable => VbaSourceDefinitionKind.Variable,
            VbaDeclarationKind.Parameter => VbaSourceDefinitionKind.Parameter,
            VbaDeclarationKind.Enum => VbaSourceDefinitionKind.Enum,
            VbaDeclarationKind.EnumMember => VbaSourceDefinitionKind.EnumMember,
            VbaDeclarationKind.Type => VbaSourceDefinitionKind.Type,
            VbaDeclarationKind.TypeMember => VbaSourceDefinitionKind.TypeMember,
            VbaDeclarationKind.Event => VbaSourceDefinitionKind.Event,
            _ => VbaSourceDefinitionKind.Variable
        };

    private static VbaSourceDefinitionVisibility MapVisibility(VbaDeclarationVisibility visibility)
        => visibility switch
        {
            VbaDeclarationVisibility.Public => VbaSourceDefinitionVisibility.Public,
            VbaDeclarationVisibility.Local => VbaSourceDefinitionVisibility.Local,
            _ => VbaSourceDefinitionVisibility.Private
        };

    private static VbaRange MapRange(VbaSyntaxRange range)
        => new(
            new VbaPosition(range.Start.Line, range.Start.Character),
            new VbaPosition(range.End.Line, range.End.Character));

    private static VbaCallableSignature MapSignature(VbaDeclarationSyntax declaration)
    {
        var signature = declaration.Signature!;
        var parameterLabels = signature.Parameters.Select(CreateSignatureParameterLabel).ToArray();
        var callableKind = GetCallableKind(declaration);
        var declarePrefix = declaration.IsExternal ? "Declare " : "";
        var label = $"{declarePrefix}{callableKind} {declaration.Name}({string.Join(", ", parameterLabels)})";
        if (declaration.TypeReference is not null)
        {
            label = $"{label} As {declaration.TypeReference.Name}";
        }

        return new VbaCallableSignature(
            label,
            signature.Parameters
                .Select((parameter, index) => new VbaCallableParameter(
                    Name: parameter.Name,
                    Documentation: parameter.Documentation,
                    IsOptional: parameter.IsOptional,
                    DisplayLabel: parameterLabels[index],
                    TypeReference: parameter.TypeReference is null ? null : MapTypeReference(parameter.TypeReference),
                    IsByRef: parameter.IsByRef,
                    IsParamArray: parameter.IsParamArray,
                    IsArray: parameter.IsArray))
                .ToArray(),
            signature.Documentation,
            CallableKind: callableKind);
    }

    private static VbaCallableKind GetCallableKind(VbaDeclarationSyntax declaration)
        => declaration.CallableKind?.ToUpperInvariant() switch
        {
            "SUB" => VbaCallableKind.Sub,
            "FUNCTION" => VbaCallableKind.Function,
            "PROPERTY" => VbaCallableKind.Property,
            "EVENT" => VbaCallableKind.Event,
            _ => declaration.Kind switch
            {
                VbaDeclarationKind.Property => VbaCallableKind.Property,
                VbaDeclarationKind.Event => VbaCallableKind.Event,
                _ => declaration.TypeReference is null ? VbaCallableKind.Sub : VbaCallableKind.Function
            }
        };

    private static VbaPropertyAccess MapPropertyAccess(VbaPropertyAccessorKind? accessorKind)
        => accessorKind switch
        {
            VbaPropertyAccessorKind.Get => VbaPropertyAccess.Readable,
            VbaPropertyAccessorKind.Let or VbaPropertyAccessorKind.Set => VbaPropertyAccess.Writable,
            _ => VbaPropertyAccess.Unknown
        };

    private static string CreateSignatureParameterLabel(VbaCallableParameterInfoSyntax parameter)
    {
        var parts = new List<string>();
        if (parameter.IsParamArray)
        {
            parts.Add("ParamArray");
        }
        else if (parameter.IsByRef)
        {
            parts.Add("ByRef");
        }

        parts.Add(parameter.IsArray ? $"{parameter.Name}()" : parameter.Name);
        if (parameter.TypeReference is not null)
        {
            parts.Add($"As {parameter.TypeReference.Name}");
        }

        var label = string.Join(" ", parts);
        return parameter.IsOptional ? $"[{label}]" : label;
    }

    private static VbaTypeReference MapTypeReference(VbaTypeReferenceSyntax typeReference)
        => new(typeReference.Name, typeReference.Qualifier);

    private static bool IsIdentifierName(string value)
        => Regex.IsMatch(
            value,
            "^[A-Za-z_][A-Za-z0-9_]*$",
            RegexOptions.CultureInvariant);

    private static bool SameUri(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

}
