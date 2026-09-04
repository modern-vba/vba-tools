using System.Text.Json;
using VbaLanguageServer.Processes;

namespace VbaLanguageServer.Lsp;

internal sealed record VbaDevReferenceListStartupState(
    string? ExecutablePath,
    string? WarningMessage)
{
    private const string RequiredSchemaVersion = "1.0";

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

        var process = new VbaDevProcessInvocation(executablePath);
        return await ResolveValidatedExecutableAsync(
                executablePath,
                process.RunAsync,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task<VbaDevReferenceListStartupState> ResolveAsync(
        IReadOnlyList<string> arguments,
        VbaDevProcessInvocationRunner runProcess,
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
        VbaDevProcessInvocationRunner runProcess,
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

    private static bool HasRequiredReferenceListCapability(JsonElement root)
        => root.ValueKind == JsonValueKind.Object
            && TryGetUniqueProperty(root, "commands", out var commands)
            && commands.ValueKind == JsonValueKind.Object
            && TryGetUniqueProperty(commands, "reference list", out var referenceList)
            && referenceList.ValueKind == JsonValueKind.Object
            && TryGetUniqueProperty(
                referenceList,
                "outputSchemaVersion",
                out var schemaVersion)
            && schemaVersion.ValueKind == JsonValueKind.String
            && schemaVersion.GetString() == RequiredSchemaVersion;

    private static bool TryGetUniqueProperty(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        value = default;
        var found = false;
        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals(propertyName, StringComparison.Ordinal))
            {
                continue;
            }

            if (found)
            {
                value = default;
                return false;
            }

            value = property.Value;
            found = true;
        }

        return found;
    }

    private static VbaDevReferenceListStartupState Unavailable(string reason)
        => new(
            null,
            $"{reason} CLI-backed reference catalog resolution is disabled; registry-only discovery remains available.");
}
