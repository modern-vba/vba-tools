
namespace VbaTools.Semantics;

/// <summary>
/// Coalesces complementary accessors that represent one logical property.
/// </summary>
internal static class VbaPropertyAccessorCoalescing
{
    private const char KeySeparator = '\u001f';

    public static IReadOnlyList<VbaSourceDefinition> Coalesce(
        IEnumerable<VbaSourceDefinition> definitions)
    {
        var definitionArray = definitions.ToArray();
        var result = definitionArray
            .Where(definition => definition.Kind != VbaSourceDefinitionKind.Property)
            .ToList();

        foreach (var group in definitionArray
            .Where(definition => definition.Kind == VbaSourceDefinitionKind.Property)
            .GroupBy(CreateOwnerKey, StringComparer.OrdinalIgnoreCase))
        {
            var accessors = group.ToArray();
            if (accessors.Length == 1 || !CanCoalesce(accessors))
            {
                result.AddRange(accessors);
                continue;
            }

            var representative = accessors
                .OrderByDescending(definition => definition.PropertyAccess.HasFlag(VbaPropertyAccess.Readable))
                .ThenByDescending(definition => definition.TypeReference is not null)
                .ThenByDescending(definition => definition.Signature is not null)
                .ThenByDescending(definition => definition.Documentation is not null)
                .First();
            result.Add(representative with
            {
                PropertyAccess = accessors.Aggregate(
                    VbaPropertyAccess.Unknown,
                    (access, definition) => access | definition.PropertyAccess),
                PropertyAccessorKind = null
            });
        }

        return result;
    }

    private static bool CanCoalesce(IReadOnlyList<VbaSourceDefinition> accessors)
    {
        if (accessors.Any(accessor => accessor.PropertyAccess == VbaPropertyAccess.Unknown))
        {
            return false;
        }

        var declaredAccessorKinds = accessors
            .Select(accessor => accessor.PropertyAccessorKind)
            .ToArray();
        if (declaredAccessorKinds.All(kind => kind is not null))
        {
            return declaredAccessorKinds
                .Select(kind => kind!.Value)
                .Distinct()
                .Count() == declaredAccessorKinds.Length;
        }

        if (declaredAccessorKinds.Any(kind => kind is not null))
        {
            return false;
        }

        var combined = VbaPropertyAccess.Unknown;
        foreach (var accessor in accessors)
        {
            if ((combined & accessor.PropertyAccess) != 0)
            {
                return false;
            }

            combined |= accessor.PropertyAccess;
        }

        return true;
    }

    private static string CreateOwnerKey(VbaSourceDefinition definition)
    {
        var owner = definition.Identity.Origin == VbaDefinitionOrigin.ProjectReference
            ? $"reference{KeySeparator}{definition.ModuleName}{KeySeparator}{definition.ParentTypeName ?? ""}"
            : $"source{KeySeparator}"
                + $"{VbaDocumentIdentityPolicy.GetDocumentStableKey(definition.Uri)}"
                + $"{KeySeparator}{definition.ModuleName}";
        return $"{owner}{KeySeparator}{definition.Name}";
    }
}
