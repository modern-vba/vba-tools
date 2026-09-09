using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.ProjectModel;
using VbaTools.Syntax;

namespace VbaLanguageServer.SourceModel;

/// <summary>
/// Provides semantic resolution for completion, definition, signature help, and formatting.
/// </summary>
internal sealed class VbaSemanticResolution
{
    private static readonly VbaCompletionResult EmptyCompletion = new([]);
    private readonly VbaProjectSemanticResolution core;
    internal VbaProjectSemanticResolution Core => core;
    private readonly VbaNameCandidateInventory definitionCandidates;
    private readonly VbaResolutionPolicy resolutionPolicy;
    private readonly VbaNameResolutionService nameResolution;
    private readonly VbaTypeResolution typeResolution;
    private readonly VbaMemberChainResolution memberChainResolution;
    private readonly VbaCallSiteResolution callSiteResolution;
    private readonly VbaWithEventsSemanticModel withEventsSemantics;
    private readonly VbaIntrinsicHostEventSemanticModel intrinsicHostEvents;
    private readonly VbaInterfaceSemanticModel interfaceSemantics;

    /// <summary>
    /// Creates the semantic resolution service.
    /// </summary>
    /// <param name="definitionCandidates">The immutable source and reference candidate inventory.</param>
    public VbaSemanticResolution(
        VbaNameCandidateInventory definitionCandidates,
        VbaResolutionPolicy? resolutionPolicy = null,
        IReadOnlyDictionary<string, VbaProjectReferenceCatalogIdentity>?
            referenceCatalogIdentities = null,
        VbaIntrinsicHostEventCatalog? intrinsicHostEventCatalog = null)
    {
        core = new VbaProjectSemanticResolution(definitionCandidates, resolutionPolicy,
            referenceCatalogIdentities, intrinsicHostEventCatalog);
        this.definitionCandidates = core.DefinitionCandidates;
        this.resolutionPolicy = core.ResolutionPolicy;
        this.nameResolution = core.NameResolution;
        this.typeResolution = core.TypeResolution;
        this.memberChainResolution = core.MemberChainResolution;
        this.callSiteResolution = core.CallSiteResolution;
        this.withEventsSemantics = core.WithEventsSemantics;
        this.intrinsicHostEvents = core.IntrinsicHostEvents;
        this.interfaceSemantics = core.InterfaceSemantics;
    }

    internal VbaWithEventsTypeEligibility? GetWithEventsTypeEligibility(
        VbaSourceDocument currentDocument,
        VbaSourceDefinition variable)
        => withEventsSemantics.ClassifyType(currentDocument, variable);

    internal bool HasIndeterminateConditionalCompilationOwnership(
        VbaSourceDefinition definition)
        => core.HasIndeterminateConditionalCompilationOwnership(definition);

    internal IReadOnlyList<VbaProjectValidationDiagnostic>
        GetInterfaceContractDiagnostics(VbaSourceDocument currentDocument)
        => interfaceSemantics.GetDiagnostics(currentDocument).Select(diagnostic =>
            new VbaProjectValidationDiagnostic(diagnostic.Code, diagnostic.Message, diagnostic.Range,
                diagnostic.Severity, Details: diagnostic.Details)).ToArray();

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

    internal VbaEffectiveDeclaredType GetEffectiveDeclaredType(
        VbaSourceDefinition definition, int parameterOrdinal = -1)
        => nameResolution.EffectiveDeclaredTypes.Get(definition, parameterOrdinal);

    internal VbaSourceDefinition ProjectDefinitionPresentation(
        VbaSourceDefinition definition)
        => nameResolution.EffectiveDeclaredTypes.Project(
            interfaceSemantics.ProjectSourceInterfaceDocumentation(definition));

    internal VbaEventHandlerCompatibility AnalyzeWithEventsHandlerCompatibility(
        VbaSourceDocument currentDocument,
        VbaWithEventsHandlerAnalysis handlerAnalysis)
        => withEventsSemantics.AnalyzeHandlerCompatibility(
            currentDocument,
            handlerAnalysis);

    internal VbaIntrinsicHostHandlerAnalysis? AnalyzeIntrinsicHostHandler(
        VbaSourceDocument currentDocument,
        VbaSourceDefinition handler)
        => intrinsicHostEvents.AnalyzeIntrinsicHandler(
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
        => core.ResolveSourceTarget(uri, line, character);

    private VbaResolvedNameTarget? ResolveSourceTarget(
        string uri,
        int line,
        int character,
        bool retargetConditionalPropertyAccessor)
        => core.ResolveSourceTarget(uri, line, character, retargetConditionalPropertyAccessor);

    private static bool IsModuleIdentityDefinition(VbaSourceDefinition definition)
        => VbaProjectSemanticResolution.IsModuleIdentityDefinition(definition);

    internal VbaNameResolutionOutcome ClassifySourceModuleValueQualifier(
        string uri,
        int line,
        int character)
        => core.ClassifySourceModuleValueQualifier(uri, line, character);

    private bool CanUseAsSourceModuleValueQualifier(
        VbaSourceDefinition definition)
        => core.CanUseAsSourceModuleValueQualifier(definition);

    private static bool HasRaiseEventPlacementDiagnostic(
        VbaSyntaxTree syntaxTree,
        VbaArgumentListSyntax argumentList)
        => VbaProjectSemanticResolution.HasRaiseEventPlacementDiagnostic(syntaxTree, argumentList);

    private static bool HasRaiseEventPlacementDiagnostic(
        VbaSyntaxTree syntaxTree,
        int line,
        int character)
        => VbaProjectSemanticResolution.HasRaiseEventPlacementDiagnostic(syntaxTree, line, character);

    private static bool HasRaiseEventPlacementDiagnostic(
        VbaSyntaxTree syntaxTree,
        int beforeOffset)
        => VbaProjectSemanticResolution.HasRaiseEventPlacementDiagnostic(syntaxTree, beforeOffset);

    private static VbaResolvedNameTarget? RetargetConditionalPropertyAccessor(
        VbaSourceDocument currentDocument,
        VbaCompletionExpectation propertyUsageExpectation,
        bool isCurrentResultTarget,
        VbaPropertyAccessorKind? requestedWriteAccessorKind,
        VbaResolvedNameTarget? target)
        => VbaProjectSemanticResolution.RetargetConditionalPropertyAccessor(currentDocument, propertyUsageExpectation, isCurrentResultTarget, requestedWriteAccessorKind, target);

    private static Func<VbaSourceDefinition, bool>? GetPropertyUsageFilter(
        VbaCompletionExpectation expectation,
        bool isCurrentResultTarget)
        => VbaProjectSemanticResolution.GetPropertyUsageFilter(expectation, isCurrentResultTarget);

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
            ProjectDefinitionPresentation);
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
            var intrinsicAnalysis = intrinsicHostEvents.AnalyzeIntrinsicHandler(
                currentDocument,
                declaration);
            if (intrinsicAnalysis?.EventTarget.EventContract.Signature is not null)
            {
                var signature = VbaIntrinsicHostEventSemanticModel
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

        var intrinsicAnalysis = intrinsicHostEvents.AnalyzeIntrinsicHandler(
            currentDocument,
            handler);
        if (intrinsicAnalysis?.EventTarget.EventContract.Signature
            is not null)
        {
            var intrinsicParameterIndex = GetHandlerParameterIndex(
                callable,
                position);
            var signature = VbaIntrinsicHostEventSemanticModel
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
        => core.AnalyzeCompleteCall(uri, argumentList);

    internal bool TryResolveRaiseEventTarget(
        string uri,
        VbaArgumentListSyntax argumentList,
        out VbaResolvedNameTarget? target)
        => core.TryResolveRaiseEventTarget(uri, argumentList, out target);

    internal bool TryResolveRaiseEventTarget(
        string uri,
        VbaCallSiteSyntax? callSite,
        out VbaResolvedNameTarget? target)
        => core.TryResolveRaiseEventTarget(uri, callSite, out target);

    internal static bool IsCallableResultAssignment(
        VbaSourceDocument currentDocument,
        VbaArgumentListSyntax argumentList,
        VbaSyntaxRange calleeRange)
        => VbaProjectSemanticResolution.IsCallableResultAssignment(currentDocument, argumentList, calleeRange);

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
        => VbaProjectSemanticResolution.IsReadableDefinition(definition);

    private static bool IsWritableDefinition(VbaSourceDefinition definition)
        => VbaProjectSemanticResolution.IsWritableDefinition(definition);

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
            var syntaxTree = currentDocument.SyntaxTree
                ?? VbaSyntaxTree.ParseModule(currentDocument.Uri, currentDocument.Text);
            if (syntaxTree.Module.Kind == VbaModuleKind.ClassModule)
            {
                origins.Add(VbaClassLifecycleCompletion.Origin);
            }

            if (intrinsicHostEvents.TryGetEffectiveSurface(
                    currentDocument,
                    out var surface))
            {
                origins.Add(new VbaContractPrefixCompletionOrigin(
                    surface.Catalog.IntrinsicEventSourceName + "_",
                    VbaContractCompletionDomain.HostEvents,
                    IsConditionalPrefix: false,
                    surface.Catalog.Events
                        .Where(hostEvent => hostEvent.AuthoringAvailable)
                        .Select(hostEvent => new VbaContractMemberCompletionOrigin(
                            hostEvent.Name,
                            VbaContractCompletionDomain.HostEvents,
                            IsConditionalContract: false,
                            VbaIntrinsicHostEventSemanticModel.CreateHandlerSignature(
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
            editedDefinition?.Identity,
            fragmentRange,
            declarationName.Kind switch
            {
                VbaCallableDeclarationNameKind.Function => VbaCallableKind.Function,
                VbaCallableDeclarationNameKind.Sub => VbaCallableKind.Sub,
                _ => VbaCallableKind.Property
            });
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
                if (eligibility.IntrinsicHostEventSurface is { } hostSurface)
                {
                    foreach (var hostEvent in hostSurface.Catalog.Events.Where(
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
                                VbaIntrinsicHostEventSemanticModel
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

        return VbaCallablePresentation.Assemble(
            new VbaCallablePresentationShape(member.Name, VbaCallableKind.Event),
            signature with
            {
                Documentation = signature.Documentation ?? member.Documentation,
                CallableKind = VbaCallableKind.Event
            });
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
        => VbaProjectSemanticResolution.GetCurrentCallableCompletionContext(syntaxTree, line, character);

    private static bool Contains(VbaSyntaxRange range, VbaSyntaxPosition position)
        => VbaProjectSemanticResolution.Contains(range, position);

    private static bool Contains(VbaRange range, VbaPosition position)
        => VbaProjectSemanticResolution.Contains(range, position);

    private static int Compare(VbaSyntaxPosition left, VbaSyntaxPosition right)
        => VbaProjectSemanticResolution.Compare(left, right);

    private static int Compare(VbaPosition left, VbaPosition right)
        => VbaProjectSemanticResolution.Compare(left, right);

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


    private bool TryResolveMemberDefinition(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        VbaPositionSyntax positionSyntax,
        out VbaSourceDefinition? definition)
        => core.TryResolveMemberDefinition(currentDocument, line, character, positionSyntax, out definition);

    internal VbaMemberChainResolutionResult? ResolveMemberChainAt(string uri, int line, int character)
    {
        var document = definitionCandidates.FindDocument(uri);
        if (document is null)
        {
            return null;
        }
        var syntax = GetSyntaxTree(document).GetPositionSyntax(line, character);
        return syntax.MemberAccess is { } access
            ? memberChainResolution.ResolveMemberChain(
                document, line, character, access, syntax.EnclosingWithScopes)
            : null;
    }

    private bool TryResolveWithEventsHandler(
        VbaSourceDocument currentDocument,
        VbaSourceDefinition handler,
        out VbaResolvedNameTarget variableTarget,
        out VbaWithEventsEventNameTarget? eventTarget,
        out VbaWithEventsHandlerNameDecomposition decomposition)
        => core.TryResolveWithEventsHandler(currentDocument, handler, out variableTarget, out eventTarget, out decomposition);

    internal VbaWithEventsHandlerAnalysis? AnalyzeWithEventsHandler(
        VbaSourceDocument currentDocument,
        VbaSourceDefinition handler)
        => core.AnalyzeWithEventsHandler(currentDocument, handler);

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
        => core.CreateResolvedEventContracts(eventTarget, isConditionalBinding);

    private static VbaEventHandlerValidationAuthority
        GetEventHandlerValidationAuthority(VbaSourceDefinition eventDefinition)
        => VbaProjectSemanticResolution.GetEventHandlerValidationAuthority(eventDefinition);

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
        => VbaProjectSemanticResolution.GetImmediateQualifier(access, identifier);

    private static string GetRangeKey(VbaRange range)
        => $"{range.Start.Line}:{range.Start.Character}:{range.End.Line}:{range.End.Character}";

    private static VbaSyntaxTree GetSyntaxTree(VbaSourceDocument document)
        => VbaProjectSemanticResolution.GetSyntaxTree(document);

}
