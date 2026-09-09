using System.Collections.Immutable;

namespace VbaTools.Semantics;

public sealed record VbaReferencedProjectNamespace(string ReferenceName, string Name);

/// <summary>Captures authoritative containing and ordered referenced project names.</summary>
public sealed class VbaProjectNamespaceIdentity
{
    private VbaProjectNamespaceIdentity(string? containingProjectName, ImmutableArray<VbaReferencedProjectNamespace> references)
    {
        ContainingProjectName = containingProjectName;
        References = references;
    }

    public string? ContainingProjectName { get; }
    public ImmutableArray<VbaReferencedProjectNamespace> References { get; }

    public static VbaProjectNamespaceIdentity Capture(string? containingProjectName,
        IEnumerable<VbaReferencedProjectNamespace> references)
    {
        ArgumentNullException.ThrowIfNull(references);
        return new(containingProjectName, references.ToImmutableArray());
    }
}
