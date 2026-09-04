using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using VbaLanguageServer.Lsp;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Workspace;
using Xunit;

namespace VbaLanguageServer.Tests;

[Collection(VbaDocumentAnalysisPerformanceTestCollection.Name)]
public sealed class VbaLanguageServerRuntimeTests
{
    [Fact]
    public async Task Companion_executable_notification_is_routed_before_shutdown()
    {
        var previousExitCode = Environment.ExitCode;
        try
        {
            var executablePath = Path.GetFullPath("vba-dev.exe");
            await using var input = new MemoryStream(CreateFramedInput(
                new
                {
                    jsonrpc = "2.0",
                    method = "vba/companionExecutable",
                    @params = new
                    {
                        schemaVersion = "1.0",
                        executablePath,
                        referenceListOutputSchemaVersion = "1.0"
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
            var handler = new RecordingCompanionExecutableHandler();
            var runtime = new VbaLanguageServerRuntime(
                transport,
                new VbaLspRequestExecution(transport, workspace),
                new VbaDocumentLifecycle(
                    transport,
                    workspace,
                    new NoOpReferenceCatalogLifecycle()),
                companionExecutableHandler: handler);

            await runtime.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(executablePath, Assert.Single(handler.Paths));
        }
        finally
        {
            Environment.ExitCode = previousExitCode;
        }
    }

    [Fact]
    public async Task Companion_notifications_pin_the_first_valid_path_in_input_order_when_its_application_is_delayed()
    {
        var previousExitCode = Environment.ExitCode;
        await using var input = new ControlledInputStream();
        await using var output = new SynchronizedCaptureStream();
        using var hostCancellation = new CancellationTokenSource();
        Task? run = null;
        var firstExecutablePath = Path.GetFullPath("first-vba-dev.exe");
        var secondExecutablePath = Path.GetFullPath("second-vba-dev.exe");
        var pinnedPaths = new List<string>();
        var sessionDiscovery = new SessionPinnedVbaDevReferenceCatalogDiscovery(
            new SignallingDiscovery("registry"),
            executablePath =>
            {
                pinnedPaths.Add(executablePath);
                return new SignallingContextFactoryDiscovery();
            });
        var handler = new DelayedFirstCompanionExecutableHandler(
            new VbaCompanionExecutableNotificationHandler(
                sessionDiscovery,
                static () => [],
                new NoOpCompanionRefresh()));
        try
        {
            var transport = new LspMessageTransport(input, output);
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()));
            var runtime = new VbaLanguageServerRuntime(
                transport,
                new VbaLspRequestExecution(transport, workspace),
                new VbaDocumentLifecycle(
                    transport,
                    workspace,
                    new NoOpReferenceCatalogLifecycle()),
                companionExecutableHandler: handler);

            run = runtime.RunAsync(hostCancellation.Token);
            input.Enqueue(CreateCompanionNotification(firstExecutablePath));
            await handler.FirstApplicationStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(5));

            input.Enqueue(CreateCompanionNotification(secondExecutablePath));
            await handler.SecondNotificationPrepared.Task.WaitAsync(
                TimeSpan.FromSeconds(5));

            handler.ReleaseFirstApplication.TrySetResult();
            input.Enqueue(
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
                });
            await run.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(firstExecutablePath, Assert.Single(pinnedPaths));
        }
        finally
        {
            handler.ReleaseFirstApplication.TrySetResult();
            input.Complete();
            hostCancellation.Cancel();
            if (run is not null)
            {
                try
                {
                    await run.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception)
                {
                }
            }

            Environment.ExitCode = previousExitCode;
        }
    }

    [Fact]
    public async Task Companion_executable_notification_does_not_block_a_following_hover_behind_an_active_read()
    {
        var previousExitCode = Environment.ExitCode;
        var gate = new BlockingRequestGate();
        await using var input = new ControlledInputStream();
        await using var output = new SynchronizedCaptureStream();
        using var hostCancellation = new CancellationTokenSource();
        Task? run = null;
        try
        {
            const string sourceUri = "file:///C:/work/CompanionBackground.bas";
            var executablePath = Path.GetFullPath("vba-dev.exe");
            var transport = new LspMessageTransport(input, output);
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()));
            var handler = new RecordingCompanionExecutableHandler();
            var runtime = new VbaLanguageServerRuntime(
                transport,
                new VbaLspRequestExecution(transport, workspace, gate),
                new VbaDocumentLifecycle(
                    transport,
                    workspace,
                    new NoOpReferenceCatalogLifecycle()),
                companionExecutableHandler: handler);

            run = runtime.RunAsync(hostCancellation.Token);
            input.Enqueue(
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
                            text = "Attribute VB_Name = \"CompanionBackground\"\nPublic Sub Run()\nEnd Sub\n"
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
                        textDocument = new { uri = sourceUri }
                    }
                });
            await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            input.Enqueue(
                new
                {
                    jsonrpc = "2.0",
                    method = "vba/companionExecutable",
                    @params = new
                    {
                        schemaVersion = "1.0",
                        executablePath,
                        referenceListOutputSchemaVersion = "1.0"
                    }
                },
                new
                {
                    jsonrpc = "2.0",
                    id = 3,
                    method = "textDocument/hover",
                    @params = new
                    {
                        textDocument = new { uri = sourceUri },
                        position = new { line = 1, character = 11 }
                    }
                });

            var hoverResponse = await WaitForResponseAsync(output, 3);
            Assert.True(hoverResponse.ContainsKey("result"));
            await handler.Applied.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(executablePath, Assert.Single(handler.Paths));
            Assert.False(gate.Release.Task.IsCompleted);

            gate.Release.TrySetResult();
            input.Enqueue(
                new
                {
                    jsonrpc = "2.0",
                    id = 4,
                    method = "shutdown"
                },
                new
                {
                    jsonrpc = "2.0",
                    method = "exit"
                });
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            gate.Release.TrySetResult();
            input.Complete();
            hostCancellation.Cancel();
            if (run is not null)
            {
                try
                {
                    await run.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception)
                {
                }
            }

            Environment.ExitCode = previousExitCode;
        }
    }

    [Fact]
    public async Task Companion_refresh_does_not_block_a_following_change_and_block_skeleton_request()
    {
        var previousExitCode = Environment.ExitCode;
        var catalogRefresh = new BlockingCompanionRefresh();
        await using var input = new ControlledInputStream();
        await using var output = new SynchronizedCaptureStream();
        using var hostCancellation = new CancellationTokenSource();
        Task? run = null;
        try
        {
            const string sourceUri = "file:///C:/work/CompanionBlockSkeleton.bas";
            const string header = "Public Sub Run()";
            var executablePath = Path.GetFullPath("vba-dev.exe");
            var transport = new LspMessageTransport(input, output);
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()));
            var sessionDiscovery =
                new SessionPinnedVbaDevReferenceCatalogDiscovery(
                    new SignallingDiscovery("registry"),
                    _ => new SignallingContextFactoryDiscovery());
            var companionHandler = new VbaCompanionExecutableNotificationHandler(
                sessionDiscovery,
                () => workspace.GetOpenDocumentUris(),
                catalogRefresh);
            var runtime = new VbaLanguageServerRuntime(
                transport,
                new VbaLspRequestExecution(transport, workspace),
                new VbaDocumentLifecycle(
                    transport,
                    workspace,
                    new NoOpReferenceCatalogLifecycle()),
                companionExecutableHandler: companionHandler);

            run = runtime.RunAsync(hostCancellation.Token);
            input.Enqueue(
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
                            text = header
                        }
                    }
                },
                new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    method = "vba/blockSkeletonInsertion",
                    @params = new
                    {
                        documentUri = sourceUri,
                        documentVersion = 1,
                        position = new { line = 0, character = header.Length },
                        options = new
                        {
                            insertSpaces = false,
                            tabSize = 4,
                            indentSize = 4
                        }
                    }
                });
            await WaitForResponseAsync(output, 1);

            input.Enqueue(
                new
                {
                    jsonrpc = "2.0",
                    method = "vba/companionExecutable",
                    @params = new
                    {
                        schemaVersion = "1.0",
                        executablePath,
                        referenceListOutputSchemaVersion = "1.0"
                    }
                });
            await catalogRefresh.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(sourceUri, Assert.Single(catalogRefresh.OpenDocumentUris));

            input.Enqueue(
                new
                {
                    jsonrpc = "2.0",
                    method = "textDocument/didChange",
                    @params = new
                    {
                        textDocument = new { uri = sourceUri, version = 2 },
                        contentChanges = new[] { new { text = $"{header}\n\t" } }
                    }
                },
                new
                {
                    jsonrpc = "2.0",
                    id = 2,
                    method = "vba/blockSkeletonInsertion",
                    @params = new
                    {
                        documentUri = sourceUri,
                        documentVersion = 2,
                        position = new { line = 0, character = header.Length },
                        options = new
                        {
                            insertSpaces = false,
                            tabSize = 4,
                            indentSize = 4
                        }
                    }
                });

            var response = await WaitForResponseAsync(output, 2);
            var result = Assert.IsType<JsonObject>(response["result"]);
            Assert.Equal(2, result["documentVersion"]?.GetValue<int>());
            Assert.False(catalogRefresh.Release.Task.IsCompleted);

            catalogRefresh.Release.TrySetResult();
            input.Enqueue(
                new
                {
                    jsonrpc = "2.0",
                    id = 3,
                    method = "shutdown"
                },
                new
                {
                    jsonrpc = "2.0",
                    method = "exit"
                });
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            catalogRefresh.Release.TrySetResult();
            input.Complete();
            hostCancellation.Cancel();
            if (run is not null)
            {
                try
                {
                    await run.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception)
                {
                }
            }

            Environment.ExitCode = previousExitCode;
        }
    }

    [Fact]
    public async Task Standalone_companion_validation_never_blocks_the_message_loop_and_is_cancelled_on_exit()
    {
        var previousExitCode = Environment.ExitCode;
        var validationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var validationCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await using var input = new MemoryStream(CreateFramedInput(
                new
                {
                    jsonrpc = "2.0",
                    method = "initialized",
                    @params = new { }
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
            var runtime = new VbaLanguageServerRuntime(
                transport,
                new VbaLspRequestExecution(transport, workspace),
                new VbaDocumentLifecycle(
                    transport,
                    workspace,
                    new NoOpReferenceCatalogLifecycle()),
                vbaDevStartupResolver: async cancellationToken =>
                {
                    validationStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(
                            Timeout.InfiniteTimeSpan,
                            cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        validationCancelled.TrySetResult();
                        throw;
                    }

                    throw new InvalidOperationException("Unreachable.");
                });

            await runtime.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

            await validationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await validationCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = previousExitCode;
        }
    }

    [Fact]
    public async Task Throwing_startup_cancellation_callback_cannot_skip_runtime_cleanup()
    {
        var previousExitCode = Environment.ExitCode;
        var validationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var input = new ControlledInputStream();
        await using var output = new SynchronizedCaptureStream();
        using var hostCancellation = new CancellationTokenSource();
        Task? run = null;
        try
        {
            var transport = new LspMessageTransport(input, output);
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()));
            var catalogLifecycle = new TrackingReferenceCatalogRuntimeLifecycle();
            var runtime = new VbaLanguageServerRuntime(
                transport,
                new VbaLspRequestExecution(transport, workspace),
                new VbaDocumentLifecycle(transport, workspace, catalogLifecycle),
                catalogLifecycle,
                vbaDevStartupResolver: async cancellationToken =>
                {
                    using var registration = cancellationToken.Register(
                        static () => throw new InvalidOperationException(
                            "Expected startup cancellation callback failure."));
                    validationStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("Unreachable.");
                });

            run = runtime.RunAsync(hostCancellation.Token);
            input.Enqueue(new
            {
                jsonrpc = "2.0",
                method = "initialized",
                @params = new { }
            });
            await validationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            input.Enqueue(
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
                });

            await run.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, catalogLifecycle.StopCount);
            var scheduler = Assert.IsType<VbaInteractiveWorkScheduler>(
                catalogLifecycle.AttachedScheduler);
            Assert.False(scheduler.IsAccepting);
            Assert.Equal(0, Environment.ExitCode);
        }
        finally
        {
            input.Complete();
            hostCancellation.Cancel();
            if (run is not null)
            {
                try
                {
                    await run.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception)
                {
                }
            }

            Environment.ExitCode = previousExitCode;
        }
    }

    [Fact]
    public async Task Blocked_standalone_validation_allows_semantic_tokens_then_late_pin_refreshes_the_open_project()
    {
        var previousExitCode = Environment.ExitCode;
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-late-companion-").FullName;
        var resolverRelease = new TaskCompletionSource<VbaDevReferenceListStartupState>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var input = new ControlledInputStream();
        await using var output = new SynchronizedCaptureStream();
        using var hostCancellation = new CancellationTokenSource();
        Task? run = null;
        try
        {
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            Directory.CreateDirectory(sourceRoot);
            File.WriteAllText(
                Path.Combine(projectRoot, "vba-project.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    projectName = "LateCompanionProject",
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
                            references = new[]
                            {
                                new
                                {
                                    name = "Generated Library",
                                    requested = true
                                }
                            }
                        }
                    }
                }));
            var sourceUri = new Uri(
                Path.Combine(sourceRoot, "Worker.bas")).AbsoluteUri;
            var transport = new LspMessageTransport(input, output);
            var catalogCache = new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.Empty);
            var registryDiscovery = new SignallingDiscovery("registry");
            var companionDiscovery = new SignallingContextFactoryDiscovery();
            var sessionDiscovery =
                new SessionPinnedVbaDevReferenceCatalogDiscovery(
                    registryDiscovery,
                    _ => companionDiscovery);
            var refreshService = new VbaProjectReferenceCatalogRefreshService(
                catalogCache,
                sessionDiscovery);
            var workspace = new VbaLanguageWorkspace(catalogCache);
            var catalogRefresh = new ReferenceCatalogRefreshCoordinator(
                catalogCache,
                refreshService,
                workspace.ManifestWorkspace,
                transport);
            var companionHandler = new VbaCompanionExecutableNotificationHandler(
                sessionDiscovery,
                () => workspace.GetOpenDocumentUris(),
                catalogRefresh);
            var runtime = new VbaLanguageServerRuntime(
                transport,
                new VbaLspRequestExecution(transport, workspace),
                new VbaDocumentLifecycle(
                    transport,
                    workspace,
                    catalogRefresh),
                catalogRefresh,
                companionExecutableHandler: companionHandler,
                vbaDevStartupResolver: cancellationToken =>
                    resolverRelease.Task.WaitAsync(cancellationToken));

            run = runtime.RunAsync(hostCancellation.Token);
            input.Enqueue(
                new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    method = "initialize",
                    @params = new
                    {
                        processId = Environment.ProcessId,
                        rootUri = (string?)null,
                        capabilities = new { }
                    }
                },
                new
                {
                    jsonrpc = "2.0",
                    method = "initialized",
                    @params = new { }
                },
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
                    id = 2,
                    method = "textDocument/semanticTokens/full",
                    @params = new
                    {
                        textDocument = new { uri = sourceUri }
                    }
                });

            var semanticResponse = await WaitForResponseAsync(output, 2);
            var semanticData = semanticResponse["result"]?["data"] as JsonArray;
            Assert.NotNull(semanticData);
            Assert.NotEmpty(semanticData);
            Assert.False(resolverRelease.Task.IsCompleted);
            await registryDiscovery.Called.Task.WaitAsync(TimeSpan.FromSeconds(5));

            resolverRelease.TrySetResult(new VbaDevReferenceListStartupState(
                Path.GetFullPath("vba-dev.exe"),
                null));
            await companionDiscovery.Called.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, registryDiscovery.CallCount);
            Assert.Equal(1, companionDiscovery.CallCount);
            input.Enqueue(
                new
                {
                    jsonrpc = "2.0",
                    id = 3,
                    method = "shutdown"
                },
                new
                {
                    jsonrpc = "2.0",
                    method = "exit"
                });
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            resolverRelease.TrySetCanceled();
            input.Complete();
            hostCancellation.Cancel();
            if (run is not null)
            {
                try
                {
                    await run.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception)
                {
                }
            }

            Environment.ExitCode = previousExitCode;
            Directory.Delete(projectRoot, recursive: true);
        }
    }

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

    private static async Task<JsonObject> WaitForResponseAsync(
        SynchronizedCaptureStream output,
        int requestId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            foreach (var message in ParseCompleteFrames(output.Snapshot()))
            {
                if (message["id"] is JsonValue idNode
                    && idNode.TryGetValue<int>(out var id)
                    && id == requestId
                    && message.ContainsKey("result"))
                {
                    return message;
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException(
                    $"Timed out waiting for response {requestId}.");
            }
        }
    }

    private static IReadOnlyList<JsonObject> ParseCompleteFrames(byte[] bytes)
    {
        var messages = new List<JsonObject>();
        var offset = 0;
        while (offset < bytes.Length)
        {
            var delimiter = FindHeaderDelimiter(bytes, offset);
            if (delimiter < 0)
            {
                break;
            }

            var header = Encoding.ASCII.GetString(
                bytes,
                offset,
                delimiter - offset);
            var contentLengthLine = header
                .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                .SingleOrDefault(line => line.StartsWith(
                    "Content-Length:",
                    StringComparison.OrdinalIgnoreCase));
            if (contentLengthLine is null
                || !int.TryParse(
                    contentLengthLine["Content-Length:".Length..].Trim(),
                    out var contentLength))
            {
                break;
            }

            var contentOffset = delimiter + 4;
            if (bytes.Length - contentOffset < contentLength)
            {
                break;
            }

            if (JsonNode.Parse(
                    Encoding.UTF8.GetString(
                        bytes,
                        contentOffset,
                        contentLength)) is JsonObject message)
            {
                messages.Add(message);
            }

            offset = contentOffset + contentLength;
        }

        return messages;
    }

    private static int FindHeaderDelimiter(byte[] bytes, int offset)
    {
        for (var index = offset; index <= bytes.Length - 4; index++)
        {
            if (bytes[index] == '\r'
                && bytes[index + 1] == '\n'
                && bytes[index + 2] == '\r'
                && bytes[index + 3] == '\n')
            {
                return index;
            }
        }

        return -1;
    }

    private static object CreateCompanionNotification(string executablePath)
        => new
        {
            jsonrpc = "2.0",
            method = "vba/companionExecutable",
            @params = new
            {
                schemaVersion = "1.0",
                executablePath,
                referenceListOutputSchemaVersion = "1.0"
            }
        };

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

    private sealed class RecordingCompanionExecutableHandler
        : IVbaCompanionExecutableNotificationHandler
    {
        public TaskCompletionSource Applied { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Paths { get; } = [];

        public VbaCompanionExecutableApplication TryPrepare(
            VbaCompanionExecutableUpdate update)
        {
            return new VbaCompanionExecutableApplication(() =>
            {
                Paths.Add(update.ExecutablePath);
                Applied.TrySetResult();
                return true;
            });
        }
    }

    private sealed class DelayedFirstCompanionExecutableHandler(
        VbaCompanionExecutableNotificationHandler inner)
        : IVbaCompanionExecutableNotificationHandler
    {
        private int applicationCount;

        public TaskCompletionSource FirstApplicationStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstApplication { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondNotificationPrepared { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public VbaCompanionExecutableApplication? TryPrepare(
            VbaCompanionExecutableUpdate update)
        {
            var applicationNumber = Interlocked.Increment(
                ref applicationCount);
            var application = inner.TryPrepare(update);
            if (applicationNumber == 2)
            {
                SecondNotificationPrepared.TrySetResult();
            }

            if (application is null)
            {
                return null;
            }

            return new VbaCompanionExecutableApplication(() =>
            {
                if (applicationNumber == 1)
                {
                    FirstApplicationStarted.TrySetResult();
                    ReleaseFirstApplication.Task.GetAwaiter().GetResult();
                }

                return application.Apply();
            });
        }
    }

    private sealed class NoOpCompanionRefresh
        : IVbaCompanionReferenceCatalogRefresh
    {
        public void RefreshActiveProjects(IReadOnlyList<string> openDocumentUris)
        {
        }
    }

    private sealed class BlockingCompanionRefresh
        : IVbaCompanionReferenceCatalogRefresh
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<string> OpenDocumentUris { get; private set; } = [];

        public void RefreshActiveProjects(IReadOnlyList<string> openDocumentUris)
        {
            OpenDocumentUris = openDocumentUris.ToArray();
            Started.TrySetResult();
            Release.Task.GetAwaiter().GetResult();
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

        public int StopCount { get; private set; }

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
        {
            StopCount++;
            return Task.CompletedTask;
        }
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

    private sealed class SignallingDiscovery(string marker)
        : IVbaProjectReferenceCatalogDiscovery
    {
        private int callCount;

        public TaskCompletionSource Called { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount => Volatile.Read(ref callCount);

        public Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref callCount);
            Called.TrySetResult();
            return Task.FromResult(
                VbaProjectReferenceCatalogDiscoveryResult.Failure(
                    referenceName,
                    marker));
        }
    }

    private sealed class SignallingContextFactoryDiscovery
        : IVbaProjectReferenceCatalogDiscovery,
          IVbaProjectReferenceCatalogContextDiscoveryFactory
    {
        private int callCount;

        public TaskCompletionSource Called { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount => Volatile.Read(ref callCount);

        public IVbaProjectReferenceCatalogDiscovery CreateContextDiscovery(
            VbaProjectReferenceCatalogRefreshContext context)
            => this;

        public Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref callCount);
            Called.TrySetResult();
            return Task.FromResult(
                VbaProjectReferenceCatalogDiscoveryResult.Failure(
                    referenceName,
                    "companion"));
        }
    }

    private sealed class ControlledInputStream : Stream
    {
        private readonly Channel<byte[]> chunks = Channel.CreateUnbounded<byte[]>();
        private byte[]? currentChunk;
        private int currentOffset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void Enqueue(params object[] messages)
        {
            if (!chunks.Writer.TryWrite(CreateFramedInput(messages)))
            {
                throw new InvalidOperationException("The input stream is complete.");
            }
        }

        public void Complete()
            => chunks.Writer.TryComplete();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            while (currentChunk is null
                || currentOffset >= currentChunk.Length)
            {
                if (!await chunks.Reader.WaitToReadAsync(cancellationToken)
                    || !chunks.Reader.TryRead(out currentChunk))
                {
                    return 0;
                }

                currentOffset = 0;
            }

            var count = Math.Min(
                buffer.Length,
                currentChunk.Length - currentOffset);
            currentChunk.AsMemory(currentOffset, count).CopyTo(buffer);
            currentOffset += count;
            return count;
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
                Complete();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class SynchronizedCaptureStream : Stream
    {
        private readonly object gate = new();
        private readonly MemoryStream buffer = new();

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length
        {
            get
            {
                lock (gate)
                {
                    return buffer.Length;
                }
            }
        }

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public byte[] Snapshot()
        {
            lock (gate)
            {
                return buffer.ToArray();
            }
        }

        public override void Write(byte[] source, int offset, int count)
        {
            lock (gate)
            {
                buffer.Write(source, offset, count);
            }
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                buffer.Write(source.Span);
            }

            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(
            byte[] source,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(source, offset, count);
            return Task.CompletedTask;
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override int Read(byte[] target, int offset, int count)
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (gate)
                {
                    buffer.Dispose();
                }
            }

            base.Dispose(disposing);
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
