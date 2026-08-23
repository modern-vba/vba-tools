using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VbaDebugAdapter.Infrastructure;

internal sealed class WindowsVbaDebugWorkspaceTreeDeleter
{
    private const uint DeleteAccess = 0x00010000;
    private const uint GenericRead = 0x80000000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileWriteAttributes = 0x00000100;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private readonly string workspaceRoot;
    private readonly string workspacesPath;
    private readonly Action? beforeOpenScope;
    private readonly Action<string>? beforeDelete;
    private readonly Action<string>? beforeOpenEntry;

    public WindowsVbaDebugWorkspaceTreeDeleter(
        string workspaceRoot,
        Action? beforeOpenScope = null,
        Action<string>? beforeDelete = null,
        Action<string>? beforeOpenEntry = null)
    {
        this.workspaceRoot = WindowsVbaDebugWorkspacePath.CanonicalizeOrCreate(
            workspaceRoot);
        workspacesPath = Path.Combine(this.workspaceRoot, "workspaces");
        this.beforeOpenScope = beforeOpenScope;
        this.beforeDelete = beforeDelete;
        this.beforeOpenEntry = beforeOpenEntry;
    }

    public IVbaDebugWorkspaceCleanupScope OpenScope(string cleanupTargetPath)
    {
        var fullCleanupTargetPath = Path.GetFullPath(cleanupTargetPath);
        var relativeComponents = ValidateCleanupTarget(fullCleanupTargetPath);
        var sessionWorkspacePath = Path.Combine(workspacesPath, relativeComponents[0]);
        var ancestorHandles = new List<SafeFileHandle>();
        SafeFileHandle? targetHandle = null;
        try
        {
            beforeOpenScope?.Invoke();
            ancestorHandles.Add(OpenPinnedDirectory(workspaceRoot, deleteAccess: false));
            ancestorHandles.Add(OpenPinnedDirectory(workspacesPath, deleteAccess: false));
            var currentPath = workspacesPath;
            for (var index = 0; index < relativeComponents.Length; index++)
            {
                currentPath = Path.Combine(currentPath, relativeComponents[index]);
                var isTarget = index == relativeComponents.Length - 1;
                var handle = OpenPinnedDirectory(currentPath, deleteAccess: isTarget);
                if (isTarget)
                {
                    targetHandle = handle;
                }
                else
                {
                    ancestorHandles.Add(handle);
                }
            }
            return new PinnedWorkspaceCleanupScope(
                fullCleanupTargetPath,
                sessionWorkspacePath,
                ancestorHandles,
                targetHandle!,
                beforeDelete,
                beforeOpenEntry);
        }
        catch
        {
            targetHandle?.Dispose();
            for (var index = ancestorHandles.Count - 1; index >= 0; index--)
            {
                ancestorHandles[index].Dispose();
            }
            throw;
        }
    }

    private string[] ValidateCleanupTarget(string cleanupTargetPath)
    {
        var relativePath = Path.GetRelativePath(workspacesPath, cleanupTargetPath);
        var components = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var isSessionTarget = components.Length == 1 && IsCanonicalHex32(components[0]);
        var isGenerationTarget = components.Length == 3 &&
            IsCanonicalHex32(components[0]) &&
            components[1].Equals("generations", StringComparison.Ordinal) &&
            components[2].StartsWith("generation-", StringComparison.Ordinal) &&
            components[2].Length == "generation-".Length + 10 &&
            components[2]["generation-".Length..].All(char.IsAsciiDigit);
        if (!isSessionTarget && !isGenerationTarget)
        {
            throw new InvalidOperationException(
                "The VBA debug cleanup target is outside a session or generation workspace.");
        }
        return components;
    }

    private static bool IsCanonicalHex32(string value)
        => value.Length == 32 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static SafeFileHandle OpenPinnedDirectory(
        string directoryPath,
        bool deleteAccess)
    {
        var desiredAccess = FileReadAttributes |
            (deleteAccess ? DeleteAccess | FileWriteAttributes : 0u);
        var handle = OpenHandle(directoryPath, desiredAccess);
        try
        {
            var attributes = GetAttributes(handle, directoryPath);
            if ((attributes & FileAttributes.Directory) == 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    $"The pinned VBA debug workspace boundary is not a physical directory: {directoryPath}");
            }
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenHandle(string path, uint desiredAccess)
    {
        var handle = CreateFileW(
            path,
            desiredAccess,
            FileShare.Read | FileShare.Write,
            nint.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics,
            nint.Zero);
        if (!handle.IsInvalid)
        {
            return handle;
        }

        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw new IOException(
            $"The VBA debug workspace entry could not be pinned: {path}",
            new Win32Exception(error));
    }

    internal static Stream OpenPhysicalLeaseStream(string sessionWorkspacePath)
    {
        var leasePath = Path.Combine(sessionWorkspacePath, "lease.json");
        var leaseHandle = OpenHandle(
            leasePath,
            GenericRead | FileReadAttributes);
        try
        {
            var attributes = GetAttributes(leaseHandle, leasePath);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new IOException(
                    "The VBA debug session lease is not a physical file.");
            }
            return new FileStream(leaseHandle, FileAccess.Read);
        }
        catch
        {
            leaseHandle.Dispose();
            throw;
        }
    }

    private static FileAttributes GetAttributes(SafeFileHandle handle, string path)
    {
        if (GetFileInformationByHandleEx(
                handle,
                FileInfoByHandleClass.FileAttributeTagInfo,
                out FileAttributeTagInfo information,
                (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
        {
            return (FileAttributes)information.FileAttributes;
        }

        throw CreateHandleIOException(
            $"The VBA debug workspace entry attributes could not be read: {path}");
    }

    private static void ClearReadOnlyAttribute(
        SafeFileHandle handle,
        string path,
        FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReadOnly) == 0)
        {
            return;
        }
        if (!GetFileInformationByHandleEx(
                handle,
                FileInfoByHandleClass.FileBasicInfo,
                out FileBasicInfo information,
                (uint)Marshal.SizeOf<FileBasicInfo>()))
        {
            throw CreateHandleIOException(
                $"The VBA debug workspace entry metadata could not be read: {path}");
        }

        information.FileAttributes &= ~(uint)FileAttributes.ReadOnly;
        if (!SetFileInformationByHandle(
                handle,
                FileInfoByHandleClass.FileBasicInfo,
                ref information,
                (uint)Marshal.SizeOf<FileBasicInfo>()))
        {
            throw CreateHandleIOException(
                $"The VBA debug workspace entry read-only attribute could not be cleared: {path}");
        }
    }

    private static void MarkDelete(SafeFileHandle handle, string path)
    {
        var disposition = new FileDispositionInfo { DeleteFile = true };
        if (!SetFileInformationByHandle(
                handle,
                FileInfoByHandleClass.FileDispositionInfo,
                ref disposition,
                (uint)Marshal.SizeOf<FileDispositionInfo>()))
        {
            throw CreateHandleIOException(
                $"The VBA debug workspace entry could not be deleted: {path}");
        }
    }

    private static IOException CreateHandleIOException(string message)
        => new(message, new Win32Exception(Marshal.GetLastPInvokeError()));

    internal static void DeletePinnedWorkspaceDirectory(
        string directoryPath,
        SafeFileHandle directoryHandle,
        Action<string>? beforeOpenEntry = null)
    {
        foreach (var entryPath in Directory
                     .EnumerateFileSystemEntries(directoryPath)
                     .ToArray())
        {
            beforeOpenEntry?.Invoke(entryPath);
            DeleteWorkspaceEntry(entryPath, beforeOpenEntry);
        }

        var attributes = GetAttributes(directoryHandle, directoryPath);
        ClearReadOnlyAttribute(directoryHandle, directoryPath, attributes);
        MarkDelete(directoryHandle, directoryPath);
    }

    private static void DeleteWorkspaceEntry(
        string entryPath,
        Action<string>? beforeOpenEntry)
    {
        using var entryHandle = OpenHandle(
            entryPath,
            DeleteAccess | FileReadAttributes | FileWriteAttributes);
        var attributes = GetAttributes(entryHandle, entryPath);
        if ((attributes & FileAttributes.Directory) != 0 &&
            (attributes & FileAttributes.ReparsePoint) == 0)
        {
            DeletePinnedWorkspaceDirectory(
                entryPath,
                entryHandle,
                beforeOpenEntry);
            return;
        }

        ClearReadOnlyAttribute(entryHandle, entryPath, attributes);
        MarkDelete(entryHandle, entryPath);
    }

    private sealed class PinnedWorkspaceCleanupScope(
        string cleanupTargetPath,
        string sessionWorkspacePath,
        IReadOnlyList<SafeFileHandle> ancestorHandles,
        SafeFileHandle targetHandle,
        Action<string>? beforeDelete,
        Action<string>? beforeOpenEntry)
        : IVbaDebugWorkspaceCleanupScope
    {
        private bool deleted;
        private bool disposed;

        public Stream OpenLeaseStream()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return OpenPhysicalLeaseStream(sessionWorkspacePath);
        }

        public void DeleteDirectory()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (deleted)
            {
                return;
            }

            beforeDelete?.Invoke(cleanupTargetPath);
            DeletePinnedWorkspaceDirectory(
                cleanupTargetPath,
                targetHandle,
                beforeOpenEntry);
            deleted = true;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            targetHandle.Dispose();
            for (var index = ancestorHandles.Count - 1; index >= 0; index--)
            {
                ancestorHandles[index].Dispose();
            }
        }

    }

    private enum FileInfoByHandleClass
    {
        FileBasicInfo = 0,
        FileDispositionInfo = 4,
        FileAttributeTagInfo = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool DeleteFile;
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
        out FileBasicInfo fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle fileHandle,
        FileInfoByHandleClass fileInformationClass,
        ref FileBasicInfo fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle fileHandle,
        FileInfoByHandleClass fileInformationClass,
        ref FileDispositionInfo fileInformation,
        uint bufferSize);
}
