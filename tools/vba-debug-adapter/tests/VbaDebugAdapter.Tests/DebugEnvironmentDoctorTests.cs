using VbaDebugAdapter.Diagnostics;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class DebugEnvironmentDoctorTests
{
    [Fact]
    public async Task AllReadinessStagesPassInStableOrder()
    {
        var probe = new RecordingDebugEnvironmentProbe();
        var doctor = new DebugEnvironmentDoctor(
            "9.8.7+doctor-test",
            () => true,
            new RecordingDebugEnvironmentProbeFactory(probe),
            DebugEnvironmentDoctorDeadlines.Default);

        var report = await doctor.RunAsync(CancellationToken.None);

        Assert.Equal("1.0", report.SchemaVersion);
        Assert.Equal("9.8.7+doctor-test", report.ToolVersion);
        Assert.Equal(DebugEnvironmentDiagnosticStatus.Pass, report.Status);
        Assert.True(report.Complete);
        Assert.Equal(
            DebugEnvironmentDoctor.CheckIds,
            report.Checks.Select(check => check.Id));
        Assert.Equal(
            DebugEnvironmentDoctor.CheckIds.Skip(1),
            probe.ObservedCheckIds);
        Assert.All(report.Checks, check =>
        {
            Assert.Equal(DebugEnvironmentDiagnosticStatus.Pass, check.Status);
            Assert.False(string.IsNullOrWhiteSpace(check.Message));
            Assert.True(check.DurationMilliseconds >= 0);
        });
    }

    [Fact]
    public async Task ReadinessStagesUseTheAcceptedIndependentDeadlines()
    {
        var stageRunner = new RecordingDeadlineStageRunner();
        var doctor = new DebugEnvironmentDoctor(
            "9.8.7+doctor-test",
            () => true,
            new RecordingDebugEnvironmentProbeFactory(
                new RecordingDebugEnvironmentProbe()),
            DebugEnvironmentDoctorDeadlines.Default,
            stageRunner);

        await doctor.RunAsync(CancellationToken.None);

        Assert.Equal(
            new (string CheckId, TimeSpan Timeout)[]
            {
                ("workspace.session", TimeSpan.FromSeconds(5)),
                ("excel.startup", TimeSpan.FromSeconds(30)),
                ("excel.processOwnership", TimeSpan.FromSeconds(60)),
                ("workbook.fixtureCreation", TimeSpan.FromSeconds(60)),
                ("workbook.open", TimeSpan.FromSeconds(60)),
                ("vbide.access", TimeSpan.FromSeconds(60)),
                ("vbe.commandContext", TimeSpan.FromSeconds(60)),
                ("vbe.breakpoint", TimeSpan.FromSeconds(60)),
                ("vbe.breakMode", TimeSpan.FromSeconds(60)),
                ("vbe.continue", TimeSpan.FromSeconds(60)),
                ("vbe.procedureCompletion", TimeSpan.FromSeconds(60)),
                ("vbe.breakpointCleanup", TimeSpan.FromSeconds(60)),
                ("excel.processClose", TimeSpan.FromSeconds(5)),
                ("workspace.deletion", TimeSpan.FromSeconds(5))
            },
            stageRunner.ObservedDeadlines);
    }

    [Fact]
    public async Task ConclusiveFailureSkipsDependantsButStillRunsTerminalCleanup()
    {
        var probe = new RecordingDebugEnvironmentProbe(new Dictionary<
            string,
            DebugEnvironmentProbeCheckResult>(StringComparer.Ordinal)
        {
            ["excel.processOwnership"] = new(
                DebugEnvironmentDiagnosticStatus.Fail,
                "Strong process ownership was not established.")
        });
        var doctor = new DebugEnvironmentDoctor(
            "9.8.7+doctor-test",
            () => true,
            new RecordingDebugEnvironmentProbeFactory(probe),
            DebugEnvironmentDoctorDeadlines.Default);

        var report = await doctor.RunAsync(CancellationToken.None);

        Assert.True(report.Complete);
        Assert.Equal(DebugEnvironmentDiagnosticStatus.Fail, report.Status);
        Assert.Equal(
            [
                "workspace.session",
                "excel.startup",
                "excel.processOwnership",
                "vbe.breakpointCleanup",
                "excel.processClose",
                "workspace.deletion"
            ],
            probe.ObservedCheckIds);
        Assert.All(
            report.Checks.Where(check => check.Id is
                "workbook.fixtureCreation" or
                "workbook.open" or
                "vbide.access" or
                "vbe.commandContext" or
                "vbe.breakpoint" or
                "vbe.breakMode" or
                "vbe.continue" or
                "vbe.procedureCompletion"),
            check => Assert.Equal(
                DebugEnvironmentDiagnosticStatus.Skipped,
                check.Status));
        Assert.All(
            report.Checks.Where(check => check.Id is
                "vbe.breakpointCleanup" or
                "excel.processClose" or
                "workspace.deletion"),
            check => Assert.Equal(DebugEnvironmentDiagnosticStatus.Pass, check.Status));
    }

    [Fact]
    public async Task ClassifiedStageTimeoutIsCompleteAfterTerminalCleanup()
    {
        var probe = new RecordingDebugEnvironmentProbe();
        var stageRunner = new TimeoutAtCheckStageRunner("workbook.open");
        var doctor = new DebugEnvironmentDoctor(
            "9.8.7+doctor-test",
            () => true,
            new RecordingDebugEnvironmentProbeFactory(probe),
            DebugEnvironmentDoctorDeadlines.Default,
            stageRunner);

        var report = await doctor.RunAsync(CancellationToken.None);

        Assert.True(report.Complete);
        Assert.Equal(DebugEnvironmentDiagnosticStatus.Unverified, report.Status);
        Assert.Equal(
            DebugEnvironmentDiagnosticStatus.Unverified,
            report.Checks.Single(check => check.Id == "workbook.open").Status);
        Assert.All(
            report.Checks.Where(check => check.Id is
                "vbide.access" or
                "vbe.commandContext" or
                "vbe.breakpoint" or
                "vbe.breakMode" or
                "vbe.continue" or
                "vbe.procedureCompletion"),
            check => Assert.Equal(
                DebugEnvironmentDiagnosticStatus.Skipped,
                check.Status));
        Assert.Equal(
            [
                "workspace.session",
                "excel.startup",
                "excel.processOwnership",
                "workbook.fixtureCreation",
                "workbook.open",
                "vbe.breakpointCleanup",
                "excel.processClose",
                "workspace.deletion"
            ],
            stageRunner.ObservedCheckIds);
    }

    [Fact]
    public async Task ActiveCallerCancellationIsIncompleteButCleanupUsesFreshTokens()
    {
        using var cancellation = new CancellationTokenSource();
        var probe = new RecordingDebugEnvironmentProbe();
        var stageRunner = new CancellationAtCheckStageRunner(
            "vbe.breakMode",
            cancellation);
        var doctor = new DebugEnvironmentDoctor(
            "9.8.7+doctor-test",
            () => true,
            new RecordingDebugEnvironmentProbeFactory(probe),
            DebugEnvironmentDoctorDeadlines.Default,
            stageRunner);

        var report = await doctor.RunAsync(cancellation.Token);

        Assert.False(report.Complete);
        Assert.Equal(DebugEnvironmentDiagnosticStatus.Unverified, report.Status);
        Assert.Equal(
            DebugEnvironmentDiagnosticStatus.Unverified,
            report.Checks.Single(check => check.Id == "vbe.breakMode").Status);
        Assert.Equal(
            [
                "vbe.breakpointCleanup",
                "excel.processClose",
                "workspace.deletion"
            ],
            stageRunner.CleanupCheckIds);
    }

    private sealed class RecordingDebugEnvironmentProbeFactory(
        IDebugEnvironmentProbe probe) : IDebugEnvironmentProbeFactory
    {
        public IDebugEnvironmentProbe Create() => probe;
    }

    private sealed class RecordingDebugEnvironmentProbe(
        IReadOnlyDictionary<string, DebugEnvironmentProbeCheckResult>? results = null)
        : IDebugEnvironmentProbe
    {
        public List<string> ObservedCheckIds { get; } = [];

        public Task<DebugEnvironmentProbeCheckResult> RunStageAsync(
            string checkId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObservedCheckIds.Add(checkId);
            return Task.FromResult(
                results?.GetValueOrDefault(checkId)
                ?? DebugEnvironmentProbeCheckResult.Pass($"{checkId} passed."));
        }
    }

    private sealed class TimeoutAtCheckStageRunner(string timeoutCheckId)
        : IDebugEnvironmentDoctorStageRunner
    {
        public List<string> ObservedCheckIds { get; } = [];

        public async Task<DebugEnvironmentDoctorStageExecution> RunAsync(
            string checkId,
            TimeSpan timeout,
            Func<CancellationToken, Task<DebugEnvironmentProbeCheckResult>> operation,
            CancellationToken callerCancellationToken)
        {
            ObservedCheckIds.Add(checkId);
            if (string.Equals(checkId, timeoutCheckId, StringComparison.Ordinal))
            {
                return new DebugEnvironmentDoctorStageExecution(
                    new DebugEnvironmentDiagnosticCheck(
                        checkId,
                        DebugEnvironmentDiagnosticStatus.Unverified,
                        $"The check did not complete within {timeout.TotalSeconds:0.###} seconds.",
                        DurationMilliseconds: 1),
                    DebugEnvironmentDoctorStageTermination.Timeout);
            }

            var result = await operation(callerCancellationToken);
            return new DebugEnvironmentDoctorStageExecution(
                new DebugEnvironmentDiagnosticCheck(
                    checkId,
                    result.Status,
                    result.Message,
                    DurationMilliseconds: 1),
                DebugEnvironmentDoctorStageTermination.Completed);
        }
    }

    private sealed class RecordingDeadlineStageRunner
        : IDebugEnvironmentDoctorStageRunner
    {
        public List<(string CheckId, TimeSpan Timeout)> ObservedDeadlines { get; } = [];

        public async Task<DebugEnvironmentDoctorStageExecution> RunAsync(
            string checkId,
            TimeSpan timeout,
            Func<CancellationToken, Task<DebugEnvironmentProbeCheckResult>> operation,
            CancellationToken callerCancellationToken)
        {
            ObservedDeadlines.Add((checkId, timeout));
            var result = await operation(callerCancellationToken);
            return new DebugEnvironmentDoctorStageExecution(
                new DebugEnvironmentDiagnosticCheck(
                    checkId,
                    result.Status,
                    result.Message,
                    DurationMilliseconds: 0),
                DebugEnvironmentDoctorStageTermination.Completed);
        }
    }

    private sealed class CancellationAtCheckStageRunner(
        string cancellationCheckId,
        CancellationTokenSource cancellation)
        : IDebugEnvironmentDoctorStageRunner
    {
        public List<string> CleanupCheckIds { get; } = [];

        public async Task<DebugEnvironmentDoctorStageExecution> RunAsync(
            string checkId,
            TimeSpan timeout,
            Func<CancellationToken, Task<DebugEnvironmentProbeCheckResult>> operation,
            CancellationToken callerCancellationToken)
        {
            if (string.Equals(checkId, cancellationCheckId, StringComparison.Ordinal))
            {
                cancellation.Cancel();
                Assert.True(callerCancellationToken.IsCancellationRequested);
                return new DebugEnvironmentDoctorStageExecution(
                    new DebugEnvironmentDiagnosticCheck(
                        checkId,
                        DebugEnvironmentDiagnosticStatus.Unverified,
                        "The check was canceled before terminal classification.",
                        DurationMilliseconds: 1),
                    DebugEnvironmentDoctorStageTermination.CallerCancellation);
            }

            if (checkId is
                "vbe.breakpointCleanup" or
                "excel.processClose" or
                "workspace.deletion")
            {
                Assert.False(callerCancellationToken.CanBeCanceled);
                CleanupCheckIds.Add(checkId);
            }

            var result = await operation(callerCancellationToken);
            return new DebugEnvironmentDoctorStageExecution(
                new DebugEnvironmentDiagnosticCheck(
                    checkId,
                    result.Status,
                    result.Message,
                    DurationMilliseconds: 1),
                DebugEnvironmentDoctorStageTermination.Completed);
        }
    }
}
