using System.Collections.Immutable;
using VbaDev.App.Debugging;
using VbaDev.App.Diagnostics;
using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Debugging;
using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaDev.Tests;

public sealed class VbeDebugEnvironmentProbeTests
{
    [Fact]
    public async Task RunsBreakpointBreakContinueCompletionAndClearInOrder()
    {
        var events = new List<string>();
        var artifact = new FakeDebugProbeWorkbookArtifact(events);
        var session = new FakeNativeDebugProbeSession(events);
        var factory = new VbeDebugEnvironmentProbeFactory(
            new FakeDebugProbeWorkbookBuilder(artifact, events),
            new FakeNativeDebugProbeSessionFactory(session, events),
            TimeSpan.FromSeconds(2));

        await using var probe = await factory.StartAsync(CancellationToken.None);
        var results = await probe.RunAsync(CancellationToken.None);

        Assert.All(results, result => Assert.Equal(DiagnosticStatus.Pass, result.Status));
        Assert.Equal(
            [
                "build-hidden",
                "start-visible",
                "open-workbook",
                "set-breakpoint",
                "run-target",
                "wait-break",
                "continue",
                "wait-completion",
                "clear-breakpoint"
            ],
            events);
        Assert.Equal(7319, probe.ProcessId);
        Assert.True(probe.StrongProcessOwnershipEstablished);
        Assert.Contains(probe.StartupDiagnostics, result =>
            result.Name == "Temporary macro workbook" &&
            result.Status == DiagnosticStatus.Pass);
    }

    [Fact]
    public async Task ContinueFailureIsStageSpecificAndCleanupOwnsSessionAndArtifact()
    {
        var events = new List<string>();
        var artifact = new FakeDebugProbeWorkbookArtifact(events);
        var session = new FakeNativeDebugProbeSession(
            events,
            continueError: new DebugSetupException(
                "The native VBE Run Sub/UserForm command (ID 186) is disabled in the break mode context."));
        var factory = new VbeDebugEnvironmentProbeFactory(
            new FakeDebugProbeWorkbookBuilder(artifact, events),
            new FakeNativeDebugProbeSessionFactory(session, events),
            TimeSpan.FromSeconds(2));

        var probe = await factory.StartAsync(CancellationToken.None);
        var results = await probe.RunAsync(CancellationToken.None);
        await probe.DisposeAsync();

        Assert.Contains(results, result =>
            result.Name == "Native Continue command (ID 186)" &&
            result.Status == DiagnosticStatus.Fail &&
            result.Message.Contains("disabled", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("wait-completion", events);
        Assert.DoesNotContain("clear-breakpoint", events);
        Assert.Equal("session-dispose", events[^2]);
        Assert.Equal("artifact-dispose", events[^1]);
    }

    [Fact]
    public async Task NativeStageCancellationPropagatesInsteadOfBecomingAStageFailure()
    {
        var events = new List<string>();
        var factory = new VbeDebugEnvironmentProbeFactory(
            new FakeDebugProbeWorkbookBuilder(
                new FakeDebugProbeWorkbookArtifact(events),
                events),
            new FakeNativeDebugProbeSessionFactory(
                new FakeNativeDebugProbeSession(
                    events,
                    continueError: new OperationCanceledException("Synthetic stage cancellation.")),
                events),
            TimeSpan.FromSeconds(2));

        await using var probe = await factory.StartAsync(CancellationToken.None);

        var error = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            probe.RunAsync(CancellationToken.None));

        Assert.Contains("Synthetic stage cancellation", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("wait-completion", events);
    }

    [Fact]
    public void WorkbookBuilderMapsTheExportedAssignmentAndClosesHiddenExcelBeforeReturning()
    {
        var events = new List<string>();
        var automation = new FakeDebugProbeWorkbookAutomation(events);
        var builder = new ExcelComDebugProbeWorkbookBuilder(
            automation,
            new BreakpointSourceMapper());

        var artifact = builder.Build(CancellationToken.None);
        var directoryPath = Path.GetDirectoryName(artifact.WorkbookPath)!;
        try
        {
            Assert.Equal(["create-hidden", "import-module", "save-workbook", "hidden-dispose"], events);
            Assert.Contains("Option Private Module", automation.ImportedSource, StringComparison.Ordinal);
            Assert.Contains("vba-tools-doctor-complete", artifact.Breakpoint.ExpectedCodeLine, StringComparison.Ordinal);
            Assert.Equal("VbaToolsDoctorProbe", artifact.Breakpoint.ModuleName);
            Assert.Equal("RunDoctorProbe", artifact.Target.ProcedureName);
        }
        finally
        {
            artifact.Dispose();
        }

        Assert.False(Directory.Exists(directoryPath));
    }

    [Fact]
    public void SourceMapFailureRemovesTheTemporaryWorkbookAndSourceDirectory()
    {
        var automation = new FakeDebugProbeWorkbookAutomation([]);
        var builder = new ExcelComDebugProbeWorkbookBuilder(
            automation,
            new ThrowingBreakpointSourceMapper());

        var error = Assert.Throws<DebugEnvironmentProbeStartException>(() =>
            builder.Build(CancellationToken.None));

        Assert.Equal("Native breakpoint source map", error.DiagnosticName);
        Assert.NotNull(automation.WorkbookPath);
        Assert.False(Directory.Exists(Path.GetDirectoryName(automation.WorkbookPath)!));
    }

    [Fact]
    public void WorkbookBuilderCancellationRemovesTheTemporaryWorkbookAndSourceDirectory()
    {
        using var cancellationSource = new CancellationTokenSource();
        var automation = new CancelingDebugProbeWorkbookAutomation(cancellationSource);
        var builder = new ExcelComDebugProbeWorkbookBuilder(
            automation,
            new BreakpointSourceMapper());

        var error = Assert.Throws<DebugEnvironmentProbeStartException>(() =>
            builder.Build(cancellationSource.Token));

        Assert.IsType<OperationCanceledException>(error.InnerException);
        Assert.True(error.CleanupVerified);
        Assert.NotNull(automation.WorkbookPath);
        Assert.False(Directory.Exists(Path.GetDirectoryName(automation.WorkbookPath)!));
    }

    [Fact]
    public void VisibleExcelStartTimeoutReportsTimeoutAndVerifiedArtifactCleanup()
    {
        var events = new List<string>();
        var diagnostic = new DebugEnvironmentDiagnostic(
            new VbeDebugEnvironmentProbeFactory(
                new FakeDebugProbeWorkbookBuilder(
                    new FakeDebugProbeWorkbookArtifact(events),
                    events),
                new CancelingNativeDebugProbeSessionFactory(events),
                TimeSpan.FromSeconds(2)),
            () => true,
            TimeSpan.FromMilliseconds(50));

        var results = diagnostic.RunEnvironmentDiagnostics();

        Assert.Contains(results, result =>
            result.Name == "Excel COM availability" &&
            result.Status == DiagnosticStatus.Fail &&
            result.Message.Contains("exceeded", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(results, result =>
            result.Name == "Temporary debug probe cleanup" &&
            result.Status == DiagnosticStatus.Pass);
        Assert.Equal(["build-hidden", "start-visible", "artifact-dispose"], events);
    }

    [Fact]
    public void VisibleExcelStartTimeoutReportsArtifactCleanupFailureSeparately()
    {
        var events = new List<string>();
        var diagnostic = new DebugEnvironmentDiagnostic(
            new VbeDebugEnvironmentProbeFactory(
                new FakeDebugProbeWorkbookBuilder(
                    new FakeDebugProbeWorkbookArtifact(
                        events,
                        new IOException("Synthetic artifact cleanup failure.")),
                    events),
                new CancelingNativeDebugProbeSessionFactory(events),
                TimeSpan.FromSeconds(2)),
            () => true,
            TimeSpan.FromMilliseconds(50));

        var results = diagnostic.RunEnvironmentDiagnostics();

        Assert.Contains(results, result =>
            result.Name == "Excel COM availability" &&
            result.Status == DiagnosticStatus.Fail &&
            result.Message.Contains("exceeded", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(results, result =>
            result.Name == "Temporary debug probe cleanup" &&
            result.Status == DiagnosticStatus.Fail &&
            result.Message.Contains("Synthetic artifact cleanup failure", StringComparison.Ordinal));
        Assert.Equal(["build-hidden", "start-visible", "artifact-dispose"], events);
    }

    [Fact]
    public async Task VisibleExcelStartFailureWithoutCleanupEvidenceIsNotVerified()
    {
        var events = new List<string>();
        var factory = new VbeDebugEnvironmentProbeFactory(
            new FakeDebugProbeWorkbookBuilder(
                new FakeDebugProbeWorkbookArtifact(events),
                events),
            new ThrowingNativeDebugProbeSessionFactory(
                events,
                new DebugSetupException("Synthetic visible start failure.")),
            TimeSpan.FromSeconds(2));

        var error = await Assert.ThrowsAsync<DebugEnvironmentProbeStartException>(() =>
            factory.StartAsync(CancellationToken.None));

        Assert.False(error.CleanupVerified);
        Assert.Null(error.CleanupException);
        Assert.Equal(["build-hidden", "start-visible", "artifact-dispose"], events);
    }

    private sealed class FakeDebugProbeWorkbookBuilder(
        IDebugProbeWorkbookArtifact artifact,
        List<string> events) : IDebugProbeWorkbookBuilder
    {
        public IDebugProbeWorkbookArtifact Build(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("build-hidden");
            return artifact;
        }
    }

    private sealed class FakeDebugProbeWorkbookAutomation(List<string> events)
        : IDebugProbeWorkbookAutomation
    {
        public string? WorkbookPath { get; private set; }

        public string ImportedSource { get; private set; } = string.Empty;

        public IWorkbookBuildSession CreateMacroEnabledWorkbook(
            string workbookPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("create-hidden");
            WorkbookPath = workbookPath;
            File.WriteAllText(workbookPath, "fake xlsm");
            return new FakeDebugProbeWorkbookBuildSession(this, events);
        }

        private sealed class FakeDebugProbeWorkbookBuildSession(
            FakeDebugProbeWorkbookAutomation owner,
            List<string> events) : IWorkbookBuildSession
        {
            public IReadOnlyList<WorkbookModule> GetModules() => [];

            public IReadOnlyList<WorkbookReference> GetReferences() => [];

            public bool RemoveReference(string referenceName) => false;

            public void AddReference(ResolvedVbaProjectReference reference)
                => throw new NotSupportedException();

            public void RemoveModule(string moduleName) => throw new NotSupportedException();

            public void ImportModule(VbaSourceFile sourceFile)
            {
                events.Add("import-module");
                owner.ImportedSource = File.ReadAllText(sourceFile.SourcePath);
            }

            public void Save() => events.Add("save-workbook");

            public void Dispose() => events.Add("hidden-dispose");
        }
    }

    private sealed class CancelingDebugProbeWorkbookAutomation(
        CancellationTokenSource cancellationSource) : IDebugProbeWorkbookAutomation
    {
        public string? WorkbookPath { get; private set; }

        public IWorkbookBuildSession CreateMacroEnabledWorkbook(
            string workbookPath,
            CancellationToken cancellationToken)
        {
            WorkbookPath = workbookPath;
            File.WriteAllText(workbookPath, "fake xlsm");
            cancellationSource.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Cancellation should have interrupted the build.");
        }
    }

    private sealed class ThrowingBreakpointSourceMapper : IBreakpointSourceMapper
    {
        public VbeBreakpoint Map(
            DebugSourceSnapshot snapshot,
            DebugSourceBreakpoint breakpoint)
            => throw new DebugSetupException("Synthetic source-map failure.");
    }

    private sealed class FakeDebugProbeWorkbookArtifact(
        List<string> events,
        Exception? disposeError = null)
        : IDebugProbeWorkbookArtifact
    {
        private static readonly VbeCodeModuleSourceMap SourceMap = new(
            "VbaToolsDoctorProbe",
            VbaModuleKind.StandardModule,
            ImmutableArray.Create(
                "Option Explicit",
                "Option Private Module",
                "Public Sub RunDoctorProbe()",
                "    ThisWorkbook.Worksheets(1).Range(\"A1\").Value2 = \"vba-tools-doctor-complete\"",
                "End Sub"));

        public string WorkbookPath => "C:\\Temp\\vba-tools-doctor\\DoctorProbe.xlsm";

        public DebugTargetProcedure Target => new("VbaToolsDoctorProbe", "RunDoctorProbe");

        public VbeBreakpoint Breakpoint => new(
            new DebugSourceBreakpoint("C:\\Temp\\vba-tools-doctor\\VbaToolsDoctorProbe.bas", 4),
            SourceMap,
            4);

        public string CompletionMarker => "vba-tools-doctor-complete";

        public IReadOnlyList<DiagnosticResult> StartupDiagnostics =>
            [DiagnosticResult.Pass("Temporary macro workbook", "Created in hidden owned Excel.")];

        public void Dispose()
        {
            events.Add("artifact-dispose");
            if (disposeError is not null)
            {
                throw disposeError;
            }
        }
    }

    private sealed class FakeNativeDebugProbeSessionFactory(
        IVbeDebugSession session,
        List<string> events) : IVbeDebugSessionFactory
    {
        public Task<IVbeDebugSession> StartVisibleAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("start-visible");
            return Task.FromResult(session);
        }
    }

    private sealed class CancelingNativeDebugProbeSessionFactory(List<string> events)
        : IVbeDebugSessionFactory
    {
        public async Task<IVbeDebugSession> StartVisibleAsync(
            CancellationToken cancellationToken)
        {
            events.Add("start-visible");
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Cancellation should have interrupted startup.");
            }
            catch (OperationCanceledException ex)
            {
                throw new VbeDebugSessionStartCanceledException(
                    ex,
                    cleanupException: null,
                    cleanupVerified: true);
            }
        }
    }

    private sealed class ThrowingNativeDebugProbeSessionFactory(
        List<string> events,
        Exception error) : IVbeDebugSessionFactory
    {
        public Task<IVbeDebugSession> StartVisibleAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("start-visible");
            return Task.FromException<IVbeDebugSession>(error);
        }
    }

    private sealed class FakeNativeDebugProbeSession(
        List<string> events,
        Exception? continueError = null) : IVbeDebugSession, IVbeDebugProbeControl
    {
        private int breakpointCalls;

        public int ProcessId => 7319;

        public Task<DebugProcessExit> Completion => Task.FromResult(new DebugProcessExit(0));

        public bool StrongProcessOwnershipEstablished => true;

        public Task<DebugCompilationHostFacts> GetCompilationHostFactsAsync(
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task OpenGeneratedWorkbookAsync(
            string workbookPath,
            IDebugInputWaitSink? inputWaitSink,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("open-workbook");
            return Task.CompletedTask;
        }

        public Task SetNativeBreakpointsAsync(
            IReadOnlyList<VbeBreakpoint> breakpoints,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add(++breakpointCalls == 1 ? "set-breakpoint" : "clear-breakpoint");
            return Task.CompletedTask;
        }

        public Task RunTargetAsync(
            DebugTargetProcedure target,
            IDebugInputWaitSink? inputWaitSink,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("run-target");
            return Task.CompletedTask;
        }

        public Task WaitForBreakModeAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("wait-break");
            return Task.CompletedTask;
        }

        public Task ContinueTargetAsync(
            DebugTargetProcedure target,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("continue");
            return continueError is null
                ? Task.CompletedTask
                : Task.FromException(continueError);
        }

        public Task WaitForCompletionAsync(
            string expectedMarker,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("wait-completion");
            return Task.CompletedTask;
        }

        public ValueTask TerminateAsync() => ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            events.Add("session-dispose");
            return ValueTask.CompletedTask;
        }
    }
}
