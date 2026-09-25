using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using VbaDev.Cli;
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
            Assert.Equal($"attempt-{index + 1:D3}", attempt.GetProperty("attemptId").GetString());
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

    [Fact]
    public async Task ProcDumpOutputIdentifiesTheExactChildAndItsUnsignedExitCode()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = TempDirectory.Create();
        var output = Path.Combine(temp.Path, "stdout.txt");
        await File.WriteAllTextAsync(output,
            "Process:              cmd.exe (4242)\r\n" +
            "[12:34:56]Process Exit: PID 4242, Exit Code 0xc0000005\r\n",
            Encoding.Unicode);
        var script = FindOutputReader();
        var command = $". {Quote(script)}; " +
            $"Read-ProcDumpChildEvidence -OutputPath {Quote(output)} " +
            $"-ExecutablePath {Quote(Path.Combine(Environment.SystemDirectory, "cmd.exe"))} " +
            "| ConvertTo-Json -Depth 5";

        var result = await RunPowerShellAsync(command);

        Assert.True(result.ExitCode == 0, $"stdout: {result.Output}\nstderr: {result.Error}");
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal(4242, json.RootElement.GetProperty("childPid").GetInt32());
        Assert.Equal("cmd.exe", json.RootElement.GetProperty("childImageName").GetString());
        Assert.Equal("0xC0000005", json.RootElement.GetProperty("childExitCodeHex").GetString());
        Assert.Equal(3221225477u, json.RootElement.GetProperty("childExitCodeUnsigned").GetUInt32());
    }

    [Fact]
    public async Task ProcDumpOutputRejectsAnExitForAnotherProcess()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = TempDirectory.Create();
        var output = Path.Combine(temp.Path, "stdout.txt");
        await File.WriteAllTextAsync(output,
            "Process:              cmd.exe (4242)\r\n" +
            "[12:34:56]Process Exit: PID 5252, Exit Code 0x00000000\r\n",
            Encoding.Unicode);
        var command = $". {Quote(FindOutputReader())}; " +
            $"Read-ProcDumpChildEvidence -OutputPath {Quote(output)} " +
            $"-ExecutablePath {Quote(Path.Combine(Environment.SystemDirectory, "cmd.exe"))} " +
            "| ConvertTo-Json -Depth 5";

        var result = await RunPowerShellAsync(command);

        Assert.True(result.ExitCode == 0, $"stdout: {result.Output}\nstderr: {result.Error}");
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal(4242, json.RootElement.GetProperty("childPid").GetInt32());
        Assert.True(json.RootElement.GetProperty("childExitIntegrityError").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("childExitCodeHex").ValueKind);
        Assert.Contains("does not match",
            json.RootElement.GetProperty("childExitReason").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcDumpOutputFindsItsExitAfterOddLengthChildUtf8Output()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = TempDirectory.Create();
        var output = Path.Combine(temp.Path, "stdout.txt");
        await File.WriteAllBytesAsync(output,
        [.. Encoding.Unicode.GetBytes("Process:              cmd.exe (4242)\r\n"),
         .. Encoding.UTF8.GetBytes("odd"),
         .. Encoding.Unicode.GetBytes("[12:34:56]Process Exit: PID 4242, Exit Code 0x0000002a\r\n")]);
        var command = $". {Quote(FindOutputReader())}; " +
            $"Read-ProcDumpChildEvidence -OutputPath {Quote(output)} " +
            $"-ExecutablePath {Quote(Path.Combine(Environment.SystemDirectory, "cmd.exe"))} " +
            "| ConvertTo-Json -Depth 5";

        var result = await RunPowerShellAsync(command);

        Assert.True(result.ExitCode == 0, $"stdout: {result.Output}\nstderr: {result.Error}");
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal(4242, json.RootElement.GetProperty("childPid").GetInt32());
        Assert.Equal("0x0000002A", json.RootElement.GetProperty("childExitCodeHex").GetString());
    }

    [Fact]
    public async Task ProcDumpOutputLeavesExitUnknownWhenNoExitLineWasCaptured()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = TempDirectory.Create();
        var output = Path.Combine(temp.Path, "stdout.txt");
        await File.WriteAllBytesAsync(output,
            Encoding.Unicode.GetBytes("Process:              cmd.exe (4242)\r\n"));
        var command = $". {Quote(FindOutputReader())}; " +
            $"Read-ProcDumpChildEvidence -OutputPath {Quote(output)} " +
            $"-ExecutablePath {Quote(Path.Combine(Environment.SystemDirectory, "cmd.exe"))} " +
            "| ConvertTo-Json -Depth 5";

        var result = await RunPowerShellAsync(command);

        Assert.True(result.ExitCode == 0, $"stdout: {result.Output}\nstderr: {result.Error}");
        using var json = JsonDocument.Parse(result.Output);
        Assert.False(json.RootElement.GetProperty("childExitObserved").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("childExitCodeHex").ValueKind);
        Assert.Contains("No complete Process Exit line",
            json.RootElement.GetProperty("childExitReason").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void StartupReceiptRecordsTheActualChildRuntimeWithoutCommandArguments()
    {
        using var temp = TempDirectory.Create();

        VbaDevProcessStartupEvidence.TryWrite(temp.Path, "test-run", "attempt-001");

        var receipt = Assert.Single(Directory.GetFiles(
            Path.Combine(temp.Path, "source-admission-processes"), "*.json"));
        var raw = File.ReadAllText(receipt);
        using var json = JsonDocument.Parse(raw);
        var root = json.RootElement;
        Assert.Equal("vba-dev-runtime-startup", root.GetProperty("kind").GetString());
        Assert.Equal("test-run", root.GetProperty("runId").GetString());
        Assert.Equal("attempt-001", root.GetProperty("attemptId").GetString());
        Assert.Equal(Environment.ProcessId, root.GetProperty("processId").GetInt32());
        Assert.Equal(Environment.Version.ToString(), root.GetProperty("runtimeVersion").GetString());
        Assert.Equal(RuntimeInformation.FrameworkDescription,
            root.GetProperty("frameworkDescription").GetString());
        Assert.Equal(RuntimeInformation.RuntimeIdentifier,
            root.GetProperty("runtimeIdentifier").GetString());
        Assert.False(root.TryGetProperty("commandArguments", out _));
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

    private static string FindOutputReader()
        => Path.Combine(Path.GetDirectoryName(FindLauncher())!, "Read-ProcDumpChildEvidence.ps1");

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
