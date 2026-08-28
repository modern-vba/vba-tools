using VbaLanguageServer.Lsp;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Workspace;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaDiagnosticsPublisherTests
{
    [Fact]
    public async Task DocumentChangeCommitsWithoutAwaitingDiagnosticsTransport()
    {
        const string uri = "file:///C:/work/Worker.bas";
        await using var output = new BlockingWriteStream();
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        publisher.AttachScheduler(scheduler);
        var pipeline = new VbaDocumentChangePipeline(
            workspace,
            new RecordingReferenceCatalogLifecycle(),
            publisher);

        var apply = pipeline.ApplyAsync(
            new VbaTextDocumentOpenedChange(
                uri,
                1,
                "Public Sub Run()\n    "),
            CancellationToken.None);
        await output.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("Public Sub Run()\n    ", workspace.GetDocumentText(uri));
        Assert.True(apply.IsCompleted);

        output.ReleaseWrites();
        await apply.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Diagnostics_publication_uses_the_bounded_background_scheduler()
    {
        const string uri = "file:///C:/work/Worker.bas";
        await using var output = new BlockingWriteStream();
        var timingSink = new SignallingTimingSink();
        await using var scheduler = new VbaInteractiveWorkScheduler(
            timingSink,
            options: new VbaInteractiveWorkSchedulerOptions(
                CoalesceSupersededMutations: true,
                EnableConcurrentReads: true,
                MaxConcurrentReads: 1,
                MaxConcurrentBulkReads: 1));
        var blockerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocker = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = scheduler.AdmitRequest(
            requestId: null,
            "textDocument/hover",
            _ => new object(),
            async (_, cancellationToken) =>
            {
                blockerStarted.TrySetResult();
                await releaseBlocker.Task.WaitAsync(cancellationToken);
            });
        await blockerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        publisher.AttachScheduler(scheduler);
        workspace.OpenDocument(
            uri,
            7,
            "Public Sub Run()\n    ");

        await publisher.PublishTrackedDiagnosticsAsync(uri, CancellationToken.None);
        await timingSink.WaitForAdmissionAsync("textDocument/diagnostic");

        Assert.False(output.WriteStarted.Task.IsCompleted);

        releaseBlocker.TrySetResult();
        await blocker.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await output.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var idle = publisher.WaitForIdleAsync(uri);
        var stop = scheduler.StopAsync(VbaInteractiveStopReason.Complete);

        Assert.False(timingSink.IsCompleted("textDocument/diagnostic"));
        Assert.False(idle.IsCompleted);
        Assert.False(stop.IsCompleted);

        output.ReleaseWrites();
        await timingSink.WaitForCompletionAsync("textDocument/diagnostic");
        await idle.WaitAsync(TimeSpan.FromSeconds(5));
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Diagnostics_overflow_retries_only_the_latest_revision_after_capacity_returns()
    {
        const string uri = "file:///C:/work/Worker.bas";
        await using var output = new CapturingWriteStream();
        var blockerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocker = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scheduler = new VbaInteractiveWorkScheduler(
            options: new VbaInteractiveWorkSchedulerOptions(
                CoalesceSupersededMutations: true,
                MaxOwnedWork: 1));
        var blocker = scheduler.AdmitMutation(async cancellationToken =>
        {
            blockerStarted.TrySetResult();
            await releaseBlocker.Task.WaitAsync(cancellationToken);
        });
        await blockerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        publisher.AttachScheduler(scheduler);
        workspace.OpenDocument(uri, 1, "Public Sub Run()\n    ");
        await publisher.PublishTrackedDiagnosticsAsync(uri, CancellationToken.None);
        Assert.True(workspace.ChangeDocument(
            uri,
            2,
            "Public Sub Run()\nEnd Sub\n"));
        await publisher.PublishTrackedDiagnosticsAsync(uri, CancellationToken.None);
        Assert.True(workspace.ChangeDocument(
            uri,
            3,
            "Public Sub Latest()\nEnd Sub\n"));
        await publisher.PublishTrackedDiagnosticsAsync(uri, CancellationToken.None);

        Assert.Equal(0, output.MessageCount);
        Assert.True(scheduler.IsAccepting);
        releaseBlocker.TrySetResult();
        await blocker.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await publisher.WaitForIdleAsync(uri)
            .WaitAsync(TimeSpan.FromSeconds(5));

        var messages = ReadJsonMessages(
            await output.WaitForMessageCountAsync(1));
        var parameters = Assert.IsType<JsonObject>(
            Assert.Single(messages)["params"]);
        Assert.Equal(3, parameters["version"]?.GetValue<int>());
    }

    [Fact]
    public async Task Concurrent_enqueue_cannot_replace_a_newer_pending_revision_with_an_older_one()
    {
        const string uri = "file:///C:/work/Worker.bas";
        await using var output = new CapturingWriteStream();
        var blockerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocker = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scheduler = new VbaInteractiveWorkScheduler(
            options: new VbaInteractiveWorkSchedulerOptions(
                CoalesceSupersededMutations: true,
                MaxOwnedWork: 1));
        var blocker = scheduler.AdmitMutation(async cancellationToken =>
        {
            blockerStarted.TrySetResult();
            await releaseBlocker.Task.WaitAsync(cancellationToken);
        });
        await blockerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()));
        var observer = new BlockingFirstRevisionObserver();
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace,
            observer);
        publisher.AttachScheduler(scheduler);

        var first = Task.Run(
            () => publisher.PublishEmptyDiagnosticsAsync(
                uri,
                CancellationToken.None));
        await observer.FirstRevisionReserved.Task
            .WaitAsync(TimeSpan.FromSeconds(5));
        await publisher.PublishEmptyDiagnosticsAsync(
            uri,
            CancellationToken.None);
        observer.ReleaseFirstRevision();
        await first.WaitAsync(TimeSpan.FromSeconds(5));

        releaseBlocker.TrySetResult();
        await blocker.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await publisher.WaitForIdleAsync(uri)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, output.MessageCount);

        await scheduler.StopAsync(VbaInteractiveStopReason.Complete)
            .WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Project_batch_cannot_replace_a_newer_peer_tombstone_with_a_stale_reservation()
    {
        const string firstUri = "file:///C:/work/First.bas";
        const string secondUri = "file:///C:/work/Second.bas";
        await using var output = new CapturingWriteStream();
        var blockerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocker = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scheduler = new VbaInteractiveWorkScheduler(
            options: new VbaInteractiveWorkSchedulerOptions(
                CoalesceSupersededMutations: true,
                MaxOwnedWork: 1));
        var blocker = scheduler.AdmitMutation(async cancellationToken =>
        {
            blockerStarted.TrySetResult();
            await releaseBlocker.Task.WaitAsync(cancellationToken);
        });
        await blockerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()));
        workspace.OpenDocument(firstUri, 1, "Public Enum RunMode\nEnd Enum\n");
        workspace.OpenDocument(secondUri, 1, "Public Enum runmode\nEnd Enum\n");
        var observer = new BlockingFirstRevisionObserver();
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace,
            observer);
        publisher.AttachScheduler(scheduler);

        var staleBatch = Task.Run(
            () => publisher.PublishProjectDiagnosticsAsync(
                firstUri,
                CancellationToken.None));
        var firstReservedUri = await observer.FirstRevisionReserved.Task
            .WaitAsync(TimeSpan.FromSeconds(5));
        var closingUri = string.Equals(
            firstReservedUri,
            firstUri,
            StringComparison.OrdinalIgnoreCase)
                ? secondUri
                : firstUri;
        Assert.True(workspace.CloseDocument(closingUri));
        await publisher.PublishEmptyDiagnosticsAsync(
            closingUri,
            CancellationToken.None);

        observer.ReleaseFirstRevision();
        await staleBatch.WaitAsync(TimeSpan.FromSeconds(5));
        releaseBlocker.TrySetResult();
        await blocker.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(
                publisher.WaitForIdleAsync(firstUri),
                publisher.WaitForIdleAsync(secondUri))
            .WaitAsync(TimeSpan.FromSeconds(5));

        var notification = Assert.Single(ReadJsonMessages(output.ReadText()));
        var parameters = Assert.IsType<JsonObject>(notification["params"]);
        Assert.Equal(closingUri, parameters["uri"]?.GetValue<string>());
        Assert.Null(parameters["version"]);
        Assert.Empty(Assert.IsType<JsonArray>(parameters["diagnostics"]));
    }

    [Fact]
    public async Task Reconciliation_source_deletion_republishes_project_diagnostics_for_the_survivor()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-reconciliation-diagnostics-").FullName;
        try
        {
            var sourceRoot = Directory.CreateDirectory(Path.Combine(
                projectRoot,
                "src",
                "Book1")).FullName;
            File.WriteAllText(
                Path.Combine(projectRoot, "vba-project.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    projectName = "ReconciliationDiagnostics",
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
            var firstPath = Path.Combine(sourceRoot, "ProjectTypeA.bas");
            var firstText = string.Join('\n', [
                "Attribute VB_Name = \"ProjectTypeA\"",
                "Public Enum RunMode",
                "    FirstMode = 1",
                "End Enum"
            ]);
            File.WriteAllText(firstPath, firstText);
            var secondPath = Path.Combine(sourceRoot, "ProjectTypeB.bas");
            File.WriteAllText(secondPath, string.Join('\n', [
                "Attribute VB_Name = \"ProjectTypeB\"",
                "#If SECOND_CONFIGURATION Then",
                "Public Enum runmode",
                "    SecondMode = 2",
                "End Enum",
                "#End If"
            ]));
            var firstUri = new Uri(Path.GetFullPath(firstPath)).AbsoluteUri;
            var secondUri = new Uri(Path.GetFullPath(secondPath)).AbsoluteUri;
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()));
            workspace.OpenDocument(firstUri, 7, firstText);
            _ = workspace.CreateProjectSnapshot(firstUri);
            await using var output = new CapturingWriteStream();
            await using var scheduler = new VbaInteractiveWorkScheduler(
                options: new VbaInteractiveWorkSchedulerOptions(
                    CoalesceSupersededMutations: true,
                    MaxOwnedWork: 1));
            var publisher = new VbaDiagnosticsPublisher(
                new LspMessageTransport(Stream.Null, output),
                workspace);
            publisher.AttachScheduler(scheduler);
            await publisher.PublishProjectDiagnosticsAsync(
                firstUri,
                CancellationToken.None);
            await publisher.WaitForIdleAsync(firstUri)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains(
                Assert.IsType<JsonArray>(
                    Assert.IsType<JsonObject>(
                        Assert.Single(ReadJsonMessages(output.ReadText()))["params"])
                    ["diagnostics"]),
                diagnostic => Assert.IsType<JsonObject>(diagnostic)["code"]
                    ?.GetValue<string>() == "validation.duplicateDeclaration");

            File.Delete(secondPath);
            await using var reconciler = new VbaProjectReconciler(
                workspace,
                publisher,
                cadence: Timeout.InfiniteTimeSpan);
            reconciler.AttachScheduler(scheduler);
            await reconciler.ReconcileAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(
                    publisher.WaitForIdleAsync(firstUri),
                    publisher.WaitForIdleAsync(secondUri))
                .WaitAsync(TimeSpan.FromSeconds(5));

            var survivorParameters = Assert.IsType<JsonObject>(
                ReadJsonMessages(output.ReadText())
                    .Select(message => Assert.IsType<JsonObject>(message["params"]))
                    .Last(parameters => parameters["uri"]?.GetValue<string>() == firstUri));
            Assert.Equal(7, survivorParameters["version"]?.GetValue<int>());
            Assert.DoesNotContain(
                Assert.IsType<JsonArray>(survivorParameters["diagnostics"]),
                diagnostic => Assert.IsType<JsonObject>(diagnostic)["code"]
                    ?.GetValue<string>() == "validation.duplicateDeclaration");
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Reconciliation_manifest_boundary_split_republishes_diagnostics_for_each_new_project()
    {
        static string CreateManifest(bool split)
        {
            static object CreateDocument(string name, string sourcePath)
                => new
                {
                    kind = "excel",
                    sourcePath,
                    templatePath = $"src/{name}/{name}.xlsm",
                    binPath = $"bin/{name}/{name}.xlsm",
                    publishPath = $"publish/{name}/{name}.xlsm",
                    commonModules = Array.Empty<object>(),
                    references = Array.Empty<object>()
                };

            var documents = split
                ? new Dictionary<string, object>
                {
                    ["Book1"] = CreateDocument("Book1", "src/Book1"),
                    ["Book2"] = CreateDocument("Book2", "src/Book2")
                }
                : new Dictionary<string, object>
                {
                    ["Book1"] = CreateDocument("Book1", "src")
                };
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                projectName = "ReconciliationBoundary",
                primaryDocument = "Book1",
                documents
            });
        }

        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-reconciliation-boundary-").FullName;
        try
        {
            var manifestPath = Path.Combine(projectRoot, "vba-project.json");
            File.WriteAllText(manifestPath, CreateManifest(split: false));
            var firstRoot = Directory.CreateDirectory(Path.Combine(
                projectRoot,
                "src",
                "Book1")).FullName;
            var secondRoot = Directory.CreateDirectory(Path.Combine(
                projectRoot,
                "src",
                "Book2")).FullName;
            var firstPath = Path.Combine(firstRoot, "ProjectTypeA.bas");
            var firstText = string.Join('\n', [
                "Attribute VB_Name = \"ProjectTypeA\"",
                "Public Enum RunMode",
                "    FirstMode = 1",
                "End Enum"
            ]);
            File.WriteAllText(firstPath, firstText);
            var secondPath = Path.Combine(secondRoot, "ProjectTypeB.bas");
            var secondText = string.Join('\n', [
                "Attribute VB_Name = \"ProjectTypeB\"",
                "#If SECOND_CONFIGURATION Then",
                "Public Enum runmode",
                "    SecondMode = 2",
                "End Enum",
                "#End If"
            ]);
            File.WriteAllText(secondPath, secondText);
            var firstUri = new Uri(Path.GetFullPath(firstPath)).AbsoluteUri;
            var secondUri = new Uri(Path.GetFullPath(secondPath)).AbsoluteUri;
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()));
            workspace.OpenDocument(firstUri, 7, firstText);
            workspace.OpenDocument(secondUri, 11, secondText);
            _ = workspace.CreateProjectSnapshot(firstUri);
            await using var output = new CapturingWriteStream();
            await using var scheduler = new VbaInteractiveWorkScheduler(
                options: new VbaInteractiveWorkSchedulerOptions(
                    CoalesceSupersededMutations: true,
                    MaxOwnedWork: 1));
            var publisher = new VbaDiagnosticsPublisher(
                new LspMessageTransport(Stream.Null, output),
                workspace);
            publisher.AttachScheduler(scheduler);
            await publisher.PublishProjectDiagnosticsAsync(
                firstUri,
                CancellationToken.None);
            await Task.WhenAll(
                    publisher.WaitForIdleAsync(firstUri),
                    publisher.WaitForIdleAsync(secondUri))
                .WaitAsync(TimeSpan.FromSeconds(5));
            var baselineMessages = ReadJsonMessages(output.ReadText());
            foreach (var uri in new[] { firstUri, secondUri })
            {
                var parameters = Assert.IsType<JsonObject>(
                    baselineMessages
                        .Select(message => Assert.IsType<JsonObject>(message["params"]))
                        .Last(candidate => candidate["uri"]?.GetValue<string>() == uri));
                Assert.Contains(
                    Assert.IsType<JsonArray>(parameters["diagnostics"]),
                    diagnostic => Assert.IsType<JsonObject>(diagnostic)["code"]
                        ?.GetValue<string>() == "validation.duplicateDeclaration");
            }

            File.WriteAllText(manifestPath, CreateManifest(split: true));
            await using var reconciler = new VbaProjectReconciler(
                workspace,
                publisher,
                cadence: Timeout.InfiniteTimeSpan);
            reconciler.AttachScheduler(scheduler);
            await reconciler.ReconcileAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(
                    publisher.WaitForIdleAsync(firstUri),
                    publisher.WaitForIdleAsync(secondUri))
                .WaitAsync(TimeSpan.FromSeconds(5));

            var refreshParameters = ReadJsonMessages(output.ReadText())
                .Skip(baselineMessages.Count)
                .Select(message => Assert.IsType<JsonObject>(message["params"]))
                .Where(parameters => new[] { firstUri, secondUri }.Contains(
                    parameters["uri"]?.GetValue<string>(),
                    StringComparer.OrdinalIgnoreCase))
                .ToArray();
            foreach (var expected in new[] { (firstUri, 7), (secondUri, 11) })
            {
                var parameters = refreshParameters.Last(candidate =>
                    candidate["uri"]?.GetValue<string>() == expected.Item1);
                Assert.Equal(expected.Item2, parameters["version"]?.GetValue<int>());
                Assert.DoesNotContain(
                    Assert.IsType<JsonArray>(parameters["diagnostics"]),
                    diagnostic => Assert.IsType<JsonObject>(diagnostic)["code"]
                        ?.GetValue<string>() == "validation.duplicateDeclaration");
            }
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Reconciliation_manifest_expansion_republishes_diagnostics_for_a_newly_owned_project()
    {
        static string CreateManifest(bool includeSecondDocument)
        {
            static object CreateDocument(string name)
                => new
                {
                    kind = "excel",
                    sourcePath = $"src/{name}",
                    templatePath = $"src/{name}/{name}.xlsm",
                    binPath = $"bin/{name}/{name}.xlsm",
                    publishPath = $"publish/{name}/{name}.xlsm",
                    commonModules = Array.Empty<object>(),
                    references = Array.Empty<object>()
                };

            var documents = new Dictionary<string, object>
            {
                ["Book1"] = CreateDocument("Book1")
            };
            if (includeSecondDocument)
            {
                documents["Book2"] = CreateDocument("Book2");
            }

            return System.Text.Json.JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                projectName = "ReconciliationExpansion",
                primaryDocument = "Book1",
                documents
            });
        }

        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-reconciliation-expansion-").FullName;
        try
        {
            var manifestPath = Path.Combine(projectRoot, "vba-project.json");
            File.WriteAllText(
                manifestPath,
                CreateManifest(includeSecondDocument: false));
            var firstPath = Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "First.bas");
            var secondPath = Path.Combine(
                projectRoot,
                "src",
                "Book2",
                "Second.bas");
            Directory.CreateDirectory(Path.GetDirectoryName(firstPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(secondPath)!);
            const string firstText = "Attribute VB_Name = \"First\"\n"
                + "Public Sub RunFirst()\n"
                + "End Sub\n";
            const string secondText = "Attribute VB_Name = \"Second\"\n"
                + "Public Sub RunSecond()\n"
                + "End Sub\n";
            File.WriteAllText(firstPath, firstText);
            File.WriteAllText(secondPath, secondText);
            var firstUri = new Uri(Path.GetFullPath(firstPath)).AbsoluteUri;
            var secondUri = new Uri(Path.GetFullPath(secondPath)).AbsoluteUri;
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()));
            workspace.OpenDocument(firstUri, 7, firstText);
            workspace.OpenDocument(secondUri, 11, secondText);
            _ = workspace.CreateProjectSnapshot(firstUri);
            _ = workspace.CreateProjectSnapshot(secondUri);
            await using var output = new CapturingWriteStream();
            await using var scheduler = new VbaInteractiveWorkScheduler(
                options: new VbaInteractiveWorkSchedulerOptions(
                    CoalesceSupersededMutations: true,
                    MaxOwnedWork: 1));
            var publisher = new VbaDiagnosticsPublisher(
                new LspMessageTransport(Stream.Null, output),
                workspace);
            publisher.AttachScheduler(scheduler);

            File.WriteAllText(
                manifestPath,
                CreateManifest(includeSecondDocument: true));
            await using var reconciler = new VbaProjectReconciler(
                workspace,
                publisher,
                cadence: Timeout.InfiniteTimeSpan);
            reconciler.AttachScheduler(scheduler);
            await reconciler.ReconcileAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(
                    publisher.WaitForIdleAsync(firstUri),
                    publisher.WaitForIdleAsync(secondUri))
                .WaitAsync(TimeSpan.FromSeconds(5));

            var publishedUris = ReadJsonMessages(output.ReadText())
                .Select(message => Assert.IsType<JsonObject>(message["params"]))
                .Select(parameters => parameters["uri"]?.GetValue<string>())
                .ToArray();
            Assert.Contains(firstUri, publishedUris);
            Assert.Contains(secondUri, publishedUris);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Manifest_change_refreshes_only_sources_owned_by_that_manifest()
    {
        static string CreateManifest(bool split)
        {
            static object CreateDocument(string name, string sourcePath)
                => new
                {
                    kind = "excel",
                    sourcePath,
                    templatePath = $"src/{name}/{name}.xlsm",
                    binPath = $"bin/{name}/{name}.xlsm",
                    publishPath = $"publish/{name}/{name}.xlsm",
                    commonModules = Array.Empty<object>(),
                    references = Array.Empty<object>()
                };

            var documents = split
                ? new Dictionary<string, object>
                {
                    ["Book1"] = CreateDocument("Book1", "src/Book1"),
                    ["Book2"] = CreateDocument("Book2", "src/Book2")
                }
                : new Dictionary<string, object>
                {
                    ["Book1"] = CreateDocument("Book1", "src")
                };
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                projectName = "ScopedManifestRefresh",
                primaryDocument = "Book1",
                documents
            });
        }

        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-scoped-manifest-refresh-").FullName;
        var unrelatedRoot = Directory.CreateTempSubdirectory(
            "vba-ls-unrelated-manifest-refresh-").FullName;
        try
        {
            var manifestPath = Path.Combine(projectRoot, "vba-project.json");
            File.WriteAllText(manifestPath, CreateManifest(split: false));
            var firstPath = Path.Combine(projectRoot, "src", "Book1", "First.bas");
            var secondPath = Path.Combine(projectRoot, "src", "Book2", "Second.bas");
            Directory.CreateDirectory(Path.GetDirectoryName(firstPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(secondPath)!);
            var firstText = "Attribute VB_Name = \"First\"\nPublic Sub RunFirst()\nEnd Sub\n";
            var secondText = "Attribute VB_Name = \"Second\"\nPublic Sub RunSecond()\nEnd Sub\n";
            File.WriteAllText(firstPath, firstText);
            File.WriteAllText(secondPath, secondText);
            var unrelatedPath = Path.Combine(unrelatedRoot, "Unrelated.bas");
            var unrelatedText = "Attribute VB_Name = \"Unrelated\"\nPublic Sub Run()\nEnd Sub\n";
            File.WriteAllText(unrelatedPath, unrelatedText);
            var firstUri = new Uri(Path.GetFullPath(firstPath)).AbsoluteUri;
            var secondUri = new Uri(Path.GetFullPath(secondPath)).AbsoluteUri;
            var unrelatedUri = new Uri(Path.GetFullPath(unrelatedPath)).AbsoluteUri;
            var manifestUri = new Uri(Path.GetFullPath(manifestPath)).AbsoluteUri;
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()));
            workspace.OpenDocument(firstUri, 7, firstText);
            workspace.UpdateDocument(secondUri, secondText);
            workspace.OpenDocument(unrelatedUri, 13, unrelatedText);
            _ = workspace.CreateProjectSnapshot(firstUri);
            _ = workspace.CreateProjectSnapshot(unrelatedUri);
            await using var output = new CapturingWriteStream();
            await using var scheduler = new VbaInteractiveWorkScheduler(
                options: new VbaInteractiveWorkSchedulerOptions(
                    CoalesceSupersededMutations: true,
                    MaxOwnedWork: 1));
            var publisher = new VbaDiagnosticsPublisher(
                new LspMessageTransport(Stream.Null, output),
                workspace);
            publisher.AttachScheduler(scheduler);
            var pipeline = new VbaDocumentChangePipeline(
                workspace,
                new RecordingReferenceCatalogLifecycle(),
                publisher);

            await pipeline.ApplyAsync(
                new VbaTextDocumentOpenedChange(
                    manifestUri,
                    20,
                    CreateManifest(split: true)),
                CancellationToken.None);
            await Task.WhenAll(
                    publisher.WaitForIdleAsync(manifestUri),
                    publisher.WaitForIdleAsync(firstUri),
                    publisher.WaitForIdleAsync(secondUri),
                    publisher.WaitForIdleAsync(unrelatedUri))
                .WaitAsync(TimeSpan.FromSeconds(5));

            var publishedUris = ReadJsonMessages(output.ReadText())
                .Select(message => Assert.IsType<JsonObject>(message["params"]))
                .Select(parameters => parameters["uri"]?.GetValue<string>())
                .ToArray();
            Assert.Contains(firstUri, publishedUris);
            Assert.Contains(secondUri, publishedUris);
            Assert.DoesNotContain(unrelatedUri, publishedUris);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(unrelatedRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Reconciliation_manifest_split_republishes_diagnostics_for_a_sibling_materialized_scope()
    {
        static string CreateManifest(bool split)
        {
            static object CreateDocument(string name, string sourcePath)
                => new
                {
                    kind = "excel",
                    sourcePath,
                    templatePath = $"src/{name}/{name}.xlsm",
                    binPath = $"bin/{name}/{name}.xlsm",
                    publishPath = $"publish/{name}/{name}.xlsm",
                    commonModules = Array.Empty<object>(),
                    references = Array.Empty<object>()
                };

            var documents = split
                ? new Dictionary<string, object>
                {
                    ["Book1A"] = CreateDocument("Book1A", "src/Book1/A"),
                    ["Book1B"] = CreateDocument("Book1B", "src/Book1/B"),
                    ["Book2A"] = CreateDocument("Book2A", "src/Book2/A"),
                    ["Book2B"] = CreateDocument("Book2B", "src/Book2/B")
                }
                : new Dictionary<string, object>
                {
                    ["Book1"] = CreateDocument("Book1", "src/Book1"),
                    ["Book2"] = CreateDocument("Book2", "src/Book2")
                };
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                projectName = "SiblingReconciliationBoundary",
                primaryDocument = split ? "Book1A" : "Book1",
                documents
            });
        }

        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-reconciliation-sibling-boundary-").FullName;
        try
        {
            var manifestPath = Path.Combine(projectRoot, "vba-project.json");
            File.WriteAllText(manifestPath, CreateManifest(split: false));
            var sourceSpecifications = new[]
            {
                ("Book1/A", "ProjectTypeA.bas", "RunMode1", 7),
                ("Book1/B", "ProjectTypeB.bas", "runmode1", 8),
                ("Book2/A", "ProjectTypeC.bas", "RunMode2", 11),
                ("Book2/B", "ProjectTypeD.bas", "runmode2", 12)
            };
            var sources = sourceSpecifications
                .Select(specification =>
                {
                    var sourceRoot = Directory.CreateDirectory(Path.Combine(
                        projectRoot,
                        "src",
                        specification.Item1.Replace('/', Path.DirectorySeparatorChar)))
                        .FullName;
                    var path = Path.Combine(sourceRoot, specification.Item2);
                    var text = string.Join('\n', [
                        $"Attribute VB_Name = \"{Path.GetFileNameWithoutExtension(path)}\"",
                        $"Public Enum {specification.Item3}",
                        "    FirstMode = 1",
                        "End Enum"
                    ]);
                    File.WriteAllText(path, text);
                    return (
                        Uri: new Uri(Path.GetFullPath(path)).AbsoluteUri,
                        Text: text,
                        Version: specification.Item4);
                })
                .ToArray();
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()));
            foreach (var source in sources)
            {
                workspace.OpenDocument(source.Uri, source.Version, source.Text);
            }

            _ = workspace.CreateProjectSnapshot(sources[0].Uri);
            _ = workspace.CreateProjectSnapshot(sources[2].Uri);
            await using var output = new CapturingWriteStream();
            await using var scheduler = new VbaInteractiveWorkScheduler(
                options: new VbaInteractiveWorkSchedulerOptions(
                    CoalesceSupersededMutations: true,
                    MaxOwnedWork: 1));
            var publisher = new VbaDiagnosticsPublisher(
                new LspMessageTransport(Stream.Null, output),
                workspace);
            publisher.AttachScheduler(scheduler);
            await publisher.PublishProjectDiagnosticsAsync(
                sources[0].Uri,
                CancellationToken.None);
            await publisher.PublishProjectDiagnosticsAsync(
                sources[2].Uri,
                CancellationToken.None);
            await Task.WhenAll(sources.Select(source =>
                    publisher.WaitForIdleAsync(source.Uri)))
                .WaitAsync(TimeSpan.FromSeconds(5));
            var baselineMessages = ReadJsonMessages(output.ReadText());
            foreach (var source in sources)
            {
                var parameters = Assert.IsType<JsonObject>(
                    baselineMessages
                        .Select(message => Assert.IsType<JsonObject>(message["params"]))
                        .Last(candidate => candidate["uri"]?.GetValue<string>()
                            == source.Uri));
                Assert.Contains(
                    Assert.IsType<JsonArray>(parameters["diagnostics"]),
                    diagnostic => Assert.IsType<JsonObject>(diagnostic)["code"]
                        ?.GetValue<string>() == "validation.duplicateDeclaration");
            }

            File.WriteAllText(manifestPath, CreateManifest(split: true));
            await using var reconciler = new VbaProjectReconciler(
                workspace,
                publisher,
                cadence: Timeout.InfiniteTimeSpan);
            reconciler.AttachScheduler(scheduler);
            await reconciler.ReconcileAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(sources.Select(source =>
                    publisher.WaitForIdleAsync(source.Uri)))
                .WaitAsync(TimeSpan.FromSeconds(5));

            var refreshParameters = ReadJsonMessages(output.ReadText())
                .Skip(baselineMessages.Count)
                .Select(message => Assert.IsType<JsonObject>(message["params"]))
                .Where(parameters => sources.Any(source =>
                    string.Equals(
                        source.Uri,
                        parameters["uri"]?.GetValue<string>(),
                        StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            foreach (var source in sources)
            {
                var parameters = refreshParameters.Last(candidate =>
                    candidate["uri"]?.GetValue<string>() == source.Uri);
                Assert.Equal(
                    source.Version,
                    parameters["version"]?.GetValue<int>());
                Assert.DoesNotContain(
                    Assert.IsType<JsonArray>(parameters["diagnostics"]),
                    diagnostic => Assert.IsType<JsonObject>(diagnostic)["code"]
                        ?.GetValue<string>() == "validation.duplicateDeclaration");
            }
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Reconciliation_decode_failure_publishes_encoding_diagnostic_with_a_tracked_project_peer()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-reconciliation-encoding-").FullName;
        try
        {
            var sourceRoot = Directory.CreateDirectory(Path.Combine(
                projectRoot,
                "src",
                "Book1")).FullName;
            File.WriteAllText(
                Path.Combine(projectRoot, "vba-project.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    projectName = "ReconciliationEncoding",
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
            var callerPath = Path.Combine(sourceRoot, "Caller.bas");
            const string callerText = "Attribute VB_Name = \"Caller\"\n"
                + "Public Sub Run()\n"
                + "End Sub\n";
            File.WriteAllText(callerPath, callerText);
            var helperPath = Path.Combine(sourceRoot, "Helper.bas");
            File.WriteAllText(
                helperPath,
                "Attribute VB_Name = \"Helper\"\n"
                    + "Public Sub Work()\n"
                    + "End Sub\n");
            var callerUri = new Uri(Path.GetFullPath(callerPath)).AbsoluteUri;
            var helperUri = new Uri(Path.GetFullPath(helperPath)).AbsoluteUri;
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()),
                NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
                NullVbaDocumentAnalysisBuildObserver.Instance,
                NullVbaProjectSnapshotBuildObserver.Instance,
                SystemVbaProjectFileSystem.Instance,
                reconciliationAuthorityLeaseObserver: null,
                sourceDecoding: new DiskSourceDecoding(
                    supportsLegacyFallback: false,
                    activeCodePage: 65001));
            workspace.OpenDocument(callerUri, 7, callerText);
            _ = workspace.CreateProjectSnapshot(callerUri);
            File.WriteAllBytes(helperPath, [0xC3, 0x28]);
            await using var output = new CapturingWriteStream();
            await using var scheduler = new VbaInteractiveWorkScheduler(
                options: new VbaInteractiveWorkSchedulerOptions(
                    CoalesceSupersededMutations: true,
                    MaxOwnedWork: 1));
            var publisher = new VbaDiagnosticsPublisher(
                new LspMessageTransport(Stream.Null, output),
                workspace);
            publisher.AttachScheduler(scheduler);
            await using var reconciler = new VbaProjectReconciler(
                workspace,
                publisher,
                cadence: Timeout.InfiniteTimeSpan);
            reconciler.AttachScheduler(scheduler);

            await reconciler.ReconcileAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(
                    publisher.WaitForIdleAsync(callerUri),
                    publisher.WaitForIdleAsync(helperUri))
                .WaitAsync(TimeSpan.FromSeconds(5));

            var parameters = Assert.IsType<JsonObject>(
                ReadJsonMessages(output.ReadText())
                    .Select(message => Assert.IsType<JsonObject>(message["params"]))
                    .Last(candidate => candidate["uri"]?.GetValue<string>() == helperUri));
            var diagnostic = Assert.IsType<JsonObject>(
                Assert.Single(Assert.IsType<JsonArray>(parameters["diagnostics"])));
            Assert.Equal(
                "invalid-disk-source-encoding",
                diagnostic["code"]?.GetValue<string>());
            Assert.Contains(
                helperPath,
                diagnostic["message"]?.GetValue<string>());
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Project_snapshot_invalidated_during_build_cannot_publish_stale_diagnostics()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-stale-project-diagnostics-").FullName;
        var buildObserver = new BlockingFirstProjectSnapshotBuildObserver();
        TaskCompletionSource? releaseSchedulerBlocker = null;
        try
        {
            var sourceRoot = Directory.CreateDirectory(Path.Combine(
                projectRoot,
                "src",
                "Book1")).FullName;
            File.WriteAllText(
                Path.Combine(projectRoot, "vba-project.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    projectName = "StaleProjectDiagnostics",
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
            var firstPath = Path.Combine(sourceRoot, "ProjectTypeA.bas");
            var firstText = string.Join('\n', [
                "Attribute VB_Name = \"ProjectTypeA\"",
                "Public Enum RunMode",
                "    FirstMode = 1",
                "End Enum"
            ]);
            File.WriteAllText(firstPath, firstText);
            var secondPath = Path.Combine(sourceRoot, "ProjectTypeB.bas");
            File.WriteAllText(secondPath, string.Join('\n', [
                "Attribute VB_Name = \"ProjectTypeB\"",
                "#If SECOND_CONFIGURATION Then",
                "Public Enum runmode",
                "    SecondMode = 2",
                "End Enum",
                "#End If"
            ]));
            var firstUri = new Uri(Path.GetFullPath(firstPath)).AbsoluteUri;
            var secondUri = new Uri(Path.GetFullPath(secondPath)).AbsoluteUri;
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()),
                NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
                NullVbaDocumentAnalysisBuildObserver.Instance,
                buildObserver);
            workspace.OpenDocument(firstUri, 7, firstText);
            await using var output = new CapturingWriteStream();
            var blockerStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            releaseSchedulerBlocker = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            await using var scheduler = new VbaInteractiveWorkScheduler(
                options: new VbaInteractiveWorkSchedulerOptions(
                    CoalesceSupersededMutations: true,
                    MaxOwnedWork: 1));
            var blocker = scheduler.AdmitMutation(async cancellationToken =>
            {
                blockerStarted.TrySetResult();
                await releaseSchedulerBlocker.Task.WaitAsync(
                    cancellationToken);
            });
            await blockerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var publisher = new VbaDiagnosticsPublisher(
                new LspMessageTransport(Stream.Null, output),
                workspace);
            publisher.AttachScheduler(scheduler);

            var stalePublication = Task.Run(
                () => publisher.PublishProjectDiagnosticsAsync(
                    firstUri,
                    CancellationToken.None));
            await buildObserver.FirstBuildWaiting.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(workspace.DeleteSourceDocument(secondUri));
            await publisher.PublishProjectDiagnosticsAsync(
                firstUri,
                CancellationToken.None);
            buildObserver.ReleaseFirstBuild();
            await stalePublication.WaitAsync(TimeSpan.FromSeconds(5));
            releaseSchedulerBlocker.TrySetResult();
            await blocker.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            await publisher.WaitForIdleAsync(firstUri)
                .WaitAsync(TimeSpan.FromSeconds(5));

            var parameters = Assert.IsType<JsonObject>(
                Assert.Single(ReadJsonMessages(output.ReadText()))["params"]);
            Assert.Equal(firstUri, parameters["uri"]?.GetValue<string>());
            Assert.Equal(7, parameters["version"]?.GetValue<int>());
            Assert.DoesNotContain(
                Assert.IsType<JsonArray>(parameters["diagnostics"]),
                diagnostic => Assert.IsType<JsonObject>(diagnostic)["code"]
                    ?.GetValue<string>() == "validation.duplicateDeclaration");
        }
        finally
        {
            buildObserver.ReleaseFirstBuild();
            releaseSchedulerBlocker?.TrySetResult();
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Template_change_after_diagnostic_capture_prevents_stale_module_identity_conflict_publication()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-stale-template-diagnostics-").FullName;
        TaskCompletionSource? releaseSchedulerBlocker = null;
        try
        {
            var sourceRoot = Directory.CreateDirectory(Path.Combine(
                projectRoot,
                "src",
                "Book1")).FullName;
            var templatePath = Path.Combine(sourceRoot, "Book1.xlsm");
            var templateBytes = new byte[] { 0x11, 0x33, 0x55, 0x77 };
            File.WriteAllBytes(templatePath, templateBytes);
            File.WriteAllText(
                Path.Combine(projectRoot, "vba-project.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    projectName = "TemplateDiagnostics",
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
            var sourcePath = Path.Combine(sourceRoot, "SourceUnit.bas");
            const string text = "Attribute VB_Name = \"ContainingProject\"";
            File.WriteAllText(sourcePath, text);
            var uri = new Uri(Path.GetFullPath(sourcePath)).AbsoluteUri;
            var context = new VbaHostClassProjectionContext(
                Path.GetFullPath(projectRoot),
                "Book1",
                Path.GetFullPath(templatePath));
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()));
            workspace.OpenDocument(uri, 7, text);
            _ = workspace.CreateProjectSnapshot(uri);
            Assert.True(workspace.TryApplyHostClassProjectionSnapshot(
                new VbaHostClassProjectionSnapshotUpdate(
                    context,
                    Revision: 1,
                    new VbaHostClassProjectionSnapshot(
                        Revision: 1,
                        context,
                        ClassEnumerationComplete: true,
                        Classes: [],
                        VbaProjectName: "ContainingProject",
                        SourceTemplateFingerprint: Convert.ToHexString(
                            System.Security.Cryptography.SHA256.HashData(
                                templateBytes))))));
            var diagnosticSnapshot = Assert.Single(
                workspace.GetProjectDiagnosticsSnapshots(uri)!);
            Assert.Contains(
                diagnosticSnapshot.ProjectValidationDiagnostics,
                diagnostic => diagnostic.Code
                    == "validation.moduleIdentityNameConflict");

            await using var output = new CapturingWriteStream();
            var blockerStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            releaseSchedulerBlocker = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            await using var scheduler = new VbaInteractiveWorkScheduler(
                options: new VbaInteractiveWorkSchedulerOptions(
                    CoalesceSupersededMutations: true,
                    MaxOwnedWork: 1));
            var blocker = scheduler.AdmitMutation(async cancellationToken =>
            {
                blockerStarted.TrySetResult();
                await releaseSchedulerBlocker.Task.WaitAsync(cancellationToken);
            });
            await blockerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var publisher = new VbaDiagnosticsPublisher(
                new LspMessageTransport(Stream.Null, output),
                workspace);
            publisher.AttachScheduler(scheduler);

            await publisher.PublishProjectDiagnosticsAsync(
                uri,
                CancellationToken.None);
            File.WriteAllBytes(templatePath, [0x22, 0x44, 0x66, 0x88]);
            releaseSchedulerBlocker.TrySetResult();
            await blocker.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            await publisher.WaitForIdleAsync(uri)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Empty(ReadJsonMessages(output.ReadText()));
        }
        finally
        {
            releaseSchedulerBlocker?.TrySetResult();
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Project_capture_unavailable_while_peer_builds_does_not_publish_document_only_clear()
    {
        const string firstUri = "file:///C:/work/First.bas";
        const string secondUri = "file:///C:/work/Second.bas";
        const string firstText = "Public Enum RunMode\nEnd Enum\n";
        const string secondText = "Public Enum runmode\nEnd Enum\n";
        var buildObserver = new BlockingNextDocumentAnalysisBuildObserver();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            buildObserver);
        workspace.OpenDocument(firstUri, 1, firstText);
        workspace.OpenDocument(secondUri, 1, secondText);
        await using var output = new CapturingWriteStream();
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        publisher.AttachScheduler(scheduler);
        await publisher.PublishProjectDiagnosticsAsync(
            firstUri,
            CancellationToken.None);
        await Task.WhenAll(
                publisher.WaitForIdleAsync(firstUri),
                publisher.WaitForIdleAsync(secondUri))
            .WaitAsync(TimeSpan.FromSeconds(5));
        var baselineMessageCount = output.MessageCount;
        Assert.Equal(2, baselineMessageCount);

        buildObserver.BlockNextBuild();
        var change = Task.Run(() => workspace.ChangeDocument(
            secondUri,
            2,
            secondText + "' pending change\n"));
        await buildObserver.BuildStarted.Task
            .WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await publisher.PublishProjectDiagnosticsAsync(
                firstUri,
                CancellationToken.None);
            await publisher.WaitForIdleAsync(firstUri)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(baselineMessageCount, output.MessageCount);
        }
        finally
        {
            buildObserver.ReleaseBuild();
        }

        Assert.True(await change.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Publication_observer_failure_cannot_strand_pending_diagnostics()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()));
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, Stream.Null),
            workspace,
            new ThrowingPublicationObserver());
        publisher.AttachScheduler(scheduler);

        Assert.Throws<InvalidOperationException>(
            () =>
            {
                _ = publisher.PublishEmptyDiagnosticsAsync(
                    uri,
                    CancellationToken.None);
            });
        await publisher.WaitForIdleAsync(uri)
            .WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Manifest_diagnostics_do_not_block_the_ordered_mutation_lane()
    {
        const string uri = "file:///C:/work/vba-project.json";
        await using var output = new BlockingWriteStream();
        var timingSink = new SignallingTimingSink();
        await using var scheduler = new VbaInteractiveWorkScheduler(timingSink);
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        publisher.AttachScheduler(scheduler);
        var pipeline = new VbaDocumentChangePipeline(
            workspace,
            new RecordingReferenceCatalogLifecycle(),
            publisher);
        var mutation = scheduler.AdmitMutation(
            "textDocument/didOpen",
            cancellationToken => pipeline.ApplyAsync(
                new VbaTextDocumentOpenedChange(uri, 1, "{"),
                cancellationToken));

        await mutation.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await timingSink.WaitForAdmissionAsync("textDocument/diagnostic");
        await output.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = scheduler.StopAsync(VbaInteractiveStopReason.Complete);

        Assert.True(mutation.Completion.IsCompletedSuccessfully);
        Assert.False(stop.IsCompleted);

        output.ReleaseWrites();
        await timingSink.WaitForCompletionAsync("textDocument/diagnostic");
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TrackedDiagnosticsCarryClientDocumentVersion()
    {
        const string uri = "file:///C:/work/Worker.bas";
        await using var output = new CapturingWriteStream();
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        publisher.AttachScheduler(scheduler);
        workspace.OpenDocument(
            uri,
            7,
            "Public Sub Run()\n    ");

        await publisher.PublishTrackedDiagnosticsAsync(uri, CancellationToken.None);

        var messages = ReadJsonMessages(
            await output.WaitForMessageCountAsync(1));
        var parameters = Assert.IsType<JsonObject>(
            messages.Single()["params"]);
        Assert.Equal(7, parameters["version"]?.GetValue<int>());
        Assert.NotNull(parameters["diagnostics"]);
    }

    [Fact]
    public async Task Watched_invalid_closed_source_publishes_actionable_encoding_diagnostic()
    {
        var sourcePath = Path.Combine(
            Directory.CreateTempSubdirectory("vba-ls-invalid-source-").FullName,
            "Worker.bas");
        try
        {
            File.WriteAllBytes(sourcePath, [0xC3, 0x28]);
            var uri = new Uri(sourcePath).AbsoluteUri;
            await using var output = new CapturingWriteStream();
            await using var scheduler = new VbaInteractiveWorkScheduler();
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()),
                new DiskSourceDecoding(
                    supportsLegacyFallback: false,
                    activeCodePage: 65001));
            var publisher = new VbaDiagnosticsPublisher(
                new LspMessageTransport(Stream.Null, output),
                workspace);
            publisher.AttachScheduler(scheduler);
            var pipeline = new VbaDocumentChangePipeline(
                workspace,
                new RecordingReferenceCatalogLifecycle(),
                publisher);

            await pipeline.ApplyAsync(
                new VbaWatchedFileReloadChange(uri),
                CancellationToken.None);

            var parameters = Assert.IsType<JsonObject>(
                Assert.Single(ReadJsonMessages(
                    await output.WaitForMessageCountAsync(1)))["params"]);
            Assert.Equal(uri, parameters["uri"]?.GetValue<string>());
            var diagnostic = Assert.IsType<JsonObject>(
                Assert.Single(Assert.IsType<JsonArray>(
                    parameters["diagnostics"])));
            Assert.Equal(
                "invalid-disk-source-encoding",
                diagnostic["code"]?.GetValue<string>());
            Assert.Contains(
                sourcePath,
                diagnostic["message"]?.GetValue<string>());
            Assert.Null(workspace.GetDocumentText(uri));
        }
        finally
        {
            Directory.Delete(
                Path.GetDirectoryName(sourcePath)!,
                recursive: true);
        }
    }

    [Fact]
    public async Task Cold_snapshot_publishes_encoding_diagnostic_for_invalid_closed_helper()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-invalid-cold-source-").FullName;
        try
        {
            var callerPath = Path.Combine(projectRoot, "Caller.bas");
            var helperPath = Path.Combine(projectRoot, "Helper.bas");
            var callerUri = new Uri(callerPath).AbsoluteUri;
            var helperUri = new Uri(helperPath).AbsoluteUri;
            const string callerText = "Public Sub Run()\nEnd Sub\n";
            File.WriteAllText(callerPath, callerText);
            File.WriteAllBytes(helperPath, [0xC3, 0x28]);
            await using var output = new CapturingWriteStream();
            await using var scheduler = new VbaInteractiveWorkScheduler();
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()),
                new DiskSourceDecoding(
                    supportsLegacyFallback: false,
                    activeCodePage: 65001));
            workspace.OpenDocument(callerUri, version: 1, callerText);
            var publisher = new VbaDiagnosticsPublisher(
                new LspMessageTransport(Stream.Null, output),
                workspace);
            publisher.AttachScheduler(scheduler);

            var snapshot = workspace.CreateProjectSnapshot(callerUri);

            Assert.DoesNotContain(
                helperUri,
                snapshot.SourceDocuments.Keys);
            await Task.WhenAll(
                    publisher.WaitForIdleAsync(callerUri),
                    publisher.WaitForIdleAsync(helperUri))
                .WaitAsync(TimeSpan.FromSeconds(5));
            var initialMessages = ReadJsonMessages(output.ReadText());
            var helperMessage = Assert.Single(
                initialMessages,
                message => Assert.IsType<JsonObject>(message["params"])
                    ["uri"]?.GetValue<string>() == helperUri);
            var parameters = Assert.IsType<JsonObject>(
                helperMessage["params"]);
            Assert.Equal(helperUri, parameters["uri"]?.GetValue<string>());
            var diagnostic = Assert.IsType<JsonObject>(
                Assert.Single(Assert.IsType<JsonArray>(
                    parameters["diagnostics"])));
            Assert.Equal(
                "invalid-disk-source-encoding",
                diagnostic["code"]?.GetValue<string>());

            File.WriteAllText(
                helperPath,
                "Public Sub Helper()\nEnd Sub\n");
            Assert.True(workspace.ChangeDocument(
                callerUri,
                version: 2,
                callerText));
            _ = workspace.CreateProjectSnapshot(callerUri);
            await Task.WhenAll(
                    publisher.WaitForIdleAsync(callerUri),
                    publisher.WaitForIdleAsync(helperUri))
                .WaitAsync(TimeSpan.FromSeconds(5));

            var recoveryParameters = ReadJsonMessages(output.ReadText())
                .Skip(initialMessages.Count)
                .Select(message => Assert.IsType<JsonObject>(message["params"]))
                .Last(parameters => parameters["uri"]?.GetValue<string>()
                    == helperUri);
            Assert.Empty(Assert.IsType<JsonArray>(
                recoveryParameters["diagnostics"]));
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Cold_encoding_failure_refreshes_project_diagnostics_for_an_open_peer()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-cold-encoding-project-refresh-").FullName;
        try
        {
            var callerPath = Path.Combine(projectRoot, "Caller.bas");
            var helperPath = Path.Combine(projectRoot, "Helper.bas");
            var callerUri = new Uri(callerPath).AbsoluteUri;
            var helperUri = new Uri(helperPath).AbsoluteUri;
            const string callerText = "Attribute VB_Name = \"Caller\"\n"
                + "Public Enum RunMode\n"
                + "    CallerMode = 1\n"
                + "End Enum\n";
            const string helperText = "Attribute VB_Name = \"Helper\"\n"
                + "Public Enum runmode\n"
                + "    HelperMode = 2\n"
                + "End Enum\n";
            File.WriteAllText(callerPath, callerText);
            File.WriteAllText(helperPath, helperText);
            await using var output = new CapturingWriteStream();
            await using var scheduler = new VbaInteractiveWorkScheduler();
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()),
                new DiskSourceDecoding(
                    supportsLegacyFallback: false,
                    activeCodePage: 65001));
            workspace.OpenDocument(callerUri, version: 7, callerText);
            var publisher = new VbaDiagnosticsPublisher(
                new LspMessageTransport(Stream.Null, output),
                workspace);
            publisher.AttachScheduler(scheduler);

            await publisher.PublishProjectDiagnosticsAsync(
                callerUri,
                CancellationToken.None);
            await publisher.WaitForIdleAsync(callerUri)
                .WaitAsync(TimeSpan.FromSeconds(5));
            var baselineMessages = ReadJsonMessages(output.ReadText());
            var baselineParameters = Assert.IsType<JsonObject>(
                baselineMessages
                    .Select(message => Assert.IsType<JsonObject>(message["params"]))
                    .Last(parameters => parameters["uri"]?.GetValue<string>()
                        == callerUri));
            Assert.Contains(
                Assert.IsType<JsonArray>(baselineParameters["diagnostics"]),
                diagnostic => Assert.IsType<JsonObject>(diagnostic)["code"]
                    ?.GetValue<string>() == "validation.duplicateDeclaration");

            File.WriteAllBytes(helperPath, [0xC3, 0x28]);
            Assert.True(workspace.ChangeDocument(
                callerUri,
                version: 8,
                callerText));
            await publisher.PublishProjectDiagnosticsAsync(
                callerUri,
                CancellationToken.None);
            await Task.WhenAll(
                    publisher.WaitForIdleAsync(callerUri),
                    publisher.WaitForIdleAsync(helperUri))
                .WaitAsync(TimeSpan.FromSeconds(5));

            var refreshParameters = ReadJsonMessages(output.ReadText())
                .Skip(baselineMessages.Count)
                .Select(message => Assert.IsType<JsonObject>(message["params"]))
                .ToArray();
            var callerParameters = refreshParameters.Last(parameters =>
                parameters["uri"]?.GetValue<string>() == callerUri);
            Assert.Equal(8, callerParameters["version"]?.GetValue<int>());
            Assert.DoesNotContain(
                Assert.IsType<JsonArray>(callerParameters["diagnostics"]),
                diagnostic => Assert.IsType<JsonObject>(diagnostic)["code"]
                    ?.GetValue<string>() == "validation.duplicateDeclaration");
            var helperParameters = refreshParameters.Last(parameters =>
                parameters["uri"]?.GetValue<string>() == helperUri);
            Assert.Equal(
                "invalid-disk-source-encoding",
                Assert.IsType<JsonObject>(
                    Assert.Single(Assert.IsType<JsonArray>(
                        helperParameters["diagnostics"])))
                    ["code"]?.GetValue<string>());
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Idle_wait_completes_only_after_the_latest_tombstone_is_terminal()
    {
        const string uri = "file:///C:/work/Worker.bas";
        await using var output = new BlockingWriteStream();
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()));
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        publisher.AttachScheduler(scheduler);

        var publish = publisher.PublishEmptyDiagnosticsAsync(
            uri,
            CancellationToken.None);
        await output.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var idle = publisher.WaitForIdleAsync(uri);

        Assert.True(publish.IsCompletedSuccessfully);
        Assert.False(idle.IsCompleted);

        output.ReleaseWrites();
        await idle.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Terminal_publications_release_per_uri_revision_state()
    {
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()));
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, Stream.Null),
            workspace);
        publisher.AttachScheduler(scheduler);

        for (var index = 0; index < 32; index++)
        {
            var uri = $"file:///C:/work/Retired{index}.bas";
            await publisher.PublishEmptyDiagnosticsAsync(
                uri,
                CancellationToken.None);
            await publisher.WaitForIdleAsync(uri)
                .WaitAsync(TimeSpan.FromSeconds(2));
        }

        Assert.Equal(0, publisher.RetainedRevisionStateCount);
    }

    [Fact]
    public async Task Failed_publication_restarts_the_latest_pending_revision_before_becoming_idle()
    {
        const string uri = "file:///C:/work/Worker.bas";
        await using var output = new FailingThenCapturingWriteStream();
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()));
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        publisher.AttachScheduler(scheduler);

        await publisher.PublishEmptyDiagnosticsAsync(
            uri,
            CancellationToken.None);
        await output.FirstWriteStarted.Task
            .WaitAsync(TimeSpan.FromSeconds(5));
        await publisher.PublishEmptyDiagnosticsAsync(
            uri,
            CancellationToken.None);
        var idle = publisher.WaitForIdleAsync(uri);

        Assert.False(idle.IsCompleted);

        output.ReleaseFirstWriteFailure();
        await idle.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, output.SuccessfulMessageCount);
    }

    [Fact]
    public async Task SupersededQueuedDiagnosticsDoNotPublishOlderClientVersion()
    {
        const string uri = "file:///C:/work/Worker.bas";
        await using var output = new CapturingWriteStream();
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        publisher.AttachScheduler(scheduler);
        workspace.OpenDocument(
            uri,
            1,
            "Public Sub Run()\n    ");

        await publisher.PublishTrackedDiagnosticsAsync(uri, CancellationToken.None);
        Assert.True(workspace.ChangeDocument(
            uri,
            2,
            "Public Sub Run()\nEnd Sub\n"));
        await publisher.PublishTrackedDiagnosticsAsync(uri, CancellationToken.None);

        var messages = ReadJsonMessages(
            await output.WaitForMessageCountAsync(1));
        await publisher.WaitForIdleAsync(uri)
            .WaitAsync(TimeSpan.FromSeconds(5));

        messages = ReadJsonMessages(output.ReadText());
        var parameters = Assert.IsType<JsonObject>(
            messages.Last()["params"]);
        Assert.Equal(2, parameters["version"]?.GetValue<int>());
        Assert.DoesNotContain(
            messages.SkipWhile(message =>
                Assert.IsType<JsonObject>(message["params"])["version"]?.GetValue<int>() != 2),
            message => Assert.IsType<JsonObject>(message["params"])["version"]?.GetValue<int>() == 1);
    }

    [Fact]
    public async Task CloseTombstoneSupersedesQueuedDiagnostics()
    {
        const string uri = "file:///C:/work/Worker.bas";
        await using var output = new CapturingWriteStream();
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        publisher.AttachScheduler(scheduler);
        workspace.OpenDocument(
            uri,
            1,
            "Public Sub Run()\n    ");

        await publisher.PublishTrackedDiagnosticsAsync(uri, CancellationToken.None);
        Assert.True(workspace.CloseDocument(uri));
        await publisher.PublishEmptyDiagnosticsAsync(uri, CancellationToken.None);

        var messages = ReadJsonMessages(
            await output.WaitForMessageCountAsync(1));
        await publisher.WaitForIdleAsync(uri)
            .WaitAsync(TimeSpan.FromSeconds(5));

        messages = ReadJsonMessages(output.ReadText());
        var parameters = Assert.IsType<JsonObject>(
            messages.Last()["params"]);
        Assert.Null(parameters["version"]);
        Assert.Empty(Assert.IsType<JsonArray>(parameters["diagnostics"]));
    }

    [Fact]
    public async Task CloseAndReopenRejectEarlierLifecycleDiagnostics()
    {
        const string uri = "file:///C:/work/Worker.bas";
        await using var output = new CapturingWriteStream();
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        publisher.AttachScheduler(scheduler);
        workspace.OpenDocument(
            uri,
            1,
            "Public Sub BeforeClose()\n    ");

        await publisher.PublishTrackedDiagnosticsAsync(uri, CancellationToken.None);
        Assert.True(workspace.CloseDocument(uri));
        await publisher.PublishEmptyDiagnosticsAsync(uri, CancellationToken.None);
        workspace.OpenDocument(
            uri,
            2,
            "Public Sub AfterReopen()\nEnd Sub\n");
        await publisher.PublishTrackedDiagnosticsAsync(uri, CancellationToken.None);

        var messages = ReadJsonMessages(
            await output.WaitForMessageCountAsync(1));
        await publisher.WaitForIdleAsync(uri)
            .WaitAsync(TimeSpan.FromSeconds(5));

        messages = ReadJsonMessages(output.ReadText());
        var parameters = Assert.IsType<JsonObject>(
            messages.Last()["params"]);
        Assert.Equal(2, parameters["version"]?.GetValue<int>());
        Assert.DoesNotContain(
            messages.SkipWhile(message =>
                Assert.IsType<JsonObject>(message["params"])["version"]?.GetValue<int>() != 2),
            message => Assert.IsType<JsonObject>(message["params"])["version"]?.GetValue<int>() == 1);
    }

    private static IReadOnlyList<JsonObject> ReadJsonMessages(string text)
    {
        var messages = new List<JsonObject>();
        var offset = 0;
        while (offset < text.Length)
        {
            var headerEnd = text.IndexOf("\r\n\r\n", offset, StringComparison.Ordinal);
            if (headerEnd < 0)
            {
                break;
            }

            var header = text[offset..headerEnd];
            var length = header
                .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split(':', 2))
                .Where(parts => parts.Length == 2)
                .Where(parts => parts[0].Equals(
                    "Content-Length",
                    StringComparison.OrdinalIgnoreCase))
                .Select(parts => int.Parse(parts[1].Trim()))
                .Single();
            var contentStart = headerEnd + 4;
            var json = text.Substring(contentStart, length);
            messages.Add(JsonNode.Parse(json)!.AsObject());
            offset = contentStart + length;
        }

        return messages;
    }

    private sealed class BlockingWriteStream : Stream
    {
        private readonly TaskCompletionSource writeStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseWrites =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource WriteStarted => writeStarted;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => 0;

        public override long Position { get; set; }

        public void ReleaseWrites()
            => releaseWrites.TrySetResult();

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            writeStarted.TrySetResult();
            return new ValueTask(releaseWrites.Task.WaitAsync(cancellationToken));
        }
    }

    private sealed class SignallingTimingSink : IVbaInteractiveWorkTimingSink
    {
        private readonly object gate = new();
        private readonly Dictionary<string, WorkSignals> signals =
            new(StringComparer.Ordinal);

        public bool IsCompleted(string method)
        {
            lock (gate)
            {
                return signals.TryGetValue(method, out var workSignals)
                    && workSignals.Completion.Task.IsCompleted;
            }
        }

        public Task WaitForAdmissionAsync(string method)
            => GetSignals(method).Admission.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public Task WaitForCompletionAsync(string method)
            => GetSignals(method).Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void RecordAdmission(VbaInteractiveWorkAdmissionTiming timing)
            => GetSignals(timing.Method).Admission.TrySetResult();

        public void RecordCompletion(VbaInteractiveWorkCompletionTiming timing)
            => GetSignals(timing.Method).Completion.TrySetResult();

        private WorkSignals GetSignals(string method)
        {
            lock (gate)
            {
                if (!signals.TryGetValue(method, out var workSignals))
                {
                    workSignals = new WorkSignals(
                        new TaskCompletionSource(
                            TaskCreationOptions.RunContinuationsAsynchronously),
                        new TaskCompletionSource(
                            TaskCreationOptions.RunContinuationsAsynchronously));
                    signals[method] = workSignals;
                }

                return workSignals;
            }
        }

        private sealed record WorkSignals(
            TaskCompletionSource Admission,
            TaskCompletionSource Completion);
    }

    private sealed class BlockingFirstRevisionObserver
        : IVbaDiagnosticsPublicationObserver
    {
        private readonly TaskCompletionSource<string> firstRevisionReserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseFirstRevision =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int reservationClaimed;

        public TaskCompletionSource<string> FirstRevisionReserved
            => firstRevisionReserved;

        public void AfterRevisionReserved(string uri, long revision)
        {
            if (revision != 1
                || Interlocked.Exchange(ref reservationClaimed, 1) != 0)
            {
                return;
            }

            firstRevisionReserved.TrySetResult(uri);
            releaseFirstRevision.Task.GetAwaiter().GetResult();
        }

        public void ReleaseFirstRevision()
            => releaseFirstRevision.TrySetResult();
    }

    private sealed class ThrowingPublicationObserver
        : IVbaDiagnosticsPublicationObserver
    {
        public void AfterRevisionReserved(string uri, long revision)
            => throw new InvalidOperationException(
                "Injected diagnostics observer failure.");
    }

    private sealed class CapturingWriteStream : Stream
    {
        private readonly MemoryStream buffer = new();
        private readonly object gate = new();
        private readonly List<TaskCompletionSource> waiters = [];
        private int messageCount;

        public int MessageCount
        {
            get
            {
                lock (gate)
                {
                    return messageCount;
                }
            }
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => 0;

        public override long Position { get; set; }

        public async Task<string> WaitForMessageCountAsync(int count)
        {
            Task wait;
            lock (gate)
            {
                if (messageCount >= count)
                {
                    return Encoding.UTF8.GetString(buffer.ToArray());
                }

                var waiter = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                waiters.Add(waiter);
                wait = waiter.Task;
            }

            await wait.WaitAsync(TimeSpan.FromSeconds(5));
            lock (gate)
            {
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
        }

        public string ReadText()
        {
            lock (gate)
            {
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            lock (gate)
            {
                messageCount++;
                foreach (var waiter in waiters)
                {
                    waiter.TrySetResult();
                }

                waiters.Clear();
            }

            return Task.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                this.buffer.Write(buffer.Span);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingThenCapturingWriteStream : Stream
    {
        private readonly MemoryStream buffer = new();
        private readonly TaskCompletionSource firstWriteStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseFirstWriteFailure =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int writeAttempts;

        public TaskCompletionSource FirstWriteStarted => firstWriteStarted;

        public int SuccessfulMessageCount { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => 0;

        public override long Position { get; set; }

        public void ReleaseFirstWriteFailure()
            => releaseFirstWriteFailure.TrySetResult();

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            SuccessfulMessageCount++;
            return Task.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref writeAttempts) == 1)
            {
                firstWriteStarted.TrySetResult();
                await releaseFirstWriteFailure.Task
                    .WaitAsync(cancellationToken);
                throw new IOException("Injected diagnostics transport failure.");
            }

            this.buffer.Write(buffer.Span);
        }
    }

    private sealed class RecordingReferenceCatalogLifecycle : IReferenceCatalogLifecycle
    {
        public void ActivateProject(string activeUri)
        {
        }

        public void ApplyManifestSelectionChange(string uri, string text)
        {
        }

        public void DeactivateManifest(string uri)
        {
        }
    }

    private sealed class BlockingFirstProjectSnapshotBuildObserver
        : IVbaProjectSnapshotBuildObserver
    {
        private readonly ManualResetEventSlim release = new();
        private int observedBuilds;

        public TaskCompletionSource FirstBuildWaiting { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void BeforeStore(
            long workspaceVersion,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref observedBuilds) != 1)
            {
                return;
            }

            FirstBuildWaiting.TrySetResult();
            release.Wait(cancellationToken);
        }

        public void ReleaseFirstBuild()
            => release.Set();
    }

    private sealed class BlockingNextDocumentAnalysisBuildObserver
        : IVbaDocumentAnalysisBuildObserver
    {
        private readonly TaskCompletionSource releaseBuild = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int blockNext;

        public TaskCompletionSource BuildStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void BlockNextBuild()
            => Interlocked.Exchange(ref blockNext, 1);

        public void ReleaseBuild()
            => releaseBuild.TrySetResult();

        public void BeforeBuild(
            VbaDocumentAnalysisBuildContext context,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref blockNext, 0) == 0)
            {
                return;
            }

            BuildStarted.TrySetResult();
            releaseBuild.Task.Wait(cancellationToken);
        }
    }
}
