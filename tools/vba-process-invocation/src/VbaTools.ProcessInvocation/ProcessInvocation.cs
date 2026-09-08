using System.Diagnostics;

namespace VbaTools.Processes;

public sealed record ProcessInvocationResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

public delegate Task<ProcessInvocationResult> ProcessInvocationRunner(
    IReadOnlyList<string> arguments,
    CancellationToken cancellationToken);

public sealed class ProcessLifecycleException(
    string executablePath,
    Exception primaryFailure,
    Exception? terminationFailure,
    Exception cleanupFailure) : InvalidOperationException(
        $"Process at '{executablePath}' could not prove terminal process exit and complete stream drain after cancellation or execution failure.",
        new AggregateException(terminationFailure is null
            ? [primaryFailure, cleanupFailure]
            : [primaryFailure, terminationFailure, cleanupFailure]))
{
    public Exception PrimaryFailure { get; } = primaryFailure;
    public Exception? TerminationFailure { get; } = terminationFailure;
    public Exception CleanupFailure { get; } = cleanupFailure;
}

public interface IProcessPlatform
{
    IProcessHandle Start(
        string executablePath,
        IReadOnlyList<string> arguments);
}

public interface IProcessHandle : IDisposable
{
    int ExitCode { get; }

    Task<string> ReadStandardOutputToEndAsync();

    Task<string> ReadStandardErrorToEndAsync();

    // Suspended adapters resume here, after both drains have started.
    // Ordinary Process adapters are already running.
    void Resume() { }

    Task WaitForExitAsync(CancellationToken cancellationToken);

    void KillEntireProcessTree();
}

public sealed class ProcessInvocation
{
    public static readonly TimeSpan DefaultCleanupTimeout =
        TimeSpan.FromSeconds(5);

    private readonly string executablePath;
    private readonly IProcessPlatform platform;
    private readonly TimeSpan cancellationCleanupTimeout;

    public ProcessInvocation(string executablePath)
        : this(executablePath, SystemProcessPlatform.Instance)
    {
    }

    public ProcessInvocation(
        string executablePath,
        IProcessPlatform platform,
        TimeSpan? cancellationCleanupTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(platform);
        if (!Path.IsPathFullyQualified(executablePath))
        {
            throw new ArgumentException(
                "The pinned executable path must be absolute.",
                nameof(executablePath));
        }

        var cleanupTimeout =
            cancellationCleanupTimeout ?? DefaultCleanupTimeout;
        if (cleanupTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cancellationCleanupTimeout),
                "The cancellation cleanup timeout must be finite and non-negative.");
        }

        this.executablePath = executablePath;
        this.platform = platform;
        this.cancellationCleanupTimeout = cleanupTimeout;
    }

    public async Task<ProcessInvocationResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();
        var argumentSnapshot = arguments.ToArray();
        var process = platform.Start(executablePath, argumentSnapshot);
        Exception? primaryFailure = null;
        ProcessInvocationResult result;
        try
        {
            result = await RunOwnedAsync(process, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            primaryFailure = failure;
            throw;
        }
        finally
        {
            try { process.Dispose(); }
            catch (Exception releaseFailure) when (primaryFailure is not null)
            {
                var lifecycle = primaryFailure as ProcessLifecycleException;
                throw new ProcessLifecycleException(executablePath,
                    lifecycle?.PrimaryFailure ?? primaryFailure,
                    lifecycle?.TerminationFailure,
                    lifecycle is null ? releaseFailure
                        : new AggregateException(lifecycle.CleanupFailure, releaseFailure));
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private async Task<ProcessInvocationResult> RunOwnedAsync(
        IProcessHandle process, CancellationToken cancellationToken)
    {
        var standardOutput = StartReader(process.ReadStandardOutputToEndAsync);
        var standardError = StartReader(process.ReadStandardErrorToEndAsync);
        try
        {
            if (standardOutput.IsFaulted) { await standardOutput.ConfigureAwait(false); }
            if (standardError.IsFaulted) { await standardError.ConfigureAwait(false); }
            process.Resume();
            var exit = WaitForExitAsync(process, cancellationToken);
            ObserveLateCleanupFailure(exit);
            var pending = new List<Task>
            {
                exit, standardOutput, standardError
            };
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
                pending.Remove(completed);
            }
            var result = new ProcessInvocationResult(
                process.ExitCode,
                await standardOutput.ConfigureAwait(false),
                await standardError.ConfigureAwait(false));
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception executionFailure)
        {
            Exception? terminationRequestFailure = null;
            try
            {
                process.KillEntireProcessTree();
            }
            catch (Exception exception)
            {
                terminationRequestFailure = exception;
            }

            var cleanupTasks = StartCleanup(
                process,
                standardOutput,
                standardError,
                executionFailure);
            foreach (var task in cleanupTasks) { ObserveLateCleanupFailure(task); }
            var cleanup = Task.WhenAll(cleanupTasks);
            try
            {
                await cleanup.WaitAsync(cancellationCleanupTimeout).ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                ObserveLateCleanupFailure(cleanup);
                var failures = cleanupTasks.Where(task => task.IsFaulted)
                    .SelectMany(task => task.Exception!.Flatten().InnerExceptions)
                    .Append(cleanupFailure).Distinct().ToArray();
                var evidence = failures.Length > 1
                    ? new AggregateException(failures)
                    : cleanupFailure;
                throw new ProcessLifecycleException(
                    executablePath,
                    executionFailure,
                    terminationRequestFailure,
                    evidence);
            }

            // A concurrent process exit is benign once the uncancelled wait proves termination.
            throw;
        }

    }

    private static Task<string> StartReader(Func<Task<string>> start)
    {
        Task<string> reader;
        try { reader = start(); }
        catch (Exception failure) { reader = Task.FromException<string>(failure); }
        ObserveLateCleanupFailure(reader);
        return reader;
    }

    private static void ObserveLateCleanupFailure(Task cleanup)
    {
        _ = cleanup.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously
                | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private static Task[] StartCleanup(
        IProcessHandle process,
        Task<string> standardOutput,
        Task<string> standardError,
        Exception executionFailure)
    {
        return [
            WaitForExitAsync(process, CancellationToken.None),
            CompleteReaderAsync(standardOutput, executionFailure),
            CompleteReaderAsync(standardError, executionFailure)];
    }

    private static async Task WaitForExitAsync(IProcessHandle process, CancellationToken token)
        => await process.WaitForExitAsync(token).ConfigureAwait(false);

    private static async Task CompleteReaderAsync(Task<string> reader, Exception executionFailure)
    {
        try
        {
            await reader.ConfigureAwait(false);
        }
        catch (Exception failure) when (ReferenceEquals(failure, executionFailure))
        {
            // The original read failure remains the invocation result after terminal cleanup.
        }
    }
}

internal sealed class SystemProcessPlatform : IProcessPlatform
{
    public static SystemProcessPlatform Instance { get; } = new();

    private SystemProcessPlatform()
    {
    }

    public IProcessHandle Start(
        string executablePath,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException(
                    $"Process at '{executablePath}' could not be started.");
            }

            return new SystemProcessHandle(process);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }
}

internal sealed class SystemProcessHandle(Process process) : IProcessHandle
{
    public int ExitCode => process.ExitCode;

    public Task<string> ReadStandardOutputToEndAsync()
        => process.StandardOutput.ReadToEndAsync();

    public Task<string> ReadStandardErrorToEndAsync()
        => process.StandardError.ReadToEndAsync();

    public Task WaitForExitAsync(CancellationToken cancellationToken)
        => process.WaitForExitAsync(cancellationToken);

    public void KillEntireProcessTree()
        => process.Kill(entireProcessTree: true);

    public void Dispose()
        => process.Dispose();
}
