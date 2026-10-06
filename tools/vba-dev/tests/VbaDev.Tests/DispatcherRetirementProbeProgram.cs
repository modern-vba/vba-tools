using VbaDev.Infrastructure.Debugging;

namespace VbaDev.Tests;

internal static class DispatcherRetirementProbeProgram
{
    internal const string ProbeArgument = "--probe-sta-retirement";
    internal const string SuccessMarker = "STA retirement completed before ThreadPool release.";
    private static readonly TimeSpan WaitBound = TimeSpan.FromSeconds(5);

    private static int Main(string[] args)
    {
        // Preserve the test SDK's otherwise empty generated entry point.
        if (args.Length == 0) return 0;
        if (args.Length == 1 && args[0] == RuntimeRetirementObservationProbe.SuccessArgument)
            return RuntimeRetirementObservationProbe.Run(failOperation: false);
        if (args.Length == 1 && args[0] == RuntimeRetirementObservationProbe.FailureArgument)
            return RuntimeRetirementObservationProbe.Run(failOperation: true);
        if (args.Length != 1 || args[0] != ProbeArgument)
        {
            Console.Error.WriteLine("Unrecognized test probe arguments.");
            return 2;
        }

        return RunProbe();
    }

    private static int RunProbe()
    {
        var failures = new List<Exception>();
        var poolStarted = new ManualResetEventSlim();
        var poolRelease = new ManualResetEventSlim();
        var operationStarted = new ManualResetEventSlim();
        var operationRelease = new ManualResetEventSlim();
        Thread? worker = null;
        IStaComDispatcher? dispatcher = null;
        Task? poolBlocker = null;
        Task<int>? operation = null;
        Task? disposal = null;
        ThreadPool.GetMinThreads(out var originalMinWorkers, out var originalMinIo);
        ThreadPool.GetMaxThreads(out var originalMaxWorkers, out var originalMaxIo);

        try
        {
            Require(ThreadPool.SetMinThreads(1, originalMinIo), "Cannot constrain minimum pool workers.");
            Require(ThreadPool.SetMaxThreads(1, originalMaxIo), "Cannot constrain maximum pool workers.");
            poolBlocker = Task.Run(() =>
            {
                poolStarted.Set();
                Require(poolRelease.Wait(WaitBound), "The pool gate's five-second guard expired.");
            });
            Require(poolStarted.Wait(WaitBound), "The pool blocker did not start.");

            dispatcher = new StaComDispatcherFactory().Create();
            operation = dispatcher.InvokeAsync(() =>
            {
                worker = Thread.CurrentThread;
                operationStarted.Set();
                Require(operationRelease.Wait(WaitBound), "The operation gate's five-second guard expired.");
                return 17;
            }, CancellationToken.None);
            Require(operationStarted.Wait(WaitBound), "The dedicated worker did not start the operation.");
            Require(worker is not null, "The operation did not capture its worker.");
            disposal = dispatcher.DisposeAsync().AsTask();
            Require(!disposal.IsCompleted, "Disposal completed before the operation was released.");

            operationRelease.Set();
            Require(worker!.Join(WaitBound), "The dedicated worker did not exit within five seconds.");
            Require(operation.IsCompletedSuccessfully && operation.Result == 17,
                "The worker exited without completing its accepted operation.");
            ThreadPool.GetAvailableThreads(out var availableWorkers, out _);
            Require(!poolRelease.IsSet && !poolBlocker.IsCompleted && availableWorkers == 0,
                "The pool was not held at the retirement observation boundary.");
            Require(disposal.IsCompletedSuccessfully,
                $"The worker exited while disposal was {disposal.Status}; retirement still needs the pool.");
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }
        finally
        {
            operationRelease.Set();
            poolRelease.Set();
            Capture("Restore maximum pool workers", () =>
                Require(ThreadPool.SetMaxThreads(originalMaxWorkers, originalMaxIo), "Restore failed."));
            Capture("Restore minimum pool workers", () =>
                Require(ThreadPool.SetMinThreads(originalMinWorkers, originalMinIo), "Restore failed."));
            if (disposal is null && dispatcher is not null)
                Capture("Start dispatcher cleanup", () => disposal = dispatcher.DisposeAsync().AsTask());

            // One bounded wait observes every fault, rather than replacing the probe failure.
            var pending = new[] { poolBlocker, operation, disposal }.OfType<Task>().ToArray();
            Capture("Drain owned work", () =>
            {
                try
                {
                    Require(Task.WaitAll(pending, WaitBound), "Owned work did not drain within five seconds.");
                }
                catch (AggregateException)
                {
                    // Retain each task's failure below, including on an incomplete drain.
                }
            });
            foreach (var task in pending)
            {
                if (task.IsFaulted)
                    failures.Add(new InvalidOperationException("Owned work failed during drain.", task.Exception));
                else if (task.IsCanceled)
                    failures.Add(new TaskCanceledException(task));
            }
            if (worker is not null)
                Capture("Join owned worker", () =>
                    Require(worker.Join(WaitBound), "The owned worker remained alive."));

            if (pending.All(task => task.IsCompleted) && (worker is null || !worker.IsAlive))
            {
                poolStarted.Dispose();
                poolRelease.Dispose();
                operationStarted.Dispose();
                operationRelease.Dispose();
            }
            // On incomplete drain, retain the gates until this isolated process exits.
        }

        if (failures.Count > 0)
        {
            Console.Error.WriteLine(new AggregateException("STA retirement probe failed.", failures));
            return 1;
        }

        Console.WriteLine(SuccessMarker);
        return 0;

        void Capture(string stage, Action action)
        {
            try { action(); }
            catch (Exception failure) { failures.Add(new InvalidOperationException(stage, failure)); }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
