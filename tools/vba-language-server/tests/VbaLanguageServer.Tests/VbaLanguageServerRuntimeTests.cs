using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VbaLanguageServer.Lsp;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Workspace;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaLanguageServerRuntimeTests
{
    [Fact]
    public async Task Exit_waits_for_owned_capacity_after_shutdown_instead_of_faulting()
    {
        var previousExitCode = Environment.ExitCode;
        var gate = new BlockingRequestGate();
        await using var input = new MemoryStream(CreateFramedInput(
            new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "textDocument/documentSymbol",
                @params = new
                {
                    textDocument = new
                    {
                        uri = "file:///C:/work/RuntimeRequiredExit.bas"
                    }
                }
            },
            new
            {
                jsonrpc = "2.0",
                method = "exit"
            }));
        await using var output = new MemoryStream();
        var transport = new LspMessageTransport(input, output);
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()));
        var requestExecution = new VbaLspRequestExecution(
            transport,
            workspace,
            gate);
        var shutdownRequest = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "shutdown"
        };
        await requestExecution.ExecuteAsync(
            requestExecution.Capture(shutdownRequest, CancellationToken.None),
            CancellationToken.None,
            CancellationToken.None);
        var runtime = new VbaLanguageServerRuntime(
            transport,
            requestExecution,
            new VbaDocumentLifecycle(
                transport,
                workspace,
                new NoOpReferenceCatalogLifecycle()),
            schedulerOptions: new VbaInteractiveWorkSchedulerOptions(
                CoalesceSupersededMutations: true,
                MaxOwnedWork: 1));

        try
        {
            var run = runtime.RunAsync();
            await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(run.IsCompleted);

            gate.Release.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, Environment.ExitCode);
        }
        finally
        {
            gate.Release.TrySetResult();
            Environment.ExitCode = previousExitCode;
        }
    }

    [Fact]
    public async Task Duplicate_request_reports_duplicate_error_when_owned_capacity_is_full()
    {
        var previousExitCode = Environment.ExitCode;
        var gate = new BlockingRequestGate();
        await using var input = new MemoryStream(CreateFramedInput(
            new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "textDocument/documentSymbol",
                @params = new
                {
                    textDocument = new
                    {
                        uri = "file:///C:/work/RuntimeRequiredDuplicate.bas"
                    }
                }
            },
            new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "textDocument/documentSymbol",
                @params = new
                {
                    textDocument = new
                    {
                        uri = "file:///C:/work/RuntimeRequiredDuplicate.bas"
                    }
                }
            },
            new
            {
                jsonrpc = "2.0",
                method = "exit"
            }));
        await using var output = new MemoryStream();
        var transport = new LspMessageTransport(input, output);
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()));
        var requestExecution = new VbaLspRequestExecution(
            transport,
            workspace,
            gate);
        var shutdownRequest = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "shutdown"
        };
        await requestExecution.ExecuteAsync(
            requestExecution.Capture(shutdownRequest, CancellationToken.None),
            CancellationToken.None,
            CancellationToken.None);
        var runtime = new VbaLanguageServerRuntime(
            transport,
            requestExecution,
            new VbaDocumentLifecycle(
                transport,
                workspace,
                new NoOpReferenceCatalogLifecycle()),
            schedulerOptions: new VbaInteractiveWorkSchedulerOptions(
                CoalesceSupersededMutations: true,
                MaxOwnedWork: 1));

        try
        {
            var run = runtime.RunAsync();
            await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            gate.Release.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(5));

            var outputText = Encoding.UTF8.GetString(output.ToArray());
            Assert.Contains("\"code\":-32600", outputText, StringComparison.Ordinal);
            Assert.DoesNotContain("\"code\":-32000", outputText, StringComparison.Ordinal);
        }
        finally
        {
            gate.Release.TrySetResult();
            Environment.ExitCode = previousExitCode;
        }
    }

    [Fact]
    public async Task Runtime_stops_the_scheduler_when_catalog_shutdown_faults()
    {
        await using var input = new MemoryStream();
        await using var output = new MemoryStream();
        var transport = new LspMessageTransport(input, output);
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.CreateBundled());
        var workspace = new VbaLanguageWorkspace(catalogCache);
        var catalogLifecycle = new FaultingStopReferenceCatalogLifecycle();
        var runtime = new VbaLanguageServerRuntime(
            transport,
            new VbaLspRequestExecution(transport, workspace),
            new VbaDocumentLifecycle(transport, workspace, catalogLifecycle),
            catalogLifecycle);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.RunAsync());

        Assert.Equal("Expected catalog shutdown failure.", exception.Message);
        var scheduler = Assert.IsType<VbaInteractiveWorkScheduler>(
            catalogLifecycle.AttachedScheduler);
        Assert.False(scheduler.IsAccepting);
        await scheduler.StopAsync(VbaInteractiveStopReason.Abort)
            .WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Fatal_scheduler_failure_does_not_run_blocking_response_cancellation_inline()
    {
        await using var input = new FramedThenCancellationGateStream(
            CreateFramedInput(new
            {
                jsonrpc = "2.0",
                method = "textDocument/didOpen",
                @params = new
                {
                    textDocument = new
                    {
                        uri = "file:///C:/work/FatalRuntime.bas",
                        languageId = "vba",
                        version = 1,
                        text = "Public Sub Run()\nEnd Sub\n"
                    }
                }
            }));
        await using var output = new MemoryStream();
        var transport = new LspMessageTransport(input, output);
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.CreateBundled());
        var workspace = new VbaLanguageWorkspace(
            catalogCache,
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            new ThrowingDocumentAnalysisObserver(
                input.CancellationCallbackRegistered));
        var catalogLifecycle = new TrackingReferenceCatalogRuntimeLifecycle();
        var runtime = new VbaLanguageServerRuntime(
            transport,
            new VbaLspRequestExecution(transport, workspace),
            new VbaDocumentLifecycle(transport, workspace, catalogLifecycle),
            catalogLifecycle);

        var run = runtime.RunAsync();
        try
        {
            await input.CancellationCallbackStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            var scheduler = Assert.IsType<VbaInteractiveWorkScheduler>(
                catalogLifecycle.AttachedScheduler);

            await scheduler.StopAsync(VbaInteractiveStopReason.Abort)
                .WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(
                run.IsCompleted,
                "RunAsync must observe response cancellation dispatch before disposing its response lifetime token source.");
        }
        finally
        {
            input.ReleaseCancellationCallback();
        }

        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static byte[] CreateFramedInput(params object[] messages)
    {
        using var stream = new MemoryStream();
        foreach (var message in messages)
        {
            var content = JsonSerializer.SerializeToUtf8Bytes(message);
            var header = Encoding.ASCII.GetBytes(
                $"Content-Length: {content.Length}\r\n\r\n");
            stream.Write(header);
            stream.Write(content);
        }

        return stream.ToArray();
    }

    private sealed class BlockingRequestGate : IVbaLspRequestExecutionGate
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WaitAsync(
            VbaLspRequestId? requestId,
            string method,
            CancellationToken cancellationToken)
        {
            if (requestId is not
                {
                    Kind: VbaLspRequestIdKind.Number,
                    Value: "2"
                })
            {
                return;
            }

            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class NoOpReferenceCatalogLifecycle : IReferenceCatalogLifecycle
    {
        public void ActivateProject(string uri)
        {
        }

        public void ApplyManifestSelectionChange(string uri, string text)
        {
        }

        public void DeactivateManifest(string uri)
        {
        }
    }

    private sealed class FaultingStopReferenceCatalogLifecycle
        : IReferenceCatalogRuntimeLifecycle
    {
        public VbaInteractiveWorkScheduler? AttachedScheduler { get; private set; }

        public void ActivateProject(string uri)
        {
        }

        public void ApplyManifestSelectionChange(string uri, string text)
        {
        }

        public void DeactivateManifest(string uri)
        {
        }

        public void AttachScheduler(VbaInteractiveWorkScheduler scheduler)
            => AttachedScheduler = scheduler;

        public Task StopAsync()
            => Task.FromException(
                new InvalidOperationException("Expected catalog shutdown failure."));
    }

    private sealed class TrackingReferenceCatalogRuntimeLifecycle
        : IReferenceCatalogRuntimeLifecycle
    {
        public VbaInteractiveWorkScheduler? AttachedScheduler { get; private set; }

        public void ActivateProject(string uri)
        {
        }

        public void ApplyManifestSelectionChange(string uri, string text)
        {
        }

        public void DeactivateManifest(string uri)
        {
        }

        public void AttachScheduler(VbaInteractiveWorkScheduler scheduler)
            => AttachedScheduler = scheduler;

        public Task StopAsync()
            => Task.CompletedTask;
    }

    private sealed class ThrowingDocumentAnalysisObserver(
        Task callbackRegistered)
        : IVbaDocumentAnalysisBuildObserver
    {
        public void BeforeBuild(
            VbaDocumentAnalysisBuildContext context,
            CancellationToken cancellationToken)
        {
            callbackRegistered.Wait(cancellationToken);
            throw new InvalidOperationException(
                "Expected fatal document analysis failure.");
        }
    }

    private sealed class FramedThenCancellationGateStream(
        byte[] framedInput)
        : Stream
    {
        private readonly ManualResetEventSlim cancellationCallbackRelease = new();
        private readonly TaskCompletionSource cancellationCallbackRegistered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration cancellationRegistration;
        private int position;
        private int registered;

        public Task CancellationCallbackRegistered
            => cancellationCallbackRegistered.Task;

        public TaskCompletionSource CancellationCallbackStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => framedInput.Length;

        public override long Position
        {
            get => position;
            set => throw new NotSupportedException();
        }

        public void ReleaseCancellationCallback()
            => cancellationCallbackRelease.Set();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (position < framedInput.Length)
            {
                var count = Math.Min(
                    buffer.Length,
                    framedInput.Length - position);
                framedInput.AsMemory(position, count).CopyTo(buffer);
                position += count;
                return count;
            }

            if (Interlocked.CompareExchange(ref registered, 1, 0) == 0)
            {
                cancellationRegistration = cancellationToken.Register(() =>
                {
                    CancellationCallbackStarted.TrySetResult();
                    cancellationCallbackRelease.Wait();
                });
                cancellationCallbackRegistered.TrySetResult();
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                cancellationCallbackRelease.Set();
                cancellationRegistration.Dispose();
                cancellationCallbackRelease.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
