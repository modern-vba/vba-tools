using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
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
        var attached = new ManualResetEventSlim();
        var releaseWorker = new ManualResetEventSlim();
        var failures = new List<Exception>();
        NativeThreadExit? nativeWorker = null;
        var workerStarted = false;
        Exception? workerFailure = null;
        var worker = new Thread(() =>
        {
            try
            {
                nativeWorker = NativeThreadExit.CaptureCurrent();
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
            workerStarted = true;
            Assert.True(attached.Wait(TimeSpan.FromSeconds(5)));
            Assert.Null(workerFailure);

            Assert.NotNull(nativeWorker);
            Assert.Throws<TimeoutException>(() => nativeWorker.Wait(TimeSpan.Zero));
            Assert.Throws<Win32Exception>(desktop.Dispose);

            releaseWorker.Set();
            WaitForWorkerExit();
            Assert.Null(workerFailure);
            desktop.Dispose();
            Assert.Throws<ObjectDisposedException>(() => desktop.Handle);
        }
        catch (Exception failure) { RecordFailure("Verify desktop close", failure); }
        finally
        {
            releaseWorker.Set();
            if (workerStarted)
            {
                try { WaitForWorkerExit(); }
                catch (Exception failure) { RecordFailure("Drain attached worker", failure); }
            }
            if (workerFailure is not null) RecordFailure("Attached worker", workerFailure);

            try { desktop.Dispose(); }
            catch (Exception failure) { RecordFailure("Dispose desktop during teardown", failure); }
            nativeWorker?.Dispose();
            if (!worker.IsAlive) { attached.Dispose(); releaseWorker.Dispose(); }
        }
        if (failures.Count > 0) throw new AggregateException("Private desktop fixture failed.", failures);

        void WaitForWorkerExit()
        {
            var started = Stopwatch.GetTimestamp();
            var bound = TimeSpan.FromSeconds(5);
            Assert.True(worker.Join(bound), "The attached worker did not leave managed execution.");
            // Join can observe managed termination before Windows releases desktop use.
            // Share the existing bound; never treat IsAlive == false as native exit proof.
            nativeWorker?.Wait(bound - Stopwatch.GetElapsedTime(started));
        }

        void RecordFailure(string stage, Exception failure)
        {
            var nativeCode = failure is Win32Exception native ? $" NativeErrorCode={native.NativeErrorCode}." : string.Empty;
            failures.Add(new InvalidOperationException(stage + "." + nativeCode, failure));
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

    private sealed class NativeThreadExit(uint threadId, SafeWaitHandle handle) : IDisposable
    {
        internal static NativeThreadExit CaptureCurrent()
        {
            var threadId = GetCurrentThreadId();
            // Capture on the live owner itself: this exact thread cannot exit or
            // have its ID reused between the two calls. No terminate rights needed.
            var handle = OpenThread(0x00100000, false, threadId); // SYNCHRONIZE.
            if (handle.IsInvalid)
            {
                var failure = new Win32Exception(Marshal.GetLastWin32Error());
                handle.Dispose();
                throw failure;
            }
            return new NativeThreadExit(threadId, handle);
        }

        internal void Wait(TimeSpan remaining)
        {
            var milliseconds = (uint)Math.Clamp(Math.Ceiling(remaining.TotalMilliseconds), 0, uint.MaxValue - 1d);
            var result = WaitForSingleObject(handle, milliseconds);
            if (result == 0xFFFFFFFF) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (result != 0)
                throw new TimeoutException($"Native worker {threadId} did not exit (wait result 0x{result:X8}).");
        }

        public void Dispose() => handle.Dispose();

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeWaitHandle OpenThread(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint id);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
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
