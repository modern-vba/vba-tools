namespace VbaDev.Cli;

/// <summary>Owns explicit confirmation input for one invocation, never a shared process reader.</summary>
internal sealed class VbaDevWorkbookConfirmationInput : IDisposable
{
    private static readonly AsyncLocal<VbaDevWorkbookConfirmationInput?> current = new();
    private readonly VbaDevWorkbookConfirmationInput? previous;
    private readonly Stream input;
    private readonly TextWriter error;
    private readonly bool managed;
    private readonly object pendingLock = new();
    private string? pendingId;
    private TaskCompletionSource<bool>? pendingAnswer;
    private bool inputClosed;

    internal VbaDevWorkbookConfirmationInput(Stream input, TextWriter error, bool managed = false)
    {
        this.input = input;
        this.error = error;
        this.managed = managed;
        previous = current.Value;
        current.Value = this;
    }

    internal static Task<bool> ConfirmAsync(string message, CancellationToken cancellationToken)
        => current.Value?.ReadConfirmationAsync(message, cancellationToken) ?? Task.FromResult(false);

    public void Dispose()
    {
        CompleteInput();
        current.Value = previous;
    }

    internal void CompleteInput()
    {
        lock (pendingLock)
        {
            inputClosed = true;
            pendingAnswer?.TrySetResult(false);
        }
    }

    internal void ObserveFrame(string frame)
    {
        lock (pendingLock)
        {
            if (pendingId is null || pendingAnswer is null) return;
            if (frame == $"confirm:{pendingId}:yes") pendingAnswer.TrySetResult(true);
            else if (frame == $"confirm:{pendingId}:no") pendingAnswer.TrySetResult(false);
        }
    }

    private async Task<bool> ReadConfirmationAsync(string message, CancellationToken cancellationToken)
    {
        if (managed) return await ReadManagedConfirmationAsync(message, cancellationToken).ConfigureAwait(false);
        await error.WriteLineAsync(message).ConfigureAwait(false);
        await error.WriteAsync("[y/N] ").ConfigureAwait(false);
        await error.FlushAsync(cancellationToken).ConfigureAwait(false);
        var answer = new System.Text.StringBuilder();
        var buffer = new byte[1];
        var stopped = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        try
        {
            while (answer.Length < 512)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = input.ReadAsync(buffer, cancellationToken).AsTask();
                if (await Task.WhenAny(read, stopped).ConfigureAwait(false) != read)
                {
                    _ = read.ContinueWith(static task => _ = task.Exception,
                        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted,
                        TaskScheduler.Default);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                if (await read.ConfigureAwait(false) == 0) return false;
                if (buffer[0] == (byte)'\n')
                {
                    var text = answer.ToString().Trim();
                    return text.Equals("y", StringComparison.OrdinalIgnoreCase) ||
                        text.Equals("yes", StringComparison.OrdinalIgnoreCase);
                }
                answer.Append((char)buffer[0]);
            }
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> ReadManagedConfirmationAsync(string message, CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (pendingLock)
        {
            if (inputClosed || pendingAnswer is not null) return false;
            pendingId = requestId;
            pendingAnswer = answer;
        }
        try
        {
            await error.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(new
            {
                type = "workbookConfirmation", schemaVersion = "1.0", requestId, message
            })).ConfigureAwait(false);
            await error.FlushAsync(cancellationToken).ConfigureAwait(false);
            return await answer.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (pendingLock)
            {
                pendingId = null;
                pendingAnswer = null;
            }
        }
    }
}
