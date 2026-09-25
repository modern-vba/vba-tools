using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VbaDev.Domain;

namespace VbaDev.App.Workbooks;

/// <summary>
/// Opt-in, local-only source identity receipts that survive a process exit during parsing.
/// </summary>
internal sealed class SourceAdmissionPreparseEvidence(
    string? runRoot,
    string? runId = null,
    int maxRecords = 10000)
{
    internal const string RunRootEnvironmentVariable = "VBA_TOOLS_DIAGNOSTIC_RUN_ROOT";
    internal const string RunIdEnvironmentVariable = "VBA_TOOLS_DIAGNOSTIC_RUN_ID";
    private const int MaximumPathCodeUnits = 4096;
    private static readonly ConcurrentDictionary<string, Counter> RunCounters =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();
    private readonly string? boundedRunId = runId is { Length: <= 128 } ? runId : null;
    private string? sessionDirectory;

    internal static SourceAdmissionPreparseEvidence FromEnvironment() => new(
        Environment.GetEnvironmentVariable(RunRootEnvironmentVariable),
        Environment.GetEnvironmentVariable(RunIdEnvironmentVariable));

    internal Receipt? Begin(
        string sourcePath,
        VbaSourceKind sourceKind,
        ReadOnlySpan<byte> sourceBytes,
        string encodingToken,
        int activeCodePage,
        int decodedCharacterCount,
        string admissionPurpose)
    {
        if (string.IsNullOrWhiteSpace(runRoot) || maxRecords <= 0) return null;
        var counter = RunCounters.GetOrAdd(runRoot, static _ => new Counter());
        var sequence = Interlocked.Increment(ref counter.Value);
        if (sequence > maxRecords) return null;
        try
        {
            var directory = GetSessionDirectory();
            var capturedLength = Math.Min(sourcePath.Length, MaximumPathCodeUnits);
            var pathHex = new StringBuilder(capturedLength * 4);
            for (var index = 0; index < capturedLength; index++)
                pathHex.Append(((int)sourcePath[index]).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
            WriteNew(Path.Combine(directory, $"{sequence:D8}-preparse.json"), new
            {
                schemaVersion = "1.0",
                kind = "vba-source-preparse",
                status = "prepared",
                processId = Environment.ProcessId,
                runId = boundedRunId,
                sequence,
                preparedAtUtc = DateTimeOffset.UtcNow,
                admissionPurpose,
                sourceKind = sourceKind.ToString(),
                sourcePathCodeUnitLength = sourcePath.Length,
                sourcePathUtf16Hex = pathHex.ToString(),
                sourcePathCaptureComplete = capturedLength == sourcePath.Length,
                sourcePathSha256Utf16Le = Convert.ToHexString(SHA256.HashData(
                    MemoryMarshal.AsBytes(sourcePath.AsSpan()))),
                sourceByteLength = sourceBytes.Length,
                sourceSha256 = Convert.ToHexString(SHA256.HashData(sourceBytes)),
                encodingToken,
                activeCodePage,
                decodedCharacterCount
            });
            return new Receipt(directory, sequence, boundedRunId);
        }
        catch (Exception)
        {
            // Diagnostic storage must never change source admission or hide its primary failure.
            return null;
        }
    }

    private string GetSessionDirectory()
    {
        lock (gate)
        {
            if (sessionDirectory is not null) return sessionDirectory;
            VerifyRunRoot(runRoot!);
            var evidenceRoot = Path.Combine(runRoot!, "source-admission");
            Directory.CreateDirectory(evidenceRoot);
            if ((File.GetAttributes(evidenceRoot) & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                throw new IOException("Source-admission evidence requires an ordinary local directory.");
            var created = Path.Combine(evidenceRoot,
                $"process-{Environment.ProcessId}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(created);
            sessionDirectory = created;
            return created;
        }
    }

    private static void VerifyRunRoot(string root)
    {
        if (!Path.IsPathFullyQualified(root) || root.StartsWith("\\\\", StringComparison.Ordinal))
            throw new IOException("Source-admission evidence requires an explicit local absolute directory.");
        if (new DriveInfo(Path.GetPathRoot(root)!).DriveType != DriveType.Fixed)
            throw new IOException("Source-admission evidence requires a local fixed drive.");
        for (var current = new DirectoryInfo(root); current is not null; current = current.Parent)
        {
            if (!current.Exists || (current.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                throw new IOException("Source-admission evidence requires existing ordinary directories.");
        }
    }

    private static void WriteNew(string path, object record)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record);
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
            FileShare.Read, 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.WriteByte((byte)'\n');
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path);
    }

    internal sealed class Receipt(string directory, int sequence, string? runId)
    {
        internal void Complete()
        {
            try
            {
                WriteNew(Path.Combine(directory, $"{sequence:D8}-complete.json"), new
                {
                    schemaVersion = "1.0",
                    kind = "vba-source-preparse",
                    status = "parse-complete",
                    processId = Environment.ProcessId,
                    runId,
                    sequence,
                    completedAtUtc = DateTimeOffset.UtcNow
                });
            }
            catch (Exception)
            {
                // Completion evidence is secondary to the parser result.
            }
        }
    }

    private sealed class Counter
    {
        internal int Value;
    }
}
