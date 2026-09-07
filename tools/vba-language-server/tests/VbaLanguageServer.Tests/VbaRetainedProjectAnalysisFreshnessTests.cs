using System.Text;
using System.Text.Json.Nodes;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Workspace;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed partial class VbaRetainedProjectAnalysisTests
{
    [Fact]
    public void Inactive_reopen_follows_a_replaced_manifest_source_set()
    {
        using var project = new ProjectFixture();
        var observer = new BuildObserver();
        var workspace = CreateWorkspace(observer);
        workspace.OpenDocument(project.CallerUri, 1, project.CallerText);
        _ = workspace.CreateProjectSnapshot(project.CallerUri);
        Assert.True(workspace.CloseDocument(project.CallerUri));
        var replacementRoot = Path.Combine(project.Root, "src", "Replacement");
        Directory.CreateDirectory(replacementRoot);
        var callerPath = Path.Combine(replacementRoot, "Caller.bas");
        var helperPath = Path.Combine(replacementRoot, "Helper.bas");
        File.WriteAllText(callerPath, project.CallerText, new UTF8Encoding(true));
        File.WriteAllText(helperPath, project.HelperText, new UTF8Encoding(true));
        var manifestPath = Path.Combine(project.Root, "vba-project.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        manifest["documents"]!["Book1"]!["sourcePath"] = "src/Replacement";
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        var callerUri = new Uri(callerPath).AbsoluteUri;
        var helperUri = new Uri(helperPath).AbsoluteUri;
        workspace.OpenDocument(callerUri, 1, project.CallerText);

        var reopened = workspace.CreateProjectSnapshot(callerUri);

        Assert.Equal(2, observer.SemanticBuilds);
        Assert.Equal(VbaProjectResolutionKind.ManifestDocument, reopened.Resolution.Kind);
        Assert.Equal(Path.GetFullPath(replacementRoot), reopened.Resolution.RootPath,
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal([callerUri, helperUri], reopened.SourceDocuments.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(helperUri, reopened.SemanticInventory.ResolveDefinition(callerUri, 2, 8)!.Uri);
        using var oracle = CreateWorkspace(new BuildObserver());
        oracle.OpenDocument(callerUri, 1, project.CallerText);
        var fresh = oracle.CreateProjectSnapshot(callerUri);
        Assert.Equal(fresh.SemanticInventory.GetSemanticTokenData(callerUri),
            reopened.SemanticInventory.GetSemanticTokenData(callerUri));
        Assert.Equal(fresh.SemanticInventory.FindReferences(callerUri, 2, 8),
            reopened.SemanticInventory.FindReferences(callerUri, 2, 8));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Inactive_reopen_observes_nested_project_ownership_changes(
        bool nestedManifestExistsInitially)
    {
        using var project = new ProjectFixture();
        var nestedRoot = Path.Combine(project.Root, "src", "Nested");
        var childPath = Path.Combine(nestedRoot, "src", "Child.bas");
        Directory.CreateDirectory(Path.GetDirectoryName(childPath)!);
        File.WriteAllText(childPath,
            "Attribute VB_Name = \"Child\"\nPublic Sub ChildValue()\nEnd Sub\n", new UTF8Encoding(true));
        var childUri = new Uri(childPath).AbsoluteUri;
        var nestedManifestPath = Path.Combine(nestedRoot, "vba-project.json");
        var nestedManifestText = File.ReadAllText(Path.Combine(project.Root, "vba-project.json"));
        if (nestedManifestExistsInitially)
        {
            File.WriteAllText(nestedManifestPath, nestedManifestText);
        }

        var observer = new BuildObserver();
        var workspace = CreateWorkspace(observer);
        workspace.OpenDocument(project.CallerUri, 1, project.CallerText);
        var original = workspace.CreateProjectSnapshot(project.CallerUri);
        Assert.Equal(!nestedManifestExistsInitially, original.SourceDocuments.ContainsKey(childUri));
        _ = original.SemanticInventory.GetSemanticTokenData(project.CallerUri);
        Assert.True(workspace.CloseDocument(project.CallerUri));
        if (nestedManifestExistsInitially)
        {
            File.Delete(nestedManifestPath);
        }
        else
        {
            File.WriteAllText(nestedManifestPath, nestedManifestText);
        }

        workspace.OpenDocument(project.CallerUri, 1, project.CallerText);
        var reopened = workspace.CreateProjectSnapshot(project.CallerUri);

        Assert.Equal(2, observer.SemanticBuilds);
        Assert.Equal(nestedManifestExistsInitially, reopened.SourceDocuments.ContainsKey(childUri));
        Assert.Equal(nestedManifestExistsInitially ? 1 : 0,
            reopened.SemanticInventory.GetWorkspaceSymbols("ChildValue").Count);
        using var oracle = project.CreateFreshWorkspace();
        AssertCurrentAnalysisMatchesFresh(project, reopened,
            oracle.Workspace.CreateProjectSnapshot(project.CallerUri));
    }

    [Fact]
    public async Task Workspace_disposal_rejects_a_pending_store_after_analysis_completes()
    {
        using var project = new ProjectFixture();
        var observer = new PendingStoreObserver();
        var workspace = CreatePendingWorkspace(observer);
        Task<VbaProjectSnapshot>? pending = null;
        try
        {
            workspace.OpenDocument(project.CallerUri, 1, project.CallerText);
            _ = workspace.CreateProjectSnapshot(project.CallerUri);
            workspace.UpdateDocument(project.CallerUri,
                project.CallerText.Replace("Run", "Resume", StringComparison.Ordinal));
            pending = Task.Run(() => workspace.CreateProjectSnapshot(project.CallerUri));
            await observer.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));

            workspace.Dispose();
            observer.Release.TrySetResult();
            _ = await pending.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(0, workspace.RetainedReusableAnalysisCount);
            Assert.Equal(0, workspace.RetainedReusableAnalysisBytes);
            Assert.Equal(0, workspace.RetainedProjectSnapshotCount);
            Assert.Equal(0, workspace.RetainedProjectScopeInvalidationStateCount);
        }
        finally
        {
            observer.Release.TrySetResult();
            if (pending is not null)
            {
                _ = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            }

            workspace.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_stale_or_cancelled_build_cannot_replace_a_reopened_lifecycle(
        bool cancelStaleBuild)
    {
        using var project = new ProjectFixture();
        using var cancellation = new CancellationTokenSource();
        var observer = new PendingStoreObserver();
        var workspace = CreatePendingWorkspace(observer);
        Task<VbaProjectSnapshot>? pending = null;
        try
        {
            workspace.OpenDocument(project.CallerUri, 1, project.CallerText);
            var original = workspace.CreateProjectSnapshot(project.CallerUri);
            var tokens = original.SemanticInventory.GetSemanticTokenData(project.CallerUri).ToArray();
            Assert.True(workspace.CloseDocument(project.CallerUri));
            workspace.OpenDocument(project.CallerUri, 1,
                project.CallerText.Replace("BuildValue", "FetchValue", StringComparison.Ordinal));
            pending = Task.Run(() => workspace.CreateProjectSnapshot(project.CallerUri, cancellation.Token));
            await observer.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(workspace.CloseDocument(project.CallerUri));
            workspace.OpenDocument(project.CallerUri, 1, project.CallerText);
            var reopened = workspace.CreateProjectSnapshot(project.CallerUri);
            Assert.Equal(tokens, reopened.SemanticInventory.GetSemanticTokenData(project.CallerUri));

            if (cancelStaleBuild)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            }
            else
            {
                observer.Release.TrySetResult();
                _ = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            }

            Assert.Same(reopened, workspace.CreateProjectSnapshot(project.CallerUri));
            Assert.Equal(1, workspace.RetainedReusableAnalysisCount);
            Assert.Equal(2, observer.SemanticBuilds);
            Assert.True(workspace.CloseDocument(project.CallerUri));
            workspace.OpenDocument(project.HelperUri, 1, project.HelperText);
            var switched = workspace.CreateProjectSnapshot(project.HelperUri);
            Assert.Equal(2, observer.SemanticBuilds);
            using var oracle = project.CreateFreshWorkspace();
            AssertCurrentAnalysisMatchesFresh(project, switched,
                oracle.Workspace.CreateProjectSnapshot(project.CallerUri));
        }
        finally
        {
            cancellation.Cancel();
            observer.Release.TrySetResult();
            if (pending is not null)
            {
                try
                {
                    _ = await pending.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }

    [Fact]
    public async Task Eviction_keeps_a_pinned_read_valid_without_restoring_its_retired_scope()
    {
        using var project = new ProjectFixture();
        var peers = Enumerable.Range(0, 4).Select(_ => new ProjectFixture()).ToArray();
        var observer = new PendingStoreObserver();
        var workspace = CreatePendingWorkspace(observer);
        Task<VbaProjectSnapshot>? pending = null;
        try
        {
            workspace.OpenDocument(project.CallerUri, 1, project.CallerText);
            var original = workspace.CreateProjectSnapshot(project.CallerUri);
            var tokens = original.SemanticInventory.GetSemanticTokenData(project.CallerUri).ToArray();
            Assert.True(workspace.CloseDocument(project.CallerUri));
            workspace.OpenDocument(project.CallerUri, 1, project.CallerText);
            pending = Task.Run(() => workspace.CreateProjectSnapshot(project.CallerUri));
            await observer.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(workspace.CloseDocument(project.CallerUri));
            foreach (var peer in peers)
            {
                workspace.OpenDocument(peer.CallerUri, 1, peer.CallerText);
                _ = workspace.CreateProjectSnapshot(peer.CallerUri);
                Assert.True(workspace.CloseDocument(peer.CallerUri));
            }

            Assert.Equal(4, workspace.RetainedReusableAnalysisCount);
            observer.Release.TrySetResult();
            var pinned = await pending.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(tokens, pinned.SemanticInventory.GetSemanticTokenData(project.CallerUri));
            Assert.Equal(4, workspace.RetainedReusableAnalysisCount);
            Assert.Equal(0, workspace.RetainedProjectSnapshotCount);
            workspace.OpenDocument(project.CallerUri, 1, project.CallerText);
            var rebuilt = workspace.CreateProjectSnapshot(project.CallerUri);
            Assert.Equal(6, observer.SemanticBuilds);
            using var oracle = project.CreateFreshWorkspace();
            AssertCurrentAnalysisMatchesFresh(project, rebuilt,
                oracle.Workspace.CreateProjectSnapshot(project.CallerUri));
        }
        finally
        {
            observer.Release.TrySetResult();
            if (pending is not null)
            {
                _ = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            }

            foreach (var peer in peers)
            {
                peer.Dispose();
            }
        }
    }

    [Fact]
    public void Cancellation_after_source_projection_does_not_retain_partial_analysis()
    {
        using var project = new ProjectFixture();
        using var cancellation = new CancellationTokenSource();
        var observer = new CancelFirstSemanticBuildObserver(cancellation);
        var workspace = CreatePendingWorkspace(observer);
        workspace.OpenDocument(project.CallerUri, 1, project.CallerText);

        Assert.Throws<OperationCanceledException>(() =>
            workspace.CreateProjectSnapshot(project.CallerUri, cancellation.Token));

        Assert.Equal(0, workspace.RetainedReusableAnalysisCount);
        Assert.Equal(0, workspace.RetainedProjectSnapshotCount);
        Assert.True(workspace.CloseDocument(project.CallerUri));
        workspace.OpenDocument(project.CallerUri, 1, project.CallerText);
        var completed = workspace.CreateProjectSnapshot(project.CallerUri);
        Assert.Equal(1, workspace.RetainedReusableAnalysisCount);
        using var oracle = project.CreateFreshWorkspace();
        AssertCurrentAnalysisMatchesFresh(project, completed,
            oracle.Workspace.CreateProjectSnapshot(project.CallerUri));
    }

    private static VbaLanguageWorkspace CreatePendingWorkspace(
        IVbaProjectSnapshotBuildObserver observer)
        => new(new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            NullVbaDocumentAnalysisBuildObserver.Instance, observer);

    private sealed class PendingStoreObserver : IVbaProjectSnapshotBuildObserver
    {
        private int semanticBuilds;
        private int stores;
        public int SemanticBuilds => Volatile.Read(ref semanticBuilds);
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void BeforeBuildSemanticInventory(string activeUri, CancellationToken cancellationToken)
            => Interlocked.Increment(ref semanticBuilds);

        public void BeforeStore(long workspaceVersion, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref stores) == 2)
            {
                Blocked.TrySetResult();
                Release.Task.Wait(cancellationToken);
            }
        }
    }

    private sealed class CancelFirstSemanticBuildObserver(CancellationTokenSource cancellation)
        : IVbaProjectSnapshotBuildObserver
    {
        private bool first = true;

        public void BeforeBuildSemanticInventory(string activeUri, CancellationToken cancellationToken)
        {
            if (first)
            {
                first = false;
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        public void BeforeStore(long workspaceVersion, CancellationToken cancellationToken)
        {
        }
    }

    [Fact]
    public void Inactive_reopen_uses_the_current_reference_catalog_semantics()
    {
        using var project = new ProjectFixture();
        const string referenceName = "Generated Library";
        var manifestPath = Path.Combine(project.Root, "vba-project.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        manifest["documents"]!["Book1"]!["references"] = new JsonArray(
            new JsonObject { ["name"] = referenceName, ["requested"] = true });
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        var callerText = project.CallerText.Replace("End Sub",
            "    Dim generated As GeneratedType\nEnd Sub", StringComparison.Ordinal);
        File.WriteAllText(new Uri(project.CallerUri).LocalPath, callerText, new UTF8Encoding(true));
        var catalogCache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled());
        var identity = new VbaProjectReferenceCatalogIdentity(referenceName,
            "{33333333-3333-3333-3333-333333333333}", 1, 0, 0, @"C:\TypeLibs\Generated.tlb");
        VbaProjectReferenceCatalog Catalog(VbaSourceDefinitionKind kind)
            => new(referenceName, ["Generated"],
                [new VbaProjectReferenceDefinition(referenceName, "GeneratedType", kind)]);
        catalogCache.Store(VbaProjectReferenceCatalogDiscoveryResult.Success(
            identity, Catalog(VbaSourceDefinitionKind.Class)));
        var observer = new BuildObserver();
        var workspace = new VbaLanguageWorkspace(catalogCache,
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            NullVbaDocumentAnalysisBuildObserver.Instance, observer);
        workspace.OpenDocument(project.CallerUri, 1, callerText);
        var original = workspace.CreateProjectSnapshot(project.CallerUri);
        Assert.Equal(VbaSourceDefinitionKind.Class,
            original.SemanticInventory.ResolveSourceDefinition(project.CallerUri, 3, 22)!.Kind);
        _ = original.SemanticInventory.GetSemanticTokenData(project.CallerUri);
        Assert.True(workspace.CloseDocument(project.CallerUri));
        catalogCache.Store(VbaProjectReferenceCatalogDiscoveryResult.Success(
            identity, Catalog(VbaSourceDefinitionKind.Enum)));
        workspace.OpenDocument(project.CallerUri, 1, callerText);

        var reopened = workspace.CreateProjectSnapshot(project.CallerUri);

        Assert.Equal(2, observer.SemanticBuilds);
        Assert.Equal(VbaSourceDefinitionKind.Enum,
            reopened.SemanticInventory.ResolveSourceDefinition(project.CallerUri, 3, 22)!.Kind);
        using var oracle = new VbaLanguageWorkspace(catalogCache);
        oracle.OpenDocument(project.CallerUri, 1, callerText);
        AssertCurrentAnalysisMatchesFresh(project, reopened,
            oracle.CreateProjectSnapshot(project.CallerUri));
    }

    [Fact]
    public void Inactive_reopen_uses_the_current_intrinsic_host_event_catalog()
    {
        using var project = new ProjectFixture();
        var formPath = Path.Combine(project.Root, "src", "Dialog.frm");
        var formUri = new Uri(formPath).AbsoluteUri;
        File.WriteAllText(formPath,
            "VERSION 5.00\nBegin VB.UserForm Dialog\nEnd\nAttribute VB_Name = \"Dialog\"\n"
                + "Private Sub UserForm_Initialize()\nEnd Sub\n", new UTF8Encoding(true));
        VbaIntrinsicHostEventCatalog Catalog(string name)
            => new(VbaIntrinsicHostEventSourceKind.UserForm, "UserForm",
                [new VbaIntrinsicHostEvent(new VbaIntrinsicHostEventIdentity("UserForm", name),
                    new VbaIntrinsicHostEventSignature([], null), true, true)]);
        var observer = new BuildObserver();
        var workspace = CreateWorkspace(observer);
        Assert.True(workspace.TryApplyIntrinsicHostEventCatalog(
            new VbaIntrinsicHostEventCatalogUpdate(1, Catalog("Initialize"))));
        workspace.OpenDocument(project.CallerUri, 1, project.CallerText);
        var original = workspace.CreateProjectSnapshot(project.CallerUri);
        _ = original.SemanticInventory.GetSemanticTokenData(formUri);
        Assert.True(workspace.CloseDocument(project.CallerUri));
        var update = new VbaIntrinsicHostEventCatalogUpdate(2, Catalog("Activate"));
        Assert.True(workspace.TryApplyIntrinsicHostEventCatalog(update));
        workspace.OpenDocument(project.CallerUri, 1, project.CallerText);

        var reopened = workspace.CreateProjectSnapshot(project.CallerUri);

        Assert.Equal(2, observer.SemanticBuilds);
        Assert.Equal("Activate", Assert.Single(
            reopened.SemanticInventory.IntrinsicHostEventCatalog!.Events).Name);
        using var oracle = project.CreateFreshWorkspace();
        Assert.True(oracle.Workspace.TryApplyIntrinsicHostEventCatalog(update));
        var fresh = oracle.Workspace.CreateProjectSnapshot(project.CallerUri);
        AssertCurrentAnalysisMatchesFresh(project, reopened, fresh);
        Assert.Equal(fresh.SemanticInventory.GetSemanticTokenData(formUri),
            reopened.SemanticInventory.GetSemanticTokenData(formUri));
    }

    [Fact]
    public void Inactive_reopen_uses_disk_after_discarding_an_unsaved_source_buffer()
    {
        using var project = new ProjectFixture();
        var observer = new BuildObserver();
        var workspace = CreateWorkspace(observer);
        workspace.OpenDocument(project.CallerUri, 1, project.CallerText);
        workspace.OpenDocument(project.HelperUri, 1,
            project.HelperText.Replace("BuildValue", "FetchValue", StringComparison.Ordinal));
        var dirty = workspace.CreateProjectSnapshot(project.CallerUri);
        Assert.Null(dirty.SemanticInventory.ResolveDefinition(project.CallerUri, 2, 8));
        _ = dirty.SemanticInventory.GetSemanticTokenData(project.CallerUri);
        Assert.True(workspace.CloseDocument(project.HelperUri));
        Assert.True(workspace.CloseDocument(project.CallerUri));
        workspace.OpenDocument(project.CallerUri, 1, project.CallerText);

        var reopened = workspace.CreateProjectSnapshot(project.CallerUri);

        Assert.Equal(2, observer.SemanticBuilds);
        using var oracle = project.CreateFreshWorkspace();
        AssertCurrentAnalysisMatchesFresh(project, reopened,
            oracle.Workspace.CreateProjectSnapshot(project.CallerUri));
        Assert.Equal(["Helper", "BuildValue"],
            reopened.SemanticInventory.GetDocumentDefinitions(project.HelperUri)
                .Select(definition => definition.Name));
        Assert.Equal(project.HelperText, File.ReadAllText(new Uri(project.HelperUri).LocalPath));
    }

    [Fact]
    public void Preview_switching_with_vscode_encoded_drive_uris_reuses_analysis()
    {
        using var project = new ProjectFixture();
        var observer = new BuildObserver();
        var workspace = CreateWorkspace(observer);
        var callerUri = AsVscodeUri(project.CallerUri);
        var helperUri = AsVscodeUri(project.HelperUri);
        Assert.NotEqual(project.CallerUri, callerUri);
        workspace.OpenDocument(callerUri, 1, project.CallerText);
        var first = workspace.CreateProjectSnapshot(callerUri);
        var helperTokens = first.SemanticInventory.GetSemanticTokenData(project.HelperUri).ToArray();
        Assert.True(workspace.CloseDocument(callerUri));
        workspace.OpenDocument(helperUri, 1, project.HelperText);

        var reopened = workspace.CreateProjectSnapshot(helperUri);

        Assert.Equal(1, observer.SemanticBuilds);
        Assert.Equal(helperTokens, reopened.SemanticInventory.GetSemanticTokenData(helperUri));
        var oracleWorkspace = CreateWorkspace(new BuildObserver());
        oracleWorkspace.OpenDocument(helperUri, 1, project.HelperText);
        var oracle = oracleWorkspace.CreateProjectSnapshot(helperUri);
        Assert.Equal(oracle.SemanticInventory.GetSemanticTokenData(helperUri),
            reopened.SemanticInventory.GetSemanticTokenData(helperUri));
        Assert.Equal(oracle.SemanticInventory.GetSemanticTokenData(project.CallerUri),
            reopened.SemanticInventory.GetSemanticTokenData(project.CallerUri));
        Assert.Equal(
            oracle.SemanticInventory.FindReferences(helperUri, 1, 12)
                .Select(reference => (SourceIdentity(reference.Uri), reference.Range))
                .OrderBy(reference => reference.Item1.CanonicalValue, StringComparer.OrdinalIgnoreCase),
            reopened.SemanticInventory.FindReferences(helperUri, 1, 12)
                .Select(reference => (SourceIdentity(reference.Uri), reference.Range))
                .OrderBy(reference => reference.Item1.CanonicalValue, StringComparer.OrdinalIgnoreCase));
    }

    private static string AsVscodeUri(string uri)
    {
        var drive = Path.GetPathRoot(new Uri(uri).LocalPath)![0];
        return uri.Replace($"file:///{drive}:",
            $"file:///{char.ToLowerInvariant(drive)}%3A", StringComparison.OrdinalIgnoreCase);
    }

    private static VbaDocumentIdentity SourceIdentity(string uri)
    {
        Assert.True(VbaProjectIdentityModel.TryIdentifyDocument(uri, out var identity));
        return identity;
    }

    [Theory]
    [InlineData("add")]
    [InlineData("delete")]
    [InlineData("rename")]
    public void Inactive_reopen_rebuilds_changed_source_membership(string mutation)
    {
        using var project = new ProjectFixture();
        var observer = new BuildObserver();
        var workspace = CreateWorkspace(observer);
        workspace.OpenDocument(project.CallerUri, 1, project.CallerText);
        var original = workspace.CreateProjectSnapshot(project.CallerUri);
        _ = original.SemanticInventory.GetSemanticTokenData(project.CallerUri);
        Assert.True(workspace.CloseDocument(project.CallerUri));
        var helperPath = new Uri(project.HelperUri).LocalPath;
        switch (mutation)
        {
            case "add":
                File.WriteAllText(Path.Combine(project.Root, "src", "Other.bas"),
                    project.HelperText.Replace("Helper", "Other", StringComparison.Ordinal),
                    new UTF8Encoding(true));
                break;
            case "delete":
                File.Delete(helperPath);
                break;
            case "rename":
                File.Move(helperPath, Path.Combine(project.Root, "src", "Moved.bas"));
                break;
        }

        workspace.OpenDocument(project.CallerUri, 1, project.CallerText);
        var reopened = workspace.CreateProjectSnapshot(project.CallerUri);

        Assert.Equal(2, observer.SemanticBuilds);
        using var oracle = project.CreateFreshWorkspace();
        AssertCurrentAnalysisMatchesFresh(project, reopened,
            oracle.Workspace.CreateProjectSnapshot(project.CallerUri));
        Assert.Equal(mutation == "add" ? 3 : mutation == "delete" ? 1 : 2,
            reopened.SourceDocuments.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Inactive_reopen_admits_a_recreated_source_after_an_earlier_delete(
        bool keepUnrelatedProjectOpen)
    {
        using var project = new ProjectFixture();
        using var peer = new ProjectFixture();
        var observer = new BuildObserver();
        var workspace = CreateWorkspace(observer);
        VbaProjectSnapshot? peerSnapshot = null;
        if (keepUnrelatedProjectOpen)
        {
            workspace.OpenDocument(peer.CallerUri, 1, peer.CallerText);
            peerSnapshot = workspace.CreateProjectSnapshot(peer.CallerUri);
        }

        workspace.OpenDocument(project.CallerUri, 1, project.CallerText);
        _ = workspace.CreateProjectSnapshot(project.CallerUri);
        var helperPath = new Uri(project.HelperUri).LocalPath;
        File.Delete(helperPath);
        Assert.True(workspace.DeleteSourceDocument(project.HelperUri));
        var withoutHelper = workspace.CreateProjectSnapshot(project.CallerUri);
        Assert.Empty(withoutHelper.SemanticInventory.GetDocumentDefinitions(project.HelperUri));
        Assert.True(workspace.CloseDocument(project.CallerUri));
        var buildsBeforeReopen = observer.SemanticBuilds;
        File.WriteAllText(helperPath, project.HelperText, new UTF8Encoding(true));
        workspace.OpenDocument(project.CallerUri, 1, project.CallerText);

        var reopened = workspace.CreateProjectSnapshot(project.CallerUri);

        using var oracle = project.CreateFreshWorkspace();
        AssertCurrentAnalysisMatchesFresh(project, reopened,
            oracle.Workspace.CreateProjectSnapshot(project.CallerUri));
        Assert.Equal(["Helper", "BuildValue"],
            reopened.SemanticInventory.GetDocumentDefinitions(project.HelperUri)
                .Select(definition => definition.Name));
        Assert.Equal(buildsBeforeReopen + 1, observer.SemanticBuilds);

        var changedCaller = project.CallerText.Replace("Run", "Resume", StringComparison.Ordinal);
        workspace.UpdateDocument(project.CallerUri, changedCaller);
        oracle.Workspace.UpdateDocument(project.CallerUri, changedCaller);
        AssertCurrentAnalysisMatchesFresh(project,
            workspace.CreateProjectSnapshot(project.CallerUri),
            oracle.Workspace.CreateProjectSnapshot(project.CallerUri));
        if (keepUnrelatedProjectOpen)
        {
            Assert.Same(peerSnapshot, workspace.CreateProjectSnapshot(peer.CallerUri));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Inactive_reopen_rejects_changed_disk_bytes_with_unchanged_metadata(
        bool invalidEncoding)
    {
        using var project = new ProjectFixture();
        var observer = new BuildObserver();
        var workspace = CreateWorkspace(observer);
        workspace.OpenDocument(project.CallerUri, 1, project.CallerText);
        var original = workspace.CreateProjectSnapshot(project.CallerUri);
        _ = original.SemanticInventory.GetSemanticTokenData(project.CallerUri);
        Assert.True(workspace.CloseDocument(project.CallerUri));
        var helperPath = new Uri(project.HelperUri).LocalPath;
        var originalMetadata = new FileInfo(helperPath);
        var originalLength = originalMetadata.Length;
        var originalWriteTime = originalMetadata.LastWriteTimeUtc;
        var changedBytes = invalidEncoding
            ? File.ReadAllBytes(helperPath)
            : Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(
                project.HelperText.Replace("BuildValue", "FetchValue", StringComparison.Ordinal)))
                .ToArray();
        if (invalidEncoding)
        {
            changedBytes[^1] = 0xff;
        }

        File.WriteAllBytes(helperPath, changedBytes);
        File.SetLastWriteTimeUtc(helperPath, originalWriteTime);
        Assert.Equal(originalLength, new FileInfo(helperPath).Length);
        Assert.Equal(originalWriteTime, File.GetLastWriteTimeUtc(helperPath));
        workspace.OpenDocument(project.CallerUri, 1, project.CallerText);

        var reopened = workspace.CreateProjectSnapshot(project.CallerUri);

        Assert.Equal(2, observer.SemanticBuilds);
        using var oracle = project.CreateFreshWorkspace();
        var fresh = oracle.Workspace.CreateProjectSnapshot(project.CallerUri);
        AssertCurrentAnalysisMatchesFresh(project, reopened, fresh);
        Assert.Equal(invalidEncoding ? 1 : 0, reopened.DiskSourceFailures.Count);
        Assert.Equal(
            invalidEncoding ? Array.Empty<string>() : ["Helper", "FetchValue"],
            reopened.SemanticInventory.GetDocumentDefinitions(project.HelperUri)
                .Select(definition => definition.Name));
    }

    private static void AssertCurrentAnalysisMatchesFresh(
        ProjectFixture project,
        VbaProjectSnapshot actual,
        VbaProjectSnapshot expected)
    {
        Assert.Equal(expected.SourceDocuments.Keys.Order(StringComparer.Ordinal),
            actual.SourceDocuments.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(expected.SemanticInventory.GetSemanticTokenData(project.CallerUri),
            actual.SemanticInventory.GetSemanticTokenData(project.CallerUri));
        Assert.Equal(expected.SemanticInventory.ResolveDefinition(project.CallerUri, 2, 8),
            actual.SemanticInventory.ResolveDefinition(project.CallerUri, 2, 8));
        Assert.Equal(expected.SemanticInventory.FindReferences(project.CallerUri, 2, 8),
            actual.SemanticInventory.FindReferences(project.CallerUri, 2, 8));
        Assert.Equal(expected.SemanticInventory.GetWorkspaceSymbols("Build").Select(
                definition => (definition.Name, definition.Uri, definition.Range)),
            actual.SemanticInventory.GetWorkspaceSymbols("Build").Select(
                definition => (definition.Name, definition.Uri, definition.Range)));
    }
}
