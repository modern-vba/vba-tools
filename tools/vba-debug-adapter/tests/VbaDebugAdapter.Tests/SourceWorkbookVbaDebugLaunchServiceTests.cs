using System.Text.Json;
using VbaDebugAdapter.Build;
using VbaDebugAdapter.Cli;
using VbaDebugAdapter.Debugging;
using VbaDebugAdapter.Infrastructure;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class SourceWorkbookVbaDebugLaunchServiceTests
{
    [Fact]
    public async Task SuccessfulSourceLaunchDisplaysALegacyWorkbookBinWarningExactlyOnce()
    {
        const string warning = "[WARN] project-workbook-bin-deprecated: The binPath setting for document 'Book' "
            + "is deprecated and scheduled for removal. Remove binPath from vba-project.json; "
            + "ordinary Build, Debug, Test, and project Export use templatePath. Existing bin files are left unchanged.";
        await using var fixture = await Fixture.CreateAsync();
        fixture.Description.StandardError = warning + Environment.NewLine;
        fixture.Process.StandardError = warning + Environment.NewLine;
        var sink = new RecordingLifecycleSink();

        await using var plan = await fixture.PrepareAsync(sink);
        var running = await plan.CommitAsync(null, CancellationToken.None);
        await running.TerminateAsync();
        await running.DisposeAsync();

        Assert.Equal(warning, Assert.Single(sink.Messages).Output);
        Assert.Contains("run:Boot.Start", fixture.Events);
        Assert.Equal("detach", fixture.Events[^1]);
        Assert.DoesNotContain("save", fixture.Events);
        Assert.DoesNotContain("close", fixture.Events);
        Assert.DoesNotContain("kill", fixture.Events);
        Assert.False(Directory.Exists(plan.Snapshot.GenerationWorkspacePath));
    }

    [Fact]
    public async Task BinFreeSourceLaunchDoesNotDisplayUnrelatedOrControlChildStderr()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Description.StandardError = "An unrelated description diagnostic.\n"
            + "[WARN] unrelated-warning: This is not a workbook-bin compatibility warning.\n"
            + "[WARN] project-workbook-bin-deprecated: \n"
            + "{\"type\":\"workbookConfirmation\",\"message\":\"Not console output.\"}\n";
        fixture.Process.StandardError = "An unrelated preparation diagnostic.\n"
            + "{\"type\":\"debugPreparationReady\",\"generationId\":\"Not console output.\"}\n";
        var sink = new RecordingLifecycleSink();

        await using var plan = await fixture.PrepareAsync(sink);
        var running = await plan.CommitAsync(null, CancellationToken.None);
        await running.TerminateAsync();
        await running.DisposeAsync();

        Assert.Empty(sink.Messages);
        Assert.Contains("run:Boot.Start", fixture.Events);
        Assert.Equal("detach", fixture.Events[^1]);
    }

    [Fact]
    public async Task MismatchedSourceDescriptionDoesNotDisplayItsWarningOrAcquireExcel()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Description.ReportedDocumentName = "Other";
        fixture.Description.StandardError = "[WARN] project-workbook-bin-deprecated: "
            + "Remove binPath from vba-project.json; the setting is deprecated and scheduled for removal.\n";
        var sink = new RecordingLifecycleSink();

        var failure = await Assert.ThrowsAsync<DebugFailureException>(() => fixture.PrepareAsync(sink));
        var cause = Assert.IsType<DebugSetupException>(failure.FailureOutcome.PrimaryFailure);

        Assert.Contains("does not match the selected project and document", cause.Message,
            StringComparison.Ordinal);
        Assert.False(failure.FailureOutcome.HasUnprovedRelease);
        Assert.NotEmpty(failure.FailureOutcome.Evidence);
        Assert.All(failure.FailureOutcome.Evidence, evidence => Assert.True(evidence.Released));
        Assert.Empty(sink.Messages);
        Assert.Null(fixture.Factory.OpenedPath);
        Assert.Null(fixture.Process.Arguments);
        Assert.Empty(fixture.Events);
    }

    [Fact]
    public async Task EmptyNativeCleanupEvidenceRemainsUnprovedAfterStoppingARunningSourceSession()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Native.ReportEmptyCleanup = true;
        await using var plan = await fixture.PrepareAsync();
        var running = await plan.CommitAsync(null, CancellationToken.None);

        await running.TerminateAsync();
        await running.DisposeAsync();

        var outcome = Assert.IsAssignableFrom<IDebugResourceOwnerEvidence>(running).CleanupOutcome!;
        Assert.True(outcome.HasUnprovedRelease);
        Assert.Contains(outcome.Evidence, item => item.Kind == DebugResourceKind.Com && !item.Released);
        Assert.Contains(outcome.Evidence, item => item.Kind == DebugResourceKind.Handle && !item.Released);
        Assert.Equal("detach", fixture.Events[^1]);
        Assert.DoesNotContain("kill", fixture.Events);
    }

    [Fact]
    public async Task EmptyNativeCleanupEvidenceCannotProveAFailedLaunchReleasedItsBinding()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Native.ReportEmptyCleanup = true;
        fixture.Native.RunFailure = new InvalidOperationException("Native Run failed.");
        var plan = await fixture.PrepareAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => plan.CommitAsync(null, CancellationToken.None));

        var failure = await Assert.ThrowsAsync<DebugFailureException>(
            () => plan.DisposeAsync().AsTask());

        Assert.True(failure.FailureOutcome.HasUnprovedRelease);
        Assert.Contains(failure.FailureOutcome.Evidence,
            item => item.Kind == DebugResourceKind.Com && !item.Released);
        Assert.Contains(failure.FailureOutcome.Evidence,
            item => item.Kind == DebugResourceKind.Handle && !item.Released);
        Assert.Equal("detach", fixture.Events[^1]);
    }

    [Fact]
    public async Task AnUnobservedProjectConstantStopsBeforeCaptureOrReplacement()
    {
        await using var fixture = await Fixture.CreateAsync(
            "Attribute VB_Name = \"Boot\"\n#If ProjectFeature Then\n"
            + "Public Sub Other()\nEnd Sub\n#Else\nPublic Sub Start()\nEnd Sub\n#End If\n");
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => fixture.PrepareAsync());
        Assert.Contains("actual workbook compilation context", failure.Message);
        Assert.Contains("ProjectFeature", failure.Message);
        Assert.DoesNotContain("generated workbook", failure.Message);
        Assert.Equal(1, fixture.Native.HostFactsCalls);
        Assert.Null(fixture.Process.Arguments);
        Assert.Equal(new[] { "detach" }, fixture.Events);
    }

    [Fact]
    public async Task ATargetUsingLiveBuiltInsAndSnapshotLocalConstantsCanLaunchWithoutSnapshotBuild()
    {
        await using var fixture = await Fixture.CreateAsync(
            "Attribute VB_Name = \"Boot\"\n#Const Feature = VBA7\n#If Feature And Win64 Then\n"
            + "Public Sub Start()\nEnd Sub\n#End If\n");
        await using var plan = await fixture.PrepareAsync();
        var running = await plan.CommitAsync(null, CancellationToken.None);
        await running.DisposeAsync();
        Assert.Equal(1, fixture.Native.HostFactsCalls);
        Assert.Contains("run:Boot.Start", fixture.Events);
        Assert.DoesNotContain("build", fixture.Process.Arguments!);
    }

    [Fact]
    public async Task MissingCompanionOwnerEvidenceIsUnprovedReleaseNotJustFileDeletionFailure()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.RetainWorkspaceOnDisposal = true;
        fixture.Process.ResultOutcome = new DebugFailureCompletion().Complete();
        var plan = await fixture.PrepareAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => plan.CommitAsync(null, CancellationToken.None));
        await Assert.ThrowsAnyAsync<Exception>(() => plan.DisposeAsync().AsTask());
        var outcome = Assert.IsAssignableFrom<IDebugResourceOwnerEvidence>(plan).CleanupOutcome!;
        Assert.True(outcome.HasUnprovedRelease);
        Assert.Contains(outcome.Evidence, item => item.Kind == DebugResourceKind.Process && !item.Released);
        Assert.Contains(outcome.Evidence, item => item.Kind == DebugResourceKind.Handle && !item.Released);
        Assert.True(File.Exists(fixture.Process.RetentionMarkerPath));
    }

    [Fact]
    public async Task CompanionRetentionIsArmedBeforeStartAndClearedOnlyAfterProvedCompletion()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var plan = await fixture.PrepareAsync();
        var armedBeforeStart = fixture.Process.StartObservedRetentionMarker;
        var markerExistsWhenReady = File.Exists(fixture.Process.RetentionMarkerPath);
        var running = await plan.CommitAsync(null, CancellationToken.None);
        Assert.False(File.Exists(fixture.Process.RetentionMarkerPath));
        await running.DisposeAsync();
        Assert.True(armedBeforeStart);
        Assert.True(markerExistsWhenReady);
    }

    [Fact]
    public async Task UnprovedCompanionReleaseRetainsTheWholeSessionThroughOuterDisposal()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.RetainWorkspaceOnDisposal = true;
        var unproved = new DebugFailureCompletion();
        unproved.AddEvidence(new("companion-exit", "fixture", DebugResourceKind.Process,
            false, "Fixture simulates an unproved companion exit."));
        unproved.AddEvidence(new("companion-handles", "fixture", DebugResourceKind.Handle,
            false, "Fixture simulates unproved companion handle release."));
        fixture.Process.ResultOutcome = unproved.Complete();
        var plan = await fixture.PrepareAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => plan.CommitAsync(null, CancellationToken.None));
        await Assert.ThrowsAnyAsync<Exception>(() => plan.DisposeAsync().AsTask());
        Assert.True(File.Exists(fixture.Process.RetentionMarkerPath));
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Lease.DisposeAsync().AsTask());
        Assert.True(Directory.Exists(plan.Snapshot.GenerationWorkspacePath));
        Assert.True(File.Exists(Path.Combine(plan.Snapshot.GenerationWorkspacePath, "source", "Boot.bas")));
    }

    [Fact]
    public async Task AnUnprovedNativeAcquisitionFailureIsNotReportedAsReleased()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Factory.Failure = new InvalidOperationException("Partial native binding acquisition failed.");
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => fixture.PrepareAsync());
        var outcome = Assert.IsAssignableFrom<IDebugFailureEvidence>(failure).FailureOutcome;
        Assert.True(outcome.HasUnprovedRelease);
        Assert.Contains(outcome.Evidence, item => item.Kind == DebugResourceKind.Com && !item.Released);
        Assert.Contains(outcome.Evidence, item => item.Kind == DebugResourceKind.Handle && !item.Released);
        Assert.DoesNotContain(outcome.Evidence, item => item.Stage == "source-admission"
            && item.Kind == DebugResourceKind.Com && item.Released);
    }

    [Fact]
    public async Task ARunAttemptThatFailsIsResetBeforeItsBindingIsDetached()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Native.RunFailure = new InvalidOperationException("Native Run failed after starting.");
        await using var plan = await fixture.PrepareAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => plan.CommitAsync(null, CancellationToken.None));
        await plan.DisposeAsync();
        Assert.Equal(2, fixture.Events.Count(item => item == "reset"));
        Assert.Equal("detach", fixture.Events[^1]);
    }

    [Fact]
    public async Task DisposingACommittingPlanWaitsForTheNativeRunAttemptBeforeReleasingItsBinding()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Native.RunContinuation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var plan = await fixture.PrepareAsync();
        var commit = plan.CommitAsync(null, CancellationToken.None);
        await fixture.Native.RunEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposal = plan.DisposeAsync().AsTask();
        try
        {
            await Task.Yield();
            Assert.False(disposal.IsCompleted);
            Assert.DoesNotContain("detach", fixture.Events);
        }
        finally
        {
            fixture.Native.RunContinuation.TrySetResult();
            try { _ = await commit; } catch (OperationCanceledException) { }
            await disposal;
        }
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("negative-count")]
    [InlineData("invalid-warning")]
    public async Task APreparationReceiptMustBeClosedUnambiguousAndWellTyped(string malformed)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Process.TransformReceipt = json => malformed switch
        {
            "duplicate" => json.Replace("\"type\":", "\"type\":\"debugWorkbookPrepared\",\"type\":"),
            "unknown" => json.Insert(json.Length - 1, ",\"untrusted\":true"),
            "negative-count" => json.Replace("\"importedSourceFileCount\":1", "\"importedSourceFileCount\":-1"),
            _ => json.Replace("\"warnings\":[]", "\"warnings\":[123]")
        };
        await using var plan = await fixture.PrepareAsync();
        await Assert.ThrowsAsync<DebugSetupException>(() => plan.CommitAsync(null, CancellationToken.None));
        Assert.DoesNotContain("run:Boot.Start", fixture.Events);
    }

    [Fact]
    public async Task LaunchPreparesTheExactSourceAndStopResetsWithoutOwningItsLifetime()
    {
        using var temp = TempDirectory.Create();
        var project = Path.Combine(temp.Path, "Project");
        Directory.CreateDirectory(project);
        var sourceWorkbook = Path.Combine(project, "src", "Book", "Book.xlsm");
        var executable = Path.Combine(temp.Path, "vba-dev.exe");
        File.WriteAllBytes(executable, []);
        var events = new List<string>();
        var native = new NativeSession(sourceWorkbook, events);
        var factory = new NativeFactory(native);
        var process = new PreparationProcess(events);
        var service = new SourceWorkbookVbaDebugLaunchService(new DebugSourceAdmission(932),
            new VbaDevSourceWorkbookResolver(new DescriptionProcess(project, sourceWorkbook)),
            process, factory);
        await using var lease = await new VbaDebugSessionWorkspaceManager(Path.Combine(temp.Path, "Workspaces"))
            .ClaimAsync(DebugSessionId.Parse("0123456789abcdef0123456789abcdef"), CancellationToken.None);
        var sourceUri = new Uri(Path.Combine(project, "src", "Book", "Boot.bas")).AbsoluteUri;
        var sources = new TransportedDebugSourceSnapshot(2,
            [new("Boot.bas", sourceUri, "utf8bom", Convert.ToBase64String(
                DebugSnapshotTestEncoding.Utf8BomBytes(
                    "Attribute VB_Name = \"Boot\"\nPublic Sub Start()\nEnd Sub\n")))]);

        await using var plan = await service.PrepareAsync(executable, lease,
            new(project, "Book", "Book.xlsm", "Boot", "Start", sources),
            null, CancellationToken.None);
        Assert.Equal(sourceWorkbook, factory.OpenedPath);
        Assert.Equal(new[] { "captured-ready" }, events);
        Assert.False(File.Exists(Path.Combine(plan.Snapshot.GenerationWorkspacePath, "Book.xlsm")));

        var running = await plan.CommitAsync(null, CancellationToken.None);
        Assert.Equal(new[] { "captured-ready", "reset", "import", "run:Boot.Start" }, events);
        Assert.Equal(native.ProcessId, running.ProcessId);
        Assert.DoesNotContain("build", process.Arguments!);
        Assert.DoesNotContain("--output", process.Arguments!);
        await running.TerminateAsync();
        Assert.Equal(2, events.Count(item => item == "reset"));
        await running.DisposeAsync();
        Assert.Contains("detach", events);
        Assert.DoesNotContain("close", events);
        Assert.DoesNotContain("save", events);
        Assert.DoesNotContain("kill", events);
        Assert.False(Directory.Exists(plan.Snapshot.GenerationWorkspacePath));
    }

    private static DebugFailureOutcome ReleasedProcess()
    {
        var completion = new DebugFailureCompletion();
        completion.AddEvidence(new("companion-exit", "fixture", DebugResourceKind.Process,
            true, "Fixture invocation exited."));
        completion.AddEvidence(new("companion-handles", "fixture", DebugResourceKind.Handle,
            true, "Fixture invocation released its handles."));
        return completion.Complete();
    }

    private sealed class DescriptionProcess(string project, string workbook) : IVbaDevBuildProcess
    {
        internal string StandardError { get; set; } = "";
        internal string ReportedDocumentName { get; set; } = "Book";

        public Task<VbaDevBuildProcessResult> RunAsync(string fileName,
            IReadOnlyList<string> arguments, CancellationToken cancellationToken)
            => Task.FromResult(new VbaDevBuildProcessResult(0, JsonSerializer.Serialize(new
            {
                type = "debugWorkbookDescription", schemaVersion = "1.0",
                projectRoot = project, documentName = ReportedDocumentName, workbookPath = workbook
            }), StandardError) { CleanupOutcome = ReleasedProcess() });
    }

    private sealed class PreparationProcess(List<string> events) : IManagedDebugPreparationProcess
    {
        internal IReadOnlyList<string>? Arguments { get; private set; }
        internal Func<string, string> TransformReceipt { get; set; } = value => value;
        internal DebugFailureOutcome? ResultOutcome { get; set; }
        internal string? RetentionMarkerPath { get; set; }
        internal bool StartObservedRetentionMarker { get; private set; }
        internal string StandardError { get; set; } = "";
        public async Task<ManagedDebugPreparationProcessResult> RunAsync(string executable,
            IReadOnlyList<string> arguments, DebugPreparationProcessBinding binding,
            Func<string, CancellationToken, Task<bool>> confirmReplacement,
            Func<CancellationToken, Task<bool>> continueAfterCapture, CancellationToken cancellationToken)
        {
            Arguments = arguments;
            StartObservedRetentionMarker = File.Exists(RetentionMarkerPath);
            events.Add("captured-ready");
            var accepted = await continueAfterCapture(cancellationToken);
            if (accepted) events.Add("import");
            return new(accepted ? 0 : 1, accepted ? TransformReceipt(JsonSerializer.Serialize(new
            {
                type = "debugWorkbookPrepared", schemaVersion = "1.0",
                generationId = binding.GenerationId, workbookPath = binding.WorkbookPath,
                excelProcessId = binding.ExcelProcessId,
                excelProcessStartUtcTicks = binding.ExcelProcessStartUtcTicks,
                importedSourceFileCount = 1, warnings = Array.Empty<string>()
            })) : "", StandardError, ResultOutcome ?? ReleasedProcess());
        }
    }

    private sealed class RecordingLifecycleSink : IDebugLifecycleSink
    {
        internal List<DebugLifecycleMessage> Messages { get; } = [];

        public ValueTask WriteAsync(DebugLifecycleMessage message, CancellationToken cancellationToken)
        {
            Messages.Add(message);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NativeFactory(NativeSession session) : ISourceVbeDebugSessionFactory
    {
        internal string? OpenedPath { get; private set; }
        internal Exception? Failure { get; set; }
        public Task<ISourceVbeDebugSession> AttachOrOpenAsync(string path, CancellationToken cancellationToken)
        {
            OpenedPath = path;
            if (Failure is not null) throw Failure;
            return Task.FromResult<ISourceVbeDebugSession>(session);
        }
    }

    private sealed class NativeSession(string workbookPath, List<string> events)
        : ISourceVbeDebugSession, IDebugResourceOwnerEvidence
    {
        private readonly TaskCompletionSource<SourceVbeDebugSessionCompletion> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Exception? RunFailure { get; set; }
        internal TaskCompletionSource? RunContinuation { get; set; }
        internal TaskCompletionSource RunEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int HostFactsCalls { get; private set; }
        internal bool ReportEmptyCleanup { get; set; }
        public int ProcessId => 12345;
        public long ProcessStartUtcTicks => 638000000000000000;
        public bool WasAlreadyOpen => true;
        public Task<SourceVbeDebugSessionCompletion> Completion => completion.Task;
        public DebugFailureOutcome? CleanupOutcome { get; private set; }
        public Task<SourceVbeDebugSessionInspection> InspectAsync(CancellationToken cancellationToken)
            => Task.FromResult(new SourceVbeDebugSessionInspection(
                workbookPath, ProcessId, ProcessStartUtcTicks, true, 2));
        public Task<DebugCompilationHostFacts> GetCompilationHostFactsAsync(CancellationToken cancellationToken)
        {
            HostFactsCalls++;
            return Task.FromResult(new DebugCompilationHostFacts("16.0", "7.01", "Windows (64-bit) NT 10.00",
                DebugExcelProcessArchitecture.X64, DebugCompilationHostFactsStatus.Verified,
                new DebugCompilerBuiltInConstants(true, true, false, true, true, false), null));
        }
        public Task SetNativeBreakpointsAsync(IReadOnlyList<VbeBreakpoint> breakpoints,
            CancellationToken cancellationToken) => Task.CompletedTask;
        public async Task RunTargetAsync(DebugTargetProcedure target, IDebugInputWaitSink? inputWaitSink,
            CancellationToken cancellationToken)
        {
            events.Add($"run:{target.ModuleName}.{target.ProcedureName}");
            RunEntered.TrySetResult();
            if (RunContinuation is not null) await RunContinuation.Task;
            if (RunFailure is not null) throw RunFailure;
        }
        public Task ResetExecutionAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            events.Add("reset");
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            events.Add("detach");
            var released = new DebugFailureCompletion();
            if (!ReportEmptyCleanup)
                foreach (var kind in new[] { DebugResourceKind.Process, DebugResourceKind.Com, DebugResourceKind.Handle })
                    released.AddEvidence(new("source-binding-release", workbookPath, kind, true,
                        "Fixture binding released; no workbook/process lifetime ownership was acquired.", ProcessId));
            CleanupOutcome = released.Complete();
            completion.TrySetResult(new(SourceVbeDebugSessionEndReason.Detached, null));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TempDirectory temp = TempDirectory.Create();
        private IVbaDebugSessionWorkspaceLease lease = null!;
        private SourceWorkbookVbaDebugLaunchService service = null!;
        private StandaloneVbaDebugLaunchRequest request = null!;
        private string executable = "";
        internal List<string> Events { get; } = [];
        internal NativeSession Native { get; private set; } = null!;
        internal PreparationProcess Process { get; private set; } = null!;
        internal DescriptionProcess Description { get; private set; } = null!;
        internal NativeFactory Factory { get; private set; } = null!;
        internal IVbaDebugSessionWorkspaceLease Lease => lease;
        internal bool RetainWorkspaceOnDisposal { get; set; }
        internal static async Task<Fixture> CreateAsync(string source =
            "Attribute VB_Name = \"Boot\"\nPublic Sub Start()\nEnd Sub\n")
        {
            var fixture = new Fixture();
            var project = Path.Combine(fixture.temp.Path, "Project");
            Directory.CreateDirectory(project);
            var workbook = Path.Combine(project, "src", "Book", "Book.xlsm");
            fixture.executable = Path.Combine(fixture.temp.Path, "vba-dev.exe");
            File.WriteAllBytes(fixture.executable, []);
            fixture.Native = new(workbook, fixture.Events);
            fixture.Process = new(fixture.Events);
            fixture.Factory = new(fixture.Native);
            fixture.Description = new(project, workbook);
            fixture.service = new(new DebugSourceAdmission(932),
                new VbaDevSourceWorkbookResolver(fixture.Description),
                fixture.Process, fixture.Factory);
            fixture.lease = await new VbaDebugSessionWorkspaceManager(Path.Combine(fixture.temp.Path, "Workspaces"))
                .ClaimAsync(DebugSessionId.Parse("0123456789abcdef0123456789abcdef"), CancellationToken.None);
            fixture.Process.RetentionMarkerPath = Path.Combine(fixture.lease.SessionWorkspacePath, "source-companion-retention.json");
            var sources = new TransportedDebugSourceSnapshot(2,
                [new("Boot.bas", new Uri(Path.Combine(project, "src", "Book", "Boot.bas")).AbsoluteUri,
                    "utf8bom", Convert.ToBase64String(DebugSnapshotTestEncoding.Utf8BomBytes(source)))]);
            fixture.request = new(project, "Book", "Book.xlsm", "Boot", "Start", sources);
            return fixture;
        }
        internal Task<IPreparedDebugLaunchPlan> PrepareAsync(IDebugLifecycleSink? lifecycleSink = null)
            => service.PrepareAsync(executable, lease, request, null, CancellationToken.None, lifecycleSink);
        public async ValueTask DisposeAsync()
        {
            try { await lease.DisposeAsync(); }
            catch when (RetainWorkspaceOnDisposal) { }
            if (!RetainWorkspaceOnDisposal) temp.Dispose();
        }
    }
}
