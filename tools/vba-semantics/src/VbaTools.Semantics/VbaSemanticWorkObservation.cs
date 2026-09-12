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
    private readonly Action<long>? tokenIndexPreparationProgress;

    private VbaSemanticWorkObservation(Action<long>? tokenIndexPreparationProgress)
    {
        previous = current;
        this.tokenIndexPreparationProgress = tokenIndexPreparationProgress;
        current = this;
    }

    internal long DocumentIdentifications { get; private set; }
    internal long LogicalContextTokensExamined { get; private set; }
    internal long ArgumentRangeTokensExamined { get; private set; }
    internal long TokenIndexPreparationTokens { get; private set; }

    internal static VbaSemanticWorkObservation Begin(Action<long>? tokenIndexPreparationProgress = null)
        => new(tokenIndexPreparationProgress);

    internal static void RecordDocumentIdentification()
    {
        if (current is { } observation)
        {
            observation.DocumentIdentifications++;
        }
    }

    internal static void RecordLogicalContextToken()
    {
        if (current is { } observation) observation.LogicalContextTokensExamined++;
    }

    internal static void RecordArgumentRangeToken()
    {
        if (current is { } observation) observation.ArgumentRangeTokensExamined++;
    }

    internal static void RecordTokenIndexPreparationToken()
    {
        if (current is { } observation)
        {
            observation.TokenIndexPreparationTokens++;
            observation.tokenIndexPreparationProgress?.Invoke(observation.TokenIndexPreparationTokens);
        }
    }

    public void Dispose() => current = previous;
}
