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
    private readonly AutomationExcelProcessRuntime runtime;
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
        runtime = new AutomationExcelProcessRuntime(dispatcherFactory);
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
        TResult? result = default;
        var outcome = await runtime.RunReferenceProbeAsync(
            lifecycle,
            timeouts,
            async (execution, token) =>
            {
                var session = new BoundedReferenceProbeSession(
                    execution, lifecycle, baseline.Kind, workspace, timeouts);
                result = await operation(session, token).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
        var evidence = outcome.Evidence;
        var operationError = evidence.OperationFailure is null
            ? null
            : !evidence.DispatcherCreated
                ? new VbaProjectReferenceProbeAttemptException(
                    "excelVbeFailure",
                    "The Excel STA dispatcher required for reference probing could not be created.",
                    processTrusted: true,
                    evidence.OperationFailure)
                : NormalizeError(evidence.OperationFailure, cancellationToken);
        var cleanupError = evidence.CleanupFailure;
        if (evidence.DispatcherFailure is { } dispatcherError)
        {
            cleanupError = cleanupError is null
                ? dispatcherError
                : !evidence.ProcessReleaseVerified
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
            cleanupError = !evidence.ProcessReleaseVerified
                ? new WorkbookAutomationCleanupException(
                    "The reference probe could not verify exact owned-process release, and its workspace cleanup also failed.",
                    combinedCleanupError)
                : new WorkbookAutomationReleasedProcessCleanupException(
                    "The reference-probe Excel process was released, but its workspace cleanup failed.",
                    combinedCleanupError);
        }

        if (cleanupError is not null)
        {
            throw new VbaProjectReferenceProbeAttemptException(
                "cleanupFailure",
                evidence.ProcessReleaseVerified
                    ? "The reference-probe Excel process was released, but cooperative cleanup or automation isolation failed."
                    : "The reference probe could not prove cleanup of its workbook copies and owned Excel process.",
                processTrusted: !evidence.DispatcherCreated,
                operationError is null
                    ? cleanupError
                    : new AggregateException(operationError, cleanupError),
                partialResult: result);
        }

        if (operationError is not null)
        {
            ExceptionDispatchInfo.Capture(operationError).Throw();
        }

        // A cancellation first observed during final cleanup does not replace
        // the completed reference classification. The runtime only reports it.
        return result!;
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
        AutomationExcelProcessRuntime.AutomationExcelProcessSession execution,
        IExcelComVbaProjectReferenceProbeLifecycle lifecycle,
        VbaProjectReferenceProbeBaselineKind baselineKind,
        ReferenceProbeWorkspace? workspace,
        WorkbookAutomationTimeouts timeouts) : IVbaProjectReferenceProbeSession
    {
        public async Task<VbaProjectReferenceProbeAttemptResult> TryResolveAsync(
            string referenceName,
            ResolvedVbaProjectReference candidate,
            CancellationToken cancellationToken)
        {
            execution.ThrowIfRetired();
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
                    workbook = await execution.ExecuteAsync(
                        new WorkbookAutomationStage(
                            WorkbookAutomationStageKind.WorkbookOpen,
                            attemptName),
                        timeouts.WorkbookOpen,
                        cancellationToken,
                        host => attemptPath is null
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
                    reference = await execution.ExecuteAsync(
                        new WorkbookAutomationStage(
                            WorkbookAutomationStageKind.ReferenceIdentityInspection,
                            referenceName),
                        timeouts.ReferenceAttempt,
                        cancellationToken,
                        _ => lifecycle.FindReference(workbook, referenceName)).ConfigureAwait(false);
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
                        reference = await execution.ExecuteAsync(
                            new WorkbookAutomationStage(
                                WorkbookAutomationStageKind.ReferenceAttempt,
                                referenceName),
                            timeouts.ReferenceAttempt,
                            cancellationToken,
                            _ => lifecycle.AddReference(workbook, candidate)).ConfigureAwait(false);
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
                        identity = await execution.ExecuteAsync(
                            new WorkbookAutomationStage(
                                WorkbookAutomationStageKind.ReferenceIdentityInspection,
                                referenceName),
                            timeouts.ReferenceAttempt,
                            cancellationToken,
                            _ => lifecycle.ReadIdentity(reference!, referenceName)).ConfigureAwait(false);
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
            if (!execution.HasAbandonedOperation && !execution.HasOwnedProcessExited)
            {
                try
                {
                    await execution.ExecuteAsync(
                        new WorkbookAutomationStage(
                            WorkbookAutomationStageKind.ProcessCleanup,
                            attemptName),
                        timeouts.ProcessCleanup,
                        CancellationToken.None,
                        _ =>
                            {
                                lifecycle.ReleaseReference(reference);
                                if (workbook is not null)
                                {
                                    lifecycle.CloseWorkbookWithoutSave(workbook);
                                }

                                return true;
                            }).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    cleanupError = exception;
                }
            }

            if (cleanupError is null &&
                attemptPath is not null &&
                (!execution.HasAbandonedOperation || execution.HasOwnedProcessExited))
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
