using System.Diagnostics;
using System.Reflection;

namespace VbaTools.Integration.Tests;

internal static class PrebuiltTools
{
    public static string LanguageServerPath()
        => Resolve(
            "VBA_TOOLS_INTEGRATION_LANGUAGE_SERVER_PATH",
            "vba-language-server",
            "VbaLanguageServer.Cli",
            "vba-language-server.exe");

    public static string VbaDevPath()
        => Resolve(
            "VBA_TOOLS_INTEGRATION_VBA_DEV_PATH",
            "vba-dev",
            "VbaDev.Cli",
            "vba-dev.exe");

    private static string Resolve(
        string environmentVariable,
        string toolDirectory,
        string projectName,
        string executableName)
    {
        var configured = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrEmpty(configured))
        {
            if (!Path.IsPathFullyQualified(configured) || !File.Exists(configured))
            {
                throw new InvalidOperationException(
                    $"{environmentVariable} must name an existing absolute executable path: {configured}");
            }

            return configured;
        }

        var configuration = typeof(PrebuiltTools).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var projectDirectory = Path.Combine(directory.FullName, "tools", toolDirectory, "src", projectName);
            if (!File.Exists(Path.Combine(projectDirectory, projectName + ".csproj")))
            {
                continue;
            }

            var path = Path.Combine(projectDirectory, "bin", configuration, "net10.0", "win-x64", executableName);
            if (File.Exists(path))
            {
                return path;
            }

            throw new InvalidOperationException(
                $"Build {projectName} first, or set {environmentVariable}. Expected already-built executable: {path}");
        }

        throw new InvalidOperationException(
            $"Cannot locate the repository from the test output directory. Set {environmentVariable} to an already-built executable.");
    }

    public static async Task RunVbaDevAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add("--cancellation-transport");
        startInfo.ArgumentList.Add("stdin-v1");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not launch the already-built vba-dev executable.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var completion = Task.WhenAll(process.WaitForExitAsync(), stdout, stderr);
        try
        {
            try
            {
                await completion.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await SendCancellationAsync(process.StandardInput);
                }
                catch (IOException) when (process.HasExited)
                {
                }

                await completion.WaitAsync(TimeSpan.FromSeconds(30));
                throw;
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"vba-dev exited with code {process.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{await stdout}{Environment.NewLine}stderr:{Environment.NewLine}{await stderr}");
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    internal static async Task SendCancellationAsync(TextWriter standardInput)
    {
        await standardInput.WriteAsync("cancel\n");
        await standardInput.FlushAsync();
    }
}
