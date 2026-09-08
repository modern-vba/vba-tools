using VbaDebugAdapter.Infrastructure;
using VbaTools.Processes;

namespace VbaDebugAdapter.Build;

public sealed class ProcessVbaDevBuildProcess : IVbaDevBuildProcess
{
    public async Task<VbaDevBuildProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await new ProcessInvocation(fileName, WindowsJobProcessPlatform.Instance)
            .RunAsync(arguments, cancellationToken).ConfigureAwait(false);
        return new VbaDevBuildProcessResult(
            result.ExitCode, result.StandardOutput, result.StandardError);
    }
}

internal sealed class WindowsJobProcessPlatform : IProcessPlatform
{
    public static WindowsJobProcessPlatform Instance { get; } = new();
    private readonly Func<IDebugProcessJob> createJob;
    private readonly Func<IDebugProcessJob, string, IReadOnlyList<string>, DebugSuspendedProcessLaunch> start;

    private WindowsJobProcessPlatform()
        : this(WindowsDebugProcessJob.Create,
            (job, executable, arguments) => ((WindowsDebugProcessJob)job)
                .StartSuspended(executable, arguments, redirectOutput: true)) { }

    internal WindowsJobProcessPlatform(
        Func<IDebugProcessJob> createJob,
        Func<IDebugProcessJob, string, IReadOnlyList<string>, DebugSuspendedProcessLaunch> start)
    {
        this.createJob = createJob;
        this.start = start;
    }

    public IProcessHandle Start(string executablePath, IReadOnlyList<string> arguments)
    {
        var job = createJob();
        try
        {
            var launch = start(job, executablePath, arguments);
            return new WindowsJobProcessHandle(launch, job);
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }
}

internal sealed class WindowsJobProcessHandle(
    DebugSuspendedProcessLaunch launch,
    IDebugProcessJob job) : IProcessHandle
{
    public int ExitCode => launch.Process.ExitCode;
    public Task<string> ReadStandardOutputToEndAsync() => launch.StandardOutput!.ReadToEndAsync();
    public Task<string> ReadStandardErrorToEndAsync() => launch.StandardError!.ReadToEndAsync();
    public void Resume() => launch.PrimaryThread.ResumeExactlyOnce();
    public Task WaitForExitAsync(CancellationToken cancellationToken)
        => launch.Process.WaitForExitAsync(cancellationToken);
    public void KillEntireProcessTree() => job.Terminate();

    public void Dispose()
    {
        // Handle release must never re-enter a terminal wait after the common cleanup deadline.
        try { job.Dispose(); }
        finally
        {
            try { launch.PrimaryThread.Dispose(); }
            finally
            {
                try { launch.StandardOutput?.Dispose(); }
                finally
                {
                    try { launch.StandardError?.Dispose(); }
                    finally { launch.Process.Dispose(); }
                }
            }
        }
    }
}
