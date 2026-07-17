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
    public async Task CatalogRefreshRejectsSuccessfulResultWhoseCatalogIdentityDoesNotMatchReservation()
    {
        var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
        var discovery = new SuccessfulResultNameMismatchCatalogDiscovery("Reported Library");
        var service = CreateService(cache, discovery);
        var selection = CreateSelection("Requested Library");

        var firstResult = Assert.Single(await service.RefreshAsync(selection));
        await service.RefreshAsync(selection);

        Assert.True(firstResult.DiscoveryResult.IsFailure);
        Assert.False(cache.Current.HasCatalog("Reported Library"));
        Assert.False(cache.Current.HasCatalog("Requested Library"));
        Assert.Equal(["Requested Library", "Requested Library"], discovery.ReferenceNames);
    }

    [Fact]
    public async Task CatalogRefreshRejectsIdentityOnlyResultWhoseNameDoesNotMatchReservation()
    {
        var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
        var discovery = new IdentityOnlyResultNameMismatchCatalogDiscovery("Reported Library");
        var service = CreateService(cache, discovery);
        var selection = CreateSelection("Requested Library");

        var firstResult = Assert.Single(await service.RefreshAsync(selection));
        await service.RefreshAsync(selection);

        Assert.True(firstResult.DiscoveryResult.IsFailure);
        Assert.False(cache.HasIdentity("Reported Library"));
        Assert.False(cache.HasIdentity("Requested Library"));
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

    [Fact]
    public async Task SlowStalePreloadDoesNotReplaceNewerGeneratedCatalog()
    {
        var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
        var staleResult = CreateSuccess("Library A");
        var persistentStore = new DelayedFirstStalePersistentStore(
            new VbaProjectReferenceCatalogPersistentEntry(
                Assert.Single(staleResult.Identities),
                staleResult.Catalog!));
        var discovery = new SuccessfulCatalogDiscovery();
        var service = new VbaProjectReferenceCatalogRefreshService(
            cache,
            discovery,
            persistentStore,
            new InlineRefreshWorker());
        var selection = CreateSelection("Library A");

        var slowRefresh = service.RefreshAsync(selection);
        await persistentStore.FirstLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var competingRefresh = service.RefreshAsync(selection);
        await competingRefresh.WaitAsync(TimeSpan.FromSeconds(5));

        persistentStore.ReleaseFirstLoad();
        await slowRefresh.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            VbaProjectReferenceCatalogSource.Generated,
            cache.GetCatalogSource("Library A"));
        Assert.Equal(1, discovery.CallCount);
    }

    [Fact]
    public async Task AutomaticRefreshWaitsForExplicitOwnerBeforeRetryingOverlappingReference()
    {
        var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
        var discovery = new SelectiveBlockingCatalogDiscovery("Library A");
        var service = CreateService(cache, discovery);

        var explicitRefresh = service.RefreshAsync(CreateSelection("Library A"));
        await discovery.BlockedReferenceStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var automaticRefresh = service.RefreshAutomaticallyAsync(
            CreateSelection("Library A", "Library B"),
            CancellationToken.None);

        var completedBeforeOwner = await Task.WhenAny(
            automaticRefresh,
            Task.Delay(TimeSpan.FromMilliseconds(250)));
        Assert.NotSame(automaticRefresh, completedBeforeOwner);

        discovery.ReleaseBlockedReference();
        await explicitRefresh.WaitAsync(TimeSpan.FromSeconds(5));
        var automaticResults = await automaticRefresh.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            ["Library A", "Library B"],
            automaticResults.Select(result => result.ReferenceName));
        Assert.Equal(
            ["Library A", "Library A", "Library B"],
            discovery.ReferenceNames);
    }

    [Fact]
    public async Task CancellationAfterPersistedLoadDoesNotCommitTheIgnoredResult()
    {
        var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
        var currentResult = CreateSuccess("Library A");
        var persistentStore = new IgnoringCancellationPersistentStore(
            VbaProjectReferenceCatalogPersistentLoadResult.Current(
                new VbaProjectReferenceCatalogPersistentEntry(
                    Assert.Single(currentResult.Identities),
                    currentResult.Catalog!)));
        var service = new VbaProjectReferenceCatalogRefreshService(
            cache,
            new SuccessfulCatalogDiscovery(),
            persistentStore,
            new InlineRefreshWorker());
        using var cancellation = new CancellationTokenSource();

        var refresh = service.RefreshAsync(
            CreateSelection("Library A"),
            cancellation.Token);
        await persistentStore.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        persistentStore.ReleaseLoad();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        Assert.False(cache.HasIdentity("Library A"));
        Assert.False(cache.Current.HasCatalog("Library A"));
    }

    [Fact]
    public async Task CancellationAfterDiscoveryDoesNotCommitTheIgnoredResult()
    {
        var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
        var discovery = new IgnoringCancellationSuccessfulDiscovery();
        var service = CreateService(cache, discovery);
        using var cancellation = new CancellationTokenSource();

        var refresh = service.RefreshAsync(
            CreateSelection("Library A"),
            cancellation.Token);
        await discovery.DiscoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        discovery.ReleaseDiscovery();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        Assert.False(cache.HasIdentity("Library A"));
        Assert.False(cache.Current.HasCatalog("Library A"));
    }

    [Theory]
    [InlineData(MalformedDiscoveryResultKind.FailureWithCatalog)]
    [InlineData(MalformedDiscoveryResultKind.AmbiguousWithCatalog)]
    [InlineData(MalformedDiscoveryResultKind.CatalogWithoutIdentity)]
    public async Task MalformedDiscoveryResultPreservesLastKnownGoodAndRemainsRetryable(
        MalformedDiscoveryResultKind resultKind)
    {
        var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
        cache.StoreStaleCatalog(CreateSuccess("Library A").Catalog!);
        var discovery = new MalformedCatalogDiscovery(resultKind);
        var service = CreateService(cache, discovery);
        var selection = CreateSelection("Library A");

        var firstResult = Assert.Single(await service.RefreshAsync(selection));
        Assert.Single(await service.RefreshAsync(selection));

        Assert.False(firstResult.DiscoveryResult.HasUsableCatalog);
        Assert.False(cache.HasIdentity("Library A"));
        Assert.Equal(
            VbaProjectReferenceCatalogSource.StalePersisted,
            cache.GetCatalogSource("Library A"));
        Assert.Equal(2, discovery.CallCount);
    }

    [Fact]
    public void CacheStoreRejectsInternallyInconsistentSuccessfulResult()
    {
        var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
        var success = CreateSuccess("Library A");
        var inconsistent = success with
        {
            Catalog = CreateSuccess("Library B").Catalog
        };

        cache.Store(inconsistent);

        Assert.False(inconsistent.IsSuccessful);
        Assert.False(cache.HasIdentity("Library A"));
        Assert.False(cache.Current.HasCatalog("Library A"));
        Assert.False(cache.Current.HasCatalog("Library B"));
        Assert.Equal(
            VbaProjectReferenceCatalogSource.Unavailable,
            cache.GetCatalogSource("Library A"));
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

    private sealed class DelayedFirstStalePersistentStore
        : IVbaProjectReferenceCatalogPersistentStore
    {
        private readonly VbaProjectReferenceCatalogPersistentEntry entry;
        private readonly TaskCompletionSource releaseFirstLoad =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int loadCount;

        public DelayedFirstStalePersistentStore(
            VbaProjectReferenceCatalogPersistentEntry entry)
        {
            this.entry = entry;
        }

        public TaskCompletionSource FirstLoadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<VbaProjectReferenceCatalogPersistentLoadResult> LoadAsync(
            string referenceName,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref loadCount) == 1)
            {
                FirstLoadStarted.TrySetResult();
                await releaseFirstLoad.Task.WaitAsync(cancellationToken);
                return VbaProjectReferenceCatalogPersistentLoadResult.Stale(
                    entry,
                    "Expected delayed stale entry.");
            }

            return VbaProjectReferenceCatalogPersistentLoadResult.Miss();
        }

        public Task SaveAsync(
            VbaProjectReferenceCatalogPersistentEntry entry,
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        public void ReleaseFirstLoad()
            => releaseFirstLoad.TrySetResult();
    }

    private sealed class IgnoringCancellationPersistentStore
        : IVbaProjectReferenceCatalogPersistentStore
    {
        private readonly VbaProjectReferenceCatalogPersistentLoadResult loadResult;
        private readonly TaskCompletionSource releaseLoad =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IgnoringCancellationPersistentStore(
            VbaProjectReferenceCatalogPersistentLoadResult loadResult)
        {
            this.loadResult = loadResult;
        }

        public TaskCompletionSource LoadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<VbaProjectReferenceCatalogPersistentLoadResult> LoadAsync(
            string referenceName,
            CancellationToken cancellationToken)
        {
            LoadStarted.TrySetResult();
            await releaseLoad.Task;
            return loadResult;
        }

        public Task SaveAsync(
            VbaProjectReferenceCatalogPersistentEntry entry,
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        public void ReleaseLoad()
            => releaseLoad.TrySetResult();
    }

    private sealed class SuccessfulCatalogDiscovery : IVbaProjectReferenceCatalogDiscovery
    {
        private int callCount;

        public int CallCount => Volatile.Read(ref callCount);

        public Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult(CreateSuccess(referenceName));
        }
    }

    private sealed class IgnoringCancellationSuccessfulDiscovery
        : IVbaProjectReferenceCatalogDiscovery
    {
        private readonly TaskCompletionSource releaseDiscovery =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource DiscoveryStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
        {
            DiscoveryStarted.TrySetResult();
            await releaseDiscovery.Task;
            return CreateSuccess(referenceName);
        }

        public void ReleaseDiscovery()
            => releaseDiscovery.TrySetResult();
    }

    public enum MalformedDiscoveryResultKind
    {
        FailureWithCatalog,
        AmbiguousWithCatalog,
        CatalogWithoutIdentity
    }

    private sealed class MalformedCatalogDiscovery
        : IVbaProjectReferenceCatalogDiscovery
    {
        private readonly MalformedDiscoveryResultKind resultKind;
        private int callCount;

        public MalformedCatalogDiscovery(MalformedDiscoveryResultKind resultKind)
        {
            this.resultKind = resultKind;
        }

        public int CallCount => Volatile.Read(ref callCount);

        public Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref callCount);
            var success = CreateSuccess(referenceName);
            var identity = Assert.Single(success.Identities);
            var catalog = success.Catalog!;
            var result = resultKind switch
            {
                MalformedDiscoveryResultKind.FailureWithCatalog =>
                    new VbaProjectReferenceCatalogDiscoveryResult(
                        referenceName,
                        [identity],
                        catalog,
                        "Expected malformed failure."),
                MalformedDiscoveryResultKind.AmbiguousWithCatalog =>
                    new VbaProjectReferenceCatalogDiscoveryResult(
                        referenceName,
                        [
                            identity,
                            identity with
                            {
                                Guid = "{44444444-4444-4444-4444-444444444444}"
                            }
                        ],
                        catalog),
                _ => new VbaProjectReferenceCatalogDiscoveryResult(
                    referenceName,
                    [],
                    catalog)
            };
            return Task.FromResult(result);
        }
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

    private sealed class SuccessfulResultNameMismatchCatalogDiscovery
        : IVbaProjectReferenceCatalogDiscovery
    {
        private readonly string resultReferenceName;
        private readonly List<string> referenceNames = [];

        public SuccessfulResultNameMismatchCatalogDiscovery(string resultReferenceName)
        {
            this.resultReferenceName = resultReferenceName;
        }

        public IReadOnlyList<string> ReferenceNames => referenceNames;

        public Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
        {
            referenceNames.Add(referenceName);
            return Task.FromResult(CreateSuccess(resultReferenceName));
        }
    }

    private sealed class IdentityOnlyResultNameMismatchCatalogDiscovery
        : IVbaProjectReferenceCatalogDiscovery
    {
        private readonly string resultReferenceName;
        private readonly List<string> referenceNames = [];

        public IdentityOnlyResultNameMismatchCatalogDiscovery(string resultReferenceName)
        {
            this.resultReferenceName = resultReferenceName;
        }

        public IReadOnlyList<string> ReferenceNames => referenceNames;

        public Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
        {
            referenceNames.Add(referenceName);
            return Task.FromResult(
                VbaProjectReferenceCatalogDiscoveryResult.Success(
                    new VbaProjectReferenceCatalogIdentity(
                        resultReferenceName,
                        "{33333333-3333-3333-3333-333333333333}",
                        1,
                        0,
                        0,
                        $@"C:\TypeLibs\{resultReferenceName}.tlb")));
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
