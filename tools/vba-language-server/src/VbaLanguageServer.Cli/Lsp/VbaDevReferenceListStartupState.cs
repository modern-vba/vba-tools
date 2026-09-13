using VbaTools.Capabilities;
using VbaTools.Processes;

namespace VbaLanguageServer.Lsp;

internal sealed record VbaDevReferenceListStartupState(
    string? ExecutablePath,
    string? WarningMessage)
{
    private const string RequiredSchemaVersion = "1.0";
    private static readonly CapabilityRequirements RequiredCapabilities = new(
        commandSchemaVersions: new Dictionary<string, string> { ["reference list"] = RequiredSchemaVersion });

    private static readonly string[] CapabilitiesArguments =
        ["capabilities", "--format", "json"];

    public bool IsAvailable => ExecutablePath is not null;

    public static async Task<VbaDevReferenceListStartupState> ResolveAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetExecutablePath(arguments, out var executablePath))
        {
            return InvalidStartupArguments();
        }

        var process = new ProcessInvocation(executablePath);
        return await ResolveValidatedExecutableAsync(
                executablePath,
                process.RunAsync,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task<VbaDevReferenceListStartupState> ResolveAsync(
        IReadOnlyList<string> arguments,
        ProcessInvocationRunner runProcess,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(runProcess);

        if (!TryGetExecutablePath(arguments, out var executablePath))
        {
            return InvalidStartupArguments();
        }

        return await ResolveValidatedExecutableAsync(
                executablePath,
                runProcess,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<VbaDevReferenceListStartupState> ResolveValidatedExecutableAsync(
        string executablePath,
        ProcessInvocationRunner runProcess,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await runProcess(
                CapabilitiesArguments,
                cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                return Unavailable(
                    $"VbaDev at '{executablePath}' exited with code {result.ExitCode} during capability inspection.");
            }

            if (!VbaDevCapabilityAdmission.Admit(result.StandardOutput, RequiredCapabilities).IsAccepted)
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

    private static bool TryGetExecutablePath(
        IReadOnlyList<string> arguments,
        out string executablePath)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        executablePath = "";
        var stdioSeen = false;
        var executableSeen = false;
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument.Equals("--stdio", StringComparison.Ordinal))
            {
                if (stdioSeen)
                {
                    return false;
                }

                stdioSeen = true;
                continue;
            }

            if (!argument.Equals("--vba-dev", StringComparison.Ordinal)
                || executableSeen
                || index + 1 >= arguments.Count
                || string.IsNullOrWhiteSpace(arguments[index + 1])
                || !Path.IsPathFullyQualified(arguments[index + 1]))
            {
                return false;
            }

            executablePath = arguments[++index];
            executableSeen = true;
        }

        return executableSeen;
    }

    private static VbaDevReferenceListStartupState InvalidStartupArguments()
        => Unavailable(
            "VBA Language Server did not receive one absolute --vba-dev executable path.");

    private static VbaDevReferenceListStartupState Unavailable(string reason)
        => new(
            null,
            $"{reason} CLI-backed reference catalog resolution is disabled; registry-only discovery remains available.");
}
