using System.Text;
using System.Text.Json;
using VbaLanguageServer.Lsp;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed partial class VbaRetainedProjectAnalysisTests
{
    [Fact]
    public void Reopening_discards_inactive_watcher_text_before_a_project_scope_is_materialized()
    {
        using var project = new ProjectFixture();
        using var workspace = CreateWorkspace(new BuildObserver());
        workspace.OpenDocument(project.CallerUri, 1, project.CallerText);
        _ = workspace.CreateProjectSnapshot(project.CallerUri);
        Assert.True(workspace.CloseDocument(project.CallerUri));
        Assert.True(workspace.ReloadSourceDocumentFromDisk(project.HelperUri));
        Assert.Equal(0, workspace.RetainedProjectSnapshotCount);
        var helperPath = new Uri(project.HelperUri).LocalPath;
        var previousLength = new FileInfo(helperPath).Length;
        var previousWriteTime = File.GetLastWriteTimeUtc(helperPath);
        File.WriteAllText(helperPath,
            project.HelperText.Replace("BuildValue", "FetchValue", StringComparison.Ordinal),
            new UTF8Encoding(true));
        File.SetLastWriteTimeUtc(helperPath, previousWriteTime);
        Assert.Equal(previousLength, new FileInfo(helperPath).Length);

        workspace.OpenDocument(project.CallerUri, 1, project.CallerText);
        var reopened = workspace.CreateProjectSnapshot(project.CallerUri);

        Assert.Null(reopened.SemanticInventory.ResolveDefinition(project.CallerUri, 2, 8));
        using var oracle = project.CreateFreshWorkspace();
        AssertCurrentAnalysisMatchesFresh(project, reopened,
            oracle.Workspace.CreateProjectSnapshot(project.CallerUri));
    }

    [Fact]
    public async Task Reopening_after_an_inactive_watcher_notification_validates_current_disk_content()
    {
        using var project = new ProjectFixture();
        using var workspace = CreateWorkspace(new BuildObserver());
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var lifecycle = new VbaDocumentLifecycle(
            new LspMessageTransport(Stream.Null, Stream.Null),
            workspace,
            new RetainedWatcherCatalogLifecycle());
        lifecycle.AttachScheduler(scheduler);
        var opened = JsonSerializer.SerializeToNode(new
        {
            textDocument = new
            {
                uri = project.CallerUri, languageId = "vba", version = 1,
                text = project.CallerText
            }
        });
        try
        {
            await ApplyNotification("textDocument/didOpen", token =>
                lifecycle.RecordOpenedDocumentAsync(opened, token));
            await WaitForMaterializedSnapshot();
            await ApplyNotification("textDocument/didClose", token =>
                lifecycle.RecordClosedDocumentAsync(JsonSerializer.SerializeToNode(new
                {
                    textDocument = new { uri = project.CallerUri }
                }), token));
            Assert.Empty(workspace.GetOpenDocumentUris());
            Assert.Equal(0, workspace.RetainedProjectSnapshotCount);
            await ApplyNotification("workspace/didChangeWatchedFiles", token =>
                lifecycle.RecordWatchedFilesChangedAsync(JsonSerializer.SerializeToNode(new
                {
                    changes = new[] { new { uri = project.HelperUri, type = 2 } }
                }), token));
            await WaitForMaterializedSnapshot();
            Assert.Empty(workspace.GetOpenDocumentUris());
            var helperPath = new Uri(project.HelperUri).LocalPath;
            var previousLength = new FileInfo(helperPath).Length;
            var previousWriteTime = File.GetLastWriteTimeUtc(helperPath);
            File.WriteAllText(helperPath,
                project.HelperText.Replace("BuildValue", "FetchValue", StringComparison.Ordinal),
                new UTF8Encoding(true));
            File.SetLastWriteTimeUtc(helperPath, previousWriteTime);
            Assert.Equal(previousLength, new FileInfo(helperPath).Length);
            Assert.Equal(previousWriteTime, File.GetLastWriteTimeUtc(helperPath));

            await ApplyNotification("textDocument/didOpen", token =>
                lifecycle.RecordOpenedDocumentAsync(opened, token));
            var reopened = workspace.CreateProjectSnapshot(project.CallerUri);

            Assert.Null(reopened.SemanticInventory.ResolveDefinition(project.CallerUri, 2, 8));
            using var oracle = project.CreateFreshWorkspace();
            AssertCurrentAnalysisMatchesFresh(project, reopened,
                oracle.Workspace.CreateProjectSnapshot(project.CallerUri));
        }
        finally
        {
            lifecycle.Stop();
            await scheduler.StopAsync(VbaInteractiveStopReason.Abort);
        }

        async Task ApplyNotification(string method, Func<CancellationToken, Task> apply)
        {
            var admission = await scheduler.AdmitRequiredMutationAsync(method, apply, CancellationToken.None);
            await admission.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }

        async Task WaitForMaterializedSnapshot()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (workspace.RetainedProjectSnapshotCount == 0)
            {
                await Task.Delay(10, timeout.Token);
            }
        }
    }

    [Fact]
    public void Last_open_close_retires_watched_siblings_before_metadata_equal_inactive_changes()
    {
        using var project = new ProjectFixture();
        using var workspace = CreateWorkspace(new BuildObserver());
        workspace.OpenDocument(project.CallerUri, 1, project.CallerText);
        Assert.NotNull(workspace.CreateProjectSnapshot(project.CallerUri)
            .SemanticInventory.ResolveDefinition(project.CallerUri, 2, 8));
        Assert.True(workspace.ReloadSourceDocumentFromDisk(project.HelperUri));
        _ = workspace.CreateProjectSnapshot(project.CallerUri);
        Assert.True(workspace.CloseDocument(project.CallerUri));
        var snapshotsAfterClose = workspace.RetainedProjectSnapshotCount;
        int reconciliationScopesAfterClose;
        using (var reconciliation = workspace.CaptureProjectReconciliation())
        {
            reconciliationScopesAfterClose = reconciliation.Scopes.Count;
        }
        var helperPath = new Uri(project.HelperUri).LocalPath;
        var previousLength = new FileInfo(helperPath).Length;
        var previousWriteTime = File.GetLastWriteTimeUtc(helperPath);
        File.WriteAllText(helperPath,
            project.HelperText.Replace("BuildValue", "FetchValue", StringComparison.Ordinal),
            new UTF8Encoding(true));
        File.SetLastWriteTimeUtc(helperPath, previousWriteTime);
        Assert.Equal(previousLength, new FileInfo(helperPath).Length);
        Assert.Equal(previousWriteTime, File.GetLastWriteTimeUtc(helperPath));
        workspace.OpenDocument(project.CallerUri, 1, project.CallerText);

        var reopened = workspace.CreateProjectSnapshot(project.CallerUri);

        Assert.Null(reopened.SemanticInventory.ResolveDefinition(project.CallerUri, 2, 8));
        Assert.Equal(0, snapshotsAfterClose);
        Assert.Equal(0, reconciliationScopesAfterClose);
        using var oracle = project.CreateFreshWorkspace();
        AssertCurrentAnalysisMatchesFresh(project, reopened,
            oracle.Workspace.CreateProjectSnapshot(project.CallerUri));
    }

    private sealed class RetainedWatcherCatalogLifecycle : IReferenceCatalogLifecycle
    {
        public void ActivateProject(string uri) { }
        public void ApplyManifestSelectionChange(string uri, string text) { }
        public void DeactivateManifest(string uri) { }
    }
}
