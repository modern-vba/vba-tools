using VbaDev.App.FileSystem;
using VbaDev.App.Workbooks;

namespace VbaDev.App.Build;

internal sealed class BuildSourceSnapshotCaptureFactory
{
    private readonly IExactFileSystemObjectOwnershipFactory ownershipFactory;
    private readonly string scratchRoot;
    private readonly VbaSourceAdmission admission;
    private readonly Action<string>? afterFileCaptured;

    public BuildSourceSnapshotCaptureFactory(IExactFileSystemObjectOwnershipFactory ownershipFactory)
        : this(ownershipFactory, Path.Combine(Path.GetTempPath(), "vba-dev-build-source-snapshot"))
    {
    }

    internal BuildSourceSnapshotCaptureFactory(IExactFileSystemObjectOwnershipFactory ownershipFactory, string scratchRoot)
        : this(ownershipFactory, scratchRoot, new VbaSourceAdmission(ActiveWindowsAnsiCodePage.Get))
    {
    }

    internal BuildSourceSnapshotCaptureFactory(
        IExactFileSystemObjectOwnershipFactory ownershipFactory,
        string scratchRoot,
        VbaSourceAdmission admission,
        Action<string>? afterFileCaptured = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchRoot);
        this.ownershipFactory = ownershipFactory ?? throw new ArgumentNullException(nameof(ownershipFactory));
        this.scratchRoot = Path.GetFullPath(scratchRoot);
        this.admission = admission ?? throw new ArgumentNullException(nameof(admission));
        this.afterFileCaptured = afterFileCaptured;
    }

    public BuildSourceSnapshotCapture Create(string sourceSnapshotPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSnapshotPath);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshotPath = Path.GetFullPath(sourceSnapshotPath);

        var admitted = admission.AdmitSourceSnapshotBuild(snapshotPath, cancellationToken);
        var inventory = admitted.Sources.Select(source => new SnapshotSourceInventoryEntry(
            source, GetSafeRelativePath(snapshotPath, source.SourcePath),
            source.BinaryPath is null ? null : GetSafeRelativePath(snapshotPath, source.BinaryPath))).ToArray();
        // The shared container is not invocation scratch and is never adopted for deletion.
        Directory.CreateDirectory(scratchRoot);
        var ownership = ownershipFactory.Open();
        var scratch = new InvocationScratch(ownership);
        var directories = new Dictionary<string, ExactFileSystemObjectOwnership.DirectoryReceipt>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var transferred = false;
        var fencesReleased = false;
        try
        {
            var root = ownership.TryCreateOnlyDirectory(scratchRoot, Guid.NewGuid().ToString("N"))
                ?? throw new IOException($"A unique build source capture could not be created beneath '{scratchRoot}'.");
            directories.Add(string.Empty, root);
            scratch.Register(root);
            var capturedSources = new List<VbaSourceFile>(inventory.Length);
            foreach (var entry in inventory)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePath = CopyExact(entry.Source.OriginalBytes.AsSpan(), entry.RelativeSourcePath);
                var binaryPath = entry.RelativeBinaryPath is null ? null
                    : CopyExact(entry.Source.BinaryBytes!.Value.AsSpan(), entry.RelativeBinaryPath);
                capturedSources.Add(new VbaSourceFile(sourcePath, entry.Source.Kind, binaryPath)
                {
                    DiagnosticSourcePath = entry.Source.DiagnosticSourcePath
                });
            }
            cancellationToken.ThrowIfCancellationRequested();
            ReleaseFences();
            var capture = new BuildSourceSnapshotCapture(ownership, scratch, root.Route,
                capturedSources.AsReadOnly(), admitted, snapshotPath);
            transferred = true;
            return capture;
        }
        catch (Exception captureError)
        {
            ReleaseFences();
            var cleanup = scratch.Cleanup();
            if (cleanup.Status != InvocationScratchCleanupStatus.Removed)
                throw new BuildSourceSnapshotCaptureRetainedException(captureError, cleanup);
            throw;
        }
        finally
        {
            if (!transferred) ownership.Dispose();
        }

        string CopyExact(ReadOnlySpan<byte> bytes, string relativePath)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = GetDirectory(Path.GetDirectoryName(relativePath) ?? string.Empty);
            ExactFileSystemObjectOwnership.FileReceipt receipt;
            try
            {
                receipt = ownership.CreateOnlyFile(directory, Path.GetFileName(relativePath), bytes);
            }
            catch (ExactFileSystemObjectOwnership.FileCreationCleanupException error)
            {
                if (error.RetainedReceipt is not null) scratch.Register(error.RetainedReceipt);
                throw;
            }
            scratch.Register(receipt);
            afterFileCaptured?.Invoke(receipt.Route);
            return receipt.Route;
        }

        ExactFileSystemObjectOwnership.DirectoryReceipt GetDirectory(string relativePath)
        {
            if (directories.TryGetValue(relativePath, out var known)) return known;
            var parent = GetDirectory(Path.GetDirectoryName(relativePath) ?? string.Empty);
            var created = ownership.TryCreateOnlyDirectory(parent.Route, Path.GetFileName(relativePath))
                ?? throw new IOException($"Build source capture directory already exists: {Path.Combine(parent.Route, Path.GetFileName(relativePath))}");
            directories.Add(relativePath, created);
            scratch.Register(created);
            return created;
        }

        void ReleaseFences()
        {
            if (fencesReleased) return;
            foreach (var directory in directories.Values) ownership.ReleaseCreationFence(directory);
            fencesReleased = true;
        }
    }

    private static string GetSafeRelativePath(string rootPath, string path)
    {
        var relativePath = Path.GetRelativePath(rootPath, path);
        if (Path.IsPathRooted(relativePath) || relativePath.Equals("..", StringComparison.Ordinal)
            || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
            throw new InvalidOperationException($"Build source snapshot entry resolves outside the snapshot directory: {path}");
        return relativePath;
    }

    private sealed record SnapshotSourceInventoryEntry(AdmittedVbaSource Source, string RelativeSourcePath, string? RelativeBinaryPath);
}

internal sealed class BuildSourceSnapshotCaptureRetainedException(
    Exception captureError, InvocationScratchCleanupEvidence cleanup)
    : InvalidOperationException(captureError.Message + " " + BuildSourceSnapshotCapture.DescribeCleanup(cleanup), captureError)
{
    internal Exception CaptureError => InnerException!;
    internal InvocationScratchCleanupEvidence CleanupEvidence => cleanup;
}

internal sealed class BuildSourceSnapshotCapture(
    ExactFileSystemObjectOwnership ownership,
    InvocationScratch scratch,
    string stagingPath,
    IReadOnlyList<VbaSourceFile> sourceFiles,
    AdmittedVbaSourceSet admission,
    string sourceRootPath) : IAdmittedWorkbookGenerationSourceInput
{
    private readonly object cleanupGate = new();
    private InvocationScratchCleanupEvidence? cleanupEvidence;
    private int disposed;

    public string StagingPath { get; } = stagingPath;
    public IReadOnlyList<VbaSourceFile> SourceFiles { get; } = sourceFiles;
    public AdmittedVbaSourceSet Admission { get; } = admission;
    internal string SourceRootPath { get; } = sourceRootPath;

    internal InvocationScratchCleanupEvidence Cleanup(Action<string>? onProofComplete = null)
    {
        lock (cleanupGate)
        {
            if (cleanupEvidence is not null) return cleanupEvidence;
            try { return cleanupEvidence = scratch.Cleanup(onProofComplete); }
            finally { ownership.Dispose(); }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        var cleanup = Cleanup();
        if (cleanup.Status != InvocationScratchCleanupStatus.Removed)
            throw new InvalidOperationException(DescribeCleanup(cleanup));
    }

    internal static string DescribeCleanup(InvocationScratchCleanupEvidence cleanup)
        => $"The build source snapshot staging directory could not be removed ({cleanup.Status}). Retained paths: {string.Join(", ", cleanup.RetainedPaths)}";
}
