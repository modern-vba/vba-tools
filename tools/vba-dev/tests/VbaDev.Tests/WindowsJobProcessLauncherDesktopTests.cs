using System.Text;
using VbaDev.Infrastructure.Debugging;
using Xunit;

namespace VbaDev.Tests;

public sealed class WindowsJobProcessLauncherDesktopTests
{
    [Fact]
    public async Task DesktopObservationRetriesWhileThePublisherHoldsTheFileLock()
    {
        var observationPath = Path.Combine(
            Path.GetTempPath(),
            $"vba-dev-desktop-probe-observer-{Guid.NewGuid():N}.txt");
        const string expected = "vba-dev-private-desktop";
        FileStream? writer = null;
        try
        {
            writer = new FileStream(
                observationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            var observation = WaitForObservationAsync(
                observationPath,
                expected,
                TimeSpan.FromSeconds(2));
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            await writer.WriteAsync(Encoding.UTF8.GetBytes(expected));
            await writer.FlushAsync();
            writer.Dispose();
            writer = null;

            Assert.Equal(expected, await observation);
        }
        finally
        {
            writer?.Dispose();
            File.Delete(observationPath);
        }
    }

    [Fact]
    public async Task AtomicJobLaunchKeepsTheProcessSuspendedThenTargetsTheRequestedDesktop()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var observationPath = Path.Combine(
            Path.GetTempPath(),
            $"vba-dev-desktop-probe-{Guid.NewGuid():N}.txt");
        try
        {
            using var desktop = WindowsPrivateDesktopLease.Create();
            var job = WindowsDebugProcessJob.Create();
            DebugSuspendedProcessLaunch? launch = null;
            try
            {
                launch = job.StartSuspended(
                    GetWindowsPowerShellPath(),
                    [
                        "-NoLogo",
                        "-NoProfile",
                        "-NonInteractive",
                        "-EncodedCommand",
                        CreateDesktopProbeCommand(observationPath)
                    ],
                    desktop.QualifiedName);

                Assert.False(launch.Process.HasExited);
                Assert.False(File.Exists(observationPath));

                launch.PrimaryThread.ResumeExactlyOnce();
                Assert.Equal(
                    desktop.Name,
                    await WaitForObservationAsync(
                        observationPath,
                        desktop.Name,
                        TimeSpan.FromSeconds(10)));
                Assert.False(launch.Process.HasExited);
            }
            finally
            {
                launch?.PrimaryThread.Dispose();
                job.Dispose();
                if (launch is not null && !launch.Process.HasExited)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await launch.Process.WaitForExitAsync(timeout.Token);
                }

                launch?.Process.Dispose();
            }
        }
        finally
        {
            if (File.Exists(observationPath))
            {
                File.Delete(observationPath);
            }
        }
    }

    private static string GetWindowsPowerShellPath()
        => Path.Combine(
            Environment.SystemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");

    private static string CreateDesktopProbeCommand(string observationPath)
    {
        var escapedPath = observationPath.Replace("'", "''", StringComparison.Ordinal);
        var script = $$"""
            Add-Type -TypeDefinition @'
            using System;
            using System.Runtime.InteropServices;

            public static class VbaDevDesktopProbe
            {
                [DllImport("kernel32.dll")]
                public static extern uint GetCurrentThreadId();

                [DllImport("user32.dll", SetLastError = true)]
                public static extern IntPtr GetThreadDesktop(uint threadId);

                [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
                [return: MarshalAs(UnmanagedType.Bool)]
                public static extern bool GetUserObjectInformation(
                    IntPtr userObject,
                    int index,
                    IntPtr information,
                    uint informationLength,
                    out uint requiredLength);
            }
            '@
            $desktop = [VbaDevDesktopProbe]::GetThreadDesktop(
                [VbaDevDesktopProbe]::GetCurrentThreadId())
            $required = [uint32]0
            [VbaDevDesktopProbe]::GetUserObjectInformation(
                $desktop, 2, [IntPtr]::Zero, 0, [ref]$required) | Out-Null
            $buffer = [Runtime.InteropServices.Marshal]::AllocHGlobal([int]$required)
            try {
                if (-not [VbaDevDesktopProbe]::GetUserObjectInformation(
                    $desktop, 2, $buffer, $required, [ref]$required)) {
                    throw [ComponentModel.Win32Exception]::new(
                        [Runtime.InteropServices.Marshal]::GetLastWin32Error())
                }
                $name = [Runtime.InteropServices.Marshal]::PtrToStringUni($buffer)
                [IO.File]::WriteAllText('{{escapedPath}}', $name)
            }
            finally {
                [Runtime.InteropServices.Marshal]::FreeHGlobal($buffer)
            }
            Start-Sleep -Seconds 30
            """;
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }

    private static async Task<string> WaitForObservationAsync(
        string path,
        string expected,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(path))
                {
                    var observation = File.ReadAllText(path);
                    if (observation.Equals(expected, StringComparison.Ordinal))
                    {
                        return observation;
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // The publisher creates the path before the exact text is fully closed.
            }

            await Task.Delay(20);
        }

        throw new TimeoutException(
            $"The suspended-launch desktop probe did not write '{path}' before the deadline.");
    }
}
