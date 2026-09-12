using VbaTools.Syntax;

namespace VbaTools.Semantics;

/// <summary>Resolves supplied source and catalog facts independently of editor operations.</summary>
internal sealed class VbaProjectSemanticResolution
{
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
    public VbaProjectSemanticResolution(
        VbaNameCandidateInventory definitionCandidates,
        VbaResolutionPolicy? resolutionPolicy = null,
        IReadOnlyDictionary<string, VbaProjectReferenceCatalogIdentity>?
            referenceCatalogIdentities = null,
        VbaIntrinsicHostEventCatalog? intrinsicHostEventCatalog = null)
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
        intrinsicHostEvents = new VbaIntrinsicHostEventSemanticModel(
            intrinsicHostEventCatalog,
            nameResolution,
            referenceCatalogIdentities);
        withEventsSemantics = new VbaWithEventsSemanticModel(
            nameResolution,
            intrinsicHostEvents,
            referenceCatalogIdentities);
        interfaceSemantics = new VbaInterfaceSemanticModel(nameResolution);
        callSiteResolution = new VbaCallSiteResolution(
            nameResolution,
            memberChainResolution,
            resolutionPolicy,
            interfaceSemantics);
    }

    internal IReadOnlyList<VbaSemanticDiagnostic> GetInterfaceContractDiagnostics(VbaSourceDocument document)
        => interfaceSemantics.GetDiagnostics(document);

    internal VbaWithEventsTypeEligibility? GetWithEventsTypeEligibility(VbaSourceDocument document, VbaSourceDefinition variable)
        => withEventsSemantics.ClassifyType(document, variable);

    internal VbaIntrinsicHostHandlerAnalysis? AnalyzeIntrinsicHostHandler(VbaSourceDocument document, VbaSourceDefinition handler)
        => intrinsicHostEvents.AnalyzeIntrinsicHandler(document, handler);

    internal VbaEventHandlerCompatibility AnalyzeIntrinsicHostHandlerCompatibility(VbaSourceDocument document, VbaIntrinsicHostHandlerAnalysis analysis)
        => withEventsSemantics.AnalyzeHandlerCompatibility(document, analysis);

    internal VbaEventHandlerCompatibility AnalyzeWithEventsHandlerCompatibility(VbaSourceDocument document, VbaWithEventsHandlerAnalysis analysis)
        => withEventsSemantics.AnalyzeHandlerCompatibility(document, analysis);

    internal VbaNameCandidateInventory DefinitionCandidates => definitionCandidates;
    internal VbaResolutionPolicy ResolutionPolicy => resolutionPolicy;
    internal VbaNameResolutionService NameResolution => nameResolution;
    internal VbaTypeResolution TypeResolution => typeResolution;
    internal VbaMemberChainResolution MemberChainResolution => memberChainResolution;
    internal VbaCallSiteResolution CallSiteResolution => callSiteResolution;
    internal VbaWithEventsSemanticModel WithEventsSemantics => withEventsSemantics;
    internal VbaIntrinsicHostEventSemanticModel IntrinsicHostEvents => intrinsicHostEvents;
    internal VbaInterfaceSemanticModel InterfaceSemantics => interfaceSemantics;

    internal bool HasIndeterminateConditionalCompilationOwnership(
        VbaSourceDefinition definition)
        => nameResolution
            .HasIndeterminateConditionalCompilationOwnership(definition);

    internal VbaResolvedNameTarget? ResolveSourceTarget(
        string uri,
        int line,
        int character)
        => ResolveSourceTarget(
            uri,
            line,
            character,
            retargetConditionalPropertyAccessor: true);

    internal VbaResolvedNameTarget? ResolveSourceTarget(
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
            && intrinsicHostEvents.AnalyzeIntrinsicHandler(
                currentDocument,
                declaredDefinition) is { } intrinsicHandler)
        {
            var prefixLength = intrinsicHandler.Surface.Catalog
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

        var moduleOutcome = ClassifySourceModuleValueQualifier(uri, line, character);
        if (moduleOutcome.Target is not null)
        {
            return moduleOutcome.Target;
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

    internal static bool IsModuleIdentityDefinition(VbaSourceDefinition definition)
        => definition.Kind is VbaSourceDefinitionKind.Module
            or VbaSourceDefinitionKind.Class
            or VbaSourceDefinitionKind.Form;

    internal VbaNameResolutionOutcome ClassifySourceModuleValueQualifier(
        string uri,
        int line,
        int character)
    {
        var document = definitionCandidates.FindDocument(uri);
        if (document is null)
        {
            return VbaNameResolutionOutcome.AnalysisIncomplete;
        }
        var syntax = GetSyntaxTree(document).GetPositionSyntax(line, character);
        if (syntax.Region != VbaPositionRegion.Code || syntax.Identifier is not { } identifier
            || syntax.MemberAccess is not { IsLeadingDot: false, TargetSegmentIndex: 0 } access
            || access.Segments.Count <= 1)
        {
            return VbaNameResolutionOutcome.NonSemantic;
        }
        if (nameResolution.HasLocalSourceQualifierShadow(
                document, new VbaPosition(line, character), identifier.Name))
        {
            return VbaNameResolutionOutcome.Unresolved;
        }
        return resolutionPolicy.ResolveRankedCandidatesOutcome(
            definitionCandidates.GetSourceCandidates(identifier.Name)
                .Select(candidate => candidate.Definition)
                .Where(CanUseAsSourceModuleValueQualifier)
                .Select(definition => new VbaRankedDefinition(definition, VbaResolutionPolicy.ProjectRank)),
            referenceSelection: null);
    }

    internal bool CanUseAsSourceModuleValueQualifier(
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

    internal static bool HasRaiseEventPlacementDiagnostic(
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

    internal static bool HasRaiseEventPlacementDiagnostic(
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

    internal static bool HasRaiseEventPlacementDiagnostic(
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

    internal VbaResolvedNameTarget? RetargetConditionalPropertyAccessor(
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
                    .Where(definition => nameResolution.SameDocument(
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

    internal static Func<VbaSourceDefinition, bool>? GetPropertyUsageFilter(
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

        if (IsCallableResultStorage(currentDocument, argumentList, calleeRange))
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

    internal static bool IsCallableResultStorage(
        VbaSourceDocument currentDocument,
        VbaArgumentListSyntax argumentList,
        VbaSyntaxRange calleeRange)
    {
        if (argumentList.Form is not (VbaCallSyntaxForm.PropertyAssignment
                or VbaCallSyntaxForm.BareValueRead)
            || argumentList.Callee.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        return FindCallableResultOwner(currentDocument, argumentList.Callee, calleeRange) is not null;
    }

    internal static VbaCallableDeclarationSyntax? FindCallableResultOwner(
        VbaSourceDocument currentDocument,
        string name,
        VbaSyntaxRange range)
    {
        var syntaxTree = currentDocument.SyntaxTree
            ?? VbaSyntaxTree.ParseModule(currentDocument.Uri, currentDocument.Text);
        return syntaxTree.Module.CallableDeclarations.FirstOrDefault(declaration =>
            declaration.BlockRange.Start.Offset <= range.Start.Offset
            && range.End.Offset <= declaration.BlockRange.End.Offset
            && declaration.Name.Equals(
                name,
                StringComparison.OrdinalIgnoreCase)
            && (declaration.PropertyAccessorKind == VbaPropertyAccessorKind.Get
                || declaration.Kind == VbaDeclarationKind.Procedure
                    && declaration.DeclarationKeyword?.Equals(
                        "Function",
                        StringComparison.OrdinalIgnoreCase) == true));
    }

    internal static bool IsReadableDefinition(VbaSourceDefinition definition)
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

    internal static bool IsWritableDefinition(VbaSourceDefinition definition)
        => definition.Kind switch
        {
            VbaSourceDefinitionKind.Variable
                or VbaSourceDefinitionKind.Parameter
                or VbaSourceDefinitionKind.TypeMember => true,
            VbaSourceDefinitionKind.Property =>
                definition.PropertyAccess.HasFlag(VbaPropertyAccess.Writable),
            _ => false
        };

    internal static VbaCallableCompletionContext GetCurrentCallableCompletionContext(
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

    internal static bool Contains(VbaSyntaxRange range, VbaSyntaxPosition position)
        => Compare(range.Start, position) <= 0 && Compare(position, range.End) <= 0;

    internal static bool Contains(VbaRange range, VbaPosition position)
        => Compare(range.Start, position) <= 0 && Compare(position, range.End) <= 0;

    internal static int Compare(VbaSyntaxPosition left, VbaSyntaxPosition right)
    {
        var lineComparison = left.Line.CompareTo(right.Line);
        return lineComparison != 0
            ? lineComparison
            : left.Character.CompareTo(right.Character);
    }

    internal static int Compare(VbaPosition left, VbaPosition right)
    {
        var lineComparison = left.Line.CompareTo(right.Line);
        return lineComparison != 0
            ? lineComparison
            : left.Character.CompareTo(right.Character);
    }

    internal bool TryResolveMemberDefinition(
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

    internal bool TryResolveWithEventsHandler(
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
            && nameResolution.SameDocument(
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
            if (eligibility.IntrinsicHostEventSurface is { } hostSurface
                && intrinsicHostEvents.TryCreateExistingHandlerEventTarget(
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

    internal (
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
                    nameResolution.EffectiveDeclaredTypes.Project(definition).Signature,
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

    internal static VbaEventHandlerValidationAuthority
        GetEventHandlerValidationAuthority(VbaSourceDefinition eventDefinition)
        => eventDefinition.Identity.Origin == VbaDefinitionOrigin.ProjectReference
            ? VbaEventHandlerValidationAuthority.ExternalTypeLibAdvisory
            : VbaEventHandlerValidationAuthority.SourceDeclared;

    internal static string? GetImmediateQualifier(
        VbaMemberAccessSyntax? access,
        VbaPositionIdentifierSyntax identifier)
        => access?.Target?.Range == identifier.Range && access.TargetSegmentIndex > 0
            ? access.Segments[access.TargetSegmentIndex - 1].Name
            : null;

    internal static VbaSyntaxTree GetSyntaxTree(VbaSourceDocument document)
        => document.SyntaxTree ?? VbaSyntaxTree.ParseModule(document.Uri, document.Text);
}

internal sealed record VbaCallableCompletionContext(
        string? ResultTargetName,
        string? SetterPropertyName,
        VbaPropertyAccessorKind? RequestedPropertyWriteAccessorKind)
    {
        public static VbaCallableCompletionContext None { get; } = new(
            null,
            null,
            null);
    }
