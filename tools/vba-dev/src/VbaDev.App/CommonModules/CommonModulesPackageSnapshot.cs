using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VbaDev.App.CommonModules;

internal interface ICommonModulesPackageSnapshotCleanupObserver
{
    void OnProofComplete(string path);
}

/// <summary>
/// Captures and validates one invocation-owned, immutable CommonModules package snapshot.
/// </summary>
public sealed class CommonModulesPackageSnapshotFactory
{
    private const int CleanupAttempts = 3;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint SynchronizeAccess = 0x00100000;
    private const uint FileListDirectory = 0x00000001;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
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
    private const uint ObjectAttributesCaseInsensitive = 0x00000040;
    private const uint NtFileCreate = 2;
    private const uint NtFileDirectoryFile = 0x00000001;
    private const uint NtFileSynchronousIoNonAlert = 0x00000020;
    private const uint NtFileOpenReparsePoint = 0x00200000;
    private const int StatusObjectNameCollision = unchecked((int)0xC0000035);
    private static readonly TimeSpan CleanupRetryDelay = TimeSpan.FromMilliseconds(50);
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private readonly CommonModulesPackageReader packageReader;
    private readonly string scratchRoot;
    private readonly Action? beforePackageLoad;
    private readonly Action? beforeLiveStabilityProof;
    private readonly ICommonModulesPackageSnapshotCleanupObserver cleanupObserver;

    /// <summary>
    /// Creates a factory that stores snapshots in the command's temporary workspace.
    /// </summary>
    public CommonModulesPackageSnapshotFactory(CommonModulesPackageReader packageReader)
        : this(
            packageReader,
            Path.Combine(Path.GetTempPath(), "vba-dev-common-modules-snapshot"))
    {
    }

    /// <summary>
    /// Creates a factory that stores snapshots beneath the specified scratch root.
    /// </summary>
    public CommonModulesPackageSnapshotFactory(
        CommonModulesPackageReader packageReader,
        string scratchRoot)
        : this(
            packageReader,
            scratchRoot,
            beforePackageLoad: null,
            beforeLiveStabilityProof: null,
            NoOpCommonModulesPackageSnapshotCleanupObserver.Instance)
    {
    }

    internal CommonModulesPackageSnapshotFactory(
        CommonModulesPackageReader packageReader,
        string scratchRoot,
        Action? beforeLiveStabilityProof)
        : this(
            packageReader,
            scratchRoot,
            beforePackageLoad: null,
            beforeLiveStabilityProof,
            NoOpCommonModulesPackageSnapshotCleanupObserver.Instance)
    {
    }

    internal CommonModulesPackageSnapshotFactory(
        CommonModulesPackageReader packageReader,
        string scratchRoot,
        Action? beforeLiveStabilityProof,
        ICommonModulesPackageSnapshotCleanupObserver cleanupObserver)
        : this(
            packageReader,
            scratchRoot,
            beforePackageLoad: null,
            beforeLiveStabilityProof,
            cleanupObserver)
    {
    }

    internal CommonModulesPackageSnapshotFactory(
        CommonModulesPackageReader packageReader,
        string scratchRoot,
        Action? beforePackageLoad,
        Action? beforeLiveStabilityProof,
        ICommonModulesPackageSnapshotCleanupObserver cleanupObserver)
    {
        this.packageReader = packageReader
            ?? throw new ArgumentNullException(nameof(packageReader));
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchRoot);
        this.scratchRoot = Path.GetFullPath(scratchRoot);
        this.beforePackageLoad = beforePackageLoad;
        this.beforeLiveStabilityProof = beforeLiveStabilityProof;
        this.cleanupObserver = cleanupObserver
            ?? throw new ArgumentNullException(nameof(cleanupObserver));
    }

    /// <summary>
    /// Captures a complete package, validates the staged bytes, and proves the live inputs remained stable.
    /// </summary>
    public CommonModulesPackageSnapshot Capture(
        string commonModulesRepositoryPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commonModulesRepositoryPath);
        cancellationToken.ThrowIfCancellationRequested();
        var repositoryPath = Path.GetFullPath(commonModulesRepositoryPath);
        var inventory = ReadInventory(repositoryPath);
        CommonModulesPackageSnapshotStagingEvidence? staging = null;
        SafeFileHandle? stagingCaptureHandle = null;
        try
        {
            var createdStaging = CreateStagingDirectory(scratchRoot);
            staging = createdStaging.Evidence;
            stagingCaptureHandle = createdStaging.Handle;
            var capturedBytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var entry in inventory)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var content = ReadExactBytes(entry.FullName);
                CreateStagingFile(staging, entry.Name, content);
                capturedBytes.Add(entry.Name, content);
            }

            beforePackageLoad?.Invoke();
            var package = FreezePackage(packageReader.LoadCaptured(
                staging.Path,
                capturedBytes));
            beforeLiveStabilityProof?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            ProveLiveInputsStable(repositoryPath, inventory, capturedBytes);
            stagingCaptureHandle.Dispose();
            stagingCaptureHandle = null;
            return new CommonModulesPackageSnapshot(
                staging,
                package,
                capturedBytes,
                cleanupObserver);
        }
        catch (Exception captureError)
        {
            stagingCaptureHandle?.Dispose();
            stagingCaptureHandle = null;
            if (staging is not null)
            {
                var cleanup = CleanupStagingDirectory(staging, cleanupObserver);
                if (!cleanup.Deleted)
                {
                    throw new CommonModulesPackageSnapshotRetainedException(
                        captureError,
                        cleanup);
                }
            }

            throw;
        }
        finally
        {
            stagingCaptureHandle?.Dispose();
        }
    }

    internal static CommonModulesPackageSnapshotCleanupResult CleanupStagingDirectory(
        CommonModulesPackageSnapshotStagingEvidence staging,
        ICommonModulesPackageSnapshotCleanupObserver cleanupObserver)
    {
        ArgumentNullException.ThrowIfNull(staging);
        ArgumentNullException.ThrowIfNull(cleanupObserver);
        ValidateStagingPath(staging.ScratchRoot, staging.Path);

        var removedFiles = new HashSet<string>(PathComparer);
        var retainedPaths = new HashSet<string>(PathComparer);
        var observationIncompletePaths = new HashSet<string>(PathComparer);
        for (var attempt = 1; attempt <= CleanupAttempts; attempt++)
        {
            retainedPaths.Clear();
            observationIncompletePaths.Clear();
            var retryable = false;
            foreach (var file in staging.Files)
            {
                if (removedFiles.Contains(file.Path))
                {
                    continue;
                }

                var fileCleanup = TryDeleteOwnedFile(file, cleanupObserver);
                if (fileCleanup.Removed)
                {
                    removedFiles.Add(file.Path);
                    continue;
                }

                retainedPaths.Add(file.Path);
                if (!fileCleanup.Conclusive)
                {
                    observationIncompletePaths.Add(file.Path);
                    retryable = true;
                }
            }

            if (removedFiles.Count == staging.Files.Count)
            {
                var directoryCleanup = TryDeleteOwnedDirectory(
                    staging,
                    cleanupObserver);
                if (directoryCleanup.Removed)
                {
                    return new CommonModulesPackageSnapshotCleanupResult(
                        Deleted: true,
                        RetainedPath: null);
                }

                retainedPaths.UnionWith(directoryCleanup.RetainedPaths);
                if (!directoryCleanup.Conclusive)
                {
                    observationIncompletePaths.Add(staging.Path);
                    retryable = true;
                }
            }
            else
            {
                retainedPaths.Add(staging.Path);
            }

            if (!retryable || attempt == CleanupAttempts)
            {
                break;
            }

            Thread.Sleep(CleanupRetryDelay);
        }

        return new CommonModulesPackageSnapshotCleanupResult(
            Deleted: false,
            RetainedPath: staging.Path)
        {
            RetainedEntryPaths = SortPaths(retainedPaths.Append(staging.Path)),
            ObservationIncompletePaths = SortPaths(observationIncompletePaths)
        };
    }

    private static CreatedStagingDirectory CreateStagingDirectory(string scratchRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "CommonModules snapshot identity requires Windows file handles.");
        }

        Directory.CreateDirectory(scratchRoot);
        using var scratchHandle = OpenDirectory(
            scratchRoot,
            GenericRead,
            FileShareRead | FileShareWrite | FileShareDelete);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var name = Guid.NewGuid().ToString("N");
            var stagingPath = Path.Combine(scratchRoot, name);
            var handle = CreateDirectoryRelative(scratchHandle, name, stagingPath);
            if (handle is null)
            {
                continue;
            }

            try
            {
                var information = ReadDirectoryInformation(handle, stagingPath);
                if (information.IsReparsePoint)
                {
                    throw new IOException(
                        $"The CommonModules snapshot staging directory is a reparse point: '{stagingPath}'.");
                }

                using var routeHandle = OpenDirectory(
                    stagingPath,
                    GenericRead,
                    FileShareRead | FileShareWrite | FileShareDelete);
                var routeInformation = ReadDirectoryInformation(
                    routeHandle,
                    stagingPath);
                if (routeInformation.IsReparsePoint
                    || routeInformation.Identity != information.Identity)
                {
                    throw new IOException(
                        $"The CommonModules snapshot staging route changed while it was created: '{stagingPath}'.");
                }

                return new CreatedStagingDirectory(
                    handle,
                    new CommonModulesPackageSnapshotStagingEvidence(
                        scratchRoot,
                        stagingPath,
                        information.Identity));
            }
            catch
            {
                _ = TrySetDeleteDisposition(handle);
                handle.Dispose();
                throw;
            }
        }

        throw new IOException(
            $"A unique CommonModules snapshot staging directory could not be created beneath '{scratchRoot}'.");
    }

    private static SafeFileHandle? CreateDirectoryRelative(
        SafeFileHandle scratchHandle,
        string name,
        string stagingPath)
    {
        var nameBuffer = Marshal.StringToHGlobalUni(name);
        var unicodeStringBuffer = IntPtr.Zero;
        try
        {
            var nameBytes = checked((ushort)(name.Length * sizeof(char)));
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
                RootDirectory = scratchHandle.DangerousGetHandle(),
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
                $"The CommonModules snapshot staging directory could not be created: '{stagingPath}'.");
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

    private static void CreateStagingFile(
        CommonModulesPackageSnapshotStagingEvidence staging,
        string fileName,
        byte[] content)
    {
        var path = Path.Combine(staging.Path, fileName);
        using var handle = CreateFile(
            path,
            GenericRead | GenericWrite,
            FileShareRead,
            IntPtr.Zero,
            CreateNew,
            FileAttributeNormal | FileFlagSequentialScan | FileFlagWriteThrough,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw CreateFileOpenException(
                handle,
                path,
                "could not be created");
        }

        var evidence = new CommonModulesPackageSnapshotFileEvidence(
            path,
            identity: null,
            content.ToArray());
        staging.Files.Add(evidence);
        var before = ReadFileInformation(handle, path);
        evidence.Identity = before.Identity;
        RandomAccess.Write(handle, content, 0);
        RandomAccess.FlushToDisk(handle);
        var after = ReadFileInformation(handle, path);
        if (before.Identity != after.Identity
            || after.Length != content.LongLength
            || !HandleBytesMatch(handle, content))
        {
            throw new IOException(
                $"The CommonModules snapshot file changed while it was staged: '{path}'.");
        }
    }

    private static CleanupAttempt TryDeleteOwnedFile(
        CommonModulesPackageSnapshotFileEvidence evidence,
        ICommonModulesPackageSnapshotCleanupObserver cleanupObserver)
    {
        if (evidence.Identity is null)
        {
            return CleanupAttempt.Inconclusive(evidence.Path);
        }

        try
        {
            using var handle = OpenFileForCleanup(evidence.Path);
            var information = ReadFileInformation(handle, evidence.Path);
            if (information.Identity != evidence.Identity
                || information.Length != evidence.Bytes.LongLength
                || !HandleBytesMatch(handle, evidence.Bytes))
            {
                return CleanupAttempt.Changed(evidence.Path);
            }

            cleanupObserver.OnProofComplete(evidence.Path);
            return TrySetDeleteDisposition(handle)
                ? CleanupAttempt.Deleted()
                : CleanupAttempt.Inconclusive(evidence.Path);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
        {
            return CleanupAttempt.Deleted();
        }
        catch (FileNotFoundException)
        {
            return CleanupAttempt.Deleted();
        }
        catch (DirectoryNotFoundException)
        {
            return CleanupAttempt.Deleted();
        }
        catch (Exception exception) when (IsCleanupObservationFailure(exception))
        {
            return CleanupAttempt.Inconclusive(evidence.Path);
        }
    }

    private static CleanupAttempt TryDeleteOwnedDirectory(
        CommonModulesPackageSnapshotStagingEvidence staging,
        ICommonModulesPackageSnapshotCleanupObserver cleanupObserver)
    {
        try
        {
            using var handle = OpenDirectory(
                staging.Path,
                GenericRead | DeleteAccess,
                FileShareRead);
            var information = ReadDirectoryInformation(handle, staging.Path);
            if (information.IsReparsePoint || information.Identity != staging.Identity)
            {
                return CleanupAttempt.Changed(staging.Path);
            }

            var entries = Directory.EnumerateFileSystemEntries(staging.Path)
                .Select(Path.GetFullPath)
                .ToArray();
            if (entries.Length > 0)
            {
                return CleanupAttempt.Changed(entries.Append(staging.Path));
            }

            cleanupObserver.OnProofComplete(staging.Path);
            if (TrySetDeleteDisposition(handle))
            {
                return CleanupAttempt.Deleted();
            }

            var error = Marshal.GetLastWin32Error();
            return error == 145
                ? CleanupAttempt.Changed(ReadRetainedDirectoryEntries(staging.Path))
                : CleanupAttempt.Inconclusive(staging.Path);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
        {
            return CleanupAttempt.Deleted();
        }
        catch (DirectoryNotFoundException)
        {
            return CleanupAttempt.Deleted();
        }
        catch (Exception exception) when (IsCleanupObservationFailure(exception))
        {
            return CleanupAttempt.Inconclusive(staging.Path);
        }
    }

    private static IReadOnlyList<string> ReadRetainedDirectoryEntries(string path)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(path)
                .Select(Path.GetFullPath)
                .Append(path)
                .ToArray();
        }
        catch (Exception exception) when (IsCleanupObservationFailure(exception))
        {
            return [path];
        }
    }

    private static SafeFileHandle OpenFileForCleanup(string path)
    {
        var handle = CreateFile(
            path,
            GenericRead | DeleteAccess,
            FileShareRead,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal | FileFlagSequentialScan | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        return !handle.IsInvalid
            ? handle
            : throw CreateFileOpenException(handle, path, "could not be opened for cleanup");
    }

    private static SafeFileHandle OpenDirectory(
        string path,
        uint desiredAccess,
        uint shareMode)
    {
        var handle = CreateFile(
            path,
            desiredAccess,
            shareMode,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        return !handle.IsInvalid
            ? handle
            : throw CreateFileOpenException(handle, path, "directory handle could not be opened");
    }

    private static Win32Exception CreateFileOpenException(
        SafeFileHandle handle,
        string path,
        string action)
    {
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        return new Win32Exception(
            error,
            $"The CommonModules snapshot path {action}: '{path}'.");
    }

    private static FileInformation ReadFileInformation(
        SafeFileHandle handle,
        string path)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"The CommonModules snapshot file identity could not be read: '{path}'.");
        }

        if ((information.FileAttributes & FileAttributeDirectory) != 0
            || (information.FileAttributes & FileAttributeReparsePoint) != 0)
        {
            throw new IOException(
                $"The CommonModules snapshot path is not an ordinary file: '{path}'.");
        }

        return new FileInformation(
            ReadIdentity(information),
            ((long)information.FileSizeHigh << 32) | information.FileSizeLow);
    }

    private static DirectoryInformation ReadDirectoryInformation(
        SafeFileHandle handle,
        string path)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"The CommonModules snapshot directory identity could not be read: '{path}'.");
        }

        if ((information.FileAttributes & FileAttributeDirectory) == 0)
        {
            throw new IOException(
                $"The CommonModules snapshot staging path is not a directory: '{path}'.");
        }

        return new DirectoryInformation(
            ReadIdentity(information),
            (information.FileAttributes & FileAttributeReparsePoint) != 0);
    }

    private static CommonModulesPackageSnapshotObjectIdentity ReadIdentity(
        ByHandleFileInformation information)
        => new(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);

    private static bool HandleBytesMatch(
        SafeFileHandle handle,
        byte[] expected)
    {
        var buffer = new byte[Math.Min(64 * 1024, Math.Max(expected.Length, 1))];
        var offset = 0;
        while (offset < expected.Length)
        {
            var requested = Math.Min(buffer.Length, expected.Length - offset);
            var read = RandomAccess.Read(handle, buffer.AsSpan(0, requested), offset);
            if (read == 0)
            {
                return false;
            }

            if (!buffer.AsSpan(0, read).SequenceEqual(expected.AsSpan(offset, read)))
            {
                return false;
            }

            offset += read;
        }

        var extra = RandomAccess.Read(handle, buffer.AsSpan(0, 1), expected.Length);
        if (extra > 0)
        {
            return false;
        }

        return true;
    }

    private static bool TrySetDeleteDisposition(SafeFileHandle handle)
    {
        var disposition = new FileDispositionInformation { DeleteFile = true };
        return SetFileInformationByHandle(
            handle,
            FileDispositionInfoClass,
            ref disposition,
            (uint)Marshal.SizeOf<FileDispositionInformation>());
    }

    private static bool IsCleanupObservationFailure(Exception exception)
        => exception is Win32Exception
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or NotSupportedException
            or System.Security.SecurityException;

    private static void ValidateStagingPath(string scratchRoot, string stagingPath)
    {
        var absoluteScratchRoot = Path.GetFullPath(scratchRoot);
        var absoluteStagingPath = Path.GetFullPath(stagingPath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(
                Path.GetDirectoryName(absoluteStagingPath),
                absoluteScratchRoot,
                comparison)
            || !Guid.TryParseExact(Path.GetFileName(absoluteStagingPath), "N", out _))
        {
            throw new InvalidOperationException(
                $"CommonModules package snapshot must be a direct GUID child of its scratch root: {absoluteStagingPath}");
        }
    }

    private static IReadOnlyList<string> SortPaths(IEnumerable<string> paths)
        => paths
            .Distinct(PathComparer)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<FileInfo> ReadInventory(string repositoryPath)
    {
        try
        {
            var repository = new DirectoryInfo(repositoryPath);
            if (!repository.Exists)
            {
                throw new CommonModulesManifestException(
                    $"CommonModulesRepository was not found: {repositoryPath}");
            }

            if (repository.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new CommonModulesManifestException(
                    $"CommonModules package root must be an ordinary directory: {repositoryPath}");
            }

            var inventory = new List<FileInfo>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in repository.EnumerateFileSystemInfos(
                "*",
                new EnumerationOptions
                {
                    AttributesToSkip = 0,
                    IgnoreInaccessible = false,
                    RecurseSubdirectories = false,
                    ReturnSpecialDirectories = false
                }))
            {
                if (!names.Add(entry.Name))
                {
                    throw new CommonModulesManifestException(
                        $"CommonModules package contains case-insensitive duplicate entry '{entry.Name}'.");
                }

                if (entry is not FileInfo file
                    || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new CommonModulesManifestException(
                        $"CommonModules package entry must be an ordinary file: {entry.FullName}");
                }

                inventory.Add(file);
            }

            inventory.Sort((left, right) =>
                StringComparer.Ordinal.Compare(left.Name, right.Name));
            return inventory;
        }
        catch (CommonModulesManifestException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CommonModulesManifestException(
                $"CommonModules package inventory could not be read: {repositoryPath}");
        }
    }

    private static byte[] ReadExactBytes(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CommonModulesManifestException(
                $"CommonModules package entry could not be read: {path}");
        }
    }

    private static void ProveLiveInputsStable(
        string repositoryPath,
        IReadOnlyList<FileInfo> capturedInventory,
        IReadOnlyDictionary<string, byte[]> capturedBytes)
    {
        var currentInventory = ReadInventory(repositoryPath);
        if (capturedInventory.Count != currentInventory.Count
            || !capturedInventory.Select(entry => entry.Name).SequenceEqual(
                currentInventory.Select(entry => entry.Name),
                StringComparer.Ordinal))
        {
            throw PackageChanged();
        }

        foreach (var currentEntry in currentInventory)
        {
            if (!ReadExactBytes(currentEntry.FullName).AsSpan().SequenceEqual(
                    capturedBytes[currentEntry.Name]))
            {
                throw PackageChanged();
            }
        }
    }

    private static CommonModulesManifestException PackageChanged()
        => new(
            "CommonModules package changed while its immutable snapshot was being captured. "
            + "No source or manifest changes were made. Rerun the command.");

    private static CommonModulesPackage FreezePackage(CommonModulesPackage package)
        => new(Array.AsReadOnly(package.Entries
            .Select(entry => new CommonModuleManifestEntry(
                entry.ModuleFile,
                Array.AsReadOnly(entry.Categories.ToArray()),
                Array.AsReadOnly(entry.Dependencies.ToArray()),
                Array.AsReadOnly(entry.RequiredReferences.ToArray())))
            .ToArray()));

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

    private sealed record CreatedStagingDirectory(
        SafeFileHandle Handle,
        CommonModulesPackageSnapshotStagingEvidence Evidence);

    private readonly record struct FileInformation(
        CommonModulesPackageSnapshotObjectIdentity Identity,
        long Length);

    private readonly record struct DirectoryInformation(
        CommonModulesPackageSnapshotObjectIdentity Identity,
        bool IsReparsePoint);

    private sealed record CleanupAttempt(
        bool Removed,
        bool Conclusive,
        IReadOnlyList<string> RetainedPaths)
    {
        public static CleanupAttempt Deleted() => new(true, true, []);

        public static CleanupAttempt Changed(string path)
            => Changed([path]);

        public static CleanupAttempt Changed(IEnumerable<string> paths)
            => new(false, true, paths.ToArray());

        public static CleanupAttempt Inconclusive(string path)
            => new(false, false, [path]);
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

    private sealed class NoOpCommonModulesPackageSnapshotCleanupObserver
        : ICommonModulesPackageSnapshotCleanupObserver
    {
        public static NoOpCommonModulesPackageSnapshotCleanupObserver Instance { get; } = new();

        public void OnProofComplete(string path)
        {
        }
    }
}

internal sealed class CommonModulesPackageSnapshotStagingEvidence
{
    public CommonModulesPackageSnapshotStagingEvidence(
        string scratchRoot,
        string path,
        CommonModulesPackageSnapshotObjectIdentity identity)
    {
        ScratchRoot = System.IO.Path.GetFullPath(scratchRoot);
        Path = System.IO.Path.GetFullPath(path);
        Identity = identity;
    }

    public string ScratchRoot { get; }

    public string Path { get; }

    public CommonModulesPackageSnapshotObjectIdentity Identity { get; }

    public List<CommonModulesPackageSnapshotFileEvidence> Files { get; } = [];
}

internal sealed class CommonModulesPackageSnapshotFileEvidence(
    string path,
    CommonModulesPackageSnapshotObjectIdentity? identity,
    byte[] bytes)
{
    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public CommonModulesPackageSnapshotObjectIdentity? Identity { get; set; } = identity;

    public byte[] Bytes { get; } = bytes;
}

internal readonly record struct CommonModulesPackageSnapshotObjectIdentity(
    ulong VolumeSerialNumber,
    ulong FileId);

/// <summary>
/// Reports whether bounded cleanup removed an invocation-owned CommonModules snapshot.
/// </summary>
/// <param name="Deleted">Whether the snapshot workspace is conclusively absent.</param>
/// <param name="RetainedPath">The retained absolute path when deletion did not complete.</param>
public sealed record CommonModulesPackageSnapshotCleanupResult(
    bool Deleted,
    string? RetainedPath)
{
    /// <summary>
    /// Gets the exact retained staging entries observed during cleanup.
    /// </summary>
    public IReadOnlyList<string> RetainedEntryPaths { get; init; } = [];

    /// <summary>
    /// Gets retained paths whose identity or state could not be proved conclusively.
    /// </summary>
    public IReadOnlyList<string> ObservationIncompletePaths { get; init; } = [];

    /// <summary>
    /// Gets whether every retained entry was observed conclusively.
    /// </summary>
    public bool IsConclusive => ObservationIncompletePaths.Count == 0;
}

/// <summary>
/// Reports capture failure together with the structured cleanup evidence for a retained snapshot.
/// </summary>
public sealed class CommonModulesPackageSnapshotRetainedException
    : InvalidOperationException
{
    /// <summary>
    /// Creates a retained-snapshot failure.
    /// </summary>
    public CommonModulesPackageSnapshotRetainedException(
        Exception captureFailure,
        CommonModulesPackageSnapshotCleanupResult cleanupResult)
        : base(
            $"{captureFailure.Message} The CommonModules package snapshot staging directory "
            + $"could not be removed: '{cleanupResult.RetainedPath}'.",
            captureFailure)
    {
        CleanupResult = cleanupResult;
    }

    /// <summary>
    /// Gets the exact cleanup result that caused the workspace to be retained.
    /// </summary>
    public CommonModulesPackageSnapshotCleanupResult CleanupResult { get; }
}

/// <summary>
/// Owns the staged package bytes and exposes planning and file reads that cannot consult the live repository.
/// </summary>
public sealed class CommonModulesPackageSnapshot : IDisposable
{
    private readonly CommonModulesPackageSnapshotStagingEvidence staging;
    private readonly CommonModulesPackage package;
    private readonly IReadOnlyDictionary<string, byte[]> capturedBytes;
    private readonly ICommonModulesPackageSnapshotCleanupObserver cleanupObserver;
    private CommonModulesPackageSnapshotCleanupResult? cleanupResult;

    internal CommonModulesPackageSnapshot(
        CommonModulesPackageSnapshotStagingEvidence staging,
        CommonModulesPackage package,
        IReadOnlyDictionary<string, byte[]> capturedBytes,
        ICommonModulesPackageSnapshotCleanupObserver cleanupObserver)
    {
        this.staging = staging;
        this.package = package;
        this.capturedBytes = capturedBytes;
        this.cleanupObserver = cleanupObserver;
    }

    /// <summary>
    /// Gets the invocation-owned staging directory containing the validated package bytes.
    /// </summary>
    public string StagingPath => staging.Path;

    /// <summary>
    /// Gets the canonical manifest entries parsed exclusively from the staged manifest bytes.
    /// </summary>
    public IReadOnlyList<CommonModuleManifestEntry> Entries
    {
        get
        {
            ThrowIfDisposed();
            return package.Entries;
        }
    }

    /// <summary>
    /// Resolves a dependency and reference plan using only the manifest parsed from staged bytes.
    /// </summary>
    public CommonModulesSelectionPlan ResolveRequestedPlan(
        IReadOnlyList<string> requestedModules)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(requestedModules);
        return CommonModulesDependencyResolver.ResolveRequestedPlan(
            package.Entries,
            requestedModules);
    }

    /// <summary>
    /// Returns a copy of one exact package file captured in staging.
    /// </summary>
    public byte[] ReadFileBytes(string fileName)
    {
        if (!TryReadFileBytes(fileName, out var content))
        {
            throw new CommonModulesManifestException(
                $"CommonModules snapshot file was not found: {fileName}");
        }

        return content;
    }

    /// <summary>
    /// Tries to return a copy of one exact package file captured in staging.
    /// </summary>
    public bool TryReadFileBytes(string fileName, out byte[] content)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (capturedBytes.TryGetValue(fileName, out var capturedContent))
        {
            content = capturedContent.ToArray();
            return true;
        }

        content = [];
        return false;
    }

    /// <summary>
    /// Applies bounded deletion retries and reports a retained path without changing transaction outcome policy.
    /// </summary>
    public CommonModulesPackageSnapshotCleanupResult Cleanup()
    {
        cleanupResult ??= CommonModulesPackageSnapshotFactory.CleanupStagingDirectory(
            staging,
            cleanupObserver);
        return cleanupResult;
    }

    /// <inheritdoc />
    public void Dispose() => _ = Cleanup();

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(cleanupResult is not null, this);
    }
}
