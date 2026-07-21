using VbaDev.App.Debugging;
using VbaDev.Infrastructure.Debugging;
using Xunit;

namespace VbaDev.Tests;

public sealed class DebugExcelProcessOwnerTests
{
    [Fact]
    public void CaptureRejectsAWindowBelongingToAnExistingExcelProcess()
    {
        var started = new DateTime(2026, 7, 21, 8, 0, 0, DateTimeKind.Local);
        var processApi = new FakeDebugExcelProcessApi(
            windowProcessId: 42,
            new FakeDebugOwnedProcess(42, started));

        var error = Assert.Throws<DebugSetupException>(() =>
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
}

internal sealed class FakeDebugExcelProcessApi(
    int windowProcessId,
    IDebugOwnedProcess process) : IDebugExcelProcessApi
{
    public IReadOnlyDictionary<int, DateTime> RunningExcelProcesses { get; init; } =
        new Dictionary<int, DateTime>();

    public int OpenProcessCalls { get; private set; }

    public IReadOnlyDictionary<int, DateTime> CaptureRunningExcelProcesses()
        => RunningExcelProcesses;

    public int GetProcessId(nint windowHandle) => windowProcessId;

    public IDebugOwnedProcess OpenProcess(int processId)
    {
        OpenProcessCalls++;
        Assert.Equal(process.Id, processId);
        return process;
    }
}

internal sealed class FakeDebugOwnedProcess(int id, DateTime startTime) : IDebugOwnedProcess
{
    private readonly TaskCompletionSource completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Id { get; } = id;

    public DateTime StartTime { get; } = startTime;

    public bool HasExited { get; private set; }

    public int ExitCode { get; private set; }

    public int KillCalls { get; private set; }

    public Task WaitForExitAsync(CancellationToken cancellationToken)
        => completion.Task.WaitAsync(cancellationToken);

    public void Kill()
    {
        KillCalls++;
        Exit(-1);
    }

    public void Exit(int exitCode)
    {
        ExitCode = exitCode;
        HasExited = true;
        completion.TrySetResult();
    }

    public void Dispose()
    {
    }
}
