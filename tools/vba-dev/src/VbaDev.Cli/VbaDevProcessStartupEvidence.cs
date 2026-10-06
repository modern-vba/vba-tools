using System.Runtime.InteropServices;
using System.Text.Json;

namespace VbaDev.Cli;

/// <summary>Opt-in local receipt from the actual managed child, before command dispatch.</summary>
internal static class VbaDevProcessStartupEvidence
{
    internal const string AttemptIdEnvironmentVariable = "VBA_TOOLS_DIAGNOSTIC_ATTEMPT_ID";

    internal static void TryWriteFromEnvironment() => TryWrite(
        Environment.GetEnvironmentVariable("VBA_TOOLS_DIAGNOSTIC_RUN_ROOT"),
        Environment.GetEnvironmentVariable("VBA_TOOLS_DIAGNOSTIC_RUN_ID"),
        Environment.GetEnvironmentVariable(AttemptIdEnvironmentVariable));

    internal static void TryWrite(string? runRoot, string? runId, string? attemptId)
    {
        if (string.IsNullOrWhiteSpace(runRoot) ||
            string.IsNullOrWhiteSpace(runId) || runId.Length > 128 ||
            string.IsNullOrWhiteSpace(attemptId) || attemptId.Length > 64)
            return;

        try
        {
            if (!Path.IsPathFullyQualified(runRoot) ||
                runRoot.StartsWith("\\\\", StringComparison.Ordinal) ||
                new DriveInfo(Path.GetPathRoot(runRoot)!).DriveType != DriveType.Fixed)
                return;
            for (var current = new DirectoryInfo(runRoot); current is not null; current = current.Parent)
            {
                if (!current.Exists ||
                    (current.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    return;
            }

            var directory = Path.Combine(runRoot, "source-admission-processes");
            Directory.CreateDirectory(directory);
            if ((File.GetAttributes(directory) & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                return;
            var receipt = Path.Combine(directory,
                $"process-{Environment.ProcessId}-{Guid.NewGuid():N}.json");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = "1.0",
                kind = "vba-dev-runtime-startup",
                runId,
                attemptId,
                processId = Environment.ProcessId,
                processPath = Environment.ProcessPath,
                startedAtUtc = DateTimeOffset.UtcNow,
                runtimeVersion = Environment.Version.ToString(),
                frameworkDescription = RuntimeInformation.FrameworkDescription,
                runtimeIdentifier = RuntimeInformation.RuntimeIdentifier,
                targetFramework = AppContext.TargetFrameworkName
            });
            var temporary = receipt + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.Read, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.WriteByte((byte)'\n');
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, receipt);
        }
        catch (Exception)
        {
            // A diagnostic receipt must not change the CLI's behavior or hide its failure.
        }
    }
}
