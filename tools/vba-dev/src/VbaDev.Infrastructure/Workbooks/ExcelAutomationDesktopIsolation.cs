using System.Runtime.InteropServices;
using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Debugging;

namespace VbaDev.Infrastructure.Workbooks;

internal interface IExcelAutomationDesktopIsolationFactory
{
    IExcelAutomationDesktopIsolation Create();
}

internal interface IExcelAutomationDesktopIsolation : IAsyncDisposable
{
    string QualifiedDesktopName { get; }

    nint DesktopHandle { get; }

    Task StartObservingBeforeResumeAsync(
        int exactProcessId,
        CancellationToken cancellationToken);

    Task<DesktopWindowExposureEvidence> CompleteAfterExitAsync(
        Task exactProcessExit,
        CancellationToken cancellationToken);
}

internal interface IExcelAutomationDesktopEvidence
{
    DesktopWindowExposureEvidence Evidence { get; }

    void Capture(DesktopWindowLifecyclePhase phase);
}

internal interface IExcelAutomationDesktopProcessControl
{
    void Capture(DesktopWindowLifecyclePhase phase);

    string DescribeCurrentEvidence();
}

internal static class ReleasedAutomationCleanupPolicy
{
    // Callers must prove exact owned-process release before applying this policy.
    public static bool CanPreservePrimaryFailure(
        Exception? primaryFailure,
        Exception cooperativeCleanupError)
    {
        if (primaryFailure is null ||
            !ContainsTerminalFailure(primaryFailure))
        {
            return false;
        }

        return ContainsOnlyPostTerminationComFailureLeaves(cooperativeCleanupError);
    }

    private static bool ContainsTerminalFailure(Exception error)
        => ContainsException<WorkbookAutomationTimeoutException>(error) ||
           ContainsException<WorkbookAutomationCanceledException>(error) ||
           ContainsException<WorkbookAutomationProcessLostException>(error);

    private static bool ContainsOnlyPostTerminationComFailureLeaves(Exception error)
    {
        if (error is AggregateException aggregate)
        {
            return aggregate.InnerExceptions.Count > 0 &&
                   aggregate.InnerExceptions.All(ContainsOnlyPostTerminationComFailureLeaves);
        }

        if (error is WorkbookAutomationReleasedProcessCleanupException releasedCleanup &&
            releasedCleanup.InnerException is not null)
        {
            return ContainsOnlyPostTerminationComFailureLeaves(
                releasedCleanup.InnerException);
        }

        return error.InnerException is null &&
               error is (COMException or
                   InvalidComObjectException or
                   MissingMemberException);
    }

    private static bool ContainsException<TException>(Exception error)
        where TException : Exception
    {
        if (error is TException)
        {
            return true;
        }

        if (error is AggregateException aggregate &&
            aggregate.InnerExceptions.Any(ContainsException<TException>))
        {
            return true;
        }

        return error.InnerException is not null &&
               ContainsException<TException>(error.InnerException);
    }
}

internal sealed class WindowsExcelAutomationDesktopIsolationFactory
    : IExcelAutomationDesktopIsolationFactory
{
    private WindowsExcelAutomationDesktopIsolationFactory()
    {
    }

    public static WindowsExcelAutomationDesktopIsolationFactory Instance { get; } = new();

    public IExcelAutomationDesktopIsolation Create()
        => WindowsExcelAutomationDesktopIsolation.Create();
}

/// <summary>
/// Owns the invocation-scoped desktop and exact-PID exposure observer used by
/// one non-debug Excel automation process.
/// </summary>
internal sealed class WindowsExcelAutomationDesktopIsolation
    : IExcelAutomationDesktopIsolation,
      IExcelAutomationDesktopEvidence
{
    private readonly WindowsPrivateDesktopLease privateDesktop;
    private readonly DesktopWindowObservationScope callerDesktop;
    private readonly DesktopWindowObservationScope privateDesktopScope;
    private readonly IDesktopWindowObservationNativeApi nativeApi;
    private readonly SemaphoreSlim disposalGate = new(1, 1);
    private OwnedDesktopWindowExposureObserver? observer;
    private DesktopWindowExposureEvidence? completedEvidence;
    private int observationStarted;
    private int disposed;

    private WindowsExcelAutomationDesktopIsolation(
        WindowsPrivateDesktopLease privateDesktop,
        DesktopWindowObservationScope callerDesktop,
        IDesktopWindowObservationNativeApi nativeApi)
    {
        this.privateDesktop = privateDesktop;
        this.callerDesktop = callerDesktop;
        this.nativeApi = nativeApi;
        privateDesktopScope = new DesktopWindowObservationScope(
            privateDesktop.Handle,
            privateDesktop.QualifiedName,
            DesktopWindowLocation.Private);
    }

    public string QualifiedDesktopName => privateDesktop.QualifiedName;

    public nint DesktopHandle => privateDesktop.Handle;

    public DesktopWindowExposureEvidence Evidence
        => completedEvidence
            ?? observer?.Evidence
            ?? new DesktopWindowExposureEvidence(0, []);

    public static WindowsExcelAutomationDesktopIsolation Create()
    {
        var nativeApi = WindowsDesktopWindowObservationNativeApi.Instance;
        var callerDesktop = nativeApi.CaptureCurrentThreadDesktop();
        WindowsPrivateDesktopLease? privateDesktop = null;
        try
        {
            privateDesktop = WindowsPrivateDesktopLease.Create();
            return new WindowsExcelAutomationDesktopIsolation(
                privateDesktop,
                callerDesktop,
                nativeApi);
        }
        catch
        {
            privateDesktop?.Dispose();
            throw;
        }
    }

    public async Task StartObservingBeforeResumeAsync(
        int exactProcessId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.Exchange(ref observationStarted, 1) != 0)
        {
            throw new InvalidOperationException(
                "Private-desktop observation has already been started.");
        }

        try
        {
            observer = await OwnedDesktopWindowExposureObserver.StartAsync(
                    nativeApi,
                    exactProcessId,
                    callerDesktop,
                    privateDesktopScope,
                    DesktopWindowLifecyclePhase.BeforePrimaryThreadResume,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Exchange(ref observationStarted, 0);
            throw;
        }
    }

    public void Capture(DesktopWindowLifecyclePhase phase)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        (observer ?? throw new InvalidOperationException(
            "Private-desktop observation has not been started.")).Capture(phase);
    }

    public async Task<DesktopWindowExposureEvidence> CompleteAfterExitAsync(
        Task exactProcessExit,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (completedEvidence is not null)
        {
            return completedEvidence;
        }

        var activeObserver = observer ?? throw new InvalidOperationException(
            "Private-desktop observation has not been started.");
        completedEvidence = await activeObserver.CompleteAfterExitAsync(
                exactProcessExit,
                cancellationToken)
            .ConfigureAwait(false);
        return completedEvidence;
    }

    public async ValueTask DisposeAsync()
    {
        await disposalGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            Exception? cleanupError = null;
            if (observer is not null)
            {
                try
                {
                    await observer.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    cleanupError = ex;
                }
            }

            try
            {
                privateDesktop.Dispose();
            }
            catch (Exception ex)
            {
                cleanupError = Combine(cleanupError, ex);
            }

            if (cleanupError is not null)
            {
                throw new WorkbookAutomationCleanupException(
                    "The private Excel automation desktop could not be released.",
                    cleanupError);
            }

            Volatile.Write(ref disposed, 1);
        }
        finally
        {
            disposalGate.Release();
        }
    }

    private static Exception Combine(Exception? current, Exception next)
        => current is null ? next : new AggregateException(current, next);
}

/// <summary>
/// Keeps exact process-tree ownership, desktop observation, and the desktop
/// handle alive as one cleanup boundary.
/// </summary>
internal sealed class PrivateDesktopOwnedExcelProcessControl(
    DebugExcelProcessOwner owner,
    IExcelAutomationDesktopIsolation desktopIsolation)
    : IOwnedExcelProcessControl,
      IExcelAutomationDesktopProcessControl
{
    private static readonly TimeSpan ProcessTreeCleanupTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ExitObservationTimeout = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim disposalGate = new(1, 1);
    private int disposed;

    // Covers bounded process-tree termination, exit observation, and scheduler slack.
    internal static TimeSpan ForcedCleanupObservationAllowance { get; } =
        ProcessTreeCleanupTimeout + ExitObservationTimeout + TimeSpan.FromSeconds(1);

    public bool HasExited
    {
        get
        {
            try
            {
                return owner.HasExited;
            }
            catch (Exception ex) when (
                ex is ObjectDisposedException or InvalidOperationException)
            {
                if (owner.Completion.IsCompletedSuccessfully)
                {
                    return true;
                }

                throw;
            }
        }
    }

    public Task Completion => owner.Completion;

    public void Capture(DesktopWindowLifecyclePhase phase)
    {
        if (desktopIsolation is not IExcelAutomationDesktopEvidence evidence)
        {
            return;
        }

        evidence.Capture(phase);
    }

    public Task TerminateAsync()
        => owner.TerminateProcessTreeAsync(ProcessTreeCleanupTimeout).AsTask();

    public string DescribeCurrentEvidence()
    {
        var observations = desktopIsolation is IExcelAutomationDesktopEvidence evidence
            ? evidence.Evidence.Observations
            : [];
        var latestWindows = observations
            .GroupBy(static observation => (
                observation.WindowHandle,
                observation.Desktop))
            .Select(static group => group.MaxBy(observation => observation.Sequence)!)
            .Where(static observation =>
                observation.IsVisible &&
                observation.Cause is not DesktopWindowObservationCause.WinEventHide and
                    not DesktopWindowObservationCause.WinEventDestroy)
            .OrderBy(static observation => observation.Location)
            .ThenBy(static observation => observation.WindowHandle.ToInt64())
            .Take(8)
            .Select(FormatObservation)
            .ToArray();
        var windows = latestWindows.Length == 0
            ? "no exact-PID top-level windows were present in the latest snapshot"
            : string.Join("; ", latestWindows);
        return $"Automation Excel isolation evidence: PID={owner.ProcessId}, " +
               $"privateDesktop='{desktopIsolation.QualifiedDesktopName}', {windows}.";
    }

    public async ValueTask DisposeAsync()
    {
        await disposalGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            var cleanupErrors = new List<Exception>();
            try
            {
                await owner.TerminateProcessTreeAsync(ProcessTreeCleanupTimeout)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                cleanupErrors.Add(ex);
            }

            DesktopWindowExposureEvidence? finalEvidence = null;
            Exception? observationCompletionError = null;
            try
            {
                using var completionTimeout = new CancellationTokenSource(
                    ExitObservationTimeout);
                finalEvidence = await desktopIsolation.CompleteAfterExitAsync(
                        owner.Completion,
                        completionTimeout.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                observationCompletionError = ex;
            }

            try
            {
                await owner.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                cleanupErrors.Add(ex);
            }

            try
            {
                await desktopIsolation.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                cleanupErrors.Add(ex);
            }

            WorkbookAutomationReleasedProcessCleanupException? isolationViolation = null;
            if (finalEvidence is not null)
            {
                try
                {
                    ThrowIfWindowsRemainAfterExit(finalEvidence);
                }
                catch (Exception ex)
                {
                    cleanupErrors.Add(ex);
                }

                try
                {
                    ThrowIfCallerDesktopExposure(finalEvidence);
                }
                catch (WorkbookAutomationReleasedProcessCleanupException ex)
                {
                    isolationViolation = ex;
                }
            }

            if (cleanupErrors.Count > 0)
            {
                if (observationCompletionError is not null)
                {
                    cleanupErrors.Add(observationCompletionError);
                }

                if (isolationViolation is not null)
                {
                    cleanupErrors.Add(isolationViolation);
                }

                throw cleanupErrors.Count == 1
                    ? cleanupErrors[0]
                    : new WorkbookAutomationCleanupException(
                        "Private-desktop Excel automation cleanup failed.",
                        new AggregateException(cleanupErrors));
            }

            Volatile.Write(ref disposed, 1);
            if (observationCompletionError is not null)
            {
                throw new WorkbookAutomationReleasedProcessCleanupException(
                    "Desktop observation could not be completed after exact Excel process and isolation-resource release was verified.",
                    observationCompletionError);
            }

            if (isolationViolation is not null)
            {
                throw isolationViolation;
            }
        }
        finally
        {
            disposalGate.Release();
        }
    }

    private static void ThrowIfCallerDesktopExposure(
        DesktopWindowExposureEvidence evidence)
    {
        if (!evidence.HasCallerDesktopExposure)
        {
            return;
        }

        var observations = evidence.Observations
            .Where(static observation =>
                observation.Location == DesktopWindowLocation.CallerInteractive &&
                (observation.Cause is DesktopWindowObservationCause.WinEventShow or
                        DesktopWindowObservationCause.WinEventForeground ||
                    observation.IsVisible))
            .DistinctBy(static observation => (
                observation.WindowHandle,
                observation.Desktop,
                observation.WindowClass,
                observation.Title,
                observation.LifecyclePhase))
            .Select(FormatObservation);
        throw new WorkbookAutomationReleasedProcessCleanupException(
            $"Automation Excel caller-desktop exposure was detected after exact " +
            $"process and isolation-resource release was verified for " +
            $"PID={evidence.ExactProcessId}: {string.Join("; ", observations)}");
    }

    private static void ThrowIfWindowsRemainAfterExit(
        DesktopWindowExposureEvidence evidence)
    {
        var remaining = evidence.Observations
            .Where(static observation =>
                observation.Cause == DesktopWindowObservationCause.ProcessExitSnapshot)
            .DistinctBy(static observation => (
                observation.WindowHandle,
                observation.Desktop,
                observation.WindowClass,
                observation.Title))
            .ToArray();
        if (remaining.Length == 0)
        {
            return;
        }

        throw new WorkbookAutomationCleanupException(
            $"Top-level windows remained after exact Excel PID={evidence.ExactProcessId} " +
            $"exit: {string.Join("; ", remaining.Select(FormatObservation))}");
    }

    private static string FormatObservation(DesktopWindowObservation observation)
        => $"PID={observation.ProcessId}, " +
           $"HWND=0x{observation.WindowHandle.ToInt64():X}, " +
           $"desktop='{observation.Desktop}', " +
           $"class='{observation.WindowClass}', " +
           $"title='{observation.Title}', " +
           $"phase={observation.LifecyclePhase}, " +
           $"cause={observation.Cause}";
}
