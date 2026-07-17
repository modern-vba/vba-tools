using System.Diagnostics;
using System.Text.Json;
using VbaLanguageServer.Lsp;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Workspace;
using Xunit;
using Xunit.Abstractions;

namespace VbaLanguageServer.Tests;

public sealed class ReferenceCatalogLifecycleTests
{
    private readonly ITestOutputHelper testOutput;

    public ReferenceCatalogLifecycleTests(ITestOutputHelper output)
    {
        testOutput = output;
    }

    [Fact]
    public async Task Ordinary_source_change_updates_analysis_without_restarting_reference_lifecycle()
    {
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.CreateBundled());
        var workspace = new VbaLanguageWorkspace(catalogCache);
        await using var output = new MemoryStream();
        var transport = new LspMessageTransport(Stream.Null, output);
        var lifecycle = new RecordingReferenceCatalogLifecycle();
        var pipeline = new VbaDocumentChangePipeline(
            workspace,
            lifecycle,
            new VbaDiagnosticsPublisher(transport, workspace));
        const string uri = "file:///C:/work/Book1/Worker.bas";
        var openedText = string.Join('\n',
        [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub BeforeChange()",
            "End Sub"
        ]);
        var changedText = openedText.Replace(
            "BeforeChange",
            "AfterChange",
            StringComparison.Ordinal);

        await pipeline.ApplyAsync(
            new VbaTextDocumentOpenedChange(uri, 1, openedText),
            CancellationToken.None);
        await pipeline.ApplyAsync(
            new VbaTextDocumentChangedChange(uri, 2, changedText),
            CancellationToken.None);

        Assert.Equal(1, lifecycle.ProjectActivationCount);
        Assert.Equal(0, lifecycle.ManifestSelectionChangeCount);
        Assert.Equal(changedText, workspace.GetDocumentText(uri));
    }

    [Fact]
    public async Task Repeated_activation_for_same_selection_runs_one_automatic_catalog_lifecycle()
    {
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var persistentStore = new CountingPersistentStore();
        var discovery = new CountingDiscovery();
        var observer = new RecordingLifecycleObserver();
        var refreshService = new VbaProjectReferenceCatalogRefreshService(
            catalogCache,
            discovery,
            persistentStore,
            new InlineRefreshWorker(),
            observer);
        var manifestWorkspace = new VbaProjectManifestWorkspace();
        await using var output = new MemoryStream();
        var transport = new LspMessageTransport(Stream.Null, output);
        var lifecycle = new ReferenceCatalogRefreshCoordinator(
            catalogCache,
            refreshService,
            manifestWorkspace,
            transport,
            observer);
        const string manifestUri = "file:///C:/work/Book1/vba-project.json";
        var manifestText = CreateManifestText("Library A", "Library B");

        lifecycle.ApplyManifestSelectionChange(manifestUri, manifestText);
        await lifecycle.WaitForIdleAsync();
        lifecycle.ApplyManifestSelectionChange(
            manifestUri,
            CreateManifestText("library b", "library a").Replace(
                "LifecycleProject",
                "RenamedLifecycleProject",
                StringComparison.Ordinal));
        await lifecycle.WaitForIdleAsync();

        Assert.Equal(2, persistentStore.LoadCount);
        Assert.Equal(2, discovery.CallCount);
        Assert.Equal(
            2,
            observer.Count(
                VbaProjectReferenceCatalogLifecycleOperation.ProjectSelectionResolve));
        Assert.Equal(
            2,
            observer.Count(
                VbaProjectReferenceCatalogLifecycleOperation.PersistedPreload));
        Assert.Equal(
            2,
            observer.Count(VbaProjectReferenceCatalogLifecycleOperation.Discovery));
        Assert.Equal(
            0,
            observer.Count(VbaProjectReferenceCatalogLifecycleOperation.Commit));
        Assert.Equal(
            0,
            observer.Count(VbaProjectReferenceCatalogLifecycleOperation.ExplicitRetry));
        Assert.Equal(
            0,
            observer.Count(
                VbaProjectReferenceCatalogLifecycleOperation.ProjectScopeInvalidation));
        await lifecycle.StopAsync();
    }

    [Fact]
    public async Task Opening_multiple_sources_activates_the_manifest_project_once()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-project-activation-").FullName;
        try
        {
            var firstSourcePath = WriteProject(projectRoot, "Generated Library");
            var secondSourcePath = Path.Combine(
                Path.GetDirectoryName(firstSourcePath)!,
                "Helper.bas");
            var catalogCache = new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.Empty);
            var persistentStore = new CountingPersistentStore();
            var discovery = new CountingDiscovery();
            var refreshService = new VbaProjectReferenceCatalogRefreshService(
                catalogCache,
                discovery,
                persistentStore,
                new InlineRefreshWorker());
            await using var output = new MemoryStream();
            var lifecycle = new ReferenceCatalogRefreshCoordinator(
                catalogCache,
                refreshService,
                new VbaProjectManifestWorkspace(),
                new LspMessageTransport(Stream.Null, output));

            lifecycle.ActivateProject(new Uri(firstSourcePath).AbsoluteUri);
            await lifecycle.WaitForIdleAsync();
            lifecycle.ActivateProject(new Uri(secondSourcePath).AbsoluteUri);
            await lifecycle.WaitForIdleAsync();

            Assert.Equal(1, persistentStore.LoadCount);
            Assert.Equal(1, discovery.CallCount);
            await lifecycle.StopAsync();
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Persisted_negative_result_is_cached_for_revision_but_retry_and_changed_selection_run()
    {
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var persistentStore = new CountingPersistentStore
        {
            LoadResult = VbaProjectReferenceCatalogPersistentLoadResult.Warning(
                "Expected unreadable catalog.")
        };
        var discovery = new CountingDiscovery();
        var observer = new RecordingLifecycleObserver();
        var refreshService = new VbaProjectReferenceCatalogRefreshService(
            catalogCache,
            discovery,
            persistentStore,
            new InlineRefreshWorker(),
            observer);
        await using var output = new MemoryStream();
        var lifecycle = new ReferenceCatalogRefreshCoordinator(
            catalogCache,
            refreshService,
            new VbaProjectManifestWorkspace(),
            new LspMessageTransport(Stream.Null, output),
            observer);
        const string manifestUri = "file:///C:/work/Book1/vba-project.json";
        var firstSelection = CreateManifestText("Library A");

        lifecycle.ApplyManifestSelectionChange(manifestUri, firstSelection);
        await lifecycle.WaitForIdleAsync();
        lifecycle.ApplyManifestSelectionChange(manifestUri, firstSelection);
        await lifecycle.WaitForIdleAsync();

        Assert.Equal(1, persistentStore.LoadCount);
        Assert.Equal(1, discovery.CallCount);

        await refreshService.RefreshAsync(CreateSelection("Library A"));

        Assert.Equal(2, persistentStore.LoadCount);
        Assert.Equal(2, discovery.CallCount);

        lifecycle.ApplyManifestSelectionChange(
            manifestUri,
            CreateManifestText("Library A", "Library B"));
        await lifecycle.WaitForIdleAsync();

        Assert.Equal(4, persistentStore.LoadCount);
        Assert.Equal(4, discovery.CallCount);
        Assert.Equal(
            4,
            observer.Count(VbaProjectReferenceCatalogLifecycleOperation.PersistedPreload));
        Assert.Equal(
            4,
            observer.Count(VbaProjectReferenceCatalogLifecycleOperation.Discovery));
        Assert.Equal(
            1,
            observer.Count(VbaProjectReferenceCatalogLifecycleOperation.ExplicitRetry));
        Assert.Equal(
            0,
            observer.Count(VbaProjectReferenceCatalogLifecycleOperation.Commit));
        await lifecycle.StopAsync();
    }

    [Fact]
    public async Task Failed_manifest_selection_resolution_is_counted_without_starting_catalog_work()
    {
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var persistentStore = new CountingPersistentStore();
        var discovery = new CountingDiscovery();
        var observer = new RecordingLifecycleObserver();
        var refreshService = new VbaProjectReferenceCatalogRefreshService(
            catalogCache,
            discovery,
            persistentStore,
            new InlineRefreshWorker(),
            observer);
        await using var output = new MemoryStream();
        var lifecycle = new ReferenceCatalogRefreshCoordinator(
            catalogCache,
            refreshService,
            new VbaProjectManifestWorkspace(),
            new LspMessageTransport(Stream.Null, output),
            observer);

        lifecycle.ApplyManifestSelectionChange(
            "file:///C:/work/Book1/vba-project.json",
            "{ invalid json");
        await lifecycle.WaitForIdleAsync();

        Assert.Equal(
            1,
            observer.Count(
                VbaProjectReferenceCatalogLifecycleOperation.ProjectSelectionResolve));
        Assert.Equal(
            0,
            observer.Count(
                VbaProjectReferenceCatalogLifecycleOperation.PersistedPreload));
        Assert.Equal(
            0,
            observer.Count(VbaProjectReferenceCatalogLifecycleOperation.Discovery));
        Assert.Equal(
            0,
            observer.Count(VbaProjectReferenceCatalogLifecycleOperation.Commit));
        Assert.Equal(
            0,
            observer.Count(VbaProjectReferenceCatalogLifecycleOperation.ExplicitRetry));
        Assert.Equal(
            0,
            observer.Count(
                VbaProjectReferenceCatalogLifecycleOperation.ProjectScopeInvalidation));
        await lifecycle.StopAsync();
    }

    [Fact]
    public async Task Equal_fingerprints_in_one_manifest_share_automatic_preload_and_discovery()
    {
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var persistentStore = new CountingPersistentStore();
        var discovery = new CountingDiscovery();
        var refreshService = new VbaProjectReferenceCatalogRefreshService(
            catalogCache,
            discovery,
            persistentStore,
            new InlineRefreshWorker());
        await using var output = new MemoryStream();
        var lifecycle = new ReferenceCatalogRefreshCoordinator(
            catalogCache,
            refreshService,
            new VbaProjectManifestWorkspace(),
            new LspMessageTransport(Stream.Null, output));

        lifecycle.ApplyManifestSelectionChange(
            "file:///C:/work/Book1/vba-project.json",
            CreateTwoDocumentManifestText("Shared Library"));
        await lifecycle.WaitForIdleAsync();

        Assert.Equal(1, persistentStore.LoadCount);
        Assert.Equal(1, discovery.CallCount);
        await lifecycle.StopAsync();
    }

    [Fact]
    public async Task Concurrent_project_scopes_share_in_flight_work_for_same_fingerprint()
    {
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var persistentStore = new BlockingPersistentStore();
        var discovery = new CountingDiscovery();
        var refreshService = new VbaProjectReferenceCatalogRefreshService(
            catalogCache,
            discovery,
            persistentStore,
            new InlineRefreshWorker());
        await using var output = new MemoryStream();
        var lifecycle = new ReferenceCatalogRefreshCoordinator(
            catalogCache,
            refreshService,
            new VbaProjectManifestWorkspace(),
            new LspMessageTransport(Stream.Null, output));
        var manifestText = CreateManifestText("Shared Library");

        lifecycle.ApplyManifestSelectionChange(
            "file:///C:/work/ProjectA/vba-project.json",
            manifestText);
        await persistentStore.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lifecycle.ApplyManifestSelectionChange(
            "file:///C:/work/ProjectB/vba-project.json",
            manifestText);
        persistentStore.Release();
        await lifecycle.WaitForIdleAsync();

        Assert.Equal(1, persistentStore.LoadCount);
        Assert.Equal(1, discovery.CallCount);
        await lifecycle.StopAsync();
    }

    [Fact]
    public async Task Changed_selection_waits_for_canceled_overlapping_refresh_to_release_its_reservation()
    {
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var persistentStore = new CountingPersistentStore();
        var discovery = new CancellationCleanupBlockingDiscovery();
        var observer = new RecordingLifecycleObserver();
        var refreshService = new VbaProjectReferenceCatalogRefreshService(
            catalogCache,
            discovery,
            persistentStore,
            new InlineRefreshWorker(),
            observer);
        await using var output = new MemoryStream();
        var lifecycle = new ReferenceCatalogRefreshCoordinator(
            catalogCache,
            refreshService,
            new VbaProjectManifestWorkspace(),
            new LspMessageTransport(Stream.Null, output),
            observer);
        const string manifestUri = "file:///C:/work/Book1/vba-project.json";

        lifecycle.ApplyManifestSelectionChange(
            manifestUri,
            CreateManifestText("Library A"));
        await discovery.FirstAttemptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lifecycle.ApplyManifestSelectionChange(
            manifestUri,
            CreateManifestText("Library A", "Library B"));
        await discovery.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var replacementStartedBeforeCleanup = await Task.WhenAny(
            discovery.ReplacementAttemptStarted.Task,
            Task.Delay(TimeSpan.FromMilliseconds(250)));
        Assert.NotSame(
            discovery.ReplacementAttemptStarted.Task,
            replacementStartedBeforeCleanup);

        discovery.ReleaseCancellationCleanup();
        await lifecycle.WaitForIdleAsync();

        Assert.Equal(
            ["Library A", "Library A", "Library B"],
            discovery.ReferenceNames);
        Assert.Equal(
            2,
            observer.Count(
                VbaProjectReferenceCatalogLifecycleOperation.ProjectSelectionResolve));
        Assert.Equal(
            3,
            observer.Count(
                VbaProjectReferenceCatalogLifecycleOperation.PersistedPreload));
        Assert.Equal(
            3,
            observer.Count(VbaProjectReferenceCatalogLifecycleOperation.Discovery));
        Assert.Equal(
            0,
            observer.Count(VbaProjectReferenceCatalogLifecycleOperation.Commit));
        Assert.Equal(
            0,
            observer.Count(VbaProjectReferenceCatalogLifecycleOperation.ExplicitRetry));
        Assert.Equal(
            0,
            observer.Count(
                VbaProjectReferenceCatalogLifecycleOperation.ProjectScopeInvalidation));
        await lifecycle.StopAsync();
    }

    [Fact]
    public async Task Overlapping_project_fingerprints_wait_for_the_in_flight_reference_owner()
    {
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var discovery = new OverlapBlockingDiscovery();
        var refreshService = new VbaProjectReferenceCatalogRefreshService(
            catalogCache,
            discovery,
            persistentStore: null,
            new InlineRefreshWorker());
        await using var output = new MemoryStream();
        var lifecycle = new ReferenceCatalogRefreshCoordinator(
            catalogCache,
            refreshService,
            new VbaProjectManifestWorkspace(),
            new LspMessageTransport(Stream.Null, output));

        lifecycle.ApplyManifestSelectionChange(
            "file:///C:/work/ProjectA/vba-project.json",
            CreateManifestText("Library A"));
        await discovery.FirstAttemptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lifecycle.ApplyManifestSelectionChange(
            "file:///C:/work/ProjectB/vba-project.json",
            CreateManifestText("Library A", "Library B"));

        var replacementStartedBeforeOwnerFinished = await Task.WhenAny(
            discovery.ReplacementAttemptStarted.Task,
            Task.Delay(TimeSpan.FromMilliseconds(250)));
        Assert.NotSame(
            discovery.ReplacementAttemptStarted.Task,
            replacementStartedBeforeOwnerFinished);

        discovery.ReleaseFirstAttempt();
        await lifecycle.WaitForIdleAsync();

        Assert.Equal(
            ["Library A", "Library A", "Library B"],
            discovery.ReferenceNames);
        await lifecycle.StopAsync();
    }

    [Fact]
    public async Task Removing_the_reference_owner_releases_its_dependent_project_scope_to_retry()
    {
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var discovery = new CancellationCleanupBlockingDiscovery();
        var refreshService = new VbaProjectReferenceCatalogRefreshService(
            catalogCache,
            discovery,
            persistentStore: null,
            new InlineRefreshWorker());
        await using var output = new MemoryStream();
        var lifecycle = new ReferenceCatalogRefreshCoordinator(
            catalogCache,
            refreshService,
            new VbaProjectManifestWorkspace(),
            new LspMessageTransport(Stream.Null, output));
        const string manifestUri = "file:///C:/work/Book1/vba-project.json";

        lifecycle.ApplyManifestSelectionChange(
            manifestUri,
            CreateOverlappingDocumentManifestText(includeReferenceOwner: true));
        await discovery.FirstAttemptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lifecycle.ApplyManifestSelectionChange(
            manifestUri,
            CreateOverlappingDocumentManifestText(includeReferenceOwner: false));
        await discovery.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var dependentStartedBeforeCleanup = await Task.WhenAny(
            discovery.ReplacementAttemptStarted.Task,
            Task.Delay(TimeSpan.FromMilliseconds(250)));
        Assert.NotSame(
            discovery.ReplacementAttemptStarted.Task,
            dependentStartedBeforeCleanup);

        discovery.ReleaseCancellationCleanup();
        await lifecycle.WaitForIdleAsync();

        Assert.Equal(
            ["Library A", "Library A", "Library B"],
            discovery.ReferenceNames);
        await lifecycle.StopAsync();
    }

    [Fact]
    public async Task Delayed_lifecycle_does_not_block_source_updates_or_project_queries()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-delayed-lifecycle-").FullName;
        try
        {
            var sourcePath = WriteProject(projectRoot, "Delayed Library");
            var sourceUri = new Uri(sourcePath).AbsoluteUri;
            var catalogCache = new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.Empty);
            var persistentStore = new BlockingPersistentStore();
            var observer = new RecordingLifecycleObserver();
            var refreshService = new VbaProjectReferenceCatalogRefreshService(
                catalogCache,
                new CountingDiscovery(),
                persistentStore,
                new InlineRefreshWorker(),
                observer);
            var workspace = new VbaLanguageWorkspace(catalogCache, observer);
            const string initialText =
                "Attribute VB_Name = \"Worker\"\nPublic Sub BeforeChange()\nEnd Sub";
            const string changedText =
                "Attribute VB_Name = \"Worker\"\nPublic Sub AfterChange()\nEnd Sub";
            await using var output = new MemoryStream();
            var transport = new LspMessageTransport(Stream.Null, output);
            workspace.OpenDocument(sourceUri, 1, initialText);
            var requestExecution = new VbaLspRequestExecution(transport, workspace);
            var baselineP95 = await MeasurePositionRequestP95Async(
                requestExecution,
                "textDocument/completion",
                sourceUri,
                line: 1,
                character: 0);
            var lifecycle = new ReferenceCatalogRefreshCoordinator(
                catalogCache,
                refreshService,
                workspace.ManifestWorkspace,
                transport,
                observer);
            var pipeline = new VbaDocumentChangePipeline(
                workspace,
                lifecycle,
                new VbaDiagnosticsPublisher(transport, workspace));

            lifecycle.ActivateProject(sourceUri);
            await persistentStore.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var resolveCountBeforeChange = observer.Count(
                VbaProjectReferenceCatalogLifecycleOperation.ProjectSelectionResolve);
            var snapshotResolveCountBeforeChange = observer.Count(
                VbaProjectReferenceCatalogLifecycleOperation.ProjectSnapshotManifestResolve);
            var preloadCountBeforeChange = observer.Count(
                VbaProjectReferenceCatalogLifecycleOperation.PersistedPreload);
            var discoveryCountBeforeChange = observer.Count(
                VbaProjectReferenceCatalogLifecycleOperation.Discovery);
            var commitCountBeforeChange = observer.Count(
                VbaProjectReferenceCatalogLifecycleOperation.Commit);
            var retryCountBeforeChange = observer.Count(
                VbaProjectReferenceCatalogLifecycleOperation.ExplicitRetry);
            var invalidationCountBeforeChange = observer.Count(
                VbaProjectReferenceCatalogLifecycleOperation.ProjectScopeInvalidation);
            try
            {
                await pipeline.ApplyAsync(
                        new VbaTextDocumentChangedChange(sourceUri, 2, changedText),
                        CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(1));
                await ExecutePositionRequestAsync(
                    requestExecution,
                    requestId: 20_001,
                    "textDocument/hover",
                    sourceUri,
                    line: 1,
                    character: 5);
                await ExecutePositionRequestAsync(
                    requestExecution,
                    requestId: 20_002,
                    "textDocument/signatureHelp",
                    sourceUri,
                    line: 1,
                    character: 5);
                var delayedP95 = await MeasurePositionRequestP95Async(
                    requestExecution,
                    "textDocument/completion",
                    sourceUri,
                    line: 1,
                    character: 0,
                    requestIdBase: 30_000);
                testOutput.WriteLine(
                    $"interactiveQueryBaselineP95Ms={baselineP95.TotalMilliseconds:F6} interactiveQueryDelayedP95Ms={delayedP95.TotalMilliseconds:F6} deltaP95Ms={(delayedP95 - baselineP95).TotalMilliseconds:F6}");

                Assert.Equal(changedText, workspace.GetDocumentText(sourceUri));
                Assert.Equal(
                    resolveCountBeforeChange,
                    observer.Count(
                        VbaProjectReferenceCatalogLifecycleOperation.ProjectSelectionResolve));
                Assert.Equal(
                    snapshotResolveCountBeforeChange,
                    observer.Count(
                        VbaProjectReferenceCatalogLifecycleOperation.ProjectSnapshotManifestResolve));
                Assert.Equal(
                    preloadCountBeforeChange,
                    observer.Count(
                        VbaProjectReferenceCatalogLifecycleOperation.PersistedPreload));
                Assert.Equal(
                    discoveryCountBeforeChange,
                    observer.Count(VbaProjectReferenceCatalogLifecycleOperation.Discovery));
                Assert.Equal(
                    commitCountBeforeChange,
                    observer.Count(VbaProjectReferenceCatalogLifecycleOperation.Commit));
                Assert.Equal(
                    retryCountBeforeChange,
                    observer.Count(
                        VbaProjectReferenceCatalogLifecycleOperation.ExplicitRetry));
                Assert.Equal(
                    invalidationCountBeforeChange,
                    observer.Count(
                        VbaProjectReferenceCatalogLifecycleOperation.ProjectScopeInvalidation));
                Assert.True(
                    delayedP95 - baselineP95 <= TimeSpan.FromMilliseconds(5),
                    $"Expected delayed lifecycle p95 delta <= 5 ms, baseline={baselineP95.TotalMilliseconds:F6} ms, delayed={delayedP95.TotalMilliseconds:F6} ms.");
            }
            finally
            {
                persistentStore.Release();
                await lifecycle.WaitForIdleAsync();
                await lifecycle.StopAsync();
            }
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    private static Task ExecutePositionRequestAsync(
        VbaLspRequestExecution requestExecution,
        int requestId,
        string method,
        string uri,
        int line,
        int character)
    {
        var request = new System.Text.Json.Nodes.JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = requestId,
            ["method"] = method,
            ["params"] = JsonSerializer.SerializeToNode(new
            {
                textDocument = new { uri },
                position = new { line, character }
            })
        };
        return requestExecution.ExecuteAsync(
                request,
                CancellationToken.None,
                CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Stop_cancels_and_observes_blocked_persisted_preload()
    {
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var persistentStore = new BlockingPersistentStore();
        var observer = new RecordingLifecycleObserver();
        var refreshService = new VbaProjectReferenceCatalogRefreshService(
            catalogCache,
            new CountingDiscovery(),
            persistentStore,
            new InlineRefreshWorker(),
            observer);
        await using var output = new MemoryStream();
        var lifecycle = new ReferenceCatalogRefreshCoordinator(
            catalogCache,
            refreshService,
            new VbaProjectManifestWorkspace(),
            new LspMessageTransport(Stream.Null, output),
            observer);

        lifecycle.ApplyManifestSelectionChange(
            "file:///C:/work/Book1/vba-project.json",
            CreateManifestText("Blocked Library"));
        await persistentStore.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await lifecycle.StopAsync().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(persistentStore.CancellationObserved.Task.IsCompletedSuccessfully);
        Assert.Equal(
            0,
            observer.Count(VbaProjectReferenceCatalogLifecycleOperation.Commit));
    }

    [Fact]
    public async Task Stop_is_bounded_when_discovery_cannot_observe_cancellation()
    {
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var discovery = new NonCooperativeBlockingDiscovery();
        var refreshService = new VbaProjectReferenceCatalogRefreshService(
            catalogCache,
            discovery,
            persistentStore: null,
            new InlineRefreshWorker());
        await using var output = new MemoryStream();
        var lifecycle = new ReferenceCatalogRefreshCoordinator(
            catalogCache,
            refreshService,
            new VbaProjectManifestWorkspace(),
            new LspMessageTransport(Stream.Null, output));

        lifecycle.ApplyManifestSelectionChange(
            "file:///C:/work/Book1/vba-project.json",
            CreateManifestText("Blocked Library"));
        await discovery.DiscoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stop = lifecycle.StopAsync();
        try
        {
            await stop.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(discovery.DiscoveryCompleted.Task.IsCompleted);
        }
        finally
        {
            discovery.ReleaseDiscovery();
            await lifecycle.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await stop;
        }
    }

    private static async Task<TimeSpan> MeasurePositionRequestP95Async(
        VbaLspRequestExecution requestExecution,
        string method,
        string sourceUri,
        int line,
        int character,
        int requestIdBase = 10_000)
    {
        await ExecutePositionRequestAsync(
            requestExecution,
            requestIdBase,
            method,
            sourceUri,
            line,
            character);
        var measurements = new long[128];
        for (var index = 0; index < measurements.Length; index++)
        {
            var started = Stopwatch.GetTimestamp();
            await ExecutePositionRequestAsync(
                requestExecution,
                requestIdBase + index + 1,
                method,
                sourceUri,
                line,
                character);
            measurements[index] = Stopwatch.GetTimestamp() - started;
        }

        Array.Sort(measurements);
        var p95Index = (int)Math.Ceiling(measurements.Length * 0.95) - 1;
        return Stopwatch.GetElapsedTime(0, measurements[p95Index]);
    }

    [Fact]
    public void Source_snapshot_invalidation_reuses_the_manifest_resolution()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-resolution-cache-").FullName;
        try
        {
            var sourcePath = Path.Combine(projectRoot, "Worker.bas");
            var sourceUri = new Uri(sourcePath).AbsoluteUri;
            var observer = new RecordingLifecycleObserver();
            var resolutionSource = new CountingManifestResolutionSource(
                new VbaProjectResolution(
                    VbaProjectResolutionKind.AdHoc,
                    projectRoot));
            var provider = new VbaProjectSnapshotProvider(
                new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty),
                new VbaProjectSourceDocumentCache(),
                resolutionSource,
                observer);
            var initialState = new VbaWorkspaceSnapshotState(
                new Dictionary<string, VbaTrackedDocument>(),
                new HashSet<string>(),
                Version: 1);
            var changedState = initialState with { Version = 2 };

            provider.CreateProjectSnapshot(sourceUri, initialState, CancellationToken.None);
            provider.Invalidate();
            provider.CreateProjectSnapshot(sourceUri, changedState, CancellationToken.None);

            Assert.Equal(1, resolutionSource.ResolveCount);
            Assert.Equal(
                1,
                observer.Count(
                    VbaProjectReferenceCatalogLifecycleOperation.ProjectSnapshotManifestResolve));
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Catalog_commit_rebuilds_only_project_scopes_that_select_the_reference()
    {
        var workspaceRoot = Directory.CreateTempSubdirectory("vba-ls-catalog-scope-").FullName;
        try
        {
            var projectARoot = Path.Combine(workspaceRoot, "ProjectA");
            var projectBRoot = Path.Combine(workspaceRoot, "ProjectB");
            var projectASourcePath = WriteProject(projectARoot, "Library A");
            var projectBSourcePath = WriteProject(projectBRoot, "Library B");
            var projectAUri = new Uri(projectASourcePath).AbsoluteUri;
            var projectBUri = new Uri(projectBSourcePath).AbsoluteUri;
            var observer = new RecordingLifecycleObserver();
            var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
            var workspace = new VbaLanguageWorkspace(cache, observer);
            const string sourceText = "Attribute VB_Name = \"Worker\"\nPublic Sub Run()\nEnd Sub";
            workspace.UpdateDocument(projectAUri, sourceText);
            workspace.UpdateDocument(projectBUri, sourceText);

            var beforeA = workspace.CreateProjectSnapshot(projectAUri);
            var beforeB = workspace.CreateProjectSnapshot(projectBUri);
            var refreshService = new VbaProjectReferenceCatalogRefreshService(
                cache,
                new SuccessfulDiscovery(),
                persistentStore: null,
                new InlineRefreshWorker(),
                observer);
            await refreshService.RefreshAsync(CreateSelection("Library A"));
            var afterA = workspace.CreateProjectSnapshot(projectAUri);
            var afterB = workspace.CreateProjectSnapshot(projectBUri);

            Assert.NotSame(beforeA, afterA);
            Assert.Same(beforeB, afterB);
            Assert.Equal(
                1,
                observer.Count(VbaProjectReferenceCatalogLifecycleOperation.Commit));
            Assert.Equal(
                1,
                observer.Count(
                    VbaProjectReferenceCatalogLifecycleOperation.ProjectScopeInvalidation));
            Assert.Equal(
                1,
                observer.Count(VbaProjectReferenceCatalogLifecycleOperation.ExplicitRetry));
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    private static string WriteProject(string projectRoot, string referenceName)
    {
        var sourceDirectory = Path.Combine(projectRoot, "src", "Book1");
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(
            Path.Combine(projectRoot, "vba-project.json"),
            CreateManifestText(referenceName));
        return Path.Combine(sourceDirectory, "Worker.bas");
    }

    private static VbaProjectReferenceCatalogDiscoveryResult CreateDiscoverySuccess(
        string referenceName)
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
                [referenceName.Replace(" ", "", StringComparison.Ordinal)],
                [
                    new VbaProjectReferenceDefinition(
                        referenceName,
                        $"{referenceName.Replace(" ", "", StringComparison.Ordinal)}Type",
                        VbaSourceDefinitionKind.Class)
                ]));

    private static string CreateManifestText(params string[] referenceNames)
        => JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            projectName = "LifecycleProject",
            primaryDocument = "Book1",
            documents = new Dictionary<string, object>
            {
                ["Book1"] = new
                {
                    kind = "excel",
                    sourcePath = "src/Book1",
                    templatePath = "src/Book1/Book1.xlsm",
                    binPath = "bin/Book1/Book1.xlsm",
                    publishPath = "publish/Book1/Book1.xlsm",
                    references = referenceNames
                        .Select(referenceName => new { name = referenceName })
                        .ToArray()
                }
            }
        });

    private static string CreateTwoDocumentManifestText(string referenceName)
        => JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            projectName = "SharedLifecycleProject",
            primaryDocument = "Book1",
            documents = new Dictionary<string, object>
            {
                ["Book1"] = CreateDocument("src/Book1", referenceName),
                ["Book2"] = CreateDocument("src/Book2", referenceName)
            }
        });

    private static string CreateOverlappingDocumentManifestText(bool includeReferenceOwner)
    {
        var documents = new Dictionary<string, object>();
        if (includeReferenceOwner)
        {
            documents["Book1"] = CreateDocument("src/Book1", "Library A");
        }

        documents["Book2"] = CreateDocument(
            "src/Book2",
            "Library A",
            "Library B");
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            projectName = "OverlappingLifecycleProject",
            primaryDocument = "Book2",
            documents
        });
    }

    private static object CreateDocument(string sourcePath, params string[] referenceNames)
        => new
        {
            kind = "excel",
            sourcePath,
            templatePath = $"{sourcePath}/Book.xlsm",
            binPath = $"bin/{Path.GetFileName(sourcePath)}/Book.xlsm",
            publishPath = $"publish/{Path.GetFileName(sourcePath)}/Book.xlsm",
            references = referenceNames
                .Select(referenceName => new { name = referenceName })
                .ToArray()
        };

    private static VbaProjectReferenceSelection CreateSelection(params string[] referenceNames)
        => VbaProjectReferenceSelection.Create(
            ProjectDocument.ExcelKind,
            referenceNames
                .Select(referenceName => new VbaProjectReference(referenceName))
                .ToArray());

    private sealed class RecordingReferenceCatalogLifecycle : IReferenceCatalogLifecycle
    {
        public int ProjectActivationCount { get; private set; }

        public int ManifestSelectionChangeCount { get; private set; }

        public void ActivateProject(string uri)
            => ProjectActivationCount++;

        public void ApplyManifestSelectionChange(string uri, string text)
            => ManifestSelectionChangeCount++;

        public void DeactivateManifest(string uri)
        {
        }

    }

    private sealed class CountingPersistentStore : IVbaProjectReferenceCatalogPersistentStore
    {
        public VbaProjectReferenceCatalogPersistentLoadResult LoadResult { get; init; } =
            VbaProjectReferenceCatalogPersistentLoadResult.Miss();

        public int LoadCount { get; private set; }

        public Task<VbaProjectReferenceCatalogPersistentLoadResult> LoadAsync(
            string referenceName,
            CancellationToken cancellationToken)
        {
            LoadCount++;
            return Task.FromResult(LoadResult);
        }

        public Task SaveAsync(
            VbaProjectReferenceCatalogPersistentEntry entry,
            CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class CountingDiscovery : IVbaProjectReferenceCatalogDiscovery
    {
        public int CallCount { get; private set; }

        public Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(
                VbaProjectReferenceCatalogDiscoveryResult.Failure(
                    referenceName,
                    "Expected lifecycle test result."));
        }
    }

    private sealed class SuccessfulDiscovery : IVbaProjectReferenceCatalogDiscovery
    {
        public Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDiscoverySuccess(referenceName));
    }

    private sealed class NonCooperativeBlockingDiscovery
        : IVbaProjectReferenceCatalogDiscovery
    {
        private readonly TaskCompletionSource releaseDiscovery =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource DiscoveryStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource DiscoveryCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
        {
            DiscoveryStarted.TrySetResult();
            await releaseDiscovery.Task;
            DiscoveryCompleted.TrySetResult();
            return CreateDiscoverySuccess(referenceName);
        }

        public void ReleaseDiscovery()
            => releaseDiscovery.TrySetResult();
    }

    private sealed class CancellationCleanupBlockingDiscovery
        : IVbaProjectReferenceCatalogDiscovery
    {
        private readonly object gate = new();
        private readonly List<string> referenceNames = [];
        private readonly TaskCompletionSource releaseCancellationCleanup =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool firstAttempt = true;

        public TaskCompletionSource FirstAttemptStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReplacementAttemptStarted { get; } =
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

            if (firstAttempt)
            {
                firstAttempt = false;
                FirstAttemptStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    CancellationObserved.TrySetResult();
                    await releaseCancellationCleanup.Task;
                    throw;
                }
            }

            ReplacementAttemptStarted.TrySetResult();
            return VbaProjectReferenceCatalogDiscoveryResult.Failure(
                referenceName,
                "Expected replacement lifecycle result.");
        }

        public void ReleaseCancellationCleanup()
            => releaseCancellationCleanup.TrySetResult();
    }

    private sealed class OverlapBlockingDiscovery
        : IVbaProjectReferenceCatalogDiscovery
    {
        private readonly object gate = new();
        private readonly List<string> referenceNames = [];
        private readonly TaskCompletionSource releaseFirstAttempt =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool firstAttempt = true;

        public TaskCompletionSource FirstAttemptStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReplacementAttemptStarted { get; } =
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

            if (firstAttempt)
            {
                firstAttempt = false;
                FirstAttemptStarted.TrySetResult();
                await releaseFirstAttempt.Task.WaitAsync(cancellationToken);
            }
            else
            {
                ReplacementAttemptStarted.TrySetResult();
            }

            return VbaProjectReferenceCatalogDiscoveryResult.Failure(
                referenceName,
                "Expected overlapping lifecycle result.");
        }

        public void ReleaseFirstAttempt()
            => releaseFirstAttempt.TrySetResult();
    }

    private sealed class BlockingPersistentStore : IVbaProjectReferenceCatalogPersistentStore
    {
        private readonly TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource LoadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int LoadCount { get; private set; }

        public async Task<VbaProjectReferenceCatalogPersistentLoadResult> LoadAsync(
            string referenceName,
            CancellationToken cancellationToken)
        {
            LoadCount++;
            LoadStarted.TrySetResult();
            try
            {
                await release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                throw;
            }

            return VbaProjectReferenceCatalogPersistentLoadResult.Miss();
        }

        public Task SaveAsync(
            VbaProjectReferenceCatalogPersistentEntry entry,
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        public void Release()
            => release.TrySetResult();
    }

    private sealed class InlineRefreshWorker : IVbaProjectReferenceCatalogRefreshWorker
    {
        public Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            IVbaProjectReferenceCatalogDiscovery discovery,
            string referenceName,
            CancellationToken cancellationToken)
            => discovery.DiscoverAsync(referenceName, cancellationToken);
    }

    private sealed class CountingManifestResolutionSource : IVbaProjectManifestResolutionSource
    {
        private readonly VbaProjectResolution resolution;

        public CountingManifestResolutionSource(VbaProjectResolution resolution)
        {
            this.resolution = resolution;
        }

        public long Version { get; set; }

        public int ResolveCount { get; private set; }

        public VbaProjectResolution Resolve(string activeUri)
        {
            ResolveCount++;
            return resolution;
        }
    }

    private sealed class RecordingLifecycleObserver
        : IVbaProjectReferenceCatalogLifecycleObserver
    {
        private readonly object gate = new();
        private readonly List<VbaProjectReferenceCatalogLifecycleEvent> events = [];

        public void Record(VbaProjectReferenceCatalogLifecycleEvent lifecycleEvent)
        {
            lock (gate)
            {
                events.Add(lifecycleEvent);
            }
        }

        public int Count(VbaProjectReferenceCatalogLifecycleOperation operation)
        {
            lock (gate)
            {
                return events.Count(lifecycleEvent =>
                    lifecycleEvent.Operation == operation);
            }
        }
    }
}
