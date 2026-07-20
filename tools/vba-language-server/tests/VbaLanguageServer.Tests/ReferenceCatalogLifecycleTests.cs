using System.Diagnostics;
using System.Text.Json;
using VbaLanguageServer.Lsp;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Workspace;
using Xunit;
using Xunit.Abstractions;

namespace VbaLanguageServer.Tests;

public sealed class ReferenceCatalogLifecycleTests : IAsyncLifetime
{
    private readonly ITestOutputHelper testOutput;
    private readonly VbaInteractiveWorkScheduler defaultScheduler = new();

    public ReferenceCatalogLifecycleTests(ITestOutputHelper output)
    {
        testOutput = output;
    }

    public Task InitializeAsync()
        => Task.CompletedTask;

    public async Task DisposeAsync()
        => await defaultScheduler.StopAsync(VbaInteractiveStopReason.Abort);

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
            CreateDiagnosticsPublisher(transport, workspace));
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
    public async Task Closing_deleted_manifest_overlay_reactivates_the_outer_open_source_catalog()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-close-overlay-outer-catalog-").FullName;
        try
        {
            var nestedRoot = Path.Combine(
                projectRoot,
                "src",
                "Nested");
            var sourcePath = Path.Combine(
                nestedRoot,
                "src",
                "Book1",
                "Worker.bas");
            Directory.CreateDirectory(
                Path.GetDirectoryName(sourcePath)!);
            var outerManifestText = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                projectName = "OuterCatalogProject",
                primaryDocument = "Book1",
                documents = new Dictionary<string, object>
                {
                    ["Book1"] = CreateDocument(
                        "src",
                        "Outer Custom Library")
                }
            });
            var nestedManifestText = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                projectName = "NestedCatalogProject",
                primaryDocument = "Book1",
                documents = new Dictionary<string, object>
                {
                    ["Book1"] = CreateDocument(
                        "src/Book1",
                        "Nested Custom Library")
                }
            });
            File.WriteAllText(
                Path.Combine(projectRoot, "vba-project.json"),
                outerManifestText);
            var nestedManifestPath = Path.Combine(
                nestedRoot,
                "vba-project.json");
            File.WriteAllText(
                nestedManifestPath,
                nestedManifestText);
            var sourceUri = new Uri(sourcePath).AbsoluteUri;
            var nestedManifestUri =
                new Uri(nestedManifestPath).AbsoluteUri;
            var catalogCache = new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled());
            var workspace = new VbaLanguageWorkspace(catalogCache);
            await using var output = new MemoryStream();
            var transport = new LspMessageTransport(
                Stream.Null,
                output);
            var lifecycle =
                new RecordingReferenceCatalogLifecycle();
            var pipeline = new VbaDocumentChangePipeline(
                workspace,
                lifecycle,
                CreateDiagnosticsPublisher(transport, workspace));
            const string sourceText =
                "Attribute VB_Name = \"Worker\"\n"
                + "Public Sub Run()\n"
                + "End Sub";
            await pipeline.ApplyAsync(
                new VbaTextDocumentOpenedChange(
                    sourceUri,
                    1,
                    sourceText),
                CancellationToken.None);
            _ = workspace.CreateProjectSnapshot(sourceUri);
            await pipeline.ApplyAsync(
                new VbaTextDocumentOpenedChange(
                    nestedManifestUri,
                    1,
                    nestedManifestText),
                CancellationToken.None);
            var capturedRevision = workspace.ManifestWorkspace
                .GetReconciliationRevision(nestedManifestUri);
            var deletion = workspace.ManifestWorkspace
                .DeleteReconciledManifest(
                    nestedManifestUri,
                    capturedRevision);
            Assert.Equal(
                VbaProjectManifestReconciliationStatus.Observed,
                deletion.Status);

            await pipeline.ApplyAsync(
                new VbaTextDocumentClosedChange(
                    nestedManifestUri),
                CancellationToken.None);

            Assert.Equal(2, lifecycle.ProjectActivationCount);
            var resolution = workspace.ManifestWorkspace
                .CaptureResolution(sourceUri)
                .Resolution;
            Assert.Equal(
                "Outer Custom Library",
                Assert.Single(resolution.ReferenceEntries).Name);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Watched_manifest_deletion_reactivates_the_outer_open_source_catalog()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-delete-manifest-outer-catalog-").FullName;
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "vba-project.json"),
                CreateManifestText("Outer Custom Library"));
            var nestedRoot = Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Nested");
            var sourcePath = Path.Combine(
                nestedRoot,
                "src",
                "Book1",
                "Worker.bas");
            Directory.CreateDirectory(
                Path.GetDirectoryName(sourcePath)!);
            var nestedManifestPath = Path.Combine(
                nestedRoot,
                "vba-project.json");
            File.WriteAllText(
                nestedManifestPath,
                CreateManifestText("Nested Custom Library"));
            var sourceUri = new Uri(sourcePath).AbsoluteUri;
            var nestedManifestUri =
                new Uri(nestedManifestPath).AbsoluteUri;
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()));
            await using var output = new MemoryStream();
            var transport = new LspMessageTransport(
                Stream.Null,
                output);
            var lifecycle =
                new RecordingReferenceCatalogLifecycle();
            var pipeline = new VbaDocumentChangePipeline(
                workspace,
                lifecycle,
                CreateDiagnosticsPublisher(transport, workspace));
            const string sourceText =
                "Attribute VB_Name = \"Worker\"\n"
                + "Public Sub Run()\n"
                + "End Sub";
            await pipeline.ApplyAsync(
                new VbaTextDocumentOpenedChange(
                    sourceUri,
                    1,
                    sourceText),
                CancellationToken.None);
            _ = workspace.CreateProjectSnapshot(sourceUri);
            File.Delete(nestedManifestPath);

            await pipeline.ApplyAsync(
                new VbaWatchedFileDeletedChange(
                    nestedManifestUri),
                CancellationToken.None);

            Assert.Equal(2, lifecycle.ProjectActivationCount);
            var resolution = workspace.ManifestWorkspace
                .CaptureResolution(sourceUri)
                .Resolution;
            Assert.Equal(
                "Outer Custom Library",
                Assert.Single(resolution.ReferenceEntries).Name);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Closing_unmapped_manifest_boundary_reactivates_the_outer_open_source_catalog()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-close-unmapped-boundary-catalog-").FullName;
        try
        {
            var nestedRoot = Path.Combine(
                projectRoot,
                "src",
                "Nested");
            var sourcePath = Path.Combine(
                nestedRoot,
                "Actual",
                "Worker.bas");
            Directory.CreateDirectory(
                Path.GetDirectoryName(sourcePath)!);
            var outerManifestText = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                projectName = "OuterCatalogProject",
                primaryDocument = "Book1",
                documents = new Dictionary<string, object>
                {
                    ["Book1"] = CreateDocument(
                        "src",
                        "Outer Custom Library")
                }
            });
            var nestedManifestText = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                projectName = "NestedCatalogProject",
                primaryDocument = "Book1",
                documents = new Dictionary<string, object>
                {
                    ["Book1"] = CreateDocument(
                        "Elsewhere",
                        "Nested Custom Library")
                }
            });
            File.WriteAllText(
                Path.Combine(projectRoot, "vba-project.json"),
                outerManifestText);
            var nestedManifestPath = Path.Combine(
                nestedRoot,
                "vba-project.json");
            File.WriteAllText(
                nestedManifestPath,
                nestedManifestText);
            var sourceUri = new Uri(sourcePath).AbsoluteUri;
            var nestedManifestUri =
                new Uri(nestedManifestPath).AbsoluteUri;
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()));
            await using var output = new MemoryStream();
            var transport = new LspMessageTransport(
                Stream.Null,
                output);
            var lifecycle =
                new RecordingReferenceCatalogLifecycle();
            var pipeline = new VbaDocumentChangePipeline(
                workspace,
                lifecycle,
                CreateDiagnosticsPublisher(transport, workspace));
            const string sourceText =
                "Attribute VB_Name = \"Worker\"\n"
                + "Public Sub Run()\n"
                + "End Sub";
            await pipeline.ApplyAsync(
                new VbaTextDocumentOpenedChange(
                    sourceUri,
                    1,
                    sourceText),
                CancellationToken.None);
            Assert.Equal(
                VbaProjectResolutionKind.AdHoc,
                workspace.ManifestWorkspace
                    .CaptureResolution(sourceUri)
                    .Resolution
                    .Kind);
            await pipeline.ApplyAsync(
                new VbaTextDocumentOpenedChange(
                    nestedManifestUri,
                    1,
                    nestedManifestText),
                CancellationToken.None);
            var capturedRevision = workspace.ManifestWorkspace
                .GetReconciliationRevision(nestedManifestUri);
            Assert.Equal(
                VbaProjectManifestReconciliationStatus.Observed,
                workspace.ManifestWorkspace
                    .DeleteReconciledManifest(
                        nestedManifestUri,
                        capturedRevision)
                    .Status);

            await pipeline.ApplyAsync(
                new VbaTextDocumentClosedChange(
                    nestedManifestUri),
                CancellationToken.None);

            Assert.Equal(2, lifecycle.ProjectActivationCount);
            Assert.Equal(
                "Outer Custom Library",
                Assert.Single(
                    workspace.ManifestWorkspace
                        .CaptureResolution(sourceUri)
                        .Resolution
                        .ReferenceEntries)
                    .Name);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
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
        lifecycle.AttachScheduler(defaultScheduler);
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
    public async Task Automatic_catalog_lifecycle_starts_through_the_background_scheduler()
    {
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var persistentStore = new CountingPersistentStore();
        var refreshService = new VbaProjectReferenceCatalogRefreshService(
            catalogCache,
            new CountingDiscovery(),
            persistentStore,
            new InlineRefreshWorker());
        await using var output = new MemoryStream();
        var lifecycle = new ReferenceCatalogRefreshCoordinator(
            catalogCache,
            refreshService,
            new VbaProjectManifestWorkspace(),
            new LspMessageTransport(Stream.Null, output));
        var timingSink = new SignallingTimingSink();
        await using var scheduler = new VbaInteractiveWorkScheduler(
            timingSink,
            options: new VbaInteractiveWorkSchedulerOptions(
                CoalesceSupersededMutations: true,
                EnableConcurrentReads: true,
                MaxConcurrentReads: 1,
                MaxConcurrentBulkReads: 1));
        var blockerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocker = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = scheduler.AdmitRequest(
            requestId: null,
            "textDocument/hover",
            _ => new object(),
            async (_, cancellationToken) =>
            {
                blockerStarted.TrySetResult();
                await releaseBlocker.Task.WaitAsync(cancellationToken);
            });
        await blockerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lifecycle.AttachScheduler(scheduler);

        lifecycle.ApplyManifestSelectionChange(
            "file:///C:/work/Book1/vba-project.json",
            CreateManifestText("Library A"));
        await timingSink.WaitForAdmissionAsync("vba/referenceCatalogRefresh");

        Assert.Equal(0, persistentStore.LoadCount);

        releaseBlocker.TrySetResult();
        await blocker.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await lifecycle.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, persistentStore.LoadCount);
        await scheduler.StopAsync(VbaInteractiveStopReason.Complete);
        await lifecycle.StopAsync();
    }

    [Fact]
    public async Task Catalog_refresh_overflow_retries_only_the_latest_plan_after_capacity_returns()
    {
        const string uri = "file:///C:/work/Book1/vba-project.json";
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var discovery = new CountingDiscovery();
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
        var blockerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocker = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scheduler = new VbaInteractiveWorkScheduler(
            options: new VbaInteractiveWorkSchedulerOptions(
                CoalesceSupersededMutations: true,
                MaxOwnedWork: 1));
        var blocker = scheduler.AdmitMutation(async cancellationToken =>
        {
            blockerStarted.TrySetResult();
            await releaseBlocker.Task.WaitAsync(cancellationToken);
        });
        await blockerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lifecycle.AttachScheduler(scheduler);

        lifecycle.ApplyManifestSelectionChange(uri, CreateManifestText("Library A"));
        lifecycle.ApplyManifestSelectionChange(uri, CreateManifestText("Library B"));
        lifecycle.ApplyManifestSelectionChange(uri, CreateManifestText("Library C"));

        Assert.Empty(discovery.ReferenceNames);
        Assert.True(scheduler.IsAccepting);
        releaseBlocker.TrySetResult();
        await blocker.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await lifecycle.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["Library C"], discovery.ReferenceNames);
        await lifecycle.StopAsync();
        await scheduler.StopAsync(VbaInteractiveStopReason.Complete);
    }

    [Fact]
    public async Task Concurrent_plan_posts_cannot_restore_an_older_reserved_plan()
    {
        const string uri = "file:///C:/work/Book1/vba-project.json";
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var discovery = new CountingDiscovery();
        var refreshService = new VbaProjectReferenceCatalogRefreshService(
            catalogCache,
            discovery,
            persistentStore: null,
            new InlineRefreshWorker());
        var planObserver = new BlockingFirstPlanReservationObserver();
        await using var output = new MemoryStream();
        var lifecycle = new ReferenceCatalogRefreshCoordinator(
            catalogCache,
            refreshService,
            new VbaProjectManifestWorkspace(),
            new LspMessageTransport(Stream.Null, output),
            lifecycleObserver: null,
            planObserver: planObserver);
        var blockerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocker = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scheduler = new VbaInteractiveWorkScheduler(
            options: new VbaInteractiveWorkSchedulerOptions(
                CoalesceSupersededMutations: true,
                MaxOwnedWork: 1));
        var blocker = scheduler.AdmitMutation(async cancellationToken =>
        {
            blockerStarted.TrySetResult();
            await releaseBlocker.Task.WaitAsync(cancellationToken);
        });
        await blockerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lifecycle.AttachScheduler(scheduler);

        var older = Task.Run(
            () => lifecycle.ApplyManifestSelectionChange(
                uri,
                CreateManifestText("Library A")));
        await planObserver.FirstPlanReserved.Task
            .WaitAsync(TimeSpan.FromSeconds(5));
        var latestStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var latest = Task.Run(
            () =>
            {
                latestStarted.TrySetResult();
                lifecycle.ApplyManifestSelectionChange(
                    uri,
                    CreateManifestText("Library B"));
            });
        await latestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        Assert.False(latest.IsCompleted);

        planObserver.ReleaseFirstPlan();
        await Task.WhenAll(older, latest).WaitAsync(TimeSpan.FromSeconds(5));
        releaseBlocker.TrySetResult();
        await blocker.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await lifecycle.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["Library B"], discovery.ReferenceNames);
        await lifecycle.StopAsync();
        await scheduler.StopAsync(VbaInteractiveStopReason.Complete);
    }

    [Fact]
    public async Task Catalog_refresh_overflow_discards_a_plan_deactivated_before_capacity_returns()
    {
        const string uri = "file:///C:/work/Book1/vba-project.json";
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var discovery = new CountingDiscovery();
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
        var blockerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocker = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scheduler = new VbaInteractiveWorkScheduler(
            options: new VbaInteractiveWorkSchedulerOptions(
                CoalesceSupersededMutations: true,
                MaxOwnedWork: 1));
        var blocker = scheduler.AdmitMutation(async cancellationToken =>
        {
            blockerStarted.TrySetResult();
            await releaseBlocker.Task.WaitAsync(cancellationToken);
        });
        await blockerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lifecycle.AttachScheduler(scheduler);

        lifecycle.ApplyManifestSelectionChange(uri, CreateManifestText("Library A"));
        lifecycle.ApplyManifestSelectionChange(uri, "{");

        releaseBlocker.TrySetResult();
        await blocker.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await lifecycle.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(discovery.ReferenceNames);
        await lifecycle.StopAsync();
        await scheduler.StopAsync(VbaInteractiveStopReason.Complete);
    }

    [Fact]
    public async Task Deactivation_rejects_a_plan_already_taken_by_the_mailbox()
    {
        const string uri = "file:///C:/work/Book1/vba-project.json";
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var persistentStore = new CountingPersistentStore();
        var discovery = new CountingDiscovery();
        var refreshService = new VbaProjectReferenceCatalogRefreshService(
            catalogCache,
            discovery,
            persistentStore,
            new InlineRefreshWorker());
        var planObserver = new BlockingFirstPlanObserver();
        await using var output = new MemoryStream();
        var lifecycle = new ReferenceCatalogRefreshCoordinator(
            catalogCache,
            refreshService,
            new VbaProjectManifestWorkspace(),
            new LspMessageTransport(Stream.Null, output),
            lifecycleObserver: null,
            planObserver: planObserver);
        await using var scheduler = new VbaInteractiveWorkScheduler();
        lifecycle.AttachScheduler(scheduler);

        lifecycle.ApplyManifestSelectionChange(
            uri,
            CreateManifestText("Library A"));
        await planObserver.FirstPlanStarted.Task
            .WaitAsync(TimeSpan.FromSeconds(5));
        lifecycle.DeactivateManifest(uri);
        planObserver.ReleaseFirstPlan();
        await lifecycle.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, persistentStore.LoadCount);
        Assert.Empty(discovery.ReferenceNames);
        await lifecycle.StopAsync();
        await scheduler.StopAsync(VbaInteractiveStopReason.Complete);
    }

    [Fact]
    public async Task Manifest_change_supersedes_a_source_plan_already_taken_by_the_mailbox()
    {
        var projectRoot =
            Directory.CreateTempSubdirectory("vba-ls-source-plan-change-").FullName;
        try
        {
            var sourcePath = WriteProject(projectRoot, "Library A");
            var sourceUri = new Uri(sourcePath).AbsoluteUri;
            var manifestUri = new Uri(
                Path.Combine(projectRoot, "vba-project.json")).AbsoluteUri;
            var catalogCache = new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.Empty);
            var discovery = new CountingDiscovery();
            var refreshService = new VbaProjectReferenceCatalogRefreshService(
                catalogCache,
                discovery,
                persistentStore: null,
                new InlineRefreshWorker());
            var planObserver = new BlockingFirstPlanObserver();
            await using var output = new MemoryStream();
            var lifecycle = new ReferenceCatalogRefreshCoordinator(
                catalogCache,
                refreshService,
                new VbaProjectManifestWorkspace(),
                new LspMessageTransport(Stream.Null, output),
                lifecycleObserver: null,
                planObserver: planObserver);
            await using var scheduler = new VbaInteractiveWorkScheduler();
            lifecycle.AttachScheduler(scheduler);

            try
            {
                lifecycle.ActivateProject(sourceUri);
                await planObserver.FirstPlanStarted.Task
                    .WaitAsync(TimeSpan.FromSeconds(5));

                lifecycle.ApplyManifestSelectionChange(
                    manifestUri,
                    CreateManifestText("Library B"));
                planObserver.ReleaseFirstPlan();
                await lifecycle.WaitForIdleAsync()
                    .WaitAsync(TimeSpan.FromSeconds(5));

                Assert.Equal(["Library B"], discovery.ReferenceNames);
            }
            finally
            {
                planObserver.ReleaseFirstPlan();
                await lifecycle.StopAsync();
                await scheduler.StopAsync(VbaInteractiveStopReason.Complete);
            }
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Manifest_change_invalidates_a_taken_source_scope_removed_from_the_manifest()
    {
        var projectRoot =
            Directory.CreateTempSubdirectory("vba-ls-source-scope-removal-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "Book1"));
            var book2SourceDirectory = Path.Combine(projectRoot, "src", "Book2");
            Directory.CreateDirectory(book2SourceDirectory);
            var manifestPath = Path.Combine(projectRoot, "vba-project.json");
            File.WriteAllText(
                manifestPath,
                CreateTwoDocumentManifestText(
                    "Disk Book1 Library",
                    "Removed Library"));
            var sourceUri = new Uri(
                Path.Combine(book2SourceDirectory, "Worker.bas")).AbsoluteUri;
            var manifestUri = new Uri(manifestPath).AbsoluteUri;
            var catalogCache = new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.Empty);
            var discovery = new CountingDiscovery();
            var refreshService = new VbaProjectReferenceCatalogRefreshService(
                catalogCache,
                discovery,
                persistentStore: null,
                new InlineRefreshWorker());
            var planObserver = new BlockingFirstPlanObserver();
            await using var output = new MemoryStream();
            var lifecycle = new ReferenceCatalogRefreshCoordinator(
                catalogCache,
                refreshService,
                new VbaProjectManifestWorkspace(),
                new LspMessageTransport(Stream.Null, output),
                lifecycleObserver: null,
                planObserver: planObserver);
            await using var scheduler = new VbaInteractiveWorkScheduler();
            lifecycle.AttachScheduler(scheduler);

            try
            {
                lifecycle.ActivateProject(sourceUri);
                await planObserver.FirstPlanStarted.Task
                    .WaitAsync(TimeSpan.FromSeconds(5));

                lifecycle.ApplyManifestSelectionChange(
                    manifestUri,
                    CreateManifestText("Retained Library"));
                planObserver.ReleaseFirstPlan();
                await lifecycle.WaitForIdleAsync()
                    .WaitAsync(TimeSpan.FromSeconds(5));

                Assert.Equal(["Retained Library"], discovery.ReferenceNames);
            }
            finally
            {
                planObserver.ReleaseFirstPlan();
                await lifecycle.StopAsync();
                await scheduler.StopAsync(VbaInteractiveStopReason.Complete);
            }
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Manifest_deactivation_rejects_a_source_plan_already_taken_by_the_mailbox()
    {
        var projectRoot =
            Directory.CreateTempSubdirectory("vba-ls-source-plan-deactivate-").FullName;
        try
        {
            var sourcePath = WriteProject(projectRoot, "Library A");
            var sourceUri = new Uri(sourcePath).AbsoluteUri;
            var manifestUri = new Uri(
                Path.Combine(projectRoot, "vba-project.json")).AbsoluteUri;
            var catalogCache = new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.Empty);
            var persistentStore = new CountingPersistentStore();
            var discovery = new CountingDiscovery();
            var refreshService = new VbaProjectReferenceCatalogRefreshService(
                catalogCache,
                discovery,
                persistentStore,
                new InlineRefreshWorker());
            var planObserver = new BlockingFirstPlanObserver();
            await using var output = new MemoryStream();
            var lifecycle = new ReferenceCatalogRefreshCoordinator(
                catalogCache,
                refreshService,
                new VbaProjectManifestWorkspace(),
                new LspMessageTransport(Stream.Null, output),
                lifecycleObserver: null,
                planObserver: planObserver);
            await using var scheduler = new VbaInteractiveWorkScheduler();
            lifecycle.AttachScheduler(scheduler);

            try
            {
                lifecycle.ActivateProject(sourceUri);
                await planObserver.FirstPlanStarted.Task
                    .WaitAsync(TimeSpan.FromSeconds(5));

                lifecycle.DeactivateManifest(manifestUri);
                planObserver.ReleaseFirstPlan();
                await lifecycle.WaitForIdleAsync()
                    .WaitAsync(TimeSpan.FromSeconds(5));

                Assert.Equal(0, persistentStore.LoadCount);
                Assert.Empty(discovery.ReferenceNames);
            }
            finally
            {
                planObserver.ReleaseFirstPlan();
                await lifecycle.StopAsync();
                await scheduler.StopAsync(VbaInteractiveStopReason.Complete);
            }
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Stale_scope_does_not_discard_a_fresh_peer_in_the_same_plan()
    {
        var projectRoot =
            Directory.CreateTempSubdirectory("vba-ls-scope-plan-fence-").FullName;
        try
        {
            var book1SourceDirectory = Path.Combine(projectRoot, "src", "Book1");
            Directory.CreateDirectory(book1SourceDirectory);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "Book2"));
            var manifestPath = Path.Combine(projectRoot, "vba-project.json");
            File.WriteAllText(
                manifestPath,
                CreateTwoDocumentManifestText(
                    "Latest Library",
                    "Disk Peer Library"));
            var sourceUri = new Uri(
                Path.Combine(book1SourceDirectory, "Worker.bas")).AbsoluteUri;
            var manifestUri = new Uri(manifestPath).AbsoluteUri;
            var catalogCache = new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.Empty);
            var discovery = new CountingDiscovery();
            var refreshService = new VbaProjectReferenceCatalogRefreshService(
                catalogCache,
                discovery,
                persistentStore: null,
                new InlineRefreshWorker());
            var planObserver = new BlockingFirstPlanObserver();
            await using var output = new MemoryStream();
            var lifecycle = new ReferenceCatalogRefreshCoordinator(
                catalogCache,
                refreshService,
                new VbaProjectManifestWorkspace(),
                new LspMessageTransport(Stream.Null, output),
                lifecycleObserver: null,
                planObserver: planObserver);
            await using var scheduler = new VbaInteractiveWorkScheduler();
            lifecycle.AttachScheduler(scheduler);

            try
            {
                lifecycle.ApplyManifestSelectionChange(
                    manifestUri,
                    CreateTwoDocumentManifestText(
                        "Stale Library",
                        "Fresh Peer Library"));
                await planObserver.FirstPlanStarted.Task
                    .WaitAsync(TimeSpan.FromSeconds(5));

                lifecycle.ActivateProject(sourceUri);
                planObserver.ReleaseFirstPlan();
                await lifecycle.WaitForIdleAsync()
                    .WaitAsync(TimeSpan.FromSeconds(5));

                Assert.Equal(
                    ["Fresh Peer Library", "Latest Library"],
                    discovery.ReferenceNames
                        .OrderBy(referenceName => referenceName, StringComparer.Ordinal)
                        .ToArray());
            }
            finally
            {
                planObserver.ReleaseFirstPlan();
                await lifecycle.StopAsync();
                await scheduler.StopAsync(VbaInteractiveStopReason.Complete);
            }
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Catalog_refresh_is_background_admitted_but_commit_waits_for_the_mutation_lane()
    {
        const string referenceName = "Library A";
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var discovery = new NonCooperativeBlockingDiscovery();
        var observer = new RecordingLifecycleObserver();
        var refreshService = new VbaProjectReferenceCatalogRefreshService(
            catalogCache,
            discovery,
            persistentStore: null,
            new InlineRefreshWorker(),
            observer);
        await using var output = new MemoryStream();
        var lifecycle = new ReferenceCatalogRefreshCoordinator(
            catalogCache,
            refreshService,
            new VbaProjectManifestWorkspace(),
            new LspMessageTransport(Stream.Null, output),
            observer);
        var timingSink = new SignallingTimingSink();
        await using var scheduler = new VbaInteractiveWorkScheduler(timingSink);
        lifecycle.AttachScheduler(scheduler);
        lifecycle.ApplyManifestSelectionChange(
            "file:///C:/work/Book1/vba-project.json",
            CreateManifestText(referenceName));
        await timingSink.WaitForAdmissionAsync("vba/referenceCatalogRefresh");
        await discovery.DiscoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var blockingMutationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlockingMutation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var blockingMutation = scheduler.AdmitMutation(
            "test/block-catalog-commit",
            async cancellationToken =>
            {
                blockingMutationStarted.TrySetResult();
                await releaseBlockingMutation.Task.WaitAsync(cancellationToken);
            });
        await blockingMutationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        discovery.ReleaseDiscovery();
        await timingSink.WaitForAdmissionAsync("vba/referenceCatalogCommit");

        Assert.Equal(
            VbaProjectReferenceCatalogSource.Unavailable,
            catalogCache.GetCatalogSource(referenceName));
        Assert.Equal(
            0,
            observer.Count(VbaProjectReferenceCatalogLifecycleOperation.Commit));

        releaseBlockingMutation.TrySetResult();
        await blockingMutation.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await lifecycle.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            VbaProjectReferenceCatalogSource.Generated,
            catalogCache.GetCatalogSource(referenceName));
        Assert.Equal(
            1,
            observer.Count(VbaProjectReferenceCatalogLifecycleOperation.Commit));
        await lifecycle.StopAsync();
        await scheduler.StopAsync(VbaInteractiveStopReason.Complete);
    }

    [Fact]
    public async Task Catalog_commit_waits_for_owned_capacity_without_losing_the_visible_commit()
    {
        await using var scheduler = new VbaInteractiveWorkScheduler(
            options: new VbaInteractiveWorkSchedulerOptions(
                CoalesceSupersededMutations: true,
                MaxOwnedWork: 1));
        var blockingMutationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlockingMutation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var blockingMutation = scheduler.AdmitMutation(
            "test/fill-owned-capacity",
            async cancellationToken =>
            {
                blockingMutationStarted.TrySetResult();
                await releaseBlockingMutation.Task.WaitAsync(cancellationToken);
            });
        await blockingMutationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var commitCount = 0;
        var mutationLane = new VbaInteractiveReferenceCatalogMutationLane(scheduler);

        var commit = mutationLane.CommitAsync(
            "project:Book1",
            () => commitCount++,
            CancellationToken.None);

        Assert.False(commit.IsCompleted);
        Assert.Equal(0, commitCount);
        releaseBlockingMutation.TrySetResult();
        await blockingMutation.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await commit.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, commitCount);
        await scheduler.StopAsync(VbaInteractiveStopReason.Complete);
    }

    [Fact]
    public async Task Cancelled_catalog_commit_capacity_wait_never_commits_late()
    {
        await using var scheduler = new VbaInteractiveWorkScheduler(
            options: new VbaInteractiveWorkSchedulerOptions(
                CoalesceSupersededMutations: true,
                MaxOwnedWork: 1));
        var blockerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocker = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = scheduler.AdmitMutation(async cancellationToken =>
        {
            blockerStarted.TrySetResult();
            await releaseBlocker.Task.WaitAsync(cancellationToken);
        });
        await blockerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var commitCount = 0;
        var mutationLane = new VbaInteractiveReferenceCatalogMutationLane(scheduler);
        using var cancellation = new CancellationTokenSource();
        var commit = mutationLane.CommitAsync(
            "project:Book1",
            () => commitCount++,
            cancellation.Token);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => commit);
        releaseBlocker.TrySetResult();
        await blocker.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        var later = scheduler.AdmitMutation(_ => Task.CompletedTask);
        await later.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, commitCount);
        await scheduler.StopAsync(VbaInteractiveStopReason.Complete);
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
            lifecycle.AttachScheduler(defaultScheduler);

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
        lifecycle.AttachScheduler(defaultScheduler);
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
        lifecycle.AttachScheduler(defaultScheduler);

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
        lifecycle.AttachScheduler(defaultScheduler);

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
        lifecycle.AttachScheduler(defaultScheduler);
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
    public async Task Manifest_replacement_does_not_block_the_mutation_lane_on_a_cancellation_callback()
    {
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var persistentStore = new BlockingCancellationCallbackPersistentStore();
        var refreshService = new VbaProjectReferenceCatalogRefreshService(
            catalogCache,
            new CountingDiscovery(),
            persistentStore,
            new InlineRefreshWorker());
        await using var output = new MemoryStream();
        var lifecycle = new ReferenceCatalogRefreshCoordinator(
            catalogCache,
            refreshService,
            new VbaProjectManifestWorkspace(),
            new LspMessageTransport(Stream.Null, output));
        await using var scheduler = new VbaInteractiveWorkScheduler();
        lifecycle.AttachScheduler(scheduler);
        const string manifestUri = "file:///C:/work/Book1/vba-project.json";

        lifecycle.ApplyManifestSelectionChange(
            manifestUri,
            CreateManifestText("Library A"));
        await persistentStore.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var replacement = scheduler.AdmitMutation(
            "test/replace-reference-selection",
            _ =>
            {
                lifecycle.ApplyManifestSelectionChange(
                    manifestUri,
                    CreateManifestText("Library B"));
                return Task.CompletedTask;
            });
        await persistentStore.CancellationCallbackStarted.Task
            .WaitAsync(TimeSpan.FromSeconds(5));
        var laterMutation = scheduler.AdmitMutation(_ => Task.CompletedTask);

        try
        {
            await replacement.Completion.WaitAsync(TimeSpan.FromMilliseconds(250));
            await laterMutation.Completion.WaitAsync(TimeSpan.FromMilliseconds(250));
        }
        finally
        {
            persistentStore.ReleaseCancellationCallback();
            await replacement.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            await laterMutation.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            await lifecycle.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await lifecycle.StopAsync();
            await scheduler.StopAsync(VbaInteractiveStopReason.Complete);
        }
    }

    [Fact]
    public async Task Manifest_deactivation_observes_a_throwing_cancellation_callback_off_the_mutation_lane()
    {
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var persistentStore = new ThrowingCancellationCallbackPersistentStore();
        var refreshService = new VbaProjectReferenceCatalogRefreshService(
            catalogCache,
            new CountingDiscovery(),
            persistentStore,
            new InlineRefreshWorker());
        await using var output = new MemoryStream();
        var lifecycle = new ReferenceCatalogRefreshCoordinator(
            catalogCache,
            refreshService,
            new VbaProjectManifestWorkspace(),
            new LspMessageTransport(Stream.Null, output));
        var schedulerFailures = new List<VbaInteractiveWorkFailure>();
        await using var scheduler = new VbaInteractiveWorkScheduler(
            failureSink: schedulerFailures.Add);
        lifecycle.AttachScheduler(scheduler);
        const string manifestUri = "file:///C:/work/Book1/vba-project.json";

        lifecycle.ApplyManifestSelectionChange(
            manifestUri,
            CreateManifestText("Library A"));
        await persistentStore.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var deactivation = scheduler.AdmitMutation(
            "test/deactivate-reference-selection",
            _ =>
            {
                lifecycle.DeactivateManifest(manifestUri);
                return Task.CompletedTask;
            });
        try
        {
            await deactivation.Completion.WaitAsync(TimeSpan.FromSeconds(1));
            var laterMutation = scheduler.AdmitMutation(_ => Task.CompletedTask);
            await laterMutation.Completion.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.True(scheduler.IsAccepting);
            Assert.Empty(schedulerFailures);
        }
        finally
        {
            await lifecycle.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await lifecycle.StopAsync();
            await scheduler.StopAsync(VbaInteractiveStopReason.Complete);
        }
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
        lifecycle.AttachScheduler(defaultScheduler);
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
        lifecycle.AttachScheduler(defaultScheduler);

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
        lifecycle.AttachScheduler(defaultScheduler);
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
            lifecycle.AttachScheduler(defaultScheduler);
            var pipeline = new VbaDocumentChangePipeline(
                workspace,
                lifecycle,
                CreateDiagnosticsPublisher(transport, workspace));

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
                    delayedP95 - baselineP95 <= TimeSpan.FromMilliseconds(10),
                    $"Expected delayed lifecycle p95 delta <= 10 ms, baseline={baselineP95.TotalMilliseconds:F6} ms, delayed={delayedP95.TotalMilliseconds:F6} ms.");
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

    [Fact]
    public async Task Position_request_timeout_includes_synchronous_workspace_capture()
    {
        var workspace = new BlockingInteractiveWorkspaceCapture();
        await using var output = new MemoryStream();
        var requestExecution = new VbaLspRequestExecution(
            new LspMessageTransport(Stream.Null, output),
            workspace);

        var request = Task.Run(
            () => ExecutePositionRequestAsync(
                requestExecution,
                requestId: 40_001,
                "textDocument/completion",
                "file:///C:/work/BlockedCapture.bas",
                line: 0,
                character: 0,
                timeout: TimeSpan.FromMilliseconds(100)));
        try
        {
            await workspace.CaptureStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(TimeSpan.FromMilliseconds(250));

            Assert.True(
                request.IsCompleted,
                "The request timeout did not include synchronous workspace capture.");
            await Assert.ThrowsAsync<TimeoutException>(() => request);
        }
        finally
        {
            workspace.Release();
        }
    }

    private static Task ExecutePositionRequestAsync(
        VbaLspRequestExecution requestExecution,
        int requestId,
        string method,
        string uri,
        int line,
        int character,
        TimeSpan? timeout = null)
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
        return Task.Run(async () =>
            {
                var capturedRequest = requestExecution.Capture(
                    request,
                    CancellationToken.None);
                await requestExecution.ExecuteAsync(
                    capturedRequest,
                    CancellationToken.None,
                    CancellationToken.None);
            })
            .WaitAsync(timeout ?? TimeSpan.FromSeconds(1));
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
        lifecycle.AttachScheduler(defaultScheduler);

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
        lifecycle.AttachScheduler(defaultScheduler);

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

    [Fact]
    public async Task Stop_is_bounded_when_cancellation_callback_cannot_return()
    {
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var persistentStore = new BlockingCancellationCallbackPersistentStore();
        var refreshService = new VbaProjectReferenceCatalogRefreshService(
            catalogCache,
            new CountingDiscovery(),
            persistentStore,
            new InlineRefreshWorker());
        await using var output = new MemoryStream();
        var lifecycle = new ReferenceCatalogRefreshCoordinator(
            catalogCache,
            refreshService,
            new VbaProjectManifestWorkspace(),
            new LspMessageTransport(Stream.Null, output));
        lifecycle.AttachScheduler(defaultScheduler);

        lifecycle.ApplyManifestSelectionChange(
            "file:///C:/work/Book1/vba-project.json",
            CreateManifestText("Blocked Library"));
        await persistentStore.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stop = Task.Run(lifecycle.StopAsync);
        try
        {
            await persistentStore.CancellationCallbackStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(1));
            await stop.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            persistentStore.ReleaseCancellationCallback();
            await lifecycle.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Stop_observes_a_throwing_cancellation_callback_without_faulting()
    {
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var persistentStore = new ThrowingCancellationCallbackPersistentStore();
        var refreshService = new VbaProjectReferenceCatalogRefreshService(
            catalogCache,
            new CountingDiscovery(),
            persistentStore,
            new InlineRefreshWorker());
        await using var output = new MemoryStream();
        var lifecycle = new ReferenceCatalogRefreshCoordinator(
            catalogCache,
            refreshService,
            new VbaProjectManifestWorkspace(),
            new LspMessageTransport(Stream.Null, output));
        lifecycle.AttachScheduler(defaultScheduler);

        lifecycle.ApplyManifestSelectionChange(
            "file:///C:/work/Book1/vba-project.json",
            CreateManifestText("Faulting Library"));
        await persistentStore.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await lifecycle.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }

    private VbaDiagnosticsPublisher CreateDiagnosticsPublisher(
        LspMessageTransport transport,
        VbaLanguageWorkspace workspace)
    {
        var publisher = new VbaDiagnosticsPublisher(transport, workspace);
        publisher.AttachScheduler(defaultScheduler);
        return publisher;
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
                new VbaFileSystemProjectDiskInventory(),
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
        => CreateTwoDocumentManifestText(referenceName, referenceName);

    private static string CreateTwoDocumentManifestText(
        string book1ReferenceName,
        string book2ReferenceName)
        => JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            projectName = "SharedLifecycleProject",
            primaryDocument = "Book1",
            documents = new Dictionary<string, object>
            {
                ["Book1"] = CreateDocument("src/Book1", book1ReferenceName),
                ["Book2"] = CreateDocument("src/Book2", book2ReferenceName)
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

    private sealed class BlockingFirstPlanReservationObserver
        : IReferenceCatalogRefreshPlanObserver
    {
        private readonly TaskCompletionSource releaseFirstPlan = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int observationCount;

        public TaskCompletionSource FirstPlanReserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void AfterPlanReservedBeforePost(string uri, long revision)
        {
            if (Interlocked.Increment(ref observationCount) != 1)
            {
                return;
            }

            FirstPlanReserved.TrySetResult();
            releaseFirstPlan.Task.GetAwaiter().GetResult();
        }

        public void BeforePlanCommit(string uri, long revision)
        {
        }

        public void ReleaseFirstPlan()
            => releaseFirstPlan.TrySetResult();
    }

    private sealed class BlockingFirstPlanObserver
        : IReferenceCatalogRefreshPlanObserver
    {
        private readonly TaskCompletionSource releaseFirstPlan = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int observationCount;

        public TaskCompletionSource FirstPlanStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void AfterPlanReservedBeforePost(string uri, long revision)
        {
        }

        public void BeforePlanCommit(string uri, long revision)
        {
            if (Interlocked.Increment(ref observationCount) != 1)
            {
                return;
            }

            FirstPlanStarted.TrySetResult();
            releaseFirstPlan.Task.GetAwaiter().GetResult();
        }

        public void ReleaseFirstPlan()
            => releaseFirstPlan.TrySetResult();
    }

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
        private readonly object gate = new();
        private readonly List<string> referenceNames = [];
        private int callCount;

        public int CallCount
        {
            get
            {
                lock (gate)
                {
                    return callCount;
                }
            }
        }

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

        public Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                callCount++;
                referenceNames.Add(referenceName);
            }

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

    private sealed class BlockingCancellationCallbackPersistentStore
        : IVbaProjectReferenceCatalogPersistentStore
    {
        private readonly TaskCompletionSource releaseCancellationCallback =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseLoad =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource LoadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationCallbackStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<VbaProjectReferenceCatalogPersistentLoadResult> LoadAsync(
            string referenceName,
            CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(() =>
            {
                CancellationCallbackStarted.TrySetResult();
                releaseCancellationCallback.Task.GetAwaiter().GetResult();
                releaseLoad.TrySetResult();
            });
            LoadStarted.TrySetResult();
            await releaseLoad.Task;
            cancellationToken.ThrowIfCancellationRequested();
            return VbaProjectReferenceCatalogPersistentLoadResult.Miss();
        }

        public Task SaveAsync(
            VbaProjectReferenceCatalogPersistentEntry entry,
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        public void ReleaseCancellationCallback()
            => releaseCancellationCallback.TrySetResult();
    }

    private sealed class ThrowingCancellationCallbackPersistentStore
        : IVbaProjectReferenceCatalogPersistentStore
    {
        public TaskCompletionSource LoadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<VbaProjectReferenceCatalogPersistentLoadResult> LoadAsync(
            string referenceName,
            CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(
                static () => throw new InvalidOperationException(
                    "Expected cancellation callback failure."));
            LoadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return VbaProjectReferenceCatalogPersistentLoadResult.Miss();
        }

        public Task SaveAsync(
            VbaProjectReferenceCatalogPersistentEntry entry,
            CancellationToken cancellationToken)
            => Task.CompletedTask;
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

        public long GetRevision(string authorityUri)
            => Version;

        public VbaProjectResolution Resolve(string activeUri)
        {
            ResolveCount++;
            return resolution;
        }
    }

    private sealed class BlockingInteractiveWorkspaceCapture
        : IVbaInteractiveWorkspaceCapture
    {
        private static readonly VbaSemanticInventory EmptyInventory =
            VbaSemanticInventory.Create(
                new Dictionary<string, VbaSourceDocument>(
                    StringComparer.OrdinalIgnoreCase));
        private readonly ManualResetEventSlim release = new();

        public TaskCompletionSource CaptureStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public VbaSemanticInventory CaptureProjectSemanticInventory(
            string activeUri,
            CancellationToken cancellationToken = default)
        {
            CaptureStarted.TrySetResult();
            release.Wait(cancellationToken);
            return EmptyInventory;
        }

        public IReadOnlyList<VbaSemanticInventory>
            CaptureWorkspaceSemanticInventories(
                CancellationToken cancellationToken = default)
            => [EmptyInventory];

        public VbaVersionedDocumentSnapshot? CaptureExactDocumentSnapshot(
            string uri,
            int expectedVersion,
            CancellationToken cancellationToken = default)
            => null;

        public void Release()
            => release.Set();
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

    private sealed class SignallingTimingSink : IVbaInteractiveWorkTimingSink
    {
        private readonly object gate = new();
        private readonly Dictionary<string, TaskCompletionSource> admissions =
            new(StringComparer.Ordinal);

        public Task WaitForAdmissionAsync(string method)
        {
            lock (gate)
            {
                if (!admissions.TryGetValue(method, out var signal))
                {
                    signal = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    admissions[method] = signal;
                }

                return signal.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        public void RecordAdmission(VbaInteractiveWorkAdmissionTiming timing)
        {
            lock (gate)
            {
                if (!admissions.TryGetValue(timing.Method, out var signal))
                {
                    signal = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    admissions[timing.Method] = signal;
                }

                signal.TrySetResult();
            }
        }

        public void RecordCompletion(VbaInteractiveWorkCompletionTiming timing)
        {
        }
    }
}
