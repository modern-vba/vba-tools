using System.Diagnostics;
using System.Text.Json;

namespace VbaLanguageServer.Lsp;

internal sealed record VbaDevCapabilitiesProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

internal delegate Task<VbaDevCapabilitiesProcessResult> VbaDevCapabilitiesProcessRunner(
    string executablePath,
    IReadOnlyList<string> arguments,
    CancellationToken cancellationToken);

internal sealed record VbaDevReferenceListStartupState(
    string? ExecutablePath,
    string? WarningMessage)
{
    private const string RequiredSchemaVersion = "1.0";

    private static readonly string[] CapabilitiesArguments =
        ["capabilities", "--format", "json"];

    public bool IsAvailable => ExecutablePath is not null;

    public static Task<VbaDevReferenceListStartupState> ResolveAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
        => ResolveAsync(arguments, RunProcessAsync, cancellationToken);

    public static async Task<VbaDevReferenceListStartupState> ResolveAsync(
        IReadOnlyList<string> arguments,
        VbaDevCapabilitiesProcessRunner runProcess,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(runProcess);

        if (arguments.Count != 2
            || !arguments[0].Equals("--vba-dev", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(arguments[1])
            || !Path.IsPathFullyQualified(arguments[1]))
        {
            return Unavailable(
                "VBA Language Server did not receive one absolute --vba-dev executable path.");
        }

        var executablePath = arguments[1];
        try
        {
            var result = await runProcess(
                executablePath,
                CapabilitiesArguments,
                cancellationToken);
            if (result.ExitCode != 0)
            {
                return Unavailable(
                    $"VbaDev at '{executablePath}' exited with code {result.ExitCode} during capability inspection.");
            }

            using var document = JsonDocument.Parse(result.StandardOutput);
            if (!HasRequiredReferenceListCapability(document.RootElement))
            {
                return Unavailable(
                    $"VbaDev at '{executablePath}' does not report reference list output schema {RequiredSchemaVersion}.");
            }

            return new VbaDevReferenceListStartupState(executablePath, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Unavailable(
                $"VbaDev at '{executablePath}' could not be validated: {exception.Message}");
        }
    }

    private static bool HasRequiredReferenceListCapability(JsonElement root)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("commands", out var commands)
            && commands.ValueKind == JsonValueKind.Object
            && commands.TryGetProperty("reference list", out var referenceList)
            && referenceList.ValueKind == JsonValueKind.Object
            && referenceList.TryGetProperty("outputSchemaVersion", out var schemaVersion)
            && schemaVersion.ValueKind == JsonValueKind.String
            && schemaVersion.GetString() == RequiredSchemaVersion;

    private static async Task<VbaDevCapabilitiesProcessResult> RunProcessAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException(
                $"VbaDev at '{executablePath}' could not be started.");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new VbaDevCapabilitiesProcessResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }

    private static VbaDevReferenceListStartupState Unavailable(string reason)
        => new(
            null,
            $"{reason} CLI-backed reference catalog resolution is disabled; registry-only discovery remains available.");
}
