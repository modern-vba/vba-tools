namespace VbaLanguageServer.SourceModel;

/// <summary>
/// Schedules low-impact reference catalog discovery work.
/// </summary>
public interface IVbaProjectReferenceCatalogRefreshWorker
{
    /// <summary>
    /// Runs discovery for one reference through the worker.
    /// </summary>
    /// <param name="discovery">The discovery service to invoke.</param>
    /// <param name="referenceName">The reference name to discover.</param>
    /// <param name="cancellationToken">A cancellation token for the refresh work.</param>
    /// <returns>The discovery result.</returns>
    Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
        IVbaProjectReferenceCatalogDiscovery discovery,
        string referenceName,
        CancellationToken cancellationToken);
}

/// <summary>
/// Runs TypeLib reference catalog discovery as bounded, low-priority background work.
/// </summary>
public sealed class LowImpactReferenceCatalogRefreshWorker : IVbaProjectReferenceCatalogRefreshWorker
{
    private readonly SemaphoreSlim concurrencyGate;

    /// <summary>
    /// Creates a low-impact refresh worker.
    /// </summary>
    /// <param name="maxConcurrency">The maximum number of concurrent refresh operations.</param>
    public LowImpactReferenceCatalogRefreshWorker(int maxConcurrency = 1)
    {
        if (maxConcurrency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency), "Concurrency must be positive.");
        }

        concurrencyGate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    /// <summary>
    /// Gets a shared single-concurrency low-impact refresh worker.
    /// </summary>
    public static LowImpactReferenceCatalogRefreshWorker Shared { get; } = new();

    /// <summary>
    /// Runs discovery for one reference through bounded low-priority work.
    /// </summary>
    /// <param name="discovery">The discovery service to invoke.</param>
    /// <param name="referenceName">The reference name to discover.</param>
    /// <param name="cancellationToken">A cancellation token for the refresh work.</param>
    /// <returns>The discovery result.</returns>
    public async Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
        IVbaProjectReferenceCatalogDiscovery discovery,
        string referenceName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        await concurrencyGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            return await Task.Factory.StartNew(
                () => RunWithLowerThreadPriority(discovery, referenceName, cancellationToken),
                cancellationToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();
        }
        finally
        {
            concurrencyGate.Release();
        }
    }

    private static async Task<VbaProjectReferenceCatalogDiscoveryResult> RunWithLowerThreadPriority(
        IVbaProjectReferenceCatalogDiscovery discovery,
        string referenceName,
        CancellationToken cancellationToken)
    {
        var thread = Thread.CurrentThread;
        var originalPriority = thread.Priority;
        try
        {
            TrySetPriority(thread, ThreadPriority.BelowNormal);
            cancellationToken.ThrowIfCancellationRequested();
            var result = await discovery.DiscoverAsync(referenceName, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            return result;
        }
        finally
        {
            TrySetPriority(thread, originalPriority);
        }
    }

    private static void TrySetPriority(Thread thread, ThreadPriority priority)
    {
        try
        {
            thread.Priority = priority;
        }
        catch (ThreadStateException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }
    }
}
