using System.Runtime.ExceptionServices;
using VbaDebugAdapter.Infrastructure;
using VbaTools.Processes;

namespace VbaDebugAdapter.Build;

public sealed class ProcessVbaDevBuildProcess : IVbaDevBuildProcess
{
    private readonly Func<WindowsJobProcessPlatform> createPlatform;

    public ProcessVbaDevBuildProcess() : this(() => new WindowsJobProcessPlatform()) { }

    internal ProcessVbaDevBuildProcess(Func<WindowsJobProcessPlatform> createPlatform)
        => this.createPlatform = createPlatform;

    public async Task<VbaDevBuildProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var platform = createPlatform();
        try
        {
            var result = await new ProcessInvocation(fileName, platform)
                .RunAsync(arguments, cancellationToken).ConfigureAwait(false);
            return new VbaDevBuildProcessResult(
                result.ExitCode, result.StandardOutput, result.StandardError)
            {
                CleanupOutcome = platform.CleanupOutcome
            };
        }
        catch (Exception failure)
        {
            var lifecycle = failure as ProcessLifecycleException;
            var completion = new DebugFailureCompletion(lifecycle?.PrimaryFailure ?? failure);
            if (lifecycle?.TerminationFailure is { } termination)
            {
                completion.AddFailure("companion-termination", fileName,
                    DebugResourceKind.Process, termination);
            }
            if (lifecycle is not null)
            {
                completion.AddFailure("companion-terminal-cleanup", fileName,
                    DebugResourceKind.Observation, lifecycle.CleanupFailure);
            }
            if (platform.CleanupOutcome is { } outcome) { completion.Merge(outcome); }
            else if (!platform.StartAttempted)
            {
                completion.AddEvidence(new("companion-acquisition", fileName, DebugResourceKind.Process,
                    true, "The invocation was rejected before acquiring a process."));
                completion.AddEvidence(new("companion-acquisition", fileName, DebugResourceKind.Handle,
                    true, "The invocation was rejected before acquiring handles."));
            }
            else
            {
                completion.AddEvidence(new("companion-cleanup", fileName, DebugResourceKind.Process,
                    false, "The invocation owner did not supply terminal process evidence."));
                completion.AddEvidence(new("companion-cleanup", fileName, DebugResourceKind.Handle,
                    false, "The invocation owner did not supply handle-release evidence."));
            }
            var retained = completion.Complete();
            if (retained.PrimaryFailure is OperationCanceledException && !retained.HasCleanupFailure)
            {
                throw new DebugFailureCanceledException(retained);
            }
            throw new DebugFailureException(retained);
        }
    }
}

internal sealed class WindowsJobProcessPlatform : IProcessPlatform, IDebugResourceOwnerEvidence
{
    private readonly Func<IDebugProcessJob> createJob;
    private readonly Func<IDebugProcessJob, string, IReadOnlyList<string>, DebugSuspendedProcessLaunch> start;

    private WindowsJobProcessHandle? handle;
    private DebugFailureOutcome? startupOutcome;

    public bool StartAttempted { get; private set; }
    public DebugFailureOutcome? CleanupOutcome => handle?.CleanupOutcome ?? startupOutcome;

    internal WindowsJobProcessPlatform()
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
        StartAttempted = true;
        IDebugProcessJob? job = null;
        var launchAttempted = false;
        try
        {
            job = createJob();
            launchAttempted = true;
            var launch = start(job, executablePath, arguments);
            return handle = new WindowsJobProcessHandle(launch, job);
        }
        catch (Exception primary)
        {
            var completion = new DebugFailureCompletion(primary);
            if (job is not null)
            {
                try { job.Dispose(); }
                catch (Exception cleanup)
                {
                    completion.AddFailure("companion-job-release", "companion Job Object",
                        DebugResourceKind.Handle, cleanup);
                }
                completion.AddEvidence(new("companion-job-release", "companion Job Object", DebugResourceKind.Handle,
                    job.HandleReleaseVerified, "The acquired Job owner reports native handle release."));
            }
            else if (primary is not IDebugFailureEvidence partialJob
                || !partialJob.FailureOutcome.Evidence.Any(item => item.Kind == DebugResourceKind.Handle))
            {
                completion.AddEvidence(new("companion-job-acquisition", "companion Job Object", DebugResourceKind.Handle,
                    false, "The failed Job factory did not report its partially acquired handle resources."));
            }
            if (launchAttempted && (primary is not IDebugFailureEvidence launchCarrier
                || !launchCarrier.FailureOutcome.Evidence.Any(item => item.Kind == DebugResourceKind.Handle)))
            {
                completion.AddEvidence(new("companion-launch-cleanup", executablePath, DebugResourceKind.Handle,
                    false, "The launcher did not report release of its own partially acquired handles."));
            }
            if (primary is not IDebugFailureEvidence carrier
                || !carrier.FailureOutcome.Evidence.Any(item => item.Kind == DebugResourceKind.Process))
            {
                completion.AddEvidence(new("companion-launch-cleanup", executablePath, DebugResourceKind.Process,
                    !launchAttempted, launchAttempted
                        ? "The launcher did not report whether a process was acquired and released."
                        : "Process launch was not attempted."));
            }
            startupOutcome = completion.Complete();
            startupOutcome.Throw();
            throw;
        }
    }
}

internal sealed class WindowsJobProcessHandle(
    DebugSuspendedProcessLaunch launch,
    IDebugProcessJob job) : IProcessHandle, IDebugResourceOwnerEvidence
{
    private readonly object gate = new();
    private bool disposed;
    private ExceptionDispatchInfo? disposalFailure;
    private readonly List<Task> exitWaits = [];
    private readonly List<Exception> terminationFailures = [];
    private Task<string>? outputRead;
    private Task<string>? errorRead;

    public DebugFailureOutcome? CleanupOutcome { get; private set; }
    public int ExitCode => launch.Process.ExitCode;
    public Task<string> ReadStandardOutputToEndAsync()
    {
        lock (gate) { return outputRead ??= StartRead(launch.StandardOutput!); }
    }
    public Task<string> ReadStandardErrorToEndAsync()
    {
        lock (gate) { return errorRead ??= StartRead(launch.StandardError!); }
    }
    public void Resume() => launch.PrimaryThread.ResumeExactlyOnce();
    public Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Task wait;
            try { wait = launch.Process.WaitForExitAsync(cancellationToken); }
            catch (Exception failure) { wait = Task.FromException(failure); }
            exitWaits.Add(wait);
            return wait;
        }
    }
    public void KillEntireProcessTree()
    {
        try { job.Terminate(); }
        catch (Exception failure)
        {
            lock (gate) { terminationFailures.Add(failure); }
            throw;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                disposalFailure?.Throw();
                return;
            }
            disposed = true;
            var completion = new DebugFailureCompletion();
            if (launch.LaunchCleanupOutcome is { } launchOutcome) { completion.Merge(launchOutcome); }
            var exited = exitWaits.Any(wait => wait.IsCompletedSuccessfully);
            completion.AddEvidence(new("companion-exit", "companion process", DebugResourceKind.Process,
                exited, "Terminal completion requires a successful exit wait owned by this invocation.", launch.Process.Id));
            foreach (var failure in terminationFailures)
            {
                completion.AddFailure("companion-termination", "companion process", DebugResourceKind.Process,
                    failure, launch.Process.Id);
            }
            if (terminationFailures.Count != 0)
            {
                completion.AddEvidence(new("companion-termination", "companion process", DebugResourceKind.Process,
                    exited, "An exit wait independently reports whether the termination request reached completion.", launch.Process.Id));
            }
            // Handle release must never re-enter a terminal wait after the common cleanup deadline.
            Release(job, "companion-job-release", "companion Job Object", completion);
            RecordHandle(completion, "companion-job-release", "companion Job Object", job.HandleReleaseVerified);
            Release(launch.PrimaryThread, "companion-thread-release", "companion primary thread", completion);
            RecordHandle(completion, "companion-thread-release", "companion primary thread", launch.PrimaryThread.HandleReleaseVerified);
            ReleaseReader(launch.StandardOutput, outputRead, "companion-output-release", "companion stdout", completion);
            ReleaseReader(launch.StandardError, errorRead, "companion-error-release", "companion stderr", completion);
            Release(launch.Process, "companion-process-release", "companion process", completion);
            RecordHandle(completion, "companion-process-release", "companion process", launch.Process.HandleReleaseVerified);
            CleanupOutcome = completion.Complete();
            try { CleanupOutcome.Throw(); }
            catch (Exception failure)
            {
                disposalFailure = ExceptionDispatchInfo.Capture(failure);
                throw;
            }
        }
    }

    private static Task<string> StartRead(StreamReader reader)
    {
        try { return reader.ReadToEndAsync(); }
        catch (Exception failure) { return Task.FromException<string>(failure); }
    }

    private void ReleaseReader(StreamReader? reader, Task<string>? read, string stage, string name,
        DebugFailureCompletion completion)
    {
        var settled = read is null || read.IsCompleted;
        completion.AddEvidence(new(stage + "-read", name, DebugResourceKind.Observation,
            settled, "The invocation retains whether its output read has settled before handle release.", launch.Process.Id));
        if (read?.Exception is { } readFailure)
        {
            completion.AddFailure(stage + "-read", name, DebugResourceKind.Observation, readFailure, launch.Process.Id);
        }
        var verified = reader is null;
        if (reader?.BaseStream is FileStream file && settled)
        {
            var release = new DebugNativeHandleRelease(file.SafeFileHandle);
            try { release.Release(); }
            catch (Exception failure)
            {
                completion.AddFailure(stage, name, DebugResourceKind.Handle, failure, launch.Process.Id);
            }
            verified = release.IsVerified;
        }
        Release(reader, stage, name, completion);
        if (reader is IDebugResourceOwnerEvidence { CleanupOutcome: { } readerOutcome })
        {
            completion.Merge(readerOutcome);
            verified = settled && !readerOutcome.HasUnprovedRelease
                && readerOutcome.Evidence.Any(item => item.Kind == DebugResourceKind.Handle && item.Released);
        }
        RecordHandle(completion, stage, name, verified);
    }

    private void RecordHandle(DebugFailureCompletion completion, string stage, string name, bool verified)
        => completion.AddEvidence(new(stage, name, DebugResourceKind.Handle, verified,
            "The exact resource owner must report native release; managed disposal alone is not proof.", launch.Process.Id));

    private void Release(IDisposable? resource, string stage, string name, DebugFailureCompletion completion)
    {
        try { resource?.Dispose(); }
        catch (Exception failure)
        {
            completion.AddFailure(stage, name, DebugResourceKind.Handle, failure, launch.Process.Id);
        }
    }
}
