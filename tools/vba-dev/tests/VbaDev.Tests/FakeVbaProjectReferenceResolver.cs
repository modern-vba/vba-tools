using VbaDev.App.Workbooks;

namespace VbaDev.Tests;

internal sealed class FakeVbaProjectReferenceResolver : IVbaProjectReferenceResolver
{
    private readonly IReadOnlyList<ResolvedVbaProjectReference> references;

    public FakeVbaProjectReferenceResolver(params ResolvedVbaProjectReference[] references)
    {
        this.references = references;
    }

    public List<string> RequestedNames { get; } = [];

    public IReadOnlyList<ResolvedVbaProjectReference> Resolve(string referenceName)
    {
        RequestedNames.Add(referenceName);
        return references
            .Where(reference => reference.Name.Equals(referenceName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }
}
