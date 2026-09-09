using VbaDev.App.FileSystem;

namespace VbaDev.App.Build;

/// <summary>
/// Owns the exact disposable workbook copy and its explicitly observed saved version.
/// The workflow supplies process-release proof before selecting cleanup or commitment.
/// </summary>
internal sealed class WorkbookStagingArtifact : IDisposable
{
    private readonly ExactFileSystemObjectOwnership ownership;
    private readonly ExactFileSystemObjectOwnership.DirectoryReceipt? directory;
    private ExactFileSystemObjectOwnership.FileReceipt file;
    private ExactFileSystemObjectOwnership.PendingFileCapture? saved;
    private bool released;

    private WorkbookStagingArtifact(ExactFileSystemObjectOwnership ownership,
        ExactFileSystemObjectOwnership.DirectoryReceipt? directory,
        ExactFileSystemObjectOwnership.FileReceipt file)
    {
        this.ownership = ownership;
        this.directory = directory;
        this.file = file;
        Path = file.Route;
    }

    internal string Path { get; }
    internal InvocationScratchCleanupEvidence? CleanupEvidence { get; private set; }

    internal static WorkbookStagingArtifact CreateCopy(
        IExactFileSystemObjectOwnershipFactory ownershipFactory,
        string templatePath,
        string directoryPath,
        string fileName,
        bool createDirectory = false,
        Action<string>? afterCreated = null)
        => CreateFromBytes(ownershipFactory, File.ReadAllBytes(System.IO.Path.GetFullPath(templatePath)),
            directoryPath, fileName, createDirectory, afterCreated);

    internal static WorkbookStagingArtifact CreateFromBytes(
        IExactFileSystemObjectOwnershipFactory ownershipFactory,
        ReadOnlySpan<byte> bytes,
        string directoryPath,
        string fileName,
        bool createDirectory = false,
        Action<string>? afterCreated = null)
    {
        var absoluteDirectory = System.IO.Path.GetFullPath(directoryPath);
        var ownership = ownershipFactory.Open();
        ExactFileSystemObjectOwnership.DirectoryReceipt? directory = null;
        ExactFileSystemObjectOwnership.FileReceipt? file = null;
        var transferred = false;
        try
        {
            if (createDirectory)
            {
                var parent = System.IO.Path.GetDirectoryName(absoluteDirectory)!;
                Directory.CreateDirectory(parent);
                directory = ownership.TryCreateOnlyDirectory(parent, System.IO.Path.GetFileName(absoluteDirectory))
                    ?? throw new IOException($"Workbook staging directory already exists: {absoluteDirectory}");
            }
            else
            {
                // This is the workflow's persistent output container, not disposable scratch.
                Directory.CreateDirectory(absoluteDirectory);
            }
            file = directory is null
                ? ownership.CreateOnlyFile(absoluteDirectory, fileName, bytes)
                : ownership.CreateOnlyFile(directory, fileName, bytes);
            if (directory is not null) ownership.ReleaseCreationFence(directory);
            afterCreated?.Invoke(file.Route);
            var artifact = new WorkbookStagingArtifact(ownership, directory, file);
            transferred = true;
            return artifact;
        }
        catch (Exception error)
        {
            if (directory is not null) ownership.ReleaseCreationFence(directory);
            var scratch = new InvocationScratch(ownership);
            if (directory is not null) scratch.Register(directory);
            if (file is not null) scratch.Register(file);
            if (error is ExactFileSystemObjectOwnership.FileCreationCleanupException { RetainedReceipt: { } partial })
                scratch.Register(partial);
            var evidence = scratch.Cleanup();
            if (evidence.Status != InvocationScratchCleanupStatus.Removed)
                throw new WorkbookStagingPreparationException(error, evidence);
            throw;
        }
        finally
        {
            if (!transferred) ownership.Dispose();
        }
    }

    // Only the scenario's successful Save callback may select a new candidate version.
    internal void CaptureSavedWorkbook()
    {
        ObjectDisposedException.ThrowIf(released, this);
        try { saved = ownership.CapturePendingSavedFile(Path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            throw new BuildCommandException($"The saved staging workbook could not be read: {Path}", error);
        }
    }

    // Called after the runtime's exact process release, including a secondary cleanup failure.
    internal void CompleteSavedCapture()
    {
        ObjectDisposedException.ThrowIf(released, this);
        if (saved is null) return;
        var completion = ownership.CompleteStableCapture(saved);
        if (completion.Capture is null)
            throw new BuildCommandException($"Saved workbook staging ownership could not be proved ({completion.Observation}): {Path}");
        file = completion.Capture.Receipt;
        saved = null;
    }

    internal void ProveUnchanged()
    {
        ObjectDisposedException.ThrowIf(released, this);
        var observation = ownership.Observe(file);
        if (observation != ExactFileSystemObjectOwnership.ObservationResult.Unchanged)
            throw new BuildCommandException($"Workbook staging changed or could not be proved ({observation}): {Path}");
    }

    internal InvocationScratchCleanupEvidence Cleanup()
    {
        if (CleanupEvidence is not null) return CleanupEvidence;
        ObjectDisposedException.ThrowIf(released, this);
        var scratch = new InvocationScratch(ownership);
        scratch.Register(file);
        if (directory is not null) scratch.Register(directory);
        try { return CleanupEvidence = scratch.Cleanup(); }
        finally { ReleaseWithoutCleanup(); }
    }

    internal void ReleaseWithoutCleanup()
    {
        if (released) return;
        released = true;
        ownership.Dispose();
    }

    public void Dispose()
    {
        if (released) return;
        var evidence = Cleanup();
        if (evidence.Status != InvocationScratchCleanupStatus.Removed)
            throw new BuildCommandException(DescribeCleanup(evidence));
    }

    internal static string DescribeCleanup(InvocationScratchCleanupEvidence evidence)
        => $"Workbook staging could not be removed completely ({evidence.Status}). Retained absolute paths: {string.Join(", ", evidence.RetainedPaths)}";
}

internal sealed class WorkbookStagingPreparationException(Exception primary, InvocationScratchCleanupEvidence evidence)
    : InvalidOperationException(primary.Message + " " + WorkbookStagingArtifact.DescribeCleanup(evidence), primary)
{
    internal InvocationScratchCleanupEvidence CleanupEvidence => evidence;
}
