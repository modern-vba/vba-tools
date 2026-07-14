using System.Threading;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaProjectReferenceCatalogRefreshWorkerTests
{
    [Fact]
    public async Task CatalogRefreshSchedulesDiscoveryThroughRefreshWorker()
    {
        var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
        var discovery = new CountingCatalogDiscovery(
            VbaProjectReferenceCatalogDiscoveryResult.Success(
                CreateIdentity("Generated Library"),
                CreateCatalog("Generated Library", "GeneratedType")));
        var worker = new RecordingRefreshWorker();
        var service = new VbaProjectReferenceCatalogRefreshService(
            cache,
            discovery,
            persistentStore: null,
            refreshWorker: worker);
        var selection = VbaProjectReferenceSelection.Create(
            ProjectDocument.ExcelKind,
            [new VbaProjectReference("Generated Library")]);

        var results = await service.RefreshAsync(selection);

        Assert.Single(results);
        Assert.Equal(1, worker.CallCount);
        Assert.Equal(1, discovery.CallCount);
        Assert.Equal("Generated Library", Assert.Single(worker.ReferenceNames));
        Assert.True(cache.Current.HasCatalog("Generated Library"));
    }

    [Fact]
    public async Task LowImpactRefreshWorkerBoundsConcurrencyAndKeepsCallerThreadPriority()
    {
        var originalPriority = Thread.CurrentThread.Priority;
        var worker = new LowImpactReferenceCatalogRefreshWorker(maxConcurrency: 1);
        var discovery = new BlockingCatalogDiscovery(
            VbaProjectReferenceCatalogDiscoveryResult.Success(
                CreateIdentity("Generated Library"),
                CreateCatalog("Generated Library", "GeneratedType")));

        var first = worker.DiscoverAsync(discovery, "Generated Library", CancellationToken.None);
        await discovery.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = worker.DiscoverAsync(discovery, "Generated Library", CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        Assert.Equal(1, discovery.CallCount);
        Assert.Equal(1, discovery.MaxActiveCallCount);
        Assert.Equal(originalPriority, Thread.CurrentThread.Priority);

        discovery.ReleaseAll();
        await first;
        await second;

        Assert.Equal(2, discovery.CallCount);
        Assert.Equal(1, discovery.MaxActiveCallCount);
        Assert.All(discovery.ObservedPriorities, priority =>
            Assert.True(priority <= ThreadPriority.BelowNormal));
        Assert.Equal(originalPriority, Thread.CurrentThread.Priority);
    }

    [Fact]
    public async Task LowImpactRefreshWorkerHonorsCancellationBeforeDiscoveryStarts()
    {
        var worker = new LowImpactReferenceCatalogRefreshWorker(maxConcurrency: 1);
        var discovery = new CountingCatalogDiscovery(
            VbaProjectReferenceCatalogDiscoveryResult.Success(
                CreateIdentity("Generated Library"),
                CreateCatalog("Generated Library", "GeneratedType")));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => worker.DiscoverAsync(discovery, "Generated Library", cancellation.Token));

        Assert.Equal(0, discovery.CallCount);
    }

    private static VbaProjectReferenceCatalogIdentity CreateIdentity(string referenceName)
        => new(
            referenceName,
            "{33333333-3333-3333-3333-333333333333}",
            1,
            0,
            0,
            @"C:\TypeLibs\Generated.tlb");

    private static VbaProjectReferenceCatalog CreateCatalog(string referenceName, string typeName)
        => new(
            referenceName,
            ["Generated"],
            [
                new VbaProjectReferenceDefinition(
                    referenceName,
                    typeName,
                    VbaSourceDefinitionKind.Class)
            ]);

    private sealed class RecordingRefreshWorker : IVbaProjectReferenceCatalogRefreshWorker
    {
        private readonly List<string> referenceNames = [];

        public int CallCount { get; private set; }

        public IReadOnlyList<string> ReferenceNames => referenceNames;

        public async Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            IVbaProjectReferenceCatalogDiscovery discovery,
            string referenceName,
            CancellationToken cancellationToken)
        {
            CallCount++;
            referenceNames.Add(referenceName);
            return await discovery.DiscoverAsync(referenceName, cancellationToken);
        }
    }

    private sealed class CountingCatalogDiscovery : IVbaProjectReferenceCatalogDiscovery
    {
        private readonly VbaProjectReferenceCatalogDiscoveryResult result;

        public CountingCatalogDiscovery(VbaProjectReferenceCatalogDiscoveryResult result)
        {
            this.result = result;
        }

        public int CallCount { get; private set; }

        public Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class BlockingCatalogDiscovery : IVbaProjectReferenceCatalogDiscovery
    {
        private readonly VbaProjectReferenceCatalogDiscoveryResult result;
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int activeCallCount;

        public BlockingCatalogDiscovery(VbaProjectReferenceCatalogDiscoveryResult result)
        {
            this.result = result;
        }

        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount { get; private set; }

        public int MaxActiveCallCount { get; private set; }

        public List<ThreadPriority> ObservedPriorities { get; } = [];

        public async Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            var active = Interlocked.Increment(ref activeCallCount);
            MaxActiveCallCount = Math.Max(MaxActiveCallCount, active);
            ObservedPriorities.Add(Thread.CurrentThread.Priority);
            FirstStarted.TrySetResult();
            try
            {
                await release.Task.WaitAsync(cancellationToken);
                return result;
            }
            finally
            {
                Interlocked.Decrement(ref activeCallCount);
            }
        }

        public void ReleaseAll() => release.TrySetResult();
    }
}
