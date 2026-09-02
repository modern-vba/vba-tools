using System.ComponentModel;
using VbaDev.Infrastructure.Debugging;
using Xunit;

namespace VbaDev.Tests;

public sealed class WindowsPrivateDesktopLeaseTests
{
    [Fact]
    public void CreateOwnsANamedDesktopWithoutReassigningTheCallingThread()
    {
        var callerThreadId = Environment.CurrentManagedThreadId;
        var api = new FakeWindowsDesktopApi
        {
            WindowStationName = "TestWindowStation",
            CreatedHandle = (nint)123
        };

        using (var desktop = WindowsPrivateDesktopLease.Create(
                   api,
                   "vba-dev-automation-test"))
        {
            Assert.Equal("vba-dev-automation-test", desktop.Name);
            Assert.Equal(
                @"TestWindowStation\vba-dev-automation-test",
                desktop.QualifiedName);
            Assert.Equal((nint)123, desktop.Handle);
            Assert.NotEqual(callerThreadId, api.CreateThreadId);
            Assert.Empty(api.AttachedHandles);
            Assert.Empty(api.ClosedHandles);
        }

        Assert.Equal([(nint)123], api.ClosedHandles);
    }

    [Fact]
    public void FailedCloseKeepsTheOwnedHandleAvailableForVerifiedRetry()
    {
        var closeFailure = new InvalidOperationException(
            "A desktop-bound thread is still running.");
        var api = new FakeWindowsDesktopApi();
        api.CloseResults.Enqueue(closeFailure);
        api.CloseResults.Enqueue(null);
        var desktop = WindowsPrivateDesktopLease.Create(
            api,
            "vba-dev-automation-retry");

        var error = Assert.Throws<InvalidOperationException>(desktop.Dispose);

        Assert.Same(closeFailure, error);
        Assert.Equal(api.CreatedHandle, desktop.Handle);
        desktop.Dispose();
        desktop.Dispose();
        Assert.Equal([api.CreatedHandle, api.CreatedHandle], api.ClosedHandles);
        Assert.Throws<ObjectDisposedException>(() => desktop.Handle);
    }

    [Fact]
    public void FailedThreadAttachmentLeavesTheDesktopOwnedForCleanup()
    {
        var attachFailure = new InvalidOperationException(
            "The thread already owns a window or hook.");
        var api = new FakeWindowsDesktopApi
        {
            AttachException = attachFailure
        };
        using var desktop = WindowsPrivateDesktopLease.Create(
            api,
            "vba-dev-automation-attach-failure");

        var error = Assert.Throws<InvalidOperationException>(
            desktop.AttachCurrentThread);

        Assert.Same(attachFailure, error);
        Assert.Equal([api.CreatedHandle], api.AttachedHandles);
        Assert.Empty(api.ClosedHandles);
    }

    [Fact]
    public void CreationFailureDoesNotAttemptToCloseAnUnownedDesktop()
    {
        var createFailure = new InvalidOperationException("Desktop creation failed.");
        var api = new FakeWindowsDesktopApi
        {
            CreateException = createFailure
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            WindowsPrivateDesktopLease.Create(
                api,
                "vba-dev-automation-create-failure"));

        Assert.Same(createFailure, error);
        Assert.Empty(api.ClosedHandles);
    }

    [Fact]
    public void RealDesktopCloseCanBeRetriedAfterItsAttachedWorkerExits()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var desktop = WindowsPrivateDesktopLease.Create();
        using var attached = new ManualResetEventSlim();
        using var releaseWorker = new ManualResetEventSlim();
        Exception? workerFailure = null;
        var worker = new Thread(() =>
        {
            try
            {
                desktop.AttachCurrentThread();
                attached.Set();
                releaseWorker.Wait();
            }
            catch (Exception ex)
            {
                workerFailure = ex;
                attached.Set();
            }
        })
        {
            IsBackground = true,
            Name = "VbaDev private desktop lease test"
        };

        try
        {
            worker.Start();
            Assert.True(attached.Wait(TimeSpan.FromSeconds(5)));
            Assert.Null(workerFailure);

            Assert.Throws<Win32Exception>(desktop.Dispose);

            releaseWorker.Set();
            Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
            Assert.Null(workerFailure);
            desktop.Dispose();
            Assert.Throws<ObjectDisposedException>(() => desktop.Handle);
        }
        finally
        {
            releaseWorker.Set();
            if (worker.IsAlive)
            {
                worker.Join(TimeSpan.FromSeconds(5));
            }

            desktop.Dispose();
        }
    }

    [Fact]
    public void RealEmptyDesktopCanBeObservedBeforeItsFirstWindowExists()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var desktop = WindowsPrivateDesktopLease.Create();
        var windows = WindowsDesktopWindowObservationNativeApi.Instance
            .EnumerateTopLevelWindows(new DesktopWindowObservationScope(
                desktop.Handle,
                desktop.QualifiedName,
                DesktopWindowLocation.Private));

        Assert.Empty(windows);
    }

    private sealed class FakeWindowsDesktopApi : IWindowsDesktopApi
    {
        public string WindowStationName { get; set; } = "TestWindowStation";

        public nint CreatedHandle { get; set; } = (nint)123;

        public int CreateThreadId { get; private set; }

        public List<nint> AttachedHandles { get; } = [];

        public List<nint> ClosedHandles { get; } = [];

        public Queue<Exception?> CloseResults { get; } = [];

        public Exception? AttachException { get; set; }

        public Exception? CreateException { get; set; }

        public string GetCurrentWindowStationName() => WindowStationName;

        public nint CreateDesktop(string name)
        {
            CreateThreadId = Environment.CurrentManagedThreadId;
            if (CreateException is not null)
            {
                throw CreateException;
            }

            return CreatedHandle;
        }

        public void AttachCurrentThread(nint desktopHandle)
        {
            AttachedHandles.Add(desktopHandle);
            if (AttachException is not null)
            {
                throw AttachException;
            }
        }

        public void CloseDesktop(nint desktopHandle)
        {
            ClosedHandles.Add(desktopHandle);
            if (CloseResults.TryDequeue(out var error) && error is not null)
            {
                throw error;
            }
        }

    }
}
