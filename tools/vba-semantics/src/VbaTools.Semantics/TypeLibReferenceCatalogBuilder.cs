using System.Text;
using VbaTools.Syntax;

namespace VbaTools.Semantics;

/// <summary>
/// Builds a VBA project reference catalog from TypeLib metadata.
/// </summary>
public static class TypeLibReferenceCatalogBuilder
{
    /// <summary>
    /// Builds catalog definitions from TypeLib metadata.
    /// </summary>
    /// <param name="referenceName">The manifest reference name.</param>
    /// <param name="metadata">The TypeLib metadata.</param>
    /// <returns>The generated reference catalog.</returns>
    public static VbaProjectReferenceCatalog Build(string referenceName, TypeLibCatalogMetadata metadata)
    {
        var aliases = new[] { metadata.QualifierAlias, CreateQualifierAlias(referenceName) }
            .Where(alias => IsSingleLineForeignIdentifier(alias))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var definitions = new List<VbaProjectReferenceDefinition>();

        foreach (var type in metadata.Types.Where(type => !string.IsNullOrEmpty(type.Name)))
        {
            var isExplicitlyResolvableCoClass =
                type.Metadata?.RawTypeKind == TypeLibCatalogRawTypeKind.CoClass;
            if (!type.IsBrowsable
                && !type.IsApplicationObject
                && !type.IsCreatable
                && !isExplicitlyResolvableCoClass)
            {
                continue;
            }

            if (type.IsBrowsable || type.IsCreatable || isExplicitlyResolvableCoClass)
            {
                definitions.Add(new VbaProjectReferenceDefinition(
                    referenceName,
                    type.Name,
                    type.Kind,
                    type.Documentation,
                    IsCreatable: type.IsCreatable,
                    IsAuthoringAvailable: type.IsBrowsable));
            }

            foreach (var member in type.Members.Where(member =>
                !string.IsNullOrEmpty(member.Name)
                && (TypeLibCatalogMemberFacts.IsBrowsableForNameAuthoring(member)
                    || member.Kind == VbaSourceDefinitionKind.Event
                    || member.Metadata?.MemberId == 0)))
            {
                var isCallableMetadataComplete =
                    member.Metadata?.IsComplete ?? true;
                definitions.Add(new VbaProjectReferenceDefinition(
                    referenceName,
                    member.Name,
                    member.Kind,
                    member.Documentation,
                    member.Signature is null || !isCallableMetadataComplete
                        ? null
                        : member.Signature with { SupportsNamedArguments = true },
                    ParentTypeName: type.Name,
                    TypeReference: member.TypeReference,
                    PropertyAccess: member.PropertyAccess,
                    GlobalExposure: GetGlobalExposure(type),
                    IsAuthoringAvailable:
                        member.Kind == VbaSourceDefinitionKind.Event
                            ? TypeLibCatalogMemberFacts.IsAuthoringAvailable(member)
                            : TypeLibCatalogMemberFacts
                                .IsBrowsableForNameAuthoring(member),
                    IsCallableMetadataComplete:
                        isCallableMetadataComplete)
                {
                    IsDefaultMember = member.Metadata?.MemberId == 0,
                    PropertyAccessorKind = member.Kind
                        == VbaSourceDefinitionKind.Property
                            ? member.Metadata?.PropertyAccessorKind
                            : null,
                    IsReturnArray = member.Metadata?.IsReturnArray,
                    CallableKind = member.Signature?.CallableKind
                });
            }
        }

        return new VbaProjectReferenceCatalog(
            referenceName,
            aliases,
            DeduplicateDefinitions(definitions),
            metadata.Types
                .Where(type => type.Metadata is not null)
                .ToArray())
        {
            ReferencedVbaProjectName = string.IsNullOrEmpty(
                metadata.ReferencedVbaProjectName)
                    ? null
                    : metadata.ReferencedVbaProjectName
        };
    }

    private static ReferenceDefinitionGlobalExposure GetGlobalExposure(TypeLibCatalogType type)
        => type.IsApplicationObject
            ? ReferenceDefinitionGlobalExposure.MainHostGlobal
            : type.Kind is VbaSourceDefinitionKind.Module or VbaSourceDefinitionKind.Enum
                ? ReferenceDefinitionGlobalExposure.LibraryGlobal
                : ReferenceDefinitionGlobalExposure.None;

    private static bool IsSingleLineForeignIdentifier(string? value)
        => !string.IsNullOrEmpty(value)
            && !value.Contains('\r')
            && !value.Contains('\n');

    private static IReadOnlyList<VbaProjectReferenceDefinition> DeduplicateDefinitions(
        IReadOnlyList<VbaProjectReferenceDefinition> definitions)
    {
        return definitions
            .GroupBy(
                definition => string.Join(
                    "\u001f",
                    definition.ReferenceName,
                    definition.Name,
                    definition.Kind.ToString(),
                    definition.ParentTypeName ?? "",
                    definition.PropertyAccessorKind?.ToString() ?? ""),
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var selected = group
                    .OrderByDescending(definition => definition.TypeReference is not null)
                    .ThenByDescending(definition => definition.Signature is not null)
                    .First();
                return selected with
                {
                    PropertyAccess = selected.Kind == VbaSourceDefinitionKind.Property
                        ? group.Aggregate(
                            VbaPropertyAccess.Unknown,
                            (access, definition) => access | definition.PropertyAccess)
                        : VbaPropertyAccess.Unknown,
                    IsCreatable = group.Any(definition => definition.IsCreatable),
                    GlobalExposure = MergeGlobalExposure(group)
                };
            })
            .ToArray();
    }

    private static ReferenceDefinitionGlobalExposure MergeGlobalExposure(
        IEnumerable<VbaProjectReferenceDefinition> definitions)
    {
        var exposures = definitions
            .Select(definition => definition.GlobalExposure)
            .ToArray();
        if (exposures.Contains(ReferenceDefinitionGlobalExposure.LibraryGlobal))
        {
            return ReferenceDefinitionGlobalExposure.LibraryGlobal;
        }

        return exposures.Contains(ReferenceDefinitionGlobalExposure.MainHostGlobal)
            ? ReferenceDefinitionGlobalExposure.MainHostGlobal
            : ReferenceDefinitionGlobalExposure.None;
    }

    internal static string CreateQualifierAlias(string referenceName)
    {
        if (VbaIdentifier.IsIdentifier(referenceName))
        {
            return referenceName;
        }

        var alias = new StringBuilder(referenceName.Length);
        foreach (var rune in referenceName.EnumerateRunes())
        {
            var candidate = string.Concat(alias.ToString(), rune.ToString());
            if (VbaIdentifier.IsLexIdentifier(candidate))
            {
                alias.Append(rune.ToString());
            }
        }

        var value = alias.Length == 0 ? "Library" : alias.ToString();
        return VbaIdentifier.IsReservedIdentifier(value) ? $"Library_{value}" : value;
    }
}
