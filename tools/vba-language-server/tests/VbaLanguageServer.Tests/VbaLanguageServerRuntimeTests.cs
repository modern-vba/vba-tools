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
    public async Task Intrinsic_host_event_catalog_notification_mutates_the_workspace_before_shutdown()
    {
        var previousExitCode = Environment.ExitCode;
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-runtime-host-").FullName;
        try
        {
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            Directory.CreateDirectory(sourceRoot);
            File.WriteAllText(
                Path.Combine(projectRoot, "vba-project.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    projectName = "RuntimeHostProject",
                    primaryDocument = "Book1",
                    documents = new Dictionary<string, object>
                    {
                        ["Book1"] = new
                        {
                            kind = "excel",
                            sourcePath = "src/Book1",
                            templatePath = "src/Book1/Book1.xlsm",
                            binPath = "bin/Book1/Book1.xlsm",
                            publishPath = "publish/Book1/Book1.xlsm",
                            commonModules = Array.Empty<object>(),
                            references = Array.Empty<object>()
                        }
                    }
                }));
            var sourceUri = new Uri(Path.Combine(sourceRoot, "Worker.bas")).AbsoluteUri;
            await using var input = new MemoryStream(CreateFramedInput(
                new
                {
                    jsonrpc = "2.0",
                    method = "textDocument/didOpen",
                    @params = new
                    {
                        textDocument = new
                        {
                            uri = sourceUri,
                            languageId = "vba",
                            version = 1,
                            text = "Attribute VB_Name = \"Worker\"\nPublic Sub Run()\nEnd Sub\n"
                        }
                    }
                },
                new
                {
                    jsonrpc = "2.0",
                    method = "vba/intrinsicHostEventCatalog",
                    @params = new
                    {
                        schemaVersion = "1.0",
                        revision = 1,
                        catalog = CreateCatalog()
                    }
                },
                new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    method = "shutdown"
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
            var catalogLifecycle = new NoOpReferenceCatalogLifecycle();
            var runtime = new VbaLanguageServerRuntime(
                transport,
                new VbaLspRequestExecution(transport, workspace),
                new VbaDocumentLifecycle(
                    transport,
                    workspace,
                    catalogLifecycle),
                intrinsicHostEventCatalogHandler:
                    new VbaIntrinsicHostEventCatalogHandler(workspace));

            await runtime.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

            var catalog = workspace.CreateProjectSnapshot(sourceUri)
                .SemanticInventory.IntrinsicHostEventCatalog;
            Assert.NotNull(catalog);
            Assert.Equal("Initialize", Assert.Single(catalog.Events).Name);
        }
        finally
        {
            Environment.ExitCode = previousExitCode;
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Intrinsic_host_event_catalog_notifications_reject_a_stale_revision()
    {
        var previousExitCode = Environment.ExitCode;
        var analysisGate = new BlockingDocumentAnalysisObserver();
        try
        {
            await using var input = new MemoryStream(CreateFramedInput(
                new
                {
                    jsonrpc = "2.0",
                    method = "textDocument/didOpen",
                    @params = new
                    {
                        textDocument = new
                        {
                            uri = "file:///C:/work/HostQueueGate.bas",
                            languageId = "vba",
                            version = 1,
                            text = "Public Sub Run()\nEnd Sub\n"
                        }
                    }
                },
                CreateCatalogNotification(revision: 3),
                CreateCatalogNotification(revision: 2),
                new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    method = "shutdown"
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
                    VbaProjectReferenceCatalogSet.CreateBundled()),
                NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
                analysisGate);
            var handler = new RecordingIntrinsicHostEventCatalogHandler();
            var catalogLifecycle = new NoOpReferenceCatalogLifecycle();
            var runtime = new VbaLanguageServerRuntime(
                transport,
                new VbaLspRequestExecution(transport, workspace),
                new VbaDocumentLifecycle(
                    transport,
                    workspace,
                    catalogLifecycle),
                intrinsicHostEventCatalogHandler: handler);

            var run = runtime.RunAsync();
            await analysisGate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            analysisGate.Release.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal([3], handler.AppliedRevisions);
        }
        finally
        {
            analysisGate.Release.TrySetResult();
            Environment.ExitCode = previousExitCode;
        }
    }

    [Fact]
    public async Task Intrinsic_host_event_catalog_notifications_apply_in_revision_order()
    {
        var previousExitCode = Environment.ExitCode;
        var analysisGate = new BlockingDocumentAnalysisObserver();
        try
        {
            await using var input = new MemoryStream(CreateFramedInput(
                new
                {
                    jsonrpc = "2.0",
                    method = "textDocument/didOpen",
                    @params = new
                    {
                        textDocument = new
                        {
                            uri = "file:///C:/work/HostInterleavedGate.bas",
                            languageId = "vba",
                            version = 1,
                            text = "Public Sub Run()\nEnd Sub\n"
                        }
                    }
                },
                CreateCatalogNotification(revision: 2),
                CreateCatalogNotification(revision: 1),
                CreateCatalogNotification(revision: 3),
                new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    method = "shutdown"
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
                    VbaProjectReferenceCatalogSet.CreateBundled()),
                NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
                analysisGate);
            var handler = new RecordingIntrinsicHostEventCatalogHandler();
            var runtime = new VbaLanguageServerRuntime(
                transport,
                new VbaLspRequestExecution(transport, workspace),
                new VbaDocumentLifecycle(
                    transport,
                    workspace,
                    new NoOpReferenceCatalogLifecycle()),
                intrinsicHostEventCatalogHandler: handler);

            var run = runtime.RunAsync();
            await analysisGate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            analysisGate.Release.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal([2, 3], handler.AppliedRevisions);
        }
        finally
        {
            analysisGate.Release.TrySetResult();
            Environment.ExitCode = previousExitCode;
        }
    }

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

    private static object CreateCatalogNotification(long revision)
        => new
        {
            jsonrpc = "2.0",
            method = "vba/intrinsicHostEventCatalog",
            @params = new
            {
                schemaVersion = "1.0",
                revision,
                catalog = CreateCatalog()
            }
        };

    private static object CreateCatalog()
        => new
        {
            sourceKind = "userForm",
            intrinsicEventSourceName = "UserForm",
            events = new object[]
            {
                new
                {
                    identity = new { sourceName = "UserForm", name = "Initialize" },
                    signature = new
                    {
                        parameters = Array.Empty<object>(),
                        documentation = "Initializes the form."
                    },
                    authoringAvailable = true,
                    existingHandlerRecognizable = true
                }
            }
        };

    private sealed class RecordingIntrinsicHostEventCatalogHandler
        : IVbaIntrinsicHostEventCatalogHandler
    {
        public List<long> AppliedRevisions { get; } = [];
        private long revision;

        public bool TryParse(
            JsonNode? parameters,
            out VbaIntrinsicHostEventCatalogUpdate update)
        {
            update = default!;
            if (parameters is not JsonObject payload
                || payload["revision"] is not JsonValue revisionNode
                || !revisionNode.TryGetValue<long>(out var revision))
            {
                return false;
            }

            update = new VbaIntrinsicHostEventCatalogUpdate(revision, null);
            return true;
        }

        public bool TryApply(VbaIntrinsicHostEventCatalogUpdate update)
        {
            if (update.Revision <= revision)
            {
                return false;
            }

            revision = update.Revision;
            AppliedRevisions.Add(update.Revision);
            return true;
        }
    }

    private sealed class BlockingDocumentAnalysisObserver
        : IVbaDocumentAnalysisBuildObserver
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void BeforeBuild(
            VbaDocumentAnalysisBuildContext context,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            Release.Task.Wait(cancellationToken);
        }
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
