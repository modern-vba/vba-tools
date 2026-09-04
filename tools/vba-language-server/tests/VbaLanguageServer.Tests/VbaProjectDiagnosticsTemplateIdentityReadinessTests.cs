using VbaLanguageServer.Lsp;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Workspace;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaProjectDiagnosticsTemplateIdentityReadinessTests
{
    [Fact]
    public async Task Manifest_template_identity_read_does_not_block_open_or_interactive_capture()
    {
        using var project = CreateManifestProject(
            "vba-ls-template-readiness-",
            "TemplateReadiness");
        var fileSystem = new BlockingSourceTemplateFileSystem(
            SystemVbaProjectFileSystem.Instance,
            project.TemplatePath);
        var workspace = CreateWorkspace(fileSystem);
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, Stream.Null),
            workspace);
        publisher.AttachScheduler(scheduler);
        var pipeline = new VbaDocumentChangePipeline(
            workspace,
            new NullReferenceCatalogLifecycle(),
            publisher);

        var opened = scheduler.AdmitMutation(
            "textDocument/didOpen",
            cancellationToken => pipeline.ApplyAsync(
                new VbaTextDocumentOpenedChange(
                    project.Uri,
                    1,
                    project.SourceText),
                cancellationToken));

        try
        {
            await fileSystem.TemplateReadStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            await opened.Completion.WaitAsync(TimeSpan.FromSeconds(1));

            IReadOnlyList<int>? tokenData = null;
            var semanticCapture = scheduler.AdmitRequest(
                requestId: null,
                "textDocument/semanticTokens/full",
                cancellationToken =>
                    ((IVbaInteractiveWorkspaceCapture)workspace)
                        .CaptureProjectSemanticInventory(
                            project.Uri,
                            cancellationToken),
                (inventory, cancellationToken) =>
                {
                    tokenData = inventory.GetSemanticTokenData(
                        project.Uri,
                        cancellationToken);
                    return Task.CompletedTask;
                });

            await semanticCapture.Completion.WaitAsync(
                TimeSpan.FromSeconds(1));
            Assert.NotNull(tokenData);
            Assert.NotEmpty(tokenData);
        }
        finally
        {
            fileSystem.ReleaseTemplateRead();
        }

        await publisher.WaitForIdleAsync(project.Uri)
            .WaitAsync(TimeSpan.FromSeconds(5));
        publisher.Stop();
    }

    [Fact]
    public async Task Background_template_identity_read_observes_cancellation()
    {
        using var project = CreateManifestProject(
            "vba-ls-template-cancellation-",
            "TemplateCancellation");
        var fileSystem = new BlockingSourceTemplateFileSystem(
            SystemVbaProjectFileSystem.Instance,
            project.TemplatePath);
        var workspace = CreateWorkspace(fileSystem);
        workspace.OpenDocument(project.Uri, 1, project.SourceText);
        var capture = Assert.IsType<VbaProjectDiagnosticsCapture>(
            workspace.CaptureProjectDiagnostics(project.Uri));
        using var cancellation = new CancellationTokenSource();
        var validation = Task.Run(() =>
            workspace.BuildProjectDiagnosticsSnapshots(
                capture,
                cancellation.Token));

        try
        {
            await fileSystem.TemplateReadStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await validation.WaitAsync(TimeSpan.FromSeconds(1));
            });
        }
        finally
        {
            fileSystem.ReleaseTemplateRead();
        }
    }

    [Fact]
    public async Task Scheduler_abort_cancels_the_final_template_freshness_read()
    {
        using var project = CreateManifestProject(
            "vba-ls-template-final-fence-cancellation-",
            "TemplateFinalFenceCancellation");
        var fileSystem = new StagedSourceTemplateFileSystem(
            SystemVbaProjectFileSystem.Instance,
            project.TemplatePath);
        var workspace = CreateWorkspace(fileSystem);
        workspace.OpenDocument(project.Uri, 1, project.SourceText);
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, Stream.Null),
            workspace);
        publisher.AttachScheduler(scheduler);
        Task? publisherStop = null;
        Task? schedulerStop = null;
        var stoppedBeforeRelease = false;
        try
        {
            await publisher.PublishProjectDiagnosticsAsync(
                project.Uri,
                CancellationToken.None);
            await fileSystem.FinalFenceReadStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5));

            publisherStop = Task.Run(publisher.Stop);
            schedulerStop = scheduler.StopAsync(VbaInteractiveStopReason.Abort);
            await Task.WhenAll(publisherStop, schedulerStop)
                .WaitAsync(TimeSpan.FromSeconds(1));
            stoppedBeforeRelease = true;
        }
        catch (TimeoutException)
        {
        }
        finally
        {
            fileSystem.ReleaseFinalFenceRead();
            publisherStop ??= Task.Run(publisher.Stop);
            schedulerStop ??= scheduler.StopAsync(
                VbaInteractiveStopReason.Abort);
            await Task.WhenAll(publisherStop, schedulerStop)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(stoppedBeforeRelease);
        Assert.Equal(2, fileSystem.TemplateReadCount);
        Assert.Equal(2, fileSystem.CancellableTemplateReadCount);
        Assert.Equal(0, fileSystem.NonCancellableTemplateReadCount);
    }

    [Fact]
    public async Task Template_freshness_is_read_once_per_partition_without_a_batch_duplicate()
    {
        using var project = CreateManifestProject(
            "vba-ls-template-final-fence-count-",
            "TemplateFinalFenceCount");
        var secondPath = Path.Combine(
            Path.GetDirectoryName(project.TemplatePath)!,
            "Second.bas");
        const string secondText = "Attribute VB_Name = \"Second\"\n"
            + "Public Sub RunSecond()\n"
            + "End Sub\n";
        File.WriteAllText(secondPath, secondText);
        var secondUri = new Uri(secondPath).AbsoluteUri;
        var fileSystem = new CountingSourceTemplateFileSystem(
            SystemVbaProjectFileSystem.Instance,
            project.TemplatePath);
        var workspace = CreateWorkspace(fileSystem);
        workspace.OpenDocument(project.Uri, 1, project.SourceText);
        workspace.OpenDocument(secondUri, 1, secondText);
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, Stream.Null),
            workspace);
        publisher.AttachScheduler(scheduler);

        await publisher.PublishProjectDiagnosticsAsync(
            project.Uri,
            CancellationToken.None);
        await Task.WhenAll(
                publisher.WaitForIdleAsync(project.Uri),
                publisher.WaitForIdleAsync(secondUri))
            .WaitAsync(TimeSpan.FromSeconds(5));
        publisher.Stop();

        Assert.Equal(3, fileSystem.TemplateReadCount);
        Assert.Equal(3, fileSystem.CancellableTemplateReadCount);
        Assert.Equal(0, fileSystem.NonCancellableTemplateReadCount);
    }

    private static VbaLanguageWorkspace CreateWorkspace(
        IVbaProjectFileSystem fileSystem)
        => new(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            NullVbaDocumentAnalysisBuildObserver.Instance,
            NullVbaProjectSnapshotBuildObserver.Instance,
            fileSystem);

    private static ManifestProject CreateManifestProject(
        string temporaryDirectoryPrefix,
        string projectName)
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            temporaryDirectoryPrefix).FullName;
        var sourceRoot = Directory.CreateDirectory(Path.Combine(
            projectRoot,
            "src",
            "Book1")).FullName;
        var templatePath = Path.Combine(sourceRoot, "Book1.xlsm");
        File.WriteAllBytes(
            templatePath,
            VbaProjectIdentityWorkbookFixture.Create(projectName, 1252));
        File.WriteAllText(
            Path.Combine(projectRoot, "vba-project.json"),
            $$"""
            {
              "schemaVersion": 1,
              "projectName": "{{projectName}}",
              "primaryDocument": "Book1",
              "documents": {
                "Book1": {
                  "kind": "excel",
                  "sourcePath": "src/Book1",
                  "templatePath": "src/Book1/Book1.xlsm",
                  "binPath": "bin/Book1/Book1.xlsm",
                  "publishPath": "publish/Book1/Book1.xlsm",
                  "commonModules": [],
                  "references": []
                }
              }
            }
            """);
        var sourcePath = Path.Combine(sourceRoot, "Main.bas");
        const string sourceText = "Attribute VB_Name = \"Main\"\n"
            + "Public Sub Run()\n"
            + "End Sub\n";
        File.WriteAllText(sourcePath, sourceText);
        return new ManifestProject(
            projectRoot,
            templatePath,
            new Uri(sourcePath).AbsoluteUri,
            sourceText);
    }

    private sealed record ManifestProject(
        string RootPath,
        string TemplatePath,
        string Uri,
        string SourceText) : IDisposable
    {
        public void Dispose()
            => Directory.Delete(RootPath, recursive: true);
    }

    private sealed class NullReferenceCatalogLifecycle
        : IReferenceCatalogLifecycle
    {
        public void ActivateProject(string uri)
        {
        }

        public void ApplyManifestSelectionChange(string uri, string text)
        {
        }

        public void DeactivateManifest(string uri)
        {
        }
    }

    private sealed class BlockingSourceTemplateFileSystem(
        IVbaProjectFileSystem inner,
        string sourceTemplatePath)
        : IVbaProjectFileSystem
    {
        private readonly ManualResetEventSlim release = new();
        private readonly string fullSourceTemplatePath =
            Path.GetFullPath(sourceTemplatePath);

        public TaskCompletionSource TemplateReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool FileExists(string path)
            => inner.FileExists(path);

        public bool DirectoryExists(string path)
            => inner.DirectoryExists(path);

        public IEnumerable<string> EnumerateSourceFiles(
            string rootPath,
            string searchPattern,
            SearchOption searchOption)
            => inner.EnumerateSourceFiles(
                rootPath,
                searchPattern,
                searchOption);

        public bool TryGetSourceMetadata(
            string path,
            out VbaProjectSourceFileMetadata metadata)
            => inner.TryGetSourceMetadata(path, out metadata);

        public string ReadManifestText(string path)
            => inner.ReadManifestText(path);

        public byte[] ReadSourceBytes(string path)
        {
            BlockSourceTemplateRead(path, CancellationToken.None);
            return inner.ReadSourceBytes(path);
        }

        public byte[] ReadSourceBytes(
            string path,
            CancellationToken cancellationToken)
        {
            BlockSourceTemplateRead(path, cancellationToken);
            return inner.ReadSourceBytes(path, cancellationToken);
        }

        private void BlockSourceTemplateRead(
            string path,
            CancellationToken cancellationToken)
        {
            if (Path.GetFullPath(path).Equals(
                    fullSourceTemplatePath,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
            {
                TemplateReadStarted.TrySetResult();
                release.Wait(cancellationToken);
            }
        }

        public bool PathsReferToSameEntry(string left, string right)
            => inner.PathsReferToSameEntry(left, right);

        public void ReleaseTemplateRead()
            => release.Set();
    }

    private sealed class StagedSourceTemplateFileSystem(
        IVbaProjectFileSystem inner,
        string sourceTemplatePath)
        : IVbaProjectFileSystem
    {
        private readonly ManualResetEventSlim releaseFinalFenceRead = new();
        private readonly string fullSourceTemplatePath =
            Path.GetFullPath(sourceTemplatePath);
        private int cancellableTemplateReadCount;
        private int nonCancellableTemplateReadCount;
        private int templateReadCount;

        public TaskCompletionSource FinalFenceReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int CancellableTemplateReadCount =>
            Volatile.Read(ref cancellableTemplateReadCount);

        public int NonCancellableTemplateReadCount =>
            Volatile.Read(ref nonCancellableTemplateReadCount);

        public int TemplateReadCount => Volatile.Read(ref templateReadCount);

        public bool FileExists(string path)
            => inner.FileExists(path);

        public bool DirectoryExists(string path)
            => inner.DirectoryExists(path);

        public IEnumerable<string> EnumerateSourceFiles(
            string rootPath,
            string searchPattern,
            SearchOption searchOption)
            => inner.EnumerateSourceFiles(
                rootPath,
                searchPattern,
                searchOption);

        public bool TryGetSourceMetadata(
            string path,
            out VbaProjectSourceFileMetadata metadata)
            => inner.TryGetSourceMetadata(path, out metadata);

        public string ReadManifestText(string path)
            => inner.ReadManifestText(path);

        public byte[] ReadSourceBytes(string path)
        {
            BlockFinalFenceRead(
                path,
                CancellationToken.None,
                ref nonCancellableTemplateReadCount);
            return inner.ReadSourceBytes(path);
        }

        public byte[] ReadSourceBytes(
            string path,
            CancellationToken cancellationToken)
        {
            BlockFinalFenceRead(
                path,
                cancellationToken,
                ref cancellableTemplateReadCount);
            return inner.ReadSourceBytes(path, cancellationToken);
        }

        public bool PathsReferToSameEntry(string left, string right)
            => inner.PathsReferToSameEntry(left, right);

        public void ReleaseFinalFenceRead()
            => releaseFinalFenceRead.Set();

        private void BlockFinalFenceRead(
            string path,
            CancellationToken cancellationToken,
            ref int overloadReadCount)
        {
            if (!IsSourceTemplate(path))
            {
                return;
            }

            Interlocked.Increment(ref overloadReadCount);
            if (Interlocked.Increment(ref templateReadCount) != 2)
            {
                return;
            }

            FinalFenceReadStarted.TrySetResult();
            releaseFinalFenceRead.Wait(cancellationToken);
        }

        private bool IsSourceTemplate(string path)
            => Path.GetFullPath(path).Equals(
                fullSourceTemplatePath,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
    }

    private sealed class CountingSourceTemplateFileSystem(
        IVbaProjectFileSystem inner,
        string sourceTemplatePath)
        : IVbaProjectFileSystem
    {
        private readonly string fullSourceTemplatePath =
            Path.GetFullPath(sourceTemplatePath);
        private int cancellableTemplateReadCount;
        private int nonCancellableTemplateReadCount;

        public int CancellableTemplateReadCount =>
            Volatile.Read(ref cancellableTemplateReadCount);

        public int NonCancellableTemplateReadCount =>
            Volatile.Read(ref nonCancellableTemplateReadCount);

        public int TemplateReadCount => CancellableTemplateReadCount
            + NonCancellableTemplateReadCount;

        public bool FileExists(string path)
            => inner.FileExists(path);

        public bool DirectoryExists(string path)
            => inner.DirectoryExists(path);

        public IEnumerable<string> EnumerateSourceFiles(
            string rootPath,
            string searchPattern,
            SearchOption searchOption)
            => inner.EnumerateSourceFiles(
                rootPath,
                searchPattern,
                searchOption);

        public bool TryGetSourceMetadata(
            string path,
            out VbaProjectSourceFileMetadata metadata)
            => inner.TryGetSourceMetadata(path, out metadata);

        public string ReadManifestText(string path)
            => inner.ReadManifestText(path);

        public byte[] ReadSourceBytes(string path)
        {
            CountTemplateRead(path, ref nonCancellableTemplateReadCount);
            return inner.ReadSourceBytes(path);
        }

        public byte[] ReadSourceBytes(
            string path,
            CancellationToken cancellationToken)
        {
            CountTemplateRead(path, ref cancellableTemplateReadCount);
            return inner.ReadSourceBytes(path, cancellationToken);
        }

        public bool PathsReferToSameEntry(string left, string right)
            => inner.PathsReferToSameEntry(left, right);

        private void CountTemplateRead(string path, ref int readCount)
        {
            if (Path.GetFullPath(path).Equals(
                    fullSourceTemplatePath,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
            {
                Interlocked.Increment(ref readCount);
            }
        }
    }
}
