using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using VbaDebugAdapter.Infrastructure;

namespace VbaDebugAdapter.Build;

internal sealed record DebugPreparationProcessBinding(
    string GenerationId, string WorkbookPath, int ExcelProcessId, long ExcelProcessStartUtcTicks);

internal sealed record ManagedDebugPreparationProcessResult(
    int ExitCode, string StandardOutput, string StandardError, DebugFailureOutcome CleanupOutcome);

internal interface IManagedDebugPreparationProcess
{
    Task<ManagedDebugPreparationProcessResult> RunAsync(
        string executable, IReadOnlyList<string> arguments, DebugPreparationProcessBinding binding,
        Func<string, CancellationToken, Task<bool>> confirmReplacement,
        Func<CancellationToken, Task<bool>> continueAfterCapture,
        CancellationToken cancellationToken);
}

internal interface IDebugPreparationChildProcessFactory
{
    IDebugPreparationChildProcess Start(string executable, IReadOnlyList<string> arguments);
}

internal interface IDebugPreparationChildProcess
{
    int ProcessId { get; }
    bool HasExited { get; }
    int ExitCode { get; }
    TextReader StandardOutput { get; }
    TextReader StandardError { get; }
    Task WriteFrameAsync(string frame);
    Task WaitForExitAsync();
    DebugFailureOutcome ReleaseAfterTerminalCompletion();
}

internal sealed class ManagedDebugPreparationProcess : IManagedDebugPreparationProcess
{
    private readonly IDebugPreparationChildProcessFactory factory;
    private readonly Func<string, string> canonicalizeWorkbookPath;
    private readonly TimeSpan cleanupBudget;
    private readonly TimeSpan activeBudget;

    internal ManagedDebugPreparationProcess() : this(new SystemDebugPreparationChildProcessFactory()) { }

    internal ManagedDebugPreparationProcess(IDebugPreparationChildProcessFactory factory,
        Func<string, string>? canonicalizeWorkbookPath = null, TimeSpan? cleanupBudgetOverride = null,
        TimeSpan? activeBudgetOverride = null)
    {
        this.factory = factory;
        this.canonicalizeWorkbookPath = canonicalizeWorkbookPath ?? ReadPhysicalWorkbookIdentity;
        cleanupBudget = cleanupBudgetOverride ?? TimeSpan.FromMinutes(12);
        activeBudget = activeBudgetOverride ?? TimeSpan.FromMinutes(12);
    }

    public async Task<ManagedDebugPreparationProcessResult> RunAsync(
        string executable, IReadOnlyList<string> arguments, DebugPreparationProcessBinding binding,
        Func<string, CancellationToken, Task<bool>> confirmReplacement,
        Func<CancellationToken, Task<bool>> continueAfterCapture,
        CancellationToken cancellationToken)
    {
        string expectedIdentity;
        try
        {
            ArgumentNullException.ThrowIfNull(binding);
            ArgumentNullException.ThrowIfNull(arguments);
            ArgumentNullException.ThrowIfNull(confirmReplacement);
            ArgumentNullException.ThrowIfNull(continueAfterCapture);
            if (!IsLowerHex32(binding.GenerationId) || binding.ExcelProcessId <= 0
                || binding.ExcelProcessStartUtcTicks <= 0 || binding.ExcelProcessStartUtcTicks > DateTime.MaxValue.Ticks
                || !Path.IsPathFullyQualified(binding.WorkbookPath) || !Path.IsPathFullyQualified(executable))
                throw new InvalidOperationException("Debug preparation requires an exact generation, workbook, process, " +
                    "UTC start-time binding and absolute executable path.");
            cancellationToken.ThrowIfCancellationRequested();
            expectedIdentity = canonicalizeWorkbookPath(binding.WorkbookPath);
        }
        catch (Exception failure)
        {
            var completion = new DebugFailureCompletion(failure);
            completion.AddEvidence(new("preparation-child-acquisition", "vba-dev", DebugResourceKind.Process,
                true, "The invocation was rejected before acquiring a child process."));
            completion.AddEvidence(new("preparation-child-acquisition", "vba-dev", DebugResourceKind.Handle,
                true, "The invocation was rejected before acquiring child handles."));
            var outcome = completion.Complete();
            throw new ManagedDebugPreparationFailure(new(-1, "", "", outcome), outcome, failure);
        }
        IDebugPreparationChildProcess child;
        try { child = factory.Start(executable, arguments); }
        catch (Exception failure)
        {
            var completion = new DebugFailureCompletion(failure);
            if (failure is not IDebugFailureEvidence)
            {
                completion.AddEvidence(new("preparation-child-acquisition", "vba-dev", DebugResourceKind.Process,
                    false, "The child factory failed without proving whether a child was acquired."));
                completion.AddEvidence(new("preparation-child-acquisition", "vba-dev", DebugResourceKind.Handle,
                    false, "The child factory failed without native handle-release proof."));
            }
            var outcome = completion.Complete();
            throw new ManagedDebugPreparationFailure(new(-1, "", "", outcome), outcome, failure);
        }
        return await new Invocation(child, binding, expectedIdentity, canonicalizeWorkbookPath, cleanupBudget, activeBudget,
            confirmReplacement, continueAfterCapture).RunAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool IsLowerHex32(string? value)
        => value is { Length: 32 } && value.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static string ReadPhysicalWorkbookIdentity(string path)
    {
        var identity = new WindowsSourceVbePhysicalIdentityReader().Read(path);
        return $"{identity.VolumeSerialNumber:X8}:{identity.FileIndex:X16}";
    }

    private sealed class Invocation(
        IDebugPreparationChildProcess child, DebugPreparationProcessBinding binding, string expectedIdentity,
        Func<string, string> canonicalizePath, TimeSpan cleanupBudget, TimeSpan activeBudget,
        Func<string, CancellationToken, Task<bool>> confirm,
        Func<CancellationToken, Task<bool>> continueAfterCapture)
    {
        private readonly object gate = new();
        private readonly StringBuilder output = new();
        private readonly StringBuilder error = new();
        private readonly List<Task> callbacks = [];
        private readonly HashSet<string> nonces = new(StringComparer.Ordinal);
        private readonly CancellationTokenSource stoppedToken = new();
        private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim writer = new(1, 1);
        private Exception? primaryFailure;
        private volatile bool stopRequested;
        private bool readySeen;
        private volatile bool exitObserved;
        private Task? confirmation;
        private Task cancellationCallbacks = Task.CompletedTask;
        private volatile bool positiveConfirmationFrameStarted;
        private volatile bool awaitingResponse;

        public async Task<ManagedDebugPreparationProcessResult> RunAsync(CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(() => Stop(null));
            using var activeTimer = new CancellationTokenSource();
            var activeDeadline = Task.Delay(activeBudget, activeTimer.Token);
            var terminal = ObserveTerminalAsync();
            var first = await Task.WhenAny(terminal, stopped.Task, activeDeadline).ConfigureAwait(false);
            activeTimer.Cancel();
            if (first == activeDeadline)
                Stop(new TimeoutException($"The active preparation deadline ({activeBudget}) expired; " +
                    "cooperative cancellation must complete before the source generation can be released."));
            if (first != terminal || stopRequested)
            {
                var cleanup = CancelAndWaitAsync(terminal);
                try { await cleanup.WaitAsync(cleanupBudget).ConfigureAwait(false); }
                catch (TimeoutException timeout)
                {
                    var deadlineCompletion = new DebugFailureCompletion(primaryFailure ?? timeout);
                    if (primaryFailure is not null)
                        deadlineCompletion.AddFailure("preparation-completion-deadline", "vba-dev",
                            DebugResourceKind.Observation, timeout, child.ProcessId);
                    RecordUnproved(deadlineCompletion, "The cooperative completion deadline expired; " +
                        "the child and its pending reads remain retained, and the generation workspace must be kept.");
                    _ = ReleaseWhenTerminalSettlesAsync(cleanup);
                    throw Failure(deadlineCompletion.Complete());
                }
            }
            await terminal.ConfigureAwait(false);
            var completion = new DebugFailureCompletion(primaryFailure);
            if (!exitObserved)
            {
                RecordUnproved(completion, "The invocation did not observe an authoritative terminal child wait.");
                throw Failure(completion.Complete());
            }
            try
            {
                var owner = child.ReleaseAfterTerminalCompletion();
                completion.Merge(owner);
                foreach (var kind in new[] { DebugResourceKind.Process, DebugResourceKind.Handle })
                    if (!owner.Evidence.Any(item => item.Kind == kind && item.ProcessId == child.ProcessId && item.Released))
                        completion.AddEvidence(new("preparation-child-owner-proof", "vba-dev", kind, false,
                            "The exact companion owner did not provide positive terminal/native release evidence.", child.ProcessId));
            }
            catch (Exception failure)
            {
                completion.AddFailure("preparation-child-handles", "vba-dev", DebugResourceKind.Handle,
                    failure, child.ProcessId);
                if (failure is not IDebugFailureEvidence)
                    completion.AddEvidence(new("preparation-child-handles", "vba-dev", DebugResourceKind.Handle,
                        false, "The child owner failed without native handle-release proof.", child.ProcessId));
            }
            var outcome = completion.Complete();
            var result = new ManagedDebugPreparationProcessResult(child.ExitCode,
                Text(output), Text(error), outcome);
            if (primaryFailure is not null || outcome.HasCleanupFailure)
                throw new ManagedDebugPreparationFailure(result, outcome, this);
            return result;
        }

        private async Task CancelAndWaitAsync(Task terminal)
        {
            await SendCancellationAsync().ConfigureAwait(false);
            await terminal.ConfigureAwait(false);
            await cancellationCallbacks.ConfigureAwait(false);
        }

        private async Task ReleaseWhenTerminalSettlesAsync(Task terminal)
        {
            // The pending task retains this exact owner; no Job closure or process kill is used.
            try
            {
                await terminal.ConfigureAwait(false);
                if (exitObserved) { child.ReleaseAfterTerminalCompletion(); }
            }
            catch { /* The already-returned failure retains unproved release, never a late success. */ }
        }

        private void RecordUnproved(DebugFailureCompletion completion, string reason)
        {
            completion.AddEvidence(new("preparation-child-exit", "vba-dev", DebugResourceKind.Process,
                exitObserved, reason, child.ProcessId));
            completion.AddEvidence(new("preparation-child-handles", "vba-dev", DebugResourceKind.Handle,
                false, reason, child.ProcessId));
        }

        private ManagedDebugPreparationFailure Failure(DebugFailureOutcome outcome)
            => new(new(exitObserved ? child.ExitCode : -1, Text(output), Text(error), outcome), outcome, this);

        private async Task ObserveTerminalAsync()
        {
            var exit = ObserveExitAsync();
            var stdout = ReadOutputAsync();
            var stderr = ReadErrorAsync();
            await Task.WhenAll(exit, stdout, stderr).ConfigureAwait(false);
            Task[] pending;
            lock (gate) { pending = callbacks.ToArray(); }
            await Task.WhenAll(pending).ConfigureAwait(false);
            await cancellationCallbacks.ConfigureAwait(false);
        }

        private async Task ObserveExitAsync()
        {
            try
            {
                await child.WaitForExitAsync().ConfigureAwait(false);
                exitObserved = true;
                if (awaitingResponse && !stopRequested)
                    Stop(new InvalidOperationException("The preparation child exited before its pending control response."));
            }
            catch (Exception failure) { Stop(failure); }
        }

        private async Task ReadOutputAsync()
        {
            try
            {
                var buffer = new char[4096];
                int count;
                while ((count = await child.StandardOutput.ReadAsync(buffer, 0, buffer.Length)
                    .ConfigureAwait(false)) != 0)
                    lock (gate) { output.Append(buffer, 0, count); }
            }
            catch (Exception failure) { Stop(failure); }
        }

        private async Task ReadErrorAsync()
        {
            try
            {
                while (await child.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    lock (gate) { error.AppendLine(line); }
                    if (stopRequested || !line.TrimStart().StartsWith('{')) { continue; }
                    try { HandleRecord(line); }
                    catch (Exception failure) { Stop(failure); }
                }
                if (awaitingResponse && !stopRequested)
                    Stop(new EndOfStreamException("The preparation control stream ended before its pending response."));
            }
            catch (Exception failure) { Stop(failure); }
        }

        private void HandleRecord(string line)
        {
            using var document = JsonDocument.Parse(line);
            var record = document.RootElement;
            if (record.ValueKind != JsonValueKind.Object) { return; }
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in record.EnumerateObject())
                if (!names.Add(property.Name))
                    throw new InvalidOperationException("Duplicate debug preparation control property: " + property.Name);
            if (!record.TryGetProperty("type", out var type))
            {
                if (names.Contains("requestId") || names.Contains("schemaVersion") || names.Contains("generationId"))
                    throw new InvalidOperationException("Debug preparation control record is missing its type.");
                return;
            }
            if (type.GetString() == "workbookConfirmation")
            {
                if (child.HasExited) { throw new InvalidOperationException("A terminated child cannot request workbook consent."); }
                RequireFields(names, ["type", "schemaVersion", "requestId", "message"]);
                RequireSchema(record);
                if (confirmation is not null || readySeen)
                    throw new InvalidOperationException("Duplicate or out-of-order workbook confirmation.");
                var nonce = AdmitNonce(record);
                var message = record.GetProperty("message").GetString()!;
                if (string.IsNullOrWhiteSpace(message))
                    throw new InvalidOperationException("Workbook confirmation message must not be empty.");
                confirmation = RespondAsync("confirm", nonce,
                    token => confirm(message, token));
                callbacks.Add(confirmation);
            }
            else if (type.GetString() == "debugPreparationReady")
            {
                if (child.HasExited) { throw new InvalidOperationException("A terminated child cannot authorize debug preparation."); }
                RequireFields(names, ["type", "schemaVersion", "requestId", "generationId", "workbookPath",
                    "excelProcessId", "excelProcessStartUtcTicks"]);
                RequireSchema(record);
                if (readySeen) { throw new InvalidOperationException("Duplicate debug preparation readiness."); }
                if (confirmation is not null && !positiveConfirmationFrameStarted)
                    throw new InvalidOperationException("Debug preparation readiness arrived before positive workbook consent.");
                readySeen = true;
                if (record.GetProperty("generationId").GetString() != binding.GenerationId
                    || record.GetProperty("excelProcessId").GetInt32() != binding.ExcelProcessId
                    || record.GetProperty("excelProcessStartUtcTicks").GetInt64() != binding.ExcelProcessStartUtcTicks
                    || !StringComparer.OrdinalIgnoreCase.Equals(
                        canonicalizePath(record.GetProperty("workbookPath").GetString()!),
                        expectedIdentity))
                    throw new InvalidOperationException("Debug preparation readiness does not match the selected workbook process.");
                callbacks.Add(RespondAsync("prepare", AdmitNonce(record),
                    continueAfterCapture, confirmation));
            }
        }

        private static void RequireFields(HashSet<string> actual, string[] expected)
        {
            if (!actual.SetEquals(expected))
                throw new InvalidOperationException("Debug preparation control record has missing or unexpected fields.");
        }

        private static void RequireSchema(JsonElement record)
        {
            if (record.GetProperty("schemaVersion").GetString() != "1.0")
                throw new InvalidOperationException("Unsupported debug preparation control schemaVersion; expected 1.0.");
        }

        private string AdmitNonce(JsonElement record)
        {
            var nonce = record.GetProperty("requestId").GetString();
            if (!IsLowerHex32(nonce))
                throw new InvalidOperationException("Debug preparation requestId must be 32 lowercase hexadecimal characters.");
            if (!nonces.Add(nonce!))
                throw new InvalidOperationException("Debug preparation requestId was already used by this invocation.");
            return nonce!;
        }

        private async Task RespondAsync(string kind, string nonce,
            Func<CancellationToken, Task<bool>> callback, Task? prior = null)
        {
            try
            {
                awaitingResponse = true;
                if (prior is not null) { await prior.ConfigureAwait(false); }
                stoppedToken.Token.ThrowIfCancellationRequested();
                var accepted = await callback(stoppedToken.Token).WaitAsync(stoppedToken.Token).ConfigureAwait(false);
                await writer.WaitAsync(stoppedToken.Token).ConfigureAwait(false);
                try
                {
                    stoppedToken.Token.ThrowIfCancellationRequested();
                    if (child.HasExited)
                        throw new InvalidOperationException("The preparation child exited before control response transmission.");
                    var decision = kind == "confirm" ? (accepted ? "yes" : "no")
                        : (accepted ? "ready" : "declined");
                    if (kind == "confirm" && accepted) { positiveConfirmationFrameStarted = true; }
                    awaitingResponse = false;
                    await child.WriteFrameAsync($"{kind}:{nonce}:{decision}\n").ConfigureAwait(false);
                }
                finally { writer.Release(); }
            }
            catch (OperationCanceledException) when (stopRequested) { }
            catch (Exception failure) { Stop(failure); }
        }

        private void Stop(Exception? failure)
        {
            lock (gate)
            {
                primaryFailure ??= failure;
                if (stopRequested) { return; }
                stopRequested = true;
                // Foreign callback registrations must never block the caller's cancellation
                // request or the stderr control pump. Their completion still belongs to cleanup.
                cancellationCallbacks = ObserveCancellationCallbacksAsync(stoppedToken.CancelAsync());
            }
            stopped.TrySetResult();
        }

        private async Task ObserveCancellationCallbacksAsync(Task callbacksCompletion)
        {
            try { await callbacksCompletion.ConfigureAwait(false); }
            catch (Exception callbackFailure)
            {
                var causal = callbackFailure is AggregateException aggregate
                    && aggregate.Flatten().InnerExceptions.Count == 1
                    ? aggregate.Flatten().InnerExceptions[0] : callbackFailure;
                lock (gate) { primaryFailure ??= causal; }
            }
        }

        private async Task SendCancellationAsync()
        {
            await writer.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!child.HasExited) { await child.WriteFrameAsync("cancel\n").ConfigureAwait(false); }
            }
            catch (Exception failure) { lock (gate) { primaryFailure ??= failure; } }
            finally { writer.Release(); }
        }

        private string Text(StringBuilder builder) { lock (gate) { return builder.ToString(); } }
    }
}

internal sealed class ManagedDebugPreparationFailure(
    ManagedDebugPreparationProcessResult result, DebugFailureOutcome outcome, object? retainedOwner = null)
    : InvalidOperationException(outcome.Describe(), outcome.PrimaryFailure), IDebugFailureEvidence
{
    public ManagedDebugPreparationProcessResult Result { get; } = result;
    public DebugFailureOutcome FailureOutcome { get; } = outcome;
    internal object? RetainedOwner { get; } = retainedOwner;
    public int? ObservedExitCode => FailureOutcome.Evidence.Any(item => item.Kind == DebugResourceKind.Process && !item.Released)
        || FailureOutcome.Evidence.Any(item => item.Stage == "preparation-child-acquisition") ? null : Result.ExitCode;
}

internal sealed class SystemDebugPreparationChildProcessFactory : IDebugPreparationChildProcessFactory
{
    public IDebugPreparationChildProcess Start(string executable, IReadOnlyList<string> arguments)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false, true),
                StandardErrorEncoding = new UTF8Encoding(false, true)
            }
        };
        var startAttempted = false;
        try
        {
            foreach (var argument in arguments) { process.StartInfo.ArgumentList.Add(argument); }
            startAttempted = true;
            if (!process.Start()) { throw new InvalidOperationException("The preparation child was not started."); }
            return new SystemDebugPreparationChildProcess(process);
        }
        catch (Exception failure)
        {
            var completion = new DebugFailureCompletion(failure);
            completion.AddEvidence(new("preparation-child-acquisition", "vba-dev", DebugResourceKind.Process,
                !startAttempted, startAttempted ? "Process.Start failed without authoritative terminal acquisition evidence."
                    : "The arguments were rejected before process acquisition."));
            completion.AddEvidence(new("preparation-child-acquisition", "vba-dev", DebugResourceKind.Handle,
                !startAttempted, startAttempted ? "The launch owner cannot prove release of partially acquired handles."
                    : "No launch handles were acquired."));
            if (startAttempted) { _ = RetainFailedStartAsync(process); }
            else { process.Dispose(); }
            var outcome = completion.Complete();
            throw new ManagedDebugPreparationFailure(new(-1, "", "", outcome), outcome, process);
        }
    }

    private static async Task RetainFailedStartAsync(Process process)
    {
        // A partially started child, if available, gets only cooperative cancellation.
        // The returned failure remains unproved; later completion is not retroactive success.
        try
        {
            var child = new SystemDebugPreparationChildProcess(process);
            var output = child.StandardOutput.ReadToEndAsync();
            var error = child.StandardError.ReadToEndAsync();
            try { await child.WriteFrameAsync("cancel\n").ConfigureAwait(false); }
            catch { }
            await child.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            child.ReleaseAfterTerminalCompletion();
        }
        catch { /* The failure carrier keeps the exact ambiguous launch owner. */ }
    }
}

internal sealed class SystemDebugPreparationChildProcess : IDebugPreparationChildProcess
{
    private readonly Process process;
    private readonly StreamWriter input;
    private readonly Stream inputPipe;
    private readonly StreamReader output;
    private readonly StreamReader error;
    private readonly object gate = new();
    private volatile bool exitObserved;
    private int terminalExitCode;
    private DebugFailureOutcome? releaseOutcome;

    internal SystemDebugPreparationChildProcess(Process process)
    {
        this.process = process;
        ProcessId = process.Id;
        // Keep the pipe open when this writer flushes/disposes; native closure is proved separately.
        inputPipe = process.StandardInput.BaseStream;
        input = new StreamWriter(inputPipe, new UTF8Encoding(false), leaveOpen: true);
        output = process.StandardOutput;
        error = process.StandardError;
    }

    public int ProcessId { get; }
    public bool HasExited => exitObserved || process.HasExited;
    public int ExitCode => exitObserved ? terminalExitCode : process.ExitCode;
    public TextReader StandardOutput => output;
    public TextReader StandardError => error;

    public async Task WriteFrameAsync(string frame)
    {
        await input.WriteAsync(frame).ConfigureAwait(false);
        await input.FlushAsync().ConfigureAwait(false);
    }

    public async Task WaitForExitAsync()
    {
        await process.WaitForExitAsync().ConfigureAwait(false);
        terminalExitCode = process.ExitCode;
        exitObserved = true;
    }

    public DebugFailureOutcome ReleaseAfterTerminalCompletion()
    {
        lock (gate)
        {
            if (releaseOutcome is not null) { return releaseOutcome; }
            var completion = new DebugFailureCompletion();
            completion.AddEvidence(new("preparation-child-exit", "vba-dev", DebugResourceKind.Process,
                exitObserved, "The exact child owner completed its terminal exit wait.", ProcessId));
            if (!exitObserved)
            {
                completion.AddEvidence(new("preparation-child-handles", "vba-dev", DebugResourceKind.Handle,
                    false, "Native child handles are retained until terminal exit and both output drains settle.", ProcessId));
                return completion.Complete();
            }
            // The transport calls this only after all readers and response writes have settled.
            DisposeManaged(input, "preparation-input-writer", completion);
            ReleaseStream(inputPipe, "preparation-input-pipe", completion);
            ReleaseStream(output.BaseStream, "preparation-output-pipe", completion);
            ReleaseStream(error.BaseStream, "preparation-error-pipe", completion);
            ReleaseHandle(process.SafeHandle, "preparation-process-handle", completion);
            DisposeManaged(output, "preparation-output-reader", completion);
            DisposeManaged(error, "preparation-error-reader", completion);
            DisposeManaged(process, "preparation-process-object", completion);
            return releaseOutcome = completion.Complete();
        }
    }

    private void ReleaseStream(Stream stream, string stage, DebugFailureCompletion completion)
    {
        var handle = stream switch
        {
            FileStream file => (SafeHandle)file.SafeFileHandle,
            PipeStream pipe => pipe.SafePipeHandle,
            _ => null
        };
        if (handle is null)
            completion.AddEvidence(new(stage, "vba-dev", DebugResourceKind.Handle, false,
                "The redirected pipe did not expose a native owner handle.", ProcessId));
        else { ReleaseHandle(handle, stage, completion); }
        DisposeManaged(stream, stage + "-stream", completion);
    }

    private void ReleaseHandle(SafeHandle handle, string stage, DebugFailureCompletion completion)
    {
        var release = new DebugNativeHandleRelease(handle);
        try { release.Release(); }
        catch (Exception failure)
        {
            completion.AddFailure(stage, "vba-dev", DebugResourceKind.Handle, failure, ProcessId);
        }
        completion.AddEvidence(new(stage, "vba-dev", DebugResourceKind.Handle,
            release.IsVerified, "The exact child owner observed native CloseHandle, separately from terminal exit.", ProcessId));
    }

    private void DisposeManaged(IDisposable owned, string stage, DebugFailureCompletion completion)
    {
        try { owned.Dispose(); }
        catch (Exception failure)
        {
            completion.AddFailure(stage, "vba-dev", DebugResourceKind.Handle, failure, ProcessId);
        }
    }
}
