using System.Reflection;
using System.Text;
using System.Text.Json;

namespace VbaDebugAdapter.Cli;

public sealed class VbaDebugAdapterCommandLine
{
    private static readonly JsonSerializerOptions CapabilitiesJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IVbaDebugAdapterStdioRunner stdioRunner;
    private readonly IVbaDevCapabilitiesProbe vbaDevCapabilitiesProbe;

    private VbaDebugAdapterCommandLine(
        IVbaDebugAdapterStdioRunner stdioRunner,
        IVbaDevCapabilitiesProbe vbaDevCapabilitiesProbe)
    {
        this.stdioRunner = stdioRunner;
        this.vbaDevCapabilitiesProbe = vbaDevCapabilitiesProbe;
    }

    public static VbaDebugAdapterCommandLine Create()
        => new(new StandaloneVbaDebugAdapterStdioRunner(), new ProcessVbaDevCapabilitiesProbe());

    public static VbaDebugAdapterCommandLine Create(IVbaDebugAdapterStdioRunner stdioRunner)
        => new(
            stdioRunner ?? throw new ArgumentNullException(nameof(stdioRunner)),
            new ProcessVbaDevCapabilitiesProbe());

    public static VbaDebugAdapterCommandLine Create(
        IVbaDebugAdapterStdioRunner stdioRunner,
        IVbaDevCapabilitiesProbe vbaDevCapabilitiesProbe)
        => new(
            stdioRunner ?? throw new ArgumentNullException(nameof(stdioRunner)),
            vbaDevCapabilitiesProbe ?? throw new ArgumentNullException(nameof(vbaDevCapabilitiesProbe)));

    public Task<int> InvokeAsync(
        IReadOnlyList<string> args,
        Stream standardOutput,
        Stream standardError,
        CancellationToken cancellationToken)
        => InvokeAsync(
            args,
            Stream.Null,
            standardOutput,
            standardError,
            cancellationToken);

    public async Task<int> InvokeAsync(
        IReadOnlyList<string> args,
        Stream standardInput,
        Stream standardOutput,
        Stream standardError,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(standardInput);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);
        cancellationToken.ThrowIfCancellationRequested();

        if (args.SequenceEqual(["capabilities", "--format", "json"], StringComparer.Ordinal))
        {
            var capabilities = new AdapterCapabilities(
                ToolVersion,
                "1.0",
                "1.1",
                ["stdio"],
                "lowercase-hex-32",
                ["cleanup", "doctor"],
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["doctor"] = "1.0"
                },
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["build.sourceSnapshot"] = "1.0"
                });
            await WriteLineAsync(
                standardOutput,
                JsonSerializer.Serialize(capabilities, CapabilitiesJsonOptions)).ConfigureAwait(false);
            return 0;
        }

        if (
            args.Count == 5 &&
            string.Equals(args[0], "--stdio", StringComparison.Ordinal) &&
            string.Equals(args[1], "--vba-dev", StringComparison.Ordinal) &&
            Path.IsPathFullyQualified(args[2]) &&
            string.Equals(args[3], "--session", StringComparison.Ordinal) &&
            IsCanonicalSessionId(args[4]))
        {
            var vbaDevPath = Path.GetFullPath(args[2]);
            var capabilities = await vbaDevCapabilitiesProbe
                .ProbeAsync(vbaDevPath, cancellationToken)
                .ConfigureAwait(false);
            if (!AdvertisesRequiredSnapshotBuildFeature(capabilities))
            {
                await WriteLineAsync(
                    standardError,
                    "The supplied vba-dev executable is incompatible; " +
                    "it must advertise build.sourceSnapshot 1.0.").ConfigureAwait(false);
                return 1;
            }

            return await stdioRunner.RunAsync(
                vbaDevPath,
                args[4],
                standardInput,
                standardOutput,
                standardError,
                cancellationToken).ConfigureAwait(false);
        }

        await WriteLineAsync(
            standardError,
            "Usage: vba-debug-adapter capabilities --format json | " +
            "vba-debug-adapter --stdio --vba-dev <absolute-path> --session <lowercase-hex-32>").ConfigureAwait(false);
        return 1;
    }

    private static Task WriteLineAsync(Stream stream, string value)
        => stream.WriteAsync(
            Encoding.UTF8.GetBytes(value + Environment.NewLine),
            CancellationToken.None).AsTask();

    private static bool AdvertisesRequiredSnapshotBuildFeature(
        VbaDevCapabilitiesProbeResult capabilities)
    {
        if (capabilities.ExitCode != 0)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(capabilities.StandardOutput);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("featureVersions", out var featureVersions) &&
                   featureVersions.ValueKind == JsonValueKind.Object &&
                   featureVersions.TryGetProperty("build.sourceSnapshot", out var version) &&
                   version.ValueKind == JsonValueKind.String &&
                   string.Equals(version.GetString(), "1.0", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsCanonicalSessionId(string value)
        => value.Length == 32 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string ToolVersion
        => typeof(VbaDebugAdapterCommandLine).Assembly
               .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
               .InformationalVersion
           ?? throw new InvalidOperationException(
               "vba-debug-adapter informational version metadata is missing.");

    private sealed record AdapterCapabilities(
        string ToolVersion,
        string ContractVersion,
        string ProtocolVersion,
        IReadOnlyList<string> Transports,
        string SessionIdFormat,
        IReadOnlyList<string> Commands,
        IReadOnlyDictionary<string, string> CommandSchemaVersions,
        IReadOnlyDictionary<string, string> RequiredVbaDevFeatureVersions);

}

public interface IVbaDebugAdapterStdioRunner
{
    Task<int> RunAsync(
        string vbaDevPath,
        string sessionId,
        Stream standardInput,
        Stream standardOutput,
        Stream standardError,
        CancellationToken cancellationToken);
}
