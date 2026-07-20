using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Syntax;
using VbaLanguageServer.Workspace;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaVersionedDocumentSnapshotTests
{
    [Fact]
    public async Task Document_analysis_build_does_not_hold_the_workspace_state_lock()
    {
        const string uri = "file:///C:/work/Snapshot.bas";
        const string versionOneText = "Public Sub VersionOne()\nEnd Sub\n";
        const string versionTwoText = "Public Sub VersionTwo()\nEnd Sub\n";
        var observer = new BlockingDocumentAnalysisBuildObserver();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            observer);
        workspace.OpenDocument(uri, version: 1, versionOneText);
        var versionOne = Assert.IsType<VbaVersionedDocumentSnapshot>(
            workspace.GetDocumentSnapshot(uri, expectedVersion: 1));
        observer.BlockVersion(2);

        var changeTask = Task.Run(() =>
            workspace.ChangeDocument(uri, version: 2, versionTwoText));
        await observer.WaitUntilBlockedAsync();

        try
        {
            var currentAnalysis = await Task.Run(() => workspace.GetDocumentAnalysis(uri))
                .WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Same(versionOne.Analysis, currentAnalysis);
        }
        finally
        {
            observer.Release();
        }

        Assert.True(await changeTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(
            versionTwoText,
            workspace.GetDocumentSnapshot(uri, expectedVersion: 2)?.Text);
    }

    [Fact]
    public async Task Older_analysis_build_cannot_overwrite_a_newer_accepted_revision()
    {
        const string uri = "file:///C:/work/Snapshot.bas";
        const string versionOneText = "Public Sub VersionOne()\nEnd Sub\n";
        const string versionTwoText = "Public Sub VersionTwo()\nEnd Sub\n";
        const string versionThreeText = "Public Sub VersionThree()\nEnd Sub\n";
        var observer = new BlockingDocumentAnalysisBuildObserver();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            observer);
        workspace.OpenDocument(uri, version: 1, versionOneText);
        observer.BlockVersion(2);
        var versionTwoTask = Task.Run(() =>
            workspace.ChangeDocument(uri, version: 2, versionTwoText));
        await observer.WaitUntilBlockedAsync();

        try
        {
            Assert.True(workspace.ChangeDocument(uri, version: 3, versionThreeText));
            Assert.Equal(
                versionThreeText,
                workspace.GetDocumentSnapshot(uri, expectedVersion: 3)?.Text);
        }
        finally
        {
            observer.Release();
        }

        Assert.True(await versionTwoTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(workspace.GetDocumentSnapshot(uri, expectedVersion: 2));
        Assert.Equal(
            versionThreeText,
            workspace.GetDocumentSnapshot(uri, expectedVersion: 3)?.Text);
    }

    [Fact]
    public async Task Superseded_disk_analysis_does_not_report_that_it_became_authoritative()
    {
        const string uri = "file:///C:/work/Snapshot.bas";
        const string initialDiskText = "Public Sub InitialDisk()\nEnd Sub\n";
        const string supersededDiskText = "Public Sub SupersededDisk()\nEnd Sub\n";
        const string openText = "Public Sub OpenBuffer()\nEnd Sub\n";
        var observer = new BlockingDocumentAnalysisBuildObserver();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            observer);
        Assert.True(workspace.ReloadSourceDocument(uri, initialDiskText));
        observer.BlockUnversioned();

        var reloadTask = Task.Run(() =>
            workspace.ReloadSourceDocument(uri, supersededDiskText));
        await observer.WaitUntilBlockedAsync();

        try
        {
            workspace.OpenDocument(uri, version: 1, openText);
        }
        finally
        {
            observer.Release();
        }

        Assert.False(await reloadTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(
            openText,
            workspace.GetDocumentSnapshot(uri, expectedVersion: 1)?.Text);
    }

    [Fact]
    public async Task Pending_revision_hides_both_superseded_and_not_yet_committed_snapshots()
    {
        const string uri = "file:///C:/work/Snapshot.bas";
        const string versionOneText = "Public Sub VersionOne()\nEnd Sub\n";
        const string versionTwoText = "Public Sub VersionTwo()\nEnd Sub\n";
        var observer = new BlockingDocumentAnalysisBuildObserver();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            observer);
        workspace.OpenDocument(uri, version: 1, versionOneText);
        observer.BlockVersion(2);

        var changeTask = Task.Run(() =>
            workspace.ChangeDocument(uri, version: 2, versionTwoText));
        await observer.WaitUntilBlockedAsync();

        try
        {
            Assert.Null(workspace.GetDocumentSnapshot(uri, expectedVersion: 1));
            Assert.Null(workspace.GetDocumentSnapshot(uri, expectedVersion: 2));
            Assert.False(workspace.ChangeDocument(uri, version: 1, versionOneText));
            Assert.False(workspace.ChangeDocument(uri, version: 2, versionTwoText));
        }
        finally
        {
            observer.Release();
        }

        Assert.True(await changeTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(
            versionTwoText,
            workspace.GetDocumentSnapshot(uri, expectedVersion: 2)?.Text);
    }

    [Fact]
    public async Task Close_and_reopen_prevent_an_old_lifecycle_from_resurrecting()
    {
        const string uri = "file:///C:/work/Snapshot.bas";
        const string originalText = "Public Sub Original()\nEnd Sub\n";
        const string closingLifecycleText = "Public Sub ClosingLifecycle()\nEnd Sub\n";
        const string reopenedText = "Public Sub Reopened()\nEnd Sub\n";
        var observer = new BlockingDocumentAnalysisBuildObserver();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            observer);
        workspace.OpenDocument(uri, version: 1, originalText);
        observer.BlockVersion(2);
        var closingLifecycleTask = Task.Run(() =>
            workspace.ChangeDocument(uri, version: 2, closingLifecycleText));
        await observer.WaitUntilBlockedAsync();

        try
        {
            Assert.True(workspace.CloseDocument(uri));
            workspace.OpenDocument(uri, version: 2, reopenedText);
        }
        finally
        {
            observer.Release();
        }

        Assert.True(
            await closingLifecycleTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(
            reopenedText,
            workspace.GetDocumentSnapshot(uri, expectedVersion: 2)?.Text);
    }

    [Fact]
    public async Task Superseded_update_returns_after_a_later_revision_commits_while_a_newer_build_is_pending()
    {
        const string uri = "file:///C:/work/Snapshot.bas";
        const string initialText = "Public Sub Initial()\nEnd Sub\n";
        const string supersededText = "Public Sub Superseded()\nEnd Sub\n";
        const string committedText = "Public Sub Committed()\nEnd Sub\n";
        const string pendingText = "Public Sub Pending()\nEnd Sub\n";
        var observer = new SequencedDocumentAnalysisBuildObserver(
            firstBlockedVersion: 1,
            secondBlockedVersion: 3);
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            observer);
        workspace.UpdateDocument(uri, initialText);

        var supersededTask = Task.Run(() =>
            workspace.UpdateDocument(uri, supersededText));
        await observer.WaitUntilFirstBlockedAsync();
        workspace.UpdateDocument(uri, committedText);

        var pendingTask = Task.Run(() =>
            workspace.UpdateDocument(uri, pendingText));
        await observer.WaitUntilSecondBlockedAsync();
        observer.ReleaseFirst();

        try
        {
            await supersededTask.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            observer.ReleaseSecond();
        }

        await pendingTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(
            pendingText,
            workspace.GetDocumentSnapshot(uri, expectedVersion: 3)?.Text);
    }

    [Fact]
    public void Exact_snapshot_pin_remains_coherent_after_the_workspace_advances()
    {
        const string uri = "file:///C:/work/Snapshot.bas";
        const string versionOneText =
            "Attribute VB_Name = \"Snapshot\"\nPublic Sub VersionOne()\nEnd Sub\n";
        const string versionTwoText =
            "Attribute VB_Name = \"Snapshot\"\nPublic Sub VersionTwo()\nEnd Sub\n";
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
        workspace.OpenDocument(uri, version: 1, versionOneText);
        var pinned = Assert.IsType<VbaVersionedDocumentSnapshot>(
            workspace.GetDocumentSnapshot(uri, expectedVersion: 1));

        workspace.ChangeDocument(uri, version: 2, versionTwoText);

        Assert.Null(workspace.GetDocumentSnapshot(uri, expectedVersion: 1));
        Assert.Equal(versionOneText, pinned.Text);
        Assert.Equal(versionOneText, pinned.SourceText.Text);
        Assert.Equal(versionOneText, pinned.SyntaxTree.Text);
        Assert.Equal(versionOneText, pinned.SourceDocument.Text);
        Assert.Same(pinned.SyntaxTree, pinned.SourceDocument.SyntaxTree);
        Assert.Contains(
            pinned.SourceDocument.Definitions,
            definition => definition.Name == "VersionOne");
        Assert.DoesNotContain(
            pinned.SourceDocument.Definitions,
            definition => definition.Name == "VersionTwo");
    }

    [Fact]
    public void Unexpected_analysis_failure_does_not_expose_stale_artifacts_and_can_recover()
    {
        const string uri = "file:///C:/work/Snapshot.bas";
        const string versionOneText = "Public Sub VersionOne()\nEnd Sub\n";
        const string failedText = "Public Sub FailedVersion()\nEnd Sub\n";
        const string recoveredText = "Public Sub RecoveredVersion()\nEnd Sub\n";
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            new FailingDocumentAnalysisBuildObserver(versionToFail: 2));
        workspace.OpenDocument(uri, version: 1, versionOneText);

        Assert.Throws<InvalidOperationException>(() =>
            workspace.ChangeDocument(uri, version: 2, failedText));
        Assert.Null(workspace.GetDocumentSnapshot(uri, expectedVersion: 1));
        Assert.Null(workspace.GetDocumentSnapshot(uri, expectedVersion: 2));

        Assert.True(
            workspace.ChangeDocument(uri, version: 3, recoveredText));
        var recovered = Assert.IsType<VbaVersionedDocumentSnapshot>(
            workspace.GetDocumentSnapshot(uri, expectedVersion: 3));
        Assert.Equal(recoveredText, recovered.Text);
        Assert.Contains(
            recovered.SourceDocument.Definitions,
            definition => definition.Name == "RecoveredVersion");
    }

    [Fact]
    public void Malformed_revision_is_coherent_and_a_later_revision_recovers()
    {
        const string uri = "file:///C:/work/Snapshot.bas";
        const string malformedText =
            "Attribute VB_Name = \"Snapshot\"\nPublic Sub Broken()\n    If True Then\n";
        const string recoveredText =
            "Attribute VB_Name = \"Snapshot\"\nPublic Sub Recovered()\nEnd Sub\n";
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));

        workspace.OpenDocument(uri, version: 1, malformedText);
        var malformed = Assert.IsType<VbaVersionedDocumentSnapshot>(
            workspace.GetDocumentSnapshot(uri, expectedVersion: 1));
        Assert.Equal(malformedText, malformed.SyntaxTree.Text);
        Assert.Same(malformed.SyntaxTree, malformed.SourceDocument.SyntaxTree);
        Assert.NotEmpty(malformed.Diagnostics.SyntaxDiagnostics);

        Assert.True(
            workspace.ChangeDocument(uri, version: 2, recoveredText));
        var recovered = Assert.IsType<VbaVersionedDocumentSnapshot>(
            workspace.GetDocumentSnapshot(uri, expectedVersion: 2));
        Assert.Equal(recoveredText, recovered.SyntaxTree.Text);
        Assert.Contains(
            recovered.SourceDocument.Definitions,
            definition => definition.Name == "Recovered");
        Assert.DoesNotContain(
            recovered.SourceDocument.Definitions,
            definition => definition.Name == "Broken");
    }

    [Fact]
    public void Ordinary_member_edit_reuses_definitions_outside_the_changed_member()
    {
        const string uri = "file:///C:/work/Snapshot.bas";
        const string versionOneText =
            """
            Attribute VB_Name = "Snapshot"
            Public Sub Before()
                Dim beforeValue As Long
            End Sub
            Public Sub Edited()
                Dim editedValue As Long
                editedValue = 1
            End Sub
            Public Sub After()
                Dim afterValue As Long
            End Sub

            """;
        const string versionTwoText =
            """
            Attribute VB_Name = "Snapshot"
            Public Sub Before()
                Dim beforeValue As Long
            End Sub
            Public Sub Edited()
                Dim editedValue As Long
                editedValue = 2
            End Sub
            Public Sub After()
                Dim afterValue As Long
            End Sub

            """;
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
        workspace.OpenDocument(uri, version: 1, versionOneText);
        var previous = Assert.IsType<VbaVersionedDocumentSnapshot>(
            workspace.GetDocumentSnapshot(uri, expectedVersion: 1));

        var accepted = workspace.ChangeDocument(uri, version: 2, versionTwoText);
        var current = Assert.IsType<VbaVersionedDocumentSnapshot>(
            workspace.GetDocumentSnapshot(uri, expectedVersion: 2));
        var clean = VbaSourceDocumentProjector.Project(
            uri,
            VbaSyntaxTree.ParseModule(uri, versionTwoText));

        Assert.True(accepted);
        Assert.Same(
            FindDefinition(previous.SourceDocument, "Before"),
            FindDefinition(current.SourceDocument, "Before"));
        Assert.Same(
            FindDefinition(previous.SourceDocument, "beforeValue"),
            FindDefinition(current.SourceDocument, "beforeValue"));
        Assert.NotSame(
            FindDefinition(previous.SourceDocument, "Edited"),
            FindDefinition(current.SourceDocument, "Edited"));
        Assert.NotSame(
            FindDefinition(previous.SourceDocument, "editedValue"),
            FindDefinition(current.SourceDocument, "editedValue"));
        Assert.Same(
            FindDefinition(previous.SourceDocument, "After"),
            FindDefinition(current.SourceDocument, "After"));
        Assert.Same(
            FindDefinition(previous.SourceDocument, "afterValue"),
            FindDefinition(current.SourceDocument, "afterValue"));
        Assert.Equal(clean.Definitions, current.SourceDocument.Definitions);
        Assert.Equal(versionTwoText, current.SourceDocument.Text);
        Assert.Same(current.SyntaxTree, current.SourceDocument.SyntaxTree);
    }

    [Fact]
    public void Exact_version_reads_reuse_the_analysis_committed_for_the_open_revision()
    {
        const string uri = "file:///C:/work/Snapshot.bas";
        const string text = "Attribute VB_Name = \"Snapshot\"\nPublic Sub Run()\nEnd Sub\n";
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
        workspace.OpenDocument(uri, version: 9, text);

        var first = workspace.GetDocumentSnapshot(uri, expectedVersion: 9);
        var second = workspace.GetDocumentSnapshot(uri, expectedVersion: 9);
        var currentAnalysis = workspace.GetDocumentAnalysis(uri);

        Assert.NotNull(first);
        Assert.NotNull(currentAnalysis);
        Assert.Same(first, second);
        Assert.Same(first.Analysis, currentAnalysis);
        Assert.Same(first.SourceDocument, currentAnalysis.SourceDocument);
        Assert.Same(first.Diagnostics, currentAnalysis.Diagnostics);
    }

    [Fact]
    public void Exact_snapshot_constructor_uses_the_supplied_source_document_without_reprojection()
    {
        const string uri = "file:///C:/work/Snapshot.bas";
        const string text = "Attribute VB_Name = \"Snapshot\"\nPublic Sub Run()\nEnd Sub\n";
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
        workspace.OpenDocument(uri, version: 9, text);
        var analysis = Assert.IsType<VbaDocumentAnalysis>(
            workspace.GetDocumentAnalysis(uri));

        var snapshot = new VbaVersionedDocumentSnapshot(
            analysis.Uri,
            9,
            analysis.Text,
            analysis.SyntaxTree,
            analysis.ModuleKind,
            analysis.Diagnostics,
            analysis.SourceDocument);

        Assert.Same(analysis.SourceDocument, snapshot.SourceDocument);
    }

    [Fact]
    public void Exact_version_snapshot_rejects_analysis_ownership_after_uri_mutation()
    {
        const string uri = "file:///C:/work/Snapshot.bas";
        const string text = "Attribute VB_Name = \"Snapshot\"\nPublic Sub Run()\nEnd Sub\n";
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
        workspace.OpenDocument(uri, version: 9, text);
        var snapshot = Assert.IsType<VbaVersionedDocumentSnapshot>(
            workspace.GetDocumentSnapshot(uri, expectedVersion: 9));

        var mutated = snapshot with
        {
            Uri = "file:///C:/work/Different.bas"
        };

        Assert.False(mutated.IsOwnedByAnalysis);
    }

    [Fact]
    public void Exact_version_snapshot_rejects_analysis_ownership_after_version_mutation()
    {
        const string uri = "file:///C:/work/Snapshot.bas";
        const string text = "Attribute VB_Name = \"Snapshot\"\nPublic Sub Run()\nEnd Sub\n";
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
        workspace.OpenDocument(uri, version: 9, text);
        var snapshot = Assert.IsType<VbaVersionedDocumentSnapshot>(
            workspace.GetDocumentSnapshot(uri, expectedVersion: 9));

        var mutated = snapshot with
        {
            Version = snapshot.Version + 1
        };

        Assert.False(mutated.IsOwnedByAnalysis);
    }

    [Fact]
    public void Exact_version_snapshot_keeps_text_tree_module_kind_and_diagnostics_together()
    {
        const string uri = "file:///C:/work/Snapshot.bas";
        const string text = "Attribute VB_Name = \"Snapshot\"\nPublic Sub Run()\n    ";
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
        workspace.OpenDocument(uri, version: 9, text);

        var snapshot = workspace.GetDocumentSnapshot(uri, expectedVersion: 9);

        Assert.NotNull(snapshot);
        Assert.Equal(uri, snapshot.Uri);
        Assert.Equal(9, snapshot.Version);
        Assert.Equal(text, snapshot.Text);
        Assert.Equal(text, snapshot.SourceText.Text);
        Assert.Equal(text, snapshot.SyntaxTree.Text);
        Assert.Equal(VbaModuleKind.StandardModule, snapshot.ModuleKind);
        Assert.Equal(text, snapshot.SourceDocument.Text);
        Assert.Same(snapshot.SyntaxTree, snapshot.SourceDocument.SyntaxTree);
        Assert.Contains(
            snapshot.SourceDocument.Definitions,
            definition => definition.Name == "Run");
        Assert.Contains(
            snapshot.Diagnostics.SyntaxDiagnostics,
            diagnostic => diagnostic.Code == "syntax.missingBlockTerminator"
                && diagnostic.Message.Contains("End Sub", StringComparison.Ordinal));
        Assert.Null(workspace.GetDocumentSnapshot(uri, expectedVersion: 8));
    }

    private static VbaSourceDefinition FindDefinition(
        VbaSourceDocument document,
        string name)
        => Assert.Single(
            document.Definitions,
            definition => definition.Name.Equals(
                name,
                StringComparison.OrdinalIgnoreCase));

    private sealed class BlockingDocumentAnalysisBuildObserver
        : IVbaDocumentAnalysisBuildObserver
    {
        private readonly TaskCompletionSource blocked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim release = new(initialState: false);
        private int blockedVersion = -1;
        private bool blockUnversioned;
        private int blockClaimed;

        public void BlockVersion(int version)
        {
            blockedVersion = version;
        }

        public void BlockUnversioned()
        {
            blockUnversioned = true;
        }

        public void BeforeBuild(
            VbaDocumentAnalysisBuildContext context,
            CancellationToken cancellationToken)
        {
            if (context.ClientVersion != blockedVersion
                && !(blockUnversioned && context.ClientVersion is null))
            {
                return;
            }

            if (Interlocked.CompareExchange(ref blockClaimed, 1, 0) != 0)
            {
                return;
            }

            blocked.TrySetResult();
            release.Wait(cancellationToken);
        }

        public Task WaitUntilBlockedAsync()
            => blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void Release()
            => release.Set();
    }

    private sealed class FailingDocumentAnalysisBuildObserver
        : IVbaDocumentAnalysisBuildObserver
    {
        private readonly int versionToFail;
        private int failurePending = 1;

        public FailingDocumentAnalysisBuildObserver(int versionToFail)
        {
            this.versionToFail = versionToFail;
        }

        public void BeforeBuild(
            VbaDocumentAnalysisBuildContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.ClientVersion == versionToFail
                && Interlocked.Exchange(ref failurePending, 0) == 1)
            {
                throw new InvalidOperationException("Injected analysis failure.");
            }
        }
    }

    private sealed class SequencedDocumentAnalysisBuildObserver
        : IVbaDocumentAnalysisBuildObserver
    {
        private readonly int firstBlockedVersion;
        private readonly int secondBlockedVersion;
        private readonly TaskCompletionSource firstBlocked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource secondBlocked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim firstRelease = new(initialState: false);
        private readonly ManualResetEventSlim secondRelease = new(initialState: false);

        public SequencedDocumentAnalysisBuildObserver(
            int firstBlockedVersion,
            int secondBlockedVersion)
        {
            this.firstBlockedVersion = firstBlockedVersion;
            this.secondBlockedVersion = secondBlockedVersion;
        }

        public void BeforeBuild(
            VbaDocumentAnalysisBuildContext context,
            CancellationToken cancellationToken)
        {
            if (context.ClientVersion == firstBlockedVersion)
            {
                firstBlocked.TrySetResult();
                firstRelease.Wait(cancellationToken);
                return;
            }

            if (context.ClientVersion == secondBlockedVersion)
            {
                secondBlocked.TrySetResult();
                secondRelease.Wait(cancellationToken);
            }
        }

        public Task WaitUntilFirstBlockedAsync()
            => firstBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public Task WaitUntilSecondBlockedAsync()
            => secondBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void ReleaseFirst()
            => firstRelease.Set();

        public void ReleaseSecond()
            => secondRelease.Set();
    }
}
