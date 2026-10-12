using System.Text.Json;

namespace VbaLanguageServer.Tests;

internal static class TranscriptMessageWait
{
    internal readonly record struct Snapshot(JsonElement? Message, Task Changed);

    internal sealed class WatchdogExpiredException : TimeoutException
    {
        internal WatchdogExpiredException(OperationCanceledException cause)
            : base("Timed out waiting for a language-server message.", cause)
        {
        }
    }

    internal static async Task<JsonElement> WaitAsync(
        Func<Snapshot> read,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(timeout);
        while (true)
        {
            var snapshot = read();
            if (snapshot.Message is { } message)
            {
                return message;
            }

            try
            {
                await snapshot.Changed.WaitAsync(wait.Token);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Committed transcript data is authoritative even when the
                // separate notification loses its race with the watchdog.
                cancellationToken.ThrowIfCancellationRequested();
                if (read().Message is { } recorded)
                {
                    return recorded;
                }
                throw new WatchdogExpiredException(exception);
            }
        }
    }
}
