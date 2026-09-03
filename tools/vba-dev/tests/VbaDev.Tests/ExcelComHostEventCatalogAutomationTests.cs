using System.Runtime.InteropServices;
using VbaDev.App.HostEvents;
using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Debugging;
using VbaDev.Infrastructure.Workbooks;
using Xunit;

namespace VbaDev.Tests;

public sealed class ExcelComHostEventCatalogAutomationTests
{
    [Theory]
    [InlineData(FailurePoint.SecurityConfiguration)]
    [InlineData(FailurePoint.WorkbookCreation)]
    [InlineData(FailurePoint.UserFormCreation)]
    [InlineData(FailurePoint.EventInspection)]
    public async Task OperationFailureReleasesEveryResourceThatWasCreated(
        FailurePoint failurePoint)
    {
        var events = new List<string>();
        var lifecycle = new RecordingHostEventLifecycle(events, failurePoint);
        var automation = new ExcelComHostEventCatalogAutomation(
            new RecordingDispatcherFactory(events),
            lifecycle,
            CreateTimeouts());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => automation.ReadAsync(CancellationToken.None));

        Assert.Contains("quit", events);
        Assert.Contains("process-exit-proved", events);
        Assert.Contains("dispatcher-dispose", events);
        if (failurePoint >= FailurePoint.UserFormCreation)
        {
            Assert.Contains("close:false", events);
        }

        if (failurePoint >= FailurePoint.EventInspection)
        {
            Assert.Contains("remove-userform", events);
        }
    }

    [Theory]
    [InlineData(FailurePoint.UserFormRemoval)]
    [InlineData(FailurePoint.WorkbookClose)]
    [InlineData(FailurePoint.HostDispose)]
    public async Task CooperativeCleanupFailureStillAttemptsLaterCleanupAndProvesProcessExit(
        FailurePoint failurePoint)
    {
        var events = new List<string>();
        var lifecycle = new RecordingHostEventLifecycle(events, failurePoint);
        var automation = new ExcelComHostEventCatalogAutomation(
            new RecordingDispatcherFactory(events),
            lifecycle,
            CreateTimeouts());

        await Assert.ThrowsAsync<WorkbookAutomationReleasedProcessCleanupException>(
            () => automation.ReadAsync(CancellationToken.None));

        Assert.Contains("remove-userform", events);
        Assert.Contains("close:false", events);
        Assert.Contains("quit", events);
        Assert.Contains("process-exit-proved", events);
        Assert.Contains("dispatcher-dispose", events);
    }

    [Fact]
    public async Task ProcessLossWithComOnlyPostReleaseCleanupPreservesTheProcessLoss()
    {
        var events = new List<string>();
        var processLoss = new WorkbookAutomationProcessLostException(
            new WorkbookAutomationStage(WorkbookAutomationStageKind.HostEventInspection));
        var lifecycle = new RecordingHostEventLifecycle(
            events,
            eventInspectionError: processLoss,
            disposeHostError: new COMException(
                "The released Excel server rejected Quit."));
        var automation = new ExcelComHostEventCatalogAutomation(
            new RecordingDispatcherFactory(events),
            lifecycle,
            CreateTimeouts());

        var error = await Assert.ThrowsAsync<WorkbookAutomationProcessLostException>(
            () => automation.ReadAsync(CancellationToken.None));

        Assert.Same(processLoss, error);
        Assert.Contains("quit", events);
        Assert.Contains("process-exit-proved", events);
        Assert.Contains("dispatcher-dispose", events);
    }

    [Fact]
    public async Task ProcessLossWithMixedPostReleaseCleanupSurfacesTheCleanupFailure()
    {
        var events = new List<string>();
        var processLoss = new WorkbookAutomationProcessLostException(
            new WorkbookAutomationStage(WorkbookAutomationStageKind.HostEventInspection));
        var lifecycle = new RecordingHostEventLifecycle(
            events,
            eventInspectionError: processLoss,
            disposeHostError: new AggregateException(
                new COMException("The released Excel server rejected Quit."),
                new InvalidOperationException("Unexpected cleanup defect.")));
        var automation = new ExcelComHostEventCatalogAutomation(
            new RecordingDispatcherFactory(events),
            lifecycle,
            CreateTimeouts());

        var error = await Assert.ThrowsAsync<WorkbookAutomationReleasedProcessCleanupException>(
            () => automation.ReadAsync(CancellationToken.None));

        var failures = Assert.IsType<AggregateException>(error.InnerException)
            .Flatten()
            .InnerExceptions;
        Assert.Contains(failures, failure => failure is WorkbookAutomationProcessLostException);
        Assert.Contains(failures, failure => failure is COMException);
        Assert.Contains(failures, failure => failure is InvalidOperationException);
        Assert.Contains("quit", events);
        Assert.Contains("process-exit-proved", events);
        Assert.Contains("dispatcher-dispose", events);
    }

    [Fact]
    public async Task ProofFailurePreservesCooperativeAndDispatcherCleanupFailures()
    {
        var events = new List<string>();
        var cooperativeError = new InvalidOperationException(
            "Cooperative Host Event cleanup failed.");
        var proofError = new WorkbookAutomationCleanupException(
            "Exact owned-process release could not be proved.");
        var dispatcherError = new InvalidOperationException(
            "The Host Event dispatcher could not be disposed.");
        var lifecycle = new RecordingHostEventLifecycle(
            events,
            disposeHostError: cooperativeError,
            ownerDisposeError: proofError);
        var automation = new ExcelComHostEventCatalogAutomation(
            new RecordingDispatcherFactory(events, dispatcherError),
            lifecycle,
            CreateTimeouts());

        var error = await Assert.ThrowsAsync<WorkbookAutomationCleanupException>(
            () => automation.ReadAsync(CancellationToken.None));

        Assert.True(WorkbookAutomationFailureClassifier.ContainsCleanupProofFailure(error));
        var failures = Assert.IsType<AggregateException>(error.InnerException)
            .Flatten()
            .InnerExceptions;
        Assert.Contains(failures, failure => ReferenceEquals(failure, cooperativeError));
        Assert.Contains(failures, failure => ReferenceEquals(failure, proofError));
        Assert.Contains(failures, failure => ReferenceEquals(failure, dispatcherError));
    }

    [Fact]
    public async Task CancellationDuringInspectionForceReleasesTheOwnedProcessAndPublishesNoCatalog()
    {
        var events = new List<string>();
        var paused = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lifecycle = new RecordingHostEventLifecycle(events);
        var automation = new ExcelComHostEventCatalogAutomation(
            new PausingDispatcherFactory(events, paused, invocationToPause: 4),
            lifecycle,
            CreateTimeouts());
        using var cancellation = new CancellationTokenSource();

        var reading = automation.ReadAsync(cancellation.Token);
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        Assert.Contains("process-exit-proved", events);
        Assert.Contains("dispatcher-dispose", events);
        Assert.Equal(1, automation.LifecycleMetrics.EmptyUserFormsRemoved);
        Assert.Equal(1, automation.LifecycleMetrics.WorkbooksClosedWithoutSave);
    }

    [Fact]
    public void FifteenWorkspaceDocumentsStillAcquireExactlyOneEnvironmentCatalog()
    {
        using var temp = TempDirectory.Create();
        var sentinelBytes = Enumerable.Range(1, 15)
            .Select(index =>
            {
                var path = Path.Combine(temp.Path, $"Book{index}.xlsm");
                var bytes = Enumerable.Repeat((byte)index, 64 + index).ToArray();
                File.WriteAllBytes(path, bytes);
                return (path, bytes);
            })
            .ToArray();
        var events = new List<string>();
        var lifecycle = new RecordingHostEventLifecycle(events);
        var automation = new ExcelComHostEventCatalogAutomation(
            new RecordingDispatcherFactory(events),
            lifecycle,
            CreateTimeouts());
        var application = CommandLineTestFactory.Create(
            temp.Path,
            hostEventCatalogAutomation: automation);

        var result = application.Run(["host-event", "list", "--format", "json"]);

        Assert.Equal(0, result.ExitCode);
        var metrics = automation.LifecycleMetrics;
        Assert.Equal(1, metrics.OwnedExcelProcessesStarted);
        Assert.Equal(1, metrics.BlankWorkbooksCreated);
        Assert.Equal(1, metrics.EmptyUserFormsCreated);
        Assert.Equal(1, metrics.EmptyUserFormsRemoved);
        Assert.Equal(1, metrics.WorkbooksClosedWithoutSave);
        Assert.Equal(0, metrics.TemplatesOpened);
        Assert.Equal(0, metrics.WorksheetsEnumerated);
        Assert.Equal(0, metrics.ControlsEnumerated);
        Assert.Equal(0, metrics.ModulesImported);
        Assert.Equal(0, metrics.WorkbooksSaved);
        Assert.Equal(0, metrics.PerDocumentFallbacks);
        foreach (var (path, bytes) in sentinelBytes)
        {
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
    }

    [Fact]
    public void FormInspectorStripsComponentIdentityAndKeepsStructuredEventMetadata()
    {
        var observation = new VbaDev.Infrastructure.Workbooks.ResolvedUserFormEventInspection(
            new VbaDev.Infrastructure.Workbooks.UserFormEventComponentIdentity(
                "UserForm42"),
            "UserForm",
            [
                new VbaDev.Infrastructure.Workbooks.UserFormEventObservation(
                    "QueryClose",
                    [
                        new VbaDev.Infrastructure.Workbooks.ObservedHostEventParameter(
                            "Cancel",
                            new VbaDev.Infrastructure.Workbooks.ObservedIntrinsicHostEventTypeReference("Integer"),
                            VbaDev.Infrastructure.Workbooks.ObservedHostEventPassingMechanism.ByRef,
                            VbaDev.Infrastructure.Workbooks.ObservedHostEventArrayShape.Scalar,
                            Optional: false,
                            ParamArray: false)
                    ],
                    null,
                    AuthoringAvailable: true,
                    ExistingHandlerRecognizable: true)
            ]);

        var catalog = ExcelComUserFormEventInspector.CreateCatalog(observation);

        var inspectedEvent = Assert.Single(catalog.Events);
        Assert.Equal(new HostEventIdentity("UserForm", "QueryClose"), inspectedEvent.Identity);
        Assert.True(inspectedEvent.AuthoringAvailable);
        Assert.True(inspectedEvent.ExistingHandlerRecognizable);
        var parameter = Assert.Single(inspectedEvent.Signature.Parameters);
        Assert.Equal("Cancel", parameter.Name);
        Assert.IsType<VbaDev.App.HostEvents.IntrinsicHostEventTypeReference>(parameter.Type);
    }

    [Fact]
    public async Task PublishesOnlyAfterOneBlankWorkbookAndOneEmptyUserFormAreReleased()
    {
        var events = new List<string>();
        var lifecycle = new RecordingHostEventLifecycle(events);
        var automation = new ExcelComHostEventCatalogAutomation(
            new RecordingDispatcherFactory(events),
            lifecycle,
            CreateTimeouts());

        var catalog = await automation.ReadAsync(CancellationToken.None);

        Assert.Equal("UserForm", catalog.IntrinsicEventSourceName);
        Assert.Equal(
            [
                "start-owned-hidden-excel",
                "security-force-disable",
                "events-off",
                "create-unsaved-blank-workbook",
                "add-empty-userform",
                "inspect-empty-userform",
                "remove-userform",
                "close:false",
                "quit",
                "process-exit-proved",
                "dispatcher-dispose",
                "return"
            ],
            [.. events, "return"]);
    }

    private static HostEventCatalogTimeouts CreateTimeouts()
        => new(
            ExcelProcessStart: TimeSpan.FromSeconds(30),
            WorkbookCreate: TimeSpan.FromSeconds(30),
            UserFormCreate: TimeSpan.FromSeconds(30),
            EventInspection: TimeSpan.FromSeconds(60),
            CooperativeCleanup: TimeSpan.FromSeconds(5));

    private sealed class RecordingDispatcherFactory(
        List<string> events,
        Exception? disposeError = null)
        : IStaComDispatcherFactory
    {
        public IStaComDispatcher Create()
            => new RecordingDispatcher(events, disposeError);
    }

    private sealed class RecordingDispatcher(
        List<string> events,
        Exception? disposeError) : IStaComDispatcher
    {
        public Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(operation());
        }

        public ValueTask DisposeAsync()
        {
            events.Add("dispatcher-dispose");
            return disposeError is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(disposeError);
        }
    }

    private sealed class RecordingHostEventLifecycle(
        List<string> events,
        FailurePoint failAt = FailurePoint.None,
        Exception? eventInspectionError = null,
        Exception? disposeHostError = null,
        Exception? ownerDisposeError = null)
        : IExcelComHostEventCatalogLifecycle
    {
        private readonly RecordingOwnedProcess owner = new(events, ownerDisposeError);

        public HostEventCatalogLifecycleCounters Counters { get; } = new();

        public object Start(
            OwnedExcelTerminationController terminationController,
            CancellationToken cancellationToken)
        {
            events.Add("start-owned-hidden-excel");
            terminationController.Attach(owner);
            Counters.RecordOwnedExcelProcessStarted();
            return new object();
        }

        public void ForceDisableAutomationSecurity(object host)
        {
            events.Add("security-force-disable");
            ThrowIf(FailurePoint.SecurityConfiguration);
        }

        public void DisableExcelEvents(object host)
            => events.Add("events-off");

        public object CreateUnsavedBlankWorkbook(object host)
        {
            events.Add("create-unsaved-blank-workbook");
            ThrowIf(FailurePoint.WorkbookCreation);
            Counters.RecordBlankWorkbookCreated();
            return new object();
        }

        public object AddEmptyUserForm(object workbook)
        {
            events.Add("add-empty-userform");
            ThrowIf(FailurePoint.UserFormCreation);
            Counters.RecordEmptyUserFormCreated();
            return new object();
        }

        public IntrinsicHostEventCatalog InspectEmptyUserForm(
            object host,
            object workbook,
            object userForm)
        {
            events.Add("inspect-empty-userform");
            if (eventInspectionError is not null)
            {
                throw eventInspectionError;
            }

            ThrowIf(FailurePoint.EventInspection);
            return new IntrinsicHostEventCatalog(
                "UserForm",
                [
                    new HostEvent(
                        new HostEventIdentity("UserForm", "Initialize"),
                        new VbaDev.App.HostEvents.HostEventSignature([], null),
                        AuthoringAvailable: true,
                        ExistingHandlerRecognizable: true)
                ]);
        }

        public void RemoveUserForm(object workbook, object userForm)
        {
            events.Add("remove-userform");
            ThrowIf(FailurePoint.UserFormRemoval);
            Counters.RecordEmptyUserFormRemoved();
        }

        public void CloseWorkbookWithoutSave(object workbook)
        {
            events.Add("close:false");
            ThrowIf(FailurePoint.WorkbookClose);
            Counters.RecordWorkbookClosedWithoutSave();
        }

        public void DisposeHost(object host, TimeSpan cleanupGrace)
        {
            events.Add("quit");
            owner.Complete();
            if (disposeHostError is not null)
            {
                throw disposeHostError;
            }

            ThrowIf(FailurePoint.HostDispose);
        }

        private void ThrowIf(FailurePoint point)
        {
            if (failAt == point)
            {
                throw new InvalidOperationException($"Injected failure: {point}.");
            }
        }
    }

    private sealed class RecordingOwnedProcess(
        List<string> events,
        Exception? disposeError)
        : IOwnedExcelProcessControl
    {
        private readonly TaskCompletionSource completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool HasExited => completion.Task.IsCompleted;

        public Task Completion => completion.Task;

        public Task TerminateAsync()
        {
            Complete();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
            => disposeError is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(disposeError);

        public void Complete()
        {
            if (completion.TrySetResult())
            {
                events.Add("process-exit-proved");
            }
        }
    }

    private sealed class PausingDispatcherFactory(
        List<string> events,
        TaskCompletionSource paused,
        int invocationToPause) : IStaComDispatcherFactory
    {
        public IStaComDispatcher Create()
            => new PausingDispatcher(events, paused, invocationToPause);
    }

    private sealed class PausingDispatcher(
        List<string> events,
        TaskCompletionSource paused,
        int invocationToPause) : IStaComDispatcher
    {
        private int invocationCount;

        public Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken)
        {
            invocationCount++;
            return invocationCount == invocationToPause
                ? PauseAsync<T>(cancellationToken)
                : Task.FromResult(operation());
        }

        public ValueTask DisposeAsync()
        {
            events.Add("dispatcher-dispose");
            return ValueTask.CompletedTask;
        }

        private async Task<T> PauseAsync<T>(CancellationToken cancellationToken)
        {
            paused.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The paused operation unexpectedly resumed.");
        }
    }

    public enum FailurePoint
    {
        None,
        SecurityConfiguration,
        WorkbookCreation,
        UserFormCreation,
        EventInspection,
        UserFormRemoval,
        WorkbookClose,
        HostDispose
    }
}
