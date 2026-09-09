using System.Collections.Immutable;

namespace VbaTools.Semantics;

/// <summary>Fixes supplied semantic evidence without discovering or acquiring it.</summary>
public sealed class VbaProjectSemanticInputs
{
    private VbaProjectSemanticInputs(
        VbaReferenceSelection? selection,
        VbaProjectReferenceCatalogSet catalogs,
        VbaIntrinsicHostEventCatalog? hostEvents,
        ImmutableDictionary<string, VbaProjectReferenceCatalogIdentity> identities,
        ImmutableDictionary<string, VbaProjectReferenceCatalogSource> sources,
        VbaProjectNamespaceIdentity? projectNamespaces)
    {
        ReferenceSelection = selection;
        ReferenceCatalogs = catalogs;
        IntrinsicHostEvents = hostEvents;
        ReferenceCatalogIdentities = identities;
        ReferenceCatalogSources = sources;
        ProjectNamespaces = projectNamespaces;
    }

    public VbaReferenceSelection? ReferenceSelection { get; }
    public VbaProjectReferenceCatalogSet ReferenceCatalogs { get; }
    public VbaIntrinsicHostEventCatalog? IntrinsicHostEvents { get; }
    public ImmutableDictionary<string, VbaProjectReferenceCatalogIdentity> ReferenceCatalogIdentities { get; }
    public ImmutableDictionary<string, VbaProjectReferenceCatalogSource> ReferenceCatalogSources { get; }
    public VbaProjectNamespaceIdentity? ProjectNamespaces { get; }

    public static VbaProjectSemanticInputs Empty { get; } = Capture(null, VbaProjectReferenceCatalogSet.Empty);

    public static VbaProjectSemanticInputs Capture(
        VbaReferenceSelection? selection,
        VbaProjectReferenceCatalogSet catalogs,
        VbaIntrinsicHostEventCatalog? hostEvents = null,
        IReadOnlyDictionary<string, VbaProjectReferenceCatalogIdentity>? identities = null,
        IReadOnlyDictionary<string, VbaProjectReferenceCatalogSource>? sources = null,
        VbaProjectNamespaceIdentity? projectNamespaces = null)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        return new(selection, catalogs, VbaSemanticFacts.CaptureHostEvents(hostEvents),
            (identities ?? ImmutableDictionary<string, VbaProjectReferenceCatalogIdentity>.Empty)
                .ToImmutableDictionary(VbaReferenceName.Comparer),
            (sources ?? ImmutableDictionary<string, VbaProjectReferenceCatalogSource>.Empty)
                .ToImmutableDictionary(VbaReferenceName.Comparer), projectNamespaces);
    }
}
