using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.Syntax;

namespace VbaLanguageServer.SourceModel;

/// <summary>
/// Provides semantic resolution for completion, definition, signature help, and formatting.
/// </summary>
internal sealed class VbaSemanticResolution
{
    private readonly IReadOnlyList<VbaSourceDocument> documents;
    private readonly VbaNameResolutionService nameResolution;
    private readonly VbaTypeResolution typeResolution;
    private readonly VbaMemberChainResolution memberChainResolution;
    private readonly VbaCallSiteResolution callSiteResolution;

    /// <summary>
    /// Creates the semantic resolution service.
    /// </summary>
    /// <param name="documents">The indexed source documents.</param>
    /// <param name="referenceSelection">The active reference selection for the project.</param>
    /// <param name="referenceCatalogs">The available reference catalogs.</param>
    public VbaSemanticResolution(
        IReadOnlyList<VbaSourceDocument> documents,
        VbaProjectReferenceSelection? referenceSelection,
        VbaProjectReferenceCatalogSet referenceCatalogs,
        VbaResolutionPolicy? resolutionPolicy = null)
    {
        this.documents = documents;
        resolutionPolicy ??= new VbaResolutionPolicy();
        nameResolution = new VbaNameResolutionService(
            documents,
            referenceSelection,
            referenceCatalogs,
            activeReferenceDefinitions: null,
            resolutionPolicy: resolutionPolicy);
        typeResolution = new VbaTypeResolution(nameResolution);
        memberChainResolution = new VbaMemberChainResolution(typeResolution);
        callSiteResolution = new VbaCallSiteResolution(nameResolution, memberChainResolution);
    }

    /// <summary>
    /// Gets completion definitions visible at a position, including member completions when a receiver resolves.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="line">The zero-based line.</param>
    /// <param name="character">The zero-based character.</param>
    /// <returns>The completion candidate definitions.</returns>
    public IReadOnlyList<VbaSourceDefinition> GetCompletionDefinitions(string uri, int line, int character)
        => GetCompletionResult(uri, line, character).Definitions;

    /// <summary>
    /// Gets completion definitions and vocabulary eligibility for a source position.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="line">The zero-based line.</param>
    /// <param name="character">The zero-based character.</param>
    /// <returns>The completion result for the position.</returns>
    public VbaCompletionResult GetCompletionResult(string uri, int line, int character)
    {
        var currentDocument = documents.FirstOrDefault(document => SameUri(document.Uri, uri));
        if (currentDocument is null)
        {
            return new VbaCompletionResult(
                nameResolution.GetCompletionDefinitions(uri, new VbaPosition(line, character)),
                VbaCompletionVocabularyKind.Keyword);
        }

        var positionSyntax = GetSyntaxTree(currentDocument).GetPositionSyntax(line, character);
        if (positionSyntax.CompletionExpectation == VbaCompletionExpectation.None)
        {
            return new VbaCompletionResult([], VbaCompletionVocabularyKind.None);
        }

        if (positionSyntax.MemberAccess is not null
            && (positionSyntax.MemberAccess.TargetSegmentIndex > 0
                || positionSyntax.MemberAccess.IsLeadingDot
                || positionSyntax.MemberAccess.IsIncomplete)
            && TryGetMemberCompletionDefinitions(
                currentDocument,
                line,
                character,
                positionSyntax,
                out var memberDefinitions))
        {
            return new VbaCompletionResult(memberDefinitions, VbaCompletionVocabularyKind.None);
        }

        if (positionSyntax.CompletionExpectation is VbaCompletionExpectation.TypeName
            or VbaCompletionExpectation.CreatableType)
        {
            var typeDefinitions = GetTypeCompletionDefinitions(currentDocument);
            return new VbaCompletionResult(typeDefinitions, VbaCompletionVocabularyKind.TypeName);
        }

        return new VbaCompletionResult(
            nameResolution.GetCompletionDefinitions(uri, new VbaPosition(line, character)),
            VbaCompletionVocabularyKind.Keyword);
    }

    /// <summary>
    /// Resolves the definition referenced at a source position.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="line">The zero-based line.</param>
    /// <param name="character">The zero-based character.</param>
    /// <returns>The resolved source or reference definition, or null when unresolved or ambiguous.</returns>
    public VbaSourceDefinition? ResolveSourceDefinition(string uri, int line, int character)
    {
        var currentDocument = documents.FirstOrDefault(document => SameUri(document.Uri, uri));
        if (currentDocument is null)
        {
            return null;
        }

        var positionSyntax = GetSyntaxTree(currentDocument).GetPositionSyntax(line, character);
        var identifier = positionSyntax.Identifier;
        if (positionSyntax.Region != VbaPositionRegion.Code || identifier is null)
        {
            return null;
        }

        if (positionSyntax.TypeReference is not null
            && typeResolution.TryResolveTypeReferenceDefinition(
                currentDocument,
                positionSyntax.TypeReference,
                identifier,
                out var typeDefinition))
        {
            return typeDefinition;
        }

        if (TryResolveWithEventsHandler(currentDocument, identifier.Name, out var eventDefinition))
        {
            return eventDefinition;
        }

        if (TryResolveMemberDefinition(
            currentDocument,
            line,
            character,
            positionSyntax,
            out var memberDefinition))
        {
            return memberDefinition;
        }

        var qualifier = GetImmediateQualifier(positionSyntax.MemberAccess, identifier);
        return nameResolution.Resolve(uri, new VbaPosition(line, character), qualifier, identifier.Name);
    }

    /// <summary>
    /// Resolves callable signature help at a source position.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="line">The zero-based line.</param>
    /// <param name="character">The zero-based character.</param>
    /// <returns>The signature help result, or null when no callable resolves.</returns>
    public VbaSignatureHelp? GetSignatureHelp(string uri, int line, int character)
    {
        var currentDocument = documents.FirstOrDefault(document => SameUri(document.Uri, uri));
        if (currentDocument is null)
        {
            return null;
        }

        var positionSyntax = GetSyntaxTree(currentDocument).GetPositionSyntax(line, character);
        return callSiteResolution.GetSignatureHelp(currentDocument, line, character, positionSyntax);
    }

    /// <summary>
    /// Resolves the canonical casing for an identifier occurrence during formatting.
    /// </summary>
    /// <param name="occurrence">The identifier occurrence to normalize.</param>
    /// <param name="document">The source document being formatted.</param>
    /// <param name="lineIndex">The zero-based physical line index.</param>
    /// <param name="declarationRanges">The declaration ranges that must not be renamed by formatting.</param>
    /// <param name="canonicalNamesByRange">Snapshot-cached canonical names keyed by resolved occurrence range.</param>
    /// <returns>The canonical name, or null when formatting should leave the occurrence unchanged.</returns>
    public string? GetCanonicalFormattingName(
        VbaIdentifierOccurrence occurrence,
        VbaSourceDocument document,
        int lineIndex,
        IReadOnlySet<string> declarationRanges,
        IReadOnlyDictionary<VbaRange, string> canonicalNamesByRange)
    {
        if (VbaLanguageVocabulary.CanonicalKeywords.TryGetValue(occurrence.Name, out var keyword))
        {
            return keyword;
        }

        var occurrenceRange = new VbaRange(
            new VbaPosition(lineIndex, occurrence.Start),
            new VbaPosition(lineIndex, occurrence.End));
        if (declarationRanges.Contains(GetRangeKey(occurrenceRange)))
        {
            return null;
        }

        if (canonicalNamesByRange.TryGetValue(occurrenceRange, out var resolvedCanonicalName))
        {
            return resolvedCanonicalName;
        }

        var positionSyntax = GetSyntaxTree(document).GetPositionSyntax(lineIndex, occurrence.Start);
        if (TryGetMemberChainCanonicalName(
            positionSyntax,
            occurrence,
            document,
            lineIndex,
            out var memberChainCanonicalName))
        {
            return memberChainCanonicalName;
        }

        if (TryGetQualifiedCanonicalName(
            positionSyntax.MemberAccess,
            occurrence,
            document.Uri,
            lineIndex,
            out var qualifiedCanonicalName))
        {
            return qualifiedCanonicalName;
        }

        if (positionSyntax.MemberAccess is not null)
        {
            return null;
        }

        var definition = nameResolution.Resolve(
            document.Uri,
            new VbaPosition(lineIndex, occurrence.Start),
            qualifier: null,
            occurrence.Name);
        return definition?.Name;
    }

    private bool TryGetMemberCompletionDefinitions(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        VbaPositionSyntax positionSyntax,
        out IReadOnlyList<VbaSourceDefinition> definitions)
    {
        definitions = [];
        if (positionSyntax.MemberAccess is null)
        {
            return false;
        }

        definitions = memberChainResolution.GetMemberCompletions(
            currentDocument,
            line,
            character,
            positionSyntax.MemberAccess,
            positionSyntax.EnclosingWithScopes);
        return true;
    }

    private IReadOnlyList<VbaSourceDefinition> GetTypeCompletionDefinitions(
        VbaSourceDocument currentDocument)
        => typeResolution.GetVisibleTypeDefinitions(currentDocument)
            .GroupBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => typeResolution.ResolveSourceTypeCompletionGroup(group.ToArray()))
            .Where(definition => definition is not null)
            .Select(definition => definition!)
            .OrderBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private bool TryResolveMemberDefinition(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        VbaPositionSyntax positionSyntax,
        out VbaSourceDefinition? definition)
    {
        definition = null;
        if (positionSyntax.MemberAccess is null)
        {
            return false;
        }

        if (!memberChainResolution.TryResolveMemberChainDefinition(
            currentDocument,
            line,
            character,
            positionSyntax.MemberAccess,
            positionSyntax.EnclosingWithScopes,
            out definition))
        {
            return false;
        }

        return true;
    }

    private bool TryResolveWithEventsHandler(
        VbaSourceDocument currentDocument,
        string handlerName,
        out VbaSourceDefinition? eventDefinition)
    {
        eventDefinition = null;
        foreach (var variable in currentDocument.Definitions
            .Where(definition => definition.IsWithEvents)
            .Where(definition => definition.TypeReference is not null)
            .OrderByDescending(definition => definition.Name.Length))
        {
            var prefix = $"{variable.Name}_";
            if (!handlerName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var eventName = handlerName[prefix.Length..];
            if (!typeResolution.TryResolveTypeReference(currentDocument, variable.TypeReference!, out var receiverType))
            {
                return true;
            }

            eventDefinition = memberChainResolution.ResolveEvent(currentDocument, receiverType, eventName);
            return true;
        }

        return false;
    }

    private bool TryGetMemberChainCanonicalName(
        VbaPositionSyntax positionSyntax,
        VbaIdentifierOccurrence occurrence,
        VbaSourceDocument document,
        int lineIndex,
        out string? canonicalName)
    {
        canonicalName = null;
        var access = positionSyntax.MemberAccess;
        if (access is not null
            && (access.TargetSegmentIndex > 0 || access.IsLeadingDot))
        {
            if (!memberChainResolution.TryGetCanonicalMemberName(
                document,
                lineIndex,
                occurrence.Start,
                access,
                positionSyntax.EnclosingWithScopes,
                out canonicalName))
            {
                return false;
            }

            return canonicalName is not null;
        }

        if (access is null
            || access.TargetSegmentIndex != 0
            || access.Segments.Count < 2)
        {
            return false;
        }

        var definition = nameResolution.Resolve(
            document.Uri,
            new VbaPosition(lineIndex, occurrence.Start),
            qualifier: null,
            occurrence.Name);
        canonicalName = definition?.Name;
        return canonicalName is not null;
    }

    private bool TryGetQualifiedCanonicalName(
        VbaMemberAccessSyntax? access,
        VbaIdentifierOccurrence occurrence,
        string uri,
        int lineIndex,
        out string? canonicalName)
    {
        canonicalName = null;
        if (access?.Target is not null && access.TargetSegmentIndex > 0)
        {
            var qualifier = access.Segments[access.TargetSegmentIndex - 1];
            var definition = nameResolution.Resolve(
                uri,
                new VbaPosition(lineIndex, 0),
                qualifier.Name,
                occurrence.Name);
            canonicalName = definition?.Name;
            return canonicalName is not null;
        }

        if (access?.Target is not null
            && access.TargetSegmentIndex == 0
            && access.Segments.Count > 1)
        {
            var member = access.Segments[1];
            var definition = nameResolution.Resolve(
                uri,
                new VbaPosition(lineIndex, 0),
                occurrence.Name,
                member.Name);
            canonicalName = definition is null
                ? null
                : nameResolution.GetCanonicalQualifierName(definition, occurrence.Name);
            return canonicalName is not null;
        }

        return false;
    }

    private static string? GetImmediateQualifier(
        VbaMemberAccessSyntax? access,
        VbaPositionIdentifierSyntax identifier)
        => access?.Target?.Range == identifier.Range && access.TargetSegmentIndex > 0
            ? access.Segments[access.TargetSegmentIndex - 1].Name
            : null;

    private static string GetRangeKey(VbaRange range)
        => $"{range.Start.Line}:{range.Start.Character}:{range.End.Line}:{range.End.Character}";

    private static VbaSyntaxTree GetSyntaxTree(VbaSourceDocument document)
        => document.SyntaxTree ?? VbaSyntaxTree.ParseModule(document.Uri, document.Text);

    private static bool SameUri(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

}
