using VbaDebugAdapter.Debugging;
using VbaDebugAdapter.Infrastructure;
using System.Collections.Immutable;
using VbaTools.Syntax;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class SourceVbeDebugAutomationTests
{
    [Fact]
    public async Task LateReferenceReleaseAfterBoundedDetachCannotWriteIntoSealedOwnerEvidence()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "Source.xlsm");
        File.WriteAllText(path, "source fixture");
        var model = FakeVbeModel.Create(path, []);
        var cleanup = new InvalidOperationException("Late source project reference release failed.");
        using var continueInspection = new ManualResetEventSlim();
        var workbook = new BlockingInspectionWorkbook(path, model.Project, continueInspection);
        var binding = new SourceVbeDesktopBinding(model.Excel, workbook, path,
            12345, 638000000000000000, true, () => true, () => { });
        var dispatcher = new StaComDispatcher();
        var automation = new SourceVbeDebugAutomation(new FakeSourceVbeDesktopConnector(binding),
            new FakeStaComDispatcherFactory(dispatcher), new FakeDebugWindowActivator(),
            releaseReference: value => ReferenceEquals(value, model.Project) ? throw cleanup : true);
        var session = await automation.AttachOrOpenAsync(path, CancellationToken.None);
        var inspecting = session.InspectAsync(CancellationToken.None);
        try
        {
            await workbook.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var detachFailure = await Assert.ThrowsAnyAsync<Exception>(() =>
                session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15)));
            var retained = Assert.IsAssignableFrom<IDebugFailureEvidence>(detachFailure).FailureOutcome;
            Assert.True(retained.HasUnprovedRelease);
            continueInspection.Set();

            var lateFailure = await Assert.ThrowsAnyAsync<Exception>(() => inspecting.WaitAsync(TimeSpan.FromSeconds(3)));
            var lateOutcome = Assert.IsAssignableFrom<IDebugFailureEvidence>(lateFailure).FailureOutcome;
            Assert.Contains(lateOutcome.CleanupFailures, item => ReferenceEquals(item.Exception, cleanup));
            Assert.DoesNotContain("already retained", lateFailure.Message);
            Assert.Same(retained, Assert.IsAssignableFrom<IDebugResourceOwnerEvidence>(session).CleanupOutcome);
            Assert.True(retained.HasUnprovedRelease);
        }
        finally
        {
            continueInspection.Set();
            await dispatcher.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InspectionOrResetFailureRemainsPrimaryWhenItsAcquiredReferenceReleaseAlsoFails(bool inspection)
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "Source.xlsm");
        File.WriteAllText(path, "source fixture");
        var model = FakeVbeModel.Create(path, []);
        var primary = new InvalidOperationException("The original native operation failed.");
        var cleanup = new InvalidOperationException("Source VBA project reference release failed.");
        var operationFailed = inspection;
        object application = model.Excel;
        object workbook = new FailingInspectionWorkbook(path, model.Project, primary);
        if (!inspection)
        {
            model.Project.Mode = 1;
            var reset = new FakeCommandBarControl(228, []) { ExecuteAction = () => { operationFailed = true; throw primary; } };
            var resetApplication = new ResetApplication(model.Excel, reset);
            resetApplication.VBE.ActiveVBProject = model.Project;
            application = resetApplication;
            workbook = model.Workbook;
        }
        var binding = new SourceVbeDesktopBinding(application, workbook, path,
            12345, 638000000000000000, true, () => true, () => { });
        var automation = new SourceVbeDebugAutomation(new FakeSourceVbeDesktopConnector(binding),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()), new FakeDebugWindowActivator(),
            releaseReference: value => operationFailed && ReferenceEquals(value, model.Project) ? throw cleanup : true);
        var session = await automation.AttachOrOpenAsync(path, CancellationToken.None);
        try
        {
            var failure = await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                if (inspection) await session.InspectAsync(CancellationToken.None);
                else await session.ResetExecutionAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
            });
            var outcome = Assert.IsAssignableFrom<IDebugFailureEvidence>(failure).FailureOutcome;
            Assert.Same(primary, outcome.PrimaryFailure);
            Assert.Contains(outcome.CleanupFailures, item => ReferenceEquals(item.Exception, cleanup));
            Assert.True(outcome.HasUnprovedRelease);
        }
        finally
        {
            try { await session.DisposeAsync(); }
            catch (Exception failure) when (failure is IDebugFailureEvidence) { }
        }
    }

    [Fact]
    public async Task CanceledAcquisitionRetainsLateConnectorEvidenceWithoutUnwrappingTheCarrier()
    {
        var primary = new InvalidOperationException("Late connector acquisition failed.");
        var result = new DebugFailureCompletion(primary);
        result.AddEvidence(new("connector partial release", "connector book reference", DebugResourceKind.Com, true,
            "The connector released its acquired book reference."));
        var connector = new PausedFailureSourceConnector(new DebugFailureException(result.Complete()));
        var dispatcher = new NotifyingSourceStaDispatcher();
        var automation = new SourceVbeDebugAutomation(connector,
            new FakeStaComDispatcherFactory(dispatcher), new FakeDebugWindowActivator());
        using var cancellation = new CancellationTokenSource();
        var starting = automation.AttachOrOpenAsync("C:\\Source.xlsm", cancellation.Token);
        try
        {
            await connector.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            cancellation.Cancel();
            await dispatcher.ReleaseQueued.Task.WaitAsync(TimeSpan.FromSeconds(1));
            connector.Continue.Set();

            var failure = await Assert.ThrowsAnyAsync<Exception>(() => starting.WaitAsync(TimeSpan.FromSeconds(3)));

            var retained = Assert.IsAssignableFrom<IDebugFailureEvidence>(failure).FailureOutcome;
            Assert.IsAssignableFrom<OperationCanceledException>(retained.PrimaryFailure);
            Assert.False(retained.HasUnprovedRelease);
            Assert.Contains(retained.Evidence, item => item.Resource == "connector book reference" && item.Released);
            Assert.Contains(retained.CleanupFailures, item => ReferenceEquals(item.Exception, primary));
        }
        finally { connector.Continue.Set(); connector.Continue.Dispose(); }
    }

    [Fact]
    public async Task ProbeReferenceReleaseFailureRemainsVisibleOnCompletionAndFinalOwnerEvidence()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "Source.xlsm");
        File.WriteAllText(path, "source fixture");
        var model = FakeVbeModel.Create(path, []);
        var primary = new InvalidOperationException("Exact source observation failed.");
        var cleanup = new InvalidOperationException("Probe workbook Item reference release failed.");
        var result = new DebugFailureCompletion(primary);
        result.AddFailure("source probe release", "probe workbook Item", DebugResourceKind.Com, cleanup, 12345, path);
        result.AddEvidence(new("source probe release", "probe workbook Item", DebugResourceKind.Com, false,
            "The probe reference release is unproved.", 12345, path));
        var carrier = new DebugFailureException(result.Complete());
        var binding = new SourceVbeDesktopBinding(model.Excel, model.Workbook, path,
            12345, 638000000000000000, true, () => true, () => { }, completionProbe: () => throw carrier);
        var automation = new SourceVbeDebugAutomation(new FakeSourceVbeDesktopConnector(binding),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()), new FakeDebugWindowActivator());
        var session = await automation.AttachOrOpenAsync(path, CancellationToken.None);
        try
        {
            var failure = await Assert.ThrowsAnyAsync<Exception>(() => session.Completion.WaitAsync(TimeSpan.FromSeconds(1)));
            var retained = Assert.IsAssignableFrom<IDebugFailureEvidence>(failure).FailureOutcome;
            Assert.Same(primary, retained.PrimaryFailure);
            Assert.Contains("Use Reset", failure.Message);
        }
        finally
        {
            try { await session.DisposeAsync(); }
            catch (Exception failure) when (failure is IDebugFailureEvidence) { }
        }
        var outcome = Assert.IsAssignableFrom<IDebugResourceOwnerEvidence>(session).CleanupOutcome;
        Assert.NotNull(outcome);
        Assert.True(outcome.HasUnprovedRelease);
        Assert.Contains(outcome.Evidence, item => item.Resource == "probe workbook Item" && !item.Released);
        Assert.Contains(outcome.CleanupFailures, item => ReferenceEquals(item.Exception, cleanup));
    }

    [Fact]
    public void SourceInputGuidanceDoesNotClaimOwnershipOfTheExcelProcess()
    {
        var wait = new DebugInputWait(DebugInputWaitKind.ExcelOrVbe,
            DebugInputWaitPhase.TargetStart, 12345);

        var message = wait.ToSourceLifecycleMessage();

        Assert.Contains("Source Excel process 12345 is waiting for", message.Output);
        Assert.DoesNotContain("Owned", message.Output);
        Assert.Contains("starting the debug target", message.Output);
    }

    [Fact]
    public async Task SourceModalBaselineIncludesTheVbeContextBeforeNativeRunButStillReportsANewPrompt()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "Source.xlsm");
        File.WriteAllText(path, "source fixture");
        var model = FakeVbeModel.Create(path, []);
        var windows = new ContextAwareSourceModalWindowApi(() => model.Excel.VBE!.MainWindow.Visible);
        var sink = new SourceInputWaitSink();
        var binding = new SourceVbeDesktopBinding(model.Excel, model.Workbook, path,
            12345, 638000000000000000, true, () => true, () => { });
        var automation = new SourceVbeDebugAutomation(new FakeSourceVbeDesktopConnector(binding),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()), new FakeDebugWindowActivator(),
            new DebugModalPromptMonitor(windows));
        await using var session = await automation.AttachOrOpenAsync(path, CancellationToken.None);

        await session.RunTargetAsync(new DebugTargetProcedure("DebugModule", "RunProbe"), sink, CancellationToken.None);
        await windows.OrdinaryObservationCompleted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(0, sink.Count);

        windows.ShowPrompt();
        Assert.Equal(12345, (await sink.Notified.Task.WaitAsync(TimeSpan.FromSeconds(1))).ProcessId);
        Assert.Equal(1, sink.Count);
        Assert.False(session.Completion.IsCompleted);
    }

    [Fact]
    public async Task SourceNativeRunContinuesObservingNewModalAfterTheCommandReturns()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "Source.xlsm");
        File.WriteAllText(path, "source fixture");
        var model = FakeVbeModel.Create(path, []);
        var windows = new SourceModalWindowApi();
        var sink = new SourceInputWaitSink();
        var binding = new SourceVbeDesktopBinding(model.Excel, model.Workbook, path,
            12345, 638000000000000000, true, () => true, () => { });
        var automation = new SourceVbeDebugAutomation(new FakeSourceVbeDesktopConnector(binding),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()), new FakeDebugWindowActivator(),
            new DebugModalPromptMonitor(windows));
        await using var session = await automation.AttachOrOpenAsync(path, CancellationToken.None);
        await session.RunTargetAsync(new DebugTargetProcedure("DebugModule", "RunProbe"), sink, CancellationToken.None);

        windows.ShowModal();
        var wait = await sink.Notified.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(12345, wait.ProcessId);
        Assert.Equal(DebugInputWaitPhase.TargetStart, wait.Phase);
        Assert.False(session.Completion.IsCompleted);
        Assert.All(windows.ProcessIds, processId => Assert.Equal(12345, processId));
    }

    [Fact]
    public async Task SourceModalObservationFailureFaultsTheSessionWithManualResetGuidance()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "Source.xlsm");
        File.WriteAllText(path, "source fixture");
        var model = FakeVbeModel.Create(path, []);
        var windows = new SourceModalWindowApi();
        var primary = new InvalidOperationException("Native source modal observation failed.");
        var binding = new SourceVbeDesktopBinding(model.Excel, model.Workbook, path,
            12345, 638000000000000000, true, () => true, () => { });
        var automation = new SourceVbeDebugAutomation(new FakeSourceVbeDesktopConnector(binding),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()), new FakeDebugWindowActivator(),
            new DebugModalPromptMonitor(windows));
        var session = await automation.AttachOrOpenAsync(path, CancellationToken.None);
        try
        {
            await session.RunTargetAsync(new DebugTargetProcedure("DebugModule", "RunProbe"),
                new SourceInputWaitSink(), CancellationToken.None);
            windows.Fail(primary);

            var failure = await Assert.ThrowsAsync<DebugSetupException>(() =>
                session.Completion.WaitAsync(TimeSpan.FromSeconds(1)));

            Assert.Contains("Use Reset", failure.Message);
            Assert.Same(primary, failure.InnerException);
        }
        finally
        {
            try { await session.DisposeAsync(); }
            catch (Exception failure) when (failure is IDebugFailureEvidence) { }
        }
    }

    [Fact]
    public async Task ResetThenRunOnTheSameNativeSourceCreatesAFreshModalObservationGeneration()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "Source.xlsm");
        File.WriteAllText(path, "source fixture");
        var model = FakeVbeModel.Create(path, []);
        var windows = new SourceModalWindowApi();
        var binding = new SourceVbeDesktopBinding(model.Excel, model.Workbook, path,
            12345, 638000000000000000, true, () => true, () => { });
        var automation = new SourceVbeDebugAutomation(new FakeSourceVbeDesktopConnector(binding),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()), new FakeDebugWindowActivator(),
            new DebugModalPromptMonitor(windows));
        await using var session = await automation.AttachOrOpenAsync(path, CancellationToken.None);
        await session.RunTargetAsync(new DebugTargetProcedure("DebugModule", "RunProbe"),
            new SourceInputWaitSink(), CancellationToken.None);
        await session.ResetExecutionAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        var nextSink = new SourceInputWaitSink();

        await session.RunTargetAsync(new DebugTargetProcedure("DebugModule", "RunProbe"), nextSink, CancellationToken.None);
        windows.ShowModal();
        Assert.Equal(12345, (await nextSink.Notified.Task.WaitAsync(TimeSpan.FromSeconds(1))).ProcessId);
        Assert.False(session.Completion.IsCompleted);
    }

    [Fact]
    public async Task SourceModalPhaseCleanupFailureIsRetainedWithoutClaimingObserverRelease()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "Source.xlsm");
        File.WriteAllText(path, "source fixture");
        var model = FakeVbeModel.Create(path, []);
        var primary = new InvalidOperationException("Modal phase cleanup failed.");
        var binding = new SourceVbeDesktopBinding(model.Excel, model.Workbook, path,
            12345, 638000000000000000, true, () => true, () => { });
        var automation = new SourceVbeDebugAutomation(new FakeSourceVbeDesktopConnector(binding),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()), new FakeDebugWindowActivator(),
            new CleanupFailureSourceModalMonitor(primary));
        var session = await automation.AttachOrOpenAsync(path, CancellationToken.None);
        await session.RunTargetAsync(new DebugTargetProcedure("DebugModule", "RunProbe"),
            new SourceInputWaitSink(), CancellationToken.None);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => session.DisposeAsync().AsTask());

        var outcome = Assert.IsAssignableFrom<IDebugFailureEvidence>(failure).FailureOutcome;
        Assert.Contains(outcome.CleanupFailures, item => ReferenceEquals(item.Exception, primary));
        Assert.Contains(outcome.Evidence, item => item.Resource == "source modal observer" && !item.Released);
        Assert.True(outcome.HasUnprovedRelease);
    }

    [Fact]
    public async Task UnknownConnectorAcquisitionFailureRetainsComUncertaintyAndActualStaReleaseEvidence()
    {
        var primary = new InvalidOperationException("Binding failed after unknown acquisition.");
        var automation = new SourceVbeDebugAutomation(new FailingSourceVbeDesktopConnector(primary),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()), new FakeDebugWindowActivator());

        var failure = await Assert.ThrowsAnyAsync<Exception>(() =>
            automation.AttachOrOpenAsync("C:\\Source.xlsm", CancellationToken.None));

        var outcome = Assert.IsAssignableFrom<IDebugFailureEvidence>(failure).FailureOutcome;
        Assert.Same(primary, outcome.PrimaryFailure);
        Assert.True(outcome.HasUnprovedRelease);
        Assert.Contains(outcome.Evidence, item => item.Kind == DebugResourceKind.Com && !item.Released);
        Assert.Contains(outcome.Evidence, item => item.Kind == DebugResourceKind.Handle && item.Released
            && item.Resource == "source COM dispatcher");
    }

    [Fact]
    public async Task AnUnexpectedObservationFaultIsNotReportedAsSuccessfulDetach()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "Source.xlsm");
        File.WriteAllText(path, "source fixture");
        var model = FakeVbeModel.Create(path, []);
        var binding = new SourceVbeDesktopBinding(model.Excel, model.Workbook, path,
            12345, 638000000000000000, true, () => true, () => { },
            completionProbe: () => throw new InvalidOperationException("Live inventory unavailable."));
        var automation = new SourceVbeDebugAutomation(new FakeSourceVbeDesktopConnector(binding),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()), new FakeDebugWindowActivator());
        await using var session = await automation.AttachOrOpenAsync(path, CancellationToken.None);

        var failure = await Assert.ThrowsAsync<DebugSetupException>(() =>
            session.Completion.WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.Contains("Use Reset", failure.Message);
        Assert.IsType<InvalidOperationException>(failure.InnerException);
    }

    [Fact]
    public async Task CompilationBuiltInsComeFromTheVerifiedLiveExcelProcessNotSavedWorkbookBytes()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "Source.xlsm");
        File.WriteAllText(path, "not a saved VBA package");
        var model = FakeVbeModel.Create(path, []);
        model.Workbook.Saved = false;
        var binding = new SourceVbeDesktopBinding(model.Excel, model.Workbook, path,
            12345, 638000000000000000, true, () => true, () => { }, DebugExcelProcessArchitecture.X64);
        var automation = new SourceVbeDebugAutomation(new FakeSourceVbeDesktopConnector(binding),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()), new FakeDebugWindowActivator());
        await using var session = await automation.AttachOrOpenAsync(path, CancellationToken.None);

        var facts = await session.GetCompilationHostFactsAsync(CancellationToken.None);

        Assert.Equal(DebugCompilationHostFactsStatus.Verified, facts.Status);
        Assert.NotNull(facts.BuiltInConstants);
        Assert.True(facts.BuiltInConstants.Vba7);
        Assert.True(facts.BuiltInConstants.Win64);
        Assert.Equal("16.0", facts.ExcelVersion);
        Assert.False(model.Workbook.Saved);
    }

    [Fact]
    public async Task X86ExcelOnX64WindowsIsVerifiedWithWin64False()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "Source.xlsm");
        File.WriteAllText(path, "not a saved VBA package");
        var model = FakeVbeModel.Create(path, []);
        var binding = new SourceVbeDesktopBinding(model.Excel, model.Workbook, path,
            12345, 638000000000000000, true, () => true, () => { }, DebugExcelProcessArchitecture.X86);
        var automation = new SourceVbeDebugAutomation(new FakeSourceVbeDesktopConnector(binding),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()), new FakeDebugWindowActivator());
        await using var session = await automation.AttachOrOpenAsync(path, CancellationToken.None);

        var facts = await session.GetCompilationHostFactsAsync(CancellationToken.None);

        Assert.Equal(DebugCompilationHostFactsStatus.Verified, facts.Status);
        Assert.NotNull(facts.BuiltInConstants);
        Assert.True(facts.BuiltInConstants.Win32);
        Assert.False(facts.BuiltInConstants.Win64);
        Assert.True(facts.BuiltInConstants.Vba7);
    }

    [Fact]
    public async Task DetachIsIdempotentAndProvesReferenceReleaseWithoutClaimingExcelExit()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "Source.xlsm");
        File.WriteAllText(path, "source fixture");
        var events = new List<string>();
        var model = FakeVbeModel.Create(path, events);
        var binding = new SourceVbeDesktopBinding(model.Excel, model.Workbook, path,
            12345, 638000000000000000, true, () => true, () => events.Add("binding-released"));
        var automation = new SourceVbeDebugAutomation(new FakeSourceVbeDesktopConnector(binding),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()), new FakeDebugWindowActivator());
        var session = await automation.AttachOrOpenAsync(path, CancellationToken.None);

        await session.DisposeAsync();
        await session.DisposeAsync();

        var outcome = Assert.IsAssignableFrom<IDebugResourceOwnerEvidence>(session).CleanupOutcome;
        Assert.NotNull(outcome);
        Assert.False(outcome.HasUnprovedRelease);
        Assert.Contains(outcome.Evidence, item => item.Kind == DebugResourceKind.Process && item.Released
            && item.Reason.Contains("no lifetime ownership", StringComparison.Ordinal));
        Assert.Contains(outcome.Evidence, item => item.Kind == DebugResourceKind.Com && item.Released);
        Assert.Contains(outcome.Evidence, item => item.Kind == DebugResourceKind.Handle && item.Released);
        Assert.Single(events, item => item == "binding-released");
        Assert.Null((await session.Completion).ProcessExitCode);
    }

    [Fact]
    public async Task StopDoesNotResetAnotherActiveProjectOrPretendItOwnsThatExecution()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "Source.xlsm");
        File.WriteAllText(path, "source fixture");
        var events = new List<string>();
        var model = FakeVbeModel.Create(path, events);
        model.Project.Mode = 1;
        var reset = new FakeCommandBarControl(228, events) { ExecuteAction = () => model.Project.Mode = 2 };
        var application = new ResetApplication(model.Excel, reset);
        var otherProject = new object();
        application.VBE.ActiveVBProject = otherProject;
        var binding = new SourceVbeDesktopBinding(application, model.Workbook, path,
            12345, 638000000000000000, true, () => true, () => { });
        var automation = new SourceVbeDebugAutomation(new FakeSourceVbeDesktopConnector(binding),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()), new FakeDebugWindowActivator());
        await using var session = await automation.AttachOrOpenAsync(path, CancellationToken.None);

        var failure = await Assert.ThrowsAsync<DebugSetupException>(() =>
            session.ResetExecutionAsync(TimeSpan.FromSeconds(1), CancellationToken.None));

        Assert.Contains("Use Reset", failure.Message);
        Assert.Same(otherProject, application.VBE.ActiveVBProject);
        Assert.DoesNotContain("execute:228", events);
        Assert.Equal(1, model.Project.Mode);
    }

    [Fact]
    public async Task OnlyAnActualCloseEndsTheSourceSessionAndDoesNotInventAProcessExitCode()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "Source.xlsm");
        File.WriteAllText(path, "source fixture");
        var model = FakeVbeModel.Create(path, []);
        SourceVbeDebugSessionCompletion? observed = null;
        var binding = new SourceVbeDesktopBinding(model.Excel, model.Workbook, path,
            12345, 638000000000000000, true, () => true, () => { },
            completionProbe: () => observed);
        var automation = new SourceVbeDebugAutomation(new FakeSourceVbeDesktopConnector(binding),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()), new FakeDebugWindowActivator());
        await using var session = await automation.AttachOrOpenAsync(path, CancellationToken.None);

        // A canceled BeforeClose still leaves the workbook in the live inventory.
        await Task.Delay(150);
        Assert.False(session.Completion.IsCompleted);
        observed = new(SourceVbeDebugSessionEndReason.WorkbookClosed, null);
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(SourceVbeDebugSessionEndReason.WorkbookClosed, result.Reason);
        Assert.Null(result.ProcessExitCode);
    }

    [Fact]
    public async Task BreakpointIsPlacedAtTheExactVerifiedSourceLine()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "Source.xlsm");
        File.WriteAllText(path, "source fixture");
        var events = new List<string>();
        var lines = new[] { "Option Explicit", "Public Sub Probe()", "    Debug.Print 1", "End Sub" };
        var model = FakeVbeModel.Create(path, events, codeLines: lines);
        var binding = new SourceVbeDesktopBinding(model.Excel, model.Workbook, path,
            12345, 638000000000000000, true, () => true, () => { });
        var automation = new SourceVbeDebugAutomation(new FakeSourceVbeDesktopConnector(binding),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()),
            new FakeDebugWindowActivator(events));
        await using var session = await automation.AttachOrOpenAsync(path, CancellationToken.None);
        var breakpoint = new VbeBreakpoint(new DebugSourceBreakpoint(new Uri(path).AbsoluteUri, 2),
            new VbeCodeModuleSourceMap("DebugModule", VbaModuleKind.StandardModule, lines.ToImmutableArray()), 3);

        await session.SetNativeBreakpointsAsync([breakpoint], CancellationToken.None);

        Assert.Contains("execute:51", events);
        Assert.Contains("selection:3:1:3:1", events);
    }

    [Fact]
    public async Task TargetUsesNativeRunInTheExactWorkbookWithoutSavingOrQuitting()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "Source.xlsm");
        File.WriteAllText(path, "source fixture");
        var events = new List<string>();
        var model = FakeVbeModel.Create(path, events);
        var binding = new SourceVbeDesktopBinding(model.Excel, model.Workbook, path,
            12345, 638000000000000000, true, () => true, () => { });
        var automation = new SourceVbeDebugAutomation(new FakeSourceVbeDesktopConnector(binding),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()),
            new FakeDebugWindowActivator(events));
        await using var session = await automation.AttachOrOpenAsync(path, CancellationToken.None);

        await session.RunTargetAsync(new DebugTargetProcedure("DebugModule", "RunProbe"),
            null, CancellationToken.None);

        Assert.Contains("execute:186", events);
        Assert.Contains("selection:7:1:7:1", events);
        Assert.DoesNotContain("excel-quit", events);
        Assert.DoesNotContain("close:False", events);
    }

    [Fact]
    public async Task StopResetsOnlyTheBoundProjectAndLeavesUnsavedWorkbookOpen()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "Source.xlsm");
        File.WriteAllText(path, "source fixture");
        var events = new List<string>();
        var model = FakeVbeModel.Create(path, events);
        model.Project.Mode = 1;
        model.Workbook.Saved = false;
        var reset = new FakeCommandBarControl(228, events)
        {
            ExecuteAction = () => model.Project.Mode = 2
        };
        var application = new ResetApplication(model.Excel, reset);
        application.VBE.ActiveVBProject = model.Project;
        var binding = new SourceVbeDesktopBinding(application, model.Workbook, path,
            12345, 638000000000000000, true, () => true, () => { });
        var automation = new SourceVbeDebugAutomation(new FakeSourceVbeDesktopConnector(binding),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()),
            new FakeDebugWindowActivator(events));

        await using var session = await automation.AttachOrOpenAsync(path, CancellationToken.None);
        await session.ResetExecutionAsync(TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal(2, model.Project.Mode);
        Assert.Contains("execute:228", events);
        Assert.Equal(path, model.Workbook.FullName);
        Assert.False(model.Workbook.Saved);
        Assert.DoesNotContain("excel-quit", events);
        Assert.DoesNotContain("close:False", events);
    }

    [Fact]
    public async Task ResetDoesNotInvokeAnyVbeCommandWhenExactSourceIsAlreadyInDesignMode()
    {
        using var temp = TempDirectory.Create();
        var workbookPath = Path.Combine(temp.Path, "Source.xlsm");
        File.WriteAllText(workbookPath, "source fixture");
        var events = new List<string>();
        var model = FakeVbeModel.Create(workbookPath, events);
        var connector = new FakeSourceVbeDesktopConnector(new SourceVbeDesktopBinding(
            model.Excel, model.Workbook, workbookPath, 12345, 638000000000000000,
            true, () => true, () => events.Add("release-binding")));
        var automation = new SourceVbeDebugAutomation(
            connector,
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()),
            new FakeDebugWindowActivator(events));

        await using var session = await automation.AttachOrOpenAsync(workbookPath,
            CancellationToken.None);
        await session.ResetExecutionAsync(TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal(2, model.Project.Mode);
        Assert.DoesNotContain(events, item => item.StartsWith("find-control:", StringComparison.Ordinal));
        Assert.DoesNotContain("excel-quit", events);
    }

    [Fact]
    public async Task InspectionReadsTheExactLiveWorkbookAndVbeMode()
    {
        using var temp = TempDirectory.Create();
        var workbookPath = Path.Combine(temp.Path, "Source.xlsm");
        File.WriteAllText(workbookPath, "source fixture");
        var model = FakeVbeModel.Create(workbookPath, []);
        model.Workbook.Saved = false;
        model.Project.Mode = 1;
        var connector = new FakeSourceVbeDesktopConnector(new SourceVbeDesktopBinding(
            model.Excel, model.Workbook, workbookPath, 12345, 638000000000000000,
            true, () => true, () => { }));
        var automation = new SourceVbeDebugAutomation(
            connector,
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()),
            new FakeDebugWindowActivator());

        await using var session = await automation.AttachOrOpenAsync(workbookPath,
            CancellationToken.None);
        var state = await session.InspectAsync(CancellationToken.None);

        Assert.Equal(workbookPath, state.WorkbookPath);
        Assert.Equal(12345, state.ProcessId);
        Assert.Equal(638000000000000000, state.ProcessStartUtcTicks);
        Assert.False(state.IsSaved);
        Assert.Equal(1, state.ProjectMode);
    }

    [Fact]
    public async Task DisposingBorrowedSourceSessionOnlyReleasesItsBinding()
    {
        using var temp = TempDirectory.Create();
        var workbookPath = Path.Combine(temp.Path, "Source.xlsm");
        File.WriteAllText(workbookPath, "source fixture");
        var events = new List<string>();
        var model = FakeVbeModel.Create(workbookPath, events);
        model.Workbook.Saved = false;
        var connector = new FakeSourceVbeDesktopConnector(new SourceVbeDesktopBinding(
            model.Excel,
            model.Workbook,
            workbookPath,
            processId: 12345,
            processStartUtcTicks: 638000000000000000,
            wasAlreadyOpen: true,
            isExactWorkbook: () => true,
            release: () => events.Add("release-binding")));
        var automation = new SourceVbeDebugAutomation(
            connector,
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()),
            new FakeDebugWindowActivator(events));

        var session = await automation.AttachOrOpenAsync(workbookPath, CancellationToken.None);
        await session.DisposeAsync();

        Assert.Equal(SourceVbeDebugSessionEndReason.Detached,
            (await session.Completion).Reason);
        Assert.Contains("release-binding", events);
        Assert.DoesNotContain("excel-quit", events);
        Assert.DoesNotContain("close:False", events);
        Assert.False(model.Workbook.Saved);
    }

    private sealed class FakeSourceVbeDesktopConnector(SourceVbeDesktopBinding binding)
        : ISourceVbeDesktopConnector
    {
        public SourceVbeDesktopBinding AttachOrOpen(string exactSourceWorkbookPath)
            => binding;
    }

    private sealed class FailingSourceVbeDesktopConnector(Exception failure) : ISourceVbeDesktopConnector
    {
        public SourceVbeDesktopBinding AttachOrOpen(string exactSourceWorkbookPath) => throw failure;
    }

    private sealed class PausedFailureSourceConnector(Exception failure) : ISourceVbeDesktopConnector
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Continue { get; } = new();
        public SourceVbeDesktopBinding AttachOrOpen(string path)
        {
            Entered.TrySetResult();
            Continue.Wait();
            throw failure;
        }
    }

    private sealed class NotifyingSourceStaDispatcher : IStaComDispatcher
    {
        private readonly StaComDispatcher inner = new();
        private int calls;
        internal TaskCompletionSource ReleaseQueued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ReleaseVerified => inner.ReleaseVerified;
        public Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken token)
        {
            var task = inner.InvokeAsync(operation, token);
            if (Interlocked.Increment(ref calls) == 2) ReleaseQueued.TrySetResult();
            return task;
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class SourceInputWaitSink : IDebugInputWaitSink
    {
        private int count;
        internal int Count => Volatile.Read(ref count);
        internal TaskCompletionSource<DebugInputWait> Notified { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask InputRequiredAsync(DebugInputWait wait, CancellationToken token)
        {
            Interlocked.Increment(ref count);
            Notified.TrySetResult(wait);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ContextAwareSourceModalWindowApi(Func<bool> vbeVisible) : IDebugModalWindowApi
    {
        private int promptVisible;
        internal TaskCompletionSource OrdinaryObservationCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void ShowPrompt() => Volatile.Write(ref promptVisible, 1);
        public IReadOnlySet<nint> CaptureVisibleModalWindows(int processId)
        {
            Assert.Equal(12345, processId);
            var windows = new HashSet<nint>();
            if (vbeVisible()) windows.Add(9753); // Broad native API also observes ordinary owned VBE windows.
            if (Volatile.Read(ref promptVisible) != 0) windows.Add(123);
            return windows;
        }
        public Task WaitForNextObservationAsync(CancellationToken token)
        {
            if (vbeVisible() && Volatile.Read(ref promptVisible) == 0)
                OrdinaryObservationCompleted.TrySetResult();
            return Task.Delay(10, token);
        }
    }

    private sealed class SourceModalWindowApi : IDebugModalWindowApi
    {
        private int visible;
        private Exception? failure;
        internal System.Collections.Concurrent.ConcurrentQueue<int> ProcessIds { get; } = new();
        internal void ShowModal() => Volatile.Write(ref visible, 1);
        internal void Fail(Exception error) => Volatile.Write(ref failure, error);
        public IReadOnlySet<nint> CaptureVisibleModalWindows(int processId)
        {
            ProcessIds.Enqueue(processId);
            if (Volatile.Read(ref failure) is { } error) throw error;
            return Volatile.Read(ref visible) == 0 ? new HashSet<nint>() : new HashSet<nint> { 123 };
        }
        public Task WaitForNextObservationAsync(CancellationToken token) => Task.Delay(10, token);
    }

    private sealed class CleanupFailureSourceModalMonitor(Exception failure) : IDebugModalPromptMonitor
    {
        public IDebugModalPromptPhase BeginPhase(DebugInputWait wait, Task<DebugProcessExit> process,
            IDebugInputWaitSink sink) => new CleanupFailureSourceModalPhase(failure);
    }

    private sealed class CleanupFailureSourceModalPhase(Exception failure) : IDebugModalPromptPhase
    {
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Completion => completion.Task;
        public Task<T> ObserveOperationAsync<T>(Func<Task<T>> operation, CancellationToken token) => operation();
        public ValueTask DisposeAsync()
        {
            completion.TrySetResult();
            return ValueTask.FromException(failure);
        }
    }

    public sealed class ResetApplication(FakeExcelApplication original, FakeCommandBarControl reset)
    {
        public ResetVbe VBE { get; } = new(original.VBE!, reset);
    }

    public sealed class FailingInspectionWorkbook(string path, object project, Exception failure)
    {
        public string FullName => path;
        public object VBProject => project;
        public bool Saved => throw failure;
    }

    public sealed class BlockingInspectionWorkbook(string path, object project, ManualResetEventSlim resume)
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string FullName => path;
        public object VBProject
        {
            get { Entered.TrySetResult(); resume.Wait(); return project; }
        }
        public bool Saved => false;
    }

    public sealed class ResetVbe(FakeVbe original, FakeCommandBarControl reset)
    {
        public FakeVbeWindow MainWindow => original.MainWindow;
        public object? ActiveVBProject { get; set; }
        public ResetCommandBars CommandBars { get; } = new(reset);
    }

    public sealed class ResetCommandBars(FakeCommandBarControl reset)
    {
        public object? FindControl(object type, object id, object tag, object visible)
            => Convert.ToInt32(id) == 228 ? reset : null;
    }
}
