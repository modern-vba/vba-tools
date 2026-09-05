using System.Text;
using VbaDebugAdapter.Build;
using VbaDebugAdapter.Cli;
using VbaDebugAdapter.Debugging;
using VbaDebugAdapter.Infrastructure;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class StandaloneVbaDebugLaunchServiceTests
{
    [Fact]
    public void LaunchServiceExposesPreparationWithoutRawLaunchBypasses()
    {
        var methodNames = typeof(IStandaloneVbaDebugLaunchService)
            .GetMethods()
            .Select(method => method.Name)
            .ToArray();

        Assert.Contains("PrepareAsync", methodNames);
        Assert.DoesNotContain("ValidateForLaunch", methodNames);
        Assert.DoesNotContain("LaunchAsync", methodNames);
    }

    [Fact]
    public async Task PreparationFreezesExactLaunchEvidenceBeforeStartingVisibleExcel()
    {
        await using var fixture = await LeaseIssuedGenerationFixture.CreateAsync();
        var events = new List<string>();
        var visibleSession = new RecordingVbeDebugSession(events);
        var service = new StandaloneVbaDebugLaunchService(
            new TransportedDebugSourceSnapshotValidator(932),
            new RecordingWorkbookBuilder(
                events,
                new VbaDevSnapshotBuildResult(fixture.GenerationWorkspace)),
            new RecordingVbeDebugSessionFactory(events, visibleSession));
        const string sourceUri = "file:///C:/persistent/Module1.bas";
        var sources = new List<TransportedDebugSource>
        {
            new(
                "Module1.bas",
                sourceUri,
                "utf8bom",
                Convert.ToBase64String(DebugSnapshotTestEncoding.Utf8BomBytes(
                    "Attribute VB_Name = \"Module1\"\r\n" +
                    "Public Sub Run()\r\n" +
                    "    Debug.Print \"frozen\"\r\n" +
                    "End Sub\r\n")))
        };
        var breakpoints = new List<TransportedDebugSourceBreakpoint>
        {
            new(sourceUri, 2)
        };
        var request = new StandaloneVbaDebugLaunchRequest(
            Path.GetFullPath("project"),
            "Book1",
            "Book1.xlsm",
            "Module1",
            "Run",
            new TransportedDebugSourceSnapshot(2, sources)
            {
                ActiveSource = new TransportedDebugSourcePosition(sourceUri, 2, 4),
                Breakpoints = breakpoints
            });

        await using var plan = await service.PrepareAsync(
            Path.GetFullPath("vba-dev.exe"),
            fixture.WorkspaceLease,
            request,
            restartBinding: null,
            CancellationToken.None);

        Assert.Equal(["build:Book1.xlsm"], events);
        Assert.Empty(plan.GetType().GetConstructors());
        sources.Clear();
        breakpoints.Clear();
        Assert.Single(plan.Snapshot.SourceInventory.Sources);
        Assert.Single(plan.Snapshot.SourceInventory.Breakpoints);
        Assert.Equal(new DebugSourcePosition(sourceUri, 2, 4), plan.Snapshot.ActiveSource);
        Assert.Equal(new DebugTargetProcedure("Module1", "Run"), plan.Snapshot.Target);
        Assert.Equal(DebugGenerationId.Initial, plan.Snapshot.GenerationId);
        Assert.Equal(
            Path.GetFullPath("project"),
            plan.Snapshot.LaunchSettings.CanonicalProjectRoot);
        Assert.Equal("Module1", plan.Snapshot.LaunchSettings.RequestedModuleName);
        Assert.Equal("Run", plan.Snapshot.LaunchSettings.RequestedProcedureName);
        Assert.Equal(
            fixture.GenerationWorkspace.GenerationWorkspacePath,
            plan.Snapshot.GenerationWorkspacePath);

        var runningSession = await plan.CommitAsync(
            restartBinding: null,
            CancellationToken.None);

        Assert.Single(visibleSession.Breakpoints);
        Assert.Equal(
            ["build:Book1.xlsm", "start-visible", "open:Book1.xlsm", "set-breakpoints", "run:Module1.Run"],
            events);
        await runningSession.DisposeAsync();
    }

    [Fact]
    public async Task RestartPreparationBuildsBeforeReleasingItsExactBoundSession()
    {
        var generation = DebugGenerationId.FromValue(1);
        await using var fixture = await LeaseIssuedGenerationFixture.CreateAsync(generation);
        var events = new List<string>();
        var oldSession = new RecordingRunningSession(events, "old");
        var visibleSession = new RecordingVbeDebugSession(events);
        var service = new StandaloneVbaDebugLaunchService(
            new TransportedDebugSourceSnapshotValidator(932),
            new RecordingWorkbookBuilder(
                events,
                new VbaDevSnapshotBuildResult(fixture.GenerationWorkspace)),
            new RecordingVbeDebugSessionFactory(events, visibleSession));
        var preparationId = DebugRestartPreparationId.Parse(
            "fedcba9876543210fedcba9876543210");
        var restartGeneration = DebugRestartGeneration.FromValue(1);
        var request = CreateLaunchRequest() with
        {
            RestartPreparation = new RestartPreparationDescriptor(
                preparationId,
                restartGeneration)
        };
        var binding = CreateRestartBinding(
            oldSession,
            preparationId,
            restartGeneration);

        await using var plan = await service.PrepareAsync(
            Path.GetFullPath("vba-dev.exe"),
            fixture.WorkspaceLease,
            request,
            binding,
            CancellationToken.None);

        Assert.Equal(["build:Book1.xlsm"], events);
        Assert.Equal(0, oldSession.TerminateCalls);
        Assert.Equal(0, oldSession.DisposeCalls);
        Assert.Same(binding, plan.Snapshot.RestartBinding);
        Assert.Equal(generation, plan.Snapshot.GenerationId);

        var runningSession = await plan.CommitAsync(binding, CancellationToken.None);

        Assert.Equal(
            ["build:Book1.xlsm", "old:terminate", "old:dispose", "start-visible", "open:Book1.xlsm", "run:Module1.Run"],
            events);
        Assert.True(plan.RestartSessionReleased);
        await runningSession.DisposeAsync();
    }

    [Fact]
    public async Task RestartCommitAwaitsAsynchronousBoundSessionTeardownBeforeStartingVisibleExcel()
    {
        var generation = DebugGenerationId.FromValue(1);
        await using var fixture = await LeaseIssuedGenerationFixture.CreateAsync(generation);
        var events = new List<string>();
        var oldSession = new GatedRunningSession(events, "old");
        var visibleSession = new RecordingVbeDebugSession(events);
        var service = new StandaloneVbaDebugLaunchService(
            new TransportedDebugSourceSnapshotValidator(932),
            new RecordingWorkbookBuilder(
                events,
                new VbaDevSnapshotBuildResult(fixture.GenerationWorkspace)),
            new RecordingVbeDebugSessionFactory(events, visibleSession));
        var preparationId = DebugRestartPreparationId.Parse(
            "fedcba9876543210fedcba9876543210");
        var restartGeneration = DebugRestartGeneration.FromValue(1);
        var request = CreateLaunchRequest() with
        {
            RestartPreparation = new RestartPreparationDescriptor(
                preparationId,
                restartGeneration)
        };
        var binding = CreateRestartBinding(
            oldSession,
            preparationId,
            restartGeneration);
        await using var plan = await service.PrepareAsync(
            Path.GetFullPath("vba-dev.exe"),
            fixture.WorkspaceLease,
            request,
            binding,
            CancellationToken.None);
        var commit = plan.CommitAsync(binding, CancellationToken.None);
        IStandaloneVbaDebugRunningSession? runningSession = null;

        try
        {
            await oldSession.TerminateStarted.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.DoesNotContain("start-visible", events);
            Assert.False(commit.IsCompleted);

            oldSession.ReleaseTerminate();
            await oldSession.DisposeStarted.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.DoesNotContain("start-visible", events);
            Assert.False(commit.IsCompleted);

            oldSession.ReleaseDispose();
            runningSession = await commit.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(
                [
                    "build:Book1.xlsm",
                    "old:terminate-start",
                    "old:terminate-complete",
                    "old:dispose-start",
                    "old:dispose-complete",
                    "start-visible",
                    "open:Book1.xlsm",
                    "run:Module1.Run"
                ],
                events);
        }
        finally
        {
            oldSession.ReleaseTerminate();
            oldSession.ReleaseDispose();
            if (runningSession is null)
            {
                runningSession = await commit;
            }
            await runningSession.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("session")]
    [InlineData("bound-session")]
    [InlineData("project")]
    [InlineData("document")]
    [InlineData("workbook")]
    [InlineData("module")]
    [InlineData("procedure")]
    [InlineData("preparation")]
    [InlineData("generation")]
    [InlineData("dap-request")]
    public async Task PreparedRestartRejectsEveryStaleCommitIdentityWithoutLaunching(
        string staleIdentity)
    {
        var generation = DebugGenerationId.FromValue(1);
        await using var fixture = await LeaseIssuedGenerationFixture.CreateAsync(generation);
        var events = new List<string>();
        var oldSession = new RecordingRunningSession(events, "old");
        var preparationId = DebugRestartPreparationId.Parse(
            "fedcba9876543210fedcba9876543210");
        var restartGeneration = DebugRestartGeneration.FromValue(1);
        var request = CreateLaunchRequest() with
        {
            RestartPreparation = new RestartPreparationDescriptor(
                preparationId,
                restartGeneration)
        };
        var binding = CreateRestartBinding(
            oldSession,
            preparationId,
            restartGeneration);
        var service = new StandaloneVbaDebugLaunchService(
            new TransportedDebugSourceSnapshotValidator(932),
            new RecordingWorkbookBuilder(
                events,
                new VbaDevSnapshotBuildResult(fixture.GenerationWorkspace)),
            new RecordingVbeDebugSessionFactory(
                events,
                new RecordingVbeDebugSession(events)));
        await using var plan = await service.PrepareAsync(
            Path.GetFullPath("vba-dev.exe"),
            fixture.WorkspaceLease,
            request,
            binding,
            CancellationToken.None);
        var staleBinding = staleIdentity switch
        {
            "session" => binding with
            {
                SessionId = DebugSessionId.Parse("11111111111111111111111111111111")
            },
            "bound-session" => binding with
            {
                BoundSession = new RecordingRunningSession([], "other")
            },
            "project" => binding with
            {
                CanonicalProjectRoot = Path.GetFullPath("other-project")
            },
            "document" => binding with { DocumentName = "OtherBook" },
            "workbook" => binding with { WorkbookFileName = "OtherBook.xlsm" },
            "module" => binding with { TargetModuleName = "OtherModule" },
            "procedure" => binding with { TargetProcedureName = "OtherProcedure" },
            "preparation" => binding with
            {
                PreparationId = DebugRestartPreparationId.Parse(
                    "11111111111111111111111111111111")
            },
            "generation" => binding with
            {
                Generation = DebugRestartGeneration.FromValue(2)
            },
            "dap-request" => binding with { DapRequestSequence = 4 },
            _ => throw new ArgumentOutOfRangeException(nameof(staleIdentity))
        };

        var error = await Assert.ThrowsAsync<DebugSetupException>(() =>
            plan.CommitAsync(staleBinding, CancellationToken.None));

        Assert.Contains("stale", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["build:Book1.xlsm"], events);
        Assert.Equal(0, oldSession.TerminateCalls);
        Assert.Equal(0, oldSession.DisposeCalls);
        Assert.False(plan.RestartSessionReleased);
        Assert.False(Directory.Exists(fixture.GenerationWorkspace.GenerationWorkspacePath));
    }

    [Theory]
    [InlineData("removed-target")]
    [InlineData("unmappable-breakpoint")]
    public async Task PreparationFailsBeforeBuildForIndeterminateTargetOrBreakpoint(
        string invalidEvidence)
    {
        await using var fixture = await LeaseIssuedGenerationFixture.CreateAsync();
        var events = new List<string>();
        const string sourceUri = "file:///C:/persistent/Module1.bas";
        var content = invalidEvidence == "removed-target"
            ? "Attribute VB_Name = \"Module1\"\r\nPublic Sub Other()\r\nEnd Sub\r\n"
            : "Attribute VB_Name = \"Module1\"\r\nPublic Sub Run()\r\n\r\nEnd Sub\r\n";
        var snapshot = new TransportedDebugSourceSnapshot(
            2,
            [
                new TransportedDebugSource(
                    "Module1.bas",
                    sourceUri,
                    "utf8bom",
                    Convert.ToBase64String(DebugSnapshotTestEncoding.Utf8BomBytes(content)))
            ])
        {
            Breakpoints = invalidEvidence == "unmappable-breakpoint"
                ? [new TransportedDebugSourceBreakpoint(sourceUri, 2)]
                : []
        };
        var service = new StandaloneVbaDebugLaunchService(
            new TransportedDebugSourceSnapshotValidator(932),
            new RecordingWorkbookBuilder(
                events,
                new VbaDevSnapshotBuildResult(fixture.GenerationWorkspace)),
            new RecordingVbeDebugSessionFactory(
                events,
                new RecordingVbeDebugSession(events)));
        var request = new StandaloneVbaDebugLaunchRequest(
            Path.GetFullPath("project"),
            "Book1",
            "Book1.xlsm",
            "Module1",
            "Run",
            snapshot);
        DebugRestartLaunchBinding? restartBinding = null;
        RecordingRunningSession? oldSession = null;
        if (invalidEvidence == "removed-target")
        {
            oldSession = new RecordingRunningSession(events, "old");
            var preparationId = DebugRestartPreparationId.Parse(
                "fedcba9876543210fedcba9876543210");
            var generation = DebugRestartGeneration.FromValue(1);
            request = request with
            {
                RestartPreparation = new RestartPreparationDescriptor(
                    preparationId,
                    generation)
            };
            restartBinding = CreateRestartBinding(
                oldSession,
                preparationId,
                generation);
        }

        await Assert.ThrowsAsync<DebugSetupException>(() => service.PrepareAsync(
            Path.GetFullPath("vba-dev.exe"),
            fixture.WorkspaceLease,
            request,
            restartBinding,
            CancellationToken.None));

        Assert.Empty(events);
        Assert.Equal(0, oldSession?.TerminateCalls ?? 0);
    }

    [Fact]
    public async Task PreparationRejectsAMismatchedOwnedGenerationWithoutReleasingRestart()
    {
        await using var fixture = await LeaseIssuedGenerationFixture.CreateAsync();
        var events = new List<string>();
        var oldSession = new RecordingRunningSession(events, "old");
        var preparationId = DebugRestartPreparationId.Parse(
            "fedcba9876543210fedcba9876543210");
        var restartGeneration = DebugRestartGeneration.FromValue(1);
        var request = CreateLaunchRequest() with
        {
            RestartPreparation = new RestartPreparationDescriptor(
                preparationId,
                restartGeneration)
        };
        var service = new StandaloneVbaDebugLaunchService(
            new TransportedDebugSourceSnapshotValidator(932),
            new RecordingWorkbookBuilder(
                events,
                new VbaDevSnapshotBuildResult(fixture.GenerationWorkspace)),
            new RecordingVbeDebugSessionFactory(
                events,
                new RecordingVbeDebugSession(events)));

        var error = await Assert.ThrowsAsync<DebugSetupException>(() =>
            service.PrepareAsync(
                Path.GetFullPath("vba-dev.exe"),
                fixture.WorkspaceLease,
                request,
                CreateRestartBinding(
                    oldSession,
                    preparationId,
                    restartGeneration),
                CancellationToken.None));

        Assert.Contains("generation", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["build:Book1.xlsm"], events);
        Assert.Equal(0, oldSession.TerminateCalls);
        Assert.False(Directory.Exists(fixture.GenerationWorkspace.GenerationWorkspacePath));
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("dispose")]
    public async Task CancellationOrDisposalBeforeCommitReleasesTheOwnedGeneration(
        string action)
    {
        await using var fixture = await LeaseIssuedGenerationFixture.CreateAsync();
        var events = new List<string>();
        var service = new StandaloneVbaDebugLaunchService(
            new TransportedDebugSourceSnapshotValidator(932),
            new RecordingWorkbookBuilder(
                events,
                new VbaDevSnapshotBuildResult(fixture.GenerationWorkspace)),
            new RecordingVbeDebugSessionFactory(
                events,
                new RecordingVbeDebugSession(events)));
        var plan = await service.PrepareAsync(
            Path.GetFullPath("vba-dev.exe"),
            fixture.WorkspaceLease,
            CreateLaunchRequest(),
            restartBinding: null,
            CancellationToken.None);

        if (action == "cancel")
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                plan.CommitAsync(restartBinding: null, cancellation.Token));
        }
        else
        {
            await plan.DisposeAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            plan.CommitAsync(restartBinding: null, CancellationToken.None));
        await plan.DisposeAsync();
        Assert.Equal(["build:Book1.xlsm"], events);
        Assert.False(Directory.Exists(fixture.GenerationWorkspace.GenerationWorkspacePath));
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("dispose")]
    public async Task CancellationOrDisposalBeforeRestartCommitRetainsTheBoundSessionAndCleansTheGeneration(
        string action)
    {
        var generation = DebugGenerationId.FromValue(1);
        await using var fixture = await LeaseIssuedGenerationFixture.CreateAsync(generation);
        var events = new List<string>();
        var oldSession = new RecordingRunningSession(events, "old");
        var preparationId = DebugRestartPreparationId.Parse(
            "fedcba9876543210fedcba9876543210");
        var restartGeneration = DebugRestartGeneration.FromValue(1);
        var request = CreateLaunchRequest() with
        {
            RestartPreparation = new RestartPreparationDescriptor(
                preparationId,
                restartGeneration)
        };
        var binding = CreateRestartBinding(
            oldSession,
            preparationId,
            restartGeneration);
        var service = new StandaloneVbaDebugLaunchService(
            new TransportedDebugSourceSnapshotValidator(932),
            new RecordingWorkbookBuilder(
                events,
                new VbaDevSnapshotBuildResult(fixture.GenerationWorkspace)),
            new RecordingVbeDebugSessionFactory(
                events,
                new RecordingVbeDebugSession(events)));
        var plan = await service.PrepareAsync(
            Path.GetFullPath("vba-dev.exe"),
            fixture.WorkspaceLease,
            request,
            binding,
            CancellationToken.None);

        if (action == "cancel")
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                plan.CommitAsync(binding, cancellation.Token));
        }
        else
        {
            await plan.DisposeAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            plan.CommitAsync(binding, CancellationToken.None));
        await plan.DisposeAsync();
        Assert.Equal(["build:Book1.xlsm"], events);
        Assert.Equal(0, oldSession.TerminateCalls);
        Assert.Equal(0, oldSession.DisposeCalls);
        Assert.False(plan.RestartSessionReleased);
        Assert.False(Directory.Exists(fixture.GenerationWorkspace.GenerationWorkspacePath));
    }

    [Fact]
    public async Task PreparedPlanAllowsOnlyOneSequentialCommit()
    {
        await using var fixture = await LeaseIssuedGenerationFixture.CreateAsync();
        var events = new List<string>();
        var service = new StandaloneVbaDebugLaunchService(
            new TransportedDebugSourceSnapshotValidator(932),
            new RecordingWorkbookBuilder(
                events,
                new VbaDevSnapshotBuildResult(fixture.GenerationWorkspace)),
            new RecordingVbeDebugSessionFactory(
                events,
                new RecordingVbeDebugSession(events)));
        await using var plan = await service.PrepareAsync(
            Path.GetFullPath("vba-dev.exe"),
            fixture.WorkspaceLease,
            CreateLaunchRequest(),
            restartBinding: null,
            CancellationToken.None);

        var runningSession = await plan.CommitAsync(
            restartBinding: null,
            CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            plan.CommitAsync(restartBinding: null, CancellationToken.None));

        Assert.Equal(1, events.Count(item => item == "start-visible"));
        await runningSession.DisposeAsync();
    }

    [Fact]
    public async Task PreparedPlanAllowsOnlyOneConcurrentCommit()
    {
        await using var fixture = await LeaseIssuedGenerationFixture.CreateAsync();
        var events = new List<string>();
        var factory = new GatedVbeDebugSessionFactory(
            events,
            new RecordingVbeDebugSession(events));
        var service = new StandaloneVbaDebugLaunchService(
            new TransportedDebugSourceSnapshotValidator(932),
            new RecordingWorkbookBuilder(
                events,
                new VbaDevSnapshotBuildResult(fixture.GenerationWorkspace)),
            factory);
        await using var plan = await service.PrepareAsync(
            Path.GetFullPath("vba-dev.exe"),
            fixture.WorkspaceLease,
            CreateLaunchRequest(),
            restartBinding: null,
            CancellationToken.None);
        var winningCommit = plan.CommitAsync(
            restartBinding: null,
            CancellationToken.None);
        await factory.Started.WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            plan.CommitAsync(restartBinding: null, CancellationToken.None));
        factory.Release();
        var runningSession = await winningCommit;

        Assert.Equal(1, events.Count(item => item == "start-visible"));
        await runningSession.DisposeAsync();
    }

    [Fact]
    public async Task BuildFailureProducesNoPlanAndStartsNoVisibleExcel()
    {
        await using var fixture = await LeaseIssuedGenerationFixture.CreateAsync();
        var events = new List<string>();
        var service = new StandaloneVbaDebugLaunchService(
            new TransportedDebugSourceSnapshotValidator(932),
            new ThrowingWorkbookBuilder(events),
            new RecordingVbeDebugSessionFactory(
                events,
                new RecordingVbeDebugSession(events)));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PrepareAsync(
                Path.GetFullPath("vba-dev.exe"),
                fixture.WorkspaceLease,
                CreateLaunchRequest(),
                restartBinding: null,
                CancellationToken.None));

        Assert.Equal("Synthetic debug build failure.", error.Message);
        Assert.Equal(["build-failed"], events);
    }

    [Fact]
    public async Task RestartBuildFailureRetainsItsBoundSessionAndCleansTheGeneration()
    {
        using var temp = TempDirectory.Create();
        const string sessionId = "0123456789abcdef0123456789abcdef";
        var workspaceRoot = Path.Combine(temp.Path, "adapter-root");
        var projectRoot = Path.Combine(temp.Path, "project");
        var vbaDevPath = Path.Combine(temp.Path, "vba-dev.exe");
        Directory.CreateDirectory(projectRoot);
        await File.WriteAllBytesAsync(vbaDevPath, []);
        await using var workspaceLease = await new VbaDebugSessionWorkspaceManager(workspaceRoot)
            .ClaimAsync(DebugSessionId.Parse(sessionId), CancellationToken.None);
        var generation = DebugGenerationId.FromValue(1);
        var generationWorkspacePath = Path.Combine(
            workspaceLease.SessionWorkspacePath,
            "generations",
            generation.WorkspaceDirectoryName);
        var events = new List<string>();
        var oldSession = new RecordingRunningSession(events, "old");
        var preparationId = DebugRestartPreparationId.Parse(
            "fedcba9876543210fedcba9876543210");
        var restartGeneration = DebugRestartGeneration.FromValue(1);
        var request = CreateLaunchRequest() with
        {
            ProjectRoot = projectRoot,
            RestartPreparation = new RestartPreparationDescriptor(
                preparationId,
                restartGeneration)
        };
        var binding = CreateRestartBinding(
            oldSession,
            preparationId,
            restartGeneration) with
        {
            CanonicalProjectRoot = projectRoot
        };
        var service = new StandaloneVbaDebugLaunchService(
            new TransportedDebugSourceSnapshotValidator(932),
            new VbaDevSnapshotWorkbookBuilder(
                new FailingVbaDevBuildProcess(events),
                new TransportedDebugSourceSnapshotValidator(932)),
            new RecordingVbeDebugSessionFactory(
                events,
                new RecordingVbeDebugSession(events)));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PrepareAsync(
                vbaDevPath,
                workspaceLease,
                request,
                binding,
                CancellationToken.None));

        Assert.Contains("snapshot build exited", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["build-failed"], events);
        Assert.Equal(0, oldSession.TerminateCalls);
        Assert.Equal(0, oldSession.DisposeCalls);
        Assert.False(Directory.Exists(generationWorkspacePath));
    }

    [Theory]
    [InlineData("start")]
    [InlineData("open")]
    public async Task CommitFailureCleansTheGenerationAndRejectsRetry(string failure)
    {
        await using var fixture = await LeaseIssuedGenerationFixture.CreateAsync();
        var events = new List<string>();
        IVbeDebugSessionFactory factory = failure == "start"
            ? new ThrowingVbeDebugSessionFactory(events)
            : new RecordingVbeDebugSessionFactory(
                events,
                new RecordingVbeDebugSession(
                    events,
                    new InvalidOperationException("Synthetic workbook-open failure.")));
        var service = new StandaloneVbaDebugLaunchService(
            new TransportedDebugSourceSnapshotValidator(932),
            new RecordingWorkbookBuilder(
                events,
                new VbaDevSnapshotBuildResult(fixture.GenerationWorkspace)),
            factory);
        await using var plan = await service.PrepareAsync(
            Path.GetFullPath("vba-dev.exe"),
            fixture.WorkspaceLease,
            CreateLaunchRequest(),
            restartBinding: null,
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            plan.CommitAsync(restartBinding: null, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            plan.CommitAsync(restartBinding: null, CancellationToken.None));

        Assert.Equal(1, events.Count(item => item == "start-visible"));
        Assert.False(Directory.Exists(fixture.GenerationWorkspace.GenerationWorkspacePath));
        if (failure == "open")
        {
            Assert.Contains("terminate", events);
            Assert.Contains("dispose", events);
        }
    }

    [Fact]
    public async Task RestartReplacementStartFailureReleasesTheOldSessionOnceAndCleansTheNewGeneration()
    {
        var generation = DebugGenerationId.FromValue(1);
        await using var fixture = await LeaseIssuedGenerationFixture.CreateAsync(generation);
        var events = new List<string>();
        var oldSession = new RecordingRunningSession(events, "old");
        var preparationId = DebugRestartPreparationId.Parse(
            "fedcba9876543210fedcba9876543210");
        var restartGeneration = DebugRestartGeneration.FromValue(1);
        var request = CreateLaunchRequest() with
        {
            RestartPreparation = new RestartPreparationDescriptor(
                preparationId,
                restartGeneration)
        };
        var binding = CreateRestartBinding(
            oldSession,
            preparationId,
            restartGeneration);
        var service = new StandaloneVbaDebugLaunchService(
            new TransportedDebugSourceSnapshotValidator(932),
            new RecordingWorkbookBuilder(
                events,
                new VbaDevSnapshotBuildResult(fixture.GenerationWorkspace)),
            new ThrowingVbeDebugSessionFactory(events));
        await using var plan = await service.PrepareAsync(
            Path.GetFullPath("vba-dev.exe"),
            fixture.WorkspaceLease,
            request,
            binding,
            CancellationToken.None);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            plan.CommitAsync(binding, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            plan.CommitAsync(binding, CancellationToken.None));

        Assert.Equal("Synthetic visible-session start failure.", error.Message);
        Assert.Equal(
            ["build:Book1.xlsm", "old:terminate", "old:dispose", "start-visible"],
            events);
        Assert.Equal(1, oldSession.TerminateCalls);
        Assert.Equal(1, oldSession.DisposeCalls);
        Assert.True(plan.RestartSessionReleased);
        Assert.False(Directory.Exists(fixture.GenerationWorkspace.GenerationWorkspacePath));
    }

    [Fact]
    public async Task LaunchBuildsThenOpensAndRunsTheTemporarySameNameWorkbook()
    {
        var fixture = await LeaseIssuedGenerationFixture.CreateAsync();
        var workbookPath = fixture.GenerationWorkspace.WorkbookPath;
        var events = new List<string>();
        var generationCapability = fixture.GenerationWorkspace;
        var builder = new RecordingWorkbookBuilder(
            events,
            new VbaDevSnapshotBuildResult(generationCapability)
            {
                Output =
                [
                    "WARN Protected reference remains.",
                    "[WARN] vbeIdentifierRecased: Imported component 'Module1' identifier casing (source -> VBE): 'FileName' -> 'Filename'."
                ]
            });
        var visibleSession = new RecordingVbeDebugSession(events);
        var lifecycleSink = new RecordingDebugLifecycleSink();
        var service = new StandaloneVbaDebugLaunchService(
            new TransportedDebugSourceSnapshotValidator(932),
            builder,
            new RecordingVbeDebugSessionFactory(events, visibleSession));
        var sourceBytes = DebugSnapshotTestEncoding.Utf8BomBytes(
            "Attribute VB_Name = \"Module1\"\r\nPublic Sub Run()\r\nEnd Sub\r\n");

        try
        {
            var runningSession = await PrepareAndCommitAsync(
                service,
                Path.GetFullPath("vba-dev.exe"),
                fixture.WorkspaceLease,
                new StandaloneVbaDebugLaunchRequest(
                    Path.GetFullPath("project"),
                    "Book1",
                    "Book1.xlsm",
                    "Module1",
                    "Run",
                    new TransportedDebugSourceSnapshot(
                        2,
                        [
                            new TransportedDebugSource(
                                "Module1.bas",
                                "file:///C:/persistent/Module1.bas",
                                "utf8bom",
                                Convert.ToBase64String(sourceBytes))
                        ])),
                CancellationToken.None,
                lifecycleSink);

            Assert.Equal(
                ["build:Book1.xlsm", "start-visible", "open:Book1.xlsm", "run:Module1.Run"],
                events);
            Assert.Equal(workbookPath, visibleSession.OpenedWorkbookPath);
            Assert.Same(lifecycleSink, visibleSession.WorkbookOpenInputWaitSink);
            Assert.Equal(
                [
                    new DebugLifecycleMessage("WARN Protected reference remains."),
                    new DebugLifecycleMessage(
                        "[WARN] vbeIdentifierRecased: Imported component 'Module1' identifier casing (source -> VBE): 'FileName' -> 'Filename'.")
                ],
                lifecycleSink.Messages);
            Assert.Equal(new DebugTargetProcedure("Module1", "Run"), visibleSession.Target);
            await runningSession.TerminateAsync();
            await runningSession.DisposeAsync();
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task LaunchRetainsGenerationOwnershipUntilTheRunningSessionIsDisposed()
    {
        var fixture = await LeaseIssuedGenerationFixture.CreateAsync();
        var generationCapability = fixture.GenerationWorkspace;
        var generationWorkspacePath = generationCapability.GenerationWorkspacePath;
        var buildResult = new VbaDevSnapshotBuildResult(generationCapability);
        var events = new List<string>();
        var visibleSession = new RecordingVbeDebugSession(events);
        var service = new StandaloneVbaDebugLaunchService(
            new TransportedDebugSourceSnapshotValidator(932),
            new RecordingWorkbookBuilder(events, buildResult),
            new RecordingVbeDebugSessionFactory(
                events,
                visibleSession));

        try
        {
            var runningSession = await PrepareAndCommitAsync(
                service,
                Path.GetFullPath("vba-dev.exe"),
                fixture.WorkspaceLease,
                new StandaloneVbaDebugLaunchRequest(
                    Path.GetFullPath("project"),
                    "Book1",
                    "Book1.xlsm",
                    "Module1",
                    "Run",
                    new TransportedDebugSourceSnapshot(
                        2,
                        [
                            new TransportedDebugSource(
                                "Module1.bas",
                                "file:///C:/persistent/Module1.bas",
                                "utf8bom",
                                Convert.ToBase64String(DebugSnapshotTestEncoding.Utf8BomBytes(
                                    "Attribute VB_Name = \"Module1\"\r\n" +
                                    "Public Sub Run()\r\nEnd Sub\r\n")))
                        ])),
                CancellationToken.None);

            Assert.Same(generationCapability, visibleSession.AdoptedGenerationWorkspace);
            Assert.True(Directory.Exists(generationWorkspacePath));

            await runningSession.DisposeAsync();

            Assert.False(Directory.Exists(generationWorkspacePath));

            await runningSession.DisposeAsync();

            Assert.False(Directory.Exists(generationWorkspacePath));

            await buildResult.DisposeAsync();

            Assert.False(Directory.Exists(generationWorkspacePath));
        }
        finally
        {
            await buildResult.DisposeAsync();
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task LaunchAcceptsTheClientRawUtf16OrdinalPathOrder()
    {
        var fixture = await LeaseIssuedGenerationFixture.CreateAsync();
        var events = new List<string>();
        var visibleSession = new RecordingVbeDebugSession(events);
        var generationCapability = fixture.GenerationWorkspace;
        var service = new StandaloneVbaDebugLaunchService(
            new TransportedDebugSourceSnapshotValidator(932),
            new RecordingWorkbookBuilder(
                events,
                new VbaDevSnapshotBuildResult(generationCapability)),
            new RecordingVbeDebugSessionFactory(events, visibleSession));
        var snapshot = new TransportedDebugSourceSnapshot(
            2,
            [
                Source("B.bas", "ModuleB", "Other"),
                Source("a.bas", "ModuleA", "Run"),
                Source("j.bas", "ModuleJ", "OtherJ"),
                Source("İ.bas", "ModuleDottedI", "OtherDottedI")
            ]);

        try
        {
            var runningSession = await PrepareAndCommitAsync(
                service,
                Path.GetFullPath("vba-dev.exe"),
                fixture.WorkspaceLease,
                new StandaloneVbaDebugLaunchRequest(
                    Path.GetFullPath("project"),
                    "Book1",
                    "Book1.xlsm",
                    "ModuleA",
                    "Run",
                    snapshot),
                CancellationToken.None);

            Assert.Equal(new DebugTargetProcedure("ModuleA", "Run"), visibleSession.Target);
            await runningSession.TerminateAsync();
            await runningSession.DisposeAsync();
        }
        finally
        {
            await fixture.DisposeAsync();
        }

        static TransportedDebugSource Source(
            string relativePath,
            string moduleName,
            string procedureName)
            => new(
                relativePath,
                $"file:///C:/persistent/{relativePath}",
                "utf8bom",
                Convert.ToBase64String(DebugSnapshotTestEncoding.Utf8BomBytes(
                    $"Attribute VB_Name = \"{moduleName}\"\r\n" +
                    $"Public Sub {procedureName}()\r\nEnd Sub\r\n")));
    }

    [Fact]
    public async Task LaunchRejectsAMismatchedPersistentSourceIdentityBeforeBuild()
    {
        await using var fixture = await LeaseIssuedGenerationFixture.CreateAsync();
        var events = new List<string>();
        var generationCapability = fixture.GenerationWorkspace;
        var buildResult = new VbaDevSnapshotBuildResult(generationCapability);
        var service = new StandaloneVbaDebugLaunchService(
            new TransportedDebugSourceSnapshotValidator(932),
            new RecordingWorkbookBuilder(
                events,
                buildResult),
            new RecordingVbeDebugSessionFactory(
                events,
                new RecordingVbeDebugSession(events)));
        var snapshot = new TransportedDebugSourceSnapshot(
            2,
            [
                new TransportedDebugSource(
                    "nested/Module1.bas",
                    "file:///C:/persistent/Elsewhere.bas",
                    "utf8bom",
                    Convert.ToBase64String(DebugSnapshotTestEncoding.Utf8BomBytes(
                        "Attribute VB_Name = \"Module1\"\r\n" +
                        "Public Sub Run()\r\nEnd Sub\r\n")))
            ]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PrepareAndCommitAsync(
                service,
                Path.GetFullPath("vba-dev.exe"),
                fixture.WorkspaceLease,
                new StandaloneVbaDebugLaunchRequest(
                    Path.GetFullPath("project"),
                    "Book1",
                    "Book1.xlsm",
                    "Module1",
                    "Run",
                    snapshot),
                CancellationToken.None));

        Assert.Contains("sourceUri", exception.Message, StringComparison.Ordinal);
        Assert.Empty(events);
        await buildResult.DisposeAsync();
    }

    [Fact]
    public async Task LaunchResolvesTheTargetFromTheTransportedPersistentSourcePosition()
    {
        var fixture = await LeaseIssuedGenerationFixture.CreateAsync();
        var events = new List<string>();
        var visibleSession = new RecordingVbeDebugSession(events);
        var generationCapability = fixture.GenerationWorkspace;
        var service = new StandaloneVbaDebugLaunchService(
            new TransportedDebugSourceSnapshotValidator(932),
            new RecordingWorkbookBuilder(
                events,
                new VbaDevSnapshotBuildResult(generationCapability)),
            new RecordingVbeDebugSessionFactory(events, visibleSession));
        const string sourceUri = "file:///C:/persistent/Module1.bas";
        var sourceBytes = DebugSnapshotTestEncoding.Utf8BomBytes(
            "Attribute VB_Name = \"Module1\"\r\nPublic Sub Run()\r\n    Debug.Print \"run\"\r\nEnd Sub\r\n");
        var transportedSnapshot = new TransportedDebugSourceSnapshot(
            2,
            [
                new TransportedDebugSource(
                    "Module1.bas",
                    sourceUri,
                    "utf8bom",
                    Convert.ToBase64String(sourceBytes))
            ])
        {
            ActiveSource = new TransportedDebugSourcePosition(sourceUri, 2, 4)
        };

        try
        {
            var runningSession = await PrepareAndCommitAsync(
                service,
                Path.GetFullPath("vba-dev.exe"),
                fixture.WorkspaceLease,
                new StandaloneVbaDebugLaunchRequest(
                    Path.GetFullPath("project"),
                    "Book1",
                    "Book1.xlsm",
                    ModuleName: null,
                    ProcedureName: null,
                    transportedSnapshot),
                CancellationToken.None);

            Assert.Equal(new DebugTargetProcedure("Module1", "Run"), visibleSession.Target);
            await runningSession.TerminateAsync();
            await runningSession.DisposeAsync();
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task LaunchTransfersTheExactTransportedBreakpointBeforeRunningTheTarget()
    {
        var fixture = await LeaseIssuedGenerationFixture.CreateAsync();
        var events = new List<string>();
        var visibleSession = new RecordingVbeDebugSession(events);
        var generationCapability = fixture.GenerationWorkspace;
        var service = new StandaloneVbaDebugLaunchService(
            new TransportedDebugSourceSnapshotValidator(932),
            new RecordingWorkbookBuilder(
                events,
                new VbaDevSnapshotBuildResult(generationCapability)),
            new RecordingVbeDebugSessionFactory(events, visibleSession));
        const string sourceUri = "file:///C:/persistent/Module1.bas";
        var transportedSnapshot = new TransportedDebugSourceSnapshot(
            2,
            [
                new TransportedDebugSource(
                    "Module1.bas",
                    sourceUri,
                    "utf8bom",
                    Convert.ToBase64String(DebugSnapshotTestEncoding.Utf8BomBytes(
                        "Attribute VB_Name = \"Module1\"\r\n" +
                        "Public Sub Run()\r\n" +
                        "    Debug.Print \"break\"\r\n" +
                        "End Sub\r\n")))
            ])
        {
            Breakpoints = [new TransportedDebugSourceBreakpoint(sourceUri, 2)]
        };

        try
        {
            var runningSession = await PrepareAndCommitAsync(
                service,
                Path.GetFullPath("vba-dev.exe"),
                fixture.WorkspaceLease,
                new StandaloneVbaDebugLaunchRequest(
                    Path.GetFullPath("project"),
                    "Book1",
                    "Book1.xlsm",
                    "Module1",
                    "Run",
                    transportedSnapshot),
                CancellationToken.None);

            var breakpoint = Assert.Single(visibleSession.Breakpoints);
            Assert.Equal(sourceUri, breakpoint.Source.SourceUri);
            Assert.Equal(2, breakpoint.Source.EditorLine);
            Assert.Equal("Module1", breakpoint.ModuleName);
            Assert.Equal(2, breakpoint.VbideLine);
            Assert.True(events.IndexOf("set-breakpoints") < events.IndexOf("run:Module1.Run"));
            await runningSession.TerminateAsync();
            await runningSession.DisposeAsync();
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task LaunchDeletesTheBuiltGenerationWorkspaceWhenCompilationSettingsReadFails()
    {
        var fixture = await LeaseIssuedGenerationFixture.CreateAsync();
        var generationCapability = fixture.GenerationWorkspace;
        var generationWorkspacePath = generationCapability.GenerationWorkspacePath;
        var service = new StandaloneVbaDebugLaunchService(
            new TransportedDebugSourceSnapshotValidator(932),
            new RecordingWorkbookBuilder(
                [],
                new VbaDevSnapshotBuildResult(generationCapability)),
            new RecordingVbeDebugSessionFactory([], new RecordingVbeDebugSession([])),
            breakpointSourceMapper: null,
            compilationSettingsReader: new ThrowingCompilationSettingsReader(),
            compilationEnvironmentFactory: new DebugCompilationEnvironmentFactory(),
            conditionalCompilationPreflight: new DebugConditionalCompilationPreflight());
        var sourceBytes = DebugSnapshotTestEncoding.Utf8BomBytes(string.Join('\n',
        [
            "Attribute VB_Name = \"Module1\"",
            "#If VBA7 Then",
            "Public Sub Run()",
            "End Sub",
            "#End If"
        ]));

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                PrepareAndCommitAsync(
                    service,
                    Path.GetFullPath("vba-dev.exe"),
                    fixture.WorkspaceLease,
                    new StandaloneVbaDebugLaunchRequest(
                        Path.GetFullPath("project"),
                        "Book1",
                        "Book1.xlsm",
                        "Module1",
                        "Run",
                        new TransportedDebugSourceSnapshot(
                            2,
                            [
                                new TransportedDebugSource(
                                    "Module1.bas",
                                    "file:///C:/persistent/Module1.bas",
                                    "utf8bom",
                                    Convert.ToBase64String(sourceBytes))
                            ])),
                    CancellationToken.None));

            Assert.Equal("Synthetic compilation-settings read failure.", exception.Message);
            Assert.False(Directory.Exists(generationWorkspacePath));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task LaunchRejectsAnInactiveTargetUsingTheBuiltWorkbookAndVisibleHostFacts()
    {
        var fixture = await LeaseIssuedGenerationFixture.CreateAsync();
        var events = new List<string>();
        var visibleSession = new RecordingVbeDebugSession(events);
        var settings = new DebugCompilationSettings(
            VbaProjectSystemKind.Win64,
            1252,
            [],
            new string('A', 64));
        var generationCapability = fixture.GenerationWorkspace;
        var generationWorkspacePath = generationCapability.GenerationWorkspacePath;
        var service = new StandaloneVbaDebugLaunchService(
            new TransportedDebugSourceSnapshotValidator(932),
            new RecordingWorkbookBuilder(
                events,
                new VbaDevSnapshotBuildResult(generationCapability)),
            new RecordingVbeDebugSessionFactory(events, visibleSession),
            breakpointSourceMapper: null,
            compilationSettingsReader: new ConstantCompilationSettingsReader(settings),
            compilationEnvironmentFactory: new DebugCompilationEnvironmentFactory(),
            conditionalCompilationPreflight: new DebugConditionalCompilationPreflight());
        var sourceBytes = DebugSnapshotTestEncoding.Utf8BomBytes(string.Join('\n',
        [
            "Attribute VB_Name = \"Module1\"",
            "#If VBA7 Then",
            "Public Sub ModernTarget()",
            "End Sub",
            "#Else",
            "Public Sub LegacyTarget()",
            "End Sub",
            "#End If"
        ]));

        try
        {
            await using var plan = await service.PrepareAsync(
                Path.GetFullPath("vba-dev.exe"),
                fixture.WorkspaceLease,
                new StandaloneVbaDebugLaunchRequest(
                    Path.GetFullPath("project"),
                    "Book1",
                    "Book1.xlsm",
                    "Module1",
                    "LegacyTarget",
                    new TransportedDebugSourceSnapshot(
                        2,
                        [
                            new TransportedDebugSource(
                                "Module1.bas",
                                "file:///C:/persistent/Module1.bas",
                                "utf8bom",
                                Convert.ToBase64String(sourceBytes))
                        ])),
                restartBinding: null,
                CancellationToken.None);
            Assert.True(plan.Snapshot.RequiresConditionalCompilationPreflight);
            var exception = await Assert.ThrowsAsync<DebugSetupException>(() =>
                plan.CommitAsync(restartBinding: null, CancellationToken.None));

            Assert.Contains("inactive", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("open:Book1.xlsm", events);
            Assert.DoesNotContain("run:Module1.LegacyTarget", events);
            Assert.False(Directory.Exists(generationWorkspacePath));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static StandaloneVbaDebugLaunchRequest CreateLaunchRequest()
    {
        const string sourceUri = "file:///C:/persistent/Module1.bas";
        return new StandaloneVbaDebugLaunchRequest(
            Path.GetFullPath("project"),
            "Book1",
            "Book1.xlsm",
            "Module1",
            "Run",
            new TransportedDebugSourceSnapshot(
                2,
                [
                    new TransportedDebugSource(
                        "Module1.bas",
                        sourceUri,
                        "utf8bom",
                        Convert.ToBase64String(DebugSnapshotTestEncoding.Utf8BomBytes(
                            "Attribute VB_Name = \"Module1\"\r\n" +
                            "Public Sub Run()\r\n" +
                            "End Sub\r\n")))
                ]));
    }

    private static DebugRestartLaunchBinding CreateRestartBinding(
        IStandaloneVbaDebugRunningSession oldSession,
        DebugRestartPreparationId preparationId,
        DebugRestartGeneration generation)
        => new(
            DebugSessionId.Parse("0123456789abcdef0123456789abcdef"),
            oldSession,
            Path.GetFullPath("project"),
            "Book1",
            "Book1.xlsm",
            "Module1",
            "Run",
            "Module1",
            "Run",
            preparationId,
            generation,
            DapRequestSequence: 3);

    private static async Task<IStandaloneVbaDebugRunningSession> PrepareAndCommitAsync(
        IStandaloneVbaDebugLaunchService service,
        string vbaDevPath,
        IVbaDebugSessionWorkspaceLease workspaceLease,
        StandaloneVbaDebugLaunchRequest request,
        CancellationToken cancellationToken,
        IDebugLifecycleSink? lifecycleSink = null)
    {
        await using var plan = await service.PrepareAsync(
            vbaDevPath,
            workspaceLease,
            request,
            restartBinding: null,
            cancellationToken,
            lifecycleSink);
        return await plan.CommitAsync(
            restartBinding: null,
            cancellationToken);
    }

    private sealed class RecordingWorkbookBuilder(
        List<string> events,
        VbaDevSnapshotBuildResult result) : IVbaDebugWorkbookBuilder
    {
        public Task<VbaDevSnapshotBuildResult> BuildAsync(
            string vbaDevPath,
            IVbaDebugSessionWorkspaceLease workspaceLease,
            VbaDevSnapshotBuildRequest request,
            CancellationToken cancellationToken)
        {
            events.Add($"build:{request.WorkbookFileName}");
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingWorkbookBuilder(List<string> events)
        : IVbaDebugWorkbookBuilder
    {
        public Task<VbaDevSnapshotBuildResult> BuildAsync(
            string vbaDevPath,
            IVbaDebugSessionWorkspaceLease workspaceLease,
            VbaDevSnapshotBuildRequest request,
            CancellationToken cancellationToken)
        {
            events.Add("build-failed");
            return Task.FromException<VbaDevSnapshotBuildResult>(
                new InvalidOperationException("Synthetic debug build failure."));
        }
    }

    private sealed class FailingVbaDevBuildProcess(List<string> events)
        : IVbaDevBuildProcess
    {
        public Task<VbaDevBuildProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            events.Add("build-failed");
            return Task.FromResult(new VbaDevBuildProcessResult(
                ExitCode: 23,
                StandardOutput: "Preparing restart generation.",
                StandardError: "Synthetic debug build failure."));
        }
    }

    private sealed class LeaseIssuedGenerationFixture : IAsyncDisposable
    {
        private int disposed;
        private readonly TempDirectory temp;

        private LeaseIssuedGenerationFixture(
            TempDirectory temp,
            IVbaDebugSessionWorkspaceLease workspaceLease,
            IVbaDebugGenerationWorkspace generationWorkspace)
        {
            this.temp = temp;
            WorkspaceLease = workspaceLease;
            GenerationWorkspace = generationWorkspace;
        }

        public IVbaDebugSessionWorkspaceLease WorkspaceLease { get; }

        public IVbaDebugGenerationWorkspace GenerationWorkspace { get; }

        public static async Task<LeaseIssuedGenerationFixture> CreateAsync(
            DebugGenerationId? generationId = null)
        {
            var temp = TempDirectory.Create();
            IVbaDebugSessionWorkspaceLease? workspaceLease = null;
            IVbaDebugGenerationWorkspace? generationWorkspace = null;
            try
            {
                var manager = new VbaDebugSessionWorkspaceManager(temp.Path);
                workspaceLease = await manager.ClaimAsync(
                    DebugSessionId.Parse(
                        "0123456789abcdef0123456789abcdef"),
                    CancellationToken.None);
                generationWorkspace = workspaceLease.CreateGenerationWorkspace(
                    generationId ?? DebugGenerationId.Initial,
                    "Book1.xlsm");
                await File.WriteAllBytesAsync(
                    generationWorkspace.WorkbookPath,
                    [0x50, 0x4b]);
                return new LeaseIssuedGenerationFixture(
                    temp,
                    workspaceLease,
                    generationWorkspace);
            }
            catch
            {
                if (generationWorkspace is not null)
                {
                    await generationWorkspace.DisposeAsync();
                }
                if (workspaceLease is not null)
                {
                    await workspaceLease.DisposeAsync();
                }
                temp.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            try
            {
                await GenerationWorkspace.DisposeAsync();
            }
            finally
            {
                try
                {
                    await WorkspaceLease.DisposeAsync();
                }
                finally
                {
                    temp.Dispose();
                }
            }
        }
    }

    private sealed class ConstantCompilationSettingsReader(
        DebugCompilationSettings settings) : IDebugCompilationSettingsReader
    {
        public DebugCompilationSettings Read(string workbookPath) => settings;
    }

    private sealed class ThrowingCompilationSettingsReader : IDebugCompilationSettingsReader
    {
        public DebugCompilationSettings Read(string workbookPath)
            => throw new InvalidOperationException(
                "Synthetic compilation-settings read failure.");
    }

    private sealed class RecordingVbeDebugSessionFactory(
        List<string> events,
        IVbeDebugSession session) : IVbeDebugSessionFactory
    {
        public Task<IVbeDebugSession> StartVisibleAsync(CancellationToken cancellationToken)
        {
            events.Add("start-visible");
            return Task.FromResult(session);
        }
    }

    private sealed class GatedVbeDebugSessionFactory(
        List<string> events,
        IVbeDebugSession session) : IVbeDebugSessionFactory
    {
        private readonly TaskCompletionSource started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => started.Task;

        public void Release() => release.TrySetResult();

        public async Task<IVbeDebugSession> StartVisibleAsync(
            CancellationToken cancellationToken)
        {
            events.Add("start-visible");
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return session;
        }
    }

    private sealed class ThrowingVbeDebugSessionFactory(List<string> events)
        : IVbeDebugSessionFactory
    {
        public Task<IVbeDebugSession> StartVisibleAsync(
            CancellationToken cancellationToken)
        {
            events.Add("start-visible");
            return Task.FromException<IVbeDebugSession>(
                new InvalidOperationException("Synthetic visible-session start failure."));
        }
    }

    private sealed class RecordingRunningSession(
        List<string> events,
        string label) : IStandaloneVbaDebugRunningSession
    {
        private readonly TaskCompletionSource<int> completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<int> Completion => completion.Task;

        public int ProcessId => 4321;

        public string TargetModuleName => "Module1";

        public string TargetProcedureName => "Run";

        public IReadOnlyList<VbeBreakpoint> VerifiedBreakpoints => [];

        public int TerminateCalls { get; private set; }

        public int DisposeCalls { get; private set; }

        public ValueTask TerminateAsync()
        {
            TerminateCalls++;
            events.Add($"{label}:terminate");
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            events.Add($"{label}:dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class GatedRunningSession(
        List<string> events,
        string label) : IStandaloneVbaDebugRunningSession
    {
        private readonly TaskCompletionSource<int> completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource terminateStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseTerminate = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource disposeStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseDispose = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<int> Completion => completion.Task;

        public Task TerminateStarted => terminateStarted.Task;

        public Task DisposeStarted => disposeStarted.Task;

        public int ProcessId => 4321;

        public string TargetModuleName => "Module1";

        public string TargetProcedureName => "Run";

        public IReadOnlyList<VbeBreakpoint> VerifiedBreakpoints => [];

        public void ReleaseTerminate() => releaseTerminate.TrySetResult();

        public void ReleaseDispose() => releaseDispose.TrySetResult();

        public async ValueTask TerminateAsync()
        {
            events.Add($"{label}:terminate-start");
            terminateStarted.TrySetResult();
            await releaseTerminate.Task;
            events.Add($"{label}:terminate-complete");
        }

        public async ValueTask DisposeAsync()
        {
            events.Add($"{label}:dispose-start");
            disposeStarted.TrySetResult();
            await releaseDispose.Task;
            events.Add($"{label}:dispose-complete");
        }
    }

    private sealed class RecordingVbeDebugSession(
        List<string> events,
        Exception? openException = null) : IVbeDebugSession
    {
        private int disposed;

        public int ProcessId => 1234;

        public string? OpenedWorkbookPath { get; private set; }

        public IVbaDebugGenerationWorkspace? AdoptedGenerationWorkspace { get; private set; }

        public IDebugInputWaitSink? WorkbookOpenInputWaitSink { get; private set; }

        public DebugTargetProcedure? Target { get; private set; }

        public IReadOnlyList<VbeBreakpoint> Breakpoints { get; private set; } = [];

        public Task<DebugProcessExit> Completion { get; } =
            Task.FromResult(new DebugProcessExit(0));

        public Task<DebugCompilationHostFacts> GetCompilationHostFactsAsync(
            CancellationToken cancellationToken)
            => Task.FromResult(new DebugCompilationHostFacts(
                "16.0",
                "7.1",
                "Windows",
                DebugExcelProcessArchitecture.X64,
                DebugCompilationHostFactsStatus.Verified,
                new DebugCompilerBuiltInConstants(true, true, false, true, true, false),
                null));

        public void AdoptGenerationWorkspace(
            IVbaDebugGenerationWorkspace generationWorkspace)
        {
            ArgumentNullException.ThrowIfNull(generationWorkspace);
            if (AdoptedGenerationWorkspace is not null)
            {
                throw new InvalidOperationException(
                    "A generation workspace has already been adopted.");
            }
            AdoptedGenerationWorkspace = generationWorkspace;
        }

        public Task OpenGeneratedWorkbookAsync(
            IDebugInputWaitSink? inputWaitSink,
            CancellationToken cancellationToken)
        {
            OpenedWorkbookPath = AdoptedGenerationWorkspace?.WorkbookPath
                ?? throw new InvalidOperationException(
                    "A generation workspace must be adopted before opening its workbook.");
            WorkbookOpenInputWaitSink = inputWaitSink;
            events.Add($"open:{Path.GetFileName(OpenedWorkbookPath)}");
            return openException is null
                ? Task.CompletedTask
                : Task.FromException(openException);
        }

        public Task SetNativeBreakpointsAsync(
            IReadOnlyList<VbeBreakpoint> breakpoints,
            CancellationToken cancellationToken)
        {
            Breakpoints = breakpoints;
            events.Add("set-breakpoints");
            return Task.CompletedTask;
        }

        public Task RunTargetAsync(
            DebugTargetProcedure target,
            IDebugInputWaitSink? inputWaitSink,
            CancellationToken cancellationToken)
        {
            Target = target;
            events.Add($"run:{target.ModuleName}.{target.ProcedureName}");
            return Task.CompletedTask;
        }

        public ValueTask TerminateAsync()
        {
            events.Add("terminate");
            return ValueTask.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }
            events.Add("dispose");
            if (AdoptedGenerationWorkspace is not null)
            {
                await AdoptedGenerationWorkspace.DisposeAsync();
            }
        }
    }

    private sealed class RecordingDebugLifecycleSink : IDebugLifecycleSink
    {
        public List<DebugLifecycleMessage> Messages { get; } = [];

        public ValueTask WriteAsync(
            DebugLifecycleMessage message,
            CancellationToken cancellationToken)
        {
            Messages.Add(message);
            return ValueTask.CompletedTask;
        }
    }
}
