using VbaLanguageServer.Processes;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaDevProcessInvocationTests
{
    [Fact]
    public async Task Invocation_preserves_the_pinned_executable_arguments_and_complete_result()
    {
        var executablePath = Path.GetFullPath(Path.Combine("tools", "vba-dev.exe"));
        var handle = new StubProcessHandle(
            exitCode: 17,
            standardOutput: "output",
            standardError: "error");
        var platform = new RecordingProcessPlatform(handle);
        var invocation = new VbaDevProcessInvocation(executablePath, platform);

        var result = await invocation.RunAsync(
            ["reference", "list", "--format", "json"]);

        Assert.Equal(executablePath, platform.ExecutablePath);
        Assert.Equal(
            ["reference", "list", "--format", "json"],
            platform.Arguments);
        Assert.Equal(17, result.ExitCode);
        Assert.Equal("output", result.StandardOutput);
        Assert.Equal("error", result.StandardError);
    }

    [Fact]
    public async Task Cancellation_before_start_never_starts_a_process()
    {
        var executablePath = Path.GetFullPath(Path.Combine("tools", "vba-dev.exe"));
        var platform = new RecordingProcessPlatform(new StubProcessHandle(0, "", ""));
        var invocation = new VbaDevProcessInvocation(executablePath, platform);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => invocation.RunAsync(["capabilities"], cancellation.Token));

        Assert.Equal(0, platform.StartCount);
    }

    [Fact]
    public async Task Both_stream_drains_start_before_waiting_for_process_exit()
    {
        var executablePath = Path.GetFullPath(Path.Combine("tools", "vba-dev.exe"));
        var handle = new DrainOrderingProcessHandle();
        var invocation = new VbaDevProcessInvocation(
            executablePath,
            new RecordingProcessPlatform(handle));

        await invocation.RunAsync(["capabilities"]);

        Assert.True(handle.BothDrainsStartedBeforeExitWait);
    }

    [Fact]
    public async Task Cancellation_after_start_kills_once_waits_without_the_cancelled_token_and_remains_cancellation()
    {
        var executablePath = Path.GetFullPath(Path.Combine("tools", "vba-dev.exe"));
        var handle = new CancellationProcessHandle();
        var invocation = new VbaDevProcessInvocation(
            executablePath,
            new RecordingProcessPlatform(handle));
        using var cancellation = new CancellationTokenSource();

        var running = invocation.RunAsync(["reference", "list"], cancellation.Token);
        await handle.CancellableWaitStarted.Task;
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => running);

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, handle.KillCount);
        Assert.Equal(2, handle.WaitTokens.Count);
        Assert.Equal(cancellation.Token, handle.WaitTokens[0]);
        Assert.False(handle.WaitTokens[1].CanBeCanceled);
    }

    [Fact]
    public async Task Cancellation_does_not_complete_until_both_streams_are_drained()
    {
        var executablePath = Path.GetFullPath(Path.Combine("tools", "vba-dev.exe"));
        var handle = new DelayedDrainCancellationProcessHandle();
        var invocation = new VbaDevProcessInvocation(
            executablePath,
            new RecordingProcessPlatform(handle));
        using var cancellation = new CancellationTokenSource();

        var running = invocation.RunAsync(["capabilities"], cancellation.Token);
        await handle.CancellableWaitStarted.Task;
        cancellation.Cancel();
        await handle.TerminalWaitCompleted.Task;

        try
        {
            Assert.False(running.IsCompleted);
            handle.CompleteStandardOutput();
            Assert.False(running.IsCompleted);
            handle.CompleteStandardError();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        }
        finally
        {
            handle.CompleteStandardOutput();
            handle.CompleteStandardError();
        }
    }

    [Fact]
    public async Task Cancellation_after_terminal_exit_waits_for_stream_drain_then_remains_cancellation()
    {
        var executablePath = Path.GetFullPath(Path.Combine("tools", "vba-dev.exe"));
        var handle = new ExitedBeforeDrainCancellationProcessHandle();
        var invocation = new VbaDevProcessInvocation(
            executablePath,
            new RecordingProcessPlatform(handle));
        using var cancellation = new CancellationTokenSource();

        var running = invocation.RunAsync(["reference", "list"], cancellation.Token);
        await handle.CancellableWaitStarted.Task;
        handle.CompleteTerminalExit();
        await handle.CancellableWaitCompleted.Task;
        cancellation.Cancel();

        try
        {
            handle.CompleteStandardOutput();
            Assert.False(running.IsCompleted);
            handle.CompleteStandardError();
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => running.WaitAsync(TimeSpan.FromSeconds(1)));

            Assert.Equal(cancellation.Token, exception.CancellationToken);
            Assert.Equal(1, handle.KillCount);
            Assert.Equal(2, handle.WaitTokens.Count);
            Assert.Equal(cancellation.Token, handle.WaitTokens[0]);
            Assert.False(handle.WaitTokens[1].CanBeCanceled);
        }
        finally
        {
            handle.CompleteStandardOutput();
            handle.CompleteStandardError();
        }
    }

    [Fact]
    public async Task Cancellation_after_terminal_exit_bounds_an_incomplete_stream_drain()
    {
        var executablePath = Path.GetFullPath(Path.Combine("tools", "vba-dev.exe"));
        var handle = new ExitedBeforeDrainCancellationProcessHandle();
        var invocation = new VbaDevProcessInvocation(
            executablePath,
            new RecordingProcessPlatform(handle),
            cancellationCleanupTimeout: TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();

        var running = invocation.RunAsync(["reference", "list"], cancellation.Token);
        await handle.CancellableWaitStarted.Task;
        handle.CompleteTerminalExit();
        await handle.CancellableWaitCompleted.Task;
        cancellation.Cancel();

        try
        {
            var exception = await Assert.ThrowsAsync<VbaDevProcessLifecycleException>(
                () => running.WaitAsync(TimeSpan.FromSeconds(1)));

            Assert.Contains("complete stream drain", exception.Message);
            Assert.IsType<TimeoutException>(exception.InnerException);
            Assert.Equal(1, handle.KillCount);
            Assert.Equal(2, handle.WaitTokens.Count);
            Assert.Equal(cancellation.Token, handle.WaitTokens[0]);
            Assert.False(handle.WaitTokens[1].CanBeCanceled);
        }
        finally
        {
            handle.CompleteStandardOutput();
            handle.CompleteStandardError();
        }
    }

    [Fact]
    public async Task Cancellation_cleanup_deadline_bounds_an_uncooperative_terminal_wait()
    {
        var executablePath = Path.GetFullPath(Path.Combine("tools", "vba-dev.exe"));
        var handle = new StalledCancellationCleanupProcessHandle(
            stallTerminalWait: true,
            stallStandardOutput: false,
            stallStandardError: false);
        var invocation = new VbaDevProcessInvocation(
            executablePath,
            new RecordingProcessPlatform(handle),
            cancellationCleanupTimeout: TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();

        var running = invocation.RunAsync(["capabilities"], cancellation.Token);
        await handle.CancellableWaitStarted.Task;
        cancellation.Cancel();

        var exception = await Assert.ThrowsAsync<VbaDevProcessLifecycleException>(
            () => running);

        Assert.Contains("terminal process exit", exception.Message);
        Assert.IsType<TimeoutException>(exception.InnerException);
        Assert.Equal(1, handle.KillCount);
        Assert.Equal(2, handle.WaitTokens.Count);
        Assert.Equal(cancellation.Token, handle.WaitTokens[0]);
        Assert.False(handle.WaitTokens[1].CanBeCanceled);
    }

    [Fact]
    public async Task Cancellation_cleanup_deadline_bounds_an_incomplete_stream_drain()
    {
        var executablePath = Path.GetFullPath(Path.Combine("tools", "vba-dev.exe"));
        var handle = new StalledCancellationCleanupProcessHandle(
            stallTerminalWait: false,
            stallStandardOutput: true,
            stallStandardError: false);
        var invocation = new VbaDevProcessInvocation(
            executablePath,
            new RecordingProcessPlatform(handle),
            cancellationCleanupTimeout: TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();

        var running = invocation.RunAsync(["reference", "list"], cancellation.Token);
        await handle.CancellableWaitStarted.Task;
        cancellation.Cancel();

        var exception = await Assert.ThrowsAsync<VbaDevProcessLifecycleException>(
            () => running);

        Assert.Contains("complete stream drain", exception.Message);
        Assert.IsType<TimeoutException>(exception.InnerException);
        Assert.Equal(1, handle.KillCount);
        Assert.Equal(2, handle.WaitTokens.Count);
        Assert.Equal(cancellation.Token, handle.WaitTokens[0]);
        Assert.False(handle.WaitTokens[1].CanBeCanceled);
    }

    [Fact]
    public async Task Cancellation_cleanup_deadline_still_bounds_wait_after_kill_failure()
    {
        var executablePath = Path.GetFullPath(Path.Combine("tools", "vba-dev.exe"));
        var killFailure = new InvalidOperationException("Process tree kill failed.");
        var handle = new StalledCancellationCleanupProcessHandle(
            stallTerminalWait: true,
            stallStandardOutput: false,
            stallStandardError: false,
            killFailure);
        var invocation = new VbaDevProcessInvocation(
            executablePath,
            new RecordingProcessPlatform(handle),
            cancellationCleanupTimeout: TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();

        var running = invocation.RunAsync(["capabilities"], cancellation.Token);
        await handle.CancellableWaitStarted.Task;
        cancellation.Cancel();

        var exception = await Assert.ThrowsAsync<VbaDevProcessLifecycleException>(
            () => running);

        var cleanupFailures = Assert.IsType<AggregateException>(exception.InnerException);
        Assert.Contains(killFailure, cleanupFailures.InnerExceptions);
        Assert.Contains(cleanupFailures.InnerExceptions, failure => failure is TimeoutException);
        Assert.Equal(1, handle.KillCount);
        Assert.Equal(2, handle.WaitTokens.Count);
        Assert.False(handle.WaitTokens[1].CanBeCanceled);
    }

    [Fact]
    public async Task Already_exited_kill_race_preserves_cancellation_after_terminal_proof()
    {
        var executablePath = Path.GetFullPath(Path.Combine("tools", "vba-dev.exe"));
        var handle = new AlreadyExitedCancellationProcessHandle();
        var invocation = new VbaDevProcessInvocation(
            executablePath,
            new RecordingProcessPlatform(handle));
        using var cancellation = new CancellationTokenSource();

        var running = invocation.RunAsync(["capabilities"], cancellation.Token);
        await handle.CancellableWaitStarted.Task;
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => running);

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, handle.KillCount);
        Assert.Equal(2, handle.WaitTokens.Count);
        Assert.False(handle.WaitTokens[1].CanBeCanceled);
    }

    [Fact]
    public async Task Cancellation_reports_a_lifecycle_failure_when_terminal_exit_cannot_be_proved()
    {
        var executablePath = Path.GetFullPath(Path.Combine("tools", "vba-dev.exe"));
        var handle = new UnprovableTerminationProcessHandle();
        var invocation = new VbaDevProcessInvocation(
            executablePath,
            new RecordingProcessPlatform(handle));
        using var cancellation = new CancellationTokenSource();

        var running = invocation.RunAsync(["reference", "list"], cancellation.Token);
        await handle.CancellableWaitStarted.Task;
        cancellation.Cancel();

        var exception = await Assert.ThrowsAsync<VbaDevProcessLifecycleException>(
            () => running);

        Assert.Contains("terminal process exit", exception.Message);
        Assert.IsType<IOException>(exception.InnerException);
        Assert.Equal(1, handle.KillCount);
        Assert.Equal(2, handle.WaitTokens.Count);
        Assert.False(handle.WaitTokens[1].CanBeCanceled);
    }

    private sealed class RecordingProcessPlatform(IVbaDevProcessHandle platformProcess)
        : IVbaDevProcessPlatform
    {
        public string? ExecutablePath { get; private set; }

        public IReadOnlyList<string>? Arguments { get; private set; }

        public int StartCount { get; private set; }

        public IVbaDevProcessHandle Start(
            string executablePath,
            IReadOnlyList<string> arguments)
        {
            StartCount++;
            ExecutablePath = executablePath;
            Arguments = [.. arguments];
            return platformProcess;
        }
    }

    private sealed class StubProcessHandle(
        int exitCode,
        string standardOutput,
        string standardError) : IVbaDevProcessHandle
    {
        public int ExitCode => exitCode;

        public Task<string> ReadStandardOutputToEndAsync()
            => Task.FromResult(standardOutput);

        public Task<string> ReadStandardErrorToEndAsync()
            => Task.FromResult(standardError);

        public Task WaitForExitAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

        public void KillEntireProcessTree()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class DrainOrderingProcessHandle : IVbaDevProcessHandle
    {
        private bool standardOutputDrainStarted;
        private bool standardErrorDrainStarted;

        public int ExitCode => 0;

        public bool BothDrainsStartedBeforeExitWait { get; private set; }

        public Task<string> ReadStandardOutputToEndAsync()
        {
            standardOutputDrainStarted = true;
            return Task.FromResult("");
        }

        public Task<string> ReadStandardErrorToEndAsync()
        {
            standardErrorDrainStarted = true;
            return Task.FromResult("");
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            BothDrainsStartedBeforeExitWait =
                standardOutputDrainStarted && standardErrorDrainStarted;
            return Task.CompletedTask;
        }

        public void KillEntireProcessTree()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class CancellationProcessHandle : IVbaDevProcessHandle
    {
        public TaskCompletionSource CancellableWaitStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public List<CancellationToken> WaitTokens { get; } = [];

        public int ExitCode => 0;

        public int KillCount { get; private set; }

        public Task<string> ReadStandardOutputToEndAsync()
            => Task.FromResult("");

        public Task<string> ReadStandardErrorToEndAsync()
            => Task.FromResult("");

        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            WaitTokens.Add(cancellationToken);
            if (WaitTokens.Count == 1)
            {
                CancellableWaitStarted.SetResult();
                return WaitForCancellationAsync(cancellationToken);
            }

            return Task.CompletedTask;
        }

        public void KillEntireProcessTree()
            => KillCount++;

        public void Dispose()
        {
        }

        private static async Task WaitForCancellationAsync(
            CancellationToken cancellationToken)
            => await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private sealed class DelayedDrainCancellationProcessHandle : IVbaDevProcessHandle
    {
        private readonly TaskCompletionSource<string> standardOutput = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> standardError = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int waitCount;

        public TaskCompletionSource CancellableWaitStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource TerminalWaitCompleted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int ExitCode => 0;

        public Task<string> ReadStandardOutputToEndAsync()
            => standardOutput.Task;

        public Task<string> ReadStandardErrorToEndAsync()
            => standardError.Task;

        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            waitCount++;
            if (waitCount == 1)
            {
                CancellableWaitStarted.SetResult();
                return WaitForCancellationAsync(cancellationToken);
            }

            TerminalWaitCompleted.SetResult();
            return Task.CompletedTask;
        }

        public void CompleteStandardOutput()
            => standardOutput.TrySetResult("output");

        public void CompleteStandardError()
            => standardError.TrySetResult("error");

        public void KillEntireProcessTree()
        {
        }

        public void Dispose()
        {
        }

        private static async Task WaitForCancellationAsync(
            CancellationToken cancellationToken)
            => await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private sealed class AlreadyExitedCancellationProcessHandle : IVbaDevProcessHandle
    {
        public TaskCompletionSource CancellableWaitStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public List<CancellationToken> WaitTokens { get; } = [];

        public int ExitCode => 0;

        public int KillCount { get; private set; }

        public Task<string> ReadStandardOutputToEndAsync()
            => Task.FromResult("");

        public Task<string> ReadStandardErrorToEndAsync()
            => Task.FromResult("");

        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            WaitTokens.Add(cancellationToken);
            if (WaitTokens.Count == 1)
            {
                CancellableWaitStarted.SetResult();
                return WaitForCancellationAsync(cancellationToken);
            }

            return Task.CompletedTask;
        }

        public void KillEntireProcessTree()
        {
            KillCount++;
            throw new InvalidOperationException("The process has already exited.");
        }

        public void Dispose()
        {
        }

        private static async Task WaitForCancellationAsync(
            CancellationToken cancellationToken)
            => await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private sealed class ExitedBeforeDrainCancellationProcessHandle
        : IVbaDevProcessHandle
    {
        private readonly TaskCompletionSource terminalExit = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> standardOutput = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> standardError = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellableWaitStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellableWaitCompleted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public List<CancellationToken> WaitTokens { get; } = [];

        public int ExitCode => 0;

        public int KillCount { get; private set; }

        public Task<string> ReadStandardOutputToEndAsync()
            => standardOutput.Task;

        public Task<string> ReadStandardErrorToEndAsync()
            => standardError.Task;

        public async Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            WaitTokens.Add(cancellationToken);
            if (WaitTokens.Count == 1)
            {
                CancellableWaitStarted.SetResult();
                await terminalExit.Task.WaitAsync(cancellationToken);
                CancellableWaitCompleted.SetResult();
            }
        }

        public void CompleteTerminalExit()
            => terminalExit.TrySetResult();

        public void CompleteStandardOutput()
            => standardOutput.TrySetResult("output");

        public void CompleteStandardError()
            => standardError.TrySetResult("error");

        public void KillEntireProcessTree()
            => KillCount++;

        public void Dispose()
        {
        }
    }

    private sealed class StalledCancellationCleanupProcessHandle(
        bool stallTerminalWait,
        bool stallStandardOutput,
        bool stallStandardError,
        Exception? killFailure = null) : IVbaDevProcessHandle
    {
        private readonly TaskCompletionSource<string> standardOutput = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> standardError = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellableWaitStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public List<CancellationToken> WaitTokens { get; } = [];

        public int ExitCode => 0;

        public int KillCount { get; private set; }

        public Task<string> ReadStandardOutputToEndAsync()
            => stallStandardOutput
                ? standardOutput.Task
                : Task.FromResult("");

        public Task<string> ReadStandardErrorToEndAsync()
            => stallStandardError
                ? standardError.Task
                : Task.FromResult("");

        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            WaitTokens.Add(cancellationToken);
            if (WaitTokens.Count == 1)
            {
                CancellableWaitStarted.SetResult();
                return WaitForCancellationAsync(cancellationToken);
            }

            return stallTerminalWait
                ? Task.Delay(Timeout.InfiniteTimeSpan)
                : Task.CompletedTask;
        }

        public void KillEntireProcessTree()
        {
            KillCount++;
            if (killFailure is not null)
            {
                throw killFailure;
            }
        }

        public void Dispose()
        {
        }

        private static async Task WaitForCancellationAsync(
            CancellationToken cancellationToken)
            => await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private sealed class UnprovableTerminationProcessHandle : IVbaDevProcessHandle
    {
        public TaskCompletionSource CancellableWaitStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public List<CancellationToken> WaitTokens { get; } = [];

        public int ExitCode => 0;

        public int KillCount { get; private set; }

        public Task<string> ReadStandardOutputToEndAsync()
            => Task.FromResult("");

        public Task<string> ReadStandardErrorToEndAsync()
            => Task.FromResult("");

        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            WaitTokens.Add(cancellationToken);
            if (WaitTokens.Count == 1)
            {
                CancellableWaitStarted.SetResult();
                return WaitForCancellationAsync(cancellationToken);
            }

            throw new IOException("Terminal exit was not observable.");
        }

        public void KillEntireProcessTree()
            => KillCount++;

        public void Dispose()
        {
        }

        private static async Task WaitForCancellationAsync(
            CancellationToken cancellationToken)
            => await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
