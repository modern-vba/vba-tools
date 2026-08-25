using VbaDev.App.Workbooks;

namespace VbaDev.App.Build;

internal interface IWorkbookGenerationSourceInput : IDisposable
{
    IReadOnlyList<VbaSourceFile> SourceFiles { get; }
}

internal sealed class BorrowedWorkbookGenerationSourceInput(
    IReadOnlyList<VbaSourceFile> sourceFiles) : IWorkbookGenerationSourceInput
{
    public IReadOnlyList<VbaSourceFile> SourceFiles => sourceFiles;

    public void Dispose()
    {
    }
}

internal sealed class BuildSourceSnapshotCaptureFactory
{
    private readonly string scratchRoot;

    public BuildSourceSnapshotCaptureFactory()
        : this(Path.Combine(Path.GetTempPath(), "vba-dev-build-source-snapshot"))
    {
    }

    internal BuildSourceSnapshotCaptureFactory(string scratchRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchRoot);
        this.scratchRoot = Path.GetFullPath(scratchRoot);
    }

    public BuildSourceSnapshotCapture Create(
        string sourceSnapshotPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSnapshotPath);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshotPath = Path.GetFullPath(sourceSnapshotPath);
        if (!Directory.Exists(snapshotPath))
        {
            throw new InvalidOperationException(
                $"Build source snapshot directory was not found: {snapshotPath}");
        }

        var sourceFiles = DocumentSourceSetLayout
            .EnumerateVbaSourceFiles(snapshotPath)
            .OrderBy(source => source.FileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(source => source.FileName, StringComparer.Ordinal)
            .ThenBy(source => source.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(source => source.SourcePath, StringComparer.Ordinal)
            .ToArray();
        DocumentSourceSetLayout.ThrowIfDuplicateSourceFileNames(snapshotPath, sourceFiles);
        var inventory = sourceFiles
            .Select(source => new SnapshotSourceInventoryEntry(
                source,
                GetSafeRelativePath(snapshotPath, source.SourcePath),
                source.BinaryPath is null
                    ? null
                    : GetSafeRelativePath(snapshotPath, source.BinaryPath)))
            .ToArray();
        var stagingPath = Path.Combine(
            scratchRoot,
            Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(stagingPath);
            var capturedSources = new List<VbaSourceFile>(inventory.Length);
            foreach (var entry in inventory)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var capturedSourcePath = CopyExact(
                    entry.Source.SourcePath,
                    stagingPath,
                    entry.RelativeSourcePath);
                var capturedBinaryPath = entry.RelativeBinaryPath is null
                    ? null
                    : CopyExact(
                        entry.Source.BinaryPath!,
                        stagingPath,
                        entry.RelativeBinaryPath);
                capturedSources.Add(new VbaSourceFile(
                    capturedSourcePath,
                    entry.Source.Kind,
                    capturedBinaryPath)
                {
                    ExpectedUnicodeText = entry.Source.ExpectedUnicodeText,
                    ExpectedUnicodeTextSourcePath = entry.Source.ExpectedUnicodeTextSourcePath,
                    DiagnosticSourcePath = entry.Source.DiagnosticSourcePath
                        ?? entry.Source.SourcePath
                });
            }

            return new BuildSourceSnapshotCapture(
                stagingPath,
                capturedSources.AsReadOnly());
        }
        catch (Exception captureError)
        {
            try
            {
                DeleteStagingDirectory(stagingPath);
            }
            catch (Exception cleanupError)
            {
                throw new InvalidOperationException(
                    $"{captureError.Message} The build source snapshot staging directory could not be removed: '{stagingPath}'.",
                    new AggregateException(captureError, cleanupError));
            }

            throw;
        }
    }

    private static string CopyExact(
        string sourcePath,
        string stagingPath,
        string relativePath)
    {
        var targetPath = Path.Combine(stagingPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.WriteAllBytes(targetPath, File.ReadAllBytes(sourcePath));
        return targetPath;
    }

    private static string GetSafeRelativePath(string rootPath, string path)
    {
        var relativePath = Path.GetRelativePath(rootPath, path);
        if (Path.IsPathRooted(relativePath)
            || relativePath.Equals("..", StringComparison.Ordinal)
            || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Build source snapshot entry resolves outside the snapshot directory: {path}");
        }

        return relativePath;
    }

    internal static void DeleteStagingDirectory(string stagingPath)
    {
        try
        {
            if (Directory.Exists(stagingPath))
            {
                Directory.Delete(stagingPath, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"The build source snapshot staging directory could not be removed: '{stagingPath}'.",
                ex);
        }
    }

    private sealed record SnapshotSourceInventoryEntry(
        VbaSourceFile Source,
        string RelativeSourcePath,
        string? RelativeBinaryPath);
}

internal sealed class BuildSourceSnapshotCapture : IWorkbookGenerationSourceInput
{
    private int disposed;

    internal BuildSourceSnapshotCapture(
        string stagingPath,
        IReadOnlyList<VbaSourceFile> sourceFiles)
    {
        StagingPath = stagingPath;
        SourceFiles = sourceFiles;
    }

    public string StagingPath { get; }

    public IReadOnlyList<VbaSourceFile> SourceFiles { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        BuildSourceSnapshotCaptureFactory.DeleteStagingDirectory(StagingPath);
    }
}
