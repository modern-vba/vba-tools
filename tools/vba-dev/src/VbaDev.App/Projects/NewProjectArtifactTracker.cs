using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using VbaDev.App.Workbooks;
using VbaDev.Domain;

namespace VbaDev.App.Projects;

internal interface INewProjectArtifactRollbackObserver
{
    void OnProofComplete(string path);
}

internal interface INewProjectDirectoryCreationObserver
{
    void OnDirectoryCreated(string path);
}

internal sealed class NewProjectArtifactTracker
{
    private const uint GenericRead = 0x80000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileReadAttributes = 0x00000080;
    private const uint SynchronizeAccess = 0x00100000;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
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
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly List<CreatedFile> createdFiles = [];
    private readonly List<CreatedDirectory> createdDirectories = [];
    private readonly HashSet<string> removedOwnedPaths = new(PathComparer);
    private readonly Func<string, SafeFileHandle?> createDirectoryExclusively;
    private readonly INewProjectDirectoryCreationObserver directoryCreationObserver;
    private readonly INewProjectArtifactRollbackObserver rollbackObserver;
    private readonly IFileSystemPathIdentityResolver pathIdentityResolver =
        new FileSystemPathIdentityResolver();
    private string? allowedLeaseMarkerPath;

    public NewProjectArtifactTracker()
        : this(
            CreateDirectoryExclusively,
            NoOpNewProjectArtifactRollbackObserver.Instance,
            NoOpNewProjectDirectoryCreationObserver.Instance)
    {
    }

    internal NewProjectArtifactTracker(Action<string> createDirectoryExclusively)
        : this(
            AdaptDirectoryCreator(createDirectoryExclusively),
            NoOpNewProjectArtifactRollbackObserver.Instance,
            NoOpNewProjectDirectoryCreationObserver.Instance)
    {
    }

    internal NewProjectArtifactTracker(
        INewProjectDirectoryCreationObserver directoryCreationObserver)
        : this(
            CreateDirectoryExclusively,
            NoOpNewProjectArtifactRollbackObserver.Instance,
            directoryCreationObserver)
    {
    }

    internal NewProjectArtifactTracker(
        Action<string> createDirectoryExclusively,
        INewProjectArtifactRollbackObserver rollbackObserver)
        : this(
            AdaptDirectoryCreator(createDirectoryExclusively),
            rollbackObserver,
            NoOpNewProjectDirectoryCreationObserver.Instance)
    {
    }

    private NewProjectArtifactTracker(
        Func<string, SafeFileHandle?> createDirectoryExclusively,
        INewProjectArtifactRollbackObserver rollbackObserver,
        INewProjectDirectoryCreationObserver directoryCreationObserver)
    {
        this.createDirectoryExclusively = createDirectoryExclusively;
        this.rollbackObserver = rollbackObserver;
        this.directoryCreationObserver = directoryCreationObserver;
    }

    public void EnsureDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var missingDirectories = new Stack<string>();
        var current = fullPath;
        while (!Directory.Exists(current))
        {
            if (File.Exists(current))
            {
                throw new IOException($"New project directory path is occupied by a file: {current}");
            }

            missingDirectories.Push(current);
            var parent = Path.GetDirectoryName(
                Path.TrimEndingDirectorySeparator(current));
            if (string.IsNullOrEmpty(parent)
                || parent.Equals(current, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"New project directory could not be created safely: {fullPath}");
            }

            current = parent;
        }

        foreach (var missingDirectory in missingDirectories)
        {
            using var creationHandle = createDirectoryExclusively(missingDirectory);
            var createdDirectory = new CreatedDirectory(missingDirectory);
            createdDirectories.Add(createdDirectory);
            directoryCreationObserver.OnDirectoryCreated(missingDirectory);
            createdDirectory.Identity = creationHandle is null
                ? pathIdentityResolver.Resolve(missingDirectory)
                : ReadCreatedDirectoryIdentity(creationHandle, missingDirectory);
            createdDirectory.IdentityConclusive = true;
        }
    }

    public void RecordCreatedFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        RejectDuplicateTrackedPath(fullPath);
        createdFiles.Add(CaptureStableFile(fullPath));
    }

    public void RecordCreatedFile(InitialWorkbookArtifactEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var fullPath = Path.GetFullPath(evidence.WorkbookPath);
        RejectDuplicateTrackedPath(fullPath);
        var trustedEvidence = evidence with { WorkbookPath = fullPath };
        var file = new CreatedFile(fullPath)
        {
            Identity = new FileSystemPathIdentity(
                fullPath,
                fullPath,
                trustedEvidence.ObjectIdentity),
            WorkbookEvidence = trustedEvidence,
            SnapshotConclusive = true
        };
        createdFiles.Add(file);

        var observation = ObserveFile(file);
        if (observation != ArtifactObservation.Unchanged)
        {
            throw new NewProjectArtifactEvidenceMismatchException(
                fullPath,
                observation == ArtifactObservation.Inconclusive);
        }
    }

    public void CreateFile(string path, ReadOnlyMemory<byte> contents)
    {
        var fullPath = Path.GetFullPath(path);
        RejectDuplicateTrackedPath(fullPath);
        var file = new CreatedFile(fullPath);
        FileStream stream;
        try
        {
            stream = new FileStream(
                fullPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.WriteThrough);
        }
        catch (IOException ex) when (File.Exists(fullPath) || Directory.Exists(fullPath))
        {
            throw new NewProjectArtifactAlreadyExistsException(fullPath, ex);
        }

        createdFiles.Add(file);
        try
        {
            using (stream)
            {
                file.Identity = pathIdentityResolver.Resolve(fullPath);
                stream.Write(contents.Span);
                stream.Flush(flushToDisk: true);
                var identityAfterWrite = pathIdentityResolver.Resolve(fullPath);
                if (!HasSameObjectIdentity(file.Identity, identityAfterWrite))
                {
                    throw new IOException(
                        $"The created project artifact identity changed while it was written: {fullPath}");
                }

                file.Contents = contents.ToArray();
                file.SnapshotConclusive = true;
            }
        }
        catch
        {
            TryCapturePartialFile(file);
            throw;
        }
    }

    public NewProjectTargetInventoryResult ProveCompleteTargetInventory(
        string targetRoot,
        string allowedLeaseMarkerPath)
    {
        var fullTargetRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(targetRoot));
        var fullLeaseMarkerPath = AllowLeaseMarker(
            fullTargetRoot,
            allowedLeaseMarkerPath);

        var expectedPaths = new HashSet<string>(PathComparer)
        {
            fullLeaseMarkerPath
        };
        foreach (var file in createdFiles)
        {
            if (IsDescendant(file.Path, fullTargetRoot))
            {
                expectedPaths.Add(file.Path);
            }
        }

        foreach (var directory in createdDirectories)
        {
            if (IsDescendant(directory.Path, fullTargetRoot))
            {
                expectedPaths.Add(directory.Path);
            }
        }

        var targetEnumeration = EnumerateTargetEntries(fullTargetRoot);
        var actualPaths = targetEnumeration.Paths;
        var fileObservations = createdFiles
            .Where(file => IsSameOrDescendant(file.Path, fullTargetRoot))
            .Select(file => new PathObservation(file.Path, ObserveFile(file)))
            .ToArray();
        var directoryObservations = createdDirectories
            .Where(directory => IsSameOrDescendant(directory.Path, fullTargetRoot))
            .Select(directory => new PathObservation(
                directory.Path,
                ObserveDirectory(directory)))
            .ToArray();
        var markerObservation = ObserveAllowedLeaseMarker(fullLeaseMarkerPath);
        var observationIncompletePaths = targetEnumeration.ObservationIncompletePaths
            .Concat(fileObservations
                .Where(observation => observation.Observation == ArtifactObservation.Inconclusive)
                .Select(observation => observation.Path))
            .Concat(directoryObservations
                .Where(observation => observation.Observation == ArtifactObservation.Inconclusive)
                .Select(observation => observation.Path))
            .Concat(markerObservation == ArtifactObservation.Inconclusive
                ? [fullLeaseMarkerPath]
                : []);
        var targetChangedPaths = expectedPaths
            .Except(actualPaths, PathComparer)
            .Concat(actualPaths.Except(expectedPaths, PathComparer))
            .Concat(fileObservations
                .Where(observation => observation.Observation != ArtifactObservation.Unchanged)
                .Select(observation => observation.Path))
            .Concat(directoryObservations
                .Where(observation => observation.Observation != ArtifactObservation.Unchanged)
                .Select(observation => observation.Path))
            .Concat(markerObservation == ArtifactObservation.Unchanged
                ? []
                : [fullLeaseMarkerPath])
            .Concat(observationIncompletePaths);
        return new NewProjectTargetInventoryResult(
            SortPaths(targetChangedPaths),
            SortPaths(observationIncompletePaths));
    }

    public string AllowLeaseMarker(
        string targetRoot,
        string leaseMarkerPath)
    {
        var fullTargetRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(targetRoot));
        var fullLeaseMarkerPath = Path.GetFullPath(leaseMarkerPath);
        if (!Path.GetDirectoryName(fullLeaseMarkerPath)!.Equals(
                fullTargetRoot,
                PathComparison))
        {
            throw new ArgumentException(
                "The allowed lease marker must be an immediate child of the target root.",
                nameof(leaseMarkerPath));
        }

        this.allowedLeaseMarkerPath = fullLeaseMarkerPath;
        return fullLeaseMarkerPath;
    }

    public NewProjectRollbackResult Rollback()
        => Rollback(RollbackScope.AllTrackedArtifacts, targetRoot: null);

    public NewProjectRollbackResult RollbackUnderLease(string targetRoot)
        => Rollback(
            RollbackScope.UnderLease,
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetRoot)));

    public NewProjectRollbackResult RollbackAfterLeaseRelease(string targetRoot)
        => Rollback(
            RollbackScope.AfterLeaseRelease,
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetRoot)));

    private NewProjectRollbackResult Rollback(
        RollbackScope scope,
        string? targetRoot)
    {
        var targetChangedPaths = new HashSet<string>(PathComparer);
        var cleanupIncompletePaths = new HashSet<string>(PathComparer);
        foreach (var file in createdFiles.AsEnumerable().Reverse())
        {
            if (removedOwnedPaths.Contains(file.Path))
            {
                continue;
            }

            if (!ShouldDeleteFile(scope, file.Path, targetRoot))
            {
                ClassifyRetainedFile(
                    file,
                    targetChangedPaths,
                    cleanupIncompletePaths);
                continue;
            }

            var observation = ObserveFile(file);
            if (observation == ArtifactObservation.Missing
                || observation == ArtifactObservation.Changed)
            {
                targetChangedPaths.Add(file.Path);
                continue;
            }

            if (observation == ArtifactObservation.Inconclusive)
            {
                cleanupIncompletePaths.Add(file.Path);
                continue;
            }

            if (OperatingSystem.IsWindows())
            {
                var exactDelete = TryDeleteFileExactly(file);
                if (exactDelete == ExactDeleteResult.Removed)
                {
                    removedOwnedPaths.Add(file.Path);
                }
                else if (exactDelete is ExactDeleteResult.Missing
                    or ExactDeleteResult.Changed)
                {
                    targetChangedPaths.Add(file.Path);
                }
                else
                {
                    cleanupIncompletePaths.Add(file.Path);
                }

                continue;
            }

            try
            {
                rollbackObserver.OnProofComplete(file.Path);
                File.Delete(file.Path);
            }
            catch (Exception ex) when (IsFileSystemObservationFailure(ex))
            {
                ClassifyRetainedFile(
                    file,
                    targetChangedPaths,
                    cleanupIncompletePaths);
                continue;
            }

            var afterDelete = ObserveFile(file);
            if (afterDelete == ArtifactObservation.Missing)
            {
                removedOwnedPaths.Add(file.Path);
            }
            else if (afterDelete == ArtifactObservation.Changed)
            {
                targetChangedPaths.Add(file.Path);
            }
            else
            {
                cleanupIncompletePaths.Add(file.Path);
            }
        }

        foreach (var directory in createdDirectories.AsEnumerable().Reverse())
        {
            if (removedOwnedPaths.Contains(directory.Path))
            {
                continue;
            }

            var observation = ObserveDirectory(directory);
            if (observation == ArtifactObservation.Missing
                || observation == ArtifactObservation.Changed)
            {
                targetChangedPaths.Add(directory.Path);
                continue;
            }

            if (observation == ArtifactObservation.Inconclusive)
            {
                cleanupIncompletePaths.Add(directory.Path);
                continue;
            }

            string[] entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory.Path)
                    .Select(Path.GetFullPath)
                    .ToArray();
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                targetChangedPaths.Add(directory.Path);
                continue;
            }
            catch (Exception ex) when (IsFileSystemObservationFailure(ex))
            {
                cleanupIncompletePaths.Add(directory.Path);
                continue;
            }

            if (entries.Length > 0)
            {
                foreach (var entry in entries)
                {
                    if (!IsKnownRetainedEntry(entry))
                    {
                        targetChangedPaths.Add(entry);
                    }
                }

                continue;
            }

            if (!ShouldDeleteDirectory(
                    scope,
                    directory.Path,
                    targetRoot))
            {
                if (scope == RollbackScope.AfterLeaseRelease)
                {
                    cleanupIncompletePaths.Add(directory.Path);
                }

                continue;
            }

            if (OperatingSystem.IsWindows())
            {
                var exactDelete = TryDeleteDirectoryExactly(directory);
                if (exactDelete.Result == ExactDeleteResult.Removed)
                {
                    removedOwnedPaths.Add(directory.Path);
                }
                else if (exactDelete.Result is ExactDeleteResult.Missing
                    or ExactDeleteResult.Changed)
                {
                    targetChangedPaths.Add(directory.Path);
                }
                else if (exactDelete.Result == ExactDeleteResult.NotEmpty)
                {
                    var unknownEntryFound = false;
                    foreach (var entry in exactDelete.Entries)
                    {
                        if (!IsKnownRetainedEntry(entry))
                        {
                            targetChangedPaths.Add(entry);
                            unknownEntryFound = true;
                        }
                    }

                    if (exactDelete.Entries.Count == 0 && !unknownEntryFound)
                    {
                        cleanupIncompletePaths.Add(directory.Path);
                    }
                }
                else
                {
                    cleanupIncompletePaths.Add(directory.Path);
                }

                continue;
            }

            try
            {
                rollbackObserver.OnProofComplete(directory.Path);
                Directory.Delete(directory.Path, recursive: false);
            }
            catch (Exception ex) when (IsFileSystemObservationFailure(ex))
            {
                ClassifyRetainedDirectory(
                    directory,
                    targetChangedPaths,
                    cleanupIncompletePaths);
                continue;
            }

            var afterDelete = ObserveDirectory(directory);
            if (afterDelete == ArtifactObservation.Missing)
            {
                removedOwnedPaths.Add(directory.Path);
            }
            else if (afterDelete == ArtifactObservation.Changed)
            {
                targetChangedPaths.Add(directory.Path);
            }
            else
            {
                cleanupIncompletePaths.Add(directory.Path);
            }
        }

        var retainedOwnedPaths = createdFiles
            .Where(file => !removedOwnedPaths.Contains(file.Path))
            .Where(file => ObserveFile(file) is ArtifactObservation.Unchanged
                or ArtifactObservation.Inconclusive)
            .Select(file => file.Path)
            .Concat(createdDirectories
                .Where(directory => !removedOwnedPaths.Contains(directory.Path))
                .Where(directory => ObserveDirectory(directory) is ArtifactObservation.Unchanged
                    or ArtifactObservation.Inconclusive)
                .Select(directory => directory.Path))
            .Concat(cleanupIncompletePaths);
        return new NewProjectRollbackResult(
            SortPaths(targetChangedPaths),
            SortPaths(cleanupIncompletePaths),
            SortPaths(retainedOwnedPaths));
    }

    private static bool ShouldDeleteFile(
        RollbackScope scope,
        string path,
        string? targetRoot)
        => scope == RollbackScope.AllTrackedArtifacts
            || scope == RollbackScope.UnderLease
                && IsSameOrDescendant(path, targetRoot!);

    private static bool ShouldDeleteDirectory(
        RollbackScope scope,
        string path,
        string? targetRoot)
        => scope switch
        {
            RollbackScope.AllTrackedArtifacts => true,
            RollbackScope.UnderLease => IsDescendant(path, targetRoot!),
            RollbackScope.AfterLeaseRelease => path.Equals(
                    targetRoot,
                    PathComparison)
                || IsDescendant(targetRoot!, path),
            _ => false
        };

    private void ClassifyRetainedFile(
        CreatedFile file,
        ISet<string> targetChangedPaths,
        ISet<string> cleanupIncompletePaths)
    {
        var observation = ObserveFile(file);
        if (observation is ArtifactObservation.Missing or ArtifactObservation.Changed)
        {
            targetChangedPaths.Add(file.Path);
        }
        else
        {
            cleanupIncompletePaths.Add(file.Path);
        }
    }

    private ExactDeleteResult TryDeleteFileExactly(CreatedFile file)
    {
        try
        {
            using var handle = OpenExistingFileForExactDelete(file.Path);
            if (!GetFileInformationByHandle(handle, out var information))
            {
                return ExactDeleteResult.Inconclusive;
            }

            if ((information.FileAttributes & FileAttributeDirectory) != 0
                || (information.FileAttributes & FileAttributeReparsePoint) != 0)
            {
                return ExactDeleteResult.Changed;
            }

            var identity = new FileSystemObjectIdentity(
                information.VolumeSerialNumber,
                ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
            if (file.Identity?.ObjectIdentity is null
                || identity != file.Identity.ObjectIdentity)
            {
                return ExactDeleteResult.Changed;
            }

            var length = ((long)information.FileSizeHigh << 32)
                | information.FileSizeLow;
            var expectedLength = file.WorkbookEvidence?.Length
                ?? file.Contents.LongLength;
            if (length != expectedLength)
            {
                return ExactDeleteResult.Changed;
            }

            var hash = ComputeHash(handle, length, file.Path);
            var expectedHash = file.WorkbookEvidence?.Sha256
                ?? Convert.ToHexString(SHA256.HashData(file.Contents))
                    .ToLowerInvariant();
            if (!Convert.ToHexString(hash).ToLowerInvariant().Equals(
                    expectedHash,
                    StringComparison.Ordinal))
            {
                return ExactDeleteResult.Changed;
            }

            rollbackObserver.OnProofComplete(file.Path);
            var disposition = new FileDispositionInformation { DeleteFile = true };
            return SetFileInformationByHandle(
                handle,
                FileDispositionInfoClass,
                ref disposition,
                (uint)Marshal.SizeOf<FileDispositionInformation>())
                    ? ExactDeleteResult.Removed
                    : ExactDeleteResult.Inconclusive;
        }
        catch (Win32Exception exception) when (
            exception.NativeErrorCode is 2 or 3)
        {
            return ExactDeleteResult.Missing;
        }
        catch (FileNotFoundException)
        {
            return ExactDeleteResult.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return ExactDeleteResult.Missing;
        }
        catch (Exception exception) when (
            exception is Win32Exception || IsFileSystemObservationFailure(exception))
        {
            return ExactDeleteResult.Inconclusive;
        }
    }

    private ExactDirectoryDeleteResult TryDeleteDirectoryExactly(
        CreatedDirectory directory)
    {
        try
        {
            using var handle = OpenExistingDirectoryForExactDelete(directory.Path);
            if (!GetFileInformationByHandle(handle, out var information))
            {
                return ExactDirectoryDeleteResult.Inconclusive();
            }

            if ((information.FileAttributes & FileAttributeDirectory) == 0
                || (information.FileAttributes & FileAttributeReparsePoint) != 0)
            {
                return ExactDirectoryDeleteResult.Changed();
            }

            var identity = new FileSystemObjectIdentity(
                information.VolumeSerialNumber,
                ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
            if (directory.Identity?.ObjectIdentity is null
                || identity != directory.Identity.ObjectIdentity)
            {
                return ExactDirectoryDeleteResult.Changed();
            }

            var entries = EnumerateDirectoryEntries(directory.Path);
            if (entries.Count > 0)
            {
                return ExactDirectoryDeleteResult.NotEmpty(entries);
            }

            rollbackObserver.OnProofComplete(directory.Path);
            var disposition = new FileDispositionInformation { DeleteFile = true };
            if (SetFileInformationByHandle(
                    handle,
                    FileDispositionInfoClass,
                    ref disposition,
                    (uint)Marshal.SizeOf<FileDispositionInformation>()))
            {
                return ExactDirectoryDeleteResult.Removed();
            }

            var retainedEntries = EnumerateDirectoryEntries(directory.Path);
            return retainedEntries.Count > 0
                ? ExactDirectoryDeleteResult.NotEmpty(retainedEntries)
                : ExactDirectoryDeleteResult.Inconclusive();
        }
        catch (Win32Exception exception) when (
            exception.NativeErrorCode is 2 or 3)
        {
            return ExactDirectoryDeleteResult.Missing();
        }
        catch (FileNotFoundException)
        {
            return ExactDirectoryDeleteResult.Missing();
        }
        catch (DirectoryNotFoundException)
        {
            return ExactDirectoryDeleteResult.Missing();
        }
        catch (Exception exception) when (
            exception is Win32Exception || IsFileSystemObservationFailure(exception))
        {
            return ExactDirectoryDeleteResult.Inconclusive();
        }
    }

    private static SafeFileHandle OpenExistingFileForExactDelete(string path)
    {
        var handle = CreateFile(
            path,
            GenericRead | DeleteAccess,
            FileShareRead,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal
                | FileFlagSequentialScan
                | FileFlagBackupSemantics
                | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            return handle;
        }

        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(
            error,
            $"The exact created project artifact could not be opened for rollback: {path}");
    }

    private static SafeFileHandle OpenExistingDirectoryForExactDelete(string path)
    {
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
            $"The exact created project directory could not be opened for rollback: {path}");
    }

    private static IReadOnlyList<string> EnumerateDirectoryEntries(string path)
        => Directory.EnumerateFileSystemEntries(path)
            .Select(Path.GetFullPath)
            .ToArray();

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
                    $"The created project artifact became shorter during rollback proof: {path}");
            }

            hash.AppendData(buffer, 0, read);
            offset += read;
        }

        return hash.GetHashAndReset();
    }

    private void ClassifyRetainedDirectory(
        CreatedDirectory directory,
        ISet<string> targetChangedPaths,
        ISet<string> cleanupIncompletePaths)
    {
        var observation = ObserveDirectory(directory);
        if (observation is ArtifactObservation.Missing or ArtifactObservation.Changed)
        {
            targetChangedPaths.Add(directory.Path);
        }
        else
        {
            cleanupIncompletePaths.Add(directory.Path);
        }
    }

    private bool IsKnownRetainedEntry(string path)
    {
        if (allowedLeaseMarkerPath is not null
            && path.Equals(allowedLeaseMarkerPath, PathComparison))
        {
            return true;
        }

        return createdFiles.Any(file => file.Path.Equals(path, PathComparison))
            || createdDirectories.Any(directory => directory.Path.Equals(path, PathComparison));
    }

    private static bool HasSameObjectIdentity(
        FileSystemPathIdentity expected,
        FileSystemPathIdentity current)
    {
        if (expected.ObjectIdentity is not null
            || current.ObjectIdentity is not null)
        {
            return expected.ObjectIdentity is not null
                && current.ObjectIdentity is not null
                && expected.ObjectIdentity == current.ObjectIdentity;
        }

        return FileSystemPathIdentityRelations.Same(expected, current);
    }

    private static TargetEnumeration EnumerateTargetEntries(string targetRoot)
    {
        var entries = new HashSet<string>(PathComparer);
        var observationIncompletePaths = new HashSet<string>(PathComparer);
        try
        {
            _ = File.GetAttributes(targetRoot);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new TargetEnumeration(entries, observationIncompletePaths);
        }
        catch (Exception ex) when (IsFileSystemObservationFailure(ex))
        {
            observationIncompletePaths.Add(targetRoot);
            return new TargetEnumeration(entries, observationIncompletePaths);
        }

        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(targetRoot);
        while (pendingDirectories.Count > 0)
        {
            var directory = pendingDirectories.Pop();
            string[] directoryEntries;
            try
            {
                directoryEntries = Directory.EnumerateFileSystemEntries(directory).ToArray();
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                continue;
            }
            catch (Exception ex) when (IsFileSystemObservationFailure(ex))
            {
                observationIncompletePaths.Add(directory);
                continue;
            }

            foreach (var entry in directoryEntries)
            {
                var fullPath = Path.GetFullPath(entry);
                entries.Add(fullPath);
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(fullPath);
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    continue;
                }
                catch (Exception ex) when (IsFileSystemObservationFailure(ex))
                {
                    observationIncompletePaths.Add(fullPath);
                    continue;
                }

                if (attributes.HasFlag(FileAttributes.Directory)
                    && !attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    pendingDirectories.Push(fullPath);
                }
            }
        }

        return new TargetEnumeration(entries, observationIncompletePaths);
    }

    private static bool IsDescendant(string path, string directory)
    {
        var directoryPrefix = directory.EndsWith(Path.DirectorySeparatorChar)
            ? directory
            : directory + Path.DirectorySeparatorChar;
        return path.StartsWith(directoryPrefix, PathComparison);
    }

    private static bool IsSameOrDescendant(string path, string directory)
        => path.Equals(directory, PathComparison)
            || IsDescendant(path, directory);

    private ArtifactObservation ObserveFile(CreatedFile file)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(file.Path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return ArtifactObservation.Missing;
        }
        catch (Exception ex) when (IsFileSystemObservationFailure(ex))
        {
            return ArtifactObservation.Inconclusive;
        }

        if (attributes.HasFlag(FileAttributes.Directory)
            || attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return ArtifactObservation.Changed;
        }

        if (!file.SnapshotConclusive || file.Identity is null)
        {
            return ArtifactObservation.Inconclusive;
        }

        try
        {
            var identityBeforeRead = pathIdentityResolver.Resolve(file.Path);
            if (!HasSameObjectIdentity(file.Identity, identityBeforeRead))
            {
                return ArtifactObservation.Changed;
            }

            var contents = File.ReadAllBytes(file.Path);
            var identityAfterRead = pathIdentityResolver.Resolve(file.Path);
            var contentsMatch = file.WorkbookEvidence is null
                ? contents.AsSpan().SequenceEqual(file.Contents)
                : contents.LongLength == file.WorkbookEvidence.Length
                    && string.Equals(
                        Convert.ToHexString(SHA256.HashData(contents)).ToLowerInvariant(),
                        file.WorkbookEvidence.Sha256,
                        StringComparison.Ordinal);
            return HasSameObjectIdentity(file.Identity, identityAfterRead)
                && contentsMatch
                    ? ArtifactObservation.Unchanged
                    : ArtifactObservation.Changed;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return ArtifactObservation.Missing;
        }
        catch (Exception ex) when (IsFileSystemObservationFailure(ex))
        {
            return ArtifactObservation.Inconclusive;
        }
    }

    private ArtifactObservation ObserveDirectory(CreatedDirectory directory)
    {
        try
        {
            var attributes = File.GetAttributes(directory.Path);
            if (!attributes.HasFlag(FileAttributes.Directory)
                || attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return ArtifactObservation.Changed;
            }

            if (!directory.IdentityConclusive || directory.Identity is null)
            {
                return ArtifactObservation.Inconclusive;
            }

            return HasSameObjectIdentity(
                directory.Identity,
                pathIdentityResolver.Resolve(directory.Path))
                    ? ArtifactObservation.Unchanged
                    : ArtifactObservation.Changed;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return ArtifactObservation.Missing;
        }
        catch (Exception ex) when (IsFileSystemObservationFailure(ex))
        {
            return ArtifactObservation.Inconclusive;
        }
    }

    private static ArtifactObservation ObserveAllowedLeaseMarker(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return !attributes.HasFlag(FileAttributes.Directory)
                && !attributes.HasFlag(FileAttributes.ReparsePoint)
                    ? ArtifactObservation.Unchanged
                    : ArtifactObservation.Changed;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return ArtifactObservation.Missing;
        }
        catch (Exception ex) when (IsFileSystemObservationFailure(ex))
        {
            return ArtifactObservation.Inconclusive;
        }
    }

    private static bool IsFileSystemObservationFailure(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or InvalidOperationException
            or ArgumentException
            or System.Security.SecurityException;

    private CreatedFile CaptureStableFile(string path)
    {
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.Directory)
            || attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException(
                $"The created project artifact is not an ordinary file: {path}");
        }

        var identityBeforeRead = pathIdentityResolver.Resolve(path);
        var contents = File.ReadAllBytes(path);
        var identityAfterRead = pathIdentityResolver.Resolve(path);
        if (!HasSameObjectIdentity(identityBeforeRead, identityAfterRead))
        {
            throw new IOException(
                $"The created project artifact identity changed while it was recorded: {path}");
        }

        return new CreatedFile(path)
        {
            Contents = contents,
            Identity = identityAfterRead,
            SnapshotConclusive = true
        };
    }

    private void TryCapturePartialFile(CreatedFile file)
    {
        try
        {
            var snapshot = CaptureStableFile(file.Path);
            if (file.Identity is not null
                && snapshot.Identity is not null
                && HasSameObjectIdentity(file.Identity, snapshot.Identity))
            {
                file.Contents = snapshot.Contents;
                file.SnapshotConclusive = true;
            }
        }
        catch (Exception ex) when (IsFileSystemObservationFailure(ex))
        {
            // The path remains tracked but intentionally inconclusive.
        }
    }

    private void RejectDuplicateTrackedPath(string path)
    {
        if (createdFiles.Any(file => file.Path.Equals(path, PathComparison))
            || createdDirectories.Any(directory => directory.Path.Equals(path, PathComparison)))
        {
            throw new InvalidOperationException(
                $"The project artifact path is already tracked: {path}");
        }
    }

    private static Func<string, SafeFileHandle?> AdaptDirectoryCreator(
        Action<string> createDirectoryExclusively)
    {
        ArgumentNullException.ThrowIfNull(createDirectoryExclusively);
        return path =>
        {
            createDirectoryExclusively(path);
            return null;
        };
    }

    private static SafeFileHandle? CreateDirectoryExclusively(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return CreateDirectoryWindowsExclusively(path);
        }

        const uint ownerGroupAndOtherRwx = 0x1ff;
        if (CreateDirectoryPortable(path, ownerGroupAndOtherRwx) == 0)
        {
            return null;
        }

        var portableError = Marshal.GetLastPInvokeError();
        throw new IOException(
            $"The project directory could not be created exclusively: {path}",
            new Win32Exception(portableError));
    }

    private static SafeFileHandle CreateDirectoryWindowsExclusively(string path)
    {
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
                FileShareRead,
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
            var error = checked((int)RtlNtStatusToDosError(status));
            throw new IOException(
                $"The project directory could not be created exclusively: {path}",
                new Win32Exception(error));
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

    private static FileSystemPathIdentity ReadCreatedDirectoryIdentity(
        SafeFileHandle handle,
        string path)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new IOException(
                $"The created project directory identity could not be captured: {path}",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        if ((information.FileAttributes & FileAttributeDirectory) == 0
            || (information.FileAttributes & FileAttributeReparsePoint) != 0)
        {
            throw new IOException(
                $"The created project directory is not an ordinary directory: {path}");
        }

        return new FileSystemPathIdentity(
            path,
            path,
            new FileSystemObjectIdentity(
                information.VolumeSerialNumber,
                ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow));
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

    private static IReadOnlyList<string> SortPaths(IEnumerable<string> paths)
        => paths
            .Distinct(PathComparer)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private sealed class CreatedFile
    {
        public CreatedFile(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public byte[] Contents { get; set; } = [];

        public FileSystemPathIdentity? Identity { get; set; }

        public InitialWorkbookArtifactEvidence? WorkbookEvidence { get; set; }

        public bool SnapshotConclusive { get; set; }
    }

    private sealed class CreatedDirectory
    {
        public CreatedDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public FileSystemPathIdentity? Identity { get; set; }

        public bool IdentityConclusive { get; set; }
    }

    private sealed record TargetEnumeration(
        HashSet<string> Paths,
        HashSet<string> ObservationIncompletePaths);

    private sealed record PathObservation(
        string Path,
        ArtifactObservation Observation);

    private enum ArtifactObservation
    {
        Missing,
        Unchanged,
        Changed,
        Inconclusive
    }

    private enum ExactDeleteResult
    {
        Removed,
        Missing,
        Changed,
        NotEmpty,
        Inconclusive
    }

    private enum RollbackScope
    {
        AllTrackedArtifacts,
        UnderLease,
        AfterLeaseRelease
    }

    private sealed record ExactDirectoryDeleteResult(
        ExactDeleteResult Result,
        IReadOnlyList<string> Entries)
    {
        public static ExactDirectoryDeleteResult Removed()
            => new(ExactDeleteResult.Removed, []);

        public static ExactDirectoryDeleteResult Missing()
            => new(ExactDeleteResult.Missing, []);

        public static ExactDirectoryDeleteResult Changed()
            => new(ExactDeleteResult.Changed, []);

        public static ExactDirectoryDeleteResult NotEmpty(
            IReadOnlyList<string> entries)
            => new(ExactDeleteResult.NotEmpty, entries);

        public static ExactDirectoryDeleteResult Inconclusive()
            => new(ExactDeleteResult.Inconclusive, []);
    }

    private sealed class NoOpNewProjectArtifactRollbackObserver
        : INewProjectArtifactRollbackObserver
    {
        public static NoOpNewProjectArtifactRollbackObserver Instance { get; } = new();

        public void OnProofComplete(string path)
        {
        }
    }

    private sealed class NoOpNewProjectDirectoryCreationObserver
        : INewProjectDirectoryCreationObserver
    {
        public static NoOpNewProjectDirectoryCreationObserver Instance { get; } = new();

        public void OnDirectoryCreated(string path)
        {
        }
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
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

    [DllImport("libc", EntryPoint = "mkdir", SetLastError = true)]
    private static extern int CreateDirectoryPortable(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        uint mode);

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
}

internal sealed record NewProjectTargetInventoryResult(
    IReadOnlyList<string> TargetChangedPaths,
    IReadOnlyList<string> ObservationIncompletePaths)
{
    public bool IsComplete => TargetChangedPaths.Count == 0
        && ObservationIncompletePaths.Count == 0;

    public bool IsConclusive => ObservationIncompletePaths.Count == 0;
}

internal sealed record NewProjectRollbackResult(
    IReadOnlyList<string> TargetChangedPaths,
    IReadOnlyList<string> CleanupIncompletePaths,
    IReadOnlyList<string> RetainedOwnedPaths)
{
    public bool IsComplete => RetainedOwnedPaths.Count == 0
        && CleanupIncompletePaths.Count == 0;
}

internal sealed class NewProjectArtifactAlreadyExistsException : IOException
{
    public NewProjectArtifactAlreadyExistsException(string path, Exception innerException)
        : base($"The project artifact already exists: {path}", innerException)
    {
    }
}

internal sealed class NewProjectArtifactEvidenceMismatchException : IOException
{
    public NewProjectArtifactEvidenceMismatchException(
        string path,
        bool observationIncomplete)
        : base(
            observationIncomplete
                ? $"The created project artifact could not be conclusively matched to its trusted evidence: {path}"
                : $"The created project artifact no longer matches its trusted evidence: {path}")
    {
        Path = path;
        ObservationIncomplete = observationIncomplete;
    }

    public string Path { get; }

    public bool ObservationIncomplete { get; }
}
