using System.Diagnostics;

namespace VbaDebugAdapter.Cli;

public sealed class ProcessVbaDevCapabilitiesProbe : IVbaDevCapabilitiesProbe
{
    public async Task<VbaDevCapabilitiesProbeResult> ProbeAsync(
        string vbaDevPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vbaDevPath);
        if (!Path.IsPathFullyQualified(vbaDevPath))
        {
            throw new ArgumentException(
                "The supplied vba-dev path must be absolute.",
                nameof(vbaDevPath));
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Path.GetFullPath(vbaDevPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("capabilities");
        process.StartInfo.ArgumentList.Add("--format");
        process.StartInfo.ArgumentList.Add("json");

        process.Start();
        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(standardOutputTask, standardErrorTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            throw;
        }

        return new VbaDevCapabilitiesProbeResult(
            process.ExitCode,
            await standardOutputTask.ConfigureAwait(false),
            await standardErrorTask.ConfigureAwait(false));
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}

public sealed record VbaDevCapabilitiesProbeResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

public interface IVbaDevCapabilitiesProbe
{
    Task<VbaDevCapabilitiesProbeResult> ProbeAsync(
        string vbaDevPath,
        CancellationToken cancellationToken);
}
