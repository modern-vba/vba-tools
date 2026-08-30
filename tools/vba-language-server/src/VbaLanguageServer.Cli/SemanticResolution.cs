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
    private readonly VbaNameCandidateInventory definitionCandidates;
    private readonly VbaResolutionPolicy resolutionPolicy;
    private readonly VbaNameResolutionService nameResolution;
    private readonly VbaTypeResolution typeResolution;
    private readonly VbaMemberChainResolution memberChainResolution;
    private readonly VbaCallSiteResolution callSiteResolution;
    private readonly VbaWithEventsSemanticModel withEventsSemantics;
    private readonly VbaHostClassEventSemanticModel hostClassEvents;
    private readonly VbaInterfaceSemanticModel interfaceSemantics;

    /// <summary>
    /// Creates the semantic resolution service.
    /// </summary>
    /// <param name="definitionCandidates">The immutable source and reference candidate inventory.</param>
    public VbaSemanticResolution(
        VbaNameCandidateInventory definitionCandidates,
        VbaResolutionPolicy? resolutionPolicy = null,
        VbaHostClassProjectionSnapshot? hostClassProjectionSnapshot = null,
        IReadOnlyDictionary<string, VbaProjectReferenceCatalogIdentity>?
            referenceCatalogIdentities = null)
    {
        this.definitionCandidates = definitionCandidates;
        resolutionPolicy ??= new VbaResolutionPolicy(
            definitionCandidates.ConditionalFamilies);
        this.resolutionPolicy = resolutionPolicy;
        nameResolution = new VbaNameResolutionService(
            definitionCandidates,
            resolutionPolicy);
        typeResolution = new VbaTypeResolution(nameResolution);
        memberChainResolution = new VbaMemberChainResolution(typeResolution);
        hostClassEvents = new VbaHostClassEventSemanticModel(
            hostClassProjectionSnapshot,
            nameResolution,
            referenceCatalogIdentities);
        withEventsSemantics = new VbaWithEventsSemanticModel(
            nameResolution,
            hostClassEvents,
            referenceCatalogIdentities);
        interfaceSemantics = new VbaInterfaceSemanticModel(nameResolution);
        callSiteResolution = new VbaCallSiteResolution(
            nameResolution,
            memberChainResolution,
            resolutionPolicy);
    }

    internal VbaWithEventsTypeEligibility? GetWithEventsTypeEligibility(
        VbaSourceDocument currentDocument,
        VbaSourceDefinition variable)
        => withEventsSemantics.ClassifyType(currentDocument, variable);

    internal bool HasIndeterminateConditionalCompilationOwnership(
        VbaSourceDefinition definition)
        => nameResolution
            .HasIndeterminateConditionalCompilationOwnership(definition);

    internal IReadOnlyList<VbaProjectValidationDiagnostic>
        GetInterfaceContractDiagnostics(VbaSourceDocument currentDocument)
        => interfaceSemantics.GetDiagnostics(currentDocument);

    internal IReadOnlyList<VbaSourceDefinition>
        ResolveInterfaceAccessorContractDefinitions(
            VbaSourceDocument currentDocument,
            int line,
            int character)
        => interfaceSemantics.ResolveAccessorContractDefinitions(
            currentDocument,
            line,
            character);

    internal IReadOnlyList<VbaInterfaceImplementationAssociation>
        GetConclusiveSourceInterfaceImplementationAssociations(
            VbaSourceDocument currentDocument)
        => interfaceSemantics.GetConclusiveSourceImplementationAssociations(
            currentDocument);

    internal VbaInterfaceImplementationAssociationAnalysis
        AnalyzeSourceInterfaceImplementationAssociations(
            VbaSourceDocument currentDocument)
        => interfaceSemantics.AnalyzeSourceImplementationAssociations(
            currentDocument);

    internal bool IsPotentialInterfaceImplementationDeclaration(
        VbaSourceDocument currentDocument,
        VbaSourceDefinition declaration)
        => interfaceSemantics.IsPotentialInterfaceImplementationDeclaration(
            currentDocument,
            declaration);

    internal VbaSourceDefinition ProjectSourceInterfaceDocumentation(
        VbaSourceDefinition definition)
        => interfaceSemantics.ProjectSourceInterfaceDocumentation(definition);

    internal VbaEventHandlerCompatibility AnalyzeWithEventsHandlerCompatibility(
        VbaSourceDocument currentDocument,
        VbaWithEventsHandlerAnalysis handlerAnalysis)
        => withEventsSemantics.AnalyzeHandlerCompatibility(
            currentDocument,
            handlerAnalysis);

    internal VbaIntrinsicHostHandlerAnalysis? AnalyzeIntrinsicHostHandler(
        VbaSourceDocument currentDocument,
        VbaSourceDefinition handler)
        => hostClassEvents.AnalyzeIntrinsicHandler(
            currentDocument,
            handler);

    internal VbaEventHandlerCompatibility
        AnalyzeIntrinsicHostHandlerCompatibility(
            VbaSourceDocument currentDocument,
            VbaIntrinsicHostHandlerAnalysis handlerAnalysis)
        => withEventsSemantics.AnalyzeHandlerCompatibility(
            currentDocument,
            handlerAnalysis);

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
        => GetCompletionResult(
            uri,
            line,
            character,
            VbaCompletionInvocation.Explicit);

    internal VbaCompletionResult GetCompletionResult(
        string uri,
        int line,
        int character,
        VbaCompletionInvocation invocation)
    {
        var currentDocument = definitionCandidates.FindDocument(uri);
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

        if (!AllowsCompletionInvocation(invocation, positionSyntax))
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

        var callableContext = GetCurrentCallableCompletionContext(
            syntaxTree,
            line,
            character) with
        {
            RequestedPropertyWriteAccessorKind =
                positionSyntax.AssignmentPropertyAccessorKind
        };
        var qualifiedCompletionContext = callableContext with
        {
            ResultTargetName = null,
            SetterPropertyName = null
        };

        if (TryGetConditionalCallResultMemberCompletionDefinitions(
                currentDocument,
                syntaxTree,
                line,
                character,
                out var conditionalCallResultMembers))
        {
            return Complete(
                CreateDefinitionCandidates(FilterDefinitions(
                    conditionalCallResultMembers,
                    expectation,
                    qualifiedCompletionContext)),
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
            && TryGetSourceQualifierCompletionDefinitions(
                currentDocument,
                line,
                character,
                positionSyntax,
                qualifiedCompletionContext,
                out var sourceQualifierDefinitions))
        {
            return Complete(
                CreateDefinitionCandidates(FilterDefinitions(
                    sourceQualifierDefinitions,
                    expectation,
                    qualifiedCompletionContext)),
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
                    qualifiedCompletionContext)),
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
                    qualifiedCompletionContext)),
                positionSyntax.CompletionReplacementRange);
        }

        var visibleDefinitions = nameResolution.GetRankedCompletionDefinitions(
            uri,
            new VbaPosition(line, character),
            definition => IsAllowedDefinition(
                    definition,
                    expectation,
                    callableContext),
            definition => !nameResolution.IsTypeDefinition(definition));
        var sourceQualifiers = nameResolution.GetCompletionSourceQualifiers(
            uri,
            new VbaPosition(line, character));
        var referenceQualifiers = nameResolution.GetCompletionReferenceQualifiers(
            uri,
            new VbaPosition(line, character));
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
                CreateDefinitionCandidates(FilterRankedDefinitions(
                    visibleDefinitions,
                    expectation,
                    callableContext)),
            VbaCompletionExpectation.ProcedureStatement =>
                CreateDefinitionCandidates(FilterRankedDefinitions(
                    visibleDefinitions,
                    expectation,
                    callableContext))
                    .Concat(CreateQualifierCandidates(
                        sourceQualifiers,
                        currentDocument.ModuleName))
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
                CreateDefinitionCandidates(FilterRankedDefinitions(
                    visibleDefinitions,
                    expectation,
                    callableContext)),
            VbaCompletionExpectation.TypeName =>
                CreateDefinitionCandidates(GetTypeCompletionDefinitions(currentDocument, typeQualifier))
                    .Concat(CreateQualifierCandidates(
                        typeQualifier is null ? sourceQualifiers : [],
                        currentDocument.ModuleName))
                    .Concat(CreateReferenceQualifierCandidates(typeQualifier is null ? referenceQualifiers : []))
                    .Concat(typeQualifier is null
                        ? CreateVocabularyCandidates(VbaLanguageVocabulary.TypeNames)
                        : []),
            VbaCompletionExpectation.CreatableType =>
                CreateDefinitionCandidates(GetTypeCompletionDefinitions(currentDocument, typeQualifier)
                    .Where(candidate => candidate.Definition.IsCreatable))
                    .Concat(CreateQualifierCandidates(
                        typeQualifier is null ? sourceQualifiers : [],
                        currentDocument.ModuleName))
                    .Concat(CreateReferenceQualifierCandidates(typeQualifier is null ? referenceQualifiers : [])),
            VbaCompletionExpectation.ImplementsType =>
                CreateDefinitionCandidates(GetTypeCompletionDefinitions(currentDocument, typeQualifier)
                    .Where(candidate => candidate.Definition.Kind == VbaSourceDefinitionKind.Class)
                    .Where(candidate => !VbaProjectIdentityModel.SameDocument(
                        candidate.Definition.Uri,
                        currentDocument.Uri))),
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
                HasRaiseEventPlacementDiagnostic(syntaxTree, line, character)
                    ? []
                    : CreateDefinitionCandidates(currentDocument.Definitions
                        .Where(definition => definition.IsEventNameCompletionEligible)),
            VbaCompletionExpectation.LabelName =>
                CreateLabelCandidates(syntaxTree, positionSyntax),
            VbaCompletionExpectation.ContractDeclarationName =>
                CreateContractDeclarationNameCandidates(
                    currentDocument,
                    positionSyntax),
            _ => []
        };

        return Complete(
            candidates.Concat(CreateVocabularyCandidates(positionSyntax.SupplementalSyntaxWords)),
            positionSyntax.CompletionReplacementRange);
    }

    private static bool AllowsCompletionInvocation(
        VbaCompletionInvocation invocation,
        VbaPositionSyntax positionSyntax)
    {
        if (invocation.Kind != VbaCompletionInvocationKind.TriggerCharacter)
        {
            return true;
        }

        return invocation.TriggerCharacter switch
        {
            "_" => positionSyntax.CompletionExpectation
                == VbaCompletionExpectation.ContractDeclarationName,
            " " => positionSyntax.CompletionExpectation
                    != VbaCompletionExpectation.ContractDeclarationName
                || positionSyntax.CallableDeclarationName?.Fragment.Length == 0,
            _ => true
        };
    }

    private bool TryGetConditionalCallResultMemberCompletionDefinitions(
        VbaSourceDocument currentDocument,
        VbaSyntaxTree syntaxTree,
        int line,
        int character,
        out IReadOnlyList<VbaSourceDefinition> definitions)
    {
        definitions = [];
        if (line < 0
            || line >= syntaxTree.SourceText.Lines.Count
            || character < 0
            || character > syntaxTree.SourceText.Lines[line].Text.Length)
        {
            return false;
        }

        var positionOffset = syntaxTree.SourceText.Lines[line].StartOffset
            + character;
        var argumentList = syntaxTree.Module.ArgumentLists
            .Where(candidate => candidate.Form == VbaCallSyntaxForm.Parenthesized)
            .Where(candidate => candidate.CalleeRange is not null)
            .Where(candidate => candidate.Range.End.Offset < positionOffset)
            .Where(candidate => candidate.Range.End.Offset
                < syntaxTree.SourceText.Text.Length)
            .Where(candidate => syntaxTree.SourceText.Text[
                candidate.Range.End.Offset] == '.')
                .Where(candidate => syntaxTree.SourceText.Text[
                    (candidate.Range.End.Offset + 1)..positionOffset]
                .All(VbaSourceText.IsIdentifierCharacter))
            .OrderByDescending(candidate => candidate.Range.End.Offset)
            .FirstOrDefault();
        if (argumentList is null)
        {
            return false;
        }

        var compatibility = AnalyzeCompleteCall(
            currentDocument.Uri,
            argumentList);
        if (compatibility is not null
            && typeResolution.TryResolveConditionalCallResultType(
                currentDocument,
                compatibility,
                out var resultType))
        {
            definitions = memberChainResolution.GetMembersOfType(
                currentDocument,
                resultType);
        }

        return true;
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
        var target = ResolveSourceTarget(uri, line, character);
        return target switch
        {
            VbaHostEventNameTarget hostEventTarget
                => hostEventTarget.NavigableDefinition,
            VbaWithEventsEventNameTarget withEventsTarget
                when withEventsTarget.EventTargets.All(eventTarget =>
                    eventTarget is VbaHostEventNameTarget)
                => withEventsTarget.EventTargets
                    .OfType<VbaHostEventNameTarget>()
                    .Select(eventTarget => eventTarget.NavigableDefinition)
                    .FirstOrDefault(definition => definition is not null),
            _ => target?.SelectedDefinition
        };
    }

    internal VbaResolvedNameTarget? ResolveSourceTarget(
        string uri,
        int line,
        int character)
        => ResolveSourceTarget(
            uri,
            line,
            character,
            retargetConditionalPropertyAccessor: true);

    private VbaResolvedNameTarget? ResolveSourceTarget(
        string uri,
        int line,
        int character,
        bool retargetConditionalPropertyAccessor)
    {
        var currentDocument = definitionCandidates.FindDocument(uri);
        if (currentDocument is null)
        {
            return null;
        }

        var syntaxTree = GetSyntaxTree(currentDocument);
        var positionSyntax = syntaxTree.GetPositionSyntax(line, character);
        var identifier = positionSyntax.Identifier;
        if (positionSyntax.Region != VbaPositionRegion.Code || identifier is null)
        {
            return null;
        }
        var propertyUsageSyntax = syntaxTree.GetPositionSyntax(
            identifier.Range.End.Line,
            identifier.Range.End.Character);
        var propertyUsageExpectation = propertyUsageSyntax
            .CompletionExpectation;
        var qualifier = GetImmediateQualifier(
            positionSyntax.MemberAccess,
            identifier);
        var callableContext = GetCurrentCallableCompletionContext(
            syntaxTree,
            line,
            character);
        var isCurrentResultTarget = positionSyntax.MemberAccess is null
            && callableContext.ResultTargetName is not null
            && identifier.Name.Equals(
                callableContext.ResultTargetName,
                StringComparison.OrdinalIgnoreCase);
        var requestedWriteAccessorKind = isCurrentResultTarget
            ? null
            : propertyUsageSyntax.AssignmentPropertyAccessorKind;

        if (positionSyntax.TypeReference is not null
            && typeResolution.TryResolveTypeReferenceDefinition(
                currentDocument,
                positionSyntax.TypeReference,
                identifier,
                out var typeDefinition))
        {
            return typeDefinition is null
                ? null
                : resolutionPolicy.CreateNameTarget(typeDefinition);
        }

        if (positionSyntax.CompletionExpectation == VbaCompletionExpectation.EventName)
        {
            var argumentList = syntaxTree.Module.ArgumentLists.SingleOrDefault(candidate =>
                candidate.CalleeRange == identifier.Range);
            if (argumentList is not null)
            {
                if (!callSiteResolution.TryResolveRaiseEventTarget(
                    currentDocument,
                    argumentList,
                    out var raiseEventTarget)
                    || HasRaiseEventPlacementDiagnostic(syntaxTree, argumentList))
                {
                    return null;
                }

                return raiseEventTarget;
            }

            var sourceLine = syntaxTree.SourceText.Lines[identifier.Range.End.Line];
            var callPositionSyntax = syntaxTree.GetPositionSyntax(
                identifier.Range.End.Line,
                Math.Min(
                    sourceLine.Text.Length,
                    identifier.Range.End.Character + 1));
            if (!callSiteResolution.TryResolveRaiseEventTarget(
                    currentDocument,
                    callPositionSyntax.CallSite,
                    out var incompleteRaiseEventTarget)
                || HasRaiseEventPlacementDiagnostic(
                    syntaxTree,
                    identifier.Range.Start.Offset))
            {
                return null;
            }

            return incompleteRaiseEventTarget;
        }

        var position = new VbaPosition(line, character);
        var declaredDefinition = currentDocument.Definitions.FirstOrDefault(
            definition => Contains(definition.Range, position)
                && definition.Name.Equals(
                    identifier.Name,
                    StringComparison.OrdinalIgnoreCase));
        if (declaredDefinition?.Kind == VbaSourceDefinitionKind.Event)
        {
            return resolutionPolicy.CreateNameTarget(declaredDefinition);
        }

        if (declaredDefinition is not null
            && hostClassEvents.AnalyzeIntrinsicHandler(
                currentDocument,
                declaredDefinition) is { } intrinsicHandler)
        {
            var prefixLength = intrinsicHandler.Surface.Projection
                .IntrinsicEventSourceName.Length;
            var identifierOffset = character
                - declaredDefinition.Range.Start.Character;
            if (line == declaredDefinition.Range.Start.Line
                && identifierOffset > prefixLength)
            {
                return intrinsicHandler.EventTarget;
            }

            return resolutionPolicy.CreateNameTarget(declaredDefinition);
        }

        if (declaredDefinition is not null
            && TryResolveWithEventsHandler(
                currentDocument,
                declaredDefinition,
                out var variableTarget,
                out var eventTarget,
                out var decomposition))
        {
            var identifierOffset = character - declaredDefinition.Range.Start.Character;
            if (line == declaredDefinition.Range.Start.Line
                && identifierOffset < decomposition.VariableName.Length)
            {
                return variableTarget;
            }

            if (line == declaredDefinition.Range.Start.Line
                && identifierOffset > decomposition.VariableName.Length)
            {
                return eventTarget;
            }

            return resolutionPolicy.CreateNameTarget(declaredDefinition);
        }

        if (declaredDefinition is not null
            && interfaceSemantics.TryResolveSourceInterfaceDeclarationPrefix(
                currentDocument,
                declaredDefinition,
                out var interfaceTarget,
                out var interfacePrefixLength)
            && line == declaredDefinition.Range.Start.Line
            && character - declaredDefinition.Range.Start.Character
                < interfacePrefixLength)
        {
            return interfaceTarget;
        }

        if (declaredDefinition is not null)
        {
            return resolutionPolicy.CreateNameTarget(declaredDefinition);
        }

        if (TryResolveMemberDefinition(
            currentDocument,
            line,
            character,
            positionSyntax,
            out var memberDefinition))
        {
            var memberTarget = memberDefinition is null
                ? null
                : resolutionPolicy.CreateNameTarget(memberDefinition);
            return retargetConditionalPropertyAccessor
                ? RetargetConditionalPropertyAccessor(
                    currentDocument,
                    propertyUsageExpectation,
                    isCurrentResultTarget,
                    requestedWriteAccessorKind,
                    memberTarget)
                : memberTarget;
        }

        if (positionSyntax.MemberAccess is { IsLeadingDot: false } moduleAccess
            && moduleAccess.TargetSegmentIndex == 0
            && moduleAccess.Segments.Count > 1)
        {
            if (!nameResolution.HasLocalSourceQualifierShadow(
                    currentDocument,
                    new VbaPosition(line, character),
                    identifier.Name))
            {
                var moduleOutcome = resolutionPolicy.ResolveRankedCandidatesOutcome(
                    definitionCandidates
                        .GetSourceCandidates(identifier.Name)
                        .Select(candidate => candidate.Definition)
                        .Where(CanUseAsSourceModuleValueQualifier)
                        .Select(definition => new VbaRankedDefinition(
                            definition,
                            VbaResolutionPolicy.ProjectRank)),
                    referenceSelection: null);
                if (moduleOutcome.Target is not null)
                {
                    return moduleOutcome.Target;
                }
            }
        }

        var outcome = qualifier is null
            ? nameResolution.ResolveValueOutcome(
                uri,
                new VbaPosition(line, character),
                qualifier: null,
                identifier.Name)
            : nameResolution.ResolvePreferredOutcome(
                uri,
                new VbaPosition(line, character),
                qualifier,
                identifier.Name,
                definition => !nameResolution.IsTypeDefinition(definition));
        return retargetConditionalPropertyAccessor
            ? RetargetConditionalPropertyAccessor(
                currentDocument,
                propertyUsageExpectation,
                isCurrentResultTarget,
                requestedWriteAccessorKind,
                outcome.Target)
            : outcome.Target;
    }

    private static bool IsModuleIdentityDefinition(VbaSourceDefinition definition)
        => definition.Kind is VbaSourceDefinitionKind.Module
            or VbaSourceDefinitionKind.Class
            or VbaSourceDefinitionKind.Form;

    private bool CanUseAsSourceModuleValueQualifier(
        VbaSourceDefinition definition)
    {
        if (definition.Kind == VbaSourceDefinitionKind.Module)
        {
            return true;
        }

        if (!IsModuleIdentityDefinition(definition))
        {
            return false;
        }

        var document = definitionCandidates.FindDocument(definition.Uri);
        var syntaxTree = document is null ? null : GetSyntaxTree(document);
        return syntaxTree?.Module.Attributes
            .LastOrDefault(attribute => attribute.Name.Equals(
                "VB_PredeclaredId",
                StringComparison.OrdinalIgnoreCase))?
            .Value.Equals("True", StringComparison.OrdinalIgnoreCase)
            == true;
    }

    private static bool HasRaiseEventPlacementDiagnostic(
        VbaSyntaxTree syntaxTree,
        VbaArgumentListSyntax argumentList)
    {
        var calleeRange = argumentList.CalleeRange;
        if (calleeRange is null)
        {
            return true;
        }

        return HasRaiseEventPlacementDiagnostic(
            syntaxTree,
            calleeRange.Start.Offset);
    }

    private static bool HasRaiseEventPlacementDiagnostic(
        VbaSyntaxTree syntaxTree,
        int line,
        int character)
    {
        if (line < 0 || line >= syntaxTree.SourceText.Lines.Count)
        {
            return true;
        }

        var sourceLine = syntaxTree.SourceText.Lines[line];
        var offset = sourceLine.StartOffset
            + Math.Clamp(character, 0, sourceLine.Text.Length);
        return HasRaiseEventPlacementDiagnostic(syntaxTree, offset);
    }

    private static bool HasRaiseEventPlacementDiagnostic(
        VbaSyntaxTree syntaxTree,
        int beforeOffset)
    {
        var raiseEventKeyword = syntaxTree.TokenStream.Tokens.LastOrDefault(token =>
            token.Kind == VbaTokenKind.Keyword
            && token.Text.Equals("RaiseEvent", StringComparison.OrdinalIgnoreCase)
            && token.Range.End.Offset <= beforeOffset);
        return raiseEventKeyword is null
            || syntaxTree.Diagnostics.Any(diagnostic =>
                diagnostic.Code == "syntax.raiseEventStatementNotAllowedHere"
                && diagnostic.Range == raiseEventKeyword.Range);
    }

    private static VbaResolvedNameTarget? RetargetConditionalPropertyAccessor(
        VbaSourceDocument currentDocument,
        VbaCompletionExpectation propertyUsageExpectation,
        bool isCurrentResultTarget,
        VbaPropertyAccessorKind? requestedWriteAccessorKind,
        VbaResolvedNameTarget? target)
    {
        if (target is not VbaPropertyNameTarget property
            || !property.AccessorTargets.Any(
                accessorTarget => accessorTarget.IsConditionalFamily))
        {
            return target;
        }

        var definitionFilter = GetPropertyUsageFilter(
            propertyUsageExpectation,
            isCurrentResultTarget);
        if (definitionFilter is null)
        {
            return target;
        }

        var eligibleAccessors = property.AccessorTargets
            .Select(accessorTarget => new
            {
                Target = accessorTarget,
                Definitions = accessorTarget.PhysicalDefinitions
                    .Where(definition => VbaProjectIdentityModel.SameDocument(
                            currentDocument.Uri,
                            definition.Uri)
                        || definition.Visibility.IsProjectVisible())
                    .Where(definitionFilter)
                    .ToArray()
            })
            .Where(candidate => candidate.Definitions.Length > 0)
            .GroupBy(candidate => candidate.Target.Identity)
            .Select(group => group.First())
            .ToArray();
        if (requestedWriteAccessorKind is not null)
        {
            eligibleAccessors = eligibleAccessors
                .Where(candidate => candidate.Definitions.Any(
                    definition => definition.PropertyAccessorKind
                        == requestedWriteAccessorKind))
                .ToArray();
        }

        return eligibleAccessors.Length == 1
            ? new VbaPropertyNameTarget(
                property.Property,
                eligibleAccessors[0].Definitions[0])
            : null;
    }

    private static Func<VbaSourceDefinition, bool>? GetPropertyUsageFilter(
        VbaCompletionExpectation expectation,
        bool isCurrentResultTarget)
        => expectation switch
        {
            VbaCompletionExpectation.AssignmentTarget
                when isCurrentResultTarget => IsReadableDefinition,
            VbaCompletionExpectation.AssignmentTarget => IsWritableDefinition,
            VbaCompletionExpectation.ExpressionValue
                or VbaCompletionExpectation.CallArgument
                or VbaCompletionExpectation.NamedArgumentValue => IsReadableDefinition,
            _ => null
        };

    internal VbaNameResolutionOutcome ClassifySourceDefinition(
        string uri,
        int line,
        int character)
    {
        var target = ResolveSourceTarget(uri, line, character);
        if (target is not null)
        {
            return VbaNameResolutionOutcome.Resolved(target);
        }

        var currentDocument = definitionCandidates.FindDocument(uri);
        if (currentDocument is null)
        {
            return VbaNameResolutionOutcome.AnalysisIncomplete;
        }

        var positionSyntax = GetSyntaxTree(currentDocument)
            .GetPositionSyntax(line, character);
        var identifier = positionSyntax.Identifier;
        if (positionSyntax.Region != VbaPositionRegion.Code
            || identifier is null)
        {
            return VbaNameResolutionOutcome.NonSemantic;
        }

        if (positionSyntax.TypeReference is not null
            && typeResolution.TryClassifyTypeReferenceDefinition(
                currentDocument,
                positionSyntax.TypeReference,
                identifier,
                out var typeOutcome))
        {
            return typeOutcome;
        }

        var position = new VbaPosition(line, character);
        var declaredDefinition = currentDocument.Definitions.FirstOrDefault(
            definition => Contains(definition.Range, position)
                && definition.Name.Equals(
                    identifier.Name,
                    StringComparison.OrdinalIgnoreCase));
        if (declaredDefinition is not null
            && TryResolveWithEventsHandler(
                currentDocument,
                declaredDefinition,
                out _,
                out _,
                out _))
        {
            return VbaNameResolutionOutcome.AnalysisIncomplete;
        }

        var qualifier = GetImmediateQualifier(
            positionSyntax.MemberAccess,
            identifier);
        return qualifier is null
            ? nameResolution.ResolveValueOutcome(
                uri,
                new VbaPosition(line, character),
                qualifier: null,
                identifier.Name)
            : nameResolution.ResolvePreferredOutcome(
                uri,
                new VbaPosition(line, character),
                qualifier,
                identifier.Name,
                definition => !nameResolution.IsTypeDefinition(definition));
    }

    /// <summary>
    /// Resolves callable signature help at a source position.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="line">The zero-based line.</param>
    /// <param name="character">The zero-based character.</param>
    /// <returns>The signature help result, or null when no callable resolves.</returns>
    public VbaSignatureHelp? GetSignatureHelp(
        string uri,
        int line,
        int character,
        VbaSignaturePresentationIdentity? retriggerIdentity = null)
    {
        var currentDocument = definitionCandidates.FindDocument(uri);
        if (currentDocument is null)
        {
            return null;
        }

        var syntaxTree = GetSyntaxTree(currentDocument);
        var contractSignatureHelp = TryGetContractDeclarationSignatureHelp(
            currentDocument,
            syntaxTree,
            line,
            character,
            retriggerIdentity);
        if (contractSignatureHelp is not null)
        {
            return contractSignatureHelp;
        }

        var interfaceSignatureHelp = interfaceSemantics.GetAccessorSignatureHelp(
            currentDocument,
            line,
            character,
            retriggerIdentity);
        if (interfaceSignatureHelp is not null)
        {
            return interfaceSignatureHelp;
        }

        var handlerSignatureHelp = TryGetWithEventsHandlerSignatureHelp(
            currentDocument,
            syntaxTree,
            line,
            character,
            retriggerIdentity);
        if (handlerSignatureHelp is not null)
        {
            return handlerSignatureHelp;
        }

        var positionSyntax = syntaxTree.GetPositionSyntax(line, character);
        if (callSiteResolution.IsRaiseEventCall(currentDocument, positionSyntax.CallSite)
            && HasRaiseEventPlacementDiagnostic(syntaxTree, line, character))
        {
            return null;
        }

        return callSiteResolution.GetSignatureHelp(
            currentDocument,
            line,
            character,
            positionSyntax,
            retriggerIdentity,
            interfaceSemantics.ProjectSourceInterfaceDocumentation);
    }

    private VbaSignatureHelp? TryGetContractDeclarationSignatureHelp(
        VbaSourceDocument currentDocument,
        VbaSyntaxTree syntaxTree,
        int line,
        int character,
        VbaSignaturePresentationIdentity? retriggerIdentity)
    {
        var position = new VbaSyntaxPosition(line, character, 0);
        var callable = syntaxTree.Module.CallableDeclarations.FirstOrDefault(
            candidate => candidate.ParameterListRange is { } parameterListRange
                && Contains(parameterListRange, position));
        if (callable is null)
        {
            return null;
        }

        var declaration = currentDocument.Definitions.FirstOrDefault(definition =>
            definition.Kind is VbaSourceDefinitionKind.Procedure
                or VbaSourceDefinitionKind.Property
            && definition.Range.Start.Line == callable.Range.Start.Line
            && definition.Name.Equals(
                callable.Name,
                StringComparison.OrdinalIgnoreCase)
            && definition.PropertyAccessorKind == callable.PropertyAccessorKind);
        if (declaration is null
            || !TryGetCallableDeclarationNameKind(
                declaration,
                out var declarationKind))
        {
            return null;
        }

        var activeParameterIndex = VbaInterfaceSemanticModel
            .GetPhysicalParameterIndex(syntaxTree, callable, position);
        var variants = new List<VbaSignatureHelpVariant>();
        if (declarationKind == VbaCallableDeclarationNameKind.Sub)
        {
            var intrinsicAnalysis = hostClassEvents.AnalyzeIntrinsicHandler(
                currentDocument,
                declaration);
            if (intrinsicAnalysis?.EventTarget.EventContract.Signature is not null)
            {
                var signature = VbaHostClassEventSemanticModel
                    .CreateHandlerSignature(
                        intrinsicAnalysis.Surface,
                        intrinsicAnalysis.HostEvent);
                variants.Add(CreateContractSignatureHelpVariant(
                    signature,
                    activeParameterIndex,
                    isConditional: false));
            }

            var eventSignatures = AnalyzeWithEventsHandler(
                    currentDocument,
                    declaration)
                ?.BindingSet.ResolvedEventSignatures;
            if (eventSignatures is not null)
            {
                variants.AddRange(eventSignatures.Contracts
                    .Where(contract => contract.Signature is not null)
                    .Select(contract => CreateContractSignatureHelpVariant(
                        contract.Signature!,
                        activeParameterIndex,
                        contract.IsConditionalContract)));
            }
        }

        variants.AddRange(interfaceSemantics
            .GetDeclarationNameCompletionOrigins(
                currentDocument,
                declarationKind)
            .SelectMany(origin => origin.Members.Select(member => new
            {
                FullName = origin.Prefix + member.Name,
                Member = member
            }))
            .Where(candidate => candidate.Member.Signature is not null
                && candidate.FullName.Equals(
                    declaration.Name,
                    StringComparison.OrdinalIgnoreCase))
            .Select(candidate => CreateContractSignatureHelpVariant(
                candidate.Member.Signature!,
                activeParameterIndex,
                candidate.Member.IsConditionalContract)));
        variants = CoalesceContractSignatureHelpVariants(variants);
        if (variants.Count == 0)
        {
            return null;
        }

        var activeSignature = 0;
        if (retriggerIdentity is not null)
        {
            var retainedIndex = variants.FindIndex(
                variant => variant.PresentationIdentity.Matches(
                    retriggerIdentity));
            if (retainedIndex >= 0)
            {
                activeSignature = retainedIndex;
            }
        }

        var active = variants[activeSignature];
        return new VbaSignatureHelp(
            active.Signature,
            active.ActiveParameter,
            variants,
            activeSignature);
    }

    private static List<VbaSignatureHelpVariant> CoalesceContractSignatureHelpVariants(
        IEnumerable<VbaSignatureHelpVariant> variants)
    {
        var result = new List<VbaSignatureHelpVariant>();
        foreach (var variant in variants)
        {
            var identity = variant.PresentationIdentity;
            if (result.Any(existing =>
                    existing.PresentationIdentity.Matches(identity)))
            {
                continue;
            }

            result.Add(variant);
        }

        return result;
    }

    private static VbaSignatureHelpVariant CreateContractSignatureHelpVariant(
        VbaCallableSignature signature,
        int? physicalParameterIndex,
        bool isConditional)
        => new(
            signature,
            physicalParameterIndex is int parameterIndex
                    && parameterIndex < signature.Parameters.Count
                ? parameterIndex
                : null,
            isConditional);

    private static bool TryGetCallableDeclarationNameKind(
        VbaSourceDefinition declaration,
        out VbaCallableDeclarationNameKind declarationKind)
    {
        if (declaration.Kind == VbaSourceDefinitionKind.Property)
        {
            declarationKind = declaration.PropertyAccessorKind switch
            {
                VbaPropertyAccessorKind.Get =>
                    VbaCallableDeclarationNameKind.PropertyGet,
                VbaPropertyAccessorKind.Let =>
                    VbaCallableDeclarationNameKind.PropertyLet,
                VbaPropertyAccessorKind.Set =>
                    VbaCallableDeclarationNameKind.PropertySet,
                _ => default
            };
            return declaration.PropertyAccessorKind is not null;
        }

        declarationKind = declaration.CallableKind switch
        {
            VbaCallableKind.Sub => VbaCallableDeclarationNameKind.Sub,
            VbaCallableKind.Function => VbaCallableDeclarationNameKind.Function,
            _ => default
        };
        return declaration.Kind == VbaSourceDefinitionKind.Procedure
            && declaration.CallableKind is VbaCallableKind.Sub
                or VbaCallableKind.Function;
    }

    private VbaSignatureHelp? TryGetWithEventsHandlerSignatureHelp(
        VbaSourceDocument currentDocument,
        VbaSyntaxTree syntaxTree,
        int line,
        int character,
        VbaSignaturePresentationIdentity? retriggerIdentity)
    {
        var position = new VbaSyntaxPosition(line, character, 0);
        var callable = syntaxTree.Module.CallableDeclarations.FirstOrDefault(
            candidate => candidate.ParameterListRange is { } parameterListRange
                && Contains(parameterListRange, position));
        if (callable is null)
        {
            return null;
        }

        var handler = currentDocument.Definitions.FirstOrDefault(definition =>
            definition.Kind is VbaSourceDefinitionKind.Procedure
                or VbaSourceDefinitionKind.Property
            && definition.Range.Start.Line == callable.Range.Start.Line
            && definition.Name.Equals(
                callable.Name,
                StringComparison.OrdinalIgnoreCase)
            && definition.PropertyAccessorKind == callable.PropertyAccessorKind);
        if (handler is null)
        {
            return null;
        }

        var intrinsicAnalysis = hostClassEvents.AnalyzeIntrinsicHandler(
            currentDocument,
            handler);
        if (intrinsicAnalysis?.EventTarget.EventContract.Signature
            is not null)
        {
            var intrinsicParameterIndex = GetHandlerParameterIndex(
                callable,
                position);
            var signature = VbaHostClassEventSemanticModel
                .CreateHandlerSignature(
                    intrinsicAnalysis.Surface,
                    intrinsicAnalysis.HostEvent);
            int? activeParameter = intrinsicParameterIndex is int parameterIndex
                    && parameterIndex < signature.Parameters.Count
                ? parameterIndex
                : null;
            var variant = new VbaSignatureHelpVariant(
                signature,
                activeParameter,
                IsConditionalVariant: false);
            return new VbaSignatureHelp(
                signature,
                activeParameter,
                [variant]);
        }

        var analysis = AnalyzeWithEventsHandler(currentDocument, handler);
        var signatureSet = analysis?.BindingSet.ResolvedEventSignatures;
        if (signatureSet is null)
        {
            return null;
        }

        var handlerParameterIndex = GetHandlerParameterIndex(callable, position);
        var variants = signatureSet.Contracts
            .Where(contract => contract.Signature is not null)
            .Select(contract => new VbaSignatureHelpVariant(
                contract.Signature!,
                handlerParameterIndex is int parameterIndex
                        && parameterIndex < contract.Signature!.Parameters.Count
                    ? parameterIndex
                    : null,
                contract.IsConditionalContract))
            .ToArray();
        if (variants.Length == 0)
        {
            return null;
        }

        var activeSignature = 0;
        if (retriggerIdentity is not null)
        {
            var retainedIndex = Array.FindIndex(
                variants,
                variant => variant.PresentationIdentity.Matches(retriggerIdentity));
            if (retainedIndex >= 0)
            {
                activeSignature = retainedIndex;
            }
        }

        var activeVariant = variants[activeSignature];
        return new VbaSignatureHelp(
            activeVariant.Signature,
            activeVariant.ActiveParameter,
            variants,
            activeSignature);
    }

    private static int? GetHandlerParameterIndex(
        VbaCallableDeclarationSyntax callable,
        VbaSyntaxPosition position)
    {
        if (callable.Parameters.Count == 0)
        {
            return null;
        }

        for (var index = 0; index < callable.Parameters.Count; index++)
        {
            if (Contains(callable.Parameters[index].Range, position))
            {
                return index;
            }

            if (Compare(position, callable.Parameters[index].Range.Start) < 0)
            {
                return index;
            }
        }

        return callable.Parameters.Count - 1;
    }

    internal VbaConditionalCallCompatibility? AnalyzeCompleteCall(
        string uri,
        VbaArgumentListSyntax argumentList)
    {
        var currentDocument = definitionCandidates.FindDocument(uri);
        var calleeRange = argumentList.CalleeRange;
        if (currentDocument is null || calleeRange is null)
        {
            return null;
        }

        if (IsCallableResultAssignment(currentDocument, argumentList, calleeRange))
        {
            return null;
        }

        VbaResolvedNameTarget? target;
        var isRaiseEvent = callSiteResolution.TryResolveRaiseEventTarget(
                currentDocument,
                argumentList,
                out target);
        if (isRaiseEvent
            && HasRaiseEventPlacementDiagnostic(
                GetSyntaxTree(currentDocument),
                argumentList))
        {
            return null;
        }

        if (!isRaiseEvent)
        {
            target = ResolveSourceTarget(
                uri,
                calleeRange.End.Line,
                Math.Max(
                    calleeRange.Start.Character,
                    calleeRange.End.Character - 1),
                retargetConditionalPropertyAccessor: false);
        }

        if (target is null
            || argumentList.Form == VbaCallSyntaxForm.PropertyAssignment
                && !target.PhysicalDefinitions.Any(definition =>
                    definition.Kind == VbaSourceDefinitionKind.Property))
        {
            return null;
        }

        return callSiteResolution.AnalyzeCompleteCall(
            currentDocument,
            argumentList,
            target);
    }

    internal bool TryResolveRaiseEventTarget(
        string uri,
        VbaArgumentListSyntax argumentList,
        out VbaResolvedNameTarget? target)
    {
        var currentDocument = definitionCandidates.FindDocument(uri);
        if (currentDocument is null)
        {
            target = null;
            return false;
        }

        return callSiteResolution.TryResolveRaiseEventTarget(
            currentDocument,
            argumentList,
            out target);
    }

    internal bool TryResolveRaiseEventTarget(
        string uri,
        VbaCallSiteSyntax? callSite,
        out VbaResolvedNameTarget? target)
    {
        var currentDocument = definitionCandidates.FindDocument(uri);
        if (currentDocument is null)
        {
            target = null;
            return false;
        }

        return callSiteResolution.TryResolveRaiseEventTarget(
            currentDocument,
            callSite,
            out target);
    }

    internal static bool IsCallableResultAssignment(
        VbaSourceDocument currentDocument,
        VbaArgumentListSyntax argumentList,
        VbaSyntaxRange calleeRange)
    {
        if (argumentList.Form != VbaCallSyntaxForm.PropertyAssignment
            || argumentList.Callee.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        var syntaxTree = currentDocument.SyntaxTree
            ?? VbaSyntaxTree.ParseModule(currentDocument.Uri, currentDocument.Text);
        return syntaxTree.Module.CallableDeclarations.Any(declaration =>
            declaration.BlockRange.Start.Offset <= calleeRange.Start.Offset
            && calleeRange.End.Offset <= declaration.BlockRange.End.Offset
            && declaration.Name.Equals(
                argumentList.Callee,
                StringComparison.OrdinalIgnoreCase)
            && (declaration.PropertyAccessorKind == VbaPropertyAccessorKind.Get
                || declaration.Kind == VbaDeclarationKind.Procedure
                    && declaration.DeclarationKeyword?.Equals(
                        "Function",
                        StringComparison.OrdinalIgnoreCase) == true));
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
        var isContextualIdentifier = VbaIdentifier.IsIdentifier(occurrence.Name);
        if (!isContextualIdentifier
            && VbaLanguageVocabulary.CanonicalKeywords.TryGetValue(occurrence.Name, out var keyword))
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

        var positionSyntax = GetSyntaxTree(document).GetPositionSyntax(
            lineIndex,
            occurrence.Start);
        if (canonicalNamesByRange.TryGetValue(occurrenceRange, out var resolvedCanonicalName))
        {
            if (positionSyntax.MemberAccess is
                    {
                        IsLeadingDot: false,
                        TargetSegmentIndex: 0,
                        Segments.Count: > 1
                    } access
                && ResolveSourceTarget(
                    document.Uri,
                    lineIndex,
                    occurrence.Start) is { } resolvedTarget
                && IsModuleIdentityDefinition(resolvedTarget.SelectedDefinition)
                && !TryGetQualifiedCanonicalName(
                    access,
                    occurrence,
                    document.Uri,
                    lineIndex,
                    out _))
            {
                return null;
            }

            return resolvedCanonicalName;
        }

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

        var definition = nameResolution.ResolveValue(
            document.Uri,
            new VbaPosition(lineIndex, occurrence.Start),
            qualifier: null,
            occurrence.Name);
        if (definition is not null)
        {
            return definition.Name;
        }

        return isContextualIdentifier
            && VbaLanguageVocabulary.CanonicalKeywords.TryGetValue(occurrence.Name, out keyword)
                ? keyword
                : null;
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
                positionSyntax.EnclosingWithScopes)
            .SelectMany(definition =>
                ExpandLogicalCompletionDefinitions(
                    currentDocument,
                    definition))
            .ToArray();
        return true;
    }

    private IEnumerable<VbaSourceDefinition> ExpandLogicalCompletionDefinitions(
        VbaSourceDocument currentDocument,
        VbaSourceDefinition definition)
    {
        var target = resolutionPolicy.CreateNameTarget(definition);
        IReadOnlyList<VbaSourceDefinition> physicalDefinitions;
        if (target is VbaConditionalFamilyNameTarget)
        {
            physicalDefinitions = target.PhysicalDefinitions;
        }
        else if (target is VbaPropertyNameTarget property
            && property.AccessorTargets.Any(
                accessorTarget => accessorTarget.IsConditionalFamily))
        {
            physicalDefinitions = property.Property
                .IsUnifiedConditionalFamily
                    ? property.Property.UnifiedPhysicalDefinitions
                    : property.Property.PropertyDefinitions;
        }
        else
        {
            return [definition];
        }

        return physicalDefinitions.Where(variant =>
            VbaProjectIdentityModel.SameDocument(
                variant.Uri,
                currentDocument.Uri)
            || variant.Visibility.IsProjectVisible());
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
        VbaCallableCompletionContext callableContext,
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
        var receiverDefinition = nameResolution.ResolveValue(
            currentDocument.Uri,
            new VbaPosition(line, character),
            qualifier: null,
            qualifier);
        if (receiverDefinition is
            {
                Identity.Origin: VbaDefinitionOrigin.ProjectReference,
                ReferenceGlobalExposure: ReferenceDefinitionGlobalExposure.MainHostGlobal
            })
        {
            return false;
        }

        definitions = nameResolution.GetResolvedSourceQualifiedCompletionDefinitions(
            currentDocument,
            new VbaPosition(line, character),
            qualifier,
            definition => !nameResolution.IsTypeDefinition(definition),
            definition => IsAllowedDefinition(
                definition,
                positionSyntax.CompletionExpectation,
                callableContext));
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
        IReadOnlyList<VbaRankedDefinition> visibleDefinitions)
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
                FilterText: parameter.Name,
                IsConditionalFamily: availability.IsConditionalNamedParameter(
                    parameter.Name))));
        return candidates;
    }

    private IEnumerable<VbaCompletionCandidate> CreateNamedArgumentValueCandidates(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        VbaPositionSyntax positionSyntax,
        IReadOnlyList<VbaRankedDefinition> visibleDefinitions)
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

    private IEnumerable<VbaCompletionCandidate> CreateExpressionCandidates(
        VbaSourceDocument currentDocument,
        IEnumerable<VbaRankedDefinition> definitions)
        => CreateDefinitionCandidates(definitions.Where(candidate =>
                IsReadableDefinition(candidate.Definition)))
            .Concat(CreateVocabularyCandidates(VbaLanguageVocabulary.GetExpressionValueWords(
                GetSyntaxTree(currentDocument).Module.Kind)));

    private IEnumerable<VbaCompletionCandidate> CreateExpressionValueCandidates(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        VbaPositionSyntax positionSyntax,
        IReadOnlyList<VbaRankedDefinition> visibleDefinitions,
        IReadOnlyList<string> sourceQualifiers,
        IReadOnlyList<string> referenceQualifiers)
    {
        if (!IsInsideActiveCallArgument(positionSyntax.CallSite, line, character))
        {
            return CreateExpressionCandidates(currentDocument, visibleDefinitions)
                .Concat(CreateQualifierCandidates(
                    sourceQualifiers,
                    currentDocument.ModuleName))
                .Concat(CreateReferenceQualifierCandidates(referenceQualifiers));
        }

        var availability = callSiteResolution.GetCallArgumentAvailability(
            currentDocument,
            line,
            character,
            positionSyntax);
        return availability.AllowsPositionalExpression
            ? CreateExpressionCandidates(currentDocument, visibleDefinitions)
                .Concat(CreateQualifierCandidates(
                    sourceQualifiers,
                    currentDocument.ModuleName))
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
        => definitions.Where(definition => IsAllowedDefinition(
            definition,
            expectation,
            callableContext));

    private static IEnumerable<VbaRankedDefinition> FilterRankedDefinitions(
        IEnumerable<VbaRankedDefinition> definitions,
        VbaCompletionExpectation expectation,
        VbaCallableCompletionContext callableContext)
        => definitions.Where(candidate => IsAllowedDefinition(
            candidate.Definition,
            expectation,
            callableContext));

    private static bool IsAllowedDefinition(
        VbaSourceDefinition definition,
        VbaCompletionExpectation expectation,
        VbaCallableCompletionContext callableContext)
        => expectation switch
        {
            VbaCompletionExpectation.ExpressionValue
                or VbaCompletionExpectation.CallArgument
                or VbaCompletionExpectation.NamedArgumentValue => IsReadableDefinition(definition),
            VbaCompletionExpectation.AssignmentTarget =>
                (IsWritableDefinition(definition)
                    && MatchesRequestedPropertyWriteAccessor(
                        definition,
                        callableContext.RequestedPropertyWriteAccessorKind)
                    && !IsCurrentSetterProperty(definition, callableContext.SetterPropertyName))
                || IsCurrentResultTarget(definition, callableContext.ResultTargetName),
            VbaCompletionExpectation.ProcedureStatement =>
                IsProcedureStatementDefinition(definition),
            VbaCompletionExpectation.CallableName =>
                IsCallableDefinition(definition),
            _ => false
        };

    private static bool MatchesRequestedPropertyWriteAccessor(
        VbaSourceDefinition definition,
        VbaPropertyAccessorKind? requestedAccessorKind)
        => requestedAccessorKind is null
            || definition.Kind != VbaSourceDefinitionKind.Property
            || definition.PropertyAccessorKind is null
            || definition.PropertyAccessorKind == requestedAccessorKind;

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

    private IEnumerable<VbaCompletionCandidate> CreateDefinitionCandidates(
        IEnumerable<VbaSourceDefinition> definitions)
        => definitions.Select(CreateDefinitionCandidate);

    private IEnumerable<VbaCompletionCandidate> CreateDefinitionCandidates(
        IEnumerable<VbaRankedDefinition> definitions)
        => definitions.Select(candidate => CreateDefinitionCandidate(
            candidate.Definition) with
            {
                SortRank = candidate.Rank
            });

    private VbaCompletionCandidate CreateDefinitionCandidate(
        VbaSourceDefinition definition)
    {
        var target = resolutionPolicy.CreateNameTarget(definition);
        return new VbaCompletionCandidate(
            target.IsConditionalFamily
                ? target.CanonicalName
                : definition.Name,
            VbaCompletionCandidateKind.Definition,
            Definition: definition,
            IsConditionalFamily: target.IsConditionalFamily);
    }

    private static IEnumerable<VbaCompletionCandidate> CreateReferenceQualifierCandidates(
        IEnumerable<string> qualifiers)
        => qualifiers.Select(qualifier => new VbaCompletionCandidate(
            qualifier,
            VbaCompletionCandidateKind.ReferenceQualifier,
            InsertText: $"{qualifier}.",
            FilterText: qualifier)
        {
            SortRank = VbaResolutionPolicy.ReferenceRank
        });

    private static IEnumerable<VbaCompletionCandidate> CreateQualifierCandidates(
        IEnumerable<string> qualifiers,
        string currentModuleName)
        => qualifiers.Select(qualifier => new VbaCompletionCandidate(
            qualifier,
            VbaCompletionCandidateKind.SourceQualifier,
            InsertText: $"{qualifier}.",
            FilterText: qualifier)
        {
            SortRank = qualifier.Equals(currentModuleName, StringComparison.OrdinalIgnoreCase)
                ? VbaResolutionPolicy.CurrentModuleRank
                : VbaResolutionPolicy.ProjectRank
        });

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

    private IEnumerable<VbaCompletionCandidate> CreateContractDeclarationNameCandidates(
        VbaSourceDocument currentDocument,
        VbaPositionSyntax positionSyntax)
    {
        var declarationName = positionSyntax.CallableDeclarationName;
        if (declarationName is null)
        {
            return [];
        }

        return VbaContractDeclarationNameCompletion.CreateCandidates(
            declarationName,
            CreateContractDeclarationNameOrigins(
                currentDocument,
                declarationName.Kind),
            CreateProspectiveDeclaration(currentDocument, declarationName),
            currentDocument.Definitions);
    }

    private IReadOnlyList<VbaContractPrefixCompletionOrigin>
        CreateContractDeclarationNameOrigins(
            VbaSourceDocument currentDocument,
            VbaCallableDeclarationNameKind declarationKind)
    {
        var origins = new List<VbaContractPrefixCompletionOrigin>();
        if (declarationKind == VbaCallableDeclarationNameKind.Sub)
        {
            if (hostClassEvents.TryGetEffectiveSurface(
                    currentDocument,
                    out var surface))
            {
                origins.Add(new VbaContractPrefixCompletionOrigin(
                    surface.Projection.IntrinsicEventSourceName + "_",
                    VbaContractCompletionDomain.HostEvents,
                    IsConditionalPrefix: false,
                    surface.Projection.Events
                        .Where(hostEvent => hostEvent.AuthoringAvailable)
                        .Select(hostEvent => new VbaContractMemberCompletionOrigin(
                            hostEvent.Name,
                            VbaContractCompletionDomain.HostEvents,
                            IsConditionalContract: false,
                            VbaHostClassEventSemanticModel.CreateHandlerSignature(
                                surface,
                                hostEvent),
                            hostEvent.Documentation,
                            Identity: hostEvent))
                        .ToArray()));
            }

            origins.AddRange(
                CreateSourceWithEventsCompletionOrigins(currentDocument));
        }

        origins.AddRange(interfaceSemantics.GetDeclarationNameCompletionOrigins(
            currentDocument,
            declarationKind));
        return origins;
    }

    private static VbaProspectiveDeclaration CreateProspectiveDeclaration(
        VbaSourceDocument currentDocument,
        VbaCallableDeclarationNameSyntax declarationName)
    {
        var (kind, accessorKind) = declarationName.Kind switch
        {
            VbaCallableDeclarationNameKind.Sub =>
                (VbaSourceDefinitionKind.Procedure,
                    (VbaPropertyAccessorKind?)null),
            VbaCallableDeclarationNameKind.Function =>
                (VbaSourceDefinitionKind.Procedure,
                    (VbaPropertyAccessorKind?)null),
            VbaCallableDeclarationNameKind.PropertyGet =>
                (VbaSourceDefinitionKind.Property,
                    VbaPropertyAccessorKind.Get),
            VbaCallableDeclarationNameKind.PropertyLet =>
                (VbaSourceDefinitionKind.Property,
                    VbaPropertyAccessorKind.Let),
            VbaCallableDeclarationNameKind.PropertySet =>
                (VbaSourceDefinitionKind.Property,
                    VbaPropertyAccessorKind.Set),
            _ => throw new InvalidOperationException(
                "Unsupported callable declaration name kind.")
        };
        var syntaxTree = currentDocument.SyntaxTree
            ?? VbaSyntaxTree.ParseModule(
                currentDocument.Uri,
                currentDocument.Text);
        var conditionalPath =
            VbaConditionalCompilationBranchFacts.TryGetPath(
                syntaxTree,
                declarationName.FragmentRange,
                requireCompleteStructure: true,
                out var path)
                ? path
                : null;
        var fragmentRange = new VbaRange(
            new VbaPosition(
                declarationName.FragmentRange.Start.Line,
                declarationName.FragmentRange.Start.Character),
            new VbaPosition(
                declarationName.FragmentRange.End.Line,
                declarationName.FragmentRange.End.Character));
        var editedDefinition = declarationName.Fragment.Length == 0
            ? null
            : currentDocument.Definitions.FirstOrDefault(definition =>
                definition.ParentProcedureName is null
                && definition.Kind == kind
                && definition.PropertyAccessorKind == accessorKind
                && definition.Range == fragmentRange);
        return new VbaProspectiveDeclaration(
            currentDocument.Uri,
            kind,
            accessorKind,
            conditionalPath,
            editedDefinition?.Identity);
    }

    private IEnumerable<VbaContractPrefixCompletionOrigin>
        CreateSourceWithEventsCompletionOrigins(
            VbaSourceDocument currentDocument)
    {
        foreach (var variable in currentDocument.Definitions.Where(definition =>
                     definition.Kind == VbaSourceDefinitionKind.Variable
                     && definition.ParentProcedureName is null
                     && definition.IsWithEvents
                     && !definition.IsRecoveredWithEventsVariableDeclaration))
        {
            var eligibility = withEventsSemantics.ClassifyType(
                currentDocument,
                variable);
            if (eligibility is null
                || eligibility.Kind is VbaWithEventsTypeEligibilityKind.InvalidEnclosingClass
                    or VbaWithEventsTypeEligibilityKind.InvalidNotClass
                    or VbaWithEventsTypeEligibilityKind.InvalidInaccessibleType
                    or VbaWithEventsTypeEligibilityKind.InvalidNoEvents
                || nameResolution
                    .HasIndeterminateConditionalCompilationOwnership(variable)
                || variable.TypeReference is null
                || !typeResolution.TryResolveTypeReference(
                    currentDocument,
                    variable.TypeReference,
                    out var receiverType))
            {
                continue;
            }

            var variableIsConditional =
                variable.ConditionalCompilationPath is { IsEmpty: false };
            IReadOnlyList<VbaContractMemberCompletionOrigin> members;
            if (eligibility.Kind == VbaWithEventsTypeEligibilityKind.Eligible
                && eligibility.TypeLibEventSurface is { } typeLibSurface)
            {
                members = typeLibSurface.AuthoringEvents
                    .Select(member => new VbaContractMemberCompletionOrigin(
                        member.Name,
                        VbaContractCompletionDomain.WithEvents,
                        variableIsConditional,
                        CreateTypeLibEventSignature(member),
                        member.Documentation,
                        member))
                    .ToArray();
            }
            else if (receiverType.SourceDefinition?.Identity.Origin
                == VbaDefinitionOrigin.Source)
            {
                var sourceEvents = nameResolution
                    .GetPhysicalMembersOfType(receiverType)
                    .Where(member => member.IsEventNameProjectionEligible
                        && member.IsAuthoringAvailable
                        && !nameResolution
                            .HasIndeterminateConditionalCompilationOwnership(member))
                    .ToArray();
                var sourceMembers = sourceEvents
                    .Select(member => new VbaContractMemberCompletionOrigin(
                        member.Name,
                        VbaContractCompletionDomain.WithEvents,
                        variableIsConditional
                            || member.ConditionalCompilationPath is
                                { IsEmpty: false },
                        member.Signature,
                        member.Documentation,
                        member.Identity))
                    .ToList();
                if (eligibility.HostClassEventSurface is { } hostSurface)
                {
                    foreach (var hostEvent in hostSurface.Projection.Events.Where(
                                 hostEvent => hostEvent.AuthoringAvailable))
                    {
                        var shadowingEvents = sourceEvents
                            .Where(sourceEvent =>
                                !sourceEvent.IsRecoveredEventDeclaration
                                && sourceEvent.Name.Equals(
                                    hostEvent.Name,
                                    StringComparison.OrdinalIgnoreCase))
                            .ToArray();
                        if (shadowingEvents.Any(sourceEvent =>
                                sourceEvent.ConditionalCompilationPath
                                    is { IsEmpty: true }))
                        {
                            continue;
                        }

                        sourceMembers.Add(
                            new VbaContractMemberCompletionOrigin(
                                hostEvent.Name,
                                VbaContractCompletionDomain.WithEvents,
                                variableIsConditional
                                    || shadowingEvents.Length > 0,
                                VbaHostClassEventSemanticModel
                                    .CreateEventSignature(hostEvent),
                                hostEvent.Documentation,
                                hostEvent));
                    }
                }

                members = sourceMembers;
            }
            else
            {
                continue;
            }

            if (members.Count == 0)
            {
                continue;
            }

            yield return new VbaContractPrefixCompletionOrigin(
                variable.Name + "_",
                VbaContractCompletionDomain.WithEvents,
                variableIsConditional,
                members);
        }
    }

    private static VbaCallableSignature? CreateTypeLibEventSignature(
        TypeLibCatalogMember member)
    {
        if (member.Signature is not { } signature)
        {
            return null;
        }

        var parameters = signature.Parameters
            .Select(parameter => parameter with
            {
                DisplayLabel = CreateTypeLibEventParameterLabel(parameter)
            })
            .ToArray();
        return signature with
        {
            Label = $"Event {member.Name}({string.Join(", ", parameters.Select(
                parameter => parameter.Label))})",
            Parameters = parameters,
            Documentation = signature.Documentation ?? member.Documentation,
            CallableKind = VbaCallableKind.Event
        };
    }

    private static string CreateTypeLibEventParameterLabel(
        VbaCallableParameter parameter)
    {
        var parts = new List<string>();
        if (parameter.IsParamArray)
        {
            parts.Add("ParamArray");
        }
        else if (parameter.IsByRef == true)
        {
            parts.Add("ByRef");
        }

        parts.Add(parameter.IsArray
            ? $"{parameter.Name}()"
            : parameter.Name);
        if (parameter.TypeReference is { } typeReference)
        {
            parts.Add($"As {typeReference.Name}");
        }

        var label = string.Join(" ", parts);
        return parameter.IsOptional ? $"[{label}]" : label;
    }

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

    private VbaCompletionResult Complete(
        IEnumerable<VbaCompletionCandidate> candidates,
        VbaSyntaxRange? replacementRange)
    {
        var completed = candidates
            .Select(candidate => AddReplacementEdit(candidate, replacementRange))
            .GroupBy(GetCandidateIdentity)
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

    private CompletionCandidateIdentity GetCandidateIdentity(
        VbaCompletionCandidate candidate)
    {
        object? definitionIdentity = candidate.Definition?.Kind;
        if (candidate.Definition is not null)
        {
            var target = resolutionPolicy.CreateNameTarget(candidate.Definition);
            if (target is VbaConditionalFamilyNameTarget
                or VbaPropertyNameTarget)
            {
                definitionIdentity = target.Identity;
            }
        }

        return new CompletionCandidateIdentity(
            candidate.Label.ToUpperInvariant(),
            GetEffectiveInsertionText(candidate).ToUpperInvariant(),
            candidate.Kind,
            definitionIdentity,
            candidate.SortRank);
    }

    private sealed record CompletionCandidateIdentity(
        string Label,
        string InsertionText,
        VbaCompletionCandidateKind Kind,
        object? DefinitionIdentity,
        int? SortRank);

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
            return new VbaCallableCompletionContext(
                declaration.Name,
                SetterPropertyName: null,
                RequestedPropertyWriteAccessorKind: null);
        }

        return declaration.PropertyAccessorKind is VbaPropertyAccessorKind.Let
                or VbaPropertyAccessorKind.Set
            ? new VbaCallableCompletionContext(
                ResultTargetName: null,
                SetterPropertyName: declaration.Name,
                RequestedPropertyWriteAccessorKind: null)
            : VbaCallableCompletionContext.None;
    }

    private static bool Contains(VbaSyntaxRange range, VbaSyntaxPosition position)
        => Compare(range.Start, position) <= 0 && Compare(position, range.End) <= 0;

    private static bool Contains(VbaRange range, VbaPosition position)
        => Compare(range.Start, position) <= 0 && Compare(position, range.End) <= 0;

    private static int Compare(VbaSyntaxPosition left, VbaSyntaxPosition right)
    {
        var lineComparison = left.Line.CompareTo(right.Line);
        return lineComparison != 0
            ? lineComparison
            : left.Character.CompareTo(right.Character);
    }

    private static int Compare(VbaPosition left, VbaPosition right)
    {
        var lineComparison = left.Line.CompareTo(right.Line);
        return lineComparison != 0
            ? lineComparison
            : left.Character.CompareTo(right.Character);
    }

    private IReadOnlyList<VbaRankedDefinition> GetTypeCompletionDefinitions(
        VbaSourceDocument currentDocument,
        string? qualifier)
        => nameResolution.GetRankedVisibleTypeDefinitions(currentDocument, qualifier)
            .Where(candidate => candidate.Definition.IsAuthoringAvailable)
            .GroupBy(candidate => candidate.Definition.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var bestRank = group.Min(candidate => candidate.Rank);
                var definition = typeResolution.ResolveSourceTypeCompletionGroup(group
                    .Where(candidate => candidate.Rank == bestRank)
                    .Select(candidate => candidate.Definition)
                    .ToArray());
                return definition is null
                    ? null
                    : new VbaRankedDefinition(definition, bestRank);
            })
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .ToArray();

    private sealed record VbaCallableCompletionContext(
        string? ResultTargetName,
        string? SetterPropertyName,
        VbaPropertyAccessorKind? RequestedPropertyWriteAccessorKind)
    {
        public static VbaCallableCompletionContext None { get; } = new(
            null,
            null,
            null);
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
        VbaSourceDefinition handler,
        out VbaResolvedNameTarget variableTarget,
        out VbaWithEventsEventNameTarget? eventTarget,
        out VbaWithEventsHandlerNameDecomposition decomposition)
    {
        var analysis = AnalyzeWithEventsHandler(currentDocument, handler);
        if (analysis is null
            || analysis.Recognition
                == VbaWithEventsHandlerRecognition.OrdinaryProcedure)
        {
            variableTarget = default!;
            eventTarget = null;
            decomposition = default!;
            return false;
        }

        variableTarget = analysis.BindingSet.VariableTarget;
        eventTarget = analysis.EventTarget;
        decomposition = analysis.Decomposition;
        return true;
    }

    internal VbaWithEventsHandlerAnalysis? AnalyzeWithEventsHandler(
        VbaSourceDocument currentDocument,
        VbaSourceDefinition handler)
    {
        if (handler.Kind is not (
                VbaSourceDefinitionKind.Procedure or VbaSourceDefinitionKind.Property)
            || !VbaWithEventsHandlerNameDecomposition.TryCreate(
                handler.Name,
                out var parsedDecomposition))
        {
            return null;
        }

        var variableName = parsedDecomposition.VariableName;
        var eventName = parsedDecomposition.EventName;

        var moduleDefinition = currentDocument.Definitions.FirstOrDefault(definition =>
            definition.Identity.Origin == VbaDefinitionOrigin.Source
            && definition.Name.Equals(
                currentDocument.ModuleName,
                StringComparison.OrdinalIgnoreCase)
            && definition.Kind is VbaSourceDefinitionKind.Class or VbaSourceDefinitionKind.Form);
        if (moduleDefinition is null)
        {
            return null;
        }

        var variableOutcome = nameResolution
            .ResolveCurrentDocumentModuleVariableOutcome(
                currentDocument,
                variableName);
        if (variableOutcome.Kind != VbaNameResolutionKind.Resolved
            || variableOutcome.Target is null)
        {
            return null;
        }

        var variableTarget = variableOutcome.Target;
        var entries = new List<VbaWithEventsEventBindingEntry>();
        var hasAdmittedWithEventsVariant = false;
        foreach (var variable in variableTarget.PhysicalDefinitions.Where(definition =>
            definition.Kind == VbaSourceDefinitionKind.Variable
            && definition.ParentProcedureName is null
            && VbaProjectIdentityModel.SameDocument(
                definition.Uri,
                currentDocument.Uri)))
        {
            if (variable.IsRecoveredWithEventsVariableDeclaration)
            {
                continue;
            }

            if (!variable.IsWithEvents)
            {
                entries.Add(new VbaWithEventsEventBindingEntry(
                    variable,
                    VbaWithEventsEventBindingStatus.NotWithEvents));
                continue;
            }

            var eligibility = withEventsSemantics.ClassifyType(
                currentDocument,
                variable);
            if (eligibility is null
                || eligibility.Kind is VbaWithEventsTypeEligibilityKind.InvalidEnclosingClass
                    or VbaWithEventsTypeEligibilityKind.InvalidNotClass
                    or VbaWithEventsTypeEligibilityKind.InvalidInaccessibleType
                    or VbaWithEventsTypeEligibilityKind.InvalidNoEvents)
            {
                continue;
            }

            hasAdmittedWithEventsVariant = true;

            if (variable.TypeReference is null
                || !typeResolution.TryResolveTypeReference(
                    currentDocument,
                    variable.TypeReference,
                    out var receiverType))
            {
                entries.Add(new VbaWithEventsEventBindingEntry(
                    variable,
                    VbaWithEventsEventBindingStatus.Indeterminate));
                continue;
            }

            const int restrictedTypeFlag = 0x200;
            VbaHostEventNameTarget? hostEventTarget = null;
            if (eligibility.HostClassEventSurface is { } hostSurface
                && hostClassEvents.TryCreateExistingHandlerEventTarget(
                    hostSurface,
                    eventName,
                    handler,
                    out var resolvedHostEventTarget))
            {
                hostEventTarget = resolvedHostEventTarget;
            }

            if (hostEventTarget is not null
                && variableTarget.IsConditionalFamily)
            {
                var conditionalContract = hostEventTarget.EventContract with
                {
                    IsConditionalContract = true
                };
                hostEventTarget = new VbaHostEventNameTarget(
                    hostEventTarget.HostEventIdentity,
                    hostEventTarget.SelectedDefinition,
                    conditionalContract,
                    hostEventTarget.NavigableDefinition);
            }

            var hasKnownHostEvent = hostEventTarget is not null;
            var hasKnownPartialTypeLibEvent =
                eligibility.Kind == VbaWithEventsTypeEligibilityKind.Indeterminate
                && eligibility.TypeLibEventSurface is
                {
                    State: VbaTypeLibEventSurfaceState.Partial,
                    RawTypeKind: TypeLibCatalogRawTypeKind.CoClass
                } partialSurface
                && (partialSurface.TypeFlags & restrictedTypeFlag) == 0
                && partialSurface.ExistingHandlerRecognitionEvents.Any(member =>
                    member.Name.Equals(
                        eventName,
                        StringComparison.OrdinalIgnoreCase));
            var hasKnownIndeterminateSourceEvent =
                eligibility.Kind == VbaWithEventsTypeEligibilityKind.Indeterminate
                && receiverType.SourceDefinition?.Identity.Origin
                    == VbaDefinitionOrigin.Source
                && nameResolution
                    .GetPhysicalMembersOfType(receiverType)
                    .Any(member =>
                        member.Kind == VbaSourceDefinitionKind.Event
                        && member.Name.Equals(
                            eventName,
                            StringComparison.OrdinalIgnoreCase)
                        && !member.IsRecoveredEventDeclaration
                        && (nameResolution
                                .HasIndeterminateConditionalCompilationOwnership(
                                    member)
                            || nameResolution
                                .HasIncompleteSourceEventSurfaceEvidence(
                                    member.Uri)));
            if (eligibility.Kind == VbaWithEventsTypeEligibilityKind.Indeterminate
                && !hasKnownPartialTypeLibEvent
                && !hasKnownIndeterminateSourceEvent
                && !hasKnownHostEvent)
            {
                entries.Add(new VbaWithEventsEventBindingEntry(
                    variable,
                    VbaWithEventsEventBindingStatus.Indeterminate));
                continue;
            }

            if (eligibility.TypeLibEventSurface is not null
                && !eligibility.TypeLibEventSurface.ExistingHandlerRecognitionEvents.Any(member =>
                    member.Name.Equals(
                        eventName,
                        StringComparison.OrdinalIgnoreCase)))
            {
                entries.Add(new VbaWithEventsEventBindingEntry(
                    variable,
                    eligibility.Kind == VbaWithEventsTypeEligibilityKind.Indeterminate
                        ? VbaWithEventsEventBindingStatus.Indeterminate
                        : VbaWithEventsEventBindingStatus.NotEvent));
                continue;
            }

            var typeLibEventContracts = eligibility.Kind
                    == VbaWithEventsTypeEligibilityKind.Eligible
                && eligibility.TypeLibEventSurface is { } typeLibEventSurface
                ? withEventsSemantics.CreateTypeLibEventContracts(
                    receiverType.ReferenceName,
                    receiverType.Name,
                    typeLibEventSurface,
                    eventName,
                    variableTarget.IsConditionalFamily)
                : [];

            var outcome = nameResolution.ResolveMemberOutcome(
                currentDocument,
                receiverType,
                eventName,
                VbaSourceDefinitionKind.Event);
            if (outcome.Kind == VbaNameResolutionKind.Resolved)
            {
                if (outcome.Target is not null)
                {
                    var eventContracts = CreateResolvedEventContracts(
                        outcome.Target,
                        variableTarget.IsConditionalFamily);
                    if (eventContracts.Contracts.Count == 0
                        && eventContracts.HasRecoveredEventEvidence
                        && hasKnownHostEvent)
                    {
                        entries.Add(new VbaWithEventsEventBindingEntry(
                            variable,
                            VbaWithEventsEventBindingStatus.Resolved,
                            hostEventTarget!,
                            [hostEventTarget!.EventContract],
                            HasRecoveredEventEvidence: true));
                        continue;
                    }

                    if (eventContracts.Contracts.Count > 0
                        && eventContracts.Contracts.All(contract =>
                            contract.IsConditionalContract)
                        && hasKnownHostEvent)
                    {
                        var conditionalHostContract = hostEventTarget!.EventContract with
                        {
                            IsConditionalContract = true
                        };
                        var conditionalHostTarget = new VbaHostEventNameTarget(
                            hostEventTarget.HostEventIdentity,
                            hostEventTarget.SelectedDefinition,
                            conditionalHostContract,
                            hostEventTarget.NavigableDefinition);
                        entries.Add(new VbaWithEventsEventBindingEntry(
                            variable,
                            VbaWithEventsEventBindingStatus.Resolved,
                            outcome.Target,
                            [.. eventContracts.Contracts, conditionalHostContract],
                            eventContracts.HasRecoveredEventEvidence,
                            [outcome.Target, conditionalHostTarget]));
                        continue;
                    }

                    entries.Add(new VbaWithEventsEventBindingEntry(
                        variable,
                        VbaWithEventsEventBindingStatus.Resolved,
                        outcome.Target,
                        eventContracts.Contracts,
                        eventContracts.HasRecoveredEventEvidence));
                }

                continue;
            }

            if (outcome.Kind == VbaNameResolutionKind.Unresolved
                && hasKnownHostEvent)
            {
                entries.Add(new VbaWithEventsEventBindingEntry(
                    variable,
                    VbaWithEventsEventBindingStatus.Resolved,
                    hostEventTarget!,
                    [hostEventTarget!.EventContract]));
                continue;
            }

            if (outcome.Kind == VbaNameResolutionKind.Unresolved
                && typeLibEventContracts.Count > 0)
            {
                entries.Add(new VbaWithEventsEventBindingEntry(
                    variable,
                    VbaWithEventsEventBindingStatus.Resolved,
                    EventTarget: null,
                    EventContracts: typeLibEventContracts));
                continue;
            }

            if (outcome.Kind is VbaNameResolutionKind.Ambiguous
                or VbaNameResolutionKind.AnalysisIncomplete)
            {
                entries.Add(new VbaWithEventsEventBindingEntry(
                    variable,
                    VbaWithEventsEventBindingStatus.Indeterminate));
                continue;
            }

            entries.Add(new VbaWithEventsEventBindingEntry(
                variable,
                eligibility.Kind == VbaWithEventsTypeEligibilityKind.Indeterminate
                    ? VbaWithEventsEventBindingStatus.Indeterminate
                    : VbaWithEventsEventBindingStatus.NotEvent));
        }

        if (!hasAdmittedWithEventsVariant)
        {
            return null;
        }

        var bindingSet = new VbaWithEventsEventBindingSet(
            variableTarget,
            entries);
        var resolvedEventTargets = bindingSet.ResolvedEntries
            .SelectMany(entry => entry.ResolvedEventTargets)
            .ToArray();
        var eventTarget = resolvedEventTargets.Length == 0
            ? null
            : new VbaWithEventsEventNameTarget(
                handler,
                eventName,
                resolvedEventTargets,
                variableTarget.IsConditionalFamily);
        var recognition = bindingSet.ResolvedEntries.Count > 0
            ? handler.CallableKind == VbaCallableKind.Sub
                ? VbaWithEventsHandlerRecognition.ResolvedHandler
                : VbaWithEventsHandlerRecognition.NonSubProcedureAssociation
            : entries.Any(entry =>
                entry.Status == VbaWithEventsEventBindingStatus.Indeterminate)
                ? VbaWithEventsHandlerRecognition.IndeterminateCandidate
                : VbaWithEventsHandlerRecognition.OrdinaryProcedure;

        return new VbaWithEventsHandlerAnalysis(
            handler,
            parsedDecomposition,
            bindingSet,
            recognition,
            eventTarget);
    }

    internal VbaHandlerEventRenameConvergence
        AnalyzeHandlerEventRenameConvergence(
            VbaWithEventsHandlerAnalysis handlerAnalysis)
        => withEventsSemantics.AnalyzeHandlerEventRenameConvergence(
            handlerAnalysis);

    private (
        IReadOnlyList<VbaResolvedEventContract> Contracts,
        bool HasRecoveredEventEvidence) CreateResolvedEventContracts(
            VbaResolvedNameTarget eventTarget,
            bool isConditionalBinding)
    {
        var eventDefinitions = eventTarget.PhysicalDefinitions
            .Where(definition => definition.Kind == VbaSourceDefinitionKind.Event)
            .DistinctBy(definition => definition.Identity)
            .OrderBy(definition => definition.Uri, StringComparer.OrdinalIgnoreCase)
            .ThenBy(definition => definition.Uri, StringComparer.Ordinal)
            .ThenBy(definition => definition.Range.Start.Line)
            .ThenBy(definition => definition.Range.Start.Character)
            .ThenBy(definition => definition.Range.End.Line)
            .ThenBy(definition => definition.Range.End.Character)
            .ToArray();
        return (
            eventDefinitions
                .Where(definition => !definition.IsRecoveredEventDeclaration)
                .Select(definition => new VbaResolvedEventContract(
                    new VbaDefinitionEventContractIdentity(definition.Identity),
                    definition.Name,
                    definition.Signature,
                    definition.Documentation,
                    GetEventHandlerValidationAuthority(definition),
                    isConditionalBinding
                        || definition.ConditionalCompilationPath is { IsEmpty: false },
                    definition.IsAuthoringAvailable,
                    Definition: definition,
                    NavigableLocation:
                        definition.Identity.Origin == VbaDefinitionOrigin.Source
                        ? definition.Location
                        : null,
                    ParameterTypeEvidence:
                        withEventsSemantics.GetParameterTypeEvidence(definition)))
                .ToArray(),
            eventDefinitions.Any(definition =>
                definition.IsRecoveredEventDeclaration
                || nameResolution
                    .HasIndeterminateConditionalCompilationOwnership(
                        definition)
                || nameResolution
                    .HasIncompleteSourceEventSurfaceEvidence(definition.Uri)));
    }

    private static VbaEventHandlerValidationAuthority
        GetEventHandlerValidationAuthority(VbaSourceDefinition eventDefinition)
        => eventDefinition.Identity.Origin == VbaDefinitionOrigin.ProjectReference
            ? VbaEventHandlerValidationAuthority.ExternalTypeLibAdvisory
            : VbaEventHandlerValidationAuthority.SourceDeclared;

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

        var definition = nameResolution.ResolveValue(
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
            var definition = nameResolution.ResolvePreferred(
                uri,
                new VbaPosition(lineIndex, 0),
                qualifier.Name,
                occurrence.Name,
                candidate => !nameResolution.IsTypeDefinition(candidate));
            canonicalName = definition?.Name;
            return canonicalName is not null;
        }

        if (access?.Target is not null
            && access.TargetSegmentIndex == 0
            && access.Segments.Count > 1)
        {
            var member = access.Segments[1];
            var definition = nameResolution.ResolvePreferred(
                uri,
                new VbaPosition(lineIndex, 0),
                occurrence.Name,
                member.Name,
                candidate => !nameResolution.IsTypeDefinition(candidate));
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

}
