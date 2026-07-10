using System.Text.RegularExpressions;
using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.Parsing;
using VbaLanguageServer.ProjectModel;

namespace VbaLanguageServer.SourceModel;

public enum VbaSourceDefinitionKind
{
    Module,
    Class,
    Form,
    Procedure,
    Property,
    Constant,
    Variable,
    Parameter,
    Enum,
    EnumMember,
    Type,
    TypeMember,
    Event
}

public enum VbaSourceDefinitionVisibility
{
    Public,
    Private,
    Local
}

public sealed record VbaTypeReference(string Name, string? Qualifier = null);

public sealed record VbaSourceDefinition(
    string Name,
    VbaSourceDefinitionKind Kind,
    VbaSourceDefinitionVisibility Visibility,
    string Uri,
    string ModuleName,
    VbaRange Range,
    string? ParentProcedureName = null,
    VbaRange? ParentProcedureRange = null,
    string? Documentation = null,
    VbaCallableSignature? Signature = null,
    string? ParentTypeName = null,
    VbaTypeReference? TypeReference = null,
    bool IsWithEvents = false);

public sealed record VbaCallableParameter(string Name, string? Documentation = null);

public sealed record VbaCallableSignature(
    string Label,
    IReadOnlyList<VbaCallableParameter> Parameters,
    string? Documentation = null);

public sealed record VbaSignatureHelp(VbaCallableSignature Signature, int ActiveParameter);

public sealed record VbaSourceDocument(
    string Uri,
    string Text,
    string ModuleName,
    IReadOnlyList<VbaSourceDefinition> Definitions);

public sealed record VbaDefinitionLocation(string Uri, VbaRange Range);

public sealed record VbaTextEdit(VbaRange Range, string NewText);

public sealed record VbaWorkspaceSymbol(
    string Name,
    VbaSourceDefinitionKind Kind,
    string Uri,
    VbaRange Range);

public sealed record VbaSemanticToken(
    VbaRange Range,
    string Text,
    string TokenType,
    IReadOnlyList<string> TokenModifiers);

public sealed class VbaSourceIndex
{
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
        "enumMember",
        "event",
        "function",
        "method"
    ];

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

    private static readonly IReadOnlyDictionary<string, int> SemanticTokenTypeIndexes =
        SemanticTokenTypes
            .Select((tokenType, index) => new { tokenType, index })
            .ToDictionary(item => item.tokenType, item => item.index, StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, int> SemanticTokenModifierIndexes =
        SemanticTokenModifiers
            .Select((modifier, index) => new { modifier, index })
            .ToDictionary(item => item.modifier, item => item.index, StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, string> LanguageKeywords =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["as"] = "As",
            ["byref"] = "ByRef",
            ["byval"] = "ByVal",
            ["const"] = "Const",
            ["dim"] = "Dim",
            ["else"] = "Else",
            ["elseif"] = "ElseIf",
            ["end"] = "End",
            ["enum"] = "Enum",
            ["explicit"] = "Explicit",
            ["false"] = "False",
            ["for"] = "For",
            ["function"] = "Function",
            ["global"] = "Global",
            ["if"] = "If",
            ["next"] = "Next",
            ["nothing"] = "Nothing",
            ["option"] = "Option",
            ["private"] = "Private",
            ["property"] = "Property",
            ["public"] = "Public",
            ["set"] = "Set",
            ["string"] = "String",
            ["sub"] = "Sub",
            ["then"] = "Then",
            ["true"] = "True",
            ["type"] = "Type",
            ["while"] = "While",
            ["with"] = "With"
        };

    public static readonly IReadOnlyList<string> LanguageVocabulary = [
        "As",
        "ByRef",
        "ByVal",
        "Const",
        "Dim",
        "Else",
        "ElseIf",
        "End",
        "Enum",
        "Explicit",
        "False",
        "For",
        "Function",
        "Global",
        "If",
        "Next",
        "Nothing",
        "Option",
        "Private",
        "Property",
        "Public",
        "Set",
        "String",
        "Sub",
        "Then",
        "True",
        "Type",
        "While",
        "With"
    ];

    private readonly IReadOnlyList<VbaSourceDocument> documents;
    private readonly VbaProjectReferenceSelection? referenceSelection;
    private readonly VbaProjectReferenceCatalogSet referenceCatalogs;

    private VbaSourceIndex(
        IReadOnlyList<VbaSourceDocument> documents,
        VbaProjectReferenceSelection? referenceSelection,
        VbaProjectReferenceCatalogSet referenceCatalogs)
    {
        this.documents = documents;
        this.referenceSelection = referenceSelection;
        this.referenceCatalogs = referenceCatalogs;
    }

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

    public static VbaSourceIndex BuildFromSyntaxTrees(
        IReadOnlyDictionary<string, VbaModuleSyntaxTree> sourceDocuments,
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

    public IReadOnlyList<VbaSourceDefinition> GetDocumentDefinitions(string uri)
        => documents
            .FirstOrDefault(document => SameUri(document.Uri, uri))
            ?.Definitions
            ?? Array.Empty<VbaSourceDefinition>();

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

    public IReadOnlyList<VbaDefinitionLocation> FindReferences(string uri, int line, int character)
    {
        var target = ResolveSourceDefinition(uri, line, character);
        if (target is null)
        {
            return [];
        }

        var references = new List<VbaDefinitionLocation>();
        foreach (var document in documents)
        {
            var lines = SplitLines(document.Text);
            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                foreach (var occurrence in FindIdentifierOccurrences(lines[lineIndex]))
                {
                    var resolved = ResolveSourceDefinition(document.Uri, lineIndex, occurrence.Start);
                    if (resolved is null || !SameDefinition(resolved, target))
                    {
                        continue;
                    }

                    references.Add(new VbaDefinitionLocation(
                        document.Uri,
                        new VbaRange(
                            new VbaPosition(lineIndex, occurrence.Start),
                            new VbaPosition(lineIndex, occurrence.End))));
                }
            }
        }

        return references
            .GroupBy(reference => $"{reference.Uri}:{GetRangeKey(reference.Range)}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(reference => reference.Uri, StringComparer.OrdinalIgnoreCase)
            .ThenBy(reference => reference.Range.Start.Line)
            .ThenBy(reference => reference.Range.Start.Character)
            .ToArray();
    }

    public IReadOnlyList<VbaSemanticToken> GetSemanticTokens(string uri)
    {
        var currentDocument = documents.FirstOrDefault(document => SameUri(document.Uri, uri));
        if (currentDocument is null)
        {
            return [];
        }

        var lines = SplitLines(currentDocument.Text);
        var tokens = new List<VbaSemanticToken>();
        var declarationRanges = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in currentDocument.Definitions)
        {
            if (!TryCreateSemanticToken(lines, definition, isDeclaration: true, out var token))
            {
                continue;
            }

            tokens.Add(token);
            declarationRanges.Add(GetRangeKey(token.Range));
        }

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            foreach (var occurrence in FindIdentifierOccurrences(lines[lineIndex]))
            {
                var occurrenceRange = new VbaRange(
                    new VbaPosition(lineIndex, occurrence.Start),
                    new VbaPosition(lineIndex, occurrence.End));
                if (declarationRanges.Contains(GetRangeKey(occurrenceRange)))
                {
                    continue;
                }

                var definition = ResolveSourceDefinition(uri, lineIndex, occurrence.Start);
                if (definition is null
                    || !TryCreateSemanticToken(
                        lines,
                        definition,
                        isDeclaration: false,
                        out var referenceToken,
                        occurrenceRange,
                        occurrence.Name))
                {
                    continue;
                }

                tokens.Add(referenceToken);
            }
        }

        return tokens
            .GroupBy(token => $"{GetRangeKey(token.Range)}:{token.TokenType}:{string.Join(",", token.TokenModifiers)}", StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(token => token.Range.Start.Line)
            .ThenBy(token => token.Range.Start.Character)
            .ToArray();
    }

    public IReadOnlyList<int> GetSemanticTokenData(string uri)
    {
        var data = new List<int>();
        var previousLine = 0;
        var previousStart = 0;
        foreach (var token in GetSemanticTokens(uri))
        {
            var line = token.Range.Start.Line;
            var start = token.Range.Start.Character;
            var deltaLine = line - previousLine;
            var deltaStart = deltaLine == 0 ? start - previousStart : start;
            data.Add(deltaLine);
            data.Add(deltaStart);
            data.Add(token.Range.End.Character - token.Range.Start.Character);
            data.Add(SemanticTokenTypeIndexes[token.TokenType]);
            data.Add(GetSemanticTokenModifierBits(token.TokenModifiers));
            previousLine = line;
            previousStart = start;
        }

        return data;
    }

    public IReadOnlyList<VbaSourceDefinition> GetCompletionDefinitions(string uri, int line, int character)
    {
        var currentDocument = documents.FirstOrDefault(document => SameUri(document.Uri, uri));
        if (currentDocument is not null
            && TryGetMemberCompletionDefinitions(currentDocument, line, character, out var memberDefinitions))
        {
            return memberDefinitions;
        }

        return CreateNameResolutionService()
            .GetCompletionDefinitions(uri, new VbaPosition(line, character));
    }

    public VbaDefinitionLocation? ResolveDefinition(string uri, int line, int character)
    {
        var definition = ResolveSourceDefinition(uri, line, character);
        return definition is null ? null : new VbaDefinitionLocation(definition.Uri, definition.Range);
    }

    public VbaSourceDefinition? ResolveSourceDefinition(string uri, int line, int character)
    {
        var currentDocument = documents.FirstOrDefault(document => SameUri(document.Uri, uri));
        if (currentDocument is null)
        {
            return null;
        }

        var lines = SplitLines(currentDocument.Text);
        if (line < 0 || line >= lines.Length)
        {
            return null;
        }

        if (!IsCodePosition(lines[line], character))
        {
            return null;
        }

        var identifier = GetIdentifierAt(lines[line], character);
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
        return CreateNameResolutionService()
            .Resolve(uri, new VbaPosition(line, character), qualifier, identifier.Name);
    }

    public VbaSignatureHelp? GetSignatureHelp(string uri, int line, int character)
    {
        var currentDocument = documents.FirstOrDefault(document => SameUri(document.Uri, uri));
        if (currentDocument is null)
        {
            return null;
        }

        var lines = SplitLines(currentDocument.Text);
        if (line < 0 || line >= lines.Length)
        {
            return null;
        }

        var logicalPrefix = GetLogicalPrefix(lines, line, character);
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

    public IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>>? CreateRenameChanges(
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
        if (target is null || !IsRenameTarget(target))
        {
            return null;
        }

        var changes = new Dictionary<string, IReadOnlyList<VbaTextEdit>>(StringComparer.OrdinalIgnoreCase);
        foreach (var document in documents)
        {
            var edits = new List<VbaTextEdit>();
            var lines = SplitLines(document.Text);
            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                foreach (var occurrence in FindIdentifierOccurrences(lines[lineIndex]))
                {
                    if (!SameName(occurrence.Name, target.Name))
                    {
                        continue;
                    }

                    var resolved = ResolveSourceDefinition(document.Uri, lineIndex, occurrence.Start);
                    if (resolved is not null && SameDefinition(resolved, target))
                    {
                        edits.Add(new VbaTextEdit(
                            new VbaRange(
                                new VbaPosition(lineIndex, occurrence.Start),
                                new VbaPosition(lineIndex, occurrence.End)),
                            newName));
                    }
                }
            }

            if (edits.Count > 0)
            {
                changes[document.Uri] = edits;
            }
        }

        return changes.Count == 0 ? null : changes;
    }

    public VbaTextEdit? FormatDocument(string uri, int tabSize)
    {
        var document = documents.FirstOrDefault(candidate => SameUri(candidate.Uri, uri));
        if (document is null)
        {
            return null;
        }

        var formattedText = FormatText(document, Math.Max(1, tabSize));
        if (string.Equals(formattedText, document.Text, StringComparison.Ordinal))
        {
            return null;
        }

        var lines = SplitLines(document.Text);
        return new VbaTextEdit(
            new VbaRange(
                new VbaPosition(0, 0),
                new VbaPosition(Math.Max(0, lines.Length - 1), lines.Length == 0 ? 0 : lines[^1].Length)),
            formattedText);
    }

    private bool TryGetMemberCompletionDefinitions(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        out IReadOnlyList<VbaSourceDefinition> definitions)
    {
        definitions = [];
        var lines = SplitLines(currentDocument.Text);
        if (line < 0 || line >= lines.Length)
        {
            return false;
        }

        var logicalPrefix = GetLogicalPrefix(lines, line, character);
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

    private bool TryResolveMemberDefinition(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        string memberName,
        out VbaSourceDefinition? definition)
    {
        definition = null;
        var lines = SplitLines(currentDocument.Text);
        if (line < 0 || line >= lines.Length)
        {
            return false;
        }

        var logicalPrefix = GetLogicalPrefix(lines, line, character);
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
        definition = CreateNameResolutionService()
            .Resolve(currentDocument.Uri, new VbaPosition(line, character), qualifier, unqualifiedName);
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

        var firstDefinition = CreateNameResolutionService()
            .Resolve(currentDocument.Uri, new VbaPosition(line, character), null, parts[0]);
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
        var lines = SplitLines(currentDocument.Text);
        var stack = new List<ResolvedType>();
        for (var lineIndex = 0; lineIndex < targetLine && lineIndex < lines.Length; lineIndex++)
        {
            var statement = GetLogicalStatement(lines, lineIndex, out var endLine);
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

    private static bool TryGetMemberReceiverExpression(string logicalPrefix, out string receiverExpression)
    {
        receiverExpression = "";
        var trimmed = logicalPrefix.TrimEnd();
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

    private static string? GetQualifierFromCallee(string callee)
    {
        var normalized = NormalizeMemberExpression(callee);
        var lastDot = normalized.LastIndexOf('.');
        return lastDot < 0 ? null : normalized[..lastDot];
    }

    private static string GetLogicalPrefix(string[] lines, int line, int character)
    {
        var startLine = line;
        while (startLine > 0 && HasLineContinuation(lines[startLine - 1]))
        {
            startLine--;
        }

        var parts = new List<string>();
        for (var lineIndex = startLine; lineIndex <= line; lineIndex++)
        {
            var text = lineIndex == line
                ? lines[lineIndex][..Math.Clamp(character, 0, lines[lineIndex].Length)]
                : lines[lineIndex];
            parts.Add(RemoveLineContinuation(text));
        }

        return string.Join(' ', parts);
    }

    private static string GetLogicalStatement(string[] lines, int startLine, out int endLine)
    {
        var parts = new List<string>();
        endLine = startLine;
        while (endLine < lines.Length)
        {
            parts.Add(RemoveLineContinuation(lines[endLine]));
            if (!HasLineContinuation(lines[endLine]))
            {
                break;
            }

            endLine++;
        }

        return string.Join(' ', parts);
    }

    private static bool HasLineContinuation(string line)
        => line.TrimEnd().EndsWith("_", StringComparison.Ordinal);

    private static string RemoveLineContinuation(string line)
    {
        var trimmed = line.TrimEnd();
        return trimmed.EndsWith("_", StringComparison.Ordinal)
            ? trimmed[..^1]
            : line;
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

    private static bool TryCreateSemanticToken(
        string[] lines,
        VbaSourceDefinition definition,
        bool isDeclaration,
        out VbaSemanticToken token,
        VbaRange? overrideRange = null,
        string? overrideText = null)
    {
        token = default!;
        var tokenType = GetSemanticTokenType(definition);
        if (tokenType is null)
        {
            return false;
        }

        var range = overrideRange ?? definition.Range;
        if (range.Start.Line < 0
            || range.Start.Line >= lines.Length
            || range.End.Line != range.Start.Line
            || range.Start.Character < 0
            || range.End.Character > lines[range.Start.Line].Length
            || range.End.Character <= range.Start.Character)
        {
            return false;
        }

        var text = overrideText ?? lines[range.Start.Line][range.Start.Character..range.End.Character];
        token = new VbaSemanticToken(
            range,
            text,
            tokenType,
            GetSemanticTokenModifiers(definition, isDeclaration));
        return true;
    }

    private static string? GetSemanticTokenType(VbaSourceDefinition definition)
        => definition.Kind switch
        {
            VbaSourceDefinitionKind.Class => "class",
            VbaSourceDefinitionKind.Form => "class",
            VbaSourceDefinitionKind.Type => "struct",
            VbaSourceDefinitionKind.Enum => "enum",
            VbaSourceDefinitionKind.EnumMember => "enumMember",
            VbaSourceDefinitionKind.Procedure => definition.ParentTypeName is null ? "function" : "method",
            VbaSourceDefinitionKind.Property => "property",
            VbaSourceDefinitionKind.TypeMember => "property",
            VbaSourceDefinitionKind.Event => "event",
            VbaSourceDefinitionKind.Constant => "variable",
            VbaSourceDefinitionKind.Variable => "variable",
            VbaSourceDefinitionKind.Parameter => "parameter",
            _ => null
        };

    private static IReadOnlyList<string> GetSemanticTokenModifiers(
        VbaSourceDefinition definition,
        bool isDeclaration)
    {
        var modifiers = new List<string>();
        if (isDeclaration)
        {
            modifiers.Add("declaration");
        }

        if (definition.Kind == VbaSourceDefinitionKind.Constant)
        {
            modifiers.Add("readonly");
        }

        if (VbaProjectReferenceCatalogSet.IsExternalDefinition(definition))
        {
            modifiers.Add("defaultLibrary");
        }

        return modifiers;
    }

    private static int GetSemanticTokenModifierBits(IReadOnlyList<string> modifiers)
    {
        var bits = 0;
        foreach (var modifier in modifiers)
        {
            if (SemanticTokenModifierIndexes.TryGetValue(modifier, out var index))
            {
                bits |= 1 << index;
            }
        }

        return bits;
    }

    private static string GetRangeKey(VbaRange range)
        => $"{range.Start.Line}:{range.Start.Character}:{range.End.Line}:{range.End.Character}";

    private static VbaSourceDocument ParseDocument(string uri, string text)
    {
        var syntaxTree = VbaModuleParser.Parse(uri, text);
        return CreateDocument(uri, syntaxTree);
    }

    private static VbaSourceDocument CreateDocument(string uri, VbaModuleSyntaxTree syntaxTree)
    {
        var definitions = new List<VbaSourceDefinition>();
        var moduleDefinition = CreateModuleDefinition(uri, syntaxTree.Identity);
        definitions.Add(moduleDefinition);
        definitions.AddRange(syntaxTree.Declarations.Select(declaration =>
            CreateSourceDefinition(uri, moduleDefinition.Name, declaration)));

        return new VbaSourceDocument(uri, syntaxTree.Text, moduleDefinition.Name, definitions);
    }

    private static VbaSourceDefinition CreateModuleDefinition(string uri, VbaModuleIdentity identity)
    {
        return new VbaSourceDefinition(
            identity.Name,
            identity.Kind,
            VbaSourceDefinitionVisibility.Public,
            uri,
            identity.Name,
            identity.Range);
    }

    private static VbaSourceDefinition CreateSourceDefinition(
        string uri,
        string moduleName,
        VbaSourceDeclarationSyntax declaration)
    {
        return new VbaSourceDefinition(
            declaration.Name,
            declaration.Kind,
            declaration.Visibility,
            uri,
            moduleName,
            declaration.Range,
            declaration.ParentProcedureName,
            declaration.ParentProcedureRange,
            declaration.Documentation,
            declaration.Signature,
            declaration.ParentTypeName,
            declaration.TypeReference,
            declaration.IsWithEvents);
    }

    private static bool IsReferenceTarget(VbaSourceDefinition definition)
        => definition.Visibility != VbaSourceDefinitionVisibility.Local
            && definition.Kind != VbaSourceDefinitionKind.Module
            && definition.Kind != VbaSourceDefinitionKind.Class
            && definition.Kind != VbaSourceDefinitionKind.Form;

    private static bool IsRenameTarget(VbaSourceDefinition definition)
        => !VbaProjectReferenceCatalogSet.IsExternalDefinition(definition)
            && (definition.Visibility == VbaSourceDefinitionVisibility.Local || IsReferenceTarget(definition));

    private static bool SameDefinition(VbaSourceDefinition left, VbaSourceDefinition right)
        => SameUri(left.Uri, right.Uri)
            && SameName(left.Name, right.Name)
            && ComparePosition(left.Range.Start, right.Range.Start) == 0
            && ComparePosition(left.Range.End, right.Range.End) == 0;

    private static string[] SplitLines(string source)
        => source.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

    private string FormatText(VbaSourceDocument document, int tabSize)
    {
        var nameResolution = CreateNameResolutionService();
        var declarationRanges = document.Definitions
            .Select(definition => GetRangeKey(definition.Range))
            .ToHashSet(StringComparer.Ordinal);
        var lines = SourceFormatting.SplitLogicalLines(document.Text);
        var formattedLines = new List<string>(lines.Count);
        var depth = 0;
        var indent = new string(' ', tabSize);

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            if (string.IsNullOrWhiteSpace(line))
            {
                formattedLines.Add("");
                continue;
            }

            var casedLine = FormatLineCasing(line, nameResolution, document, lineIndex, declarationRanges);
            var trimmed = casedLine.TrimStart();
            if (ShouldDedentBefore(trimmed))
            {
                depth = Math.Max(0, depth - 1);
            }

            formattedLines.Add($"{string.Concat(Enumerable.Repeat(indent, depth))}{trimmed}");

            if (ShouldIndentAfter(trimmed))
            {
                depth++;
            }
        }

        var formattedText = string.Join(SourceFormatting.DetectDominantLineEnding(document.Text), formattedLines);
        var edits = new SourceFormattingEditCollector();
        if (!string.Equals(formattedText, document.Text, StringComparison.Ordinal))
        {
            edits.Replace(0, document.Text.Length, formattedText);
        }

        return edits.Apply(document.Text);
    }

    private string FormatLineCasing(
        string line,
        VbaNameResolutionService nameResolution,
        VbaSourceDocument document,
        int lineIndex,
        IReadOnlySet<string> declarationRanges)
    {
        var commentStart = FindApostropheCommentStart(line);
        var codePart = commentStart < 0 ? line : line[..commentStart];
        var commentPart = commentStart < 0 ? "" : line[commentStart..];

        codePart = Regex.Replace(
            codePart,
            "^\\s*Attribute\\s+VB_Name",
            match => match.Value[..^"Attribute VB_Name".Length] + "Attribute VB_Name",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        var edits = new SourceFormattingEditCollector();
        foreach (var occurrence in FindIdentifierOccurrences(codePart))
        {
            var canonicalName = GetCanonicalFormattingName(
                codePart,
                occurrence,
                nameResolution,
                document,
                lineIndex,
                declarationRanges);
            if (canonicalName is not null
                && !string.Equals(occurrence.Name, canonicalName, StringComparison.Ordinal))
            {
                edits.Replace(occurrence.Start, occurrence.End, canonicalName);
            }
        }

        return edits.Apply(codePart) + commentPart;
    }

    private string? GetCanonicalFormattingName(
        string codePart,
        IdentifierAtPosition occurrence,
        VbaNameResolutionService nameResolution,
        VbaSourceDocument document,
        int lineIndex,
        IReadOnlySet<string> declarationRanges)
    {
        if (LanguageKeywords.TryGetValue(occurrence.Name, out var keyword))
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
            nameResolution,
            document,
            lineIndex,
            out var memberChainCanonicalName))
        {
            return memberChainCanonicalName;
        }

        if (TryGetQualifiedCanonicalName(
            codePart,
            occurrence,
            nameResolution,
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
        if (definition is null)
        {
            return null;
        }

        return definition.Name;
    }

    private bool TryGetMemberChainCanonicalName(
        string codePart,
        IdentifierAtPosition occurrence,
        VbaNameResolutionService nameResolution,
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
        IdentifierAtPosition occurrence,
        VbaNameResolutionService nameResolution,
        string uri,
        int lineIndex,
        out string? canonicalName)
    {
        canonicalName = null;
        if (TryGetPreviousQualifier(codePart, occurrence, out var qualifier))
        {
            var definition = ResolveQualifiedDefinition(
                nameResolution,
                uri,
                lineIndex,
                qualifier.Name,
                occurrence.Name);
            canonicalName = definition?.Name;
            return canonicalName is not null;
        }

        if (TryGetNextMember(codePart, occurrence, out var member))
        {
            var definition = ResolveQualifiedDefinition(
                nameResolution,
                uri,
                lineIndex,
                occurrence.Name,
                member.Name);
            canonicalName = definition is null
                ? null
                : GetCanonicalQualifierName(definition, occurrence.Name);
            return canonicalName is not null;
        }

        return false;
    }

    private static VbaSourceDefinition? ResolveQualifiedDefinition(
        VbaNameResolutionService nameResolution,
        string uri,
        int lineIndex,
        string qualifier,
        string identifier)
    {
        var definition = nameResolution.Resolve(
            uri,
            new VbaPosition(lineIndex, 0),
            qualifier,
            identifier);
        return definition;
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

    private static bool TryGetPreviousMemberReceiverExpression(
        string codePart,
        IdentifierAtPosition occurrence,
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

    private static bool IsQualifiedIdentifierOccurrence(string codePart, IdentifierAtPosition occurrence)
    {
        return TryGetPreviousQualifier(codePart, occurrence, out _)
            || TryGetNextMember(codePart, occurrence, out _);
    }

    private static bool TryGetPreviousQualifier(
        string codePart,
        IdentifierAtPosition occurrence,
        out IdentifierAtPosition qualifier)
    {
        qualifier = new IdentifierAtPosition("", 0, 0);
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

        if (qualifierEnd < 0 || !IsIdentifierCharacter(codePart[qualifierEnd]))
        {
            return false;
        }

        var qualifierStart = qualifierEnd;
        while (qualifierStart > 0 && IsIdentifierCharacter(codePart[qualifierStart - 1]))
        {
            qualifierStart--;
        }

        qualifier = new IdentifierAtPosition(
            codePart[qualifierStart..(qualifierEnd + 1)],
            qualifierStart,
            qualifierEnd + 1);
        return true;
    }

    private static bool TryGetNextMember(
        string codePart,
        IdentifierAtPosition occurrence,
        out IdentifierAtPosition member)
    {
        member = new IdentifierAtPosition("", 0, 0);
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

        if (memberStart >= codePart.Length || !IsIdentifierStart(codePart[memberStart]))
        {
            return false;
        }

        var memberEnd = memberStart + 1;
        while (memberEnd < codePart.Length && IsIdentifierCharacter(codePart[memberEnd]))
        {
            memberEnd++;
        }

        member = new IdentifierAtPosition(codePart[memberStart..memberEnd], memberStart, memberEnd);
        return true;
    }

    private static bool ShouldDedentBefore(string trimmedLine)
        => Regex.IsMatch(
            trimmedLine,
            "^(End\\s+(Sub|Function|Property|If|Select|With|Enum|Type)|Else\\b|ElseIf\\b|Case\\b|Loop\\b|Wend\\b|Next\\b)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool ShouldIndentAfter(string trimmedLine)
        => Regex.IsMatch(
            trimmedLine,
            "^((Public|Private|Friend)\\s+)?(Sub|Function|Property)\\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || Regex.IsMatch(trimmedLine, "^If\\b.*\\bThen\\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || Regex.IsMatch(trimmedLine, "^(Else\\b|ElseIf\\b|Case\\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || Regex.IsMatch(trimmedLine, "^(For\\b|Do\\b|While\\b|With\\b|Select\\s+Case\\b|Enum\\b|Type\\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static IEnumerable<IdentifierAtPosition> FindIdentifierOccurrences(string line)
    {
        var inString = false;
        for (var index = 0; index < line.Length; index++)
        {
            var current = line[index];
            if (current == '"' && inString && index + 1 < line.Length && line[index + 1] == '"')
            {
                index++;
                continue;
            }

            if (current == '"')
            {
                inString = !inString;
                continue;
            }

            if (!inString && current == '\'')
            {
                yield break;
            }

            if (inString || !IsIdentifierStart(current))
            {
                continue;
            }

            var start = index;
            index++;
            while (index < line.Length && IsIdentifierCharacter(line[index]))
            {
                index++;
            }

            yield return new IdentifierAtPosition(line[start..index], start, index);
            index--;
        }
    }

    private static int FindApostropheCommentStart(string line)
    {
        var inString = false;
        for (var index = 0; index < line.Length; index++)
        {
            var current = line[index];
            if (current == '"' && inString && index + 1 < line.Length && line[index + 1] == '"')
            {
                index++;
                continue;
            }

            if (current == '"')
            {
                inString = !inString;
                continue;
            }

            if (!inString && current == '\'')
            {
                return index;
            }
        }

        return -1;
    }

    private static IdentifierAtPosition? GetIdentifierAt(string line, int character)
    {
        if (line.Length == 0)
        {
            return null;
        }

        var clamped = Math.Clamp(character, 0, line.Length - 1);
        if (!IsIdentifierCharacter(line[clamped]) && clamped > 0 && IsIdentifierCharacter(line[clamped - 1]))
        {
            clamped--;
        }

        if (!IsIdentifierCharacter(line[clamped]))
        {
            return null;
        }

        var start = clamped;
        while (start > 0 && IsIdentifierCharacter(line[start - 1]))
        {
            start--;
        }

        var end = clamped + 1;
        while (end < line.Length && IsIdentifierCharacter(line[end]))
        {
            end++;
        }

        return new IdentifierAtPosition(line[start..end], start, end);
    }

    private static bool IsCodePosition(string line, int character)
    {
        var inString = false;
        var clamped = Math.Clamp(character, 0, Math.Max(0, line.Length - 1));
        for (var index = 0; index <= clamped && index < line.Length; index++)
        {
            var current = line[index];
            if (current == '"' && inString && index + 1 < line.Length && line[index + 1] == '"')
            {
                index++;
                continue;
            }

            if (current == '"')
            {
                inString = !inString;
                if (index == clamped)
                {
                    return false;
                }

                continue;
            }

            if (!inString && current == '\'')
            {
                return false;
            }
        }

        return !inString;
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

        if (qualifierEnd < 0 || !IsIdentifierCharacter(line[qualifierEnd]))
        {
            return null;
        }

        var qualifierStart = qualifierEnd;
        while (qualifierStart > 0 && IsIdentifierCharacter(line[qualifierStart - 1]))
        {
            qualifierStart--;
        }

        return line[qualifierStart..(qualifierEnd + 1)];
    }

    private static bool IsIdentifierStart(char value)
        => char.IsAsciiLetter(value) || value == '_';

    private static bool IsIdentifierCharacter(char value)
        => char.IsAsciiLetterOrDigit(value) || value == '_';

    private static bool IsIdentifierName(string value)
        => Regex.IsMatch(
            value,
            "^[A-Za-z_][A-Za-z0-9_]*$",
            RegexOptions.CultureInvariant);

    private static bool SameUri(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool SameName(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static int ComparePosition(VbaPosition left, VbaPosition right)
    {
        var lineComparison = left.Line.CompareTo(right.Line);
        return lineComparison != 0 ? lineComparison : left.Character.CompareTo(right.Character);
    }

    private VbaNameResolutionService CreateNameResolutionService()
        => new(documents, referenceSelection, referenceCatalogs);

    private sealed record ResolvedType(string Name, string? ReferenceName);

    private sealed record IdentifierAtPosition(string Name, int Start, int End);
}
