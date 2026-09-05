namespace VbaDev.App.FileSystem;

/// <summary>
/// Opens invocation-owned exact filesystem sessions supplied by Infrastructure.
/// </summary>
public interface IExactFileSystemObjectOwnershipFactory
{
    /// <summary>Opens one independent ownership session.</summary>
    ExactFileSystemObjectOwnership Open();
}

/// <summary>
/// Provides opaque ownership receipts without exposing native filesystem proof.
/// </summary>
public abstract class ExactFileSystemObjectOwnership : IDisposable
{
    /// <summary>Initializes an infrastructure-owned session.</summary>
    protected ExactFileSystemObjectOwnership()
    {
    }

    internal abstract DirectoryReceipt? TryCreateOnlyDirectory(string parentRoute, string childName);

    internal abstract FileReceipt CreateOnlyFile(
        DirectoryReceipt directory,
        string fileName,
        ReadOnlySpan<byte> content,
        Action<string>? onDirectoryProofComplete = null);

    internal abstract FileReceipt CreateOnlyFile(
        string parentRoute,
        string fileName,
        ReadOnlySpan<byte> content,
        Action<string>? onDirectoryProofComplete = null,
        Action<string>? onFileCreated = null,
        Action<string, long>? onBytesWritten = null,
        CancellationToken cancellationToken = default);

    internal abstract PendingFileCapture CapturePendingSavedFile(string route);

    internal abstract StableCaptureCompletion CompleteStableCapture(PendingFileCapture pending);

    internal abstract StableFileCapture CaptureTrustedStableFile(string route);

    internal abstract void ReleaseCreationFence(DirectoryReceipt directory);

    internal abstract ObservationResult Observe(FileReceipt receipt, Action<string>? onProofComplete = null);

    internal abstract ObservationResult Observe(DirectoryReceipt receipt);

    internal abstract DeletionResult TryDelete(
        FileReceipt receipt,
        Action<string>? onProofComplete = null,
        Action<string>? onDispositionStarting = null,
        Action<string>? onDispositionSet = null,
        Func<int, bool>? rollbackAttemptGate = null);

    internal abstract DeletionResult TryDeleteEmpty(
        DirectoryReceipt receipt,
        Action<string>? onProofComplete = null);

    /// <summary>Releases session resources without deleting caller paths.</summary>
    public abstract void Dispose();

    internal enum ObservationResult
    {
        Unchanged,
        Missing,
        Changed,
        Inconclusive
    }

    // These views expose presentation data only. Native proof and receipt
    // authority remain private to the issuing infrastructure implementation.
    internal interface IFileReceiptState
    {
        string Route { get; }
    }

    internal interface IDirectoryReceiptState
    {
        string Route { get; }
    }

    internal interface IPendingFileCaptureState
    {
        string Route { get; }
        byte[] Bytes { get; }
    }

    internal interface IStableFileCaptureState
    {
        FileReceipt Receipt { get; }
        byte[] Bytes { get; }
    }

    internal sealed class FileReceipt
    {
        internal FileReceipt(object provenState)
        {
            State = provenState as IFileReceiptState
                ?? throw new InvalidOperationException(
                    "A file receipt requires private proven ownership state.");
        }

        internal IFileReceiptState State { get; }
        internal string Route => State.Route;
    }

    internal sealed class DirectoryReceipt
    {
        internal DirectoryReceipt(object provenState)
        {
            State = provenState as IDirectoryReceiptState
                ?? throw new InvalidOperationException(
                    "A directory receipt requires private proven ownership state.");
        }

        internal IDirectoryReceiptState State { get; }
        internal string Route => State.Route;
    }

    internal sealed class PendingFileCapture
    {
        internal PendingFileCapture(object provenState)
        {
            State = provenState as IPendingFileCaptureState
                ?? throw new InvalidOperationException(
                    "A pending file capture requires private observed state.");
        }

        internal IPendingFileCaptureState State { get; }
        internal string Route => State.Route;
        internal byte[] Bytes => State.Bytes;
    }

    internal sealed class StableCaptureCompletion
    {
        private StableCaptureCompletion(ObservationResult observation, StableFileCapture? capture)
        {
            Observation = observation;
            Capture = capture;
        }

        internal ObservationResult Observation { get; }
        internal StableFileCapture? Capture { get; }

        internal static StableCaptureCompletion Completed(StableFileCapture capture)
        {
            ArgumentNullException.ThrowIfNull(capture);
            return new(ObservationResult.Unchanged, capture);
        }

        internal static StableCaptureCompletion Failed(ObservationResult observation)
            => observation is ObservationResult.Changed or ObservationResult.Missing or ObservationResult.Inconclusive
                ? new(observation, null)
                : throw new ArgumentOutOfRangeException(nameof(observation));
    }

    internal sealed class StableFileCapture
    {
        private readonly byte[] bytes;

        internal StableFileCapture(object provenState)
        {
            if (provenState is not IStableFileCaptureState proof)
            {
                throw new InvalidOperationException(
                    "A stable capture requires private proven ownership state.");
            }

            Receipt = proof.Receipt;
            bytes = proof.Bytes.ToArray();
        }

        internal FileReceipt Receipt { get; }
        internal byte[] Bytes => bytes.ToArray();
    }

    /// <summary>
    /// Reports one narrow exact-object deletion attempt without owning retry or
    /// workflow outcome policy.
    /// </summary>
    internal sealed class DeletionResult
    {
        private DeletionResult(
            bool removed,
            bool conclusive,
            IReadOnlyList<string> retainedPaths)
        {
            Removed = removed;
            Conclusive = conclusive;
            RetainedPaths = retainedPaths;
        }

        internal bool Removed { get; }

        internal bool Conclusive { get; }

        internal IReadOnlyList<string> RetainedPaths { get; }

        internal static DeletionResult RemovedResult()
            => new(true, true, []);

        internal static DeletionResult RetainedConclusive(string route)
            => RetainedConclusive([route]);

        internal static DeletionResult RetainedConclusive(IEnumerable<string> routes)
            => new(false, true, FreezeRoutes(routes));

        internal static DeletionResult RetainedInconclusive(string route)
            => new(false, false, [route]);

        private static IReadOnlyList<string> FreezeRoutes(IEnumerable<string> routes)
            => routes
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => path, StringComparer.Ordinal)
                .ToArray();
    }

    /// <summary>
    /// Preserves the original creation failure and exact-object cleanup evidence
    /// when deletion of a partially created file could not be proven.
    /// </summary>
    internal sealed class FileCreationCleanupException : IOException
    {
        internal FileCreationCleanupException(
            string route,
            FileReceipt? retainedReceipt,
            bool targetChanged,
            Exception originalFailure,
            Exception cleanupFailure)
            : base(
                $"Created file cleanup could not be proven for '{route}'. {originalFailure.Message} {cleanupFailure.Message}",
                new AggregateException(originalFailure, cleanupFailure))
        {
            Route = route;
            RetainedReceipt = retainedReceipt;
            TargetChanged = targetChanged;
            RollbackUnproven = cleanupFailure is RollbackException;
        }

        internal string Route { get; }
        internal FileReceipt? RetainedReceipt { get; }
        internal bool TargetChanged { get; }

        /// <summary>
        /// When true, retention is not proven even if TargetChanged is also
        /// true. Workflow consumers must preserve both independent facts.
        /// </summary>
        internal bool RollbackUnproven { get; }
    }

    /// <summary>
    /// Reports that a tentative same-handle delete disposition could not be
    /// proven rolled back and therefore cannot be represented as retention.
    /// </summary>
    internal sealed class RollbackException : Exception
    {
        internal RollbackException(string route)
            : base(
                "The exact ownership delete disposition could not be proven "
                + $"rolled back for '{route}'.")
        {
        }
    }

}
