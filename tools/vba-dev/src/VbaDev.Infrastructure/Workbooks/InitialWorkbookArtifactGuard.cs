using VbaDev.Infrastructure.FileSystem;
using System.ComponentModel;
using System.Security.Cryptography;
using VbaDev.App.FileSystem;
using VbaDev.App.Workbooks;
using VbaDev.Domain;

namespace VbaDev.Infrastructure.Workbooks;

internal sealed class InitialWorkbookStagingArtifact(
    string directoryPath,
    string workbookPath,
    ExactFileSystemObjectOwnership? ownership = null,
    ExactFileSystemObjectOwnership.DirectoryReceipt? directoryReceipt = null) : IDisposable
{
    internal string DirectoryPath { get; } = directoryPath;

    internal string WorkbookPath { get; } = workbookPath;

    internal ExactFileSystemObjectOwnership Ownership => ownership
        ?? throw new InvalidOperationException("The staging artifact has no ownership session.");

    internal ExactFileSystemObjectOwnership.DirectoryReceipt DirectoryReceipt => directoryReceipt
        ?? throw new InvalidOperationException("The staging artifact has no directory receipt.");

    internal ExactFileSystemObjectOwnership.StableFileCapture? SavedCapture { get; private set; }

    internal ExactFileSystemObjectOwnership.PendingFileCapture? PendingCapture { get; private set; }

    internal ExactFileSystemObjectOwnership.ObservationResult? CompletionObservation { get; set; }

    internal InitialWorkbookArtifactEvidence? Evidence { get; set; }

    internal void StorePendingCapture(ExactFileSystemObjectOwnership.PendingFileCapture pending)
    {
        if (PendingCapture is not null || SavedCapture is not null)
        {
            throw new InvalidOperationException("The saved staging workbook was already captured.");
        }

        PendingCapture = pending;
    }

    internal void StoreCapture(ExactFileSystemObjectOwnership.StableFileCapture capture)
    {
        if (SavedCapture is not null)
        {
            throw new InvalidOperationException("The saved staging workbook was already captured.");
        }

        SavedCapture = capture;
    }

    public void Dispose() => ownership?.Dispose();
}

internal sealed record InitialWorkbookMaterializedArtifact(
    InitialWorkbookArtifactEvidence Evidence,
    ExactFileSystemObjectOwnership.FileReceipt Receipt);

internal interface IInitialWorkbookArtifactGuard
{
    InitialWorkbookStagingArtifact CreateStagingArtifact();

    InitialWorkbookArtifactEvidence Capture(InitialWorkbookStagingArtifact staging);

    void CompleteCapture(InitialWorkbookStagingArtifact staging);

    InitialWorkbookMaterializedArtifact MaterializeCreateOnly(
        InitialWorkbookStagingArtifact staging,
        string workbookPath,
        ExactFileSystemObjectOwnership ownership,
        CancellationToken cancellationToken);

    InitialWorkbookArtifactCleanupResult TryDeleteStaging(InitialWorkbookStagingArtifact staging);

    InitialWorkbookArtifactCleanupResult TryDeleteFinalArtifact(
        ExactFileSystemObjectOwnership ownership,
        ExactFileSystemObjectOwnership.FileReceipt receipt);
}

internal interface IInitialWorkbookCopyObserver
{
    void OnDestinationCreated(string workbookPath);

    void OnBytesCopied(string workbookPath, long bytesCopied);

    void OnDestinationProved(string workbookPath)
    {
    }
}

internal interface IInitialWorkbookCleanupObserver
{
    void OnProofComplete(string path);
}

internal interface IInitialWorkbookStagingObserver
{
    void OnDirectoryCreated(string path);
}

internal sealed record InitialWorkbookArtifactCleanupResult(
    bool RemovedOrAbsent,
    bool TargetChanged,
    Exception? Failure)
{
    public static InitialWorkbookArtifactCleanupResult Removed()
        => new(RemovedOrAbsent: true, TargetChanged: false, Failure: null);

    public static InitialWorkbookArtifactCleanupResult Changed(Exception? failure = null)
        => new(RemovedOrAbsent: false, TargetChanged: true, failure);

    public static InitialWorkbookArtifactCleanupResult Failed(Exception failure)
        => new(RemovedOrAbsent: false, TargetChanged: false, failure);
}

internal sealed class InitialWorkbookArtifactGuard : IInitialWorkbookArtifactGuard
{
    private readonly IInitialWorkbookCopyObserver copyObserver;
    private readonly IInitialWorkbookCleanupObserver cleanupObserver;
    private readonly IInitialWorkbookStagingObserver stagingObserver;

    public InitialWorkbookArtifactGuard()
        : this(
            NoOpInitialWorkbookCopyObserver.Instance,
            NoOpInitialWorkbookCleanupObserver.Instance,
            NoOpInitialWorkbookStagingObserver.Instance)
    {
    }

    internal InitialWorkbookArtifactGuard(IInitialWorkbookCopyObserver copyObserver)
        : this(
            copyObserver,
            NoOpInitialWorkbookCleanupObserver.Instance,
            NoOpInitialWorkbookStagingObserver.Instance)
    {
    }

    internal InitialWorkbookArtifactGuard(IInitialWorkbookCleanupObserver cleanupObserver)
        : this(
            NoOpInitialWorkbookCopyObserver.Instance,
            cleanupObserver,
            NoOpInitialWorkbookStagingObserver.Instance)
    {
    }

    internal InitialWorkbookArtifactGuard(IInitialWorkbookStagingObserver stagingObserver)
        : this(
            NoOpInitialWorkbookCopyObserver.Instance,
            NoOpInitialWorkbookCleanupObserver.Instance,
            stagingObserver)
    {
    }

    internal InitialWorkbookArtifactGuard(
        IInitialWorkbookCopyObserver copyObserver,
        IInitialWorkbookCleanupObserver cleanupObserver)
        : this(
            copyObserver,
            cleanupObserver,
            NoOpInitialWorkbookStagingObserver.Instance)
    {
    }

    internal InitialWorkbookArtifactGuard(
        IInitialWorkbookCopyObserver copyObserver,
        IInitialWorkbookCleanupObserver cleanupObserver,
        IInitialWorkbookStagingObserver stagingObserver)
    {
        this.copyObserver = copyObserver;
        this.cleanupObserver = cleanupObserver;
        this.stagingObserver = stagingObserver;
    }

    public InitialWorkbookStagingArtifact CreateStagingArtifact()
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var ownership = new WindowsExactFileSystemObjectOwnershipFactory().Open();
            ExactFileSystemObjectOwnership.DirectoryReceipt? directory = null;
            try
            {
                directory = ownership.TryCreateOnlyDirectory(
                    Path.GetTempPath(),
                    $"vba-dev-new-{Guid.NewGuid():N}");
                if (directory is null)
                {
                    ownership.Dispose();
                    continue;
                }

                // The creation fence pins the directory while allowing Excel's
                // temporary-child write and SaveAs rename sequence.
                stagingObserver.OnDirectoryCreated(directory.Route);
                return new InitialWorkbookStagingArtifact(
                    directory.Route,
                    Path.Combine(directory.Route, "initial.xlsm"),
                    ownership,
                    directory);
            }
            catch (Exception creationFailure)
            {
                try
                {
                    if (directory is not null)
                    {
                        var cleanup = ownership.TryDeleteEmpty(directory);
                        if (!cleanup.Removed)
                        {
                            throw new InitialWorkbookArtifactRetainedException(
                                directory.Route,
                                expectedArtifact: null,
                                targetChanged: cleanup.Conclusive,
                                new AggregateException(
                                    creationFailure,
                                    new IOException(
                                        $"The private staging directory was retained: '{directory.Route}'.")));
                        }
                    }

                    throw;
                }
                finally
                {
                    ownership.Dispose();
                }
            }
        }

        throw new IOException(
            "A unique invocation-owned initial workbook staging directory could not be created.");
    }

    public InitialWorkbookArtifactEvidence Capture(InitialWorkbookStagingArtifact staging)
    {
        ArgumentNullException.ThrowIfNull(staging);
        if (staging.PendingCapture is not null || staging.SavedCapture is not null)
        {
            throw new InvalidOperationException("The saved staging workbook was already captured.");
        }

        var pending = staging.Ownership.CapturePendingSavedFile(staging.WorkbookPath);
        staging.StorePendingCapture(pending);
        return staging.Evidence = DescribeSavedBytes(pending.Route, pending.Bytes);
    }

    public void CompleteCapture(InitialWorkbookStagingArtifact staging)
    {
        ArgumentNullException.ThrowIfNull(staging);
        if (staging.PendingCapture is null)
        {
            return;
        }

        var completion = staging.Ownership.CompleteStableCapture(staging.PendingCapture);
        staging.CompletionObservation = completion.Observation;
        if (completion.Observation != ExactFileSystemObjectOwnership.ObservationResult.Unchanged)
        {
            throw StagingChanged(staging, completion.Observation);
        }

        staging.StoreCapture(completion.Capture!);
    }

    public InitialWorkbookMaterializedArtifact MaterializeCreateOnly(
        InitialWorkbookStagingArtifact staging,
        string workbookPath,
        ExactFileSystemObjectOwnership ownership,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(staging);
        ArgumentNullException.ThrowIfNull(ownership);
        cancellationToken.ThrowIfCancellationRequested();
        var capture = staging.SavedCapture
            ?? throw new InvalidOperationException("The saved staging workbook was not captured.");
        var stagingObservation = staging.Ownership.Observe(capture.Receipt);
        if (stagingObservation != ExactFileSystemObjectOwnership.ObservationResult.Unchanged)
        {
            throw StagingChanged(staging, stagingObservation);
        }

        var bytes = capture.Bytes;
        var absoluteWorkbookPath = Path.GetFullPath(workbookPath);
        ExactFileSystemObjectOwnership.FileReceipt? receipt = null;
        InitialWorkbookArtifactEvidence? destinationEvidence = null;
        try
        {
            receipt = ownership.CreateOnlyFile(
                Path.GetDirectoryName(absoluteWorkbookPath)!,
                Path.GetFileName(absoluteWorkbookPath),
                bytes,
                onFileCreated: copyObserver.OnDestinationCreated,
                onBytesWritten: copyObserver.OnBytesCopied,
                cancellationToken: cancellationToken);
            var observation = ownership.Observe(
                receipt,
                path =>
                {
                    destinationEvidence = DescribeSavedBytes(path, bytes);
                    copyObserver.OnDestinationProved(path);
                });
            if (observation != ExactFileSystemObjectOwnership.ObservationResult.Unchanged)
            {
                throw new InitialWorkbookArtifactRetainedException(
                    absoluteWorkbookPath,
                    destinationEvidence,
                    targetChanged: observation == ExactFileSystemObjectOwnership.ObservationResult.Changed,
                    new IOException(
                        $"The initial workbook destination could not be proved unchanged after materialization: '{absoluteWorkbookPath}'."));
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new InitialWorkbookMaterializedArtifact(destinationEvidence!, receipt);
        }
        catch (ExactFileSystemObjectOwnership.FileCreationCleanupException exception)
        {
            throw new InitialWorkbookArtifactRetainedException(
                exception.Route,
                expectedArtifact: null,
                targetChanged: exception.TargetChanged && !exception.RollbackUnproven,
                exception);
        }
        catch (Win32Exception exception) when (
            receipt is null && exception.NativeErrorCode is 80 or 183)
        {
            throw new InitialWorkbookArtifactRetainedException(
                absoluteWorkbookPath,
                expectedArtifact: null,
                targetChanged: true,
                exception);
        }
        catch (Exception exception)
        {
            if (receipt is null)
            {
                throw;
            }

            var cleanup = TryDeleteFinalArtifact(ownership, receipt);
            if (!cleanup.RemovedOrAbsent)
            {
                throw new InitialWorkbookArtifactRetainedException(
                    absoluteWorkbookPath,
                    destinationEvidence,
                    cleanup.TargetChanged,
                    new AggregateException(
                        exception,
                        cleanup.Failure ?? new IOException(
                            "The final workbook no longer names the unchanged receipt-owned artifact.")));
            }

            throw;
        }
    }

    public InitialWorkbookArtifactCleanupResult TryDeleteFinalArtifact(
        ExactFileSystemObjectOwnership ownership,
        ExactFileSystemObjectOwnership.FileReceipt receipt)
    {
        try
        {
            var cleanup = ownership.TryDelete(receipt, cleanupObserver.OnProofComplete);
            if (cleanup.Removed)
            {
                return InitialWorkbookArtifactCleanupResult.Removed();
            }

            var failure = new IOException(
                $"The receipt-owned initial workbook could not be safely removed: '{receipt.Route}'.");
            return ownership.Observe(receipt) == ExactFileSystemObjectOwnership.ObservationResult.Changed
                ? InitialWorkbookArtifactCleanupResult.Changed(failure)
                : InitialWorkbookArtifactCleanupResult.Failed(failure);
        }
        catch (Exception exception)
        {
            return InitialWorkbookArtifactCleanupResult.Failed(exception);
        }
    }

    public InitialWorkbookArtifactCleanupResult TryDeleteStaging(InitialWorkbookStagingArtifact staging)
    {
        ArgumentNullException.ThrowIfNull(staging);
        try
        {
            var ownership = staging.Ownership;
            var directoryObservation = ownership.Observe(staging.DirectoryReceipt);
            if (directoryObservation == ExactFileSystemObjectOwnership.ObservationResult.Changed)
            {
                return InitialWorkbookArtifactCleanupResult.Changed();
            }

            if (directoryObservation == ExactFileSystemObjectOwnership.ObservationResult.Inconclusive)
            {
                return InitialWorkbookArtifactCleanupResult.Failed(
                    new IOException($"The private staging directory could not be proved: '{staging.DirectoryPath}'."));
            }

            if (staging.SavedCapture is not null)
            {
                var fileCleanup = TryDeleteFinalArtifact(ownership, staging.SavedCapture.Receipt);
                if (!fileCleanup.RemovedOrAbsent)
                {
                    return fileCleanup;
                }
            }
            else if (staging.PendingCapture is not null)
            {
                var pendingFailure = new IOException(
                    $"The saved staging workbook has no completed receipt authority: '{staging.WorkbookPath}'.");
                return staging.CompletionObservation is
                    ExactFileSystemObjectOwnership.ObservationResult.Changed or
                    ExactFileSystemObjectOwnership.ObservationResult.Missing
                    ? InitialWorkbookArtifactCleanupResult.Changed(pendingFailure)
                    : InitialWorkbookArtifactCleanupResult.Failed(pendingFailure);
            }

            // No path capture is permitted here. Without a saved receipt an
            // existing workbook remains an unowned child and blocks deletion.
            var directoryCleanup = ownership.TryDeleteEmpty(
                staging.DirectoryReceipt,
                cleanupObserver.OnProofComplete);
            if (directoryCleanup.Removed)
            {
                return InitialWorkbookArtifactCleanupResult.Removed();
            }

            var failure = new IOException(
                $"The private staging directory and its retained children could not be removed: '{staging.DirectoryPath}'.");
            return directoryCleanup.Conclusive
                ? InitialWorkbookArtifactCleanupResult.Changed(failure)
                : InitialWorkbookArtifactCleanupResult.Failed(failure);
        }
        catch (Exception exception)
        {
            return InitialWorkbookArtifactCleanupResult.Failed(exception);
        }
        finally
        {
            staging.Dispose();
        }
    }

    private static InitialWorkbookArtifactEvidence DescribeSavedBytes(
        string path,
        byte[] bytes)
    {
        // Public identity is diagnostic compatibility data only. It never
        // creates or restores receipt authority; bytes come from the saved capture.
        var identity = new FileSystemPathIdentityResolver().Resolve(path).ObjectIdentity
            ?? throw new IOException($"The initial workbook diagnostic identity could not be read: '{path}'.");
        return new InitialWorkbookArtifactEvidence(
            path,
            identity,
            bytes.LongLength,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private static InitialWorkbookArtifactRetainedException StagingChanged(
        InitialWorkbookStagingArtifact staging,
        ExactFileSystemObjectOwnership.ObservationResult observation)
        => new(
            staging.WorkbookPath,
            staging.Evidence,
            targetChanged: observation is
                ExactFileSystemObjectOwnership.ObservationResult.Changed or
                ExactFileSystemObjectOwnership.ObservationResult.Missing,
            new IOException(
                $"The saved staging workbook could not be proved unchanged: '{staging.WorkbookPath}'."));

    private sealed class NoOpInitialWorkbookCopyObserver : IInitialWorkbookCopyObserver
    {
        public static NoOpInitialWorkbookCopyObserver Instance { get; } = new();

        public void OnDestinationCreated(string workbookPath)
        {
        }

        public void OnBytesCopied(string workbookPath, long bytesCopied)
        {
        }
    }

    private sealed class NoOpInitialWorkbookCleanupObserver
        : IInitialWorkbookCleanupObserver
    {
        public static NoOpInitialWorkbookCleanupObserver Instance { get; } = new();

        public void OnProofComplete(string path)
        {
        }
    }

    private sealed class NoOpInitialWorkbookStagingObserver
        : IInitialWorkbookStagingObserver
    {
        public static NoOpInitialWorkbookStagingObserver Instance { get; } = new();

        public void OnDirectoryCreated(string path)
        {
        }
    }
}
