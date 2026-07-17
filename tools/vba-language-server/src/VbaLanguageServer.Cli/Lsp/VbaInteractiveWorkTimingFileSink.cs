using System.Globalization;
using System.Text;

namespace VbaLanguageServer.Lsp;

/// <summary>
/// Records scheduler timing events as files when deterministic process instrumentation is configured.
/// </summary>
internal sealed class VbaInteractiveWorkTimingFileSink : IVbaInteractiveWorkTimingSink
{
    internal const string DirectoryEnvironmentVariable =
        "VBA_TOOLS_INTERACTIVE_ADMISSION_DIRECTORY";

    private readonly string directory;

    private VbaInteractiveWorkTimingFileSink(string directory)
    {
        this.directory = directory;
    }

    public static IVbaInteractiveWorkTimingSink CreateFromEnvironment()
    {
        var directory = Environment.GetEnvironmentVariable(DirectoryEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return NullVbaInteractiveWorkTimingSink.Instance;
        }

        Directory.CreateDirectory(directory);
        return new VbaInteractiveWorkTimingFileSink(directory);
    }

    public void RecordAdmission(VbaInteractiveWorkAdmissionTiming timing)
    {
        var stem = CreateFileStem(
            timing.InputSequence,
            timing.Kind,
            timing.Method,
            timing.RequestId);
        File.WriteAllText(
            Path.Combine(directory, $"{stem}.admitted"),
            string.Join('\n',
            [
                $"inputSequence={timing.InputSequence}",
                $"readFence={timing.ReadFence}",
                $"kind={timing.Kind}",
                $"method={timing.Method}",
                $"requestId={timing.RequestId?.ToString() ?? "none"}",
                $"admissionMilliseconds={FormatMilliseconds(timing.AdmissionTime)}"
            ]));
    }

    public void RecordCompletion(VbaInteractiveWorkCompletionTiming timing)
    {
        var stem = CreateFileStem(
            timing.InputSequence,
            timing.Kind,
            timing.Method,
            timing.RequestId);
        File.WriteAllText(
            Path.Combine(directory, $"{stem}.completed"),
            string.Join('\n',
            [
                $"inputSequence={timing.InputSequence}",
                $"readFence={timing.ReadFence}",
                $"kind={timing.Kind}",
                $"method={timing.Method}",
                $"requestId={timing.RequestId?.ToString() ?? "none"}",
                $"queueMilliseconds={FormatMilliseconds(timing.QueueTime)}",
                $"executionMilliseconds={FormatMilliseconds(timing.ExecutionTime)}",
                $"cancelled={timing.Cancelled}",
                $"faulted={timing.Faulted}"
            ]));
    }

    private static string CreateFileStem(
        long inputSequence,
        VbaInteractiveWorkKind kind,
        string method,
        VbaLspRequestId? requestId)
    {
        var idSuffix = requestId is { } id
            ? $"{id.Kind.ToString().ToLowerInvariant()}-{Sanitize(id.Value)}"
            : "none";
        return $"{inputSequence:D20}-{kind.ToString().ToLowerInvariant()}-"
            + $"{Sanitize(method)}-{idSuffix}";
    }

    private static string Sanitize(string value)
    {
        var sanitized = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            sanitized.Append(char.IsLetterOrDigit(character) || character is '-' or '_' or '.'
                ? character
                : '_');
        }

        return sanitized.ToString();
    }

    private static string FormatMilliseconds(TimeSpan value)
        => value.TotalMilliseconds.ToString("0.000000", CultureInfo.InvariantCulture);
}
