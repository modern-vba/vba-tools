using VbaTools.ProjectMetadata;
using VbaDev.App.FileSystem;

namespace VbaDev.App.Build;

/// <summary>Owns one immutable capture of the whole source-template package.</summary>
public sealed class CapturedWorkbookTemplate
{
    private readonly byte[] bytes;

    private CapturedWorkbookTemplate(string sourcePath, byte[] bytes)
    {
        SourcePath = sourcePath;
        this.bytes = bytes;
    }

    public string SourcePath { get; }

    public static CapturedWorkbookTemplate Capture(string sourcePath, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(sourcePath);
        return new(path, ReadBytes(path, FileShare.Read, cancellationToken));
    }

    internal static CapturedWorkbookTemplate CaptureSavedOpenSource(
        string sourcePath, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(sourcePath);
        const FileShare share = FileShare.ReadWrite | FileShare.Delete;
        var first = ReadBytes(path, share, cancellationToken);
        var second = ReadBytes(path, share, cancellationToken);
        if (!first.AsSpan().SequenceEqual(second))
            throw new IOException(
                $"The saved source workbook changed during Build admission: {path}. Save it and retry Build.");
        return new(path, second);
    }

    private static byte[] ReadBytes(string path, FileShare share, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, share);
        if (stream.Length > VbaProjectPackageMetadataReader.MaximumPackageLength)
        {
            throw new InvalidOperationException($"The source-template package exceeds the supported size: {path}");
        }
        var captured = new byte[checked((int)stream.Length)];
        stream.ReadExactly(captured);
        cancellationToken.ThrowIfCancellationRequested();
        return captured;
    }

    internal VbaProjectPackageMetadataReadResult ReadMetadata(CancellationToken cancellationToken)
        => new VbaProjectPackageMetadataReader().Read(bytes, cancellationToken);

    internal void WriteTo(Stream destination) => destination.Write(bytes);

    internal WorkbookStagingArtifact CreateStage(IExactFileSystemObjectOwnershipFactory ownershipFactory,
        string directory, string fileName, bool createDirectory = false, Action<string>? afterCreated = null)
        => WorkbookStagingArtifact.CreateFromBytes(ownershipFactory, bytes, directory, fileName, createDirectory, afterCreated);
}
