using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace VbaDebugAdapter.Infrastructure;

internal interface IVbaDebugSessionWorkspaceCreationScope
    : IDisposable
{
    string SessionWorkspacePath { get; }

    FileStream CreateLeaseStream();

    void DeleteOwnedTree();

    IVbaDebugGenerationWorkspace CreateGenerationWorkspace(
        DebugGenerationId generationId,
        string workbookFileName);
}

public interface IVbaDebugGenerationWorkspace : IAsyncDisposable
{
    DebugGenerationId GenerationId { get; }

    string GenerationWorkspacePath { get; }

    string SourceSnapshotPath { get; }

    string WorkbookPath { get; }

    FileStream CreateSourceFile(string relativePath);

    void SealSourceSnapshot();

    void VerifySourceSnapshot();

    void PinGeneratedWorkbook();

    void VerifyGeneratedWorkbook();
}

internal sealed class WindowsVbaDebugWorkspaceCreator
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileWriteAttributes = 0x00000100;
    private const uint Synchronize = 0x00100000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint ObjectCaseInsensitive = 0x00000040;
    private const uint NtFileDirectoryFile = 0x00000001;
    private const uint NtFileWriteThrough = 0x00000002;
    private const uint NtFileSynchronousIoNonalert = 0x00000020;
    private const uint NtFileNonDirectoryFile = 0x00000040;
    private const uint NtFileOpenReparsePoint = 0x00200000;
    private const uint NtFileOpen = 1;
    private const uint NtFileCreate = 2;
    private const uint NtFileOpenIf = 3;
    private const int StatusObjectNameCollision = unchecked((int)0xC0000035);
    private readonly string workspaceRoot;
    private readonly string workspacesPath;
    private readonly Action<string>? afterCreateDirectoryBeforeOpen;
    private readonly Action<string>? beforeDeleteOwnedTree;
    private readonly Action<string>? beforeCreateLeaseFile;
    private readonly Action<string>? beforeCreateSourceFile;
    private readonly Action<string>? afterCreateSourceFileBeforeOwnershipTransfer;

    public WindowsVbaDebugWorkspaceCreator(
        string workspaceRoot,
        Action<string>? afterCreateDirectoryBeforeOpen = null,
        Action<string>? beforeDeleteOwnedTree = null,
        Action<string>? beforeCreateLeaseFile = null,
        Action<string>? beforeCreateSourceFile = null,
        Action<string>? afterCreateSourceFileBeforeOwnershipTransfer = null)
    {
        this.workspaceRoot = WindowsVbaDebugWorkspacePath.CanonicalizeOrCreate(
            workspaceRoot);
        workspacesPath = Path.Combine(this.workspaceRoot, "workspaces");
        this.afterCreateDirectoryBeforeOpen = afterCreateDirectoryBeforeOpen;
        this.beforeDeleteOwnedTree = beforeDeleteOwnedTree;
        this.beforeCreateLeaseFile = beforeCreateLeaseFile;
        this.beforeCreateSourceFile = beforeCreateSourceFile;
        this.afterCreateSourceFileBeforeOwnershipTransfer =
            afterCreateSourceFileBeforeOwnershipTransfer;
    }

    public string WorkspaceRoot => workspaceRoot;

    public IVbaDebugSessionWorkspaceCreationScope ClaimSession(
        DebugSessionId sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);

        var handles = new List<SafeFileHandle>();
        try
        {
            var rootHandle = OpenPhysicalDirectory(workspaceRoot);
            handles.Add(rootHandle);
            var workspacesHandle = OpenOrCreatePhysicalDirectory(
                rootHandle,
                "workspaces",
                workspacesPath);
            handles.Add(workspacesHandle);
            var sessionPath = Path.Combine(workspacesPath, sessionId.Value);
            var sessionHandle = CreatePhysicalDirectoryExclusive(
                workspacesHandle,
                sessionId.Value,
                sessionPath,
                "session workspace");
            handles.Add(sessionHandle);
            afterCreateDirectoryBeforeOpen?.Invoke(sessionPath);
            return new SessionCreationScope(
                sessionPath,
                handles,
                sessionHandle,
                beforeDeleteOwnedTree,
                beforeCreateLeaseFile,
                afterCreateDirectoryBeforeOpen,
                beforeCreateSourceFile,
                afterCreateSourceFileBeforeOwnershipTransfer);
        }
        catch
        {
            DisposeHandles(handles);
            throw;
        }
    }

    private static IVbaDebugGenerationWorkspace CreateGenerationWorkspace(
        string sessionWorkspacePath,
        SafeFileHandle sessionHandle,
        DebugGenerationId generationId,
        string workbookFileName,
        Action<string>? afterCreateDirectoryBeforeOpen,
        Action<string>? beforeDeleteOwnedTree,
        Action<string>? beforeCreateSourceFile,
        Action<string>? afterCreateSourceFileBeforeOwnershipTransfer)
    {
        ArgumentNullException.ThrowIfNull(generationId);
        if (
            !WindowsVbaDebugWorkspacePath.IsUnambiguousEntryName(
                workbookFileName) ||
            !string.Equals(
                Path.GetFileName(workbookFileName),
                workbookFileName,
                StringComparison.Ordinal) ||
            !string.Equals(
                Path.GetExtension(workbookFileName),
                ".xlsm",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The debug workbook file name must be a path-free .xlsm file name.",
                nameof(workbookFileName));
        }

        var handles = new List<SafeFileHandle>();
        SafeFileHandle? generationHandle = null;
        string? generationPath = null;
        try
        {
            var generationsPath = Path.Combine(
                sessionWorkspacePath,
                "generations");
            var generationsHandle = OpenOrCreatePhysicalDirectory(
                sessionHandle,
                "generations",
                generationsPath);
            handles.Add(generationsHandle);
            generationPath = Path.Combine(
                generationsPath,
                generationId.WorkspaceDirectoryName);
            generationHandle = CreatePhysicalDirectoryExclusive(
                generationsHandle,
                Path.GetFileName(generationPath),
                generationPath,
                "generation workspace");
            handles.Add(generationHandle);
            afterCreateDirectoryBeforeOpen?.Invoke(generationPath);
            var sourcePath = Path.Combine(generationPath, "source");
            var sourceHandle = CreatePhysicalDescendantDirectoryExclusive(
                generationHandle,
                "source",
                sourcePath,
                "generation source workspace");
            handles.Add(sourceHandle);
            afterCreateDirectoryBeforeOpen?.Invoke(sourcePath);
            var outputPath = Path.Combine(generationPath, "output");
            var outputHandle = CreatePhysicalDescendantDirectoryExclusive(
                generationHandle,
                "output",
                outputPath,
                "generation output workspace");
            handles.Add(outputHandle);
            afterCreateDirectoryBeforeOpen?.Invoke(outputPath);
            return new GenerationCreationScope(
                generationId,
                generationPath,
                sourcePath,
                Path.Combine(outputPath, workbookFileName),
                handles,
                generationHandle,
                sourceHandle,
                outputHandle,
                [sourceHandle, outputHandle],
                beforeDeleteOwnedTree,
                beforeCreateSourceFile,
                afterCreateSourceFileBeforeOwnershipTransfer);
        }
        catch
        {
            if (generationHandle is null || generationPath is null)
            {
                DisposeHandles(handles);
            }
            else
            {
                for (var index = handles.Count - 1; index >= 0; index--)
                {
                    if (!ReferenceEquals(handles[index], generationHandle))
                    {
                        handles[index].Dispose();
                    }
                }
                try
                {
                    DeletePinnedWorkspaceDirectoryIfPresent(
                        generationPath,
                        generationHandle);
                }
                catch
                {
                    // The generation-claim failure remains authoritative.
                }
                generationHandle.Dispose();
            }
            throw;
        }
    }

    private static void DeletePinnedWorkspaceDirectoryIfPresent(
        string generationPath,
        SafeFileHandle generationHandle)
    {
        if (WindowsVbaDebugWorkspacePath.EntryExistsNoFollow(generationPath))
        {
            WindowsVbaDebugWorkspaceTreeDeleter.DeletePinnedWorkspaceDirectory(
                generationPath,
                generationHandle);
        }
    }

    private static SafeFileHandle OpenOrCreatePhysicalDirectory(
        SafeFileHandle parentHandle,
        string name,
        string path)
    {
        var handle = CreateRelativeHandle(
            parentHandle,
            name,
            FileReadAttributes | Synchronize,
            FileShare.Read | FileShare.Write,
            FileAttributeNormal,
            NtFileOpenIf,
            NtFileDirectoryFile |
            NtFileSynchronousIoNonalert |
            NtFileOpenReparsePoint,
            path,
            existingDescription: null);
        try
        {
            ValidatePhysicalDirectory(handle, path);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle CreatePhysicalDirectoryExclusive(
        SafeFileHandle parentHandle,
        string name,
        string path,
        string description)
    {
        var handle = CreateRelativeHandle(
            parentHandle,
            name,
            DeleteAccess | FileReadAttributes | FileWriteAttributes | Synchronize,
            FileShare.Read | FileShare.Write,
            FileAttributeNormal,
            NtFileCreate,
            NtFileDirectoryFile |
            NtFileSynchronousIoNonalert |
            NtFileOpenReparsePoint,
            path,
            description);
        try
        {
            ValidatePhysicalDirectory(handle, path);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle CreatePhysicalDescendantDirectoryExclusive(
        SafeFileHandle parentHandle,
        string name,
        string path,
        string description)
    {
        var handle = CreateRelativeHandle(
            parentHandle,
            name,
            FileReadAttributes | Synchronize,
            FileShare.Read | FileShare.Write,
            FileAttributeNormal,
            NtFileCreate,
            NtFileDirectoryFile |
            NtFileSynchronousIoNonalert |
            NtFileOpenReparsePoint,
            path,
            description);
        try
        {
            ValidatePhysicalDirectory(handle, path);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenExistingPhysicalDirectoryReadSeal(
        SafeFileHandle parentHandle,
        string name,
        string path)
    {
        var handle = CreateRelativeHandle(
            parentHandle,
            name,
            FileReadAttributes | Synchronize,
            FileShare.Read,
            FileAttributeNormal,
            NtFileOpen,
            NtFileDirectoryFile |
            NtFileSynchronousIoNonalert |
            NtFileOpenReparsePoint,
            path,
            existingDescription: null);
        try
        {
            ValidatePhysicalDirectory(handle, path);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenPhysicalDirectory(string path)
    {
        var handle = CreateFileW(
            path,
            FileReadAttributes,
            FileShare.Read | FileShare.Write,
            nint.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics,
            nint.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException(
                $"The VBA debug workspace directory could not be pinned: {path}",
                new Win32Exception(error));
        }

        try
        {
            ValidatePhysicalDirectory(handle, path);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static FileStream CreateNewPhysicalFile(
        SafeFileHandle parentHandle,
        string name,
        string path,
        bool asynchronous)
    {
        var createOptions = NtFileNonDirectoryFile |
            NtFileOpenReparsePoint |
            NtFileWriteThrough |
            (asynchronous ? 0u : NtFileSynchronousIoNonalert);
        var desiredAccess = GenericWrite | FileReadAttributes |
            (asynchronous ? 0u : Synchronize);
        var handle = CreateRelativeHandle(
            parentHandle,
            name,
            desiredAccess,
            FileShare.Read,
            FileAttributeNormal,
            NtFileCreate,
            createOptions,
            path,
            existingDescription: "workspace file");

        try
        {
            if (!GetFileInformationByHandleEx(
                    handle,
                    FileInfoByHandleClass.FileAttributeTagInfo,
                    out FileAttributeTagInfo information,
                    (uint)Marshal.SizeOf<FileAttributeTagInfo>()) ||
                ((FileAttributes)information.FileAttributes &
                 (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new IOException(
                    $"The VBA debug workspace file is not a physical file: {path}",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }
            return new FileStream(
                handle,
                FileAccess.Write,
                bufferSize: 4096,
                isAsync: asynchronous);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static (FileStream Stream, SafeFileHandle IdentityPin)
        CreateNewPhysicalFileWithIdentityPin(
            SafeFileHandle parentHandle,
            string name,
            string path)
    {
        var stream = CreateNewPhysicalFile(
            parentHandle,
            name,
            path,
            asynchronous: false);
        try
        {
            return (
                stream,
                OpenExistingPhysicalFile(
                    parentHandle,
                    name,
                    path,
                    requireSingleLink: true));
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenExistingPhysicalFile(
        SafeFileHandle parentHandle,
        string name,
        string path,
        bool requireSingleLink,
        bool verificationOnly = false,
        bool allowWriteSharing = true)
    {
        var handle = CreateRelativeHandle(
            parentHandle,
            name,
            (verificationOnly ? 0u : GenericRead) |
            FileReadAttributes |
            Synchronize,
            FileShare.Read |
            (allowWriteSharing ? FileShare.Write : 0) |
            (verificationOnly ? FileShare.Delete : 0),
            FileAttributeNormal,
            NtFileOpen,
            NtFileNonDirectoryFile |
            NtFileSynchronousIoNonalert |
            NtFileOpenReparsePoint,
            path,
            existingDescription: null);
        try
        {
            ValidatePhysicalFile(handle, path, requireSingleLink);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle CreateRelativeHandle(
        SafeFileHandle parentHandle,
        string name,
        uint desiredAccess,
        FileShare shareMode,
        uint fileAttributes,
        uint createDisposition,
        uint createOptions,
        string path,
        string? existingDescription)
    {
        if (!WindowsVbaDebugWorkspacePath.IsUnambiguousEntryName(name) ||
            name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new InvalidOperationException(
                "A VBA debug workspace entry name must be one unambiguous Windows path component.");
        }

        var nameBuffer = Marshal.StringToHGlobalUni(name);
        var unicodeNamePointer = Marshal.AllocHGlobal(
            Marshal.SizeOf<UnicodeString>());
        var parentHandleAdded = false;
        try
        {
            var unicodeName = new UnicodeString
            {
                Length = checked((ushort)(name.Length * sizeof(char))),
                MaximumLength = checked((ushort)((name.Length + 1) * sizeof(char))),
                Buffer = nameBuffer
            };
            Marshal.StructureToPtr(unicodeName, unicodeNamePointer, fDeleteOld: false);
            parentHandle.DangerousAddRef(ref parentHandleAdded);
            var objectAttributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parentHandle.DangerousGetHandle(),
                ObjectName = unicodeNamePointer,
                Attributes = ObjectCaseInsensitive
            };
            var status = NtCreateFile(
                out var handle,
                desiredAccess,
                ref objectAttributes,
                out _,
                nint.Zero,
                fileAttributes,
                shareMode,
                createDisposition,
                createOptions,
                nint.Zero,
                0);
            if (status >= 0)
            {
                return handle;
            }

            handle?.Dispose();
            if (status == StatusObjectNameCollision &&
                existingDescription is not null)
            {
                throw new InvalidOperationException(
                    $"The VBA debug {existingDescription} already exists or is claimed: {path}");
            }
            throw new IOException(
                $"The VBA debug workspace entry could not be created or opened: {path}",
                new Win32Exception(checked((int)RtlNtStatusToDosError(status))));
        }
        finally
        {
            if (parentHandleAdded)
            {
                parentHandle.DangerousRelease();
            }
            Marshal.FreeHGlobal(unicodeNamePointer);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    private static void ValidatePhysicalDirectory(
        SafeFileHandle handle,
        string path)
    {
        if (!GetFileInformationByHandleEx(
                handle,
                FileInfoByHandleClass.FileAttributeTagInfo,
                out FileAttributeTagInfo information,
                (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
        {
            throw new IOException(
                $"The VBA debug workspace directory attributes could not be read: {path}",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }
        var attributes = (FileAttributes)information.FileAttributes;
        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException(
                $"The VBA debug workspace boundary is not a physical directory: {path}");
        }
    }

    private static void ValidatePhysicalFile(
        SafeFileHandle handle,
        string path,
        bool requireSingleLink)
    {
        if (!GetFileInformationByHandleEx(
                handle,
                FileInfoByHandleClass.FileAttributeTagInfo,
                out FileAttributeTagInfo information,
                (uint)Marshal.SizeOf<FileAttributeTagInfo>()) ||
            ((FileAttributes)information.FileAttributes &
             (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new IOException(
                $"The VBA debug workspace file is not a physical file: {path}",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        if (!requireSingleLink)
        {
            return;
        }
        if (!GetFileInformationByHandleEx(
                handle,
                FileInfoByHandleClass.FileStandardInfo,
                out FileStandardInfo standardInformation,
                (uint)Marshal.SizeOf<FileStandardInfo>()))
        {
            throw new IOException(
                $"The VBA debug workspace file link count could not be verified: {path}",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }
        if (standardInformation.NumberOfLinks != 1)
        {
            throw new IOException(
                $"The generated VBA debug workbook must have exactly one physical file link: {path}");
        }
    }

    private static WindowsPhysicalFileIdentity ReadPhysicalFileIdentity(
        SafeFileHandle handle,
        string path)
    {
        if (!GetFileInformationByHandle(
                handle,
                out ByHandleFileInformation information))
        {
            throw new IOException(
                $"The VBA debug workspace file identity could not be read: {path}",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }
        return new WindowsPhysicalFileIdentity(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) |
            information.FileIndexLow);
    }

    private static byte[] ComputeSha256(
        SafeFileHandle handle,
        string path)
    {
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long offset = 0;
            while (true)
            {
                var read = RandomAccess.Read(handle, buffer, offset);
                if (read == 0)
                {
                    return hash.GetHashAndReset();
                }
                hash.AppendData(buffer, 0, read);
                offset += read;
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"The VBA debug workspace file content could not be verified: {path}",
                exception);
        }
    }

    private static void DisposeHandles(IReadOnlyList<SafeFileHandle> handles)
    {
        for (var index = handles.Count - 1; index >= 0; index--)
        {
            handles[index].Dispose();
        }
    }

    private sealed class SessionCreationScope(
        string sessionWorkspacePath,
        IReadOnlyList<SafeFileHandle> handles,
        SafeFileHandle sessionHandle,
        Action<string>? beforeDeleteOwnedTree,
        Action<string>? beforeCreateLeaseFile,
        Action<string>? afterCreateDirectoryBeforeOpen,
        Action<string>? beforeCreateSourceFile,
        Action<string>? afterCreateSourceFileBeforeOwnershipTransfer)
        : IVbaDebugSessionWorkspaceCreationScope
    {
        private int disposed;
        private bool deleted;

        public string SessionWorkspacePath { get; } = sessionWorkspacePath;

        public FileStream CreateLeaseStream()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            var leasePath = Path.Combine(SessionWorkspacePath, "lease.json");
            beforeCreateLeaseFile?.Invoke(leasePath);
            return CreateNewPhysicalFile(
                sessionHandle,
                "lease.json",
                leasePath,
                asynchronous: true);
        }

        public IVbaDebugGenerationWorkspace CreateGenerationWorkspace(
            DebugGenerationId generationId,
            string workbookFileName)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            return WindowsVbaDebugWorkspaceCreator.CreateGenerationWorkspace(
                SessionWorkspacePath,
                sessionHandle,
                generationId,
                workbookFileName,
                afterCreateDirectoryBeforeOpen,
                beforeDeleteOwnedTree,
                beforeCreateSourceFile,
                afterCreateSourceFileBeforeOwnershipTransfer);
        }

        public void DeleteOwnedTree()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (deleted)
            {
                return;
            }
            beforeDeleteOwnedTree?.Invoke(SessionWorkspacePath);
            WindowsVbaDebugWorkspaceTreeDeleter.DeletePinnedWorkspaceDirectory(
                SessionWorkspacePath,
                sessionHandle);
            deleted = true;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                DisposeHandles(handles);
            }
        }
    }

    private sealed class GenerationCreationScope(
        DebugGenerationId generationId,
        string generationPath,
        string sourcePath,
        string workbookPath,
        IReadOnlyList<SafeFileHandle> handles,
        SafeFileHandle generationHandle,
        SafeFileHandle sourceHandle,
        SafeFileHandle outputHandle,
        IReadOnlyList<SafeFileHandle> descendantHandles,
        Action<string>? beforeDeleteOwnedTree,
        Action<string>? beforeCreateSourceFile,
        Action<string>? afterCreateSourceFileBeforeOwnershipTransfer)
        : IVbaDebugGenerationWorkspace
    {
        private readonly object gate = new();
        private readonly List<SafeFileHandle> nestedHandles = [];
        private readonly Dictionary<string, NestedDirectoryPin> nestedDirectories =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly List<SafeFileHandle> sourceDirectorySeals = [];
        private readonly List<SourceFileIdentityPin> sourceFileIdentityPins = [];
        private SafeFileHandle? workbookIdentityPin;
        private WindowsPhysicalFileIdentity? workbookIdentity;
        private byte[]? workbookSha256;
        private int disposed;
        private bool descendantsReleased;
        private bool sourceSnapshotSealed;

        public DebugGenerationId GenerationId { get; } = generationId;

        public string GenerationWorkspacePath { get; } = generationPath;

        public string SourceSnapshotPath { get; } = sourcePath;

        public string WorkbookPath { get; } = workbookPath;

        public FileStream CreateSourceFile(string relativePath)
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed != 0, this);
                if (sourceSnapshotSealed)
                {
                    throw new InvalidOperationException(
                        "The VBA debug source snapshot has already been sealed.");
                }
                var components = relativePath
                    .Replace('\\', '/')
                    .Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (components.Length == 0 ||
                    components.Any(component => component is "." or ".."))
                {
                    throw new InvalidOperationException(
                        "The transported source path is not a strict relative path.");
                }

                var parentHandle = sourceHandle;
                var relativeDirectoryPath = string.Empty;
                for (var index = 0; index < components.Length - 1; index++)
                {
                    relativeDirectoryPath = Path.Combine(
                        relativeDirectoryPath,
                        components[index]);
                    if (!nestedDirectories.TryGetValue(
                            relativeDirectoryPath,
                            out var directory))
                    {
                        var directoryPath = Path.Combine(
                            SourceSnapshotPath,
                            relativeDirectoryPath);
                        var directoryHandle =
                            CreatePhysicalDescendantDirectoryExclusive(
                                parentHandle,
                                components[index],
                                directoryPath,
                                "source snapshot directory");
                        nestedHandles.Add(directoryHandle);
                        directory = new NestedDirectoryPin(
                            components[index],
                            relativeDirectoryPath,
                            directoryPath,
                            parentHandle,
                            directoryHandle);
                        nestedDirectories.Add(relativeDirectoryPath, directory);
                    }
                    parentHandle = directory.Handle;
                }
                var parentPath = components.Length == 1
                    ? SourceSnapshotPath
                    : nestedDirectories[relativeDirectoryPath].Path;
                var filePath = Path.Combine(parentPath, components[^1]);
                beforeCreateSourceFile?.Invoke(filePath);
                var createdFile = CreateNewPhysicalFileWithIdentityPin(
                    parentHandle,
                    components[^1],
                    filePath);
                try
                {
                    afterCreateSourceFileBeforeOwnershipTransfer?.Invoke(filePath);
                    var identity = ReadPhysicalFileIdentity(
                        createdFile.IdentityPin,
                        filePath);
                    sourceFileIdentityPins.Add(new SourceFileIdentityPin(
                        components[^1],
                        string.Join(Path.DirectorySeparatorChar, components),
                        filePath,
                        parentHandle,
                        createdFile.IdentityPin,
                        identity));
                    return createdFile.Stream;
                }
                catch
                {
                    createdFile.Stream.Dispose();
                    createdFile.IdentityPin.Dispose();
                    throw;
                }
            }
        }

        public void SealSourceSnapshot()
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed != 0, this);
                if (sourceSnapshotSealed)
                {
                    throw new InvalidOperationException(
                        "The VBA debug source snapshot has already been sealed.");
                }

                var pendingDirectorySeals = new List<SafeFileHandle>();
                var pendingFileSeals = new List<SealedSourceFile>();
                var transferred = false;
                try
                {
                    pendingDirectorySeals.Add(
                        OpenExistingPhysicalDirectoryReadSeal(
                            generationHandle,
                            "source",
                            SourceSnapshotPath));
                    foreach (var directory in nestedDirectories.Values)
                    {
                        pendingDirectorySeals.Add(
                            OpenExistingPhysicalDirectoryReadSeal(
                                directory.ParentHandle,
                                directory.Name,
                                directory.Path));
                    }
                    ValidateSourceInventory();

                    foreach (var sourceFile in sourceFileIdentityPins)
                    {
                        var sealedHandle = OpenExistingPhysicalFile(
                            sourceFile.ParentHandle,
                            sourceFile.Name,
                            sourceFile.Path,
                            requireSingleLink: true,
                            allowWriteSharing: false);
                        try
                        {
                            if (ReadPhysicalFileIdentity(
                                    sealedHandle,
                                    sourceFile.Path) != sourceFile.Identity)
                            {
                                throw new IOException(
                                    $"The materialized VBA debug source file identity changed before the snapshot was sealed: {sourceFile.Path}");
                            }
                            pendingFileSeals.Add(new SealedSourceFile(
                                sourceFile,
                                sealedHandle,
                                ComputeSha256(sealedHandle, sourceFile.Path)));
                        }
                        catch
                        {
                            sealedHandle.Dispose();
                            throw;
                        }
                    }
                    ValidateSourceInventory();

                    foreach (var sealedSource in pendingFileSeals)
                    {
                        sealedSource.Source.IdentityPin.Dispose();
                        sealedSource.Source.IdentityPin = sealedSource.Handle;
                        sealedSource.Source.SealedSha256 = sealedSource.Sha256;
                    }
                    sourceDirectorySeals.AddRange(pendingDirectorySeals);
                    sourceSnapshotSealed = true;
                    transferred = true;
                }
                finally
                {
                    if (!transferred)
                    {
                        DisposeHandles(pendingFileSeals
                            .Select(source => source.Handle)
                            .ToArray());
                        DisposeHandles(pendingDirectorySeals);
                    }
                }
            }
        }

        public void VerifySourceSnapshot()
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed != 0, this);
                if (!sourceSnapshotSealed)
                {
                    throw new InvalidOperationException(
                        "The VBA debug source snapshot has not been sealed.");
                }
                ValidateSourceInventory();
                foreach (var sourceFile in sourceFileIdentityPins)
                {
                    using var currentHandle = OpenExistingPhysicalFile(
                        sourceFile.ParentHandle,
                        sourceFile.Name,
                        sourceFile.Path,
                        requireSingleLink: true,
                        verificationOnly: true);
                    var currentIdentity = ReadPhysicalFileIdentity(
                        currentHandle,
                        sourceFile.Path);
                    if (currentIdentity != sourceFile.Identity)
                    {
                        throw new IOException(
                            $"The materialized VBA debug source file identity changed during the build: {sourceFile.Path}");
                    }
                    var currentSha256 = ComputeSha256(
                        sourceFile.IdentityPin,
                        sourceFile.Path);
                    if (!CryptographicOperations.FixedTimeEquals(
                            currentSha256,
                            sourceFile.SealedSha256!))
                    {
                        throw new IOException(
                            $"The materialized VBA debug source file content changed during the build: {sourceFile.Path}");
                    }
                }
                ValidateSourceInventory();
            }
        }

        private void ValidateSourceInventory()
        {
            var expectedEntries = new HashSet<string>(StringComparer.Ordinal);
            foreach (var directory in nestedDirectories.Values)
            {
                expectedEntries.Add(
                    $"D:{NormalizeRelativePath(directory.RelativePath)}");
            }
            foreach (var sourceFile in sourceFileIdentityPins)
            {
                expectedEntries.Add(
                    $"F:{NormalizeRelativePath(sourceFile.RelativePath)}");
            }

            var actualEntries = new HashSet<string>(StringComparer.Ordinal);
            var pendingDirectories = new Stack<string>();
            pendingDirectories.Push(SourceSnapshotPath);
            while (pendingDirectories.Count != 0)
            {
                var directoryPath = pendingDirectories.Pop();
                foreach (var entryPath in Directory.EnumerateFileSystemEntries(
                             directoryPath))
                {
                    var attributes = File.GetAttributes(entryPath);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new IOException(
                            $"The sealed VBA debug source inventory contains a reparse point: {entryPath}");
                    }
                    var relativePath = NormalizeRelativePath(
                        Path.GetRelativePath(SourceSnapshotPath, entryPath));
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        actualEntries.Add($"D:{relativePath}");
                        pendingDirectories.Push(entryPath);
                    }
                    else
                    {
                        actualEntries.Add($"F:{relativePath}");
                    }
                }
            }

            if (!actualEntries.SetEquals(expectedEntries))
            {
                throw new IOException(
                    "The materialized VBA debug source inventory changed after it was created.");
            }
        }

        private static string NormalizeRelativePath(string relativePath)
            => relativePath.Replace('\\', '/');

        public void PinGeneratedWorkbook()
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed != 0, this);
                if (workbookIdentityPin is not null)
                {
                    throw new InvalidOperationException(
                        "The generated VBA debug workbook identity has already been pinned.");
                }
                workbookIdentityPin = OpenExistingPhysicalFile(
                    outputHandle,
                    Path.GetFileName(WorkbookPath),
                    WorkbookPath,
                    requireSingleLink: true,
                    allowWriteSharing: false);
                workbookIdentity = ReadPhysicalFileIdentity(
                    workbookIdentityPin,
                    WorkbookPath);
                workbookSha256 = ComputeSha256(
                    workbookIdentityPin,
                    WorkbookPath);
            }
        }

        public void VerifyGeneratedWorkbook()
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed != 0, this);
                if (workbookIdentityPin is null ||
                    workbookIdentity is null ||
                    workbookSha256 is null)
                {
                    throw new InvalidOperationException(
                        "The generated VBA debug workbook identity has not been pinned.");
                }
                using var currentHandle = OpenExistingPhysicalFile(
                    outputHandle,
                    Path.GetFileName(WorkbookPath),
                    WorkbookPath,
                    requireSingleLink: true,
                    verificationOnly: true);
                var currentIdentity = ReadPhysicalFileIdentity(
                    currentHandle,
                    WorkbookPath);
                if (currentIdentity != workbookIdentity.Value)
                {
                    throw new IOException(
                        "The generated VBA debug workbook identity changed after the build completed.");
                }
                var currentSha256 = ComputeSha256(
                    workbookIdentityPin,
                    WorkbookPath);
                if (!CryptographicOperations.FixedTimeEquals(
                        currentSha256,
                        workbookSha256))
                {
                    throw new IOException(
                        "The generated VBA debug workbook content changed after the build completed.");
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (gate)
            {
                if (disposed != 0)
                {
                    return ValueTask.CompletedTask;
                }
                disposed = 1;
            }

            try
            {
                ReleaseDescendantHandles();
                beforeDeleteOwnedTree?.Invoke(GenerationWorkspacePath);
                WindowsVbaDebugWorkspaceTreeDeleter.DeletePinnedWorkspaceDirectory(
                    GenerationWorkspacePath,
                    generationHandle);
            }
            finally
            {
                DisposeHandles(handles);
            }
            return ValueTask.CompletedTask;
        }

        private void ReleaseDescendantHandles()
        {
            if (descendantsReleased)
            {
                return;
            }
            descendantsReleased = true;
            workbookIdentityPin?.Dispose();
            workbookIdentityPin = null;
            workbookIdentity = null;
            workbookSha256 = null;
            DisposeHandles(sourceFileIdentityPins
                .Select(sourceFile => sourceFile.IdentityPin)
                .ToArray());
            DisposeHandles(sourceDirectorySeals);
            DisposeHandles(nestedHandles);
            DisposeHandles(descendantHandles);
        }

        private sealed class SourceFileIdentityPin(
            string name,
            string relativePath,
            string path,
            SafeFileHandle parentHandle,
            SafeFileHandle identityPin,
            WindowsPhysicalFileIdentity identity)
        {
            public string Name { get; } = name;

            public string RelativePath { get; } = relativePath;

            public string Path { get; } = path;

            public SafeFileHandle ParentHandle { get; } = parentHandle;

            public SafeFileHandle IdentityPin { get; set; } = identityPin;

            public WindowsPhysicalFileIdentity Identity { get; } = identity;

            public byte[]? SealedSha256 { get; set; }
        }

        private sealed record NestedDirectoryPin(
            string Name,
            string RelativePath,
            string Path,
            SafeFileHandle ParentHandle,
            SafeFileHandle Handle);

        private sealed record SealedSourceFile(
            SourceFileIdentityPin Source,
            SafeFileHandle Handle,
            byte[] Sha256);
    }

    private readonly record struct WindowsPhysicalFileIdentity(
        uint VolumeSerialNumber,
        ulong FileIndex);

    private enum FileInfoByHandleClass
    {
        FileStandardInfo = 1,
        FileAttributeTagInfo = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInfo
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;

        [MarshalAs(UnmanagedType.U1)]
        public bool DeletePending;

        [MarshalAs(UnmanagedType.U1)]
        public bool Directory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FileTime CreationTime;
        public FileTime LastAccessTime;
        public FileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public nint RootDirectory;
        public nint ObjectName;
        public uint Attributes;
        public nint SecurityDescriptor;
        public nint SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public nint Status;
        public nuint Information;
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle fileHandle,
        FileInfoByHandleClass fileInformationClass,
        out FileAttributeTagInfo fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle fileHandle,
        FileInfoByHandleClass fileInformationClass,
        out FileStandardInfo fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle fileHandle,
        out ByHandleFileInformation fileInformation);

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(
        out SafeFileHandle fileHandle,
        uint desiredAccess,
        ref ObjectAttributes objectAttributes,
        out IoStatusBlock ioStatusBlock,
        nint allocationSize,
        uint fileAttributes,
        FileShare shareAccess,
        uint createDisposition,
        uint createOptions,
        nint eaBuffer,
        uint eaLength);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);
}
