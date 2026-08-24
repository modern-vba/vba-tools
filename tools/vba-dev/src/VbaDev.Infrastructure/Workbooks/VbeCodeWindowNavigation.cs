using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Text;

namespace VbaDev.Infrastructure.Workbooks;

internal sealed record VbeCodeWindowNavigationPair(
    nint CodeWindow,
    int OwnedProcessId,
    nint ObjectBox,
    nint ProcedureBox);

internal static class VbeCodeWindowNavigation
{
    private const uint WmCommand = 0x0111;
    private const uint WmMdiGetActive = 0x0229;
    private const uint WmLeftButtonDown = 0x0201;
    private const uint WmLeftButtonUp = 0x0202;
    private const int MouseKeyLeftButton = 0x0001;
    private const uint CbGetCount = 0x0146;
    private const uint CbGetCurrentSelection = 0x0147;
    private const uint CbGetItemText = 0x0148;
    private const uint CbGetItemTextLength = 0x0149;
    private const uint CbSetCurrentSelection = 0x014e;
    private const uint CbShowDropDown = 0x014f;
    private const int CbnSelectionChange = 1;
    private const int CbnDropDown = 7;
    private const int CbnCloseUp = 8;
    private const int WindowLongStyle = -16;
    private const long ComboOwnerDrawFixed = 0x0010;
    private const long ComboOwnerDrawVariable = 0x0020;
    private const long ComboHasStrings = 0x0200;
    private const uint SendTimeoutBlock = 0x0001;
    private const uint SendTimeoutAbortIfHung = 0x0002;
    private const uint SendTimeoutErrorOnExit = 0x0020;
    private const uint UiMessageTimeoutMilliseconds = 2_000;
    private const uint SetWindowPositionNoSize = 0x0001;
    private const uint SetWindowPositionNoZOrder = 0x0004;
    private const uint SetWindowPositionNoActivate = 0x0010;
    private const int HideWindow = 0;
    private static readonly nint ComboError = new(-1);

    public static VbeCodeWindowNavigationPair Discover(
        nint codeWindow,
        int ownedProcessId)
    {
        RequireOwnedWindow(codeWindow, codeWindow, ownedProcessId);
        var candidates = new List<ComboCandidate>();
        Exception? callbackError = null;
        _ = EnumChildWindows(
            codeWindow,
            (window, parameter) =>
            {
                try
                {
                    if (!IsOwnedDescendant(codeWindow, window, ownedProcessId) ||
                        !ReadClassName(window).Equals("ComboBox", StringComparison.Ordinal))
                    {
                        return true;
                    }

                    var style = ReadWindowStyle(window);
                    var ownerDrawn = (style & (ComboOwnerDrawFixed | ComboOwnerDrawVariable)) != 0;
                    if (ownerDrawn && (style & ComboHasStrings) == 0)
                    {
                        throw new InvalidOperationException(
                            "The VBE code-window navigation control is owner-drawn without string storage.");
                    }

                    if (!GetWindowRect(window, out var rectangle))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }

                    if (rectangle.Right <= rectangle.Left || rectangle.Bottom <= rectangle.Top)
                    {
                        return true;
                    }

                    candidates.Add(new ComboCandidate(window, rectangle));
                    return true;
                }
                catch (Exception exception)
                {
                    callbackError = exception;
                    return false;
                }
            },
            nint.Zero);
        if (callbackError is not null)
        {
            ExceptionDispatchInfo.Capture(callbackError).Throw();
        }

        var pairs = candidates
            .GroupBy(candidate => candidate.Rectangle.Top)
            .Where(group => group.Count() == 2)
            .Select(group => group.OrderBy(candidate => candidate.Rectangle.Left).ToArray())
            .Where(pair => pair[0].Rectangle.Right <= pair[1].Rectangle.Left)
            .ToArray();
        if (pairs.Length != 1)
        {
            throw new InvalidOperationException(
                $"The active VBE code window exposed {pairs.Length} unambiguous Object/Event navigation pairs instead of one.");
        }

        return new VbeCodeWindowNavigationPair(
            codeWindow,
            ownedProcessId,
            pairs[0][0].Window,
            pairs[0][1].Window);
    }

    public static VbeCodeWindowNavigationPair DiscoverActiveCodeWindow(
        nint mainWindow,
        int ownedProcessId)
    {
        RequireOwnedWindow(mainWindow, mainWindow, ownedProcessId);
        var mdiClients = EnumerateOwnedDescendants(mainWindow, ownedProcessId)
            .Where(window => ReadClassName(window).Equals(
                "MDIClient",
                StringComparison.Ordinal))
            .ToArray();
        if (mdiClients.Length != 1)
        {
            throw new InvalidOperationException(
                $"The owned VBE window exposed {mdiClients.Length} MDI clients instead of one.");
        }

        var activeCodeWindow = Send(
            mdiClients[0],
            WmMdiGetActive,
            nint.Zero,
            nint.Zero);
        if (activeCodeWindow == nint.Zero)
        {
            throw new InvalidOperationException(
                "The owned VBE MDI client did not expose an active code window.");
        }

        RequireOwnedWindow(mainWindow, activeCodeWindow, ownedProcessId);
        return Discover(activeCodeWindow, ownedProcessId);
    }

    public static void PrepareOffscreen(nint window, int ownedProcessId)
    {
        RequireOwnedWindow(window, window, ownedProcessId);
        if (!SetWindowPos(
                window,
                nint.Zero,
                -32_000,
                -32_000,
                0,
                0,
                SetWindowPositionNoSize |
                SetWindowPositionNoZOrder |
                SetWindowPositionNoActivate))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public static void HideOffscreen(nint window, int ownedProcessId)
    {
        RequireOwnedWindow(window, window, ownedProcessId);
        PrepareOffscreen(window, ownedProcessId);
        _ = ShowWindow(window, HideWindow);
        if (IsWindowVisible(window))
        {
            throw new InvalidOperationException(
                "The exactly owned VBE window could not be kept hidden for host-class inspection.");
        }
    }

    public static IReadOnlyList<string> ReadObjectItems(VbeCodeWindowNavigationPair pair)
        => ReadItems(pair, pair.ObjectBox);

    public static IReadOnlyList<string> ReadProcedureItems(VbeCodeWindowNavigationPair pair)
        => ReadItems(pair, pair.ProcedureBox);

    public static string ReadCurrentObject(VbeCodeWindowNavigationPair pair)
        => ReadCurrentItem(pair, pair.ObjectBox);

    public static string ReadCurrentProcedure(VbeCodeWindowNavigationPair pair)
        => ReadCurrentItem(pair, pair.ProcedureBox);

    public static void SelectObject(VbeCodeWindowNavigationPair pair, int index)
        => Select(pair, pair.ObjectBox, index);

    private static IReadOnlyList<string> ReadItems(
        VbeCodeWindowNavigationPair pair,
        nint combo)
    {
        RequireOwnedWindow(pair.CodeWindow, combo, pair.OwnedProcessId);
        ClickDropDownArrow(combo);
        Notify(combo, pair, CbnDropDown);
        _ = Send(combo, CbShowDropDown, new nint(1), nint.Zero);
        try
        {
            var countResult = Send(combo, CbGetCount, nint.Zero, nint.Zero);
            if (countResult == ComboError || countResult < nint.Zero)
            {
                throw new InvalidOperationException(
                    "The VBE code-window navigation item count could not be read.");
            }

            var count = checked((int)countResult);
            var items = new string[count];
            for (var index = 0; index < count; index++)
            {
                items[index] = ReadItem(combo, index);
            }

            return items;
        }
        finally
        {
            _ = Send(combo, CbShowDropDown, nint.Zero, nint.Zero);
            Notify(combo, pair, CbnCloseUp);
        }
    }

    private static void ClickDropDownArrow(nint combo)
    {
        if (!GetClientRect(combo, out var rectangle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var x = Math.Max(1, rectangle.Right - 4);
        var y = Math.Max(1, (rectangle.Bottom - rectangle.Top) / 2);
        var coordinates = new nint((y << 16) | (x & 0xffff));
        _ = Send(
            combo,
            WmLeftButtonDown,
            new nint(MouseKeyLeftButton),
            coordinates);
        _ = Send(combo, WmLeftButtonUp, nint.Zero, coordinates);
    }

    private static string ReadCurrentItem(
        VbeCodeWindowNavigationPair pair,
        nint combo)
    {
        RequireOwnedWindow(pair.CodeWindow, combo, pair.OwnedProcessId);
        var selected = Send(combo, CbGetCurrentSelection, nint.Zero, nint.Zero);
        if (selected == ComboError || selected < nint.Zero)
        {
            throw new InvalidOperationException(
                "The current VBE code-window navigation item could not be read.");
        }

        return ReadItem(combo, checked((int)selected));
    }

    private static string ReadItem(nint combo, int index)
    {
        var lengthResult = Send(
            combo,
            CbGetItemTextLength,
            new nint(index),
            nint.Zero);
        if (lengthResult == ComboError || lengthResult < nint.Zero)
        {
            throw new InvalidOperationException(
                "A VBE code-window navigation item length could not be read.");
        }

        var buffer = new StringBuilder(checked((int)lengthResult + 1));
        var copied = SendText(
            combo,
            CbGetItemText,
            new nint(index),
            buffer);
        if (copied == ComboError || copied < nint.Zero)
        {
            throw new InvalidOperationException(
                "A VBE code-window navigation item could not be read.");
        }

        return buffer.ToString();
    }

    private static void Select(
        VbeCodeWindowNavigationPair pair,
        nint combo,
        int index)
    {
        RequireOwnedWindow(pair.CodeWindow, combo, pair.OwnedProcessId);
        if (Send(combo, CbSetCurrentSelection, new nint(index), nint.Zero) != new nint(index))
        {
            throw new InvalidOperationException(
                "The VBE code-window Object selection could not be changed.");
        }

        Notify(combo, pair, CbnSelectionChange);
        if (Send(combo, CbGetCurrentSelection, nint.Zero, nint.Zero) != new nint(index))
        {
            throw new InvalidOperationException(
                "The VBE code-window Object selection was not retained.");
        }
    }

    private static void Notify(
        nint combo,
        VbeCodeWindowNavigationPair pair,
        int notificationCode)
    {
        var parent = GetParent(combo);
        var controlId = GetDlgCtrlId(combo);
        if (parent == nint.Zero || controlId <= 0)
        {
            throw new InvalidOperationException(
                "The VBE code-window navigation control identity could not be established.");
        }

        RequireOwnedWindow(pair.CodeWindow, parent, pair.OwnedProcessId);
        var command = new nint(
            (controlId & 0xffff) | (notificationCode << 16));
        _ = Send(parent, WmCommand, command, combo);
    }

    private static bool IsOwnedDescendant(
        nint codeWindow,
        nint candidate,
        int ownedProcessId)
    {
        _ = GetWindowThreadProcessId(candidate, out var processId);
        return processId == ownedProcessId && IsChild(codeWindow, candidate);
    }

    private static IReadOnlyList<nint> EnumerateOwnedDescendants(
        nint parent,
        int ownedProcessId)
    {
        var descendants = new List<nint>();
        _ = EnumChildWindows(
            parent,
            (window, parameter) =>
            {
                if (IsOwnedDescendant(parent, window, ownedProcessId))
                {
                    descendants.Add(window);
                }

                return true;
            },
            nint.Zero);
        return descendants;
    }

    private static void RequireOwnedWindow(
        nint codeWindow,
        nint candidate,
        int ownedProcessId)
    {
        _ = GetWindowThreadProcessId(candidate, out var processId);
        var isExpectedRoot = candidate == codeWindow;
        if (processId != ownedProcessId ||
            (!isExpectedRoot && !IsChild(codeWindow, candidate)))
        {
            throw new InvalidOperationException(
                $"The VBE navigation window does not belong to the exactly owned Excel process and active code-window subtree (expected PID {ownedProcessId}, actual PID {processId}, code HWND 0x{codeWindow.ToInt64():X}, candidate HWND 0x{candidate.ToInt64():X}).");
        }
    }

    private static string ReadClassName(nint window)
    {
        var buffer = new StringBuilder(256);
        var length = GetClassName(window, buffer, buffer.Capacity);
        if (length == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return buffer.ToString();
    }

    private static long ReadWindowStyle(nint window)
        => nint.Size == 8
            ? GetWindowLongPtr64(window, WindowLongStyle).ToInt64()
            : GetWindowLong32(window, WindowLongStyle);

    private static nint Send(
        nint window,
        uint message,
        nint parameter,
        nint data)
    {
        if (SendMessageTimeout(
                window,
                message,
                parameter,
                data,
                SendTimeoutBlock | SendTimeoutAbortIfHung | SendTimeoutErrorOnExit,
                UiMessageTimeoutMilliseconds,
                out var result) == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return result;
    }

    private static nint SendText(
        nint window,
        uint message,
        nint parameter,
        StringBuilder data)
    {
        if (SendMessageTimeout(
                window,
                message,
                parameter,
                data,
                SendTimeoutBlock | SendTimeoutAbortIfHung | SendTimeoutErrorOnExit,
                UiMessageTimeoutMilliseconds,
                out var result) == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return result;
    }

    private sealed record ComboCandidate(nint Window, NativeRectangle Rectangle);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsCallback(nint window, nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(
        nint parent,
        EnumWindowsCallback callback,
        nint parameter);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(
        nint window,
        StringBuilder className,
        int maximumCharacterCount);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(
        nint window,
        out NativeRectangle rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(
        nint window,
        out NativeRectangle rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(
        nint window,
        out int processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(nint parent, nint child);

    [DllImport("user32.dll")]
    private static extern nint GetParent(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll", EntryPoint = "GetDlgCtrlID", SetLastError = true)]
    private static extern int GetDlgCtrlId(nint window);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SendMessageTimeout(
        nint window,
        uint message,
        nint parameter,
        nint data,
        uint flags,
        uint timeoutMilliseconds,
        out nint result);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SendMessageTimeout(
        nint window,
        uint message,
        nint parameter,
        StringBuilder data,
        uint flags,
        uint timeoutMilliseconds,
        out nint result);
}
