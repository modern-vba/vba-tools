namespace VbaTools.Semantics;

/// <summary>
/// Scoped, thread-local work accounting for synchronous semantic regression probes.
/// Observes the real operation without replacing resolution or retaining an inventory.
/// </summary>
internal sealed class VbaSemanticWorkObservation : IDisposable
{
    [ThreadStatic]
    private static VbaSemanticWorkObservation? current;

    private readonly VbaSemanticWorkObservation? previous;

    private VbaSemanticWorkObservation()
    {
        previous = current;
        current = this;
    }

    internal long DocumentIdentifications { get; private set; }

    internal static VbaSemanticWorkObservation Begin() => new();

    internal static void RecordDocumentIdentification()
    {
        if (current is { } observation)
        {
            observation.DocumentIdentifications++;
        }
    }

    public void Dispose() => current = previous;
}
