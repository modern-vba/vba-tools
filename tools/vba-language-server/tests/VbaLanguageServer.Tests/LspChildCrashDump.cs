using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VbaLanguageServer.Tests;

/// <summary>
/// Arms the exact LSP test child for one local, opt-in full crash dump.
/// Receipts are local and contain no raw arguments or environment values.
/// </summary>
internal static class LspChildCrashDump
{
    private const string RunRootEnvironmentVariable = "VBA_TOOLS_DIAGNOSTIC_RUN_ROOT";
    private const string RunIdEnvironmentVariable = "VBA_TOOLS_DIAGNOSTIC_RUN_ID";
    private const long MinimumFreeBytes = 10L * 1024 * 1024 * 1024;
    private const long MaximumReceiptBytes = 2L * 1024 * 1024;
    private const int MaximumReceiptLineBytes = 4096;
    private static readonly Regex RunName = new(
        @"\Arun-[0-9]{8}T[0-9]{9}Z-[0-9a-f]{16}\z",
        RegexOptions.CultureInvariant);

    internal static Plan? TryConfigure(ProcessStartInfo startInfo) => TryConfigure(
        startInfo,
        Environment.GetEnvironmentVariable(RunRootEnvironmentVariable),
        Environment.GetEnvironmentVariable(RunIdEnvironmentVariable));

    internal static Plan? TryConfigure(
        ProcessStartInfo startInfo,
        string? runRoot,
        string? runId,
        long? availableFreeBytesOverride = null)
    {
        if (!OperatingSystem.IsWindows()
            || string.IsNullOrWhiteSpace(runRoot)
            || string.IsNullOrWhiteSpace(runId)
            || !RunName.IsMatch(runId))
        {
            return null;
        }

        try
        {
            if (!Path.IsPathFullyQualified(runRoot)
                || runRoot.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return null;
            }

            var fullRoot = Path.GetFullPath(runRoot);
            if (!string.Equals(
                    Path.GetFileName(Path.TrimEndingDirectorySeparator(fullRoot)),
                    runId,
                    StringComparison.Ordinal))
            {
                return null;
            }

            var drive = new DriveInfo(Path.GetPathRoot(fullRoot)!);
            if (drive.DriveType != DriveType.Fixed
                || !IsOrdinaryExistingDirectoryChain(fullRoot))
            {
                return null;
            }

            var dumpDirectory = Path.Combine(fullRoot, "lsp-crash-dumps");
            Directory.CreateDirectory(dumpDirectory);
            if (!IsOrdinaryExistingDirectoryChain(dumpDirectory))
            {
                return null;
            }

            if ((availableFreeBytesOverride ?? drive.AvailableFreeSpace) < MinimumFreeBytes)
            {
                return new Plan(dumpDirectory, null, null, "insufficient-disk-space");
            }

            if (Directory.EnumerateFiles(dumpDirectory, "*.dmp").Any())
            {
                return new Plan(dumpDirectory, null, null, "dump-budget-exhausted");
            }

            var slotPath = Path.Combine(dumpDirectory, ".armed");
            FileStream slot;
            try
            {
                slot = new FileStream(slotPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, bufferSize: 1, FileOptions.None);
            }
            catch (IOException)
            {
                return new Plan(dumpDirectory, null, null, "slot-busy");
            }

            var lease = new SlotLease(slot, slotPath);
            try
            {
                if (Directory.EnumerateFiles(dumpDirectory, "*.dmp").Any())
                {
                    lease.Dispose();
                    return new Plan(dumpDirectory, null, null, "dump-budget-exhausted");
                }

                var prefix = $"lsp-{Guid.NewGuid():N}-";
                // The published LSP is single-file, which requires a Full dump.
                startInfo.Environment["DOTNET_DbgMiniDumpName"] = Path.Combine(
                    dumpDirectory, $"{prefix}%p-%t.dmp");
                startInfo.Environment["DOTNET_DbgMiniDumpType"] = "4";
                startInfo.Environment["DOTNET_DbgEnableMiniDump"] = "1";
                return new Plan(dumpDirectory, lease, prefix, null);
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            // Diagnostic setup must not replace the LSP test result.
            return null;
        }
    }

    private static bool IsOrdinaryExistingDirectoryChain(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            if (!current.Exists
                || (current.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            {
                return false;
            }
        }

        return true;
    }

    internal sealed class Plan(
        string dumpDirectory,
        SlotLease? slot,
        string? dumpPrefix,
        string? skipReason) : IDisposable
    {
        public bool IsArmed => slot is not null;
        public string? SkipReason => skipReason;

        public void Started(Process process, string phase)
        {
            var info = process.StartInfo;
            var executablePath = info.FileName;
            string? executableHash = null;
            try
            {
                using var stream = File.OpenRead(executablePath);
                executableHash = Convert.ToHexString(SHA256.HashData(stream));
            }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
            }

            var argumentBytes = Encoding.UTF8.GetBytes(string.Join('\0', info.ArgumentList));
            Append(new
            {
                kind = "lsp-child-crash-capture",
                schemaVersion = 1,
                eventType = "started",
                atUtc = DateTimeOffset.UtcNow,
                phase,
                processId = process.Id,
                executablePath = executablePath.Length <= 2048 ? executablePath : executablePath[..2048],
                executablePathComplete = executablePath.Length <= 2048,
                executableSha256 = executableHash,
                argumentCount = info.ArgumentList.Count,
                argumentSha256 = Convert.ToHexString(SHA256.HashData(argumentBytes)),
                armed = IsArmed,
                skipReason
            });
        }

        public void Finished(Process process)
        {
            string? dumpFileName = null;
            long? dumpLength = null;
            try
            {
                if (dumpPrefix is not null)
                {
                    var dumpPath = Directory.EnumerateFiles(dumpDirectory, dumpPrefix + "*.dmp")
                        .FirstOrDefault();
                    if (dumpPath is not null)
                    {
                        var dump = new FileInfo(dumpPath);
                        if ((dump.Attributes & FileAttributes.ReparsePoint) == 0)
                        {
                            dumpFileName = dump.Name;
                            dumpLength = dump.Length;
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
            }

            int? exitCode = null;
            try { if (process.HasExited) exitCode = process.ExitCode; }
            catch (Exception exception) when (exception is not StackOverflowException) { }
            Append(new
            {
                kind = "lsp-child-crash-capture",
                schemaVersion = 1,
                eventType = "finished",
                atUtc = DateTimeOffset.UtcNow,
                processId = process.Id,
                exitCode,
                dumpExists = dumpFileName is not null,
                dumpFileName,
                dumpLength
            });
        }

        public void Dispose() => slot?.Dispose();

        private void Append(object value)
        {
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
                if (bytes.Length + 1 > MaximumReceiptLineBytes) return;
                var path = Path.Combine(dumpDirectory, "children.ndjson");
                if (File.Exists(path)
                    && (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                {
                    return;
                }

                for (var attempt = 0; attempt < 10; attempt++)
                {
                    try
                    {
                        using var stream = new FileStream(path, FileMode.OpenOrCreate,
                            FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.WriteThrough);
                        if (stream.Length + bytes.Length + 1 > MaximumReceiptBytes) return;
                        stream.Seek(0, SeekOrigin.End);
                        stream.Write(bytes);
                        stream.WriteByte((byte)'\n');
                        stream.Flush(flushToDisk: true);
                        return;
                    }
                    catch (IOException) when (attempt < 9)
                    {
                        Thread.Sleep(5);
                    }
                }
            }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                // Receipt failure is secondary to the test or child failure.
            }
        }
    }

    internal sealed class SlotLease(FileStream stream, string path) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { stream.Dispose(); } catch (IOException) { }
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
