using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace VbaDebugAdapter.Infrastructure;

internal static class WindowsVbaDebugWorkspacePath
{
    private const uint FileReadAttributes = 0x00000080;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    public static string CanonicalizeOrCreate(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "VBA debug workspace ownership requires Windows.");
        }

        var fullPath = Path.GetFullPath(workspaceRoot);
        Directory.CreateDirectory(fullPath);
        using var handle = CreateFileW(
            fullPath,
            FileReadAttributes,
            FileShare.Read | FileShare.Write | FileShare.Delete,
            nint.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            nint.Zero);
        if (handle.IsInvalid)
        {
            throw new IOException(
                $"The VBA debug workspace root could not be opened: {fullPath}",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        var capacity = 512;
        while (true)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandleW(
                handle,
                buffer,
                (uint)buffer.Capacity,
                flags: 0);
            if (length == 0)
            {
                throw new IOException(
                    $"The VBA debug workspace root could not be canonicalized: {fullPath}",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }
            if (length < buffer.Capacity)
            {
                return Path.GetFullPath(ToDosPath(buffer.ToString()));
            }
            capacity = checked((int)length + 1);
        }
    }

    public static bool EntryExistsNoFollow(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static string ToDosPath(string path)
    {
        const string extendedUncPrefix = @"\\?\UNC\";
        const string extendedPrefix = @"\\?\";
        if (path.StartsWith(extendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[extendedUncPrefix.Length..];
        }
        return path.StartsWith(extendedPrefix, StringComparison.Ordinal)
            ? path[extendedPrefix.Length..]
            : path;
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

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFinalPathNameByHandleW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);
}

internal sealed class VbaDebugWorkspaceRootBinding
{
    private readonly Lazy<string> canonicalWorkspaceRoot;

    public VbaDebugWorkspaceRootBinding(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        var configuredWorkspaceRoot = Path.GetFullPath(workspaceRoot);
        canonicalWorkspaceRoot = new Lazy<string>(
            () => WindowsVbaDebugWorkspacePath.CanonicalizeOrCreate(
                configuredWorkspaceRoot),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string Resolve() => canonicalWorkspaceRoot.Value;
}
