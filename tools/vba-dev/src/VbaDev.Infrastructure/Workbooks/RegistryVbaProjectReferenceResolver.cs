using VbaDev.App.Workbooks;
using VbaTools.TypeLibRegistry;

namespace VbaDev.Infrastructure.Workbooks;

/// <summary>
/// Resolves VBA project references from one cached TypeLib registry catalog.
/// </summary>
public sealed class RegistryVbaProjectReferenceResolver : IVbaProjectReferenceResolver
{
    private readonly Lazy<TypeLibRegistryCatalog> catalog;

    /// <summary>
    /// Creates a resolver backed by the merged HKEY_CLASSES_ROOT TypeLib catalog.
    /// </summary>
    public RegistryVbaProjectReferenceResolver()
        : this(new RegistryTypeLibRegistryCatalogReader())
    {
    }

    internal RegistryVbaProjectReferenceResolver(ITypeLibRegistryCatalogReader catalogReader)
    {
        ArgumentNullException.ThrowIfNull(catalogReader);
        catalog = new Lazy<TypeLibRegistryCatalog>(
            catalogReader.Read,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public VbaProjectReferenceResolutionBatch ResolveAvailable()
    {
        var snapshot = catalog.Value;
        return new VbaProjectReferenceResolutionBatch(
            snapshot.Complete,
            snapshot.Warnings,
            snapshot.Diagnostic,
            snapshot.Names
                .Select(name => Resolve(snapshot, name.Name))
                .ToArray());
    }

    /// <summary>
    /// Resolves the requested names from one catalog snapshot.
    /// </summary>
    /// <param name="referenceNames">The ordered human-visible reference descriptions.</param>
    /// <returns>The complete batch result, including catalog warnings or failure.</returns>
    public VbaProjectReferenceResolutionBatch Resolve(IReadOnlyList<string> referenceNames)
    {
        ArgumentNullException.ThrowIfNull(referenceNames);

        var snapshot = catalog.Value;
        var references = referenceNames
            .Select(referenceName => Resolve(snapshot, referenceName))
            .ToArray();

        return new VbaProjectReferenceResolutionBatch(
            snapshot.Complete,
            snapshot.Warnings,
            snapshot.Diagnostic,
            references);
    }

    private static VbaProjectReferenceNameResolution Resolve(
        TypeLibRegistryCatalog catalog,
        string referenceName)
    {
        var requestedName = referenceName.Trim();
        var registeredName = catalog.Find(requestedName);
        if (registeredName is null)
        {
            return new VbaProjectReferenceNameResolution(
                requestedName,
                null,
                false,
                []);
        }

        var lineages = registeredName.Lineages
            .Select(lineage => new VbaProjectReferenceCandidateLineage(
                lineage.Guid,
                lineage.Versions
                .OrderByDescending(version => version.Major)
                .ThenByDescending(version => version.Minor)
                .Select(version => new ResolvedVbaProjectReference(
                    registeredName.Name,
                    lineage.Guid,
                    version.Major,
                    version.Minor))
                .ToArray()))
            .ToArray();
        var matches = lineages
            .Select(lineage => lineage.Versions[0])
            .ToArray();

        return new VbaProjectReferenceNameResolution(
            requestedName,
            registeredName.Name,
            true,
            matches,
            lineages);
    }
}
