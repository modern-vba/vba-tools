using System.Text;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class LanguageServerStderrEvidenceTests
{
    private const string RunId = "run-20260930T120000000Z-0123456789abcdef";
    private const string FaultOrigin = "fault-origin-before-twenty-later-lines";

    [Fact]
    public async Task Abnormal_child_preserves_early_stderr_locally_without_exposing_it_in_failure()
    {
        if (!OperatingSystem.IsWindows()) return;

        var parent = Directory.CreateTempSubdirectory("vba-lsp-stderr-").FullName;
        var runRoot = Directory.CreateDirectory(Path.Combine(parent, RunId)).FullName;
        try
        {
            var command = new StringBuilder($"echo {FaultOrigin} 1>&2");
            for (var index = 1; index <= 25; index++)
            {
                command.Append($" & echo later-{index} 1>&2");
            }

            command.Append(" & exit /b 7");
            await using (var child = await LanguageServerProcessHarness.StartFromExecutableAsync(
                             Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                             serverArguments: ["/d", "/c", command.ToString()],
                             diagnosticRunRoot: runRoot,
                             diagnosticRunId: RunId))
            {
                Assert.Equal(7, await child.WaitForProcessExitAsync());
                var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => child.WaitForLogMessageAsync("never-sent"));
                Assert.DoesNotContain(FaultOrigin, failure.ToString(), StringComparison.Ordinal);
                Assert.DoesNotContain("later-25", failure.ToString(), StringComparison.Ordinal);
            }

            var captures = Directory.GetFiles(
                Path.Combine(runRoot, "lsp-crash-dumps"), "lsp-stderr-*.log");
            var capture = Assert.Single(captures);
            var stderr = File.ReadAllText(capture);
            Assert.Contains(FaultOrigin, stderr, StringComparison.Ordinal);
            Assert.Contains("later-25", stderr, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task Oversized_stderr_keeps_fault_origin_and_marks_the_two_MiB_limit()
    {
        if (!OperatingSystem.IsWindows()) return;

        var parent = Directory.CreateTempSubdirectory("vba-lsp-stderr-").FullName;
        var runRoot = Directory.CreateDirectory(Path.Combine(parent, RunId)).FullName;
        try
        {
            var command = $"echo {FaultOrigin} 1>&2"
                + $" & (for /L %i in (1,1,2300) do @echo {new string('x', 1024)}-%i 1>&2)"
                + " & exit /b 7";
            await using (var child = await LanguageServerProcessHarness.StartFromExecutableAsync(
                             Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                             serverArguments: ["/d", "/c", command],
                             diagnosticRunRoot: runRoot,
                             diagnosticRunId: RunId))
            {
                Assert.Equal(7, await child.WaitForProcessExitAsync());
            }

            var path = Assert.Single(Directory.GetFiles(
                Path.Combine(runRoot, "lsp-crash-dumps"), "lsp-stderr-*.log"));
            var content = File.ReadAllText(path);
            Assert.InRange(new FileInfo(path).Length, 1, 2 * 1024 * 1024);
            Assert.StartsWith(FaultOrigin, content, StringComparison.Ordinal);
            Assert.EndsWith("[stderr capture truncated at 2 MiB]\n", content,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task Successful_child_does_not_leave_stderr_evidence()
    {
        if (!OperatingSystem.IsWindows()) return;

        var parent = Directory.CreateTempSubdirectory("vba-lsp-stderr-").FullName;
        var runRoot = Directory.CreateDirectory(Path.Combine(parent, RunId)).FullName;
        try
        {
            await using (var child = await LanguageServerProcessHarness.StartFromExecutableAsync(
                             Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                             serverArguments: ["/d", "/c", "echo normal-stderr 1>&2 & exit /b 0"],
                             diagnosticRunRoot: runRoot,
                             diagnosticRunId: RunId))
            {
                Assert.Equal(0, await child.WaitForProcessExitAsync());
            }

            Assert.Empty(Directory.GetFiles(
                Path.Combine(runRoot, "lsp-crash-dumps"), "lsp-stderr-*"));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task Invalid_run_identity_disables_stderr_evidence()
    {
        if (!OperatingSystem.IsWindows()) return;

        var parent = Directory.CreateTempSubdirectory("vba-lsp-stderr-").FullName;
        var runRoot = Directory.CreateDirectory(Path.Combine(parent, RunId)).FullName;
        try
        {
            await using (var child = await LanguageServerProcessHarness.StartFromExecutableAsync(
                             Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                             serverArguments: ["/d", "/c", "echo private-stderr 1>&2 & exit /b 7"],
                             diagnosticRunRoot: runRoot,
                             diagnosticRunId: "run-20260930T120000000Z-fedcba9876543210"))
            {
                Assert.Equal(7, await child.WaitForProcessExitAsync());
                var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => child.WaitForLogMessageAsync("never-sent"));
                Assert.Contains("private-stderr", failure.Message, StringComparison.Ordinal);
            }

            Assert.Empty(Directory.GetFileSystemEntries(runRoot));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task Ordinary_failure_tail_keeps_twenty_bounded_lines()
    {
        if (!OperatingSystem.IsWindows()) return;

        var oversizedLine = new string('x', 3000);
        var command = new StringBuilder("echo oldest-line 1>&2");
        for (var index = 1; index <= 20; index++)
        {
            command.Append($" & echo recent-{index} 1>&2");
        }

        command.Append($" & echo {oversizedLine} 1>&2 & exit /b 7");
        await using var child = await LanguageServerProcessHarness.StartFromExecutableAsync(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            serverArguments: ["/d", "/c", command.ToString()],
            diagnosticRunRoot: RunId,
            diagnosticRunId: RunId);
        Assert.Equal(7, await child.WaitForProcessExitAsync());
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => child.WaitForLogMessageAsync("never-sent"));
        Assert.DoesNotContain("oldest-line", failure.Message, StringComparison.Ordinal);
        Assert.Contains("recent-20", failure.Message, StringComparison.Ordinal);
        Assert.Contains("[line truncated]", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(oversizedLine, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diagnostic_run_keeps_no_more_than_sixteen_stderr_files()
    {
        if (!OperatingSystem.IsWindows()) return;

        var parent = Directory.CreateTempSubdirectory("vba-lsp-stderr-").FullName;
        var runRoot = Directory.CreateDirectory(Path.Combine(parent, RunId)).FullName;
        try
        {
            for (var index = 0; index < 17; index++)
            {
                await using var child = await LanguageServerProcessHarness.StartFromExecutableAsync(
                    Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                    serverArguments: ["/d", "/c", "echo bounded-stderr 1>&2 & exit /b 7"],
                    diagnosticRunRoot: runRoot,
                    diagnosticRunId: RunId);
                Assert.Equal(7, await child.WaitForProcessExitAsync());
            }

            var directory = Path.Combine(runRoot, "lsp-crash-dumps");
            var captures = Directory.GetFiles(directory, "lsp-stderr-*.log");
            Assert.Equal(16, captures.Length);
            Assert.Equal(16, Directory.GetFiles(directory, "lsp-stderr-*.claim").Length);
            Assert.True(captures.Sum(path => new FileInfo(path).Length)
                <= 16L * 2 * 1024 * 1024);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }
}
