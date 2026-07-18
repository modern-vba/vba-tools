using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.Syntax;

namespace VbaLanguageServer.SourceModel;

/// <summary>
/// Provides semantic resolution for completion, definition, signature help, and formatting.
/// </summary>
internal sealed class VbaSemanticResolution
{
    private static readonly VbaCompletionResult EmptyCompletion = new([]);
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
    /// Gets the complete editor-neutral candidates valid at a source position.
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
            return EmptyCompletion;
        }

        var syntaxTree = GetSyntaxTree(currentDocument);
        var positionSyntax = syntaxTree.GetPositionSyntax(line, character);
        var expectation = positionSyntax.CompletionExpectation;
        if (expectation == VbaCompletionExpectation.None)
        {
            return EmptyCompletion;
        }

        if (!AllowsOperandCompletionInActiveCall(
                currentDocument,
                line,
                character,
                positionSyntax))
        {
            return EmptyCompletion;
        }

        if (IsMemberCompletionPosition(
                currentDocument,
                line,
                character,
                positionSyntax)
            && positionSyntax.MemberAccess is not null
            && (positionSyntax.MemberAccess.TargetSegmentIndex > 0
                || positionSyntax.MemberAccess.IsLeadingDot
                || positionSyntax.MemberAccess.IsIncomplete)
            && TryGetSourceQualifierCompletionDefinitions(
                currentDocument,
                line,
                character,
                positionSyntax,
                out var sourceQualifierDefinitions))
        {
            return Complete(
                CreateDefinitionCandidates(FilterDefinitions(
                    sourceQualifierDefinitions,
                    expectation,
                    VbaCallableCompletionContext.None)),
                positionSyntax.CompletionReplacementRange);
        }

        if (IsMemberCompletionPosition(
                currentDocument,
                line,
                character,
                positionSyntax)
            && positionSyntax.MemberAccess is not null
            && (positionSyntax.MemberAccess.TargetSegmentIndex > 0
                || positionSyntax.MemberAccess.IsLeadingDot
                || positionSyntax.MemberAccess.IsIncomplete)
            && TryGetReferenceQualifierCompletionDefinitions(
                currentDocument,
                line,
                character,
                positionSyntax,
                out var referenceQualifierDefinitions))
        {
            return Complete(
                CreateDefinitionCandidates(FilterDefinitions(
                    referenceQualifierDefinitions,
                    expectation,
                    VbaCallableCompletionContext.None)),
                positionSyntax.CompletionReplacementRange);
        }

        if (IsMemberCompletionPosition(
                currentDocument,
                line,
                character,
                positionSyntax)
            && positionSyntax.MemberAccess is not null
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
            return Complete(
                CreateDefinitionCandidates(FilterDefinitions(
                    memberDefinitions,
                    expectation,
                    VbaCallableCompletionContext.None)),
                positionSyntax.CompletionReplacementRange);
        }

        var visibleDefinitions = nameResolution.GetCompletionDefinitions(
            uri,
            new VbaPosition(line, character));
        var sourceQualifiers = nameResolution.GetCompletionSourceQualifiers(
            uri,
            new VbaPosition(line, character));
        var referenceQualifiers = nameResolution.GetCompletionReferenceQualifiers(
            uri,
            new VbaPosition(line, character));
        var callableContext = GetCurrentCallableCompletionContext(syntaxTree, line, character);
        var typeQualifier = positionSyntax.TypeReference?.Qualifier?.Name;
        IEnumerable<VbaCompletionCandidate> candidates = expectation switch
        {
            VbaCompletionExpectation.ModuleDeclaration =>
                CreateModuleDeclarationCandidates(positionSyntax),
            VbaCompletionExpectation.SyntaxWord =>
                CreateVocabularyCandidates(positionSyntax.SyntaxWords),
            VbaCompletionExpectation.ContextualStatement =>
                CreateContextualStatementCandidates(positionSyntax.ContextualStatements),
            VbaCompletionExpectation.CallableName =>
                CreateDefinitionCandidates(FilterDefinitions(
                    visibleDefinitions,
                    expectation,
                    callableContext)),
            VbaCompletionExpectation.ProcedureStatement =>
                CreateDefinitionCandidates(FilterDefinitions(
                    visibleDefinitions,
                    expectation,
                    callableContext))
                    .Concat(CreateQualifierCandidates(sourceQualifiers))
                    .Concat(CreateReferenceQualifierCandidates(referenceQualifiers))
                    .Concat(CreateVocabularyCandidates(VbaLanguageVocabulary.ProcedureStatementWords))
                    .Concat(CreateContextualStatementCandidates(positionSyntax.ContextualStatements)),
            VbaCompletionExpectation.ExpressionValue =>
                CreateExpressionValueCandidates(
                    currentDocument,
                    line,
                    character,
                    positionSyntax,
                    visibleDefinitions,
                    sourceQualifiers,
                    referenceQualifiers),
            VbaCompletionExpectation.AssignmentTarget =>
                CreateDefinitionCandidates(FilterDefinitions(
                    visibleDefinitions,
                    expectation,
                    callableContext)),
            VbaCompletionExpectation.TypeName =>
                CreateDefinitionCandidates(GetTypeCompletionDefinitions(currentDocument, typeQualifier))
                    .Concat(CreateQualifierCandidates(typeQualifier is null ? sourceQualifiers : []))
                    .Concat(CreateReferenceQualifierCandidates(typeQualifier is null ? referenceQualifiers : []))
                    .Concat(typeQualifier is null
                        ? CreateVocabularyCandidates(VbaLanguageVocabulary.TypeNames)
                        : []),
            VbaCompletionExpectation.CreatableType =>
                CreateDefinitionCandidates(GetTypeCompletionDefinitions(currentDocument, typeQualifier)
                    .Where(definition => definition.IsCreatable))
                    .Concat(CreateQualifierCandidates(typeQualifier is null ? sourceQualifiers : []))
                    .Concat(CreateReferenceQualifierCandidates(typeQualifier is null ? referenceQualifiers : [])),
            VbaCompletionExpectation.ImplementsType =>
                CreateDefinitionCandidates(GetTypeCompletionDefinitions(currentDocument, typeQualifier)
                    .Where(definition => definition.Kind == VbaSourceDefinitionKind.Class)
                    .Where(definition => !SameUri(definition.Uri, currentDocument.Uri))),
            VbaCompletionExpectation.CallArgument =>
                CreateCallArgumentCandidates(
                    currentDocument,
                    line,
                    character,
                    positionSyntax,
                    visibleDefinitions),
            VbaCompletionExpectation.NamedArgumentValue =>
                CreateNamedArgumentValueCandidates(
                    currentDocument,
                    line,
                    character,
                    positionSyntax,
                    visibleDefinitions),
            VbaCompletionExpectation.EventName =>
                CreateDefinitionCandidates(currentDocument.Definitions
                    .Where(definition => definition.Kind == VbaSourceDefinitionKind.Event)),
            VbaCompletionExpectation.LabelName =>
                CreateLabelCandidates(syntaxTree, positionSyntax),
            _ => []
        };

        return Complete(
            candidates.Concat(CreateVocabularyCandidates(positionSyntax.SupplementalSyntaxWords)),
            positionSyntax.CompletionReplacementRange);
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

    private bool TryGetReferenceQualifierCompletionDefinitions(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        VbaPositionSyntax positionSyntax,
        out IReadOnlyList<VbaSourceDefinition> definitions)
    {
        definitions = [];
        var access = positionSyntax.MemberAccess;
        if (access is null
            || access.IsLeadingDot
            || !access.IsIncomplete
            || access.Target is not null
            || access.ReceiverSegments.Count != 1
            || !SupportsMemberCompletion(positionSyntax.CompletionExpectation))
        {
            return false;
        }

        var qualifier = access.ReceiverSegments[0].Name;
        definitions = nameResolution.GetQualifiedCompletionDefinitions(
            currentDocument,
            new VbaPosition(line, character),
            qualifier);
        return definitions.Count > 0;
    }

    private bool TryGetSourceQualifierCompletionDefinitions(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        VbaPositionSyntax positionSyntax,
        out IReadOnlyList<VbaSourceDefinition> definitions)
    {
        definitions = [];
        var access = positionSyntax.MemberAccess;
        if (access is null
            || access.IsLeadingDot
            || !access.IsIncomplete
            || access.Target is not null
            || access.ReceiverSegments.Count != 1
            || !SupportsMemberCompletion(positionSyntax.CompletionExpectation))
        {
            return false;
        }

        var qualifier = access.ReceiverSegments[0].Name;
        definitions = nameResolution.GetSourceQualifiedCompletionDefinitions(
            currentDocument,
            new VbaPosition(line, character),
            qualifier);
        return definitions.Count > 0;
    }

    private static IEnumerable<VbaCompletionCandidate> CreateModuleDeclarationCandidates(
        VbaPositionSyntax positionSyntax)
    {
        var contextualCandidates = CreateContextualStatementCandidates(
            positionSyntax.ContextualStatements);
        var innermostKind = positionSyntax.EnclosingBlocks.LastOrDefault()?.Block.Kind;
        return innermostKind is VbaBlockKind.Enum or VbaBlockKind.Type
            ? contextualCandidates
            : CreateVocabularyCandidates(positionSyntax.StarterWords)
                .Concat(contextualCandidates);
    }

    private IEnumerable<VbaCompletionCandidate> CreateCallArgumentCandidates(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        VbaPositionSyntax positionSyntax,
        IReadOnlyList<VbaSourceDefinition> visibleDefinitions)
    {
        var availability = callSiteResolution.GetCallArgumentAvailability(
            currentDocument,
            line,
            character,
            positionSyntax);
        if (availability.CallableDefinition is { IsArray: false }
            && availability.Signature?.CallableKind is not (VbaCallableKind.Sub
                or VbaCallableKind.Function
                or VbaCallableKind.Property
                or VbaCallableKind.Event))
        {
            return [];
        }

        var candidates = new List<VbaCompletionCandidate>();
        if (availability.AllowsPositionalExpression)
        {
            candidates.AddRange(CreateExpressionCandidates(currentDocument, visibleDefinitions));
        }

        candidates.AddRange(availability.RemainingNamedParameters.Select(parameter =>
            new VbaCompletionCandidate(
                parameter.Name,
                VbaCompletionCandidateKind.NamedArgument,
                InsertText: $"{parameter.Name}:=",
                FilterText: parameter.Name)));
        return candidates;
    }

    private IEnumerable<VbaCompletionCandidate> CreateNamedArgumentValueCandidates(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        VbaPositionSyntax positionSyntax,
        IReadOnlyList<VbaSourceDefinition> visibleDefinitions)
    {
        var availability = callSiteResolution.GetCallArgumentAvailability(
            currentDocument,
            line,
            character,
            positionSyntax);
        if (!CanCompleteNamedArgumentValue(availability, positionSyntax))
        {
            return [];
        }

        return CreateExpressionCandidates(currentDocument, visibleDefinitions);
    }

    private static bool IsKnownCallable(VbaCallArgumentAvailability availability)
        => availability.CallableDefinition is not null
            && availability.Signature?.CallableKind is VbaCallableKind.Sub
                or VbaCallableKind.Function
                or VbaCallableKind.Property;

    private static bool CanCompleteNamedArgumentValue(
        VbaCallArgumentAvailability availability,
        VbaPositionSyntax positionSyntax)
    {
        var activeName = positionSyntax.CallSite?.ActiveNamedArgument;
        return IsKnownCallable(availability)
            && activeName is not null
            && availability.RemainingNamedParameters.Any(parameter =>
                parameter.Name.Equals(activeName, StringComparison.OrdinalIgnoreCase));
    }

    private bool AllowsOperandCompletionInActiveCall(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        VbaPositionSyntax positionSyntax)
    {
        if (positionSyntax.CompletionExpectation is not (
                VbaCompletionExpectation.ExpressionValue
                or VbaCompletionExpectation.TypeName
                or VbaCompletionExpectation.CreatableType)
            || !IsInsideActiveCallArgument(positionSyntax.CallSite, line, character))
        {
            return true;
        }

        var availability = callSiteResolution.GetCallArgumentAvailability(
            currentDocument,
            line,
            character,
            positionSyntax);
        return positionSyntax.CallSite?.ActiveNamedArgument is null
            ? availability.AllowsPositionalExpression
            : CanCompleteNamedArgumentValue(availability, positionSyntax);
    }

    private static IEnumerable<VbaCompletionCandidate> CreateExpressionCandidates(
        VbaSourceDocument currentDocument,
        IEnumerable<VbaSourceDefinition> definitions)
        => CreateDefinitionCandidates(definitions.Where(IsReadableDefinition))
            .Concat(CreateVocabularyCandidates(VbaLanguageVocabulary.GetExpressionValueWords(
                GetSyntaxTree(currentDocument).Module.Kind)));

    private IEnumerable<VbaCompletionCandidate> CreateExpressionValueCandidates(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        VbaPositionSyntax positionSyntax,
        IReadOnlyList<VbaSourceDefinition> visibleDefinitions,
        IReadOnlyList<string> sourceQualifiers,
        IReadOnlyList<string> referenceQualifiers)
    {
        if (!IsInsideActiveCallArgument(positionSyntax.CallSite, line, character))
        {
            return CreateExpressionCandidates(currentDocument, visibleDefinitions)
                .Concat(CreateQualifierCandidates(sourceQualifiers))
                .Concat(CreateReferenceQualifierCandidates(referenceQualifiers));
        }

        var availability = callSiteResolution.GetCallArgumentAvailability(
            currentDocument,
            line,
            character,
            positionSyntax);
        return availability.AllowsPositionalExpression
            ? CreateExpressionCandidates(currentDocument, visibleDefinitions)
                .Concat(CreateQualifierCandidates(sourceQualifiers))
                .Concat(CreateReferenceQualifierCandidates(referenceQualifiers))
            : [];
    }

    private static bool IsInsideActiveCallArgument(
        VbaCallSiteSyntax? callSite,
        int line,
        int character)
    {
        if (callSite is null
            || !callSite.Callee.AllowsCallTargetSyntax
            || callSite.ActiveArgumentIndex < 0
            || callSite.ActiveArgumentIndex >= callSite.Arguments.Count)
        {
            return false;
        }

        var range = callSite.Arguments[callSite.ActiveArgumentIndex].Range;
        var position = new VbaSyntaxPosition(line, character, 0);
        return Contains(range, position);
    }

    private static IEnumerable<VbaSourceDefinition> FilterDefinitions(
        IEnumerable<VbaSourceDefinition> definitions,
        VbaCompletionExpectation expectation,
        VbaCallableCompletionContext callableContext)
        => definitions.Where(definition => expectation switch
        {
            VbaCompletionExpectation.ExpressionValue
                or VbaCompletionExpectation.CallArgument
                or VbaCompletionExpectation.NamedArgumentValue => IsReadableDefinition(definition),
            VbaCompletionExpectation.AssignmentTarget =>
                (IsWritableDefinition(definition)
                    && !IsCurrentSetterProperty(definition, callableContext.SetterPropertyName))
                || IsCurrentResultTarget(definition, callableContext.ResultTargetName),
            VbaCompletionExpectation.ProcedureStatement =>
                IsProcedureStatementDefinition(definition),
            VbaCompletionExpectation.CallableName =>
                IsCallableDefinition(definition),
            _ => false
        });

    private static bool IsReadableDefinition(VbaSourceDefinition definition)
        => definition.Kind switch
        {
            VbaSourceDefinitionKind.Constant
                or VbaSourceDefinitionKind.Variable
                or VbaSourceDefinitionKind.Parameter
                or VbaSourceDefinitionKind.EnumMember
                or VbaSourceDefinitionKind.TypeMember => true,
            VbaSourceDefinitionKind.Procedure =>
                definition.Signature?.CallableKind == VbaCallableKind.Function,
            VbaSourceDefinitionKind.Property =>
                definition.PropertyAccess.HasFlag(VbaPropertyAccess.Readable),
            _ => false
        };

    private static bool IsWritableDefinition(VbaSourceDefinition definition)
        => definition.Kind switch
        {
            VbaSourceDefinitionKind.Variable
                or VbaSourceDefinitionKind.Parameter
                or VbaSourceDefinitionKind.TypeMember => true,
            VbaSourceDefinitionKind.Property =>
                definition.PropertyAccess.HasFlag(VbaPropertyAccess.Writable),
            _ => false
        };

    private static bool IsProcedureStatementDefinition(VbaSourceDefinition definition)
        => IsWritableDefinition(definition)
            || (definition.Kind == VbaSourceDefinitionKind.Property
                && definition.PropertyAccess.HasFlag(VbaPropertyAccess.Readable))
            || (definition.Kind == VbaSourceDefinitionKind.Procedure
                && definition.Signature?.CallableKind is VbaCallableKind.Sub
                    or VbaCallableKind.Function);

    private static bool IsCallableDefinition(VbaSourceDefinition definition)
        => definition.Kind == VbaSourceDefinitionKind.Procedure
            && definition.Signature?.CallableKind is VbaCallableKind.Sub
                or VbaCallableKind.Function;

    private static bool IsCurrentResultTarget(
        VbaSourceDefinition definition,
        string? resultTargetName)
        => resultTargetName is not null
            && definition.Kind is VbaSourceDefinitionKind.Procedure
                or VbaSourceDefinitionKind.Property
            && definition.Name.Equals(resultTargetName, StringComparison.OrdinalIgnoreCase);

    private static bool IsCurrentSetterProperty(
        VbaSourceDefinition definition,
        string? setterPropertyName)
        => setterPropertyName is not null
            && definition.Kind == VbaSourceDefinitionKind.Property
            && definition.Name.Equals(setterPropertyName, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<VbaCompletionCandidate> CreateDefinitionCandidates(
        IEnumerable<VbaSourceDefinition> definitions)
        => definitions.Select(definition => new VbaCompletionCandidate(
            definition.Name,
            VbaCompletionCandidateKind.Definition,
            Definition: definition));

    private static IEnumerable<VbaCompletionCandidate> CreateReferenceQualifierCandidates(
        IEnumerable<string> qualifiers)
        => CreateQualifierCandidates(qualifiers);

    private static IEnumerable<VbaCompletionCandidate> CreateQualifierCandidates(
        IEnumerable<string> qualifiers)
        => qualifiers.Select(qualifier => new VbaCompletionCandidate(
            qualifier,
            VbaCompletionCandidateKind.ReferenceQualifier,
            InsertText: $"{qualifier}.",
            FilterText: qualifier));

    private static IEnumerable<VbaCompletionCandidate> CreateVocabularyCandidates(
        IEnumerable<string> words)
        => words.Select(word => new VbaCompletionCandidate(
            word,
            VbaCompletionCandidateKind.LanguageVocabulary));

    private static IEnumerable<VbaCompletionCandidate> CreateContextualStatementCandidates(
        IEnumerable<string> statements)
        => statements.Select(statement => new VbaCompletionCandidate(
            statement,
            VbaCompletionCandidateKind.ContextualStatement));

    private static IEnumerable<VbaCompletionCandidate> CreateLabelCandidates(
        VbaSyntaxTree syntaxTree,
        VbaPositionSyntax positionSyntax)
    {
        var reference = positionSyntax.LabelReference;
        if (reference is null)
        {
            return [];
        }

        var candidates = reference.SyntaxCandidates
            .Select(label => new VbaCompletionCandidate(
                label,
                VbaCompletionCandidateKind.Label))
            .ToList();
        if (reference.AllowsProcedureLabels)
        {
            candidates.AddRange(syntaxTree.Module.LineLabels
                .Where(label => label.ProcedureRange == reference.ProcedureRange)
                .Select(label => new VbaCompletionCandidate(
                    label.Name,
                    VbaCompletionCandidateKind.Label)));
        }

        return candidates;
    }

    private static VbaCompletionResult Complete(
        IEnumerable<VbaCompletionCandidate> candidates,
        VbaSyntaxRange? replacementRange)
    {
        var completed = candidates
            .Select(candidate => AddReplacementEdit(candidate, replacementRange))
            .GroupBy(GetCandidateIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(GetCandidatePrecedence)
                .ThenBy(candidate => candidate.InsertText, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(candidate => candidate.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Label, StringComparer.Ordinal)
            .ThenBy(GetEffectiveInsertionText, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Kind)
            .ToArray();
        return completed.Length == 0 ? EmptyCompletion : new VbaCompletionResult(completed);
    }

    private static string GetCandidateIdentity(VbaCompletionCandidate candidate)
        => $"{candidate.Label}\0{GetEffectiveInsertionText(candidate)}";

    private static string GetEffectiveInsertionText(VbaCompletionCandidate candidate)
        => candidate.TextEdit?.NewText
            ?? candidate.InsertText
            ?? candidate.Label;

    private static VbaCompletionCandidate AddReplacementEdit(
        VbaCompletionCandidate candidate,
        VbaSyntaxRange? replacementRange)
    {
        if (replacementRange is null || candidate.TextEdit is not null)
        {
            return candidate;
        }

        var range = new VbaRange(
            new VbaPosition(replacementRange.Start.Line, replacementRange.Start.Character),
            new VbaPosition(replacementRange.End.Line, replacementRange.End.Character));
        return candidate with
        {
            TextEdit = new VbaTextEdit(range, candidate.InsertText ?? candidate.Label)
        };
    }

    private static int GetCandidatePrecedence(VbaCompletionCandidate candidate)
        => candidate.Kind switch
        {
            VbaCompletionCandidateKind.NamedArgument => 0,
            VbaCompletionCandidateKind.Label => 1,
            VbaCompletionCandidateKind.ContextualStatement => 1,
            VbaCompletionCandidateKind.LanguageVocabulary => 2,
            _ => 3
        };

    private static bool SupportsMemberCompletion(VbaCompletionExpectation expectation)
        => expectation is VbaCompletionExpectation.ProcedureStatement
            or VbaCompletionExpectation.CallableName
            or VbaCompletionExpectation.ExpressionValue
            or VbaCompletionExpectation.AssignmentTarget
            or VbaCompletionExpectation.CallArgument
            or VbaCompletionExpectation.NamedArgumentValue;

    private bool IsMemberCompletionPosition(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        VbaPositionSyntax positionSyntax)
    {
        var access = positionSyntax.MemberAccess;
        if (access is null
            || access.HasTrailingWhitespace
            || !SupportsMemberCompletion(positionSyntax.CompletionExpectation))
        {
            return false;
        }

        if (positionSyntax.CompletionExpectation is not (VbaCompletionExpectation.ExpressionValue
            or VbaCompletionExpectation.CallArgument
            or VbaCompletionExpectation.NamedArgumentValue)
            || positionSyntax.CallSite is null)
        {
            return true;
        }

        if (access.Range.Start.Offset < positionSyntax.CallSite.Callee.Range.End.Offset)
        {
            return false;
        }

        if (positionSyntax.CompletionExpectation == VbaCompletionExpectation.ExpressionValue
            && IsInsideActiveCallArgument(positionSyntax.CallSite, line, character))
        {
            var positionalAvailability = callSiteResolution.GetCallArgumentAvailability(
                currentDocument,
                line,
                character,
                positionSyntax);
            return positionalAvailability.AllowsPositionalExpression;
        }

        if (positionSyntax.CompletionExpectation != VbaCompletionExpectation.NamedArgumentValue)
        {
            return true;
        }

        var availability = callSiteResolution.GetCallArgumentAvailability(
            currentDocument,
            line,
            character,
            positionSyntax);
        return CanCompleteNamedArgumentValue(availability, positionSyntax);
    }

    private static VbaCallableCompletionContext GetCurrentCallableCompletionContext(
        VbaSyntaxTree syntaxTree,
        int line,
        int character)
    {
        var position = new VbaSyntaxPosition(line, character, 0);
        var declaration = syntaxTree.Module.CallableDeclarations
            .Where(declaration => !declaration.IsExternal)
            .Where(declaration => Contains(declaration.BlockRange, position))
            .OrderBy(declaration => declaration.BlockRange.End.Line - declaration.BlockRange.Start.Line)
            .FirstOrDefault();
        if (declaration is null)
        {
            return VbaCallableCompletionContext.None;
        }

        if (declaration.DeclarationKeyword?.Equals("Function", StringComparison.OrdinalIgnoreCase) == true
            || declaration.PropertyAccessorKind == VbaPropertyAccessorKind.Get)
        {
            return new VbaCallableCompletionContext(declaration.Name, null);
        }

        return declaration.PropertyAccessorKind is VbaPropertyAccessorKind.Let
                or VbaPropertyAccessorKind.Set
            ? new VbaCallableCompletionContext(null, declaration.Name)
            : VbaCallableCompletionContext.None;
    }

    private static bool Contains(VbaSyntaxRange range, VbaSyntaxPosition position)
        => Compare(range.Start, position) <= 0 && Compare(position, range.End) <= 0;

    private static int Compare(VbaSyntaxPosition left, VbaSyntaxPosition right)
    {
        var lineComparison = left.Line.CompareTo(right.Line);
        return lineComparison != 0
            ? lineComparison
            : left.Character.CompareTo(right.Character);
    }

    private IReadOnlyList<VbaSourceDefinition> GetTypeCompletionDefinitions(
        VbaSourceDocument currentDocument,
        string? qualifier)
        => nameResolution.GetVisibleTypeDefinitions(currentDocument, qualifier)
            .GroupBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => typeResolution.ResolveSourceTypeCompletionGroup(group.ToArray()))
            .Where(definition => definition is not null)
            .Select(definition => definition!)
            .OrderBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private sealed record VbaCallableCompletionContext(
        string? ResultTargetName,
        string? SetterPropertyName)
    {
        public static VbaCallableCompletionContext None { get; } = new(null, null);
    }

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
