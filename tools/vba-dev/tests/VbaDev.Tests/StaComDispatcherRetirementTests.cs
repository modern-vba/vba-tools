using System.Diagnostics;
using Xunit;

namespace VbaDev.Tests;

public sealed class StaComDispatcherRetirementTests
{
    [Theory]
    [InlineData(DispatcherRetirementProbeProgram.ProbeArgument, DispatcherRetirementProbeProgram.SuccessMarker)]
    [InlineData(RuntimeRetirementObservationProbe.SuccessArgument, RuntimeRetirementObservationProbe.SuccessMarker)]
    [InlineData(RuntimeRetirementObservationProbe.FailureArgument, RuntimeRetirementObservationProbe.SuccessMarker)]
    public async Task RetiredWorkerAndItsRuntimeObserverPreserveTerminalEvidence(
        string probeArgument, string successMarker)
    {
        var assemblyPath = Path.Combine(AppContext.BaseDirectory, "VbaDev.Tests.dll");
        Assert.True(File.Exists(assemblyPath), $"The built test assembly is missing: {assemblyPath}");
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(assemblyPath);
        startInfo.ArgumentList.Add(probeArgument);
        using var child = new Process { StartInfo = startInfo };
        var failures = new List<Exception>();
        var started = false;
        Task<string>? output = null;
        Task<string>? error = null;
        var stdout = string.Empty;
        var stderr = string.Empty;
        int? exitCode = null;

        try
        {
            started = child.Start();
            if (!started) throw new InvalidOperationException("The retirement probe did not start.");
            output = child.StandardOutput.ReadToEndAsync();
            error = child.StandardError.ReadToEndAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            exitCode = child.ExitCode;
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }
        finally
        {
            if (started)
            {
                try
                {
                    if (!child.HasExited) child.Kill();
                }
                catch (Exception failure)
                {
                    failures.Add(new InvalidOperationException("Stop the exact owned probe child", failure));
                }

                try
                {
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    exitCode = child.ExitCode;
                }
                catch (Exception failure)
                {
                    failures.Add(new InvalidOperationException("Drain the owned probe child", failure));
                }
            }

            stdout = await ReadOutput(output, "stdout");
            stderr = await ReadOutput(error, "stderr");
        }

        var details = $"Exit: {exitCode}\nStdout:\n{stdout}\nStderr:\n{stderr}\n" +
            string.Join("\n", failures.Select(failure => failure.ToString()));
        Assert.True(failures.Count == 0 && exitCode == 0, details);
        Assert.Equal(successMarker, stdout.TrimEnd());
        Assert.Equal(string.Empty, stderr);

        async Task<string> ReadOutput(Task<string>? read, string streamName)
        {
            if (read is null) return string.Empty;
            try { return await read.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception failure)
            {
                failures.Add(new InvalidOperationException($"Drain child {streamName}", failure));
                return $"<{streamName} drain failed>";
            }
        }
    }
}
