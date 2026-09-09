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
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.GetFullPath(sourcePath);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > VbaProjectPackageMetadataReader.MaximumPackageLength)
        {
            throw new InvalidOperationException($"The source-template package exceeds the supported size: {path}");
        }
        var captured = new byte[checked((int)stream.Length)];
        stream.ReadExactly(captured);
        cancellationToken.ThrowIfCancellationRequested();
        return new(path, captured);
    }

    internal VbaProjectPackageMetadataReadResult ReadMetadata(CancellationToken cancellationToken)
        => new VbaProjectPackageMetadataReader().Read(bytes, cancellationToken);

    internal void WriteTo(Stream destination) => destination.Write(bytes);

    internal WorkbookStagingArtifact CreateStage(IExactFileSystemObjectOwnershipFactory ownershipFactory,
        string directory, string fileName, bool createDirectory = false, Action<string>? afterCreated = null)
        => WorkbookStagingArtifact.CreateFromBytes(ownershipFactory, bytes, directory, fileName, createDirectory, afterCreated);
}
