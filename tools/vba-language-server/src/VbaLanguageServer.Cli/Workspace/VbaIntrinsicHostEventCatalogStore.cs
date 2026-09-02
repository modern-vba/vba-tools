using VbaLanguageServer.SourceModel;

namespace VbaLanguageServer.Workspace;

internal sealed record VbaIntrinsicHostEventCatalogState(
    long Revision,
    VbaIntrinsicHostEventCatalog? Catalog);

internal sealed record VbaIntrinsicHostEventCatalogUpdate(
    long Revision,
    VbaIntrinsicHostEventCatalog? Catalog);

internal sealed class VbaIntrinsicHostEventCatalogStore
{
    private readonly object gate = new();
    private VbaIntrinsicHostEventCatalogState state = new(0, null);

    public VbaIntrinsicHostEventCatalogState CaptureState()
    {
        lock (gate)
        {
            return state;
        }
    }

    public bool TryApply(VbaIntrinsicHostEventCatalogUpdate update)
    {
        if (update.Revision <= 0)
        {
            return false;
        }

        var captured = new VbaIntrinsicHostEventCatalogState(
            update.Revision,
            update.Catalog is null ? null : CaptureCatalog(update.Catalog));
        lock (gate)
        {
            if (state.Revision >= captured.Revision)
            {
                return false;
            }

            state = captured;
            return true;
        }
    }

    private static VbaIntrinsicHostEventCatalog CaptureCatalog(
        VbaIntrinsicHostEventCatalog catalog)
        => new(
            catalog.SourceKind,
            catalog.IntrinsicEventSourceName,
            FreezeList(catalog.Events.Select(hostEvent => new VbaIntrinsicHostEvent(
                hostEvent.Identity with { },
                new VbaIntrinsicHostEventSignature(
                    FreezeList(hostEvent.Parameters.Select(parameter => parameter with { })),
                    hostEvent.Documentation),
                hostEvent.AuthoringAvailable,
                hostEvent.ExistingHandlerRecognizable))),
            catalog.BaseTypeProvenance is null
                ? null
                : catalog.BaseTypeProvenance with { });

    private static IReadOnlyList<T> FreezeList<T>(IEnumerable<T> values)
        => Array.AsReadOnly(values.ToArray());
}
