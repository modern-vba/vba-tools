using System.Diagnostics;
using System.Reflection;

namespace VbaDebugAdapter.Diagnostics;

public enum DebugEnvironmentDiagnosticStatus
{
    Pass,
    Warning,
    Fail,
    Unverified,
    Skipped
}

public sealed record DebugEnvironmentDiagnosticCheck(
    string Id,
    DebugEnvironmentDiagnosticStatus Status,
    string Message,
    long DurationMilliseconds)
{
    public string? Remediation { get; init; }

    public IReadOnlyDictionary<string, object?>? Details { get; init; }
}

public sealed record DebugEnvironmentDiagnosticReport(
    string SchemaVersion,
    string ToolVersion,
    DebugEnvironmentDiagnosticStatus Status,
    bool Complete,
    IReadOnlyList<DebugEnvironmentDiagnosticCheck> Checks);

public interface IDebugEnvironmentDoctor
{
    Task<DebugEnvironmentDiagnosticReport> RunAsync(
        CancellationToken cancellationToken);
}

internal interface IDebugEnvironmentProbeFactory
{
    IDebugEnvironmentProbe Create();
}

internal interface IDebugEnvironmentProbe
{
    Task<DebugEnvironmentProbeCheckResult> RunStageAsync(
        string checkId,
        CancellationToken cancellationToken);
}

internal sealed record DebugEnvironmentProbeCheckResult(
    DebugEnvironmentDiagnosticStatus Status,
    string Message)
{
    public string? Remediation { get; init; }

    public IReadOnlyDictionary<string, object?>? Details { get; init; }

    public static DebugEnvironmentProbeCheckResult Pass(string message)
        => new(DebugEnvironmentDiagnosticStatus.Pass, message);
}

internal sealed record DebugEnvironmentDoctorDeadlines(
    TimeSpan WorkspaceSession,
    TimeSpan ExcelStartup,
    TimeSpan ProcessOwnership,
    TimeSpan FixtureCreation,
    TimeSpan WorkbookOpen,
    TimeSpan VbideAccess,
    TimeSpan CommandContext,
    TimeSpan Breakpoint,
    TimeSpan BreakMode,
    TimeSpan Continue,
    TimeSpan Completion,
    TimeSpan BreakpointCleanup,
    TimeSpan ProcessClose,
    TimeSpan WorkspaceDeletion)
{
    public static DebugEnvironmentDoctorDeadlines Default { get; } = new(
        WorkspaceSession: TimeSpan.FromSeconds(5),
        ExcelStartup: TimeSpan.FromSeconds(30),
        ProcessOwnership: TimeSpan.FromSeconds(60),
        FixtureCreation: TimeSpan.FromSeconds(60),
        WorkbookOpen: TimeSpan.FromSeconds(60),
        VbideAccess: TimeSpan.FromSeconds(60),
        CommandContext: TimeSpan.FromSeconds(60),
        Breakpoint: TimeSpan.FromSeconds(60),
        BreakMode: TimeSpan.FromSeconds(60),
        Continue: TimeSpan.FromSeconds(60),
        Completion: TimeSpan.FromSeconds(60),
        BreakpointCleanup: TimeSpan.FromSeconds(60),
        ProcessClose: TimeSpan.FromSeconds(5),
        WorkspaceDeletion: TimeSpan.FromSeconds(5));

    public TimeSpan For(string checkId)
        => checkId switch
        {
            "workspace.session" => WorkspaceSession,
            "excel.startup" => ExcelStartup,
            "excel.processOwnership" => ProcessOwnership,
            "workbook.fixtureCreation" => FixtureCreation,
            "workbook.open" => WorkbookOpen,
            "vbide.access" => VbideAccess,
            "vbe.commandContext" => CommandContext,
            "vbe.breakpoint" => Breakpoint,
            "vbe.breakMode" => BreakMode,
            "vbe.continue" => Continue,
            "vbe.procedureCompletion" => Completion,
            "vbe.breakpointCleanup" => BreakpointCleanup,
            "excel.processClose" => ProcessClose,
            "workspace.deletion" => WorkspaceDeletion,
            _ => throw new ArgumentOutOfRangeException(nameof(checkId), checkId, null)
        };
}

internal sealed class DebugEnvironmentDoctor : IDebugEnvironmentDoctor
{
    private const string SchemaVersion = "1.0";

    internal static IReadOnlyList<string> CheckIds { get; } =
    [
        "platform.windows",
        "workspace.session",
        "excel.startup",
        "excel.processOwnership",
        "workbook.fixtureCreation",
        "workbook.open",
        "vbide.access",
        "vbe.commandContext",
        "vbe.breakpoint",
        "vbe.breakMode",
        "vbe.continue",
        "vbe.procedureCompletion",
        "vbe.breakpointCleanup",
        "excel.processClose",
        "workspace.deletion"
    ];

    private readonly string toolVersion;
    private readonly Func<bool> isWindows;
    private readonly IDebugEnvironmentProbeFactory probeFactory;
    private readonly DebugEnvironmentDoctorDeadlines deadlines;
    private readonly IDebugEnvironmentDoctorStageRunner stageRunner;

    public DebugEnvironmentDoctor()
        : this(
            GetInformationalVersion(),
            OperatingSystem.IsWindows,
            UnconfiguredDebugEnvironmentProbeFactory.Instance,
            DebugEnvironmentDoctorDeadlines.Default,
            new DebugEnvironmentDoctorStageRunner())
    {
    }

    internal DebugEnvironmentDoctor(
        string toolVersion,
        Func<bool> isWindows,
        IDebugEnvironmentProbeFactory probeFactory,
        DebugEnvironmentDoctorDeadlines deadlines)
        : this(
            toolVersion,
            isWindows,
            probeFactory,
            deadlines,
            new DebugEnvironmentDoctorStageRunner())
    {
    }

    internal DebugEnvironmentDoctor(
        string toolVersion,
        Func<bool> isWindows,
        IDebugEnvironmentProbeFactory probeFactory,
        DebugEnvironmentDoctorDeadlines deadlines,
        IDebugEnvironmentDoctorStageRunner stageRunner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolVersion);
        ArgumentNullException.ThrowIfNull(isWindows);
        ArgumentNullException.ThrowIfNull(probeFactory);
        ArgumentNullException.ThrowIfNull(deadlines);
        ArgumentNullException.ThrowIfNull(stageRunner);
        this.toolVersion = toolVersion;
        this.isWindows = isWindows;
        this.probeFactory = probeFactory;
        this.deadlines = deadlines;
        this.stageRunner = stageRunner;
    }

    public async Task<DebugEnvironmentDiagnosticReport> RunAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var checks = new List<DebugEnvironmentDiagnosticCheck>(CheckIds.Count);
        var platformStopwatch = Stopwatch.StartNew();
        var supportedPlatform = isWindows();
        platformStopwatch.Stop();
        checks.Add(new DebugEnvironmentDiagnosticCheck(
            "platform.windows",
            supportedPlatform
                ? DebugEnvironmentDiagnosticStatus.Pass
                : DebugEnvironmentDiagnosticStatus.Fail,
            supportedPlatform
                ? "Windows supports native Excel/VBE automation."
                : "Native Excel/VBE debugging requires Windows.",
            platformStopwatch.ElapsedMilliseconds));

        if (!supportedPlatform)
        {
            checks.AddRange(CheckIds.Skip(1).Select(id => Skipped(id)));
            return CreateReport(checks, complete: true);
        }

        var probe = probeFactory.Create();
        var dependantChecksBlocked = false;
        var complete = true;
        foreach (var checkId in CheckIds.Skip(1))
        {
            var cleanupCheck = IsCleanupCheck(checkId);
            if (dependantChecksBlocked && !cleanupCheck)
            {
                checks.Add(Skipped(checkId));
                continue;
            }

            var execution = await stageRunner.RunAsync(
                checkId,
                deadlines.For(checkId),
                stageCancellationToken => probe.RunStageAsync(
                    checkId,
                    stageCancellationToken),
                cleanupCheck ? CancellationToken.None : cancellationToken)
                .ConfigureAwait(false);
            checks.Add(execution.Check);
            if (execution.Termination is
                DebugEnvironmentDoctorStageTermination.CallerCancellation or
                DebugEnvironmentDoctorStageTermination.InfrastructureLoss)
            {
                complete = false;
            }
            if (!cleanupCheck && execution.Check.Status is not (
                    DebugEnvironmentDiagnosticStatus.Pass or
                    DebugEnvironmentDiagnosticStatus.Warning))
            {
                dependantChecksBlocked = true;
            }
        }

        return CreateReport(checks, complete);
    }

    private DebugEnvironmentDiagnosticReport CreateReport(
        IReadOnlyList<DebugEnvironmentDiagnosticCheck> checks,
        bool complete)
        => new(
            SchemaVersion,
            toolVersion,
            AggregateStatus(checks),
            complete,
            checks);

    private static DebugEnvironmentDiagnosticStatus AggregateStatus(
        IReadOnlyList<DebugEnvironmentDiagnosticCheck> checks)
    {
        if (checks.Any(check => check.Status == DebugEnvironmentDiagnosticStatus.Fail))
        {
            return DebugEnvironmentDiagnosticStatus.Fail;
        }

        if (checks.Any(check => check.Status == DebugEnvironmentDiagnosticStatus.Unverified))
        {
            return DebugEnvironmentDiagnosticStatus.Unverified;
        }

        return checks.Any(check => check.Status == DebugEnvironmentDiagnosticStatus.Warning)
            ? DebugEnvironmentDiagnosticStatus.Warning
            : DebugEnvironmentDiagnosticStatus.Pass;
    }

    private static DebugEnvironmentDiagnosticCheck Skipped(string checkId)
        => new(
            checkId,
            DebugEnvironmentDiagnosticStatus.Skipped,
            "The check was skipped because a required readiness stage did not pass.",
            DurationMilliseconds: 0);

    private static bool IsCleanupCheck(string checkId)
        => checkId is
            "vbe.breakpointCleanup" or
            "excel.processClose" or
            "workspace.deletion";

    private static string GetInformationalVersion()
        => typeof(DebugEnvironmentDoctor).Assembly
               .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
               .InformationalVersion
           ?? "0.0.0";

    internal static DebugEnvironmentDiagnosticReport InfrastructureFailure(
        string toolVersion,
        Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolVersion);
        ArgumentNullException.ThrowIfNull(exception);
        return new DebugEnvironmentDiagnosticReport(
            SchemaVersion,
            toolVersion,
            DebugEnvironmentDiagnosticStatus.Unverified,
            Complete: false,
            CheckIds.Select((id, index) => new DebugEnvironmentDiagnosticCheck(
                id,
                index == 0
                    ? DebugEnvironmentDiagnosticStatus.Unverified
                    : DebugEnvironmentDiagnosticStatus.Skipped,
                index == 0
                    ? $"The VBE Doctor infrastructure did not complete: {exception.Message}"
                    : "The check was skipped because Doctor infrastructure did not complete.",
                DurationMilliseconds: 0)).ToArray());
    }

    private sealed class UnconfiguredDebugEnvironmentProbeFactory
        : IDebugEnvironmentProbeFactory
    {
        public static UnconfiguredDebugEnvironmentProbeFactory Instance { get; } = new();

        public IDebugEnvironmentProbe Create()
            => throw new InvalidOperationException(
                "The production VBE readiness probe is not configured.");
    }
}
