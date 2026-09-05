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
    public async Task Project_validation_does_not_block_document_open_or_editor_queries()
    {
        const string uri = "file:///C:/work/Worker.bas";
        const string callLine = "    result = Work(1)";
        var text = string.Join('\n', [
            "Public Function Work(ByVal value As Long) As Long",
            "    Work = value",
            "End Function",
            "Public Sub Run()",
            "    Dim result As Long",
            callLine,
            "End Sub"
        ]);
        var buildObserver = new BlockingProjectValidationBuildObserver();
        await using var output = new CapturingWriteStream();
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            NullVbaDocumentAnalysisBuildObserver.Instance,
            buildObserver);
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        publisher.AttachScheduler(scheduler);
        var pipeline = new VbaDocumentChangePipeline(
            workspace,
            new RecordingReferenceCatalogLifecycle(),
            publisher);

        var opened = scheduler.AdmitMutation(
            "textDocument/didOpen",
            cancellationToken => pipeline.ApplyAsync(
                new VbaTextDocumentOpenedChange(uri, 1, text),
                cancellationToken));
        await buildObserver.ValidationStarted.Task
            .WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            Assert.True(opened.Completion.IsCompletedSuccessfully);

            IReadOnlyList<int>? semanticTokenData = null;
            VbaVersionedDocumentSnapshot? exactDocumentSnapshot = null;
            var completionReady = false;
            var hoverReady = false;
            var signatureHelpReady = false;
            var symbolsReady = false;
            var definitionReady = false;
            var referencesReady = false;
            var prepareRenameReady = false;
            var renameReady = false;
            var semanticTokens = scheduler.AdmitRequest(
                requestId: null,
                "textDocument/semanticTokens/full",
                cancellationToken =>
                    ((IVbaInteractiveWorkspaceCapture)workspace)
                        .CaptureProjectSemanticInventory(
                            uri,
                            cancellationToken),
                (inventory, cancellationToken) =>
                {
                    var callCharacter = callLine.IndexOf(
                        "Work",
                        StringComparison.Ordinal);
                    completionReady = inventory.GetCompletionResult(
                            uri,
                            line: 5,
                            character: callCharacter + "Wor".Length)
                        .Definitions
                        .Any(definition => definition.Name == "Work");
                    hoverReady = inventory.ResolveHover(
                        uri,
                        line: 5,
                        character: callCharacter + 1) is not null;
                    signatureHelpReady = inventory.GetSignatureHelp(
                        uri,
                        line: 5,
                        character: callCharacter + "Work(".Length) is not null;
                    symbolsReady = inventory.GetDocumentDefinitions(uri)
                            .Count > 0
                        && inventory.GetWorkspaceSymbols("Work").Count > 0;
                    definitionReady = inventory.ResolveDefinition(
                        uri,
                        line: 5,
                        character: callCharacter + 1) is not null;
                    referencesReady = inventory.FindReferences(
                            uri,
                            line: 5,
                            character: callCharacter + 1,
                            cancellationToken)
                        .Count >= 2;
                    prepareRenameReady = inventory.PrepareRename(
                        uri,
                        line: 5,
                        character: callCharacter + 1) is not null;
                    renameReady = inventory.CreateRenamePlan(
                        uri,
                        line: 5,
                        character: callCharacter + 1,
                        "Build",
                        cancellationToken) is not null;
                    semanticTokenData = inventory.GetSemanticTokenData(
                        uri,
                        cancellationToken);
                    exactDocumentSnapshot =
                        ((IVbaInteractiveWorkspaceCapture)workspace)
                            .CaptureExactDocumentSnapshot(
                                uri,
                                expectedVersion: 1,
                                cancellationToken);
                    return Task.CompletedTask;
                });

            await semanticTokens.Completion
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(semanticTokenData);
            Assert.NotEmpty(semanticTokenData);
            Assert.NotNull(exactDocumentSnapshot);
            Assert.Equal(1, exactDocumentSnapshot.Version);
            Assert.Equal(text, exactDocumentSnapshot.Text);
            Assert.True(exactDocumentSnapshot.IsOwnedByAnalysis);
            var exactDocumentInventory = VbaSemanticInventory.Create(
                new Dictionary<string, VbaSourceDocument>(StringComparer.Ordinal)
                {
                    [uri] = exactDocumentSnapshot.SourceDocument
                },
                referenceCatalogs:
                    VbaProjectReferenceCatalogSet.CreateBundled());
            Assert.Equal(
                exactDocumentInventory.GetSemanticTokenData(uri),
                semanticTokenData);
            Assert.True(completionReady);
            Assert.True(hoverReady);
            Assert.True(signatureHelpReady);
            Assert.True(symbolsReady);
            Assert.True(definitionReady);
            Assert.True(referencesReady);
            Assert.True(prepareRenameReady);
            Assert.True(renameReady);
        }
        finally
        {
            buildObserver.ReleaseValidation();
        }

        await publisher.WaitForIdleAsync(uri)
            .WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Interactive_semantic_capture_does_not_enter_project_validation()
    {
        const string uri = "file:///C:/work/Worker.bas";
        const string text = "Public Sub Run()\nEnd Sub\n";
        var validationObserver = new BlockingProjectValidationBuildObserver();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            NullVbaDocumentAnalysisBuildObserver.Instance,
            validationObserver);
        workspace.OpenDocument(uri, 1, text);

        var capture = Task.Run(() =>
            ((IVbaInteractiveWorkspaceCapture)workspace)
                .CaptureProjectSemanticInventory(uri));

        try
        {
            var first = await Task.WhenAny(
                    capture,
                    validationObserver.ValidationStarted.Task)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Same(capture, first);
            var inventory = await capture.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotEmpty(inventory.GetSemanticTokenData(uri));
            Assert.Equal(0, validationObserver.StartCount);
        }
        finally
        {
            validationObserver.ReleaseValidation();
            try
            {
                await capture.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task Document_local_diagnostics_and_semantic_tokens_are_ready_while_project_validation_is_blocked()
    {
        const string uri = "file:///C:/work/Worker.bas";
        const string text = """
            Public Function Work(ByVal Value As Long) As Long
            End Function
            Public Sub Run(ByVal item As Long, ByVal ITEM As String)
                Dim result As Long
                result = Work()
            End Sub
            """;
        var validationObserver = new BlockingProjectValidationBuildObserver();
        await using var output = new CapturingWriteStream();
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            NullVbaDocumentAnalysisBuildObserver.Instance,
            validationObserver);
        workspace.OpenDocument(uri, 1, text);
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        publisher.AttachScheduler(scheduler);

        await publisher.PublishProjectDiagnosticsAsync(
            uri,
            CancellationToken.None);
        await validationObserver.ValidationStarted.Task
            .WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            var localParameters = Assert.IsType<JsonObject>(
                Assert.Single(ReadJsonMessages(
                    await output.WaitForMessageCountAsync(1)))["params"]);
            var localDiagnostics = Assert.IsType<JsonArray>(
                localParameters["diagnostics"]);
            Assert.Contains(
                localDiagnostics,
                diagnostic => Assert.IsType<JsonObject>(diagnostic)["code"]
                    ?.GetValue<string>()
                    == "validation.duplicateCallableParameterName");
            Assert.DoesNotContain(
                localDiagnostics,
                diagnostic => Assert.IsType<JsonObject>(diagnostic)["code"]
                    ?.GetValue<string>()
                    == "validation.incompatibleCallArgumentList");

            IReadOnlyList<int>? semanticTokenData = null;
            var semanticTokens = scheduler.AdmitRequest(
                requestId: null,
                "textDocument/semanticTokens/full",
                cancellationToken =>
                    ((IVbaInteractiveWorkspaceCapture)workspace)
                        .CaptureProjectSemanticInventory(
                            uri,
                            cancellationToken),
                (inventory, cancellationToken) =>
                {
                    semanticTokenData = inventory.GetSemanticTokenData(
                        uri,
                        cancellationToken);
                    return Task.CompletedTask;
                });
            await semanticTokens.Completion
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(semanticTokenData);
            Assert.NotEmpty(semanticTokenData);
        }
        finally
        {
            validationObserver.ReleaseValidation();
        }

        await publisher.WaitForIdleAsync(uri)
            .WaitAsync(TimeSpan.FromSeconds(5));
        var completedParameters = Assert.IsType<JsonObject>(
            ReadJsonMessages(output.ReadText()).Last()["params"]);
        Assert.Contains(
            Assert.IsType<JsonArray>(completedParameters["diagnostics"]),
            diagnostic => Assert.IsType<JsonObject>(diagnostic)["code"]
                ?.GetValue<string>()
                == "validation.incompatibleCallArgumentList");
    }

    [Fact]
    public async Task Empty_document_local_result_clears_a_fixed_local_diagnostic_while_project_validation_is_blocked()
    {
        const string uri = "file:///C:/work/Worker.bas";
        const string invalidText = "Public Sub Run(ByVal item As Long, ByVal ITEM As String)\nEnd Sub\n";
        const string validText = "Public Sub Run(ByVal item As Long)\nEnd Sub\n";
        var validationObserver =
            new BlockingSecondProjectValidationBuildObserver();
        await using var output = new CapturingWriteStream();
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            NullVbaDocumentAnalysisBuildObserver.Instance,
            validationObserver);
        workspace.OpenDocument(uri, 1, invalidText);
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        publisher.AttachScheduler(scheduler);
        await publisher.PublishProjectDiagnosticsAsync(
            uri,
            CancellationToken.None);
        await publisher.WaitForIdleAsync(uri)
            .WaitAsync(TimeSpan.FromSeconds(5));
        var baselineMessageCount = output.MessageCount;

        Assert.True(workspace.ChangeDocument(uri, 2, validText));
        await publisher.PublishProjectDiagnosticsAsync(
            uri,
            CancellationToken.None);
        await validationObserver.SecondValidationStarted.Task
            .WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            var localParameters = Assert.IsType<JsonObject>(
                ReadJsonMessages(
                        await output.WaitForMessageCountAsync(
                            baselineMessageCount + 1))
                    .Last()["params"]);
            Assert.Equal(2, localParameters["version"]?.GetValue<int>());
            Assert.Empty(Assert.IsType<JsonArray>(
                localParameters["diagnostics"]));
        }
        finally
        {
            validationObserver.ReleaseSecondValidation();
        }

        await publisher.WaitForIdleAsync(uri)
            .WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Empty_document_local_result_clears_project_only_diagnostics_while_revalidation_is_blocked()
    {
        const string uri = "file:///C:/work/Worker.bas";
        const string invalidText = """
            Public Function Work(ByVal value As Long) As Long
            End Function
            Public Sub Run()
                Dim result As Long
                result = Work()
            End Sub
            """;
        const string validText = """
            Public Function Work(ByVal value As Long) As Long
            End Function
            Public Sub Run()
                Dim result As Long
                result = Work(1)
            End Sub
            """;
        var validationObserver =
            new BlockingSecondProjectValidationBuildObserver();
        await using var output = new CapturingWriteStream();
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            NullVbaDocumentAnalysisBuildObserver.Instance,
            validationObserver);
        workspace.OpenDocument(uri, 1, invalidText);
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        publisher.AttachScheduler(scheduler);
        await publisher.PublishProjectDiagnosticsAsync(
            uri,
            CancellationToken.None);
        await publisher.WaitForIdleAsync(uri)
            .WaitAsync(TimeSpan.FromSeconds(5));
        var baselineMessages = ReadJsonMessages(output.ReadText());
        var baselineParameters = Assert.IsType<JsonObject>(
            baselineMessages.Last()["params"]);
        Assert.Contains(
            Assert.IsType<JsonArray>(baselineParameters["diagnostics"]),
            diagnostic => Assert.IsType<JsonObject>(diagnostic)["code"]
                ?.GetValue<string>()
                == "validation.incompatibleCallArgumentList");

        Assert.True(workspace.ChangeDocument(uri, 2, validText));
        await publisher.PublishProjectDiagnosticsAsync(
            uri,
            CancellationToken.None);
        await validationObserver.SecondValidationStarted.Task
            .WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            var localParameters = Assert.IsType<JsonObject>(
                ReadJsonMessages(
                        await output.WaitForMessageCountAsync(
                            baselineMessages.Count + 1))
                    .Last()["params"]);
            Assert.Equal(2, localParameters["version"]?.GetValue<int>());
            Assert.Empty(Assert.IsType<JsonArray>(
                localParameters["diagnostics"]));
        }
        finally
        {
            validationObserver.ReleaseSecondValidation();
        }

        await publisher.WaitForIdleAsync(uri)
            .WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Latest_clean_revision_replaces_a_superseded_clean_clear_while_revalidation_is_blocked()
    {
        const string uri = "file:///C:/work/Worker.bas";
        const string invalidText =
            "Public Sub Run(ByVal item As Long, ByVal ITEM As String)\nEnd Sub\n";
        const string firstCleanText =
            "Public Sub FirstClean(ByVal item As Long)\nEnd Sub\n";
        const string latestCleanText =
            "Public Sub LatestClean(ByVal item As Long)\nEnd Sub\n";
        var validationObserver =
            new BlockingSecondProjectValidationBuildObserver();
        var publicationObserver = new ArmableBlockingRevisionObserver();
        await using var output = new CapturingWriteStream();
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            NullVbaDocumentAnalysisBuildObserver.Instance,
            validationObserver);
        workspace.OpenDocument(uri, 1, invalidText);
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace,
            publicationObserver);
        publisher.AttachScheduler(scheduler);
        await publisher.PublishProjectDiagnosticsAsync(
            uri,
            CancellationToken.None);
        await publisher.WaitForIdleAsync(uri)
            .WaitAsync(TimeSpan.FromSeconds(5));
        var baselineMessageCount = output.MessageCount;
        using var supersededCancellation = new CancellationTokenSource();
        Task? supersededClear = null;
        try
        {
            publicationObserver.Arm();
            Assert.True(workspace.ChangeDocument(uri, 2, firstCleanText));
            supersededClear = Task.Run(
                () => publisher.PublishProjectDiagnosticsAsync(
                    uri,
                    supersededCancellation.Token));
            await publicationObserver.BlockedRevision.Task
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(workspace.ChangeDocument(uri, 3, latestCleanText));
            await publisher.PublishProjectDiagnosticsAsync(
                uri,
                CancellationToken.None);
            await validationObserver.SecondValidationStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5));

            supersededCancellation.Cancel();
            publicationObserver.Release();
            var latestParameters = Assert.IsType<JsonObject>(
                ReadJsonMessages(
                        await output.WaitForMessageCountAsync(
                            baselineMessageCount + 1))
                    .Last()["params"]);
            Assert.Equal(3, latestParameters["version"]?.GetValue<int>());
            Assert.Empty(Assert.IsType<JsonArray>(
                latestParameters["diagnostics"]));
        }
        finally
        {
            publicationObserver.Release();
            validationObserver.ReleaseSecondValidation();
            if (supersededClear is not null
                && !supersededClear.IsCompleted)
            {
                try
                {
                    await supersededClear.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                }
            }
        }

        var completedSupersededClear = Assert.IsAssignableFrom<Task>(
            supersededClear);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await completedSupersededClear.WaitAsync(
                TimeSpan.FromSeconds(5)));
        await publisher.WaitForIdleAsync(uri)
            .WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Pending_project_validation_coalesces_across_source_uris_by_project_authority()
    {
        const string firstUri = "file:///C:/work/First.bas";
        const string secondUri = "file:///C:/work/Second.bas";
        var validationObserver = new CountingProjectValidationBuildObserver();
        var blockerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocker = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var output = new CapturingWriteStream();
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
                VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            NullVbaDocumentAnalysisBuildObserver.Instance,
            validationObserver);
        workspace.OpenDocument(firstUri, 1, "Public Sub First()\nEnd Sub\n");
        workspace.OpenDocument(secondUri, 1, "Public Sub Second()\nEnd Sub\n");
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        publisher.AttachScheduler(scheduler);

        await publisher.PublishProjectDiagnosticsAsync(
            firstUri,
            CancellationToken.None);
        await publisher.PublishProjectDiagnosticsAsync(
            secondUri,
            CancellationToken.None);

        releaseBlocker.TrySetResult();
        await blocker.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(
                publisher.WaitForIdleAsync(firstUri),
                publisher.WaitForIdleAsync(secondUri))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, validationObserver.StartCount);
    }

    [Fact]
    public async Task Delayed_older_project_validation_post_cannot_replace_the_newer_pending_revision()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var validationObserver = new CountingProjectValidationBuildObserver();
        var reservationObserver =
            new BlockingFirstProjectValidationReservationObserver();
        var blockerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocker = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var output = new CapturingWriteStream();
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
                VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            NullVbaDocumentAnalysisBuildObserver.Instance,
            validationObserver);
        workspace.OpenDocument(uri, 1, "Public Sub First()\nEnd Sub\n");
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace,
            reservationObserver);
        publisher.AttachScheduler(scheduler);

        Task? olderPublication = null;
        try
        {
            olderPublication = Task.Run(
                () => publisher.PublishProjectDiagnosticsAsync(
                    uri,
                    CancellationToken.None));
            await reservationObserver.FirstReservationReached.Task
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(workspace.ChangeDocument(
                uri,
                2,
                "Public Sub Latest()\nEnd Sub\n"));
            await publisher.PublishProjectDiagnosticsAsync(
                uri,
                CancellationToken.None);
            Assert.Equal(2, reservationObserver.ReservationCount);

            reservationObserver.ReleaseFirstReservation();
            await olderPublication.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, publisher.RetainedProjectValidationStateCount);
        }
        finally
        {
            reservationObserver.ReleaseFirstReservation();
            if (olderPublication is not null)
            {
                await olderPublication.WaitAsync(TimeSpan.FromSeconds(5));
            }

            releaseBlocker.TrySetResult();
            await blocker.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }

        await publisher.WaitForIdleAsync(uri)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, validationObserver.StartCount);
        Assert.Equal(0, publisher.RetainedProjectValidationStateCount);
    }

    [Fact]
    public async Task Wait_for_idle_observes_a_project_validation_reservation_before_its_observer_returns()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var reservationObserver =
            new BlockingFirstProjectValidationReservationObserver();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()));
        workspace.OpenDocument(uri, 1, "Public Sub Run()\nEnd Sub\n");
        await using var output = new CapturingWriteStream();
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace,
            reservationObserver);
        publisher.AttachScheduler(scheduler);

        Task? publication = null;
        Task? idle = null;
        try
        {
            publication = Task.Run(
                () => publisher.PublishProjectDiagnosticsAsync(
                    uri,
                    CancellationToken.None));
            await reservationObserver.FirstReservationReached.Task
                .WaitAsync(TimeSpan.FromSeconds(5));

            idle = publisher.WaitForIdleAsync(uri);
            Assert.False(idle.IsCompleted);

            reservationObserver.ReleaseFirstReservation();
            await publication.WaitAsync(TimeSpan.FromSeconds(5));
            await idle.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            reservationObserver.ReleaseFirstReservation();
            if (publication is not null && !publication.IsCompleted)
            {
                try
                {
                    await publication.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                }
            }

            if (idle is not null && !idle.IsCompleted)
            {
                try
                {
                    await idle.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                }
            }
        }
    }

    [Fact]
    public async Task Wait_for_idle_observes_project_capture_that_started_before_routing_exists()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var buildObserver = new BlockingFirstProjectSnapshotBuildObserver();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            NullVbaDocumentAnalysisBuildObserver.Instance,
            buildObserver);
        workspace.OpenDocument(
            uri,
            1,
            "Attribute VB_Name = \"Worker\"\nPublic Sub Run()\nEnd Sub\n");
        await using var output = new CapturingWriteStream();
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        publisher.AttachScheduler(scheduler);

        var publication = Task.Run(
            () => publisher.PublishProjectDiagnosticsAsync(
                uri,
                CancellationToken.None));
        await buildObserver.FirstBuildWaiting.Task
            .WaitAsync(TimeSpan.FromSeconds(5));

        var idle = publisher.WaitForIdleAsync(uri);
        try
        {
            Assert.False(idle.IsCompleted);
        }
        finally
        {
            buildObserver.ReleaseFirstBuild();
            await publication.WaitAsync(TimeSpan.FromSeconds(5));
        }

        await idle.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, buildObserver.ValidationStartCount);
    }

    [Fact]
    public async Task Wait_for_idle_observes_an_inflight_capture_from_a_project_sibling()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-sibling-capture-idle-").FullName;
        var buildObserver = new BlockingFirstProjectSnapshotBuildObserver();
        try
        {
            var firstPath = Path.Combine(projectRoot, "First.bas");
            var secondPath = Path.Combine(projectRoot, "Second.bas");
            const string firstText = "Public Sub First()\nEnd Sub\n";
            const string secondText = "Public Sub Second()\nEnd Sub\n";
            File.WriteAllText(firstPath, firstText);
            File.WriteAllText(secondPath, secondText);
            var firstUri = new Uri(firstPath).AbsoluteUri;
            var secondUri = new Uri(secondPath).AbsoluteUri;
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()),
                NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
                NullVbaDocumentAnalysisBuildObserver.Instance,
                buildObserver);
            workspace.OpenDocument(firstUri, 1, firstText);
            workspace.OpenDocument(secondUri, 1, secondText);
            await using var scheduler = new VbaInteractiveWorkScheduler();
            var publisher = new VbaDiagnosticsPublisher(
                new LspMessageTransport(Stream.Null, Stream.Null),
                workspace);
            publisher.AttachScheduler(scheduler);

            var publication = Task.Run(
                () => publisher.PublishProjectDiagnosticsAsync(
                    firstUri,
                    CancellationToken.None));
            await buildObserver.FirstBuildWaiting.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            var siblingIdle = publisher.WaitForIdleAsync(secondUri);

            Assert.False(siblingIdle.IsCompleted);

            buildObserver.ReleaseFirstBuild();
            await publication.WaitAsync(TimeSpan.FromSeconds(5));
            await siblingIdle.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, buildObserver.ValidationStartCount);
        }
        finally
        {
            buildObserver.ReleaseFirstBuild();
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Wait_for_idle_does_not_wait_for_an_unrelated_project_authority()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-authority-idle-").FullName;
        var validationObserver =
            new BlockingFirstCancellableProjectValidationBuildObserver();
        try
        {
            var firstRoot = Directory.CreateDirectory(
                Path.Combine(projectRoot, "First")).FullName;
            var secondRoot = Directory.CreateDirectory(
                Path.Combine(projectRoot, "Second")).FullName;
            var firstPath = Path.Combine(firstRoot, "First.bas");
            var secondPath = Path.Combine(secondRoot, "Second.bas");
            const string firstText = "Attribute VB_Name = \"First\"\n"
                + "Public Sub Run()\nEnd Sub\n";
            const string secondText = "Attribute VB_Name = \"Second\"\n"
                + "Public Sub Run()\nEnd Sub\n";
            File.WriteAllText(firstPath, firstText);
            File.WriteAllText(secondPath, secondText);
            var firstUri = new Uri(firstPath).AbsoluteUri;
            var secondUri = new Uri(secondPath).AbsoluteUri;
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()),
                NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
                NullVbaDocumentAnalysisBuildObserver.Instance,
                validationObserver);
            workspace.OpenDocument(firstUri, 1, firstText);
            workspace.OpenDocument(secondUri, 1, secondText);
            await using var output = new CapturingWriteStream();
            await using var scheduler = new VbaInteractiveWorkScheduler(
                options: new VbaInteractiveWorkSchedulerOptions(
                    CoalesceSupersededMutations: true,
                    MaxConcurrentBulkReads: 2));
            var publisher = new VbaDiagnosticsPublisher(
                new LspMessageTransport(Stream.Null, output),
                workspace);
            publisher.AttachScheduler(scheduler);
            await publisher.PublishProjectDiagnosticsAsync(
                firstUri,
                CancellationToken.None);
            await validationObserver.FirstValidationStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5));

            try
            {
                await publisher.PublishProjectDiagnosticsAsync(
                    secondUri,
                    CancellationToken.None);
                await publisher.WaitForIdleAsync(secondUri)
                    .WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(2, validationObserver.StartCount);
            }
            finally
            {
                validationObserver.ReleaseFirstValidation();
            }

            await publisher.WaitForIdleAsync(firstUri)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            validationObserver.ReleaseFirstValidation();
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Wait_for_idle_observes_retired_validation_until_its_worker_is_terminal()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var validationObserver =
            new BlockingNonCooperativeProjectValidationBuildObserver();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            NullVbaDocumentAnalysisBuildObserver.Instance,
            validationObserver);
        workspace.OpenDocument(
            uri,
            1,
            "Attribute VB_Name = \"Worker\"\nPublic Sub Run()\nEnd Sub\n");
        await using var output = new CapturingWriteStream();
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        publisher.AttachScheduler(scheduler);
        try
        {
            await publisher.PublishProjectDiagnosticsAsync(
                uri,
                CancellationToken.None);
            await validationObserver.ValidationStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5));

            publisher.CancelProjectValidationsForDocuments([uri]);
            await validationObserver.CancellationObserved.Task
                .WaitAsync(TimeSpan.FromSeconds(5));

            var idle = publisher.WaitForIdleAsync(uri);
            Assert.False(idle.IsCompleted);

            validationObserver.ReleaseValidation();
            await idle.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, publisher.RetainedProjectValidationActivityCount);
        }
        finally
        {
            validationObserver.ReleaseValidation();
        }
    }

    [Fact]
    public async Task Catalog_invalidation_after_currentness_acceptance_cancels_project_transport_without_aborting_scheduler()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var publicationObserver =
            new BlockingProjectDiagnosticsTransportObserver();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()));
        workspace.OpenDocument(
            uri,
            1,
            "Attribute VB_Name = \"Worker\"\nPublic Sub Run()\nEnd Sub\n");
        Assert.True(workspace.TryCaptureProjectDiagnosticsAuthority(
            uri,
            CancellationToken.None,
            out var authority));
        await using var output = new CapturingWriteStream();
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace,
            publicationObserver);
        publisher.AttachScheduler(scheduler);
        var lease = new VbaProjectValidationLifecycleLease(
            authority,
            revision: 1);
        publisher.ActivateProjectDiagnostics(lease);

        try
        {
            await publisher.PublishProjectDiagnosticsAsync(
                uri,
                CancellationToken.None);
            await publicationObserver.TransportWriteReached.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            publisher.InvalidateProjectDiagnostics(lease);
        }
        finally
        {
            publicationObserver.Release();
            lease.Revoke();
            publisher.RetireProjectDiagnostics(lease);
        }

        await publisher.WaitForIdleAsync(uri)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(ReadJsonMessages(output.ReadText()));
        Assert.True(scheduler.IsAccepting);
    }

    [Fact]
    public async Task New_project_revision_cancels_active_validation_and_publishes_only_latest()
    {
        const string uri = "file:///C:/work/Worker.bas";
        const string firstText = "Public Sub First()\nEnd Sub\n";
        const string latestText = "Public Sub Latest()\nEnd Sub\n";
        var validationObserver =
            new BlockingFirstCancellableProjectValidationBuildObserver();
        await using var output = new CapturingWriteStream();
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            NullVbaDocumentAnalysisBuildObserver.Instance,
            validationObserver);
        workspace.OpenDocument(uri, 1, firstText);
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        publisher.AttachScheduler(scheduler);

        await publisher.PublishProjectDiagnosticsAsync(
            uri,
            CancellationToken.None);
        await validationObserver.FirstValidationStarted.Task
            .WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            Assert.True(workspace.ChangeDocument(uri, 2, latestText));
            await publisher.PublishProjectDiagnosticsAsync(
                uri,
                CancellationToken.None);
            await validationObserver.FirstValidationCancelled.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            validationObserver.ReleaseFirstValidation();
        }

        await publisher.WaitForIdleAsync(uri)
            .WaitAsync(TimeSpan.FromSeconds(5));
        var messages = ReadJsonMessages(output.ReadText());
        Assert.Equal(2, messages.Count);
        Assert.All(
            messages,
            message => Assert.Equal(
                2,
                Assert.IsType<JsonObject>(message["params"])["version"]
                    ?.GetValue<int>()));
        Assert.Equal(2, validationObserver.StartCount);
        Assert.True(scheduler.IsAccepting);
    }

    [Fact]
    public async Task Identical_new_revision_supersedes_non_cooperative_validation_before_publication()
    {
        const string uri = "file:///C:/work/Worker.bas";
        const string text = "Attribute VB_Name = \"Worker\"\n"
            + "Public Sub Run()\nEnd Sub\n";
        var validationObserver =
            new BlockingFirstAfterProjectValidationBuildObserver();
        var publicationObserver = new CountingDiagnosticsPublicationObserver();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            NullVbaDocumentAnalysisBuildObserver.Instance,
            validationObserver);
        workspace.OpenDocument(uri, 1, text);
        await using var output = new CapturingWriteStream();
        await using var scheduler = new VbaInteractiveWorkScheduler(
            options: new VbaInteractiveWorkSchedulerOptions(
                CoalesceSupersededMutations: true,
                MaxConcurrentReads: 2));
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace,
            publicationObserver);
        publisher.AttachScheduler(scheduler);
        try
        {
            await publisher.PublishProjectDiagnosticsAsync(
                uri,
                CancellationToken.None);
            await validationObserver.FirstValidationBuilt.Task
                .WaitAsync(TimeSpan.FromSeconds(5));

            await publisher.PublishProjectDiagnosticsAsync(
                uri,
                CancellationToken.None);
            Assert.Equal(2, publicationObserver.ProjectReservationCount);

            validationObserver.ReleaseFirstValidation();
            await publisher.WaitForIdleAsync(uri)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, publicationObserver.DocumentReservationCount);
            Assert.Single(ReadJsonMessages(output.ReadText()));
        }
        finally
        {
            validationObserver.ReleaseFirstValidation();
        }
    }

    [Fact]
    public async Task Superseding_project_validation_does_not_wait_for_or_propagate_cancellation_callbacks()
    {
        const string uri = "file:///C:/work/Worker.bas";
        const string firstText = "Public Sub First()\nEnd Sub\n";
        const string latestText = "Public Sub Latest()\nEnd Sub\n";
        var validationObserver =
            new BlockingThrowingCancellationCallbackProjectValidationBuildObserver();
        await using var output = new CapturingWriteStream();
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            NullVbaDocumentAnalysisBuildObserver.Instance,
            validationObserver);
        workspace.OpenDocument(uri, 1, firstText);
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        publisher.AttachScheduler(scheduler);
        Task? supersedingPublication = null;
        try
        {
            await publisher.PublishProjectDiagnosticsAsync(
                uri,
                CancellationToken.None);
            await validationObserver.FirstValidationStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(workspace.ChangeDocument(uri, 2, latestText));
            supersedingPublication = Task.Run(
                () => publisher.PublishProjectDiagnosticsAsync(
                    uri,
                    CancellationToken.None));
            await validationObserver.CancellationCallbackStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            await validationObserver.CancellationObserved.Task
                .WaitAsync(TimeSpan.FromSeconds(5));

            await supersedingPublication.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(scheduler.IsAccepting);
        }
        finally
        {
            validationObserver.ReleaseCancellationCallback();
            if (supersedingPublication is not null)
            {
                try
                {
                    await supersedingPublication.WaitAsync(
                        TimeSpan.FromSeconds(5));
                }
                catch
                {
                }
            }
        }

        await publisher.WaitForIdleAsync(uri)
            .WaitAsync(TimeSpan.FromSeconds(5));
        var messages = ReadJsonMessages(output.ReadText());
        Assert.NotEmpty(messages);
        Assert.All(
            messages,
            message => Assert.Equal(
                2,
                Assert.IsType<JsonObject>(message["params"])["version"]
                    ?.GetValue<int>()));
        Assert.Equal(2, validationObserver.StartCount);
        Assert.True(scheduler.IsAccepting);
        Assert.Equal(0, publisher.RetainedProjectValidationStateCount);
    }

    [Fact]
    public async Task Superseded_project_publication_cancellation_does_not_abort_the_scheduler()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var publicationObserver = new ArmableBlockingRevisionObserver();
        var failures = new List<VbaInteractiveWorkFailure>();
        await using var output = new CapturingWriteStream();
        await using var scheduler = new VbaInteractiveWorkScheduler(
            failureSink: failures.Add);
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()));
        workspace.OpenDocument(
            uri,
            1,
            "Public Sub First(ByVal item As Long, ByVal ITEM As String)\nEnd Sub\n");
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace,
            publicationObserver);
        publisher.AttachScheduler(scheduler);
        try
        {
            publicationObserver.ArmAfterNextProjectValidationRevision();
            await publisher.PublishProjectDiagnosticsAsync(
                uri,
                CancellationToken.None);
            await publicationObserver.BlockedRevision.Task
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(workspace.ChangeDocument(
                uri,
                2,
                "Public Sub Latest()\nEnd Sub\n"));
            await publisher.PublishProjectDiagnosticsAsync(
                uri,
                CancellationToken.None);
            publicationObserver.Release();

            await publisher.WaitForIdleAsync(uri)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(failures);
            Assert.True(scheduler.IsAccepting);
            Assert.Contains(
                ReadJsonMessages(output.ReadText()),
                message => Assert.IsType<JsonObject>(
                        message["params"])["version"]
                    ?.GetValue<int>() == 2);
        }
        finally
        {
            publicationObserver.Release();
        }
    }

    [Fact]
    public async Task Catalog_revision_change_rejects_captured_project_validation()
    {
        const string referenceName = "Validation Test Library";
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-validation-catalog-fence-").FullName;
        try
        {
            var sourceRoot = Directory.CreateDirectory(
                Path.Combine(projectRoot, "src")).FullName;
            File.WriteAllText(
                Path.Combine(projectRoot, "vba-project.json"),
                $$"""
                {
                  "schemaVersion": 1,
                  "projectName": "ValidationCatalogFence",
                  "primaryDocument": "Book1",
                  "documents": {
                    "Book1": {
                      "kind": "excel",
                      "sourcePath": "src",
                      "templatePath": "src/Book1.xlsm",
                      "binPath": "bin/Book1.xlsm",
                      "publishPath": "publish/Book1.xlsm",
                      "commonModules": [],
                      "references": [
                        {
                          "name": "{{referenceName}}",
                          "requested": true
                        }
                      ]
                    }
                  }
                }
                """);
            var sourcePath = Path.Combine(sourceRoot, "Worker.bas");
            const string text = "Attribute VB_Name = \"Worker\"\n"
                + "Public Sub Run()\nEnd Sub\n";
            File.WriteAllText(sourcePath, text);
            var uri = new Uri(sourcePath).AbsoluteUri;
            var catalogCache = new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled());
            catalogCache.StoreStaleCatalog(new VbaProjectReferenceCatalog(
                referenceName,
                [],
                [
                    new VbaProjectReferenceDefinition(
                        referenceName,
                        "BeforeType",
                        VbaSourceDefinitionKind.Class,
                        null)
                ]));
            var validationObserver =
                new BlockingProjectValidationBuildObserver();
            var workspace = new VbaLanguageWorkspace(
                catalogCache,
                NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
                NullVbaDocumentAnalysisBuildObserver.Instance,
                validationObserver);
            workspace.OpenDocument(uri, 1, text);
            await using var output = new CapturingWriteStream();
            await using var scheduler = new VbaInteractiveWorkScheduler();
            var publisher = new VbaDiagnosticsPublisher(
                new LspMessageTransport(Stream.Null, output),
                workspace);
            publisher.AttachScheduler(scheduler);

            await publisher.PublishProjectDiagnosticsAsync(
                uri,
                CancellationToken.None);
            await validationObserver.ValidationStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            catalogCache.StoreStaleCatalog(new VbaProjectReferenceCatalog(
                referenceName,
                [],
                [
                    new VbaProjectReferenceDefinition(
                        referenceName,
                        "AfterType",
                        VbaSourceDefinitionKind.Class,
                        null)
                ]));
            validationObserver.ReleaseValidation();
            await publisher.WaitForIdleAsync(uri)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Empty(ReadJsonMessages(output.ReadText()));
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Catalog_commit_cancels_stale_validation_before_the_batch_settles_and_refreshes_once()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-validation-catalog-commit-").FullName;
        var discovery = new BlockingSecondSuccessfulCatalogDiscovery();
        var validationObserver =
            new BlockingFirstCancellableProjectValidationBuildObserver();
        try
        {
            var sourceRoot = Directory.CreateDirectory(
                Path.Combine(projectRoot, "src")).FullName;
            var manifestPath = Path.Combine(projectRoot, "vba-project.json");
            var manifestText = System.Text.Json.JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                projectName = "ValidationCatalogCommit",
                primaryDocument = "Book1",
                documents = new Dictionary<string, object>
                {
                    ["Book1"] = new
                    {
                        kind = "excel",
                        sourcePath = "src",
                        templatePath = "src/Book1.xlsm",
                        binPath = "bin/Book1.xlsm",
                        publishPath = "publish/Book1.xlsm",
                        commonModules = Array.Empty<object>(),
                        references = new[]
                        {
                            new { name = "Library A", requested = true },
                            new { name = "Library B", requested = true }
                        }
                    }
                }
            });
            File.WriteAllText(manifestPath, manifestText);
            var sourcePath = Path.Combine(sourceRoot, "Worker.bas");
            const string text = "Attribute VB_Name = \"Worker\"\n"
                + "Public Sub Run()\nEnd Sub\n";
            File.WriteAllText(sourcePath, text);
            var uri = new Uri(sourcePath).AbsoluteUri;
            var manifestUri = new Uri(manifestPath).AbsoluteUri;
            var catalogCache = new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled());
            var workspace = new VbaLanguageWorkspace(
                catalogCache,
                NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
                NullVbaDocumentAnalysisBuildObserver.Instance,
                validationObserver);
            workspace.OpenDocument(uri, 1, text);
            await using var output = new CapturingWriteStream();
            await using var scheduler = new VbaInteractiveWorkScheduler();
            var publisher = new VbaDiagnosticsPublisher(
                new LspMessageTransport(Stream.Null, output),
                workspace);
            var refreshService = new VbaProjectReferenceCatalogRefreshService(
                catalogCache,
                discovery,
                persistentStore: null,
                new InlineReferenceCatalogRefreshWorker());
            var catalogLifecycle = new ReferenceCatalogRefreshCoordinator(
                catalogCache,
                refreshService,
                new VbaProjectManifestWorkspace(),
                new LspMessageTransport(Stream.Null, output));
            publisher.AttachScheduler(scheduler);
            catalogLifecycle.AttachProjectValidationLifecycle(publisher);
            catalogLifecycle.AttachScheduler(scheduler);
            await publisher.PublishProjectDiagnosticsAsync(
                uri,
                CancellationToken.None);
            await validationObserver.FirstValidationStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5));

            catalogLifecycle.ApplyManifestSelectionChange(
                manifestUri,
                manifestText);
            await discovery.SecondDiscoveryStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            await validationObserver.FirstValidationCancelled.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, validationObserver.StartCount);

            discovery.ReleaseSecondDiscovery();
            await catalogLifecycle.WaitForIdleAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            await publisher.WaitForIdleAsync(uri)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(2, validationObserver.StartCount);
            await catalogLifecycle.StopAsync();
            publisher.Stop();
        }
        finally
        {
            discovery.ReleaseSecondDiscovery();
            validationObserver.ReleaseFirstValidation();
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Stop_cancels_project_validation_and_releases_revision_state()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var validationObserver =
            new BlockingFirstCancellableProjectValidationBuildObserver();
        await using var output = new CapturingWriteStream();
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            NullVbaDocumentAnalysisBuildObserver.Instance,
            validationObserver);
        workspace.OpenDocument(uri, 1, "Public Sub Run()\nEnd Sub\n");
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        publisher.AttachScheduler(scheduler);
        await publisher.PublishProjectDiagnosticsAsync(
            uri,
            CancellationToken.None);
        await validationObserver.FirstValidationStarted.Task
            .WaitAsync(TimeSpan.FromSeconds(5));

        publisher.Stop();

        await validationObserver.FirstValidationCancelled.Task
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, publisher.RetainedProjectValidationStateCount);
        await scheduler.StopAsync(VbaInteractiveStopReason.Complete)
            .WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Stop_does_not_wait_for_or_propagate_project_validation_cancellation_callbacks()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var validationObserver =
            new BlockingThrowingCancellationCallbackProjectValidationBuildObserver();
        await using var output = new CapturingWriteStream();
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            NullVbaDocumentAnalysisBuildObserver.Instance,
            validationObserver);
        workspace.OpenDocument(uri, 1, "Public Sub Run()\nEnd Sub\n");
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        publisher.AttachScheduler(scheduler);
        Task? stop = null;
        try
        {
            await publisher.PublishProjectDiagnosticsAsync(
                uri,
                CancellationToken.None);
            await validationObserver.FirstValidationStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5));

            stop = Task.Run(publisher.Stop);
            await validationObserver.CancellationCallbackStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            await validationObserver.CancellationObserved.Task
                .WaitAsync(TimeSpan.FromSeconds(5));

            await stop.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(0, publisher.RetainedProjectValidationStateCount);
        }
        finally
        {
            validationObserver.ReleaseCancellationCallback();
            if (stop is not null)
            {
                await stop.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        await scheduler.StopAsync(VbaInteractiveStopReason.Complete)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, publisher.RetainedProjectValidationStateCount);
    }

    [Fact]
    public async Task Stop_cannot_be_followed_by_a_stale_document_local_revision_write()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var publicationObserver =
            new BlockingDocumentLocalSnapshotPublicationObserver();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()));
        workspace.OpenDocument(
            uri,
            1,
            "Public Sub Run(ByVal item As Long, ByVal ITEM As String)\nEnd Sub\n");
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, Stream.Null),
            workspace,
            publicationObserver);
        publisher.AttachScheduler(scheduler);

        var publication = Task.Run(
            () => publisher.PublishProjectDiagnosticsAsync(
                uri,
                CancellationToken.None));
        try
        {
            await publicationObserver.SnapshotCaptured.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            publisher.Stop();
        }
        finally
        {
            publicationObserver.Release();
        }

        await publication.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, publisher.RetainedDocumentLocalDiagnosticsStateCount);
        Assert.Equal(0, publisher.RetainedProjectValidationStateCount);
    }

    [Fact]
    public async Task Source_retirement_cannot_be_followed_by_a_stale_document_local_revision_write()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var publicationObserver =
            new BlockingDocumentLocalSnapshotPublicationObserver();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()));
        workspace.OpenDocument(
            uri,
            1,
            "Public Sub Run(ByVal item As Long, ByVal ITEM As String)\nEnd Sub\n");
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, Stream.Null),
            workspace,
            publicationObserver);
        publisher.AttachScheduler(scheduler);

        var publication = Task.Run(
            () => publisher.PublishProjectDiagnosticsAsync(
                uri,
                CancellationToken.None));
        try
        {
            await publicationObserver.SnapshotCaptured.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(workspace.CloseDocument(uri, CancellationToken.None));
            publisher.CancelProjectValidationsForDocuments([uri]);
        }
        finally
        {
            publicationObserver.Release();
        }

        await publication.WaitAsync(TimeSpan.FromSeconds(5));
        await publisher.WaitForIdleAsync(uri)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, publisher.RetainedDocumentLocalDiagnosticsStateCount);
        Assert.Equal(0, publisher.RetainedProjectValidationStateCount);
    }

    [Fact]
    public async Task Validation_failure_is_contained_and_a_later_revision_can_succeed()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var validationObserver =
            new ThrowingFirstProjectValidationBuildObserver();
        await using var output = new CapturingWriteStream();
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            NullVbaDocumentAnalysisBuildObserver.Instance,
            validationObserver);
        workspace.OpenDocument(uri, 1, "Public Sub Run()\nEnd Sub\n");
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        publisher.AttachScheduler(scheduler);

        await publisher.PublishProjectDiagnosticsAsync(
            uri,
            CancellationToken.None);
        await publisher.WaitForIdleAsync(uri)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(ReadJsonMessages(output.ReadText()));
        Assert.True(scheduler.IsAccepting);
        Assert.Equal(0, publisher.RetainedProjectValidationStateCount);

        await publisher.PublishProjectDiagnosticsAsync(
            uri,
            CancellationToken.None);
        await publisher.WaitForIdleAsync(uri)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(ReadJsonMessages(output.ReadText()));
        Assert.Equal(2, validationObserver.StartCount);
        Assert.True(scheduler.IsAccepting);
        Assert.Equal(0, publisher.RetainedProjectValidationStateCount);
    }

    [Fact]
    public async Task Closing_the_only_source_cancels_active_project_validation()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var validationObserver =
            new BlockingFirstCancellableProjectValidationBuildObserver();
        await using var output = new CapturingWriteStream();
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            NullVbaDocumentAnalysisBuildObserver.Instance,
            validationObserver);
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
                uri,
                1,
                "Public Sub Run()\nEnd Sub\n"),
            CancellationToken.None);
        await validationObserver.FirstValidationStarted.Task
            .WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            await pipeline.ApplyAsync(
                new VbaTextDocumentClosedChange(uri),
                CancellationToken.None);
            await validationObserver.FirstValidationCancelled.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            validationObserver.ReleaseFirstValidation();
        }

        await publisher.WaitForIdleAsync(uri)
            .WaitAsync(TimeSpan.FromSeconds(5));
        var parameters = Assert.IsType<JsonObject>(
            Assert.Single(ReadJsonMessages(output.ReadText()))["params"]);
        Assert.Null(parameters["version"]);
        Assert.Empty(Assert.IsType<JsonArray>(parameters["diagnostics"]));
        Assert.Equal(0, publisher.RetainedProjectValidationStateCount);
    }

    [Fact]
    public async Task Accepted_change_preserves_local_revision_until_source_retirement()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var validationObserver =
            new BlockingSecondProjectValidationBuildObserver();
        await using var output = new CapturingWriteStream();
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            NullVbaDocumentAnalysisBuildObserver.Instance,
            validationObserver);
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        publisher.AttachScheduler(scheduler);
        var pipeline = new VbaDocumentChangePipeline(
            workspace,
            new RecordingReferenceCatalogLifecycle(),
            publisher);
        workspace.OpenDocument(uri, 1, "Public Sub Run()\n    ");
        await publisher.PublishProjectDiagnosticsAsync(
            uri,
            CancellationToken.None);
        await publisher.WaitForIdleAsync(uri)
            .WaitAsync(TimeSpan.FromSeconds(5));
        var baselineMessageCount = output.MessageCount;

        Assert.Equal(1, publisher.RetainedDocumentLocalDiagnosticsStateCount);
        Assert.True(workspace.ChangeDocument(
            uri,
            2,
            "Public Sub Run()\nEnd Sub\n"));
        publisher.InvalidateProjectValidationsForDocuments([uri]);
        Assert.Equal(1, publisher.RetainedDocumentLocalDiagnosticsStateCount);

        await publisher.PublishProjectDiagnosticsAsync(
            uri,
            CancellationToken.None);
        await validationObserver.SecondValidationStarted.Task
            .WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var messages = ReadJsonMessages(
                await output.WaitForMessageCountAsync(
                    baselineMessageCount + 1));
            var localClear = Assert.IsType<JsonObject>(
                Assert.Single(messages.Skip(baselineMessageCount))["params"]);
            Assert.Equal(2, localClear["version"]?.GetValue<int>());
            Assert.Empty(Assert.IsType<JsonArray>(localClear["diagnostics"]));

            await pipeline.ApplyAsync(
                new VbaTextDocumentClosedChange(uri),
                CancellationToken.None);
            Assert.Equal(
                0,
                publisher.RetainedDocumentLocalDiagnosticsStateCount);
        }
        finally
        {
            validationObserver.ReleaseSecondValidation();
        }

        await publisher.WaitForIdleAsync(uri)
            .WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Retiring_a_non_active_project_member_cancels_its_project_validation()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-validation-member-routing-").FullName;
        try
        {
            var sourceRoot = Directory.CreateDirectory(
                Path.Combine(projectRoot, "src", "Book1")).FullName;
            File.WriteAllText(
                Path.Combine(projectRoot, "vba-project.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    projectName = "ValidationMemberRouting",
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
            var activePath = Path.Combine(sourceRoot, "Active.bas");
            const string activeText = "Attribute VB_Name = \"Active\"\n"
                + "Public Sub Run()\nEnd Sub\n";
            File.WriteAllText(activePath, activeText);
            var siblingPath = Path.Combine(sourceRoot, "Sibling.bas");
            const string siblingText = "Attribute VB_Name = \"Sibling\"\n"
                + "Public Sub Help()\nEnd Sub\n";
            File.WriteAllText(siblingPath, siblingText);
            var activeUri = new Uri(activePath).AbsoluteUri;
            var siblingUri = new Uri(siblingPath).AbsoluteUri;
            var validationObserver =
                new BlockingFirstCancellableProjectValidationBuildObserver();
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()),
                NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
                NullVbaDocumentAnalysisBuildObserver.Instance,
                validationObserver);
            workspace.OpenDocument(activeUri, 1, activeText);
            await using var output = new CapturingWriteStream();
            await using var scheduler = new VbaInteractiveWorkScheduler();
            var publisher = new VbaDiagnosticsPublisher(
                new LspMessageTransport(Stream.Null, output),
                workspace);
            publisher.AttachScheduler(scheduler);
            await publisher.PublishProjectDiagnosticsAsync(
                activeUri,
                CancellationToken.None);
            await validationObserver.FirstValidationStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5));

            try
            {
                publisher.CancelProjectValidationsForDocuments([siblingUri]);
                await validationObserver.FirstValidationCancelled.Task
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                validationObserver.ReleaseFirstValidation();
            }

            await publisher.WaitForIdleAsync(activeUri)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, publisher.RetainedProjectValidationStateCount);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Project_capture_started_before_retirement_cannot_restore_retired_routing()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-retired-capture-routing-").FullName;
        var buildObserver = new BlockingFirstProjectSnapshotBuildObserver();
        try
        {
            var sourcePath = Path.Combine(projectRoot, "Worker.bas");
            const string text = "Attribute VB_Name = \"Worker\"\n"
                + "Public Sub Run()\nEnd Sub\n";
            File.WriteAllText(sourcePath, text);
            var uri = new Uri(sourcePath).AbsoluteUri;
            Assert.True(VbaProjectIdentityModel.TryIdentifyAuthority(
                VbaProjectResolver.Resolve(uri),
                out var authority));
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()),
                NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
                NullVbaDocumentAnalysisBuildObserver.Instance,
                buildObserver);
            workspace.OpenDocument(uri, 1, text);
            await using var output = new CapturingWriteStream();
            await using var scheduler = new VbaInteractiveWorkScheduler();
            var publisher = new VbaDiagnosticsPublisher(
                new LspMessageTransport(Stream.Null, output),
                workspace);
            publisher.AttachScheduler(scheduler);

            var staleCapture = Task.Run(
                () => publisher.PublishProjectDiagnosticsAsync(
                    uri,
                    CancellationToken.None));
            await buildObserver.FirstBuildWaiting.Task
                .WaitAsync(TimeSpan.FromSeconds(5));

            publisher.CancelProjectValidationsForDocuments([uri]);
            buildObserver.ReleaseFirstBuild();
            await staleCapture.WaitAsync(TimeSpan.FromSeconds(5));
            await publisher.WaitForIdleAsync(uri)
                .WaitAsync(TimeSpan.FromSeconds(5));
            var refreshLease = new VbaProjectValidationLifecycleLease(
                authority,
                revision: 1);
            publisher.ActivateProjectDiagnostics(refreshLease);
            publisher.RefreshProjectDiagnostics(refreshLease);
            await publisher.WaitForIdleAsync(uri)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(0, buildObserver.ValidationStartCount);
        }
        finally
        {
            buildObserver.ReleaseFirstBuild();
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Stale_catalog_invalidation_cannot_cancel_a_newer_project_validation_lifecycle()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var validationObserver =
            new BlockingFirstCancellableProjectValidationBuildObserver();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            NullVbaDocumentAnalysisBuildObserver.Instance,
            validationObserver);
        workspace.OpenDocument(uri, 1, "Public Sub Run()\nEnd Sub\n");
        Assert.True(workspace.TryCaptureProjectDiagnosticsAuthority(
            uri,
            CancellationToken.None,
            out var authority));
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, Stream.Null),
            workspace);
        publisher.AttachScheduler(scheduler);
        var staleLease = new VbaProjectValidationLifecycleLease(
            authority,
            revision: 1);
        publisher.ActivateProjectDiagnostics(staleLease);
        staleLease.Revoke();
        var currentLease = new VbaProjectValidationLifecycleLease(
            authority,
            revision: 2);
        publisher.ActivateProjectDiagnostics(currentLease);
        await publisher.PublishProjectDiagnosticsAsync(
            uri,
            CancellationToken.None);

        try
        {
            await validationObserver.FirstValidationStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            publisher.InvalidateProjectDiagnostics(staleLease);

            Assert.False(
                validationObserver.FirstValidationCancellationRequested);
            Assert.Equal(
                1,
                publisher.RetainedProjectValidationLifecycleStateCount);

            publisher.InvalidateProjectDiagnostics(currentLease);
            await validationObserver.FirstValidationCancelled.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(
                1,
                publisher.RetainedProjectValidationLifecycleStateCount);
        }
        finally
        {
            validationObserver.ReleaseFirstValidation();
            currentLease.Revoke();
            publisher.RetireProjectDiagnostics(currentLease);
        }
    }

    [Fact]
    public async Task Refresh_acquired_by_an_old_lifecycle_cannot_cross_same_authority_replacement()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var validationObserver = new CountingProjectValidationBuildObserver();
        var routingObserver = new ArmableBlockingRoutingAcquisitionObserver();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            NullVbaDocumentAnalysisBuildObserver.Instance,
            validationObserver);
        workspace.OpenDocument(uri, 1, "Public Sub Run()\nEnd Sub\n");
        Assert.True(workspace.TryCaptureProjectDiagnosticsAuthority(
            uri,
            CancellationToken.None,
            out var authority));
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, Stream.Null),
            workspace,
            routingObserver);
        publisher.AttachScheduler(scheduler);
        var staleLease = new VbaProjectValidationLifecycleLease(
            authority,
            revision: 1);
        publisher.ActivateProjectDiagnostics(staleLease);
        await publisher.PublishProjectDiagnosticsAsync(
            uri,
            CancellationToken.None);
        await publisher.WaitForIdleAsync(uri)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, validationObserver.StartCount);
        Assert.Equal(1, routingObserver.ProjectValidationReservationCount);

        routingObserver.Arm();
        var refresh = Task.Run(
            () => publisher.RefreshProjectDiagnostics(staleLease));
        var currentLease = new VbaProjectValidationLifecycleLease(
            authority,
            revision: 2);
        try
        {
            await routingObserver.RoutingAcquired.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            staleLease.Revoke();
            publisher.ActivateProjectDiagnostics(currentLease);
        }
        finally
        {
            routingObserver.Release();
        }

        await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        await publisher.WaitForIdleAsync(uri)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, validationObserver.StartCount);
        Assert.Equal(1, routingObserver.ProjectValidationReservationCount);

        publisher.RefreshProjectDiagnostics(currentLease);
        await publisher.WaitForIdleAsync(uri)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, validationObserver.StartCount);
        Assert.Equal(2, routingObserver.ProjectValidationReservationCount);
    }

    [Fact]
    public async Task Older_retirement_cannot_remove_routing_from_a_newer_lifecycle_activation()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var validationObserver = new CountingProjectValidationBuildObserver();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            NullVbaDocumentAnalysisBuildObserver.Instance,
            validationObserver);
        workspace.OpenDocument(uri, 1, "Public Sub Run()\nEnd Sub\n");
        var authority = Assert.IsType<VbaProjectDiagnosticsCapture>(
            workspace.CaptureProjectDiagnostics(
                uri,
                CancellationToken.None)).Authority;
        await using var output = new CapturingWriteStream();
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        publisher.AttachScheduler(scheduler);
        await publisher.PublishProjectDiagnosticsAsync(
            uri,
            CancellationToken.None);
        await publisher.WaitForIdleAsync(uri)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, validationObserver.StartCount);

        var olderLease = new VbaProjectValidationLifecycleLease(
            authority,
            revision: 1);
        publisher.ActivateProjectDiagnostics(olderLease);
        olderLease.Revoke();
        var newerLease = new VbaProjectValidationLifecycleLease(
            authority,
            revision: 2);
        publisher.ActivateProjectDiagnostics(newerLease);
        publisher.RetireProjectDiagnostics(olderLease);
        Assert.True(workspace.ChangeDocument(
            uri,
            2,
            "Public Sub Latest()\nEnd Sub\n"));
        publisher.RefreshProjectDiagnostics(newerLease);
        await publisher.WaitForIdleAsync(uri)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, validationObserver.StartCount);
    }

    [Fact]
    public async Task Retired_project_lifecycle_authorities_do_not_retain_path_state()
    {
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()));
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, Stream.Null),
            workspace);
        publisher.AttachScheduler(scheduler);

        for (var index = 0; index < 256; index++)
        {
            var uri = $"file:///C:/retired-project-{index}/Worker.bas";
            Assert.True(VbaProjectIdentityModel.TryIdentifyAuthority(
                VbaProjectResolver.Resolve(uri),
                out var authority));
            var lease = new VbaProjectValidationLifecycleLease(
                authority,
                index + 1);

            publisher.ActivateProjectDiagnostics(lease);
            lease.Revoke();
            publisher.RetireProjectDiagnostics(lease);
        }

        Assert.Equal(0, publisher.RetainedProjectValidationLifecycleStateCount);
        Assert.Equal(0, publisher.RetainedProjectValidationRoutingStateCount);
    }

    [Fact]
    public async Task Revoked_delayed_activation_cannot_restore_retired_lifecycle_state()
    {
        const string uri = "file:///C:/work/Worker.bas";
        Assert.True(VbaProjectIdentityModel.TryIdentifyAuthority(
            VbaProjectResolver.Resolve(uri),
            out var authority));
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()));
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, Stream.Null),
            workspace);
        publisher.AttachScheduler(scheduler);
        var retiredLease = new VbaProjectValidationLifecycleLease(
            authority,
            revision: 1);

        retiredLease.Revoke();
        publisher.RetireProjectDiagnostics(retiredLease);
        publisher.ActivateProjectDiagnostics(retiredLease);

        Assert.Equal(0, publisher.RetainedProjectValidationLifecycleStateCount);
        Assert.Equal(0, publisher.RetainedProjectValidationRoutingStateCount);
    }

    [Fact]
    public async Task Retirement_between_refresh_lookup_and_recapture_cannot_restore_routing()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var validationObserver = new CountingProjectValidationBuildObserver();
        var routingObserver = new ArmableBlockingRoutingAcquisitionObserver();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            NullVbaDocumentAnalysisBuildObserver.Instance,
            validationObserver);
        workspace.OpenDocument(uri, 1, "Public Sub Run()\nEnd Sub\n");
        Assert.True(workspace.TryCaptureProjectDiagnosticsAuthority(
            uri,
            CancellationToken.None,
            out var authority));
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, Stream.Null),
            workspace,
            routingObserver);
        publisher.AttachScheduler(scheduler);
        var lease = new VbaProjectValidationLifecycleLease(
            authority,
            revision: 1);
        publisher.ActivateProjectDiagnostics(lease);
        await publisher.PublishProjectDiagnosticsAsync(
            uri,
            CancellationToken.None);
        await publisher.WaitForIdleAsync(uri)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, validationObserver.StartCount);

        Task? refresh = null;
        try
        {
            routingObserver.Arm();
            refresh = Task.Run(
                () => publisher.RefreshProjectDiagnostics(lease));
            await routingObserver.RoutingAcquired.Task
                .WaitAsync(TimeSpan.FromSeconds(5));

            lease.Revoke();
            publisher.RetireProjectDiagnostics(lease);
            routingObserver.Release();
            await refresh.WaitAsync(TimeSpan.FromSeconds(5));
            await publisher.WaitForIdleAsync(uri)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, validationObserver.StartCount);
            Assert.Equal(
                0,
                publisher.RetainedProjectValidationLifecycleStateCount);
            Assert.Equal(
                0,
                publisher.RetainedProjectValidationRoutingStateCount);
        }
        finally
        {
            routingObserver.Release();
            if (refresh is not null && !refresh.IsCompleted)
            {
                try
                {
                    await refresh.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                }
            }
        }
    }

    [Fact]
    public async Task Stop_cancels_refresh_recapture_before_mailbox_reservation()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var buildObserver =
            new ArmableBlockingProjectSnapshotCaptureObserver();
        var publicationObserver = new CountingDiagnosticsPublicationObserver();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            NullVbaDocumentAnalysisBuildObserver.Instance,
            buildObserver);
        workspace.OpenDocument(uri, 1, "Public Sub Run()\nEnd Sub\n");
        Assert.True(VbaProjectIdentityModel.TryIdentifyAuthority(
            VbaProjectResolver.Resolve(uri),
            out var authority));
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, Stream.Null),
            workspace,
            publicationObserver);
        publisher.AttachScheduler(scheduler);
        var lease = new VbaProjectValidationLifecycleLease(
            authority,
            revision: 1);
        publisher.ActivateProjectDiagnostics(lease);
        await publisher.PublishProjectDiagnosticsAsync(
            uri,
            CancellationToken.None);
        await publisher.WaitForIdleAsync(uri)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, buildObserver.ValidationStartCount);
        Assert.Equal(1, publicationObserver.ProjectReservationCount);

        buildObserver.Arm();
        var refresh = Task.Run(
            () => publisher.RefreshProjectDiagnostics(lease));
        try
        {
            await buildObserver.CaptureStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5));

            var stop = Task.Run(publisher.Stop);
            await buildObserver.CancellationCallbackStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            await stop.WaitAsync(TimeSpan.FromSeconds(1));
            await buildObserver.CaptureCancelled.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            buildObserver.ReleaseCancellationCallback();
            await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            buildObserver.Release();
        }

        Assert.Equal(1, buildObserver.ValidationStartCount);
        Assert.Equal(1, publicationObserver.ProjectReservationCount);
        Assert.Equal(0, publisher.RetainedProjectValidationStateCount);
        Assert.Equal(0, publisher.RetainedProjectValidationActivityCount);
        Assert.Equal(0, publisher.RetainedProjectValidationLifecycleStateCount);
        Assert.Equal(0, publisher.RetainedProjectValidationRoutingStateCount);
    }

    [Fact]
    public async Task Lease_revocation_cancels_refresh_recapture_before_mailbox_reservation()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var buildObserver =
            new ArmableBlockingProjectSnapshotCaptureObserver();
        var publicationObserver = new CountingDiagnosticsPublicationObserver();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            NullVbaDocumentAnalysisBuildObserver.Instance,
            buildObserver);
        workspace.OpenDocument(uri, 1, "Public Sub Run()\nEnd Sub\n");
        Assert.True(VbaProjectIdentityModel.TryIdentifyAuthority(
            VbaProjectResolver.Resolve(uri),
            out var authority));
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, Stream.Null),
            workspace,
            publicationObserver);
        publisher.AttachScheduler(scheduler);
        var lease = new VbaProjectValidationLifecycleLease(
            authority,
            revision: 1);
        publisher.ActivateProjectDiagnostics(lease);
        await publisher.PublishProjectDiagnosticsAsync(
            uri,
            CancellationToken.None);
        await publisher.WaitForIdleAsync(uri)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, buildObserver.ValidationStartCount);
        Assert.Equal(1, publicationObserver.ProjectReservationCount);

        buildObserver.Arm();
        var refresh = Task.Run(
            () => publisher.RefreshProjectDiagnostics(lease));
        try
        {
            await buildObserver.CaptureStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5));

            var revoke = Task.Run(lease.Revoke);
            await buildObserver.CancellationCallbackStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            await revoke.WaitAsync(TimeSpan.FromSeconds(1));
            await buildObserver.CaptureCancelled.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            buildObserver.ReleaseCancellationCallback();
            await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            buildObserver.Release();
        }

        publisher.RetireProjectDiagnostics(lease);
        Assert.Equal(1, buildObserver.ValidationStartCount);
        Assert.Equal(1, publicationObserver.ProjectReservationCount);
        Assert.Equal(0, publisher.RetainedProjectValidationStateCount);
        Assert.Equal(0, publisher.RetainedProjectValidationActivityCount);
        Assert.Equal(0, publisher.RetainedProjectValidationLifecycleStateCount);
        Assert.Equal(0, publisher.RetainedProjectValidationRoutingStateCount);
    }

    [Fact]
    public async Task Lease_revocation_cancels_reserved_validation_and_allows_replacement_progress()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var validationObserver =
            new BlockingSecondCancellableProjectValidationBuildObserver();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            NullVbaDocumentAnalysisBuildObserver.Instance,
            validationObserver);
        workspace.OpenDocument(uri, 1, "Public Sub Run()\nEnd Sub\n");
        Assert.True(workspace.TryCaptureProjectDiagnosticsAuthority(
            uri,
            CancellationToken.None,
            out var authority));
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, Stream.Null),
            workspace);
        publisher.AttachScheduler(scheduler);
        await publisher.PublishProjectDiagnosticsAsync(
            uri,
            CancellationToken.None);
        await publisher.WaitForIdleAsync(uri)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(workspace.ChangeDocument(
            uri,
            2,
            "Public Sub Updated()\nEnd Sub\n"));

        var staleLease = new VbaProjectValidationLifecycleLease(
            authority,
            revision: 1);
        var currentLease = new VbaProjectValidationLifecycleLease(
            authority,
            revision: 2);
        publisher.ActivateProjectDiagnostics(staleLease);
        publisher.RefreshProjectDiagnostics(staleLease);
        await validationObserver.SecondValidationStarted.Task
            .WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            staleLease.Revoke();
            publisher.ActivateProjectDiagnostics(currentLease);
            await validationObserver.SecondValidationCancelled.Task
                .WaitAsync(TimeSpan.FromSeconds(1));

            publisher.RefreshProjectDiagnostics(currentLease);
            await validationObserver.ThirdValidationStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            await publisher.WaitForIdleAsync(uri)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            validationObserver.ReleaseSecondValidation();
            currentLease.Revoke();
            publisher.RetireProjectDiagnostics(currentLease);
        }

        Assert.Equal(3, validationObserver.StartCount);
        Assert.True(scheduler.IsAccepting);
        Assert.Equal(0, publisher.RetainedProjectValidationStateCount);
    }

    [Fact]
    public async Task Accepted_source_change_cancels_validation_before_replacement_recapture()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var validationObserver =
            new BlockingFirstCancellableProjectValidationBuildObserver();
        var routingObserver = new ArmableBlockingRoutingAcquisitionObserver();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            NullVbaDocumentAnalysisBuildObserver.Instance,
            validationObserver);
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, Stream.Null),
            workspace,
            routingObserver);
        publisher.AttachScheduler(scheduler);
        var pipeline = new VbaDocumentChangePipeline(
            workspace,
            new RecordingReferenceCatalogLifecycle(),
            publisher);
        await pipeline.ApplyAsync(
            new VbaTextDocumentOpenedChange(
                uri,
                1,
                "Public Sub First()\nEnd Sub\n"),
            CancellationToken.None);
        await validationObserver.FirstValidationStarted.Task
            .WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            routingObserver.Arm();
            var change = Task.Run(
                () => pipeline.ApplyAsync(
                        new VbaTextDocumentChangedChange(
                            uri,
                            2,
                            "Public Sub Latest()\nEnd Sub\n"),
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult());
            await routingObserver.RoutingAcquired.Task
                .WaitAsync(TimeSpan.FromSeconds(5));

            await validationObserver.FirstValidationCancelled.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(change.IsCompleted);

            routingObserver.Release();
            await change.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            routingObserver.Release();
            validationObserver.ReleaseFirstValidation();
        }

        await publisher.WaitForIdleAsync(uri)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, validationObserver.StartCount);
    }

    [Fact]
    public async Task Retirement_after_authority_resolution_rejects_the_unbound_attempt_and_keeps_sibling_idle_pending()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-authority-routing-attempt-").FullName;
        var authorityObserver =
            new ArmableBlockingAuthorityResolutionObserver();
        var validationObserver = new CountingProjectValidationBuildObserver();
        try
        {
            var firstPath = Path.Combine(projectRoot, "First.bas");
            var secondPath = Path.Combine(projectRoot, "Second.bas");
            const string firstText = "Attribute VB_Name = \"First\"\n"
                + "Public Sub FirstRun()\nEnd Sub\n";
            const string secondText = "Attribute VB_Name = \"Second\"\n"
                + "Public Sub SecondRun()\nEnd Sub\n";
            File.WriteAllText(firstPath, firstText);
            File.WriteAllText(secondPath, secondText);
            var firstUri = new Uri(firstPath).AbsoluteUri;
            var secondUri = new Uri(secondPath).AbsoluteUri;
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()),
                NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
                NullVbaDocumentAnalysisBuildObserver.Instance,
                validationObserver);
            workspace.OpenDocument(firstUri, 1, firstText);
            workspace.OpenDocument(secondUri, 1, secondText);
            Assert.True(workspace.TryCaptureProjectDiagnosticsAuthority(
                firstUri,
                CancellationToken.None,
                out var authority));
            await using var scheduler = new VbaInteractiveWorkScheduler();
            var publisher = new VbaDiagnosticsPublisher(
                new LspMessageTransport(Stream.Null, Stream.Null),
                workspace,
                authorityObserver);
            publisher.AttachScheduler(scheduler);
            var lease = new VbaProjectValidationLifecycleLease(
                authority,
                revision: 1);
            publisher.ActivateProjectDiagnostics(lease);
            await publisher.PublishProjectDiagnosticsAsync(
                firstUri,
                CancellationToken.None);
            await publisher.WaitForIdleAsync(firstUri)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, validationObserver.StartCount);

            authorityObserver.Arm();
            var staleAttempt = Task.Run(
                () => publisher.PublishProjectDiagnosticsAsync(
                    firstUri,
                    CancellationToken.None));
            await authorityObserver.AuthorityResolved.Task
                .WaitAsync(TimeSpan.FromSeconds(5));

            lease.Revoke();
            publisher.RetireProjectDiagnostics(lease);
            var siblingIdle = publisher.WaitForIdleAsync(secondUri);
            Assert.False(siblingIdle.IsCompleted);

            authorityObserver.Release();
            await staleAttempt.WaitAsync(TimeSpan.FromSeconds(5));
            await siblingIdle.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, validationObserver.StartCount);
            Assert.Equal(0, publisher.RetainedProjectValidationRoutingStateCount);
            Assert.Equal(0, publisher.RetainedProjectValidationActivityCount);
        }
        finally
        {
            authorityObserver.Release();
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Source_invalidation_cancels_known_validation_before_manifest_authority_resolution()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-invalidation-manifest-barrier-").FullName;
        var fileSystem = new ArmableBlockingManifestResolutionFileSystem();
        var validationObserver =
            new BlockingFirstCancellableProjectValidationBuildObserver();
        try
        {
            var sourceRoot = Directory.CreateDirectory(
                Path.Combine(projectRoot, "src", "Book1")).FullName;
            File.WriteAllText(
                Path.Combine(projectRoot, "vba-project.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    projectName = "InvalidationBarrier",
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
            var sourcePath = Path.Combine(sourceRoot, "Worker.bas");
            const string text = "Attribute VB_Name = \"Worker\"\n"
                + "Public Sub Run()\nEnd Sub\n";
            File.WriteAllText(sourcePath, text);
            var uri = new Uri(sourcePath).AbsoluteUri;
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()),
                NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
                NullVbaDocumentAnalysisBuildObserver.Instance,
                validationObserver,
                fileSystem);
            workspace.OpenDocument(uri, 1, text);
            await using var scheduler = new VbaInteractiveWorkScheduler();
            var publisher = new VbaDiagnosticsPublisher(
                new LspMessageTransport(Stream.Null, Stream.Null),
                workspace);
            publisher.AttachScheduler(scheduler);
            await publisher.PublishProjectDiagnosticsAsync(
                uri,
                CancellationToken.None);
            await validationObserver.FirstValidationStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5));

            fileSystem.Arm();
            var invalidation = Task.Run(
                () => publisher.InvalidateProjectValidationsForDocuments(
                    [uri]));
            await fileSystem.ManifestReadStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5));

            await validationObserver.FirstValidationCancelled.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(invalidation.IsCompleted);

            fileSystem.Release();
            await invalidation.WaitAsync(TimeSpan.FromSeconds(5));
            validationObserver.ReleaseFirstValidation();
            await publisher.WaitForIdleAsync(uri)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, publisher.RetainedProjectValidationActivityCount);
        }
        finally
        {
            fileSystem.Release();
            validationObserver.ReleaseFirstValidation();
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Stop_cancels_pre_routing_manifest_resolution_and_rejects_later_capture_begin()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-stop-manifest-resolution-").FullName;
        var fileSystem = new ArmableBlockingManifestResolutionFileSystem();
        try
        {
            var sourceRoot = Directory.CreateDirectory(
                Path.Combine(projectRoot, "src", "Book1")).FullName;
            File.WriteAllText(
                Path.Combine(projectRoot, "vba-project.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    projectName = "StopBarrier",
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
            var sourcePath = Path.Combine(sourceRoot, "Worker.bas");
            const string text = "Attribute VB_Name = \"Worker\"\n"
                + "Public Sub Run()\nEnd Sub\n";
            File.WriteAllText(sourcePath, text);
            var uri = new Uri(sourcePath).AbsoluteUri;
            var validationObserver =
                new CountingProjectValidationBuildObserver();
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()),
                NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
                NullVbaDocumentAnalysisBuildObserver.Instance,
                validationObserver,
                fileSystem);
            workspace.OpenDocument(uri, 1, text);
            await using var scheduler = new VbaInteractiveWorkScheduler();
            var publisher = new VbaDiagnosticsPublisher(
                new LspMessageTransport(Stream.Null, Stream.Null),
                workspace);
            publisher.AttachScheduler(scheduler);

            fileSystem.Arm();
            var publication = Task.Run(
                () => publisher.PublishProjectDiagnosticsAsync(
                    uri,
                    CancellationToken.None));
            await fileSystem.ManifestReadStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5));

            publisher.Stop();
            await fileSystem.ManifestReadCancelled.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            await publication.WaitAsync(TimeSpan.FromSeconds(5));
            var blockedReadsAfterStop = fileSystem.BlockedReadCount;

            await publisher.PublishProjectDiagnosticsAsync(
                uri,
                CancellationToken.None);

            Assert.Equal(blockedReadsAfterStop, fileSystem.BlockedReadCount);
            Assert.Equal(0, validationObserver.StartCount);
            Assert.Equal(0, publisher.RetainedProjectValidationActivityCount);
            Assert.Equal(0, publisher.RetainedProjectValidationRoutingStateCount);
        }
        finally
        {
            fileSystem.Release();
            Directory.Delete(projectRoot, recursive: true);
        }
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
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()));
        var observer = new BlockingFirstRevisionObserver();
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace,
            observer);
        publisher.AttachScheduler(scheduler);
        Task? blockerCompletion = null;
        Task? first = null;
        try
        {
            var blocker = scheduler.AdmitMutation(async cancellationToken =>
            {
                blockerStarted.TrySetResult();
                await releaseBlocker.Task.WaitAsync(cancellationToken);
            });
            blockerCompletion = blocker.Completion;
            await blockerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            first = Task.Run(
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
            await blockerCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            await publisher.WaitForIdleAsync(uri)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, output.MessageCount);

            await scheduler.StopAsync(VbaInteractiveStopReason.Complete)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            observer.ReleaseFirstRevision();
            releaseBlocker.TrySetResult();
            if (first is not null && !first.IsCompleted)
            {
                try
                {
                    await first.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                }
            }

            if (blockerCompletion is not null
                && !blockerCompletion.IsCompleted)
            {
                try
                {
                    await blockerCompletion.WaitAsync(
                        TimeSpan.FromSeconds(5));
                }
                catch
                {
                }
            }
        }
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
        Task? blockerCompletion = null;
        Task? staleBatch = null;
        try
        {
            var blocker = scheduler.AdmitMutation(async cancellationToken =>
            {
                blockerStarted.TrySetResult();
                await releaseBlocker.Task.WaitAsync(cancellationToken);
            });
            blockerCompletion = blocker.Completion;
            await blockerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            staleBatch = Task.Run(
                () => publisher.PublishProjectDiagnosticsAsync(
                    firstUri,
                    CancellationToken.None));
            releaseBlocker.TrySetResult();
            await blockerCompletion.WaitAsync(TimeSpan.FromSeconds(5));
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
            await Task.WhenAll(
                    publisher.WaitForIdleAsync(firstUri),
                    publisher.WaitForIdleAsync(secondUri))
                .WaitAsync(TimeSpan.FromSeconds(5));

            var notification = Assert.Single(
                ReadJsonMessages(output.ReadText()));
            var parameters = Assert.IsType<JsonObject>(notification["params"]);
            Assert.Equal(closingUri, parameters["uri"]?.GetValue<string>());
            Assert.Null(parameters["version"]);
            Assert.Empty(Assert.IsType<JsonArray>(parameters["diagnostics"]));
        }
        finally
        {
            observer.ReleaseFirstRevision();
            releaseBlocker.TrySetResult();
            if (staleBatch is not null && !staleBatch.IsCompleted)
            {
                try
                {
                    await staleBatch.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                }
            }

            if (blockerCompletion is not null
                && !blockerCompletion.IsCompleted)
            {
                try
                {
                    await blockerCompletion.WaitAsync(
                        TimeSpan.FromSeconds(5));
                }
                catch
                {
                }
            }
        }
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
                    hasWindowsAcpAuthority: true,
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Template_change_after_diagnostic_capture_prevents_stale_module_identity_conflict_publication(
        bool templateExistsAtCapture)
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
            var templateBytes = VbaProjectIdentityWorkbookFixture.Create(
                "ContainingProject",
                1252);
            if (templateExistsAtCapture)
            {
                File.WriteAllBytes(templatePath, templateBytes);
            }
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
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()));
            workspace.OpenDocument(uri, 7, text);
            _ = workspace.CreateProjectSnapshot(uri);
            var diagnosticSnapshot = Assert.Single(
                workspace.GetProjectDiagnosticsSnapshots(uri)!);
            Assert.Equal(
                templateExistsAtCapture,
                diagnosticSnapshot.ProjectValidationDiagnostics.Any(
                    diagnostic => diagnostic.Code
                        == "validation.moduleIdentityNameConflict"));

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
            File.WriteAllBytes(
                templatePath,
                templateExistsAtCapture
                    ? [0x22, 0x44, 0x66, 0x88]
                    : templateBytes);
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
    public async Task Close_republishes_previously_reported_encoding_failure_with_open_project_peer()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-close-invalid-peer-").FullName;
        try
        {
            var callerPath = Path.Combine(projectRoot, "Caller.bas");
            var helperPath = Path.Combine(projectRoot, "Helper.bas");
            var callerUri = new Uri(callerPath).AbsoluteUri;
            var helperUri = new Uri(helperPath).AbsoluteUri;
            const string callerText = "Attribute VB_Name = \"Caller\"\n"
                + "Public Sub Run()\n    BuildValue\nEnd Sub\n";
            const string helperText = "Attribute VB_Name = \"Helper\"\n"
                + "'* @brief 日本語\n"
                + "Public Function BuildValue() As String\nEnd Function\n";
            File.WriteAllText(callerPath, callerText);
            File.WriteAllBytes(helperPath, [0xEF, 0xBB, 0xBF, 0xC3, 0x28]);
            await using var output = new CapturingWriteStream();
            await using var scheduler = new VbaInteractiveWorkScheduler();
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()),
                new DiskSourceDecoding(
                    hasWindowsAcpAuthority: true,
                    activeCodePage: 932));
            var publisher = new VbaDiagnosticsPublisher(
                new LspMessageTransport(Stream.Null, output),
                workspace);
            publisher.AttachScheduler(scheduler);
            var pipeline = new VbaDocumentChangePipeline(
                workspace,
                new RecordingReferenceCatalogLifecycle(),
                publisher);
            await pipeline.ApplyAsync(
                new VbaTextDocumentOpenedChange(callerUri, 1, callerText),
                CancellationToken.None);
            _ = workspace.CreateProjectSnapshot(callerUri);
            await Task.WhenAll(
                    publisher.WaitForIdleAsync(callerUri),
                    publisher.WaitForIdleAsync(helperUri))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(workspace.GetDiskSourceFailure(helperUri));
            var initialParameters = ReadJsonMessages(output.ReadText())
                .Select(message => Assert.IsType<JsonObject>(message["params"]))
                .Last(parameters => parameters["uri"]?.GetValue<string>() == helperUri);
            Assert.Equal(
                "invalid-disk-source-encoding",
                Assert.IsType<JsonObject>(Assert.Single(
                    Assert.IsType<JsonArray>(initialParameters["diagnostics"])))
                    ["code"]?.GetValue<string>());

            await pipeline.ApplyAsync(
                new VbaTextDocumentOpenedChange(helperUri, 1, helperText),
                CancellationToken.None);
            var openSnapshot = workspace.CreateProjectSnapshot(callerUri);
            await Task.WhenAll(
                    publisher.WaitForIdleAsync(callerUri),
                    publisher.WaitForIdleAsync(helperUri))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(helperText, openSnapshot.SourceDocuments[helperUri]);
            Assert.Contains(
                openSnapshot.SemanticInventory.GetWorkspaceSymbols("BuildValue"),
                symbol => symbol.Uri == helperUri);
            Assert.Null(workspace.GetDiskSourceFailure(helperUri));
            var openParameters = ReadJsonMessages(output.ReadText())
                .Select(message => Assert.IsType<JsonObject>(message["params"]))
                .Last(parameters => parameters["uri"]?.GetValue<string>() == helperUri);
            Assert.Empty(Assert.IsType<JsonArray>(openParameters["diagnostics"]));

            await pipeline.ApplyAsync(
                new VbaTextDocumentClosedChange(helperUri),
                CancellationToken.None);
            var closedSnapshot = workspace.CreateProjectSnapshot(callerUri);
            await Task.WhenAll(
                    publisher.WaitForIdleAsync(callerUri),
                    publisher.WaitForIdleAsync(helperUri))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.DoesNotContain(helperUri, closedSnapshot.SourceDocuments.Keys);
            Assert.Empty(closedSnapshot.SemanticInventory.GetWorkspaceSymbols("BuildValue"));
            Assert.NotNull(workspace.GetDiskSourceFailure(helperUri));
            var closedParameters = ReadJsonMessages(output.ReadText())
                .Select(message => Assert.IsType<JsonObject>(message["params"]))
                .Last(parameters => parameters["uri"]?.GetValue<string>() == helperUri);
            var codes = Assert.IsType<JsonArray>(closedParameters["diagnostics"])
                .Select(diagnostic => Assert.IsType<JsonObject>(diagnostic)["code"]?.GetValue<string>())
                .ToArray();
            Assert.Contains("invalid-disk-source-encoding", codes);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Close_reads_repaired_disk_without_republishing_hidden_failure_with_open_peer()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-close-repaired-peer-").FullName;
        try
        {
            var callerPath = Path.Combine(projectRoot, "Caller.bas");
            var helperPath = Path.Combine(projectRoot, "Helper.bas");
            var callerUri = new Uri(callerPath).AbsoluteUri;
            var helperUri = new Uri(helperPath).AbsoluteUri;
            const string callerText = "Attribute VB_Name = \"Caller\"\n"
                + "Public Sub Run()\nEnd Sub\n";
            const string openText = "Attribute VB_Name = \"Helper\"\n"
                + "Public Sub OpenValue()\nEnd Sub\n";
            const string repairedText = "Attribute VB_Name = \"Helper\"\n"
                + "'* @brief 日本語\nPublic Sub DiskValue()\nEnd Sub\n";
            File.WriteAllText(callerPath, callerText);
            File.WriteAllBytes(helperPath, [0xEF, 0xBB, 0xBF, 0xC3, 0x28]);
            await using var output = new CapturingWriteStream();
            await using var scheduler = new VbaInteractiveWorkScheduler();
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()),
                new DiskSourceDecoding(
                    hasWindowsAcpAuthority: true,
                    activeCodePage: 932));
            var publisher = new VbaDiagnosticsPublisher(
                new LspMessageTransport(Stream.Null, output),
                workspace);
            publisher.AttachScheduler(scheduler);
            var pipeline = new VbaDocumentChangePipeline(
                workspace,
                new RecordingReferenceCatalogLifecycle(),
                publisher);

            await pipeline.ApplyAsync(
                new VbaTextDocumentOpenedChange(callerUri, 1, callerText),
                CancellationToken.None);
            _ = workspace.CreateProjectSnapshot(callerUri);
            await Task.WhenAll(
                    publisher.WaitForIdleAsync(callerUri),
                    publisher.WaitForIdleAsync(helperUri))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(workspace.GetDiskSourceFailure(helperUri));
            var initialParameters = ReadJsonMessages(output.ReadText())
                .Select(message => Assert.IsType<JsonObject>(message["params"]))
                .Last(parameters => parameters["uri"]?.GetValue<string>() == helperUri);
            Assert.Equal(
                "invalid-disk-source-encoding",
                Assert.IsType<JsonObject>(Assert.Single(
                    Assert.IsType<JsonArray>(initialParameters["diagnostics"])))
                    ["code"]?.GetValue<string>());

            await pipeline.ApplyAsync(
                new VbaTextDocumentOpenedChange(helperUri, 1, openText),
                CancellationToken.None);
            File.WriteAllText(helperPath, repairedText, new UTF8Encoding(true, true));
            await pipeline.ApplyAsync(
                new VbaWatchedFileReloadChange(helperUri),
                CancellationToken.None);
            Assert.Equal(openText, workspace.CreateProjectSnapshot(callerUri)
                .SourceDocuments[helperUri]);
            Assert.Null(workspace.GetDiskSourceFailure(helperUri));
            await Task.WhenAll(
                    publisher.WaitForIdleAsync(callerUri),
                    publisher.WaitForIdleAsync(helperUri))
                .WaitAsync(TimeSpan.FromSeconds(5));
            var messagesBeforeClose = ReadJsonMessages(output.ReadText()).Count;

            await pipeline.ApplyAsync(
                new VbaTextDocumentClosedChange(helperUri),
                CancellationToken.None);
            var closedSnapshot = workspace.CreateProjectSnapshot(callerUri);
            await Task.WhenAll(
                    publisher.WaitForIdleAsync(callerUri),
                    publisher.WaitForIdleAsync(helperUri))
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(repairedText, closedSnapshot.SourceDocuments[helperUri]);
            Assert.Contains(
                closedSnapshot.SemanticInventory.GetWorkspaceSymbols("DiskValue"),
                symbol => symbol.Uri == helperUri);
            Assert.Empty(closedSnapshot.SemanticInventory.GetWorkspaceSymbols("OpenValue"));
            Assert.Null(workspace.GetDiskSourceFailure(helperUri));
            var closePublications = ReadJsonMessages(output.ReadText())
                .Skip(messagesBeforeClose)
                .Select(message => Assert.IsType<JsonObject>(message["params"]))
                .Where(parameters => parameters["uri"]?.GetValue<string>() == helperUri)
                .ToArray();
            Assert.NotEmpty(closePublications);
            Assert.All(closePublications, parameters =>
                Assert.Empty(Assert.IsType<JsonArray>(parameters["diagnostics"])));
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(932)]
    [InlineData(1252)]
    [InlineData(65001)]
    public async Task Close_readmits_invalid_disk_after_open_unicode_authority(
        int activeCodePage)
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-close-invalid-source-").FullName;
        try
        {
            var sourcePath = Path.Combine(projectRoot, "Worker.bas");
            var uri = new Uri(sourcePath).AbsoluteUri;
            File.WriteAllBytes(sourcePath, [0xEF, 0xBB, 0xBF, 0xC3, 0x28]);
            const string openText = "Attribute VB_Name = \"Worker\"\n"
                + "Public Sub 日本語()\nEnd Sub\n";
            await using var output = new CapturingWriteStream();
            await using var scheduler = new VbaInteractiveWorkScheduler();
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()),
                new DiskSourceDecoding(
                    hasWindowsAcpAuthority: true,
                    activeCodePage));
            var publisher = new VbaDiagnosticsPublisher(
                new LspMessageTransport(Stream.Null, output),
                workspace);
            publisher.AttachScheduler(scheduler);
            var pipeline = new VbaDocumentChangePipeline(
                workspace,
                new RecordingReferenceCatalogLifecycle(),
                publisher);

            await pipeline.ApplyAsync(
                new VbaTextDocumentOpenedChange(uri, 1, openText),
                CancellationToken.None);
            await pipeline.ApplyAsync(
                new VbaWatchedFileReloadChange(uri),
                CancellationToken.None);
            var openSnapshot = workspace.CreateProjectSnapshot(uri);
            await publisher.WaitForIdleAsync(uri)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(openText, openSnapshot.SourceDocuments[uri]);
            Assert.Null(workspace.GetDiskSourceFailure(uri));
            Assert.Contains(
                openSnapshot.SemanticInventory.GetWorkspaceSymbols("日本語"),
                symbol => symbol.Uri == uri);
            var openParameters = ReadJsonMessages(output.ReadText())
                .Select(message => Assert.IsType<JsonObject>(message["params"]))
                .Last(parameters => parameters["uri"]?.GetValue<string>() == uri);
            Assert.Empty(Assert.IsType<JsonArray>(openParameters["diagnostics"]));

            await pipeline.ApplyAsync(
                new VbaTextDocumentClosedChange(uri),
                CancellationToken.None);
            var closedSnapshot = workspace.CreateProjectSnapshot(uri);
            await publisher.WaitForIdleAsync(uri)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.DoesNotContain(uri, closedSnapshot.SourceDocuments.Keys);
            Assert.Empty(closedSnapshot.SemanticInventory.GetWorkspaceSymbols("日本語"));
            Assert.Null(workspace.GetDocumentText(uri));
            Assert.NotNull(workspace.GetDiskSourceFailure(uri));
            var closedParameters = ReadJsonMessages(output.ReadText())
                .Select(message => Assert.IsType<JsonObject>(message["params"]))
                .Last(parameters => parameters["uri"]?.GetValue<string>() == uri);
            var diagnostic = Assert.IsType<JsonObject>(
                Assert.Single(Assert.IsType<JsonArray>(closedParameters["diagnostics"])));
            Assert.Equal(
                "invalid-disk-source-encoding",
                diagnostic["code"]?.GetValue<string>());
            Assert.Contains(sourcePath, diagnostic["message"]?.GetValue<string>());
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(932, "日本語")]
    [InlineData(1252, "Café")]
    [InlineData(65001, "日本語 Café")]
    public async Task Watched_invalid_closed_source_recovers_with_fixed_acp_and_clears_on_deletion(
        int activeCodePage,
        string documentation)
    {
        var sourcePath = Path.Combine(
            Directory.CreateTempSubdirectory("vba-ls-invalid-source-").FullName,
            "Worker.bas");
        try
        {
            byte[] invalidBytes = [0xEF, 0xBB, 0xBF, 0xC3, 0x28];
            File.WriteAllBytes(sourcePath, invalidBytes);
            var uri = new Uri(sourcePath).AbsoluteUri;
            await using var output = new CapturingWriteStream();
            await using var scheduler = new VbaInteractiveWorkScheduler();
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()),
                new DiskSourceDecoding(
                    hasWindowsAcpAuthority: true,
                    activeCodePage));
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

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var encoding = Encoding.GetEncoding(
                activeCodePage,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
            var recoveredText = "Attribute VB_Name = \"Worker\"\n"
                + $"'* @brief {documentation}\n"
                + "Public Sub Recovered()\nEnd Sub\n";
            File.WriteAllBytes(sourcePath, encoding.GetBytes(recoveredText));
            await pipeline.ApplyAsync(
                new VbaWatchedFileReloadChange(uri),
                CancellationToken.None);
            await publisher.WaitForIdleAsync(uri)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Null(workspace.GetDiskSourceFailure(uri));
            Assert.Equal(recoveredText, workspace.GetDocumentText(uri));
            var recoveredDefinition = Assert.Single(
                workspace.CreateProjectSnapshot(uri).SemanticInventory
                    .GetDocumentDefinitions(uri),
                definition => definition.Name == "Recovered");
            Assert.Equal(documentation, recoveredDefinition.Documentation);
            var recoveredParameters = ReadJsonMessages(output.ReadText())
                .Select(message => Assert.IsType<JsonObject>(message["params"]))
                .Last(parameters => parameters["uri"]?.GetValue<string>() == uri);
            Assert.Empty(Assert.IsType<JsonArray>(recoveredParameters["diagnostics"]));

            File.WriteAllBytes(sourcePath, invalidBytes);
            await pipeline.ApplyAsync(
                new VbaWatchedFileReloadChange(uri),
                CancellationToken.None);
            await publisher.WaitForIdleAsync(uri)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(workspace.GetDiskSourceFailure(uri));
            Assert.Empty(workspace.CreateProjectSnapshot(uri)
                .SemanticInventory.GetWorkspaceSymbols("Recovered"));
            var invalidParameters = ReadJsonMessages(output.ReadText())
                .Select(message => Assert.IsType<JsonObject>(message["params"]))
                .Last(parameters => parameters["uri"]?.GetValue<string>() == uri);
            Assert.Equal(
                "invalid-disk-source-encoding",
                Assert.IsType<JsonObject>(Assert.Single(
                    Assert.IsType<JsonArray>(invalidParameters["diagnostics"])))
                    ["code"]?.GetValue<string>());

            File.Delete(sourcePath);
            await pipeline.ApplyAsync(
                new VbaWatchedFileDeletedChange(uri),
                CancellationToken.None);
            await publisher.WaitForIdleAsync(uri)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Null(workspace.GetDiskSourceFailure(uri));
            Assert.Null(workspace.GetDocumentText(uri));
            var deletedParameters = ReadJsonMessages(output.ReadText())
                .Select(message => Assert.IsType<JsonObject>(message["params"]))
                .Last(parameters => parameters["uri"]?.GetValue<string>() == uri);
            Assert.Empty(Assert.IsType<JsonArray>(deletedParameters["diagnostics"]));
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
                    hasWindowsAcpAuthority: true,
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
                    hasWindowsAcpAuthority: true,
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
        const string equivalentUri =
            "file:///C:/work/Nested/../Worker.bas";
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
        var idle = publisher.WaitForIdleAsync(equivalentUri);

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

    private sealed class ArmableBlockingRevisionObserver
        : IVbaDiagnosticsPublicationObserver
    {
        private readonly TaskCompletionSource release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int armed;
        private int armAfterProjectValidationRevision;
        private int claimed;

        public TaskCompletionSource BlockedRevision { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void AfterRevisionReserved(string uri, long revision)
        {
            if (Volatile.Read(ref armed) == 0
                || Interlocked.Exchange(ref claimed, 1) != 0)
            {
                return;
            }

            BlockedRevision.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        }

        public void AfterProjectValidationRevisionReserved(
            VbaProjectAuthorityIdentity authority,
            long revision)
        {
            if (Interlocked.Exchange(
                    ref armAfterProjectValidationRevision,
                    0) != 0)
            {
                Volatile.Write(ref armed, 1);
            }
        }

        public void Arm()
            => Volatile.Write(ref armed, 1);

        public void ArmAfterNextProjectValidationRevision()
            => Volatile.Write(ref armAfterProjectValidationRevision, 1);

        public void Release()
            => release.TrySetResult();
    }

    private sealed class BlockingDocumentLocalSnapshotPublicationObserver
        : IVbaDiagnosticsPublicationObserver
    {
        private readonly TaskCompletionSource release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SnapshotCaptured { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void AfterRevisionReserved(string uri, long revision)
        {
        }

        public void AfterDocumentLocalDiagnosticsSnapshotCaptured(
            string uri,
            int? clientVersion)
        {
            SnapshotCaptured.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        }

        public void Release()
            => release.TrySetResult();
    }

    private sealed class BlockingProjectDiagnosticsTransportObserver
        : IVbaDiagnosticsPublicationObserver
    {
        private readonly TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource TransportWriteReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void AfterRevisionReserved(string uri, long revision)
        {
        }

        public void BeforeProjectDiagnosticsTransportWrite(
            VbaProjectAuthorityIdentity authority,
            string uri,
            long revision)
        {
            _ = authority;
            _ = uri;
            _ = revision;
            TransportWriteReached.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        }

        public void Release()
            => release.TrySetResult();
    }

    private sealed class ArmableBlockingRoutingAcquisitionObserver
        : IVbaDiagnosticsPublicationObserver
    {
        private readonly TaskCompletionSource release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int armed;
        private int claimed;
        private int projectValidationReservationCount;

        public TaskCompletionSource RoutingAcquired { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int ProjectValidationReservationCount =>
            Volatile.Read(ref projectValidationReservationCount);

        public void AfterRevisionReserved(string uri, long revision)
        {
        }

        public void AfterProjectValidationRevisionReserved(
            VbaProjectAuthorityIdentity authority,
            long revision)
            => Interlocked.Increment(
                ref projectValidationReservationCount);

        public void AfterProjectValidationRoutingAcquired(
            VbaProjectAuthorityIdentity authority)
        {
            if (Volatile.Read(ref armed) == 0
                || Interlocked.Exchange(ref claimed, 1) != 0)
            {
                return;
            }

            RoutingAcquired.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        }

        public void Arm()
            => Volatile.Write(ref armed, 1);

        public void Release()
            => release.TrySetResult();
    }

    private sealed class ArmableBlockingAuthorityResolutionObserver
        : IVbaDiagnosticsPublicationObserver
    {
        private readonly TaskCompletionSource release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int armed;
        private int claimed;

        public TaskCompletionSource AuthorityResolved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void AfterRevisionReserved(string uri, long revision)
        {
        }

        public void AfterProjectValidationAuthorityResolved(
            VbaProjectAuthorityIdentity authority)
        {
            if (Volatile.Read(ref armed) == 0
                || Interlocked.Exchange(ref claimed, 1) != 0)
            {
                return;
            }

            AuthorityResolved.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        }

        public void Arm()
            => Volatile.Write(ref armed, 1);

        public void Release()
            => release.TrySetResult();
    }

    private sealed class BlockingFirstProjectValidationReservationObserver
        : IVbaDiagnosticsPublicationObserver
    {
        private readonly TaskCompletionSource firstReservationReached = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseFirstReservation = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int reservationCount;

        public TaskCompletionSource FirstReservationReached
            => firstReservationReached;

        public int ReservationCount => Volatile.Read(ref reservationCount);

        public void AfterRevisionReserved(string uri, long revision)
        {
        }

        public void AfterProjectValidationRevisionReserved(
            VbaProjectAuthorityIdentity authority,
            long revision)
        {
            if (Interlocked.Increment(ref reservationCount) != 1)
            {
                return;
            }

            firstReservationReached.TrySetResult();
            releaseFirstReservation.Task.GetAwaiter().GetResult();
        }

        public void ReleaseFirstReservation()
            => releaseFirstReservation.TrySetResult();
    }

    private sealed class CountingDiagnosticsPublicationObserver
        : IVbaDiagnosticsPublicationObserver
    {
        private int documentReservationCount;
        private int projectReservationCount;

        public int DocumentReservationCount =>
            Volatile.Read(ref documentReservationCount);

        public int ProjectReservationCount =>
            Volatile.Read(ref projectReservationCount);

        public void AfterRevisionReserved(string uri, long revision)
            => Interlocked.Increment(ref documentReservationCount);

        public void AfterProjectValidationRevisionReserved(
            VbaProjectAuthorityIdentity authority,
            long revision)
            => Interlocked.Increment(ref projectReservationCount);
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

    private sealed class ArmableBlockingManifestResolutionFileSystem
        : IVbaProjectFileSystem
    {
        private readonly IVbaProjectFileSystem inner =
            SystemVbaProjectFileSystem.Instance;
        private readonly TaskCompletionSource release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int armed;
        private int claimed;
        private int blockedReadCount;

        public TaskCompletionSource ManifestReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ManifestReadCancelled { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int BlockedReadCount => Volatile.Read(ref blockedReadCount);

        public bool FileExists(string path)
            => inner.FileExists(path);

        public bool DirectoryExists(string path)
            => inner.DirectoryExists(path);

        public IEnumerable<string> EnumerateSourceFiles(
            string rootPath,
            string searchPattern,
            SearchOption searchOption)
            => inner.EnumerateSourceFiles(
                rootPath,
                searchPattern,
                searchOption);

        public bool TryGetSourceMetadata(
            string path,
            out VbaProjectSourceFileMetadata metadata)
            => inner.TryGetSourceMetadata(path, out metadata);

        public string ReadManifestText(string path)
            => inner.ReadManifestText(path);

        public string ReadManifestText(
            string path,
            CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref armed) == 0
                || Interlocked.Exchange(ref claimed, 1) != 0)
            {
                return inner.ReadManifestText(path, cancellationToken);
            }

            Interlocked.Increment(ref blockedReadCount);
            ManifestReadStarted.TrySetResult();
            try
            {
                release.Task.WaitAsync(cancellationToken)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (OperationCanceledException)
            {
                ManifestReadCancelled.TrySetResult();
                throw;
            }

            return inner.ReadManifestText(path, cancellationToken);
        }

        public byte[] ReadSourceBytes(string path)
            => inner.ReadSourceBytes(path);

        public byte[] ReadSourceBytes(
            string path,
            CancellationToken cancellationToken)
            => inner.ReadSourceBytes(path, cancellationToken);

        public bool PathsReferToSameEntry(string left, string right)
            => inner.PathsReferToSameEntry(left, right);

        public void Arm()
            => Volatile.Write(ref armed, 1);

        public void Release()
            => release.TrySetResult();
    }

    private sealed class BlockingSecondSuccessfulCatalogDiscovery
        : IVbaProjectReferenceCatalogDiscovery
    {
        private readonly TaskCompletionSource releaseSecondDiscovery = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int discoveryCount;

        public TaskCompletionSource SecondDiscoveryStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref discoveryCount) == 2)
            {
                SecondDiscoveryStarted.TrySetResult();
                await releaseSecondDiscovery.Task.WaitAsync(cancellationToken);
            }

            return VbaProjectReferenceCatalogDiscoveryResult.Success(
                new VbaProjectReferenceCatalogIdentity(
                    referenceName,
                    "{55555555-5555-5555-5555-555555555555}",
                    1,
                    0,
                    0,
                    $@"C:\TypeLibs\{referenceName}.tlb"),
                new VbaProjectReferenceCatalog(
                    referenceName,
                    [],
                    [
                        new VbaProjectReferenceDefinition(
                            referenceName,
                            $"{referenceName.Replace(" ", "", StringComparison.Ordinal)}Type",
                            VbaSourceDefinitionKind.Class)
                    ]));
        }

        public void ReleaseSecondDiscovery()
            => releaseSecondDiscovery.TrySetResult();
    }

    private sealed class InlineReferenceCatalogRefreshWorker
        : IVbaProjectReferenceCatalogRefreshWorker
    {
        public Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            IVbaProjectReferenceCatalogDiscovery discovery,
            string referenceName,
            CancellationToken cancellationToken)
            => discovery.DiscoverAsync(referenceName, cancellationToken);
    }

    private sealed class BlockingFirstProjectSnapshotBuildObserver
        : IVbaProjectSnapshotBuildObserver
    {
        private readonly ManualResetEventSlim release = new();
        private int observedBuilds;
        private int validationStartCount;

        public TaskCompletionSource FirstBuildWaiting { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ValidationStartCount =>
            Volatile.Read(ref validationStartCount);

        public void BeforeBuildProjectValidation(
            string activeUri,
            CancellationToken cancellationToken)
            => Interlocked.Increment(ref validationStartCount);

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

    private sealed class ArmableBlockingProjectSnapshotCaptureObserver
        : IVbaProjectSnapshotBuildObserver
    {
        private readonly ManualResetEventSlim continueCapture = new();
        private readonly ManualResetEventSlim releaseCancellationCallback =
            new();
        private int armed;
        private int claimed;
        private int validationStartCount;

        public TaskCompletionSource CaptureStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CaptureCancelled { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationCallbackStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int ValidationStartCount =>
            Volatile.Read(ref validationStartCount);

        public void BeforeCapture(
            string activeUri,
            CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref armed) == 0
                || Interlocked.Exchange(ref claimed, 1) != 0)
            {
                return;
            }

            using var cancellationRegistration = cancellationToken.Register(
                () =>
                {
                    CancellationCallbackStarted.TrySetResult();
                    continueCapture.Set();
                    releaseCancellationCallback.Wait();
                });
            CaptureStarted.TrySetResult();
            try
            {
                continueCapture.Wait();
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                CaptureCancelled.TrySetResult();
                throw;
            }
        }

        public void BeforeBuildProjectValidation(
            string activeUri,
            CancellationToken cancellationToken)
            => Interlocked.Increment(ref validationStartCount);

        public void BeforeStore(
            long workspaceVersion,
            CancellationToken cancellationToken)
        {
        }

        public void Arm()
            => Volatile.Write(ref armed, 1);

        public void ReleaseCancellationCallback()
            => releaseCancellationCallback.Set();

        public void Release()
        {
            continueCapture.Set();
            releaseCancellationCallback.Set();
        }
    }

    private sealed class BlockingProjectValidationBuildObserver
        : IVbaProjectSnapshotBuildObserver
    {
        private readonly ManualResetEventSlim release = new();
        private int startCount;

        public TaskCompletionSource ValidationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int StartCount => Volatile.Read(ref startCount);

        public void BeforeBuildProjectValidation(
            string activeUri,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref startCount);
            ValidationStarted.TrySetResult();
            release.Wait(cancellationToken);
        }

        public void BeforeStore(
            long workspaceVersion,
            CancellationToken cancellationToken)
        {
        }

        public void ReleaseValidation()
            => release.Set();
    }

    private sealed class BlockingFirstAfterProjectValidationBuildObserver
        : IVbaProjectSnapshotBuildObserver
    {
        private readonly ManualResetEventSlim release = new();
        private int completedBuilds;

        public TaskCompletionSource FirstValidationBuilt { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void AfterBuildProjectValidation(string activeUri)
        {
            if (Interlocked.Increment(ref completedBuilds) != 1)
            {
                return;
            }

            FirstValidationBuilt.TrySetResult();
            release.Wait();
        }

        public void BeforeStore(
            long workspaceVersion,
            CancellationToken cancellationToken)
        {
        }

        public void ReleaseFirstValidation()
            => release.Set();
    }

    private sealed class CountingProjectValidationBuildObserver
        : IVbaProjectSnapshotBuildObserver
    {
        private int startCount;

        public int StartCount => Volatile.Read(ref startCount);

        public void BeforeBuildProjectValidation(
            string activeUri,
            CancellationToken cancellationToken)
            => Interlocked.Increment(ref startCount);

        public void BeforeStore(
            long workspaceVersion,
            CancellationToken cancellationToken)
        {
        }
    }

    private sealed class BlockingSecondProjectValidationBuildObserver
        : IVbaProjectSnapshotBuildObserver
    {
        private readonly TaskCompletionSource releaseSecondValidation = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int startCount;

        public TaskCompletionSource SecondValidationStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void BeforeBuildProjectValidation(
            string activeUri,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref startCount) != 2)
            {
                return;
            }

            SecondValidationStarted.TrySetResult();
            releaseSecondValidation.Task
                .WaitAsync(cancellationToken)
                .GetAwaiter()
                .GetResult();
        }

        public void BeforeStore(
            long workspaceVersion,
            CancellationToken cancellationToken)
        {
        }

        public void ReleaseSecondValidation()
            => releaseSecondValidation.TrySetResult();
    }

    private sealed class BlockingSecondCancellableProjectValidationBuildObserver
        : IVbaProjectSnapshotBuildObserver
    {
        private readonly ManualResetEventSlim releaseSecondValidation = new();
        private int startCount;

        public TaskCompletionSource SecondValidationStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondValidationCancelled { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ThirdValidationStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int StartCount => Volatile.Read(ref startCount);

        public void BeforeBuildProjectValidation(
            string activeUri,
            CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref startCount);
            if (current == 3)
            {
                ThirdValidationStarted.TrySetResult();
                return;
            }

            if (current != 2)
            {
                return;
            }

            SecondValidationStarted.TrySetResult();
            try
            {
                releaseSecondValidation.Wait(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                SecondValidationCancelled.TrySetResult();
                throw;
            }
        }

        public void BeforeStore(
            long workspaceVersion,
            CancellationToken cancellationToken)
        {
        }

        public void ReleaseSecondValidation()
            => releaseSecondValidation.Set();
    }

    private sealed class BlockingFirstCancellableProjectValidationBuildObserver
        : IVbaProjectSnapshotBuildObserver
    {
        private readonly ManualResetEventSlim release = new();
        private int startCount;
        private CancellationToken firstValidationCancellationToken;

        public TaskCompletionSource FirstValidationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstValidationCancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int StartCount => Volatile.Read(ref startCount);

        public bool FirstValidationCancellationRequested
            => firstValidationCancellationToken.IsCancellationRequested;

        public void BeforeBuildProjectValidation(
            string activeUri,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref startCount) != 1)
            {
                return;
            }

            firstValidationCancellationToken = cancellationToken;
            FirstValidationStarted.TrySetResult();
            try
            {
                release.Wait(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                FirstValidationCancelled.TrySetResult();
                throw;
            }
        }

        public void BeforeStore(
            long workspaceVersion,
            CancellationToken cancellationToken)
        {
        }

        public void ReleaseFirstValidation()
            => release.Set();
    }

    private sealed class BlockingNonCooperativeProjectValidationBuildObserver
        : IVbaProjectSnapshotBuildObserver
    {
        private readonly ManualResetEventSlim release = new();
        private int started;

        public TaskCompletionSource ValidationStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void BeforeBuildProjectValidation(
            string activeUri,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref started, 1) != 0)
            {
                return;
            }

            using var cancellationRegistration = cancellationToken.Register(
                () => CancellationObserved.TrySetResult());
            ValidationStarted.TrySetResult();
            release.Wait();
            cancellationToken.ThrowIfCancellationRequested();
        }

        public void BeforeStore(
            long workspaceVersion,
            CancellationToken cancellationToken)
        {
        }

        public void ReleaseValidation()
            => release.Set();
    }

    private sealed class BlockingThrowingCancellationCallbackProjectValidationBuildObserver
        : IVbaProjectSnapshotBuildObserver
    {
        private readonly ManualResetEventSlim releaseCancellationCallback = new();
        private int startCount;

        public TaskCompletionSource FirstValidationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationCallbackStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int StartCount => Volatile.Read(ref startCount);

        public void BeforeBuildProjectValidation(
            string activeUri,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref startCount) != 1)
            {
                return;
            }

            using var registration = cancellationToken.Register(() =>
            {
                CancellationCallbackStarted.TrySetResult();
                releaseCancellationCallback.Wait();
                throw new InvalidOperationException(
                    "Injected cancellation-callback failure.");
            });
            FirstValidationStarted.TrySetResult();
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    Thread.Yield();
                }

                CancellationObserved.TrySetResult();
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                throw;
            }
        }

        public void BeforeStore(
            long workspaceVersion,
            CancellationToken cancellationToken)
        {
        }

        public void ReleaseCancellationCallback()
            => releaseCancellationCallback.Set();
    }

    private sealed class ThrowingFirstProjectValidationBuildObserver
        : IVbaProjectSnapshotBuildObserver
    {
        private int startCount;

        public int StartCount => Volatile.Read(ref startCount);

        public void BeforeBuildProjectValidation(
            string activeUri,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref startCount) == 1)
            {
                throw new InvalidOperationException(
                    "Injected project-validation failure.");
            }
        }

        public void BeforeStore(
            long workspaceVersion,
            CancellationToken cancellationToken)
        {
        }
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
