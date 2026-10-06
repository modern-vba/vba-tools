using System.Collections.Concurrent;

namespace VbaDev.Infrastructure.Debugging;

internal sealed class StaComDispatcher : IStaComDispatcher
{
    private readonly object lifetimeGate = new();
    private readonly BlockingCollection<IWorkItem> workItems = new();
    private readonly TaskCompletionSource workerCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread workerThread;
    private bool disposed;

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
        var workItem = new WorkItem<T>(operation, cancellationToken);
        lock (lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            workItems.Add(workItem, cancellationToken);
        }

        return workItem.Completion;
    }

    public ValueTask DisposeAsync()
    {
        lock (lifetimeGate)
        {
            if (!disposed)
            {
                disposed = true;
                workItems.CompleteAdding();
            }
        }

        return new ValueTask(workerCompletion.Task);
    }

    private void Run()
    {
        Exception? failure = null;
        try
        {
            foreach (var workItem in workItems.GetConsumingEnumerable())
            {
                workItem.Run();
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        try
        {
            // BlockingCollection disposal must not race admission or CompleteAdding.
            lock (lifetimeGate)
            {
                disposed = true;
                workItems.Dispose();
            }
        }
        catch (Exception ex)
        {
            failure = failure is null ? ex : new AggregateException(failure, ex);
        }

        // Publish full retirement from this worker, without a ThreadPool continuation.
        if (failure is null)
        {
            workerCompletion.TrySetResult();
        }
        else
        {
            workerCompletion.TrySetException(failure);
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

        public Task<T> Completion => completion.Task;

        public void Run()
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(operation());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }
    }
}
