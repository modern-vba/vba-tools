using System.Text;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.Workspace;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaProjectDiskInventoryTests
{
    private const string InitialText =
        "Attribute VB_Name = \"Module1\"\n"
        + "Public Sub One()\n"
        + "End Sub\n";
    private const string ChangedSameLengthText =
        "Attribute VB_Name = \"Module1\"\n"
        + "Public Sub Two()\n"
        + "End Sub\n";

    [Fact]
    public void Cold_capture_reuses_decoded_text_when_metadata_is_unchanged()
    {
        var fileSystem = new MutableSourceFileSystem(InitialText);
        var inventory = new VbaFileSystemProjectDiskInventory(fileSystem);

        var first = CaptureSingleColdSource(inventory, fileSystem.Path);
        var second = CaptureSingleColdSource(inventory, fileSystem.Path);

        Assert.Equal(1, fileSystem.SourceReadCount);
        Assert.Equal(first.Text, second.Text);
        Assert.Equal(first.ContentIdentity, second.ContentIdentity);
    }

    [Fact]
    public void Invalidation_forces_cold_capture_to_read_source_again()
    {
        var fileSystem = new MutableSourceFileSystem(InitialText);
        var inventory = new VbaFileSystemProjectDiskInventory(fileSystem);
        var first = CaptureSingleColdSource(inventory, fileSystem.Path);

        fileSystem.ReplaceSource(
            ChangedSameLengthText,
            advanceMetadata: false);
        inventory.InvalidateSource(fileSystem.Path);
        var second = CaptureSingleColdSource(inventory, fileSystem.Path);

        Assert.Equal(2, fileSystem.SourceReadCount);
        Assert.NotEqual(first.ContentIdentity, second.ContentIdentity);
        Assert.Equal(ChangedSameLengthText, second.Text);
    }

    [Fact]
    public async Task Reconciliation_reads_and_detects_changed_content_with_unchanged_metadata()
    {
        var fileSystem = new MutableSourceFileSystem(InitialText);
        var inventory = new VbaFileSystemProjectDiskInventory(fileSystem);
        var first = CaptureSingleColdSource(inventory, fileSystem.Path);
        fileSystem.ReplaceSource(
            ChangedSameLengthText,
            advanceMetadata: false);

        var scan = await inventory.ObserveReconciliationAsync(
            CreateObservationRequest(first),
            CancellationToken.None);
        var observed = Assert.Single(scan.Sources);

        Assert.Equal(2, fileSystem.SourceReadCount);
        Assert.Equal(first.Metadata, observed.Metadata);
        Assert.NotEqual(
            first.ContentIdentity,
            observed.ContentIdentity);
        Assert.Equal(ChangedSameLengthText, observed.Text);
    }

    [Fact]
    public void Content_identity_is_stable_for_equal_text_and_changes_for_changed_text()
    {
        var fileSystem = new MutableSourceFileSystem(InitialText);
        var inventory = new VbaFileSystemProjectDiskInventory(fileSystem);
        var first = CaptureSingleColdSource(inventory, fileSystem.Path);

        inventory.InvalidateSource(fileSystem.Path);
        var equalText = CaptureSingleColdSource(
            inventory,
            fileSystem.Path);
        fileSystem.ReplaceSource(
            ChangedSameLengthText,
            advanceMetadata: false);
        inventory.InvalidateSource(fileSystem.Path);
        var changedText = CaptureSingleColdSource(
            inventory,
            fileSystem.Path);

        Assert.Equal(
            first.ContentIdentity,
            equalText.ContentIdentity);
        Assert.NotEqual(
            first.ContentIdentity,
            changedText.ContentIdentity);
    }

    [Fact]
    public async Task Invalidated_older_load_cannot_overwrite_a_newer_cached_source()
    {
        var fileSystem = new BlockingSourceFileSystem(InitialText);
        var inventory = new VbaFileSystemProjectDiskInventory(fileSystem);

        var olderLoad = Task.Run(
            () => CaptureSingleColdSource(
                inventory,
                fileSystem.Path));
        await fileSystem.FirstReadStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(2));

        fileSystem.ReplaceSource(ChangedSameLengthText);
        inventory.InvalidateSource(fileSystem.Path);
        var newer = CaptureSingleColdSource(
            inventory,
            fileSystem.Path);

        fileSystem.ReleaseFirstRead();
        _ = await olderLoad.WaitAsync(TimeSpan.FromSeconds(2));
        var retained = CaptureSingleColdSource(
            inventory,
            fileSystem.Path);

        Assert.Equal(
            newer.ContentIdentity,
            retained.ContentIdentity);
        Assert.Equal(ChangedSameLengthText, retained.Text);
        Assert.Equal(1, inventory.Count);
    }

    [Fact]
    public async Task Older_parallel_load_cannot_overwrite_a_newer_cached_source_when_metadata_is_unchanged()
    {
        var fileSystem = new BlockingSourceFileSystem(InitialText);
        var inventory = new VbaFileSystemProjectDiskInventory(fileSystem);

        var olderLoad = Task.Run(
            () => CaptureSingleColdSource(
                inventory,
                fileSystem.Path));
        await fileSystem.FirstReadStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(2));

        fileSystem.ReplaceSource(
            ChangedSameLengthText,
            advanceMetadata: false);
        var newer = CaptureSingleColdSource(
            inventory,
            fileSystem.Path);

        fileSystem.ReleaseFirstRead();
        _ = await olderLoad.WaitAsync(TimeSpan.FromSeconds(2));
        var retained = CaptureSingleColdSource(
            inventory,
            fileSystem.Path);

        Assert.Equal(
            newer.ContentIdentity,
            retained.ContentIdentity);
        Assert.Equal(ChangedSameLengthText, retained.Text);
        Assert.Equal(1, inventory.Count);
    }

    [Fact]
    public void Cold_capture_ignores_source_deleted_between_metadata_and_read()
    {
        var fileSystem = new DeletedDuringReadFileSystem(InitialText);
        var inventory = new VbaFileSystemProjectDiskInventory(fileSystem);

        var capture = CaptureColdSources(
            inventory,
            fileSystem.Path);

        Assert.Empty(capture.Sources);
        Assert.Equal(0, inventory.Count);
    }

    [Fact]
    public void Watched_capture_reads_one_owned_source_without_enumerating_the_project()
    {
        var fileSystem = new MutableSourceFileSystem(InitialText);
        var inventory = new VbaFileSystemProjectDiskInventory(fileSystem);
        var resolution = new VbaProjectResolution(
            VbaProjectResolutionKind.AdHoc,
            Path.GetDirectoryName(fileSystem.Path)!);

        var source = inventory.CaptureWatchedSource(
            resolution,
            new Uri(fileSystem.Path).AbsoluteUri,
            new Dictionary<string, bool>(
                StringComparer.OrdinalIgnoreCase),
            CancellationToken.None);

        Assert.NotNull(source);
        Assert.Equal(0, fileSystem.EnumerationCount);
        Assert.Equal(1, fileSystem.SourceReadCount);
        Assert.Equal(InitialText, source.Text);
    }

    private static VbaProjectDiskSource CaptureSingleColdSource(
        VbaFileSystemProjectDiskInventory inventory,
        string sourcePath)
        => Assert.Single(
            CaptureColdSources(
                inventory,
                sourcePath).Sources);

    private static VbaProjectDiskColdSourceCapture CaptureColdSources(
        VbaFileSystemProjectDiskInventory inventory,
        string sourcePath)
    {
        var resolution = new VbaProjectResolution(
            VbaProjectResolutionKind.AdHoc,
            Path.GetDirectoryName(sourcePath)!);
        var capture = inventory.CaptureColdSources(
            resolution,
            candidateSourceUris: [],
            excludedSourceUris: new HashSet<string>(
                StringComparer.OrdinalIgnoreCase),
            manifestBarrierOverrides:
                new Dictionary<string, bool>(
                    StringComparer.OrdinalIgnoreCase),
            CancellationToken.None);
        return capture;
    }

    private static VbaProjectDiskObservationRequest
        CreateObservationRequest(VbaProjectDiskSource source)
        => new(
            new VbaProjectDiskProjectScope(
                VbaProjectResolutionKind.AdHoc,
                Path.GetDirectoryName(source.FullPath)!,
                OwningManifestPath: null),
            manifestCandidates: [],
            barrierOverrides: [],
            observedManifestBarrierUris: []);

    private class MutableSourceFileSystem : IVbaProjectFileSystem
    {
        private readonly object gate = new();
        private byte[] sourceBytes;
        private VbaProjectSourceFileMetadata metadata;

        public MutableSourceFileSystem(string source)
        {
            Path = System.IO.Path.GetFullPath("Module1.bas");
            sourceBytes = Encoding.UTF8.GetBytes(source);
            metadata = new VbaProjectSourceFileMetadata(
                sourceBytes.LongLength,
                LastWriteTimeUtcTicks: 1);
        }

        public string Path { get; }

        public int SourceReadCount { get; protected set; }

        public int EnumerationCount { get; private set; }

        public bool FileExists(string path) => false;

        public bool DirectoryExists(string path) => true;

        public IEnumerable<string> EnumerateSourceFiles(
            string rootPath,
            string searchPattern,
            SearchOption searchOption)
        {
            EnumerationCount++;
            return searchPattern.Equals(
                "*.bas",
                StringComparison.OrdinalIgnoreCase)
                ? [Path]
                : [];
        }

        public bool TryGetSourceMetadata(
            string path,
            out VbaProjectSourceFileMetadata captured)
        {
            lock (gate)
            {
                captured = metadata;
                return true;
            }
        }

        public string ReadManifestText(string path) => "";

        public virtual byte[] ReadSourceBytes(string path)
        {
            lock (gate)
            {
                SourceReadCount++;
                return sourceBytes.ToArray();
            }
        }

        public void ReplaceSource(
            string source,
            bool advanceMetadata = true)
        {
            lock (gate)
            {
                sourceBytes = Encoding.UTF8.GetBytes(source);
                metadata = new VbaProjectSourceFileMetadata(
                    sourceBytes.LongLength,
                    advanceMetadata
                        ? metadata.LastWriteTimeUtcTicks + 1
                        : metadata.LastWriteTimeUtcTicks);
            }
        }

        protected byte[] CaptureSourceBytes()
        {
            lock (gate)
            {
                SourceReadCount++;
                return sourceBytes.ToArray();
            }
        }
    }

    private sealed class BlockingSourceFileSystem
        : MutableSourceFileSystem
    {
        private readonly ManualResetEventSlim releaseFirstRead =
            new(false);
        private int readCount;

        public BlockingSourceFileSystem(string source)
            : base(source)
        {
        }

        public TaskCompletionSource FirstReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override byte[] ReadSourceBytes(string path)
        {
            var captured = CaptureSourceBytes();
            if (Interlocked.Increment(ref readCount) == 1)
            {
                FirstReadStarted.TrySetResult();
                releaseFirstRead.Wait();
            }

            return captured;
        }

        public void ReleaseFirstRead()
            => releaseFirstRead.Set();
    }

    private sealed class DeletedDuringReadFileSystem
        : MutableSourceFileSystem
    {
        public DeletedDuringReadFileSystem(string source)
            : base(source)
        {
        }

        public override byte[] ReadSourceBytes(string path)
            => throw new FileNotFoundException(
                "Source was deleted during capture.",
                path);
    }
}
