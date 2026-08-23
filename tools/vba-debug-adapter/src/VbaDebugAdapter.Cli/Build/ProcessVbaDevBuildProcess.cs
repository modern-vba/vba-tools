using VbaDebugAdapter.Infrastructure;

namespace VbaDebugAdapter.Build;

public sealed class ProcessVbaDevBuildProcess : IVbaDevBuildProcess
{
    public async Task<VbaDevBuildProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The VBA debug adapter process boundary requires Windows.");
        }

        WindowsDebugProcessJob? job = null;
        DebugSuspendedProcessLaunch? launch = null;
        DebugExcelProcessOwner? processOwner = null;
        try
        {
            job = WindowsDebugProcessJob.Create();
            launch = job.StartSuspended(
                Path.GetFullPath(fileName),
                arguments,
                redirectOutput: true);
            processOwner = DebugExcelProcessOwner.AdoptPreassignedProcess(
                launch.Process,
                job);
            job = null;
            var standardOutput = launch.StandardOutput
                ?? throw new InvalidOperationException(
                    "The atomically owned vba-dev process has no standard-output pipe.");
            var standardError = launch.StandardError
                ?? throw new InvalidOperationException(
                    "The atomically owned vba-dev process has no standard-error pipe.");
            var standardOutputTask = standardOutput.ReadToEndAsync();
            var standardErrorTask = standardError.ReadToEndAsync();
            launch.PrimaryThread.ResumeExactlyOnce();
            try
            {
                _ = await processOwner.Completion
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await processOwner.TerminateAsync().ConfigureAwait(false);
                await Task.WhenAll(standardOutputTask, standardErrorTask).ConfigureAwait(false);
                throw;
            }

            await Task.WhenAll(standardOutputTask, standardErrorTask).ConfigureAwait(false);
            var processExit = await processOwner.Completion.ConfigureAwait(false);
            return new VbaDevBuildProcessResult(
                processExit.ExitCode,
                await standardOutputTask.ConfigureAwait(false),
                await standardErrorTask.ConfigureAwait(false));
        }
        finally
        {
            launch?.PrimaryThread.Dispose();
            launch?.StandardOutput?.Dispose();
            launch?.StandardError?.Dispose();
            if (processOwner is not null)
            {
                await processOwner.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                launch?.Process.Dispose();
                job?.Dispose();
            }
        }
    }
}
