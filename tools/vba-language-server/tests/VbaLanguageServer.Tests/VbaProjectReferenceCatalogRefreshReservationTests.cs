using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaProjectReferenceCatalogRefreshReservationTests
{
    [Fact]
    public async Task CatalogRefreshAtomicallyReservesSelectionAndAllowsNonOverlappingConcurrentWork()
    {
        var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
        var discovery = new SelectiveBlockingCatalogDiscovery("Library A");
        var service = CreateService(cache, discovery);

        var firstRefresh = service.RefreshAsync(CreateSelection("Library B", "Library A"));
        await discovery.BlockedReferenceStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var overlappingRefresh = service.RefreshAsync(CreateSelection("library b", "Library C"));
        var overlappingResults = await overlappingRefresh.WaitAsync(TimeSpan.FromSeconds(5));

        var overlappingResult = Assert.Single(overlappingResults);
        Assert.Equal("Library C", overlappingResult.ReferenceName);
        Assert.Equal(["Library A", "Library C"], discovery.ReferenceNames);

        discovery.ReleaseBlockedReference();
        var firstResults = await firstRefresh.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["Library A", "Library B"], firstResults.Select(result => result.ReferenceName));
        Assert.Equal(["Library A", "Library C", "Library B"], discovery.ReferenceNames);
    }

    [Fact]
    public async Task CatalogRefreshKeepsCompletedCatalogWhenLaterReferenceIsCanceled()
    {
        var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
        var discovery = new SuccessfulThenBlockingCatalogDiscovery(
            successfulReferenceName: "Library A",
            blockingReferenceName: "Library B");
        var service = CreateService(cache, discovery);
        using var cancellation = new CancellationTokenSource();

        var refresh = service.RefreshAsync(
            CreateSelection("Library C", "Library B", "Library A"),
            cancellation.Token);
        await discovery.BlockingReferenceStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);

        Assert.True(cache.HasIdentity("Library A"));
        Assert.True(cache.Current.HasCatalog("Library A"));
        Assert.Equal(
            VbaProjectReferenceCatalogSource.Generated,
            cache.GetCatalogSource("Library A"));
        Assert.Equal(["Library A", "Library B"], discovery.ReferenceNames);
        Assert.Empty(await service.RefreshAsync(CreateSelection("library a")));
    }

    private static VbaProjectReferenceCatalogRefreshService CreateService(
        VbaProjectReferenceCatalogCache cache,
        IVbaProjectReferenceCatalogDiscovery discovery)
        => new(
            cache,
            discovery,
            persistentStore: null,
            refreshWorker: new InlineRefreshWorker());

    private static VbaProjectReferenceSelection CreateSelection(params string[] referenceNames)
        => VbaProjectReferenceSelection.Create(
            ProjectDocument.ExcelKind,
            referenceNames
                .Select(referenceName => new VbaProjectReference(referenceName))
                .ToArray());

    private static VbaProjectReferenceCatalogDiscoveryResult CreateSuccess(string referenceName)
        => VbaProjectReferenceCatalogDiscoveryResult.Success(
            new VbaProjectReferenceCatalogIdentity(
                referenceName,
                $"{{{referenceName.GetHashCode():X8}-3333-3333-3333-333333333333}}",
                1,
                0,
                0,
                $@"C:\TypeLibs\{referenceName}.tlb"),
            new VbaProjectReferenceCatalog(
                referenceName,
                [],
                [
                    new VbaProjectReferenceDefinition(
                        referenceName,
                        $"{referenceName.Replace(" ", string.Empty, StringComparison.Ordinal)}Type",
                        VbaSourceDefinitionKind.Class)
                ]));

    private sealed class InlineRefreshWorker : IVbaProjectReferenceCatalogRefreshWorker
    {
        public Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            IVbaProjectReferenceCatalogDiscovery discovery,
            string referenceName,
            CancellationToken cancellationToken)
            => discovery.DiscoverAsync(referenceName, cancellationToken);
    }

    private sealed class SelectiveBlockingCatalogDiscovery : IVbaProjectReferenceCatalogDiscovery
    {
        private readonly string blockedReferenceName;
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> referenceNames = [];
        private readonly object gate = new();

        public SelectiveBlockingCatalogDiscovery(string blockedReferenceName)
        {
            this.blockedReferenceName = blockedReferenceName;
        }

        public TaskCompletionSource BlockedReferenceStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<string> ReferenceNames
        {
            get
            {
                lock (gate)
                {
                    return referenceNames.ToArray();
                }
            }
        }

        public async Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                referenceNames.Add(referenceName);
            }

            if (referenceName.Equals(blockedReferenceName, StringComparison.OrdinalIgnoreCase))
            {
                BlockedReferenceStarted.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }

            return VbaProjectReferenceCatalogDiscoveryResult.Failure(referenceName, "Expected test result.");
        }

        public void ReleaseBlockedReference() => release.TrySetResult();
    }

    private sealed class SuccessfulThenBlockingCatalogDiscovery : IVbaProjectReferenceCatalogDiscovery
    {
        private readonly string successfulReferenceName;
        private readonly string blockingReferenceName;
        private readonly List<string> referenceNames = [];

        public SuccessfulThenBlockingCatalogDiscovery(
            string successfulReferenceName,
            string blockingReferenceName)
        {
            this.successfulReferenceName = successfulReferenceName;
            this.blockingReferenceName = blockingReferenceName;
        }

        public TaskCompletionSource BlockingReferenceStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<string> ReferenceNames => referenceNames;

        public async Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
        {
            referenceNames.Add(referenceName);
            if (referenceName.Equals(successfulReferenceName, StringComparison.OrdinalIgnoreCase))
            {
                return CreateSuccess(referenceName);
            }

            if (referenceName.Equals(blockingReferenceName, StringComparison.OrdinalIgnoreCase))
            {
                BlockingReferenceStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return VbaProjectReferenceCatalogDiscoveryResult.Failure(referenceName, "Unexpected test call.");
        }
    }
}
