using System.Diagnostics;
using System.Text.Json;
using VbaLanguageServer.Lsp;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Workspace;
using Xunit;
using Xunit.Abstractions;

namespace VbaLanguageServer.Tests;

[Collection(VbaDocumentAnalysisPerformanceTestCollection.Name)]
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
    public async Task Opening_source_activates_catalog_before_reserving_project_validation()
    {
        var sequence = new List<string>();
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.CreateBundled());
        var workspace = new VbaLanguageWorkspace(catalogCache);
        await using var output = new MemoryStream();
        var transport = new LspMessageTransport(Stream.Null, output);
        var lifecycle = new RecordingReferenceCatalogLifecycle(
            _ => sequence.Add("activate"));
        var publisher = new VbaDiagnosticsPublisher(
            transport,
            workspace,
            new RecordingProjectValidationReservationObserver(
                () => sequence.Add("reserve")));
        publisher.AttachScheduler(defaultScheduler);
        var pipeline = new VbaDocumentChangePipeline(
            workspace,
            lifecycle,
            publisher);
        const string uri = "file:///C:/work/Book1/Worker.bas";
        const string text = "Attribute VB_Name = \"Worker\"\nPublic Sub Run()\nEnd Sub";

        await pipeline.ApplyAsync(
            new VbaTextDocumentOpenedChange(uri, 1, text),
            CancellationToken.None);

        Assert.Equal(["activate", "reserve"], sequence);
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
            CreateManifestText("library a", "library b").Replace(
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
    public async Task Automatic_catalog_batch_without_a_commit_does_not_request_project_validation()
    {
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var refreshService = new VbaProjectReferenceCatalogRefreshService(
            catalogCache,
            new CountingDiscovery(),
            new CountingPersistentStore(),
            new InlineRefreshWorker());
        var manifestWorkspace = new VbaProjectManifestWorkspace();
        await using var output = new MemoryStream();
        var lifecycle = new ReferenceCatalogRefreshCoordinator(
            catalogCache,
            refreshService,
            manifestWorkspace,
            new LspMessageTransport(Stream.Null, output));
        var validationLifecycle = new RecordingProjectValidationLifecycleSink();
        lifecycle.AttachProjectValidationLifecycle(validationLifecycle);
        lifecycle.AttachScheduler(defaultScheduler);

        lifecycle.ApplyManifestSelectionChange(
            "file:///C:/work/Book1/vba-project.json",
            CreateManifestText("Library A", "Library B"));
        await lifecycle.WaitForIdleAsync();

        Assert.Empty(validationLifecycle.RefreshedAuthorities);
        Assert.Empty(validationLifecycle.InvalidatedAuthorities);
        await lifecycle.StopAsync();
    }

    [Fact]
    public async Task Stale_persisted_preload_refreshes_project_validation_before_discovery_settles()
    {
        const string referenceName = "Library A";
        var discovered = CreateDiscoverySuccess(referenceName);
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var persistentStore = new CountingPersistentStore
        {
            LoadResult = VbaProjectReferenceCatalogPersistentLoadResult.Stale(
                new VbaProjectReferenceCatalogPersistentEntry(
                    Assert.Single(discovered.Identities),
                    Assert.IsType<VbaProjectReferenceCatalog>(discovered.Catalog)),
                "Expected stale persisted catalog.")
        };
        var discovery = new NonCooperativeBlockingDiscovery();
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
        var validationLifecycle = new RecordingProjectValidationLifecycleSink();
        lifecycle.AttachProjectValidationLifecycle(validationLifecycle);
        lifecycle.AttachScheduler(defaultScheduler);

        lifecycle.ApplyManifestSelectionChange(
            "file:///C:/work/Book1/vba-project.json",
            CreateManifestText(referenceName));
        try
        {
            await discovery.DiscoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Single(validationLifecycle.InvalidatedAuthorities);
            Assert.Single(validationLifecycle.RefreshedAuthorities);
            Assert.False(discovery.DiscoveryCompleted.Task.IsCompleted);

            discovery.ReleaseDiscovery();
            await lifecycle.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(2, validationLifecycle.InvalidatedAuthorities.Count);
            Assert.Equal(2, validationLifecycle.RefreshedAuthorities.Count);
        }
        finally
        {
            discovery.ReleaseDiscovery();
            await lifecycle.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await lifecycle.StopAsync();
        }
    }

    [Fact]
    public async Task Catalog_commit_observer_runs_before_the_new_catalog_revision_is_visible()
    {
        const string referenceName = "Library A";
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var refreshService = new VbaProjectReferenceCatalogRefreshService(
            catalogCache,
            new SuccessfulDiscovery(),
            persistentStore: null,
            new InlineRefreshWorker());
        var observer = new CatalogCommitOrderingObserver(
            catalogCache,
            referenceName);
        refreshService.AttachCatalogCommitObserver(observer);

        await refreshService.RefreshAsync(CreateSelection(referenceName));

        Assert.Equal(
            VbaProjectReferenceCatalogSource.Unavailable,
            observer.SourceObservedAtCommit);
        Assert.Equal(
            VbaProjectReferenceCatalogSource.Generated,
            catalogCache.GetCatalogSource(referenceName));
        Assert.Equal(1, observer.CommitCount);
        Assert.Equal(1, observer.SettlementCount);
    }

    [Fact]
    public async Task Catalog_commit_during_project_currentness_check_rejects_older_snapshot()
    {
        const string referenceName = "Library A";
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-catalog-currentness-").FullName;
        try
        {
            var sourcePath = Path.Combine(projectRoot, "Worker.bas");
            File.WriteAllText(
                sourcePath,
                "Attribute VB_Name = \"Worker\"\nPublic Sub Run()\nEnd Sub\n");
            var sourceUri = new Uri(sourcePath).AbsoluteUri;
            Assert.True(VbaProjectIdentityModel.TryIdentifyDocument(
                sourceUri,
                out var sourceIdentity));
            var resolution = new VbaProjectResolution(
                VbaProjectResolutionKind.AdHoc,
                projectRoot,
                References: [new VbaProjectReference(referenceName)]);
            var resolutionSource =
                new BlockingScopeBarrierManifestResolutionSource(resolution);
            var catalogCache = new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.Empty);
            var provider = new VbaProjectSnapshotProvider(
                catalogCache,
                new VbaFileSystemProjectDiskInventory(),
                new VbaProjectSourceDocumentCache(),
                resolutionSource);
            var snapshot = provider.CreateProjectSnapshot(
                new VbaIdentifiedDocument(sourceIdentity, sourceUri),
                new VbaWorkspaceSnapshotState(
                    new Dictionary<VbaDocumentIdentity, VbaTrackedDocument>(),
                    new HashSet<VbaDocumentIdentity>(),
                    Version: 1),
                CancellationToken.None);
            var ownership = Assert.IsType<
                VbaProjectSnapshotProvider.ProjectSnapshotOwnership>(
                snapshot.DiagnosticsOwnership);
            resolutionSource.Arm();
            try
            {
                var currentness = Task.Run(
                    () => provider.IsCurrentProjectSnapshot(ownership));
                await resolutionSource.BarrierCaptureStarted.Task
                    .WaitAsync(TimeSpan.FromSeconds(5));
                catalogCache.Store(CreateDiscoverySuccess(referenceName));
                resolutionSource.Release();

                Assert.False(
                    await currentness.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            finally
            {
                resolutionSource.Release();
            }
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Project_validation_activation_precedes_immediate_catalog_commit_and_settlement()
    {
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var refreshService = new VbaProjectReferenceCatalogRefreshService(
            catalogCache,
            new SuccessfulDiscovery(),
            persistentStore: null,
            new InlineRefreshWorker());
        var admissionTiming = new ReferenceCatalogAdmissionTimingSink();
        await using var scheduler = new VbaInteractiveWorkScheduler(admissionTiming);
        var lifecycle = new ReferenceCatalogRefreshCoordinator(
            catalogCache,
            refreshService,
            new VbaProjectManifestWorkspace(),
            new LspMessageTransport(Stream.Null, Stream.Null));
        var validationLifecycle =
            new BlockingCatalogCallbackProjectValidationLifecycleSink();
        lifecycle.AttachProjectValidationLifecycle(validationLifecycle);
        lifecycle.AttachScheduler(scheduler);
        const string manifestUri = "file:///C:/work/Book1/vba-project.json";
        validationLifecycle.ArmActivation();

        var activation = Task.Run(
            () => lifecycle.ApplyManifestSelectionChange(
                manifestUri,
                CreateManifestText("Library A")));
        try
        {
            await validationLifecycle.ActivationStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(0, admissionTiming.ReferenceCatalogRefreshAdmissionCount);

            validationLifecycle.ReleaseActivation();
            await activation.WaitAsync(TimeSpan.FromSeconds(5));
            await lifecycle.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, admissionTiming.ReferenceCatalogRefreshAdmissionCount);
            var activeLease = validationLifecycle.CurrentLease;
            Assert.Same(
                activeLease,
                validationLifecycle.LastInvalidationAttempt);
            Assert.Same(
                activeLease,
                validationLifecycle.LastRefreshAttempt);
            Assert.Single(validationLifecycle.InvalidatedAuthorities);
            Assert.Single(validationLifecycle.RefreshedAuthorities);
        }
        finally
        {
            validationLifecycle.ReleaseActivation();
            await activation.WaitAsync(TimeSpan.FromSeconds(5));
            await lifecycle.StopAsync();
            await scheduler.StopAsync(VbaInteractiveStopReason.Complete);
        }
    }

    [Fact]
    public async Task Deactivation_cannot_retire_a_reserved_lease_before_its_activation()
    {
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var refreshService = new VbaProjectReferenceCatalogRefreshService(
            catalogCache,
            new CountingDiscovery(),
            persistentStore: null,
            new InlineRefreshWorker());
        var deactivationObserver = new ManifestDeactivationContentionObserver();
        var lifecycle = new ReferenceCatalogRefreshCoordinator(
            catalogCache,
            refreshService,
            new VbaProjectManifestWorkspace(),
            new LspMessageTransport(Stream.Null, Stream.Null),
            planObserver: deactivationObserver);
        var validationLifecycle =
            new BlockingCatalogCallbackProjectValidationLifecycleSink();
        lifecycle.AttachProjectValidationLifecycle(validationLifecycle);
        lifecycle.AttachScheduler(defaultScheduler);
        const string manifestUri = "file:///C:/work/Book1/vba-project.json";
        var manifestText = CreateManifestText("Library A");
        lifecycle.ApplyManifestSelectionChange(manifestUri, manifestText);
        await lifecycle.WaitForIdleAsync();
        var originalLease = validationLifecycle.CurrentLease;

        validationLifecycle.ArmActivation();
        var replacement = Task.Run(
            () => lifecycle.ApplyManifestSelectionChange(
                manifestUri,
                manifestText));
        Task? deactivation = null;
        try
        {
            await validationLifecycle.ActivationStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            deactivation = Task.Factory.StartNew(
                () => lifecycle.DeactivateManifest(manifestUri),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            await deactivationObserver.BlockedOnLifecyclePlanGate.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(validationLifecycle.RetirementStarted.Task.IsCompleted);

            validationLifecycle.ReleaseActivation();
            await replacement.WaitAsync(TimeSpan.FromSeconds(5));
            await deactivation.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(originalLease.IsRevoked);
            Assert.True(
                validationLifecycle.RetirementStarted.Task
                    .IsCompletedSuccessfully);
            Assert.Equal(0, validationLifecycle.ActiveLeaseCount);
        }
        finally
        {
            validationLifecycle.ReleaseActivation();
            await replacement.WaitAsync(TimeSpan.FromSeconds(5));
            if (deactivation is not null)
            {
                await deactivation.WaitAsync(TimeSpan.FromSeconds(5));
            }

            await lifecycle.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await lifecycle.StopAsync();
        }
    }

    [Fact]
    public async Task Scoped_catalog_commits_invalidate_the_owning_project_before_one_settled_refresh()
    {
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var refreshService = new VbaProjectReferenceCatalogRefreshService(
            catalogCache,
            new ScopedContextDiscoveryFactory(),
            persistentStore: null,
            new InlineRefreshWorker());
        await using var output = new MemoryStream();
        var lifecycle = new ReferenceCatalogRefreshCoordinator(
            catalogCache,
            refreshService,
            new VbaProjectManifestWorkspace(),
            new LspMessageTransport(Stream.Null, output));
        var validationLifecycle = new RecordingProjectValidationLifecycleSink();
        lifecycle.AttachProjectValidationLifecycle(validationLifecycle);
        lifecycle.AttachScheduler(defaultScheduler);

        lifecycle.ApplyManifestSelectionChange(
            "file:///C:/work/Book1/vba-project.json",
            CreateManifestText("Library A", "Library B"));
        await lifecycle.WaitForIdleAsync();

        var refreshedAuthority = Assert.Single(validationLifecycle.RefreshedAuthorities);
        Assert.Equal(
            [refreshedAuthority, refreshedAuthority],
            validationLifecycle.InvalidatedAuthorities);
        Assert.Equal(
            ["invalidate", "invalidate", "refresh"],
            validationLifecycle.Events);
        Assert.Empty(validationLifecycle.RetiredAuthorities);
        await lifecycle.StopAsync();
    }

    [Fact]
    public async Task Shared_catalog_commit_invalidates_every_current_project_that_selects_the_reference()
    {
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var refreshService = new VbaProjectReferenceCatalogRefreshService(
            catalogCache,
            new SuccessfulDiscovery(),
            persistentStore: null,
            new InlineRefreshWorker());
        await using var output = new MemoryStream();
        var lifecycle = new ReferenceCatalogRefreshCoordinator(
            catalogCache,
            refreshService,
            new VbaProjectManifestWorkspace(),
            new LspMessageTransport(Stream.Null, output));
        var validationLifecycle = new RecordingProjectValidationLifecycleSink();
        lifecycle.AttachProjectValidationLifecycle(validationLifecycle);
        lifecycle.AttachScheduler(defaultScheduler);

        lifecycle.ApplyManifestSelectionChange(
            "file:///C:/work/Shared/vba-project.json",
            CreateTwoDocumentManifestText("Shared Library"));
        await lifecycle.WaitForIdleAsync();

        Assert.Equal(2, validationLifecycle.InvalidatedAuthorities.Count);
        Assert.Equal(2, validationLifecycle.RefreshedAuthorities.Count);
        Assert.True(
            validationLifecycle.InvalidatedAuthorities
                .ToHashSet()
                .SetEquals(validationLifecycle.RefreshedAuthorities));
        Assert.Empty(validationLifecycle.RetiredAuthorities);
        await lifecycle.StopAsync();
    }

    [Fact]
    public async Task Shared_catalog_commit_while_a_new_project_lifecycle_is_pending_refreshes_that_project()
    {
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var refreshService = new VbaProjectReferenceCatalogRefreshService(
            catalogCache,
            new CountingDiscovery(),
            persistentStore: null,
            new InlineRefreshWorker());
        var planObserver = new BlockingFirstPlanObserver();
        var lifecycle = new ReferenceCatalogRefreshCoordinator(
            catalogCache,
            refreshService,
            new VbaProjectManifestWorkspace(),
            new LspMessageTransport(Stream.Null, Stream.Null),
            planObserver: planObserver);
        var validationLifecycle = new RecordingProjectValidationLifecycleSink();
        lifecycle.AttachProjectValidationLifecycle(validationLifecycle);
        lifecycle.AttachScheduler(defaultScheduler);
        const string referenceName = "Shared Library";

        lifecycle.ApplyManifestSelectionChange(
            "file:///C:/work/Pending/vba-project.json",
            CreateManifestText(referenceName));

        try
        {
            await planObserver.FirstPlanStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            var activatedAuthority =
                Assert.Single(validationLifecycle.ActivatedAuthorities);
            var observer = (IVbaProjectReferenceCatalogCommitObserver)lifecycle;
            var batch = new VbaProjectReferenceCatalogRefreshBatchIdentity(91);
            var commitAuthority =
                VbaProjectReferenceCatalogRefreshAuthorityIdentity.Create(
                    scope: null,
                    referenceName);

            observer.CatalogCommitAccepted(batch, commitAuthority);
            observer.CatalogRefreshSettled(batch);

            Assert.Equal(
                [activatedAuthority],
                validationLifecycle.InvalidatedAuthorities);
            Assert.Equal(
                [activatedAuthority],
                validationLifecycle.RefreshedAuthorities);
        }
        finally
        {
            planObserver.ReleaseFirstPlan();
            await lifecycle.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await lifecycle.StopAsync();
        }
    }

    [Fact]
    public async Task Later_shared_catalog_batch_refreshes_an_already_settled_sibling_it_invalidated()
    {
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var refreshService = new VbaProjectReferenceCatalogRefreshService(
            catalogCache,
            new FailingFirstSuccessfulSecondDiscovery(),
            persistentStore: null,
            new InlineRefreshWorker());
        await using var output = new MemoryStream();
        var lifecycle = new ReferenceCatalogRefreshCoordinator(
            catalogCache,
            refreshService,
            new VbaProjectManifestWorkspace(),
            new LspMessageTransport(Stream.Null, output));
        var validationLifecycle = new RecordingProjectValidationLifecycleSink();
        lifecycle.AttachProjectValidationLifecycle(validationLifecycle);
        lifecycle.AttachScheduler(defaultScheduler);

        lifecycle.ApplyManifestSelectionChange(
            "file:///C:/work/First/vba-project.json",
            CreateManifestText("Shared Library"));
        await lifecycle.WaitForIdleAsync();
        Assert.Empty(validationLifecycle.InvalidatedAuthorities);
        Assert.Empty(validationLifecycle.RefreshedAuthorities);

        lifecycle.ApplyManifestSelectionChange(
            "file:///C:/work/Second/vba-project.json",
            CreateManifestText("Shared Library"));
        await lifecycle.WaitForIdleAsync();

        Assert.Equal(2, validationLifecycle.InvalidatedAuthorities.Count);
        Assert.Equal(2, validationLifecycle.RefreshedAuthorities.Count);
        Assert.True(
            validationLifecycle.InvalidatedAuthorities
                .ToHashSet()
                .SetEquals(validationLifecycle.RefreshedAuthorities));
        await lifecycle.StopAsync();
    }

    [Fact]
    public async Task Earlier_batch_settlement_cannot_flush_authorities_dirtied_by_a_later_batch()
    {
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var lifecycle = new ReferenceCatalogRefreshCoordinator(
            catalogCache,
            new VbaProjectReferenceCatalogRefreshService(
                catalogCache,
                new CountingDiscovery(),
                persistentStore: null,
                new InlineRefreshWorker()),
            new VbaProjectManifestWorkspace(),
            new LspMessageTransport(Stream.Null, Stream.Null));
        var validationLifecycle = new RecordingProjectValidationLifecycleSink();
        lifecycle.AttachProjectValidationLifecycle(validationLifecycle);
        lifecycle.AttachScheduler(defaultScheduler);
        lifecycle.ApplyManifestSelectionChange(
            "file:///C:/work/Shared/vba-project.json",
            CreateTwoDocumentManifestText("Shared Library"));
        await lifecycle.WaitForIdleAsync();
        validationLifecycle.Clear();
        var observer = (IVbaProjectReferenceCatalogCommitObserver)lifecycle;
        var earlierBatch =
            new VbaProjectReferenceCatalogRefreshBatchIdentity(101);
        var laterBatch =
            new VbaProjectReferenceCatalogRefreshBatchIdentity(102);
        var authority = VbaProjectReferenceCatalogRefreshAuthorityIdentity.Create(
            scope: null,
            referenceName: "Shared Library");

        observer.CatalogCommitAccepted(earlierBatch, authority);
        observer.CatalogCommitAccepted(laterBatch, authority);
        observer.CatalogRefreshSettled(earlierBatch);

        Assert.Equal(4, validationLifecycle.InvalidatedAuthorities.Count);
        Assert.Empty(validationLifecycle.RefreshedAuthorities);

        observer.CatalogRefreshSettled(laterBatch);

        Assert.Equal(2, validationLifecycle.RefreshedAuthorities.Count);
        await lifecycle.StopAsync();
    }

    [Fact]
    public async Task Delayed_catalog_commit_callback_cannot_invalidate_a_newer_lifecycle()
    {
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var lifecycle = new ReferenceCatalogRefreshCoordinator(
            catalogCache,
            new VbaProjectReferenceCatalogRefreshService(
                catalogCache,
                new CountingDiscovery(),
                persistentStore: null,
                new InlineRefreshWorker()),
            new VbaProjectManifestWorkspace(),
            new LspMessageTransport(Stream.Null, Stream.Null));
        var validationLifecycle =
            new BlockingCatalogCallbackProjectValidationLifecycleSink();
        lifecycle.AttachProjectValidationLifecycle(validationLifecycle);
        lifecycle.AttachScheduler(defaultScheduler);
        const string manifestUri = "file:///C:/work/Book1/vba-project.json";
        var manifestText = CreateManifestText("Shared Library");
        lifecycle.ApplyManifestSelectionChange(manifestUri, manifestText);
        await lifecycle.WaitForIdleAsync();
        validationLifecycle.ClearEffects();

        var observer = (IVbaProjectReferenceCatalogCommitObserver)lifecycle;
        var staleBatch =
            new VbaProjectReferenceCatalogRefreshBatchIdentity(201);
        var commitAuthority =
            VbaProjectReferenceCatalogRefreshAuthorityIdentity.Create(
                scope: null,
                referenceName: "Shared Library");
        validationLifecycle.ArmInvalidation();
        var delayedCommit = Task.Run(
            () => observer.CatalogCommitAccepted(staleBatch, commitAuthority));
        try
        {
            await validationLifecycle.InvalidationStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            lifecycle.ApplyManifestSelectionChange(manifestUri, manifestText);
        }
        finally
        {
            validationLifecycle.ReleaseInvalidation();
        }

        await delayedCommit.WaitAsync(TimeSpan.FromSeconds(5));

        var staleLease = Assert.IsType<VbaProjectValidationLifecycleLease>(
            validationLifecycle.LastInvalidationAttempt);
        Assert.True(staleLease.IsRevoked);
        Assert.NotSame(staleLease, validationLifecycle.CurrentLease);
        Assert.Empty(validationLifecycle.InvalidatedAuthorities);

        var currentBatch =
            new VbaProjectReferenceCatalogRefreshBatchIdentity(202);
        observer.CatalogCommitAccepted(currentBatch, commitAuthority);

        Assert.Single(validationLifecycle.InvalidatedAuthorities);
        observer.CatalogRefreshSettled(staleBatch);
        observer.CatalogRefreshSettled(currentBatch);
        await lifecycle.StopAsync();
    }

    [Fact]
    public async Task Delayed_catalog_settlement_callback_cannot_refresh_a_newer_lifecycle()
    {
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var lifecycle = new ReferenceCatalogRefreshCoordinator(
            catalogCache,
            new VbaProjectReferenceCatalogRefreshService(
                catalogCache,
                new CountingDiscovery(),
                persistentStore: null,
                new InlineRefreshWorker()),
            new VbaProjectManifestWorkspace(),
            new LspMessageTransport(Stream.Null, Stream.Null));
        var validationLifecycle =
            new BlockingCatalogCallbackProjectValidationLifecycleSink();
        lifecycle.AttachProjectValidationLifecycle(validationLifecycle);
        lifecycle.AttachScheduler(defaultScheduler);
        const string manifestUri = "file:///C:/work/Book1/vba-project.json";
        var manifestText = CreateManifestText("Shared Library");
        lifecycle.ApplyManifestSelectionChange(manifestUri, manifestText);
        await lifecycle.WaitForIdleAsync();

        var observer = (IVbaProjectReferenceCatalogCommitObserver)lifecycle;
        var staleBatch =
            new VbaProjectReferenceCatalogRefreshBatchIdentity(211);
        var commitAuthority =
            VbaProjectReferenceCatalogRefreshAuthorityIdentity.Create(
                scope: null,
                referenceName: "Shared Library");
        observer.CatalogCommitAccepted(staleBatch, commitAuthority);
        validationLifecycle.ClearEffects();
        validationLifecycle.ArmRefresh();
        var delayedSettlement = Task.Run(
            () => observer.CatalogRefreshSettled(staleBatch));
        try
        {
            await validationLifecycle.RefreshStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            lifecycle.ApplyManifestSelectionChange(manifestUri, manifestText);
        }
        finally
        {
            validationLifecycle.ReleaseRefresh();
        }

        await delayedSettlement.WaitAsync(TimeSpan.FromSeconds(5));

        var staleLease = Assert.IsType<VbaProjectValidationLifecycleLease>(
            validationLifecycle.LastRefreshAttempt);
        Assert.True(staleLease.IsRevoked);
        Assert.NotSame(staleLease, validationLifecycle.CurrentLease);
        Assert.Empty(validationLifecycle.RefreshedAuthorities);

        var currentBatch =
            new VbaProjectReferenceCatalogRefreshBatchIdentity(212);
        observer.CatalogCommitAccepted(currentBatch, commitAuthority);
        observer.CatalogRefreshSettled(currentBatch);

        Assert.Single(validationLifecycle.RefreshedAuthorities);
        await lifecycle.StopAsync();
    }

    [Fact]
    public async Task Superseded_catalog_batch_settlement_does_not_refresh_a_newer_selection_for_the_same_authority()
    {
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var discovery = new SuccessfulFirstFailingSecondDiscovery();
        var persistentStore = new NonCooperativeBlockingSavePersistentStore();
        var lifecycle = new ReferenceCatalogRefreshCoordinator(
            catalogCache,
            new VbaProjectReferenceCatalogRefreshService(
                catalogCache,
                discovery,
                persistentStore,
                new InlineRefreshWorker()),
            new VbaProjectManifestWorkspace(),
            new LspMessageTransport(Stream.Null, Stream.Null));
        var validationLifecycle = new RecordingProjectValidationLifecycleSink();
        lifecycle.AttachProjectValidationLifecycle(validationLifecycle);
        lifecycle.AttachScheduler(defaultScheduler);
        const string manifestUri = "file:///C:/work/Book1/vba-project.json";

        lifecycle.ApplyManifestSelectionChange(
            manifestUri,
            CreateManifestText("Library A"));

        try
        {
            await persistentStore.SaveStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(validationLifecycle.InvalidatedAuthorities);
            validationLifecycle.Clear();

            lifecycle.ApplyManifestSelectionChange(
                manifestUri,
                CreateManifestText("Library B"));
            await discovery.SecondDiscoveryStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5));

            persistentStore.ReleaseSave();
            await lifecycle.WaitForIdleAsync();

            Assert.Empty(validationLifecycle.InvalidatedAuthorities);
            Assert.Empty(validationLifecycle.RefreshedAuthorities);
        }
        finally
        {
            persistentStore.ReleaseSave();
            await lifecycle.StopAsync();
        }
    }

    [Fact]
    public async Task Manifest_deactivation_retires_its_project_validation_authority()
    {
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var refreshService = new VbaProjectReferenceCatalogRefreshService(
            catalogCache,
            new CountingDiscovery(),
            persistentStore: null,
            new InlineRefreshWorker());
        await using var output = new MemoryStream();
        var lifecycle = new ReferenceCatalogRefreshCoordinator(
            catalogCache,
            refreshService,
            new VbaProjectManifestWorkspace(),
            new LspMessageTransport(Stream.Null, output));
        var validationLifecycle = new RecordingProjectValidationLifecycleSink();
        lifecycle.AttachProjectValidationLifecycle(validationLifecycle);
        lifecycle.AttachScheduler(defaultScheduler);
        const string manifestUri = "file:///C:/work/Book1/vba-project.json";

        lifecycle.ApplyManifestSelectionChange(
            manifestUri,
            CreateManifestText("Library A"));
        await lifecycle.WaitForIdleAsync();
        var activeAuthority = Assert.Single(
            validationLifecycle.ActivatedAuthorities.Distinct());
        validationLifecycle.Clear();

        lifecycle.DeactivateManifest(manifestUri);

        Assert.Equal(
            activeAuthority,
            Assert.Single(validationLifecycle.RetiredAuthorities));
        Assert.Empty(validationLifecycle.InvalidatedAuthorities);
        Assert.Empty(validationLifecycle.RefreshedAuthorities);
        await lifecycle.StopAsync();
    }

    [Fact]
    public async Task Delayed_manifest_retirement_cannot_retire_a_newer_reactivation()
    {
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var lifecycle = new ReferenceCatalogRefreshCoordinator(
            catalogCache,
            new VbaProjectReferenceCatalogRefreshService(
                catalogCache,
                new CountingDiscovery(),
                persistentStore: null,
                new InlineRefreshWorker()),
            new VbaProjectManifestWorkspace(),
            new LspMessageTransport(Stream.Null, Stream.Null));
        var validationLifecycle =
            new BlockingRetirementProjectValidationLifecycleSink();
        lifecycle.AttachProjectValidationLifecycle(validationLifecycle);
        lifecycle.AttachScheduler(defaultScheduler);
        const string manifestUri = "file:///C:/work/Book1/vba-project.json";
        var manifestText = CreateManifestText("Library A");

        lifecycle.ApplyManifestSelectionChange(manifestUri, manifestText);
        await lifecycle.WaitForIdleAsync();
        var authority = Assert.Single(validationLifecycle.ActiveAuthorities);

        Task? deactivation = null;
        try
        {
            deactivation = Task.Run(
                () => lifecycle.DeactivateManifest(manifestUri));
            await validationLifecycle.RetirementStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            lifecycle.ApplyManifestSelectionChange(manifestUri, manifestText);
            validationLifecycle.ReleaseRetirement();
            await deactivation.WaitAsync(TimeSpan.FromSeconds(5));
            await lifecycle.WaitForIdleAsync();

            Assert.Contains(authority, validationLifecycle.ActiveAuthorities);
            Assert.True(
                validationLifecycle.LatestActivationRevision
                > validationLifecycle.RetirementRevision);
            await lifecycle.StopAsync();
        }
        finally
        {
            validationLifecycle.ReleaseRetirement();
            if (deactivation is not null)
            {
                await deactivation.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    [Fact]
    public async Task Delayed_old_retirement_cannot_clear_a_newer_catalog_batch_refresh()
    {
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var persistentStore = new BlockingSavePersistentStore();
        var planObserver = new BlockingProjectValidationRetirementPlanObserver();
        var lifecycle = new ReferenceCatalogRefreshCoordinator(
            catalogCache,
            new VbaProjectReferenceCatalogRefreshService(
                catalogCache,
                new FailingFirstSuccessfulSecondDiscovery(),
                persistentStore,
                new InlineRefreshWorker()),
            new VbaProjectManifestWorkspace(),
            new LspMessageTransport(Stream.Null, Stream.Null),
            planObserver: planObserver);
        var validationLifecycle = new RecordingProjectValidationLifecycleSink();
        lifecycle.AttachProjectValidationLifecycle(validationLifecycle);
        lifecycle.AttachScheduler(defaultScheduler);
        const string manifestUri = "file:///C:/work/Book1/vba-project.json";
        var manifestText = CreateManifestText("Library A");

        lifecycle.ApplyManifestSelectionChange(manifestUri, manifestText);
        await lifecycle.WaitForIdleAsync();
        var authority = Assert.Single(
            validationLifecycle.ActivatedAuthorities.Distinct());
        validationLifecycle.Clear();

        planObserver.Arm();
        var deactivation = Task.Run(
            () => lifecycle.DeactivateManifest(manifestUri));
        try
        {
            await planObserver.RetirementReached.Task
                .WaitAsync(TimeSpan.FromSeconds(5));

            lifecycle.ApplyManifestSelectionChange(manifestUri, manifestText);
            await persistentStore.SaveStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(
                authority,
                Assert.Single(validationLifecycle.InvalidatedAuthorities));

            planObserver.ReleaseRetirement();
            await deactivation.WaitAsync(TimeSpan.FromSeconds(5));
            persistentStore.ReleaseSave();
            await lifecycle.WaitForIdleAsync();

            Assert.Equal(
                authority,
                Assert.Single(validationLifecycle.RefreshedAuthorities));
        }
        finally
        {
            planObserver.ReleaseRetirement();
            persistentStore.ReleaseSave();
            await deactivation.WaitAsync(TimeSpan.FromSeconds(5));
            await lifecycle.StopAsync();
        }
    }

    [Fact]
    public async Task Removing_one_manifest_document_retires_only_its_validation_authority()
    {
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var refreshService = new VbaProjectReferenceCatalogRefreshService(
            catalogCache,
            new CountingDiscovery(),
            persistentStore: null,
            new InlineRefreshWorker());
        await using var output = new MemoryStream();
        var lifecycle = new ReferenceCatalogRefreshCoordinator(
            catalogCache,
            refreshService,
            new VbaProjectManifestWorkspace(),
            new LspMessageTransport(Stream.Null, output));
        var validationLifecycle = new RecordingProjectValidationLifecycleSink();
        lifecycle.AttachProjectValidationLifecycle(validationLifecycle);
        lifecycle.AttachScheduler(defaultScheduler);
        const string manifestUri = "file:///C:/work/Shared/vba-project.json";

        lifecycle.ApplyManifestSelectionChange(
            manifestUri,
            CreateTwoDocumentManifestText("Library A", "Library B"));
        await lifecycle.WaitForIdleAsync();
        var activeAuthorities = validationLifecycle.ActivatedAuthorities
            .ToHashSet();
        Assert.Equal(2, activeAuthorities.Count);
        validationLifecycle.Clear();

        lifecycle.ApplyManifestSelectionChange(
            manifestUri,
            CreateSingleBook2ManifestText("Library B"));
        await lifecycle.WaitForIdleAsync();

        var retiredAuthority = Assert.Single(
            validationLifecycle.RetiredAuthorities);
        Assert.Contains(retiredAuthority, activeAuthorities);
        Assert.Empty(validationLifecycle.InvalidatedAuthorities);
        Assert.Empty(validationLifecycle.RefreshedAuthorities);
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

        var latestStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task? older = null;
        Task? latest = null;
        try
        {
            older = Task.Run(
                () => lifecycle.ApplyManifestSelectionChange(
                    uri,
                    CreateManifestText("Library A")));
            await planObserver.FirstPlanReserved.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            latest = Task.Run(
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
        finally
        {
            planObserver.ReleaseFirstPlan();
            releaseBlocker.TrySetResult();
            if (older is not null)
            {
                await older.WaitAsync(TimeSpan.FromSeconds(5));
            }
            if (latest is not null)
            {
                await latest.WaitAsync(TimeSpan.FromSeconds(5));
            }

            await blocker.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ReservedManifestReplacementRejectsOlderContextCommitBeforeReplacementPosts()
    {
        const string manifestUri = "file:///C:/work/ReservationFence/vba-project.json";
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var discovery = new BlockingFirstContextSuccessFactory();
        var refreshService = new VbaProjectReferenceCatalogRefreshService(
            catalogCache,
            discovery,
            persistentStore: null,
            new InlineRefreshWorker());
        var planObserver = new BlockingSecondPlanReservationObserver();
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
                CreateManifestText("Library A", "Library B"));
            await discovery.FirstDiscoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var replacement = Task.Run(() => lifecycle.ApplyManifestSelectionChange(
                manifestUri,
                CreateManifestText("Library B", "Library A")));
            await planObserver.SecondPlanReserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

            discovery.ReleaseFirstDiscovery();
            await lifecycle.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

            var manifestPath = Path.GetFullPath(@"C:\work\ReservationFence\vba-project.json");
            var selection = CreateSelection("Library A", "Library B");
            var supersededScope = CreateCatalogScope(
                manifestPath,
                "Book1",
                selection);
            var supersededState = catalogCache.CaptureSelectionState(
                selection.References,
                supersededScope);
            Assert.DoesNotContain(
                supersededState.CatalogSet.GetActiveDefinitions(selection),
                definition => definition.Name.StartsWith("Superseded", StringComparison.Ordinal));

            planObserver.ReleaseSecondPlan();
            await replacement.WaitAsync(TimeSpan.FromSeconds(5));
            await lifecycle.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

            var currentScope = CreateCatalogScope(
                manifestPath,
                "Book1",
                CreateSelection("Library B", "Library A"));
            var currentState = catalogCache.CaptureSelectionState(
                selection.References,
                currentScope);
            Assert.Contains(
                currentState.CatalogSet.GetActiveDefinitions(selection),
                definition => definition.Name == "CurrentLibraryAType");
            Assert.Contains(
                currentState.CatalogSet.GetActiveDefinitions(selection),
                definition => definition.Name == "CurrentLibraryBType");
        }
        finally
        {
            discovery.ReleaseFirstDiscovery();
            planObserver.ReleaseSecondPlan();
            await lifecycle.StopAsync();
            await scheduler.StopAsync(VbaInteractiveStopReason.Complete);
        }
    }

    [Fact]
    public async Task ReservedManifestReplacementRejectsRemovedContextCommitBeforeReplacementPosts()
    {
        const string manifestUri = "file:///C:/work/RemovedReservationFence/vba-project.json";
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var discovery = new BlockingFirstContextSuccessFactory("Book1");
        var refreshService = new VbaProjectReferenceCatalogRefreshService(
            catalogCache,
            discovery,
            persistentStore: null,
            new InlineRefreshWorker());
        var planObserver = new BlockingSecondPlanReservationObserver();
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
                CreateTwoDocumentManifestText("Removed Library", "Retained Library"));
            await discovery.FirstDiscoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var replacement = Task.Run(() => lifecycle.ApplyManifestSelectionChange(
                manifestUri,
                CreateSingleBook2ManifestText("Retained Library")));
            await planObserver.SecondPlanReserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

            discovery.ReleaseFirstDiscovery();
            await lifecycle.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

            var manifestPath = Path.GetFullPath(
                @"C:\work\RemovedReservationFence\vba-project.json");
            var removedSelection = CreateSelection("Removed Library");
            var removedScope = CreateCatalogScope(
                manifestPath,
                "Book1",
                removedSelection);
            var removedState = catalogCache.CaptureSelectionState(
                removedSelection.References,
                removedScope);
            Assert.DoesNotContain(
                removedState.CatalogSet.GetActiveDefinitions(removedSelection),
                definition => definition.Name == "SupersededRemovedLibraryType");

            planObserver.ReleaseSecondPlan();
            await replacement.WaitAsync(TimeSpan.FromSeconds(5));
            await lifecycle.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            discovery.ReleaseFirstDiscovery();
            planObserver.ReleaseSecondPlan();
            await lifecycle.StopAsync();
            await scheduler.StopAsync(VbaInteractiveStopReason.Complete);
        }
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

        try
        {
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
        finally
        {
            planObserver.ReleaseFirstPlan();
        }
    }

    [Fact]
    public async Task Manifest_deactivation_with_an_unidentifiable_authority_fails_closed()
    {
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var refreshService = new VbaProjectReferenceCatalogRefreshService(
            catalogCache,
            new CountingDiscovery(),
            persistentStore: null,
            new InlineRefreshWorker());
        await using var output = new MemoryStream();
        var lifecycle = new ReferenceCatalogRefreshCoordinator(
            catalogCache,
            refreshService,
            new VbaProjectManifestWorkspace(),
            new LspMessageTransport(Stream.Null, output));

        lifecycle.DeactivateManifest("\0");

        await lifecycle.StopAsync();
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
        var blockingMutationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlockingMutation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        VbaInteractiveWorkAdmission? blockingMutation = null;
        try
        {
            lifecycle.ApplyManifestSelectionChange(
                "file:///C:/work/Book1/vba-project.json",
                CreateManifestText(referenceName));
            await timingSink.WaitForAdmissionAsync("vba/referenceCatalogRefresh");
            await discovery.DiscoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            blockingMutation = scheduler.AdmitMutation(
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
            await blockingMutation.Value.Completion.WaitAsync(TimeSpan.FromSeconds(5));
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
        finally
        {
            discovery.ReleaseDiscovery();
            releaseBlockingMutation.TrySetResult();
            if (blockingMutation is { } admittedMutation)
            {
                await admittedMutation.Completion.WaitAsync(
                    TimeSpan.FromSeconds(5));
            }
        }
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
            VbaProjectReferenceCatalogRefreshAuthorityIdentity.Create(
                scope: null,
                "project:Book1"),
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
            VbaProjectReferenceCatalogRefreshAuthorityIdentity.Create(
                scope: null,
                "project:Book1"),
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
    public async Task Companion_supply_refreshes_an_open_project_even_when_its_selection_is_unchanged()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-companion-refresh-").FullName;
        try
        {
            var sourcePath = WriteProject(projectRoot, "Generated Library");
            var sourceUri = new Uri(sourcePath).AbsoluteUri;
            var catalogCache = new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.Empty);
            var discovery = new CountingDiscovery();
            var refreshService = new VbaProjectReferenceCatalogRefreshService(
                catalogCache,
                discovery,
                new CountingPersistentStore(),
                new InlineRefreshWorker());
            await using var output = new MemoryStream();
            var lifecycle = new ReferenceCatalogRefreshCoordinator(
                catalogCache,
                refreshService,
                new VbaProjectManifestWorkspace(),
                new LspMessageTransport(Stream.Null, output));
            lifecycle.AttachScheduler(defaultScheduler);

            lifecycle.ActivateProject(sourceUri);
            await lifecycle.WaitForIdleAsync();
            lifecycle.RefreshActiveProjects([sourceUri]);
            await lifecycle.WaitForIdleAsync();

            Assert.Equal(2, discovery.CallCount);
            await lifecycle.StopAsync();
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Stale_companion_refresh_cannot_replace_a_newer_manifest_selection()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-stale-companion-refresh-").FullName;
        var sourceUri = new Uri(WriteProject(projectRoot, "Library A")).AbsoluteUri;
        var manifestPath = Path.Combine(projectRoot, "vba-project.json");
        var manifestUri = new Uri(manifestPath).AbsoluteUri;
        var initialManifest = CreateManifestText("Library A");
        var changedManifest = CreateManifestText("Library B");
        var manifestWorkspace = new VbaProjectManifestWorkspace();
        var initialUpdate = manifestWorkspace.OpenManifest(
            manifestUri,
            documentVersion: 1,
            initialManifest);
        Assert.True(initialUpdate.Accepted);
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var discovery = new CountingDiscovery();
        var refreshService = new VbaProjectReferenceCatalogRefreshService(
            catalogCache,
            discovery,
            new CountingPersistentStore(),
            new InlineRefreshWorker());
        await using var output = new MemoryStream();
        var lifecycle = new ReferenceCatalogRefreshCoordinator(
            catalogCache,
            refreshService,
            manifestWorkspace,
            new LspMessageTransport(Stream.Null, output));
        lifecycle.AttachScheduler(defaultScheduler);
        var activeUris = new BlockingAfterFirstUriList(sourceUri);
        var staleRefresh = Task.Factory.StartNew(
            () => lifecycle.RefreshActiveProjects(activeUris),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        try
        {
            await activeUris.FirstUriEnumerated.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var changedUpdate = manifestWorkspace.ChangeManifest(
                manifestUri,
                documentVersion: 2,
                changedManifest);
            Assert.True(changedUpdate.Accepted);
            Assert.True(changedUpdate.EffectiveChanged);
            lifecycle.ApplyManifestSelectionChange(manifestUri, changedManifest);
            await lifecycle.WaitForIdleAsync();

            Assert.Equal(["Library B"], discovery.ReferenceNames);

            activeUris.Release();
            await staleRefresh.WaitAsync(TimeSpan.FromSeconds(5));
            await lifecycle.WaitForIdleAsync();

            Assert.Equal(["Library B"], discovery.ReferenceNames);
        }
        finally
        {
            activeUris.Release();
            await lifecycle.StopAsync();
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Project_reopened_after_companion_pin_replaces_its_registry_only_lifecycle()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-reopened-companion-refresh-").FullName;
        try
        {
            var sourceUri = new Uri(
                WriteProject(projectRoot, "Library A")).AbsoluteUri;
            var catalogCache = new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.Empty);
            var registryDiscovery = new CountingDiscovery();
            var companionDiscovery = new RecordingContextDiscoveryFactory();
            var sessionDiscovery =
                new SessionPinnedVbaDevReferenceCatalogDiscovery(
                    registryDiscovery,
                    _ => companionDiscovery);
            var refreshService = new VbaProjectReferenceCatalogRefreshService(
                catalogCache,
                sessionDiscovery,
                new CountingPersistentStore(),
                new InlineRefreshWorker());
            await using var output = new MemoryStream();
            var lifecycle = new ReferenceCatalogRefreshCoordinator(
                catalogCache,
                refreshService,
                new VbaProjectManifestWorkspace(),
                new LspMessageTransport(Stream.Null, output));
            lifecycle.AttachScheduler(defaultScheduler);

            lifecycle.ActivateProject(sourceUri);
            await lifecycle.WaitForIdleAsync();
            Assert.Equal(1, registryDiscovery.CallCount);
            Assert.Empty(companionDiscovery.Contexts);

            var companionHandler = new VbaCompanionExecutableNotificationHandler(
                sessionDiscovery,
                static () => [],
                lifecycle);
            Assert.True(companionHandler.TryApply(
                new VbaCompanionExecutableUpdate(
                    Path.GetFullPath("vba-dev.exe"))));
            await lifecycle.WaitForIdleAsync();
            Assert.Empty(companionDiscovery.Contexts);

            lifecycle.ActivateProject(sourceUri);
            await lifecycle.WaitForIdleAsync();

            Assert.Single(companionDiscovery.Contexts);
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
    public async Task AutomaticLifecycleSuppliesCanonicalProjectAndDocumentToContextDiscovery()
    {
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var discovery = new RecordingContextDiscoveryFactory();
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
        const string manifestUri = "file:///C:/work/ContextProject/vba-project.json";

        lifecycle.ApplyManifestSelectionChange(
            manifestUri,
            CreateManifestText("Ambiguous Library"));
        await lifecycle.WaitForIdleAsync();

        var context = Assert.Single(discovery.Contexts);
        Assert.Equal(Path.GetFullPath(@"C:\work\ContextProject"), context.ProjectPath);
        Assert.Equal("Book1", context.DocumentName);
        Assert.Equal("Ambiguous Library", Assert.Single(context.Selection.References).Name);
        await lifecycle.StopAsync();
    }

    [Fact]
    public async Task EqualFingerprintsKeepContextSpecificDiscoverySeparatePerDocument()
    {
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var discovery = new RecordingContextDiscoveryFactory();
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
            "file:///C:/work/ContextProject/vba-project.json",
            CreateTwoDocumentManifestText("Ambiguous Library"));
        await lifecycle.WaitForIdleAsync();

        Assert.Equal(
            ["Book1", "Book2"],
            discovery.Contexts
                .Select(context => context.DocumentName)
                .OrderBy(name => name, StringComparer.Ordinal));
        await lifecycle.StopAsync();
    }

    [Fact]
    public async Task ContextResolvedCatalogBindingsRemainIsolatedPerManifestDocument()
    {
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var discovery = new ScopedContextDiscoveryFactory();
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
        const string manifestUri = "file:///C:/work/ScopedProject/vba-project.json";
        lifecycle.ApplyManifestSelectionChange(
            manifestUri,
            CreateTwoDocumentManifestText("Ambiguous Library"));
        await lifecycle.WaitForIdleAsync();

        var manifestPath = Path.GetFullPath(@"C:\work\ScopedProject\vba-project.json");
        var selection = CreateSelection("Ambiguous Library");
        var book1Scope = CreateCatalogScope(
            manifestPath,
            "Book1",
            selection);
        var book2Scope = CreateCatalogScope(
            manifestPath,
            "Book2",
            selection);
        var book1State = catalogCache.CaptureSelectionState(
            selection.References,
            book1Scope);
        var book2State = catalogCache.CaptureSelectionState(
            selection.References,
            book2Scope);

        Assert.Contains(
            book1State.CatalogSet.GetActiveDefinitions(selection),
            definition => definition.Name == "Book1ResolvedType");
        Assert.DoesNotContain(
            book1State.CatalogSet.GetActiveDefinitions(selection),
            definition => definition.Name == "Book2ResolvedType");
        Assert.Contains(
            book2State.CatalogSet.GetActiveDefinitions(selection),
            definition => definition.Name == "Book2ResolvedType");
        Assert.DoesNotContain(
            book2State.CatalogSet.GetActiveDefinitions(selection),
            definition => definition.Name == "Book1ResolvedType");
        Assert.Equal(
            "11111111-1111-1111-1111-111111111111",
            Assert.Single(book1State.Identities).Value.Guid);
        Assert.Equal(
            "22222222-2222-2222-2222-222222222222",
            Assert.Single(book2State.Identities).Value.Guid);
        await lifecycle.StopAsync();
    }

    [Fact]
    public async Task ManifestReferenceReorderRefreshesSuccessfulContextCatalogForNewFingerprint()
    {
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var discovery = new BlockingFirstContextSuccessFactory();
        discovery.ReleaseFirstDiscovery();
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
        const string manifestUri = "file:///C:/work/Reordered/vba-project.json";

        lifecycle.ApplyManifestSelectionChange(
            manifestUri,
            CreateManifestText("Library A", "Library B"));
        await lifecycle.WaitForIdleAsync();
        var manifestPath = Path.GetFullPath(@"C:\work\Reordered\vba-project.json");
        var selection = CreateSelection("Library A", "Library B");
        var initialScope = CreateCatalogScope(
            manifestPath,
            "Book1",
            selection);
        var initialState = catalogCache.CaptureSelectionState(
            selection.References,
            initialScope);
        Assert.Contains(
            initialState.CatalogSet.GetActiveDefinitions(selection),
            definition => definition.Name == "SupersededLibraryAType");
        Assert.Contains(
            initialState.CatalogSet.GetActiveDefinitions(selection),
            definition => definition.Name == "SupersededLibraryBType");

        lifecycle.ApplyManifestSelectionChange(
            manifestUri,
            CreateManifestText("Library B", "Library A"));
        await lifecycle.WaitForIdleAsync();

        var reorderedSelection = CreateSelection("Library B", "Library A");
        var reorderedScope = CreateCatalogScope(
            manifestPath,
            "Book1",
            reorderedSelection);
        var reorderedState = catalogCache.CaptureSelectionState(
            selection.References,
            reorderedScope);
        var reorderedDefinitions = reorderedState.CatalogSet.GetActiveDefinitions(selection);
        Assert.Contains(
            reorderedDefinitions,
            definition => definition.Name == "CurrentLibraryAType");
        Assert.Contains(
            reorderedDefinitions,
            definition => definition.Name == "CurrentLibraryBType");
        Assert.DoesNotContain(
            reorderedDefinitions,
            definition => definition.Name.StartsWith("Superseded", StringComparison.Ordinal));
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

        VbaInteractiveWorkAdmission? replacement = null;
        VbaInteractiveWorkAdmission? laterMutation = null;
        try
        {
            lifecycle.ApplyManifestSelectionChange(
                manifestUri,
                CreateManifestText("Library A"));
            await persistentStore.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            replacement = scheduler.AdmitMutation(
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
            laterMutation = scheduler.AdmitMutation(_ => Task.CompletedTask);

            await replacement.Value.Completion.WaitAsync(
                TimeSpan.FromSeconds(5));
            await laterMutation.Value.Completion.WaitAsync(
                TimeSpan.FromSeconds(5));
        }
        finally
        {
            persistentStore.ReleaseCancellationCallback();
            if (replacement is { } admittedReplacement)
            {
                await admittedReplacement.Completion.WaitAsync(
                    TimeSpan.FromSeconds(5));
            }
            if (laterMutation is { } admittedLaterMutation)
            {
                await admittedLaterMutation.Completion.WaitAsync(
                    TimeSpan.FromSeconds(5));
            }

            await lifecycle.StopAsync();
            await lifecycle.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
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

        try
        {
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
        }
        finally
        {
            discovery.ReleaseCancellationCleanup();
            await lifecycle.StopAsync();
            await lifecycle.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
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

        try
        {
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
        }
        finally
        {
            discovery.ReleaseCancellationCleanup();
            await lifecycle.StopAsync();
            await lifecycle.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
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
            var completed = await Task.WhenAny(
                request,
                Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(request, completed);
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
    public async Task Stop_keeps_observing_bounded_companion_cleanup_past_the_registry_only_window()
    {
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        var discovery = new BoundedCancellationCleanupDiscovery();
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
            CreateManifestText("Bounded Cleanup Library"));
        await discovery.DiscoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stop = lifecycle.StopAsync();
        await discovery.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(TimeSpan.FromMilliseconds(1_100));

        try
        {
            Assert.False(stop.IsCompleted);
        }
        finally
        {
            discovery.CompleteCleanup();
        }

        await stop.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(discovery.CleanupCompleted.Task.IsCompletedSuccessfully);
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

        Task? stop = null;
        try
        {
            lifecycle.ApplyManifestSelectionChange(
                "file:///C:/work/Book1/vba-project.json",
                CreateManifestText("Blocked Library"));
            await discovery.DiscoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            stop = lifecycle.StopAsync();
            await stop.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(discovery.DiscoveryCompleted.Task.IsCompleted);
        }
        finally
        {
            discovery.ReleaseDiscovery();
            stop ??= lifecycle.StopAsync();
            await lifecycle.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
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

        Task? stop = null;
        try
        {
            lifecycle.ApplyManifestSelectionChange(
                "file:///C:/work/Book1/vba-project.json",
                CreateManifestText("Blocked Library"));
            await persistentStore.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            stop = Task.Run(lifecycle.StopAsync);
            await persistentStore.CancellationCallbackStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(1));
            await stop.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            persistentStore.ReleaseCancellationCallback();
            stop ??= Task.Run(lifecycle.StopAsync);
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
                new Dictionary<VbaDocumentIdentity, VbaTrackedDocument>(),
                new HashSet<VbaDocumentIdentity>(),
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
                beforeA.DiagnosticsOwnership?.CacheIdentity,
                afterA.DiagnosticsOwnership?.CacheIdentity);
            Assert.Equal(
                beforeA.DiagnosticsOwnership?.ActiveDocumentIdentity,
                afterA.DiagnosticsOwnership?.ActiveDocumentIdentity);
            Assert.True(
                VbaProjectIdentityModel.TryIdentifyAuthority(
                    beforeA.Resolution,
                    out var beforeAuthority));
            Assert.True(
                VbaProjectIdentityModel.TryIdentifyAuthority(
                    afterA.Resolution,
                    out var afterAuthority));
            Assert.Equal(beforeAuthority, afterAuthority);
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
                    commonModules = Array.Empty<object>(),
                    references = referenceNames
                        .Select(referenceName => new { name = referenceName, requested = true })
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

    private static string CreateSingleBook2ManifestText(string referenceName)
        => JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            projectName = "SharedLifecycleProject",
            primaryDocument = "Book2",
            documents = new Dictionary<string, object>
            {
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
            commonModules = Array.Empty<object>(),
            references = referenceNames
                .Select(referenceName => new { name = referenceName, requested = true })
                .ToArray()
        };

    private static VbaProjectReferenceSelection CreateSelection(params string[] referenceNames)
        => VbaProjectReferenceSelection.Create(
            ProjectDocument.ExcelKind,
            referenceNames
                .Select(referenceName => new VbaProjectReference(referenceName))
                .ToArray());

    private static VbaProjectReferenceCatalogScopeIdentity CreateCatalogScope(
        string manifestPath,
        string documentName,
        VbaProjectReferenceSelection selection)
    {
        var resolution = new VbaProjectResolution(
            VbaProjectResolutionKind.ManifestDocument,
            Path.GetDirectoryName(manifestPath)!,
            manifestPath,
            documentName,
            ProjectDocument.ExcelKind,
            selection.References);
        Assert.True(
            VbaProjectReferenceCatalogScopeIdentity.TryCreate(
                resolution,
                out var scope));
        return scope;
    }

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

    private sealed class BlockingAfterFirstUriList(string uri)
        : IReadOnlyList<string>
    {
        private readonly TaskCompletionSource release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstUriEnumerated { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int Count => 1;

        public string this[int index]
            => index == 0
                ? uri
                : throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<string> GetEnumerator()
        {
            yield return uri;
            FirstUriEnumerated.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            => GetEnumerator();

        public void Release()
            => release.TrySetResult();
    }

    private sealed class ManifestDeactivationContentionObserver
        : IReferenceCatalogRefreshPlanObserver
    {
        public TaskCompletionSource BlockedOnLifecyclePlanGate { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void AfterPlanReservedBeforePost(string uri, long revision)
        {
        }

        public void BeforePlanCommit(string uri, long revision)
        {
        }

        public void AfterManifestDeactivationBlockedOnLifecyclePlan(string uri)
            => BlockedOnLifecyclePlanGate.TrySetResult();
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

    private sealed class BlockingSecondPlanReservationObserver
        : IReferenceCatalogRefreshPlanObserver
    {
        private readonly TaskCompletionSource releaseSecondPlan = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int observationCount;

        public TaskCompletionSource SecondPlanReserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void AfterPlanReservedBeforePost(string uri, long revision)
        {
            if (Interlocked.Increment(ref observationCount) != 2)
            {
                return;
            }

            SecondPlanReserved.TrySetResult();
            releaseSecondPlan.Task.GetAwaiter().GetResult();
        }

        public void BeforePlanCommit(string uri, long revision)
        {
        }

        public void ReleaseSecondPlan()
            => releaseSecondPlan.TrySetResult();
    }

    private sealed class BlockingProjectValidationRetirementPlanObserver
        : IReferenceCatalogRefreshPlanObserver
    {
        private readonly TaskCompletionSource releaseRetirement = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int armed;

        public TaskCompletionSource RetirementReached { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void AfterPlanReservedBeforePost(string uri, long revision)
        {
        }

        public void BeforePlanCommit(string uri, long revision)
        {
        }

        public void BeforeProjectValidationRetirement(long lifecycleRevision)
        {
            if (Volatile.Read(ref armed) == 0)
            {
                return;
            }

            RetirementReached.TrySetResult();
            releaseRetirement.Task.GetAwaiter().GetResult();
        }

        public void Arm()
            => Volatile.Write(ref armed, 1);

        public void ReleaseRetirement()
            => releaseRetirement.TrySetResult();
    }

    private sealed class RecordingReferenceCatalogLifecycle : IReferenceCatalogLifecycle
    {
        private readonly Action<string>? onActivate;

        public RecordingReferenceCatalogLifecycle(Action<string>? onActivate = null)
        {
            this.onActivate = onActivate;
        }

        public int ProjectActivationCount { get; private set; }

        public int ManifestSelectionChangeCount { get; private set; }

        public void ActivateProject(string uri)
        {
            ProjectActivationCount++;
            onActivate?.Invoke(uri);
        }

        public void ApplyManifestSelectionChange(string uri, string text)
            => ManifestSelectionChangeCount++;

        public void DeactivateManifest(string uri)
        {
        }

    }

    private sealed class RecordingProjectValidationReservationObserver(
        Action onReservation)
        : IVbaDiagnosticsPublicationObserver
    {
        public void AfterRevisionReserved(string uri, long revision)
        {
        }

        public void AfterProjectValidationRevisionReserved(
            VbaProjectAuthorityIdentity authority,
            long revision)
            => onReservation();
    }

    private sealed class ReferenceCatalogAdmissionTimingSink
        : IVbaInteractiveWorkTimingSink
    {
        private int referenceCatalogRefreshAdmissionCount;

        public int ReferenceCatalogRefreshAdmissionCount =>
            Volatile.Read(ref referenceCatalogRefreshAdmissionCount);

        public void RecordAdmission(VbaInteractiveWorkAdmissionTiming timing)
        {
            if (string.Equals(
                    timing.Method,
                    "vba/referenceCatalogRefresh",
                    StringComparison.Ordinal))
            {
                Interlocked.Increment(
                    ref referenceCatalogRefreshAdmissionCount);
            }
        }

        public void RecordCompletion(VbaInteractiveWorkCompletionTiming timing)
        {
        }
    }

    private sealed class RecordingProjectValidationLifecycleSink
        : IVbaProjectValidationLifecycleSink
    {
        private readonly object gate = new();
        private readonly List<VbaProjectAuthorityIdentity> activatedAuthorities = [];
        private readonly List<VbaProjectAuthorityIdentity> invalidatedAuthorities = [];
        private readonly List<VbaProjectAuthorityIdentity> refreshedAuthorities = [];
        private readonly List<VbaProjectAuthorityIdentity> retiredAuthorities = [];
        private readonly List<string> events = [];

        public IReadOnlyList<VbaProjectAuthorityIdentity> ActivatedAuthorities
        {
            get
            {
                lock (gate)
                {
                    return activatedAuthorities.ToArray();
                }
            }
        }

        public IReadOnlyList<VbaProjectAuthorityIdentity> InvalidatedAuthorities
        {
            get
            {
                lock (gate)
                {
                    return invalidatedAuthorities.ToArray();
                }
            }
        }

        public IReadOnlyList<VbaProjectAuthorityIdentity> RefreshedAuthorities
        {
            get
            {
                lock (gate)
                {
                    return refreshedAuthorities.ToArray();
                }
            }
        }

        public IReadOnlyList<VbaProjectAuthorityIdentity> RetiredAuthorities
        {
            get
            {
                lock (gate)
                {
                    return retiredAuthorities.ToArray();
                }
            }
        }

        public IReadOnlyList<string> Events
        {
            get
            {
                lock (gate)
                {
                    return events.ToArray();
                }
            }
        }

        public void ActivateProjectDiagnostics(
            VbaProjectValidationLifecycleLease lease)
        {
            lock (gate)
            {
                activatedAuthorities.Add(lease.Authority);
            }
        }

        public void InvalidateProjectDiagnostics(
            VbaProjectValidationLifecycleLease lease)
        {
            lock (gate)
            {
                invalidatedAuthorities.Add(lease.Authority);
                events.Add("invalidate");
            }
        }

        public void RefreshProjectDiagnostics(
            VbaProjectValidationLifecycleLease lease)
        {
            lock (gate)
            {
                refreshedAuthorities.Add(lease.Authority);
                events.Add("refresh");
            }
        }

        public void RetireProjectDiagnostics(
            VbaProjectValidationLifecycleLease lease)
        {
            lock (gate)
            {
                retiredAuthorities.Add(lease.Authority);
                events.Add("retire");
            }
        }

        public void Clear()
        {
            lock (gate)
            {
                activatedAuthorities.Clear();
                invalidatedAuthorities.Clear();
                refreshedAuthorities.Clear();
                retiredAuthorities.Clear();
                events.Clear();
            }
        }
    }

    private sealed class BlockingRetirementProjectValidationLifecycleSink
        : IVbaProjectValidationLifecycleSink
    {
        private readonly object gate = new();
        private readonly Dictionary<
            VbaProjectAuthorityIdentity,
            VbaProjectValidationLifecycleLease> leases = new();
        private readonly HashSet<VbaProjectAuthorityIdentity> activeAuthorities = [];
        private readonly TaskCompletionSource releaseRetirement = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource RetirementStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlySet<VbaProjectAuthorityIdentity> ActiveAuthorities
        {
            get
            {
                lock (gate)
                {
                    return activeAuthorities.ToHashSet();
                }
            }
        }

        public long LatestActivationRevision { get; private set; }

        public long RetirementRevision { get; private set; }

        public void ActivateProjectDiagnostics(
            VbaProjectValidationLifecycleLease lease)
        {
            lock (gate)
            {
                if (!lease.IsRevoked)
                {
                    leases[lease.Authority] = lease;
                    activeAuthorities.Add(lease.Authority);
                    LatestActivationRevision = lease.Revision;
                }
            }
        }

        public void InvalidateProjectDiagnostics(
            VbaProjectValidationLifecycleLease lease)
        {
        }

        public void RefreshProjectDiagnostics(
            VbaProjectValidationLifecycleLease lease)
        {
        }

        public void RetireProjectDiagnostics(
            VbaProjectValidationLifecycleLease lease)
        {
            RetirementRevision = lease.Revision;
            RetirementStarted.TrySetResult();
            releaseRetirement.Task.GetAwaiter().GetResult();
            lock (gate)
            {
                if (!leases.TryGetValue(lease.Authority, out var current)
                    || !ReferenceEquals(current, lease))
                {
                    return;
                }

                leases.Remove(lease.Authority);
                activeAuthorities.Remove(lease.Authority);
            }
        }

        public void ReleaseRetirement()
            => releaseRetirement.TrySetResult();
    }

    private sealed class BlockingCatalogCallbackProjectValidationLifecycleSink
        : IVbaProjectValidationLifecycleSink
    {
        private readonly object gate = new();
        private readonly Dictionary<
            VbaProjectAuthorityIdentity,
            VbaProjectValidationLifecycleLease> leases = new();
        private readonly List<VbaProjectAuthorityIdentity>
            invalidatedAuthorities = [];
        private readonly List<VbaProjectAuthorityIdentity>
            refreshedAuthorities = [];
        private readonly TaskCompletionSource releaseInvalidation = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseRefresh = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseActivation = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int activationArmed;
        private int activationClaimed;
        private int invalidationArmed;
        private int invalidationClaimed;
        private int refreshArmed;
        private int refreshClaimed;
        private VbaProjectValidationLifecycleLease? lastInvalidationAttempt;
        private VbaProjectValidationLifecycleLease? lastRefreshAttempt;

        public TaskCompletionSource InvalidationStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource RefreshStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ActivationStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource RetirementStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int ActiveLeaseCount
        {
            get
            {
                lock (gate)
                {
                    return leases.Count;
                }
            }
        }

        public VbaProjectValidationLifecycleLease CurrentLease
        {
            get
            {
                lock (gate)
                {
                    return Assert.Single(leases.Values);
                }
            }
        }

        public VbaProjectValidationLifecycleLease? LastInvalidationAttempt
        {
            get
            {
                lock (gate)
                {
                    return lastInvalidationAttempt;
                }
            }
        }

        public VbaProjectValidationLifecycleLease? LastRefreshAttempt
        {
            get
            {
                lock (gate)
                {
                    return lastRefreshAttempt;
                }
            }
        }

        public IReadOnlyList<VbaProjectAuthorityIdentity> InvalidatedAuthorities
        {
            get
            {
                lock (gate)
                {
                    return invalidatedAuthorities.ToArray();
                }
            }
        }

        public IReadOnlyList<VbaProjectAuthorityIdentity> RefreshedAuthorities
        {
            get
            {
                lock (gate)
                {
                    return refreshedAuthorities.ToArray();
                }
            }
        }

        public void ActivateProjectDiagnostics(
            VbaProjectValidationLifecycleLease lease)
        {
            if (Volatile.Read(ref activationArmed) != 0
                && Interlocked.Exchange(ref activationClaimed, 1) == 0)
            {
                ActivationStarted.TrySetResult();
                releaseActivation.Task.GetAwaiter().GetResult();
            }

            lock (gate)
            {
                if (!lease.IsRevoked)
                {
                    leases[lease.Authority] = lease;
                }
            }
        }

        public void InvalidateProjectDiagnostics(
            VbaProjectValidationLifecycleLease lease)
        {
            lock (gate)
            {
                lastInvalidationAttempt = lease;
            }

            if (Volatile.Read(ref invalidationArmed) != 0
                && Interlocked.Exchange(ref invalidationClaimed, 1) == 0)
            {
                InvalidationStarted.TrySetResult();
                releaseInvalidation.Task.GetAwaiter().GetResult();
            }

            lock (gate)
            {
                if (IsCurrentCore(lease))
                {
                    invalidatedAuthorities.Add(lease.Authority);
                }
            }
        }

        public void RefreshProjectDiagnostics(
            VbaProjectValidationLifecycleLease lease)
        {
            lock (gate)
            {
                lastRefreshAttempt = lease;
            }

            if (Volatile.Read(ref refreshArmed) != 0
                && Interlocked.Exchange(ref refreshClaimed, 1) == 0)
            {
                RefreshStarted.TrySetResult();
                releaseRefresh.Task.GetAwaiter().GetResult();
            }

            lock (gate)
            {
                if (IsCurrentCore(lease))
                {
                    refreshedAuthorities.Add(lease.Authority);
                }
            }
        }

        public void RetireProjectDiagnostics(
            VbaProjectValidationLifecycleLease lease)
        {
            RetirementStarted.TrySetResult();
            lock (gate)
            {
                if (leases.TryGetValue(lease.Authority, out var current)
                    && ReferenceEquals(current, lease))
                {
                    leases.Remove(lease.Authority);
                }
            }
        }

        public void ArmInvalidation()
            => Volatile.Write(ref invalidationArmed, 1);

        public void ArmRefresh()
            => Volatile.Write(ref refreshArmed, 1);

        public void ArmActivation()
            => Volatile.Write(ref activationArmed, 1);

        public void ReleaseInvalidation()
            => releaseInvalidation.TrySetResult();

        public void ReleaseRefresh()
            => releaseRefresh.TrySetResult();

        public void ReleaseActivation()
            => releaseActivation.TrySetResult();

        public void ClearEffects()
        {
            lock (gate)
            {
                invalidatedAuthorities.Clear();
                refreshedAuthorities.Clear();
                lastInvalidationAttempt = null;
                lastRefreshAttempt = null;
            }
        }

        private bool IsCurrentCore(VbaProjectValidationLifecycleLease lease)
            => !lease.IsRevoked
                && leases.TryGetValue(lease.Authority, out var current)
                && ReferenceEquals(current, lease);
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

    private sealed class RecordingContextDiscoveryFactory
        : IVbaProjectReferenceCatalogDiscovery,
          IVbaProjectReferenceCatalogContextDiscoveryFactory
    {
        private readonly object gate = new();
        private readonly List<VbaProjectReferenceCatalogRefreshContext> contexts = [];

        public IReadOnlyList<VbaProjectReferenceCatalogRefreshContext> Contexts
        {
            get
            {
                lock (gate)
                {
                    return contexts.ToArray();
                }
            }
        }

        public Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VbaProjectReferenceCatalogDiscoveryResult.Failure(
                referenceName,
                "Context-free discovery should not be selected for automatic lifecycle work."));

        public IVbaProjectReferenceCatalogDiscovery CreateContextDiscovery(
            VbaProjectReferenceCatalogRefreshContext context)
        {
            lock (gate)
            {
                contexts.Add(context);
            }

            return this;
        }
    }

    private sealed class ScopedContextDiscoveryFactory
        : IVbaProjectReferenceCatalogDiscovery,
          IVbaProjectReferenceCatalogContextDiscoveryFactory
    {
        public Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VbaProjectReferenceCatalogDiscoveryResult.Failure(
                referenceName,
                "Context-free discovery is not authoritative for this test."));

        public IVbaProjectReferenceCatalogDiscovery CreateContextDiscovery(
            VbaProjectReferenceCatalogRefreshContext context)
            => new ScopedContextDiscovery(context.DocumentName);

        private sealed class ScopedContextDiscovery(string documentName)
            : IVbaProjectReferenceCatalogDiscovery
        {
            public Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
                string referenceName,
                CancellationToken cancellationToken = default)
            {
                var guid = documentName.Equals("Book1", StringComparison.Ordinal)
                    ? "11111111-1111-1111-1111-111111111111"
                    : "22222222-2222-2222-2222-222222222222";
                return Task.FromResult(VbaProjectReferenceCatalogDiscoveryResult.Success(
                    new VbaProjectReferenceCatalogIdentity(
                        referenceName,
                        guid,
                        1,
                        0,
                        0,
                        $@"C:\TypeLibs\{documentName}.tlb"),
                    new VbaProjectReferenceCatalog(
                        referenceName,
                        [],
                        [
                            new VbaProjectReferenceDefinition(
                                referenceName,
                                $"{documentName}ResolvedType",
                                VbaSourceDefinitionKind.Class)
                        ])));
            }
        }
    }

    private sealed class BlockingFirstContextSuccessFactory
        : IVbaProjectReferenceCatalogDiscovery,
          IVbaProjectReferenceCatalogContextDiscoveryFactory
    {
        private readonly TaskCompletionSource releaseFirstDiscovery = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string? blockedDocumentName;
        private int contextCount;

        public BlockingFirstContextSuccessFactory(string? blockedDocumentName = null)
        {
            this.blockedDocumentName = blockedDocumentName;
        }

        public TaskCompletionSource FirstDiscoveryStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VbaProjectReferenceCatalogDiscoveryResult.Failure(
                referenceName,
                "Context-free discovery is not authoritative for this test."));

        public IVbaProjectReferenceCatalogDiscovery CreateContextDiscovery(
            VbaProjectReferenceCatalogRefreshContext context)
            => new ContextDiscovery(
                this,
                blockedDocumentName is null
                    ? Interlocked.Increment(ref contextCount) == 1
                    : context.DocumentName.Equals(
                        blockedDocumentName,
                        StringComparison.Ordinal));

        public void ReleaseFirstDiscovery()
            => releaseFirstDiscovery.TrySetResult();

        private async Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverContextAsync(
            string referenceName,
            bool isFirstContext,
            CancellationToken cancellationToken)
        {
            if (isFirstContext)
            {
                FirstDiscoveryStarted.TrySetResult();
                await releaseFirstDiscovery.Task.WaitAsync(cancellationToken);
            }

            var prefix = isFirstContext ? "Superseded" : "Current";
            var compactReferenceName = referenceName.Replace(" ", "", StringComparison.Ordinal);
            return VbaProjectReferenceCatalogDiscoveryResult.Success(
                new VbaProjectReferenceCatalogIdentity(
                    referenceName,
                    "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                    1,
                    0,
                    0,
                    $@"C:\TypeLibs\{prefix}.tlb"),
                new VbaProjectReferenceCatalog(
                    referenceName,
                    [],
                    [
                        new VbaProjectReferenceDefinition(
                            referenceName,
                            $"{prefix}{compactReferenceName}Type",
                            VbaSourceDefinitionKind.Class)
                    ]));
        }

        private sealed class ContextDiscovery(
            BlockingFirstContextSuccessFactory owner,
            bool isFirstContext)
            : IVbaProjectReferenceCatalogDiscovery
        {
            public Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
                string referenceName,
                CancellationToken cancellationToken = default)
                => owner.DiscoverContextAsync(
                    referenceName,
                    isFirstContext,
                    cancellationToken);
        }
    }

    private sealed class SuccessfulDiscovery : IVbaProjectReferenceCatalogDiscovery
    {
        public Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDiscoverySuccess(referenceName));
    }

    private sealed class FailingFirstSuccessfulSecondDiscovery
        : IVbaProjectReferenceCatalogDiscovery
    {
        private int callCount;

        public Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Interlocked.Increment(ref callCount) == 1
                ? VbaProjectReferenceCatalogDiscoveryResult.Failure(
                    referenceName,
                    "Expected first-batch failure.")
                : CreateDiscoverySuccess(referenceName));
    }

    private sealed class SuccessfulFirstFailingSecondDiscovery
        : IVbaProjectReferenceCatalogDiscovery
    {
        private int callCount;

        public TaskCompletionSource SecondDiscoveryStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref callCount) == 1)
            {
                return Task.FromResult(CreateDiscoverySuccess(referenceName));
            }

            SecondDiscoveryStarted.TrySetResult();
            return Task.FromResult(
                VbaProjectReferenceCatalogDiscoveryResult.Failure(
                    referenceName,
                    "Expected replacement-batch failure."));
        }
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

    private sealed class BoundedCancellationCleanupDiscovery
        : IVbaProjectReferenceCatalogDiscovery,
          IVbaProjectReferenceCatalogCancellationCleanup
    {
        private readonly TaskCompletionSource completeCleanup = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource DiscoveryStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CleanupCompleted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TimeSpan CancellationCleanupTimeout => TimeSpan.FromSeconds(5);

        public async Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
        {
            DiscoveryStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                await completeCleanup.Task;
                CleanupCompleted.TrySetResult();
                throw;
            }

            throw new InvalidOperationException("Expected cancellation.");
        }

        public void CompleteCleanup()
            => completeCleanup.TrySetResult();
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

    private sealed class BlockingSavePersistentStore
        : IVbaProjectReferenceCatalogPersistentStore
    {
        private readonly TaskCompletionSource releaseSave = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SaveStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<VbaProjectReferenceCatalogPersistentLoadResult> LoadAsync(
            string referenceName,
            CancellationToken cancellationToken)
            => Task.FromResult(
                VbaProjectReferenceCatalogPersistentLoadResult.Miss());

        public async Task SaveAsync(
            VbaProjectReferenceCatalogPersistentEntry entry,
            CancellationToken cancellationToken)
        {
            SaveStarted.TrySetResult();
            await releaseSave.Task.WaitAsync(cancellationToken);
        }

        public void ReleaseSave()
            => releaseSave.TrySetResult();
    }

    private sealed class NonCooperativeBlockingSavePersistentStore
        : IVbaProjectReferenceCatalogPersistentStore
    {
        private readonly TaskCompletionSource releaseSave = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SaveStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<VbaProjectReferenceCatalogPersistentLoadResult> LoadAsync(
            string referenceName,
            CancellationToken cancellationToken)
            => Task.FromResult(
                VbaProjectReferenceCatalogPersistentLoadResult.Miss());

        public async Task SaveAsync(
            VbaProjectReferenceCatalogPersistentEntry entry,
            CancellationToken cancellationToken)
        {
            SaveStarted.TrySetResult();
            await releaseSave.Task;
        }

        public void ReleaseSave()
            => releaseSave.TrySetResult();
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

        public long GetRevision(VbaIdentifiedDocument authorityDocument)
            => Version;

        public VbaProjectResolution Resolve(string activeUri)
        {
            ResolveCount++;
            return resolution;
        }
    }

    private sealed class BlockingScopeBarrierManifestResolutionSource(
        VbaProjectResolution resolution)
        : IVbaProjectManifestResolutionSource
    {
        private readonly TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int armed;

        public long Version => 0;

        public TaskCompletionSource BarrierCaptureStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long GetRevision(VbaIdentifiedDocument authorityDocument)
        {
            _ = authorityDocument;
            return 0;
        }

        public VbaProjectResolution Resolve(string activeUri)
        {
            _ = activeUri;
            return resolution;
        }

        public VbaProjectManifestBarrierSnapshot CaptureScopeBarriers(
            VbaIdentifiedDocument activeDocument,
            VbaProjectResolution currentResolution)
        {
            _ = activeDocument;
            _ = currentResolution;
            if (Volatile.Read(ref armed) != 0)
            {
                BarrierCaptureStarted.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            }

            return new VbaProjectManifestBarrierSnapshot(
                Revision: 0,
                new Dictionary<VbaDocumentIdentity, bool>());
        }

        public void Arm()
            => Volatile.Write(ref armed, 1);

        public void Release()
            => release.TrySetResult();
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

    private sealed class CatalogCommitOrderingObserver(
        VbaProjectReferenceCatalogCache catalogCache,
        string referenceName)
        : IVbaProjectReferenceCatalogCommitObserver
    {
        public VbaProjectReferenceCatalogSource SourceObservedAtCommit { get; private set; }

        public int CommitCount { get; private set; }

        public int SettlementCount { get; private set; }

        public void CatalogCommitAccepted(
            VbaProjectReferenceCatalogRefreshBatchIdentity batch,
            VbaProjectReferenceCatalogRefreshAuthorityIdentity authority)
        {
            _ = batch;
            _ = authority;
            CommitCount++;
            SourceObservedAtCommit = catalogCache.GetCatalogSource(referenceName);
        }

        public void CatalogPersistedPreloadSettled(
            VbaProjectReferenceCatalogRefreshBatchIdentity batch)
        {
            _ = batch;
        }

        public void CatalogRefreshSettled(
            VbaProjectReferenceCatalogRefreshBatchIdentity batch)
        {
            _ = batch;
            SettlementCount++;
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
