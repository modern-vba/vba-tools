namespace VbaLanguageServer.Workspace;

/// <summary>
/// Represents the stable disk metadata used to cache one exported VBA source file.
/// </summary>
internal readonly record struct VbaProjectSourceFileMetadata(
    long Length,
    long LastWriteTimeUtcTicks);

/// <summary>
/// Isolates project-manifest and exported-source reads from interactive snapshot construction.
/// </summary>
internal interface IVbaProjectFileSystem
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    IEnumerable<string> EnumerateSourceFiles(
        string rootPath,
        string searchPattern,
        SearchOption searchOption);

    bool TryGetSourceMetadata(
        string path,
        out VbaProjectSourceFileMetadata metadata);

    string ReadManifestText(string path);

    string ReadManifestText(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var text = ReadManifestText(path);
        cancellationToken.ThrowIfCancellationRequested();
        return text;
    }

    byte[] ReadSourceBytes(string path);

    byte[] ReadSourceBytes(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = ReadSourceBytes(path);
        cancellationToken.ThrowIfCancellationRequested();
        return bytes;
    }

    bool PathsReferToSameEntry(string left, string right)
        => Path.GetFullPath(left).Equals(
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
}

/// <summary>
/// Reads project inputs from the host filesystem.
/// </summary>
internal sealed class SystemVbaProjectFileSystem : IVbaProjectFileSystem
{
    public static SystemVbaProjectFileSystem Instance { get; } = new();

    private SystemVbaProjectFileSystem()
    {
    }

    public bool FileExists(string path)
        => File.Exists(path);

    public bool DirectoryExists(string path)
        => Directory.Exists(path);

    public IEnumerable<string> EnumerateSourceFiles(
        string rootPath,
        string searchPattern,
        SearchOption searchOption)
        => Directory.EnumerateFiles(rootPath, searchPattern, searchOption);

    public bool TryGetSourceMetadata(
        string path,
        out VbaProjectSourceFileMetadata metadata)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
        {
            metadata = default;
            return false;
        }

        metadata = new VbaProjectSourceFileMetadata(
            fileInfo.Length,
            fileInfo.LastWriteTimeUtc.Ticks);
        return true;
    }

    public string ReadManifestText(string path)
        => File.ReadAllText(path);

    public string ReadManifestText(
        string path,
        CancellationToken cancellationToken)
        => File.ReadAllTextAsync(path, cancellationToken)
            .GetAwaiter()
            .GetResult();

    public byte[] ReadSourceBytes(string path)
        => File.ReadAllBytes(path);

    public byte[] ReadSourceBytes(
        string path,
        CancellationToken cancellationToken)
        => File.ReadAllBytesAsync(path, cancellationToken)
            .GetAwaiter()
            .GetResult();

    public bool PathsReferToSameEntry(string left, string right)
        => Path.GetFullPath(left).Equals(
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
}
