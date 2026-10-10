using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using VbaDebugAdapter.Build;
using VbaDebugAdapter.Infrastructure;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class ManagedDebugPreparationProcessTests
{
    [Fact]
    public async Task ConfirmationAndBoundReadinessAreAnsweredBeforeTheChildCanComplete()
    {
        var binding = Binding();
        var child = new Child();
        child.ErrorLines.Add(Confirmation("a".PadLeft(32, 'a')));
        child.OnFrame = frame =>
        {
            if (frame == $"confirm:{new string('a', 32)}:yes\n")
                child.ErrorLines.Add(Ready(binding, new string('b', 32)));
            if (frame == $"prepare:{new string('b', 32)}:ready\n") child.Complete(0);
        };
        var transport = Transport(child);
        var confirmationSeen = false;
        var readinessSeen = false;

        var result = await transport.RunAsync(Path.GetFullPath("vba-dev.exe"), ["prepare-debug"], binding,
            (message, _) =>
            {
                Assert.Equal("Replace VBA code?", message);
                confirmationSeen = true;
                return Task.FromResult(true);
            }, _ =>
            {
                Assert.Contains($"confirm:{new string('a', 32)}:yes\n", child.Frames);
                readinessSeen = true;
                return Task.FromResult(true);
            }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(confirmationSeen);
        Assert.True(readinessSeen);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("prepared receipt", result.StandardOutput);
        Assert.False(result.CleanupOutcome.HasUnprovedRelease);
        Assert.True(child.Released);
    }

    [Fact]
    public async Task DuplicateReadinessWhileThePlanWaitsCannotAuthorizeReplacement()
    {
        var binding = Binding();
        var child = new Child();
        var nonce = new string('b', 32);
        child.ErrorLines.Add(Ready(binding, nonce));
        child.OnFrame = frame =>
        {
            if (frame == "cancel\n")
            {
                child.ErrorLines.Add("Original modules restored; no workbook was saved.");
                child.Complete(130);
            }
        };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = Transport(child).RunAsync(Path.GetFullPath("vba-dev.exe"), ["prepare-debug"], binding,
            (_, _) => Task.FromResult(true), token =>
            {
                entered.TrySetResult();
                return decision.Task.WaitAsync(token);
            }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        child.ErrorLines.Add(Ready(binding, nonce));

        var failure = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => run.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.IsAssignableFrom<IDebugFailureEvidence>(failure);
        Assert.Equal(new[] { "cancel\n" }, child.Frames);
        Assert.True(child.Released);
        decision.TrySetResult(true);
        Assert.DoesNotContain($"prepare:{nonce}:ready\n", child.Frames);
    }

    [Fact]
    public async Task MalformedReadinessNonceIsRejectedBeforeCallingThePlan()
    {
        var binding = Binding();
        var child = new Child();
        child.ErrorLines.Add(Ready(binding, "short"));
        child.OnFrame = frame => child.Complete(frame == "cancel\n" ? 1 : 0);
        var callbackInvoked = false;

        var failure = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Transport(child).RunAsync(
            Path.GetFullPath("vba-dev.exe"), ["prepare-debug"], binding, (_, _) => Task.FromResult(true),
            _ => { callbackInvoked = true; return Task.FromResult(true); }, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3)));

        Assert.False(callbackInvoked);
        Assert.IsAssignableFrom<IDebugFailureEvidence>(failure);
        Assert.Equal(new[] { "cancel\n" }, child.Frames);
        Assert.True(child.Released);
    }

    [Fact]
    public async Task CancellationDeadlineRetainsTheUnprovedChildUntilItActuallyCompletes()
    {
        var binding = Binding();
        var child = new Child();
        child.ErrorLines.Add(Ready(binding, new string('b', 32)));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var run = Transport(child, TimeSpan.FromMilliseconds(80)).RunAsync(
            Path.GetFullPath("vba-dev.exe"), ["prepare-debug"], binding, (_, _) => Task.FromResult(true),
            token => { entered.TrySetResult(); return decision.Task.WaitAsync(token); }, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();

        var failure = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => run.WaitAsync(TimeSpan.FromSeconds(3)));
        var retained = Assert.IsAssignableFrom<IDebugFailureEvidence>(failure).FailureOutcome;
        Assert.True(retained.HasUnprovedRelease);
        Assert.False(child.Released);
        Assert.Equal(new[] { "cancel\n" }, child.Frames);

        child.Complete(130);
        await child.ReleasedSignal.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(child.Released);
        Assert.True(retained.HasUnprovedRelease);
    }

    [Fact]
    public async Task UnsupportedReadinessSchemaIsRejectedBeforeCallingThePlan()
    {
        var binding = Binding();
        var child = new Child();
        child.ErrorLines.Add(Ready(binding, new string('b', 32)).Replace("\"1.0\"", "\"2.0\"", StringComparison.Ordinal));
        child.OnFrame = frame => child.Complete(frame == "cancel\n" ? 1 : 0);
        var callbackInvoked = false;

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Transport(child).RunAsync(
            Path.GetFullPath("vba-dev.exe"), ["prepare-debug"], binding, (_, _) => Task.FromResult(true),
            _ => { callbackInvoked = true; return Task.FromResult(true); }, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3)));

        Assert.False(callbackInvoked);
        Assert.Equal(new[] { "cancel\n" }, child.Frames);
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("duplicate")]
    [InlineData("escapedDuplicate")]
    [InlineData("missingType")]
    public async Task ReadinessControlHasAClosedUnambiguousFieldSet(string invalidity)
    {
        var binding = Binding();
        var child = new Child();
        var nonce = new string('b', 32);
        var record = Ready(binding, nonce);
        record = invalidity switch
        {
            "extra" => record[..^1] + ",\"unexpected\":true}",
            "duplicate" => record[..^1] + $",\"requestId\":\"{nonce}\"}}",
            "escapedDuplicate" => record[..^1] + $",\"request\\u0049d\":\"{nonce}\"}}",
            "missingType" => record.Replace("\"type\":\"debugPreparationReady\",", "", StringComparison.Ordinal),
            _ => throw new ArgumentException(invalidity)
        };
        child.ErrorLines.Add(record);
        child.OnFrame = frame => child.Complete(frame == "cancel\n" ? 1 : 0);
        var callbackInvoked = false;

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Transport(child).RunAsync(
            Path.GetFullPath("vba-dev.exe"), ["prepare-debug"], binding, (_, _) => Task.FromResult(true),
            _ => { callbackInvoked = true; return Task.FromResult(true); }, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3)));

        Assert.False(callbackInvoked);
        Assert.Equal(new[] { "cancel\n" }, child.Frames);
    }

    [Theory]
    [InlineData("reusedNonce")]
    [InlineData("beforeConsent")]
    [InlineData("afterRefusal")]
    public async Task ReadinessNeedsAFreshNonceAndCompletedPositiveConsent(string invalidity)
    {
        var binding = Binding();
        var child = new Child();
        var confirmationNonce = new string('a', 32);
        var readinessNonce = invalidity == "reusedNonce" ? confirmationNonce : new string('b', 32);
        child.ErrorLines.Add(Confirmation(confirmationNonce));
        child.OnFrame = frame =>
        {
            if (frame == "cancel\n") { child.Complete(1); }
            else if (frame.StartsWith("confirm:", StringComparison.Ordinal))
                child.ErrorLines.Add(Ready(binding, readinessNonce));
            else if (frame.StartsWith("prepare:", StringComparison.Ordinal)) { child.Complete(0); }
        };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCallbackInvoked = false;
        var run = Transport(child).RunAsync(Path.GetFullPath("vba-dev.exe"), ["prepare-debug"], binding,
            (_, token) =>
            {
                entered.TrySetResult();
                return invalidity == "beforeConsent" ? decision.Task.WaitAsync(token)
                    : Task.FromResult(invalidity != "afterRefusal");
            }, _ => { readyCallbackInvoked = true; return Task.FromResult(true); }, CancellationToken.None);
        if (invalidity == "beforeConsent")
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            child.ErrorLines.Add(Ready(binding, readinessNonce));
        }

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => run.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.False(readyCallbackInvoked);
        Assert.DoesNotContain(child.Frames, frame => frame.StartsWith("prepare:", StringComparison.Ordinal));
        Assert.Single(child.Frames, frame => frame == "cancel\n");
    }

    [Fact]
    public async Task AChildWithNoReadinessIsCancelledByASeparateActiveDeadline()
    {
        var child = new Child();
        child.OnFrame = frame =>
        {
            if (frame == "cancel\n")
            {
                child.ErrorLines.Add("Cooperative preparation cancellation completed.");
                child.Complete(130);
            }
        };
        var transport = new ManagedDebugPreparationProcess(new Factory(child), Path.GetFullPath,
            TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(80));

        var failure = await Assert.ThrowsAsync<ManagedDebugPreparationFailure>(() => transport.RunAsync(
            Path.GetFullPath("vba-dev.exe"), ["prepare-debug"], Binding(), (_, _) => Task.FromResult(true),
            _ => Task.FromResult(true), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)));

        Assert.Contains("active preparation deadline", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(130, failure.ObservedExitCode);
        Assert.Contains("Cooperative preparation cancellation completed", failure.Result.StandardError);
        Assert.False(failure.FailureOutcome.HasUnprovedRelease);
        Assert.Equal(new[] { "cancel\n" }, child.Frames);
        Assert.True(child.Released);
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("pid")]
    [InlineData("ticks")]
    [InlineData("relativeWorkbook")]
    [InlineData("relativeExecutable")]
    public async Task InvalidInvocationBindingCannotAcquireAChild(string invalidity)
    {
        var child = new Child();
        child.Complete(0);
        var factory = new Factory(child);
        var binding = Binding();
        var executable = Path.GetFullPath("vba-dev.exe");
        binding = invalidity switch
        {
            "generation" => binding with { GenerationId = new string('A', 32) },
            "pid" => binding with { ExcelProcessId = 0 },
            "ticks" => binding with { ExcelProcessStartUtcTicks = 0 },
            "relativeWorkbook" => binding with { WorkbookPath = "Book1.xlsm" },
            "relativeExecutable" => binding,
            _ => throw new ArgumentException(invalidity)
        };
        if (invalidity == "relativeExecutable") { executable = "vba-dev.exe"; }

        var failure = await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            new ManagedDebugPreparationProcess(factory, Path.GetFullPath).RunAsync(executable,
                ["prepare-debug"], binding, (_, _) => Task.FromResult(true), _ => Task.FromResult(true),
                CancellationToken.None));

        Assert.Equal(0, factory.StartCount);
        var evidence = Assert.IsAssignableFrom<IDebugFailureEvidence>(failure).FailureOutcome;
        Assert.False(evidence.HasUnprovedRelease);
        Assert.Contains(evidence.Evidence, item => item.Kind == DebugResourceKind.Process && item.Released);
        Assert.Contains(evidence.Evidence, item => item.Kind == DebugResourceKind.Handle && item.Released);
    }

    [Fact]
    public async Task ChildExitDuringPendingReadinessRejectsTheLatePlanDecision()
    {
        var child = new Child();
        var binding = Binding();
        var nonce = new string('b', 32);
        child.ErrorLines.Add(Ready(binding, nonce));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = Transport(child).RunAsync(Path.GetFullPath("vba-dev.exe"), ["prepare-debug"], binding,
            (_, _) => Task.FromResult(true), _ => { entered.TrySetResult(); return decision.Task; }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        child.Complete(2);

        var failure = await Assert.ThrowsAsync<ManagedDebugPreparationFailure>(() => run.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(2, failure.ObservedExitCode);
        Assert.False(failure.FailureOutcome.HasUnprovedRelease);
        Assert.True(child.Released);
        decision.TrySetResult(true);
        Assert.DoesNotContain($"prepare:{nonce}:ready\n", child.Frames);
    }

    [Fact]
    public async Task FaultingCallbackCancellationCannotInterruptCooperativeChildCleanup()
    {
        var binding = Binding();
        var child = new Child();
        child.ErrorLines.Add(Ready(binding, new string('b', 32)));
        child.OnFrame = frame => { if (frame == "cancel\n") { child.Complete(130); } };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = new InvalidOperationException("The plan cancellation callback failed.");
        using var cancellation = new CancellationTokenSource();
        var run = Transport(child).RunAsync(Path.GetFullPath("vba-dev.exe"), ["prepare-debug"], binding,
            (_, _) => Task.FromResult(true), token =>
            {
                token.Register(() => throw original);
                entered.TrySetResult();
                return decision.Task;
            }, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Null(Record.Exception(cancellation.Cancel));
        var failure = await Assert.ThrowsAsync<ManagedDebugPreparationFailure>(() => run.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Same(original, failure.FailureOutcome.PrimaryFailure);
        Assert.True(child.Released);
        Assert.Equal(new[] { "cancel\n" }, child.Frames);
    }

    [Fact]
    public async Task NativeChildReportsTerminalExitAndSeparateNativeHandleRelease()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var sourcePath = Path.Combine(Path.GetTempPath(), "vba-tools-prepare-transport-" + Guid.NewGuid().ToString("N") + ".xlsm");
        File.WriteAllText(sourcePath, "Only a transport identity fixture; Excel is not invoked.", new UTF8Encoding(false));
        try
        {
            var binding = Binding() with { WorkbookPath = sourcePath };
            var nonce = new string('b', 32);
            var record = Ready(binding, nonce).Replace("'", "''", StringComparison.Ordinal);
            var script = $"[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); " +
                $"[Console]::InputEncoding=[Text.UTF8Encoding]::new($false); " +
                $"[Console]::Error.WriteLine('{record}'); " +
                "$read=[Console]::In.ReadLineAsync(); if(-not $read.Wait(10000)){exit 9}; " +
                $"if($read.Result -ne 'prepare:{nonce}:ready'){{exit 2}}; " +
                "[Console]::Write('native prepared receipt'); exit 0";
            var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            var result = await new ManagedDebugPreparationProcess().RunAsync(executable,
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
                    Convert.ToBase64String(Encoding.Unicode.GetBytes(script))], binding,
                (_, _) => Task.FromResult(true), _ => Task.FromResult(true), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(25));

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("native prepared receipt", result.StandardOutput);
            Assert.False(result.CleanupOutcome.HasUnprovedRelease);
            Assert.Contains(result.CleanupOutcome.Evidence, item => item.Kind == DebugResourceKind.Process && item.Released);
            Assert.Equal(4, result.CleanupOutcome.Evidence.Count(item => item.Kind == DebugResourceKind.Handle && item.Released));
            Assert.DoesNotContain(result.CleanupOutcome.Evidence, item => item.ProcessId == binding.ExcelProcessId);
        }
        finally { File.Delete(sourcePath); }
    }

    [Fact]
    public async Task ReplacingTheWorkbookAtTheSamePathCannotAuthorizeTheCapturedPlan()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var path = Path.Combine(Path.GetTempPath(), "vba-tools-prepare-binding-" + Guid.NewGuid().ToString("N") + ".xlsm");
        var original = path + ".original";
        File.WriteAllText(path, "The original transport identity fixture.", new UTF8Encoding(false));
        try
        {
            var binding = Binding() with { WorkbookPath = path };
            var child = new Child();
            child.OnFrame = frame => child.Complete(frame == "cancel\n" ? 1 : 0);
            var callbackInvoked = false;
            var factory = new Factory(child, () =>
            {
                // Keep the original physical file alive so NTFS cannot recycle its file ID.
                File.Move(path, original);
                File.WriteAllText(path, "A different workbook at the same path.", new UTF8Encoding(false));
                child.ErrorLines.Add(Ready(binding, new string('b', 32)));
            });

            var failure = await Assert.ThrowsAsync<ManagedDebugPreparationFailure>(() =>
                new ManagedDebugPreparationProcess(factory).RunAsync(Path.GetFullPath("vba-dev.exe"),
                    ["prepare-debug"], binding, (_, _) => Task.FromResult(true),
                    _ => { callbackInvoked = true; return Task.FromResult(true); }, CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(3)));

            Assert.False(callbackInvoked);
            Assert.False(failure.FailureOutcome.HasUnprovedRelease);
            Assert.Equal(new[] { "cancel\n" }, child.Frames);
        }
        finally
        {
            File.Delete(path);
            File.Delete(original);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessRequiresReleaseEvidenceForTheExactCompanionOwner(bool unrelatedEvidence)
    {
        var binding = Binding();
        var child = new Child();
        child.ErrorLines.Add(Ready(binding, new string('b', 32)));
        child.OnFrame = _ => child.Complete(0);
        var unrelated = new DebugFailureCompletion();
        if (unrelatedEvidence)
        {
            unrelated.AddEvidence(new("unrelated-process", "Excel", DebugResourceKind.Process, true,
                "This does not prove companion release.", binding.ExcelProcessId));
            unrelated.AddEvidence(new("unrelated-handle", "Excel", DebugResourceKind.Handle, true,
                "This does not prove companion release.", binding.ExcelProcessId));
        }
        child.ReleaseOutcomeOverride = unrelated.Complete();

        var failure = await Assert.ThrowsAsync<ManagedDebugPreparationFailure>(() => Transport(child).RunAsync(
            Path.GetFullPath("vba-dev.exe"), ["prepare-debug"], binding,
            (_, _) => Task.FromResult(true), _ => Task.FromResult(true), CancellationToken.None));

        Assert.Equal(0, failure.Result.ExitCode);
        Assert.True(failure.FailureOutcome.HasUnprovedRelease);
        Assert.Contains(failure.FailureOutcome.Evidence, item => item.Kind == DebugResourceKind.Handle
            && item.ProcessId == child.ProcessId && !item.Released);
    }

    [Fact]
    public async Task BlockingCallbackCancellationCannotBlockTheCallerOrPretendCleanupCompleted()
    {
        var binding = Binding();
        var child = new Child();
        child.ErrorLines.Add(Ready(binding, new string('b', 32)));
        child.OnFrame = frame => { if (frame == "cancel\n") child.Complete(130); };
        using var blocked = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellingCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = Transport(child, TimeSpan.FromMilliseconds(80)).RunAsync(
            Path.GetFullPath("vba-dev.exe"), ["prepare-debug"], binding, (_, _) => Task.FromResult(true), token =>
            {
                token.Register(() => { cancellingCallback.TrySetResult(); blocked.Wait(); });
                entered.TrySetResult();
                return decision.Task;
            }, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var requestCancellation = Task.Run(cancellation.Cancel);
        try
        {
            await cancellingCallback.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await requestCancellation.WaitAsync(TimeSpan.FromMilliseconds(250));
            var failure = await Assert.ThrowsAsync<ManagedDebugPreparationFailure>(() => run.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.True(failure.FailureOutcome.HasUnprovedRelease);
            Assert.False(child.Released);
            Assert.Equal(new[] { "cancel\n" }, child.Frames);
        }
        finally
        {
            blocked.Set();
            await requestCancellation.WaitAsync(TimeSpan.FromSeconds(3));
            await child.ReleasedSignal.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private static DebugPreparationProcessBinding Binding()
        => new(Guid.NewGuid().ToString("N"), Path.GetFullPath("Book1.xlsm"), 123, DateTime.UtcNow.Ticks);

    private static ManagedDebugPreparationProcess Transport(Child child, TimeSpan? budget = null)
        => new(new Factory(child), Path.GetFullPath, budget);

    private static string Confirmation(string nonce)
        => JsonSerializer.Serialize(new { type = "workbookConfirmation", schemaVersion = "1.0",
            requestId = nonce, message = "Replace VBA code?" });

    private static string Ready(DebugPreparationProcessBinding binding, string nonce)
        => JsonSerializer.Serialize(new { type = "debugPreparationReady", schemaVersion = "1.0",
            requestId = nonce, generationId = binding.GenerationId, workbookPath = binding.WorkbookPath,
            excelProcessId = binding.ExcelProcessId, excelProcessStartUtcTicks = binding.ExcelProcessStartUtcTicks });

    private sealed class Factory(Child child, Action? onStart = null) : IDebugPreparationChildProcessFactory
    {
        public int StartCount { get; private set; }
        public IDebugPreparationChildProcess Start(string executable, IReadOnlyList<string> arguments)
        {
            StartCount++;
            onStart?.Invoke();
            return child;
        }
    }

    private sealed class Child : IDebugPreparationChildProcess
    {
        private readonly TaskCompletionSource exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Lines ErrorLines { get; } = new();
        public List<string> Frames { get; } = [];
        public Action<string>? OnFrame { get; set; }
        public DebugFailureOutcome? ReleaseOutcomeOverride { get; set; }
        public bool Released { get; private set; }
        public TaskCompletionSource ReleasedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ProcessId => 987;
        public bool HasExited => exit.Task.IsCompletedSuccessfully;
        public int ExitCode { get; private set; }
        public TextReader StandardOutput { get; } = new StringReader("prepared receipt");
        public TextReader StandardError => ErrorLines;
        public Task WriteFrameAsync(string frame)
        {
            Frames.Add(frame);
            OnFrame?.Invoke(frame);
            return Task.CompletedTask;
        }
        public Task WaitForExitAsync() => exit.Task;
        public void Complete(int exitCode)
        {
            ExitCode = exitCode;
            ErrorLines.Complete();
            exit.TrySetResult();
        }
        public DebugFailureOutcome ReleaseAfterTerminalCompletion()
        {
            Assert.True(HasExited);
            Released = true;
            ReleasedSignal.TrySetResult();
            if (ReleaseOutcomeOverride is not null) { return ReleaseOutcomeOverride; }
            var completion = new DebugFailureCompletion();
            completion.AddEvidence(new("preparation-child-exit", "vba-dev", DebugResourceKind.Process,
                true, "The fake child terminal exit was observed.", ProcessId));
            completion.AddEvidence(new("preparation-child-handles", "vba-dev", DebugResourceKind.Handle,
                true, "The fake child owner reports native handles released.", ProcessId));
            return completion.Complete();
        }
    }

    private sealed class Lines : TextReader
    {
        private readonly Channel<string> lines = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions { AllowSynchronousContinuations = false, SingleReader = true });
        public void Add(string line) => Assert.True(lines.Writer.TryWrite(line));
        public void Complete() => lines.Writer.TryComplete();
        public override async Task<string?> ReadLineAsync()
        {
            while (await lines.Reader.WaitToReadAsync())
                if (lines.Reader.TryRead(out var line)) return line;
            return null;
        }
    }
}
