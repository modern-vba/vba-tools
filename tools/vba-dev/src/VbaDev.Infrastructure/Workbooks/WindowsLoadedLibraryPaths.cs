using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace VbaDev.Infrastructure.Workbooks;

internal static class WindowsLoadedLibraryPaths
{
    internal static IReadOnlyList<string> Capture(Process process)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Loaded library inspection requires Windows.");
        var drives = new List<KeyValuePair<string, string>>();
        foreach (var drive in Environment.GetLogicalDrives())
        {
            var device = new StringBuilder(32768);
            if (QueryDosDeviceW(drive.TrimEnd('\\'), device, device.Capacity) != 0)
                drives.Add(new(drive.TrimEnd('\\'), device.ToString()));
        }
        var paths = new List<string>();
        foreach (ProcessModule module in process.Modules)
        {
            var mapped = new StringBuilder(32768);
            var path = K32GetMappedFileNameW(process.Handle, module.BaseAddress, mapped, mapped.Capacity) == 0
                ? null : ToDosPath(mapped.ToString(), drives);
            // Keep an unresolved entry so it cannot silently disappear from a
            // same-name ambiguity check. PEB module names can be Office virtual paths.
            paths.Add(path ?? module.FileName);
        }
        return paths;
    }

    internal static string? ToDosPath(string mappedPath, IReadOnlyList<KeyValuePair<string, string>> drives)
    {
        const string networkPrefix = @"\Device\Mup\";
        if (mappedPath.StartsWith(networkPrefix, StringComparison.OrdinalIgnoreCase))
            return @"\\" + mappedPath[networkPrefix.Length..];
        foreach (var drive in drives.OrderByDescending(pair => pair.Value.Length).ThenBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (mappedPath.StartsWith(drive.Value + "\\", StringComparison.OrdinalIgnoreCase))
                return drive.Key + mappedPath[drive.Value.Length..];
        }
        return null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint QueryDosDeviceW(string deviceName, StringBuilder targetPath, int maximumLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint K32GetMappedFileNameW(nint process, nint address, StringBuilder fileName, int size);
}
