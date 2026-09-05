using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace VbaDev.App.FileSystem;

/// <summary>
/// Issues invocation-scoped receipts for exact Windows filesystem objects and
/// authorizes only same-handle deletion of an unchanged receipt-owned object.
/// </summary>
internal sealed class ExactFileSystemObjectOwnership : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint SynchronizeAccess = 0x00100000;
    private const uint FileListDirectory = 0x00000001;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const int FileStandardInfoClass = 1;
    private const int FileDispositionInfoClass = 4;
    private const int FileAttributeTagInfoClass = 9;
    private const int FileIdInfoClass = 18;
    private const uint ObjectAttributesCaseInsensitive = 0x00000040;
    private const uint NtFileCreate = 2;
    private const uint NtFileDirectoryFile = 0x00000001;
    private const uint NtFileNonDirectoryFile = 0x00000040;
    private const uint NtFileSynchronousIoNonAlert = 0x00000020;
    private const uint NtFileOpenReparsePoint = 0x00200000;
    private const int StatusObjectNameCollision = unchecked((int)0xC0000035);
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorDirectoryNotEmpty = 145;
    private const int FileNamesInformationClass = 12;
    private const int StatusNoMoreFiles = unchecked((int)0x80000006);
    private const int HashBufferSize = 64 * 1024;
    private const int DirectoryQueryBufferSize = 64 * 1024;
    private const int DispositionRollbackAttempts = 2;

    private readonly HashSet<FileReceipt> fileReceipts = [];
    private readonly HashSet<DirectoryReceipt> directoryReceipts = [];
    private readonly HashSet<PendingFileCapture> pendingFileCaptures = [];
    private bool disposed;

    private ExactFileSystemObjectOwnership()
    {
    }

    /// <summary>
    /// Opens one Windows-only ownership session.
    /// </summary>
    internal static ExactFileSystemObjectOwnership Open()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Exact filesystem object ownership requires Windows file handles.");
        }

        return new ExactFileSystemObjectOwnership();
    }

    /// <summary>
    /// Creates one direct child directory with create-only authority and returns
    /// null when that child name already exists.
    /// </summary>
    internal DirectoryReceipt? TryCreateOnlyDirectory(
        string parentRoute,
        string childName)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(parentRoute);
        ValidateChildName(childName);

        var absoluteParentRoute = Path.GetFullPath(parentRoute);
        var route = Path.GetFullPath(Path.Combine(absoluteParentRoute, childName));
        using var parentHandle = OpenDirectory(
            absoluteParentRoute,
            FileListDirectory | FileReadAttributes | SynchronizeAccess,
            FileShareRead | FileShareWrite | FileShareDelete);
        var parentInformation = ReadObjectInformation(parentHandle, absoluteParentRoute);
        if (!parentInformation.IsDirectory || parentInformation.IsReparsePoint)
        {
            throw new IOException(
                $"The exact ownership parent must be an ordinary directory: '{absoluteParentRoute}'.");
        }

        var creationFence = CreateDirectoryRelative(parentHandle, childName, route);
        if (creationFence is null)
        {
            return null;
        }

        SafeFileHandle? anchor = null;
        try
        {
            var createdInformation = ReadObjectInformation(creationFence, route);
            RequireOrdinaryDirectory(createdInformation, route);

            using var routeHandle = OpenDirectory(
                route,
                FileReadAttributes | SynchronizeAccess,
                FileShareRead | FileShareWrite | FileShareDelete);
            var routeInformation = ReadObjectInformation(routeHandle, route);
            RequireOrdinaryDirectory(routeInformation, route);
            if (routeInformation.Identity != createdInformation.Identity)
            {
                throw new IOException(
                    $"The exact ownership directory route changed while it was created: '{route}'.");
            }

            anchor = OpenAnchor(route);
            var anchorInformation = ReadObjectInformation(anchor, route);
            RequireOrdinaryDirectory(anchorInformation, route);
            if (anchorInformation.Identity != createdInformation.Identity)
            {
                throw new IOException(
                    $"The exact ownership directory anchor changed while it was created: '{route}'.");
            }

            var receipt = new DirectoryReceipt(new DirectoryReceiptProof(
                this,
                route,
                createdInformation.Identity,
                creationFence,
                anchor));
            directoryReceipts.Add(receipt);
            return receipt;
        }
        catch
        {
            _ = TrySetDeleteDisposition(creationFence);
            anchor?.Dispose();
            creationFence.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates and flushes one ordinary file beneath a receipt-owned directory.
    /// </summary>
    internal FileReceipt CreateOnlyFile(
        DirectoryReceipt directory,
        string fileName,
        ReadOnlySpan<byte> content,
        Action<string>? onDirectoryProofComplete = null)
    {
        ThrowIfDisposed();
        RequireOwned(directory);
        ValidateChildName(fileName);

        var directoryAuthority = (IDirectoryReceiptAuthority)directory;
        var directoryFence = directoryAuthority.CreationFence
            ?? throw new InvalidOperationException(
                "Files can be created only while the directory creation fence is active.");
        var directoryInformation = ReadObjectInformation(directoryFence, directory.Route);
        if (!MatchesDirectoryReceipt(directory, directoryInformation))
        {
            throw new IOException(
                $"The exact ownership directory changed before file creation: '{directory.Route}'.");
        }

        onDirectoryProofComplete?.Invoke(directory.Route);
        return CreateOnlyFileCore(directoryFence, directory.Route, fileName, content);
    }

    /// <summary>
    /// Creates one owned child beneath a fixed ordinary parent without adopting
    /// ownership of the existing parent directory.
    /// </summary>
    internal FileReceipt CreateOnlyFile(
        string parentRoute,
        string fileName,
        ReadOnlySpan<byte> content,
        Action<string>? onDirectoryProofComplete = null,
        Action<string>? onFileCreated = null,
        Action<string, long>? onBytesWritten = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(parentRoute);
        ValidateChildName(fileName);
        var absoluteParentRoute = Path.GetFullPath(parentRoute);
        using var parentHandle = OpenDirectory(
            absoluteParentRoute,
            FileListDirectory | FileReadAttributes | SynchronizeAccess,
            FileShareRead | FileShareWrite | FileShareDelete);
        RequireOrdinaryDirectory(
            ReadObjectInformation(parentHandle, absoluteParentRoute),
            absoluteParentRoute);
        onDirectoryProofComplete?.Invoke(absoluteParentRoute);
        return CreateOnlyFileCore(
            parentHandle, absoluteParentRoute, fileName, content,
            onFileCreated, onBytesWritten, cancellationToken);
    }

    private FileReceipt CreateOnlyFileCore(
        SafeFileHandle directoryHandle,
        string directoryRoute,
        string fileName,
        ReadOnlySpan<byte> content,
        Action<string>? onFileCreated = null,
        Action<string, long>? onBytesWritten = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var route = Path.GetFullPath(Path.Combine(directoryRoute, fileName));
        using var handle = CreateFileRelative(
            directoryHandle,
            fileName,
            route);

        SafeFileHandle? anchor = null;
        ObjectIdentity? createdIdentity = null;
        try
        {
            var createdInformation = ReadObjectInformation(handle, route);
            createdIdentity = createdInformation.Identity;
            RequireOrdinarySingleLinkFile(createdInformation, expectedLength: 0, route);
            onFileCreated?.Invoke(route);
            var offset = 0;
            while (offset < content.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(HashBufferSize, content.Length - offset);
                RandomAccess.Write(handle, content.Slice(offset, count), offset);
                offset += count;
                onBytesWritten?.Invoke(route, offset);
            }

            cancellationToken.ThrowIfCancellationRequested();
            RandomAccess.FlushToDisk(handle);

            var expectedHash = SHA256.HashData(content);
            var beforeHash = ReadObjectInformation(handle, route);
            RequireOrdinarySingleLinkFile(beforeHash, content.Length, route);
            var actualHash = ComputeSha256(handle, beforeHash.Length);
            var afterHash = ReadObjectInformation(handle, route);
            RequireOrdinarySingleLinkFile(afterHash, content.Length, route);
            if (afterHash.Identity != beforeHash.Identity
                || !CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
            {
                throw new IOException(
                    $"The exact ownership file changed while it was created: '{route}'.");
            }

            anchor = OpenAnchor(route);
            var anchorInformation = ReadObjectInformation(anchor, route);
            if (!MatchesIssuedFile(
                    anchorInformation,
                    afterHash.Identity,
                    afterHash.Length))
            {
                throw new IOException(
                    $"The exact ownership file anchor changed while it was created: '{route}'.");
            }

            var receipt = new FileReceipt(new FileReceiptProof(
                this,
                route,
                afterHash.Identity,
                afterHash.Length,
                expectedHash,
                anchor));
            fileReceipts.Add(receipt);
            return receipt;
        }
        catch (Exception originalFailure)
        {
            anchor?.Dispose();
            var cleanupFailure = CleanupCreatedFile(
                handle, route, createdIdentity, content, originalFailure);
            if (cleanupFailure is not null)
            {
                throw cleanupFailure;
            }

            throw;
        }
    }

    private FileCreationCleanupException? CleanupCreatedFile(
        SafeFileHandle handle,
        string route,
        ObjectIdentity? createdIdentity,
        ReadOnlySpan<byte> content,
        Exception originalFailure)
    {
        FileReceipt? retainedReceipt = null;
        SafeFileHandle? partialAnchor = null;
        var targetChanged = false;
        try
        {
            if (createdIdentity is not { } identity)
            {
                throw new IOException("The created file identity could not be proven.");
            }

            var information = ReadObjectInformation(handle, route);
            if (information.Length < 0 || information.Length > content.Length
                || !MatchesIssuedFile(information, identity, information.Length))
            {
                targetChanged = true;
                throw new IOException("The created file changed before partial-copy cleanup.");
            }

            var partialHash = SHA256.HashData(content[..(int)information.Length]);
            if (!MatchesFixedFileState(
                    handle, route, identity, information.Length, partialHash,
                    expectedDeletePending: false, static links => links == 1))
            {
                targetChanged = true;
                throw new IOException("The partial file no longer matches the bytes created by this invocation.");
            }

            // A moved parent can make the diagnostic route unavailable. The
            // fixed create-only handle still owns its child, but no route-based
            // receipt may be issued unless its anchor is independently proven.
            try
            {
                partialAnchor = OpenAnchor(route);
                if (MatchesIssuedFile(
                        ReadObjectInformation(partialAnchor, route), identity, information.Length))
                {
                    retainedReceipt = new FileReceipt(new FileReceiptProof(
                        this, route, identity, information.Length, partialHash, partialAnchor));
                    fileReceipts.Add(retainedReceipt);
                    partialAnchor = null;
                }
                else
                {
                    targetChanged = true;
                }
            }
            catch (Exception exception) when (IsObservationFailure(exception))
            {
                // Retention can be reported without adopting an unproven route.
            }

            if (!MatchesFixedFileState(
                    handle, route, identity, information.Length, partialHash,
                    expectedDeletePending: false, static links => links == 1))
            {
                targetChanged = true;
                throw new IOException("The partial file changed immediately before cleanup.");
            }

            if (!TrySetDeleteDisposition(handle))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            var deleteAuthorized = false;
            try
            {
                deleteAuthorized = MatchesFixedFileState(
                    handle, route, identity, information.Length, partialHash,
                    expectedDeletePending: true, static links => links == 0);
                targetChanged |= !deleteAuthorized;
            }
            finally
            {
                if (!deleteAuthorized)
                {
                    EnsureCreatedFileDispositionCleared(handle, route, identity);
                }
            }

            if (!deleteAuthorized)
            {
                targetChanged = true;
                throw new IOException("The partial file changed while cleanup was authorized.");
            }

            if (retainedReceipt is not null)
            {
                Retire(retainedReceipt);
            }

            return null;
        }
        catch (Exception cleanupFailure) when (
            cleanupFailure is RollbackException || IsObservationFailure(cleanupFailure))
        {
            return new FileCreationCleanupException(
                route, retainedReceipt, targetChanged,
                originalFailure, cleanupFailure);
        }
        finally
        {
            partialAnchor?.Dispose();
        }
    }

    private static bool MatchesFixedFileState(
        SafeFileHandle handle,
        string route,
        ObjectIdentity identity,
        long length,
        byte[] expectedHash,
        bool expectedDeletePending,
        Func<uint, bool> linksMatch)
    {
        bool Matches(ObjectInformation information)
            => !information.IsDirectory
               && !information.IsReparsePoint
               && information.Identity == identity
               && information.Length == length
               && information.DeletePending == expectedDeletePending
               && linksMatch(information.NumberOfLinks);

        if (!Matches(ReadObjectInformation(handle, route)))
        {
            return false;
        }

        var actualHash = ComputeSha256(handle, length);
        return Matches(ReadObjectInformation(handle, route))
               && CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
    }

    private static void EnsureCreatedFileDispositionCleared(
        SafeFileHandle handle, string route, ObjectIdentity identity)
    {
        for (var attempt = 0; attempt < DispositionRollbackAttempts; attempt++)
        {
            try
            {
                _ = TrySetDeleteDisposition(handle, delete: false);
                var information = ReadObjectInformation(handle, route);
                if (!information.IsDirectory && !information.IsReparsePoint
                    && information.Identity == identity && !information.DeletePending
                    && information.NumberOfLinks > 0)
                {
                    return;
                }
            }
            catch (Exception exception) when (IsObservationFailure(exception))
            {
                // Retry the rollback and its postcondition as one bounded operation.
            }
        }

        throw new RollbackException(route);
    }

    /// <summary>
    /// Observes saved producer bytes without issuing mutation authority. The
    /// pending read handle remains alive until completion or session disposal.
    /// </summary>
    internal PendingFileCapture CapturePendingSavedFile(string route)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        var absoluteRoute = Path.GetFullPath(route);
        var handle = OpenFileForObservation(
            absoluteRoute, GenericRead, FileShareRead | FileShareWrite);
        try
        {
            var information = ReadObjectInformation(handle, absoluteRoute);
            RequireOrdinarySingleLinkFile(information, expectedLength: null, absoluteRoute);
            var bytes = ReadExactBytes(handle, information.Length, absoluteRoute);
            var hash = SHA256.HashData(bytes);
            if (!MatchesFixedFileState(
                    handle, absoluteRoute, information.Identity, bytes.LongLength, hash,
                    expectedDeletePending: false, static links => links == 1))
            {
                throw new IOException($"The saved producer file changed during capture: '{absoluteRoute}'.");
            }

            var pending = new PendingFileCapture(new PendingFileCaptureProof(
                this, absoluteRoute, information.Identity, bytes, hash, handle));
            pendingFileCaptures.Add(pending);
            return pending;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Issues a receipt only after a strict handle proves the saved observation
    /// unchanged. A failed completion grants no authority and keeps the pending
    /// observation alive; a successful completion consumes it exactly once.
    /// </summary>
    internal StableCaptureCompletion CompleteStableCapture(PendingFileCapture pending)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(pending);
        var state = ((IPendingFileCaptureState)pending).State;
        if (!ReferenceEquals(state.Owner, this) || !pendingFileCaptures.Contains(pending))
        {
            throw new ArgumentException("The pending capture is not active in this ownership session.", nameof(pending));
        }

        SafeFileHandle? anchor = null;
        try
        {
            var information = ReadObjectInformation(state.Handle, state.Route);
            if (information.IsDirectory || information.IsReparsePoint
                || information.Identity != state.Identity || information.Length != state.Bytes.LongLength
                || information.NumberOfLinks > 1)
            {
                return StableCaptureCompletion.Failed(ObservationResult.Changed);
            }

            if (information.DeletePending || information.NumberOfLinks == 0)
            {
                return CompleteMissingPendingCapture(state);
            }

            using var handle = OpenFileForObservation(state.Route, GenericRead, FileShareRead);
            if (!MatchesFixedFileState(
                    handle, state.Route, state.Identity, state.Bytes.LongLength, state.Sha256,
                    expectedDeletePending: false, static links => links == 1)
                || !MatchesIssuedFile(
                    ReadObjectInformation(state.Handle, state.Route), state.Identity, state.Bytes.LongLength))
            {
                return StableCaptureCompletion.Failed(ObservationResult.Changed);
            }

            // The producer observation's deny-delete handle cannot become the
            // receipt anchor: retaining it would prevent future exact cleanup.
            anchor = OpenAnchor(state.Route);
            if (!MatchesIssuedFile(
                    ReadObjectInformation(anchor, state.Route), state.Identity, state.Bytes.LongLength))
            {
                return StableCaptureCompletion.Failed(ObservationResult.Changed);
            }

            var receipt = new FileReceipt(new FileReceiptProof(
                this, state.Route, state.Identity, state.Bytes.LongLength, state.Sha256, anchor));
            var capture = new StableFileCapture(new StableFileCaptureProof(receipt, state.Bytes));
            fileReceipts.Add(receipt);
            anchor = null;
            pendingFileCaptures.Remove(pending);
            state.Handle.Dispose();
            return StableCaptureCompletion.Completed(capture);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is ErrorFileNotFound or ErrorPathNotFound)
        {
            return CompleteMissingPendingCapture(state);
        }
        catch (FileNotFoundException)
        {
            return CompleteMissingPendingCapture(state);
        }
        catch (DirectoryNotFoundException)
        {
            return CompleteMissingPendingCapture(state);
        }
        catch (Exception exception) when (IsObservationFailure(exception))
        {
            return StableCaptureCompletion.Failed(ObservationResult.Inconclusive);
        }
        finally
        {
            anchor?.Dispose();
        }
    }

    private static StableCaptureCompletion CompleteMissingPendingCapture(PendingFileCaptureProof state)
    {
        try
        {
            var routeState = ObserveRouteState(state.Route);
            if (routeState != RouteState.Absent)
            {
                return StableCaptureCompletion.Failed(routeState == RouteState.Present
                    ? ObservationResult.Changed : ObservationResult.Inconclusive);
            }

            var information = ReadObjectInformation(state.Handle, state.Route);
            return StableCaptureCompletion.Failed(
                !information.IsDirectory && !information.IsReparsePoint
                && information.Identity == state.Identity && information.Length == state.Bytes.LongLength
                && information.NumberOfLinks == 0
                    ? ObservationResult.Missing : ObservationResult.Changed);
        }
        catch (Exception exception) when (IsObservationFailure(exception))
        {
            return StableCaptureCompletion.Failed(ObservationResult.Inconclusive);
        }
    }

    /// <summary>
    /// Captures one stable ordinary single-link file and issues a receipt from
    /// the bytes and native identity observed through the same fixed handle.
    /// </summary>
    internal StableFileCapture CaptureTrustedStableFile(string route)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        var absoluteRoute = Path.GetFullPath(route);

        using var handle = OpenFileForObservation(
            absoluteRoute,
            GenericRead,
            FileShareRead);
        SafeFileHandle? anchor = null;
        try
        {
            var beforeRead = ReadObjectInformation(handle, absoluteRoute);
            RequireOrdinarySingleLinkFile(beforeRead, expectedLength: null, absoluteRoute);
            var bytes = ReadExactBytes(handle, beforeRead.Length, absoluteRoute);
            var hash = SHA256.HashData(bytes);
            var afterRead = ReadObjectInformation(handle, absoluteRoute);
            RequireOrdinarySingleLinkFile(afterRead, bytes.LongLength, absoluteRoute);
            if (afterRead.Identity != beforeRead.Identity)
            {
                throw new IOException(
                    $"The exact ownership file changed during stable capture: '{absoluteRoute}'.");
            }

            anchor = OpenAnchor(absoluteRoute);
            var anchorInformation = ReadObjectInformation(anchor, absoluteRoute);
            if (!MatchesIssuedFile(
                    anchorInformation,
                    afterRead.Identity,
                    afterRead.Length))
            {
                throw new IOException(
                    $"The exact ownership file anchor changed during stable capture: '{absoluteRoute}'.");
            }

            var receipt = new FileReceipt(new FileReceiptProof(
                this,
                absoluteRoute,
                afterRead.Identity,
                afterRead.Length,
                hash,
                anchor));
            fileReceipts.Add(receipt);
            return new StableFileCapture(new StableFileCaptureProof(receipt, bytes));
        }
        catch
        {
            anchor?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Releases the create-only directory handle that fences its route during
    /// construction. The receipt remains valid for later cleanup.
    /// </summary>
    internal void ReleaseCreationFence(DirectoryReceipt directory)
    {
        ThrowIfDisposed();
        RequireOwned(directory);
        ReleaseCreationFenceCore(directory);
    }

    internal enum ObservationResult
    {
        Unchanged,
        Missing,
        Changed,
        Inconclusive
    }

    /// <summary>
    /// Observes the exact ordinary single-link file without retiring its receipt.
    /// </summary>
    internal ObservationResult Observe(FileReceipt receipt, Action<string>? onProofComplete = null)
    {
        ThrowIfDisposed();
        RequireOwned(receipt);
        var invokingCallback = false;
        try
        {
            var anchorInformation = ReadObjectInformation(
                ((IFileReceiptAuthority)receipt).Anchor!, receipt.Route);
            if (!MatchesAnchoredFile(receipt, anchorInformation) || anchorInformation.NumberOfLinks > 1)
            {
                return ObservationResult.Changed;
            }

            if (anchorInformation.DeletePending || anchorInformation.NumberOfLinks == 0)
            {
                return ObserveMissing(receipt);
            }

            using var handle = OpenFileForObservation(receipt.Route, GenericRead, FileShareRead);
            if (!MatchesFileReceipt(handle, receipt))
            {
                return ObservationResult.Changed;
            }

            invokingCallback = true;
            onProofComplete?.Invoke(receipt.Route);
            invokingCallback = false;
            return MatchesFileReceipt(handle, receipt)
                ? ObservationResult.Unchanged
                : ObservationResult.Changed;
        }
        catch (Win32Exception exception) when (
            !invokingCallback && exception.NativeErrorCode is ErrorFileNotFound or ErrorPathNotFound)
        {
            return ObserveMissing(receipt);
        }
        catch (FileNotFoundException) when (!invokingCallback)
        {
            return ObserveMissing(receipt);
        }
        catch (DirectoryNotFoundException) when (!invokingCallback)
        {
            return ObserveMissing(receipt);
        }
        catch (Exception exception) when (!invokingCallback && IsObservationFailure(exception))
        {
            return ObservationResult.Inconclusive;
        }
    }

    /// <summary>
    /// Observes directory identity, not emptiness, without retiring the receipt
    /// or releasing its construction fence.
    /// </summary>
    internal ObservationResult Observe(DirectoryReceipt receipt)
    {
        ThrowIfDisposed();
        RequireOwned(receipt);
        try
        {
            var anchorInformation = ReadObjectInformation(
                ((IDirectoryReceiptAuthority)receipt).Anchor!, receipt.Route);
            if (!MatchesAnchoredDirectory(receipt, anchorInformation))
            {
                return ObservationResult.Changed;
            }

            if (anchorInformation.DeletePending || anchorInformation.NumberOfLinks == 0)
            {
                return ObserveMissing(receipt);
            }

            using var handle = OpenDirectory(
                receipt.Route,
                FileReadAttributes | SynchronizeAccess,
                FileShareRead | FileShareWrite | FileShareDelete);
            return MatchesDirectoryReceipt(receipt, handle)
                ? ObservationResult.Unchanged
                : ObservationResult.Changed;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is ErrorFileNotFound or ErrorPathNotFound)
        {
            return ObserveMissing(receipt);
        }
        catch (DirectoryNotFoundException)
        {
            return ObserveMissing(receipt);
        }
        catch (Exception exception) when (IsObservationFailure(exception))
        {
            return ObservationResult.Inconclusive;
        }
    }

    /// <summary>
    /// Deletes only the unchanged ordinary single-link file represented by the
    /// supplied receipt.
    /// </summary>
    internal DeletionResult TryDelete(
        FileReceipt receipt,
        Action<string>? onProofComplete = null,
        Action<string>? onDispositionStarting = null,
        Action<string>? onDispositionSet = null,
        Func<int, bool>? rollbackAttemptGate = null)
    {
        ThrowIfDisposed();
        RequireOwned(receipt);

        try
        {
            var authority = (IFileReceiptAuthority)receipt;
            var anchorInformation = ReadObjectInformation(
                authority.Anchor!,
                receipt.Route);
            if (!MatchesAnchoredFile(receipt, anchorInformation)
                || anchorInformation.NumberOfLinks > 1)
            {
                return DeletionResult.RetainedConclusive(receipt.Route);
            }

            if (anchorInformation.DeletePending
                || anchorInformation.NumberOfLinks == 0)
            {
                return ClassifyMissing(receipt);
            }

            using var handle = OpenFileForObservation(
                receipt.Route,
                GenericRead | DeleteAccess,
                shareMode: 0);
            if (!MatchesFileReceipt(handle, receipt))
            {
                return DeletionResult.RetainedConclusive(receipt.Route);
            }

            onProofComplete?.Invoke(receipt.Route);
            if (!MatchesFileReceipt(handle, receipt))
            {
                return DeletionResult.RetainedConclusive(receipt.Route);
            }

            onDispositionStarting?.Invoke(receipt.Route);
            if (!TrySetDeleteDisposition(handle))
            {
                return DeletionResult.RetainedInconclusive(receipt.Route);
            }

            var deleteAuthorized = false;
            try
            {
                onDispositionSet?.Invoke(receipt.Route);
                if (MatchesFileReceiptState(
                        handle,
                        receipt,
                        expectedDeletePending: true,
                        static links => links == 0))
                {
                    deleteAuthorized = true;
                    Retire(receipt);
                    return DeletionResult.RemovedResult();
                }
            }
            finally
            {
                if (!deleteAuthorized)
                {
                    EnsureFileDispositionCleared(
                        handle,
                        receipt,
                        rollbackAttemptGate);
                }
            }

            return DeletionResult.RetainedConclusive(receipt.Route);
        }
        catch (Win32Exception exception) when (
            exception.NativeErrorCode is ErrorFileNotFound or ErrorPathNotFound)
        {
            return ClassifyMissing(receipt);
        }
        catch (FileNotFoundException)
        {
            return ClassifyMissing(receipt);
        }
        catch (DirectoryNotFoundException)
        {
            return ClassifyMissing(receipt);
        }
        catch (Exception exception) when (
            exception is not RollbackException
            && IsObservationFailure(exception))
        {
            return DeletionResult.RetainedInconclusive(receipt.Route);
        }
    }

    /// <summary>
    /// Deletes only the unchanged, empty ordinary directory represented by the
    /// supplied receipt.
    /// </summary>
    internal DeletionResult TryDeleteEmpty(
        DirectoryReceipt receipt,
        Action<string>? onProofComplete = null)
    {
        ThrowIfDisposed();
        RequireOwned(receipt);
        ReleaseCreationFenceCore(receipt);

        try
        {
            var authority = (IDirectoryReceiptAuthority)receipt;
            var anchorInformation = ReadObjectInformation(
                authority.Anchor!,
                receipt.Route);
            if (!MatchesAnchoredDirectory(receipt, anchorInformation))
            {
                return DeletionResult.RetainedConclusive(receipt.Route);
            }

            if (anchorInformation.DeletePending
                || anchorInformation.NumberOfLinks == 0)
            {
                return ClassifyMissing(receipt);
            }

            using var handle = OpenDirectory(
                receipt.Route,
                FileListDirectory | FileReadAttributes | DeleteAccess | SynchronizeAccess,
                shareMode: 0);
            if (!MatchesDirectoryReceipt(receipt, handle))
            {
                return DeletionResult.RetainedConclusive(receipt.Route);
            }

            var entries = ReadDirectoryEntries(handle, receipt.Route);
            if (entries.Count > 0)
            {
                return DeletionResult.RetainedConclusive(
                    entries.Append(receipt.Route));
            }

            onProofComplete?.Invoke(receipt.Route);
            if (!MatchesDirectoryReceipt(receipt, handle))
            {
                return DeletionResult.RetainedConclusive(receipt.Route);
            }

            entries = ReadDirectoryEntries(handle, receipt.Route);
            if (entries.Count > 0)
            {
                return DeletionResult.RetainedConclusive(
                    entries.Append(receipt.Route));
            }

            if (TrySetDeleteDisposition(handle))
            {
                Retire(receipt);
                return DeletionResult.RemovedResult();
            }

            var deletionError = Marshal.GetLastWin32Error();
            return deletionError == ErrorDirectoryNotEmpty
                ? DeletionResult.RetainedConclusive(
                    ReadRetainedDirectoryEntries(handle, receipt.Route))
                : DeletionResult.RetainedInconclusive(receipt.Route);
        }
        catch (Win32Exception exception) when (
            exception.NativeErrorCode is ErrorFileNotFound or ErrorPathNotFound)
        {
            return ClassifyMissing(receipt);
        }
        catch (DirectoryNotFoundException)
        {
            return ClassifyMissing(receipt);
        }
        catch (Exception exception) when (IsObservationFailure(exception))
        {
            return DeletionResult.RetainedInconclusive(receipt.Route);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        try
        {
            foreach (var receipt in fileReceipts.ToArray())
            {
                Retire(receipt);
            }

            foreach (var receipt in directoryReceipts.ToArray())
            {
                Retire(receipt);
            }

            foreach (var pending in pendingFileCaptures)
            {
                ((IPendingFileCaptureState)pending).State.Handle.Dispose();
            }
        }
        finally
        {
            disposed = true;
            fileReceipts.Clear();
            directoryReceipts.Clear();
            pendingFileCaptures.Clear();
        }
    }

    private static SafeFileHandle? CreateDirectoryRelative(
        SafeFileHandle parentHandle,
        string childName,
        string route)
    {
        var nameBuffer = Marshal.StringToHGlobalUni(childName);
        var unicodeStringBuffer = IntPtr.Zero;
        try
        {
            var nameBytes = checked((ushort)(childName.Length * sizeof(char)));
            var unicodeString = new UnicodeString
            {
                Length = nameBytes,
                MaximumLength = checked((ushort)(nameBytes + sizeof(char))),
                Buffer = nameBuffer
            };
            unicodeStringBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(unicodeString, unicodeStringBuffer, fDeleteOld: false);
            var objectAttributes = new ObjectAttributes
            {
                Length = (uint)Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parentHandle.DangerousGetHandle(),
                ObjectName = unicodeStringBuffer,
                Attributes = ObjectAttributesCaseInsensitive
            };
            var status = NtCreateFile(
                out var handle,
                FileListDirectory | FileReadAttributes | DeleteAccess | SynchronizeAccess,
                ref objectAttributes,
                out _,
                IntPtr.Zero,
                FileAttributeNormal,
                FileShareRead | FileShareWrite,
                NtFileCreate,
                NtFileDirectoryFile | NtFileSynchronousIoNonAlert | NtFileOpenReparsePoint,
                IntPtr.Zero,
                0);
            if (status >= 0 && !handle.IsInvalid)
            {
                return handle;
            }

            handle.Dispose();
            if (status == StatusObjectNameCollision)
            {
                return null;
            }

            throw new Win32Exception(
                unchecked((int)RtlNtStatusToDosError(status)),
                $"The exact ownership directory could not be created: '{route}'.");
        }
        finally
        {
            if (unicodeStringBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(unicodeStringBuffer);
            }

            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    private static SafeFileHandle CreateFileRelative(
        SafeFileHandle directoryHandle,
        string fileName,
        string route)
    {
        var nameBuffer = Marshal.StringToHGlobalUni(fileName);
        var unicodeStringBuffer = IntPtr.Zero;
        try
        {
            var nameBytes = checked((ushort)(fileName.Length * sizeof(char)));
            var unicodeString = new UnicodeString
            {
                Length = nameBytes,
                MaximumLength = checked((ushort)(nameBytes + sizeof(char))),
                Buffer = nameBuffer
            };
            unicodeStringBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(unicodeString, unicodeStringBuffer, fDeleteOld: false);
            var objectAttributes = new ObjectAttributes
            {
                Length = (uint)Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = directoryHandle.DangerousGetHandle(),
                ObjectName = unicodeStringBuffer,
                Attributes = ObjectAttributesCaseInsensitive
            };
            var status = NtCreateFile(
                out var handle,
                GenericRead | GenericWrite | DeleteAccess | SynchronizeAccess,
                ref objectAttributes,
                out _,
                IntPtr.Zero,
                FileAttributeNormal,
                FileShareRead,
                NtFileCreate,
                NtFileNonDirectoryFile
                | NtFileSynchronousIoNonAlert
                | NtFileOpenReparsePoint,
                IntPtr.Zero,
                0);
            if (status >= 0 && !handle.IsInvalid)
            {
                return handle;
            }

            handle.Dispose();
            throw new Win32Exception(
                unchecked((int)RtlNtStatusToDosError(status)),
                $"The exact ownership file could not be created: '{route}'.");
        }
        finally
        {
            if (unicodeStringBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(unicodeStringBuffer);
            }

            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    private static SafeFileHandle OpenFileForObservation(
        string route,
        uint desiredAccess,
        uint shareMode)
    {
        var handle = CreateFile(
            route,
            desiredAccess,
            shareMode,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal
            | FileFlagSequentialScan
            | FileFlagBackupSemantics
            | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        return !handle.IsInvalid
            ? handle
            : throw CreateFileOpenException(handle, route, "could not be opened safely");
    }

    private static SafeFileHandle OpenDirectory(
        string route,
        uint desiredAccess,
        uint shareMode)
    {
        var handle = CreateFile(
            route,
            desiredAccess,
            shareMode,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        return !handle.IsInvalid
            ? handle
            : throw CreateFileOpenException(handle, route, "directory could not be opened safely");
    }

    private static SafeFileHandle OpenAnchor(string route)
    {
        var handle = CreateFile(
            route,
            desiredAccess: 0,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        return !handle.IsInvalid
            ? handle
            : throw CreateFileOpenException(handle, route, "could not be anchored safely");
    }

    private static Win32Exception CreateFileOpenException(
        SafeFileHandle handle,
        string route,
        string action)
    {
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        return new Win32Exception(
            error,
            $"The exact ownership route {action}: '{route}'.");
    }

    private static ObjectInformation ReadObjectInformation(
        SafeFileHandle handle,
        string route)
    {
        if (!GetFileIdInformation(
                handle,
                FileIdInfoClass,
                out var fileId,
                (uint)Marshal.SizeOf<FileIdInformation>()))
        {
            throw IdentityReadFailure(route);
        }

        if (!GetFileStandardInformation(
                handle,
                FileStandardInfoClass,
                out var standard,
                (uint)Marshal.SizeOf<FileStandardInformation>()))
        {
            throw IdentityReadFailure(route);
        }

        if (!GetFileAttributeTagInformation(
                handle,
                FileAttributeTagInfoClass,
                out var attributes,
                (uint)Marshal.SizeOf<FileAttributeTagInformation>()))
        {
            throw IdentityReadFailure(route);
        }

        if ((fileId.FileId.Low == 0 && fileId.FileId.High == 0)
            || (fileId.FileId.Low == ulong.MaxValue
                && fileId.FileId.High == ulong.MaxValue))
        {
            throw new IOException(
                $"The exact ownership identity is unsupported for proof: '{route}'.");
        }

        return new ObjectInformation(
            new ObjectIdentity(
                fileId.VolumeSerialNumber,
                fileId.FileId.Low,
                fileId.FileId.High),
            standard.EndOfFile,
            standard.NumberOfLinks,
            standard.DeletePending != 0,
            standard.Directory != 0,
            (attributes.FileAttributes & FileAttributeReparsePoint) != 0);
    }

    private static Win32Exception IdentityReadFailure(string route)
        => new(
            Marshal.GetLastWin32Error(),
            $"The exact ownership identity could not be read: '{route}'.");

    private static void RequireOrdinaryDirectory(
        ObjectInformation information,
        string route)
    {
        if (!information.IsDirectory
            || information.IsReparsePoint
            || information.DeletePending)
        {
            throw new IOException(
                $"The exact ownership route is not an ordinary directory: '{route}'.");
        }
    }

    private static void RequireOrdinarySingleLinkFile(
        ObjectInformation information,
        long? expectedLength,
        string route)
    {
        if (information.IsDirectory
            || information.IsReparsePoint
            || information.DeletePending
            || information.NumberOfLinks != 1
            || (expectedLength is not null && information.Length != expectedLength.Value))
        {
            throw new IOException(
                $"The exact ownership route is not an unchanged ordinary single-link file: '{route}'.");
        }
    }

    private static bool MatchesDirectoryReceipt(
        DirectoryReceipt receipt,
        ObjectInformation information)
    {
        if (!MatchesLiveDirectory(receipt, information))
        {
            return false;
        }

        var authority = (IDirectoryReceiptAuthority)receipt;
        var anchorInformation = ReadObjectInformation(
            authority.Anchor!,
            receipt.Route);
        return MatchesLiveDirectory(receipt, anchorInformation);
    }

    private static bool MatchesDirectoryReceipt(
        DirectoryReceipt receipt,
        SafeFileHandle routeHandle)
        => MatchesDirectoryReceipt(
            receipt,
            ReadObjectInformation(routeHandle, receipt.Route));

    private static bool MatchesAnchoredDirectory(
        DirectoryReceipt receipt,
        ObjectInformation information)
    {
        var authority = (IDirectoryReceiptAuthority)receipt;
        return information.IsDirectory
               && !information.IsReparsePoint
               && information.Identity == authority.Identity;
    }

    private static bool MatchesLiveDirectory(
        DirectoryReceipt receipt,
        ObjectInformation information)
        => MatchesAnchoredDirectory(receipt, information)
           && !information.DeletePending
           && information.NumberOfLinks > 0;

    private static bool MatchesFileReceipt(
        SafeFileHandle handle,
        FileReceipt receipt)
        => MatchesFileReceiptState(
            handle,
            receipt,
            expectedDeletePending: false,
            static links => links == 1);

    private static bool MatchesFileReceiptState(
        SafeFileHandle handle,
        FileReceipt receipt,
        bool expectedDeletePending,
        Func<uint, bool> linksMatch)
    {
        var authority = (IFileReceiptAuthority)receipt;
        var anchorBeforeHash = ReadObjectInformation(
            authority.Anchor!,
            receipt.Route);
        if (!MatchesFileReceiptStateInformation(
                receipt,
                anchorBeforeHash,
                expectedDeletePending,
                linksMatch))
        {
            return false;
        }

        var fixedFileMatches = MatchesFixedFileState(
            handle, receipt.Route, authority.Identity, authority.Length,
            authority.Sha256, expectedDeletePending, linksMatch);
        var anchorAfterHash = ReadObjectInformation(
            authority.Anchor!,
            receipt.Route);
        return MatchesFileReceiptStateInformation(
                   receipt,
                   anchorAfterHash,
                   expectedDeletePending,
                   linksMatch)
               && fixedFileMatches;
    }

    private static bool MatchesIssuedFile(
        ObjectInformation information,
        ObjectIdentity identity,
        long length)
        => !information.IsDirectory
           && !information.IsReparsePoint
           && !information.DeletePending
           && information.NumberOfLinks == 1
           && information.Identity == identity
           && information.Length == length;

    private static bool MatchesAnchoredFile(
        FileReceipt receipt,
        ObjectInformation information)
    {
        var authority = (IFileReceiptAuthority)receipt;
        return !information.IsDirectory
               && !information.IsReparsePoint
               && information.Identity == authority.Identity
               && information.Length == authority.Length;
    }

    private static bool MatchesFileReceiptStateInformation(
        FileReceipt receipt,
        ObjectInformation information,
        bool expectedDeletePending,
        Func<uint, bool> linksMatch)
        => MatchesAnchoredFile(receipt, information)
           && information.DeletePending == expectedDeletePending
           && linksMatch(information.NumberOfLinks);

    private static void EnsureFileDispositionCleared(
        SafeFileHandle handle,
        FileReceipt receipt,
        Func<int, bool>? rollbackAttemptGate)
    {
        for (var attempt = 0; attempt < DispositionRollbackAttempts; attempt++)
        {
            try
            {
                if (rollbackAttemptGate?.Invoke(attempt + 1) ?? true)
                {
                    _ = TrySetDeleteDisposition(handle, delete: false);
                }

                if (MatchesRestoredFileIdentity(handle, receipt))
                {
                    return;
                }
            }
            catch (Exception exception) when (IsObservationFailure(exception))
            {
                // Retry the rollback and its postcondition as one bounded operation.
            }
        }

        throw new RollbackException(receipt.Route);
    }

    private static bool MatchesRestoredFileIdentity(
        SafeFileHandle proofHandle,
        FileReceipt receipt)
    {
        var authority = (IFileReceiptAuthority)receipt;
        var anchorInformation = ReadObjectInformation(
            authority.Anchor!,
            receipt.Route);
        var proofInformation = ReadObjectInformation(proofHandle, receipt.Route);
        using var routeHandle = OpenFileForObservation(
            receipt.Route,
            desiredAccess: 0,
            FileShareRead | FileShareWrite | FileShareDelete);
        var routeInformation = ReadObjectInformation(routeHandle, receipt.Route);
        return MatchesRestoredFileIdentity(receipt, anchorInformation)
               && MatchesRestoredFileIdentity(receipt, proofInformation)
               && MatchesRestoredFileIdentity(receipt, routeInformation);
    }

    private static bool MatchesRestoredFileIdentity(
        FileReceipt receipt,
        ObjectInformation information)
    {
        var authority = (IFileReceiptAuthority)receipt;
        return !information.IsDirectory
               && !information.IsReparsePoint
               && !information.DeletePending
               && information.NumberOfLinks > 0
               && information.Identity == authority.Identity;
    }

    private static byte[] ReadExactBytes(
        SafeFileHandle handle,
        long length,
        string route)
    {
        if (length < 0 || length > int.MaxValue)
        {
            throw new IOException(
                $"The exact ownership file exceeds the stable-capture limit: '{route}'.");
        }

        var bytes = GC.AllocateUninitializedArray<byte>((int)length);
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = RandomAccess.Read(handle, bytes.AsSpan(offset), offset);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    $"The exact ownership file ended during stable capture: '{route}'.");
            }

            offset += read;
        }

        Span<byte> extra = stackalloc byte[1];
        if (RandomAccess.Read(handle, extra, length) != 0)
        {
            throw new IOException(
                $"The exact ownership file grew during stable capture: '{route}'.");
        }

        return bytes;
    }

    private static byte[] ComputeSha256(SafeFileHandle handle, long length)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[Math.Min(HashBufferSize, (int)Math.Max(1, Math.Min(length, int.MaxValue)))];
        long offset = 0;
        while (offset < length)
        {
            var requested = (int)Math.Min(buffer.Length, length - offset);
            var read = RandomAccess.Read(handle, buffer.AsSpan(0, requested), offset);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    "The exact ownership file ended while its SHA-256 content was read.");
            }

            hash.AppendData(buffer, 0, read);
            offset += read;
        }

        Span<byte> extra = stackalloc byte[1];
        if (RandomAccess.Read(handle, extra, length) != 0)
        {
            throw new IOException(
                "The exact ownership file grew while its SHA-256 content was read.");
        }

        return hash.GetHashAndReset();
    }

    private static IReadOnlyList<string> ReadDirectoryEntries(
        SafeFileHandle handle,
        string route)
    {
        var entries = new List<string>();
        var buffer = Marshal.AllocHGlobal(DirectoryQueryBufferSize);
        try
        {
            var restartScan = true;
            while (true)
            {
                var status = NtQueryDirectoryFile(
                    handle,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    out var ioStatus,
                    buffer,
                    DirectoryQueryBufferSize,
                    FileNamesInformationClass,
                    returnSingleEntry: false,
                    IntPtr.Zero,
                    restartScan);
                restartScan = false;
                if (status == StatusNoMoreFiles)
                {
                    break;
                }

                if (status < 0)
                {
                    throw new Win32Exception(
                        unchecked((int)RtlNtStatusToDosError(status)),
                        $"The exact ownership directory could not be enumerated safely: '{route}'.");
                }

                var returned = checked((int)ioStatus.Information.ToUInt64());
                if (returned == 0)
                {
                    break;
                }

                ReadDirectoryQueryBuffer(buffer, returned, route, entries);
            }

            return entries
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => path, StringComparer.Ordinal)
                .ToArray();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void ReadDirectoryQueryBuffer(
        IntPtr buffer,
        int length,
        string route,
        ICollection<string> entries)
    {
        var offset = 0;
        while (true)
        {
            const int nameOffset = 12;
            if (offset < 0 || offset > length - nameOffset)
            {
                throw new IOException(
                    $"The exact ownership directory returned invalid entry metadata: '{route}'.");
            }

            var entry = IntPtr.Add(buffer, offset);
            var nextOffset = unchecked((uint)Marshal.ReadInt32(entry, 0));
            var nameLength = unchecked((uint)Marshal.ReadInt32(entry, 8));
            if ((nameLength & 1) != 0
                || nameLength > length - offset - nameOffset)
            {
                throw new IOException(
                    $"The exact ownership directory returned an invalid entry name: '{route}'.");
            }

            var name = Marshal.PtrToStringUni(
                IntPtr.Add(entry, nameOffset),
                checked((int)(nameLength / sizeof(char))))
                ?? throw new IOException(
                    $"The exact ownership directory returned a null entry name: '{route}'.");
            if (name is not "." and not "..")
            {
                entries.Add(Path.GetFullPath(Path.Combine(route, name)));
            }

            if (nextOffset == 0)
            {
                break;
            }

            if (nextOffset < nameOffset || nextOffset > length - offset)
            {
                throw new IOException(
                    $"The exact ownership directory returned an invalid entry offset: '{route}'.");
            }

            offset = checked(offset + (int)nextOffset);
        }
    }

    private static IReadOnlyList<string> ReadRetainedDirectoryEntries(
        SafeFileHandle handle,
        string route)
    {
        try
        {
            return ReadDirectoryEntries(handle, route)
                .Append(route)
                .ToArray();
        }
        catch (Exception exception) when (IsObservationFailure(exception))
        {
            return [route];
        }
    }

    private static bool TrySetDeleteDisposition(
        SafeFileHandle handle,
        bool delete = true)
    {
        var disposition = new FileDispositionInformation
        {
            DeleteFile = delete ? (byte)1 : (byte)0
        };
        return SetFileInformationByHandle(
            handle,
            FileDispositionInfoClass,
            ref disposition,
            (uint)Marshal.SizeOf<FileDispositionInformation>());
    }

    private static bool IsObservationFailure(Exception exception)
        => exception is Win32Exception
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or NotSupportedException
            or ArgumentException
            or CryptographicException
            or System.Security.SecurityException;

    private static void ValidateChildName(string childName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(childName);
        if (childName is "." or ".."
            || Path.IsPathRooted(childName)
            || childName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || childName.Contains(Path.DirectorySeparatorChar)
            || childName.Contains(Path.AltDirectorySeparatorChar)
            || childName.Contains(':')
            || !Path.GetFileName(childName).Equals(childName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "An exact ownership child name must be one ordinary path component.",
                nameof(childName));
        }
    }

    private void RequireOwned(FileReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var authority = (IFileReceiptAuthority)receipt;
        if (!ReferenceEquals(authority.Owner, this))
        {
            throw new InvalidOperationException(
                "The file receipt belongs to a different exact ownership session.");
        }

        if (!authority.IsActive)
        {
            throw new InvalidOperationException(
                "The file receipt is no longer active in its exact ownership session.");
        }
    }

    private void RequireOwned(DirectoryReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var authority = (IDirectoryReceiptAuthority)receipt;
        if (!ReferenceEquals(authority.Owner, this))
        {
            throw new InvalidOperationException(
                "The directory receipt belongs to a different exact ownership session.");
        }

        if (!authority.IsActive)
        {
            throw new InvalidOperationException(
                "The directory receipt is no longer active in its exact ownership session.");
        }
    }

    private ObservationResult ObserveMissing(FileReceipt receipt)
    {
        var result = ClassifyMissing(receipt, retireReceipt: false);
        return result.Removed ? ObservationResult.Missing
            : result.Conclusive ? ObservationResult.Changed : ObservationResult.Inconclusive;
    }

    private ObservationResult ObserveMissing(DirectoryReceipt receipt)
    {
        var result = ClassifyMissing(receipt, retireReceipt: false);
        return result.Removed ? ObservationResult.Missing
            : result.Conclusive ? ObservationResult.Changed : ObservationResult.Inconclusive;
    }

    private DeletionResult ClassifyMissing(FileReceipt receipt, bool retireReceipt = true)
    {
        try
        {
            var routeState = ObserveRouteState(receipt.Route);
            if (routeState == RouteState.Present)
            {
                return DeletionResult.RetainedConclusive(receipt.Route);
            }

            if (routeState == RouteState.Inconclusive)
            {
                return DeletionResult.RetainedInconclusive(receipt.Route);
            }

            var authority = (IFileReceiptAuthority)receipt;
            var information = ReadObjectInformation(
                authority.Anchor!,
                receipt.Route);
            if (!MatchesAnchoredFile(receipt, information))
            {
                return DeletionResult.RetainedConclusive(receipt.Route);
            }

            if (information.NumberOfLinks != 0)
            {
                return DeletionResult.RetainedConclusive(receipt.Route);
            }

            if (retireReceipt)
            {
                Retire(receipt);
            }
            return DeletionResult.RemovedResult();
        }
        catch (Exception exception) when (IsObservationFailure(exception))
        {
            return DeletionResult.RetainedInconclusive(receipt.Route);
        }
    }

    private DeletionResult ClassifyMissing(DirectoryReceipt receipt, bool retireReceipt = true)
    {
        try
        {
            var routeState = ObserveRouteState(receipt.Route);
            if (routeState == RouteState.Present)
            {
                return DeletionResult.RetainedConclusive(receipt.Route);
            }

            if (routeState == RouteState.Inconclusive)
            {
                return DeletionResult.RetainedInconclusive(receipt.Route);
            }

            var authority = (IDirectoryReceiptAuthority)receipt;
            var information = ReadObjectInformation(
                authority.Anchor!,
                receipt.Route);
            if (!MatchesAnchoredDirectory(receipt, information))
            {
                return DeletionResult.RetainedConclusive(receipt.Route);
            }

            if (!information.DeletePending && information.NumberOfLinks != 0)
            {
                return DeletionResult.RetainedConclusive(receipt.Route);
            }

            if (retireReceipt)
            {
                Retire(receipt);
            }
            return DeletionResult.RemovedResult();
        }
        catch (Exception exception) when (IsObservationFailure(exception))
        {
            return DeletionResult.RetainedInconclusive(receipt.Route);
        }
    }

    private static RouteState ObserveRouteState(string route)
    {
        using var handle = CreateFile(
            route,
            desiredAccess: 0,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            return RouteState.Present;
        }

        var error = Marshal.GetLastWin32Error();
        return error is ErrorFileNotFound or ErrorPathNotFound
            ? RouteState.Absent
            : RouteState.Inconclusive;
    }

    private void Retire(FileReceipt receipt)
    {
        ((IFileReceiptAuthority)receipt).Retire()?.Dispose();
        fileReceipts.Remove(receipt);
    }

    private void Retire(DirectoryReceipt receipt)
    {
        var handles = ((IDirectoryReceiptAuthority)receipt).Retire();
        handles.CreationFence?.Dispose();
        handles.Anchor?.Dispose();
        directoryReceipts.Remove(receipt);
    }

    private static void ReleaseCreationFenceCore(DirectoryReceipt receipt)
        => ((IDirectoryReceiptAuthority)receipt).ReleaseCreationFence();

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(disposed, this);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFileInformationByHandleEx",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileIdInformation(
        SafeFileHandle file,
        int fileInformationClass,
        out FileIdInformation fileInformation,
        uint bufferSize);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFileInformationByHandleEx",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileStandardInformation(
        SafeFileHandle file,
        int fileInformationClass,
        out FileStandardInformation fileInformation,
        uint bufferSize);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFileInformationByHandleEx",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileAttributeTagInformation(
        SafeFileHandle file,
        int fileInformationClass,
        out FileAttributeTagInformation fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        ref FileDispositionInformation fileInformation,
        uint bufferSize);

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(
        out SafeFileHandle fileHandle,
        uint desiredAccess,
        ref ObjectAttributes objectAttributes,
        out IoStatusBlock ioStatusBlock,
        IntPtr allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        IntPtr eaBuffer,
        uint eaLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryDirectoryFile(
        SafeFileHandle fileHandle,
        IntPtr @event,
        IntPtr apcRoutine,
        IntPtr apcContext,
        out IoStatusBlock ioStatusBlock,
        IntPtr fileInformation,
        uint length,
        int fileInformationClass,
        [MarshalAs(UnmanagedType.U1)] bool returnSingleEntry,
        IntPtr fileName,
        [MarshalAs(UnmanagedType.U1)] bool restartScan);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);

    private interface IFileReceiptAuthority
    {
        ExactFileSystemObjectOwnership Owner { get; }

        ObjectIdentity Identity { get; }

        long Length { get; }

        byte[] Sha256 { get; }

        SafeFileHandle? Anchor { get; }

        bool IsActive { get; }

        SafeFileHandle? Retire();
    }

    private interface IDirectoryReceiptAuthority
    {
        ExactFileSystemObjectOwnership Owner { get; }

        ObjectIdentity Identity { get; }

        SafeFileHandle? Anchor { get; }

        SafeFileHandle? CreationFence { get; }

        bool IsActive { get; }

        void ReleaseCreationFence();

        (SafeFileHandle? CreationFence, SafeFileHandle? Anchor) Retire();
    }

    /// <summary>
    /// Opaque authority for one exact ordinary file.
    /// </summary>
    internal sealed class FileReceipt : IFileReceiptAuthority
    {
        private readonly ExactFileSystemObjectOwnership owner;
        private readonly ObjectIdentity identity;
        private readonly long length;
        private readonly byte[] sha256;
        private SafeFileHandle? anchor;
        private bool retired;

        internal FileReceipt(object provenState)
        {
            if (provenState is not FileReceiptProof proof)
            {
                throw new InvalidOperationException(
                    "A file receipt requires private proven ownership state.");
            }

            owner = proof.Owner;
            Route = proof.Route;
            identity = proof.Identity;
            length = proof.Length;
            sha256 = proof.Sha256.ToArray();
            anchor = proof.Anchor;
        }

        /// <summary>
        /// Gets the normalized route for reporting only.
        /// </summary>
        internal string Route { get; }

        ExactFileSystemObjectOwnership IFileReceiptAuthority.Owner => owner;

        ObjectIdentity IFileReceiptAuthority.Identity => identity;

        long IFileReceiptAuthority.Length => length;

        byte[] IFileReceiptAuthority.Sha256 => sha256;

        SafeFileHandle? IFileReceiptAuthority.Anchor => anchor;

        bool IFileReceiptAuthority.IsActive
            => !retired
               && anchor is { IsClosed: false, IsInvalid: false };

        SafeFileHandle? IFileReceiptAuthority.Retire()
        {
            retired = true;
            return Interlocked.Exchange(ref anchor, null);
        }
    }

    /// <summary>
    /// Opaque authority for one exact ordinary directory.
    /// </summary>
    internal sealed class DirectoryReceipt : IDirectoryReceiptAuthority
    {
        private readonly ExactFileSystemObjectOwnership owner;
        private readonly ObjectIdentity identity;
        private SafeFileHandle? creationFence;
        private SafeFileHandle? anchor;
        private bool retired;

        internal DirectoryReceipt(object provenState)
        {
            if (provenState is not DirectoryReceiptProof proof)
            {
                throw new InvalidOperationException(
                    "A directory receipt requires private proven ownership state.");
            }

            owner = proof.Owner;
            Route = proof.Route;
            identity = proof.Identity;
            creationFence = proof.CreationFence;
            anchor = proof.Anchor;
        }

        /// <summary>
        /// Gets the normalized route for reporting only.
        /// </summary>
        internal string Route { get; }

        ExactFileSystemObjectOwnership IDirectoryReceiptAuthority.Owner => owner;

        ObjectIdentity IDirectoryReceiptAuthority.Identity => identity;

        SafeFileHandle? IDirectoryReceiptAuthority.Anchor => anchor;

        SafeFileHandle? IDirectoryReceiptAuthority.CreationFence => creationFence;

        bool IDirectoryReceiptAuthority.IsActive
            => !retired
               && anchor is { IsClosed: false, IsInvalid: false };

        void IDirectoryReceiptAuthority.ReleaseCreationFence()
            => Interlocked.Exchange(ref creationFence, null)?.Dispose();

        (SafeFileHandle? CreationFence, SafeFileHandle? Anchor)
            IDirectoryReceiptAuthority.Retire()
        {
            retired = true;
            return (
                Interlocked.Exchange(ref creationFence, null),
                Interlocked.Exchange(ref anchor, null));
        }
    }

    private interface IPendingFileCaptureState
    {
        PendingFileCaptureProof State { get; }
    }

    /// <summary>
    /// Retains saved bytes and a producer-file handle, but grants no deletion
    /// authority. Only this session can complete the pending observation.
    /// </summary>
    internal sealed class PendingFileCapture : IPendingFileCaptureState
    {
        private readonly PendingFileCaptureProof state;

        internal PendingFileCapture(object provenState)
        {
            state = provenState as PendingFileCaptureProof
                ?? throw new InvalidOperationException(
                    "A pending file capture requires private observed state.");
        }

        internal string Route => state.Route;

        internal byte[] Bytes => state.Bytes.ToArray();

        PendingFileCaptureProof IPendingFileCaptureState.State => state;
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

    /// <summary>
    /// Couples immutable captured bytes to the receipt issued from the same
    /// stable handle observation.
    /// </summary>
    internal sealed class StableFileCapture
    {
        private readonly byte[] bytes;

        internal StableFileCapture(object provenState)
        {
            if (provenState is not StableFileCaptureProof proof)
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

    private readonly record struct ObjectIdentity(
        ulong VolumeSerialNumber,
        ulong FileIdLow,
        ulong FileIdHigh);

    private readonly record struct ObjectInformation(
        ObjectIdentity Identity,
        long Length,
        uint NumberOfLinks,
        bool DeletePending,
        bool IsDirectory,
        bool IsReparsePoint);

    private sealed record FileReceiptProof(
        ExactFileSystemObjectOwnership Owner,
        string Route,
        ObjectIdentity Identity,
        long Length,
        byte[] Sha256,
        SafeFileHandle Anchor);

    private sealed record DirectoryReceiptProof(
        ExactFileSystemObjectOwnership Owner,
        string Route,
        ObjectIdentity Identity,
        SafeFileHandle CreationFence,
        SafeFileHandle Anchor);

    private sealed record StableFileCaptureProof(
        FileReceipt Receipt,
        byte[] Bytes);

    private sealed record PendingFileCaptureProof(
        ExactFileSystemObjectOwnership Owner,
        string Route,
        ObjectIdentity Identity,
        byte[] Bytes,
        byte[] Sha256,
        SafeFileHandle Handle);

    private enum RouteState
    {
        Absent,
        Present,
        Inconclusive
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public uint Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public UIntPtr Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        public byte DeleteFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInformation
    {
        public ulong VolumeSerialNumber;
        public FileId128 FileId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileId128
    {
        public ulong Low;
        public ulong High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInformation
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInformation
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }
}
