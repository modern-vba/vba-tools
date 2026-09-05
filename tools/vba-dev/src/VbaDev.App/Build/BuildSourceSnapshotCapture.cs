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
    private readonly VbaSourceAdmission admission;

    public BuildSourceSnapshotCaptureFactory()
        : this(Path.Combine(Path.GetTempPath(), "vba-dev-build-source-snapshot"))
    {
    }

    internal BuildSourceSnapshotCaptureFactory(string scratchRoot)
        : this(scratchRoot, new VbaSourceAdmission(ActiveWindowsAnsiCodePage.Get))
    {
    }

    internal BuildSourceSnapshotCaptureFactory(string scratchRoot, VbaSourceAdmission admission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchRoot);
        this.scratchRoot = Path.GetFullPath(scratchRoot);
        this.admission = admission ?? throw new ArgumentNullException(nameof(admission));
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

        var admitted = admission.Admit(snapshotPath, VbaSourceAdmissionIntent.Build, cancellationToken);
        var inventory = admitted.Sources
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
                    entry.Source.OriginalBytes.AsSpan(),
                    stagingPath,
                    entry.RelativeSourcePath);
                var capturedBinaryPath = entry.RelativeBinaryPath is null
                    ? null
                    : CopyExact(
                        entry.Source.BinaryBytes!.Value.AsSpan(),
                        stagingPath,
                        entry.RelativeBinaryPath);
                capturedSources.Add(new VbaSourceFile(
                    capturedSourcePath,
                    entry.Source.Kind,
                    capturedBinaryPath)
                {
                    DiagnosticSourcePath = entry.Source.DiagnosticSourcePath
                });
            }

            return new BuildSourceSnapshotCapture(
                stagingPath,
                capturedSources.AsReadOnly(),
                admitted,
                snapshotPath);
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
        ReadOnlySpan<byte> bytes,
        string stagingPath,
        string relativePath)
    {
        var targetPath = Path.Combine(stagingPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.WriteAllBytes(targetPath, bytes);
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
        AdmittedVbaSource Source,
        string RelativeSourcePath,
        string? RelativeBinaryPath);
}

internal sealed class BuildSourceSnapshotCapture : IAdmittedWorkbookGenerationSourceInput
{
    private int disposed;

    internal BuildSourceSnapshotCapture(
        string stagingPath,
        IReadOnlyList<VbaSourceFile> sourceFiles,
        AdmittedVbaSourceSet admission,
        string sourceRootPath)
    {
        StagingPath = stagingPath;
        SourceFiles = sourceFiles;
        Admission = admission;
        SourceRootPath = sourceRootPath;
    }

    public string StagingPath { get; }

    public IReadOnlyList<VbaSourceFile> SourceFiles { get; }

    public AdmittedVbaSourceSet Admission { get; }

    internal string SourceRootPath { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        BuildSourceSnapshotCaptureFactory.DeleteStagingDirectory(StagingPath);
    }
}
