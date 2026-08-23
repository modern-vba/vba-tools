using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VbaDebugAdapter.Infrastructure;

internal interface IVbaDebugOwnedWorkspaceCreationScope : IDisposable
{
    void DeleteOwnedTree();
}

internal interface IVbaDebugSessionWorkspaceCreationScope
    : IVbaDebugOwnedWorkspaceCreationScope
{
    string SessionWorkspacePath { get; }

    FileStream CreateLeaseStream();
}

internal interface IVbaDebugGenerationWorkspaceCreationScope
    : IVbaDebugOwnedWorkspaceCreationScope
{
    string GenerationPath { get; }

    string SourcePath { get; }

    string OutputPath { get; }

    FileStream CreateSourceFile(string relativePath);
}

internal sealed class WindowsVbaDebugWorkspaceCreator
{
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

    public WindowsVbaDebugWorkspaceCreator(
        string workspaceRoot,
        Action<string>? afterCreateDirectoryBeforeOpen = null,
        Action<string>? beforeDeleteOwnedTree = null,
        Action<string>? beforeCreateLeaseFile = null,
        Action<string>? beforeCreateSourceFile = null)
    {
        this.workspaceRoot = WindowsVbaDebugWorkspacePath.CanonicalizeOrCreate(
            workspaceRoot);
        workspacesPath = Path.Combine(this.workspaceRoot, "workspaces");
        this.afterCreateDirectoryBeforeOpen = afterCreateDirectoryBeforeOpen;
        this.beforeDeleteOwnedTree = beforeDeleteOwnedTree;
        this.beforeCreateLeaseFile = beforeCreateLeaseFile;
        this.beforeCreateSourceFile = beforeCreateSourceFile;
    }

    public string WorkspaceRoot => workspaceRoot;

    public IVbaDebugSessionWorkspaceCreationScope ClaimSession(string sessionId)
    {
        if (!IsCanonicalSessionId(sessionId))
        {
            throw new ArgumentException(
                "The adapter session ID must contain 32 lowercase hexadecimal characters.",
                nameof(sessionId));
        }

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
            var sessionPath = Path.Combine(workspacesPath, sessionId);
            var sessionHandle = CreatePhysicalDirectoryExclusive(
                workspacesHandle,
                sessionId,
                sessionPath,
                "session workspace");
            handles.Add(sessionHandle);
            afterCreateDirectoryBeforeOpen?.Invoke(sessionPath);
            return new SessionCreationScope(
                sessionPath,
                handles,
                sessionHandle,
                beforeDeleteOwnedTree,
                beforeCreateLeaseFile);
        }
        catch
        {
            DisposeHandles(handles);
            throw;
        }
    }

    public IVbaDebugGenerationWorkspaceCreationScope ClaimGeneration(
        string sessionId,
        int generation)
    {
        if (!IsCanonicalSessionId(sessionId))
        {
            throw new ArgumentException(
                "The adapter session ID must contain 32 lowercase hexadecimal characters.",
                nameof(sessionId));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(generation);

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
            var sessionPath = Path.Combine(workspacesPath, sessionId);
            var sessionHandle = OpenOrCreatePhysicalDirectory(
                workspacesHandle,
                sessionId,
                sessionPath);
            handles.Add(sessionHandle);
            var generationsPath = Path.Combine(sessionPath, "generations");
            var generationsHandle = OpenOrCreatePhysicalDirectory(
                sessionHandle,
                "generations",
                generationsPath);
            handles.Add(generationsHandle);
            var generationPath = Path.Combine(
                generationsPath,
                $"generation-{generation:D10}");
            var generationHandle = CreatePhysicalDirectoryExclusive(
                generationsHandle,
                Path.GetFileName(generationPath),
                generationPath,
                "generation workspace");
            handles.Add(generationHandle);
            afterCreateDirectoryBeforeOpen?.Invoke(generationPath);
            var sourcePath = Path.Combine(generationPath, "source");
            var sourceHandle = CreatePhysicalDirectoryExclusive(
                generationHandle,
                "source",
                sourcePath,
                "generation source workspace");
            handles.Add(sourceHandle);
            afterCreateDirectoryBeforeOpen?.Invoke(sourcePath);
            var outputPath = Path.Combine(generationPath, "output");
            var outputHandle = CreatePhysicalDirectoryExclusive(
                generationHandle,
                "output",
                outputPath,
                "generation output workspace");
            handles.Add(outputHandle);
            afterCreateDirectoryBeforeOpen?.Invoke(outputPath);
            return new GenerationCreationScope(
                generationPath,
                sourcePath,
                outputPath,
                handles,
                generationHandle,
                sourceHandle,
                [sourceHandle, outputHandle],
                beforeDeleteOwnedTree,
                beforeCreateSourceFile);
        }
        catch
        {
            DisposeHandles(handles);
            throw;
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
        if (string.IsNullOrWhiteSpace(name) ||
            name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new InvalidOperationException(
                "A VBA debug workspace entry name must contain exactly one path component.");
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

    private static void DisposeHandles(IReadOnlyList<SafeFileHandle> handles)
    {
        for (var index = handles.Count - 1; index >= 0; index--)
        {
            handles[index].Dispose();
        }
    }

    private static bool IsCanonicalSessionId(string value)
        => value.Length == 32 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed class SessionCreationScope(
        string sessionWorkspacePath,
        IReadOnlyList<SafeFileHandle> handles,
        SafeFileHandle sessionHandle,
        Action<string>? beforeDeleteOwnedTree,
        Action<string>? beforeCreateLeaseFile)
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
        string generationPath,
        string sourcePath,
        string outputPath,
        IReadOnlyList<SafeFileHandle> handles,
        SafeFileHandle generationHandle,
        SafeFileHandle sourceHandle,
        IReadOnlyList<SafeFileHandle> descendantHandles,
        Action<string>? beforeDeleteOwnedTree,
        Action<string>? beforeCreateSourceFile)
        : IVbaDebugGenerationWorkspaceCreationScope
    {
        private readonly List<SafeFileHandle> nestedHandles = [];
        private int disposed;
        private bool deleted;
        private bool descendantsReleased;

        public string GenerationPath { get; } = generationPath;

        public string SourcePath { get; } = sourcePath;

        public string OutputPath { get; } = outputPath;

        public FileStream CreateSourceFile(string relativePath)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            var components = relativePath
                .Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (components.Length == 0 ||
                components.Any(component => component is "." or ".."))
            {
                throw new InvalidOperationException(
                    "The transported source path is not a strict relative path.");
            }

            var parentPath = SourcePath;
            var parentHandle = sourceHandle;
            for (var index = 0; index < components.Length - 1; index++)
            {
                parentPath = Path.Combine(parentPath, components[index]);
                parentHandle = OpenOrCreatePhysicalDirectory(
                    parentHandle,
                    components[index],
                    parentPath);
                nestedHandles.Add(parentHandle);
            }
            var filePath = Path.Combine(parentPath, components[^1]);
            beforeCreateSourceFile?.Invoke(filePath);
            return CreateNewPhysicalFile(
                parentHandle,
                components[^1],
                filePath,
                asynchronous: false);
        }

        public void DeleteOwnedTree()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (deleted)
            {
                return;
            }
            ReleaseDescendantHandles();
            beforeDeleteOwnedTree?.Invoke(GenerationPath);
            WindowsVbaDebugWorkspaceTreeDeleter.DeletePinnedWorkspaceDirectory(
                GenerationPath,
                generationHandle);
            deleted = true;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }
            ReleaseDescendantHandles();
            DisposeHandles(handles);
        }

        private void ReleaseDescendantHandles()
        {
            if (descendantsReleased)
            {
                return;
            }
            descendantsReleased = true;
            DisposeHandles(nestedHandles);
            DisposeHandles(descendantHandles);
        }
    }

    private enum FileInfoByHandleClass
    {
        FileAttributeTagInfo = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
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
