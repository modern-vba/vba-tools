using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace VbaDev.Domain;

/// <summary>
/// Resolves a path to the filesystem identity used for safety and containment decisions.
/// </summary>
public interface IFileSystemPathIdentityResolver
{
    /// <summary>
    /// Resolves one path without silently following an identity that cannot be proved.
    /// </summary>
    /// <param name="path">The path whose identity should be resolved.</param>
    /// <returns>The canonical and operation-safe path identity.</returns>
    FileSystemPathIdentity Resolve(string path);
}

/// <summary>
/// Describes the canonical identity and usable operation path for one filesystem path.
/// </summary>
/// <param name="CanonicalPath">The filesystem-canonical path used for comparisons.</param>
/// <param name="OperationPath">The normalized path used for filesystem operations.</param>
/// <param name="ObjectIdentity">The existing filesystem object identity, when available.</param>
public sealed record FileSystemPathIdentity(
    string CanonicalPath,
    string OperationPath,
    FileSystemObjectIdentity? ObjectIdentity);

/// <summary>
/// Identifies an existing filesystem object independently from its path spelling.
/// </summary>
/// <param name="VolumeSerialNumber">The volume serial number.</param>
/// <param name="FileIndex">The stable file index on the volume.</param>
public readonly record struct FileSystemObjectIdentity(
    uint VolumeSerialNumber,
    ulong FileIndex);

/// <summary>
/// Compares filesystem-canonical identities for safety and containment decisions.
/// </summary>
public static class FileSystemPathIdentityRelations
{
    /// <summary>
    /// Returns whether two identities refer to the same canonical path or existing object.
    /// </summary>
    public static bool Same(FileSystemPathIdentity left, FileSystemPathIdentity right)
        => Path.TrimEndingDirectorySeparator(left.CanonicalPath).Equals(
                Path.TrimEndingDirectorySeparator(right.CanonicalPath),
                StringComparison.OrdinalIgnoreCase)
            || left.ObjectIdentity is not null
                && right.ObjectIdentity is not null
                && left.ObjectIdentity == right.ObjectIdentity;

    /// <summary>
    /// Returns whether the candidate is the directory itself or physically below it.
    /// </summary>
    public static bool SameOrDescendant(
        FileSystemPathIdentity candidate,
        FileSystemPathIdentity directory)
        => Same(candidate, directory)
            || IsSameOrDescendantCanonicalPath(
                candidate.CanonicalPath,
                directory.CanonicalPath);

    /// <summary>
    /// Returns whether two source roots cover any of the same physical subtree.
    /// </summary>
    public static bool RootsOverlap(
        FileSystemPathIdentity left,
        FileSystemPathIdentity right)
        => SameOrDescendant(left, right)
            || SameOrDescendant(right, left);

    private static bool IsSameOrDescendantCanonicalPath(
        string candidatePath,
        string directoryPath)
    {
        var candidate = Path.TrimEndingDirectorySeparator(candidatePath);
        var directory = Path.TrimEndingDirectorySeparator(directoryPath);
        var directoryPrefix = directory.EndsWith(Path.DirectorySeparatorChar)
            ? directory
            : directory + Path.DirectorySeparatorChar;
        return candidate.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Resolves Windows reparse paths and portable symbolic links to canonical identities.
/// </summary>
public sealed class FileSystemPathIdentityResolver : IFileSystemPathIdentityResolver
{
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint VolumeNameNt = 0x2;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

    /// <inheritdoc />
    public FileSystemPathIdentity Resolve(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        ValidateUnambiguousPath(fullPath);
        var missingComponents = new Stack<string>();
        var existingPath = fullPath;
        FileAttributes existingAttributes;
        while (!TryGetAttributes(existingPath, out existingAttributes))
        {
            ThrowIfUnresolvedEntryExists(existingPath);
            var trimmedPath = Path.TrimEndingDirectorySeparator(existingPath);
            var parentPath = Path.GetDirectoryName(trimmedPath);
            var component = Path.GetFileName(trimmedPath);
            if (string.IsNullOrEmpty(parentPath)
                || string.IsNullOrEmpty(component)
                || parentPath.Equals(existingPath, StringComparison.OrdinalIgnoreCase))
            {
                throw CannotEstablishIdentity(fullPath);
            }

            ValidateMissingComponent(component, fullPath);
            missingComponents.Push(component);
            existingPath = parentPath;
        }

        if (missingComponents.Count > 0
            && !existingAttributes.HasFlag(FileAttributes.Directory))
        {
            throw CannotEstablishIdentity(fullPath);
        }

        FileSystemPathIdentity identity;
        if (OperatingSystem.IsWindows())
        {
            identity = ResolveExistingWindowsPath(existingPath);
        }
        else
        {
            var portablePath = ResolveExistingPortablePath(existingPath);
            identity = new FileSystemPathIdentity(
                portablePath,
                portablePath,
                ObjectIdentity: null);
        }

        foreach (var component in missingComponents)
        {
            identity = identity with
            {
                CanonicalPath = Path.Combine(identity.CanonicalPath, component),
                OperationPath = Path.Combine(identity.OperationPath, component),
                ObjectIdentity = null
            };
        }

        return identity with
        {
            CanonicalPath = Path.TrimEndingDirectorySeparator(identity.CanonicalPath),
            OperationPath = Path.TrimEndingDirectorySeparator(identity.OperationPath)
        };
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw CannotEstablishIdentity(path, ex);
        }
    }

    private static void ThrowIfUnresolvedEntryExists(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var handle = CreateFileW(
            path,
            0,
            FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            throw CannotEstablishIdentity(path);
        }

        var error = Marshal.GetLastWin32Error();
        if (error is not ErrorFileNotFound and not ErrorPathNotFound)
        {
            throw CannotEstablishIdentity(path, new Win32Exception(error));
        }
    }

    private static FileSystemPathIdentity ResolveExistingWindowsPath(string path)
    {
        using var handle = CreateFileW(
            path,
            0,
            FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw CannotEstablishIdentity(
                path,
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        var canonicalPath = GetFinalPathName(handle, path, VolumeNameNt);
        var operationPath = NormalizeWindowsOperationPath(
            GetFinalPathName(handle, path, volumeName: 0));
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw CannotEstablishIdentity(
                path,
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        return new FileSystemPathIdentity(
            canonicalPath,
            operationPath,
            new FileSystemObjectIdentity(
                information.VolumeSerialNumber,
                ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow));
    }

    private static string GetFinalPathName(
        SafeFileHandle handle,
        string path,
        uint volumeName)
    {
        var capacity = 512;
        while (true)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandleW(
                handle,
                buffer,
                checked((uint)buffer.Capacity),
                volumeName);
            if (length == 0)
            {
                throw CannotEstablishIdentity(
                    path,
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }

            if (length < buffer.Capacity)
            {
                return buffer.ToString();
            }

            capacity = checked((int)length + 1);
        }
    }

    private static string NormalizeWindowsOperationPath(string path)
    {
        const string extendedUncPrefix = @"\\?\UNC\";
        if (path.StartsWith(extendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[extendedUncPrefix.Length..];
        }

        const string extendedPrefix = @"\\?\";
        if (path.StartsWith(extendedPrefix, StringComparison.Ordinal)
            && path.Length > extendedPrefix.Length + 1
            && path[extendedPrefix.Length + 1] == ':')
        {
            return path[extendedPrefix.Length..];
        }

        return path;
    }

    private static string ResolveExistingPortablePath(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
        {
            throw CannotEstablishIdentity(path);
        }

        var current = root;
        var relativePath = Path.GetRelativePath(root, path);
        if (relativePath.Equals(".", StringComparison.Ordinal))
        {
            return current;
        }

        foreach (var component in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            var attributes = File.GetAttributes(current);
            FileSystemInfo entry = attributes.HasFlag(FileAttributes.Directory)
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            if (entry.LinkTarget is not null)
            {
                entry = entry.ResolveLinkTarget(returnFinalTarget: true)
                    ?? throw CannotEstablishIdentity(current);
            }

            current = entry.FullName;
        }

        return current;
    }

    private static void ValidateUnambiguousPath(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (path.StartsWith("\\\\?\\", StringComparison.Ordinal)
            || path.StartsWith("\\\\.\\", StringComparison.Ordinal))
        {
            throw CannotEstablishIdentity(path);
        }

        var root = Path.GetPathRoot(path)
            ?? throw CannotEstablishIdentity(path);
        var relativePath = path[root.Length..];
        foreach (var component in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (component.Contains(':', StringComparison.Ordinal)
                || component.EndsWith(' ')
                || component.EndsWith('.'))
            {
                throw CannotEstablishIdentity(path);
            }
        }
    }

    private static void ValidateMissingComponent(string component, string fullPath)
    {
        if (component.Equals(".", StringComparison.Ordinal)
            || component.Equals("..", StringComparison.Ordinal)
            || (OperatingSystem.IsWindows()
                && (component.Contains(':', StringComparison.Ordinal)
                    || component.EndsWith(' ')
                    || component.EndsWith('.'))))
        {
            throw CannotEstablishIdentity(fullPath);
        }
    }

    private static InvalidOperationException CannotEstablishIdentity(
        string path,
        Exception? innerException = null)
        => new(
            $"Filesystem-canonical path identity could not be established safely: {path}",
            innerException);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        FileMode creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
