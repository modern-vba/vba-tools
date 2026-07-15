using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VbaLanguageServer.SourceModel;

namespace VbaLanguageServer.Tests;

internal sealed class LanguageServerProcessHarness : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _gate = new();
    private readonly Process _process;
    private readonly Stream _stdin;
    private readonly Stream _stdout;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<int, TaskCompletionSource<JsonElement>> _pendingResponses = [];
    private readonly List<JsonElement> _transcript = [];
    private readonly Dictionary<string, int> _readCursors = new(StringComparer.Ordinal);
    private readonly List<string> _stderr = [];
    private readonly bool _ownsCacheRoot;
    private readonly string _cacheRoot;
    private readonly Task _stdoutPump;
    private readonly Task _stderrPump;
    private TaskCompletionSource<bool> _transcriptChanged = CreateSignal();
    private Exception? _sessionFailure;
    private bool _initialized;
    private bool _shutdownRequested;
    private bool _disposed;
    private int _cleanupRequestId = 1_000_000;

    private LanguageServerProcessHarness(
        Process process,
        string cacheRoot,
        bool ownsCacheRoot)
    {
        _process = process;
        _stdin = process.StandardInput.BaseStream;
        _stdout = process.StandardOutput.BaseStream;
        _cacheRoot = cacheRoot;
        _ownsCacheRoot = ownsCacheRoot;
        _stdoutPump = PumpStdoutAsync(_lifetime.Token);
        _stderrPump = PumpStderrAsync(_lifetime.Token);
    }

    public int TranscriptCheckpoint
    {
        get
        {
            lock (_gate)
            {
                return _transcript.Count;
            }
        }
    }

    public static Task<LanguageServerProcessHarness> StartAsync(
        string? referenceCatalogCacheRoot = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var serverProjectPath = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "src",
                "VbaLanguageServer.Cli",
                "VbaLanguageServer.Cli.csproj"));
        return StartFromProjectAsync(serverProjectPath, referenceCatalogCacheRoot, environment);
    }

    private static Task<LanguageServerProcessHarness> StartFromProjectAsync(
        string serverProjectPath,
        string? referenceCatalogCacheRoot = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var ownsCacheRoot = referenceCatalogCacheRoot is null;
        var cacheRoot = referenceCatalogCacheRoot
            ?? Directory.CreateTempSubdirectory("vba-ls-process-cache-").FullName;
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment[VbaProjectReferenceCatalogPersistentStore.CacheRootEnvironmentVariable] = cacheRoot;
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            startInfo.Environment[name] = value;
        }

        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(serverProjectPath);

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the language server process.");
        return Task.FromResult(new LanguageServerProcessHarness(process, cacheRoot, ownsCacheRoot));
    }

    public async Task<JsonElement> InitializeAsync(int requestId = 1, CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync(
            requestId,
            "initialize",
            new
            {
                processId = Environment.ProcessId,
                rootUri = (string?)null,
                capabilities = new { }
            },
            cancellationToken: cancellationToken);
        await SendNotificationAsync("initialized", new { }, cancellationToken);
        _initialized = true;
        return response;
    }

    public async Task<JsonElement> SendRequestAsync(
        int id,
        string method,
        object? parameters,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (!_pendingResponses.TryAdd(id, response))
            {
                throw new InvalidOperationException($"A language-server request with id {id} is already pending.");
            }
        }

        try
        {
            await WriteMessageAsync(
                new
                {
                    jsonrpc = "2.0",
                    id,
                    method,
                    @params = parameters
                },
                cancellationToken);
            return await response.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(10), cancellationToken);
        }
        catch (TimeoutException exception)
        {
            throw CreateSessionException($"Timed out waiting for response {id} ({method}).", exception);
        }
        finally
        {
            lock (_gate)
            {
                _pendingResponses.Remove(id);
            }
        }
    }

    public Task SendNotificationAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken = default)
        => WriteMessageAsync(
            new
            {
                jsonrpc = "2.0",
                method,
                @params = parameters
            },
            cancellationToken);

    public Task SendRawMessageAsync(object message, CancellationToken cancellationToken = default)
        => WriteMessageAsync(message, cancellationToken);

    public Task<JsonElement> ReadNextMessageAsync(
        int afterCheckpoint,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => WaitForTranscriptMessageAsync(
            $"raw:{afterCheckpoint}",
            static _ => true,
            timeout ?? TimeSpan.FromSeconds(10),
            afterCheckpoint,
            cancellationToken);

    public Task<JsonElement> WaitForNotificationAsync(
        string method,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => WaitForTranscriptMessageAsync(
            $"notification:{method}",
            message => message.TryGetProperty("method", out var methodElement)
                && methodElement.GetString() == method,
            timeout ?? TimeSpan.FromSeconds(5),
            afterCheckpoint: null,
            cancellationToken);

    public Task<JsonElement> WaitForDiagnosticsAsync(
        string uri,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => WaitForTranscriptMessageAsync(
            $"diagnostics:{uri}",
            message => message.TryGetProperty("method", out var methodElement)
                && methodElement.GetString() == "textDocument/publishDiagnostics"
                && message.GetProperty("params").GetProperty("uri").GetString() == uri,
            timeout ?? TimeSpan.FromSeconds(5),
            afterCheckpoint: null,
            cancellationToken);

    public async Task<JsonElement> WaitForLogMessageAsync(
        string expectedMessageFragment,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => await TryWaitForLogMessageAsync(
                expectedMessageFragment,
                timeout ?? TimeSpan.FromSeconds(5),
                cancellationToken)
            ?? throw CreateSessionException(
                $"Language server did not write a log message containing: {expectedMessageFragment}");

    public async Task<JsonElement?> TryWaitForLogMessageAsync(
        string expectedMessageFragment,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await WaitForTranscriptMessageAsync(
                $"log:{expectedMessageFragment}",
                message => IsMatchingLogMessage(message, expectedMessageFragment),
                timeout,
                afterCheckpoint: null,
                cancellationToken);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    public async Task<string> WaitForLogTextAsync(
        string expectedText,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var message = await WaitForLogMessageAsync(
            expectedText,
            timeout ?? TimeSpan.FromSeconds(10),
            cancellationToken);
        return message.GetProperty("params").GetProperty("message").GetString() ?? "";
    }

    public async Task<JsonElement> ShutdownAsync(
        int requestId,
        CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync(
            requestId,
            "shutdown",
            parameters: null,
            cancellationToken: cancellationToken);
        _shutdownRequested = true;
        await SendNotificationAsync("exit", parameters: null, cancellationToken);
        await WaitForExitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        if (_process.ExitCode != 0)
        {
            throw CreateSessionException($"Language server exited with code {_process.ExitCode}.");
        }

        return response;
    }

    private async Task WaitForExitAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException exception)
        {
            throw CreateSessionException("Timed out waiting for the language server to exit.", exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await TryStopProcessAsync();
        }
        finally
        {
            _disposed = true;
            _lifetime.Cancel();
            await IgnoreFailureAsync(_stdoutPump);
            await IgnoreFailureAsync(_stderrPump);
            _stdin.Dispose();
            _stdout.Dispose();
            _process.Dispose();
            _writeLock.Dispose();
            _lifetime.Dispose();
            if (_ownsCacheRoot)
            {
                TryDeleteDirectory(_cacheRoot);
            }
        }
    }

    private async Task TryStopProcessAsync()
    {
        if (_process.HasExited)
        {
            return;
        }

        if (_initialized && !_shutdownRequested)
        {
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await ShutdownAsync(Interlocked.Increment(ref _cleanupRequestId), cleanup.Token);
                return;
            }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                Debug.WriteLine(exception);
            }
        }

        try
        {
            _process.Kill(entireProcessTree: true);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _process.WaitForExitAsync(cleanup.Token);
        }
        catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException)
        {
            Debug.WriteLine(exception);
        }
    }

    private async Task PumpStdoutAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await ReadMessageFrameAsync(_stdout, cancellationToken);
                TaskCompletionSource<JsonElement>? response = null;
                TaskCompletionSource<bool> changed;
                lock (_gate)
                {
                    _transcript.Add(message);
                    if (message.TryGetProperty("id", out var idElement)
                        && idElement.ValueKind == JsonValueKind.Number
                        && idElement.TryGetInt32(out var id)
                        && _pendingResponses.Remove(id, out response))
                    {
                    }

                    changed = _transcriptChanged;
                    _transcriptChanged = CreateSignal();
                }

                response?.TrySetResult(message);
                changed.TrySetResult(true);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (EndOfStreamException) when (_shutdownRequested || _disposed)
        {
        }
        catch (Exception exception)
        {
            FaultSession(exception);
        }
    }

    private async Task PumpStderrAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await _process.StandardError.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    return;
                }

                lock (_gate)
                {
                    _stderr.Add(line);
                }

                Debug.WriteLine(line);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            FaultSession(exception);
        }
    }

    private async Task WriteMessageAsync(object message, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var content = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {content.Length}\r\n\r\n");
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _stdin.WriteAsync(header, cancellationToken);
            await _stdin.WriteAsync(content, cancellationToken);
            await _stdin.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<JsonElement> WaitForTranscriptMessageAsync(
        string cursorKey,
        Func<JsonElement, bool> predicate,
        TimeSpan timeout,
        int? afterCheckpoint,
        CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(timeout);
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                var start = afterCheckpoint ?? _readCursors.GetValueOrDefault(cursorKey);
                for (var index = start; index < _transcript.Count; index++)
                {
                    var message = _transcript[index];
                    if (!predicate(message))
                    {
                        continue;
                    }

                    _readCursors[cursorKey] = index + 1;
                    return message;
                }

                if (_sessionFailure is not null)
                {
                    throw CreateSessionException("The language-server session failed.", _sessionFailure);
                }

                changed = _transcriptChanged.Task;
            }

            try
            {
                await changed.WaitAsync(wait.Token);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                var failure = CreateSessionException(
                    "Timed out waiting for a language-server message.",
                    exception);
                throw new TimeoutException(failure.Message, exception);
            }
        }
    }

    private void FaultSession(Exception exception)
    {
        TaskCompletionSource<JsonElement>[] pending;
        TaskCompletionSource<bool> changed;
        lock (_gate)
        {
            _sessionFailure ??= exception;
            pending = [.. _pendingResponses.Values];
            _pendingResponses.Clear();
            changed = _transcriptChanged;
            _transcriptChanged = CreateSignal();
        }

        var failure = CreateSessionException("The language-server session failed.", exception);
        foreach (var response in pending)
        {
            response.TrySetException(failure);
        }

        changed.TrySetResult(true);
    }

    private InvalidOperationException CreateSessionException(string message, Exception? innerException = null)
    {
        string stderr;
        string transcript;
        lock (_gate)
        {
            stderr = string.Join(Environment.NewLine, _stderr.TakeLast(20));
            transcript = string.Join(
                Environment.NewLine,
                _transcript.TakeLast(10).Select(item => item.GetRawText()));
        }

        var details = new StringBuilder(message);
        if (_process.HasExited)
        {
            details.Append($" Exit code: {_process.ExitCode}.");
        }

        if (stderr.Length > 0)
        {
            details.Append($"{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");
        }

        if (transcript.Length > 0)
        {
            details.Append($"{Environment.NewLine}recent messages:{Environment.NewLine}{transcript}");
        }

        return new InvalidOperationException(details.ToString(), innerException);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static bool IsMatchingLogMessage(JsonElement message, string expectedMessageFragment)
        => message.TryGetProperty("method", out var methodElement)
            && methodElement.GetString() == "window/logMessage"
            && message.GetProperty("params").GetProperty("message").GetString()
                ?.Contains(expectedMessageFragment, StringComparison.Ordinal) == true;

    private static TaskCompletionSource<bool> CreateSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<JsonElement> ReadMessageFrameAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var headerBytes = new List<byte>();
        var singleByte = new byte[1];
        while (!EndsWithHeaderTerminator(headerBytes))
        {
            var read = await stream.ReadAsync(singleByte.AsMemory(0, 1), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("Language server closed stdout before sending a response.");
            }

            headerBytes.Add(singleByte[0]);
        }

        var headers = Encoding.ASCII.GetString(headerBytes.ToArray());
        var contentLength = headers
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2)
            .Where(parts => string.Equals(parts[0], "Content-Length", StringComparison.OrdinalIgnoreCase))
            .Select(parts => int.Parse(parts[1].Trim()))
            .Single();

        var content = new byte[contentLength];
        var offset = 0;
        while (offset < content.Length)
        {
            var read = await stream.ReadAsync(content.AsMemory(offset, content.Length - offset), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("Language server closed stdout mid-message.");
            }

            offset += read;
        }

        return JsonDocument.Parse(content).RootElement.Clone();
    }

    private static bool EndsWithHeaderTerminator(List<byte> bytes)
        => bytes.Count >= 4
            && bytes[^4] == '\r'
            && bytes[^3] == '\n'
            && bytes[^2] == '\r'
            && bytes[^1] == '\n';

    private static async Task IgnoreFailureAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
