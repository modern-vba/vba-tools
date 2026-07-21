using System.Collections.Concurrent;

namespace VbaDev.Infrastructure.Debugging;

internal sealed class StaComDispatcher : IStaComDispatcher
{
    private readonly BlockingCollection<IWorkItem> workItems = new();
    private readonly TaskCompletionSource workerCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread workerThread;
    private int disposed;

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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        workItems.CompleteAdding();
        await workerCompletion.Task.ConfigureAwait(false);
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
