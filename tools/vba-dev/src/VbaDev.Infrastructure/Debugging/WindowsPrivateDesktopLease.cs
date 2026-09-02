using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VbaDev.Infrastructure.Debugging;

internal interface IWindowsDesktopApi
{
    string GetCurrentWindowStationName();

    nint CreateDesktop(string name);

    void AttachCurrentThread(nint desktopHandle);

    void CloseDesktop(nint desktopHandle);

}

/// <summary>
/// Owns one invocation-scoped Windows desktop in the process's current window station.
/// </summary>
internal sealed class WindowsPrivateDesktopLease : IDisposable
{
    private const string GeneratedNamePrefix = "vba-dev-automation-";

    private readonly object gate = new();
    private readonly IWindowsDesktopApi api;
    private nint handle;

    private WindowsPrivateDesktopLease(
        IWindowsDesktopApi api,
        string name,
        string qualifiedName,
        nint handle)
    {
        this.api = api;
        Name = name;
        QualifiedName = qualifiedName;
        this.handle = handle;
    }

    ~WindowsPrivateDesktopLease()
    {
        if (handle == nint.Zero)
        {
            return;
        }

        try
        {
            api.CloseDesktop(handle);
        }
        catch
        {
            // Explicit disposal reports cleanup failures. A finalizer cannot do so.
        }
    }

    public string Name { get; }

    public string QualifiedName { get; }

    public nint Handle
    {
        get
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(handle == nint.Zero, this);
                return handle;
            }
        }
    }

    public static WindowsPrivateDesktopLease Create()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Private desktop isolation requires Windows.");
        }

        return Create(
            WindowsDesktopApi.Instance,
            $"{GeneratedNamePrefix}{Guid.NewGuid():N}");
    }

    internal static WindowsPrivateDesktopLease Create(
        IWindowsDesktopApi api,
        string name)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A Windows desktop name cannot contain a backslash.",
                nameof(name));
        }

        var windowStationName = api.GetCurrentWindowStationName();
        if (string.IsNullOrWhiteSpace(windowStationName))
        {
            throw new InvalidOperationException(
                "The current Windows window station has no usable name.");
        }

        var qualifiedName = $"{windowStationName}\\{name}";
        var createdHandle = CreateOnDedicatedThread(api, name);
        if (createdHandle == nint.Zero)
        {
            throw new InvalidOperationException(
                $"Creating private desktop '{qualifiedName}' returned an invalid handle.");
        }

        return new WindowsPrivateDesktopLease(
            api,
            name,
            qualifiedName,
            createdHandle);
    }

    /// <summary>
    /// Assigns this desktop to the calling thread for the remainder of that thread's lifetime.
    /// Call this before the thread creates any windows, installs hooks, or starts COM automation.
    /// Setting a managed thread's apartment to STA can initialize COM before its delegate runs,
    /// so callers must not assume that such a thread is still eligible for reassignment.
    /// </summary>
    public void AttachCurrentThread()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(handle == nint.Zero, this);
            api.AttachCurrentThread(handle);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (handle == nint.Zero)
            {
                return;
            }

            // CloseDesktop fails while any thread in this process still uses the desktop.
            // Keep the handle when it fails so the owner can end that thread and retry.
            api.CloseDesktop(handle);
            handle = nint.Zero;
        }

        GC.SuppressFinalize(this);
    }

    private static nint CreateOnDedicatedThread(
        IWindowsDesktopApi api,
        string name)
    {
        var completion = new TaskCompletionSource<nint>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var creatorThread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(api.CreateDesktop(name));
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "VbaDev private desktop creator"
        };

        creatorThread.Start();
        creatorThread.Join();
        return completion.Task.GetAwaiter().GetResult();
    }
}

internal sealed class WindowsDesktopApi : IWindowsDesktopApi
{
    private const int UserObjectName = 2;
    private const uint DesktopReadObjects = 0x0001;
    private const uint DesktopCreateWindow = 0x0002;
    private const uint DesktopCreateMenu = 0x0004;
    private const uint DesktopEnumerate = 0x0040;
    private const uint DesktopWriteObjects = 0x0080;
    private const uint DesiredDesktopAccess =
        DesktopReadObjects |
        DesktopCreateWindow |
        DesktopCreateMenu |
        DesktopEnumerate |
        DesktopWriteObjects;

    private WindowsDesktopApi()
    {
    }

    public static WindowsDesktopApi Instance { get; } = new();

    public string GetCurrentWindowStationName()
    {
        var windowStation = GetProcessWindowStation();
        if (windowStation == nint.Zero)
        {
            throw CreateWin32Exception("The current process window station could not be resolved.");
        }

        _ = GetUserObjectInformationW(
            windowStation,
            UserObjectName,
            nint.Zero,
            0,
            out var requiredBytes);
        if (requiredBytes == 0)
        {
            throw CreateWin32Exception("The current window station name size could not be resolved.");
        }

        var buffer = Marshal.AllocHGlobal(checked((int)requiredBytes));
        try
        {
            if (!GetUserObjectInformationW(
                windowStation,
                UserObjectName,
                buffer,
                requiredBytes,
                out _))
            {
                throw CreateWin32Exception("The current window station name could not be read.");
            }

            return Marshal.PtrToStringUni(buffer)?.TrimEnd('\0') ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public nint CreateDesktop(string name)
    {
        var desktop = CreateDesktopW(
            name,
            nint.Zero,
            nint.Zero,
            0,
            DesiredDesktopAccess,
            nint.Zero);
        if (desktop == nint.Zero)
        {
            throw CreateWin32Exception($"Private desktop '{name}' could not be created.");
        }

        return desktop;
    }

    public void AttachCurrentThread(nint desktopHandle)
    {
        if (!SetThreadDesktop(desktopHandle))
        {
            throw CreateWin32Exception(
                "The private desktop could not be attached to the current thread. " +
                "Attach it before the thread creates windows, installs hooks, or initializes COM; " +
                "a managed STA thread may already be ineligible when its delegate begins.");
        }
    }

    public void CloseDesktop(nint desktopHandle)
    {
        if (!CloseDesktopNative(desktopHandle))
        {
            throw CreateWin32Exception(
                "The private desktop could not be closed. " +
                "Ensure every thread attached to it has exited first.");
        }
    }

    private static Win32Exception CreateWin32Exception(string message)
        => new(Marshal.GetLastWin32Error(), message);

    [DllImport("user32.dll")]
    private static extern nint GetProcessWindowStation();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformationW(
        nint userObject,
        int index,
        nint information,
        uint informationLength,
        out uint requiredLength);

    [DllImport(
        "user32.dll",
        EntryPoint = "CreateDesktopW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern nint CreateDesktopW(
        string desktopName,
        nint device,
        nint deviceMode,
        uint flags,
        uint desiredAccess,
        nint securityAttributes);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadDesktop(nint desktop);

    [DllImport(
        "user32.dll",
        EntryPoint = "CloseDesktop",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktopNative(nint desktop);
}
