using System.Text.RegularExpressions;
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
    private readonly VbaProjectReferenceSelection? referenceSelection;
    private readonly VbaProjectReferenceCatalogSet referenceCatalogs;
    private readonly VbaNameResolutionService nameResolution;
    private readonly VbaTypeResolution typeResolution;
    private readonly VbaMemberChainResolution memberChainResolution;
    private readonly VbaMemberChainContextProvider memberChainContextProvider = new();

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
        this.referenceSelection = referenceSelection;
        this.referenceCatalogs = referenceCatalogs;
        resolutionPolicy ??= new VbaResolutionPolicy();
        var activeReferenceDefinitions = referenceSelection is null
            ? []
            : referenceCatalogs.GetActiveDefinitions(referenceSelection);
        nameResolution = new VbaNameResolutionService(
            documents,
            referenceSelection,
            referenceCatalogs,
            activeReferenceDefinitions,
            resolutionPolicy);
        typeResolution = new VbaTypeResolution(
            documents,
            referenceSelection,
            referenceCatalogs,
            activeReferenceDefinitions,
            nameResolution,
            resolutionPolicy);
        memberChainResolution = new VbaMemberChainResolution(typeResolution);
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
        if (currentDocument is not null
            && IsCompletionSuppressedAfterCompletedMemberAccess(currentDocument, line, character))
        {
            return new VbaCompletionResult([], VbaCompletionVocabularyKind.None);
        }

        if (currentDocument is not null
            && TryGetMemberCompletionDefinitions(currentDocument, line, character, out var memberDefinitions))
        {
            return new VbaCompletionResult(memberDefinitions, VbaCompletionVocabularyKind.None);
        }

        if (currentDocument is not null
            && TryGetTypeCompletionDefinitions(currentDocument, line, character, out var typeDefinitions))
        {
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

        var lexicalFacts = VbaLexicalFacts.FromText(currentDocument.Text);
        if (!lexicalFacts.TryGetCodeIdentifierAt(line, character, out var identifier))
        {
            return null;
        }

        if (typeResolution.TryResolveTypeReferenceDefinition(
            currentDocument,
            lexicalFacts.ToLineArray(),
            line,
            identifier,
            out var typeDefinition))
        {
            return typeDefinition;
        }

        if (TryResolveWithEventsHandler(currentDocument, identifier.Name, out var eventDefinition))
        {
            return eventDefinition;
        }

        if (TryResolveMemberDefinition(currentDocument, line, identifier.Start, identifier.Name, out var memberDefinition))
        {
            return memberDefinition;
        }

        if (!lexicalFacts.TryGetLine(line, out var physicalLine))
        {
            return null;
        }

        var qualifier = GetQualifierBefore(physicalLine, identifier.Start);
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

        var lexicalFacts = VbaLexicalFacts.FromText(currentDocument.Text);
        if (!lexicalFacts.TryGetLogicalPrefix(line, character, out var logicalPrefix))
        {
            return null;
        }

        if (!TryResolveCalleeDefinition(currentDocument, line, character, logicalPrefix, out var definition, out var arguments)
            && !TryResolveStatementFormCalleeDefinition(currentDocument, line, character, logicalPrefix, out definition, out arguments))
        {
            return null;
        }

        if (definition?.Signature is null)
        {
            return null;
        }

        var activeParameter = GetActiveSignatureParameter(definition.Signature, arguments);
        return new VbaSignatureHelp(definition.Signature, activeParameter);
    }

    /// <summary>
    /// Resolves the canonical casing for an identifier occurrence during formatting.
    /// </summary>
    /// <param name="codePart">The code portion of the physical source line.</param>
    /// <param name="occurrence">The identifier occurrence to normalize.</param>
    /// <param name="document">The source document being formatted.</param>
    /// <param name="lineIndex">The zero-based physical line index.</param>
    /// <param name="declarationRanges">The declaration ranges that must not be renamed by formatting.</param>
    /// <returns>The canonical name, or null when formatting should leave the occurrence unchanged.</returns>
    public string? GetCanonicalFormattingName(
        string codePart,
        VbaIdentifierOccurrence occurrence,
        VbaSourceDocument document,
        int lineIndex,
        IReadOnlySet<string> declarationRanges)
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

        if (TryGetMemberChainCanonicalName(
            codePart,
            occurrence,
            document,
            lineIndex,
            out var memberChainCanonicalName))
        {
            return memberChainCanonicalName;
        }

        if (TryGetQualifiedCanonicalName(
            codePart,
            occurrence,
            document.Uri,
            lineIndex,
            out var qualifiedCanonicalName))
        {
            return qualifiedCanonicalName;
        }

        if (VbaMemberExpressionSyntax.IsQualifiedIdentifierOccurrence(codePart, occurrence))
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
        out IReadOnlyList<VbaSourceDefinition> definitions)
    {
        definitions = [];
        if (!memberChainContextProvider.TryGetCompletionContext(
            currentDocument,
            line,
            character,
            out var context))
        {
            return false;
        }

        if (!memberChainResolution.TryResolveExpressionType(
            currentDocument,
            line,
            character,
            context.ReceiverExpression,
            out var receiverType))
        {
            return true;
        }

        definitions = memberChainResolution.GetMembersOfType(receiverType);
        return true;
    }

    private bool IsCompletionSuppressedAfterCompletedMemberAccess(
        VbaSourceDocument currentDocument,
        int line,
        int character)
        => memberChainContextProvider.IsCompletedMemberAccessWithTrailingWhitespace(
            currentDocument,
            line,
            character);

    private bool TryGetTypeCompletionDefinitions(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        out IReadOnlyList<VbaSourceDefinition> definitions)
    {
        definitions = [];
        var lexicalFacts = VbaLexicalFacts.FromText(currentDocument.Text);
        if (!lexicalFacts.TryGetLogicalPrefix(line, character, out var logicalPrefix))
        {
            return false;
        }

        if (!IsTypeAnnotationCompletionPrefix(logicalPrefix))
        {
            return false;
        }

        definitions = typeResolution.GetVisibleTypeDefinitions(currentDocument)
            .GroupBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => typeResolution.ResolveSourceTypeCompletionGroup(group.ToArray()))
            .Where(definition => definition is not null)
            .Select(definition => definition!)
            .OrderBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return true;
    }

    private bool TryResolveMemberDefinition(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        string memberName,
        out VbaSourceDefinition? definition)
    {
        definition = null;
        if (!memberChainContextProvider.TryGetMemberReferenceContext(
            currentDocument,
            line,
            character,
            memberName,
            out var context))
        {
            return false;
        }

        if (!memberChainResolution.TryResolveExpressionType(
            currentDocument,
            line,
            character,
            context.ReceiverExpression,
            out var receiverType))
        {
            return context.IsWithReceiver;
        }

        definition = memberChainResolution.ResolveMember(receiverType, memberName);
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

            eventDefinition = memberChainResolution.ResolveEvent(receiverType, eventName);
            return true;
        }

        return false;
    }

    private bool TryResolveCalleeDefinition(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        string logicalPrefix,
        out VbaSourceDefinition? definition,
        out string arguments)
    {
        definition = null;
        arguments = "";
        if (!memberChainContextProvider.TryGetCallExpressionContext(logicalPrefix, out var context))
        {
            return false;
        }

        arguments = context.Arguments;
        if (context.MemberChain is not null)
        {
            if (memberChainResolution.TryResolveExpressionType(
                currentDocument,
                line,
                character,
                context.MemberChain.ReceiverExpression,
                out var receiverType))
            {
                definition = memberChainResolution.ResolveMember(receiverType, context.MemberChain.MemberName!);
                return true;
            }

            if (context.MemberChain.IsWithReceiver)
            {
                return true;
            }
        }

        definition = nameResolution.Resolve(
            currentDocument.Uri,
            new VbaPosition(line, character),
            context.Qualifier,
            context.UnqualifiedName);
        return true;
    }

    private bool TryResolveStatementFormCalleeDefinition(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        string logicalPrefix,
        out VbaSourceDefinition? definition,
        out string arguments)
    {
        definition = null;
        arguments = "";
        if (!memberChainContextProvider.TryGetStatementFormCallContext(logicalPrefix, out var context))
        {
            return false;
        }

        arguments = context.Arguments;
        if (context.MemberChain is not null)
        {
            if (memberChainResolution.TryResolveExpressionType(
                currentDocument,
                line,
                character,
                context.MemberChain.ReceiverExpression,
                out var receiverType))
            {
                definition = memberChainResolution.ResolveMember(receiverType, context.MemberChain.MemberName!);
                return true;
            }

            if (context.MemberChain.IsWithReceiver)
            {
                return true;
            }
        }

        definition = nameResolution.Resolve(
            currentDocument.Uri,
            new VbaPosition(line, character),
            context.Qualifier,
            context.UnqualifiedName);
        return true;
    }

    private bool TryGetMemberChainCanonicalName(
        string codePart,
        VbaIdentifierOccurrence occurrence,
        VbaSourceDocument document,
        int lineIndex,
        out string? canonicalName)
    {
        canonicalName = null;
        if (memberChainContextProvider.TryGetPreviousMemberContext(codePart, occurrence, out var context))
        {
            if (!memberChainResolution.TryResolveExpressionType(
                document,
                lineIndex,
                occurrence.Start,
                context.ReceiverExpression,
                out var receiverType))
            {
                return false;
            }

            var member = memberChainResolution.ResolveMember(receiverType, occurrence.Name);
            canonicalName = member?.Name;
            return canonicalName is not null;
        }

        if (!VbaMemberExpressionSyntax.TryGetNextMember(codePart, occurrence, out _))
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
        string codePart,
        VbaIdentifierOccurrence occurrence,
        string uri,
        int lineIndex,
        out string? canonicalName)
    {
        canonicalName = null;
        if (VbaMemberExpressionSyntax.TryGetPreviousQualifier(codePart, occurrence, out var qualifier))
        {
            var definition = nameResolution.Resolve(
                uri,
                new VbaPosition(lineIndex, 0),
                qualifier.Name,
                occurrence.Name);
            canonicalName = definition?.Name;
            return canonicalName is not null;
        }

        if (VbaMemberExpressionSyntax.TryGetNextMember(codePart, occurrence, out var member))
        {
            var definition = nameResolution.Resolve(
                uri,
                new VbaPosition(lineIndex, 0),
                occurrence.Name,
                member.Name);
            canonicalName = definition is null
                ? null
                : GetCanonicalQualifierName(definition, occurrence.Name);
            return canonicalName is not null;
        }

        return false;
    }

    private string? GetCanonicalQualifierName(VbaSourceDefinition definition, string qualifier)
    {
        if (!VbaProjectReferenceCatalogSet.IsExternalDefinition(definition))
        {
            return definition.ModuleName;
        }

        return referenceSelection is null
            ? null
            : referenceCatalogs.GetActiveCanonicalQualifierAlias(referenceSelection, definition.ModuleName, qualifier);
    }

    private static int GetActiveSignatureParameter(VbaCallableSignature signature, string arguments)
    {
        var fallbackParameter = Math.Min(
            arguments.Count(characterValue => characterValue == ','),
            Math.Max(0, signature.Parameters.Count - 1));
        var currentArgumentStart = arguments.LastIndexOf(',') + 1;
        var currentArgument = arguments[currentArgumentStart..];
        var namedArgumentMatch = Regex.Match(
            currentArgument,
            "^\\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\\s*:=",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!namedArgumentMatch.Success)
        {
            return fallbackParameter;
        }

        var parameterIndex = signature.Parameters
            .Select((parameter, index) => new { parameter, index })
            .FirstOrDefault(item => item.parameter.Name.Equals(
                namedArgumentMatch.Groups["name"].Value,
                StringComparison.OrdinalIgnoreCase))
            ?.index;
        return parameterIndex ?? fallbackParameter;
    }

    private static bool IsTypeAnnotationCompletionPrefix(string logicalPrefix)
    {
        return Regex.IsMatch(
            logicalPrefix,
            "\\bAs\\s+(?:(?:[A-Za-z_][A-Za-z0-9_]*)\\s*\\.\\s*)?[A-Za-z_][A-Za-z0-9_]*$|\\bAs\\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string? GetQualifierBefore(string line, int identifierStart)
    {
        var dotIndex = identifierStart - 1;
        while (dotIndex >= 0 && char.IsWhiteSpace(line[dotIndex]))
        {
            dotIndex--;
        }

        if (dotIndex < 0 || line[dotIndex] != '.')
        {
            return null;
        }

        var qualifierEnd = dotIndex - 1;
        while (qualifierEnd >= 0 && char.IsWhiteSpace(line[qualifierEnd]))
        {
            qualifierEnd--;
        }

        if (qualifierEnd < 0 || !VbaSourceText.IsIdentifierCharacter(line[qualifierEnd]))
        {
            return null;
        }

        var qualifierStart = qualifierEnd;
        while (qualifierStart > 0 && VbaSourceText.IsIdentifierCharacter(line[qualifierStart - 1]))
        {
            qualifierStart--;
        }

        return line[qualifierStart..(qualifierEnd + 1)];
    }

    private static string GetRangeKey(VbaRange range)
        => $"{range.Start.Line}:{range.Start.Character}:{range.End.Line}:{range.End.Character}";

    private static bool SameUri(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

}
