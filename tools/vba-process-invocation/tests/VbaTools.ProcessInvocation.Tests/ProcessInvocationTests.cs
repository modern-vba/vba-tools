using VbaTools.Processes;
using Xunit;

namespace VbaTools.Processes.Tests;

public sealed class ProcessInvocationTests
{
    [Fact]
    public async Task Handle_release_failure_does_not_replace_the_original_reader_failure()
    {
        var primary = new IOException("original reader failure");
        var release = new IOException("handle release failure");
        var handle = new LateFailureHandle(primary, Task.FromResult(""), releaseFailure: release);
        var failure = await Assert.ThrowsAsync<ProcessLifecycleException>(() =>
            new ProcessInvocation(Path.GetFullPath("tool.exe"), new RecordingProcessPlatform(handle)).RunAsync([]));
        Assert.Same(primary, failure.PrimaryFailure);
        Assert.Same(release, failure.CleanupFailure);
    }

    [Fact]
    public async Task Lifecycle_failure_retains_each_terminal_wait_and_reader_failure()
    {
        var primary = new IOException("primary output failure");
        var peer = new IOException("secondary error failure");
        var exit = new IOException("terminal wait failure");
        var handle = new LateFailureHandle(primary, Task.FromException<string>(peer), exit);
        var failure = await Assert.ThrowsAsync<ProcessLifecycleException>(() =>
            new ProcessInvocation(Path.GetFullPath("tool.exe"), new RecordingProcessPlatform(handle))
                .RunAsync([]));
        Assert.Same(primary, failure.PrimaryFailure);
        Assert.Contains(peer.Message, failure.ToString());
        Assert.Contains(exit.Message, failure.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_reader_failure_with_a_stalled_peer_has_bounded_cleanup_and_releases_the_handle(bool exitAlsoFails)
    {
        var primary = new IOException("stdout failed");
        var peer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = new LateFailureHandle(primary, peer.Task,
            exitAlsoFails ? new IOException("exit proof failed before timeout") : null);
        var invocation = new ProcessInvocation(Path.GetFullPath("tool.exe"),
            new RecordingProcessPlatform(handle), cancellationCleanupTimeout: TimeSpan.Zero);

        var failure = await Assert.ThrowsAsync<ProcessLifecycleException>(
            () => invocation.RunAsync([]).WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.Same(primary, failure.PrimaryFailure);
        Assert.Contains("TimeoutException", failure.ToString());
        if (exitAlsoFails) { Assert.Contains("exit proof failed before timeout", failure.ToString()); }
        Assert.Equal(1, handle.KillCount);
        Assert.True(handle.Disposed);
        var waitsAtRelease = handle.WaitCount;
        peer.SetException(new IOException("stderr failed after handle release"));
        await Assert.ThrowsAsync<IOException>(() => peer.Task);
        Assert.Equal(waitsAtRelease, handle.WaitCount);
    }

    [Fact]
    public async Task The_system_adapter_captures_both_streams_and_a_nonzero_exit_code()
    {
        var result = await new ProcessInvocation(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            .RunAsync(["/d", "/c", "echo stdout & echo stderr 1>&2 & exit /b 17"]);
        Assert.Equal(17, result.ExitCode);
        Assert.Contains("stdout", result.StandardOutput);
        Assert.Contains("stderr", result.StandardError);
    }

    private sealed class LateFailureHandle(Exception failure, Task<string> peer,
        Exception? exitFailure = null, Exception? releaseFailure = null) : IProcessHandle
    {
        public int KillCount { get; private set; }
        public int WaitCount { get; private set; }
        public bool Disposed { get; private set; }
        public int ExitCode => 0;
        public Task<string> ReadStandardOutputToEndAsync() => Task.FromException<string>(failure);
        public Task<string> ReadStandardErrorToEndAsync() => peer;
        public Task WaitForExitAsync(CancellationToken token)
        {
            WaitCount++;
            return exitFailure is null ? Task.CompletedTask : Task.FromException(exitFailure);
        }
        public void KillEntireProcessTree() => KillCount++;
        public void Dispose()
        {
            Disposed = true;
            if (releaseFailure is not null) { throw releaseFailure; }
        }
    }

    [Fact]
    public async Task Cancellation_immediately_before_result_publication_remains_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var handle = new ExitCodeCancellationHandle(cancellation);
        var invocation = new ProcessInvocation(Path.GetFullPath("tool.exe"), new RecordingProcessPlatform(handle));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => invocation.RunAsync([], cancellation.Token));
        Assert.Equal(1, handle.KillCount);
    }

    private sealed class ExitCodeCancellationHandle(CancellationTokenSource cancellation) : IProcessHandle
    {
        public int KillCount { get; private set; }
        public int ExitCode { get { cancellation.Cancel(); return 0; } }
        public Task<string> ReadStandardOutputToEndAsync() => Task.FromResult("output");
        public Task<string> ReadStandardErrorToEndAsync() => Task.FromResult("error");
        public Task WaitForExitAsync(CancellationToken token) => Task.CompletedTask;
        public void KillEntireProcessTree() => KillCount++;
        public void Dispose() { }
    }

    [Theory]
    [InlineData("async-output")]
    [InlineData("sync-output")]
    [InlineData("resume")]
    public async Task Reader_failure_terminates_a_running_process_and_preserves_the_failure(string stage)
    {
        var failure = new IOException("The output pipe failed.");
        var handle = new FailingReaderProcessHandle(failure, stage);
        var invocation = new ProcessInvocation(
            Path.GetFullPath("vba-dev.exe"),
            new RecordingProcessPlatform(handle));

        var running = invocation.RunAsync(["capabilities"]);
        try
        {
            var actual = await Assert.ThrowsAsync<IOException>(
                () => running.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Same(failure, actual);
            Assert.Equal(1, handle.KillCount);
            Assert.True(handle.Disposed);
        }
        finally
        {
            handle.Exit.TrySetResult();
        }
    }

    private sealed class FailingReaderProcessHandle(Exception failure, string stage) : IProcessHandle
    {
        public TaskCompletionSource Exit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int KillCount { get; private set; }
        public bool Disposed { get; private set; }
        public int ExitCode => 0;
        public Task<string> ReadStandardOutputToEndAsync() => stage switch
        {
            "sync-output" => throw failure,
            "async-output" => Task.FromException<string>(failure),
            _ => Task.FromResult("")
        };
        public void Resume() { if (stage == "resume") { throw failure; } }
        public Task<string> ReadStandardErrorToEndAsync() => Task.FromResult("");
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Exit.Task.WaitAsync(cancellationToken);
        public void KillEntireProcessTree()
        {
            KillCount++;
            Exit.TrySetResult();
        }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task Invocation_preserves_the_pinned_executable_arguments_and_complete_result()
    {
        var executablePath = Path.GetFullPath(Path.Combine("tools", "vba-dev.exe"));
        var handle = new StubProcessHandle(
            exitCode: 17,
            standardOutput: "output",
            standardError: "error");
        var platform = new RecordingProcessPlatform(handle);
        var invocation = new ProcessInvocation(executablePath, platform);

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
        var invocation = new ProcessInvocation(executablePath, platform);
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
        var invocation = new ProcessInvocation(
            executablePath,
            new RecordingProcessPlatform(handle));

        await invocation.RunAsync(["capabilities"]);

        Assert.True(handle.BothDrainsStartedBeforeExitWait);
        Assert.Equal(1, handle.ResumeCount);
        Assert.True(handle.BothDrainsStartedBeforeResume);
    }

    [Fact]
    public async Task Cancellation_after_start_kills_once_waits_without_the_cancelled_token_and_remains_cancellation()
    {
        var executablePath = Path.GetFullPath(Path.Combine("tools", "vba-dev.exe"));
        var handle = new CancellationProcessHandle();
        var invocation = new ProcessInvocation(
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
        var invocation = new ProcessInvocation(
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
        var invocation = new ProcessInvocation(
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
        var invocation = new ProcessInvocation(
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
            var exception = await Assert.ThrowsAsync<ProcessLifecycleException>(
                () => running.WaitAsync(TimeSpan.FromSeconds(1)));

            Assert.Contains("complete stream drain", exception.Message);
            Assert.IsType<TimeoutException>(exception.CleanupFailure);
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
        var invocation = new ProcessInvocation(
            executablePath,
            new RecordingProcessPlatform(handle),
            cancellationCleanupTimeout: TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();

        var running = invocation.RunAsync(["capabilities"], cancellation.Token);
        await handle.CancellableWaitStarted.Task;
        cancellation.Cancel();

        var exception = await Assert.ThrowsAsync<ProcessLifecycleException>(
            () => running);

        Assert.Contains("terminal process exit", exception.Message);
        Assert.Equal(cancellation.Token,
            Assert.IsAssignableFrom<OperationCanceledException>(exception.PrimaryFailure).CancellationToken);
        Assert.IsType<TimeoutException>(exception.CleanupFailure);
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
        var invocation = new ProcessInvocation(
            executablePath,
            new RecordingProcessPlatform(handle),
            cancellationCleanupTimeout: TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();

        var running = invocation.RunAsync(["reference", "list"], cancellation.Token);
        await handle.CancellableWaitStarted.Task;
        cancellation.Cancel();

        var exception = await Assert.ThrowsAsync<ProcessLifecycleException>(
            () => running);

        Assert.Contains("complete stream drain", exception.Message);
        Assert.IsType<TimeoutException>(exception.CleanupFailure);
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
        var invocation = new ProcessInvocation(
            executablePath,
            new RecordingProcessPlatform(handle),
            cancellationCleanupTimeout: TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();

        var running = invocation.RunAsync(["capabilities"], cancellation.Token);
        await handle.CancellableWaitStarted.Task;
        cancellation.Cancel();

        var exception = await Assert.ThrowsAsync<ProcessLifecycleException>(
            () => running);

        Assert.Same(killFailure, exception.TerminationFailure);
        Assert.IsType<TimeoutException>(exception.CleanupFailure);
        Assert.IsAssignableFrom<OperationCanceledException>(exception.PrimaryFailure);
        Assert.Equal(1, handle.KillCount);
        Assert.Equal(2, handle.WaitTokens.Count);
        Assert.False(handle.WaitTokens[1].CanBeCanceled);
    }

    [Fact]
    public async Task Already_exited_kill_race_preserves_cancellation_after_terminal_proof()
    {
        var executablePath = Path.GetFullPath(Path.Combine("tools", "vba-dev.exe"));
        var handle = new AlreadyExitedCancellationProcessHandle();
        var invocation = new ProcessInvocation(
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
        var invocation = new ProcessInvocation(
            executablePath,
            new RecordingProcessPlatform(handle));
        using var cancellation = new CancellationTokenSource();

        var running = invocation.RunAsync(["reference", "list"], cancellation.Token);
        await handle.CancellableWaitStarted.Task;
        cancellation.Cancel();

        var exception = await Assert.ThrowsAsync<ProcessLifecycleException>(
            () => running);

        Assert.Contains("terminal process exit", exception.Message);
        Assert.IsType<IOException>(exception.CleanupFailure);
        Assert.Equal(1, handle.KillCount);
        Assert.Equal(2, handle.WaitTokens.Count);
        Assert.False(handle.WaitTokens[1].CanBeCanceled);
    }

    private sealed class RecordingProcessPlatform(IProcessHandle platformProcess)
        : IProcessPlatform
    {
        public string? ExecutablePath { get; private set; }

        public IReadOnlyList<string>? Arguments { get; private set; }

        public int StartCount { get; private set; }

        public IProcessHandle Start(
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
        string standardError) : IProcessHandle
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

    private sealed class DrainOrderingProcessHandle : IProcessHandle
    {
        private bool standardOutputDrainStarted;
        private bool standardErrorDrainStarted;

        public int ExitCode => 0;

        public bool BothDrainsStartedBeforeExitWait { get; private set; }
        public bool BothDrainsStartedBeforeResume { get; private set; }
        public int ResumeCount { get; private set; }

        public void Resume()
        {
            ResumeCount++;
            BothDrainsStartedBeforeResume = standardOutputDrainStarted && standardErrorDrainStarted;
        }

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

    private sealed class CancellationProcessHandle : IProcessHandle
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

    private sealed class DelayedDrainCancellationProcessHandle : IProcessHandle
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

    private sealed class AlreadyExitedCancellationProcessHandle : IProcessHandle
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
        : IProcessHandle
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
        Exception? killFailure = null) : IProcessHandle
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

    private sealed class UnprovableTerminationProcessHandle : IProcessHandle
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
