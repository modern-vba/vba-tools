using VbaDev.App.Workbooks;
using VbaTools.TypeLibRegistry;

namespace VbaDev.Tests;

internal sealed class FakeVbaProjectReferenceResolver : IVbaProjectReferenceResolver
{
    private readonly IReadOnlyList<ResolvedVbaProjectReference> references;

    public FakeVbaProjectReferenceResolver(params ResolvedVbaProjectReference[] references)
    {
        this.references = references;
    }

    public List<string> RequestedNames { get; } = [];

    public bool ThrowOnResolve { get; init; }

    public bool Complete { get; init; } = true;

    public IReadOnlyList<string> RegisteredNamesWithoutUsableIdentity { get; init; } = [];

    public IReadOnlyList<string> OmittedRequestedNames { get; init; } = [];

    public bool ReverseResolutionOrder { get; init; }

    public IReadOnlyList<TypeLibRegistryCatalogWarning> Warnings { get; init; } = [];

    public TypeLibRegistryCatalogDiagnostic? Diagnostic { get; init; }

    public VbaProjectReferenceResolutionBatch Resolve(IReadOnlyList<string> referenceNames)
    {
        if (ThrowOnResolve)
        {
            throw new InvalidOperationException("Reference resolution was not expected.");
        }

        RequestedNames.AddRange(referenceNames);
        var resolutions = referenceNames
            .Where(referenceName => !OmittedRequestedNames.Contains(
                referenceName,
                StringComparer.OrdinalIgnoreCase))
            .Select(referenceName =>
            {
                var matches = references
                    .Where(reference => reference.Name.Equals(referenceName, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var registeredWithoutIdentity = RegisteredNamesWithoutUsableIdentity
                    .Where(name => name.Equals(referenceName, StringComparison.OrdinalIgnoreCase))
                    .Order(StringComparer.Ordinal)
                    .FirstOrDefault();
                var registeredName = matches
                    .Select(reference => reference.Name)
                    .Append(registeredWithoutIdentity)
                    .Where(name => name is not null)
                    .Cast<string>()
                    .Order(StringComparer.Ordinal)
                    .FirstOrDefault();
                return new VbaProjectReferenceNameResolution(
                    referenceName,
                    registeredName,
                    registeredName is not null,
                    matches);
            })
            .ToArray();
        if (ReverseResolutionOrder)
        {
            Array.Reverse(resolutions);
        }

        return new VbaProjectReferenceResolutionBatch(
            Complete,
            Warnings,
            Diagnostic,
            resolutions);
    }
}
