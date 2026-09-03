using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using VbaDev.App.Workbooks;
using VbaDev.Domain;

namespace VbaDev.Infrastructure.Workbooks;

internal sealed record InitialWorkbookStagingArtifact(
    string DirectoryPath,
    string WorkbookPath,
    FileSystemObjectIdentity DirectoryIdentity)
{
    private SafeFileHandle? directoryOwnershipHandle;

    internal void AttachDirectoryOwnershipHandle(SafeFileHandle handle)
    {
        if (Interlocked.CompareExchange(
                ref directoryOwnershipHandle,
                handle,
                comparand: null) is not null)
        {
            throw new InvalidOperationException(
                "The staging directory ownership handle was already attached.");
        }
    }

    internal SafeFileHandle? TakeDirectoryOwnershipHandle()
        => Interlocked.Exchange(ref directoryOwnershipHandle, null);
}

internal interface IInitialWorkbookArtifactGuard
{
    InitialWorkbookStagingArtifact CreateStagingArtifact();

    InitialWorkbookArtifactEvidence Capture(string workbookPath);

    InitialWorkbookArtifactEvidence MaterializeCreateOnly(
        InitialWorkbookArtifactEvidence stagingArtifact,
        string workbookPath,
        CancellationToken cancellationToken);

    InitialWorkbookArtifactCleanupResult TryDeleteStaging(
        InitialWorkbookStagingArtifact staging,
        InitialWorkbookArtifactEvidence? expectedArtifact);

    InitialWorkbookArtifactCleanupResult TryDeleteIfUnchanged(
        string workbookPath,
        InitialWorkbookArtifactEvidence? expectedArtifact);
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
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint FileReadAttributes = 0x00000080;
    private const uint SynchronizeAccess = 0x00100000;
    private const uint CreateNew = 1;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagWriteThrough = 0x80000000;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const int FileDispositionInfoClass = 4;
    private const uint ObjectCaseInsensitive = 0x00000040;
    private const uint NtFileCreate = 2;
    private const uint NtFileDirectoryFile = 0x00000001;
    private const uint NtFileSynchronousIoNonAlert = 0x00000020;
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
            var directory = Path.Combine(
                Path.GetTempPath(),
                $"vba-dev-new-{Guid.NewGuid():N}");
            SafeFileHandle handle;
            try
            {
                handle = CreateNewDirectoryHandle(directory);
            }
            catch (Win32Exception exception) when (
                exception.NativeErrorCode is 80 or 183)
            {
                continue;
            }

            try
            {
                var directoryInformation = ReadDirectoryInformation(
                    handle,
                    directory);
                if (directoryInformation.IsReparsePoint)
                {
                    throw new IOException(
                        $"The invocation-owned staging directory is a reparse point: '{directory}'.");
                }

                stagingObserver.OnDirectoryCreated(directory);
                var staging = new InitialWorkbookStagingArtifact(
                    directory,
                    Path.Combine(directory, "initial.xlsm"),
                    directoryInformation.Identity);
                staging.AttachDirectoryOwnershipHandle(handle);
                return staging;
            }
            catch (Exception creationFailure)
            {
                try
                {
                    var cleanupFailure = TryDeleteExactHandle(handle, directory);
                    if (cleanupFailure is null)
                    {
                        throw;
                    }

                    throw new InitialWorkbookArtifactRetainedException(
                        directory,
                        expectedArtifact: null,
                        targetChanged: false,
                        new AggregateException(
                            creationFailure,
                            cleanupFailure));
                }
                finally
                {
                    handle.Dispose();
                }
            }
        }

        throw new IOException(
            "A unique invocation-owned initial workbook staging directory could not be created.");
    }

    public InitialWorkbookArtifactEvidence Capture(string workbookPath)
    {
        var absolutePath = Path.GetFullPath(workbookPath);
        using var handle = OpenExistingFile(absolutePath, requestDeleteAccess: false);
        return CaptureStableEvidence(handle, absolutePath);
    }

    public InitialWorkbookArtifactEvidence MaterializeCreateOnly(
        InitialWorkbookArtifactEvidence stagingArtifact,
        string workbookPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stagingArtifact);
        cancellationToken.ThrowIfCancellationRequested();

        var stagingPath = Path.GetFullPath(stagingArtifact.WorkbookPath);
        var absoluteWorkbookPath = Path.GetFullPath(workbookPath);
        using var sourceHandle = OpenExistingFile(
            stagingPath,
            requestDeleteAccess: false);
        var currentStagingArtifact = CaptureStableEvidence(sourceHandle, stagingPath);
        if (!ArtifactMatches(stagingArtifact, currentStagingArtifact))
        {
            throw new InitialWorkbookArtifactRetainedException(
                stagingPath,
                stagingArtifact,
                targetChanged: true,
                new IOException(
                    $"The staging workbook no longer names the exact object and bytes created by Excel: '{stagingPath}'."));
        }

        SafeFileHandle? destinationHandle = null;
        InitialWorkbookArtifactEvidence? destinationEvidence = null;
        try
        {
            destinationHandle = CreateNewDestination(absoluteWorkbookPath);
            copyObserver.OnDestinationCreated(absoluteWorkbookPath);
            CopyExact(
                sourceHandle,
                destinationHandle,
                currentStagingArtifact.Length,
                absoluteWorkbookPath,
                cancellationToken);
            RandomAccess.FlushToDisk(destinationHandle);

            var stagingAfterCopy = CaptureStableEvidence(sourceHandle, stagingPath);
            if (!ArtifactMatches(stagingArtifact, stagingAfterCopy))
            {
                throw new IOException(
                    $"The staging workbook changed while it was materialized: '{stagingPath}'.");
            }

            destinationEvidence = CaptureStableEvidence(
                destinationHandle,
                absoluteWorkbookPath);
            if (destinationEvidence.Length != stagingArtifact.Length ||
                !string.Equals(
                    destinationEvidence.Sha256,
                    stagingArtifact.Sha256,
                    StringComparison.Ordinal))
            {
                throw new IOException(
                    $"The materialized workbook does not exactly match its staging workbook: '{absoluteWorkbookPath}'.");
            }

            var pathEvidence = Capture(absoluteWorkbookPath);
            if (!ArtifactMatches(destinationEvidence, pathEvidence))
            {
                throw new InitialWorkbookArtifactRetainedException(
                    absoluteWorkbookPath,
                    destinationEvidence,
                    targetChanged: true,
                    new IOException(
                        $"The initial workbook destination changed while it was materialized: '{absoluteWorkbookPath}'."));
            }

            copyObserver.OnDestinationProved(absoluteWorkbookPath);
            cancellationToken.ThrowIfCancellationRequested();
            return destinationEvidence;
        }
        catch (Exception exception)
        {
            if (destinationHandle is null)
            {
                throw;
            }

            destinationEvidence ??= TryCaptureEvidence(
                destinationHandle,
                absoluteWorkbookPath);
            var targetChanged = !PathNamesExpectedArtifact(
                absoluteWorkbookPath,
                destinationEvidence);
            var cleanup = TryDeleteExactHandle(
                destinationHandle,
                absoluteWorkbookPath);
            if (cleanup is not null)
            {
                throw new InitialWorkbookArtifactRetainedException(
                    absoluteWorkbookPath,
                    destinationEvidence,
                    targetChanged,
                    new AggregateException(exception, cleanup));
            }

            if (targetChanged &&
                exception is not InitialWorkbookArtifactRetainedException)
            {
                throw new InitialWorkbookArtifactRetainedException(
                    absoluteWorkbookPath,
                    destinationEvidence,
                    targetChanged: true,
                    exception);
            }

            throw;
        }
        finally
        {
            destinationHandle?.Dispose();
        }
    }

    public InitialWorkbookArtifactCleanupResult TryDeleteStaging(
        InitialWorkbookStagingArtifact staging,
        InitialWorkbookArtifactEvidence? expectedArtifact)
    {
        ArgumentNullException.ThrowIfNull(staging);
        SafeFileHandle? handle = null;
        try
        {
            handle = staging.TakeDirectoryOwnershipHandle()
                ?? OpenExistingDirectory(staging.DirectoryPath);
            var directoryInformation = ReadDirectoryInformation(
                handle,
                staging.DirectoryPath);
            if (directoryInformation.IsReparsePoint ||
                directoryInformation.Identity != staging.DirectoryIdentity)
            {
                return InitialWorkbookArtifactCleanupResult.Changed();
            }

            var fileCleanup = TryDeleteIfUnchanged(
                staging.WorkbookPath,
                expectedArtifact);
            if (!fileCleanup.RemovedOrAbsent)
            {
                return fileCleanup;
            }

            cleanupObserver.OnProofComplete(staging.DirectoryPath);
            var disposition = new FileDispositionInformation { DeleteFile = true };
            if (!SetFileInformationByHandle(
                    handle,
                    FileDispositionInfoClass,
                    ref disposition,
                    (uint)Marshal.SizeOf<FileDispositionInformation>()))
            {
                var error = Marshal.GetLastWin32Error();
                var failure = new Win32Exception(
                    error,
                    $"The exact invocation-owned staging directory could not be removed: '{staging.DirectoryPath}'.");
                return error == 145
                    ? InitialWorkbookArtifactCleanupResult.Changed(failure)
                    : InitialWorkbookArtifactCleanupResult.Failed(failure);
            }

            return InitialWorkbookArtifactCleanupResult.Removed();
        }
        catch (Win32Exception exception) when (
            exception.NativeErrorCode is 2 or 3)
        {
            return InitialWorkbookArtifactCleanupResult.Removed();
        }
        catch (DirectoryNotFoundException)
        {
            return InitialWorkbookArtifactCleanupResult.Removed();
        }
        catch (Exception exception)
        {
            return InitialWorkbookArtifactCleanupResult.Failed(exception);
        }
        finally
        {
            handle?.Dispose();
        }
    }

    public InitialWorkbookArtifactCleanupResult TryDeleteIfUnchanged(
        string workbookPath,
        InitialWorkbookArtifactEvidence? expectedArtifact)
    {
        var absolutePath = Path.GetFullPath(workbookPath);
        if (expectedArtifact is null)
        {
            return File.Exists(absolutePath)
                ? InitialWorkbookArtifactCleanupResult.Failed(
                    new InvalidOperationException(
                        "The created workbook identity could not be captured before cleanup."))
                : InitialWorkbookArtifactCleanupResult.Removed();
        }

        try
        {
            using var handle = OpenExistingFile(absolutePath, requestDeleteAccess: true);
            var currentArtifact = CaptureStableEvidence(handle, absolutePath);
            if (!ArtifactMatches(expectedArtifact, currentArtifact) ||
                !PathsEqual(expectedArtifact.WorkbookPath, absolutePath))
            {
                return InitialWorkbookArtifactCleanupResult.Changed();
            }

            cleanupObserver.OnProofComplete(absolutePath);
            var disposition = new FileDispositionInformation { DeleteFile = true };
            if (!SetFileInformationByHandle(
                    handle,
                    FileDispositionInfoClass,
                    ref disposition,
                    (uint)Marshal.SizeOf<FileDispositionInformation>()))
            {
                return InitialWorkbookArtifactCleanupResult.Failed(
                    new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        $"The exact created workbook could not be marked for deletion: '{absolutePath}'."));
            }

            return InitialWorkbookArtifactCleanupResult.Removed();
        }
        catch (Win32Exception exception) when (
            exception.NativeErrorCode is 2 or 3)
        {
            return InitialWorkbookArtifactCleanupResult.Removed();
        }
        catch (InitialWorkbookPathTypeChangedException exception)
        {
            return InitialWorkbookArtifactCleanupResult.Changed(exception);
        }
        catch (Win32Exception exception) when (
            IsDirectoryOrReparsePoint(absolutePath))
        {
            return InitialWorkbookArtifactCleanupResult.Changed(exception);
        }
        catch (FileNotFoundException)
        {
            return InitialWorkbookArtifactCleanupResult.Removed();
        }
        catch (DirectoryNotFoundException)
        {
            return InitialWorkbookArtifactCleanupResult.Removed();
        }
        catch (Exception exception)
        {
            return InitialWorkbookArtifactCleanupResult.Failed(exception);
        }
    }

    private void CopyExact(
        SafeFileHandle sourceHandle,
        SafeFileHandle destinationHandle,
        long length,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long offset = 0;
        while (offset < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requested = (int)Math.Min(buffer.Length, length - offset);
            var read = RandomAccess.Read(
                sourceHandle,
                buffer.AsSpan(0, requested),
                offset);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    "The staging workbook became shorter while it was materialized.");
            }

            RandomAccess.Write(
                destinationHandle,
                buffer.AsSpan(0, read),
                offset);
            offset += read;
            copyObserver.OnBytesCopied(destinationPath, offset);
        }
    }

    private static SafeFileHandle CreateNewDestination(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Initial workbook artifact identity requires Windows file handles.");
        }

        var handle = CreateFile(
            path,
            GenericRead | GenericWrite | DeleteAccess,
            FileShareRead,
            IntPtr.Zero,
            CreateNew,
            FileAttributeNormal | FileFlagSequentialScan | FileFlagWriteThrough,
            IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            return handle;
        }

        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        var failure = new Win32Exception(
            error,
            $"The initial workbook destination could not be created: '{path}'.");
        if (error is 80 or 183)
        {
            throw new InitialWorkbookArtifactRetainedException(
                path,
                expectedArtifact: null,
                targetChanged: true,
                failure);
        }

        throw failure;
    }

    private static InitialWorkbookArtifactEvidence? TryCaptureEvidence(
        SafeFileHandle handle,
        string path)
    {
        try
        {
            return CaptureStableEvidence(handle, path);
        }
        catch
        {
            return null;
        }
    }

    private static bool PathNamesExpectedArtifact(
        string path,
        InitialWorkbookArtifactEvidence? expectedArtifact)
    {
        if (expectedArtifact is null)
        {
            return false;
        }

        try
        {
            using var handle = OpenExistingFile(path, requestDeleteAccess: false);
            return ArtifactMatches(
                expectedArtifact,
                CaptureStableEvidence(handle, path));
        }
        catch
        {
            return false;
        }
    }

    private static Exception? TryDeleteExactHandle(
        SafeFileHandle handle,
        string path)
    {
        var disposition = new FileDispositionInformation { DeleteFile = true };
        return SetFileInformationByHandle(
            handle,
            FileDispositionInfoClass,
            ref disposition,
            (uint)Marshal.SizeOf<FileDispositionInformation>())
                ? null
                : new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"The exact partial workbook could not be marked for deletion: '{path}'.");
    }

    private static InitialWorkbookArtifactEvidence CaptureStableEvidence(
        SafeFileHandle handle,
        string absolutePath)
    {
        var before = ReadFileInformation(handle, absolutePath);
        var firstHash = ComputeHash(handle, before.Length, absolutePath);
        var middle = ReadFileInformation(handle, absolutePath);
        var secondHash = ComputeHash(handle, middle.Length, absolutePath);
        var after = ReadFileInformation(handle, absolutePath);
        if (before.Identity != middle.Identity ||
            before.Identity != after.Identity ||
            before.Length != middle.Length ||
            before.Length != after.Length ||
            !firstHash.AsSpan().SequenceEqual(secondHash))
        {
            throw new IOException(
                $"The created workbook changed while its cleanup evidence was being captured: '{absolutePath}'.");
        }

        return new InitialWorkbookArtifactEvidence(
            absolutePath,
            before.Identity,
            before.Length,
            Convert.ToHexString(firstHash).ToLowerInvariant());
    }

    private static FileInformation ReadFileInformation(
        SafeFileHandle handle,
        string path)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"The created workbook identity could not be read: '{path}'.");
        }

        if ((information.FileAttributes & FileAttributeDirectory) != 0)
        {
            throw new InitialWorkbookPathTypeChangedException(
                $"The initial workbook path is a directory: '{path}'.");
        }

        if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
        {
            throw new InitialWorkbookPathTypeChangedException(
                $"The initial workbook path is a reparse point: '{path}'.");
        }

        return new FileInformation(
            new FileSystemObjectIdentity(
                information.VolumeSerialNumber,
                ((ulong)information.FileIndexHigh << 32) |
                information.FileIndexLow),
            ((long)information.FileSizeHigh << 32) |
            information.FileSizeLow);
    }

    private static byte[] ComputeHash(
        SafeFileHandle handle,
        long length,
        string path)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long offset = 0;
        while (offset < length)
        {
            var requested = (int)Math.Min(buffer.Length, length - offset);
            var read = RandomAccess.Read(
                handle,
                buffer.AsSpan(0, requested),
                offset);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    $"The created workbook became shorter while its cleanup evidence was being captured: '{path}'.");
            }

            hash.AppendData(buffer, 0, read);
            offset += read;
        }

        return hash.GetHashAndReset();
    }

    private static bool ArtifactMatches(
        InitialWorkbookArtifactEvidence expected,
        InitialWorkbookArtifactEvidence current)
        => expected.ObjectIdentity == current.ObjectIdentity &&
           expected.Length == current.Length &&
           string.Equals(expected.Sha256, current.Sha256, StringComparison.Ordinal);

    private static bool PathsEqual(string left, string right)
        => Path.GetFullPath(left).Equals(
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static bool IsDirectoryOrReparsePoint(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.Directory)
                || attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return false;
        }
    }

    private static SafeFileHandle OpenExistingFile(
        string path,
        bool requestDeleteAccess)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Initial workbook artifact identity requires Windows file handles.");
        }

        var handle = CreateFile(
            path,
            GenericRead | (requestDeleteAccess ? DeleteAccess : 0),
            requestDeleteAccess
                ? FileShareRead
                : FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal | FileFlagSequentialScan | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            return handle;
        }

        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(
            error,
            $"The initial workbook file handle could not be opened: '{path}'.");
    }

    private static SafeFileHandle OpenExistingDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Initial workbook staging identity requires Windows directory handles.");
        }

        var handle = CreateFile(
            path,
            DeleteAccess,
            FileShareRead,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            return handle;
        }

        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(
            error,
            $"The initial workbook staging directory handle could not be opened: '{path}'.");
    }

    private static SafeFileHandle CreateNewDirectoryHandle(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Initial workbook staging creation requires Windows directory handles.");
        }

        var ntPath = ToNtPath(Path.GetFullPath(path));
        var pathLength = checked((ushort)(ntPath.Length * sizeof(char)));
        var maximumPathLength = checked(
            (ushort)((ntPath.Length + 1) * sizeof(char)));
        var pathBuffer = IntPtr.Zero;
        var unicodeStringPointer = IntPtr.Zero;
        try
        {
            pathBuffer = Marshal.StringToHGlobalUni(ntPath);
            var unicodeString = new UnicodeString
            {
                Length = pathLength,
                MaximumLength = maximumPathLength,
                Buffer = pathBuffer
            };
            unicodeStringPointer = Marshal.AllocHGlobal(
                Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(
                unicodeString,
                unicodeStringPointer,
                fDeleteOld: false);
            var objectAttributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = IntPtr.Zero,
                ObjectName = unicodeStringPointer,
                Attributes = ObjectCaseInsensitive,
                SecurityDescriptor = IntPtr.Zero,
                SecurityQualityOfService = IntPtr.Zero
            };
            var status = NtCreateFile(
                out var handle,
                FileReadAttributes | DeleteAccess | SynchronizeAccess,
                ref objectAttributes,
                out _,
                IntPtr.Zero,
                FileAttributeNormal,
                // Excel SaveAs writes a temporary child before renaming it to
                // the requested workbook path. Keep delete sharing withheld
                // to pin this directory, but allow that child write sequence.
                FileShareRead | FileShareWrite,
                NtFileCreate,
                NtFileDirectoryFile |
                NtFileSynchronousIoNonAlert |
                FileFlagOpenReparsePoint,
                IntPtr.Zero,
                0);
            if (status >= 0 && !handle.IsInvalid)
            {
                return handle;
            }

            handle.Dispose();
            throw new Win32Exception(
                checked((int)RtlNtStatusToDosError(status)),
                $"The invocation-owned staging directory could not be created: '{path}'.");
        }
        finally
        {
            if (unicodeStringPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(unicodeStringPointer);
            }

            if (pathBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(pathBuffer);
            }
        }
    }

    private static string ToNtPath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\??\UNC\" + path[8..];
        }

        if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\??\" + path[4..];
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return @"\??\UNC\" + path[2..];
        }

        return @"\??\" + path;
    }

    private static DirectoryInformation ReadDirectoryInformation(
        SafeFileHandle handle,
        string path)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"The staging directory identity could not be read: '{path}'.");
        }

        if ((information.FileAttributes & FileAttributeDirectory) == 0)
        {
            throw new IOException(
                $"The staging directory path is not a directory: '{path}'.");
        }

        return new DirectoryInformation(
            new FileSystemObjectIdentity(
                information.VolumeSerialNumber,
                ((ulong)information.FileIndexHigh << 32) |
                information.FileIndexLow),
            (information.FileAttributes & FileAttributeReparsePoint) != 0);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

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
    private static extern uint RtlNtStatusToDosError(int status);

    private readonly record struct FileInformation(
        FileSystemObjectIdentity Identity,
        long Length);

    private sealed class InitialWorkbookPathTypeChangedException(string message)
        : IOException(message);

    private readonly record struct DirectoryInformation(
        FileSystemObjectIdentity Identity,
        bool IsReparsePoint);

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
        public int Length;
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
        [MarshalAs(UnmanagedType.Bool)]
        public bool DeleteFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

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
