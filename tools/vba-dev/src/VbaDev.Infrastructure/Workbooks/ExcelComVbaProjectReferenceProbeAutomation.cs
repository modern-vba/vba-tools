using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Debugging;

namespace VbaDev.Infrastructure.Workbooks;

internal interface IExcelComVbaProjectReferenceProbeLifecycle
{
    object Start(
        OwnedExcelTerminationController terminationController,
        CancellationToken cancellationToken);

    object OpenWorkbook(object host, string workbookPath);

    object CreateBlankWorkbook(object host);

    object? FindReference(object workbook, string referenceName);

    object AddReference(object workbook, ResolvedVbaProjectReference candidate);

    ResolvedVbaProjectReference ReadIdentity(object reference, string referenceName);

    void ReleaseReference(object? reference);

    void CloseWorkbookWithoutSave(object workbook);

    void DisposeHost(object host, TimeSpan cleanupGrace);
}

internal sealed class VbaProjectReferenceCandidateRejectedException(
    string message,
    Exception? innerException = null) : Exception(message, innerException);

/// <summary>
/// Probes TypeLib candidates through fresh workbook copies in one exactly owned Excel process.
/// </summary>
public sealed class ExcelComVbaProjectReferenceProbeAutomation
    : IVbaProjectReferenceProbeAutomation
{
    private static readonly TimeSpan DispatcherAbandonmentObservation =
        TimeSpan.FromMilliseconds(100);
    private readonly IStaComDispatcherFactory dispatcherFactory;
    private readonly IExcelComVbaProjectReferenceProbeLifecycle lifecycle;

    /// <summary>
    /// Creates the production Excel/VBIDE ambiguity-probe adapter.
    /// </summary>
    public ExcelComVbaProjectReferenceProbeAutomation()
        : this(
            new StaComDispatcherFactory(),
            new ExcelComVbaProjectReferenceProbeLifecycle())
    {
    }

    internal ExcelComVbaProjectReferenceProbeAutomation(
        IStaComDispatcherFactory dispatcherFactory,
        IExcelComVbaProjectReferenceProbeLifecycle lifecycle)
    {
        this.dispatcherFactory = dispatcherFactory;
        this.lifecycle = lifecycle;
    }

    /// <inheritdoc />
    public async Task<TResult> RunAsync<TResult>(
        VbaProjectReferenceProbeBaseline baseline,
        WorkbookAutomationTimeouts timeouts,
        Func<IVbaProjectReferenceProbeSession, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(timeouts);
        ArgumentNullException.ThrowIfNull(operation);

        var workspace = baseline.Kind == VbaProjectReferenceProbeBaselineKind.SourceTemplate
            ? ReferenceProbeWorkspace.Create(baseline.WorkbookPath!)
            : null;
        using var terminationController = new OwnedExcelTerminationController();
        IStaComDispatcher dispatcher;
        try
        {
            dispatcher = dispatcherFactory.Create();
        }
        catch (Exception exception)
        {
            Exception? workspaceCleanupError = null;
            try
            {
                workspace?.Dispose();
            }
            catch (Exception cleanupException)
            {
                workspaceCleanupError = cleanupException;
            }

            if (workspaceCleanupError is not null)
            {
                throw new VbaProjectReferenceProbeAttemptException(
                    "cleanupFailure",
                    "The reference-probe workspace could not be removed after dispatcher creation failed.",
                    processTrusted: true,
                    new AggregateException(exception, workspaceCleanupError));
            }

            throw new VbaProjectReferenceProbeAttemptException(
                "excelVbeFailure",
                "The Excel STA dispatcher required for reference probing could not be created.",
                processTrusted: true,
                exception);
        }

        var stageExecutor = new WorkbookAutomationStageExecutor(
            () => terminationController.HasAttachedProcessExited,
            terminationController.RequestForcedTermination,
            getOwnedProcessCompletion: () =>
                terminationController.AttachedProcessCompletion,
            captureAutomationStage: terminationController.CaptureAutomationStage,
            describeIsolationEvidence: terminationController.DescribeIsolationEvidence);
        object? host = null;
        TResult? result = default;
        Exception? operationError = null;
        try
        {
            await stageExecutor.ExecuteAsync(
                new WorkbookAutomationStage(WorkbookAutomationStageKind.ExcelStartup),
                timeouts.ExcelStartup,
                timeouts.ProcessCleanup,
                cancellationToken,
                stageCancellation => dispatcher.InvokeAsync(
                    () =>
                    {
                        host = lifecycle.Start(terminationController, stageCancellation);
                        return true;
                    },
                    stageCancellation)).ConfigureAwait(false);

            var session = new BoundedReferenceProbeSession(
                dispatcher,
                stageExecutor,
                lifecycle,
                host!,
                baseline.Kind,
                workspace,
                timeouts,
                () => terminationController.HasAttachedProcessExited);
            result = await operation(session, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            operationError = NormalizeError(exception, cancellationToken);
        }

        var cleanupError = await CleanupAsync(
            dispatcher,
            terminationController,
            host,
            timeouts.ProcessCleanup,
            stageExecutor,
            operationError).ConfigureAwait(false);
        var dispatcherError = await DisposeDispatcherAsync(
            dispatcher,
            stageExecutor.HasAbandonedOperation).ConfigureAwait(false);
        if (dispatcherError is not null)
        {
            cleanupError = cleanupError is null
                ? dispatcherError
                : WorkbookAutomationFailureClassifier.ContainsCleanupProofFailure(
                    cleanupError)
                    ? new WorkbookAutomationCleanupException(
                        "Reference-probe cleanup and STA dispatcher disposal both failed, and exact owned-process release could not be proved.",
                        new AggregateException(cleanupError, dispatcherError))
                    : new WorkbookAutomationReleasedProcessCleanupException(
                        "The reference-probe process was released, but cleanup and STA dispatcher disposal both failed.",
                        new AggregateException(cleanupError, dispatcherError));
        }

        try
        {
            workspace?.Dispose();
        }
        catch (Exception exception)
        {
            var combinedCleanupError = cleanupError is null
                ? exception
                : new AggregateException(cleanupError, exception);
            cleanupError = cleanupError is not null &&
                WorkbookAutomationFailureClassifier.ContainsCleanupProofFailure(
                    cleanupError)
                    ? new WorkbookAutomationCleanupException(
                        "The reference probe could not verify exact owned-process release, and its workspace cleanup also failed.",
                        combinedCleanupError)
                    : new WorkbookAutomationReleasedProcessCleanupException(
                        "The reference-probe Excel process was released, but its workspace cleanup failed.",
                        combinedCleanupError);
        }

        if (cleanupError is not null)
        {
            var releaseVerified =
                !WorkbookAutomationFailureClassifier.ContainsCleanupProofFailure(
                    cleanupError);
            throw new VbaProjectReferenceProbeAttemptException(
                "cleanupFailure",
                releaseVerified
                    ? "The reference-probe Excel process was released, but cooperative cleanup or automation isolation failed."
                    : "The reference probe could not prove cleanup of its workbook copies and owned Excel process.",
                processTrusted: false,
                operationError is null
                    ? cleanupError
                    : new AggregateException(operationError, cleanupError),
                partialResult: result);
        }

        if (operationError is not null)
        {
            ExceptionDispatchInfo.Capture(operationError).Throw();
        }

        return result!;
    }

    private async Task<Exception?> CleanupAsync(
        IStaComDispatcher dispatcher,
        OwnedExcelTerminationController terminationController,
        object? host,
        TimeSpan cleanupGrace,
        WorkbookAutomationStageExecutor stageExecutor,
        Exception? operationError)
    {
        var cleanupStage = new WorkbookAutomationStage(
            WorkbookAutomationStageKind.ProcessCleanup);
        terminationController.CaptureAutomationStage(cleanupStage);
        if (stageExecutor.HasAbandonedOperation ||
            terminationController.HasAttachedProcessExited ||
            host is null)
        {
            return await CleanupOwnedProcessOnlyAsync(
                terminationController,
                cleanupGrace).ConfigureAwait(false);
        }

        terminationController.RequestForcedTermination(cleanupGrace);
        Exception? cleanupError = null;
        try
        {
            var cleanupTask = dispatcher.InvokeAsync(
                () =>
                {
                    lifecycle.DisposeHost(host, cleanupGrace);
                    return true;
                },
                CancellationToken.None);
            var completed = await Task.WhenAny(
                cleanupTask,
                Task.Delay(
                    cleanupGrace +
                    PrivateDesktopOwnedExcelProcessControl.ForcedCleanupObservationAllowance))
                .ConfigureAwait(false);
            if (completed != cleanupTask)
            {
                stageExecutor.MarkOperationAbandoned();
                WorkbookAutomationStageExecutor.ObserveFault(cleanupTask);
                cleanupError = new WorkbookAutomationTimeoutException(
                    cleanupStage,
                    cleanupGrace,
                    isolationDiagnostics:
                        terminationController.DescribeIsolationEvidence());
            }
            else
            {
                await cleanupTask.ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            cleanupError = exception;
        }

        var ownershipError = await CleanupOwnedProcessOnlyAsync(
            terminationController,
            cleanupGrace).ConfigureAwait(false);
        if (cleanupError is null)
        {
            return ownershipError;
        }

        if (ownershipError is null &&
            ReleasedAutomationCleanupPolicy.CanPreservePrimaryFailure(
                operationError,
                cleanupError))
        {
            return null;
        }

        return ownershipError is null
            ? cleanupError
            : WorkbookAutomationFailureClassifier.ContainsCleanupProofFailure(
                ownershipError)
                ? new WorkbookAutomationCleanupException(
                    "Cooperative reference-probe cleanup and exact owned-process cleanup both failed.",
                    new AggregateException(cleanupError, ownershipError))
                : new WorkbookAutomationReleasedProcessCleanupException(
                    "Cooperative reference-probe cleanup and exact owned-process cleanup both failed after exact owned-process release was verified.",
                    new AggregateException(cleanupError, ownershipError));
    }

    private static async Task<Exception?> CleanupOwnedProcessOnlyAsync(
        OwnedExcelTerminationController terminationController,
        TimeSpan cleanupGrace)
    {
        try
        {
            terminationController.RequestForcedTermination(cleanupGrace);
            await terminationController.ObserveCleanupWithinAsync(
                PrivateDesktopOwnedExcelProcessControl.ForcedCleanupObservationAllowance)
                .ConfigureAwait(false);
            return null;
        }
        catch (WorkbookAutomationReleasedProcessCleanupException exception)
        {
            return exception;
        }
        catch (Exception exception)
        {
            return new WorkbookAutomationCleanupException(
                "The reference probe could not verify exact owned-process release.",
                exception);
        }
    }

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
            return new WorkbookAutomationReleasedProcessCleanupException(
                "The reference-probe process was released, but its STA dispatcher could not be disposed cleanly.",
                exception);
        }
    }

    private static Exception NormalizeError(
        Exception exception,
        CancellationToken cancellationToken)
        => exception switch
        {
            VbaProjectReferenceProbeBaselineException or
            VbaProjectReferenceProbeAttemptException => exception,
            WorkbookAutomationTimeoutException =>
                new VbaProjectReferenceProbeAttemptException(
                    "probeTimeout",
                    exception.Message,
                    processTrusted: false,
                    exception),
            WorkbookAutomationProcessLostException =>
                new VbaProjectReferenceProbeAttemptException(
                    "excelVbeFailure",
                    exception.Message,
                    processTrusted: false,
                    exception),
            WorkbookAutomationCleanupException or
            WorkbookAutomationReleasedProcessCleanupException =>
                new VbaProjectReferenceProbeAttemptException(
                    "cleanupFailure",
                    exception.Message,
                    processTrusted: false,
                    exception),
            OperationCanceledException when cancellationToken.IsCancellationRequested =>
                exception,
            _ => new VbaProjectReferenceProbeAttemptException(
                "excelVbeFailure",
                exception.Message,
                processTrusted: false,
                exception)
        };

    private static Exception NormalizeIdentityReadError(
        Exception exception,
        CancellationToken cancellationToken)
        => exception switch
        {
            VbaProjectReferenceProbeBaselineException or
            VbaProjectReferenceProbeAttemptException or
            WorkbookAutomationTimeoutException or
            WorkbookAutomationProcessLostException or
            WorkbookAutomationCleanupException or
            WorkbookAutomationReleasedProcessCleanupException or
            OperationCanceledException => NormalizeError(exception, cancellationToken),
            _ => new VbaProjectReferenceProbeAttemptException(
                "identityReadFailure",
                "The concrete identity returned by VBE could not be read.",
                processTrusted: true,
                exception)
        };

    private static Exception NormalizeBaselineOpenError(
        Exception exception,
        VbaProjectReferenceProbeBaselineKind baselineKind,
        CancellationToken cancellationToken)
        => exception switch
        {
            VbaProjectReferenceProbeBaselineException or
            VbaProjectReferenceProbeAttemptException or
            WorkbookAutomationTimeoutException or
            WorkbookAutomationProcessLostException or
            WorkbookAutomationCleanupException or
            WorkbookAutomationReleasedProcessCleanupException or
            OperationCanceledException => NormalizeError(exception, cancellationToken),
            _ => new VbaProjectReferenceProbeBaselineException(
                baselineKind == VbaProjectReferenceProbeBaselineKind.BlankWorkbook
                    ? "A fresh blank-workbook baseline could not be created or inspected by Excel/VBE."
                    : "A fresh copy of the selected source-template baseline could not be opened by Excel/VBE.",
                exception)
        };

    private sealed class BoundedReferenceProbeSession(
        IStaComDispatcher dispatcher,
        WorkbookAutomationStageExecutor stageExecutor,
        IExcelComVbaProjectReferenceProbeLifecycle lifecycle,
        object host,
        VbaProjectReferenceProbeBaselineKind baselineKind,
        ReferenceProbeWorkspace? workspace,
        WorkbookAutomationTimeouts timeouts,
        Func<bool> hasOwnedProcessExited) : IVbaProjectReferenceProbeSession
    {
        public async Task<VbaProjectReferenceProbeAttemptResult> TryResolveAsync(
            string referenceName,
            ResolvedVbaProjectReference candidate,
            CancellationToken cancellationToken)
        {
            var attemptPath = baselineKind == VbaProjectReferenceProbeBaselineKind.SourceTemplate
                ? workspace!.CreateAttemptCopy()
                : null;
            var attemptName = attemptPath is null
                ? "blank workbook"
                : Path.GetFileName(attemptPath);
            object? workbook = null;
            object? reference = null;
            VbaProjectReferenceProbeAttemptResult? result = null;
            Exception? operationError = null;
            try
            {
                try
                {
                    workbook = await ExecuteAsync(
                        new WorkbookAutomationStage(
                            WorkbookAutomationStageKind.WorkbookOpen,
                            attemptName),
                        timeouts.WorkbookOpen,
                        cancellationToken,
                        () => attemptPath is null
                            ? lifecycle.CreateBlankWorkbook(host)
                            : lifecycle.OpenWorkbook(host, attemptPath)).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    throw NormalizeBaselineOpenError(
                        exception,
                        baselineKind,
                        cancellationToken);
                }

                try
                {
                    reference = await ExecuteAsync(
                        new WorkbookAutomationStage(
                            WorkbookAutomationStageKind.ReferenceIdentityInspection,
                            referenceName),
                        timeouts.ReferenceAttempt,
                        cancellationToken,
                        () => lifecycle.FindReference(workbook, referenceName)).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    throw NormalizeBaselineOpenError(
                        exception,
                        baselineKind,
                        cancellationToken);
                }

                if (reference is null)
                {
                    try
                    {
                        reference = await ExecuteAsync(
                            new WorkbookAutomationStage(
                                WorkbookAutomationStageKind.ReferenceAttempt,
                                referenceName),
                            timeouts.ReferenceAttempt,
                            cancellationToken,
                            () => lifecycle.AddReference(workbook, candidate)).ConfigureAwait(false);
                    }
                    catch (VbaProjectReferenceCandidateRejectedException)
                    {
                        result = VbaProjectReferenceProbeAttemptResult.Rejected();
                    }
                }

                if (result is null)
                {
                    ResolvedVbaProjectReference identity;
                    try
                    {
                        identity = await ExecuteAsync(
                            new WorkbookAutomationStage(
                                WorkbookAutomationStageKind.ReferenceIdentityInspection,
                                referenceName),
                            timeouts.ReferenceAttempt,
                            cancellationToken,
                            () => lifecycle.ReadIdentity(reference!, referenceName)).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        throw NormalizeIdentityReadError(exception, cancellationToken);
                    }

                    result = VbaProjectReferenceProbeAttemptResult.Accepted(identity);
                }
            }
            catch (Exception exception)
            {
                operationError = NormalizeError(exception, cancellationToken);
            }

            Exception? cleanupError = null;
            if (!stageExecutor.HasAbandonedOperation && !hasOwnedProcessExited())
            {
                try
                {
                    await stageExecutor.ExecuteAsync(
                        new WorkbookAutomationStage(
                            WorkbookAutomationStageKind.ProcessCleanup,
                            attemptName),
                        timeouts.ProcessCleanup,
                        timeouts.ProcessCleanup,
                        CancellationToken.None,
                        stageCancellation => dispatcher.InvokeAsync(
                            () =>
                            {
                                lifecycle.ReleaseReference(reference);
                                if (workbook is not null)
                                {
                                    lifecycle.CloseWorkbookWithoutSave(workbook);
                                }

                                return true;
                            },
                            stageCancellation)).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    cleanupError = exception;
                }
            }

            if (cleanupError is null &&
                attemptPath is not null &&
                (!stageExecutor.HasAbandonedOperation || hasOwnedProcessExited()))
            {
                try
                {
                    workspace!.DeleteAttemptCopy(attemptPath);
                }
                catch (Exception exception)
                {
                    cleanupError = exception;
                }
            }

            if (cleanupError is not null)
            {
                throw new VbaProjectReferenceProbeAttemptException(
                    "cleanupFailure",
                    "The fresh reference-probe baseline could not be closed or removed.",
                    processTrusted: false,
                    operationError is null
                        ? cleanupError
                        : new AggregateException(operationError, cleanupError));
            }

            if (operationError is not null)
            {
                ExceptionDispatchInfo.Capture(operationError).Throw();
            }

            return result!;
        }

        private Task<T> ExecuteAsync<T>(
            WorkbookAutomationStage stage,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            Func<T> operation)
            => stageExecutor.ExecuteAsync(
                stage,
                timeout,
                timeouts.ProcessCleanup,
                cancellationToken,
                stageCancellation => dispatcher.InvokeAsync(
                    operation,
                    stageCancellation));
    }

    private sealed class ReferenceProbeWorkspace : IDisposable
    {
        private readonly string workspacePath;
        private readonly string fixedBaselinePath;
        private readonly List<string> attemptPaths = [];
        private int attemptOrdinal;
        private bool disposed;

        private ReferenceProbeWorkspace(
            string workspacePath,
            string fixedBaselinePath)
        {
            this.workspacePath = workspacePath;
            this.fixedBaselinePath = fixedBaselinePath;
        }

        public static ReferenceProbeWorkspace Create(string baselineWorkbookPath)
        {
            var sourcePath = Path.GetFullPath(baselineWorkbookPath);
            var workspacePath = Path.Combine(
                Path.GetTempPath(),
                "vba-dev-reference-probe",
                Guid.NewGuid().ToString("N"));
            var fixedBaselinePath = Path.Combine(
                workspacePath,
                "baseline" + Path.GetExtension(sourcePath));
            try
            {
                if (!File.Exists(sourcePath))
                {
                    throw new FileNotFoundException(
                        "The selected source-template workbook was not found.",
                        sourcePath);
                }

                Directory.CreateDirectory(workspacePath);
                File.Copy(sourcePath, fixedBaselinePath, overwrite: false);
                return new ReferenceProbeWorkspace(workspacePath, fixedBaselinePath);
            }
            catch (Exception exception)
            {
                try
                {
                    if (File.Exists(fixedBaselinePath))
                    {
                        File.Delete(fixedBaselinePath);
                    }

                    if (Directory.Exists(workspacePath))
                    {
                        Directory.Delete(workspacePath, recursive: false);
                    }
                }
                catch
                {
                    // The baseline exception retains the original preparation failure.
                }

                throw new VbaProjectReferenceProbeBaselineException(
                    $"The selected source-template workbook baseline could not be prepared: {sourcePath}",
                    exception);
            }
        }

        public string CreateAttemptCopy()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var attemptPath = Path.Combine(
                workspacePath,
                $"attempt-{++attemptOrdinal:D4}-{Guid.NewGuid():N}{Path.GetExtension(fixedBaselinePath)}");
            try
            {
                File.Copy(fixedBaselinePath, attemptPath, overwrite: false);
                attemptPaths.Add(attemptPath);
                return attemptPath;
            }
            catch (Exception exception)
            {
                throw new VbaProjectReferenceProbeBaselineException(
                    "A fresh copy of the selected source-template baseline could not be prepared.",
                    exception);
            }
        }

        public void DeleteAttemptCopy(string attemptPath)
        {
            if (File.Exists(attemptPath))
            {
                File.Delete(attemptPath);
            }

            if (File.Exists(attemptPath))
            {
                throw new IOException(
                    $"The reference-probe baseline copy remained after deletion: {attemptPath}");
            }

            attemptPaths.Remove(attemptPath);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            foreach (var attemptPath in attemptPaths)
            {
                if (File.Exists(attemptPath))
                {
                    File.Delete(attemptPath);
                }
            }

            attemptPaths.Clear();
            if (File.Exists(fixedBaselinePath))
            {
                File.Delete(fixedBaselinePath);
            }

            if (Directory.Exists(workspacePath))
            {
                Directory.Delete(workspacePath, recursive: false);
            }
        }
    }

    internal sealed class ExcelComVbaProjectReferenceProbeLifecycle
        : IExcelComVbaProjectReferenceProbeLifecycle
    {
        private const int TypeLibNotRegistered = unchecked((int)0x8002801D);

        public object Start(
            OwnedExcelTerminationController terminationController,
            CancellationToken cancellationToken)
            => ExcelComWorkbookSession.StartOwnedForGeneration(
                terminationController,
                cancellationToken);

        public object OpenWorkbook(object host, string workbookPath)
        {
            var excelHost = (ExcelComWorkbookSession.ExcelComHostObjects)host;
            dynamic workbooks = excelHost.WorkbooksObject;
            return workbooks.Open(workbookPath, 0, false);
        }

        public object CreateBlankWorkbook(object host)
        {
            var excelHost = (ExcelComWorkbookSession.ExcelComHostObjects)host;
            dynamic workbooks = excelHost.WorkbooksObject;
            return workbooks.Add();
        }

        public object? FindReference(object workbook, string referenceName)
        {
            object? vbProjectObject = null;
            object? referencesObject = null;
            try
            {
                dynamic workbookObject = workbook;
                vbProjectObject = workbookObject.VBProject;
                dynamic vbProject = vbProjectObject;
                referencesObject = vbProject.References;
                dynamic references = referencesObject;
                var count = (int)references.Count;
                for (var index = 1; index <= count; index++)
                {
                    object? reference = null;
                    try
                    {
                        reference = references.Item(index);
                        dynamic referenceObject = reference;
                        var description = Convert.ToString(referenceObject.Description);
                        if (referenceName.Equals(
                                description?.Trim(),
                                StringComparison.OrdinalIgnoreCase))
                        {
                            var result = reference;
                            reference = null;
                            return result;
                        }
                    }
                    finally
                    {
                        ComObjectReleaser.Release(reference);
                    }
                }

                return null;
            }
            finally
            {
                ComObjectReleaser.Release(referencesObject);
                ComObjectReleaser.Release(vbProjectObject);
            }
        }

        public object AddReference(
            object workbook,
            ResolvedVbaProjectReference candidate)
        {
            object? vbProjectObject = null;
            object? referencesObject = null;
            try
            {
                dynamic workbookObject = workbook;
                vbProjectObject = workbookObject.VBProject;
                dynamic vbProject = vbProjectObject;
                referencesObject = vbProject.References;
                dynamic references = referencesObject;
                try
                {
                    return references.AddFromGuid(
                        Guid.Parse(candidate.Guid).ToString("B"),
                        candidate.Major,
                        candidate.Minor);
                }
                catch (COMException exception)
                    when (exception.HResult == TypeLibNotRegistered)
                {
                    throw new VbaProjectReferenceCandidateRejectedException(
                        $"VBE rejected TypeLib candidate {candidate.Guid} {candidate.Major}.{candidate.Minor}.",
                        exception);
                }
            }
            finally
            {
                ComObjectReleaser.Release(referencesObject);
                ComObjectReleaser.Release(vbProjectObject);
            }
        }

        public ResolvedVbaProjectReference ReadIdentity(
            object reference,
            string referenceName)
        {
            dynamic referenceObject = reference;
            var guid = Convert.ToString(referenceObject.Guid)
                       ?? throw new InvalidOperationException(
                           "The returned VBE reference did not expose a GUID.");
            var major = Convert.ToInt32(referenceObject.Major);
            var minor = Convert.ToInt32(referenceObject.Minor);
            return new ResolvedVbaProjectReference(
                referenceName,
                guid,
                major,
                minor);
        }

        public void ReleaseReference(object? reference)
            => ComObjectReleaser.Release(reference);

        public void CloseWorkbookWithoutSave(object workbook)
        {
            try
            {
                dynamic workbookObject = workbook;
                workbookObject.Close(false);
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
