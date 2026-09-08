using System.Collections.Concurrent;

namespace VbaDebugAdapter.Infrastructure;

internal sealed class StaComDispatcher : IStaComDispatcher
{
    private readonly BlockingCollection<IWorkItem> workItems = new();
    private readonly TaskCompletionSource workerCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread workerThread;
    private readonly object disposalGate = new();
    private Task? disposal;
    private int disposed;

    public bool ReleaseVerified => workerCompletion.Task.IsCompleted && !workerThread.IsAlive;

    public StaComDispatcher()
    {
        workerThread = new Thread(Run)
        {
            IsBackground = true,
            Name = "VbaDev VBE COM automation"
        };
        if (OperatingSystem.IsWindows())
        {
            workerThread.SetApartmentState(ApartmentState.STA);
        }

        workerThread.Start();
    }

    public Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

        var workItem = new WorkItem<T>(operation, cancellationToken);
        try
        {
            workItems.Add(workItem, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            throw new ObjectDisposedException(nameof(StaComDispatcher));
        }

        return workItem.Completion;
    }

    public ValueTask DisposeAsync()
    {
        lock (disposalGate)
        {
            return new ValueTask(disposal ??= DisposeOnceAsync());
        }
    }

    private async Task DisposeOnceAsync()
    {
        Volatile.Write(ref disposed, 1);
        workItems.CompleteAdding();
        await workerCompletion.Task.ConfigureAwait(false);
        workerThread.Join();
        workItems.Dispose();
    }

    private void Run()
    {
        try
        {
            foreach (var workItem in workItems.GetConsumingEnumerable())
            {
                workItem.Run();
            }

            workerCompletion.TrySetResult();
        }
        catch (Exception ex)
        {
            workerCompletion.TrySetException(ex);
        }
    }

    private interface IWorkItem
    {
        void Run();
    }

    private sealed class WorkItem<T>(Func<T> operation, CancellationToken cancellationToken) : IWorkItem
    {
        private readonly TaskCompletionSource<T> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> Completion => AwaitCompletionAsync();

        private async Task<T> AwaitCompletionAsync()
            => await completion.Task.ConfigureAwait(false);

        public void Run()
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(operation());
            }
            catch (Exception ex)
            {
                // The async projection retains a thrown cancellation and its stack
                // while exposing a cancelled task to callers.
                completion.TrySetException(ex);
            }
        }
    }
}
