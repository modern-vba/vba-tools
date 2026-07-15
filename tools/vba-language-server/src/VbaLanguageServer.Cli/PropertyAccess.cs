namespace VbaLanguageServer.SourceModel;

/// <summary>
/// Identifies the operations supported by a VBA property definition.
/// </summary>
[Flags]
public enum VbaPropertyAccess
{
    /// <summary>
    /// The property access metadata is unavailable.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The property can be read as a value.
    /// </summary>
    Readable = 1 << 0,

    /// <summary>
    /// The property can be assigned a value or object reference.
    /// </summary>
    Writable = 1 << 1
}

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
            : $"source{KeySeparator}{definition.Uri}{KeySeparator}{definition.ModuleName}";
        return $"{owner}{KeySeparator}{definition.Name}";
    }
}
