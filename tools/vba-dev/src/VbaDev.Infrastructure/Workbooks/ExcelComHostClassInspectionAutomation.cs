using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Xml.Linq;
using VbaDev.App.HostClasses;
using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Debugging;
using VbaLanguageServer.Syntax;

namespace VbaDev.Infrastructure.Workbooks;

internal sealed record HostClassComponentDescriptor(
    int Ordinal,
    HostClassIdentity Identity);

internal sealed record HostClassIdentityEnumeration(
    bool Complete,
    IReadOnlyList<HostClassComponentDescriptor> Components,
    IReadOnlyList<HostClassInspectionDiagnostic> Diagnostics)
{
    public string? VbaProjectName { get; init; }

    public static HostClassIdentityEnumeration CreateComplete(
        IReadOnlyList<HostClassComponentDescriptor> components)
        => new(true, components, []);
}

internal interface IExcelComHostClassInspectionLifecycle
{
    void ValidateSafePrivateCopy(string workbookPath);

    object Start(
        OwnedExcelTerminationController terminationController,
        CancellationToken cancellationToken);

    void ForceDisableAutomationSecurity(object host);

    void DisableExcelEvents(object host);

    object OpenPrivateWorkbookReadOnly(object host, string workbookPath);

    HostClassIdentityEnumeration EnumerateClasses(object host, object workbook);

    HostClassInspectionEntry InspectClass(
        object host,
        object workbook,
        HostClassComponentDescriptor component);

    void CloseWorkbookWithoutSave(object workbook);

    void DisposeHost(object host, TimeSpan cleanupGrace);
}

/// <summary>
/// Owns Excel/VBIDE host-class inspection for one private source-template copy.
/// </summary>
public sealed class ExcelComHostClassInspectionAutomation : IHostClassInspectionAutomation
{
    private static readonly TimeSpan ForcedTerminationObservationAllowance =
        TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DispatcherAbandonmentObservation =
        TimeSpan.FromMilliseconds(100);
    private readonly IStaComDispatcherFactory dispatcherFactory;
    private readonly IExcelComHostClassInspectionLifecycle lifecycle;
    private readonly HostClassInspectionWorkspaceFactory workspaceFactory;

    /// <summary>
    /// Creates the production Excel/VBIDE host-class inspection adapter.
    /// </summary>
    public ExcelComHostClassInspectionAutomation()
        : this(
            new StaComDispatcherFactory(),
            new ExcelComHostClassInspectionLifecycle(),
            new HostClassInspectionWorkspaceFactory())
    {
    }

    internal ExcelComHostClassInspectionAutomation(
        IStaComDispatcherFactory dispatcherFactory,
        IExcelComHostClassInspectionLifecycle lifecycle,
        HostClassInspectionWorkspaceFactory workspaceFactory)
    {
        ArgumentNullException.ThrowIfNull(dispatcherFactory);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(workspaceFactory);
        this.dispatcherFactory = dispatcherFactory;
        this.lifecycle = lifecycle;
        this.workspaceFactory = workspaceFactory;
    }

    /// <inheritdoc />
    public async Task<HostClassInspectionCompletion> InspectAsync(
        HostClassInspectionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceTemplatePath);
        ArgumentNullException.ThrowIfNull(request.Timeouts);

        var workspace = workspaceFactory.Create(request.SourceTemplatePath);
        var sourceTemplateFingerprint = Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(workspace.WorkbookPath)));
        try
        {
            lifecycle.ValidateSafePrivateCopy(workspace.WorkbookPath);
        }
        catch (Exception exception)
        {
            var preflightCleanup = workspace.Cleanup();
            throw new HostClassInspectionPreparationException(
                request.SourceTemplatePath,
                workspace.WorkspacePath,
                !preflightCleanup.Deleted,
                exception);
        }

        IStaComDispatcher dispatcher;
        try
        {
            dispatcher = dispatcherFactory.Create();
        }
        catch (Exception exception)
        {
            var dispatcherCleanup = workspace.Cleanup();
            throw new HostClassInspectionPreparationException(
                request.SourceTemplatePath,
                workspace.WorkspacePath,
                !dispatcherCleanup.Deleted,
                exception);
        }

        using var terminationController = new OwnedExcelTerminationController();
        var stageExecutor = new WorkbookAutomationStageExecutor(
            () => terminationController.HasAttachedProcessExited,
            terminationController.RequestForcedTermination,
            getOwnedProcessCompletion: () =>
                terminationController.AttachedProcessCompletion);
        object? host = null;
        object? workbook = null;
        HostClassInspectionBatch? batch = null;
        Exception? operationError = null;
        try
        {
            await stageExecutor.ExecuteAsync(
                new WorkbookAutomationStage(WorkbookAutomationStageKind.ExcelStartup),
                request.Timeouts.ExcelProcessStart,
                request.Timeouts.CooperativeCleanup,
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
                new WorkbookAutomationStage(
                    WorkbookAutomationStageKind.WorkbookOpen,
                    Path.GetFileName(workspace.WorkbookPath)),
                request.Timeouts.WorkbookOpen,
                request.Timeouts.CooperativeCleanup,
                cancellationToken,
                stageCancellation => dispatcher.InvokeAsync(
                    () =>
                    {
                        workbook = lifecycle.OpenPrivateWorkbookReadOnly(
                            host!,
                            workspace.WorkbookPath);
                        return true;
                    },
                    stageCancellation)).ConfigureAwait(false);

            batch = await InspectClassesAsync(
                dispatcher,
                stageExecutor,
                lifecycle,
                host!,
                workbook!,
                request.Timeouts,
                cancellationToken).ConfigureAwait(false);
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
            request.Timeouts.CooperativeCleanup).ConfigureAwait(false);
        var dispatcherError = await DisposeDispatcherAsync(
            dispatcher,
            stageExecutor.HasAbandonedOperation).ConfigureAwait(false);
        if (release.ProofError is not null)
        {
            throw new WorkbookAutomationCleanupException(
                $"The owned Excel process could not be verified as released. The private host-class inspection workspace was retained at '{workspace.WorkspacePath}'.",
                operationError is null
                    ? release.ProofError
                    : new AggregateException(operationError, release.ProofError));
        }

        var cleanup = workspace.Cleanup();
        var releasedProcessCleanupError = CombineErrors(
            release.CooperativeError,
            dispatcherError);
        if (releasedProcessCleanupError is not null)
        {
            throw new WorkbookAutomationReleasedProcessCleanupException(
                "The owned Excel process was released, but cooperative host or STA dispatcher cleanup did not complete cleanly." +
                CreateRetainedWorkspaceErrorSuffix(cleanup),
                operationError is null
                    ? releasedProcessCleanupError
                    : new AggregateException(operationError, releasedProcessCleanupError));
        }

        if (operationError is WorkbookAutomationCanceledException cancellation)
        {
            batch ??= HostClassInspectionBatch.CreateCancelled(
                classEnumerationComplete: false,
                [],
                cancellation.Message);
            operationError = null;
        }

        if (cancellationToken.IsCancellationRequested &&
            batch is { Outcome: HostClassInspectionOutcome.Completed })
        {
            batch = new HostClassInspectionBatch(
                batch.ClassEnumerationComplete,
                batch.Classes)
            {
                VbaProjectName = batch.VbaProjectName,
                Outcome = HostClassInspectionOutcome.Cancelled,
                Diagnostics =
                [
                    .. batch.Diagnostics,
                    new HostClassInspectionDiagnostic(
                        "operationCancelled",
                        "Host-class inspection was cancelled before its released result was published.")
                ]
            };
        }

        if (operationError is not null)
        {
            if (!cleanup.Deleted)
            {
                throw new WorkbookAutomationReleasedProcessCleanupException(
                    "The host-class inspection operation failed after the owned Excel process was released." +
                    CreateRetainedWorkspaceErrorSuffix(cleanup),
                    operationError);
            }

            ExceptionDispatchInfo.Capture(operationError).Throw();
        }

        var warnings = cleanup.Deleted
            ? Array.Empty<HostClassInspectionWarning>()
            : new[]
            {
                new HostClassInspectionWarning(
                    "inspectionWorkspaceRetained",
                    $"The released host-class inspection workspace could not be removed and was retained at '{cleanup.RetainedPath}'.")
            };
        batch = batch! with
        {
            SourceTemplateFingerprint = sourceTemplateFingerprint
        };
        return new HostClassInspectionCompletion(batch, warnings);
    }

    private static string CreateRetainedWorkspaceErrorSuffix(
        HostClassInspectionWorkspaceCleanupResult cleanup)
        => cleanup.Deleted
            ? string.Empty
            : $" The private host-class inspection workspace was retained at '{cleanup.RetainedPath}'.";

    private static async Task<HostClassInspectionBatch> InspectClassesAsync(
        IStaComDispatcher dispatcher,
        WorkbookAutomationStageExecutor stageExecutor,
        IExcelComHostClassInspectionLifecycle lifecycle,
        object host,
        object workbook,
        HostClassInspectionTimeouts timeouts,
        CancellationToken cancellationToken)
    {
        HostClassIdentityEnumeration enumeration;
        try
        {
            enumeration = await stageExecutor.ExecuteAsync(
                new WorkbookAutomationStage(
                    WorkbookAutomationStageKind.HostClassEnumeration),
                timeouts.ClassEnumeration,
                timeouts.CooperativeCleanup,
                cancellationToken,
                stageCancellation => dispatcher.InvokeAsync(
                    () => lifecycle.EnumerateClasses(host, workbook),
                    stageCancellation)).ConfigureAwait(false);
        }
        catch (WorkbookAutomationTimeoutException exception)
        {
            return new HostClassInspectionBatch(false, [])
            {
                Outcome = HostClassInspectionOutcome.InspectionStateUntrusted,
                Diagnostics =
                [
                    new HostClassInspectionDiagnostic(
                        "classEnumerationFailure",
                        "The complete intrinsic host-class identity set could not be enumerated before its deadline."),
                    new HostClassInspectionDiagnostic(
                        "inspectionStateUntrusted",
                        exception.Message)
                ]
            };
        }
        catch (WorkbookAutomationCanceledException exception)
        {
            return HostClassInspectionBatch.CreateCancelled(
                classEnumerationComplete: false,
                [],
                exception.Message);
        }
        catch (WorkbookAutomationProcessLostException exception)
        {
            return new HostClassInspectionBatch(false, [])
            {
                Outcome = HostClassInspectionOutcome.InspectionStateUntrusted,
                Diagnostics =
                [
                    new HostClassInspectionDiagnostic(
                        "classEnumerationFailure",
                        "The complete intrinsic host-class identity set could not be enumerated before the owned Excel process exited."),
                    new HostClassInspectionDiagnostic(
                        "inspectionStateUntrusted",
                        exception.Message)
                ]
            };
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException and
            not WorkbookAutomationProcessLostException)
        {
            return new HostClassInspectionBatch(false, [])
            {
                Diagnostics =
                [
                    new HostClassInspectionDiagnostic(
                        "classEnumerationFailure",
                        $"The complete intrinsic host-class identity set could not be enumerated: {exception.Message}")
                ]
            };
        }
        enumeration = OmitDuplicateIdentities(enumeration);
        var entries = new List<HostClassInspectionEntry>(enumeration.Components.Count);
        for (var index = 0; index < enumeration.Components.Count; index++)
        {
            var component = enumeration.Components[index];
            try
            {
                var entry = await stageExecutor.ExecuteAsync(
                    new WorkbookAutomationStage(
                        WorkbookAutomationStageKind.HostClassInspection,
                        component.Identity.Name),
                    timeouts.ClassInspection,
                    timeouts.CooperativeCleanup,
                    cancellationToken,
                    stageCancellation => dispatcher.InvokeAsync(
                        () => lifecycle.InspectClass(host, workbook, component),
                        stageCancellation)).ConfigureAwait(false);
                entries.Add(entry);
            }
            catch (WorkbookAutomationCanceledException exception)
            {
                for (var cancelledIndex = index;
                     cancelledIndex < enumeration.Components.Count;
                     cancelledIndex++)
                {
                    var cancelled = enumeration.Components[cancelledIndex];
                    entries.Add(new UnverifiedHostClassInspectionEntry(
                        cancelled.Identity,
                        HostClassInspectionFailureReason.Cancelled,
                        $"Host-class inspection was cancelled before '{cancelled.Identity.Name}' completed."));
                }

                return WithProjectName(
                    HostClassInspectionBatch.CreateCancelled(
                    enumeration.Complete,
                    entries,
                    exception.Message),
                    enumeration);
            }
            catch (WorkbookAutomationTimeoutException exception)
            {
                entries.Add(new UnverifiedHostClassInspectionEntry(
                    component.Identity,
                    HostClassInspectionFailureReason.InspectionTimeout,
                    $"Host-class inspection exceeded its deadline for '{component.Identity.Name}'."));
                for (var abortedIndex = index + 1;
                     abortedIndex < enumeration.Components.Count;
                     abortedIndex++)
                {
                    var aborted = enumeration.Components[abortedIndex];
                    entries.Add(new UnverifiedHostClassInspectionEntry(
                        aborted.Identity,
                        HostClassInspectionFailureReason.InspectionAborted,
                        $"Host-class inspection for '{aborted.Identity.Name}' was not started after shared inspection state became untrusted."));
                }

                return WithProjectName(
                    HostClassInspectionBatch.CreateInspectionStateUntrusted(
                    enumeration.Complete,
                    entries,
                    exception.Message),
                    enumeration);
            }
            catch (WorkbookAutomationProcessLostException exception)
            {
                entries.Add(new UnverifiedHostClassInspectionEntry(
                    component.Identity,
                    HostClassInspectionFailureReason.InspectionFailure,
                    $"Host-class inspection lost its owned Excel process for '{component.Identity.Name}'."));
                for (var abortedIndex = index + 1;
                     abortedIndex < enumeration.Components.Count;
                     abortedIndex++)
                {
                    var aborted = enumeration.Components[abortedIndex];
                    entries.Add(new UnverifiedHostClassInspectionEntry(
                        aborted.Identity,
                        HostClassInspectionFailureReason.InspectionAborted,
                        $"Host-class inspection for '{aborted.Identity.Name}' was not started after shared inspection state became untrusted."));
                }

                return WithProjectName(
                    HostClassInspectionBatch.CreateInspectionStateUntrusted(
                    enumeration.Complete,
                    entries,
                    exception.Message),
                    enumeration);
            }
            catch (HostClassInspectionStateUntrustedException exception)
            {
                entries.Add(new UnverifiedHostClassInspectionEntry(
                    component.Identity,
                    HostClassInspectionFailureReason.InspectionFailure,
                    $"Host-class inspection invalidated shared state for '{component.Identity.Name}': {exception.Message}"));
                for (var abortedIndex = index + 1;
                     abortedIndex < enumeration.Components.Count;
                     abortedIndex++)
                {
                    var aborted = enumeration.Components[abortedIndex];
                    entries.Add(new UnverifiedHostClassInspectionEntry(
                        aborted.Identity,
                        HostClassInspectionFailureReason.InspectionAborted,
                        $"Host-class inspection for '{aborted.Identity.Name}' was not started after shared inspection state became untrusted."));
                }

                return WithProjectName(
                    HostClassInspectionBatch.CreateInspectionStateUntrusted(
                    enumeration.Complete,
                    entries,
                    exception.Message),
                    enumeration);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException and
                not WorkbookAutomationProcessLostException)
            {
                entries.Add(new UnverifiedHostClassInspectionEntry(
                    component.Identity,
                    HostClassInspectionFailureReason.InspectionFailure,
                    $"Host-class inspection failed for '{component.Identity.Name}': {exception.Message}"));
            }
        }

        return new HostClassInspectionBatch(enumeration.Complete, entries)
        {
            VbaProjectName = enumeration.VbaProjectName,
            Diagnostics = enumeration.Diagnostics
        };
    }

    private static HostClassInspectionBatch WithProjectName(
        HostClassInspectionBatch batch,
        HostClassIdentityEnumeration enumeration)
        => batch with { VbaProjectName = enumeration.VbaProjectName };

    private static HostClassIdentityEnumeration OmitDuplicateIdentities(
        HostClassIdentityEnumeration enumeration)
    {
        var groups = enumeration.Components
            .GroupBy(
                component => $"{(int)component.Identity.Kind}\0{component.Identity.Name}",
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var duplicateGroups = groups.Where(group => group.Count() > 1).ToArray();
        if (duplicateGroups.Length == 0)
        {
            return enumeration;
        }

        var duplicateKeys = duplicateGroups
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unique = enumeration.Components
            .Where(component => !duplicateKeys.Contains(
                $"{(int)component.Identity.Kind}\0{component.Identity.Name}"))
            .ToArray();
        var duplicateNames = duplicateGroups
            .Select(group => group.First().Identity)
            .Select(identity => $"{identity.Kind} '{identity.Name}'")
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase);
        return new HostClassIdentityEnumeration(
            Complete: false,
            Components: unique,
            Diagnostics:
            [
                .. enumeration.Diagnostics,
                new HostClassInspectionDiagnostic(
                    "classEnumerationFailure",
                    $"Duplicate case-insensitive host-class identities were omitted: {string.Join(", ", duplicateNames)}.")
            ])
        {
            VbaProjectName = enumeration.VbaProjectName
        };
    }

    private async Task<OwnedHostReleaseResult> ReleaseOwnedHostAsync(
        IStaComDispatcher dispatcher,
        OwnedExcelTerminationController terminationController,
        WorkbookAutomationStageExecutor stageExecutor,
        object? host,
        object? workbook,
        TimeSpan cleanupGrace)
    {
        Exception? cooperativeError = null;
        if (!stageExecutor.HasAbandonedOperation && host is not null)
        {
            terminationController.RequestForcedTermination(cleanupGrace);
            try
            {
                var cleanupTask = dispatcher.InvokeAsync(
                    () =>
                    {
                        if (workbook is not null)
                        {
                            lifecycle.CloseWorkbookWithoutSave(workbook);
                        }

                        lifecycle.DisposeHost(host, cleanupGrace);
                        return true;
                    },
                    CancellationToken.None);
                var completed = await Task.WhenAny(
                    cleanupTask,
                    Task.Delay(cleanupGrace + ForcedTerminationObservationAllowance))
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
                        new WorkbookAutomationStage(
                            WorkbookAutomationStageKind.ProcessCleanup),
                        cleanupGrace);
                }
            }
            catch (Exception exception)
            {
                cooperativeError = exception;
            }
        }

        try
        {
            terminationController.RequestForcedTermination(cleanupGrace);
            await terminationController.ObserveCleanupWithinAsync(
                ForcedTerminationObservationAllowance).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return new OwnedHostReleaseResult(exception, cooperativeError);
        }

        return new OwnedHostReleaseResult(null, cooperativeError);
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

    internal sealed class ExcelComHostClassInspectionLifecycle
        : IExcelComHostClassInspectionLifecycle
    {
        private const int MsoAutomationSecurityForceDisable = 3;

        public void ValidateSafePrivateCopy(string workbookPath)
            => HostClassWorkbookSafetyPreflight.ThrowIfUnsafe(workbookPath);

        public object Start(
            OwnedExcelTerminationController terminationController,
            CancellationToken cancellationToken)
            => ExcelComWorkbookSession.StartOwnedForGeneration(
                terminationController,
                cancellationToken);

        public void ForceDisableAutomationSecurity(object host)
        {
            dynamic excel = ((ExcelComWorkbookSession.ExcelComHostObjects)host).ExcelObject;
            excel.AutomationSecurity = MsoAutomationSecurityForceDisable;
            if ((int)excel.AutomationSecurity != MsoAutomationSecurityForceDisable)
            {
                throw new InvalidOperationException(
                    "Excel did not retain force-disabled automation security.");
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

        public object OpenPrivateWorkbookReadOnly(object host, string workbookPath)
        {
            dynamic workbooks = ((ExcelComWorkbookSession.ExcelComHostObjects)host).WorkbooksObject;
            object? openedWorkbookObject = null;
            try
            {
                if ((int)workbooks.Count != 0)
                {
                    throw new InvalidOperationException(
                        "The exactly owned Excel process must have no open workbooks before opening the private host-class inspection copy.");
                }

                openedWorkbookObject = workbooks.Open(
                    workbookPath,
                    0,
                    true,
                    Type.Missing,
                    Type.Missing,
                    Type.Missing,
                    true,
                    Type.Missing,
                    Type.Missing,
                    Type.Missing,
                    Type.Missing,
                    Type.Missing,
                    false);
                dynamic openedWorkbook = openedWorkbookObject;
                ValidateOpenedPrivateWorkbook(
                    (string)openedWorkbook.FullName,
                    (bool)openedWorkbook.ReadOnly,
                    workbookPath);
                if ((int)workbooks.Count != 1)
                {
                    throw new InvalidOperationException(
                        "The exactly owned Excel process must have exactly one open workbook after opening the private host-class inspection copy.");
                }

                return openedWorkbookObject;
            }
            catch
            {
                ComObjectReleaser.Release(openedWorkbookObject);
                throw;
            }
        }

        internal static void ValidateOpenedPrivateWorkbook(
            string openedFullName,
            bool readOnly,
            string expectedPrivateCopyPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(openedFullName);
            ArgumentException.ThrowIfNullOrWhiteSpace(expectedPrivateCopyPath);
            var actualPath = Path.GetFullPath(openedFullName);
            var expectedPath = Path.GetFullPath(expectedPrivateCopyPath);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!actualPath.Equals(expectedPath, comparison))
            {
                throw new InvalidOperationException(
                    $"Excel opened '{actualPath}' instead of the private copy '{expectedPath}'.");
            }

            if (!readOnly)
            {
                throw new InvalidOperationException(
                    $"Excel did not open the private copy read-only: {expectedPath}");
            }
        }

        public HostClassIdentityEnumeration EnumerateClasses(object host, object workbook)
        {
            object? projectObject = null;
            object? componentsObject = null;
            var components = new List<HostClassComponentDescriptor>();
            try
            {
                dynamic openedWorkbook = workbook;
                projectObject = openedWorkbook.VBProject;
                dynamic project = projectObject;
                string? vbaProjectName = null;
                var diagnostics = new List<HostClassInspectionDiagnostic>();
                try
                {
                    var observedName = (string)project.Name;
                    if (VbaIdentifier.IsIdentifier(observedName)
                        && observedName.EnumerateRunes().Take(32).Count() <= 31)
                    {
                        vbaProjectName = observedName;
                    }
                    else
                    {
                        diagnostics.Add(new HostClassInspectionDiagnostic(
                            "vbaProjectNameInvalid",
                            "The inspected VBA project name is not a valid module identifier."));
                    }
                }
                catch (Exception exception)
                {
                    diagnostics.Add(new HostClassInspectionDiagnostic(
                        "vbaProjectNameReadFailure",
                        $"The inspected VBA project name could not be read: {exception.Message}"));
                }

                componentsObject = project.VBComponents;
                dynamic collection = componentsObject;
                var count = (int)collection.Count;
                for (var ordinal = 1; ordinal <= count; ordinal++)
                {
                    object? componentObject = null;
                    try
                    {
                        componentObject = collection.Item(ordinal);
                        dynamic component = componentObject;
                        var kind = (int)component.Type switch
                        {
                            3 => HostClassComponentKind.Form,
                            100 => HostClassComponentKind.Document,
                            _ => (HostClassComponentKind?)null
                        };
                        if (kind is not null)
                        {
                            components.Add(new HostClassComponentDescriptor(
                                ordinal,
                                new HostClassIdentity((string)component.Name, kind.Value)));
                        }
                    }
                    finally
                    {
                        ComObjectReleaser.Release(componentObject);
                    }
                }

                return new HostClassIdentityEnumeration(
                    Complete: true,
                    Components: components,
                    Diagnostics: diagnostics)
                {
                    VbaProjectName = vbaProjectName
                };
            }
            finally
            {
                ComObjectReleaser.Release(componentsObject);
                ComObjectReleaser.Release(projectObject);
            }
        }

        public HostClassInspectionEntry InspectClass(
            object host,
            object workbook,
            HostClassComponentDescriptor component)
            => ExcelComIntrinsicHostClassInspector.Inspect(
                (ExcelComWorkbookSession.ExcelComHostObjects)host,
                workbook,
                component);

        public void CloseWorkbookWithoutSave(object workbook)
        {
            try
            {
                dynamic openedWorkbook = workbook;
                openedWorkbook.Close(false);
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

internal static class HostClassWorkbookSafetyPreflight
{
    private static readonly string[] UnsafeEntryPrefixes =
    [
        "xl/macrosheets/",
        "xl/dialogsheets/"
    ];
    private static readonly HashSet<string> UnsafeContentTypes = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "application/vnd.ms-excel.macrosheet+xml",
        "application/vnd.ms-excel.intlmacrosheet+xml",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.dialogsheet+xml"
    };
    private static readonly string[] UnsafeRelationshipTypeSuffixes =
    [
        "/xlMacrosheet",
        "/xlIntlMacrosheet",
        "/dialogsheet"
    ];

    public static void ThrowIfUnsafe(string workbookPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookPath);
        try
        {
            using var archive = ZipFile.OpenRead(workbookPath);
            if (archive.Entries.Any(entry => UnsafeEntryPrefixes.Any(prefix =>
                    entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) ||
                HasUnsafeContentType(archive) ||
                HasUnsafeRelationshipType(archive))
            {
                throw new InvalidOperationException(
                    "The source template contains an Excel 4.0 macro or dialog sheet that cannot be force-disabled for automation inspection.");
            }
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidOperationException(
                "The source template is not a valid Open XML workbook package and cannot cross the macro-disabled inspection boundary.",
                exception);
        }
    }

    private static bool HasUnsafeContentType(ZipArchive archive)
    {
        var contentTypesEntry = archive.Entries.SingleOrDefault(entry =>
            entry.FullName.Equals(
                "[Content_Types].xml",
                StringComparison.OrdinalIgnoreCase));
        if (contentTypesEntry is null)
        {
            throw new InvalidOperationException(
                "The source template has no Open XML content-type manifest and cannot cross the macro-disabled inspection boundary.");
        }

        using var stream = contentTypesEntry.Open();
        var contentTypes = XDocument.Load(stream, LoadOptions.None);
        return contentTypes
            .Descendants()
            .Select(element => (string?)element.Attribute("ContentType"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Any(value => UnsafeContentTypes.Contains(value!));
    }

    private static bool HasUnsafeRelationshipType(ZipArchive archive)
    {
        foreach (var relationshipEntry in archive.Entries.Where(entry =>
                     entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = relationshipEntry.Open();
            var relationships = XDocument.Load(stream, LoadOptions.None);
            var unsafeRelationship = relationships
                .Descendants()
                .Select(element => (string?)element.Attribute("Type"))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Any(value => UnsafeRelationshipTypeSuffixes.Any(suffix =>
                    value!.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)));
            if (unsafeRelationship)
            {
                return true;
            }
        }

        return false;
    }
}
