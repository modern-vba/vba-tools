using VbaDebugAdapter.Infrastructure;
using VbaDebugAdapter.Debugging;
using Xunit;

namespace VbaDebugAdapter.Tests;

internal static class DebugSnapshotTestEncoding
{
    public static byte[] Utf8BomBytes(string text)
        => [0xef, 0xbb, 0xbf, .. System.Text.Encoding.UTF8.GetBytes(text)];
}

internal sealed class TempDirectory : IDisposable
{
    private TempDirectory(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public static TempDirectory Create()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "vba-debug-adapter-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new TempDirectory(path);
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
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

    public FakeDebugProcessJob(
        IDebugOwnedProcess process,
        List<string>? events = null,
        Exception? assignmentError = null,
        Exception? terminateError = null,
        Action? disposeAction = null)
    {
        this.process = process;
        this.events = events;
        this.assignmentError = assignmentError;
        this.terminateError = terminateError;
        this.disposeAction = disposeAction;
    }

    public IDebugOwnedProcess? AssignedProcess { get; private set; }

    public int TerminateCalls { get; private set; }

    public bool Disposed { get; private set; }

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
        events?.Add("job-terminate");
        TerminateCalls++;
        if (terminateError is not null)
        {
            throw terminateError;
        }

        process.Kill();
    }

    public void Dispose()
    {
        events?.Add("job-dispose");
        Disposed = true;
        disposeAction?.Invoke();
    }
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
