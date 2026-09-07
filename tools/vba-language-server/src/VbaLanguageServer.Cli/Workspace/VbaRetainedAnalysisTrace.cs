using System.Text.Json;

namespace VbaLanguageServer.Workspace;

/// <summary>
/// Adds retained-cache facts to explicitly enabled scheduler performance evidence.
/// Normal server execution performs no file I/O for this instrumentation.
/// </summary>
internal static class VbaRetainedAnalysisTrace
{
    private static readonly string? DirectoryPath = Environment.GetEnvironmentVariable(
        "VBA_TOOLS_INTERACTIVE_ADMISSION_DIRECTORY");
    private static long sequence;

    internal static void Record(
        string operation, string activeUri, string result, long bytes = 0,
        long maximumBytes = 0, int entries = 0)
    {
        if (string.IsNullOrWhiteSpace(DirectoryPath))
        {
            return;
        }

        var record = new
        {
            operation, activeUri, result, bytes, maximumBytes, entries,
            managedHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
            utc = DateTimeOffset.UtcNow
        };
        var fileName = $"retained-{Environment.ProcessId}-{Interlocked.Increment(ref sequence):D8}.json";
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(Path.Combine(DirectoryPath, fileName), JsonSerializer.Serialize(record));
    }
}
