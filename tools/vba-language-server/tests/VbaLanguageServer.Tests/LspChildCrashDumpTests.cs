using System.Diagnostics;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class LspChildCrashDumpTests
{
    private const string RunId = "run-20260930T120000000Z-0123456789abcdef";
    private const long SufficientFreeBytes = 21L * 1024 * 1024 * 1024;

    [Fact]
    public void Diagnostic_run_arms_two_concurrent_LSP_children_but_not_a_third()
    {
        if (!OperatingSystem.IsWindows()) return;
        var parent = Directory.CreateTempSubdirectory("vba-lsp-dumps-").FullName;
        var runRoot = Directory.CreateDirectory(Path.Combine(parent, RunId)).FullName;
        try
        {
            var first = NewStartInfo();
            using (var firstLease = LspChildCrashDump.TryConfigure(
                       first, runRoot, RunId, SufficientFreeBytes))
            {
                Assert.NotNull(firstLease);
                Assert.True(firstLease.IsArmed);
                Assert.Equal(Path.Combine(runRoot, "lsp-crash-dumps"),
                    firstLease.DiagnosticDirectory);
                Assert.Equal("1", first.Environment["DOTNET_DbgEnableMiniDump"]);
                Assert.Equal("4", first.Environment["DOTNET_DbgMiniDumpType"]);
                var dumpPath = first.Environment["DOTNET_DbgMiniDumpName"];
                Assert.StartsWith(Path.Combine(runRoot, "lsp-crash-dumps"), dumpPath);
                Assert.Contains("%p", dumpPath);
                Assert.Contains("%t", dumpPath);

                var second = NewStartInfo();
                using var secondLease = LspChildCrashDump.TryConfigure(
                    second, runRoot, RunId, SufficientFreeBytes);
                Assert.NotNull(secondLease);
                Assert.True(secondLease.IsArmed);
                Assert.NotEqual(dumpPath, second.Environment["DOTNET_DbgMiniDumpName"]);

                var third = NewStartInfo();
                using var thirdPlan = LspChildCrashDump.TryConfigure(
                    third, runRoot, RunId, SufficientFreeBytes);
                Assert.NotNull(thirdPlan);
                Assert.False(thirdPlan.IsArmed);
                Assert.Equal(firstLease.DiagnosticDirectory, thirdPlan.DiagnosticDirectory);
                Assert.Equal("slot-busy", thirdPlan.SkipReason);
                Assert.False(third.Environment.ContainsKey("DOTNET_DbgEnableMiniDump"));

                secondLease.Dispose();
                var replacement = NewStartInfo();
                using var replacementLease = LspChildCrashDump.TryConfigure(
                    replacement, runRoot, RunId, SufficientFreeBytes);
                Assert.NotNull(replacementLease);
                Assert.True(replacementLease.IsArmed);
                Assert.NotEqual(dumpPath, replacement.Environment["DOTNET_DbgMiniDumpName"]);
            }

            using (var nextLease = LspChildCrashDump.TryConfigure(
                       NewStartInfo(), runRoot, RunId, SufficientFreeBytes))
            {
                Assert.NotNull(nextLease);
                Assert.True(nextLease.IsArmed);
            }

            File.WriteAllBytes(Path.Combine(runRoot, "lsp-crash-dumps", "lsp-1.dmp"), [1]);
            using var exhausted = LspChildCrashDump.TryConfigure(
                NewStartInfo(), runRoot, RunId, SufficientFreeBytes);
            Assert.NotNull(exhausted);
            Assert.False(exhausted.IsArmed);
            Assert.Equal("dump-budget-exhausted", exhausted.SkipReason);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void Existing_dump_stops_new_arms_even_while_other_children_are_armed()
    {
        if (!OperatingSystem.IsWindows()) return;
        var parent = Directory.CreateTempSubdirectory("vba-lsp-dumps-").FullName;
        var runRoot = Directory.CreateDirectory(Path.Combine(parent, RunId)).FullName;
        try
        {
            using (var firstLease = LspChildCrashDump.TryConfigure(
                       NewStartInfo(), runRoot, RunId, SufficientFreeBytes))
            using (var secondLease = LspChildCrashDump.TryConfigure(
                       NewStartInfo(), runRoot, RunId, SufficientFreeBytes))
            {
                Assert.NotNull(firstLease);
                Assert.NotNull(secondLease);
                Assert.True(firstLease.IsArmed);
                Assert.True(secondLease.IsArmed);

                File.WriteAllBytes(Path.Combine(runRoot, "lsp-crash-dumps", "first.dmp"), [1]);
                var later = NewStartInfo();
                using var laterPlan = LspChildCrashDump.TryConfigure(
                    later, runRoot, RunId, SufficientFreeBytes);
                Assert.NotNull(laterPlan);
                Assert.False(laterPlan.IsArmed);
                Assert.Equal("dump-budget-exhausted", laterPlan.SkipReason);
                Assert.False(later.Environment.ContainsKey("DOTNET_DbgEnableMiniDump"));
            }

            using var afterRelease = LspChildCrashDump.TryConfigure(
                NewStartInfo(), runRoot, RunId, SufficientFreeBytes);
            Assert.NotNull(afterRelease);
            Assert.False(afterRelease.IsArmed);
            Assert.Equal("dump-budget-exhausted", afterRelease.SkipReason);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void Invalid_or_low_space_run_does_not_change_the_child_environment()
    {
        if (!OperatingSystem.IsWindows()) return;
        var parent = Directory.CreateTempSubdirectory("vba-lsp-dumps-").FullName;
        var runRoot = Directory.CreateDirectory(Path.Combine(parent, RunId)).FullName;
        try
        {
            foreach (var (candidateRoot, candidateId, freeBytes) in new[]
                     {
                         (runRoot, "run-20260930T120000000Z-fedcba9876543210", SufficientFreeBytes),
                         (Path.Combine(parent, "missing", RunId), RunId, SufficientFreeBytes),
                         (@"\\server\share\" + RunId, RunId, SufficientFreeBytes),
                         (RunId, RunId, SufficientFreeBytes)
                     })
            {
                var startInfo = NewStartInfo();
                Assert.Null(LspChildCrashDump.TryConfigure(
                    startInfo, candidateRoot, candidateId, freeBytes));
                Assert.False(startInfo.Environment.ContainsKey("DOTNET_DbgEnableMiniDump"));
                Assert.False(startInfo.Environment.ContainsKey("DOTNET_DbgMiniDumpName"));
            }

            var lowSpace = NewStartInfo();
            using var skipped = LspChildCrashDump.TryConfigure(
                lowSpace, runRoot, RunId, 19L * 1024 * 1024 * 1024);
            Assert.NotNull(skipped);
            Assert.False(skipped.IsArmed);
            Assert.Equal("insufficient-disk-space", skipped.SkipReason);
            Assert.False(lowSpace.Environment.ContainsKey("DOTNET_DbgEnableMiniDump"));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void Diagnostic_receipt_keeps_child_identity_without_raw_arguments()
    {
        if (!OperatingSystem.IsWindows()) return;
        var parent = Directory.CreateTempSubdirectory("vba-lsp-dumps-").FullName;
        var runRoot = Directory.CreateDirectory(Path.Combine(parent, RunId)).FullName;
        try
        {
            var executable = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("exit");
            startInfo.ArgumentList.Add("7");
            using var plan = LspChildCrashDump.TryConfigure(
                startInfo, runRoot, RunId, SufficientFreeBytes);
            Assert.NotNull(plan);
            using var child = Process.Start(startInfo);
            Assert.NotNull(child);
            plan.Started(child, "language-server-tests");
            Assert.True(child.WaitForExit(5000));
            plan.Finished(child);

            var receiptPath = Path.Combine(runRoot, "lsp-crash-dumps", "children.ndjson");
            var raw = File.ReadAllText(receiptPath);
            var records = File.ReadAllLines(receiptPath)
                .Select(line => System.Text.Json.JsonDocument.Parse(line))
                .ToArray();
            try
            {
                Assert.Equal(2, records.Length);
                var started = records[0].RootElement;
                Assert.Equal("started", started.GetProperty("eventType").GetString());
                Assert.Equal(child.Id, started.GetProperty("processId").GetInt32());
                Assert.True(started.GetProperty("armed").GetBoolean());
                Assert.Equal(64, started.GetProperty("executableSha256").GetString()?.Length);
                Assert.Equal(3, started.GetProperty("argumentCount").GetInt32());
                Assert.DoesNotContain("\"/c\"", raw);
                var finished = records[1].RootElement;
                Assert.Equal(7, finished.GetProperty("exitCode").GetInt32());
                Assert.False(finished.GetProperty("dumpExists").GetBoolean());
            }
            finally
            {
                foreach (var record in records) record.Dispose();
            }
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void Skipped_capture_records_why_the_child_was_not_armed()
    {
        if (!OperatingSystem.IsWindows()) return;
        var parent = Directory.CreateTempSubdirectory("vba-lsp-dumps-").FullName;
        var runRoot = Directory.CreateDirectory(Path.Combine(parent, RunId)).FullName;
        try
        {
            var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("exit");
            startInfo.ArgumentList.Add("0");
            using var plan = LspChildCrashDump.TryConfigure(
                startInfo, runRoot, RunId, 19L * 1024 * 1024 * 1024);
            Assert.NotNull(plan);
            Assert.False(plan.IsArmed);
            using var child = Process.Start(startInfo);
            Assert.NotNull(child);
            plan.Started(child, "language-server-tests");
            Assert.True(child.WaitForExit(5000));
            plan.Finished(child);

            var raw = File.ReadAllText(Path.Combine(runRoot, "lsp-crash-dumps", "children.ndjson"));
            Assert.Contains("\"armed\":false", raw);
            Assert.Contains("\"skipReason\":\"insufficient-disk-space\"", raw);
            Assert.Contains($"\"processId\":{child.Id}", raw);
            Assert.False(startInfo.Environment.ContainsKey("DOTNET_DbgEnableMiniDump"));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    private static ProcessStartInfo NewStartInfo() => new("vba-language-server.exe")
    {
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
}
