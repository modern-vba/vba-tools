using VbaDev.App.Workbooks;

namespace VbaDev.Tests;

internal sealed class FakeVbaProjectReferenceResolver : IVbaProjectReferenceResolver
{
    private readonly IReadOnlyList<ResolvedVbaProjectReference> references;

    public FakeVbaProjectReferenceResolver(params ResolvedVbaProjectReference[] references)
    {
        this.references = references;
    }

    public IReadOnlyList<ResolvedVbaProjectReference> Resolve(string referenceName)
        => references
            .Where(reference => reference.Name.Equals(referenceName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
}
