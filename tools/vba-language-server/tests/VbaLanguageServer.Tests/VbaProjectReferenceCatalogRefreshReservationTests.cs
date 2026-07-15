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
    public async Task CatalogRefreshKeepsCompletedCatalogAndReleasesCanceledRemainderForRetry()
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

        var retryResults = await service.RefreshAsync(CreateSelection("Library C", "library b"));

        Assert.Equal(["library b", "Library C"], retryResults.Select(result => result.ReferenceName));
        Assert.Equal(
            ["Library A", "Library B", "library b", "Library C"],
            discovery.ReferenceNames);
    }

    [Fact]
    public async Task CatalogRefreshReleasesCanceledCurrentAndUnstartedReferencesForRetry()
    {
        var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
        var discovery = new CancelFirstAttemptCatalogDiscovery();
        var service = CreateService(cache, discovery);
        var selection = CreateSelection("Library B", "Library A");
        using var cancellation = new CancellationTokenSource();

        var canceledRefresh = service.RefreshAsync(selection, cancellation.Token);
        await discovery.FirstAttemptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledRefresh);

        var retryResults = await service.RefreshAsync(selection);

        Assert.Equal(["Library A", "Library B"], retryResults.Select(result => result.ReferenceName));
        Assert.Equal(["Library A", "Library A", "Library B"], discovery.ReferenceNames);
    }

    [Fact]
    public async Task CatalogRefreshKeepsStaleCatalogWhenRefreshIsCanceledAndRetried()
    {
        var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
        cache.StoreStaleCatalog(CreateSuccess("Library A").Catalog!);
        var discovery = new CancelFirstAttemptCatalogDiscovery();
        var service = CreateService(cache, discovery);
        var selection = CreateSelection("Library A");
        using var cancellation = new CancellationTokenSource();

        var canceledRefresh = service.RefreshAsync(selection, cancellation.Token);
        await discovery.FirstAttemptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledRefresh);

        Assert.True(cache.Current.HasCatalog("Library A"));
        Assert.Equal(
            VbaProjectReferenceCatalogSource.StalePersisted,
            cache.GetCatalogSource("Library A"));

        var retryResult = Assert.Single(await service.RefreshAsync(selection));

        Assert.Equal(VbaProjectReferenceCatalogSource.StalePersisted, retryResult.Source);
        Assert.True(cache.Current.HasCatalog("Library A"));
        Assert.Equal(
            VbaProjectReferenceCatalogSource.StalePersisted,
            cache.GetCatalogSource("Library A"));
    }

    [Fact]
    public async Task CatalogRefreshReleasesRequestedNameWhenDiscoveryResultNameDiffers()
    {
        var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
        var discovery = new ResultNameMismatchCatalogDiscovery("Reported Library");
        var service = CreateService(cache, discovery);
        var selection = CreateSelection("Requested Library");

        var firstResults = await service.RefreshAsync(selection);
        var retryResults = await service.RefreshAsync(selection);

        Assert.Equal("Reported Library", Assert.Single(firstResults).DiscoveryResult.ReferenceName);
        Assert.Equal("Reported Library", Assert.Single(retryResults).DiscoveryResult.ReferenceName);
        Assert.Equal(["Requested Library", "Requested Library"], discovery.ReferenceNames);
    }

    [Fact]
    public async Task CatalogRefreshResultNameMismatchDoesNotReleaseOverlappingReservationOwner()
    {
        var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
        var discovery = new MismatchedOverlapCatalogDiscovery(
            blockingReferenceName: "Library B",
            mismatchedResultReferenceName: "Library B");
        var service = CreateService(cache, discovery);

        var ownerRefresh = service.RefreshAsync(CreateSelection("Library B"));
        await discovery.BlockingReferenceStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await service.RefreshAsync(CreateSelection("Library A"));
        var overlappingRefresh = service.RefreshAsync(CreateSelection("library b"));

        try
        {
            Assert.Empty(await overlappingRefresh.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, discovery.BlockingReferenceCallCount);
        }
        finally
        {
            discovery.ReleaseBlockingReference();
            await ownerRefresh.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task CatalogRefreshReleasesCandidatesAfterDiscoveryThrows()
    {
        var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
        var discovery = new ThrowingCatalogDiscovery();
        var service = CreateService(cache, discovery);
        var selection = CreateSelection("Library B", "Library A");

        var firstResults = await service.RefreshAsync(selection);
        var retryResults = await service.RefreshAsync(selection);

        Assert.All(firstResults, result => Assert.True(result.DiscoveryResult.IsFailure));
        Assert.All(retryResults, result => Assert.True(result.DiscoveryResult.IsFailure));
        Assert.Equal(
            ["Library A", "Library B", "Library A", "Library B"],
            discovery.ReferenceNames);
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
                "{33333333-3333-3333-3333-333333333333}",
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
        private bool hasBlocked;

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

            if (!hasBlocked
                && referenceName.Equals(blockingReferenceName, StringComparison.OrdinalIgnoreCase))
            {
                hasBlocked = true;
                BlockingReferenceStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return VbaProjectReferenceCatalogDiscoveryResult.Failure(referenceName, "Unexpected test call.");
        }
    }

    private sealed class CancelFirstAttemptCatalogDiscovery : IVbaProjectReferenceCatalogDiscovery
    {
        private readonly List<string> referenceNames = [];
        private bool isFirstAttempt = true;

        public TaskCompletionSource FirstAttemptStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<string> ReferenceNames => referenceNames;

        public async Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
        {
            referenceNames.Add(referenceName);
            if (isFirstAttempt)
            {
                isFirstAttempt = false;
                FirstAttemptStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return VbaProjectReferenceCatalogDiscoveryResult.Failure(referenceName, "Expected test result.");
        }
    }

    private sealed class ResultNameMismatchCatalogDiscovery : IVbaProjectReferenceCatalogDiscovery
    {
        private readonly string resultReferenceName;
        private readonly List<string> referenceNames = [];

        public ResultNameMismatchCatalogDiscovery(string resultReferenceName)
        {
            this.resultReferenceName = resultReferenceName;
        }

        public IReadOnlyList<string> ReferenceNames => referenceNames;

        public Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
        {
            referenceNames.Add(referenceName);
            return Task.FromResult(VbaProjectReferenceCatalogDiscoveryResult.Failure(
                resultReferenceName,
                "Expected mismatched test result."));
        }
    }

    private sealed class ThrowingCatalogDiscovery : IVbaProjectReferenceCatalogDiscovery
    {
        private readonly List<string> referenceNames = [];

        public IReadOnlyList<string> ReferenceNames => referenceNames;

        public Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
        {
            referenceNames.Add(referenceName);
            throw new InvalidOperationException("Expected discovery exception.");
        }
    }

    private sealed class MismatchedOverlapCatalogDiscovery : IVbaProjectReferenceCatalogDiscovery
    {
        private readonly string blockingReferenceName;
        private readonly string mismatchedResultReferenceName;
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int blockingReferenceCallCount;

        public MismatchedOverlapCatalogDiscovery(
            string blockingReferenceName,
            string mismatchedResultReferenceName)
        {
            this.blockingReferenceName = blockingReferenceName;
            this.mismatchedResultReferenceName = mismatchedResultReferenceName;
        }

        public TaskCompletionSource BlockingReferenceStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int BlockingReferenceCallCount => Volatile.Read(ref blockingReferenceCallCount);

        public async Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
        {
            if (referenceName.Equals(blockingReferenceName, StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref blockingReferenceCallCount);
                BlockingReferenceStarted.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return VbaProjectReferenceCatalogDiscoveryResult.Failure(referenceName, "Expected test result.");
            }

            return VbaProjectReferenceCatalogDiscoveryResult.Failure(
                mismatchedResultReferenceName,
                "Expected mismatched test result.");
        }

        public void ReleaseBlockingReference() => release.TrySetResult();
    }
}
