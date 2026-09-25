using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace VbaDev.Tests;

public sealed class SourceAdmissionDumpLauncherTests
{
    [Fact]
    public async Task PlanOnlyScopesEachAttemptToTheExactChildWithoutGlobalAttachment()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = TempDirectory.Create();
        var executable = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var script = FindLauncher();
        var command = $"& {Quote(script)} -ExecutablePath {Quote(executable)} " +
            $"-CommandArguments @('/c','exit 42') -ProcDumpPath 'C:\\unused\\procdump64.exe' " +
            $"-RunRoot {Quote(temp.Path)} -Count 2 -PlanOnly";

        var result = await RunPowerShellAsync(command);

        Assert.True(result.ExitCode == 0, $"stdout: {result.Output}\nstderr: {result.Error}");
        using var json = JsonDocument.Parse(result.Output);
        var attempts = json.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, attempts.Length);
        for (var index = 0; index < attempts.Length; index++)
        {
            var attempt = attempts[index];
            Assert.Equal(Path.GetFileName(temp.Path), attempt.GetProperty("runId").GetString());
            var arguments = attempt.GetProperty("procdumpArguments")
                .EnumerateArray().Select(item => item.GetString()).ToArray();
            Assert.Equal(["-ma", "-e", "-n", "1", "-x"], arguments.Take(5));
            Assert.Equal(executable, arguments[6]);
            Assert.Equal(["/c", "exit 42"], arguments.Skip(7));
            Assert.EndsWith($"attempt-{index + 1:D3}\\dumps", arguments[5],
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("-i", arguments);
            Assert.DoesNotContain("-w", arguments);
            Assert.DoesNotContain("-accepteula", arguments);
        }
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Fact]
    public async Task RejectsMissingEvidenceRootBeforeAnyChildIsStarted()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = TempDirectory.Create();
        var missing = Path.Combine(temp.Path, "missing");
        var command = $"& {Quote(FindLauncher())} " +
            $"-ExecutablePath {Quote(Path.Combine(Environment.SystemDirectory, "cmd.exe"))} " +
            "-CommandArguments @('/c','exit 42') -ProcDumpPath 'C:\\unused\\procdump64.exe' " +
            $"-RunRoot {Quote(missing)} -PlanOnly";

        var result = await RunPowerShellAsync(command);

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(Directory.Exists(missing));
        Assert.Contains("missing", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindLauncher()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName,
                "scripts", "diagnostics", "Invoke-VbaDevScopedDump.ps1");
            if (File.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException("Scoped dump launcher was not found.");
    }

    private static string Quote(string value)
        => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static async Task<(int ExitCode, string Output, string Error)> RunPowerShellAsync(
        string command)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("pwsh.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("-NoLogo");
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-EncodedCommand");
        process.StartInfo.ArgumentList.Add(Convert.ToBase64String(
            Encoding.Unicode.GetBytes(command)));
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout, await stderr);
    }
}
