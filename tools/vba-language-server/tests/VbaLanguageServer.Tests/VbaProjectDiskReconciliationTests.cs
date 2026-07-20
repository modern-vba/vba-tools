using System.Text.Json;
using VbaLanguageServer.Lsp;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Workspace;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaProjectDiskReconciliationTests
{
    [Fact]
    public async Task Manual_cycle_makes_watcher_less_disk_change_visible()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-reconcile-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var helperPath = Path.Combine(projectRoot, "src", "Book1", "Helper.bas");
            var helperUri = ToFileUri(helperPath);
            var callerUri = ToFileUri(
                Path.Combine(projectRoot, "src", "Book1", "Caller.bas"));
            File.WriteAllText(
                helperPath,
                string.Join('\n', [
                    "Attribute VB_Name = \"Helper\"",
                    "Public Function BuildValue() As String",
                    "End Function"
                ]));
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()));
            workspace.UpdateDocument(
                callerUri,
                string.Join('\n', [
                    "Attribute VB_Name = \"Caller\"",
                    "Public Sub Run()",
                    "    BuildValue",
                    "End Sub"
                ]));
            var staleSnapshot = workspace.CreateProjectSnapshot(callerUri);
            File.WriteAllText(
                helperPath,
                string.Join('\n', [
                    "Attribute VB_Name = \"Helper\"",
                    "Public Function BuildReplacement() As String",
                    "End Function"
                ]));

            await using var scheduler = new VbaInteractiveWorkScheduler(
                options: new VbaInteractiveWorkSchedulerOptions(
                    CoalesceSupersededMutations: true,
                    MaxOwnedWork: 1));
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            var refreshedSnapshot = workspace.CreateProjectSnapshot(callerUri);

            Assert.DoesNotContain(
                staleSnapshot.SemanticInventory.GetWorkspaceSymbols("BuildReplacement"),
                symbol => symbol.Uri == helperUri);
            Assert.Contains(
                refreshedSnapshot.SemanticInventory.GetWorkspaceSymbols("BuildReplacement"),
                symbol => symbol.Uri == helperUri);
            Assert.DoesNotContain(
                refreshedSnapshot.SemanticInventory.GetWorkspaceSymbols("BuildValue"),
                symbol => symbol.Uri == helperUri);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Consecutive_cycles_keep_the_activated_scope_after_a_reconciled_source_change()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-reconcile-consecutive-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            var callerUri = ToFileUri(Path.Combine(sourceRoot, "Caller.bas"));
            var firstPath = Path.Combine(sourceRoot, "First.bas");
            var secondPath = Path.Combine(sourceRoot, "Second.bas");
            var firstUri = ToFileUri(firstPath);
            var secondUri = ToFileUri(secondPath);
            File.WriteAllText(firstPath, CreateModule("First", "BuildFirstBefore"));
            File.WriteAllText(secondPath, CreateModule("Second", "BuildSecondBefore"));
            var workspace = CreateWorkspace(callerUri);
            _ = workspace.CreateProjectSnapshot(callerUri);
            var boundary = new CountingDiskSourceBoundary(
                new VbaFileSystemProjectDiskSourceBoundary());
            var diagnostics = new RecordingDiagnostics();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    diagnostics,
                    sourceBoundary: boundary,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            File.WriteAllText(firstPath, CreateModule("First", "BuildFirstAfter"));
            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains(firstUri, diagnostics.TrackedUris);

            File.WriteAllText(secondPath, CreateModule("Second", "BuildSecondAfter"));
            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(2, boundary.ScanCount);
            Assert.Contains(secondUri, diagnostics.TrackedUris);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Accepted_source_change_is_not_reapplied_by_an_unchanged_later_cycle()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-reconcile-source-baseline-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            var callerUri = ToFileUri(Path.Combine(sourceRoot, "Caller.bas"));
            var helperPath = Path.Combine(sourceRoot, "Helper.bas");
            var helperUri = ToFileUri(helperPath);
            File.WriteAllText(helperPath, CreateModule("Helper", "BuildBefore"));
            var workspace = CreateWorkspace(callerUri);
            _ = workspace.CreateProjectSnapshot(callerUri);
            var diagnostics = new RecordingDiagnostics();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    diagnostics,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            File.WriteAllText(helperPath, CreateModule("Helper", "BuildAfter"));
            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(
                1,
                diagnostics.TrackedUris.Count(uri => uri == helperUri));
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Source_only_batch_validates_one_scope_fence()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-reconcile-source-fence-cache-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            var callerUri = ToFileUri(Path.Combine(sourceRoot, "Caller.bas"));
            var sourcePaths = Enumerable.Range(0, 16)
                .Select(index => Path.Combine(sourceRoot, $"Worker{index}.bas"))
                .ToArray();
            foreach (var (sourcePath, index) in
                sourcePaths.Select((path, index) => (path, index)))
            {
                File.WriteAllText(
                    sourcePath,
                    CreateModule($"Worker{index}", $"BuildBefore{index}"));
            }

            var workspace = CreateWorkspace(callerUri);
            _ = workspace.CreateProjectSnapshot(callerUri);
            foreach (var (sourcePath, index) in
                sourcePaths.Select((path, index) => (path, index)))
            {
                File.WriteAllText(
                    sourcePath,
                    CreateModule($"Worker{index}", $"BuildAfter{index}"));
            }

            var commitObserver = new RecordingCommitObserver();
            var diagnostics = new RecordingDiagnostics();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    diagnostics,
                    cadence: Timeout.InfiniteTimeSpan,
                    commitObserver: commitObserver);
            reconciliation.AttachScheduler(scheduler);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, commitObserver.ScopeFenceValidationCount);
            Assert.Equal(
                sourcePaths.Length,
                diagnostics.TrackedUris
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count());
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Manifest_mutation_invalidates_the_source_scope_fence_cache()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-reconcile-manifest-fence-cache-").FullName;
        try
        {
            WriteProjectManifest(
                projectRoot,
                "src/Book1",
                "Visual Basic For Applications");
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            var callerUri = ToFileUri(Path.Combine(sourceRoot, "Caller.bas"));
            var sourcePaths = Enumerable.Range(0, 8)
                .Select(index => Path.Combine(sourceRoot, $"Worker{index}.bas"))
                .ToArray();
            foreach (var (sourcePath, index) in
                sourcePaths.Select((path, index) => (path, index)))
            {
                File.WriteAllText(
                    sourcePath,
                    CreateModule($"Worker{index}", $"BuildBefore{index}"));
            }

            var workspace = CreateWorkspace(callerUri);
            _ = workspace.CreateProjectSnapshot(callerUri);
            WriteProjectManifest(
                projectRoot,
                "src/Book1",
                "Microsoft Excel 16.0 Object Library");
            foreach (var (sourcePath, index) in
                sourcePaths.Select((path, index) => (path, index)))
            {
                File.WriteAllText(
                    sourcePath,
                    CreateModule($"Worker{index}", $"BuildAfter{index}"));
            }

            var commitObserver = new RecordingCommitObserver();
            var diagnostics = new RecordingDiagnostics();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    diagnostics,
                    cadence: Timeout.InfiniteTimeSpan,
                    commitObserver: commitObserver);
            reconciliation.AttachScheduler(scheduler);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(3, commitObserver.ScopeFenceValidationCount);
            Assert.Equal(
                sourcePaths.Length,
                diagnostics.TrackedUris
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count());
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Interactive_barrier_capture_omits_disk_reconciliation_revisions()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-interactive-barrier-capture-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var sourceUri = ToFileUri(
                Path.Combine(projectRoot, "src", "Book1", "Caller.bas"));
            var manifestPath = Path.Combine(projectRoot, "vba-project.json");
            var manifestUri = ToFileUri(manifestPath);
            var manifestWorkspace = new VbaProjectManifestWorkspace();
            Assert.True(manifestWorkspace.ReloadManifest(manifestUri));
            var resolution = manifestWorkspace.Resolve(sourceUri);

            var interactive = manifestWorkspace.CaptureScopeBarriers(
                sourceUri,
                resolution);
            var reconciliation =
                manifestWorkspace.CaptureDiskReconciliationBarriers(
                    sourceUri,
                    resolution);

            Assert.Empty(interactive.ReconciliationRevisions);
            Assert.Contains(
                Path.GetFullPath(manifestPath),
                reconciliation.ReconciliationRevisions.Keys);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Interactive_cold_snapshot_does_not_absorb_a_newer_disk_source_before_scan_commit()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-reconcile-cold-race-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            var callerUri = ToFileUri(Path.Combine(sourceRoot, "Caller.bas"));
            var helperPath = Path.Combine(sourceRoot, "Helper.bas");
            var helperUri = ToFileUri(helperPath);
            File.WriteAllText(helperPath, CreateModule("Helper", "BuildA"));
            var workspace = CreateWorkspace(callerUri);
            _ = workspace.CreateProjectSnapshot(callerUri);
            var boundary = new BlockingDiskSourceBoundary();
            var diagnostics = new RecordingDiagnostics();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    diagnostics,
                    sourceBoundary: boundary,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            var firstTrigger = reconciliation.TriggerAsync();
            await boundary.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            File.WriteAllText(helperPath, CreateModule("Helper", "BuildC"));
            workspace.UpdateDocument(
                callerUri,
                "Attribute VB_Name = \"Caller\"\nPublic Sub Run()\n    ' changed\nEnd Sub");
            _ = workspace.CreateProjectSnapshot(callerUri);
            boundary.Complete(CreateScan(
                boundary.Scope,
                File.ReadAllText(Path.Combine(projectRoot, "vba-project.json")),
                (helperPath, CreateModule("Helper", "BuildB"))));
            await firstTrigger.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains(
                "BuildB",
                workspace.GetDocumentText(helperUri));

            await using var nextReconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    diagnostics,
                    cadence: Timeout.InfiniteTimeSpan);
            nextReconciliation.AttachScheduler(scheduler);
            await nextReconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Contains(
                "BuildC",
                workspace.GetDocumentText(helperUri));
            Assert.Equal(
                2,
                diagnostics.TrackedUris.Count(uri => uri == helperUri));
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Manual_cycle_detects_added_changed_deleted_and_renamed_sources()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-reconcile-set-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            var callerUri = ToFileUri(Path.Combine(sourceRoot, "Caller.bas"));
            var changedPath = Path.Combine(sourceRoot, "Changed.bas");
            var deletedPath = Path.Combine(sourceRoot, "Deleted.bas");
            var renamedFromPath = Path.Combine(sourceRoot, "RenameOld.bas");
            var renamedToPath = Path.Combine(sourceRoot, "RenameNew.bas");
            var addedPath = Path.Combine(sourceRoot, "Added.bas");
            File.WriteAllText(changedPath, CreateModule("Changed", "BuildBeforeChange"));
            File.WriteAllText(deletedPath, CreateModule("Deleted", "BuildDeleted"));
            File.WriteAllText(renamedFromPath, CreateModule("RenameOld", "BuildRenamed"));
            var workspace = CreateWorkspace(callerUri);
            _ = workspace.CreateProjectSnapshot(callerUri);

            File.WriteAllText(changedPath, CreateModule("Changed", "BuildAfterChange"));
            File.Delete(deletedPath);
            File.Move(renamedFromPath, renamedToPath);
            File.WriteAllText(addedPath, CreateModule("Added", "BuildAdded"));
            var diagnostics = new RecordingDiagnostics();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    diagnostics,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            var snapshot = workspace.CreateProjectSnapshot(callerUri);

            Assert.Contains(
                snapshot.SemanticInventory.GetWorkspaceSymbols("BuildAfterChange"),
                symbol => symbol.Uri == ToFileUri(changedPath));
            Assert.Empty(
                snapshot.SemanticInventory.GetWorkspaceSymbols("BuildBeforeChange"));
            Assert.Contains(
                snapshot.SemanticInventory.GetWorkspaceSymbols("BuildAdded"),
                symbol => symbol.Uri == ToFileUri(addedPath));
            Assert.Empty(
                snapshot.SemanticInventory.GetWorkspaceSymbols("BuildDeleted"));
            Assert.Contains(
                snapshot.SemanticInventory.GetWorkspaceSymbols("BuildRenamed"),
                symbol => symbol.Uri == ToFileUri(renamedToPath));
            Assert.DoesNotContain(
                snapshot.SemanticInventory.GetWorkspaceSymbols("BuildRenamed"),
                symbol => symbol.Uri == ToFileUri(renamedFromPath));
            Assert.Contains(ToFileUri(deletedPath), diagnostics.EmptyUris);
            Assert.Contains(ToFileUri(renamedFromPath), diagnostics.EmptyUris);
            Assert.Contains(ToFileUri(addedPath), diagnostics.TrackedUris);
            Assert.Contains(ToFileUri(renamedToPath), diagnostics.TrackedUris);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Descendant_manifest_transfers_a_known_source_without_deleting_workspace_state()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-reconcile-scope-transfer-").FullName;
        try
        {
            WriteProjectManifest(projectRoot, "src");
            var outerPath = Path.Combine(projectRoot, "src", "Outer.bas");
            var outerUri = ToFileUri(outerPath);
            var nestedProjectRoot = Path.Combine(
                projectRoot,
                "src",
                "NestedProject");
            var innerPath = Path.Combine(
                nestedProjectRoot,
                "src",
                "Book1",
                "Inner.bas");
            var innerUri = ToFileUri(innerPath);
            var innerText = CreateModule("Inner", "RunInner");
            File.WriteAllText(outerPath, CreateModule("Outer", "RunOuter"));
            Directory.CreateDirectory(Path.GetDirectoryName(innerPath)!);
            File.WriteAllText(innerPath, innerText);

            var workspace = CreateWorkspace(outerUri);
            Assert.True(workspace.ReloadSourceDocument(innerUri, innerText));
            var initial = workspace.CreateProjectSnapshot(outerUri);
            Assert.Contains(
                initial.SemanticInventory.GetWorkspaceSymbols("RunInner"),
                symbol => symbol.Uri == innerUri);

            WriteProjectManifest(nestedProjectRoot);
            var diagnostics = new RecordingDiagnostics();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    diagnostics,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(innerText, workspace.GetDocumentText(innerUri));
            Assert.DoesNotContain(innerUri, diagnostics.EmptyUris);
            using (var capture =
                workspace.CaptureDiskReconciliationScopes())
            {
                var outerScope = Assert.Single(capture.Scopes);
                Assert.DoesNotContain(
                    outerScope.KnownSources,
                    source => source.Uri == innerUri);
            }

            var inner = workspace.CreateProjectSnapshot(innerUri);
            Assert.Contains(
                inner.SemanticInventory.GetWorkspaceSymbols("RunInner"),
                symbol => symbol.Uri == innerUri);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Descendant_barrier_change_after_scan_rejects_stale_scope_release()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-reconcile-barrier-race-").FullName;
        try
        {
            WriteProjectManifest(projectRoot, "src");
            var outerPath = Path.Combine(projectRoot, "src", "Outer.bas");
            var outerUri = ToFileUri(outerPath);
            var nestedProjectRoot = Path.Combine(
                projectRoot,
                "src",
                "NestedProject");
            var innerPath = Path.Combine(
                nestedProjectRoot,
                "src",
                "Book1",
                "Inner.bas");
            var innerUri = ToFileUri(innerPath);
            var innerText = CreateModule("Inner", "RunInner");
            File.WriteAllText(outerPath, CreateModule("Outer", "RunOuter"));
            Directory.CreateDirectory(Path.GetDirectoryName(innerPath)!);
            File.WriteAllText(innerPath, innerText);
            var workspace = CreateWorkspace(outerUri);
            Assert.True(workspace.ReloadSourceDocument(innerUri, innerText));
            _ = workspace.CreateProjectSnapshot(outerUri);
            var nestedManifestUri = ToFileUri(Path.Combine(
                nestedProjectRoot,
                "vba-project.json"));
            Assert.True(
                workspace.ManifestWorkspace.OpenManifest(
                    nestedManifestUri,
                    documentVersion: 1,
                    CreateProjectManifestText()).Accepted);
            var boundary = new BlockingFirstDiskSourceBoundary(
                new VbaFileSystemProjectDiskSourceBoundary());
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    sourceBoundary: boundary,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            var trigger = reconciliation.TriggerAsync();
            await boundary.Started.Task.WaitAsync(
                TimeSpan.FromSeconds(2));
            var staleScan =
                await new VbaFileSystemProjectDiskSourceBoundary()
                    .ScanAsync(
                        boundary.Scope,
                        CancellationToken.None);
            Assert.Contains(
                innerPath,
                staleScan.ExistingNonOwnedSourcePaths);
            Assert.True(
                workspace.ManifestWorkspace.CloseManifest(
                    nestedManifestUri));
            boundary.Complete(staleScan);
            await trigger.WaitAsync(TimeSpan.FromSeconds(5));

            using var capture =
                workspace.CaptureDiskReconciliationScopes();
            Assert.Contains(
                Assert.Single(capture.Scopes).KnownSources,
                source => source.Uri == innerUri);
            Assert.Equal(innerText, workspace.GetDocumentText(innerUri));
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Warm_queries_do_not_cross_the_disk_reconciliation_boundary()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-reconcile-warm-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            var helperPath = Path.Combine(sourceRoot, "Helper.bas");
            var callerUri = ToFileUri(Path.Combine(sourceRoot, "Caller.bas"));
            File.WriteAllText(helperPath, CreateModule("Helper", "BuildValue"));
            var workspace = CreateWorkspace(callerUri);
            var first = workspace.CreateProjectSnapshot(callerUri);
            var boundary = new CountingDiskSourceBoundary(
                new VbaFileSystemProjectDiskSourceBoundary());
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    sourceBoundary: boundary,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            var second = workspace.CreateProjectSnapshot(callerUri);
            var third = workspace.CreateProjectSnapshot(callerUri);

            Assert.Same(first, second);
            Assert.Same(first, third);
            Assert.Equal(0, boundary.ScanCount);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, boundary.ScanCount);
            Assert.Single(boundary.AuthorityKeys);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Completed_snapshot_capture_and_cycle_release_transient_source_revision_history()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-reconcile-revision-history-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var callerUri = ToFileUri(
                Path.Combine(projectRoot, "src", "Book1", "Caller.bas"));
            var workspace = CreateWorkspace(callerUri);
            _ = workspace.CreateProjectSnapshot(callerUri);
            for (var index = 0; index < 128; index++)
            {
                var transientUri = ToFileUri(
                    Path.Combine(projectRoot, "transient", $"Module{index}.bas"));
                workspace.UpdateDocument(
                    transientUri,
                    $"Attribute VB_Name = \"Module{index}\"\n");
                workspace.RemoveDocument(transientUri);
            }

            Assert.True(workspace.RetainedSourceRevisionCount >= 128);
            Assert.True(
                workspace.RetainedProjectSnapshotSourceRevisionCount >= 128);
            _ = workspace.CreateProjectSnapshot(callerUri);
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(0, workspace.RetainedSourceRevisionCount);
            Assert.Equal(
                0,
                workspace.RetainedProjectSnapshotSourceRevisionCount);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Source_revision_history_retains_newer_revision_until_the_oldest_capture_releases()
    {
        var history = new VbaSourceRevisionHistory();
        var olderCapture = history.BeginCapture(1);
        var sourceUri = "file:///C:/workspace/Newer.bas";
        history.Record(sourceUri, 2);

        using (history.BeginCapture(2))
        {
            Assert.Equal(2, history.GetRevision(sourceUri));
            Assert.Equal(1, history.Count);
        }

        Assert.Equal(2, history.GetRevision(sourceUri));
        Assert.Equal(1, history.Count);

        olderCapture.Dispose();

        Assert.Equal(0, history.GetRevision(sourceUri));
        Assert.Equal(0, history.Count);
    }

    [Fact]
    public async Task Pending_older_capture_keeps_a_newer_source_fence_out_of_the_cache()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-revision-capture-order-").FullName;
        var releaseNewerStore = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var sourcePath = Path.Combine(projectRoot, "Worker.bas");
            var sourceUri = ToFileUri(sourcePath);
            File.WriteAllText(
                sourcePath,
                CreateModule("Worker", "BuildValue"));
            var observer = new BlockingWorkspaceVersionSnapshotObserver(
                blockedVersion: 2,
                releaseNewerStore.Task);
            var provider = new VbaProjectSnapshotProvider(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.Empty),
                new VbaProjectSourceDocumentCache(),
                new VbaProjectManifestWorkspace(),
                buildObserver: observer);
            var olderState = new VbaWorkspaceSnapshotState(
                new Dictionary<string, VbaTrackedDocument>(),
                new HashSet<string>(),
                Version: 1);
            var newerState = olderState with
            {
                ExcludedSourceUris = new HashSet<string>(
                    [sourceUri],
                    StringComparer.OrdinalIgnoreCase),
                Version = 2
            };
            using var pendingOlderCapture =
                provider.BeginSourceRevisionCapture(olderState.Version);
            provider.InvalidateSource(sourceUri, sourceRevision: 2);

            var newerBuild = Task.Run(
                () => provider.CreateProjectSnapshot(
                    sourceUri,
                    newerState,
                    CancellationToken.None));
            await observer.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var older = provider.CreateProjectSnapshot(
                sourceUri,
                olderState,
                CancellationToken.None);
            var current = provider.CreateProjectSnapshot(
                sourceUri,
                newerState,
                CancellationToken.None);

            Assert.Contains(sourceUri, older.SourceDocuments.Keys);
            Assert.DoesNotContain(sourceUri, current.SourceDocuments.Keys);

            releaseNewerStore.TrySetResult();
            await newerBuild.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            releaseNewerStore.TrySetResult();
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Last_tracked_document_removal_retires_project_state(
        bool closeDocument)
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-scope-retirement-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            var callerUri = ToFileUri(Path.Combine(sourceRoot, "Caller.bas"));
            var peerUri = ToFileUri(Path.Combine(sourceRoot, "Peer.bas"));
            File.WriteAllText(
                Path.Combine(sourceRoot, "Helper.bas"),
                CreateModule("Helper", "BuildValue"));
            var workspace = CreateWorkspace(callerUri);
            workspace.UpdateDocument(
                peerUri,
                "Attribute VB_Name = \"Peer\"\nPublic Sub Run()\nEnd Sub");
            _ = workspace.CreateProjectSnapshot(callerUri);

            Assert.True(RemoveTrackedDocument(
                workspace,
                callerUri,
                closeDocument));
            using (var remaining = workspace.CaptureDiskReconciliationScopes())
            {
                var scope = Assert.Single(remaining.Scopes);
                Assert.Equal(peerUri, scope.ActiveUri);
            }

            Assert.True(RemoveTrackedDocument(
                workspace,
                peerUri,
                closeDocument));
            using var retired = workspace.CaptureDiskReconciliationScopes();

            Assert.Empty(retired.Scopes);
            Assert.Equal(0, workspace.RetainedProjectSnapshotCount);
            Assert.Equal(
                0,
                workspace.RetainedProjectScopeInvalidationStateCount);
            Assert.Equal(0, workspace.RetainedReconciliationScopeCount);
            Assert.Equal(0, workspace.RetainedReconciliationAuthorityCount);
            Assert.Equal(0, workspace.RetainedProjectDiskDocumentCount);
            Assert.Equal(0, workspace.RetainedManifestStateCount);
            Assert.Equal(
                0,
                workspace.RetainedManifestEffectiveRevisionCount);
            Assert.Equal(
                0,
                workspace.RetainedManifestReconciliationRevisionCount);
            Assert.Equal(
                0,
                workspace.RetainedManifestReconciliationBaselineCount);
            Assert.Equal(
                0,
                workspace.RetainedManifestLastKnownGoodCount);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Manifest_retirement_preserves_an_open_overlay_until_it_closes()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-open-manifest-retention-").FullName;
        try
        {
            var manifestUri = ToFileUri(Path.Combine(
                projectRoot,
                "vba-project.json"));
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()));
            var opened = workspace.ManifestWorkspace.OpenManifest(
                manifestUri,
                documentVersion: 1,
                CreateProjectManifestText("src/Book1"));

            Assert.True(opened.Accepted);
            workspace.RetireInactiveManifestState();
            Assert.True(
                workspace.ManifestWorkspace.TryGetEffectiveManifest(
                    manifestUri,
                    out _,
                    out _,
                    out _));
            Assert.Equal(1, workspace.RetainedManifestStateCount);

            Assert.True(
                workspace.ManifestWorkspace.CloseManifest(manifestUri));
            workspace.RetireInactiveManifestState();

            Assert.Equal(0, workspace.RetainedManifestStateCount);
            Assert.Equal(
                0,
                workspace.RetainedManifestEffectiveRevisionCount);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Invalid_watched_manifest_returns_last_known_good_and_validation_error()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-invalid-watched-lkg-diagnostic-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var manifestPath = Path.Combine(
                projectRoot,
                "vba-project.json");
            var manifestUri = ToFileUri(manifestPath);
            var callerUri = ToFileUri(Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Caller.bas"));
            var workspace = CreateWorkspace(callerUri);
            _ = workspace.CreateProjectSnapshot(callerUri);
            Assert.True(
                workspace.ManifestWorkspace.TryGetEffectiveManifest(
                    manifestUri,
                    out _,
                    out var acceptedText,
                    out var acceptedError));
            Assert.Null(acceptedError);
            const string invalidText = "{\"schemaVersion\":";
            File.WriteAllText(manifestPath, invalidText);
            Assert.True(
                workspace.ManifestWorkspace.ReloadManifest(manifestUri));

            var hasFallback =
                workspace.ManifestWorkspace.TryGetEffectiveManifest(
                    manifestUri,
                    out _,
                    out var fallbackText,
                    out var validationError);

            Assert.True(hasFallback);
            Assert.Equal(acceptedText, fallbackText);
            Assert.NotNull(validationError);
            Assert.Equal(
                invalidText,
                workspace.ManifestWorkspace
                    .CaptureReconciliationState(manifestUri)
                    .Baseline
                    .Text);
            var manifestEvents = new RecordingManifestEvents();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    manifestEvents: manifestEvents,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Empty(manifestEvents.ValidationFailures);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Invalid_disk_manifest_reuses_warm_last_known_good_without_repeated_io()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-invalid-warm-lkg-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var manifestPath = Path.Combine(
                projectRoot,
                "vba-project.json");
            var manifestUri = ToFileUri(manifestPath);
            var callerUri = ToFileUri(Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Caller.bas"));
            var fileSystem = new GuardedProjectFileSystem(
                SystemVbaProjectFileSystem.Instance);
            var manifestWorkspace =
                new VbaProjectManifestWorkspace(fileSystem);
            Assert.Equal(
                VbaProjectResolutionKind.ManifestDocument,
                manifestWorkspace.CaptureResolution(callerUri)
                    .Resolution
                    .Kind);
            File.WriteAllText(
                manifestPath,
                "{\"schemaVersion\":");

            Assert.True(
                manifestWorkspace.TryGetEffectiveManifest(
                    manifestUri,
                    out _,
                    out var firstFallback,
                    out var firstError));
            Assert.NotNull(firstError);
            fileSystem.RejectOperations = true;

            Assert.True(
                manifestWorkspace.TryGetEffectiveManifest(
                    manifestUri,
                    out _,
                    out var repeatedFallback,
                    out var repeatedError));
            Assert.Equal(firstFallback, repeatedFallback);
            Assert.NotNull(repeatedError);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Known_state_resolution_preserves_invalid_open_overlay_shadowing()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-known-invalid-overlay-").FullName;
        try
        {
            WriteProjectManifest(
                projectRoot,
                "src",
                "Visual Basic For Applications");
            var innerRoot = Path.Combine(
                projectRoot,
                "src",
                "InnerProject");
            var innerManifestPath = Path.Combine(
                innerRoot,
                "vba-project.json");
            var innerManifestUri = ToFileUri(
                innerManifestPath);
            var sourceUri = ToFileUri(Path.Combine(
                innerRoot,
                "src",
                "Book1",
                "Module.bas"));
            Directory.CreateDirectory(innerRoot);
            const string invalidText = "{\"schemaVersion\":";
            File.WriteAllText(innerManifestPath, invalidText);
            var manifestWorkspace =
                new VbaProjectManifestWorkspace();
            Assert.Throws<VbaProjectManifestException>(
                () => manifestWorkspace.CaptureResolution(sourceUri));
            var outer =
                manifestWorkspace.CaptureResolution(sourceUri)
                    .Resolution;
            var overlay = manifestWorkspace.OpenManifest(
                innerManifestUri,
                documentVersion: 1,
                invalidText);
            Assert.True(overlay.Accepted);
            Assert.NotNull(overlay.Error);
            var observed = manifestWorkspace
                .ReloadReconciledManifest(
                    innerManifestUri,
                    CreateProjectManifestText(),
                    manifestWorkspace.GetReconciliationRevision(
                        innerManifestUri));
            Assert.Equal(
                VbaProjectManifestReconciliationStatus.Observed,
                observed.Status);

            Assert.True(
                manifestWorkspace.TryResolveKnownState(
                    sourceUri,
                    out var known));
            Assert.Equal(
                outer.ManifestPath,
                known.ManifestPath);
            Assert.NotEqual(
                Path.GetFullPath(innerManifestPath),
                known.ManifestPath);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Authority_comparison_performs_no_filesystem_io_and_skips_an_unrelated_cold_open()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-authority-compare-no-io-").FullName;
        var unrelatedRoot = Directory.CreateTempSubdirectory(
            "vba-ls-authority-compare-unrelated-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            WriteProjectManifest(unrelatedRoot);
            var sourceUri = ToFileUri(Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Module.bas"));
            var unrelatedUri = ToFileUri(Path.Combine(
                unrelatedRoot,
                "src",
                "Book1",
                "Unrelated.bas"));
            var fileSystem = new GuardedProjectFileSystem(
                SystemVbaProjectFileSystem.Instance);
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()),
                NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
                NullVbaDocumentAnalysisBuildObserver.Instance,
                NullVbaProjectSnapshotBuildObserver.Instance,
                fileSystem);
            workspace.UpdateDocument(
                sourceUri,
                CreateModule("Module", "RunModule"));
            workspace.UpdateDocument(
                unrelatedUri,
                CreateModule("Unrelated", "RunUnrelated"));
            _ = workspace.CreateProjectSnapshot(sourceUri);
            File.Delete(Path.Combine(
                projectRoot,
                "vba-project.json"));
            var boundary = new BlockingDiskSourceBoundary();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    sourceBoundary: boundary,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            var pending = reconciliation.TriggerAsync();
            await boundary.Started.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            var scan =
                await new VbaFileSystemProjectDiskSourceBoundary()
                    .ScanAsync(
                        boundary.Scope,
                        CancellationToken.None);
            var operationCount = fileSystem.OperationCount;
            fileSystem.RejectOperations = true;
            boundary.Complete(scan);

            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(
                operationCount,
                fileSystem.OperationCount);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(unrelatedRoot, recursive: true);
        }
    }

    [Fact]
    public void Retired_project_manifest_history_does_not_accumulate_or_rebuild_a_warm_peer()
    {
        var root = Directory.CreateTempSubdirectory(
            "vba-ls-manifest-history-retirement-").FullName;
        try
        {
            var activeRoot = Path.Combine(root, "Active");
            WriteProjectManifest(activeRoot);
            var activeUri = ToFileUri(Path.Combine(
                activeRoot,
                "src",
                "Book1",
                "Active.bas"));
            var workspace = CreateWorkspace(activeUri);
            var warm = workspace.CreateProjectSnapshot(activeUri);
            for (var index = 0; index < 24; index++)
            {
                var retiredRoot = Path.Combine(
                    root,
                    $"Retired{index}");
                WriteProjectManifest(retiredRoot);
                var retiredUri = ToFileUri(Path.Combine(
                    retiredRoot,
                    "src",
                    "Book1",
                    "Retired.bas"));
                workspace.UpdateDocument(
                    retiredUri,
                    $"Attribute VB_Name = \"Retired{index}\"\n");
                _ = workspace.CreateProjectSnapshot(retiredUri);
                Assert.True(workspace.RemoveDocument(retiredUri));
            }

            Assert.Same(
                warm,
                workspace.CreateProjectSnapshot(activeUri));
            Assert.True(workspace.RetainedManifestStateCount <= 1);
            Assert.True(
                workspace.RetainedManifestEffectiveRevisionCount <= 1);
            Assert.True(
                workspace.RetainedManifestReconciliationRevisionCount <= 1);
            Assert.True(
                workspace.RetainedManifestReconciliationBaselineCount <= 1);
            Assert.True(
                workspace.RetainedManifestLastKnownGoodCount <= 1);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Scope_retirement_does_not_resolve_an_unactivated_remaining_project(
        bool closeDocument)
    {
        var activeProjectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-retire-active-").FullName;
        var inactiveProjectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-retire-unactivated-").FullName;
        try
        {
            WriteProjectManifest(activeProjectRoot);
            WriteProjectManifest(inactiveProjectRoot);
            var activeUri = ToFileUri(Path.Combine(
                activeProjectRoot,
                "src",
                "Book1",
                "Active.bas"));
            var unactivatedUri = ToFileUri(Path.Combine(
                inactiveProjectRoot,
                "src",
                "Book1",
                "Unactivated.bas"));
            var fileSystem = new GuardedProjectFileSystem(
                SystemVbaProjectFileSystem.Instance);
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()),
                NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
                NullVbaDocumentAnalysisBuildObserver.Instance,
                NullVbaProjectSnapshotBuildObserver.Instance,
                fileSystem);
            workspace.UpdateDocument(
                activeUri,
                "Attribute VB_Name = \"Active\"\nPublic Sub Run()\nEnd Sub");
            workspace.UpdateDocument(
                unactivatedUri,
                "Attribute VB_Name = \"Unactivated\"\nPublic Sub Run()\nEnd Sub");
            _ = workspace.CreateProjectSnapshot(activeUri);
            var operationCount = fileSystem.OperationCount;
            fileSystem.RejectOperations = true;

            Assert.True(RemoveTrackedDocument(
                workspace,
                activeUri,
                closeDocument));

            Assert.Equal(operationCount, fileSystem.OperationCount);
            Assert.Equal(0, workspace.RetainedProjectSnapshotCount);
            Assert.Equal(
                0,
                workspace.RetainedProjectScopeInvalidationStateCount);
            Assert.Equal(0, workspace.RetainedReconciliationScopeCount);
            Assert.Equal(0, workspace.RetainedReconciliationAuthorityCount);
        }
        finally
        {
            Directory.Delete(activeProjectRoot, recursive: true);
            Directory.Delete(inactiveProjectRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Nested_project_authority_retires_a_containing_outer_scope(
        bool closeDocument)
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-retire-nested-authority-").FullName;
        try
        {
            var outerSourceRoot = Path.Combine(projectRoot, "src");
            var nestedProjectRoot = Path.Combine(
                outerSourceRoot,
                "NestedProject");
            WriteProjectManifest(projectRoot, "src");
            WriteProjectManifest(nestedProjectRoot, "src/Inner");
            var outerUri = ToFileUri(Path.Combine(
                outerSourceRoot,
                "Outer.bas"));
            var innerUri = ToFileUri(Path.Combine(
                nestedProjectRoot,
                "src",
                "Inner",
                "Inner.bas"));
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()));
            workspace.UpdateDocument(
                outerUri,
                "Attribute VB_Name = \"Outer\"\nPublic Sub Run()\nEnd Sub");
            workspace.UpdateDocument(
                innerUri,
                "Attribute VB_Name = \"Inner\"\nPublic Sub Run()\nEnd Sub");
            _ = workspace.CreateProjectSnapshot(outerUri);
            _ = workspace.CreateProjectSnapshot(innerUri);

            Assert.True(RemoveTrackedDocument(
                workspace,
                outerUri,
                closeDocument));
            using var remaining =
                workspace.CaptureDiskReconciliationScopes();

            var scope = Assert.Single(remaining.Scopes);
            Assert.Equal(innerUri, scope.ActiveUri);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(
                    nestedProjectRoot,
                    "vba-project.json")),
                scope.Resolution.ManifestPath);
            Assert.Equal(1, workspace.RetainedProjectSnapshotCount);
            Assert.Equal(
                1,
                workspace.RetainedProjectScopeInvalidationStateCount);
            Assert.Equal(1, workspace.RetainedReconciliationScopeCount);
            Assert.Equal(1, workspace.RetainedReconciliationAuthorityCount);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Snapshot_that_resumes_after_scope_retirement_does_not_restore_state()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-scope-retirement-race-").FullName;
        var releaseCapture = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            WriteProjectManifest(projectRoot);
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            var callerUri = ToFileUri(Path.Combine(sourceRoot, "Caller.bas"));
            File.WriteAllText(
                Path.Combine(sourceRoot, "Helper.bas"),
                CreateModule("Helper", "BuildValue"));
            var observer = new BlockingSnapshotCaptureObserver(
                blockedCaptureNumber: 2,
                releaseCapture.Task);
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()),
                NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
                NullVbaDocumentAnalysisBuildObserver.Instance,
                observer);
            workspace.UpdateDocument(
                callerUri,
                "Attribute VB_Name = \"Caller\"\nPublic Sub Run()\nEnd Sub");
            _ = workspace.CreateProjectSnapshot(callerUri);
            workspace.UpdateDocument(
                callerUri,
                "Attribute VB_Name = \"Caller\"\nPublic Sub Changed()\nEnd Sub");

            var staleBuild = Task.Run(
                () => workspace.CreateProjectSnapshot(callerUri));
            await observer.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(workspace.RemoveDocument(callerUri));
            releaseCapture.TrySetResult();
            _ = await staleBuild.WaitAsync(TimeSpan.FromSeconds(2));
            using var reconciliation =
                workspace.CaptureDiskReconciliationScopes();

            Assert.Empty(reconciliation.Scopes);
            Assert.Equal(0, workspace.RetainedProjectSnapshotCount);
            Assert.Equal(
                0,
                workspace.RetainedProjectScopeInvalidationStateCount);
            Assert.Equal(0, workspace.RetainedReconciliationScopeCount);
            Assert.Equal(0, workspace.RetainedReconciliationAuthorityCount);
            Assert.Equal(0, workspace.RetainedProjectDiskDocumentCount);
        }
        finally
        {
            releaseCapture.TrySetResult();
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Scope_retirement_releases_disk_cache_loaded_by_an_abandoned_build()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-scope-retirement-build-race-").FullName;
        var releaseBuild = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            WriteProjectManifest(projectRoot);
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            var callerUri = ToFileUri(Path.Combine(sourceRoot, "Caller.bas"));
            File.WriteAllText(
                Path.Combine(sourceRoot, "Helper.bas"),
                CreateModule("Helper", "BuildValue"));
            var observer = new BlockingSnapshotBuildObserver(
                blockedBuildNumber: 2,
                releaseBuild.Task);
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()),
                NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
                NullVbaDocumentAnalysisBuildObserver.Instance,
                observer);
            workspace.UpdateDocument(
                callerUri,
                "Attribute VB_Name = \"Caller\"\nPublic Sub Run()\nEnd Sub");
            _ = workspace.CreateProjectSnapshot(callerUri);
            workspace.UpdateDocument(
                callerUri,
                "Attribute VB_Name = \"Caller\"\nPublic Sub Changed()\nEnd Sub");

            var staleBuild = Task.Run(
                () => workspace.CreateProjectSnapshot(callerUri));
            await observer.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(workspace.RemoveDocument(callerUri));
            Assert.Equal(0, workspace.RetainedProjectDiskDocumentCount);

            releaseBuild.TrySetResult();
            _ = await staleBuild.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(0, workspace.RetainedProjectSnapshotCount);
            Assert.Equal(
                0,
                workspace.RetainedProjectScopeInvalidationStateCount);
            Assert.Equal(0, workspace.RetainedProjectDiskDocumentCount);
        }
        finally
        {
            releaseBuild.TrySetResult();
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Scope_retirement_does_not_reject_an_unrelated_snapshot_store()
    {
        var retiredProjectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-retired-scope-").FullName;
        var activeProjectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-concurrent-scope-").FullName;
        var releaseStore = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            WriteProjectManifest(retiredProjectRoot);
            WriteProjectManifest(activeProjectRoot);
            var retiredUri = ToFileUri(Path.Combine(
                retiredProjectRoot,
                "src",
                "Book1",
                "Retired.bas"));
            var activeUri = ToFileUri(Path.Combine(
                activeProjectRoot,
                "src",
                "Book1",
                "Active.bas"));
            var observer = new BlockingSnapshotStoreObserver(
                blockedStoreNumber: 2,
                releaseStore.Task);
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()),
                NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
                NullVbaDocumentAnalysisBuildObserver.Instance,
                observer);
            workspace.UpdateDocument(
                retiredUri,
                "Attribute VB_Name = \"Retired\"\nPublic Sub Run()\nEnd Sub");
            workspace.UpdateDocument(
                activeUri,
                "Attribute VB_Name = \"Active\"\nPublic Sub Run()\nEnd Sub");
            _ = workspace.CreateProjectSnapshot(retiredUri);

            var activeBuild = Task.Run(
                () => workspace.CreateProjectSnapshot(activeUri));
            await observer.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(workspace.RemoveDocument(retiredUri));
            releaseStore.TrySetResult();
            var active = await activeBuild.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Same(
                active,
                workspace.CreateProjectSnapshot(activeUri));
            Assert.Equal(1, workspace.RetainedProjectSnapshotCount);
        }
        finally
        {
            releaseStore.TrySetResult();
            Directory.Delete(retiredProjectRoot, recursive: true);
            Directory.Delete(activeProjectRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Last_disk_source_deletion_retires_project_state(
        bool reconciledDeletion)
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-delete-scope-retirement-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            var callerPath = Path.Combine(sourceRoot, "Caller.bas");
            var callerUri = ToFileUri(callerPath);
            var callerText =
                "Attribute VB_Name = \"Caller\"\nPublic Sub Run()\nEnd Sub";
            File.WriteAllText(callerPath, callerText);
            File.WriteAllText(
                Path.Combine(sourceRoot, "Helper.bas"),
                CreateModule("Helper", "BuildValue"));
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()));
            Assert.True(workspace.ReloadSourceDocument(
                callerUri,
                callerText));
            _ = workspace.CreateProjectSnapshot(callerUri);
            long capturedWorkspaceRevision;
            using (var capture =
                workspace.CaptureDiskReconciliationScopes())
            {
                capturedWorkspaceRevision =
                    Assert.Single(capture.Scopes)
                        .CapturedWorkspaceRevision;
            }

            Assert.True(workspace.RetainedProjectDiskDocumentCount > 0);
            File.Delete(callerPath);
            var deleted = reconciledDeletion
                ? workspace.DeleteReconciledSourceDocument(
                    callerUri,
                    capturedWorkspaceRevision)
                : workspace.DeleteSourceDocument(callerUri);

            Assert.True(deleted);
            using var retired =
                workspace.CaptureDiskReconciliationScopes();
            Assert.Empty(retired.Scopes);
            Assert.Equal(0, workspace.RetainedProjectSnapshotCount);
            Assert.Equal(
                0,
                workspace.RetainedProjectScopeInvalidationStateCount);
            Assert.Equal(0, workspace.RetainedReconciliationScopeCount);
            Assert.Equal(0, workspace.RetainedReconciliationAuthorityCount);
            Assert.Equal(0, workspace.RetainedProjectDiskDocumentCount);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Cycle_scans_only_project_scopes_that_have_been_activated_by_a_snapshot()
    {
        var firstRoot = Directory.CreateTempSubdirectory("vba-ls-reconcile-active-a-").FullName;
        var secondRoot = Directory.CreateTempSubdirectory("vba-ls-reconcile-active-b-").FullName;
        try
        {
            WriteProjectManifest(firstRoot);
            WriteProjectManifest(secondRoot);
            var firstUri = ToFileUri(
                Path.Combine(firstRoot, "src", "Book1", "Caller.bas"));
            var secondUri = ToFileUri(
                Path.Combine(secondRoot, "src", "Book1", "Caller.bas"));
            var workspace = CreateWorkspace(firstUri);
            workspace.UpdateDocument(
                secondUri,
                "Attribute VB_Name = \"Second\"\nPublic Sub Run()\nEnd Sub");
            _ = workspace.CreateProjectSnapshot(firstUri);
            var boundary = new CountingDiskSourceBoundary(
                new VbaFileSystemProjectDiskSourceBoundary());
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    sourceBoundary: boundary,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, boundary.ScanCount);
            Assert.DoesNotContain(
                boundary.AuthorityKeys,
                key => key.Contains(secondRoot, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(firstRoot, recursive: true);
            Directory.Delete(secondRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Cycle_limits_scan_concurrency_and_commits_in_stable_authority_order()
    {
        var projectRoots = Enumerable.Range(0, 5)
            .Select(_ => Directory.CreateTempSubdirectory(
                "vba-ls-reconcile-bounded-scan-").FullName)
            .ToArray();
        try
        {
            var callerUris = new List<string>();
            var helperPaths = new List<string>();
            foreach (var projectRoot in projectRoots)
            {
                WriteProjectManifest(projectRoot);
                var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
                var callerUri = ToFileUri(Path.Combine(sourceRoot, "Caller.bas"));
                var helperPath = Path.Combine(sourceRoot, "Helper.bas");
                File.WriteAllText(helperPath, CreateModule("Helper", "BuildBefore"));
                callerUris.Add(callerUri);
                helperPaths.Add(helperPath);
            }

            var workspace = CreateWorkspace(callerUris[0]);
            foreach (var callerUri in callerUris.Skip(1))
            {
                workspace.UpdateDocument(
                    callerUri,
                    "Attribute VB_Name = \"Caller\"\nPublic Sub Run()\nEnd Sub");
            }

            foreach (var callerUri in callerUris)
            {
                _ = workspace.CreateProjectSnapshot(callerUri);
            }

            foreach (var helperPath in helperPaths)
            {
                File.WriteAllText(
                    helperPath,
                    CreateModule("Helper", "BuildAfter"));
            }

            var boundary = new GatedConcurrencyDiskSourceBoundary(
                expectedScopeCount: projectRoots.Length,
                new VbaFileSystemProjectDiskSourceBoundary());
            var diagnostics = new RecordingDiagnostics();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    diagnostics,
                    sourceBoundary: boundary,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            var trigger = reconciliation.TriggerAsync();
            await boundary.FirstWaveStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(2));
            var startedEveryScopeBeforeRelease =
                boundary.AllScopesStarted.Task.IsCompleted;
            boundary.Release();
            await trigger.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(startedEveryScopeBeforeRelease);
            Assert.Equal(2, boundary.MaxConcurrency);
            var expectedTrackedUris = boundary.Scopes
                .OrderBy(
                    scope => scope.AuthorityKey,
                    StringComparer.OrdinalIgnoreCase)
                .Select(scope => scope.KnownSources.Single().Uri)
                .ToArray();
            Assert.Equal(expectedTrackedUris, diagnostics.TrackedUris);
        }
        finally
        {
            foreach (var projectRoot in projectRoots)
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Ad_hoc_scope_detects_a_manifest_later_added_in_an_ancestor_directory()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-reconcile-ancestor-manifest-").FullName;
        try
        {
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            Directory.CreateDirectory(sourceRoot);
            var callerUri = ToFileUri(Path.Combine(sourceRoot, "Caller.bas"));
            var workspace = CreateWorkspace(callerUri);
            var adHocSnapshot = workspace.CreateProjectSnapshot(callerUri);
            WriteProjectManifest(
                projectRoot,
                "src/Book1",
                "Microsoft Excel 16.0 Object Library");
            var manifestEvents = new RecordingManifestEvents();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    manifestEvents: manifestEvents,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            var manifestSnapshot = workspace.CreateProjectSnapshot(callerUri);

            Assert.Equal(VbaProjectResolutionKind.AdHoc, adHocSnapshot.Resolution.Kind);
            Assert.Equal(
                VbaProjectResolutionKind.ManifestDocument,
                manifestSnapshot.Resolution.Kind);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(projectRoot, "vba-project.json")),
                manifestSnapshot.Resolution.ManifestPath);
            Assert.Single(manifestEvents.SelectionChanges);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Disk_boundary_treats_document_kind_as_opaque_scope_data()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-reconcile-kind-").FullName;
        try
        {
            var sourcePath = Path.Combine(projectRoot, "Module1.bas");
            File.WriteAllText(sourcePath, CreateModule("Module1", "BuildValue"));
            var resolution = new VbaProjectResolution(
                VbaProjectResolutionKind.ManifestDocument,
                projectRoot,
                ManifestPath: Path.Combine(projectRoot, "vba-project.json"),
                DocumentName: "Document1",
                DocumentKind: "FutureHostDocument");
            var scope = new VbaProjectDiskReconciliationScope(
                "future-host",
                ToFileUri(sourcePath),
                resolution,
                CapturedWorkspaceRevision: 0,
                ManifestCandidates:
                [
                    new VbaProjectDiskManifestCandidate(
                        ToFileUri(Path.Combine(projectRoot, "vba-project.json")),
                        CapturedRevision: 0,
                        new VbaProjectDiskManifestBaseline(
                            Exists: false,
                            Text: null))
                ],
                KnownSources: []);

            var scan = await new VbaFileSystemProjectDiskSourceBoundary()
                .ScanAsync(scope, CancellationToken.None);

            Assert.Contains(
                scan.Sources,
                source => source.Uri == ToFileUri(sourcePath));
            Assert.Equal(
                "FutureHostDocument",
                scope.Resolution.DocumentKind);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Blocked_scan_does_not_hold_the_ordered_mutation_lane()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-reconcile-lane-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            var helperPath = Path.Combine(sourceRoot, "Helper.bas");
            var callerUri = ToFileUri(Path.Combine(sourceRoot, "Caller.bas"));
            File.WriteAllText(helperPath, CreateModule("Helper", "BuildValue"));
            var workspace = CreateWorkspace(callerUri);
            _ = workspace.CreateProjectSnapshot(callerUri);
            var boundary = new BlockingDiskSourceBoundary();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    sourceBoundary: boundary,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            var trigger = reconciliation.TriggerAsync();
            await boundary.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var mutation = await scheduler.AdmitRequiredMutationAsync(
                    "test/reconciliation-does-not-own-lane",
                    _ => Task.CompletedTask,
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
            await mutation.Completion.WaitAsync(TimeSpan.FromSeconds(2));

            boundary.Complete(CreateScan(
                boundary.Scope,
                File.ReadAllText(Path.Combine(projectRoot, "vba-project.json"))));
            await trigger.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Watcher_update_after_scan_capture_rejects_stale_source_result()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-reconcile-watcher-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            var helperPath = Path.Combine(sourceRoot, "Helper.bas");
            var helperUri = ToFileUri(helperPath);
            var callerUri = ToFileUri(Path.Combine(sourceRoot, "Caller.bas"));
            File.WriteAllText(helperPath, CreateModule("Helper", "BuildDisk"));
            var workspace = CreateWorkspace(callerUri);
            _ = workspace.CreateProjectSnapshot(callerUri);
            var boundary = new BlockingDiskSourceBoundary();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    sourceBoundary: boundary,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            var trigger = reconciliation.TriggerAsync();
            await boundary.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var watcher = await scheduler.AdmitRequiredMutationAsync(
                    "workspace/didChangeWatchedFiles",
                    _ =>
                    {
                        File.WriteAllText(
                            helperPath,
                            CreateModule("Helper", "BuildWatcherWins"));
                        workspace.ReloadSourceDocument(
                            helperUri,
                            CreateModule("Helper", "BuildWatcherWins"));
                        return Task.CompletedTask;
                    },
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
            await watcher.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            boundary.Complete(CreateScan(
                boundary.Scope,
                File.ReadAllText(Path.Combine(projectRoot, "vba-project.json")),
                (helperPath, CreateModule("Helper", "BuildStaleScan"))));

            await trigger.WaitAsync(TimeSpan.FromSeconds(5));
            var snapshot = workspace.CreateProjectSnapshot(callerUri);

            Assert.NotEmpty(
                snapshot.SemanticInventory.GetWorkspaceSymbols("BuildWatcherWins"));
            Assert.Empty(
                snapshot.SemanticInventory.GetWorkspaceSymbols("BuildStaleScan"));
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Open_buffer_after_scan_capture_rejects_stale_source_result()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-reconcile-open-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            var helperPath = Path.Combine(sourceRoot, "Helper.bas");
            var helperUri = ToFileUri(helperPath);
            var callerUri = ToFileUri(Path.Combine(sourceRoot, "Caller.bas"));
            File.WriteAllText(helperPath, CreateModule("Helper", "BuildDisk"));
            var workspace = CreateWorkspace(callerUri);
            _ = workspace.CreateProjectSnapshot(callerUri);
            var boundary = new BlockingDiskSourceBoundary();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    sourceBoundary: boundary,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            var trigger = reconciliation.TriggerAsync();
            await boundary.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var opened = await scheduler.AdmitRequiredMutationAsync(
                    "textDocument/didOpen",
                    _ =>
                    {
                        workspace.OpenDocument(
                            helperUri,
                            version: 1,
                            CreateModule("Helper", "BuildOpenWins"));
                        return Task.CompletedTask;
                    },
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
            await opened.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            boundary.Complete(CreateScan(
                boundary.Scope,
                File.ReadAllText(Path.Combine(projectRoot, "vba-project.json")),
                (helperPath, CreateModule("Helper", "BuildStaleScan"))));

            await trigger.WaitAsync(TimeSpan.FromSeconds(5));
            var snapshot = workspace.CreateProjectSnapshot(callerUri);

            Assert.NotEmpty(
                snapshot.SemanticInventory.GetWorkspaceSymbols("BuildOpenWins"));
            Assert.Empty(
                snapshot.SemanticInventory.GetWorkspaceSymbols("BuildStaleScan"));
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Close_after_scan_capture_rejects_stale_source_result()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-reconcile-close-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            var helperPath = Path.Combine(sourceRoot, "Helper.bas");
            var helperUri = ToFileUri(helperPath);
            var callerUri = ToFileUri(Path.Combine(sourceRoot, "Caller.bas"));
            File.WriteAllText(helperPath, CreateModule("Helper", "BuildDisk"));
            var workspace = CreateWorkspace(callerUri);
            workspace.OpenDocument(
                helperUri,
                version: 1,
                CreateModule("Helper", "BuildOpen"));
            _ = workspace.CreateProjectSnapshot(callerUri);
            var boundary = new BlockingDiskSourceBoundary();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    sourceBoundary: boundary,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            var trigger = reconciliation.TriggerAsync();
            await boundary.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var closed = await scheduler.AdmitRequiredMutationAsync(
                    "textDocument/didClose",
                    _ =>
                    {
                        workspace.CloseDocument(helperUri);
                        return Task.CompletedTask;
                    },
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
            await closed.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            boundary.Complete(CreateScan(
                boundary.Scope,
                File.ReadAllText(Path.Combine(projectRoot, "vba-project.json")),
                (helperPath, CreateModule("Helper", "BuildStaleScan"))));

            await trigger.WaitAsync(TimeSpan.FromSeconds(5));
            var snapshot = workspace.CreateProjectSnapshot(callerUri);

            Assert.NotEmpty(
                snapshot.SemanticInventory.GetWorkspaceSymbols("BuildDisk"));
            Assert.Empty(
                snapshot.SemanticInventory.GetWorkspaceSymbols("BuildStaleScan"));
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Retired_and_reactivated_authority_rejects_an_older_scan_incarnation()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-reconcile-authority-incarnation-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            var callerUri = ToFileUri(Path.Combine(
                sourceRoot,
                "Caller.bas"));
            var helperPath = Path.Combine(sourceRoot, "Helper.bas");
            var helperUri = ToFileUri(helperPath);
            File.WriteAllText(
                helperPath,
                CreateModule("Helper", "BuildCurrent"));
            var workspace = CreateWorkspace(callerUri);
            _ = workspace.CreateProjectSnapshot(callerUri);
            var boundary = new BlockingFirstDiskSourceBoundary(
                new VbaFileSystemProjectDiskSourceBoundary());
            var diagnostics = new RecordingDiagnostics();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    diagnostics,
                    sourceBoundary: boundary,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            var trigger = reconciliation.TriggerAsync();
            await boundary.Started.Task.WaitAsync(
                TimeSpan.FromSeconds(2));
            var retiredGeneration =
                boundary.Scope.AuthorityGeneration;
            Assert.True(workspace.RemoveDocument(callerUri));
            workspace.UpdateDocument(
                callerUri,
                "Attribute VB_Name = \"Caller\"\n"
                + "Public Sub Reactivated()\n"
                + "End Sub");
            _ = workspace.CreateProjectSnapshot(callerUri);
            using (var current =
                workspace.CaptureDiskReconciliationScopes())
            {
                Assert.NotEqual(
                    retiredGeneration,
                    Assert.Single(current.Scopes)
                        .AuthorityGeneration);
            }

            boundary.Complete(CreateScan(
                boundary.Scope,
                File.ReadAllText(Path.Combine(
                    projectRoot,
                    "vba-project.json")),
                (helperPath,
                    CreateModule("Helper", "BuildStale"))));
            await trigger.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Null(workspace.GetDocumentText(helperUri));
            Assert.DoesNotContain(helperUri, diagnostics.TrackedUris);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Reanchored_authority_rejects_a_scan_owned_by_the_closed_anchor()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-reconcile-reanchor-incarnation-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var sourceRoot = Path.Combine(
                projectRoot,
                "src",
                "Book1");
            var firstUri = ToFileUri(Path.Combine(
                sourceRoot,
                "First.bas"));
            var secondPath = Path.Combine(
                sourceRoot,
                "Second.bas");
            var secondUri = ToFileUri(secondPath);
            var secondText =
                CreateModule("Second", "RunSecond");
            File.WriteAllText(secondPath, secondText);
            var workspace = CreateWorkspace(firstUri);
            workspace.OpenDocument(
                secondUri,
                version: 1,
                secondText);
            _ = workspace.CreateProjectSnapshot(firstUri);
            var boundary = new BlockingDiskSourceBoundary();
            var diagnostics = new RecordingDiagnostics();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    diagnostics,
                    sourceBoundary: boundary,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            var trigger = reconciliation.TriggerAsync();
            await boundary.Started.Task.WaitAsync(
                TimeSpan.FromSeconds(2));
            var capturedGeneration =
                boundary.Scope.AuthorityGeneration;
            Assert.True(workspace.CloseDocument(firstUri));
            using (var current =
                workspace.CaptureDiskReconciliationScopes())
            {
                var reanchored = Assert.Single(current.Scopes);
                Assert.Equal(secondUri, reanchored.ActiveUri);
                Assert.NotEqual(
                    capturedGeneration,
                    reanchored.AuthorityGeneration);
            }

            var stalePath = Path.Combine(
                sourceRoot,
                "Stale.bas");
            var staleUri = ToFileUri(stalePath);
            boundary.Complete(CreateScan(
                boundary.Scope,
                File.ReadAllText(Path.Combine(
                    projectRoot,
                    "vba-project.json")),
                (stalePath,
                    CreateModule("Stale", "BuildStale"))));
            await trigger.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Null(workspace.GetDocumentText(staleUri));
            Assert.DoesNotContain(staleUri, diagnostics.TrackedUris);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Failed_scan_does_not_mutate_and_a_later_cycle_retries()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-reconcile-failure-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            var helperPath = Path.Combine(sourceRoot, "Helper.bas");
            var callerUri = ToFileUri(Path.Combine(sourceRoot, "Caller.bas"));
            File.WriteAllText(helperPath, CreateModule("Helper", "BuildBefore"));
            var workspace = CreateWorkspace(callerUri);
            var initial = workspace.CreateProjectSnapshot(callerUri);
            File.WriteAllText(helperPath, CreateModule("Helper", "BuildAfter"));
            var boundary = new FailOnceDiskSourceBoundary(
                new VbaFileSystemProjectDiskSourceBoundary());
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    sourceBoundary: boundary,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            await Assert.ThrowsAsync<IOException>(
                () => reconciliation.TriggerAsync());
            var stillStale = workspace.CreateProjectSnapshot(callerUri);

            Assert.Same(initial, stillStale);
            Assert.Empty(
                stillStale.SemanticInventory.GetWorkspaceSymbols("BuildAfter"));

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            var refreshed = workspace.CreateProjectSnapshot(callerUri);
            Assert.NotEmpty(
                refreshed.SemanticInventory.GetWorkspaceSymbols("BuildAfter"));
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Shutdown_discards_a_scan_result_that_arrives_late()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-reconcile-stop-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            var helperPath = Path.Combine(sourceRoot, "Helper.bas");
            var callerUri = ToFileUri(Path.Combine(sourceRoot, "Caller.bas"));
            File.WriteAllText(helperPath, CreateModule("Helper", "BuildBefore"));
            var workspace = CreateWorkspace(callerUri);
            var initial = workspace.CreateProjectSnapshot(callerUri);
            var boundary = new BlockingDiskSourceBoundary();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    sourceBoundary: boundary,
                    cadence: Timeout.InfiniteTimeSpan,
                    shutdownTimeout: TimeSpan.FromMilliseconds(50));
            reconciliation.AttachScheduler(scheduler);

            var trigger = reconciliation.TriggerAsync();
            await boundary.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var transientUri = ToFileUri(
                Path.Combine(projectRoot, "transient", "Late.bas"));
            workspace.UpdateDocument(
                transientUri,
                "Attribute VB_Name = \"Late\"\n");
            workspace.RemoveDocument(transientUri);
            Assert.True(workspace.RetainedSourceRevisionCount > 0);

            var stop = reconciliation.StopAsync();
            await stop.WaitAsync(TimeSpan.FromMilliseconds(500));
            Assert.Equal(0, workspace.RetainedSourceRevisionCount);
            boundary.Complete(CreateScan(
                boundary.Scope,
                File.ReadAllText(Path.Combine(projectRoot, "vba-project.json")),
                (helperPath, CreateModule("Helper", "BuildTooLate"))));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => trigger);
            var stillStale = workspace.CreateProjectSnapshot(callerUri);
            Assert.Same(initial, stillStale);
            Assert.Empty(
                stillStale.SemanticInventory.GetWorkspaceSymbols("BuildTooLate"));
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Shutdown_cancels_an_immediate_follow_up_scan()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-follow-up-stop-").FullName;
        try
        {
            WriteProjectManifest(
                projectRoot,
                "src/Book1",
                "Visual Basic For Applications");
            var callerUri = ToFileUri(Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Caller.bas"));
            var workspace = CreateWorkspace(callerUri);
            _ = workspace.CreateProjectSnapshot(callerUri);
            File.WriteAllText(
                Path.Combine(projectRoot, "vba-project.json"),
                CreateProjectManifestText(
                    "src",
                    "Microsoft Excel 16.0 Object Library"));
            var boundary = new BlockingSecondDiskSourceBoundary(
                new VbaFileSystemProjectDiskSourceBoundary());
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    sourceBoundary: boundary,
                    cadence: Timeout.InfiniteTimeSpan,
                    shutdownTimeout: TimeSpan.FromMilliseconds(500));
            reconciliation.AttachScheduler(scheduler);

            var trigger = reconciliation.TriggerAsync();
            await boundary.SecondScanStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            await reconciliation.StopAsync()
                .WaitAsync(TimeSpan.FromSeconds(2));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => trigger);
            Assert.Equal(2, boundary.ScanCount);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Shutdown_observes_a_noncooperative_scan_failure_that_arrives_late()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-reconcile-stop-fault-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var callerUri = ToFileUri(
                Path.Combine(projectRoot, "src", "Book1", "Caller.bas"));
            var workspace = CreateWorkspace(callerUri);
            _ = workspace.CreateProjectSnapshot(callerUri);
            var boundary = new BlockingDiskSourceBoundary();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    sourceBoundary: boundary,
                    cadence: Timeout.InfiniteTimeSpan,
                    shutdownTimeout: TimeSpan.FromMilliseconds(50));
            reconciliation.AttachScheduler(scheduler);

            var trigger = reconciliation.TriggerAsync();
            await boundary.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await reconciliation.StopAsync()
                .WaitAsync(TimeSpan.FromMilliseconds(500));

            boundary.Fail(
                new IOException("Expected late reconciliation failure."));

            var error = await Assert.ThrowsAsync<IOException>(
                () => trigger);
            Assert.Equal(
                "Expected late reconciliation failure.",
                error.Message);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Shutdown_cancels_a_commit_that_was_admitted_behind_ordered_work()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-reconcile-stop-queued-commit-").FullName;
        var releaseBlocker = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            WriteProjectManifest(projectRoot);
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            var helperPath = Path.Combine(sourceRoot, "Helper.bas");
            var callerUri = ToFileUri(Path.Combine(sourceRoot, "Caller.bas"));
            File.WriteAllText(helperPath, CreateModule("Helper", "BuildBefore"));
            var workspace = CreateWorkspace(callerUri);
            var initial = workspace.CreateProjectSnapshot(callerUri);
            var boundary = new BlockingDiskSourceBoundary();
            var timing = new CommitAdmissionTimingSink();
            await using var scheduler = new VbaInteractiveWorkScheduler(
                timing,
                options: new VbaInteractiveWorkSchedulerOptions(
                    CoalesceSupersededMutations: true,
                    MaxOwnedWork: 2));
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    sourceBoundary: boundary,
                    cadence: Timeout.InfiniteTimeSpan,
                    shutdownTimeout: TimeSpan.FromMilliseconds(50));
            reconciliation.AttachScheduler(scheduler);

            var trigger = reconciliation.TriggerAsync();
            await boundary.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var blockerStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var blocker = scheduler.AdmitMutation(
                "test/block-reconciliation-commit",
                async _ =>
                {
                    blockerStarted.TrySetResult();
                    await releaseBlocker.Task;
                });
            await blockerStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            boundary.Complete(CreateScan(
                boundary.Scope,
                File.ReadAllText(Path.Combine(projectRoot, "vba-project.json")),
                (helperPath, CreateModule("Helper", "BuildTooLate"))));
            await timing.CommitAdmitted.Task
                .WaitAsync(TimeSpan.FromSeconds(2));

            await reconciliation.StopAsync()
                .WaitAsync(TimeSpan.FromMilliseconds(500));
            releaseBlocker.TrySetResult();
            await blocker.Completion.WaitAsync(TimeSpan.FromSeconds(2));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => trigger);
            var stillStale = workspace.CreateProjectSnapshot(callerUri);
            Assert.Same(initial, stillStale);
            Assert.Empty(
                stillStale.SemanticInventory.GetWorkspaceSymbols("BuildTooLate"));
        }
        finally
        {
            releaseBlocker.TrySetResult();
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Shutdown_timeout_includes_a_blocking_cancellation_callback()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-reconcile-stop-callback-").FullName;
        using var boundary =
            new CancellationCallbackBlockingDiskSourceBoundary();
        try
        {
            WriteProjectManifest(projectRoot);
            var callerUri = ToFileUri(
                Path.Combine(projectRoot, "src", "Book1", "Caller.bas"));
            var workspace = CreateWorkspace(callerUri);
            _ = workspace.CreateProjectSnapshot(callerUri);
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    sourceBoundary: boundary,
                    cadence: Timeout.InfiniteTimeSpan,
                    shutdownTimeout: TimeSpan.FromMilliseconds(50));
            reconciliation.AttachScheduler(scheduler);

            var trigger = reconciliation.TriggerAsync();
            await boundary.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var stop = Task.Run(() => reconciliation.StopAsync());
            await boundary.CancellationStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(2));
            try
            {
                await stop.WaitAsync(TimeSpan.FromMilliseconds(500));
            }
            finally
            {
                boundary.ReleaseCancellationCallback();
            }

            boundary.Complete(CreateScan(
                boundary.Scope,
                File.ReadAllText(Path.Combine(projectRoot, "vba-project.json"))));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => trigger);
        }
        finally
        {
            boundary.ReleaseCancellationCallback();
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Shutdown_observer_consumes_a_late_fault_when_the_trigger_is_abandoned()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-reconcile-stop-observer-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var callerUri = ToFileUri(
                Path.Combine(projectRoot, "src", "Book1", "Caller.bas"));
            var workspace = CreateWorkspace(callerUri);
            _ = workspace.CreateProjectSnapshot(callerUri);
            var boundary = new BlockingDiskSourceBoundary();
            var failures = new RecordingReconciliationFailures();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    sourceBoundary: boundary,
                    cadence: Timeout.InfiniteTimeSpan,
                    shutdownTimeout: TimeSpan.FromMilliseconds(50),
                    failureObserver: failures);
            reconciliation.AttachScheduler(scheduler);

            _ = reconciliation.TriggerAsync();
            await boundary.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await reconciliation.StopAsync()
                .WaitAsync(TimeSpan.FromMilliseconds(500));
            boundary.Fail(
                new IOException("Expected abandoned reconciliation failure."));

            var error = await failures.Observed.Task
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(
                "Expected abandoned reconciliation failure.",
                error.Message);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Watched_nearer_manifest_replaces_the_warm_outer_authority()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-nearer-manifest-watched-").FullName;
        try
        {
            WriteProjectManifest(
                projectRoot,
                "src",
                "Visual Basic For Applications");
            var callerUri = ToFileUri(Path.Combine(
                projectRoot,
                "src",
                "Nested",
                "Book",
                "Caller.bas"));
            var workspace = CreateWorkspace(callerUri);
            var outer = workspace.CreateProjectSnapshot(callerUri);
            var nestedRoot = Path.Combine(projectRoot, "src", "Nested");
            var nestedPath = Path.Combine(nestedRoot, "vba-project.json");
            Directory.CreateDirectory(nestedRoot);
            File.WriteAllText(
                nestedPath,
                CreateProjectManifestText(
                    "Book",
                    "Microsoft Excel 16.0 Object Library"));

            Assert.True(
                workspace.ManifestWorkspace.ReloadManifest(
                    ToFileUri(nestedPath)));
            var nested = workspace.CreateProjectSnapshot(callerUri);

            Assert.Equal(
                Path.GetFullPath(Path.Combine(projectRoot, "vba-project.json")),
                outer.Resolution.ManifestPath);
            Assert.Equal(Path.GetFullPath(nestedPath), nested.Resolution.ManifestPath);
            Assert.Equal(
                "Microsoft Excel 16.0 Object Library",
                Assert.Single(nested.Resolution.ReferenceEntries).Name);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Warm_ad_hoc_retention_keeps_a_new_parent_manifest_barrier()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-warm-ad-hoc-parent-manifest-").FullName;
        try
        {
            var callerPath = Path.Combine(
                projectRoot,
                "src",
                "live",
                "caller",
                "Caller.bas");
            Directory.CreateDirectory(Path.GetDirectoryName(callerPath)!);
            File.WriteAllText(callerPath, CreateModule("Caller", "Run"));
            var callerUri = ToFileUri(callerPath);
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()));
            var before = workspace.CreateProjectSnapshot(callerUri);
            Assert.Equal(VbaProjectResolutionKind.AdHoc, before.Resolution.Kind);

            WriteProjectManifest(projectRoot, "src/live");
            var manifestPath = Path.Combine(projectRoot, "vba-project.json");
            var manifestUri = ToFileUri(manifestPath);
            Assert.True(
                workspace.ManifestWorkspace.ReloadManifest(manifestUri));
            Assert.True(
                workspace.ManifestWorkspace.TryGetEffectiveManifest(
                    manifestUri,
                    out _,
                    out _,
                    out _));

            workspace.RetireInactiveManifestState();
            var after = workspace.CreateProjectSnapshot(callerUri);

            Assert.Equal(
                VbaProjectResolutionKind.ManifestDocument,
                after.Resolution.Kind);
            Assert.Equal(
                Path.GetFullPath(manifestPath),
                after.Resolution.ManifestPath);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Reconciliation_discovers_a_missed_nearer_manifest()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-nearer-manifest-reconcile-").FullName;
        try
        {
            WriteProjectManifest(
                projectRoot,
                "src",
                "Visual Basic For Applications");
            var callerUri = ToFileUri(Path.Combine(
                projectRoot,
                "src",
                "Nested",
                "Book",
                "Caller.bas"));
            var workspace = CreateWorkspace(callerUri);
            var outer = workspace.CreateProjectSnapshot(callerUri);
            var nestedRoot = Path.Combine(projectRoot, "src", "Nested");
            var nestedPath = Path.Combine(nestedRoot, "vba-project.json");
            Directory.CreateDirectory(nestedRoot);
            File.WriteAllText(
                nestedPath,
                CreateProjectManifestText(
                    "Book",
                    "Microsoft Excel 16.0 Object Library"));
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            var nested = workspace.CreateProjectSnapshot(callerUri);

            Assert.Equal(
                Path.GetFullPath(Path.Combine(projectRoot, "vba-project.json")),
                outer.Resolution.ManifestPath);
            Assert.Equal(Path.GetFullPath(nestedPath), nested.Resolution.ManifestPath);
            Assert.Equal(
                "Microsoft Excel 16.0 Object Library",
                Assert.Single(nested.Resolution.ReferenceEntries).Name);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Reconciliation_deletes_a_missed_nearer_manifest_before_falling_back_to_outer()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-nearer-manifest-delete-").FullName;
        try
        {
            WriteProjectManifest(projectRoot, "src");
            var nestedProjectRoot = Path.Combine(
                projectRoot,
                "src",
                "NestedProject");
            WriteProjectManifest(nestedProjectRoot);
            var innerPath = Path.Combine(
                nestedProjectRoot,
                "src",
                "Book1",
                "Inner.bas");
            var innerUri = ToFileUri(innerPath);
            File.WriteAllText(
                innerPath,
                CreateModule("Inner", "RunInner"));
            var nestedManifestPath = Path.Combine(
                nestedProjectRoot,
                "vba-project.json");
            var nestedManifestUri = ToFileUri(nestedManifestPath);
            var outerManifestPath = Path.Combine(
                projectRoot,
                "vba-project.json");
            var workspace = CreateWorkspace(innerUri);
            var nested =
                workspace.CreateProjectSnapshot(innerUri);
            Assert.True(
                workspace.ManifestWorkspace
                    .CaptureReconciliationState(nestedManifestUri)
                    .Baseline
                    .Exists);
            Assert.Equal(
                1,
                workspace.ManifestWorkspace.RetainedLastKnownGoodCount);
            File.Delete(nestedManifestPath);
            var manifestEvents = new RecordingManifestEvents();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    manifestEvents: manifestEvents,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            using (var reconciledCapture =
                workspace.CaptureDiskReconciliationScopes())
            {
                Assert.Equal(
                    Path.GetFullPath(outerManifestPath),
                    Assert.Single(reconciledCapture.Scopes)
                        .Resolution
                        .ManifestPath);
            }

            var outer =
                workspace.CreateProjectSnapshot(innerUri);
            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            var stableOuter =
                workspace.CreateProjectSnapshot(innerUri);

            Assert.Equal(
                Path.GetFullPath(nestedManifestPath),
                nested.Resolution.ManifestPath);
            Assert.Equal(
                Path.GetFullPath(outerManifestPath),
                outer.Resolution.ManifestPath);
            Assert.Equal(
                Path.GetFullPath(outerManifestPath),
                stableOuter.Resolution.ManifestPath);
            Assert.False(
                workspace.ManifestWorkspace
                    .CaptureReconciliationState(nestedManifestUri)
                    .Baseline
                    .Exists);
            Assert.Equal(
                Path.GetFullPath(outerManifestPath),
                workspace.ManifestWorkspace
                    .Resolve(innerUri)
                    .ManifestPath);
            Assert.Contains(
                nestedManifestUri,
                manifestEvents.DeletedUris);
            Assert.Contains(
                manifestEvents.SelectionChanges,
                change => change.Uri == ToFileUri(outerManifestPath));
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Source_opened_after_authority_deletion_scan_receives_outer_transfer()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-delete-authority-open-race-").FullName;
        try
        {
            WriteProjectManifest(projectRoot, "src");
            var outerManifestUri = ToFileUri(Path.Combine(
                projectRoot,
                "vba-project.json"));
            var nestedRoot = Path.Combine(
                projectRoot,
                "src",
                "NestedProject");
            WriteProjectManifest(nestedRoot);
            var nestedManifestPath = Path.Combine(
                nestedRoot,
                "vba-project.json");
            var nestedManifestUri = ToFileUri(
                nestedManifestPath);
            var firstPath = Path.Combine(
                nestedRoot,
                "src",
                "Book1",
                "First.bas");
            var firstUri = ToFileUri(firstPath);
            var secondPath = Path.Combine(
                nestedRoot,
                "src",
                "Book1",
                "Second.bas");
            var secondUri = ToFileUri(secondPath);
            var secondText =
                CreateModule("Second", "RunSecond");
            var loosePath = Path.Combine(
                nestedRoot,
                "Loose.bas");
            var looseUri = ToFileUri(loosePath);
            var looseText =
                CreateModule("Loose", "RunLoose");
            File.WriteAllText(
                firstPath,
                CreateModule("First", "RunFirst"));
            File.WriteAllText(secondPath, secondText);
            File.WriteAllText(loosePath, looseText);
            var workspace = CreateWorkspace(firstUri);
            Assert.True(
                workspace.ManifestWorkspace
                    .TryGetEffectiveManifest(
                        outerManifestUri,
                        out _,
                        out _,
                        out _));
            _ = workspace.CreateProjectSnapshot(firstUri);
            File.Delete(nestedManifestPath);
            var boundary = new BlockingDiskSourceBoundary();
            var manifestEvents = new RecordingManifestEvents();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    manifestEvents: manifestEvents,
                    sourceBoundary: boundary,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            var pending = reconciliation.TriggerAsync();
            await boundary.Started.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            var scan =
                await new VbaFileSystemProjectDiskSourceBoundary()
                    .ScanAsync(
                        boundary.Scope,
                        CancellationToken.None);
            workspace.UpdateDocument(secondUri, secondText);
            workspace.UpdateDocument(looseUri, looseText);
            boundary.Complete(scan);
            await pending.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(
                [firstUri, secondUri, looseUri],
                manifestEvents.AuthorityTransferredSourceUris);
            Assert.Contains(
                nestedManifestUri,
                manifestEvents.DeletedUris);
            Assert.Equal(
                Path.GetFullPath(
                    VbaProjectResolver.TryGetLocalPath(
                        outerManifestUri)!),
                workspace.ManifestWorkspace
                    .Resolve(secondUri)
                    .ManifestPath);
            Assert.Equal(
                Path.GetFullPath(
                    VbaProjectResolver.TryGetLocalPath(
                        outerManifestUri)!),
                workspace.ManifestWorkspace
                    .Resolve(looseUri)
                    .ManifestPath);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Nearer_manifest_deletion_falls_back_to_the_effective_outer_overlay()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-nearer-delete-outer-overlay-").FullName;
        try
        {
            WriteProjectManifest(
                projectRoot,
                "src",
                "Visual Basic For Applications");
            var outerManifestPath = Path.Combine(
                projectRoot,
                "vba-project.json");
            var outerManifestUri = ToFileUri(outerManifestPath);
            var nestedProjectRoot = Path.Combine(
                projectRoot,
                "src",
                "NestedProject");
            WriteProjectManifest(
                nestedProjectRoot,
                "src/Book1",
                "Microsoft Office 16.0 Object Library");
            var nestedManifestPath = Path.Combine(
                nestedProjectRoot,
                "vba-project.json");
            var nestedManifestUri = ToFileUri(nestedManifestPath);
            var innerPath = Path.Combine(
                nestedProjectRoot,
                "src",
                "Book1",
                "Inner.bas");
            var innerUri = ToFileUri(innerPath);
            File.WriteAllText(
                innerPath,
                CreateModule("Inner", "RunInner"));
            var workspace = CreateWorkspace(innerUri);
            var outerOverlay = workspace.ManifestWorkspace.OpenManifest(
                outerManifestUri,
                documentVersion: 1,
                CreateProjectManifestText(
                    "src",
                    "Microsoft Excel 16.0 Object Library"));
            Assert.True(outerOverlay.Accepted);
            var nested =
                workspace.CreateProjectSnapshot(innerUri);
            File.Delete(nestedManifestPath);
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            using var capture =
                workspace.CaptureDiskReconciliationScopes();
            var effectiveOuter = Assert.Single(capture.Scopes);
            Assert.Equal(
                Path.GetFullPath(nestedManifestPath),
                nested.Resolution.ManifestPath);
            Assert.Equal(
                Path.GetFullPath(outerManifestPath),
                effectiveOuter.Resolution.ManifestPath);
            Assert.Equal(
                "Microsoft Excel 16.0 Object Library",
                Assert.Single(
                    effectiveOuter.Resolution.ReferenceEntries).Name);
            Assert.False(
                workspace.ManifestWorkspace
                    .CaptureReconciliationState(nestedManifestUri)
                    .Baseline
                    .Exists);
            Assert.True(
                workspace.ManifestWorkspace
                    .CaptureReconciliationState(outerManifestUri)
                    .HasOpenOverlay);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Nearer_manifest_deletion_prefers_middle_disk_authority_over_farther_overlay()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-nearer-delete-middle-authority-").FullName;
        try
        {
            WriteProjectManifest(
                projectRoot,
                "src",
                "Visual Basic For Applications");
            var outerManifestUri = ToFileUri(Path.Combine(
                projectRoot,
                "vba-project.json"));
            var middleRoot = Path.Combine(
                projectRoot,
                "src",
                "MiddleProject");
            WriteProjectManifest(
                middleRoot,
                "src",
                "Microsoft Office 16.0 Object Library");
            var middleManifestPath = Path.Combine(
                middleRoot,
                "vba-project.json");
            var innerRoot = Path.Combine(
                middleRoot,
                "src",
                "InnerProject");
            WriteProjectManifest(
                innerRoot,
                "src/Book1",
                "Visual Basic For Applications");
            var innerManifestPath = Path.Combine(
                innerRoot,
                "vba-project.json");
            var innerPath = Path.Combine(
                innerRoot,
                "src",
                "Book1",
                "Inner.bas");
            var innerUri = ToFileUri(innerPath);
            File.WriteAllText(
                innerPath,
                CreateModule("Inner", "RunInner"));
            var workspace = CreateWorkspace(innerUri);
            var outerOverlay = workspace.ManifestWorkspace.OpenManifest(
                outerManifestUri,
                documentVersion: 1,
                CreateProjectManifestText(
                    "src",
                    "Microsoft Excel 16.0 Object Library"));
            Assert.True(outerOverlay.Accepted);
            _ = workspace.CreateProjectSnapshot(innerUri);
            File.Delete(innerManifestPath);
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            using var capture =
                workspace.CaptureDiskReconciliationScopes();
            var middle = Assert.Single(capture.Scopes);
            Assert.Equal(
                Path.GetFullPath(middleManifestPath),
                middle.Resolution.ManifestPath);
            Assert.Equal(
                "Microsoft Office 16.0 Object Library",
                Assert.Single(
                    middle.Resolution.ReferenceEntries).Name);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Compound_nearer_deletion_commits_the_effective_missing_overlay_authority()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-compound-nearer-delete-overlay-").FullName;
        try
        {
            WriteProjectManifest(
                projectRoot,
                "src",
                "Visual Basic For Applications");
            var rootManifestPath = Path.Combine(
                projectRoot,
                "vba-project.json");
            var rootManifestUri = ToFileUri(rootManifestPath);
            var middleRoot = Path.Combine(
                projectRoot,
                "src",
                "MiddleProject");
            WriteProjectManifest(
                middleRoot,
                "src",
                "Visual Basic For Applications");
            var middleManifestPath = Path.Combine(
                middleRoot,
                "vba-project.json");
            var middleManifestUri = ToFileUri(middleManifestPath);
            var innerRoot = Path.Combine(
                middleRoot,
                "src",
                "InnerProject");
            WriteProjectManifest(
                innerRoot,
                "src/Book1",
                "Visual Basic For Applications");
            var innerManifestPath = Path.Combine(
                innerRoot,
                "vba-project.json");
            var innerManifestUri = ToFileUri(innerManifestPath);
            var innerPath = Path.Combine(
                innerRoot,
                "src",
                "Book1",
                "Inner.bas");
            var innerUri = ToFileUri(innerPath);
            File.WriteAllText(
                innerPath,
                CreateModule("Inner", "RunInner"));
            var workspace = CreateWorkspace(innerUri);
            Assert.True(
                workspace.ManifestWorkspace.TryGetEffectiveManifest(
                    rootManifestUri,
                    out _,
                    out _,
                    out _));
            Assert.True(
                workspace.ManifestWorkspace.TryGetEffectiveManifest(
                    middleManifestUri,
                    out _,
                    out _,
                    out _));
            _ = workspace.CreateProjectSnapshot(innerUri);
            var middleOverlay = workspace.ManifestWorkspace.OpenManifest(
                middleManifestUri,
                documentVersion: 1,
                CreateProjectManifestText(
                    "src",
                    "Microsoft Office 16.0 Object Library"));
            Assert.True(middleOverlay.Accepted);
            File.WriteAllText(
                rootManifestPath,
                CreateProjectManifestText(
                    "src",
                    "Microsoft Excel 16.0 Object Library"));
            File.Delete(innerManifestPath);
            File.Delete(middleManifestPath);
            var manifestEvents = new RecordingManifestEvents();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    manifestEvents: manifestEvents,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            using (var capture =
                workspace.CaptureDiskReconciliationScopes())
            {
                var effectiveMiddle = Assert.Single(capture.Scopes);
                Assert.Equal(
                    Path.GetFullPath(middleManifestPath),
                    effectiveMiddle.Resolution.ManifestPath);
                Assert.Equal(
                    "Microsoft Office 16.0 Object Library",
                    Assert.Single(
                        effectiveMiddle.Resolution.ReferenceEntries)
                        .Name);
            }
            Assert.False(
                workspace.ManifestWorkspace
                    .GetReconciliationBaseline(innerManifestUri)
                    .Exists);
            Assert.False(
                workspace.ManifestWorkspace
                    .GetReconciliationBaseline(middleManifestUri)
                    .Exists);
            Assert.Equal(
                [innerManifestUri],
                manifestEvents.DeletedUris);
            Assert.Empty(manifestEvents.SelectionChanges);

            Assert.True(
                workspace.ManifestWorkspace.CloseManifest(
                    middleManifestUri));
            var fallback = workspace.ManifestWorkspace
                .CaptureResolution(innerUri)
                .Resolution;
            Assert.Equal(
                Path.GetFullPath(rootManifestPath),
                fallback.ManifestPath);
            Assert.Equal(
                "Microsoft Excel 16.0 Object Library",
                Assert.Single(fallback.ReferenceEntries).Name);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task No_disk_manifest_keeps_the_nearest_effective_missing_overlay()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-no-disk-manifest-overlay-").FullName;
        try
        {
            WriteProjectManifest(
                projectRoot,
                "src",
                "Visual Basic For Applications");
            var outerManifestPath = Path.Combine(
                projectRoot,
                "vba-project.json");
            var outerManifestUri = ToFileUri(outerManifestPath);
            var innerRoot = Path.Combine(
                projectRoot,
                "src",
                "InnerProject");
            WriteProjectManifest(
                innerRoot,
                "src/Book1",
                "Visual Basic For Applications");
            var innerManifestPath = Path.Combine(
                innerRoot,
                "vba-project.json");
            var innerManifestUri = ToFileUri(innerManifestPath);
            var innerPath = Path.Combine(
                innerRoot,
                "src",
                "Book1",
                "Inner.bas");
            var innerUri = ToFileUri(innerPath);
            File.WriteAllText(
                innerPath,
                CreateModule("Inner", "RunInner"));
            var workspace = CreateWorkspace(innerUri);
            Assert.True(
                workspace.ManifestWorkspace.TryGetEffectiveManifest(
                    outerManifestUri,
                    out _,
                    out _,
                    out _));
            var outerOverlay = workspace.ManifestWorkspace.OpenManifest(
                outerManifestUri,
                documentVersion: 1,
                CreateProjectManifestText(
                    "src",
                    "Microsoft Excel 16.0 Object Library"));
            Assert.True(outerOverlay.Accepted);
            _ = workspace.CreateProjectSnapshot(innerUri);
            File.Delete(innerManifestPath);
            File.Delete(outerManifestPath);
            var manifestEvents = new RecordingManifestEvents();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    manifestEvents: manifestEvents,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            using var capture =
                workspace.CaptureDiskReconciliationScopes();
            var effectiveOuter = Assert.Single(capture.Scopes);
            Assert.Equal(
                Path.GetFullPath(outerManifestPath),
                effectiveOuter.Resolution.ManifestPath);
            Assert.Equal(
                "Microsoft Excel 16.0 Object Library",
                Assert.Single(
                    effectiveOuter.Resolution.ReferenceEntries).Name);
            Assert.False(
                workspace.ManifestWorkspace
                    .GetReconciliationBaseline(innerManifestUri)
                    .Exists);
            Assert.False(
                workspace.ManifestWorkspace
                    .GetReconciliationBaseline(outerManifestUri)
                    .Exists);
            Assert.Equal(
                [innerManifestUri],
                manifestEvents.DeletedUris);
            Assert.Equal(
                [innerUri],
                manifestEvents.AuthorityTransferredSourceUris);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task No_disk_manifest_atomically_deletes_every_stale_candidate()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-no-disk-manifest-all-stale-").FullName;
        try
        {
            WriteProjectManifest(projectRoot, "src");
            var outerManifestUri = ToFileUri(Path.Combine(
                projectRoot,
                "vba-project.json"));
            var innerRoot = Path.Combine(
                projectRoot,
                "src",
                "InnerProject");
            WriteProjectManifest(innerRoot, "src/Book1");
            var innerManifestPath = Path.Combine(
                innerRoot,
                "vba-project.json");
            var innerManifestUri = ToFileUri(innerManifestPath);
            var innerPath = Path.Combine(
                innerRoot,
                "src",
                "Book1",
                "Inner.bas");
            var innerUri = ToFileUri(innerPath);
            File.WriteAllText(
                innerPath,
                CreateModule("Inner", "RunInner"));
            var workspace = CreateWorkspace(innerUri);
            Assert.True(
                workspace.ManifestWorkspace.TryGetEffectiveManifest(
                    outerManifestUri,
                    out _,
                    out _,
                    out _));
            _ = workspace.CreateProjectSnapshot(innerUri);
            File.Delete(
                VbaProjectResolver.TryGetLocalPath(
                    outerManifestUri)!);
            File.Delete(innerManifestPath);
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            using var capture =
                workspace.CaptureDiskReconciliationScopes();
            var adHoc = Assert.Single(capture.Scopes);
            Assert.Equal(
                VbaProjectResolutionKind.AdHoc,
                adHoc.Resolution.Kind);
            Assert.False(
                workspace.ManifestWorkspace
                    .GetReconciliationBaseline(innerManifestUri)
                    .Exists);
            Assert.False(
                workspace.ManifestWorkspace
                    .GetReconciliationBaseline(outerManifestUri)
                    .Exists);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Late_open_under_farther_compound_deleted_manifest_receives_authority_transfer()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-compound-delete-late-outer-open-").FullName;
        try
        {
            WriteProjectManifest(projectRoot, "src");
            var outerManifestPath = Path.Combine(
                projectRoot,
                "vba-project.json");
            var outerManifestUri = ToFileUri(outerManifestPath);
            var peerPath = Path.Combine(
                projectRoot,
                "src",
                "Peer.bas");
            var peerUri = ToFileUri(peerPath);
            var peerText = CreateModule("Peer", "RunPeer");
            File.WriteAllText(peerPath, peerText);
            var innerRoot = Path.Combine(
                projectRoot,
                "src",
                "InnerProject");
            WriteProjectManifest(innerRoot, "src/Book1");
            var innerManifestPath = Path.Combine(
                innerRoot,
                "vba-project.json");
            var innerManifestUri = ToFileUri(innerManifestPath);
            var innerPath = Path.Combine(
                innerRoot,
                "src",
                "Book1",
                "Inner.bas");
            var innerUri = ToFileUri(innerPath);
            File.WriteAllText(
                innerPath,
                CreateModule("Inner", "RunInner"));
            var workspace = CreateWorkspace(innerUri);
            Assert.True(
                workspace.ManifestWorkspace.TryGetEffectiveManifest(
                    outerManifestUri,
                    out _,
                    out _,
                    out _));
            _ = workspace.CreateProjectSnapshot(innerUri);
            File.Delete(outerManifestPath);
            File.Delete(innerManifestPath);
            var boundary = new BlockingDiskSourceBoundary();
            var manifestEvents = new RecordingManifestEvents();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    manifestEvents: manifestEvents,
                    sourceBoundary: boundary,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            var pending = reconciliation.TriggerAsync();
            await boundary.Started.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            var scan =
                await new VbaFileSystemProjectDiskSourceBoundary()
                    .ScanAsync(
                        boundary.Scope,
                        CancellationToken.None);
            workspace.UpdateDocument(peerUri, peerText);
            boundary.Complete(scan);
            await pending.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(
                [innerUri, peerUri],
                manifestEvents.AuthorityTransferredSourceUris);
            Assert.Contains(
                innerManifestUri,
                manifestEvents.DeletedUris);
            Assert.Contains(
                outerManifestUri,
                manifestEvents.DeletedUris);
            Assert.Equal(
                VbaProjectResolutionKind.AdHoc,
                workspace.ManifestWorkspace
                    .Resolve(peerUri).Kind);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Rejected_compound_manifest_replacement_is_retried_immediately()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-rejected-compound-retry-").FullName;
        try
        {
            WriteProjectManifest(projectRoot, "src");
            var outerManifestPath = Path.Combine(
                projectRoot,
                "vba-project.json");
            var outerManifestUri = ToFileUri(outerManifestPath);
            var innerRoot = Path.Combine(
                projectRoot,
                "src",
                "InnerProject");
            WriteProjectManifest(innerRoot, "src/Book1");
            var innerManifestPath = Path.Combine(
                innerRoot,
                "vba-project.json");
            var innerManifestUri = ToFileUri(innerManifestPath);
            var innerPath = Path.Combine(
                innerRoot,
                "src",
                "Book1",
                "Inner.bas");
            var innerUri = ToFileUri(innerPath);
            File.WriteAllText(
                innerPath,
                CreateModule("Inner", "RunInner"));
            var workspace = CreateWorkspace(innerUri);
            Assert.True(
                workspace.ManifestWorkspace.TryGetEffectiveManifest(
                    outerManifestUri,
                    out _,
                    out _,
                    out _));
            _ = workspace.CreateProjectSnapshot(innerUri);
            File.Delete(outerManifestPath);
            File.Delete(innerManifestPath);
            var boundary = new BlockingFirstDiskSourceBoundary(
                new VbaFileSystemProjectDiskSourceBoundary());
            var manifestEvents = new RecordingManifestEvents();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    manifestEvents: manifestEvents,
                    sourceBoundary: boundary,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            var pending = reconciliation.TriggerAsync();
            await boundary.Started.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            var staleScan =
                await new VbaFileSystemProjectDiskSourceBoundary()
                    .ScanAsync(
                        boundary.Scope,
                        CancellationToken.None);
            Assert.True(
                workspace.ManifestWorkspace
                    .ReloadManifest(innerManifestUri));
            boundary.Complete(staleScan);
            await pending.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(boundary.ScanCount >= 2);
            using var capture =
                workspace.CaptureDiskReconciliationScopes();
            Assert.Equal(
                VbaProjectResolutionKind.AdHoc,
                Assert.Single(capture.Scopes)
                    .Resolution.Kind);
            Assert.False(
                workspace.ManifestWorkspace
                    .GetReconciliationBaseline(outerManifestUri)
                    .Exists);
            Assert.Contains(
                outerManifestUri,
                manifestEvents.DeletedUris);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Invalid_nearer_manifest_without_last_known_good_falls_back_to_outer_overlay()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-invalid-nearer-outer-overlay-").FullName;
        try
        {
            WriteProjectManifest(
                projectRoot,
                "src",
                "Visual Basic For Applications");
            var outerManifestPath = Path.Combine(
                projectRoot,
                "vba-project.json");
            var outerManifestUri = ToFileUri(outerManifestPath);
            var innerRoot = Path.Combine(
                projectRoot,
                "src",
                "InnerProject");
            WriteProjectManifest(
                innerRoot,
                "src/Book1",
                "Microsoft Office 16.0 Object Library");
            var innerManifestPath = Path.Combine(
                innerRoot,
                "vba-project.json");
            var innerManifestUri = ToFileUri(innerManifestPath);
            var innerPath = Path.Combine(
                innerRoot,
                "src",
                "Book1",
                "Inner.bas");
            var innerUri = ToFileUri(innerPath);
            File.WriteAllText(
                innerPath,
                CreateModule("Inner", "RunInner"));
            var workspace = CreateWorkspace(innerUri);
            var inner =
                workspace.CreateProjectSnapshot(innerUri);
            var outerOverlay = workspace.ManifestWorkspace.OpenManifest(
                outerManifestUri,
                documentVersion: 1,
                CreateProjectManifestText(
                    "src",
                    "Microsoft Excel 16.0 Object Library"));
            Assert.True(outerOverlay.Accepted);
            workspace.ManifestWorkspace
                .RetireInactiveState([], []);
            File.WriteAllText(
                innerManifestPath,
                "{\"schemaVersion\":");
            var manifestEvents = new RecordingManifestEvents();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    manifestEvents: manifestEvents,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            using var capture =
                workspace.CaptureDiskReconciliationScopes();
            var outer = Assert.Single(capture.Scopes);
            Assert.Equal(
                Path.GetFullPath(innerManifestPath),
                inner.Resolution.ManifestPath);
            Assert.Equal(
                Path.GetFullPath(outerManifestPath),
                outer.Resolution.ManifestPath);
            Assert.Equal(
                "Microsoft Excel 16.0 Object Library",
                Assert.Single(
                    outer.Resolution.ReferenceEntries).Name);
            Assert.Single(
                manifestEvents.ValidationFailures,
                failure => failure.Uri == innerManifestUri);
            Assert.Equal(
                [innerUri],
                manifestEvents.AuthorityTransferredSourceUris);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Cold_outer_disk_manifest_replaces_invalid_nearer_authority()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-invalid-nearer-cold-outer-").FullName;
        try
        {
            WriteProjectManifest(
                projectRoot,
                "src",
                "Visual Basic For Applications");
            var outerManifestPath = Path.Combine(
                projectRoot,
                "vba-project.json");
            var outerManifestUri = ToFileUri(outerManifestPath);
            var innerRoot = Path.Combine(
                projectRoot,
                "src",
                "InnerProject");
            WriteProjectManifest(
                innerRoot,
                "src/Book1",
                "Microsoft Office 16.0 Object Library");
            var innerManifestPath = Path.Combine(
                innerRoot,
                "vba-project.json");
            var innerManifestUri = ToFileUri(innerManifestPath);
            var innerPath = Path.Combine(
                innerRoot,
                "src",
                "Book1",
                "Inner.bas");
            var innerUri = ToFileUri(innerPath);
            File.WriteAllText(
                innerPath,
                CreateModule("Inner", "RunInner"));
            var workspace = CreateWorkspace(innerUri);
            var inner =
                workspace.CreateProjectSnapshot(innerUri);
            Assert.False(
                workspace.ManifestWorkspace
                    .GetReconciliationBaseline(outerManifestUri)
                    .Exists);
            workspace.ManifestWorkspace
                .RetireInactiveState([], []);
            File.WriteAllText(
                innerManifestPath,
                "{\"schemaVersion\":");
            var manifestEvents = new RecordingManifestEvents();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    manifestEvents: manifestEvents,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            using var capture =
                workspace.CaptureDiskReconciliationScopes();
            var outer = Assert.Single(capture.Scopes);
            Assert.Equal(
                Path.GetFullPath(innerManifestPath),
                inner.Resolution.ManifestPath);
            Assert.Equal(
                Path.GetFullPath(outerManifestPath),
                outer.Resolution.ManifestPath);
            Assert.Equal(
                "Visual Basic For Applications",
                Assert.Single(
                    outer.Resolution.ReferenceEntries).Name);
            Assert.True(
                workspace.ManifestWorkspace
                    .GetReconciliationBaseline(outerManifestUri)
                    .Exists);
            Assert.Single(
                manifestEvents.ValidationFailures,
                failure => failure.Uri == innerManifestUri);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Stacked_invalid_barriers_reach_a_cold_outer_manifest_in_one_trigger()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-stacked-invalid-cold-outer-").FullName;
        try
        {
            WriteProjectManifest(
                projectRoot,
                "src",
                "Visual Basic For Applications");
            var outerManifestPath = Path.Combine(
                projectRoot,
                "vba-project.json");
            var outerManifestUri = ToFileUri(outerManifestPath);
            var middleRoot = Path.Combine(
                projectRoot,
                "src",
                "MiddleProject");
            WriteProjectManifest(
                middleRoot,
                "src",
                "Microsoft Office 16.0 Object Library");
            var middleManifestPath = Path.Combine(
                middleRoot,
                "vba-project.json");
            var middleManifestUri = ToFileUri(middleManifestPath);
            var innerRoot = Path.Combine(
                middleRoot,
                "src",
                "InnerProject");
            WriteProjectManifest(
                innerRoot,
                "src/Book1",
                "Microsoft Excel 16.0 Object Library");
            var innerManifestPath = Path.Combine(
                innerRoot,
                "vba-project.json");
            var innerManifestUri = ToFileUri(innerManifestPath);
            var innerPath = Path.Combine(
                innerRoot,
                "src",
                "Book1",
                "Inner.bas");
            var innerUri = ToFileUri(innerPath);
            File.WriteAllText(
                innerPath,
                CreateModule("Inner", "RunInner"));
            var workspace = CreateWorkspace(innerUri);
            var inner =
                workspace.CreateProjectSnapshot(innerUri);
            workspace.ManifestWorkspace
                .RetireInactiveState([], []);
            File.WriteAllText(
                innerManifestPath,
                "{\"schemaVersion\":");
            File.WriteAllText(
                middleManifestPath,
                "{\"schemaVersion\":");
            var manifestEvents = new RecordingManifestEvents();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    manifestEvents: manifestEvents,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            using var capture =
                workspace.CaptureDiskReconciliationScopes();
            var outer = Assert.Single(capture.Scopes);
            Assert.Equal(
                Path.GetFullPath(innerManifestPath),
                inner.Resolution.ManifestPath);
            Assert.Equal(
                Path.GetFullPath(outerManifestPath),
                outer.Resolution.ManifestPath);
            Assert.Equal(
                "Visual Basic For Applications",
                Assert.Single(
                    outer.Resolution.ReferenceEntries).Name);
            Assert.True(
                workspace.ManifestWorkspace
                    .GetReconciliationBaseline(outerManifestUri)
                    .Exists);
            Assert.Contains(
                manifestEvents.ValidationFailures,
                failure => failure.Uri == innerManifestUri);
            Assert.Contains(
                manifestEvents.ValidationFailures,
                failure => failure.Uri == middleManifestUri);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Sibling_descendant_barriers_converge_in_one_trigger()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-sibling-descendant-barriers-").FullName;
        try
        {
            WriteProjectManifest(projectRoot, "src");
            var outerPath = Path.Combine(
                projectRoot,
                "src",
                "Outer.bas");
            var outerUri = ToFileUri(outerPath);
            var firstRoot = Path.Combine(
                projectRoot,
                "src",
                "FirstProject");
            var firstPath = Path.Combine(
                firstRoot,
                "First.bas");
            var firstUri = ToFileUri(firstPath);
            var firstManifestUri = ToFileUri(Path.Combine(
                firstRoot,
                "vba-project.json"));
            var secondRoot = Path.Combine(
                projectRoot,
                "src",
                "SecondProject");
            var secondPath = Path.Combine(
                secondRoot,
                "Second.bas");
            var secondUri = ToFileUri(secondPath);
            var secondManifestUri = ToFileUri(Path.Combine(
                secondRoot,
                "vba-project.json"));
            Directory.CreateDirectory(firstRoot);
            Directory.CreateDirectory(secondRoot);
            File.WriteAllText(
                outerPath,
                CreateModule("Outer", "RunOuter"));
            File.WriteAllText(
                firstPath,
                CreateModule("First", "RunFirst"));
            File.WriteAllText(
                secondPath,
                CreateModule("Second", "RunSecond"));
            var workspace = CreateWorkspace(outerUri);
            var initial =
                workspace.CreateProjectSnapshot(outerUri);
            Assert.Contains(
                initial.SourceDocuments,
                document => document.Key == firstUri);
            Assert.Contains(
                initial.SourceDocuments,
                document => document.Key == secondUri);
            File.WriteAllText(
                VbaProjectResolver.TryGetLocalPath(
                    firstManifestUri)!,
                "{\"schemaVersion\":");
            File.WriteAllText(
                VbaProjectResolver.TryGetLocalPath(
                    secondManifestUri)!,
                "{\"schemaVersion\":");
            var manifestEvents = new RecordingManifestEvents();
            var invalidatedSecondRevision = 0;
            manifestEvents.ValidationFailureObserved = uri =>
            {
                if (uri == firstManifestUri
                    && Interlocked.Exchange(
                        ref invalidatedSecondRevision,
                        1) == 0)
                {
                    Assert.True(
                        workspace.ManifestWorkspace
                            .ReloadManifest(secondManifestUri));
                }
            };
            var boundary = new CountingDiskSourceBoundary(
                new VbaFileSystemProjectDiskSourceBoundary());
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    manifestEvents: manifestEvents,
                    sourceBoundary: boundary,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(3, boundary.ScanCount);
            using (var capture =
                workspace.CaptureDiskReconciliationScopes())
            {
                var outer = Assert.Single(capture.Scopes);
                Assert.False(
                    outer.ManifestBarriers.Overrides[
                        Path.GetFullPath(
                            VbaProjectResolver.TryGetLocalPath(
                                secondManifestUri)!)]);
                var finalScan =
                    await new VbaFileSystemProjectDiskSourceBoundary()
                        .ScanAsync(
                            outer,
                            CancellationToken.None);
                Assert.Contains(
                    finalScan.Sources,
                    source => source.Uri == secondUri);
                Assert.Contains(
                    outer.KnownSources,
                    source => source.Uri == secondUri);
            }
            var reconciled =
                workspace.CreateProjectSnapshot(outerUri);
            Assert.Contains(
                reconciled.SourceDocuments,
                document => document.Key == firstUri);
            Assert.Contains(
                reconciled.SourceDocuments,
                document => document.Key == secondUri);
            Assert.Contains(
                manifestEvents.ValidationFailures,
                failure => failure.Uri == firstManifestUri);
            Assert.Contains(
                manifestEvents.ValidationFailures,
                failure => failure.Uri == secondManifestUri);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Sibling_barriers_beyond_the_active_candidate_count_converge_in_one_trigger()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-many-sibling-barriers-").FullName;
        try
        {
            WriteProjectManifest(projectRoot, "src");
            var outerPath = Path.Combine(
                projectRoot,
                "src",
                "Outer.bas");
            var outerUri = ToFileUri(outerPath);
            File.WriteAllText(
                outerPath,
                CreateModule("Outer", "RunOuter"));
            var workspace = CreateWorkspace(outerUri);
            _ = workspace.CreateProjectSnapshot(outerUri);
            int activeCandidateCount;
            using (var initialCapture =
                workspace.CaptureDiskReconciliationScopes())
            {
                activeCandidateCount = Assert.Single(
                    initialCapture.Scopes)
                    .ManifestCandidates.Count;
            }
            var siblingCount = activeCandidateCount + 1;
            Assert.True(
                siblingCount + 1
                < VbaProjectDiskReconciliationCoordinator
                    .MaximumImmediateFollowUpPasses);
            var siblingSourceUris = new List<string>(
                siblingCount);
            var siblingManifestUris = new List<string>(
                siblingCount);
            for (var index = 0;
                index < siblingCount;
                index++)
            {
                var siblingRoot = Path.Combine(
                    projectRoot,
                    "src",
                    $"Project{index:D2}");
                var sourcePath = Path.Combine(
                    siblingRoot,
                    $"Module{index:D2}.bas");
                var manifestPath = Path.Combine(
                    siblingRoot,
                    "vba-project.json");
                Directory.CreateDirectory(siblingRoot);
                File.WriteAllText(
                    sourcePath,
                    CreateModule(
                        $"Module{index:D2}",
                        $"Run{index:D2}"));
                File.WriteAllText(
                    manifestPath,
                    "{\"schemaVersion\":");
                siblingSourceUris.Add(
                    ToFileUri(sourcePath));
                siblingManifestUris.Add(
                    ToFileUri(manifestPath));
            }

            var manifestEvents = new RecordingManifestEvents();
            manifestEvents.ValidationFailureObserved = uri =>
            {
                var currentIndex =
                    siblingManifestUris.IndexOf(uri);
                var nextIndex = currentIndex + 1;
                if (currentIndex >= 0
                    && nextIndex < siblingManifestUris.Count)
                {
                    Assert.True(
                        workspace.ManifestWorkspace
                            .ReloadManifest(
                                siblingManifestUris[nextIndex]));
                }
            };
            var boundary = new CountingDiskSourceBoundary(
                new VbaFileSystemProjectDiskSourceBoundary());
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    manifestEvents: manifestEvents,
                    sourceBoundary: boundary,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(
                siblingCount + 1,
                boundary.ScanCount);
            Assert.Equal(
                siblingCount,
                manifestEvents.ValidationFailures
                    .Select(failure => failure.Uri)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count());
            var reconciled =
                workspace.CreateProjectSnapshot(outerUri);
            Assert.All(
                siblingSourceUris,
                sourceUri => Assert.Contains(
                    reconciled.SourceDocuments,
                    document => document.Key == sourceUri));
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Same_path_invalid_to_valid_recovery_converges_ownership_in_one_trigger()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-same-path-invalid-valid-").FullName;
        try
        {
            WriteProjectManifest(projectRoot, "src");
            var outerPath = Path.Combine(
                projectRoot,
                "src",
                "Outer.bas");
            var outerUri = ToFileUri(outerPath);
            var nestedRoot = Path.Combine(
                projectRoot,
                "src",
                "NestedProject");
            var nestedManifestPath = Path.Combine(
                nestedRoot,
                "vba-project.json");
            var nestedManifestUri = ToFileUri(
                nestedManifestPath);
            var nestedPath = Path.Combine(
                nestedRoot,
                "src",
                "Book1",
                "Nested.bas");
            var nestedUri = ToFileUri(nestedPath);
            Directory.CreateDirectory(
                Path.GetDirectoryName(nestedPath)!);
            File.WriteAllText(
                outerPath,
                CreateModule("Outer", "RunOuter"));
            File.WriteAllText(
                nestedPath,
                CreateModule("Nested", "RunNested"));
            var workspace = CreateWorkspace(outerUri);
            var initial =
                workspace.CreateProjectSnapshot(outerUri);
            Assert.Contains(
                initial.SourceDocuments,
                document => document.Key == nestedUri);
            File.WriteAllText(
                nestedManifestPath,
                "{\"schemaVersion\":");
            var boundary =
                new RepairManifestOnSecondScanDiskSourceBoundary(
                    new VbaFileSystemProjectDiskSourceBoundary(),
                    nestedManifestPath,
                    CreateProjectManifestText());
            var manifestEvents = new RecordingManifestEvents();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    manifestEvents: manifestEvents,
                    sourceBoundary: boundary,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(3, boundary.ScanCount);
            using var capture =
                workspace.CaptureDiskReconciliationScopes();
            var outer = Assert.Single(capture.Scopes);
            Assert.DoesNotContain(
                outer.KnownSources,
                source => source.Uri == nestedUri);
            Assert.Equal(
                Path.GetFullPath(nestedManifestPath),
                workspace.ManifestWorkspace
                    .Resolve(nestedUri).ManifestPath);
            Assert.Contains(
                manifestEvents.ValidationFailures,
                failure => failure.Uri == nestedManifestUri);
            Assert.Contains(
                nestedManifestUri,
                manifestEvents.ValidationRecoveredUris);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task New_manifest_path_churn_stops_at_the_immediate_follow_up_hard_cap()
    {
        const int expectedHardCap = 32;
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-manifest-follow-up-cap-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var callerUri = ToFileUri(Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Caller.bas"));
            var workspace = CreateWorkspace(callerUri);
            _ = workspace.CreateProjectSnapshot(callerUri);
            workspace.ManifestWorkspace
                .RetireInactiveState([], []);
            var boundary =
                new ChurningInvalidManifestDiskSourceBoundary(
                    projectRoot);
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    sourceBoundary: boundary,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(expectedHardCap, boundary.ScanCount);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Same_manifest_path_churn_stops_at_the_immediate_follow_up_hard_cap()
    {
        const int expectedHardCap = 32;
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-same-manifest-follow-up-cap-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var callerUri = ToFileUri(Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Caller.bas"));
            var workspace = CreateWorkspace(callerUri);
            _ = workspace.CreateProjectSnapshot(callerUri);
            workspace.ManifestWorkspace
                .RetireInactiveState([], []);
            var boundary =
                new ChurningInvalidManifestDiskSourceBoundary(
                    projectRoot,
                    useSamePath: true);
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    sourceBoundary: boundary,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(expectedHardCap, boundary.ScanCount);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Unchanged_invalid_manifest_releases_a_stale_provider_authority()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-stale-invalid-authority-").FullName;
        try
        {
            WriteProjectManifest(
                projectRoot,
                "src",
                "Visual Basic For Applications");
            var outerManifestPath = Path.Combine(
                projectRoot,
                "vba-project.json");
            var innerRoot = Path.Combine(
                projectRoot,
                "src",
                "InnerProject");
            WriteProjectManifest(
                innerRoot,
                "src/Book1",
                "Microsoft Office 16.0 Object Library");
            var innerManifestPath = Path.Combine(
                innerRoot,
                "vba-project.json");
            var innerManifestUri = ToFileUri(innerManifestPath);
            var innerPath = Path.Combine(
                innerRoot,
                "src",
                "Book1",
                "Inner.bas");
            var innerUri = ToFileUri(innerPath);
            File.WriteAllText(
                innerPath,
                CreateModule("Inner", "RunInner"));
            var workspace = CreateWorkspace(innerUri);
            var initial =
                workspace.CreateProjectSnapshot(innerUri);
            workspace.ManifestWorkspace
                .RetireInactiveState([], []);
            const string invalidText = "{\"schemaVersion\":";
            File.WriteAllText(innerManifestPath, invalidText);
            var invalidUpdate = workspace.ManifestWorkspace
                .ReloadReconciledManifest(
                    innerManifestUri,
                    invalidText,
                    workspace.ManifestWorkspace
                        .GetReconciliationRevision(innerManifestUri));
            Assert.Equal(
                VbaProjectManifestReconciliationStatus.Invalid,
                invalidUpdate.Status);
            Assert.False(invalidUpdate.RetainedLastKnownGood);
            using (var staleCapture =
                workspace.CaptureDiskReconciliationScopes())
            {
                var stale = Assert.Single(staleCapture.Scopes);
                Assert.Equal(
                    Path.GetFullPath(innerManifestPath),
                    stale.Resolution.ManifestPath);
                Assert.Equal(
                    invalidText,
                    Assert.Single(
                        stale.ManifestCandidates,
                        candidate => candidate.Uri == innerManifestUri)
                        .Baseline.Text);
            }

            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);
            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            using var capture =
                workspace.CaptureDiskReconciliationScopes();
            var outer = Assert.Single(capture.Scopes);
            Assert.Equal(
                Path.GetFullPath(innerManifestPath),
                initial.Resolution.ManifestPath);
            Assert.Equal(
                Path.GetFullPath(outerManifestPath),
                outer.Resolution.ManifestPath);
            Assert.Equal(
                "Visual Basic For Applications",
                Assert.Single(
                    outer.Resolution.ReferenceEntries).Name);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Invalid_authority_without_last_known_good_falls_back_to_ad_hoc()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-invalid-authority-ad-hoc-").FullName;
        try
        {
            WriteProjectManifest(
                projectRoot,
                "src/Book1",
                "Visual Basic For Applications");
            var manifestPath = Path.Combine(
                projectRoot,
                "vba-project.json");
            var manifestUri = ToFileUri(manifestPath);
            var sourcePath = Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Module.bas");
            var sourceUri = ToFileUri(sourcePath);
            File.WriteAllText(
                sourcePath,
                CreateModule("Module", "RunModule"));
            var workspace = CreateWorkspace(sourceUri);
            var manifest =
                workspace.CreateProjectSnapshot(sourceUri);
            workspace.ManifestWorkspace
                .RetireInactiveState([], []);
            File.WriteAllText(
                manifestPath,
                "{\"schemaVersion\":");
            var manifestEvents = new RecordingManifestEvents();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    manifestEvents: manifestEvents,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            using var capture =
                workspace.CaptureDiskReconciliationScopes();
            var adHoc = Assert.Single(capture.Scopes);
            Assert.Equal(
                VbaProjectResolutionKind.ManifestDocument,
                manifest.Resolution.Kind);
            Assert.Equal(
                VbaProjectResolutionKind.AdHoc,
                adHoc.Resolution.Kind);
            Assert.Single(
                manifestEvents.ValidationFailures,
                failure => failure.Uri == manifestUri);
            Assert.Equal(
                [sourceUri],
                manifestEvents.AuthorityTransferredSourceUris);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Watcher_seeded_nearer_manifest_still_replaces_the_provider_scope()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-watcher-seeded-nearer-manifest-").FullName;
        try
        {
            WriteProjectManifest(projectRoot, "src");
            var nestedProjectRoot = Path.Combine(
                projectRoot,
                "src",
                "NestedProject");
            var innerPath = Path.Combine(
                nestedProjectRoot,
                "src",
                "Book1",
                "Inner.bas");
            var innerUri = ToFileUri(innerPath);
            Directory.CreateDirectory(Path.GetDirectoryName(innerPath)!);
            File.WriteAllText(
                innerPath,
                CreateModule("Inner", "RunInner"));
            var workspace = CreateWorkspace(innerUri);
            _ = workspace.CreateProjectSnapshot(innerUri);
            WriteProjectManifest(nestedProjectRoot);
            var nestedManifestUri = ToFileUri(Path.Combine(
                nestedProjectRoot,
                "vba-project.json"));
            Assert.True(
                workspace.ManifestWorkspace.ReloadManifest(
                    nestedManifestUri));
            Assert.True(
                workspace.ManifestWorkspace.TryGetEffectiveManifest(
                    nestedManifestUri,
                    out _,
                    out _,
                    out _));
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            using var capture =
                workspace.CaptureDiskReconciliationScopes();
            var scope = Assert.Single(capture.Scopes);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(
                    nestedProjectRoot,
                    "vba-project.json")),
                scope.Resolution.ManifestPath);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Watched_invalid_descendant_manifest_does_not_hide_outer_sources()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-invalid-descendant-barrier-").FullName;
        try
        {
            WriteProjectManifest(projectRoot, "src");
            var outerPath = Path.Combine(
                projectRoot,
                "src",
                "Outer.bas");
            var outerUri = ToFileUri(outerPath);
            var nestedProjectRoot = Path.Combine(
                projectRoot,
                "src",
                "NestedProject");
            var nestedPath = Path.Combine(
                nestedProjectRoot,
                "src",
                "Book1",
                "Nested.bas");
            var nestedUri = ToFileUri(nestedPath);
            Directory.CreateDirectory(Path.GetDirectoryName(nestedPath)!);
            File.WriteAllText(
                outerPath,
                CreateModule("Outer", "RunOuter"));
            File.WriteAllText(
                nestedPath,
                CreateModule("Nested", "RunNested"));
            var workspace = CreateWorkspace(outerUri);
            var initial =
                workspace.CreateProjectSnapshot(outerUri);
            var nestedManifestPath = Path.Combine(
                nestedProjectRoot,
                "vba-project.json");
            var nestedManifestUri = ToFileUri(nestedManifestPath);
            File.WriteAllText(
                nestedManifestPath,
                "{\"schemaVersion\":");
            Assert.True(
                workspace.ManifestWorkspace.ReloadManifest(
                    nestedManifestUri));
            Assert.False(
                workspace.ManifestWorkspace.TryGetEffectiveManifest(
                    nestedManifestUri,
                    out _,
                    out _,
                    out var error));
            Assert.NotNull(error);

            var invalid =
                workspace.CreateProjectSnapshot(outerUri);

            Assert.Contains(
                initial.SemanticInventory.GetWorkspaceSymbols(
                    "RunNested"),
                symbol => symbol.Uri == nestedUri);
            Assert.Contains(
                invalid.SemanticInventory.GetWorkspaceSymbols(
                    "RunNested"),
                symbol => symbol.Uri == nestedUri);
            var invalidBarriers = workspace.ManifestWorkspace
                .CaptureScopeBarriers(
                    outerUri,
                    invalid.Resolution);
            Assert.True(
                invalidBarriers.Overrides.TryGetValue(
                    Path.GetFullPath(nestedManifestPath),
                    out var invalidBarrier));
            Assert.False(invalidBarrier);

            WriteProjectManifest(nestedProjectRoot);
            Assert.True(
                workspace.ManifestWorkspace.ReloadManifest(
                    nestedManifestUri));
            Assert.True(
                workspace.ManifestWorkspace.TryGetEffectiveManifest(
                    nestedManifestUri,
                    out _,
                    out _,
                    out error));
            Assert.Null(error);

            var valid =
                workspace.CreateProjectSnapshot(outerUri);

            Assert.DoesNotContain(
                valid.SemanticInventory.GetWorkspaceSymbols(
                    "RunNested"),
                symbol => symbol.Uri == nestedUri);
            var validBarriers = workspace.ManifestWorkspace
                .CaptureScopeBarriers(
                    outerUri,
                    valid.Resolution);
            Assert.True(
                !validBarriers.Overrides.TryGetValue(
                    Path.GetFullPath(nestedManifestPath),
                    out var validBarrier)
                || validBarrier);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Cold_invalid_descendant_manifest_converges_outer_sources_after_background_observation()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-cold-invalid-descendant-").FullName;
        try
        {
            WriteProjectManifest(projectRoot, "src");
            var outerPath = Path.Combine(
                projectRoot,
                "src",
                "Outer.bas");
            var outerUri = ToFileUri(outerPath);
            var nestedRoot = Path.Combine(
                projectRoot,
                "src",
                "NestedProject");
            var nestedManifestPath = Path.Combine(
                nestedRoot,
                "vba-project.json");
            var nestedManifestUri = ToFileUri(
                nestedManifestPath);
            var nestedPath = Path.Combine(
                nestedRoot,
                "src",
                "Book1",
                "Nested.bas");
            var nestedUri = ToFileUri(nestedPath);
            Directory.CreateDirectory(
                Path.GetDirectoryName(nestedPath)!);
            File.WriteAllText(
                outerPath,
                CreateModule("Outer", "RunOuter"));
            File.WriteAllText(
                nestedPath,
                CreateModule("Nested", "RunNested"));
            File.WriteAllText(
                nestedManifestPath,
                "{\"schemaVersion\":");
            var workspace = CreateWorkspace(outerUri);
            var cold = workspace.CreateProjectSnapshot(outerUri);
            Assert.DoesNotContain(
                cold.SemanticInventory.GetWorkspaceSymbols(
                    "RunNested"),
                symbol => symbol.Uri == nestedUri);
            var manifestEvents = new RecordingManifestEvents();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    manifestEvents: manifestEvents,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            var converged =
                workspace.CreateProjectSnapshot(outerUri);
            Assert.Contains(
                converged.SemanticInventory.GetWorkspaceSymbols(
                    "RunNested"),
                symbol => symbol.Uri == nestedUri);
            var barriers = workspace.ManifestWorkspace
                .CaptureScopeBarriers(
                    outerUri,
                    converged.Resolution);
            Assert.True(
                barriers.Overrides.TryGetValue(
                    Path.GetFullPath(nestedManifestPath),
                    out var invalidBarrier));
            Assert.False(invalidBarrier);
            Assert.Single(
                manifestEvents.ValidationFailures,
                failure => failure.Uri == nestedManifestUri);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(
                manifestEvents.ValidationFailures,
                failure => failure.Uri == nestedManifestUri);

            WriteProjectManifest(nestedRoot);
            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            var recovered =
                workspace.CreateProjectSnapshot(outerUri);
            Assert.DoesNotContain(
                recovered.SemanticInventory.GetWorkspaceSymbols(
                    "RunNested"),
                symbol => symbol.Uri == nestedUri);
            var recoveredBarriers = workspace.ManifestWorkspace
                .CaptureScopeBarriers(
                    outerUri,
                    recovered.Resolution);
            Assert.True(
                !recoveredBarriers.Overrides.TryGetValue(
                    Path.GetFullPath(nestedManifestPath),
                    out var recoveredBarrier)
                || recoveredBarrier);
            Assert.Equal(
                [nestedManifestUri],
                manifestEvents.ValidationRecoveredUris);
            Assert.Empty(manifestEvents.SelectionChanges);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Recovered_descendant_manifest_transfers_its_open_source_from_an_outer_peer_scope()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-recovered-descendant-transfer-").FullName;
        try
        {
            WriteProjectManifest(
                projectRoot,
                "src",
                "Visual Basic For Applications");
            var outerPath = Path.Combine(
                projectRoot,
                "src",
                "Outer.bas");
            var outerUri = ToFileUri(outerPath);
            var nestedRoot = Path.Combine(
                projectRoot,
                "src",
                "NestedProject");
            var nestedManifestPath = Path.Combine(
                nestedRoot,
                "vba-project.json");
            var nestedManifestUri = ToFileUri(
                nestedManifestPath);
            var nestedPath = Path.Combine(
                nestedRoot,
                "src",
                "Book1",
                "Nested.bas");
            var nestedUri = ToFileUri(nestedPath);
            Directory.CreateDirectory(
                Path.GetDirectoryName(nestedPath)!);
            var outerText = CreateModule("Outer", "RunOuter");
            var nestedText = CreateModule("Nested", "RunNested");
            File.WriteAllText(outerPath, outerText);
            File.WriteAllText(nestedPath, nestedText);

            var workspace = CreateWorkspace(outerUri);
            workspace.UpdateDocument(nestedUri, nestedText);
            var initial = workspace.CreateProjectSnapshot(outerUri);
            Assert.Contains(
                initial.SourceDocuments,
                document => document.Key == nestedUri);

            File.WriteAllText(
                nestedManifestPath,
                "{\"schemaVersion\":");
            var manifestEvents = new RecordingManifestEvents();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    manifestEvents: manifestEvents,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);
            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(
                manifestEvents.AuthorityTransferredSourceUris);

            File.WriteAllText(
                nestedManifestPath,
                CreateProjectManifestText(
                    "src/Book1",
                    "Microsoft Excel 16.0 Object Library"));
            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(
                [nestedUri],
                manifestEvents.AuthorityTransferredSourceUris);
            var nested = workspace.CreateProjectSnapshot(nestedUri);
            Assert.Equal(
                Path.GetFullPath(nestedManifestPath),
                nested.Resolution.ManifestPath);
            Assert.Equal(
                "Microsoft Excel 16.0 Object Library",
                Assert.Single(
                    nested.Resolution.ReferenceEntries).Name);
            var outer = workspace.CreateProjectSnapshot(outerUri);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(
                    projectRoot,
                    "vba-project.json")),
                outer.Resolution.ManifestPath);
            Assert.Equal(
                "Visual Basic For Applications",
                Assert.Single(
                    outer.Resolution.ReferenceEntries).Name);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Cold_valid_descendant_create_and_delete_transfer_open_nested_sources()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-cold-valid-descendant-transfer-").FullName;
        try
        {
            WriteProjectManifest(projectRoot, "src");
            var outerPath = Path.Combine(
                projectRoot,
                "src",
                "Outer.bas");
            var outerUri = ToFileUri(outerPath);
            var nestedRoot = Path.Combine(
                projectRoot,
                "src",
                "NestedProject");
            var nestedManifestPath = Path.Combine(
                nestedRoot,
                "vba-project.json");
            var nestedManifestUri = ToFileUri(
                nestedManifestPath);
            var nestedPath = Path.Combine(
                nestedRoot,
                "src",
                "Book1",
                "Nested.bas");
            var nestedUri = ToFileUri(nestedPath);
            var loosePath = Path.Combine(
                nestedRoot,
                "Loose.bas");
            var looseUri = ToFileUri(loosePath);
            Directory.CreateDirectory(
                Path.GetDirectoryName(nestedPath)!);
            File.WriteAllText(
                outerPath,
                CreateModule("Outer", "RunOuter"));
            var nestedText =
                CreateModule("Nested", "RunNested");
            var looseText =
                CreateModule("Loose", "RunLoose");
            File.WriteAllText(nestedPath, nestedText);
            File.WriteAllText(loosePath, looseText);

            var workspace = CreateWorkspace(outerUri);
            workspace.UpdateDocument(nestedUri, nestedText);
            workspace.UpdateDocument(looseUri, looseText);
            var initial = workspace.CreateProjectSnapshot(outerUri);
            Assert.Contains(
                initial.SourceDocuments,
                document => document.Key == nestedUri);
            Assert.Contains(
                initial.SourceDocuments,
                document => document.Key == looseUri);
            var manifestEvents = new RecordingManifestEvents();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    manifestEvents: manifestEvents,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            WriteProjectManifest(nestedRoot);
            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(
                [nestedUri, looseUri],
                manifestEvents.AuthorityTransferredSourceUris);
            Assert.Equal(
                VbaProjectResolutionKind.ManifestDocument,
                workspace.ManifestWorkspace
                    .Resolve(nestedUri).Kind);
            Assert.Equal(
                VbaProjectResolutionKind.AdHoc,
                workspace.ManifestWorkspace
                    .Resolve(looseUri).Kind);
            Assert.Empty(
                manifestEvents.ValidationRecoveredUris);

            manifestEvents.AuthorityTransferredSourceUris.Clear();
            File.Delete(nestedManifestPath);
            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(
                [nestedUri, looseUri],
                manifestEvents.AuthorityTransferredSourceUris);
            Assert.Empty(
                manifestEvents.ValidationRecoveredUris);
            Assert.False(
                workspace.ManifestWorkspace
                    .GetReconciliationBaseline(nestedManifestUri)
                    .Exists);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Source_opened_after_observed_manifest_scan_receives_authority_transfer()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-observed-open-race-").FullName;
        try
        {
            WriteProjectManifest(projectRoot, "src");
            var outerPath = Path.Combine(
                projectRoot,
                "src",
                "Outer.bas");
            var outerUri = ToFileUri(outerPath);
            var nestedRoot = Path.Combine(
                projectRoot,
                "src",
                "NestedProject");
            var nestedManifestPath = Path.Combine(
                nestedRoot,
                "vba-project.json");
            var nestedPath = Path.Combine(
                nestedRoot,
                "src",
                "Book1",
                "Nested.bas");
            var nestedUri = ToFileUri(nestedPath);
            Directory.CreateDirectory(
                Path.GetDirectoryName(nestedPath)!);
            File.WriteAllText(
                outerPath,
                CreateModule("Outer", "RunOuter"));
            var nestedText =
                CreateModule("Nested", "RunNested");
            File.WriteAllText(nestedPath, nestedText);
            var workspace = CreateWorkspace(outerUri);
            _ = workspace.CreateProjectSnapshot(outerUri);
            File.WriteAllText(
                nestedManifestPath,
                "{\"schemaVersion\":");
            await using var initialScheduler =
                CreateSerialScheduler();
            await using (var initialReconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    cadence: Timeout.InfiniteTimeSpan))
            {
                initialReconciliation.AttachScheduler(
                    initialScheduler);
                await initialReconciliation.TriggerAsync()
                    .WaitAsync(TimeSpan.FromSeconds(5));
                await initialReconciliation.TriggerAsync()
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }
            using (var invalidCapture =
                workspace.CaptureDiskReconciliationScopes())
            {
                var invalid = Assert.Single(
                    invalidCapture.Scopes);
                Assert.Contains(
                    invalid.KnownSources,
                    source => source.Uri == nestedUri);
                Assert.DoesNotContain(
                    nestedUri,
                    invalid.OpenDocumentUris);
            }

            WriteProjectManifest(nestedRoot);
            var boundary = new BlockingDiskSourceBoundary();
            var manifestEvents = new RecordingManifestEvents();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    manifestEvents: manifestEvents,
                    sourceBoundary: boundary,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);
            var pending = reconciliation.TriggerAsync();
            await boundary.Started.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            var scan =
                await new VbaFileSystemProjectDiskSourceBoundary()
                    .ScanAsync(
                        boundary.Scope,
                        CancellationToken.None);
            workspace.UpdateDocument(nestedUri, nestedText);
            boundary.Complete(scan);
            await pending.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(
                [nestedUri],
                manifestEvents.AuthorityTransferredSourceUris);
            Assert.Equal(
                VbaProjectResolutionKind.ManifestDocument,
                workspace.ManifestWorkspace
                    .Resolve(nestedUri).Kind);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Parent_manifest_change_does_not_transfer_a_deeper_project_source()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-parent-deep-authority-").FullName;
        try
        {
            WriteProjectManifest(projectRoot, "src");
            var outerPath = Path.Combine(
                projectRoot,
                "src",
                "Outer.bas");
            var outerUri = ToFileUri(outerPath);
            var parentRoot = Path.Combine(
                projectRoot,
                "src",
                "ParentProject");
            var parentManifestPath = Path.Combine(
                parentRoot,
                "vba-project.json");
            var deepRoot = Path.Combine(
                parentRoot,
                "DeepProject");
            WriteProjectManifest(deepRoot);
            var deepPath = Path.Combine(
                deepRoot,
                "src",
                "Book1",
                "Deep.bas");
            var deepUri = ToFileUri(deepPath);
            var deepText =
                CreateModule("Deep", "RunDeep");
            File.WriteAllText(
                outerPath,
                CreateModule("Outer", "RunOuter"));
            File.WriteAllText(deepPath, deepText);
            var workspace = CreateWorkspace(outerUri);
            workspace.UpdateDocument(deepUri, deepText);
            _ = workspace.CreateProjectSnapshot(outerUri);
            var deep =
                workspace.CreateProjectSnapshot(deepUri);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(
                    deepRoot,
                    "vba-project.json")),
                deep.Resolution.ManifestPath);

            File.WriteAllText(
                parentManifestPath,
                CreateProjectManifestText(
                    "DeepProject/src/Book1"));
            var manifestEvents = new RecordingManifestEvents();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    manifestEvents: manifestEvents,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);
            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Empty(
                manifestEvents.AuthorityTransferredSourceUris);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(
                    deepRoot,
                    "vba-project.json")),
                workspace.ManifestWorkspace
                    .Resolve(deepUri)
                    .ManifestPath);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Nested_manifest_changes_notify_one_final_authority_transfer()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-nested-manifest-single-transfer-").FullName;
        try
        {
            WriteProjectManifest(projectRoot, "src");
            var outerPath = Path.Combine(
                projectRoot,
                "src",
                "Outer.bas");
            var outerUri = ToFileUri(outerPath);
            var parentRoot = Path.Combine(
                projectRoot,
                "src",
                "ParentProject");
            var childRoot = Path.Combine(
                parentRoot,
                "ZChildProject");
            var childPath = Path.Combine(
                childRoot,
                "src",
                "Book1",
                "Child.bas");
            var childUri = ToFileUri(childPath);
            var childText =
                CreateModule("Child", "RunChild");
            Directory.CreateDirectory(
                Path.GetDirectoryName(childPath)!);
            File.WriteAllText(
                outerPath,
                CreateModule("Outer", "RunOuter"));
            File.WriteAllText(childPath, childText);
            var workspace = CreateWorkspace(outerUri);
            workspace.UpdateDocument(childUri, childText);
            _ = workspace.CreateProjectSnapshot(outerUri);
            WriteProjectManifest(
                parentRoot,
                "ZChildProject/src/Book1");
            WriteProjectManifest(childRoot);
            var manifestEvents = new RecordingManifestEvents();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    manifestEvents: manifestEvents,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(
                [childUri],
                manifestEvents.AuthorityTransferredSourceUris);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(
                    childRoot,
                    "vba-project.json")),
                workspace.ManifestWorkspace
                    .Resolve(childUri)
                    .ManifestPath);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Nested_manifest_deletions_notify_one_final_authority_transfer()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-nested-manifest-delete-single-transfer-").FullName;
        try
        {
            WriteProjectManifest(projectRoot, "src");
            var outerPath = Path.Combine(
                projectRoot,
                "src",
                "Outer.bas");
            var outerUri = ToFileUri(outerPath);
            var parentRoot = Path.Combine(
                projectRoot,
                "src",
                "ZParentProject");
            var parentManifestPath = Path.Combine(
                parentRoot,
                "vba-project.json");
            var childRoot = Path.Combine(
                parentRoot,
                "ZChildProject");
            var childManifestPath = Path.Combine(
                childRoot,
                "vba-project.json");
            var childPath = Path.Combine(
                childRoot,
                "src",
                "Book1",
                "Child.bas");
            var childUri = ToFileUri(childPath);
            var childText =
                CreateModule("Child", "RunChild");
            Directory.CreateDirectory(
                Path.GetDirectoryName(childPath)!);
            File.WriteAllText(
                outerPath,
                CreateModule("Outer", "RunOuter"));
            File.WriteAllText(childPath, childText);
            WriteProjectManifest(
                parentRoot,
                "ZChildProject/src/Book1");
            WriteProjectManifest(childRoot);
            var workspace = CreateWorkspace(outerUri);
            _ = workspace.CreateProjectSnapshot(outerUri);
            var manifestEvents = new RecordingManifestEvents();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    manifestEvents: manifestEvents,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);
            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            workspace.UpdateDocument(childUri, childText);
            using (var initialCapture =
                workspace.CaptureDiskReconciliationScopes())
            {
                Assert.Single(initialCapture.Scopes);
            }
            Assert.Equal(
                Path.GetFullPath(childManifestPath),
                workspace.ManifestWorkspace
                    .Resolve(childUri).ManifestPath);
            manifestEvents.AuthorityTransferredSourceUris.Clear();
            File.Delete(childManifestPath);
            File.Delete(parentManifestPath);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(
                [childUri],
                manifestEvents.AuthorityTransferredSourceUris);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(
                    projectRoot,
                    "vba-project.json")),
                workspace.ManifestWorkspace
                    .Resolve(childUri)
                    .ManifestPath);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Deleted_invalid_descendant_manifest_clears_background_validation_state()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-delete-invalid-descendant-").FullName;
        try
        {
            WriteProjectManifest(projectRoot, "src");
            var outerPath = Path.Combine(
                projectRoot,
                "src",
                "Outer.bas");
            var outerUri = ToFileUri(outerPath);
            var nestedRoot = Path.Combine(
                projectRoot,
                "src",
                "NestedProject");
            var nestedManifestPath = Path.Combine(
                nestedRoot,
                "vba-project.json");
            var nestedManifestUri = ToFileUri(
                nestedManifestPath);
            var nestedPath = Path.Combine(
                nestedRoot,
                "src",
                "Book1",
                "Nested.bas");
            Directory.CreateDirectory(
                Path.GetDirectoryName(nestedPath)!);
            File.WriteAllText(
                outerPath,
                CreateModule("Outer", "RunOuter"));
            File.WriteAllText(
                nestedPath,
                CreateModule("Nested", "RunNested"));
            File.WriteAllText(
                nestedManifestPath,
                "{\"schemaVersion\":");
            var workspace = CreateWorkspace(outerUri);
            _ = workspace.CreateProjectSnapshot(outerUri);
            var manifestEvents = new RecordingManifestEvents();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    manifestEvents: manifestEvents,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);
            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(
                manifestEvents.ValidationFailures,
                failure => failure.Uri == nestedManifestUri);
            File.Delete(nestedManifestPath);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(
                [nestedManifestUri],
                manifestEvents.ValidationRecoveredUris);
            Assert.False(
                workspace.ManifestWorkspace
                    .GetReconciliationBaseline(nestedManifestUri)
                    .Exists);
            Assert.False(
                workspace.ManifestWorkspace.TryGetEffectiveManifest(
                    nestedManifestUri,
                    out _,
                    out _,
                    out var error));
            Assert.Null(error);
            Assert.Empty(
                manifestEvents.AuthorityTransferredSourceUris);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Interactive_snapshot_resets_known_sources_after_watched_source_path_change()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-watcher-interactive-source-path-").FullName;
        try
        {
            WriteProjectManifest(projectRoot, "src");
            var callerUri = ToFileUri(Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Caller.bas"));
            var legacyPath = Path.Combine(
                projectRoot,
                "src",
                "Legacy",
                "Legacy.bas");
            var legacyUri = ToFileUri(legacyPath);
            Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
            File.WriteAllText(
                legacyPath,
                CreateModule("Legacy", "BuildLegacy"));
            var workspace = CreateWorkspace(callerUri);
            _ = workspace.CreateProjectSnapshot(callerUri);
            var manifestPath = Path.Combine(
                projectRoot,
                "vba-project.json");
            File.WriteAllText(
                manifestPath,
                CreateProjectManifestText("src/Book1"));
            Assert.True(
                workspace.ManifestWorkspace.ReloadManifest(
                    ToFileUri(manifestPath)));

            _ = workspace.CreateProjectSnapshot(callerUri);

            using var capture =
                workspace.CaptureDiskReconciliationScopes();
            var scope = Assert.Single(capture.Scopes);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(
                    projectRoot,
                    "src",
                    "Book1")),
                scope.Resolution.RootPath);
            Assert.DoesNotContain(
                scope.KnownSources,
                source => source.Uri == legacyUri);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Interactive_snapshot_preserves_outer_peer_after_watched_nearer_manifest()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-watcher-interactive-nearer-peer-").FullName;
        try
        {
            WriteProjectManifest(projectRoot, "src");
            var outerPath = Path.Combine(
                projectRoot,
                "src",
                "Outer.bas");
            var outerUri = ToFileUri(outerPath);
            var nestedProjectRoot = Path.Combine(
                projectRoot,
                "src",
                "NestedProject");
            var innerPath = Path.Combine(
                nestedProjectRoot,
                "src",
                "Book1",
                "Inner.bas");
            var innerUri = ToFileUri(innerPath);
            Directory.CreateDirectory(Path.GetDirectoryName(innerPath)!);
            File.WriteAllText(
                outerPath,
                CreateModule("Outer", "RunOuter"));
            File.WriteAllText(
                innerPath,
                CreateModule("Inner", "RunInner"));
            var workspace = CreateWorkspace(innerUri);
            workspace.UpdateDocument(
                outerUri,
                CreateModule("Outer", "RunOuter"));
            _ = workspace.CreateProjectSnapshot(innerUri);
            WriteProjectManifest(nestedProjectRoot);
            var nestedManifestUri = ToFileUri(Path.Combine(
                nestedProjectRoot,
                "vba-project.json"));
            Assert.True(
                workspace.ManifestWorkspace.ReloadManifest(
                    nestedManifestUri));

            _ = workspace.CreateProjectSnapshot(innerUri);

            Assert.Equal(1, workspace.RetainedProjectSnapshotCount);
            Assert.Equal(
                1,
                workspace.RetainedProjectScopeInvalidationStateCount);
            using (var capture =
                workspace.CaptureDiskReconciliationScopes())
            {
                Assert.Equal(2, capture.Scopes.Count);
                var innerScope = Assert.Single(
                    capture.Scopes,
                    scope => scope.ActiveUri == innerUri);
                var outerScope = Assert.Single(
                    capture.Scopes,
                    scope => scope.ActiveUri == outerUri);
                Assert.Equal(
                    Path.GetFullPath(Path.Combine(
                        nestedProjectRoot,
                        "vba-project.json")),
                    innerScope.Resolution.ManifestPath);
                Assert.Equal(
                    Path.GetFullPath(Path.Combine(
                        projectRoot,
                        "vba-project.json")),
                    outerScope.Resolution.ManifestPath);
                Assert.Contains(
                    outerScope.KnownSources,
                    source => source.Uri == outerUri);
                Assert.DoesNotContain(
                    outerScope.KnownSources,
                    source => source.Uri == innerUri);
            }

            var rebuiltOuter =
                workspace.CreateProjectSnapshot(outerUri);
            Assert.DoesNotContain(
                rebuiltOuter.SourceDocuments,
                document => document.Key == innerUri);
            Assert.Equal(2, workspace.RetainedProjectSnapshotCount);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Interactive_snapshot_does_not_reanchor_outer_scope_to_another_nested_document()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-watcher-interactive-multidoc-").FullName;
        try
        {
            WriteProjectManifest(projectRoot, "src");
            var nestedProjectRoot = Path.Combine(
                projectRoot,
                "src",
                "NestedProject");
            var firstPath = Path.Combine(
                nestedProjectRoot,
                "src",
                "Book1",
                "First.bas");
            var secondPath = Path.Combine(
                nestedProjectRoot,
                "src",
                "Book2",
                "Second.bas");
            var firstUri = ToFileUri(firstPath);
            var secondUri = ToFileUri(secondPath);
            Directory.CreateDirectory(Path.GetDirectoryName(firstPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(secondPath)!);
            File.WriteAllText(
                firstPath,
                CreateModule("First", "RunFirst"));
            File.WriteAllText(
                secondPath,
                CreateModule("Second", "RunSecond"));
            var workspace = CreateWorkspace(firstUri);
            workspace.UpdateDocument(
                secondUri,
                CreateModule("Second", "RunSecond"));
            _ = workspace.CreateProjectSnapshot(firstUri);
            var nestedManifestPath = Path.Combine(
                nestedProjectRoot,
                "vba-project.json");
            File.WriteAllText(
                nestedManifestPath,
                CreateTwoDocumentProjectManifestText(
                    "src/Book1",
                    "src/Book2"));
            Assert.True(
                workspace.ManifestWorkspace.ReloadManifest(
                    ToFileUri(nestedManifestPath)));

            _ = workspace.CreateProjectSnapshot(firstUri);

            using (var firstCapture =
                workspace.CaptureDiskReconciliationScopes())
            {
                var firstScope = Assert.Single(firstCapture.Scopes);
                Assert.Equal("Book1", firstScope.Resolution.DocumentName);
                Assert.Equal(
                    Path.GetFullPath(nestedManifestPath),
                    firstScope.Resolution.ManifestPath);
            }

            _ = workspace.CreateProjectSnapshot(secondUri);

            using var secondCapture =
                workspace.CaptureDiskReconciliationScopes();
            Assert.Equal(2, secondCapture.Scopes.Count);
            Assert.Contains(
                secondCapture.Scopes,
                scope => scope.Resolution.DocumentName == "Book1"
                    && scope.Resolution.ManifestPath == Path.GetFullPath(
                        nestedManifestPath));
            Assert.Contains(
                secondCapture.Scopes,
                scope => scope.Resolution.DocumentName == "Book2"
                    && scope.Resolution.ManifestPath == Path.GetFullPath(
                        nestedManifestPath));
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Nearer_manifest_transfer_preserves_a_tracked_outer_peer_scope()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-nearer-manifest-peer-scope-").FullName;
        try
        {
            WriteProjectManifest(projectRoot, "src");
            var outerPath = Path.Combine(projectRoot, "src", "Outer.bas");
            var outerUri = ToFileUri(outerPath);
            var nestedProjectRoot = Path.Combine(
                projectRoot,
                "src",
                "NestedProject");
            var innerPath = Path.Combine(
                nestedProjectRoot,
                "src",
                "Book1",
                "Inner.bas");
            var innerUri = ToFileUri(innerPath);
            Directory.CreateDirectory(Path.GetDirectoryName(innerPath)!);
            File.WriteAllText(
                outerPath,
                CreateModule("Outer", "RunOuter"));
            File.WriteAllText(
                innerPath,
                CreateModule("Inner", "RunInner"));
            var workspace = CreateWorkspace(innerUri);
            workspace.UpdateDocument(
                outerUri,
                CreateModule("Outer", "RunOuter"));
            _ = workspace.CreateProjectSnapshot(innerUri);
            WriteProjectManifest(nestedProjectRoot);
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            using var capture =
                workspace.CaptureDiskReconciliationScopes();
            Assert.Equal(2, capture.Scopes.Count);
            var innerScope = Assert.Single(
                capture.Scopes,
                scope => scope.ActiveUri == innerUri);
            var outerScope = Assert.Single(
                capture.Scopes,
                scope => scope.ActiveUri == outerUri);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(
                    nestedProjectRoot,
                    "vba-project.json")),
                innerScope.Resolution.ManifestPath);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(
                    projectRoot,
                    "vba-project.json")),
                outerScope.Resolution.ManifestPath);
            Assert.Contains(
                outerScope.KnownSources,
                source => source.Uri == outerUri);
            Assert.DoesNotContain(
                outerScope.KnownSources,
                source => source.Uri == innerUri);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Manifest_identity_change_evicts_the_superseded_snapshot_and_scope_state()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-manifest-identity-eviction-").FullName;
        try
        {
            WriteProjectManifest(
                projectRoot,
                "src/Book1",
                "Visual Basic For Applications");
            var callerUri = ToFileUri(
                Path.Combine(projectRoot, "src", "Book1", "Caller.bas"));
            var manifestPath = Path.Combine(projectRoot, "vba-project.json");
            var manifestUri = ToFileUri(manifestPath);
            var workspace = CreateWorkspace(callerUri);
            var initial = workspace.CreateProjectSnapshot(callerUri);
            File.WriteAllText(
                manifestPath,
                CreateProjectManifestText(
                    "src/Book1",
                    "Microsoft Excel 16.0 Object Library"));
            Assert.True(workspace.ManifestWorkspace.ReloadManifest(manifestUri));

            var changed = workspace.CreateProjectSnapshot(callerUri);
            var warm = workspace.CreateProjectSnapshot(callerUri);

            Assert.NotSame(initial, changed);
            Assert.Same(changed, warm);
            Assert.Equal(
                "Microsoft Excel 16.0 Object Library",
                Assert.Single(changed.Resolution.ReferenceEntries).Name);
            Assert.Equal(1, workspace.RetainedProjectSnapshotCount);
            Assert.Equal(
                1,
                workspace.RetainedProjectScopeInvalidationStateCount);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Manual_cycle_applies_missed_manifest_reference_and_source_path_change()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-reconcile-manifest-").FullName;
        try
        {
            WriteProjectManifest(
                projectRoot,
                "src",
                "Visual Basic For Applications");
            var callerUri = ToFileUri(
                Path.Combine(projectRoot, "src", "Book1", "Caller.bas"));
            var legacyPath = Path.Combine(
                projectRoot,
                "src",
                "Legacy",
                "Legacy.bas");
            Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
            File.WriteAllText(
                legacyPath,
                CreateModule("Legacy", "BuildLegacy"));
            var workspace = CreateWorkspace(callerUri);
            var initial = workspace.CreateProjectSnapshot(callerUri);
            var changedManifestText = CreateProjectManifestText(
                "src/Book1",
                "Visual Basic For Applications",
                "Microsoft Excel 16.0 Object Library");
            File.WriteAllText(
                Path.Combine(projectRoot, "vba-project.json"),
                changedManifestText);
            var stale = workspace.CreateProjectSnapshot(callerUri);
            var manifestEvents = new RecordingManifestEvents();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    manifestEvents: manifestEvents,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            var refreshed = workspace.CreateProjectSnapshot(callerUri);

            Assert.Same(initial, stale);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(projectRoot, "src", "Book1")),
                refreshed.Resolution.RootPath);
            Assert.Contains(
                refreshed.Resolution.ReferenceEntries,
                reference => reference.Name
                    == "Microsoft Excel 16.0 Object Library");
            Assert.Empty(
                refreshed.SemanticInventory.GetWorkspaceSymbols("BuildLegacy"));
            Assert.Single(manifestEvents.SelectionChanges);
            Assert.Equal(
                changedManifestText,
                manifestEvents.SelectionChanges[0].Text);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Source_path_narrowing_releases_old_scope_sources_without_global_deletion()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-reconcile-source-path-transfer-").FullName;
        try
        {
            WriteProjectManifest(projectRoot, "src");
            var callerUri = ToFileUri(
                Path.Combine(projectRoot, "src", "Book1", "Caller.bas"));
            var legacyPath = Path.Combine(
                projectRoot,
                "src",
                "Legacy",
                "Legacy.bas");
            var legacyUri = ToFileUri(legacyPath);
            var legacyText = CreateModule("Legacy", "BuildLegacy");
            Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
            File.WriteAllText(legacyPath, legacyText);
            var workspace = CreateWorkspace(callerUri);
            Assert.True(
                workspace.ReloadSourceDocument(legacyUri, legacyText));
            _ = workspace.CreateProjectSnapshot(callerUri);
            File.WriteAllText(
                Path.Combine(projectRoot, "vba-project.json"),
                CreateProjectManifestText("src/Book1"));
            var diagnostics = new RecordingDiagnostics();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    diagnostics,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(legacyText, workspace.GetDocumentText(legacyUri));
            Assert.DoesNotContain(legacyUri, diagnostics.EmptyUris);
            using var capture =
                workspace.CaptureDiskReconciliationScopes();
            var scope = Assert.Single(capture.Scopes);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(
                    projectRoot,
                    "src",
                    "Book1")),
                scope.Resolution.RootPath);
            Assert.DoesNotContain(
                scope.KnownSources,
                source => source.Uri == legacyUri);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Accepted_manifest_change_is_not_reapplied_by_an_unchanged_later_cycle()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-reconcile-manifest-baseline-").FullName;
        try
        {
            WriteProjectManifest(
                projectRoot,
                "src/Book1",
                "Visual Basic For Applications");
            var callerUri = ToFileUri(
                Path.Combine(projectRoot, "src", "Book1", "Caller.bas"));
            var workspace = CreateWorkspace(callerUri);
            _ = workspace.CreateProjectSnapshot(callerUri);
            File.WriteAllText(
                Path.Combine(projectRoot, "vba-project.json"),
                CreateProjectManifestText(
                    "src/Book1",
                    "Microsoft Excel 16.0 Object Library"));
            var manifestEvents = new RecordingManifestEvents();
            var boundary = new CountingDiskSourceBoundary(
                new VbaFileSystemProjectDiskSourceBoundary());
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    manifestEvents: manifestEvents,
                    sourceBoundary: boundary,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(3, boundary.ScanCount);
            Assert.Single(manifestEvents.SelectionChanges);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Invalid_manifest_is_reported_once_and_keeps_the_last_known_good_resolution()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-reconcile-invalid-manifest-").FullName;
        try
        {
            WriteProjectManifest(
                projectRoot,
                "src/Book1",
                "Microsoft Excel 16.0 Object Library");
            var callerUri = ToFileUri(
                Path.Combine(projectRoot, "src", "Book1", "Caller.bas"));
            var manifestPath = Path.Combine(projectRoot, "vba-project.json");
            var manifestUri = ToFileUri(manifestPath);
            var workspace = CreateWorkspace(callerUri);
            _ = workspace.CreateProjectSnapshot(callerUri);
            const string invalidText = "{\"schemaVersion\":";
            File.WriteAllText(manifestPath, invalidText);
            var manifestEvents = new RecordingManifestEvents();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    manifestEvents: manifestEvents,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            var resolution = workspace.ManifestWorkspace.Resolve(callerUri);

            Assert.Equal(
                VbaProjectResolutionKind.ManifestDocument,
                resolution.Kind);
            Assert.Contains(
                resolution.ReferenceEntries,
                reference => reference.Name
                    == "Microsoft Excel 16.0 Object Library");
            var validation = Assert.Single(manifestEvents.ValidationFailures);
            Assert.Equal(manifestUri, validation.Uri);
            Assert.NotNull(validation.Error);
            Assert.Empty(manifestEvents.SelectionChanges);

            const string secondInvalidText =
                "{\"schemaVersion\":1,\"documents\":";
            File.WriteAllText(manifestPath, secondInvalidText);
            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            var retainedResolution =
                workspace.ManifestWorkspace.Resolve(callerUri);

            Assert.Equal(
                VbaProjectResolutionKind.ManifestDocument,
                retainedResolution.Kind);
            Assert.Contains(
                retainedResolution.ReferenceEntries,
                reference => reference.Name
                    == "Microsoft Excel 16.0 Object Library");
            Assert.Equal(2, manifestEvents.ValidationFailures.Count);
            Assert.Empty(
                manifestEvents.AuthorityTransferredSourceUris);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Watched_invalid_manifest_keeps_last_known_good_and_later_recovers()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-watched-invalid-manifest-").FullName;
        try
        {
            WriteProjectManifest(
                projectRoot,
                "src/Book1",
                "Microsoft Excel 16.0 Object Library");
            var callerUri = ToFileUri(
                Path.Combine(projectRoot, "src", "Book1", "Caller.bas"));
            var manifestPath = Path.Combine(
                projectRoot,
                "vba-project.json");
            var manifestUri = ToFileUri(manifestPath);
            var workspace = CreateWorkspace(callerUri);
            var initial = workspace.CreateProjectSnapshot(callerUri);

            File.WriteAllText(manifestPath, "{\"schemaVersion\":");
            Assert.True(
                workspace.ManifestWorkspace.ReloadManifest(manifestUri));
            var lastKnownGood =
                workspace.CreateProjectSnapshot(callerUri);

            Assert.Equal(
                initial.Resolution,
                lastKnownGood.Resolution);

            File.WriteAllText(
                manifestPath,
                CreateProjectManifestText(
                    "src/Book1",
                    "Visual Basic For Applications"));
            Assert.True(
                workspace.ManifestWorkspace.ReloadManifest(manifestUri));
            var recovered = workspace.CreateProjectSnapshot(callerUri);

            Assert.Contains(
                recovered.Resolution.ReferenceEntries,
                reference => reference.Name
                    == "Visual Basic For Applications");
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Manifest_change_replaces_the_stable_reconciliation_scope_without_leaving_the_old_root()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-reconcile-stable-authority-").FullName;
        try
        {
            WriteProjectManifest(
                projectRoot,
                "src/Book1",
                "Microsoft Excel 16.0 Object Library");
            var callerUri = ToFileUri(
                Path.Combine(projectRoot, "src", "Book1", "Caller.bas"));
            var workspace = CreateWorkspace(callerUri);
            _ = workspace.CreateProjectSnapshot(callerUri);
            File.WriteAllText(
                Path.Combine(projectRoot, "vba-project.json"),
                CreateProjectManifestText(
                    "src",
                    "Microsoft Excel 16.0 Object Library"));
            var boundary = new CountingDiskSourceBoundary(
                new VbaFileSystemProjectDiskSourceBoundary());
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    sourceBoundary: boundary,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            var changedSnapshot = workspace.CreateProjectSnapshot(callerUri);
            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(
                Path.GetFullPath(Path.Combine(projectRoot, "src")),
                changedSnapshot.Resolution.RootPath);
            Assert.Equal(3, boundary.ScanCount);
            Assert.Single(
                boundary.AuthorityKeys.Distinct(
                    StringComparer.OrdinalIgnoreCase));
            Assert.Equal(
                Path.GetFullPath(Path.Combine(projectRoot, "src", "Book1")),
                boundary.RootPaths[0]);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(projectRoot, "src")),
                boundary.RootPaths[1]);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(projectRoot, "src")),
                boundary.RootPaths[2]);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Manifest_text_change_outside_the_active_projection_is_committed_for_a_later_document()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-reconcile-manifest-document-add-").FullName;
        try
        {
            var baselineText = CreateProjectManifestText(
                "src/Book1",
                "Microsoft Excel 16.0 Object Library");
            WriteProjectManifest(
                projectRoot,
                "src/Book1",
                "Microsoft Excel 16.0 Object Library");
            var manifestPath = Path.Combine(projectRoot, "vba-project.json");
            var manifestUri = ToFileUri(manifestPath);
            var firstUri = ToFileUri(
                Path.Combine(projectRoot, "src", "Book1", "Caller.bas"));
            var secondUri = ToFileUri(
                Path.Combine(projectRoot, "src", "Book2", "Caller.bas"));
            var workspace = CreateWorkspace(firstUri);
            var acceptedBaseline = workspace.ManifestWorkspace
                .ReloadReconciledManifest(
                    manifestUri,
                    baselineText,
                    capturedRevision: 0);
            Assert.Equal(
                VbaProjectManifestReconciliationStatus.Applied,
                acceptedBaseline.Status);
            _ = workspace.CreateProjectSnapshot(firstUri);
            var changedText = CreateTwoDocumentProjectManifestText(
                "src/Book1",
                "src/Book2",
                "Microsoft Excel 16.0 Object Library");
            File.WriteAllText(manifestPath, changedText);
            var manifestEvents = new RecordingManifestEvents();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    manifestEvents: manifestEvents,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            workspace.UpdateDocument(
                secondUri,
                "Attribute VB_Name = \"Second\"\nPublic Sub Run()\nEnd Sub");
            var secondSnapshot = workspace.CreateProjectSnapshot(secondUri);

            Assert.Equal(
                VbaProjectResolutionKind.ManifestDocument,
                secondSnapshot.Resolution.Kind);
            Assert.Equal("Book2", secondSnapshot.Resolution.DocumentName);
            Assert.Single(manifestEvents.SelectionChanges);
            Assert.Equal(changedText, manifestEvents.SelectionChanges[0].Text);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Open_manifest_overlay_wins_over_missed_disk_change()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-reconcile-overlay-").FullName;
        try
        {
            WriteProjectManifest(
                projectRoot,
                "src/Book1",
                "Visual Basic For Applications");
            var callerUri = ToFileUri(
                Path.Combine(projectRoot, "src", "Book1", "Caller.bas"));
            var manifestPath = Path.Combine(projectRoot, "vba-project.json");
            var manifestUri = ToFileUri(manifestPath);
            var workspace = CreateWorkspace(callerUri);
            var overlayText = CreateProjectManifestText(
                "src/Book1",
                "Microsoft Excel 16.0 Object Library");
            var opened = workspace.ManifestWorkspace.OpenManifest(
                manifestUri,
                documentVersion: 1,
                overlayText);
            Assert.True(opened.Accepted);
            var overlaySnapshot = workspace.CreateProjectSnapshot(callerUri);
            File.WriteAllText(
                manifestPath,
                CreateProjectManifestText(
                    "src/Book1",
                    "Microsoft Office 16.0 Object Library"));
            var manifestEvents = new RecordingManifestEvents();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    manifestEvents: manifestEvents,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            var afterReconciliation =
                workspace.CreateProjectSnapshot(callerUri);

            Assert.Same(overlaySnapshot, afterReconciliation);
            Assert.Single(afterReconciliation.Resolution.ReferenceEntries);
            Assert.Equal(
                "Microsoft Excel 16.0 Object Library",
                afterReconciliation.Resolution.ReferenceEntries[0].Name);
            Assert.Empty(manifestEvents.SelectionChanges);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Missed_disk_deletion_is_recorded_behind_an_open_manifest_overlay()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-reconcile-overlay-delete-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var callerUri = ToFileUri(Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Caller.bas"));
            var manifestPath = Path.Combine(
                projectRoot,
                "vba-project.json");
            var manifestUri = ToFileUri(manifestPath);
            var workspace = CreateWorkspace(callerUri);
            _ = workspace.CreateProjectSnapshot(callerUri);
            Assert.True(
                workspace.ManifestWorkspace.ReloadManifest(manifestUri));
            Assert.False(
                workspace.ManifestWorkspace
                    .CaptureReconciliationState(manifestUri)
                    .Baseline
                    .Exists);
            var opened = workspace.ManifestWorkspace.OpenManifest(
                manifestUri,
                documentVersion: 1,
                CreateProjectManifestText(
                    "src/Book1",
                    "Microsoft Excel 16.0 Object Library"));
            Assert.True(opened.Accepted);
            File.Delete(manifestPath);
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(
                workspace.ManifestWorkspace.TryGetEffectiveManifest(
                    manifestUri,
                    out _,
                    out _,
                    out var overlayError));
            Assert.Null(overlayError);
            Assert.False(
                workspace.ManifestWorkspace
                    .CaptureReconciliationState(manifestUri)
                    .Baseline
                    .Exists);
            Assert.Equal(
                0,
                workspace.ManifestWorkspace.RetainedLastKnownGoodCount);
            using (var overlayCapture =
                workspace.CaptureDiskReconciliationScopes())
            {
                Assert.Equal(
                    Path.GetFullPath(manifestPath),
                    Assert.Single(overlayCapture.Scopes)
                        .Resolution
                        .ManifestPath);
            }

            Assert.True(
                workspace.ManifestWorkspace.CloseManifest(manifestUri));

            Assert.Equal(
                VbaProjectResolutionKind.AdHoc,
                workspace.ManifestWorkspace.Resolve(callerUri).Kind);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Missed_disk_reload_is_recorded_behind_an_open_manifest_overlay()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-reconcile-overlay-reload-").FullName;
        try
        {
            var initialText = CreateProjectManifestText(
                "src/Book1",
                "Visual Basic For Applications");
            WriteProjectManifest(
                projectRoot,
                "src/Book1",
                "Visual Basic For Applications");
            var callerUri = ToFileUri(Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Caller.bas"));
            var manifestPath = Path.Combine(
                projectRoot,
                "vba-project.json");
            var manifestUri = ToFileUri(manifestPath);
            var workspace = CreateWorkspace(callerUri);
            var seeded = workspace.ManifestWorkspace
                .ReloadReconciledManifest(
                    manifestUri,
                    initialText,
                    capturedRevision: 0);
            Assert.Equal(
                VbaProjectManifestReconciliationStatus.Applied,
                seeded.Status);
            _ = workspace.CreateProjectSnapshot(callerUri);
            var overlayText = CreateProjectManifestText(
                "src/Book1",
                "Microsoft Excel 16.0 Object Library");
            var opened = workspace.ManifestWorkspace.OpenManifest(
                manifestUri,
                documentVersion: 1,
                overlayText);
            Assert.True(opened.Accepted);
            var latestDiskText = CreateProjectManifestText(
                "src/Book1",
                "Microsoft Office 16.0 Object Library");
            File.WriteAllText(manifestPath, latestDiskText);
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(
                latestDiskText,
                workspace.ManifestWorkspace
                    .CaptureReconciliationState(manifestUri)
                    .Baseline
                    .Text);
            var overlayResolution =
                workspace.ManifestWorkspace.Resolve(callerUri);
            Assert.Equal(
                "Microsoft Excel 16.0 Object Library",
                Assert.Single(overlayResolution.ReferenceEntries).Name);
            Assert.True(
                workspace.ManifestWorkspace.CloseManifest(manifestUri));

            var diskResolution =
                workspace.ManifestWorkspace.Resolve(callerUri);
            Assert.Equal(
                "Microsoft Office 16.0 Object Library",
                Assert.Single(diskResolution.ReferenceEntries).Name);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Backing_manifest_validation_error_is_exposed_after_overlay_close()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-reconcile-overlay-invalid-").FullName;
        try
        {
            var initialText = CreateProjectManifestText(
                "src/Book1",
                "Visual Basic For Applications");
            WriteProjectManifest(
                projectRoot,
                "src/Book1",
                "Visual Basic For Applications");
            var callerUri = ToFileUri(Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Caller.bas"));
            var manifestPath = Path.Combine(
                projectRoot,
                "vba-project.json");
            var manifestUri = ToFileUri(manifestPath);
            var workspace = CreateWorkspace(callerUri);
            var seeded = workspace.ManifestWorkspace
                .ReloadReconciledManifest(
                    manifestUri,
                    initialText,
                    capturedRevision: 0);
            Assert.Equal(
                VbaProjectManifestReconciliationStatus.Applied,
                seeded.Status);
            _ = workspace.CreateProjectSnapshot(callerUri);
            var overlayText = CreateProjectManifestText(
                "src/Book1",
                "Microsoft Excel 16.0 Object Library");
            var opened = workspace.ManifestWorkspace.OpenManifest(
                manifestUri,
                documentVersion: 1,
                overlayText);
            Assert.True(opened.Accepted);
            File.WriteAllText(manifestPath, "{\"schemaVersion\":");
            var manifestEvents = new RecordingManifestEvents();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    manifestEvents: manifestEvents,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(
                workspace.ManifestWorkspace.TryGetEffectiveManifest(
                    manifestUri,
                    out _,
                    out var effectiveOverlayText,
                    out var overlayError));
            Assert.Equal(overlayText, effectiveOverlayText);
            Assert.Null(overlayError);
            Assert.Empty(manifestEvents.ValidationFailures);
            Assert.True(
                workspace.ManifestWorkspace.CloseManifest(manifestUri));

            Assert.True(
                workspace.ManifestWorkspace.TryGetEffectiveManifest(
                    manifestUri,
                    out _,
                    out var fallbackText,
                    out var backingError));
            Assert.Equal(initialText, fallbackText);
            Assert.NotNull(backingError);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Watched_manifest_update_after_scan_capture_rejects_stale_manifest_result()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-reconcile-manifest-race-").FullName;
        try
        {
            WriteProjectManifest(
                projectRoot,
                "src/Book1",
                "Visual Basic For Applications");
            var callerUri = ToFileUri(
                Path.Combine(projectRoot, "src", "Book1", "Caller.bas"));
            var manifestPath = Path.Combine(projectRoot, "vba-project.json");
            var manifestUri = ToFileUri(manifestPath);
            var workspace = CreateWorkspace(callerUri);
            _ = workspace.CreateProjectSnapshot(callerUri);
            var boundary = new BlockingFirstDiskSourceBoundary(
                new VbaFileSystemProjectDiskSourceBoundary());
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    sourceBoundary: boundary,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            var trigger = reconciliation.TriggerAsync();
            await boundary.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var watchedText = CreateProjectManifestText(
                "src/Book1",
                "Microsoft Office 16.0 Object Library");
            File.WriteAllText(manifestPath, watchedText);
            var watched = await scheduler.AdmitRequiredMutationAsync(
                    "workspace/didChangeWatchedFiles",
                    _ =>
                    {
                        Assert.True(
                            workspace.ManifestWorkspace.ReloadManifest(
                                manifestUri));
                        return Task.CompletedTask;
                    },
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
            await watched.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            boundary.Complete(CreateScan(
                boundary.Scope,
                CreateProjectManifestText(
                    "src/Book1",
                    "Microsoft Excel 16.0 Object Library")));

            await trigger.WaitAsync(TimeSpan.FromSeconds(5));
            var snapshot = workspace.CreateProjectSnapshot(callerUri);

            Assert.Single(snapshot.Resolution.ReferenceEntries);
            Assert.Equal(
                "Microsoft Office 16.0 Object Library",
                snapshot.Resolution.ReferenceEntries[0].Name);
            Assert.Equal(3, boundary.ScanCount);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Manual_cycles_detect_manifest_delete_and_later_add()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-reconcile-manifest-set-").FullName;
        try
        {
            WriteProjectManifest(
                projectRoot,
                "src/Book1",
                "Visual Basic For Applications");
            var callerUri = ToFileUri(
                Path.Combine(projectRoot, "src", "Book1", "Caller.bas"));
            var manifestPath = Path.Combine(projectRoot, "vba-project.json");
            var manifestUri = ToFileUri(manifestPath);
            var workspace = CreateWorkspace(callerUri);
            _ = workspace.CreateProjectSnapshot(callerUri);
            var manifestEvents = new RecordingManifestEvents();
            await using var scheduler = CreateSerialScheduler();
            await using var reconciliation =
                new VbaProjectDiskReconciliationCoordinator(
                    workspace,
                    manifestEvents: manifestEvents,
                    cadence: Timeout.InfiniteTimeSpan);
            reconciliation.AttachScheduler(scheduler);

            File.Delete(manifestPath);
            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            var deletedSnapshot = workspace.CreateProjectSnapshot(callerUri);

            Assert.Equal(
                VbaProjectResolutionKind.AdHoc,
                deletedSnapshot.Resolution.Kind);
            Assert.Contains(manifestUri, manifestEvents.DeletedUris);

            File.WriteAllText(
                manifestPath,
                CreateProjectManifestText(
                    "src/Book1",
                    "Microsoft Excel 16.0 Object Library"));
            await reconciliation.TriggerAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            var addedSnapshot = workspace.CreateProjectSnapshot(callerUri);

            Assert.Equal(
                VbaProjectResolutionKind.ManifestDocument,
                addedSnapshot.Resolution.Kind);
            Assert.Single(addedSnapshot.Resolution.ReferenceEntries);
            Assert.Equal(
                "Microsoft Excel 16.0 Object Library",
                addedSnapshot.Resolution.ReferenceEntries[0].Name);
            Assert.Single(manifestEvents.SelectionChanges);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    private static void WriteProjectManifest(
        string projectRoot,
        string sourcePath = "src/Book1",
        params string[] references)
    {
        var manifestPath = Path.Combine(projectRoot, "vba-project.json");
        var sourceDirectory = Path.Combine(
            projectRoot,
            sourcePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(manifestPath, CreateProjectManifestText(
            sourcePath,
            references));
    }

    private static string CreateProjectManifestText(
        string sourcePath = "src/Book1",
        params string[] references)
        => JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            projectName = "DiskReconciliation",
            primaryDocument = "Book1",
            documents = new Dictionary<string, object>
            {
                ["Book1"] = new
                {
                    kind = "excel",
                    sourcePath,
                    templatePath = "src/Book1/Book1.xlsm",
                    binPath = "bin/Book1/Book1.xlsm",
                    publishPath = "publish/Book1/Book1.xlsm",
                    references = references
                        .Select(reference => new { name = reference })
                        .ToArray()
                }
            }
        });

    private static string CreateTwoDocumentProjectManifestText(
        string firstSourcePath,
        string secondSourcePath,
        params string[] references)
        => JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            projectName = "DiskReconciliation",
            primaryDocument = "Book1",
            documents = new Dictionary<string, object>
            {
                ["Book1"] = CreateDocumentManifest(
                    firstSourcePath,
                    "Book1",
                    references),
                ["Book2"] = CreateDocumentManifest(
                    secondSourcePath,
                    "Book2",
                    references)
            }
        });

    private static object CreateDocumentManifest(
        string sourcePath,
        string documentName,
        IReadOnlyList<string> references)
        => new
        {
            kind = "excel",
            sourcePath,
            templatePath = $"src/{documentName}/{documentName}.xlsm",
            binPath = $"bin/{documentName}/{documentName}.xlsm",
            publishPath = $"publish/{documentName}/{documentName}.xlsm",
            references = references
                .Select(reference => new { name = reference })
                .ToArray()
        };

    private static string CreateModule(string moduleName, string procedureName)
        => string.Join('\n', [
            $"Attribute VB_Name = \"{moduleName}\"",
            $"Public Function {procedureName}() As String",
            "End Function"
        ]);

    private static VbaLanguageWorkspace CreateWorkspace(string callerUri)
    {
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()));
        workspace.UpdateDocument(
            callerUri,
            string.Join('\n', [
                "Attribute VB_Name = \"Caller\"",
                "Public Sub Run()",
                "End Sub"
            ]));
        return workspace;
    }

    private static bool RemoveTrackedDocument(
        VbaLanguageWorkspace workspace,
        string uri,
        bool closeDocument)
        => closeDocument
            ? workspace.CloseDocument(uri)
            : workspace.RemoveDocument(uri);

    private static VbaInteractiveWorkScheduler CreateSerialScheduler()
        => new(
            options: new VbaInteractiveWorkSchedulerOptions(
                CoalesceSupersededMutations: true,
                MaxOwnedWork: 1));

    private static VbaProjectDiskScopeScan CreateScan(
        VbaProjectDiskReconciliationScope scope,
        string manifestText,
        params (string Path, string Text)[] replacements)
    {
        var replacementsByPath = replacements.ToDictionary(
            replacement => Path.GetFullPath(replacement.Path),
            replacement => replacement.Text,
            StringComparer.OrdinalIgnoreCase);
        var sources = scope.KnownSources
            .Select(
                source => new VbaProjectDiskSource(
                    source.Uri,
                    source.FullPath,
                    replacementsByPath.TryGetValue(
                        source.FullPath,
                        out var replacement)
                            ? replacement
                            : source.Text))
            .ToArray();
        VbaProjectDiskManifest? manifest = null;
        var manifestUri = scope.ManifestCandidates
            .FirstOrDefault(candidate => candidate.Baseline.Exists)
            ?.Uri
            ?? scope.ManifestCandidates.FirstOrDefault()?.Uri;
        if (manifestUri is not null
            && VbaProjectResolver.TryGetLocalPath(manifestUri) is { } manifestPath)
        {
            manifest = new VbaProjectDiskManifest(
                manifestUri,
                Path.GetFullPath(manifestPath),
                manifestText);
        }

        return new VbaProjectDiskScopeScan(sources, manifest);
    }

    private static string ToFileUri(string path)
        => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    private sealed class RecordingDiagnostics
        : IVbaProjectDiskReconciliationDiagnostics
    {
        public List<string> TrackedUris { get; } = [];

        public List<string> EmptyUris { get; } = [];

        public void EnqueueTrackedDiagnostics(
            string uri,
            CancellationToken cancellationToken)
            => TrackedUris.Add(uri);

        public void EnqueueEmptyDiagnostics(
            string uri,
            CancellationToken cancellationToken)
            => EmptyUris.Add(uri);
    }

    private sealed class RecordingCommitObserver
        : IVbaProjectDiskReconciliationCommitObserver
    {
        public int ScopeFenceValidationCount { get; private set; }

        public void ScopeFenceValidated(
            string authorityKey,
            long manifestBarrierRevision,
            long authorityGeneration)
            => ScopeFenceValidationCount++;
    }

    private sealed class RecordingManifestEvents
        : IVbaProjectDiskReconciliationManifestEvents
    {
        public List<(string Uri, string Text)> SelectionChanges { get; } = [];

        public List<(string Uri, VbaProjectManifestException Error)>
            ValidationFailures { get; } = [];

        public List<string> DeletedUris { get; } = [];

        public List<string> ValidationRecoveredUris { get; } = [];

        public List<string> AuthorityTransferredSourceUris { get; } = [];

        public Action<string>? ValidationFailureObserved { get; set; }

        public void ManifestSelectionChanged(
            string uri,
            string text,
            CancellationToken cancellationToken)
            => SelectionChanges.Add((uri, text));

        public void ManifestDeleted(
            string uri,
            CancellationToken cancellationToken)
            => DeletedUris.Add(uri);

        public void ManifestValidationFailed(
            string uri,
            VbaProjectManifestException error,
            CancellationToken cancellationToken)
        {
            ValidationFailures.Add((uri, error));
            ValidationFailureObserved?.Invoke(uri);
        }

        public void ManifestValidationRecovered(
            string uri,
            CancellationToken cancellationToken)
            => ValidationRecoveredUris.Add(uri);

        public void ProjectAuthorityTransferred(
            string sourceUri,
            CancellationToken cancellationToken)
            => AuthorityTransferredSourceUris.Add(sourceUri);

    }

    private sealed class RecordingReconciliationFailures
        : IVbaProjectDiskReconciliationFailureObserver
    {
        public TaskCompletionSource<IOException> Observed { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReconciliationFailed(Exception error)
        {
            if (error is IOException ioError)
            {
                Observed.TrySetResult(ioError);
            }
        }
    }

    private sealed class CommitAdmissionTimingSink
        : IVbaInteractiveWorkTimingSink
    {
        public TaskCompletionSource CommitAdmitted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void RecordAdmission(VbaInteractiveWorkAdmissionTiming timing)
        {
            if (timing.Method == "vba/reconcile/commit")
            {
                CommitAdmitted.TrySetResult();
            }
        }

        public void RecordCompletion(VbaInteractiveWorkCompletionTiming timing)
        {
        }
    }

    private sealed class BlockingWorkspaceVersionSnapshotObserver
        : IVbaProjectSnapshotBuildObserver
    {
        private readonly long blockedVersion;
        private readonly Task release;
        private int blocked;

        public BlockingWorkspaceVersionSnapshotObserver(
            long blockedVersion,
            Task release)
        {
            this.blockedVersion = blockedVersion;
            this.release = release;
        }

        public TaskCompletionSource Blocked { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void BeforeStore(
            long workspaceVersion,
            CancellationToken cancellationToken)
        {
            if (workspaceVersion != blockedVersion
                || Interlocked.CompareExchange(ref blocked, 1, 0) != 0)
            {
                return;
            }

            Blocked.TrySetResult();
            release.Wait(cancellationToken);
        }
    }

    private sealed class BlockingSnapshotCaptureObserver
        : IVbaProjectSnapshotBuildObserver
    {
        private readonly int blockedCaptureNumber;
        private readonly Task release;
        private int captureCount;

        public BlockingSnapshotCaptureObserver(
            int blockedCaptureNumber,
            Task release)
        {
            this.blockedCaptureNumber = blockedCaptureNumber;
            this.release = release;
        }

        public TaskCompletionSource Blocked { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void BeforeCapture(
            string activeUri,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref captureCount)
                != blockedCaptureNumber)
            {
                return;
            }

            Blocked.TrySetResult();
            release.Wait(cancellationToken);
        }

        public void BeforeStore(
            long workspaceVersion,
            CancellationToken cancellationToken)
        {
        }
    }

    private sealed class BlockingSnapshotStoreObserver
        : IVbaProjectSnapshotBuildObserver
    {
        private readonly int blockedStoreNumber;
        private readonly Task release;
        private int storeCount;

        public BlockingSnapshotStoreObserver(
            int blockedStoreNumber,
            Task release)
        {
            this.blockedStoreNumber = blockedStoreNumber;
            this.release = release;
        }

        public TaskCompletionSource Blocked { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void BeforeStore(
            long workspaceVersion,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref storeCount)
                != blockedStoreNumber)
            {
                return;
            }

            Blocked.TrySetResult();
            release.Wait(cancellationToken);
        }
    }

    private sealed class BlockingSnapshotBuildObserver
        : IVbaProjectSnapshotBuildObserver
    {
        private readonly int blockedBuildNumber;
        private readonly Task release;
        private int buildCount;

        public BlockingSnapshotBuildObserver(
            int blockedBuildNumber,
            Task release)
        {
            this.blockedBuildNumber = blockedBuildNumber;
            this.release = release;
        }

        public TaskCompletionSource Blocked { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void BeforeBuildProjectSnapshot(
            string activeUri,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref buildCount)
                != blockedBuildNumber)
            {
                return;
            }

            Blocked.TrySetResult();
            release.Wait(cancellationToken);
        }

        public void BeforeStore(
            long workspaceVersion,
            CancellationToken cancellationToken)
        {
        }
    }

    private sealed class GuardedProjectFileSystem(
        IVbaProjectFileSystem inner)
        : IVbaProjectFileSystem
    {
        public int OperationCount { get; private set; }

        public bool RejectOperations { get; set; }

        public bool FileExists(string path)
        {
            RecordOperation();
            return inner.FileExists(path);
        }

        public bool DirectoryExists(string path)
        {
            RecordOperation();
            return inner.DirectoryExists(path);
        }

        public IEnumerable<string> EnumerateSourceFiles(
            string rootPath,
            string searchPattern,
            SearchOption searchOption)
        {
            RecordOperation();
            return inner.EnumerateSourceFiles(
                rootPath,
                searchPattern,
                searchOption);
        }

        public bool TryGetSourceMetadata(
            string path,
            out VbaProjectSourceFileMetadata metadata)
        {
            RecordOperation();
            return inner.TryGetSourceMetadata(path, out metadata);
        }

        public string ReadManifestText(string path)
        {
            RecordOperation();
            return inner.ReadManifestText(path);
        }

        public byte[] ReadSourceBytes(string path)
        {
            RecordOperation();
            return inner.ReadSourceBytes(path);
        }

        private void RecordOperation()
        {
            if (RejectOperations)
            {
                throw new InvalidOperationException(
                    "Scope retirement performed filesystem I/O.");
            }

            OperationCount++;
        }
    }

    private sealed class CountingDiskSourceBoundary
        : IVbaProjectDiskSourceBoundary
    {
        private readonly IVbaProjectDiskSourceBoundary inner;
        private int scanCount;

        public CountingDiskSourceBoundary(
            IVbaProjectDiskSourceBoundary inner)
        {
            this.inner = inner;
        }

        public int ScanCount => Volatile.Read(ref scanCount);

        public List<string> AuthorityKeys { get; } = [];

        public List<string> RootPaths { get; } = [];

        public Task<VbaProjectDiskScopeScan> ScanAsync(
            VbaProjectDiskReconciliationScope scope,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref scanCount);
            lock (AuthorityKeys)
            {
                AuthorityKeys.Add(scope.AuthorityKey);
                RootPaths.Add(scope.Resolution.RootPath);
            }

            return inner.ScanAsync(scope, cancellationToken);
        }
    }

    private sealed class BlockingSecondDiskSourceBoundary
        : IVbaProjectDiskSourceBoundary
    {
        private readonly IVbaProjectDiskSourceBoundary inner;
        private int scanCount;

        public BlockingSecondDiskSourceBoundary(
            IVbaProjectDiskSourceBoundary inner)
        {
            this.inner = inner;
        }

        public TaskCompletionSource SecondScanStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int ScanCount => Volatile.Read(ref scanCount);

        public async Task<VbaProjectDiskScopeScan> ScanAsync(
            VbaProjectDiskReconciliationScope scope,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref scanCount) == 2)
            {
                SecondScanStarted.TrySetResult();
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);
            }

            return await inner.ScanAsync(
                scope,
                cancellationToken);
        }
    }

    private sealed class BlockingFirstDiskSourceBoundary
        : IVbaProjectDiskSourceBoundary
    {
        private readonly IVbaProjectDiskSourceBoundary inner;
        private readonly TaskCompletionSource<VbaProjectDiskScopeScan>
            completion = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
        private int scanCount;

        public BlockingFirstDiskSourceBoundary(
            IVbaProjectDiskSourceBoundary inner)
        {
            this.inner = inner;
        }

        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public VbaProjectDiskReconciliationScope Scope { get; private set; } =
            default!;

        public int ScanCount => Volatile.Read(ref scanCount);

        public Task<VbaProjectDiskScopeScan> ScanAsync(
            VbaProjectDiskReconciliationScope scope,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref scanCount) != 1)
            {
                return inner.ScanAsync(scope, cancellationToken);
            }

            Scope = scope;
            Started.TrySetResult();
            return completion.Task;
        }

        public void Complete(VbaProjectDiskScopeScan scan)
            => completion.TrySetResult(scan);
    }

    private sealed class ChurningInvalidManifestDiskSourceBoundary
        : IVbaProjectDiskSourceBoundary
    {
        private readonly string projectRoot;
        private readonly bool useSamePath;
        private int scanCount;

        public ChurningInvalidManifestDiskSourceBoundary(
            string projectRoot,
            bool useSamePath = false)
        {
            this.projectRoot = projectRoot;
            this.useSamePath = useSamePath;
        }

        public int ScanCount => Volatile.Read(ref scanCount);

        public Task<VbaProjectDiskScopeScan> ScanAsync(
            VbaProjectDiskReconciliationScope scope,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attempt = Interlocked.Increment(ref scanCount);
            var manifestPath = useSamePath
                ? Path.Combine(
                    projectRoot,
                    "vba-project.json")
                : Path.Combine(
                    projectRoot,
                    $"Churn{attempt}",
                    "vba-project.json");
            return Task.FromResult(
                new VbaProjectDiskScopeScan(
                    Sources: [],
                    new VbaProjectDiskManifest(
                        ToFileUri(manifestPath),
                        manifestPath,
                        $"{{\"schemaVersion\":,\"attempt\":{attempt}}}")));
        }
    }

    private sealed class RepairManifestOnSecondScanDiskSourceBoundary
        : IVbaProjectDiskSourceBoundary
    {
        private readonly IVbaProjectDiskSourceBoundary inner;
        private readonly string manifestPath;
        private readonly string repairedText;
        private int scanCount;

        public RepairManifestOnSecondScanDiskSourceBoundary(
            IVbaProjectDiskSourceBoundary inner,
            string manifestPath,
            string repairedText)
        {
            this.inner = inner;
            this.manifestPath = manifestPath;
            this.repairedText = repairedText;
        }

        public int ScanCount => Volatile.Read(ref scanCount);

        public Task<VbaProjectDiskScopeScan> ScanAsync(
            VbaProjectDiskReconciliationScope scope,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref scanCount) == 2)
            {
                File.WriteAllText(
                    manifestPath,
                    repairedText);
            }

            return inner.ScanAsync(scope, cancellationToken);
        }
    }

    private sealed class BlockingDiskSourceBoundary
        : IVbaProjectDiskSourceBoundary
    {
        private readonly TaskCompletionSource<VbaProjectDiskScopeScan> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public VbaProjectDiskReconciliationScope Scope { get; private set; } = default!;

        public Task<VbaProjectDiskScopeScan> ScanAsync(
            VbaProjectDiskReconciliationScope scope,
            CancellationToken cancellationToken)
        {
            Scope = scope;
            Started.TrySetResult();
            return completion.Task;
        }

        public void Complete(VbaProjectDiskScopeScan scan)
            => completion.TrySetResult(scan);

        public void Fail(Exception error)
            => completion.TrySetException(error);
    }

    private sealed class CancellationCallbackBlockingDiskSourceBoundary
        : IVbaProjectDiskSourceBoundary,
          IDisposable
    {
        private readonly TaskCompletionSource<VbaProjectDiskScopeScan> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim releaseCancellation = new(false);
        private CancellationTokenRegistration cancellationRegistration;

        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public VbaProjectDiskReconciliationScope Scope { get; private set; } = default!;

        public Task<VbaProjectDiskScopeScan> ScanAsync(
            VbaProjectDiskReconciliationScope scope,
            CancellationToken cancellationToken)
        {
            Scope = scope;
            cancellationRegistration = cancellationToken.Register(
                () =>
                {
                    CancellationStarted.TrySetResult();
                    releaseCancellation.Wait();
                });
            Started.TrySetResult();
            return completion.Task;
        }

        public void Complete(VbaProjectDiskScopeScan scan)
            => completion.TrySetResult(scan);

        public void ReleaseCancellationCallback()
            => releaseCancellation.Set();

        public void Dispose()
        {
            releaseCancellation.Set();
            completion.TrySetCanceled();
            cancellationRegistration.Dispose();
            releaseCancellation.Dispose();
        }
    }

    private sealed class GatedConcurrencyDiskSourceBoundary
        : IVbaProjectDiskSourceBoundary
    {
        private readonly int expectedScopeCount;
        private readonly IVbaProjectDiskSourceBoundary inner;
        private readonly TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int active;
        private int maxConcurrency;
        private int started;

        public GatedConcurrencyDiskSourceBoundary(
            int expectedScopeCount,
            IVbaProjectDiskSourceBoundary inner)
        {
            this.expectedScopeCount = expectedScopeCount;
            this.inner = inner;
        }

        public TaskCompletionSource FirstWaveStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllScopesStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int MaxConcurrency => Volatile.Read(ref maxConcurrency);

        public List<VbaProjectDiskReconciliationScope> Scopes { get; } = [];

        public async Task<VbaProjectDiskScopeScan> ScanAsync(
            VbaProjectDiskReconciliationScope scope,
            CancellationToken cancellationToken)
        {
            lock (Scopes)
            {
                Scopes.Add(scope);
            }

            var current = Interlocked.Increment(ref active);
            UpdateMaximum(current);
            var startedCount = Interlocked.Increment(ref started);
            if (startedCount >= 2)
            {
                FirstWaveStarted.TrySetResult();
            }

            if (startedCount == expectedScopeCount)
            {
                AllScopesStarted.TrySetResult();
            }

            try
            {
                await release.Task.WaitAsync(cancellationToken);
                return await inner.ScanAsync(scope, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }

        public void Release()
            => release.TrySetResult();

        private void UpdateMaximum(int candidate)
        {
            while (true)
            {
                var current = Volatile.Read(ref maxConcurrency);
                if (candidate <= current
                    || Interlocked.CompareExchange(
                        ref maxConcurrency,
                        candidate,
                        current) == current)
                {
                    return;
                }
            }
        }
    }

    private sealed class FailOnceDiskSourceBoundary
        : IVbaProjectDiskSourceBoundary
    {
        private readonly IVbaProjectDiskSourceBoundary inner;
        private int attempts;

        public FailOnceDiskSourceBoundary(
            IVbaProjectDiskSourceBoundary inner)
        {
            this.inner = inner;
        }

        public Task<VbaProjectDiskScopeScan> ScanAsync(
            VbaProjectDiskReconciliationScope scope,
            CancellationToken cancellationToken)
            => Interlocked.Increment(ref attempts) == 1
                ? Task.FromException<VbaProjectDiskScopeScan>(
                    new IOException("Expected reconciliation scan failure."))
                : inner.ScanAsync(scope, cancellationToken);
    }
}
