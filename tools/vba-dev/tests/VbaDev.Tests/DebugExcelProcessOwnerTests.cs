using VbaDev.Infrastructure.Debugging;
using Xunit;

namespace VbaDev.Tests;

public sealed class DebugExcelProcessOwnerTests
{
    [Fact]
    public void DisposedJobRejectsAtomicLaunchBeforeProcessCreation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var job = WindowsDebugProcessJob.Create();
        job.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            job.StartSuspended("not-started.exe", []));
    }

    [Fact]
    public async Task AtomicJobLaunchKeepsThePrimaryThreadSuspendedUntilResume()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var job = WindowsDebugProcessJob.Create();
        DebugSuspendedProcessLaunch? launch = null;
        try
        {
            launch = job.StartSuspended(
                Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                ["/d", "/c", "exit", "0"]);

            await Task.Delay(TimeSpan.FromMilliseconds(50));
            Assert.False(launch.Process.HasExited);
            Assert.Equal(1u, job.ActiveProcessCount);

            launch.PrimaryThread.ResumeExactlyOnce();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await launch.Process.WaitForExitAsync(timeout.Token);
            Assert.True(launch.Process.HasExited);
            Assert.Equal(0u, job.ActiveProcessCount);
        }
        finally
        {
            launch?.PrimaryThread.Dispose();
            job.Dispose();
            launch?.Process.Dispose();
        }
    }

    [Fact]
    public async Task AtomicJobCountsAndDrainsTheEntireDescendantProcessTree()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var job = WindowsDebugProcessJob.Create();
        DebugSuspendedProcessLaunch? launch = null;
        try
        {
            launch = job.StartSuspended(
                Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                ["/d", "/c", "ping -n 30 127.0.0.1 > nul"]);
            launch.PrimaryThread.ResumeExactlyOnce();

            await WaitForJobProcessCountAsync(job, minimum: 2, TimeSpan.FromSeconds(5));
            Assert.True(job.ActiveProcessCount >= 2);

            job.Terminate();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await launch.Process.WaitForExitAsync(timeout.Token);
            await WaitForJobProcessCountAsync(job, minimum: 0, TimeSpan.FromSeconds(5));
            Assert.Equal(0u, job.ActiveProcessCount);
        }
        finally
        {
            launch?.PrimaryThread.Dispose();
            job.Dispose();
            launch?.Process.Dispose();
        }
    }

    [Fact]
    public void CaptureRejectsAWindowBelongingToAnExistingExcelProcess()
    {
        var started = new DateTime(2026, 7, 21, 8, 0, 0, DateTimeKind.Local);
        var processApi = new FakeDebugExcelProcessApi(
            windowProcessId: 42,
            new FakeDebugOwnedProcess(42, started));

        var error = Assert.Throws<ExistingExcelProcessOwnershipRejectedException>(() =>
            DebugExcelProcessOwner.Capture(
                (nint)1234,
                new Dictionary<int, DateTime> { [42] = started },
                processApi));

        Assert.Contains("existing Excel process", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, processApi.OpenProcessCalls);
    }

    [Fact]
    public async Task CaptureMonitorsTheExactNewExcelProcessUntilItExits()
    {
        var started = new DateTime(2026, 7, 21, 8, 30, 0, DateTimeKind.Local);
        var process = new FakeDebugOwnedProcess(84, started);
        var processApi = new FakeDebugExcelProcessApi(windowProcessId: 84, process);

        await using var owner = DebugExcelProcessOwner.Capture(
            (nint)5678,
            new Dictionary<int, DateTime>(),
            processApi);

        Assert.Equal(84, owner.ProcessId);
        Assert.False(owner.Completion.IsCompleted);

        process.Exit(17);

        Assert.Equal(17, (await owner.Completion).ExitCode);
        Assert.Equal(0, process.KillCalls);
    }

    [Fact]
    public async Task CaptureAssignsTheExactProcessToAKillOnCloseJobBeforeReturning()
    {
        var process = new FakeDebugOwnedProcess(
            105,
            new DateTime(2026, 7, 21, 8, 45, 0, DateTimeKind.Local));
        var job = new FakeDebugProcessJob(process);
        var processApi = new FakeDebugExcelProcessApi(
            windowProcessId: process.Id,
            process,
            job);

        await using var owner = DebugExcelProcessOwner.Capture(
            (nint)6789,
            new Dictionary<int, DateTime>(),
            processApi);

        Assert.Equal(1, processApi.CreateJobCalls);
        Assert.Same(process, job.AssignedProcess);
        Assert.False(job.Disposed);
    }

    [Fact]
    public async Task OwnStartedProcessAssignsTheExactLaunchedProcessWithoutWindowDiscovery()
    {
        var process = new FakeDebugOwnedProcess(
            107,
            new DateTime(2026, 8, 22, 8, 45, 0, DateTimeKind.Local));
        var job = new FakeDebugProcessJob(process);
        var processApi = new FakeDebugExcelProcessApi(
            windowProcessId: 999,
            process,
            job);

        await using var owner = DebugExcelProcessOwner.OwnStartedProcess(
            process,
            processApi);

        Assert.Equal(107, owner.ProcessId);
        Assert.Equal(0, processApi.OpenProcessCalls);
        Assert.Equal(1, processApi.CreateJobCalls);
        Assert.Same(process, job.AssignedProcess);
    }

    [Fact]
    public void AssignmentFailureDisposesTheJobBeforeKillingAndDisposingTheExactProcess()
    {
        var events = new List<string>();
        var process = new FakeDebugOwnedProcess(
            106,
            new DateTime(2026, 7, 21, 8, 50, 0, DateTimeKind.Local),
            events: events);
        var assignmentError = new DebugSetupException("Synthetic Job Object assignment failure.");
        var job = new FakeDebugProcessJob(
            process,
            events,
            assignmentError: assignmentError);
        var processApi = new FakeDebugExcelProcessApi(
            windowProcessId: process.Id,
            process,
            job);

        var error = Assert.Throws<DebugSetupException>(() =>
            DebugExcelProcessOwner.Capture(
                (nint)6790,
                new Dictionary<int, DateTime>(),
                processApi));

        Assert.Same(assignmentError, error);
        Assert.Equal(
            ["job-assign", "job-dispose", "process-kill", "process-exit", "process-dispose"],
            events);
        Assert.True(job.Disposed);
        Assert.Equal(1, process.KillCalls);
        Assert.True(process.Disposed);
    }

    [Fact]
    public void AssignmentFailureReportsUnverifiedCleanupWhenExactProcessKillFails()
    {
        var process = new FakeDebugOwnedProcess(
            108,
            new DateTime(2026, 8, 22, 8, 55, 0, DateTimeKind.Local),
            killAction: static () => throw new InvalidOperationException("kill failed"));
        var assignmentError = new DebugSetupException("assignment failed");
        var processApi = new FakeDebugExcelProcessApi(
            windowProcessId: process.Id,
            process,
            new FakeDebugProcessJob(process, assignmentError: assignmentError));

        var error = Assert.Throws<DebugProcessOwnershipCleanupException>(() =>
            DebugExcelProcessOwner.OwnStartedProcess(process, processApi));

        Assert.Same(assignmentError, error.OwnershipException);
        Assert.Contains("kill failed", error.CleanupException.ToString());
        Assert.False(process.HasExited);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task TerminateKillsOnlyTheCapturedProcessAndIsIdempotent()
    {
        var process = new FakeDebugOwnedProcess(
            126,
            new DateTime(2026, 7, 21, 9, 0, 0, DateTimeKind.Local));
        var processApi = new FakeDebugExcelProcessApi(windowProcessId: 126, process);
        await using var owner = DebugExcelProcessOwner.Capture(
            (nint)9012,
            new Dictionary<int, DateTime>(),
            processApi);

        await owner.TerminateAsync();
        await owner.TerminateAsync();

        Assert.Equal(1, process.KillCalls);
        Assert.Equal(-1, (await owner.Completion).ExitCode);
    }

    [Fact]
    public async Task TerminateJobFailureCleansTheRootButReportsTreeCleanupAsUnverified()
    {
        var events = new List<string>();
        var process = new FakeDebugOwnedProcess(
            127,
            new DateTime(2026, 7, 21, 9, 5, 0, DateTimeKind.Local),
            events: events);
        var unrelatedProcess = new FakeDebugOwnedProcess(
            128,
            new DateTime(2026, 7, 21, 9, 5, 1, DateTimeKind.Local));
        var job = new FakeDebugProcessJob(
            process,
            events,
            terminateError: new InvalidOperationException("Synthetic TerminateJobObject failure."));
        var owner = DebugExcelProcessOwner.Capture(
            (nint)9013,
            new Dictionary<int, DateTime>(),
            new FakeDebugExcelProcessApi(process.Id, process, job));

        var error = await Assert.ThrowsAsync<DebugSetupException>(() =>
            owner.DisposeAsync().AsTask());

        Assert.Contains("tree cleanup", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TerminateJobObject failure", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(
            [
                "job-assign",
                "job-terminate",
                "process-kill",
                "process-exit",
                "process-dispose",
                "job-dispose"
            ],
            events);
        Assert.Equal(1, job.TerminateCalls);
        Assert.Equal(1, process.KillCalls);
        Assert.Equal(0, unrelatedProcess.KillCalls);
        Assert.True(process.Disposed);
        Assert.True(job.Disposed);
    }

    [Fact]
    public async Task FailedJobAndRootTerminationStillDisposeOwnershipWithoutWaitingForExitForever()
    {
        var process = new FakeDebugOwnedProcess(
            129,
            new DateTime(2026, 7, 21, 9, 6, 0, DateTimeKind.Local),
            killAction: static () => throw new IOException("root kill failed"),
            exitOnKill: false);
        var job = new FakeDebugProcessJob(
            process,
            terminateError: new IOException("job termination failed"));
        var owner = DebugExcelProcessOwner.Capture(
            (nint)9014,
            new Dictionary<int, DateTime>(),
            new FakeDebugExcelProcessApi(process.Id, process, job));

        var error = await Assert.ThrowsAsync<IOException>(() =>
            owner.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.Equal("root kill failed", error.Message);
        Assert.True(process.Disposed);
        Assert.True(job.Disposed);
        Assert.False(process.HasExited);
    }

    [Fact]
    public async Task TerminateRemainsIdempotentAfterTheOwnerIsDisposed()
    {
        var process = new FakeDebugOwnedProcess(
            252,
            new DateTime(2026, 7, 21, 9, 30, 0, DateTimeKind.Local));
        var owner = DebugExcelProcessOwner.Capture(
            (nint)3456,
            new Dictionary<int, DateTime>(),
            new FakeDebugExcelProcessApi(windowProcessId: 252, process));

        await owner.DisposeAsync();
        await owner.TerminateAsync();

        Assert.Equal(1, process.KillCalls);
    }

    [Fact]
    public async Task TerminateAfterRootProcessLossPreservesTheExistingNoOpBehavior()
    {
        var process = new FakeDebugOwnedProcess(
            253,
            new DateTime(2026, 9, 3, 6, 45, 0, DateTimeKind.Local));
        var job = new FakeDebugProcessJob(process, activeProcessCount: 2);
        var owner = DebugExcelProcessOwner.Capture(
            (nint)3457,
            new Dictionary<int, DateTime>(),
            new FakeDebugExcelProcessApi(process.Id, process, job));
        process.Exit(9);
        _ = await owner.Completion;

        await owner.TerminateAsync();

        Assert.Equal(0, job.TerminateCalls);
        Assert.Equal(2u, job.ActiveProcessCount);
        await owner.DisposeAsync();
    }

    [Fact]
    public async Task ProcessTreeCleanupTerminatesRemainingJobMembersAfterRootProcessLoss()
    {
        var process = new FakeDebugOwnedProcess(
            254,
            new DateTime(2026, 9, 3, 6, 50, 0, DateTimeKind.Local));
        var job = new FakeDebugProcessJob(process, activeProcessCount: 2);
        var owner = DebugExcelProcessOwner.Capture(
            (nint)3458,
            new Dictionary<int, DateTime>(),
            new FakeDebugExcelProcessApi(process.Id, process, job));
        process.Exit(9);
        _ = await owner.Completion;

        await owner.TerminateProcessTreeAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, job.TerminateCalls);
        Assert.Equal(0u, job.ActiveProcessCount);
        await owner.DisposeAsync();
    }

    [Fact]
    public async Task ProcessTreeCleanupFailsWhenJobMembersDoNotDrainBeforeTheDeadline()
    {
        var process = new FakeDebugOwnedProcess(
            255,
            new DateTime(2026, 9, 3, 6, 55, 0, DateTimeKind.Local));
        var job = new FakeDebugProcessJob(
            process,
            activeProcessCount: 2,
            clearActiveProcessCountOnTerminate: false);
        var owner = DebugExcelProcessOwner.Capture(
            (nint)3459,
            new Dictionary<int, DateTime>(),
            new FakeDebugExcelProcessApi(process.Id, process, job));
        process.Exit(9);
        _ = await owner.Completion;

        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            owner.TerminateProcessTreeAsync(
                    TimeSpan.FromMilliseconds(50))
                .AsTask());

        Assert.Contains("process tree", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, job.TerminateCalls);
        Assert.Equal(2u, job.ActiveProcessCount);
        await owner.DisposeAsync();
    }

    [Fact]
    public async Task ProcessTreeCleanupSerializesConcurrentDrainsAndDisposal()
    {
        var process = new FakeDebugOwnedProcess(
            256,
            new DateTime(2026, 9, 3, 7, 0, 0, DateTimeKind.Local));
        using var releaseTermination = new ManualResetEventSlim(false);
        var terminationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var job = new FakeDebugProcessJob(
            process,
            activeProcessCount: 2,
            terminateGate: releaseTermination,
            terminateStarted: () => terminationEntered.TrySetResult());
        var owner = DebugExcelProcessOwner.Capture(
            (nint)3460,
            new Dictionary<int, DateTime>(),
            new FakeDebugExcelProcessApi(process.Id, process, job));

        var firstDrain = Task.Run(() =>
            owner.TerminateProcessTreeAsync(TimeSpan.FromSeconds(2)).AsTask());
        await terminationEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var secondDrain = owner
            .TerminateProcessTreeAsync(TimeSpan.FromSeconds(2))
            .AsTask();
        var disposal = owner.DisposeAsync().AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        releaseTermination.Set();
        await Task.WhenAll(firstDrain, secondDrain, disposal).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, job.TerminateCalls);
        Assert.Equal(1, job.MaximumConcurrentOperations);
        Assert.Equal(0, job.AccessAfterDisposeCalls);
    }

    private static async Task WaitForJobProcessCountAsync(
        WindowsDebugProcessJob job,
        uint minimum,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var count = job.ActiveProcessCount;
            if ((minimum == 0 && count == 0) || (minimum > 0 && count >= minimum))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }
    }
}

internal sealed class FakeDebugExcelProcessApi : IDebugExcelProcessApi
{
    private readonly int windowProcessId;
    private readonly IDebugOwnedProcess process;
    private readonly IDebugProcessJob job;

    public FakeDebugExcelProcessApi(
        int windowProcessId,
        IDebugOwnedProcess process,
        IDebugProcessJob? job = null)
    {
        this.windowProcessId = windowProcessId;
        this.process = process;
        this.job = job ?? new FakeDebugProcessJob(process);
    }

    public IReadOnlyDictionary<int, DateTime> RunningExcelProcesses { get; init; } =
        new Dictionary<int, DateTime>();

    public int OpenProcessCalls { get; private set; }

    public int CreateJobCalls { get; private set; }

    public IReadOnlyDictionary<int, DateTime> CaptureRunningExcelProcesses()
        => RunningExcelProcesses;

    public int GetProcessId(nint windowHandle) => windowProcessId;

    public IDebugOwnedProcess OpenProcess(int processId)
    {
        OpenProcessCalls++;
        Assert.Equal(process.Id, processId);
        return process;
    }

    public IDebugProcessJob CreateKillOnCloseJob()
    {
        CreateJobCalls++;
        return job;
    }
}

internal sealed class FakeDebugProcessJob : IDebugProcessJob
{
    private readonly IDebugOwnedProcess process;
    private readonly List<string>? events;
    private readonly Exception? assignmentError;
    private readonly Exception? terminateError;
    private readonly Action? disposeAction;
    private readonly ManualResetEventSlim? terminateGate;
    private readonly Action? terminateStarted;
    private uint? activeProcessCount;
    private int activeOperations;
    private int maximumConcurrentOperations;
    private int accessAfterDisposeCalls;

    public FakeDebugProcessJob(
        IDebugOwnedProcess process,
        List<string>? events = null,
        Exception? assignmentError = null,
        Exception? terminateError = null,
        Action? disposeAction = null,
        uint? activeProcessCount = null,
        bool clearActiveProcessCountOnTerminate = true,
        ManualResetEventSlim? terminateGate = null,
        Action? terminateStarted = null)
    {
        this.process = process;
        this.events = events;
        this.assignmentError = assignmentError;
        this.terminateError = terminateError;
        this.disposeAction = disposeAction;
        this.activeProcessCount = activeProcessCount;
        this.terminateGate = terminateGate;
        this.terminateStarted = terminateStarted;
        ClearActiveProcessCountOnTerminate = clearActiveProcessCountOnTerminate;
    }

    public IDebugOwnedProcess? AssignedProcess { get; private set; }

    public int TerminateCalls { get; private set; }

    public bool Disposed { get; private set; }

    public uint ActiveProcessCount
    {
        get
        {
            EnterOperation();
            try
            {
                return activeProcessCount ?? (process.HasExited ? 0u : 1u);
            }
            finally
            {
                ExitOperation();
            }
        }
    }

    public int MaximumConcurrentOperations => Volatile.Read(ref maximumConcurrentOperations);

    public int AccessAfterDisposeCalls => Volatile.Read(ref accessAfterDisposeCalls);

    private bool ClearActiveProcessCountOnTerminate { get; }

    public void Assign(IDebugOwnedProcess ownedProcess)
    {
        events?.Add("job-assign");
        Assert.Same(process, ownedProcess);
        if (assignmentError is not null)
        {
            throw assignmentError;
        }

        AssignedProcess = process;
    }

    public void Terminate()
    {
        EnterOperation();
        try
        {
            events?.Add("job-terminate");
            TerminateCalls++;
            terminateStarted?.Invoke();
            terminateGate?.Wait();
            if (terminateError is not null)
            {
                throw terminateError;
            }

            if (ClearActiveProcessCountOnTerminate)
            {
                activeProcessCount = 0;
            }

            process.Kill();
        }
        finally
        {
            ExitOperation();
        }
    }

    public void Dispose()
    {
        events?.Add("job-dispose");
        Disposed = true;
        disposeAction?.Invoke();
    }

    private void EnterOperation()
    {
        if (Disposed)
        {
            Interlocked.Increment(ref accessAfterDisposeCalls);
        }

        var concurrent = Interlocked.Increment(ref activeOperations);
        int observed;
        do
        {
            observed = Volatile.Read(ref maximumConcurrentOperations);
            if (concurrent <= observed)
            {
                break;
            }
        }
        while (Interlocked.CompareExchange(
            ref maximumConcurrentOperations,
            concurrent,
            observed) != observed);
    }

    private void ExitOperation() => Interlocked.Decrement(ref activeOperations);
}

internal sealed class FakeDebugOwnedProcess(
    int id,
    DateTime startTime,
    DebugExcelProcessArchitecture architecture = DebugExcelProcessArchitecture.X64,
    Action? killAction = null,
    List<string>? events = null,
    bool exitOnKill = true,
    Exception? hasExitedAfterDisposeError = null)
    : IDebugOwnedProcess
{
    private readonly TaskCompletionSource completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool hasExited;

    public int Id { get; } = id;

    public DebugExcelProcessArchitecture Architecture { get; } = architecture;

    public DateTime StartTime { get; } = startTime;

    public bool HasExited
    {
        get
        {
            if (Disposed && hasExitedAfterDisposeError is not null)
            {
                throw hasExitedAfterDisposeError;
            }

            return hasExited;
        }
    }

    public int ExitCode { get; private set; }

    public int KillCalls { get; private set; }

    public bool Disposed { get; private set; }

    public Task WaitForExitAsync(CancellationToken cancellationToken)
        => completion.Task.WaitAsync(cancellationToken);

    public void Kill()
    {
        events?.Add("process-kill");
        KillCalls++;
        killAction?.Invoke();
        if (exitOnKill)
        {
            Exit(-1);
        }
    }

    public void Exit(int exitCode)
    {
        events?.Add("process-exit");
        ExitCode = exitCode;
        hasExited = true;
        completion.TrySetResult();
    }

    public void Dispose()
    {
        events?.Add("process-dispose");
        Disposed = true;
    }
}
