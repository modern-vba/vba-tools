namespace VbaLanguageServer.SourceModel;

internal enum VbaDevReferenceCatalogPinResult
{
    Pinned,
    AlreadyPinned,
    Rejected
}

/// <summary>
/// Starts with registry discovery and pins the first validated companion for the session.
/// </summary>
internal sealed class SessionPinnedVbaDevReferenceCatalogDiscovery
    : IVbaProjectReferenceCatalogDiscovery,
      IVbaProjectReferenceCatalogDiscoveryBatchFactory,
      IVbaProjectReferenceCatalogContextDiscoveryFactory,
      IVbaProjectReferenceCatalogCancellationCleanup
{
    private readonly IVbaProjectReferenceCatalogDiscovery registryDiscovery;
    private readonly Func<string, IVbaProjectReferenceCatalogDiscovery>
        companionFactory;
    private readonly object gate = new();
    private string? executablePath;
    private IVbaProjectReferenceCatalogDiscovery? companionDiscovery;

    public SessionPinnedVbaDevReferenceCatalogDiscovery(
        IVbaProjectReferenceCatalogDiscovery registryDiscovery)
        : this(
            registryDiscovery,
            executablePath => new VbaDevReferenceListCatalogDiscoveryFactory(
                registryDiscovery,
                executablePath))
    {
    }

    internal SessionPinnedVbaDevReferenceCatalogDiscovery(
        IVbaProjectReferenceCatalogDiscovery registryDiscovery,
        Func<string, IVbaProjectReferenceCatalogDiscovery> companionFactory)
    {
        ArgumentNullException.ThrowIfNull(registryDiscovery);
        ArgumentNullException.ThrowIfNull(companionFactory);
        this.registryDiscovery = registryDiscovery;
        this.companionFactory = companionFactory;
    }

    bool IVbaProjectReferenceCatalogContextDiscoveryFactory
        .UsesContextSpecificResolution
    {
        get
        {
            lock (gate)
            {
                return companionDiscovery is not null;
            }
        }
    }

    internal bool IsCompanionPinned
    {
        get
        {
            lock (gate)
            {
                return companionDiscovery is not null;
            }
        }
    }

    TimeSpan IVbaProjectReferenceCatalogCancellationCleanup.CancellationCleanupTimeout
    {
        get
        {
            lock (gate)
            {
                return companionDiscovery is
                    IVbaProjectReferenceCatalogCancellationCleanup cancellationCleanup
                        ? cancellationCleanup.CancellationCleanupTimeout
                        : TimeSpan.Zero;
            }
        }
    }

    public Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
        string referenceName,
        CancellationToken cancellationToken = default)
        => registryDiscovery.DiscoverAsync(referenceName, cancellationToken);

    public VbaDevReferenceCatalogPinResult TryPin(string candidateExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(candidateExecutablePath)
            || !Path.IsPathFullyQualified(candidateExecutablePath))
        {
            return VbaDevReferenceCatalogPinResult.Rejected;
        }

        lock (gate)
        {
            if (executablePath is not null)
            {
                return PathEquals(executablePath, candidateExecutablePath)
                    ? VbaDevReferenceCatalogPinResult.AlreadyPinned
                    : VbaDevReferenceCatalogPinResult.Rejected;
            }

            IVbaProjectReferenceCatalogDiscovery candidate;
            try
            {
                candidate = companionFactory(candidateExecutablePath);
            }
            catch (Exception exception) when (exception is ArgumentException
                or InvalidOperationException)
            {
                return VbaDevReferenceCatalogPinResult.Rejected;
            }

            if (candidate is not
                IVbaProjectReferenceCatalogContextDiscoveryFactory)
            {
                return VbaDevReferenceCatalogPinResult.Rejected;
            }

            companionDiscovery = candidate;
            executablePath = candidateExecutablePath;
            return VbaDevReferenceCatalogPinResult.Pinned;
        }
    }

    IVbaProjectReferenceCatalogDiscovery
        IVbaProjectReferenceCatalogDiscoveryBatchFactory.CreateBatchDiscovery()
        => CreateRegistryBatchDiscovery();

    IVbaProjectReferenceCatalogDiscovery
        IVbaProjectReferenceCatalogContextDiscoveryFactory.CreateContextDiscovery(
            VbaProjectReferenceCatalogRefreshContext context)
    {
        IVbaProjectReferenceCatalogDiscovery? pinnedDiscovery;
        lock (gate)
        {
            pinnedDiscovery = companionDiscovery;
        }

        return pinnedDiscovery is
            IVbaProjectReferenceCatalogContextDiscoveryFactory contextFactory
                ? contextFactory.CreateContextDiscovery(context)
                : CreateRegistryBatchDiscovery();
    }

    private IVbaProjectReferenceCatalogDiscovery CreateRegistryBatchDiscovery()
        => registryDiscovery is
            IVbaProjectReferenceCatalogDiscoveryBatchFactory batchFactory
                ? batchFactory.CreateBatchDiscovery()
                : registryDiscovery;

    private static bool PathEquals(string left, string right)
        => string.Equals(
            left,
            right,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
}
