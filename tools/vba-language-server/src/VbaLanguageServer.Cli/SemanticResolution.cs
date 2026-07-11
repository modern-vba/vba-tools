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

    /// <summary>
    /// Creates the semantic resolution service.
    /// </summary>
    /// <param name="documents">The indexed source documents.</param>
    /// <param name="referenceSelection">The active reference selection for the project.</param>
    /// <param name="referenceCatalogs">The available reference catalogs.</param>
    public VbaSemanticResolution(
        IReadOnlyList<VbaSourceDocument> documents,
        VbaProjectReferenceSelection? referenceSelection,
        VbaProjectReferenceCatalogSet referenceCatalogs)
    {
        this.documents = documents;
        this.referenceSelection = referenceSelection;
        this.referenceCatalogs = referenceCatalogs;
        nameResolution = new VbaNameResolutionService(documents, referenceSelection, referenceCatalogs);
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

        var lines = VbaSourceText.SplitLines(currentDocument.Text);
        if (line < 0 || line >= lines.Length)
        {
            return null;
        }

        if (!VbaSourceText.IsCodePosition(lines[line], character))
        {
            return null;
        }

        var identifier = VbaSourceText.GetIdentifierAt(lines[line], character);
        if (identifier is null)
        {
            return null;
        }

        if (TryResolveWithEventsHandler(currentDocument, identifier.Name, out var eventDefinition))
        {
            return eventDefinition;
        }

        if (TryResolveMemberDefinition(currentDocument, line, identifier.Start, identifier.Name, out var memberDefinition))
        {
            return memberDefinition;
        }

        var qualifier = GetQualifierBefore(lines[line], identifier.Start);
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

        var lines = VbaSourceText.SplitLines(currentDocument.Text);
        if (line < 0 || line >= lines.Length)
        {
            return null;
        }

        var logicalPrefix = VbaSourceText.GetLogicalPrefix(lines, line, character);
        if (!TryResolveCalleeDefinition(currentDocument, line, character, logicalPrefix, out var definition, out var arguments))
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

        if (IsQualifiedIdentifierOccurrence(codePart, occurrence))
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
        var lines = VbaSourceText.SplitLines(currentDocument.Text);
        if (line < 0 || line >= lines.Length)
        {
            return false;
        }

        var logicalPrefix = VbaSourceText.GetLogicalPrefix(lines, line, character);
        if (!TryGetMemberReceiverExpression(logicalPrefix, out var receiverExpression))
        {
            return false;
        }

        if (!TryResolveExpressionType(currentDocument, line, character, receiverExpression, out var receiverType))
        {
            return true;
        }

        definitions = GetMembersOfType(receiverType);
        return true;
    }

    private bool IsCompletionSuppressedAfterCompletedMemberAccess(
        VbaSourceDocument currentDocument,
        int line,
        int character)
    {
        var lines = VbaSourceText.SplitLines(currentDocument.Text);
        if (line < 0 || line >= lines.Length)
        {
            return false;
        }

        var logicalPrefix = VbaSourceText.GetLogicalPrefix(lines, line, character);
        return IsCompletedMemberAccessWithTrailingWhitespace(logicalPrefix);
    }

    private bool TryGetTypeCompletionDefinitions(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        out IReadOnlyList<VbaSourceDefinition> definitions)
    {
        definitions = [];
        var lines = VbaSourceText.SplitLines(currentDocument.Text);
        if (line < 0 || line >= lines.Length)
        {
            return false;
        }

        var logicalPrefix = VbaSourceText.GetLogicalPrefix(lines, line, character);
        if (!IsTypeAnnotationCompletionPrefix(logicalPrefix))
        {
            return false;
        }

        definitions = GetVisibleTypeDefinitions(currentDocument)
            .GroupBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => ResolveSourceTypeCompletionGroup(group.ToArray()))
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
        var lines = VbaSourceText.SplitLines(currentDocument.Text);
        if (line < 0 || line >= lines.Length)
        {
            return false;
        }

        var logicalPrefix = VbaSourceText.GetLogicalPrefix(lines, line, character);
        if (!TryGetMemberReceiverExpression(logicalPrefix, out var receiverExpression))
        {
            return false;
        }

        if (!TryResolveExpressionType(currentDocument, line, character, receiverExpression, out var receiverType))
        {
            return receiverExpression.Trim().Equals(".", StringComparison.Ordinal);
        }

        definition = ResolveMember(receiverType, memberName);
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
            if (!TryResolveTypeReference(currentDocument, variable.TypeReference!, out var receiverType))
            {
                return true;
            }

            eventDefinition = ResolveEvent(receiverType, eventName);
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
        var callMatch = Regex.Matches(
                logicalPrefix,
                "(?<callee>(?:\\.|[A-Za-z_][A-Za-z0-9_]*)(?:\\s*\\.\\s*[A-Za-z_][A-Za-z0-9_]*)*)\\s*\\((?<arguments>[^()]*)$",
                RegexOptions.CultureInvariant)
            .Cast<Match>()
            .LastOrDefault();
        if (callMatch is null)
        {
            return false;
        }

        arguments = callMatch.Groups["arguments"].Value;
        var callee = NormalizeMemberExpression(callMatch.Groups["callee"].Value);
        if (TrySplitMemberExpression(callee, out var receiverExpression, out var memberName))
        {
            if (TryResolveExpressionType(currentDocument, line, character, receiverExpression, out var receiverType))
            {
                definition = ResolveMember(receiverType, memberName);
                return true;
            }

            if (receiverExpression.Equals(".", StringComparison.Ordinal))
            {
                return true;
            }
        }

        var qualifier = GetQualifierFromCallee(callee);
        var unqualifiedName = qualifier is null ? callee : callee[(qualifier.Length + 1)..];
        definition = nameResolution.Resolve(
            currentDocument.Uri,
            new VbaPosition(line, character),
            qualifier,
            unqualifiedName);
        return true;
    }

    private bool TryResolveExpressionType(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        string expression,
        out ResolvedType resolvedType,
        IReadOnlyList<ResolvedType>? withReceivers = null)
    {
        resolvedType = default!;
        var normalized = NormalizeMemberExpression(expression);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        var parts = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (normalized.StartsWith(".", StringComparison.Ordinal))
        {
            if (withReceivers is { Count: > 0 })
            {
                resolvedType = withReceivers[^1];
            }
            else if (!TryGetWithReceiverType(currentDocument, line, character, out resolvedType))
            {
                return false;
            }

            foreach (var memberName in parts)
            {
                var member = ResolveMember(resolvedType, memberName);
                if (member?.TypeReference is null || !TryResolveTypeReference(currentDocument, member.TypeReference, out resolvedType))
                {
                    return false;
                }
            }

            return true;
        }

        if (parts.Length == 0)
        {
            return false;
        }

        if (parts.Length >= 2 && TryResolveTypeReference(currentDocument, new VbaTypeReference(parts[1], parts[0]), out resolvedType))
        {
            for (var index = 2; index < parts.Length; index++)
            {
                var member = ResolveMember(resolvedType, parts[index]);
                if (member?.TypeReference is null || !TryResolveTypeReference(currentDocument, member.TypeReference, out resolvedType))
                {
                    return false;
                }
            }

            return true;
        }

        var firstDefinition = nameResolution.Resolve(
            currentDocument.Uri,
            new VbaPosition(line, character),
            qualifier: null,
            parts[0]);
        if (firstDefinition?.TypeReference is not null)
        {
            if (!TryResolveTypeReference(currentDocument, firstDefinition.TypeReference, out resolvedType))
            {
                return false;
            }
        }
        else if (firstDefinition is not null && IsTypeDefinition(firstDefinition))
        {
            resolvedType = ToResolvedType(firstDefinition);
        }
        else
        {
            return false;
        }

        for (var index = 1; index < parts.Length; index++)
        {
            var member = ResolveMember(resolvedType, parts[index]);
            if (member?.TypeReference is null || !TryResolveTypeReference(currentDocument, member.TypeReference, out resolvedType))
            {
                return false;
            }
        }

        return true;
    }

    private bool TryResolveTypeReference(
        VbaSourceDocument currentDocument,
        VbaTypeReference typeReference,
        out ResolvedType resolvedType)
    {
        resolvedType = default!;
        if (!string.IsNullOrWhiteSpace(typeReference.Qualifier))
        {
            var referenceType = ResolveReferenceType(typeReference.Qualifier, typeReference.Name);
            if (referenceType is not null)
            {
                resolvedType = referenceType;
                return true;
            }

            var qualifiedSourceType = ResolveSourceType(typeReference.Name, typeReference.Qualifier);
            if (qualifiedSourceType is not null)
            {
                resolvedType = qualifiedSourceType;
                return true;
            }

            return false;
        }

        var sourceType = ResolveSourceType(typeReference.Name, qualifier: null);
        if (sourceType is not null)
        {
            resolvedType = sourceType;
            return true;
        }

        var referenceCandidates = GetActiveReferenceDefinitions()
            .Where(definition => SameName(definition.Name, typeReference.Name))
            .Where(IsTypeDefinition)
            .Where(definition => definition.ParentTypeName is null)
            .ToArray();
        var referenceTypeDefinition = ResolveReferenceCandidates(referenceCandidates);
        if (referenceTypeDefinition is null)
        {
            return false;
        }

        resolvedType = ToResolvedType(referenceTypeDefinition);
        return true;
    }

    private bool TryGetWithReceiverType(
        VbaSourceDocument currentDocument,
        int targetLine,
        int character,
        out ResolvedType resolvedType)
    {
        resolvedType = default!;
        var lines = VbaSourceText.SplitLines(currentDocument.Text);
        var stack = new List<ResolvedType>();
        for (var lineIndex = 0; lineIndex < targetLine && lineIndex < lines.Length; lineIndex++)
        {
            var statement = VbaSourceText.GetLogicalStatement(lines, lineIndex, out var endLine);
            if (endLine >= targetLine)
            {
                break;
            }

            var trimmed = statement.Trim();
            if (Regex.IsMatch(trimmed, "^End\\s+With\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                if (stack.Count > 0)
                {
                    stack.RemoveAt(stack.Count - 1);
                }

                lineIndex = endLine;
                continue;
            }

            var withMatch = Regex.Match(
                trimmed,
                "^With\\s+(?<expression>.+)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (withMatch.Success
                && TryResolveExpressionType(
                    currentDocument,
                    endLine,
                    lines[Math.Min(endLine, lines.Length - 1)].Length,
                    withMatch.Groups["expression"].Value,
                    out var withType,
                    stack))
            {
                stack.Add(withType);
            }

            lineIndex = endLine;
        }

        if (stack.Count == 0)
        {
            return false;
        }

        resolvedType = stack[^1];
        return true;
    }

    private IReadOnlyList<VbaSourceDefinition> GetMembersOfType(ResolvedType resolvedType)
        => GetMemberCandidates(resolvedType)
            .GroupBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .Select(group => group.Single())
            .OrderBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private VbaSourceDefinition? ResolveMember(ResolvedType resolvedType, string memberName)
    {
        var candidates = GetMemberCandidates(resolvedType)
            .Where(definition => SameName(definition.Name, memberName))
            .ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private VbaSourceDefinition? ResolveEvent(ResolvedType resolvedType, string eventName)
    {
        var candidates = GetMemberCandidates(resolvedType)
            .Where(definition => definition.Kind == VbaSourceDefinitionKind.Event)
            .Where(definition => SameName(definition.Name, eventName))
            .ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private IEnumerable<VbaSourceDefinition> GetMemberCandidates(ResolvedType resolvedType)
    {
        if (resolvedType.ReferenceName is not null)
        {
            return GetActiveReferenceDefinitions()
                .Where(definition => SameName(definition.ModuleName, resolvedType.ReferenceName))
                .Where(definition => definition.ParentTypeName is not null)
                .Where(definition => SameName(definition.ParentTypeName!, resolvedType.Name));
        }

        return documents
            .Where(document => SameName(document.ModuleName, resolvedType.Name))
            .SelectMany(document => document.Definitions)
            .Where(IsReferenceTarget);
    }

    private IEnumerable<VbaSourceDefinition> GetVisibleTypeDefinitions(VbaSourceDocument currentDocument)
    {
        foreach (var definition in currentDocument.Definitions.Where(IsTypeDefinition))
        {
            yield return definition;
        }

        foreach (var definition in documents
            .Where(document => !SameUri(document.Uri, currentDocument.Uri))
            .SelectMany(document => document.Definitions)
            .Where(IsTypeDefinition)
            .Where(definition => definition.Visibility == VbaSourceDefinitionVisibility.Public))
        {
            yield return definition;
        }

        foreach (var definition in GetActiveReferenceDefinitions()
            .Where(IsTypeDefinition)
            .Where(definition => definition.ParentTypeName is null))
        {
            yield return definition;
        }
    }

    private ResolvedType? ResolveReferenceType(string qualifier, string typeName)
    {
        if (referenceSelection is null)
        {
            return null;
        }

        var candidates = referenceCatalogs
            .GetQualifiedDefinitions(referenceSelection, qualifier, typeName)
            .Where(IsTypeDefinition)
            .Where(definition => definition.ParentTypeName is null)
            .ToArray();
        var definition = ResolveReferenceCandidates(candidates);
        return definition is null ? null : ToResolvedType(definition);
    }

    private ResolvedType? ResolveSourceType(string typeName, string? qualifier)
    {
        var candidates = documents
            .SelectMany(document => document.Definitions)
            .Where(IsTypeDefinition)
            .Where(definition => SameName(definition.Name, typeName))
            .Where(definition => qualifier is null || SameName(definition.ModuleName, qualifier))
            .ToArray();
        return candidates.Length == 1 ? ToResolvedType(candidates[0]) : null;
    }

    private VbaSourceDefinition? ResolveSourceTypeCompletionGroup(IReadOnlyList<VbaSourceDefinition> candidates)
    {
        var sourceCandidates = candidates
            .Where(definition => !VbaProjectReferenceCatalogSet.IsExternalDefinition(definition))
            .ToArray();
        if (sourceCandidates.Length == 1)
        {
            return sourceCandidates[0];
        }

        if (sourceCandidates.Length > 1)
        {
            return null;
        }

        return ResolveReferenceCandidates(candidates);
    }

    private VbaSourceDefinition? ResolveReferenceCandidates(IReadOnlyList<VbaSourceDefinition> candidates)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        if (referenceSelection?.MainVbaProjectReference is not null)
        {
            var mainCandidates = candidates
                .Where(definition => SameName(definition.ModuleName, referenceSelection.MainVbaProjectReference.Name))
                .ToArray();
            if (mainCandidates.Length == 1)
            {
                return mainCandidates[0];
            }
        }

        return null;
    }

    private IReadOnlyList<VbaSourceDefinition> GetActiveReferenceDefinitions()
        => referenceSelection is null
            ? []
            : referenceCatalogs.GetActiveDefinitions(referenceSelection);

    private bool TryGetMemberChainCanonicalName(
        string codePart,
        VbaIdentifierOccurrence occurrence,
        VbaSourceDocument document,
        int lineIndex,
        out string? canonicalName)
    {
        canonicalName = null;
        if (TryGetPreviousMemberReceiverExpression(codePart, occurrence, out var receiverExpression))
        {
            if (!TryResolveExpressionType(document, lineIndex, occurrence.Start, receiverExpression, out var receiverType))
            {
                return false;
            }

            var member = ResolveMember(receiverType, occurrence.Name);
            canonicalName = member?.Name;
            return canonicalName is not null;
        }

        if (!TryGetNextMember(codePart, occurrence, out _))
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
        if (TryGetPreviousQualifier(codePart, occurrence, out var qualifier))
        {
            var definition = nameResolution.Resolve(
                uri,
                new VbaPosition(lineIndex, 0),
                qualifier.Name,
                occurrence.Name);
            canonicalName = definition?.Name;
            return canonicalName is not null;
        }

        if (TryGetNextMember(codePart, occurrence, out var member))
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

    private static bool TryGetMemberReceiverExpression(string logicalPrefix, out string receiverExpression)
    {
        receiverExpression = "";
        var trimmed = logicalPrefix;
        if (string.IsNullOrEmpty(trimmed) || char.IsWhiteSpace(trimmed[^1]))
        {
            return false;
        }

        var partialMatch = Regex.Match(
            trimmed,
            "[A-Za-z_][A-Za-z0-9_]*$",
            RegexOptions.CultureInvariant);
        if (partialMatch.Success)
        {
            trimmed = trimmed[..partialMatch.Index].TrimEnd();
        }

        if (!trimmed.EndsWith(".", StringComparison.Ordinal))
        {
            return false;
        }

        var beforeDot = trimmed[..^1].TrimEnd();
        if (string.IsNullOrWhiteSpace(beforeDot))
        {
            receiverExpression = ".";
            return true;
        }

        var match = Regex.Match(
            beforeDot,
            "(?<expression>(?:\\.|[A-Za-z_][A-Za-z0-9_]*)(?:\\s*\\.\\s*[A-Za-z_][A-Za-z0-9_]*)*)$",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return false;
        }

        receiverExpression = match.Groups["expression"].Value;
        return true;
    }

    private static bool IsCompletedMemberAccessWithTrailingWhitespace(string logicalPrefix)
    {
        if (string.IsNullOrEmpty(logicalPrefix) || !char.IsWhiteSpace(logicalPrefix[^1]))
        {
            return false;
        }

        var trimmed = logicalPrefix.TrimEnd();
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        if (trimmed.EndsWith(".", StringComparison.Ordinal))
        {
            return TryGetMemberReceiverExpression(trimmed, out _);
        }

        return Regex.IsMatch(
            trimmed,
            "(?:\\.|[A-Za-z_][A-Za-z0-9_]*)(?:\\s*\\.\\s*[A-Za-z_][A-Za-z0-9_]*)+$",
            RegexOptions.CultureInvariant);
    }

    private static bool IsTypeAnnotationCompletionPrefix(string logicalPrefix)
    {
        return Regex.IsMatch(
            logicalPrefix,
            "\\bAs\\s+(?:(?:[A-Za-z_][A-Za-z0-9_]*)\\s*\\.\\s*)?[A-Za-z_][A-Za-z0-9_]*$|\\bAs\\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool TrySplitMemberExpression(
        string expression,
        out string receiverExpression,
        out string memberName)
    {
        receiverExpression = "";
        memberName = "";
        var normalized = NormalizeMemberExpression(expression);
        var lastDot = normalized.LastIndexOf('.');
        if (lastDot < 0)
        {
            return false;
        }

        receiverExpression = lastDot == 0 ? "." : normalized[..lastDot];
        memberName = normalized[(lastDot + 1)..];
        return !string.IsNullOrWhiteSpace(memberName);
    }

    private static bool TryGetPreviousMemberReceiverExpression(
        string codePart,
        VbaIdentifierOccurrence occurrence,
        out string receiverExpression)
    {
        receiverExpression = "";
        var dotIndex = occurrence.Start - 1;
        while (dotIndex >= 0 && char.IsWhiteSpace(codePart[dotIndex]))
        {
            dotIndex--;
        }

        if (dotIndex < 0 || codePart[dotIndex] != '.')
        {
            return false;
        }

        receiverExpression = codePart[..dotIndex].Trim();
        return !string.IsNullOrWhiteSpace(receiverExpression);
    }

    private static bool IsQualifiedIdentifierOccurrence(string codePart, VbaIdentifierOccurrence occurrence)
    {
        return TryGetPreviousQualifier(codePart, occurrence, out _)
            || TryGetNextMember(codePart, occurrence, out _);
    }

    private static bool TryGetPreviousQualifier(
        string codePart,
        VbaIdentifierOccurrence occurrence,
        out VbaIdentifierOccurrence qualifier)
    {
        qualifier = new VbaIdentifierOccurrence("", 0, 0);
        var dotIndex = occurrence.Start - 1;
        while (dotIndex >= 0 && char.IsWhiteSpace(codePart[dotIndex]))
        {
            dotIndex--;
        }

        if (dotIndex < 0 || codePart[dotIndex] != '.')
        {
            return false;
        }

        var qualifierEnd = dotIndex - 1;
        while (qualifierEnd >= 0 && char.IsWhiteSpace(codePart[qualifierEnd]))
        {
            qualifierEnd--;
        }

        if (qualifierEnd < 0 || !VbaSourceText.IsIdentifierCharacter(codePart[qualifierEnd]))
        {
            return false;
        }

        var qualifierStart = qualifierEnd;
        while (qualifierStart > 0 && VbaSourceText.IsIdentifierCharacter(codePart[qualifierStart - 1]))
        {
            qualifierStart--;
        }

        qualifier = new VbaIdentifierOccurrence(
            codePart[qualifierStart..(qualifierEnd + 1)],
            qualifierStart,
            qualifierEnd + 1);
        return true;
    }

    private static bool TryGetNextMember(
        string codePart,
        VbaIdentifierOccurrence occurrence,
        out VbaIdentifierOccurrence member)
    {
        member = new VbaIdentifierOccurrence("", 0, 0);
        var dotIndex = occurrence.End;
        while (dotIndex < codePart.Length && char.IsWhiteSpace(codePart[dotIndex]))
        {
            dotIndex++;
        }

        if (dotIndex >= codePart.Length || codePart[dotIndex] != '.')
        {
            return false;
        }

        var memberStart = dotIndex + 1;
        while (memberStart < codePart.Length && char.IsWhiteSpace(codePart[memberStart]))
        {
            memberStart++;
        }

        if (memberStart >= codePart.Length || !VbaSourceText.IsIdentifierStart(codePart[memberStart]))
        {
            return false;
        }

        var memberEnd = memberStart + 1;
        while (memberEnd < codePart.Length && VbaSourceText.IsIdentifierCharacter(codePart[memberEnd]))
        {
            memberEnd++;
        }

        member = new VbaIdentifierOccurrence(codePart[memberStart..memberEnd], memberStart, memberEnd);
        return true;
    }

    private static string? GetQualifierFromCallee(string callee)
    {
        var normalized = NormalizeMemberExpression(callee);
        var lastDot = normalized.LastIndexOf('.');
        return lastDot < 0 ? null : normalized[..lastDot];
    }

    private static string NormalizeMemberExpression(string expression)
        => Regex.Replace(expression, "\\s+", "", RegexOptions.CultureInvariant);

    private static bool IsTypeDefinition(VbaSourceDefinition definition)
        => definition.Kind is VbaSourceDefinitionKind.Class
            or VbaSourceDefinitionKind.Form
            or VbaSourceDefinitionKind.Type;

    private static ResolvedType ToResolvedType(VbaSourceDefinition definition)
        => new(
            definition.Name,
            VbaProjectReferenceCatalogSet.IsExternalDefinition(definition)
                ? definition.ModuleName
                : null);

    private static bool IsReferenceTarget(VbaSourceDefinition definition)
        => definition.Visibility != VbaSourceDefinitionVisibility.Local
            && definition.Kind != VbaSourceDefinitionKind.Module
            && definition.Kind != VbaSourceDefinitionKind.Class
            && definition.Kind != VbaSourceDefinitionKind.Form;

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

    private static bool SameName(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private sealed record ResolvedType(string Name, string? ReferenceName);
}
