using System.Text.RegularExpressions;
using VbaDebugAdapter.Debugging;
using VbaDebugAdapter.Diagnostics;
using VbaDebugAdapter.Infrastructure;
using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class VbeDebugEnvironmentProbeTests
{
    [Fact]
    public async Task WorkspaceStageClaimsAProductionLeaseAndDeletionDisposesIt()
    {
        var manager = new RecordingWorkspaceManager();
        var probe = new VbeDebugEnvironmentProbeFactory(manager).Create();

        var workspace = await probe.RunStageAsync(
            "workspace.session",
            CancellationToken.None);
        var deletion = await probe.RunStageAsync(
            "workspace.deletion",
            CancellationToken.None);

        Assert.Equal(DebugEnvironmentDiagnosticStatus.Pass, workspace.Status);
        var sessionId = Assert.Single(manager.ClaimedSessionIds);
        Assert.Matches(new Regex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant), sessionId);
        Assert.Equal([sessionId], manager.ReapedExclusions);
        Assert.True(manager.Lease.Disposed);
        Assert.Equal(DebugEnvironmentDiagnosticStatus.Pass, deletion.Status);
    }

    [Fact]
    public async Task ExcelStartupUsesTheProductionSessionFactoryAndReportsItsPid()
    {
        var manager = new RecordingWorkspaceManager();
        var session = new RecordingDebugSession(processId: 4321);
        var sessionFactory = new RecordingDebugSessionFactory(session);
        var probe = new VbeDebugEnvironmentProbeFactory(
            manager,
            sessionFactory).Create();
        _ = await probe.RunStageAsync("workspace.session", CancellationToken.None);

        var startup = await probe.RunStageAsync(
            "excel.startup",
            CancellationToken.None);

        Assert.Equal(DebugEnvironmentDiagnosticStatus.Pass, startup.Status);
        Assert.Equal(1, sessionFactory.Invocations);
        Assert.NotNull(startup.Details);
        Assert.Equal(4321, startup.Details["processId"]);
    }

    [Fact]
    public async Task ProcessOwnershipRequiresTheExactSessionKillOnCloseJob()
    {
        var session = new RecordingDebugSession(
            processId: 4321,
            strongProcessOwnershipEstablished: true);
        var probe = new VbeDebugEnvironmentProbeFactory(
            new RecordingWorkspaceManager(),
            new RecordingDebugSessionFactory(session)).Create();
        _ = await probe.RunStageAsync("workspace.session", CancellationToken.None);
        _ = await probe.RunStageAsync("excel.startup", CancellationToken.None);

        var ownership = await probe.RunStageAsync(
            "excel.processOwnership",
            CancellationToken.None);

        Assert.Equal(DebugEnvironmentDiagnosticStatus.Pass, ownership.Status);
        Assert.NotNull(ownership.Details);
        Assert.Equal(4321, ownership.Details["processId"]);
        Assert.Equal(true, ownership.Details["killOnCloseJobAssigned"]);
    }

    [Fact]
    public async Task FixtureCreationUsesOnlyTheClaimedSessionWorkspace()
    {
        using var temp = TempDirectory.Create();
        var manager = new RecordingWorkspaceManager(temp.Path);
        var session = new RecordingDebugSession(processId: 4321);
        var probe = new VbeDebugEnvironmentProbeFactory(
            manager,
            new RecordingDebugSessionFactory(session)).Create();
        _ = await probe.RunStageAsync("workspace.session", CancellationToken.None);
        _ = await probe.RunStageAsync("excel.startup", CancellationToken.None);
        _ = await probe.RunStageAsync("excel.processOwnership", CancellationToken.None);

        var fixture = await probe.RunStageAsync(
            "workbook.fixtureCreation",
            CancellationToken.None);

        var expectedWorkbookPath = Path.Combine(temp.Path, "VbaToolsDoctorProbe.xlsm");
        var expectedSourcePath = Path.Combine(temp.Path, "VbaToolsDoctorProbe.bas");
        Assert.Equal(expectedWorkbookPath, session.CreatedFixtureWorkbookPath);
        Assert.Equal(DebugEnvironmentDiagnosticStatus.Pass, fixture.Status);
        Assert.True(File.Exists(expectedSourcePath));
        var source = await File.ReadAllTextAsync(expectedSourcePath);
        Assert.Contains(
            "Attribute VB_Name = \"VbaToolsDoctorProbe\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ThisWorkbook.Worksheets(1).Range(\"A1\").Value2 = \"vba-tools-doctor-complete\"",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkbookOpenUsesTheExactFixtureCreatedByTheDoctor()
    {
        using var temp = TempDirectory.Create();
        var session = new RecordingDebugSession(processId: 4321);
        var probe = new VbeDebugEnvironmentProbeFactory(
            new RecordingWorkspaceManager(temp.Path),
            new RecordingDebugSessionFactory(session)).Create();
        foreach (var checkId in new[]
                 {
                     "workspace.session",
                     "excel.startup",
                     "excel.processOwnership",
                     "workbook.fixtureCreation"
                 })
        {
            _ = await probe.RunStageAsync(checkId, CancellationToken.None);
        }

        var opened = await probe.RunStageAsync(
            "workbook.open",
            CancellationToken.None);

        Assert.Equal(DebugEnvironmentDiagnosticStatus.Pass, opened.Status);
        Assert.Equal(
            Path.Combine(temp.Path, "VbaToolsDoctorProbe.xlsm"),
            session.OpenedFixtureWorkbookPath);
    }

    [Fact]
    public async Task VbideAccessImportsAndVerifiesTheExactProjectedStandardModule()
    {
        using var temp = TempDirectory.Create();
        var session = new RecordingDebugSession(processId: 4321);
        var probe = new VbeDebugEnvironmentProbeFactory(
            new RecordingWorkspaceManager(temp.Path),
            new RecordingDebugSessionFactory(session)).Create();
        foreach (var checkId in new[]
                 {
                     "workspace.session",
                     "excel.startup",
                     "excel.processOwnership",
                     "workbook.fixtureCreation",
                     "workbook.open"
                 })
        {
            _ = await probe.RunStageAsync(checkId, CancellationToken.None);
        }

        var vbide = await probe.RunStageAsync(
            "vbide.access",
            CancellationToken.None);

        Assert.Equal(DebugEnvironmentDiagnosticStatus.Pass, vbide.Status);
        Assert.Equal(
            Path.Combine(temp.Path, "VbaToolsDoctorProbe.bas"),
            session.ImportedFixtureSourcePath);
        Assert.NotNull(session.ImportedFixtureSourceMap);
        Assert.Equal("VbaToolsDoctorProbe", session.ImportedFixtureSourceMap.ModuleName);
        Assert.Equal(VbaModuleKind.StandardModule, session.ImportedFixtureSourceMap.ModuleKind);
        Assert.Equal(
            "    ThisWorkbook.Worksheets(1).Range(\"A1\").Value2 = \"vba-tools-doctor-complete\"",
            session.ImportedFixtureSourceMap.CodeLines[4]);
    }

    [Fact]
    public async Task CommandContextVerifiesBreakpointAndRunControlsWithoutExecutingThem()
    {
        using var temp = TempDirectory.Create();
        var session = new RecordingDebugSession(processId: 4321);
        var probe = new VbeDebugEnvironmentProbeFactory(
            new RecordingWorkspaceManager(temp.Path),
            new RecordingDebugSessionFactory(session)).Create();
        foreach (var checkId in new[]
                 {
                     "workspace.session",
                     "excel.startup",
                     "excel.processOwnership",
                     "workbook.fixtureCreation",
                     "workbook.open",
                     "vbide.access"
                 })
        {
            _ = await probe.RunStageAsync(checkId, CancellationToken.None);
        }

        var commandContext = await probe.RunStageAsync(
            "vbe.commandContext",
            CancellationToken.None);

        Assert.Equal(DebugEnvironmentDiagnosticStatus.Pass, commandContext.Status);
        Assert.NotNull(session.VerifiedCommandBreakpoint);
        Assert.Equal(5, session.VerifiedCommandBreakpoint.VbideLine);
        Assert.Equal(
            new DebugTargetProcedure("VbaToolsDoctorProbe", "RunDoctorProbe"),
            session.VerifiedCommandTarget);
        Assert.NotNull(commandContext.Details);
        Assert.Equal(51, commandContext.Details["toggleBreakpointCommandId"]);
        Assert.Equal(186, commandContext.Details["runOrContinueCommandId"]);
    }

    [Fact]
    public async Task BreakpointStageTogglesTheExactlyMappedExecutableLine()
    {
        using var temp = TempDirectory.Create();
        var session = new RecordingDebugSession(processId: 4321);
        var probe = new VbeDebugEnvironmentProbeFactory(
            new RecordingWorkspaceManager(temp.Path),
            new RecordingDebugSessionFactory(session)).Create();
        foreach (var checkId in new[]
                 {
                     "workspace.session",
                     "excel.startup",
                     "excel.processOwnership",
                     "workbook.fixtureCreation",
                     "workbook.open",
                     "vbide.access",
                     "vbe.commandContext"
                 })
        {
            _ = await probe.RunStageAsync(checkId, CancellationToken.None);
        }

        var breakpoint = await probe.RunStageAsync(
            "vbe.breakpoint",
            CancellationToken.None);

        Assert.Equal(DebugEnvironmentDiagnosticStatus.Pass, breakpoint.Status);
        var nativeBreakpoint = Assert.Single(session.NativeBreakpoints);
        Assert.Equal("VbaToolsDoctorProbe", nativeBreakpoint.ModuleName);
        Assert.Equal(5, nativeBreakpoint.VbideLine);
        Assert.Equal(
            "    ThisWorkbook.Worksheets(1).Range(\"A1\").Value2 = \"vba-tools-doctor-complete\"",
            nativeBreakpoint.ExpectedCodeLine);
    }

    [Fact]
    public async Task BreakModeStageRunsTheHarmlessTargetAndObservesNativeBreakMode()
    {
        using var temp = TempDirectory.Create();
        var session = new RecordingDebugSession(processId: 4321);
        var probe = new VbeDebugEnvironmentProbeFactory(
            new RecordingWorkspaceManager(temp.Path),
            new RecordingDebugSessionFactory(session)).Create();
        foreach (var checkId in new[]
                 {
                     "workspace.session",
                     "excel.startup",
                     "excel.processOwnership",
                     "workbook.fixtureCreation",
                     "workbook.open",
                     "vbide.access",
                     "vbe.commandContext",
                     "vbe.breakpoint"
                 })
        {
            _ = await probe.RunStageAsync(checkId, CancellationToken.None);
        }

        var breakMode = await probe.RunStageAsync(
            "vbe.breakMode",
            CancellationToken.None);

        Assert.Equal(DebugEnvironmentDiagnosticStatus.Pass, breakMode.Status);
        Assert.Equal(
            ["run:VbaToolsDoctorProbe.RunDoctorProbe", "wait-break-mode"],
            session.ProbeEvents);
    }

    [Fact]
    public async Task ContinueStageInvokesTheNativeCommandForTheSameTarget()
    {
        using var temp = TempDirectory.Create();
        var session = new RecordingDebugSession(processId: 4321);
        var probe = new VbeDebugEnvironmentProbeFactory(
            new RecordingWorkspaceManager(temp.Path),
            new RecordingDebugSessionFactory(session)).Create();
        foreach (var checkId in new[]
                 {
                     "workspace.session",
                     "excel.startup",
                     "excel.processOwnership",
                     "workbook.fixtureCreation",
                     "workbook.open",
                     "vbide.access",
                     "vbe.commandContext",
                     "vbe.breakpoint",
                     "vbe.breakMode"
                 })
        {
            _ = await probe.RunStageAsync(checkId, CancellationToken.None);
        }
        session.ProbeEvents.Clear();

        var continued = await probe.RunStageAsync(
            "vbe.continue",
            CancellationToken.None);

        Assert.Equal(DebugEnvironmentDiagnosticStatus.Pass, continued.Status);
        Assert.Equal(
            ["continue:VbaToolsDoctorProbe.RunDoctorProbe"],
            session.ProbeEvents);
    }

    [Fact]
    public async Task CompletionStageObservesDesignModeAndTheHarmlessMarker()
    {
        using var temp = TempDirectory.Create();
        var session = new RecordingDebugSession(processId: 4321);
        var probe = new VbeDebugEnvironmentProbeFactory(
            new RecordingWorkspaceManager(temp.Path),
            new RecordingDebugSessionFactory(session)).Create();
        foreach (var checkId in new[]
                 {
                     "workspace.session",
                     "excel.startup",
                     "excel.processOwnership",
                     "workbook.fixtureCreation",
                     "workbook.open",
                     "vbide.access",
                     "vbe.commandContext",
                     "vbe.breakpoint",
                     "vbe.breakMode",
                     "vbe.continue"
                 })
        {
            _ = await probe.RunStageAsync(checkId, CancellationToken.None);
        }
        session.ProbeEvents.Clear();

        var completion = await probe.RunStageAsync(
            "vbe.procedureCompletion",
            CancellationToken.None);

        Assert.Equal(DebugEnvironmentDiagnosticStatus.Pass, completion.Status);
        Assert.Equal(
            ["wait-completion:vba-tools-doctor-complete"],
            session.ProbeEvents);
    }

    [Fact]
    public async Task BreakpointCleanupTogglesOffTheExactBreakpointThatWasSet()
    {
        using var temp = TempDirectory.Create();
        var session = new RecordingDebugSession(processId: 4321);
        var probe = new VbeDebugEnvironmentProbeFactory(
            new RecordingWorkspaceManager(temp.Path),
            new RecordingDebugSessionFactory(session)).Create();
        foreach (var checkId in DebugEnvironmentDoctor.CheckIds.Skip(1).TakeWhile(
                     checkId => checkId != "vbe.breakpointCleanup"))
        {
            _ = await probe.RunStageAsync(checkId, CancellationToken.None);
        }

        var cleanup = await probe.RunStageAsync(
            "vbe.breakpointCleanup",
            CancellationToken.None);

        Assert.Equal(DebugEnvironmentDiagnosticStatus.Pass, cleanup.Status);
        Assert.NotNull(session.ClearedNativeBreakpoint);
        Assert.Same(
            Assert.Single(session.NativeBreakpoints),
            session.ClearedNativeBreakpoint);
    }

    [Fact]
    public async Task OwnedProcessExitProvesThatNoSessionLocalBreakpointRemains()
    {
        using var temp = TempDirectory.Create();
        var session = new RecordingDebugSession(processId: 4321);
        var probe = new VbeDebugEnvironmentProbeFactory(
            new RecordingWorkspaceManager(temp.Path),
            new RecordingDebugSessionFactory(session)).Create();
        foreach (var checkId in DebugEnvironmentDoctor.CheckIds.Skip(1).TakeWhile(
                     checkId => checkId != "vbe.breakMode"))
        {
            _ = await probe.RunStageAsync(checkId, CancellationToken.None);
        }
        session.MarkProcessExited();

        var cleanup = await probe.RunStageAsync(
            "vbe.breakpointCleanup",
            CancellationToken.None);

        Assert.Equal(DebugEnvironmentDiagnosticStatus.Pass, cleanup.Status);
        Assert.Null(session.ClearedNativeBreakpoint);
        Assert.Contains("process exit", cleanup.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessCloseUsesCooperativeNoSaveCloseBeforeDisposingOwnership()
    {
        using var temp = TempDirectory.Create();
        var session = new RecordingDebugSession(processId: 4321);
        var probe = new VbeDebugEnvironmentProbeFactory(
            new RecordingWorkspaceManager(temp.Path),
            new RecordingDebugSessionFactory(session)).Create();
        foreach (var checkId in DebugEnvironmentDoctor.CheckIds.Skip(1).TakeWhile(
                     checkId => checkId != "excel.processClose"))
        {
            _ = await probe.RunStageAsync(checkId, CancellationToken.None);
        }

        var close = await probe.RunStageAsync(
            "excel.processClose",
            CancellationToken.None);

        Assert.Equal(DebugEnvironmentDiagnosticStatus.Pass, close.Status);
        Assert.Equal(["cooperative-close", "dispose-session"], session.CleanupEvents);
    }

    [Fact]
    public async Task ProcessCloseAcceptsExactOwnedProcessExitWithoutDisconnectedComCleanup()
    {
        using var temp = TempDirectory.Create();
        var session = new RecordingDebugSession(processId: 4321);
        var probe = new VbeDebugEnvironmentProbeFactory(
            new RecordingWorkspaceManager(temp.Path),
            new RecordingDebugSessionFactory(session)).Create();
        _ = await probe.RunStageAsync("workspace.session", CancellationToken.None);
        _ = await probe.RunStageAsync("excel.startup", CancellationToken.None);
        session.MarkProcessExited();

        var close = await probe.RunStageAsync(
            "excel.processClose",
            CancellationToken.None);

        Assert.Equal(DebugEnvironmentDiagnosticStatus.Pass, close.Status);
        Assert.Equal(["dispose-session"], session.CleanupEvents);
        Assert.Contains("process exit", close.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WorkspaceDeletionFallsBackToJobTerminationBeforeDeletingTheLease()
    {
        using var temp = TempDirectory.Create();
        var manager = new RecordingWorkspaceManager(temp.Path);
        var session = new RecordingDebugSession(processId: 4321);
        var probe = new VbeDebugEnvironmentProbeFactory(
            manager,
            new RecordingDebugSessionFactory(session)).Create();
        _ = await probe.RunStageAsync("workspace.session", CancellationToken.None);
        _ = await probe.RunStageAsync("excel.startup", CancellationToken.None);

        var deletion = await probe.RunStageAsync(
            "workspace.deletion",
            CancellationToken.None);

        Assert.Equal(DebugEnvironmentDiagnosticStatus.Pass, deletion.Status);
        Assert.Equal(["terminate-session", "dispose-session"], session.CleanupEvents);
        Assert.True(manager.Lease.Disposed);
    }

    [Fact]
    public async Task RequiredNativeCommandFailureIsConclusiveAndCleanupStillCompletes()
    {
        using var temp = TempDirectory.Create();
        var session = new RecordingDebugSession(
            processId: 4321,
            commandContextException: new DebugSetupException(
                "The native VBE Run Sub/UserForm command (ID 186) is disabled."));
        var doctor = new DebugEnvironmentDoctor(
            "9.8.7+doctor-test",
            () => true,
            new VbeDebugEnvironmentProbeFactory(
                new RecordingWorkspaceManager(temp.Path),
                new RecordingDebugSessionFactory(session)),
            DebugEnvironmentDoctorDeadlines.Default);

        var report = await doctor.RunAsync(CancellationToken.None);

        Assert.True(report.Complete);
        Assert.Equal(DebugEnvironmentDiagnosticStatus.Fail, report.Status);
        var commandContext = report.Checks.Single(
            check => check.Id == "vbe.commandContext");
        Assert.Equal(DebugEnvironmentDiagnosticStatus.Fail, commandContext.Status);
        Assert.Contains("ID 186", commandContext.Message, StringComparison.Ordinal);
        Assert.Equal(
            DebugEnvironmentDiagnosticStatus.Pass,
            report.Checks.Single(check => check.Id == "excel.processClose").Status);
        Assert.Equal(
            DebugEnvironmentDiagnosticStatus.Pass,
            report.Checks.Single(check => check.Id == "workspace.deletion").Status);
    }

    [Fact]
    public async Task ProductionProbeRunsBreakContinueCompletionAndCleanupInOrder()
    {
        using var temp = TempDirectory.Create();
        string[] codeLines =
        [
            "Option Explicit",
            "Option Private Module",
            "",
            "Public Sub RunDoctorProbe()",
            "    ThisWorkbook.Worksheets(1).Range(\"A1\").Value2 = \"vba-tools-doctor-complete\"",
            "End Sub"
        ];
        var events = new List<string>();
        var breakpointExecutions = 0;
        var runExecutions = 0;
        FakeVbeModel? model = null;
        model = FakeVbeModel.Create(
            Path.Combine(temp.Path, "VbaToolsDoctorProbe.xlsm"),
            events,
            componentName: "VbaToolsDoctorProbe",
            componentLookupName: "VbaToolsDoctorProbe",
            codeLines: codeLines,
            breakpointCommandExecuteAction: () => breakpointExecutions++,
            runCommandExecuteAction: () =>
            {
                runExecutions++;
                if (runExecutions == 1)
                {
                    model!.Project.Mode = 1;
                }
                else
                {
                    model!.Project.Mode = 2;
                    model.Workbook.CompletionMarker = "vba-tools-doctor-complete";
                }
            });
        var process = new FakeDebugOwnedProcess(
            4321,
            new DateTime(2026, 8, 23, 9, 0, 0, DateTimeKind.Local),
            events: events);
        model.Excel.QuitAction = () => process.Exit(0);
        var automation = new VbeDebugAutomation(
            new FakeExcelDebugApplicationFactory(model.Excel),
            new FakeDebugExcelProcessApi(4321, process),
            new FakeDebugWindowActivator(events),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()));
        var manager = new RecordingWorkspaceManager(temp.Path);
        var doctor = new DebugEnvironmentDoctor(
            "9.8.7+doctor-test",
            () => true,
            new VbeDebugEnvironmentProbeFactory(manager, automation),
            DebugEnvironmentDoctorDeadlines.Default);

        var report = await doctor.RunAsync(CancellationToken.None);

        Assert.True(report.Complete);
        Assert.Equal(DebugEnvironmentDiagnosticStatus.Pass, report.Status);
        Assert.All(report.Checks, check => Assert.Equal(
            DebugEnvironmentDiagnosticStatus.Pass,
            check.Status));
        Assert.Equal(2, breakpointExecutions);
        Assert.Equal(2, runExecutions);
        Assert.Equal(
            ["execute:51", "execute:186", "execute:186", "execute:51"],
            events.Where(entry => entry.StartsWith("execute:", StringComparison.Ordinal)));
        Assert.Equal(0, process.KillCalls);
        Assert.True(manager.Lease.Disposed);
    }

    [Fact]
    public async Task CategorizedStartupFailureUsesExplicitCleanupEvidence()
    {
        using var temp = TempDirectory.Create();
        var manager = new RecordingWorkspaceManager(temp.Path);
        var doctor = new DebugEnvironmentDoctor(
            "9.8.7+doctor-test",
            () => true,
            new VbeDebugEnvironmentProbeFactory(
                manager,
                new ThrowingDebugSessionFactory(new VbeDebugSessionStartException(
                    new DebugSetupException("Synthetic owned Excel startup failure."),
                    cleanupException: null,
                    cleanupVerified: true))),
            DebugEnvironmentDoctorDeadlines.Default);

        var report = await doctor.RunAsync(CancellationToken.None);

        Assert.True(report.Complete);
        Assert.Equal(DebugEnvironmentDiagnosticStatus.Fail, report.Status);
        var processClose = report.Checks.Single(
            check => check.Id == "excel.processClose");
        Assert.Equal(DebugEnvironmentDiagnosticStatus.Pass, processClose.Status);
        Assert.Contains("verified", processClose.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(manager.Lease.Disposed);
    }

    private sealed class RecordingWorkspaceManager(string? workspacePath = null)
        : IVbaDebugSessionWorkspaceManager
    {
        public List<string> ClaimedSessionIds { get; } = [];

        public List<string> ReapedExclusions { get; } = [];

        public RecordingWorkspaceLease Lease { get; } = new(workspacePath);

        public ValueTask<IVbaDebugSessionWorkspaceLease> ClaimAsync(
            string sessionId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClaimedSessionIds.Add(sessionId);
            return ValueTask.FromResult<IVbaDebugSessionWorkspaceLease>(Lease);
        }

        public ValueTask<VbaDebugSessionCleanupResult> CleanupAsync(
            string sessionId,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("The owned lease deletes its workspace.");

        public ValueTask<IReadOnlyList<VbaDebugSessionCleanupResult>> ReapStaleAsync(
            string excludedSessionId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReapedExclusions.Add(excludedSessionId);
            return ValueTask.FromResult<IReadOnlyList<VbaDebugSessionCleanupResult>>([]);
        }
    }

    private sealed class RecordingWorkspaceLease(string? workspacePath)
        : IVbaDebugSessionWorkspaceLease
    {
        public string SessionWorkspacePath { get; } =
            workspacePath ?? Path.Combine(Path.GetTempPath(), "vba-tools-doctor-test");

        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingDebugSessionFactory(IVbeDebugSession session)
        : IVbeDebugSessionFactory
    {
        public int Invocations { get; private set; }

        public Task<IVbeDebugSession> StartVisibleAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Invocations++;
            return Task.FromResult(session);
        }
    }

    private sealed class ThrowingDebugSessionFactory(Exception exception)
        : IVbeDebugSessionFactory
    {
        public Task<IVbeDebugSession> StartVisibleAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromException<IVbeDebugSession>(exception);
        }
    }

    private sealed class RecordingDebugSession(
        int processId,
        bool strongProcessOwnershipEstablished = true,
        Exception? commandContextException = null)
        : IVbeDebugSession, IVbeDebugDoctorControl
    {
        private readonly TaskCompletionSource<DebugProcessExit> completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int ProcessId { get; } = processId;

        public bool StrongProcessOwnershipEstablished { get; } =
            strongProcessOwnershipEstablished;

        public string? CreatedFixtureWorkbookPath { get; private set; }

        public string? OpenedFixtureWorkbookPath { get; private set; }

        public string? ImportedFixtureSourcePath { get; private set; }

        public VbeCodeModuleSourceMap? ImportedFixtureSourceMap { get; private set; }

        public VbeBreakpoint? VerifiedCommandBreakpoint { get; private set; }

        public DebugTargetProcedure? VerifiedCommandTarget { get; private set; }

        public IReadOnlyList<VbeBreakpoint> NativeBreakpoints { get; private set; } = [];

        public VbeBreakpoint? ClearedNativeBreakpoint { get; private set; }

        public List<string> ProbeEvents { get; } = [];

        public List<string> CleanupEvents { get; } = [];

        public Task<DebugProcessExit> Completion => completion.Task;

        public Task<DebugCompilationHostFacts> GetCompilationHostFactsAsync(
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task OpenGeneratedWorkbookAsync(
            string workbookPath,
            IDebugInputWaitSink? inputWaitSink,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task SetNativeBreakpointsAsync(
            IReadOnlyList<VbeBreakpoint> breakpoints,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NativeBreakpoints = breakpoints;
            return Task.CompletedTask;
        }

        public Task RunTargetAsync(
            DebugTargetProcedure target,
            IDebugInputWaitSink? inputWaitSink,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProbeEvents.Add($"run:{target.ModuleName}.{target.ProcedureName}");
            return Task.CompletedTask;
        }

        public ValueTask TerminateAsync()
        {
            CleanupEvents.Add("terminate-session");
            completion.TrySetResult(new DebugProcessExit(-1));
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            CleanupEvents.Add("dispose-session");
            return ValueTask.CompletedTask;
        }

        public Task CreateFixtureWorkbookAsync(
            string workbookPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreatedFixtureWorkbookPath = workbookPath;
            return Task.CompletedTask;
        }

        public Task OpenFixtureWorkbookAsync(
            string workbookPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenedFixtureWorkbookPath = workbookPath;
            return Task.CompletedTask;
        }

        public Task ImportFixtureModuleAsync(
            string sourcePath,
            VbeCodeModuleSourceMap sourceMap,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImportedFixtureSourcePath = sourcePath;
            ImportedFixtureSourceMap = sourceMap;
            return Task.CompletedTask;
        }

        public Task VerifyCommandContextAsync(
            VbeBreakpoint breakpoint,
            DebugTargetProcedure target,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (commandContextException is not null)
            {
                return Task.FromException(commandContextException);
            }
            VerifiedCommandBreakpoint = breakpoint;
            VerifiedCommandTarget = target;
            return Task.CompletedTask;
        }

        public Task ClearNativeBreakpointAsync(
            VbeBreakpoint breakpoint,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClearedNativeBreakpoint = breakpoint;
            return Task.CompletedTask;
        }

        public Task CloseOwnedProcessCooperativelyAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CleanupEvents.Add("cooperative-close");
            completion.TrySetResult(new DebugProcessExit(0));
            return Task.CompletedTask;
        }

        public void MarkProcessExited()
            => completion.TrySetResult(new DebugProcessExit(0));

        public Task WaitForBreakModeAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task WaitForBreakModeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProbeEvents.Add("wait-break-mode");
            return Task.CompletedTask;
        }

        public Task ContinueTargetAsync(
            DebugTargetProcedure target,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProbeEvents.Add($"continue:{target.ModuleName}.{target.ProcedureName}");
            return Task.CompletedTask;
        }

        public Task WaitForCompletionAsync(
            string expectedMarker,
            TimeSpan timeout,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task WaitForCompletionAsync(
            string expectedMarker,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProbeEvents.Add($"wait-completion:{expectedMarker}");
            return Task.CompletedTask;
        }
    }
}
