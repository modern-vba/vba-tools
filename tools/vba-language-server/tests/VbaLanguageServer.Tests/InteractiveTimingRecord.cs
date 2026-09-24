using System.Diagnostics;

namespace VbaLanguageServer.Tests;

internal sealed class InteractiveTimingRecord
{
    private InteractiveTimingRecord(string[] lines)
    {
        Lines = Array.AsReadOnly(lines);
    }

    public IReadOnlyList<string> Lines { get; }

    public long ReadValue(string key)
        => long.Parse(
            Lines.Single(line => line.StartsWith($"{key}=", StringComparison.Ordinal))
                [(key.Length + 1)..]);

    public static async Task<InteractiveTimingRecord> WaitAsync(
        string directory,
        Func<string, bool> predicate,
        TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        IOException? lastSharingError = null;
        string? observedPath = null;
        while (elapsed.Elapsed < timeout)
        {
            var path = Directory.EnumerateFiles(directory)
                .FirstOrDefault(candidate => predicate(Path.GetFileName(candidate)));
            if (path is not null)
            {
                observedPath = path;
                try
                {
                    return new InteractiveTimingRecord(File.ReadAllLines(path));
                }
                catch (IOException exception) when (exception.HResult == unchecked((int)0x80070020))
                {
                    lastSharingError = exception;
                }
            }

            var remaining = timeout - elapsed.Elapsed;
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining < TimeSpan.FromMilliseconds(10)
                    ? remaining
                    : TimeSpan.FromMilliseconds(10));
            }
        }

        throw new TimeoutException(
            $"No readable timing record appeared at '{observedPath ?? directory}' within {timeout}.",
            lastSharingError);
    }
}
