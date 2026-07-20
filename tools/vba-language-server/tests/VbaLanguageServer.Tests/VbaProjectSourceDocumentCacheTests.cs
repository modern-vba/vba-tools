using System.Text;
using VbaLanguageServer.Workspace;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaProjectSourceDocumentCacheTests
{
    [Fact]
    public async Task Invalidated_older_load_cannot_overwrite_a_newer_cached_document()
        => await AssertOlderLoadCannotOverwriteAsync(invalidate: true);

    [Fact]
    public async Task Metadata_change_during_read_cannot_overwrite_a_newer_cached_document()
        => await AssertOlderLoadCannotOverwriteAsync(invalidate: false);

    private static async Task AssertOlderLoadCannotOverwriteAsync(
        bool invalidate)
    {
        const string oldText =
            "Attribute VB_Name = \"Module1\"\n"
            + "Public Sub BeforeChange()\n"
            + "End Sub\n";
        const string newText =
            "Attribute VB_Name = \"Module1\"\n"
            + "Public Sub AfterChange()\n"
            + "End Sub\n";
        var path = Path.GetFullPath("Module1.bas");
        var uri = new Uri(path).AbsoluteUri;
        var fileSystem = new BlockingSourceFileSystem(oldText);
        var cache = new VbaProjectSourceDocumentCache(fileSystem);

        var olderLoad = Task.Run(
            () => cache.GetOrLoadDocument(uri, path));
        await fileSystem.FirstReadStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(2));

        fileSystem.ReplaceSource(newText);
        if (invalidate)
        {
            cache.Invalidate(path);
        }

        var newer = cache.GetOrLoadDocument(uri, path);
        Assert.Equal(newText, newer.Text);

        fileSystem.ReleaseFirstRead();
        _ = await olderLoad.WaitAsync(TimeSpan.FromSeconds(2));
        var retained = cache.GetOrLoadDocument(uri, path);

        Assert.Same(newer, retained);
        Assert.Equal(newText, retained.Text);
        Assert.Equal(1, cache.Count);
    }

    private sealed class BlockingSourceFileSystem : IVbaProjectFileSystem
    {
        private readonly object gate = new();
        private readonly ManualResetEventSlim releaseFirstRead = new(false);
        private byte[] sourceBytes;
        private VbaProjectSourceFileMetadata metadata;
        private int readCount;

        public BlockingSourceFileSystem(string source)
        {
            sourceBytes = Encoding.UTF8.GetBytes(source);
            metadata = new VbaProjectSourceFileMetadata(
                sourceBytes.LongLength,
                LastWriteTimeUtcTicks: 1);
        }

        public TaskCompletionSource FirstReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool FileExists(string path) => true;

        public bool DirectoryExists(string path) => true;

        public IEnumerable<string> EnumerateSourceFiles(
            string rootPath,
            string searchPattern,
            SearchOption searchOption)
            => [];

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

        public byte[] ReadSourceBytes(string path)
        {
            byte[] captured;
            lock (gate)
            {
                captured = sourceBytes.ToArray();
            }

            if (Interlocked.Increment(ref readCount) == 1)
            {
                FirstReadStarted.TrySetResult();
                releaseFirstRead.Wait();
            }

            return captured;
        }

        public void ReplaceSource(string source)
        {
            lock (gate)
            {
                sourceBytes = Encoding.UTF8.GetBytes(source);
                metadata = new VbaProjectSourceFileMetadata(
                    sourceBytes.LongLength,
                    metadata.LastWriteTimeUtcTicks + 1);
            }
        }

        public void ReleaseFirstRead()
            => releaseFirstRead.Set();
    }
}
