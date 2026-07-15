using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.Syntax;

namespace VbaLanguageServer.SourceModel;

internal sealed record VbaResolvedType(string Name, string? ReferenceName);

/// <summary>
/// Resolves VBA type references and member chains across source documents and active reference catalogs.
/// </summary>
internal sealed class VbaTypeResolution
{
    private readonly IReadOnlyList<VbaSourceDocument> documents;
    private readonly VbaProjectReferenceSelection? referenceSelection;
    private readonly VbaProjectReferenceCatalogSet referenceCatalogs;
    private readonly IReadOnlyList<VbaSourceDefinition> activeReferenceDefinitions;
    private readonly VbaNameResolutionService nameResolution;
    private readonly VbaResolutionPolicy resolutionPolicy;

    public VbaTypeResolution(
        IReadOnlyList<VbaSourceDocument> documents,
        VbaProjectReferenceSelection? referenceSelection,
        VbaProjectReferenceCatalogSet referenceCatalogs,
        IReadOnlyList<VbaSourceDefinition> activeReferenceDefinitions,
        VbaNameResolutionService nameResolution,
        VbaResolutionPolicy? resolutionPolicy = null)
    {
        this.documents = documents;
        this.referenceSelection = referenceSelection;
        this.referenceCatalogs = referenceCatalogs;
        this.activeReferenceDefinitions = activeReferenceDefinitions;
        this.nameResolution = nameResolution;
        this.resolutionPolicy = resolutionPolicy ?? new VbaResolutionPolicy();
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
                if (member?.TypeReference is null || !TryResolveTypeReference(currentDocument, member.TypeReference, out resolvedType))
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
        else if (firstDefinition is not null && resolutionPolicy.IsTypeDefinition(firstDefinition))
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
            if (member?.TypeReference is null || !TryResolveTypeReference(currentDocument, member.TypeReference, out resolvedType))
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
        definition = null;
        if (!string.IsNullOrWhiteSpace(typeReference.Qualifier))
        {
            definition = ResolveReferenceTypeDefinition(typeReference.Qualifier, typeReference.Name);
            if (definition is not null)
            {
                return true;
            }

            definition = ResolveSourceTypeDefinition(typeReference.Name, typeReference.Qualifier);
            return definition is not null;
        }

        definition = ResolveSourceTypeDefinition(typeReference.Name, qualifier: null);
        if (definition is not null)
        {
            return true;
        }

        var referenceCandidates = activeReferenceDefinitions
            .Where(definition => SameName(definition.Name, typeReference.Name))
            .Where(resolutionPolicy.IsTypeDefinition)
            .Where(definition => definition.ParentTypeName is null)
            .ToArray();
        var referenceTypeDefinition = ResolveReferenceCandidates(referenceCandidates);
        if (referenceTypeDefinition is null)
        {
            return false;
        }

        definition = referenceTypeDefinition;
        return true;
    }

    public IReadOnlyList<VbaSourceDefinition> GetMembersOfType(VbaSourceDocument currentDocument, VbaResolvedType resolvedType)
        => GetMemberCandidates(currentDocument, resolvedType)
            .GroupBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .Select(group => group.Single())
            .OrderBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public VbaSourceDefinition? ResolveMember(VbaSourceDocument currentDocument, VbaResolvedType resolvedType, string memberName)
    {
        var candidates = GetMemberCandidates(currentDocument, resolvedType)
            .Where(definition => SameName(definition.Name, memberName))
            .ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    public VbaSourceDefinition? ResolveEvent(VbaSourceDocument currentDocument, VbaResolvedType resolvedType, string eventName)
    {
        var candidates = GetMemberCandidates(currentDocument, resolvedType)
            .Where(definition => definition.Kind == VbaSourceDefinitionKind.Event)
            .Where(definition => SameName(definition.Name, eventName))
            .ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    public IEnumerable<VbaSourceDefinition> GetVisibleTypeDefinitions(VbaSourceDocument currentDocument)
    {
        foreach (var definition in currentDocument.Definitions.Where(resolutionPolicy.IsTypeDefinition))
        {
            yield return definition;
        }

        foreach (var definition in documents
            .Where(document => !SameUri(document.Uri, currentDocument.Uri))
            .SelectMany(document => document.Definitions)
            .Where(resolutionPolicy.IsTypeDefinition)
            .Where(definition => definition.Visibility == VbaSourceDefinitionVisibility.Public))
        {
            yield return definition;
        }

        foreach (var definition in activeReferenceDefinitions
            .Where(resolutionPolicy.IsTypeDefinition)
            .Where(definition => definition.ParentTypeName is null))
        {
            yield return definition;
        }
    }

    public VbaSourceDefinition? ResolveSourceTypeCompletionGroup(IReadOnlyList<VbaSourceDefinition> candidates)
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

    private IEnumerable<VbaSourceDefinition> GetMemberCandidates(VbaSourceDocument currentDocument, VbaResolvedType resolvedType)
    {
        if (resolvedType.ReferenceName is not null)
        {
            return activeReferenceDefinitions
                .Where(definition => SameName(definition.ModuleName, resolvedType.ReferenceName))
                .Where(definition => definition.ParentTypeName is not null)
                .Where(definition => SameName(definition.ParentTypeName!, resolvedType.Name));
        }

        return documents
            .Where(document => SameName(document.ModuleName, resolvedType.Name))
            .SelectMany(document => document.Definitions
                .Where(resolutionPolicy.IsReferenceTarget)
                .Where(definition =>
                    SameUri(document.Uri, currentDocument.Uri)
                    || definition.Visibility == VbaSourceDefinitionVisibility.Public));
    }

    private VbaSourceDefinition? ResolveReferenceTypeDefinition(string qualifier, string typeName)
    {
        if (referenceSelection is null)
        {
            return null;
        }

        var candidates = referenceCatalogs
            .GetQualifiedDefinitions(referenceSelection, qualifier, typeName)
            .Where(resolutionPolicy.IsTypeDefinition)
            .Where(definition => definition.ParentTypeName is null)
            .ToArray();
        return ResolveReferenceCandidates(candidates);
    }

    private VbaSourceDefinition? ResolveSourceTypeDefinition(string typeName, string? qualifier)
    {
        var candidates = documents
            .SelectMany(document => document.Definitions)
            .Where(resolutionPolicy.IsTypeDefinition)
            .Where(definition => SameName(definition.Name, typeName))
            .Where(definition => qualifier is null || SameName(definition.ModuleName, qualifier))
            .ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private VbaSourceDefinition? ResolveReferenceCandidates(IEnumerable<VbaSourceDefinition> candidates)
        => resolutionPolicy.ResolveReferenceCandidates(candidates, referenceSelection);

    private static VbaResolvedType ToResolvedType(VbaSourceDefinition definition)
        => new(
            definition.Name,
            VbaProjectReferenceCatalogSet.IsExternalDefinition(definition)
                ? definition.ModuleName
                : null);

    private static bool SameUri(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool SameName(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
