using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VbaDebugAdapter.Diagnostics;
using VbaDebugAdapter.Infrastructure;

namespace VbaDebugAdapter.Cli;

public sealed class VbaDebugAdapterCommandLine
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly IVbaDebugAdapterStdioRunner stdioRunner;
    private readonly IVbaDevCapabilitiesProbe vbaDevCapabilitiesProbe;
    private readonly IVbaDebugSessionWorkspaceManager sessionWorkspaceManager;
    private readonly IDebugEnvironmentDoctor debugEnvironmentDoctor;

    private VbaDebugAdapterCommandLine(
        IVbaDebugAdapterStdioRunner stdioRunner,
        IVbaDevCapabilitiesProbe vbaDevCapabilitiesProbe,
        IVbaDebugSessionWorkspaceManager sessionWorkspaceManager,
        IDebugEnvironmentDoctor debugEnvironmentDoctor)
    {
        this.stdioRunner = stdioRunner;
        this.vbaDevCapabilitiesProbe = vbaDevCapabilitiesProbe;
        this.sessionWorkspaceManager = sessionWorkspaceManager;
        this.debugEnvironmentDoctor = debugEnvironmentDoctor;
    }

    public static VbaDebugAdapterCommandLine Create()
        => CreateForWorkspaceRoot(
            Path.Combine(Path.GetTempPath(), "vba-debug-adapter"));

    internal static VbaDebugAdapterCommandLine CreateForWorkspaceRoot(
        string workspaceRoot)
    {
        var workspaceRootBinding = new VbaDebugWorkspaceRootBinding(workspaceRoot);
        var sessionWorkspaceManager = new VbaDebugSessionWorkspaceManager(
            workspaceRootBinding,
            cleanupOperations: null);
        return new VbaDebugAdapterCommandLine(
            new StandaloneVbaDebugAdapterStdioRunner(workspaceRootBinding),
            new ProcessVbaDevCapabilitiesProbe(),
            sessionWorkspaceManager,
            new DebugEnvironmentDoctor(
                ToolVersion,
                OperatingSystem.IsWindows,
                new VbeDebugEnvironmentProbeFactory(
                    sessionWorkspaceManager,
                    new VbeDebugAutomation()),
                DebugEnvironmentDoctorDeadlines.Default));
    }

    public static VbaDebugAdapterCommandLine Create(IVbaDebugAdapterStdioRunner stdioRunner)
        => new(
            stdioRunner ?? throw new ArgumentNullException(nameof(stdioRunner)),
            new ProcessVbaDevCapabilitiesProbe(),
            PassthroughVbaDebugSessionWorkspaceManager.Instance,
            new DebugEnvironmentDoctor());

    public static VbaDebugAdapterCommandLine Create(
        IVbaDebugAdapterStdioRunner stdioRunner,
        IVbaDevCapabilitiesProbe vbaDevCapabilitiesProbe)
        => new(
            stdioRunner ?? throw new ArgumentNullException(nameof(stdioRunner)),
            vbaDevCapabilitiesProbe ?? throw new ArgumentNullException(nameof(vbaDevCapabilitiesProbe)),
            PassthroughVbaDebugSessionWorkspaceManager.Instance,
            new DebugEnvironmentDoctor());

    public static VbaDebugAdapterCommandLine Create(
        IVbaDebugAdapterStdioRunner stdioRunner,
        IVbaDevCapabilitiesProbe vbaDevCapabilitiesProbe,
        IVbaDebugSessionWorkspaceManager sessionWorkspaceManager)
        => new(
            stdioRunner ?? throw new ArgumentNullException(nameof(stdioRunner)),
            vbaDevCapabilitiesProbe ?? throw new ArgumentNullException(nameof(vbaDevCapabilitiesProbe)),
            sessionWorkspaceManager ?? throw new ArgumentNullException(nameof(sessionWorkspaceManager)),
            new DebugEnvironmentDoctor());

    public static VbaDebugAdapterCommandLine Create(
        IVbaDebugAdapterStdioRunner stdioRunner,
        IVbaDevCapabilitiesProbe vbaDevCapabilitiesProbe,
        IVbaDebugSessionWorkspaceManager sessionWorkspaceManager,
        IDebugEnvironmentDoctor debugEnvironmentDoctor)
        => new(
            stdioRunner ?? throw new ArgumentNullException(nameof(stdioRunner)),
            vbaDevCapabilitiesProbe ?? throw new ArgumentNullException(nameof(vbaDevCapabilitiesProbe)),
            sessionWorkspaceManager ?? throw new ArgumentNullException(nameof(sessionWorkspaceManager)),
            debugEnvironmentDoctor ?? throw new ArgumentNullException(nameof(debugEnvironmentDoctor)));

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
                    ["doctor.stdinCancellation"] = "1.0"
                },
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["build.sourceSnapshot"] = "1.0"
                });
            await WriteLineAsync(
                standardOutput,
                JsonSerializer.Serialize(capabilities, JsonOptions)).ConfigureAwait(false);
            return 0;
        }

        var doctorUsesStdinCancellation = args.SequenceEqual(
            [
                "doctor",
                "--format",
                "json",
                "--cancellation-transport",
                "stdin-v1"
            ],
            StringComparer.Ordinal);
        if (
            doctorUsesStdinCancellation ||
            args.SequenceEqual(["doctor", "--format", "json"], StringComparer.Ordinal))
        {
            var doctorCancellationToken = cancellationToken;
            CancellationTokenSource? doctorCancellation = null;
            CancellationTokenSource? monitorCancellation = null;
            Task monitorTask = Task.CompletedTask;
            if (doctorUsesStdinCancellation)
            {
                doctorCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                doctorCancellationToken = doctorCancellation.Token;
            }
            DebugEnvironmentDiagnosticReport report;
            try
            {
                var doctorTask = debugEnvironmentDoctor.RunAsync(doctorCancellationToken);
                if (doctorCancellation is not null)
                {
                    monitorCancellation = new CancellationTokenSource();
                    monitorTask = ObserveDoctorCancellationAsync(
                        standardInput,
                        doctorCancellation,
                        monitorCancellation.Token);
                }
                report = await doctorTask.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                report = DebugEnvironmentDoctor.InfrastructureFailure(
                    ToolVersion,
                    exception);
                await WriteLineAsync(
                    standardError,
                    $"VBE Doctor infrastructure failure: {exception.Message}")
                    .ConfigureAwait(false);
            }
            finally
            {
                if (monitorCancellation is not null)
                {
                    monitorCancellation.Cancel();
                    try
                    {
                        await monitorTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (
                        monitorCancellation.IsCancellationRequested)
                    {
                    }
                    monitorCancellation.Dispose();
                }
                doctorCancellation?.Dispose();
            }
            await WriteLineAsync(
                standardOutput,
                JsonSerializer.Serialize(report, JsonOptions)).ConfigureAwait(false);
            return report.Complete && report.Status is
                DebugEnvironmentDiagnosticStatus.Pass or
                DebugEnvironmentDiagnosticStatus.Warning
                ? 0
                : 1;
        }

        if (
            args.Count == 3 &&
            string.Equals(args[0], "cleanup", StringComparison.Ordinal) &&
            string.Equals(args[1], "--session", StringComparison.Ordinal) &&
            IsCanonicalSessionId(args[2]))
        {
            VbaDebugSessionCleanupResult cleanup;
            try
            {
                cleanup = await sessionWorkspaceManager
                    .CleanupAsync(args[2], cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await WriteLineAsync(
                    standardError,
                    $"The VBA debug session workspace cleanup failed: {exception.Message}")
                    .ConfigureAwait(false);
                return 1;
            }

            if (cleanup.Succeeded)
            {
                return 0;
            }

            var retainedDetail = cleanup.RetainedPath is null
                ? string.Empty
                : $" Retained path: {cleanup.RetainedPath}";
            await WriteLineAsync(
                standardError,
                (cleanup.Message ?? "The VBA debug session workspace was retained.") + retainedDetail)
                .ConfigureAwait(false);
            return 1;
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

            try
            {
                var retainedWorkspaces = await sessionWorkspaceManager
                    .ReapStaleAsync(args[4], cancellationToken)
                    .ConfigureAwait(false);
                foreach (var retained in retainedWorkspaces)
                {
                    var retainedDetail = retained.RetainedPath is null
                        ? string.Empty
                        : $" Retained path: {retained.RetainedPath}";
                    await WriteLineAsync(
                        standardError,
                        (retained.Message ?? "A VBA debug session workspace was retained.") +
                        retainedDetail).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                await WriteLineAsync(
                    standardError,
                    $"VBA debug stale-workspace reaping was incomplete: {exception.Message}")
                    .ConfigureAwait(false);
            }

            IVbaDebugSessionWorkspaceLease lease;
            try
            {
                lease = await sessionWorkspaceManager
                    .ClaimAsync(args[4], cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await WriteLineAsync(
                    standardError,
                    $"The VBA debug adapter session workspace could not be claimed: {exception.Message}")
                    .ConfigureAwait(false);
                return 1;
            }

            int runnerExitCode;
            try
            {
                runnerExitCode = await stdioRunner.RunAsync(
                    vbaDevPath,
                    args[4],
                    standardInput,
                    standardOutput,
                    standardError,
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _ = await TryDisposeLeaseAsync(lease, standardError).ConfigureAwait(false);
                throw;
            }

            return await TryDisposeLeaseAsync(lease, standardError).ConfigureAwait(false)
                ? runnerExitCode
                : 1;
        }

        await WriteLineAsync(
            standardError,
            "Usage: vba-debug-adapter capabilities --format json | " +
            "vba-debug-adapter doctor --format json | " +
            "vba-debug-adapter cleanup --session <lowercase-hex-32> | " +
            "vba-debug-adapter --stdio --vba-dev <absolute-path> --session <lowercase-hex-32>").ConfigureAwait(false);
        return 1;
    }

    private static Task WriteLineAsync(Stream stream, string value)
        => stream.WriteAsync(
            Encoding.UTF8.GetBytes(value + Environment.NewLine),
            CancellationToken.None).AsTask();

    private static async Task ObserveDoctorCancellationAsync(
        Stream standardInput,
        CancellationTokenSource doctorCancellation,
        CancellationToken monitorCancellation)
    {
        ReadOnlyMemory<byte> expectedPayload = "cancel"u8.ToArray();
        var buffer = new byte[64];
        var matchedBytes = 0;
        var discardingFrame = false;
        var monitorStopped = Task.Delay(Timeout.InfiniteTimeSpan, monitorCancellation);
        try
        {
            while (true)
            {
                var readTask = standardInput.ReadAsync(
                    buffer,
                    monitorCancellation).AsTask();
                var completedTask = await Task.WhenAny(
                    readTask,
                    monitorStopped).ConfigureAwait(false);
                if (completedTask != readTask)
                {
                    _ = readTask.ContinueWith(
                        static completedRead => _ = completedRead.Exception,
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted |
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    return;
                }
                var read = await readTask.ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }
                foreach (var value in buffer.AsSpan(0, read))
                {
                    if (value == (byte)'\n')
                    {
                        if (!discardingFrame && matchedBytes == expectedPayload.Length)
                        {
                            doctorCancellation.Cancel();
                        }
                        matchedBytes = 0;
                        discardingFrame = false;
                        continue;
                    }

                    if (
                        discardingFrame ||
                        matchedBytes >= expectedPayload.Length ||
                        value != expectedPayload.Span[matchedBytes])
                    {
                        discardingFrame = true;
                        continue;
                    }
                    matchedBytes++;
                }
            }
        }
        catch (OperationCanceledException) when (monitorCancellation.IsCancellationRequested)
        {
        }
        catch
        {
            // A missing or failed stdin transport does not alter Doctor outcome authority.
        }
    }

    private static async Task<bool> TryDisposeLeaseAsync(
        IVbaDebugSessionWorkspaceLease lease,
        Stream standardError)
    {
        try
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            return true;
        }
        catch
        {
            await WriteLineAsync(
                standardError,
                "The VBA debug session workspace cleanup failed. " +
                $"Retained path: {Path.GetFullPath(lease.SessionWorkspacePath)}")
                .ConfigureAwait(false);
            return false;
        }
    }

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
        IReadOnlyDictionary<string, string> FeatureVersions,
        IReadOnlyDictionary<string, string> RequiredVbaDevFeatureVersions);

    private sealed class PassthroughVbaDebugSessionWorkspaceManager
        : IVbaDebugSessionWorkspaceManager
    {
        public static PassthroughVbaDebugSessionWorkspaceManager Instance { get; } = new();

        public ValueTask<IVbaDebugSessionWorkspaceLease> ClaimAsync(
            string sessionId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IVbaDebugSessionWorkspaceLease>(
                PassthroughVbaDebugSessionWorkspaceLease.Instance);
        }

        public ValueTask<VbaDebugSessionCleanupResult> CleanupAsync(
            string sessionId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new VbaDebugSessionCleanupResult(
                Succeeded: true,
                RetainedPath: null,
                Message: null));
        }

        public ValueTask<IReadOnlyList<VbaDebugSessionCleanupResult>> ReapStaleAsync(
            string excludedSessionId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<VbaDebugSessionCleanupResult>>([]);
        }
    }

    private sealed class PassthroughVbaDebugSessionWorkspaceLease
        : IVbaDebugSessionWorkspaceLease
    {
        public static PassthroughVbaDebugSessionWorkspaceLease Instance { get; } = new();

        public string SessionWorkspacePath => string.Empty;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

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
