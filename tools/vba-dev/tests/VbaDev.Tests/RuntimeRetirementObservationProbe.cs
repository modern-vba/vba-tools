using System.Diagnostics;
using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Debugging;
using VbaDev.Infrastructure.Workbooks;

namespace VbaDev.Tests;

internal static class RuntimeRetirementObservationProbe
{
    internal const string SuccessArgument = "--probe-runtime-retirement-result";
    internal const string FailureArgument = "--probe-runtime-retirement-failure";
    internal const string SuccessMarker = "Runtime observed retired STA and preserved the operation outcome.";
    private static readonly TimeSpan WaitBound = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CleanupGrace = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ObservationBound = TimeSpan.FromMilliseconds(1100);
    private static readonly TimeSpan PressureHold = TimeSpan.FromMilliseconds(1250);

    internal static int Run(bool failOperation)
    {
        var failures = new List<Exception>();
        var startupStarted = new ManualResetEventSlim();
        var startupRelease = new ManualResetEventSlim();
        var owner = new ProbeOwner();
        PressuredDispatcher? dispatcher = null;
        Task<AutomationExcelProcessOutcome<string>>? execution = null;

        try
        {
            dispatcher = new PressuredDispatcher(owner);
            dispatcher.ConstrainChildPool();
            var runtime = new AutomationExcelProcessRuntime(
                dispatcher, new ProbeLifecycle(owner, startupStarted, startupRelease));
            var originalFailure = new WorkbookAutomationTimeoutException(
                new WorkbookAutomationStage(WorkbookAutomationStageKind.WorkbookSave, "probe.xlsm"),
                TimeSpan.FromMilliseconds(20));
            execution = runtime.RunWorkbookAsync(
                "probe.xlsm",
                WorkbookAutomationTimeouts.Default with { ProcessCleanup = CleanupGrace },
                (_, _) => failOperation
                    ? Task.FromException<string>(originalFailure)
                    : Task.FromResult("preserved result"),
                CancellationToken.None);
            Require(startupStarted.Wait(WaitBound), "The real STA did not enter fake startup.");
            Require(!execution.IsCompleted, "Startup did not leave the real runtime awaiting the STA.");
            startupRelease.Set();
            Require(execution.Wait(WaitBound), "The runtime did not finish within five seconds.");
            var outcome = execution.GetAwaiter().GetResult();
            dispatcher.VerifyRetirement();

            Require(outcome.Evidence.ProcessReleaseVerified, "Exact fake-owner release was not proved.");
            Require(outcome.Evidence.CleanupFailure is null, "Cleanup failed before retirement observation.");
            Require(outcome.Evidence.DispatcherRetired && outcome.Evidence.DispatcherFailure is null,
                "The runtime rejected the already successful disposal task. " +
                dispatcher.DescribeObservation() + " " + outcome.Evidence.DispatcherFailure);
            if (failOperation)
            {
                Require(ReferenceEquals(originalFailure, outcome.Evidence.OperationFailure),
                    "The runtime replaced the original operation failure.");
                Exception? observedFailure = null;
                try { outcome.GetReleasedResult(); }
                catch (Exception failure) { observedFailure = failure; }
                Require(ReferenceEquals(originalFailure, observedFailure),
                    "The released outcome did not throw the original operation failure.");
            }
            else
            {
                Require(outcome.Evidence.OperationFailure is null, "The operation unexpectedly failed.");
                Require(outcome.GetReleasedResult() == "preserved result", "The result was not preserved.");
            }
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }
        finally
        {
            startupRelease.Set();
            dispatcher?.Drain(failures);
            DrainTasks(new[] { execution }.OfType<Task>().ToArray(), failures);
            if (execution is null || execution.IsCompleted)
            {
                startupStarted.Dispose();
                startupRelease.Dispose();
            }
        }

        if (failures.Count > 0)
        {
            Console.Error.WriteLine(new AggregateException("Runtime retirement observation probe failed.", failures));
            return 1;
        }

        Console.WriteLine(SuccessMarker);
        return 0;
    }

    private sealed class PressuredDispatcher : IStaComDispatcher, IStaComDispatcherFactory
    {
        private readonly IStaComDispatcher inner = new StaComDispatcherFactory().Create();
        private readonly ProbeOwner owner;
        private readonly ManualResetEventSlim armed = new();
        private readonly ManualResetEventSlim poolStarted = new();
        private readonly ManualResetEventSlim poolRelease = new();
        private readonly ManualResetEventSlim operationStarted = new();
        private readonly ManualResetEventSlim operationRelease = new();
        private readonly Thread controller;
        private readonly int originalMinWorkers, originalMinIo, originalMaxWorkers, originalMaxIo;
        private Thread? worker;
        private Task? poolBlocker, gateOperation, disposal;
        private Exception? controllerFailure;
        private long armedTicks, joinedTicks, poolReleaseTicks;

        internal PressuredDispatcher(ProbeOwner owner)
        {
            this.owner = owner;
            ThreadPool.GetMinThreads(out originalMinWorkers, out originalMinIo);
            ThreadPool.GetMaxThreads(out originalMaxWorkers, out originalMaxIo);
            controller = new Thread(ObserveRetirement) { IsBackground = true };
            controller.Start();
        }

        public IStaComDispatcher Create() => this;

        internal void ConstrainChildPool()
        {
            // Establish one worker before startup; occupy it only at final disposal.
            Require(ThreadPool.SetMinThreads(1, originalMinIo), "Cannot constrain minimum pool workers.");
            Require(ThreadPool.SetMaxThreads(1, originalMaxIo), "Cannot constrain maximum pool workers.");
        }

        public Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken)
            => inner.InvokeAsync(operation, cancellationToken);

        public ValueTask DisposeAsync()
        {
            Require(disposal is null, "The runtime requested disposal more than once.");
            Require(owner.HasExited && owner.Disposed, "Pool pressure started before exact owner cleanup.");
            Require(Thread.CurrentThread.IsThreadPoolThread, "Retirement did not run on a pool continuation.");
            gateOperation = inner.InvokeAsync(() =>
            {
                worker = Thread.CurrentThread;
                operationStarted.Set();
                Require(operationRelease.Wait(WaitBound), "The final STA gate expired.");
                return true;
            }, CancellationToken.None);
            Require(operationStarted.Wait(WaitBound), "The final STA operation did not enter.");
            disposal = inner.DisposeAsync().AsTask();
            Require(!disposal.IsCompleted, "Disposal was not pending when observation was armed.");
            armedTicks = Stopwatch.GetTimestamp();
            poolBlocker = Task.Run(() =>
            {
                poolStarted.Set();
                Require(poolRelease.Wait(WaitBound), "The pool gate expired.");
            });
            armed.Set();
            // Do not wait for the blocker here. It enters after this pool continuation
            // returns through the real runtime's pending retirement observation.
            return new ValueTask(disposal);
        }

        private void ObserveRetirement()
        {
            try
            {
                Require(armed.Wait(WaitBound), "Retirement observation was not armed.");
                Require(poolStarted.Wait(WaitBound), "The pool blocker did not enter.");
                operationRelease.Set();
                Require(worker!.Join(WaitBound), "The real STA worker did not exit.");
                joinedTicks = Stopwatch.GetTimestamp();
                ThreadPool.GetAvailableThreads(out var availableWorkers, out _);
                Require(disposal!.IsCompletedSuccessfully && gateOperation!.IsCompletedSuccessfully,
                    "The worker exited without successful operation and disposal completion.");
                Require(!poolRelease.IsSet && !poolBlocker!.IsCompleted && availableWorkers == 0,
                    "Pool pressure was not held at worker retirement.");
                Require(Stopwatch.GetElapsedTime(armedTicks, joinedTicks) < ObservationBound,
                    "The worker did not retire before the configured 1.1-second observation bound. " + DescribeObservation());
                Thread.Sleep(PressureHold);
                Require(!poolBlocker!.IsCompleted, "The pool gate was lost before the deadline elapsed.");
                poolReleaseTicks = Stopwatch.GetTimestamp();
            }
            catch (Exception failure)
            {
                controllerFailure = failure;
            }
            finally
            {
                operationRelease.Set();
                poolRelease.Set();
            }
        }

        internal void VerifyRetirement()
        {
            Require(controller.Join(WaitBound), "The observation controller did not exit.");
            if (controllerFailure is not null)
                throw new InvalidOperationException("Retirement precondition failed.", controllerFailure);
            Require(poolReleaseTicks > joinedTicks && joinedTicks > armedTicks,
                "The observation controller did not record its complete sequence.");
        }

        internal string DescribeObservation()
            => $"Worker join: {Elapsed(joinedTicks):F4}ms; pool release: {Elapsed(poolReleaseTicks):F4}ms; " +
                $"disposal: {disposal?.Status}.";

        private double Elapsed(long timestamp)
            => armedTicks > 0 && timestamp > 0
                ? Stopwatch.GetElapsedTime(armedTicks, timestamp).TotalMilliseconds : -1;

        internal void Drain(List<Exception> failures)
        {
            operationRelease.Set();
            poolRelease.Set();
            Capture("Restore maximum pool workers", () => Require(
                ThreadPool.SetMaxThreads(originalMaxWorkers, originalMaxIo), "Restore failed."));
            Capture("Restore minimum pool workers", () => Require(
                ThreadPool.SetMinThreads(originalMinWorkers, originalMinIo), "Restore failed."));
            Capture("Start final dispatcher cleanup", () => disposal ??= inner.DisposeAsync().AsTask());
            Capture("Join observation controller", () => Require(controller.Join(WaitBound), "Controller stayed alive."));
            if (controllerFailure is not null) failures.Add(controllerFailure);
            var tasks = new[] { poolBlocker, gateOperation, disposal }.OfType<Task>().ToArray();
            DrainTasks(tasks, failures);
            if (worker is not null)
                Capture("Join owned worker", () => Require(worker.Join(WaitBound), "Worker stayed alive."));
            if (!controller.IsAlive && tasks.All(task => task.IsCompleted) && (worker is null || !worker.IsAlive))
            {
                armed.Dispose(); poolStarted.Dispose(); poolRelease.Dispose();
                operationStarted.Dispose(); operationRelease.Dispose();
            }

            void Capture(string stage, Action action)
            {
                try { action(); }
                catch (Exception failure) { failures.Add(new InvalidOperationException(stage, failure)); }
            }
        }
    }

    private sealed class ProbeLifecycle(
        ProbeOwner owner, ManualResetEventSlim startupStarted, ManualResetEventSlim startupRelease)
        : IExcelComWorkbookGenerationLifecycle
    {
        public object Start(OwnedExcelTerminationController controller, bool enableAutomationSecurityLow,
            CancellationToken cancellationToken)
        {
            startupStarted.Set();
            Require(startupRelease.Wait(WaitBound), "Fake startup was not released.");
            cancellationToken.ThrowIfCancellationRequested();
            controller.Attach(owner);
            return new object();
        }

        public IWorkbookBuildSession Open(object host, string workbookPath) => new ProbeSession();
        public void DisposeHost(object host, TimeSpan cleanupGrace) => owner.Complete();
        public void DisposeSession(IWorkbookBuildSession session, TimeSpan cleanupGrace) => owner.Complete();
    }

    private sealed class ProbeOwner : IOwnedExcelProcessControl
    {
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int disposed;
        public bool HasExited => completion.Task.IsCompletedSuccessfully;
        public Task Completion => completion.Task;
        internal bool Disposed => Volatile.Read(ref disposed) != 0;
        internal void Complete() => completion.TrySetResult();
        public Task TerminateAsync() { Complete(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Interlocked.Exchange(ref disposed, 1); return ValueTask.CompletedTask; }
    }

    private sealed class ProbeSession : IWorkbookBuildSession
    {
        public string GetProjectName() => throw new NotSupportedException();
        public IReadOnlyList<WorkbookModule> GetModules() => throw new NotSupportedException();
        public IReadOnlyList<WorkbookReference> GetReferences() => throw new NotSupportedException();
        public bool RemoveReference(string referenceName) => throw new NotSupportedException();
        public void AddReference(ResolvedVbaProjectReference reference) => throw new NotSupportedException();
        public void RemoveModule(string moduleName) => throw new NotSupportedException();
        public void ImportModule(VbeImportSourceFile sourceFile) => throw new NotSupportedException();
        public VbeImportVerificationReport VerifyImportedModules() => throw new NotSupportedException();
        public void Save() => throw new NotSupportedException();
    }

    private static void DrainTasks(Task[] tasks, List<Exception> failures)
    {
        try
        {
            Require(Task.WaitAll(tasks, WaitBound), "Owned work did not drain within five seconds.");
        }
        catch (AggregateException) { }
        catch (Exception failure) { failures.Add(failure); }
        foreach (var task in tasks)
        {
            if (task.IsFaulted) failures.Add(task.Exception!);
            else if (task.IsCanceled) failures.Add(new TaskCanceledException(task));
            else if (!task.IsCompleted) failures.Add(new TimeoutException("An owned task is still pending."));
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
