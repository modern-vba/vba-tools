using System.Text.Json;
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
    private static readonly TimeSpan FatalExitRetryDelay =
        TimeSpan.FromMilliseconds(100);

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
                cancellationToken,
                Console.Error.WriteLine)
            .ConfigureAwait(false);
    }

    public static async Task<VbaDevReferenceListStartupState> ResolveAsync(
        IReadOnlyList<string> arguments,
        ProcessInvocationRunner runProcess,
        CancellationToken cancellationToken = default,
        Action<string>? reportDiagnostic = null)
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
                cancellationToken,
                reportDiagnostic ?? Console.Error.WriteLine)
            .ConfigureAwait(false);
    }

    private static async Task<VbaDevReferenceListStartupState> ResolveValidatedExecutableAsync(
        string executablePath,
        ProcessInvocationRunner runProcess,
        CancellationToken cancellationToken,
        Action<string> reportDiagnostic)
    {
        int? firstFatalExitCode = null;
        try
        {
            var firstResult = await runProcess(
                CapabilitiesArguments,
                cancellationToken).ConfigureAwait(false);
            if (firstResult.ExitCode < 0)
            {
                firstFatalExitCode = firstResult.ExitCode;
                try
                {
                    reportDiagnostic(JsonSerializer.Serialize(new
                    {
                        eventType = "companion-process-abnormal-termination",
                        role = "vba-dev",
                        stage = "capabilities",
                        executablePath,
                        attempt = "1/2",
                        exitCode = firstResult.ExitCode,
                        exitCodeHex = $"0x{unchecked((uint)firstResult.ExitCode):X8}",
                        outcome = "retry-eligible",
                        retryPerformed = false
                    }));
                }
                catch
                {
                    // A failed diagnostic sink must not change the probe outcome.
                }

                // A returned result proves that the prior process exited and both
                // output streams were drained. An exception, including uncertain
                // cleanup, never reaches this retry path.
                await Task.Delay(FatalExitRetryDelay, cancellationToken)
                    .ConfigureAwait(false);
                var retryResult = await runProcess(
                    CapabilitiesArguments,
                    cancellationToken).ConfigureAwait(false);
                return InterpretResult(executablePath, retryResult, firstResult.ExitCode);
            }

            return InterpretResult(executablePath, firstResult);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (firstFatalExitCode is { } fatalExitCode)
            {
                var retryFailure = exception is ProcessLifecycleException
                    ? "could not prove process cleanup"
                    : $"failed with {exception.GetType().Name}";
                return Unavailable(
                    $"{DescribeFirstFatalExit(executablePath, fatalExitCode)}" +
                    $"the second attempt {retryFailure} (2 attempts).");
            }

            return Unavailable(
                $"VbaDev at '{executablePath}' could not be validated: {exception.Message}");
        }
    }

    private static VbaDevReferenceListStartupState InterpretResult(
        string executablePath,
        ProcessInvocationResult result,
        int? previousFatalExitCode = null)
    {
        if (result.ExitCode < 0)
        {
            var exitCode = FormatExitCode(result.ExitCode);
            return Unavailable(previousFatalExitCode is { } previous
                ? $"VbaDev at '{executablePath}' terminated unexpectedly during capability inspection " +
                    $"with exit codes {FormatExitCode(previous)} and {exitCode} across 2 attempts."
                : $"VbaDev at '{executablePath}' terminated unexpectedly during capability inspection " +
                    $"with exit code {exitCode}.");
        }

        if (result.ExitCode != 0)
        {
            var previousAttempt = previousFatalExitCode is { } previous
                ? $"{DescribeFirstFatalExit(executablePath, previous)}the second attempt "
                : $"VbaDev at '{executablePath}' ";
            return Unavailable(
                $"{previousAttempt}exited with code {result.ExitCode} during capability inspection.");
        }

        if (!VbaDevCapabilityAdmission.Admit(result.StandardOutput, RequiredCapabilities).IsAccepted)
        {
            var previousAttempt = previousFatalExitCode is { } previous
                ? $"{DescribeFirstFatalExit(executablePath, previous)}the second attempt "
                : $"VbaDev at '{executablePath}' ";
            return Unavailable(
                $"{previousAttempt}does not report reference list output schema {RequiredSchemaVersion}.");
        }

        var recoveryWarning = previousFatalExitCode is { } fatalExitCode
            ? $"VbaDev at '{executablePath}' capability inspection recovered on the second attempt " +
                $"after unexpected exit {FormatExitCode(fatalExitCode)}."
            : null;
        return new VbaDevReferenceListStartupState(executablePath, recoveryWarning);
    }

    private static string DescribeFirstFatalExit(string executablePath, int exitCode)
        => $"VbaDev at '{executablePath}' capability inspection first attempt terminated " +
            $"unexpectedly with exit code {FormatExitCode(exitCode)}; ";

    private static string FormatExitCode(int exitCode)
        => $"{exitCode} (0x{unchecked((uint)exitCode):X8})";

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
