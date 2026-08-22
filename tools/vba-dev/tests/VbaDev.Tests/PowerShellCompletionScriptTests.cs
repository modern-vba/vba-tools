using System.Diagnostics;
using System.Text;
using VbaDev.Cli;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;
using Xunit;

namespace VbaDev.Tests;

public sealed class PowerShellCompletionScriptTests
{
    [Theory]
    [InlineData("powershell.exe")]
    [InlineData("pwsh.exe")]
    public async Task GeneratedScriptCompletesTheNextTokenThroughTheExactExecutable(
        string shellExecutable)
    {
        using var temp = TempDirectory.Create();
        var executablePath = FindBuiltExecutable();
        var registrationScript = PowerShellCompletionScriptRenderer.Render(executablePath);
        var contractScriptPath = Path.Combine(temp.Path, "completion-contract.ps1");
        File.WriteAllText(
            contractScriptPath,
            $$"""
            function Install-VbaDevCompletion {
            {{Indent(registrationScript, 4)}}
            }

            Install-VbaDevCompletion
            $env:PATH = ''
            $line = 'vba-dev reference '
            $completion = [System.Management.Automation.CommandCompletion]::CompleteInput(
                $line,
                $line.Length,
                $null)
            $completion.CompletionMatches | ForEach-Object { $_.ListItemText }
            """ + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var result = await RunPowerShellAsync(shellExecutable, contractScriptPath);
        var suggestions = result.StandardOutput.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.True(
            result.ExitCode == 0,
            $"stdout: {result.StandardOutput}{Environment.NewLine}stderr: {result.StandardError}");
        Assert.Contains("add", suggestions);
        Assert.Contains("list", suggestions);
        Assert.Contains("remove", suggestions);
        Assert.Empty(result.StandardError);
    }

    [Theory]
    [InlineData("powershell.exe")]
    [InlineData("pwsh.exe")]
    public async Task GeneratedScriptCompletesAtWhitespaceBeforeLaterTokens(
        string shellExecutable)
    {
        using var temp = TempDirectory.Create();
        var registrationScript = PowerShellCompletionScriptRenderer.Render(FindBuiltExecutable());
        var contractScriptPath = Path.Combine(temp.Path, "whitespace-cursor-contract.ps1");
        File.WriteAllText(
            contractScriptPath,
            $$"""
            function Install-VbaDevCompletion {
            {{Indent(registrationScript, 4)}}
            }

            Install-VbaDevCompletion
            $env:PATH = ''
            $line = 'vba-dev reference  --help'
            $cursor = $line.IndexOf('  --', [StringComparison]::Ordinal) + 1
            $completion = [System.Management.Automation.CommandCompletion]::CompleteInput(
                $line,
                $cursor,
                $null)
            $completion.CompletionMatches | ForEach-Object { $_.ListItemText }
            """ + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var result = await RunPowerShellAsync(shellExecutable, contractScriptPath);
        var suggestions = result.StandardOutput.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.True(
            result.ExitCode == 0,
            $"stdout: {result.StandardOutput}{Environment.NewLine}stderr: {result.StandardError}");
        Assert.Contains("add", suggestions);
        Assert.Contains("list", suggestions);
        Assert.Contains("remove", suggestions);
        Assert.Empty(result.StandardError);
    }

    [Theory]
    [InlineData("powershell.exe")]
    [InlineData("pwsh.exe")]
    public async Task GeneratedScriptPreservesRootGraphFuzzyMatches(
        string shellExecutable)
    {
        using var temp = TempDirectory.Create();
        var registrationScript = PowerShellCompletionScriptRenderer.Render(FindBuiltExecutable());
        var contractScriptPath = Path.Combine(temp.Path, "fuzzy-completion-contract.ps1");
        File.WriteAllText(
            contractScriptPath,
            $$"""
            function Install-VbaDevCompletion {
            {{Indent(registrationScript, 4)}}
            }

            Install-VbaDevCompletion
            $env:PATH = ''
            $line = 'vba-dev b'
            $completion = [System.Management.Automation.CommandCompletion]::CompleteInput(
                $line,
                $line.Length,
                $null)
            $completion.CompletionMatches | ForEach-Object { $_.ListItemText }
            """ + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var result = await RunPowerShellAsync(shellExecutable, contractScriptPath);
        var suggestions = result.StandardOutput.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.True(
            result.ExitCode == 0,
            $"stdout: {result.StandardOutput}{Environment.NewLine}stderr: {result.StandardError}");
        Assert.Contains("build", suggestions);
        Assert.Contains("publish", suggestions);
        Assert.Contains("capabilities", suggestions);
        Assert.Empty(result.StandardError);
    }

    [Theory]
    [InlineData("powershell.exe")]
    [InlineData("pwsh.exe")]
    public async Task GeneratedScriptRegistersEverySupportedCommandForm(
        string shellExecutable)
    {
        using var temp = TempDirectory.Create();
        var executablePath = FindBuiltExecutable();
        var registrationScript = PowerShellCompletionScriptRenderer.Render(executablePath);
        var absoluteLine = $"& {ToPowerShellSingleQuotedLiteral(executablePath)} reference ";
        var contractScriptPath = Path.Combine(temp.Path, "registered-command-forms-contract.ps1");
        File.WriteAllText(
            contractScriptPath,
            $$"""
            function Install-VbaDevCompletion {
            {{Indent(registrationScript, 4)}}
            }

            Install-VbaDevCompletion
            $env:PATH = ''
            $lines = [ordered]@{
                short = 'vba-dev reference '
                exe = 'vba-dev.exe reference '
                absolute = {{ToPowerShellSingleQuotedLiteral(absoluteLine)}}
            }
            foreach ($entry in $lines.GetEnumerator()) {
                $completion = [System.Management.Automation.CommandCompletion]::CompleteInput(
                    $entry.Value,
                    $entry.Value.Length,
                    $null)
                $completion.CompletionMatches | ForEach-Object {
                    "[$($entry.Key)]$($_.ListItemText)"
                }
            }
            """ + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var result = await RunPowerShellAsync(shellExecutable, contractScriptPath);
        var suggestions = result.StandardOutput.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.True(
            result.ExitCode == 0,
            $"stdout: {result.StandardOutput}{Environment.NewLine}stderr: {result.StandardError}");
        Assert.Contains("[short]add", suggestions);
        Assert.Contains("[exe]add", suggestions);
        Assert.Contains("[absolute]add", suggestions);
        Assert.Empty(result.StandardError);
    }

    [Theory]
    [InlineData("powershell.exe")]
    [InlineData("pwsh.exe")]
    public async Task GeneratedScriptCompletesAnIncompleteQuotedReferenceName(
        string shellExecutable)
    {
        using var temp = TempDirectory.Create();
        var projectRoot = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", projectRoot, null);
        manifest.Documents["Book1"].References.Add(new VbaProjectReference("O'Brien Library"));
        new JsonProjectManifestStore().Save(projectRoot, manifest);

        var registrationScript = PowerShellCompletionScriptRenderer.Render(FindBuiltExecutable());
        var contractScriptPath = Path.Combine(temp.Path, "quoted-completion-contract.ps1");
        File.WriteAllText(
            contractScriptPath,
            $$"""
            function Install-VbaDevCompletion {
            {{Indent(registrationScript, 4)}}
            }

            Install-VbaDevCompletion
            $env:PATH = ''
            $line = "vba-dev reference remove 'O"
            $completion = [System.Management.Automation.CommandCompletion]::CompleteInput(
                $line,
                $line.Length,
                $null)
            $completion.CompletionMatches | ForEach-Object {
                "$($_.ListItemText)`t$($_.CompletionText)"
            }
            """ + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var result = await RunPowerShellAsync(
            shellExecutable,
            contractScriptPath,
            projectRoot);
        var suggestions = result.StandardOutput.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.True(
            result.ExitCode == 0,
            $"stdout: {result.StandardOutput}{Environment.NewLine}stderr: {result.StandardError}");
        Assert.Contains("O'Brien Library\t'O''Brien Library'", suggestions);
        Assert.Empty(result.StandardError);
    }

    [Theory]
    [InlineData("powershell.exe")]
    [InlineData("pwsh.exe")]
    public async Task GeneratedScriptFiltersOnlyTheTruncatedMultiwordPrefix(
        string shellExecutable)
    {
        using var temp = TempDirectory.Create();
        var projectRoot = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", projectRoot, null);
        manifest.Documents["Book1"].References.AddRange(
        [
            new VbaProjectReference("Alpha One"),
            new VbaProjectReference("Alpha Two")
        ]);
        new JsonProjectManifestStore().Save(projectRoot, manifest);

        var registrationScript = PowerShellCompletionScriptRenderer.Render(FindBuiltExecutable());
        var contractScriptPath = Path.Combine(temp.Path, "multiword-prefix-contract.ps1");
        File.WriteAllText(
            contractScriptPath,
            $$"""
            function Install-VbaDevCompletion {
            {{Indent(registrationScript, 4)}}
            }

            Install-VbaDevCompletion
            $env:PATH = ''
            $line = "vba-dev reference remove 'Alpha T"
            $completion = [System.Management.Automation.CommandCompletion]::CompleteInput(
                $line,
                $line.Length,
                $null)
            $completion.CompletionMatches | ForEach-Object { $_.ListItemText }
            """ + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var result = await RunPowerShellAsync(
            shellExecutable,
            contractScriptPath,
            projectRoot);
        var suggestions = result.StandardOutput.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.True(
            result.ExitCode == 0,
            $"stdout: {result.StandardOutput}{Environment.NewLine}stderr: {result.StandardError}");
        Assert.Contains("Alpha Two", suggestions);
        Assert.DoesNotContain("Alpha One", suggestions);
        Assert.Empty(result.StandardError);
    }

    [Theory]
    [InlineData("powershell.exe")]
    [InlineData("pwsh.exe")]
    public async Task GeneratedScriptCompletesAnIncompleteDoubleQuotedNonAsciiName(
        string shellExecutable)
    {
        using var temp = TempDirectory.Create();
        var projectRoot = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", projectRoot, null);
        manifest.Documents["Book1"].References.Add(
            new VbaProjectReference("日本語 ライブラリ"));
        new JsonProjectManifestStore().Save(projectRoot, manifest);

        var registrationScript = PowerShellCompletionScriptRenderer.Render(FindBuiltExecutable());
        const string commandLine = "vba-dev reference remove \"日";
        var contractScriptPath = Path.Combine(temp.Path, "double-quoted-completion-contract.ps1");
        File.WriteAllText(
            contractScriptPath,
            $$"""
            function Install-VbaDevCompletion {
            {{Indent(registrationScript, 4)}}
            }

            Install-VbaDevCompletion
            $env:PATH = ''
            [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
            $OutputEncoding = [Console]::OutputEncoding
            $line = {{ToPowerShellSingleQuotedLiteral(commandLine)}}
            $completion = [System.Management.Automation.CommandCompletion]::CompleteInput(
                $line,
                $line.Length,
                $null)
            $completion.CompletionMatches | ForEach-Object {
                "$($_.ListItemText)`t$($_.CompletionText)"
            }
            """ + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var result = await RunPowerShellAsync(
            shellExecutable,
            contractScriptPath,
            projectRoot);
        var suggestions = result.StandardOutput.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.True(
            result.ExitCode == 0,
            $"stdout: {result.StandardOutput}{Environment.NewLine}stderr: {result.StandardError}");
        Assert.Contains("日本語 ライブラリ\t'日本語 ライブラリ'", suggestions);
        Assert.Empty(result.StandardError);
    }

    [Theory]
    [InlineData("powershell.exe")]
    [InlineData("pwsh.exe")]
    public async Task GeneratedScriptCompletesAtACursorInsideTheCommandLineWithAnExplicitProject(
        string shellExecutable)
    {
        using var temp = TempDirectory.Create();
        var projectRoot = temp.CreateDirectory("Project Space's 日本語");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", projectRoot, null);
        manifest.Documents["Book1"].References.Add(
            new VbaProjectReference("日本語 ライブラリ"));
        new JsonProjectManifestStore().Save(projectRoot, manifest);

        var registrationScript = PowerShellCompletionScriptRenderer.Render(FindBuiltExecutable());
        var commandLine =
            $"vba-dev reference remove 日 --project '{projectRoot.Replace("'", "''", StringComparison.Ordinal)}'";
        var contractScriptPath = Path.Combine(temp.Path, "cursor-completion-contract.ps1");
        File.WriteAllText(
            contractScriptPath,
            $$"""
            function Install-VbaDevCompletion {
            {{Indent(registrationScript, 4)}}
            }

            Install-VbaDevCompletion
            $env:PATH = ''
            [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
            $OutputEncoding = [Console]::OutputEncoding
            $line = {{ToPowerShellSingleQuotedLiteral(commandLine)}}
            $cursor = $line.IndexOf('日', [StringComparison]::Ordinal) + 1
            $completion = [System.Management.Automation.CommandCompletion]::CompleteInput(
                $line,
                $cursor,
                $null)
            $completion.CompletionMatches | ForEach-Object {
                "$($_.ListItemText)`t$($_.CompletionText)"
            }
            """ + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var result = await RunPowerShellAsync(
            shellExecutable,
            contractScriptPath,
            projectRoot);
        var suggestions = result.StandardOutput.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.True(
            result.ExitCode == 0,
            $"stdout: {result.StandardOutput}{Environment.NewLine}stderr: {result.StandardError}");
        Assert.Contains(
            "日本語 ライブラリ\t'日本語 ライブラリ'",
            suggestions);
        Assert.Empty(result.StandardError);
    }

    private static string FindBuiltExecutable()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
                            ?? throw new InvalidOperationException("The test build configuration is unavailable.");
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src",
                "VbaDev.Cli",
                "bin",
                configuration,
                "net10.0",
                "win-x64",
                "vba-dev.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("The built vba-dev executable was not found.");
    }

    private static string Indent(string value, int spaces)
    {
        var prefix = new string(' ', spaces);
        return prefix + value.Replace(
            Environment.NewLine,
            Environment.NewLine + prefix,
            StringComparison.Ordinal);
    }

    private static string ToPowerShellSingleQuotedLiteral(string value)
        => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static async Task<ShellResult> RunPowerShellAsync(
        string shellExecutable,
        string scriptPath,
        string? workingDirectory = null)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(shellExecutable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true
            }
        };
        if (workingDirectory is not null)
        {
            process.StartInfo.WorkingDirectory = workingDirectory;
        }
        process.StartInfo.ArgumentList.Add("-NoLogo");
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-File");
        process.StartInfo.ArgumentList.Add(scriptPath);

        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ShellResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }

    private sealed record ShellResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
