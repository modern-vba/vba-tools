using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.Syntax;

namespace VbaLanguageServer.SourceModel;

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
            segments.Select(segment => segment.Name).ToArray(),
            isLeadingDot,
            withScopes,
            resolvedWithReceivers: null,
            out resolvedType);
    }

    private bool TryResolveExpressionType(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        IReadOnlyList<string> parts,
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

            foreach (var memberName in parts)
            {
                if (!TryResolveMemberResultType(
                        currentDocument,
                        resolvedType,
                        memberName,
                        out resolvedType))
                {
                    return false;
                }
            }

            return true;
        }

        if (parts.Count >= 2 && TryResolveTypeReference(currentDocument, new VbaTypeReference(parts[1], parts[0]), out resolvedType))
        {
            for (var index = 2; index < parts.Count; index++)
            {
                if (!TryResolveMemberResultType(
                        currentDocument,
                        resolvedType,
                        parts[index],
                        out resolvedType))
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
            parts[0]);
        var firstTarget = firstOutcome.Target;
        if (firstTarget?.IsConditionalFamily == true)
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
            if (firstDefinition?.TypeReference is not null)
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

        for (var index = 1; index < parts.Count; index++)
        {
            if (!TryResolveMemberResultType(
                    currentDocument,
                    resolvedType,
                    parts[index],
                    out resolvedType))
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
        out VbaResolvedType resolvedType)
    {
        resolvedType = default!;
        var target = nameResolution.ResolveMemberOutcome(
            currentDocument,
            receiverType,
            memberName).Target;
        if (target is null)
        {
            return false;
        }

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
            if (!VbaProjectIdentityModel.SameDocument(
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
                || definition.TypeReference is null
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
                || variant.Definition.TypeReference is null
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
        resolvedType = default!;
        if (definition.TypeReference is null)
        {
            return false;
        }

        VbaSourceDefinition? typeDefinition;
        if (definition.Identity.Origin == VbaDefinitionOrigin.ProjectReference)
        {
            typeDefinition = nameResolution.ResolveProjectReferenceTypeDefinition(
                definition.Identity.ReferenceName ?? definition.ModuleName,
                definition.TypeReference);
        }
        else if (nameResolution.FindDocument(definition.Uri) is not { } ownerDocument
            || !TryResolveTypeReferenceDefinition(
                ownerDocument,
                definition.TypeReference,
                out typeDefinition))
        {
            return false;
        }

        if (typeDefinition is null)
        {
            return false;
        }

        resolvedType = ToResolvedType(typeDefinition);
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
                    scope.Receiver.Segments.Select(segment => segment.Name).ToArray(),
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
