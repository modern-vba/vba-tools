using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.Syntax;

namespace VbaLanguageServer.SourceModel;

internal sealed record VbaResolvedType(string Name, string? ReferenceName);

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
                var member = ResolveMember(currentDocument, resolvedType, memberName);
                if (member is null
                    || !TryResolveDefinitionTypeReference(
                        currentDocument,
                        member,
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
                var member = ResolveMember(currentDocument, resolvedType, parts[index]);
                if (member is null
                    || !TryResolveDefinitionTypeReference(
                        currentDocument,
                        member,
                        out resolvedType))
                {
                    return false;
                }
            }

            return true;
        }

        var firstDefinition = nameResolution.ResolveValue(
            currentDocument.Uri,
            new VbaPosition(line, character),
            qualifier: null,
            parts[0]);
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

        for (var index = 1; index < parts.Count; index++)
        {
            var member = ResolveMember(currentDocument, resolvedType, parts[index]);
            if (member is null
                || !TryResolveDefinitionTypeReference(
                    currentDocument,
                    member,
                    out resolvedType))
            {
                return false;
            }
        }

        return true;
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
        else if (!TryResolveTypeReferenceDefinition(
                     currentDocument,
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

    public bool TryResolveTypeReferenceDefinition(
        VbaSourceDocument currentDocument,
        VbaTypeReference typeReference,
        out VbaSourceDefinition? definition)
    {
        definition = nameResolution.ResolveTypeDefinition(currentDocument, typeReference);
        return definition is not null;
    }

    public IReadOnlyList<VbaSourceDefinition> GetMembersOfType(VbaSourceDocument currentDocument, VbaResolvedType resolvedType)
        => nameResolution.GetMembersOfType(currentDocument, resolvedType.Name, resolvedType.ReferenceName);

    public VbaSourceDefinition? ResolveMember(VbaSourceDocument currentDocument, VbaResolvedType resolvedType, string memberName)
        => nameResolution.ResolveMember(currentDocument, resolvedType.Name, resolvedType.ReferenceName, memberName);

    public VbaSourceDefinition? ResolveEvent(VbaSourceDocument currentDocument, VbaResolvedType resolvedType, string eventName)
        => nameResolution.ResolveMember(
            currentDocument,
            resolvedType.Name,
            resolvedType.ReferenceName,
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
                : null);
}
