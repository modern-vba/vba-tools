using VbaDebugAdapter.Debugging;
using VbaDebugAdapter.Infrastructure;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class WindowsDebugWindowActivatorTests
{
    [Fact]
    public void BringOwnedWindowToForegroundRejectsAWindowFromAnotherProcess()
    {
        var windowApi = new FakeWindowsDebugWindowApi { TargetProcessId = 100 };
        var activator = new WindowsDebugWindowActivator(windowApi);

        var error = Assert.Throws<DebugSetupException>(() =>
            activator.BringOwnedWindowToForeground((nint)1234, processId: 200));

        Assert.Contains("owned Excel process", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, windowApi.RestoreCalls);
        Assert.Equal(0, windowApi.SetForegroundCalls);
    }

    [Fact]
    public void BringOwnedWindowToForegroundRestoresAndVerifiesTheOwnedProcess()
    {
        var windowApi = new FakeWindowsDebugWindowApi
        {
            TargetProcessId = 200,
            ForegroundProcessId = 200,
            ForegroundWindowHandle = (nint)1234,
            SetForegroundResult = false,
            ReturnNoForegroundOnce = true
        };
        var activator = new WindowsDebugWindowActivator(windowApi);

        Assert.True(activator.BringOwnedWindowToForeground((nint)1234, processId: 200));

        Assert.Equal(1, windowApi.RestoreCalls);
        Assert.Equal(1, windowApi.SetForegroundCalls);
        Assert.Equal(2, windowApi.GetForegroundWindowCalls);
    }

    [Fact]
    public void BringOwnedWindowToForegroundDoesNotRequireAnotherWindowFromTheOwnedProcessToYield()
    {
        var windowApi = new FakeWindowsDebugWindowApi
        {
            TargetProcessId = 200,
            ForegroundProcessId = 200,
            ForegroundWindowHandle = (nint)5678,
            SetForegroundResult = true
        };
        var activator = new WindowsDebugWindowActivator(windowApi);

        Assert.False(activator.BringOwnedWindowToForeground((nint)1234, processId: 200));

        Assert.Equal(1, windowApi.RestoreCalls);
        Assert.Equal(1, windowApi.SetForegroundCalls);
        Assert.Equal(41, windowApi.GetForegroundWindowCalls);
    }

    [Fact]
    public void BringOwnedWindowToForegroundDoesNotRequireAnUnrelatedWindowToYield()
    {
        var windowApi = new FakeWindowsDebugWindowApi
        {
            TargetProcessId = 200,
            ForegroundProcessId = 100,
            SetForegroundResult = false
        };
        var activator = new WindowsDebugWindowActivator(windowApi);

        Assert.False(activator.BringOwnedWindowToForeground((nint)1234, processId: 200));

        Assert.Equal(1, windowApi.RestoreCalls);
        Assert.Equal(1, windowApi.SetForegroundCalls);
        Assert.Equal(41, windowApi.GetForegroundWindowCalls);
    }
}

internal sealed class FakeWindowsDebugWindowApi : IWindowsDebugWindowApi
{
    public int ForegroundPermissionHResult { get; init; }

    public int TargetProcessId { get; init; }

    public int ForegroundProcessId { get; init; }

    public nint ForegroundWindowHandle { get; init; } = (nint)5678;

    public bool SetForegroundResult { get; init; } = true;

    public bool ReturnNoForegroundOnce { get; init; }

    public int RestoreCalls { get; private set; }

    public int SetForegroundCalls { get; private set; }

    public int GetForegroundWindowCalls { get; private set; }

    public int AllowComServerForeground(object comServerObject)
        => ForegroundPermissionHResult;

    public int GetProcessId(nint windowHandle)
        => windowHandle == (nint)5678 ? ForegroundProcessId : TargetProcessId;

    public void Restore(nint windowHandle) => RestoreCalls++;

    public bool SetForeground(nint windowHandle)
    {
        SetForegroundCalls++;
        return SetForegroundResult;
    }

    public nint GetForegroundWindow()
    {
        GetForegroundWindowCalls++;
        if (ReturnNoForegroundOnce && GetForegroundWindowCalls == 1)
        {
            return nint.Zero;
        }

        return ForegroundWindowHandle;
    }

    public void WaitForForegroundTransition()
    {
    }
}
