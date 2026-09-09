using System.Collections.Immutable;

namespace VbaTools.Semantics;

public sealed record VbaSelectedReference(string Name);

/// <summary>Captures ordered accepted reference names without manifest ownership or discovery.</summary>
public sealed class VbaReferenceSelection
{
    private VbaReferenceSelection(ImmutableArray<VbaSelectedReference> references, VbaSelectedReference? mainReference)
    {
        References = references;
        MainVbaProjectReference = mainReference;
    }

    public ImmutableArray<VbaSelectedReference> References { get; }
    public VbaSelectedReference? MainVbaProjectReference { get; }

    public static VbaReferenceSelection Capture(IEnumerable<string> names, string? mainReferenceName)
        => new(names.Select(name => new VbaSelectedReference(name)).ToImmutableArray(),
            mainReferenceName is null ? null : new VbaSelectedReference(mainReferenceName));
}
