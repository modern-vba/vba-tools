using System.Diagnostics;

namespace VbaLanguageServer.Processes;

internal sealed record VbaDevProcessInvocationResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

internal delegate Task<VbaDevProcessInvocationResult> VbaDevProcessInvocationRunner(
    IReadOnlyList<string> arguments,
    CancellationToken cancellationToken);

internal sealed class VbaDevProcessLifecycleException(
    string executablePath,
    Exception innerException) : InvalidOperationException(
        $"VbaDev at '{executablePath}' could not prove terminal process exit and complete stream drain after cancellation.",
        innerException);

internal interface IVbaDevProcessPlatform
{
    IVbaDevProcessHandle Start(
        string executablePath,
        IReadOnlyList<string> arguments);
}

internal interface IVbaDevProcessHandle : IDisposable
{
    int ExitCode { get; }

    Task<string> ReadStandardOutputToEndAsync();

    Task<string> ReadStandardErrorToEndAsync();

    Task WaitForExitAsync(CancellationToken cancellationToken);

    void KillEntireProcessTree();
}

internal sealed class VbaDevProcessInvocation
{
    internal static readonly TimeSpan DefaultCancellationCleanupTimeout =
        TimeSpan.FromSeconds(5);

    private readonly string executablePath;
    private readonly IVbaDevProcessPlatform platform;
    private readonly TimeSpan cancellationCleanupTimeout;

    public VbaDevProcessInvocation(string executablePath)
        : this(executablePath, SystemVbaDevProcessPlatform.Instance)
    {
    }

    internal VbaDevProcessInvocation(
        string executablePath,
        IVbaDevProcessPlatform platform,
        TimeSpan? cancellationCleanupTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(platform);
        if (!Path.IsPathFullyQualified(executablePath))
        {
            throw new ArgumentException(
                "The pinned vba-dev executable path must be absolute.",
                nameof(executablePath));
        }

        var cleanupTimeout =
            cancellationCleanupTimeout ?? DefaultCancellationCleanupTimeout;
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

    public async Task<VbaDevProcessInvocationResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();
        var argumentSnapshot = arguments.ToArray();
        using var process = platform.Start(executablePath, argumentSnapshot);
        var standardOutput = process.ReadStandardOutputToEndAsync();
        var standardError = process.ReadStandardErrorToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(standardOutput, standardError)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Exception? terminationRequestFailure = null;
            try
            {
                process.KillEntireProcessTree();
            }
            catch (Exception exception) when (exception is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or IOException
                or NotSupportedException)
            {
                terminationRequestFailure = exception;
            }

            var cleanup = CompleteCancellationCleanupAsync(
                process,
                standardOutput,
                standardError);
            try
            {
                await cleanup.WaitAsync(cancellationCleanupTimeout).ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                ObserveLateCleanupFailure(cleanup);
                var lifecycleFailure = terminationRequestFailure is null
                    ? cleanupFailure
                    : new AggregateException(terminationRequestFailure, cleanupFailure);
                throw new VbaDevProcessLifecycleException(
                    executablePath,
                    lifecycleFailure);
            }

            // A concurrent process exit is benign once the uncancelled wait proves termination.
            throw;
        }

        return new VbaDevProcessInvocationResult(
            process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false));
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

    private static async Task CompleteCancellationCleanupAsync(
        IVbaDevProcessHandle process,
        Task<string> standardOutput,
        Task<string> standardError)
    {
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
    }
}

internal sealed class SystemVbaDevProcessPlatform : IVbaDevProcessPlatform
{
    public static SystemVbaDevProcessPlatform Instance { get; } = new();

    private SystemVbaDevProcessPlatform()
    {
    }

    public IVbaDevProcessHandle Start(
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
                    $"VbaDev at '{executablePath}' could not be started.");
            }

            return new SystemVbaDevProcessHandle(process);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }
}

internal sealed class SystemVbaDevProcessHandle(Process process) : IVbaDevProcessHandle
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
