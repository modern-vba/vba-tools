using VbaTools.Syntax;

namespace VbaTools.Semantics;

internal sealed record VbaResolvedType(
    string Name,
    string? ReferenceName,
    VbaSourceDefinition? SourceDefinition);

/// <summary>
/// Propagates resolved VBA types through expressions and member chains.
/// </summary>
internal sealed class VbaTypeResolution
{
    private readonly VbaNameResolutionService nameResolution;

    public VbaTypeResolution(VbaNameResolutionService nameResolution)
    {
        this.nameResolution = nameResolution;
    }

    public bool TryResolveExpressionType(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        IReadOnlyList<VbaPositionIdentifierSyntax> segments,
        bool isLeadingDot,
        IReadOnlyList<VbaWithScopeSyntax> withScopes,
        out VbaResolvedType resolvedType)
    {
        if (segments.Count == 0 && !isLeadingDot)
        {
            resolvedType = default!;
            return false;
        }

        return TryResolveExpressionType(
            currentDocument,
            line,
            character,
            segments,
            isLeadingDot,
            withScopes,
            resolvedWithReceivers: null,
            out resolvedType);
    }

    private bool TryResolveExpressionType(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        IReadOnlyList<VbaPositionIdentifierSyntax> segments,
        bool isLeadingDot,
        IReadOnlyList<VbaWithScopeSyntax> withScopes,
        IReadOnlyList<VbaResolvedType>? resolvedWithReceivers,
        out VbaResolvedType resolvedType)
    {
        resolvedType = default!;
        if (isLeadingDot)
        {
            if (resolvedWithReceivers is { Count: > 0 })
            {
                resolvedType = resolvedWithReceivers[^1];
            }
            else if (!TryResolveWithReceiverType(
                         currentDocument,
                         line,
                         character,
                         withScopes,
                         out resolvedType))
            {
                return false;
            }

            foreach (var segment in segments)
            {
                if (!TryResolveMemberResultType(
                        currentDocument,
                        resolvedType,
                        segment.Name,
                        out var target,
                        out resolvedType))
                {
                    return false;
                }

                if (!TryApplyInvocationResultType(
                        currentDocument,
                        segment,
                        target,
                        ref resolvedType))
                {
                    return false;
                }
            }

            return true;
        }

        if (segments.Count >= 2 && TryResolveTypeReference(
                currentDocument,
                new VbaTypeReference(segments[1].Name, segments[0].Name),
                out resolvedType))
        {
            for (var index = 2; index < segments.Count; index++)
            {
                if (!TryResolveMemberResultType(
                        currentDocument,
                        resolvedType,
                        segments[index].Name,
                        out var target,
                        out resolvedType))
                {
                    return false;
                }

                if (!TryApplyInvocationResultType(
                        currentDocument,
                        segments[index],
                        target,
                        ref resolvedType))
                {
                    return false;
                }
            }

            return true;
        }

        var firstOutcome = nameResolution.ResolveValueOutcome(
            currentDocument.Uri,
            new VbaPosition(line, character),
            qualifier: null,
            segments[0].Name);
        var firstTarget = firstOutcome.Target;
        if (firstOutcome.Kind == VbaNameResolutionKind.Unresolved)
        {
            var enumQualifier = nameResolution.ResolveTypeDefinitionOutcome(
                currentDocument, new VbaTypeReference(segments[0].Name));
            if (enumQualifier.Kind == VbaNameResolutionKind.Resolved
                && enumQualifier.Target is { } enumTarget
                && enumTarget.PhysicalDefinitions.All(definition => definition.Kind == VbaSourceDefinitionKind.Enum))
            {
                firstTarget = enumTarget;
            }
        }
        if (firstTarget is not null
            && firstTarget.PhysicalDefinitions.All(definition => definition.Kind == VbaSourceDefinitionKind.Enum))
        {
            resolvedType = ToResolvedType(firstTarget.SelectedDefinition);
        }
        else if (firstTarget?.IsConditionalFamily == true)
        {
            if (!TryResolveConditionalZeroArgumentResultType(
                    currentDocument,
                    firstTarget,
                    out resolvedType))
            {
                return false;
            }
        }
        else
        {
            var firstDefinition = firstTarget?.SelectedDefinition;
            if (firstDefinition is not null && nameResolution.EffectiveDeclaredTypes.Get(firstDefinition).Reference is not null)
            {
                if (!TryResolveDefinitionTypeReference(
                    currentDocument,
                    firstDefinition,
                    out resolvedType))
                {
                    return false;
                }
            }
            else if (firstDefinition is not null && nameResolution.IsTypeDefinition(firstDefinition))
            {
                resolvedType = ToResolvedType(firstDefinition);
            }
            else
            {
                return false;
            }
        }

        if (firstTarget is not null
            && !TryApplyInvocationResultType(
                currentDocument,
                segments[0],
                firstTarget,
                ref resolvedType))
        {
            return false;
        }

        for (var index = 1; index < segments.Count; index++)
        {
            if (!TryResolveMemberResultType(
                    currentDocument,
                    resolvedType,
                    segments[index].Name,
                    out var target,
                    out resolvedType))
            {
                return false;
            }

            if (!TryApplyInvocationResultType(
                    currentDocument,
                    segments[index],
                    target,
                    ref resolvedType))
            {
                return false;
            }
        }

        return true;
    }

    private bool TryResolveMemberResultType(
        VbaSourceDocument currentDocument,
        VbaResolvedType receiverType,
        string memberName,
        out VbaResolvedNameTarget target,
        out VbaResolvedType resolvedType)
    {
        target = default!;
        resolvedType = default!;
        var resolvedTarget = nameResolution.ResolveMemberOutcome(
            currentDocument,
            receiverType,
            memberName).Target;
        if (resolvedTarget is null)
        {
            return false;
        }

        target = resolvedTarget;

        if (!target.IsConditionalFamily)
        {
            return TryResolveDefinitionTypeReference(
                currentDocument,
                target.SelectedDefinition,
                out resolvedType);
        }

        if (target.PhysicalDefinitions.Any(definition =>
                definition.Signature is not null))
        {
            return TryResolveConditionalZeroArgumentResultType(
                currentDocument,
                target,
                out resolvedType);
        }

        VbaResolvedType? converged = null;
        foreach (var definition in target.PhysicalDefinitions)
        {
            if (!TryResolveDefinitionTypeReference(
                    currentDocument,
                    definition,
                    out var variantType)
                || converged is not null
                    && !HasSameCanonicalIdentity(converged, variantType))
            {
                return false;
            }

            converged = variantType;
        }

        if (converged is null)
        {
            return false;
        }

        resolvedType = converged;
        return true;
    }

    private bool TryApplyInvocationResultType(
        VbaSourceDocument currentDocument,
        VbaPositionIdentifierSyntax segment,
        VbaResolvedNameTarget resolvedTarget,
        ref VbaResolvedType resolvedType)
    {
        var syntaxTree = currentDocument.SyntaxTree
            ?? VbaSyntaxTree.ParseModule(currentDocument.Uri, currentDocument.Text);
        var argumentLists = syntaxTree.Module.ArgumentLists
            .Where(candidate => candidate.Form == VbaCallSyntaxForm.Parenthesized
                && !candidate.IsIncomplete
                && candidate.CalleeRange?.End == segment.Range.End)
            .ToArray();
        if (argumentLists.Length == 0)
        {
            return true;
        }

        if (argumentLists.Length != 1)
        {
            return false;
        }

        var callSite = CreateCompleteCallSite(argumentLists[0]);
        var receiverDefinitions = GetReadableDefinitions(
                currentDocument,
                resolvedTarget)
            .ToArray();
        var directClassifications = receiverDefinitions
            .Select(definition => ClassifyDirectInvocation(definition, callSite))
            .ToArray();
        if (directClassifications.Length > 0
            && directClassifications.All(classification =>
                classification == VbaDirectInvocationClassification.Applicable))
        {
            return HaveConvergedResultTypes(
                currentDocument,
                receiverDefinitions,
                resolvedType);
        }
        if (directClassifications.Length == 0
            || directClassifications.Any(classification =>
                classification
                    != VbaDirectInvocationClassification.RequiresDefaultMember))
        {
            return false;
        }

        if (!TryResolveCompleteDefaultMemberTarget(
                currentDocument,
                resolvedType,
                out var defaultTarget,
                out _))
        {
            return false;
        }

        VbaResolvedType? converged = null;
        var defaultDefinitions = GetReadableDefinitions(currentDocument, defaultTarget!);
        foreach (var definition in defaultDefinitions)
        {
            if (!HasCompleteApplicableShape(
                    MapValueReadInvocation(definition, callSite))
                || !HasKnownScalarResult(definition)
                || !TryResolveDefinitionTypeReference(
                    currentDocument,
                    definition,
                    out var resultType)
                || converged is not null
                    && !HasSameCanonicalIdentity(converged, resultType))
            {
                return false;
            }

            converged = resultType;
        }

        if (converged is null)
        {
            return false;
        }

        resolvedType = converged;
        return true;
    }

    public bool TryResolveImplicitDefaultMemberTarget(
        VbaSourceDocument currentDocument,
        VbaResolvedNameTarget resolvedTarget,
        VbaCallSiteSyntax callSite,
        out VbaResolvedNameTarget? defaultTarget)
    {
        defaultTarget = null;
        var receiverDefinitions = GetReadableDefinitions(
                currentDocument,
                resolvedTarget)
            .ToArray();
        if (receiverDefinitions.Length == 0
            || receiverDefinitions.Any(definition =>
                ClassifyDirectInvocation(definition, callSite)
                    != VbaDirectInvocationClassification.RequiresDefaultMember)
            || !TryGetConvergedResultType(
                currentDocument,
                receiverDefinitions,
                out var receiverType))
        {
            return false;
        }

        return TryResolveCompleteDefaultMemberTarget(
            currentDocument,
            receiverType,
            out defaultTarget,
            out _);
    }

    private bool TryResolveCompleteDefaultMemberTarget(
        VbaSourceDocument currentDocument,
        VbaResolvedType receiverType,
        out VbaResolvedNameTarget? defaultTarget,
        out VbaResolvedType defaultResultType)
    {
        defaultTarget = null;
        defaultResultType = null!;
        var defaultOutcome = nameResolution.ResolveDefaultMemberOutcome(
            currentDocument,
            receiverType);
        if (defaultOutcome.Kind != VbaNameResolutionKind.Resolved
            || defaultOutcome.Target is not { } resolvedDefaultTarget)
        {
            return false;
        }

        var defaultDefinitions = GetReadableDefinitions(
                currentDocument,
                resolvedDefaultTarget)
            .ToArray();
        if (defaultDefinitions.Length == 0
            || defaultDefinitions.Any(definition =>
                !definition.IsDefaultMember
                || !definition.IsCallableMetadataComplete
                || definition.Signature is null)
            || !TryGetConvergedResultType(
                currentDocument,
                defaultDefinitions,
                out defaultResultType))
        {
            return false;
        }

        defaultTarget = resolvedDefaultTarget;
        return true;
    }

    private static IEnumerable<VbaSourceDefinition> GetReadableDefinitions(
        VbaSourceDocument currentDocument,
        VbaResolvedNameTarget target)
        => (target is VbaPropertyNameTarget propertyTarget
                ? propertyTarget.Property.PropertyDefinitions
                : target.PhysicalDefinitions)
            .Where(definition =>
                VbaDocumentIdentityPolicy.SameDocument(
                    definition.Uri,
                    currentDocument.Uri)
                || definition.Visibility.IsProjectVisible())
            .Where(definition => definition.PropertyAccessorKind is not (
                VbaPropertyAccessorKind.Let or VbaPropertyAccessorKind.Set));

    private static VbaCompleteCallArgumentMapping MapValueReadInvocation(
        VbaSourceDefinition definition,
        VbaCallSiteSyntax callSite)
    {
        var signature = definition.Signature!;
        return VbaCallArgumentMapper.MapComplete(
            signature,
            callSite,
            allowNamedArguments: definition.Kind != VbaSourceDefinitionKind.Event
                && signature.CallableKind != VbaCallableKind.Event,
            VbaCallArgumentMapper.GetContextCompatibility(
                definition,
                signature,
                VbaCallContext.ValueRead));
    }

    private static bool HasCompleteApplicableShape(
        VbaCompleteCallArgumentMapping mapping)
        => mapping.Mapping.HasValidSignatureShape
            && !mapping.Mapping.HasStructuralMismatch
            && !mapping.Mapping.HasIndeterminateMapping
            && mapping.Mapping.ContextCompatibility
                == VbaCallContextCompatibility.Compatible
            && mapping.MissingRequiredParameterIndexes.Count == 0;

    private static VbaDirectInvocationClassification ClassifyDirectInvocation(
        VbaSourceDefinition definition,
        VbaCallSiteSyntax callSite)
    {
        if (HasIndeterminateForeignArrayResult(definition, callSite))
        {
            return VbaDirectInvocationClassification.Indeterminate;
        }

        if (IsParameterlessArrayValue(definition))
        {
            return IsCompleteArrayIndex(callSite)
                ? VbaDirectInvocationClassification.Applicable
                : VbaDirectInvocationClassification.Indeterminate;
        }

        if (definition.Signature is { } signature)
        {
            if (!definition.IsCallableMetadataComplete)
            {
                return VbaDirectInvocationClassification.Indeterminate;
            }

            var mapping = MapValueReadInvocation(definition, callSite);
            if (HasCompleteApplicableShape(mapping))
            {
                return VbaDirectInvocationClassification.Applicable;
            }

            return signature.Parameters.Count == 0
                && VbaCallArgumentMapper.GetContextCompatibility(
                    definition,
                    signature,
                    VbaCallContext.ValueRead)
                    == VbaCallContextCompatibility.Compatible
                ? VbaDirectInvocationClassification.RequiresDefaultMember
                : VbaDirectInvocationClassification.Indeterminate;
        }

        var isCompleteParameterlessPropertyGet =
            definition.Kind == VbaSourceDefinitionKind.Property
            && definition.IsCallableMetadataComplete
            && definition.PropertyAccessorKind
                == VbaPropertyAccessorKind.Get
            && definition.PropertyAccess.HasFlag(
                VbaPropertyAccess.Readable)
            && definition.TypeReference is not null;
        if (isCompleteParameterlessPropertyGet)
        {
            return callSite.Arguments.Count == 0
                ? VbaDirectInvocationClassification.Applicable
                : VbaDirectInvocationClassification.RequiresDefaultMember;
        }

        return definition.CallableKind is null
            && definition.Kind is VbaSourceDefinitionKind.Variable
                or VbaSourceDefinitionKind.Parameter
                or VbaSourceDefinitionKind.TypeMember
            && definition.TypeReference is not null
                ? VbaDirectInvocationClassification.RequiresDefaultMember
                : VbaDirectInvocationClassification.Indeterminate;
    }

    private static bool HasIndeterminateForeignArrayResult(
        VbaSourceDefinition definition,
        VbaCallSiteSyntax callSite)
        => definition.Identity.Origin != VbaDefinitionOrigin.Source
            && definition.IsReturnArray is null
            && callSite.Arguments.Count > 0
            && IsCompleteParameterlessCallableResult(definition);

    private static bool IsCompleteParameterlessCallableResult(
        VbaSourceDefinition definition)
    {
        if (!definition.IsCallableMetadataComplete
            || definition.TypeReference is null)
        {
            return false;
        }

        if (definition.Signature is { } signature)
        {
            return signature.Parameters.Count == 0
                && VbaCallArgumentMapper.GetContextCompatibility(
                    definition,
                    signature,
                    VbaCallContext.ValueRead)
                    == VbaCallContextCompatibility.Compatible;
        }

        return definition.Kind == VbaSourceDefinitionKind.Property
            && definition.PropertyAccessorKind == VbaPropertyAccessorKind.Get
            && definition.PropertyAccess.HasFlag(VbaPropertyAccess.Readable);
    }

    private static bool IsParameterlessArrayValue(VbaSourceDefinition definition)
    {
        var isArray = definition.Identity.Origin == VbaDefinitionOrigin.Source
            ? definition.IsArray
            : definition.IsReturnArray == true;
        if (!isArray || !definition.IsCallableMetadataComplete)
        {
            return false;
        }

        if (IsCompleteParameterlessCallableResult(definition))
        {
            return true;
        }

        return definition.CallableKind is null
            && definition.Kind is VbaSourceDefinitionKind.Variable
                or VbaSourceDefinitionKind.Parameter
                or VbaSourceDefinitionKind.TypeMember
            && definition.TypeReference is not null;
    }

    private static bool IsCompleteArrayIndex(VbaCallSiteSyntax callSite)
        => callSite.Form == VbaCallSyntaxForm.Parenthesized
            && !callSite.IsIncomplete
            && callSite.Arguments.Count > 0
            && callSite.Arguments.All(argument =>
                argument.Name is null && !argument.IsOmitted);

    private static bool HasKnownScalarResult(VbaSourceDefinition definition)
        => definition.Identity.Origin == VbaDefinitionOrigin.Source
            ? !definition.IsArray
            : definition.IsReturnArray == false;

    private bool HaveConvergedResultTypes(
        VbaSourceDocument currentDocument,
        IReadOnlyList<VbaSourceDefinition> definitions,
        VbaResolvedType expectedType)
    {
        return TryGetConvergedResultType(
                currentDocument,
                definitions,
                out var resultType)
            && HasSameCanonicalIdentity(expectedType, resultType);
    }

    private bool TryGetConvergedResultType(
        VbaSourceDocument currentDocument,
        IReadOnlyList<VbaSourceDefinition> definitions,
        out VbaResolvedType resolvedType)
    {
        resolvedType = null!;
        VbaResolvedType? converged = null;
        foreach (var definition in definitions)
        {
            if (!TryResolveDefinitionTypeReference(
                    currentDocument,
                    definition,
                    out var resultType)
                || converged is not null
                    && !HasSameCanonicalIdentity(converged, resultType))
            {
                return false;
            }

            converged = resultType;
        }

        if (converged is null)
        {
            return false;
        }

        resolvedType = converged;
        return true;
    }

    private enum VbaDirectInvocationClassification
    {
        Applicable,
        RequiresDefaultMember,
        Indeterminate
    }

    private static VbaCallSiteSyntax CreateCompleteCallSite(
        VbaArgumentListSyntax argumentList)
    {
        var calleeRange = argumentList.CalleeRange ?? argumentList.Range;
        var callee = new VbaMemberAccessSyntax(
            [new VbaPositionIdentifierSyntax(
                argumentList.Callee,
                calleeRange,
                IsKeyword: false)],
            TargetSegmentIndex: 0,
            IsLeadingDot: argumentList.Callee.StartsWith(".", StringComparison.Ordinal),
            IsIncomplete: false,
            HasTrailingWhitespace: false,
            calleeRange);
        var arguments = argumentList.Arguments
            .Select((argument, index) => new VbaCallArgumentSyntax(
                index,
                argument.Name,
                argument.Kind == VbaArgumentKind.Omitted,
                argument.Range,
                argument.ValueText,
                argument.ValueRange))
            .ToArray();
        return new VbaCallSiteSyntax(
            argumentList.Form,
            callee,
            arguments,
            ActiveArgumentIndex: arguments.Length,
            ActiveNamedArgument: null,
            argumentList.IsIncomplete);
    }

    private bool TryResolveConditionalZeroArgumentResultType(
        VbaSourceDocument currentDocument,
        VbaResolvedNameTarget target,
        out VbaResolvedType resolvedType)
    {
        resolvedType = default!;
        var definitions = target.PhysicalDefinitions.ToArray();
        if (target is VbaPropertyNameTarget)
        {
            definitions = definitions
                .Where(definition => definition.PropertyAccessorKind is not (
                    VbaPropertyAccessorKind.Let or VbaPropertyAccessorKind.Set))
                .ToArray();
        }

        if (definitions.Length == 0
            || definitions
                .Select(definition => definition.Identity)
                .Distinct()
                .Count() != definitions.Length)
        {
            return false;
        }

        VbaResolvedType? converged = null;
        foreach (var definition in definitions)
        {
            var signature = definition.Signature;
            if (!VbaDocumentIdentityPolicy.SameDocument(
                    definition.Uri,
                    currentDocument.Uri)
                    && !definition.Visibility.IsProjectVisible()
                || signature is null
                || VbaCallArgumentMapper.MapCompleteZeroArgument(
                        signature,
                        VbaCallArgumentMapper.GetContextCompatibility(
                            definition,
                            signature,
                            VbaCallContext.ValueRead)).State
                    != VbaCallCompatibilityState.Applicable
                || !TryResolveDefinitionTypeReference(
                    currentDocument,
                    definition,
                    out var variantType))
            {
                return false;
            }

            if (converged is not null && !HasSameCanonicalIdentity(converged, variantType))
            {
                return false;
            }

            converged = variantType;
        }

        if (converged is null)
        {
            return false;
        }

        resolvedType = converged;
        return true;
    }

    public bool TryResolveConditionalCallResultType(
        VbaSourceDocument currentDocument,
        VbaConditionalCallCompatibility compatibility,
        out VbaResolvedType resolvedType)
    {
        resolvedType = default!;
        if (!compatibility.Target.IsConditionalFamily
            || compatibility.Variants.Count == 0)
        {
            return false;
        }

        var expectedDefinitions = compatibility.Target.PhysicalDefinitions.ToArray();
        var variants = compatibility.Variants.ToArray();
        if (compatibility.Target is VbaPropertyNameTarget)
        {
            expectedDefinitions = expectedDefinitions
                .Where(definition => definition.PropertyAccessorKind is not (
                    VbaPropertyAccessorKind.Let or VbaPropertyAccessorKind.Set))
                .ToArray();
            variants = variants
                .Where(variant => variant.Definition.PropertyAccessorKind is not (
                    VbaPropertyAccessorKind.Let or VbaPropertyAccessorKind.Set))
                .ToArray();
        }

        var expectedIdentities = expectedDefinitions
            .Select(definition => definition.Identity)
            .ToHashSet();
        var variantIdentities = variants
            .Select(variant => variant.Definition.Identity)
            .ToHashSet();
        if (expectedDefinitions.Length == 0
            || variants.Length != expectedDefinitions.Length
            || expectedIdentities.Count != expectedDefinitions.Length
            || variantIdentities.Count != variants.Length
            || !expectedIdentities.SetEquals(variantIdentities))
        {
            return false;
        }

        VbaResolvedType? converged = null;
        foreach (var variant in variants)
        {
            var signature = variant.Signature;
            if (variant.State != VbaCallCompatibilityState.Applicable
                || signature is null
                || (signature.CallableKind != VbaCallableKind.Function
                    && !(signature.CallableKind == VbaCallableKind.Property
                        && variant.Definition.PropertyAccess.HasFlag(
                            VbaPropertyAccess.Readable)))
                || !TryResolveDefinitionTypeReference(
                    currentDocument,
                    variant.Definition,
                    out var variantType))
            {
                return false;
            }

            if (converged is not null
                && !HasSameCanonicalIdentity(converged, variantType))
            {
                return false;
            }

            converged = variantType;
        }

        if (converged is null)
        {
            return false;
        }

        resolvedType = converged;
        return true;
    }

    private static bool HasSameCanonicalIdentity(
        VbaResolvedType left,
        VbaResolvedType right)
    {
        if (left.SourceDefinition is not null || right.SourceDefinition is not null)
        {
            return left.SourceDefinition?.Identity == right.SourceDefinition?.Identity;
        }

        return left.Name.Equals(right.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                left.ReferenceName,
                right.ReferenceName,
                StringComparison.OrdinalIgnoreCase);
    }

    public bool TryResolveTypeReference(
        VbaSourceDocument currentDocument,
        VbaTypeReference typeReference,
        out VbaResolvedType resolvedType)
    {
        resolvedType = default!;
        if (!TryResolveTypeReferenceDefinition(currentDocument, typeReference, out var definition)
            || definition is null)
        {
            return false;
        }

        resolvedType = ToResolvedType(definition);
        return true;
    }

    public bool TryResolveDefinitionTypeReference(
        VbaSourceDocument currentDocument,
        VbaSourceDefinition definition,
        out VbaResolvedType resolvedType)
    {
        var type = nameResolution.EffectiveDeclaredTypes.Get(definition);
        if (type.State != VbaEffectiveDeclaredTypeState.Known || type.Target is null)
        {
            resolvedType = default!;
            return false;
        }
        resolvedType = ToResolvedType(type.Target.SelectedDefinition);
        return true;
    }

    public bool TryResolveTypeReferenceDefinition(
        VbaSourceDocument currentDocument,
        VbaPositionTypeReferenceSyntax typeReference,
        VbaPositionIdentifierSyntax identifier,
        out VbaSourceDefinition? definition)
    {
        definition = null;
        if (typeReference.Name is null
            || typeReference.Name.Range != identifier.Range)
        {
            return false;
        }

        TryResolveTypeReferenceDefinition(
            currentDocument,
            new VbaTypeReference(typeReference.Name.Name, typeReference.Qualifier?.Name),
            out definition);
        return true;
    }

    public bool TryClassifyTypeReferenceDefinition(
        VbaSourceDocument currentDocument,
        VbaPositionTypeReferenceSyntax typeReference,
        VbaPositionIdentifierSyntax identifier,
        out VbaNameResolutionOutcome outcome)
    {
        if (typeReference.Name is null
            || typeReference.Name.Range != identifier.Range)
        {
            outcome = VbaNameResolutionOutcome.AnalysisIncomplete;
            return false;
        }

        outcome = nameResolution.ResolveTypeDefinitionOutcome(
            currentDocument,
            new VbaTypeReference(
                typeReference.Name.Name,
                typeReference.Qualifier?.Name));
        return true;
    }

    public bool TryResolveTypeReferenceDefinition(
        VbaSourceDocument currentDocument,
        VbaTypeReference typeReference,
        out VbaSourceDefinition? definition)
    {
        definition = nameResolution.ResolveTypeDefinition(currentDocument, typeReference);
        return definition is not null;
    }

    public IReadOnlyList<VbaSourceDefinition> GetMembersOfType(VbaSourceDocument currentDocument, VbaResolvedType resolvedType)
        => nameResolution.GetMembersOfType(currentDocument, resolvedType);

    public VbaSourceDefinition? ResolveMember(VbaSourceDocument currentDocument, VbaResolvedType resolvedType, string memberName)
        => nameResolution.ResolveMember(
            currentDocument,
            resolvedType,
            memberName);

    public VbaSourceDefinition? ResolveEvent(VbaSourceDocument currentDocument, VbaResolvedType resolvedType, string eventName)
        => nameResolution.ResolveMember(
            currentDocument,
            resolvedType,
            eventName,
            VbaSourceDefinitionKind.Event);

    public IEnumerable<VbaSourceDefinition> GetVisibleTypeDefinitions(VbaSourceDocument currentDocument)
        => nameResolution.GetVisibleTypeDefinitions(currentDocument);

    public VbaSourceDefinition? ResolveSourceTypeCompletionGroup(IReadOnlyList<VbaSourceDefinition> candidates)
        => nameResolution.ResolveSourceTypeCompletionGroup(candidates);

    private bool TryResolveWithReceiverType(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        IReadOnlyList<VbaWithScopeSyntax> withScopes,
        out VbaResolvedType resolvedType)
    {
        resolvedType = default!;
        if (withScopes.Count == 0)
        {
            return false;
        }

        var resolvedScopes = new List<VbaResolvedType>();
        foreach (var scope in withScopes)
        {
            if (scope.Receiver is null
                || !TryResolveExpressionType(
                    currentDocument,
                    line,
                    character,
                    scope.Receiver.Segments,
                    scope.Receiver.IsLeadingDot,
                    [],
                    resolvedScopes,
                    out var scopeType))
            {
                return false;
            }

            resolvedScopes.Add(scopeType);
        }

        resolvedType = resolvedScopes[^1];
        return true;
    }

    private static VbaResolvedType ToResolvedType(VbaSourceDefinition definition)
        => new(
            definition.Name,
            definition.Identity.Origin == VbaDefinitionOrigin.ProjectReference
                ? definition.ModuleName
                : null,
            definition.Identity.Origin == VbaDefinitionOrigin.ProjectReference
                ? null
                : definition);
}
