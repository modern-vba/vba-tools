using System.Runtime.ExceptionServices;
using VbaDev.App.HostEvents;
using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Debugging;

namespace VbaDev.Infrastructure.Workbooks;

internal sealed record HostEventCatalogTimeouts(
    TimeSpan ExcelProcessStart,
    TimeSpan WorkbookCreate,
    TimeSpan UserFormCreate,
    TimeSpan EventInspection,
    TimeSpan CooperativeCleanup)
{
    public static HostEventCatalogTimeouts Default { get; } = new(
        ExcelProcessStart: TimeSpan.FromSeconds(30),
        WorkbookCreate: TimeSpan.FromSeconds(30),
        UserFormCreate: TimeSpan.FromSeconds(30),
        EventInspection: TimeSpan.FromSeconds(60),
        CooperativeCleanup: TimeSpan.FromSeconds(5));
}

internal interface IExcelComHostEventCatalogLifecycle
{
    HostEventCatalogLifecycleCounters Counters { get; }

    object Start(
        OwnedExcelTerminationController terminationController,
        CancellationToken cancellationToken);

    void ForceDisableAutomationSecurity(object host);

    void DisableExcelEvents(object host);

    object CreateUnsavedBlankWorkbook(object host);

    object AddEmptyUserForm(object workbook);

    IntrinsicHostEventCatalog InspectEmptyUserForm(
        object host,
        object workbook,
        object userForm);

    void RemoveUserForm(object workbook, object userForm);

    void CloseWorkbookWithoutSave(object workbook);

    void DisposeHost(object host, TimeSpan cleanupGrace);
}

/// <summary>
/// Owns the isolated hidden Excel environment used to discover generic UserForm Events.
/// </summary>
public sealed class ExcelComHostEventCatalogAutomation : IHostEventCatalogAutomation
{
    private static readonly TimeSpan ForcedTerminationObservationAllowance =
        TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DispatcherAbandonmentObservation =
        TimeSpan.FromMilliseconds(100);
    private readonly IStaComDispatcherFactory dispatcherFactory;
    private readonly IExcelComHostEventCatalogLifecycle lifecycle;
    private readonly HostEventCatalogTimeouts timeouts;

    /// <summary>
    /// Creates the production isolated Excel/VBIDE catalog adapter.
    /// </summary>
    public ExcelComHostEventCatalogAutomation()
        : this(
            new StaComDispatcherFactory(),
            new ExcelComHostEventCatalogLifecycle(),
            HostEventCatalogTimeouts.Default)
    {
    }

    internal ExcelComHostEventCatalogAutomation(
        IStaComDispatcherFactory dispatcherFactory,
        IExcelComHostEventCatalogLifecycle lifecycle,
        HostEventCatalogTimeouts timeouts)
    {
        ArgumentNullException.ThrowIfNull(dispatcherFactory);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(timeouts);
        this.dispatcherFactory = dispatcherFactory;
        this.lifecycle = lifecycle;
        this.timeouts = timeouts;
    }

    internal HostEventCatalogLifecycleMetrics LifecycleMetrics
        => lifecycle.Counters.Snapshot();

    /// <inheritdoc />
    public async Task<IntrinsicHostEventCatalog> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dispatcher = dispatcherFactory.Create();
        using var terminationController = new OwnedExcelTerminationController();
        var stageExecutor = new WorkbookAutomationStageExecutor(
            () => terminationController.HasAttachedProcessExited,
            terminationController.RequestForcedTermination,
            getOwnedProcessCompletion: () => terminationController.AttachedProcessCompletion);
        object? host = null;
        object? workbook = null;
        object? userForm = null;
        IntrinsicHostEventCatalog? catalog = null;
        Exception? operationError = null;
        try
        {
            await stageExecutor.ExecuteAsync(
                new WorkbookAutomationStage(WorkbookAutomationStageKind.ExcelStartup),
                timeouts.ExcelProcessStart,
                timeouts.CooperativeCleanup,
                cancellationToken,
                stageCancellation => dispatcher.InvokeAsync(
                    () =>
                    {
                        host = lifecycle.Start(terminationController, stageCancellation);
                        lifecycle.ForceDisableAutomationSecurity(host);
                        lifecycle.DisableExcelEvents(host);
                        return true;
                    },
                    stageCancellation)).ConfigureAwait(false);

            await stageExecutor.ExecuteAsync(
                new WorkbookAutomationStage(WorkbookAutomationStageKind.WorkbookCreate),
                timeouts.WorkbookCreate,
                timeouts.CooperativeCleanup,
                cancellationToken,
                stageCancellation => dispatcher.InvokeAsync(
                    () =>
                    {
                        workbook = lifecycle.CreateUnsavedBlankWorkbook(host!);
                        return true;
                    },
                    stageCancellation)).ConfigureAwait(false);

            await stageExecutor.ExecuteAsync(
                new WorkbookAutomationStage(WorkbookAutomationStageKind.UserFormCreate),
                timeouts.UserFormCreate,
                timeouts.CooperativeCleanup,
                cancellationToken,
                stageCancellation => dispatcher.InvokeAsync(
                    () =>
                    {
                        userForm = lifecycle.AddEmptyUserForm(workbook!);
                        return true;
                    },
                    stageCancellation)).ConfigureAwait(false);

            catalog = await stageExecutor.ExecuteAsync(
                new WorkbookAutomationStage(WorkbookAutomationStageKind.HostEventInspection),
                timeouts.EventInspection,
                timeouts.CooperativeCleanup,
                cancellationToken,
                stageCancellation => dispatcher.InvokeAsync(
                    () => lifecycle.InspectEmptyUserForm(host!, workbook!, userForm!),
                    stageCancellation)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            operationError = exception;
        }

        var release = await ReleaseOwnedHostAsync(
            dispatcher,
            terminationController,
            stageExecutor,
            host,
            workbook,
            userForm).ConfigureAwait(false);
        var dispatcherError = await DisposeDispatcherAsync(
            dispatcher,
            stageExecutor.HasAbandonedOperation).ConfigureAwait(false);
        if (release.ProofError is not null)
        {
            throw new WorkbookAutomationCleanupException(
                "The owned Excel process used for Host Event catalog acquisition could not be verified as released.",
                operationError is null
                    ? release.ProofError
                    : new AggregateException(operationError, release.ProofError));
        }

        var cooperativeCleanupError = CombineErrors(
            release.CooperativeError,
            dispatcherError);
        if (cooperativeCleanupError is not null)
        {
            throw new WorkbookAutomationReleasedProcessCleanupException(
                "The Host Event catalog process was released, but cooperative COM or " +
                "STA dispatcher cleanup did not complete cleanly.",
                operationError is null
                    ? cooperativeCleanupError
                    : new AggregateException(operationError, cooperativeCleanupError));
        }

        if (operationError is not null)
        {
            ExceptionDispatchInfo.Capture(operationError).Throw();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return catalog
            ?? throw new InvalidOperationException(
                "Excel did not produce an intrinsic UserForm Event catalog.");
    }

    private async Task<OwnedHostReleaseResult> ReleaseOwnedHostAsync(
        IStaComDispatcher dispatcher,
        OwnedExcelTerminationController terminationController,
        WorkbookAutomationStageExecutor stageExecutor,
        object? host,
        object? workbook,
        object? userForm)
    {
        Exception? cooperativeError = null;
        if (!stageExecutor.HasAbandonedOperation && host is not null)
        {
            terminationController.RequestForcedTermination(timeouts.CooperativeCleanup);
            try
            {
                var cleanupTask = dispatcher.InvokeAsync(
                    () =>
                    {
                        var cleanupErrors = new List<Exception>();
                        if (userForm is not null && workbook is not null)
                        {
                            TryCleanup(
                                () => lifecycle.RemoveUserForm(workbook, userForm),
                                cleanupErrors);
                        }

                        if (workbook is not null)
                        {
                            TryCleanup(
                                () => lifecycle.CloseWorkbookWithoutSave(workbook),
                                cleanupErrors);
                        }

                        TryCleanup(
                            () => lifecycle.DisposeHost(host, timeouts.CooperativeCleanup),
                            cleanupErrors);
                        if (cleanupErrors.Count == 1)
                        {
                            ExceptionDispatchInfo.Capture(cleanupErrors[0]).Throw();
                        }

                        if (cleanupErrors.Count > 1)
                        {
                            throw new AggregateException(cleanupErrors);
                        }

                        return true;
                    },
                    CancellationToken.None);
                var completed = await Task.WhenAny(
                    cleanupTask,
                    Task.Delay(timeouts.CooperativeCleanup + ForcedTerminationObservationAllowance))
                    .ConfigureAwait(false);
                if (completed == cleanupTask)
                {
                    await cleanupTask.ConfigureAwait(false);
                }
                else
                {
                    stageExecutor.MarkOperationAbandoned();
                    WorkbookAutomationStageExecutor.ObserveFault(cleanupTask);
                    cooperativeError = new WorkbookAutomationTimeoutException(
                        new WorkbookAutomationStage(WorkbookAutomationStageKind.ProcessCleanup),
                        timeouts.CooperativeCleanup);
                }
            }
            catch (Exception exception)
            {
                cooperativeError = exception;
            }
        }

        try
        {
            terminationController.RequestForcedTermination(timeouts.CooperativeCleanup);
            await terminationController.ObserveCleanupWithinAsync(
                ForcedTerminationObservationAllowance).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return new OwnedHostReleaseResult(exception, cooperativeError);
        }

        return new OwnedHostReleaseResult(null, cooperativeError);
    }

    private static void TryCleanup(Action cleanup, ICollection<Exception> errors)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }
    }

    private static Exception? CombineErrors(Exception? first, Exception? second)
        => first is null
            ? second
            : second is null
                ? first
                : new AggregateException(first, second);

    private static async Task<Exception?> DisposeDispatcherAsync(
        IStaComDispatcher dispatcher,
        bool allowAbandonment)
    {
        try
        {
            var disposalTask = dispatcher.DisposeAsync().AsTask();
            if (!allowAbandonment)
            {
                await disposalTask.ConfigureAwait(false);
                return null;
            }

            var completed = await Task.WhenAny(
                disposalTask,
                Task.Delay(DispatcherAbandonmentObservation)).ConfigureAwait(false);
            if (completed == disposalTask)
            {
                await disposalTask.ConfigureAwait(false);
            }
            else
            {
                WorkbookAutomationStageExecutor.ObserveFault(disposalTask);
            }

            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private sealed record OwnedHostReleaseResult(
        Exception? ProofError,
        Exception? CooperativeError);

    internal sealed class ExcelComHostEventCatalogLifecycle
        : IExcelComHostEventCatalogLifecycle
    {
        private const int MsoAutomationSecurityForceDisable = 3;
        private const int XlWbatWorksheet = -4167;
        private const int VbextCtMsForm = 3;

        public HostEventCatalogLifecycleCounters Counters { get; } = new();

        public object Start(
            OwnedExcelTerminationController terminationController,
            CancellationToken cancellationToken)
        {
            var host = ExcelComWorkbookSession.StartOwnedForGeneration(
                terminationController,
                cancellationToken);
            Counters.RecordOwnedExcelProcessStarted();
            return host;
        }

        public void ForceDisableAutomationSecurity(object host)
        {
            dynamic excel = ((ExcelComWorkbookSession.ExcelComHostObjects)host).ExcelObject;
            excel.Visible = false;
            excel.DisplayAlerts = false;
            excel.AutomationSecurity = MsoAutomationSecurityForceDisable;
            if ((bool)excel.Visible ||
                (bool)excel.DisplayAlerts ||
                (int)excel.AutomationSecurity != MsoAutomationSecurityForceDisable)
            {
                throw new InvalidOperationException(
                    "Excel did not retain the hidden, alert-free, force-disabled automation boundary.");
            }
        }

        public void DisableExcelEvents(object host)
        {
            dynamic excel = ((ExcelComWorkbookSession.ExcelComHostObjects)host).ExcelObject;
            excel.EnableEvents = false;
            if ((bool)excel.EnableEvents)
            {
                throw new InvalidOperationException(
                    "Excel did not retain disabled application Events.");
            }
        }

        public object CreateUnsavedBlankWorkbook(object host)
        {
            dynamic workbooks = ((ExcelComWorkbookSession.ExcelComHostObjects)host).WorkbooksObject;
            object? workbook = null;
            try
            {
                if ((int)workbooks.Count != 0)
                {
                    throw new InvalidOperationException(
                        "The owned catalog process must have no workbooks before blank workbook creation.");
                }

                workbook = workbooks.Add(XlWbatWorksheet);
                dynamic generatedWorkbook = workbook;
                if ((int)workbooks.Count != 1 ||
                    !string.IsNullOrEmpty((string)generatedWorkbook.Path))
                {
                    throw new InvalidOperationException(
                        "Excel did not create exactly one unsaved blank workbook.");
                }

                Counters.RecordBlankWorkbookCreated();
                return workbook;
            }
            catch
            {
                ComObjectReleaser.Release(workbook);
                throw;
            }
        }

        public object AddEmptyUserForm(object workbook)
        {
            object? project = null;
            object? components = null;
            object? userForm = null;
            object? designer = null;
            object? controls = null;
            try
            {
                dynamic generatedWorkbook = workbook;
                project = generatedWorkbook.VBProject;
                dynamic vbProject = project;
                components = vbProject.VBComponents;
                dynamic componentCollection = components;
                userForm = componentCollection.Add(VbextCtMsForm);
                dynamic component = userForm;
                if ((int)component.Type != VbextCtMsForm)
                {
                    throw new InvalidOperationException(
                        "VBIDE did not create a UserForm component.");
                }

                designer = component.Designer;
                dynamic formDesigner = designer;
                controls = formDesigner.Controls;
                dynamic controlCollection = controls;
                if ((int)controlCollection.Count != 0)
                {
                    throw new InvalidOperationException(
                        "The generated catalog UserForm must contain no controls.");
                }

                Counters.RecordEmptyUserFormCreated();
                return new UserFormEventComponentDescriptor(
                    (int)componentCollection.Count,
                    new UserFormEventComponentIdentity((string)component.Name));
            }
            finally
            {
                ComObjectReleaser.Release(controls);
                ComObjectReleaser.Release(designer);
                ComObjectReleaser.Release(userForm);
                ComObjectReleaser.Release(components);
                ComObjectReleaser.Release(project);
            }
        }

        public IntrinsicHostEventCatalog InspectEmptyUserForm(
            object host,
            object workbook,
            object userForm)
            => ExcelComUserFormEventInspector.Inspect(
                (ExcelComWorkbookSession.ExcelComHostObjects)host,
                workbook,
                (UserFormEventComponentDescriptor)userForm);

        public void RemoveUserForm(object workbook, object userForm)
        {
            object? project = null;
            object? components = null;
            object? component = null;
            try
            {
                var descriptor = (UserFormEventComponentDescriptor)userForm;
                dynamic generatedWorkbook = workbook;
                project = generatedWorkbook.VBProject;
                dynamic vbProject = project;
                components = vbProject.VBComponents;
                dynamic componentCollection = components;
                component = componentCollection.Item(descriptor.Ordinal);
                dynamic generatedForm = component;
                if ((int)generatedForm.Type != VbextCtMsForm ||
                    !((string)generatedForm.Name).Equals(
                        descriptor.Identity.Name,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "The generated catalog UserForm identity changed before cleanup.");
                }

                componentCollection.Remove(component);
                Counters.RecordEmptyUserFormRemoved();
            }
            finally
            {
                ComObjectReleaser.Release(component);
                ComObjectReleaser.Release(components);
                ComObjectReleaser.Release(project);
            }
        }

        public void CloseWorkbookWithoutSave(object workbook)
        {
            try
            {
                dynamic generatedWorkbook = workbook;
                generatedWorkbook.Close(false);
                Counters.RecordWorkbookClosedWithoutSave();
            }
            finally
            {
                ComObjectReleaser.Release(workbook);
            }
        }

        public void DisposeHost(object host, TimeSpan cleanupGrace)
            => ExcelComWorkbookSession.DisposeOwnedGenerationHost(
                (ExcelComWorkbookSession.ExcelComHostObjects)host,
                cleanupGrace);
    }
}

internal sealed record HostEventCatalogLifecycleMetrics(
    int OwnedExcelProcessesStarted,
    int BlankWorkbooksCreated,
    int EmptyUserFormsCreated,
    int EmptyUserFormsRemoved,
    int WorkbooksClosedWithoutSave,
    int TemplatesOpened,
    int WorksheetsEnumerated,
    int ControlsEnumerated,
    int ModulesImported,
    int WorkbooksSaved,
    int PerDocumentFallbacks);

internal sealed class HostEventCatalogLifecycleCounters
{
    private int ownedExcelProcessesStarted;
    private int blankWorkbooksCreated;
    private int emptyUserFormsCreated;
    private int emptyUserFormsRemoved;
    private int workbooksClosedWithoutSave;
    private int templatesOpened;
    private int worksheetsEnumerated;
    private int controlsEnumerated;
    private int modulesImported;
    private int workbooksSaved;
    private int perDocumentFallbacks;

    public HostEventCatalogLifecycleMetrics Snapshot()
        => new(
            Volatile.Read(ref ownedExcelProcessesStarted),
            Volatile.Read(ref blankWorkbooksCreated),
            Volatile.Read(ref emptyUserFormsCreated),
            Volatile.Read(ref emptyUserFormsRemoved),
            Volatile.Read(ref workbooksClosedWithoutSave),
            Volatile.Read(ref templatesOpened),
            Volatile.Read(ref worksheetsEnumerated),
            Volatile.Read(ref controlsEnumerated),
            Volatile.Read(ref modulesImported),
            Volatile.Read(ref workbooksSaved),
            Volatile.Read(ref perDocumentFallbacks));

    internal void RecordOwnedExcelProcessStarted()
        => Interlocked.Increment(ref ownedExcelProcessesStarted);

    internal void RecordBlankWorkbookCreated()
        => Interlocked.Increment(ref blankWorkbooksCreated);

    internal void RecordEmptyUserFormCreated()
        => Interlocked.Increment(ref emptyUserFormsCreated);

    internal void RecordEmptyUserFormRemoved()
        => Interlocked.Increment(ref emptyUserFormsRemoved);

    internal void RecordWorkbookClosedWithoutSave()
        => Interlocked.Increment(ref workbooksClosedWithoutSave);

    internal void RecordTemplateOpened()
        => Interlocked.Increment(ref templatesOpened);

    internal void RecordWorksheetEnumerated()
        => Interlocked.Increment(ref worksheetsEnumerated);

    internal void RecordControlEnumerated()
        => Interlocked.Increment(ref controlsEnumerated);

    internal void RecordModuleImported()
        => Interlocked.Increment(ref modulesImported);

    internal void RecordWorkbookSaved()
        => Interlocked.Increment(ref workbooksSaved);

    internal void RecordPerDocumentFallback()
        => Interlocked.Increment(ref perDocumentFallbacks);
}
