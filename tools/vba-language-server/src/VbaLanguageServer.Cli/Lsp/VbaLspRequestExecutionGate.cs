namespace VbaLanguageServer.Lsp;

internal interface IVbaLspRequestExecutionGate
{
    Task WaitAsync(
        VbaLspRequestId? requestId,
        string method,
        CancellationToken cancellationToken);
}

internal sealed class ImmediateVbaLspRequestExecutionGate : IVbaLspRequestExecutionGate
{
    public static ImmediateVbaLspRequestExecutionGate Instance { get; } = new();

    private ImmediateVbaLspRequestExecutionGate()
    {
    }

    public Task WaitAsync(
        VbaLspRequestId? requestId,
        string method,
        CancellationToken cancellationToken)
        => Task.CompletedTask;
}

/// <summary>
/// Provides a one-shot file gate for deterministic language-server process tests.
/// </summary>
internal sealed class BlockingVbaLspRequestExecutionGate : IVbaLspRequestExecutionGate
{
    internal const string RequestIdEnvironmentVariable = "VBA_TOOLS_INTERACTIVE_REQUEST_ID";
    internal const string StartedFileEnvironmentVariable = "VBA_TOOLS_INTERACTIVE_REQUEST_STARTED_FILE";
    internal const string CancelledFileEnvironmentVariable = "VBA_TOOLS_INTERACTIVE_REQUEST_CANCELLED_FILE";
    internal const string ReleaseFileEnvironmentVariable = "VBA_TOOLS_INTERACTIVE_REQUEST_RELEASE_FILE";

    private readonly VbaLspRequestId targetRequestId;
    private readonly string startedFile;
    private readonly string? cancelledFile;
    private readonly string releaseFile;
    private int claimed;

    private BlockingVbaLspRequestExecutionGate(
        VbaLspRequestId targetRequestId,
        string startedFile,
        string? cancelledFile,
        string releaseFile)
    {
        this.targetRequestId = targetRequestId;
        this.startedFile = startedFile;
        this.cancelledFile = cancelledFile;
        this.releaseFile = releaseFile;
    }

    public static IVbaLspRequestExecutionGate CreateFromEnvironment()
    {
        var configuredRequestId = Environment.GetEnvironmentVariable(RequestIdEnvironmentVariable);
        var startedFile = Environment.GetEnvironmentVariable(StartedFileEnvironmentVariable);
        var cancelledFile = Environment.GetEnvironmentVariable(CancelledFileEnvironmentVariable);
        var releaseFile = Environment.GetEnvironmentVariable(ReleaseFileEnvironmentVariable);
        return TryParseRequestId(configuredRequestId, out var requestId)
            && !string.IsNullOrWhiteSpace(startedFile)
            && !string.IsNullOrWhiteSpace(releaseFile)
                ? new BlockingVbaLspRequestExecutionGate(
                    requestId,
                    startedFile,
                    cancelledFile,
                    releaseFile)
                : ImmediateVbaLspRequestExecutionGate.Instance;
    }

    public async Task WaitAsync(
        VbaLspRequestId? requestId,
        string method,
        CancellationToken cancellationToken)
    {
        if (requestId != targetRequestId
            || Interlocked.CompareExchange(ref claimed, 1, 0) != 0)
        {
            return;
        }

        WriteSignal(startedFile, method);
        try
        {
            await WaitForReleaseAsync(releaseFile, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            WriteSignal(cancelledFile, method);
            throw;
        }
    }

    private static bool TryParseRequestId(string? configured, out VbaLspRequestId requestId)
    {
        requestId = default;
        if (string.IsNullOrWhiteSpace(configured))
        {
            return false;
        }

        const string numberPrefix = "number:";
        const string stringPrefix = "string:";
        if (configured.StartsWith(numberPrefix, StringComparison.Ordinal))
        {
            requestId = new VbaLspRequestId(
                VbaLspRequestIdKind.Number,
                configured[numberPrefix.Length..]);
            return requestId.Value.Length > 0;
        }

        if (configured.StartsWith(stringPrefix, StringComparison.Ordinal))
        {
            requestId = new VbaLspRequestId(
                VbaLspRequestIdKind.String,
                configured[stringPrefix.Length..]);
            return true;
        }

        return false;
    }

    private static async Task WaitForReleaseAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            return;
        }

        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"The gate path has no directory: {path}");
        Directory.CreateDirectory(directory);
        var released = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(directory, Path.GetFileName(path));
        FileSystemEventHandler signalRelease = (_, _) =>
        {
            if (File.Exists(path))
            {
                released.TrySetResult();
            }
        };
        RenamedEventHandler signalRename = (_, _) =>
        {
            if (File.Exists(path))
            {
                released.TrySetResult();
            }
        };
        watcher.Created += signalRelease;
        watcher.Changed += signalRelease;
        watcher.Renamed += signalRename;
        watcher.EnableRaisingEvents = true;
        if (File.Exists(path))
        {
            released.TrySetResult();
        }

        await released.Task.WaitAsync(cancellationToken);
    }

    private static void WriteSignal(string? path, string method)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, method);
    }
}
