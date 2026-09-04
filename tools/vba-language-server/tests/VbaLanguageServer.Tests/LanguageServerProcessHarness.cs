using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace VbaLanguageServer.Tests;

internal sealed class LanguageServerProcessHarness : IAsyncDisposable
{
    private const string ReferenceCatalogCacheRootEnvironmentVariable =
        "VBA_TOOLS_REFERENCE_CATALOG_CACHE_DIR";
    private const string ProjectDiagnosticsPublicationDirectoryEnvironmentVariable =
        "VBA_TOOLS_PROJECT_DIAGNOSTICS_PUBLICATION_DIRECTORY";

    private enum HarnessState
    {
        Active,
        Disposing,
        Disposed
    }

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
    private readonly CancellationTokenSource _operations = new();
    private readonly Dictionary<int, TaskCompletionSource<JsonElement>> _pendingResponses = [];
    private readonly List<JsonElement> _transcript = [];
    private readonly Dictionary<string, int> _readCursors = new(StringComparer.Ordinal);
    private readonly List<string> _stderr = [];
    private readonly bool _ownsCacheRoot;
    private readonly string _cacheRoot;
    private readonly string? _projectDiagnosticsPublicationDirectory;
    private readonly Task _stdoutPump;
    private readonly Task _stderrPump;
    private TaskCompletionSource<bool> _transcriptChanged = CreateSignal();
    private Exception? _sessionFailure;
    private HarnessState _state = HarnessState.Active;
    private Task? _disposeTask;
    private bool _initialized;
    private bool _shutdownRequested;
    private bool _inputCompleted;
    private int _cleanupRequestId = 1_000_000;
    private int _outputFenceRequestId = 2_000_000;

    private LanguageServerProcessHarness(
        Process process,
        string cacheRoot,
        bool ownsCacheRoot,
        string? projectDiagnosticsPublicationDirectory)
    {
        _process = process;
        _stdin = process.StandardInput.BaseStream;
        _stdout = process.StandardOutput.BaseStream;
        _cacheRoot = cacheRoot;
        _ownsCacheRoot = ownsCacheRoot;
        _projectDiagnosticsPublicationDirectory =
            projectDiagnosticsPublicationDirectory;
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

    public int CountResponses(int requestId, int afterCheckpoint = 0)
    {
        lock (_gate)
        {
            return _transcript
                .Skip(afterCheckpoint)
                .Count(message =>
                    message.TryGetProperty("id", out var id)
                    && id.ValueKind == JsonValueKind.Number
                    && id.TryGetInt32(out var numericId)
                    && numericId == requestId
                    && !message.TryGetProperty("method", out _)
                    && (message.TryGetProperty("result", out _)
                        || message.TryGetProperty("error", out _)));
        }
    }

    public static Task<LanguageServerProcessHarness> StartAsync(
        string? referenceCatalogCacheRoot = null,
        IReadOnlyDictionary<string, string>? environment = null,
        IReadOnlyList<string>? serverArguments = null,
        bool enableProjectDiagnosticsSynchronization = false)
    {
        var serverExecutablePath = FindServerExecutablePath();
        return StartFromExecutableAsync(
            serverExecutablePath,
            referenceCatalogCacheRoot,
            environment,
            serverArguments,
            enableProjectDiagnosticsSynchronization);
    }

    private static Task<LanguageServerProcessHarness> StartFromExecutableAsync(
        string serverExecutablePath,
        string? referenceCatalogCacheRoot = null,
        IReadOnlyDictionary<string, string>? environment = null,
        IReadOnlyList<string>? serverArguments = null,
        bool enableProjectDiagnosticsSynchronization = false)
    {
        var ownsCacheRoot = referenceCatalogCacheRoot is null;
        var cacheRoot = referenceCatalogCacheRoot
            ?? Directory.CreateTempSubdirectory("vba-ls-process-cache-").FullName;
        var publicationDirectory = enableProjectDiagnosticsSynchronization
            ? Directory.CreateTempSubdirectory(
                "vba-ls-project-diagnostics-").FullName
            : null;
        var startInfo = CreateServerStartInfo(
            serverExecutablePath,
            cacheRoot,
            environment,
            serverArguments,
            publicationDirectory);

        try
        {
            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start the language server process.");
            return Task.FromResult(new LanguageServerProcessHarness(
                process,
                cacheRoot,
                ownsCacheRoot,
                publicationDirectory));
        }
        catch
        {
            if (ownsCacheRoot)
            {
                TryDeleteDirectory(cacheRoot);
            }
            if (publicationDirectory is not null)
            {
                TryDeleteDirectory(publicationDirectory);
            }

            throw;
        }
    }

    internal static ProcessStartInfo CreateServerStartInfo(
        string serverExecutablePath,
        string cacheRoot,
        IReadOnlyDictionary<string, string>? environment = null,
        IReadOnlyList<string>? serverArguments = null,
        string? projectDiagnosticsPublicationDirectory = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = serverExecutablePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            startInfo.Environment[name] = value;
        }
        startInfo.Environment[ReferenceCatalogCacheRootEnvironmentVariable] = cacheRoot;
        if (projectDiagnosticsPublicationDirectory is not null)
        {
            startInfo.Environment[
                ProjectDiagnosticsPublicationDirectoryEnvironmentVariable] =
                    projectDiagnosticsPublicationDirectory;
        }

        if (serverArguments is { Count: > 0 })
        {
            foreach (var argument in serverArguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        return startInfo;
    }

    private static string FindServerExecutablePath()
    {
        var configuration = typeof(LanguageServerProcessHarness).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()
            ?.Configuration
            ?? "Debug";
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var projectDirectory = Path.Combine(
                directory.FullName,
                "tools",
                "vba-language-server",
                "src",
                "VbaLanguageServer.Cli");
            var projectPath = Path.Combine(
                projectDirectory,
                "VbaLanguageServer.Cli.csproj");
            if (!File.Exists(projectPath))
            {
                continue;
            }

            var executablePath = Path.Combine(
                projectDirectory,
                "bin",
                configuration,
                "net10.0",
                "win-x64",
                "vba-language-server.exe");
            if (File.Exists(executablePath))
            {
                return executablePath;
            }

            throw new InvalidOperationException(
                $"Could not locate the {configuration} vba-language-server apphost at {executablePath}.");
        }

        throw new InvalidOperationException(
            "Could not locate the VbaLanguageServer.Cli project from the test output directory.");
    }

    public async Task<JsonElement> InitializeAsync(int requestId = 1, CancellationToken cancellationToken = default)
        => await InitializeAsync(
            new { },
            requestId,
            cancellationToken);

    public async Task<JsonElement> InitializeAsync(
        object capabilities,
        int requestId = 1,
        CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync(
            requestId,
            "initialize",
            new
            {
                processId = Environment.ProcessId,
                rootUri = (string?)null,
                capabilities
            },
            cancellationToken: cancellationToken);
        await SendNotificationAsync("initialized", new { }, cancellationToken);
        _initialized = true;
        return response;
    }

    public Task<JsonElement> SendRequestAsync(
        int id,
        string method,
        object? parameters,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => SendRequestCoreAsync(
            id,
            method,
            parameters,
            timeout ?? TimeSpan.FromSeconds(10),
            cancellationToken,
            allowDisposing: false,
            includeOperationLifetime: true);

    public Task SendNotificationAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken = default)
        => SendMessageAsync(
            new
            {
                jsonrpc = "2.0",
                method,
                @params = parameters
            },
            TimeSpan.FromSeconds(10),
            cancellationToken);

    public Task SendRawMessageAsync(object message, CancellationToken cancellationToken = default)
        => SendMessageAsync(message, TimeSpan.FromSeconds(10), cancellationToken);

    public void CompleteInput()
    {
        lock (_gate)
        {
            EnsureOperationAllowedLocked(allowDisposing: false);
            _inputCompleted = true;
        }

        _stdin.Dispose();
    }

    public async Task<int> WaitForProcessExitAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        await WaitForExitAsync(
            timeout ?? TimeSpan.FromSeconds(5),
            cancellationToken);
        return _process.ExitCode;
    }

    private async Task<JsonElement> SendRequestCoreAsync(
        int id,
        string method,
        object? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool allowDisposing,
        bool includeOperationLifetime)
    {
        using var deadline = includeOperationLifetime
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _operations.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            EnsureOperationAllowedLocked(allowDisposing);
            if (!_pendingResponses.TryAdd(id, response))
            {
                throw new InvalidOperationException($"A language-server request with id {id} is already pending.");
            }
        }

        try
        {
            await WriteMessageCoreAsync(
                new
                {
                    jsonrpc = "2.0",
                    id,
                    method,
                    @params = parameters
                },
                deadline.Token);
            return await response.Task.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
            when (includeOperationLifetime && _operations.IsCancellationRequested)
        {
            throw CreateSessionException("The language-server session is disposing.", exception);
        }
        catch (OperationCanceledException exception)
        {
            var failure = CreateSessionException(
                $"Timed out sending or waiting for response {id} ({method}).",
                exception);
            throw new TimeoutException(failure.Message, exception);
        }
        finally
        {
            lock (_gate)
            {
                _pendingResponses.Remove(id);
            }
        }
    }

    private async Task SendMessageAsync(
        object message,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _operations.Token);
        deadline.CancelAfter(timeout);
        lock (_gate)
        {
            EnsureOperationAllowedLocked(allowDisposing: false);
        }

        try
        {
            await WriteMessageCoreAsync(message, deadline.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception) when (_operations.IsCancellationRequested)
        {
            throw CreateSessionException("The language-server session is disposing.", exception);
        }
        catch (OperationCanceledException exception)
        {
            var failure = CreateSessionException("Timed out sending a language-server message.", exception);
            throw new TimeoutException(failure.Message, exception);
        }
    }

    public Task<JsonElement> WaitForMessageAsync(
        int afterCheckpoint,
        Func<JsonElement, bool> predicate,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => WaitForTranscriptMessageAsync(
            $"raw:{afterCheckpoint}",
            predicate,
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

    public ProjectDiagnosticsCheckpoint CaptureProjectDiagnosticsCheckpoint()
    {
        var directory = _projectDiagnosticsPublicationDirectory
            ?? throw new InvalidOperationException(
                "Project-diagnostics synchronization was not enabled for this harness.");
        return new ProjectDiagnosticsCheckpoint(
            TranscriptCheckpoint,
            Directory.EnumerateFiles(directory, "*.completed")
                .Select(Path.GetFileName)
                .Where(fileName => fileName is not null)
                .Select(fileName => fileName!)
                .ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// Waits until project diagnostics for a fresh client version cross the
    /// transport, then fences stdout before returning the last matching frame.
    /// Callers must not mutate the manifest or reference catalog concurrently.
    /// </summary>
    public async Task<JsonElement> WaitForProjectDiagnosticsSettledAsync(
        string uri,
        int? expectedVersion,
        ProjectDiagnosticsCheckpoint checkpoint,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var directory = _projectDiagnosticsPublicationDirectory
            ?? throw new InvalidOperationException(
                "Project-diagnostics synchronization was not enabled for this harness.");
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(10);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _operations.Token);
        deadline.CancelAfter(effectiveTimeout);
        try
        {
            await WaitForProjectDiagnosticsMarkerAsync(
                directory,
                uri,
                expectedVersion,
                checkpoint.MarkerFileNames,
                deadline.Token);
            await SendRequestAsync(
                Interlocked.Increment(ref _outputFenceRequestId),
                "vba/test/outputFence",
                parameters: null,
                timeout: effectiveTimeout,
                cancellationToken: deadline.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw CreateSessionException(
                $"Timed out waiting for settled project diagnostics for '{uri}' at version {expectedVersion?.ToString() ?? "none"}.",
                exception);
        }

        lock (_gate)
        {
            for (var index = _transcript.Count - 1;
                 index >= checkpoint.TranscriptCheckpoint;
                 index--)
            {
                var message = _transcript[index];
                if (IsDiagnosticsForVersion(message, uri, expectedVersion))
                {
                    return message;
                }
            }
        }

        throw CreateSessionException(
            $"Project diagnostics for '{uri}' crossed transport without appearing in the transcript.");
    }

    public async Task<JsonElement> WaitForDiagnosticsMatchingAsync(
        string uri,
        Func<JsonElement, bool> diagnosticsPredicate,
        string expectation,
        int? afterCheckpoint = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(diagnosticsPredicate);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectation);

        try
        {
            return await WaitForTranscriptMessageAsync(
                $"diagnostics:{uri}",
                message => message.TryGetProperty("method", out var methodElement)
                    && methodElement.GetString() == "textDocument/publishDiagnostics"
                    && message.GetProperty("params").GetProperty("uri").GetString() == uri
                    && diagnosticsPredicate(
                        message.GetProperty("params").GetProperty("diagnostics")),
                timeout ?? TimeSpan.FromSeconds(5),
                afterCheckpoint,
                cancellationToken);
        }
        catch (TimeoutException exception)
        {
            var failure = CreateSessionException(
                $"Timed out waiting for diagnostics for '{uri}' matching {expectation}.",
                exception);
            throw new TimeoutException(failure.Message, exception);
        }
    }

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

    public Task<JsonElement> ShutdownAsync(
        int requestId,
        CancellationToken cancellationToken = default)
        => ShutdownCoreAsync(requestId, cancellationToken, allowDisposing: false);

    private async Task<JsonElement> ShutdownCoreAsync(
        int requestId,
        CancellationToken cancellationToken,
        bool allowDisposing)
    {
        var response = await SendRequestCoreAsync(
            requestId,
            "shutdown",
            parameters: null,
            TimeSpan.FromSeconds(10),
            cancellationToken,
            allowDisposing,
            includeOperationLifetime: !allowDisposing);
        _shutdownRequested = true;
        var exitMessage = new
        {
            jsonrpc = "2.0",
            method = "exit",
            @params = (object?)null
        };
        if (allowDisposing)
        {
            await WriteMessageCoreAsync(exitMessage, cancellationToken);
        }
        else
        {
            await SendMessageAsync(exitMessage, TimeSpan.FromSeconds(10), cancellationToken);
        }

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

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource<bool>? owner = null;
        Task disposal;
        lock (_gate)
        {
            if (_disposeTask is null)
            {
                owner = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposeTask = owner.Task;
                _state = HarnessState.Disposing;
            }

            disposal = _disposeTask;
        }

        if (owner is not null)
        {
            _ = CompleteDisposalAsync(owner);
        }

        return new ValueTask(disposal);
    }

    private async Task CompleteDisposalAsync(TaskCompletionSource<bool> completion)
    {
        try
        {
            await DisposeCoreAsync();
            completion.TrySetResult(true);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task DisposeCoreAsync()
    {
        FaultSession(new ObjectDisposedException(
            nameof(LanguageServerProcessHarness),
            "The language-server process harness is disposing."));
        _operations.Cancel();
        try
        {
            await TryStopProcessAsync();
        }
        finally
        {
            _lifetime.Cancel();
            await IgnoreFailureAsync(_stdoutPump);
            await IgnoreFailureAsync(_stderrPump);
            _stdin.Dispose();
            _stdout.Dispose();
            _process.Dispose();
            _writeLock.Dispose();
            _operations.Dispose();
            _lifetime.Dispose();
            if (_ownsCacheRoot)
            {
                TryDeleteDirectory(_cacheRoot);
            }
            if (_projectDiagnosticsPublicationDirectory is not null)
            {
                TryDeleteDirectory(_projectDiagnosticsPublicationDirectory);
            }

            lock (_gate)
            {
                _state = HarnessState.Disposed;
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
                await ShutdownCoreAsync(
                    Interlocked.Increment(ref _cleanupRequestId),
                    cleanup.Token,
                    allowDisposing: true);
                return;
            }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                Debug.WriteLine(exception);
            }
        }

        Exception? lastFailure = null;
        for (var attempt = 0; attempt < 2 && !_process.HasExited; attempt++)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _process.WaitForExitAsync(cleanup.Token);
            }
            catch (InvalidOperationException) when (_process.HasExited)
            {
                return;
            }
            catch (Exception exception)
                when (exception is InvalidOperationException
                    or OperationCanceledException
                    or System.ComponentModel.Win32Exception)
            {
                lastFailure = exception;
                Debug.WriteLine(exception);
            }
        }

        if (!_process.HasExited)
        {
            throw CreateSessionException(
                "Failed to stop the language-server process tree.",
                lastFailure);
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
                        && !message.TryGetProperty("method", out _)
                        && (message.TryGetProperty("result", out _)
                            || message.TryGetProperty("error", out _))
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
        catch (EndOfStreamException)
            when (_shutdownRequested || _state is not HarnessState.Active)
        {
        }
        catch (EndOfStreamException exception) when (_inputCompleted)
        {
            FaultSession(exception);
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

    private async Task WriteMessageCoreAsync(object message, CancellationToken cancellationToken)
    {
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
                var savedCursor = _readCursors.GetValueOrDefault(cursorKey);
                var start = afterCheckpoint is null
                    ? savedCursor
                    : Math.Max(afterCheckpoint.Value, savedCursor);
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

    private static async Task WaitForProjectDiagnosticsMarkerAsync(
        string directory,
        string uri,
        int? expectedVersion,
        IReadOnlySet<string> existingMarkerFileNames,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var path in Directory.EnumerateFiles(directory, "*.completed"))
            {
                if (existingMarkerFileNames.Contains(Path.GetFileName(path)))
                {
                    continue;
                }

                try
                {
                    var marker = JsonSerializer.Deserialize<
                        ProjectDiagnosticsPublicationMarker>(
                        File.ReadAllText(path),
                        JsonOptions);
                    if (marker is not null
                        && marker.Uri.Equals(uri, StringComparison.Ordinal)
                        && marker.ClientVersion == expectedVersion)
                    {
                        return;
                    }
                }
                catch (IOException) when (!cancellationToken.IsCancellationRequested)
                {
                }
                catch (JsonException) when (!cancellationToken.IsCancellationRequested)
                {
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }
    }

    private static bool IsDiagnosticsForVersion(
        JsonElement message,
        string uri,
        int? expectedVersion)
    {
        if (!message.TryGetProperty("method", out var method)
            || method.GetString() != "textDocument/publishDiagnostics"
            || !message.TryGetProperty("params", out var parameters)
            || parameters.GetProperty("uri").GetString() != uri)
        {
            return false;
        }

        return expectedVersion is { } version
            ? parameters.TryGetProperty("version", out var publishedVersion)
                && publishedVersion.GetInt32() == version
            : !parameters.TryGetProperty("version", out _);
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

    public readonly record struct ProjectDiagnosticsCheckpoint(
        int TranscriptCheckpoint,
        IReadOnlySet<string> MarkerFileNames);

    private sealed record ProjectDiagnosticsPublicationMarker(
        string Authority,
        string Uri,
        long Revision,
        int? ClientVersion);

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
        try
        {
            if (_process.HasExited)
            {
                details.Append($" Exit code: {_process.ExitCode}.");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
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

    private void EnsureOperationAllowedLocked(bool allowDisposing)
    {
        if (_state is HarnessState.Active
            || (allowDisposing && _state is HarnessState.Disposing))
        {
            return;
        }

        throw new ObjectDisposedException(
            nameof(LanguageServerProcessHarness),
            $"The language-server process harness is {_state.ToString().ToLowerInvariant()}.");
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
