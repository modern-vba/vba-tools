using System.Text.Json;
using System.Threading.Channels;
using VbaDebugAdapter.Cli;
using VbaDebugAdapter.Debugging;
using VbaDebugAdapter.Infrastructure;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed partial class VbaDebugAdapterCliSurfaceTests
{
    [Theory]
    [InlineData("terminate")]
    [InlineData("disconnect")]
    public async Task ExplicitSourceStopReportsUnconfirmedResetBeforeEndingTheConnection(string command)
    {
        using var temp = TempDirectory.Create();
        await using var lease = await new VbaDebugSessionWorkspaceManager(temp.Path)
            .ClaimAsync(DebugSessionId.Parse("0123456789abcdef0123456789abcdef"), CancellationToken.None);
        var source = new ClosedSourceRunningSession
        {
            ResetFailure = new DebugSetupException("VBA Reset could not be confirmed; use Reset manually in Excel.")
        };
        var runner = new StandaloneVbaDebugAdapterStdioRunner(new RecordingDebugLaunchService(source));
        using var input = new ConfirmationDapInput();
        using var prefix = CreateDapInput(
            new { seq = 1, type = "request", command = "launch", arguments = CreateValidLaunchArguments() },
            new { seq = 2, type = "request", command = "configurationDone", arguments = new { } });
        input.Append(prefix.ToArray());
        using var output = new MemoryStream();
        var invocation = runner.RunAsync(Path.GetFullPath("vba-dev.exe"), lease,
            input, output, Stream.Null, CancellationToken.None);
        try
        {
            Assert.True(SpinWait.SpinUntil(
                () => ReadUtf8(output).Contains("\"request_seq\":1,\"success\":true", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5)));
            using var stop = CreateDapInput(new { seq = 3, type = "request", command, arguments = new { } });
            input.Append(stop.ToArray());
            try { _ = await invocation.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception failure) when (failure is IDebugFailureEvidence) { }

            var messages = ReadDapMessages(output);
            var response = Assert.Single(messages, item => item.TryGetProperty("request_seq", out var seq) && seq.GetInt32() == 3);
            Assert.False(response.GetProperty("success").GetBoolean());
            Assert.Contains("use Reset manually in Excel", response.GetProperty("message").GetString());
            Assert.Contains(messages, item => item.TryGetProperty("event", out var name) && name.GetString() == "output"
                && item.GetProperty("body").GetProperty("category").GetString() == "important"
                && item.GetProperty("body").GetProperty("output").GetString()!.Contains("use Reset manually in Excel", StringComparison.Ordinal));
            Assert.DoesNotContain(messages, item => item.TryGetProperty("event", out var name) && name.GetString() == "exited");
            Assert.Equal(1, source.ResetCalls);
            Assert.Equal(1, source.DisposeCalls);
        }
        finally { input.Complete(); }
    }

    [Fact]
    public async Task SourceObservationFailureReportsUnconfirmedResetWithoutClaimingProcessTermination()
    {
        using var temp = TempDirectory.Create();
        await using var lease = await new VbaDebugSessionWorkspaceManager(temp.Path)
            .ClaimAsync(DebugSessionId.Parse("0123456789abcdef0123456789abcdef"), CancellationToken.None);
        var source = new ClosedSourceRunningSession
        {
            ResetFailure = new DebugSetupException("VBA Reset could not be confirmed; use Reset manually in Excel.")
        };
        var runner = new StandaloneVbaDebugAdapterStdioRunner(new RecordingDebugLaunchService(source));
        using var input = new ConfirmationDapInput();
        using var prefix = CreateDapInput(
            new { seq = 1, type = "request", command = "launch", arguments = CreateValidLaunchArguments() },
            new { seq = 2, type = "request", command = "configurationDone", arguments = new { } });
        input.Append(prefix.ToArray());
        using var output = new MemoryStream();
        var invocation = runner.RunAsync(Path.GetFullPath("vba-dev.exe"), lease,
            input, output, Stream.Null, CancellationToken.None);
        try
        {
            Assert.True(SpinWait.SpinUntil(
                () => ReadUtf8(output).Contains("\"request_seq\":1,\"success\":true", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5)));
            source.FailObservation(new DebugSetupException("Source observer failed."));
            Assert.Equal(1, await invocation.WaitAsync(TimeSpan.FromSeconds(5)));

            var transcript = ReadUtf8(output);
            Assert.Contains("Source observer failed.", transcript);
            Assert.Contains("source Reset", transcript);
            Assert.Contains("Observation", transcript);
            Assert.Contains("use Reset manually in Excel", transcript);
            Assert.DoesNotContain("session termination", transcript);
            Assert.DoesNotContain("owned Excel session", transcript);
            Assert.DoesNotContain(ReadDapMessages(output), item =>
                item.TryGetProperty("event", out var name) && name.GetString() == "exited");
            Assert.Equal(1, source.ResetCalls);
        }
        finally { input.Complete(); }
    }

    [Fact]
    public void SourceWorkbookLaunchMetadataIsAdmittedAsAnExactOptionalPath()
    {
        var arguments = CreateValidLaunchArguments();
        var workbookPath = Path.GetFullPath(Path.Combine("Project", "src", "Book", "Book.xlsm"));
        arguments["__vbaDebugSourceWorkbookPath"] = workbookPath;

        var launch = DebugRequestAdmission.AdmitLaunch(JsonSerializer.SerializeToElement(arguments));

        Assert.Equal(workbookPath, launch.SourceWorkbookPath);
    }

    [Fact]
    public async Task TheDapReaderAnswersTheBoundWorkbookWarningWhileLaunchPreparationWaits()
    {
        using var temp = TempDirectory.Create();
        await using var lease = await new VbaDebugSessionWorkspaceManager(temp.Path)
            .ClaimAsync(DebugSessionId.Parse("0123456789abcdef0123456789abcdef"), CancellationToken.None);
        var service = new ConfirmationBeforePreparationLaunchService();
        var runner = new StandaloneVbaDebugAdapterStdioRunner(service);
        using var input = new ConfirmationDapInput();
        using var prefix = CreateDapInput(
            new { seq = 1, type = "request", command = "launch", arguments = CreateValidLaunchArguments() },
            new { seq = 2, type = "request", command = "configurationDone", arguments = new { } });
        input.Append(prefix.ToArray());
        using var output = new MemoryStream();
        var invocation = runner.RunAsync(Path.GetFullPath("vba-dev.exe"), lease,
            input, output, Stream.Null, CancellationToken.None);
        try
        {
            Assert.True(await service.SinkObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(SpinWait.SpinUntil(
                () => ReadUtf8(output).Contains("\"message\":\"Replace live VBA code?\"}", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5)));
            var warning = Assert.Single(ReadDapMessages(output), item =>
                item.TryGetProperty("event", out var name) && name.GetString() == "vba/workbookConfirmation")
                .GetProperty("body");
            Assert.Equal(lease.SessionId.Value, warning.GetProperty("sessionId").GetString());
            Assert.Equal(0, warning.GetProperty("generationId").GetInt32());
            Assert.False(service.Prepared);
            using var response = CreateDapInput(new
            {
                seq = 3, type = "request", command = "vba/workbookConfirmationResult",
                arguments = new
                {
                    requestId = warning.GetProperty("requestId").GetString(),
                    sessionId = lease.SessionId.Value, generationId = 0, accepted = true
                }
            });
            input.Append(response.ToArray());
            Assert.True(SpinWait.SpinUntil(
                () => ReadUtf8(output).Contains("\"request_seq\":1,\"success\":true", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5)));
            Assert.True(service.Prepared);
        }
        finally
        {
            input.Complete();
            _ = await invocation.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class ConfirmationBeforePreparationLaunchService : IStandaloneVbaDebugLaunchService
    {
        internal TaskCompletionSource<bool> SinkObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Prepared { get; private set; }
        public async Task<IPreparedDebugLaunchPlan> PrepareAsync(string executable,
            IVbaDebugSessionWorkspaceLease lease, StandaloneVbaDebugLaunchRequest request,
            DebugRestartLaunchBinding? restartBinding, CancellationToken cancellationToken,
            IDebugLifecycleSink? lifecycleSink = null)
        {
            SinkObserved.TrySetResult(lifecycleSink is IDebugWorkbookConfirmationSink);
            if (lifecycleSink is not IDebugWorkbookConfirmationSink confirmation)
                throw new DebugSourceRejectedPreparationException(new DebugFailureCompletion(
                    new DebugSetupException("No DAP confirmation boundary was supplied.")).Complete());
            if (!await confirmation.ConfirmReplacementAsync(DebugGenerationId.Initial,
                @"C:\Projects\Book\src\Book\Book.xlsm", "Replace live VBA code?", cancellationToken))
                throw new DebugSourceRejectedPreparationException(new DebugFailureCompletion(
                    new DebugSetupException("Replacement was declined.")).Complete());
            Prepared = true;
            return new FakePreparedDebugLaunchPlan(request, restartBinding,
                _ => Task.FromResult<IStandaloneVbaDebugRunningSession>(new RecordingRunningSession()));
        }
    }

    [Theory]
    [InlineData(SourceVbeDebugSessionEndReason.WorkbookClosed, null, "Source workbook closed")]
    [InlineData(SourceVbeDebugSessionEndReason.ProcessExited, null, "exit code unavailable")]
    [InlineData(SourceVbeDebugSessionEndReason.ProcessExited, 7, "exited with code 7")]
    [InlineData(SourceVbeDebugSessionEndReason.Detached, null, "debug connection was detached")]
    public async Task SourceCompletionReportsOnlyObservedProcessExitCodes(
        SourceVbeDebugSessionEndReason reason, int? exitCode, string message)
    {
        using var temp = TempDirectory.Create();
        await using var lease = await new VbaDebugSessionWorkspaceManager(temp.Path)
            .ClaimAsync(DebugSessionId.Parse("0123456789abcdef0123456789abcdef"), CancellationToken.None);
        var source = new ClosedSourceRunningSession();
        var runner = new StandaloneVbaDebugAdapterStdioRunner(new RecordingDebugLaunchService(source));
        using var input = new ConfirmationDapInput();
        using var prefix = CreateDapInput(
            new { seq = 1, type = "request", command = "launch", arguments = CreateValidLaunchArguments() },
            new { seq = 2, type = "request", command = "configurationDone", arguments = new { } });
        input.Append(prefix.ToArray());
        using var output = new MemoryStream();
        var invocation = runner.RunAsync(Path.GetFullPath("vba-dev.exe"), lease,
            input, output, Stream.Null, CancellationToken.None);
        try
        {
            Assert.True(SpinWait.SpinUntil(
                () => ReadUtf8(output).Contains("\"request_seq\":1,\"success\":true", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5)));
            source.Complete(reason, exitCode);
            Assert.Equal(0, await invocation.WaitAsync(TimeSpan.FromSeconds(5)));
            var exited = ReadDapMessages(output).Where(item =>
                item.TryGetProperty("event", out var name) && name.GetString() == "exited").ToArray();
            if (exitCode is null) Assert.Empty(exited);
            else Assert.Equal(exitCode, Assert.Single(exited).GetProperty("body").GetProperty("exitCode").GetInt32());
            Assert.Contains(message, ReadUtf8(output));
            Assert.DoesNotContain("Owned Excel process", ReadUtf8(output));
            Assert.Equal(0, source.ResetCalls);
        }
        finally { input.Complete(); }
    }

    private sealed class ClosedSourceRunningSession : IStandaloneVbaDebugRunningSession, IDebugResourceOwnerEvidence
    {
        private readonly TaskCompletionSource<SourceVbeDebugSessionCompletion> closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ClosedSourceRunningSession() => Completion = ObserveCompletionAsync();
        internal void Complete(SourceVbeDebugSessionEndReason reason, int? exitCode)
            => closed.TrySetResult(new(reason, exitCode));
        internal void FailObservation(Exception failure) => closed.TrySetException(failure);
        internal Exception? ResetFailure { get; init; }
        internal int ResetCalls { get; private set; }
        internal int DisposeCalls { get; private set; }
        public Task<SourceVbeDebugSessionCompletion>? SourceWorkbookCompletion => closed.Task;
        public Task<int> Completion { get; }
        private async Task<int> ObserveCompletionAsync() { _ = await closed.Task; return 0; }
        public int ProcessId => 12345;
        public string TargetModuleName => "Module1";
        public string TargetProcedureName => "Run";
        public IReadOnlyList<VbeBreakpoint> VerifiedBreakpoints => [];
        public DebugFailureOutcome? CleanupOutcome { get; private set; }
        public ValueTask TerminateAsync()
        {
            ResetCalls++;
            return ResetFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(ResetFailure);
        }
        public ValueTask DisposeAsync() { DisposeCalls++; CleanupOutcome = ProvedSessionRelease(); return ValueTask.CompletedTask; }
    }

    private sealed class ConfirmationDapInput : Stream
    {
        private readonly Channel<byte> bytes = Channel.CreateUnbounded<byte>();
        internal void Append(byte[] frame)
        {
            foreach (var value in frame) Assert.True(bytes.Writer.TryWrite(value));
        }
        internal void Complete() => bytes.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0) return 0;
            if (!await bytes.Reader.WaitToReadAsync(cancellationToken)) return 0;
            var count = 0;
            while (count < buffer.Length && bytes.Reader.TryRead(out var value)) buffer.Span[count++] = value;
            return count;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
