using VbaDebugAdapter.Cli;
using VbaDebugAdapter.Build;
using VbaDebugAdapter.Debugging;
using VbaDebugAdapter.Diagnostics;
using VbaDebugAdapter.Infrastructure;
using System.Text;
using System.Text.Json;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class VbaDebugAdapterCliSurfaceTests
{
    [Fact]
    public async Task CapabilitiesAdvertiseTheIndependentAdapterContract()
    {
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();
        var commandLine = VbaDebugAdapterCommandLine.Create();

        var exitCode = await commandLine.InvokeAsync(
            ["capabilities", "--format", "json"],
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            "{\"toolVersion\":\"0.1.0\",\"contractVersion\":\"1.0\",\"protocolVersion\":\"1.1\",\"transports\":[\"stdio\"],\"sessionIdFormat\":\"lowercase-hex-32\",\"commands\":[\"cleanup\",\"doctor\"],\"commandSchemaVersions\":{\"doctor\":\"1.0\"},\"featureVersions\":{\"doctor.stdinCancellation\":\"1.0\"},\"requiredVbaDevFeatureVersions\":{\"build.sourceSnapshot\":\"1.0\"}}" + Environment.NewLine,
            ReadUtf8(standardOutput));
        Assert.Empty(ReadUtf8(standardError));
    }

    [Fact]
    public async Task DoctorWritesTheClosedSchemaAndStableOrderedChecks()
    {
        string[] checkIds =
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
        var report = new DebugEnvironmentDiagnosticReport(
            "1.0",
            "0.1.0",
            DebugEnvironmentDiagnosticStatus.Pass,
            Complete: true,
            checkIds.Select(id => new DebugEnvironmentDiagnosticCheck(
                id,
                DebugEnvironmentDiagnosticStatus.Pass,
                $"{id} passed.",
                DurationMilliseconds: 0)).ToArray());
        var doctor = new RecordingDebugEnvironmentDoctor(report);
        var commandLine = VbaDebugAdapterCommandLine.Create(
            new RecordingStdioRunner(),
            new RecordingVbaDevCapabilitiesProbe(
                new VbaDevCapabilitiesProbeResult(0, string.Empty, string.Empty)),
            new RecordingSessionWorkspaceManager(),
            doctor);
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();

        var exitCode = await commandLine.InvokeAsync(
            ["doctor", "--format", "json"],
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, doctor.Invocations);
        Assert.Empty(ReadUtf8(standardError));
        using var document = JsonDocument.Parse(ReadUtf8(standardOutput));
        var root = document.RootElement;
        Assert.Equal(
            ["schemaVersion", "toolVersion", "status", "complete", "checks"],
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal("1.0", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("0.1.0", root.GetProperty("toolVersion").GetString());
        Assert.Equal("pass", root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("complete").GetBoolean());
        var checks = root.GetProperty("checks").EnumerateArray().ToArray();
        Assert.Equal(checkIds, checks.Select(check => check.GetProperty("id").GetString()));
        Assert.All(checks, check =>
        {
            Assert.Equal(
                ["id", "status", "message", "durationMilliseconds"],
                check.EnumerateObject().Select(property => property.Name));
            Assert.Equal("pass", check.GetProperty("status").GetString());
            Assert.False(string.IsNullOrWhiteSpace(check.GetProperty("message").GetString()));
            Assert.True(check.GetProperty("durationMilliseconds").GetInt64() >= 0);
        });
    }

    [Theory]
    [InlineData(DebugEnvironmentDiagnosticStatus.Pass, true, 0)]
    [InlineData(DebugEnvironmentDiagnosticStatus.Warning, true, 0)]
    [InlineData(DebugEnvironmentDiagnosticStatus.Fail, true, 1)]
    [InlineData(DebugEnvironmentDiagnosticStatus.Unverified, true, 1)]
    [InlineData(DebugEnvironmentDiagnosticStatus.Pass, false, 1)]
    public async Task DoctorExitCodeReflectsOverallStatusAndCompleteness(
        DebugEnvironmentDiagnosticStatus status,
        bool complete,
        int expectedExitCode)
    {
        var report = new DebugEnvironmentDiagnosticReport(
            "1.0",
            "0.1.0",
            status,
            complete,
            [new DebugEnvironmentDiagnosticCheck(
                "platform.windows",
                status,
                "Synthetic Doctor result.",
                DurationMilliseconds: 0)]);
        var commandLine = VbaDebugAdapterCommandLine.Create(
            new RecordingStdioRunner(),
            new RecordingVbaDevCapabilitiesProbe(
                new VbaDevCapabilitiesProbeResult(0, string.Empty, string.Empty)),
            new RecordingSessionWorkspaceManager(),
            new RecordingDebugEnvironmentDoctor(report));
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();

        var exitCode = await commandLine.InvokeAsync(
            ["doctor", "--format", "json"],
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(expectedExitCode, exitCode);
        using var document = JsonDocument.Parse(ReadUtf8(standardOutput));
        Assert.Equal(
            status.ToString().ToLowerInvariant(),
            document.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            complete,
            document.RootElement.GetProperty("complete").GetBoolean());
    }

    [Fact]
    public async Task DoctorInfrastructureLossStillWritesOneIncompleteJsonReport()
    {
        var commandLine = VbaDebugAdapterCommandLine.Create(
            new RecordingStdioRunner(),
            new RecordingVbaDevCapabilitiesProbe(
                new VbaDevCapabilitiesProbeResult(0, string.Empty, string.Empty)),
            new RecordingSessionWorkspaceManager(),
            new ThrowingDebugEnvironmentDoctor(
                new InvalidOperationException("Synthetic Doctor infrastructure loss.")));
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();

        var exitCode = await commandLine.InvokeAsync(
            ["doctor", "--format", "json"],
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        using var document = JsonDocument.Parse(ReadUtf8(standardOutput));
        var root = document.RootElement;
        Assert.Equal("1.0", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("unverified", root.GetProperty("status").GetString());
        Assert.False(root.GetProperty("complete").GetBoolean());
        var checks = root.GetProperty("checks").EnumerateArray().ToArray();
        Assert.Equal("platform.windows", checks[0].GetProperty("id").GetString());
        Assert.Equal("unverified", checks[0].GetProperty("status").GetString());
        Assert.Contains(
            "Synthetic Doctor infrastructure loss",
            checks[0].GetProperty("message").GetString(),
            StringComparison.Ordinal);
        Assert.All(checks[1..], check => Assert.Equal(
            "skipped",
            check.GetProperty("status").GetString()));
        Assert.Contains(
            "Synthetic Doctor infrastructure loss",
            ReadUtf8(standardError),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorAcceptsAnExactStdinCancellationFrame()
    {
        var doctor = new AwaitingCancellationDebugEnvironmentDoctor();
        var commandLine = VbaDebugAdapterCommandLine.Create(
            new RecordingStdioRunner(),
            new RecordingVbaDevCapabilitiesProbe(
                new VbaDevCapabilitiesProbeResult(0, string.Empty, string.Empty)),
            new RecordingSessionWorkspaceManager(),
            doctor);
        using var standardInput = new MemoryStream(Encoding.UTF8.GetBytes("cancel\n"));
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();

        var running = commandLine.InvokeAsync(
            [
                "doctor",
                "--format",
                "json",
                "--cancellation-transport",
                "stdin-v1"
            ],
            standardInput,
            standardOutput,
            standardError,
            CancellationToken.None);
        var completed = await Task.WhenAny(running, Task.Delay(TimeSpan.FromMilliseconds(250)));

        Assert.Same(running, completed);
        Assert.Equal(1, await running);
        Assert.False(doctor.CancellationRequestedAtEntry);
        Assert.True(doctor.CancellationObserved);
        using var document = JsonDocument.Parse(ReadUtf8(standardOutput));
        Assert.False(document.RootElement.GetProperty("complete").GetBoolean());
    }

    [Fact]
    public async Task DoctorCompletionDoesNotWaitForOpenStdinCancellationTransport()
    {
        var report = new DebugEnvironmentDiagnosticReport(
            "1.0",
            "0.1.0",
            DebugEnvironmentDiagnosticStatus.Pass,
            Complete: true,
            [new DebugEnvironmentDiagnosticCheck(
                "platform.windows",
                DebugEnvironmentDiagnosticStatus.Pass,
                "Windows is available.",
                DurationMilliseconds: 0)]);
        var commandLine = CreateCommandLine(new RecordingDebugEnvironmentDoctor(report));
        using var standardInput = new BlockingTailStream([]);
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();

        var invocation = commandLine.InvokeAsync(
            [
                "doctor",
                "--format",
                "json",
                "--cancellation-transport",
                "stdin-v1"
            ],
            standardInput,
            standardOutput,
            standardError,
            CancellationToken.None);
        var completed = await Task.WhenAny(
            invocation,
            Task.Delay(TimeSpan.FromSeconds(1)));

        Assert.Same(invocation, completed);
        Assert.Equal(0, await invocation);
        using var document = JsonDocument.Parse(ReadUtf8(standardOutput));
        Assert.True(document.RootElement.GetProperty("complete").GetBoolean());
    }

    [Fact]
    public async Task DoctorDiscardsAnInvalidFrameBeforeAcceptingCancellation()
    {
        var doctor = new AwaitingCancellationDebugEnvironmentDoctor();
        var commandLine = VbaDebugAdapterCommandLine.Create(
            new RecordingStdioRunner(),
            new RecordingVbaDevCapabilitiesProbe(
                new VbaDevCapabilitiesProbeResult(0, string.Empty, string.Empty)),
            new RecordingSessionWorkspaceManager(),
            doctor);
        using var standardInput = new MemoryStream(
            Encoding.UTF8.GetBytes("unknown\ncancel\n"));
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();

        var running = commandLine.InvokeAsync(
            [
                "doctor",
                "--format",
                "json",
                "--cancellation-transport",
                "stdin-v1"
            ],
            standardInput,
            standardOutput,
            standardError,
            CancellationToken.None);
        var completed = await Task.WhenAny(running, Task.Delay(TimeSpan.FromMilliseconds(250)));

        Assert.Same(running, completed);
        Assert.Equal(1, await running);
        Assert.False(doctor.CancellationRequestedAtEntry);
        Assert.True(doctor.CancellationObserved);
    }

    public static TheoryData<string> InvalidDoctorCancellationFrames => new()
    {
        string.Empty,
        "cancel\r\n",
        "\uFEFFcancel\n",
        "cancel",
        "unknown\n",
        new string('x', 4096) + "\n"
    };

    [Theory]
    [MemberData(nameof(InvalidDoctorCancellationFrames))]
    public async Task DoctorStdinCancellationIgnoresInvalidFrames(string frame)
    {
        var doctor = new DelayedCancellationInspectionDoctor();
        var commandLine = CreateCommandLine(doctor);
        using var standardInput = new MemoryStream(Encoding.UTF8.GetBytes(frame));
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();

        var exitCode = await commandLine.InvokeAsync(
            [
                "doctor",
                "--format",
                "json",
                "--cancellation-transport",
                "stdin-v1"
            ],
            standardInput,
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.False(doctor.CancellationObserved);
    }

    [Fact]
    public async Task OrdinaryDoctorDoesNotConsumeTheCancellationFrame()
    {
        var doctor = new DelayedCancellationInspectionDoctor();
        var commandLine = CreateCommandLine(doctor);
        using var standardInput = new MemoryStream(Encoding.UTF8.GetBytes("cancel\n"));
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();

        var exitCode = await commandLine.InvokeAsync(
            ["doctor", "--format", "json"],
            standardInput,
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.False(doctor.CancellationObserved);
    }

    [Theory]
    [InlineData("doctor", "--format", "json", "--project", "Example")]
    [InlineData("doctor", "--format", "json", "--document", "Book1")]
    [InlineData("doctor", "--format", "json", "--timeout", "1")]
    public async Task DoctorRejectsProjectDocumentAndTimeoutInputs(
        params string[] args)
    {
        var doctor = new RecordingDebugEnvironmentDoctor(
            DebugEnvironmentDoctor.InfrastructureFailure(
                "0.1.0",
                new InvalidOperationException("Must not run.")));
        var stdio = new RecordingStdioRunner();
        var capabilitiesProbe = new RecordingVbaDevCapabilitiesProbe(
            new VbaDevCapabilitiesProbeResult(0, string.Empty, string.Empty));
        var commandLine = VbaDebugAdapterCommandLine.Create(
            stdio,
            capabilitiesProbe,
            new RecordingSessionWorkspaceManager(),
            doctor);
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();

        var exitCode = await commandLine.InvokeAsync(
            args,
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Equal(0, doctor.Invocations);
        Assert.Empty(stdio.Invocations);
        Assert.Empty(capabilitiesProbe.Invocations);
        Assert.Empty(ReadUtf8(standardOutput));
        Assert.Contains(
            "Usage: vba-debug-adapter",
            ReadUtf8(standardError),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CapabilitiesAndInvalidCleanupDoNotCreateAWorkspaceRoot()
    {
        var parent = Path.Combine(
            Path.GetTempPath(),
            "vba-debug-adapter-cli-construction-tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            foreach (var invocation in new[]
                     {
                         (Args: new[] { "capabilities", "--format", "json" }, ExitCode: 0),
                         (Args: new[] { "cleanup", "--session", "invalid" }, ExitCode: 1)
                     })
            {
                var workspaceRoot = Path.Combine(parent, Guid.NewGuid().ToString("N"));
                var commandLine = VbaDebugAdapterCommandLine.CreateForWorkspaceRoot(
                    workspaceRoot);

                var exitCode = await commandLine.InvokeAsync(
                    invocation.Args,
                    Stream.Null,
                    Stream.Null,
                    CancellationToken.None);

                Assert.Equal(invocation.ExitCode, exitCode);
                Assert.False(Directory.Exists(workspaceRoot));
            }
        }
        finally
        {
            if (Directory.Exists(parent))
            {
                Directory.Delete(parent, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProductionCapabilitiesProbeUsesTheOwnedProcessBoundary()
    {
        var process = new RecordingCapabilitiesProcess(
            new VbaDevBuildProcessResult(
                7,
                "capabilities-output",
                "capabilities-error"));
        var probe = new ProcessVbaDevCapabilitiesProbe(process);
        var vbaDevPath = Path.GetFullPath("missing-vba-dev.exe");

        var result = await probe.ProbeAsync(vbaDevPath, CancellationToken.None);

        var invocation = Assert.Single(process.Invocations);
        Assert.Equal(vbaDevPath, invocation.FileName);
        Assert.Equal(
            ["capabilities", "--format", "json"],
            invocation.Arguments);
        Assert.Equal(7, result.ExitCode);
        Assert.Equal("capabilities-output", result.StandardOutput);
        Assert.Equal("capabilities-error", result.StandardError);
    }

    [Fact]
    public async Task StdioPinsTheSuppliedCliAndCanonicalSession()
    {
        var runner = new RecordingStdioRunner();
        var probe = new RecordingVbaDevCapabilitiesProbe(
            new VbaDevCapabilitiesProbeResult(
                0,
                "{\"contractVersion\":\"1.0\",\"featureVersions\":{\"build.sourceSnapshot\":\"1.0\"}}",
                string.Empty));
        var commandLine = VbaDebugAdapterCommandLine.Create(runner, probe);
        var vbaDevPath = Path.GetFullPath("vba-dev.exe");
        const string sessionId = "0123456789abcdef0123456789abcdef";
        using var standardInput = new MemoryStream();
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();

        var exitCode = await commandLine.InvokeAsync(
            ["--stdio", "--vba-dev", vbaDevPath, "--session", sessionId],
            standardInput,
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal([vbaDevPath], probe.Invocations);
        Assert.Equal([(vbaDevPath, sessionId)], runner.Invocations);
        Assert.Empty(ReadUtf8(standardOutput));
        Assert.Empty(ReadUtf8(standardError));
    }

    [Fact]
    public async Task StdioOwnsACreateNewLeaseForItsCompleteRunnerLifetime()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "vba-debug-adapter-cli-tests",
            Guid.NewGuid().ToString("N"));
        const string sessionId = "0123456789abcdef0123456789abcdef";
        var sessionWorkspacePath = Path.Combine(root, "workspaces", sessionId);
        var leasePath = Path.Combine(sessionWorkspacePath, "lease.json");
        var observedLease = false;
        var runner = new RecordingStdioRunner
        {
            OnRun = observedSessionId =>
            {
                Assert.Equal(sessionId, observedSessionId);
                Assert.True(File.Exists(leasePath));
                using var leaseStream = new FileStream(
                    leasePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite);
                using var lease = JsonDocument.Parse(leaseStream);
                Assert.Equal(1, lease.RootElement.GetProperty("schemaVersion").GetInt32());
                Assert.Equal(
                    sessionId,
                    lease.RootElement.GetProperty("sessionId").GetString());
                Assert.True(lease.RootElement.GetProperty("processId").GetInt32() > 0);
                Assert.Matches(
                    "^[0-9a-f]{32}$",
                    lease.RootElement.GetProperty("leaseId").GetString());
                Assert.True(DateTimeOffset.TryParse(
                    lease.RootElement.GetProperty("processStartTimeUtc").GetString(),
                    out _));
                Assert.Throws<IOException>(() =>
                    File.Open(
                        leasePath,
                        FileMode.Open,
                        FileAccess.Write,
                        FileShare.None).Dispose());
                observedLease = true;
            }
        };
        var probe = new RecordingVbaDevCapabilitiesProbe(
            new VbaDevCapabilitiesProbeResult(
                0,
                "{\"featureVersions\":{\"build.sourceSnapshot\":\"1.0\"}}",
                string.Empty));
        var commandLine = VbaDebugAdapterCommandLine.Create(
            runner,
            probe,
            new VbaDebugSessionWorkspaceManager(root));

        try
        {
            var exitCode = await commandLine.InvokeAsync(
                [
                    "--stdio",
                    "--vba-dev", Path.GetFullPath("vba-dev.exe"),
                    "--session", sessionId
                ],
                Stream.Null,
                Stream.Null,
                Stream.Null,
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.True(observedLease);
            Assert.False(Directory.Exists(sessionWorkspacePath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StdioReportsTheScopedRetainedPathWhenOwnedLeaseCleanupFails()
    {
        const string sessionId = "0123456789abcdef0123456789abcdef";
        var retainedPath = Path.GetFullPath(Path.Combine("retained", sessionId));
        var commandLine = VbaDebugAdapterCommandLine.Create(
            new RecordingStdioRunner(),
            new RecordingVbaDevCapabilitiesProbe(
                new VbaDevCapabilitiesProbeResult(
                    0,
                    "{\"featureVersions\":{\"build.sourceSnapshot\":\"1.0\"}}",
                    string.Empty)),
            new FailingDisposeSessionWorkspaceManager(retainedPath));
        using var standardError = new MemoryStream();

        var exitCode = await commandLine.InvokeAsync(
            [
                "--stdio",
                "--vba-dev", Path.GetFullPath("vba-dev.exe"),
                "--session", sessionId
            ],
            Stream.Null,
            Stream.Null,
            standardError,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains(retainedPath, ReadUtf8(standardError), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CleanupRoutesOnlyTheCanonicalSessionIdentity()
    {
        const string sessionId = "0123456789abcdef0123456789abcdef";
        var workspaceManager = new RecordingSessionWorkspaceManager();
        var commandLine = VbaDebugAdapterCommandLine.Create(
            new RecordingStdioRunner(),
            new RecordingVbaDevCapabilitiesProbe(
                new VbaDevCapabilitiesProbeResult(0, string.Empty, string.Empty)),
            workspaceManager);
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();

        var exitCode = await commandLine.InvokeAsync(
            ["cleanup", "--session", sessionId],
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal([sessionId], workspaceManager.CleanupInvocations);
        Assert.Empty(ReadUtf8(standardOutput));
        Assert.Empty(ReadUtf8(standardError));
    }

    [Fact]
    public async Task CleanupRejectsPathsAndNoncanonicalSessionIdentities()
    {
        var workspaceManager = new RecordingSessionWorkspaceManager();
        var commandLine = VbaDebugAdapterCommandLine.Create(
            new RecordingStdioRunner(),
            new RecordingVbaDevCapabilitiesProbe(
                new VbaDevCapabilitiesProbeResult(0, string.Empty, string.Empty)),
            workspaceManager);
        IReadOnlyList<string>[] invalidArguments =
        [
            ["cleanup", "--session", Path.GetFullPath("workspace")],
            ["cleanup", "--session", "../0123456789abcdef0123456789abcdef"],
            ["cleanup", "--session", "0123456789ABCDEF0123456789ABCDEF"],
            ["cleanup", "--session", "0123456789abcdef0123456789abcdef", "extra"]
        ];

        foreach (var arguments in invalidArguments)
        {
            using var standardError = new MemoryStream();
            var exitCode = await commandLine.InvokeAsync(
                arguments,
                Stream.Null,
                standardError,
                CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Contains("Usage:", ReadUtf8(standardError), StringComparison.Ordinal);
        }

        Assert.Empty(workspaceManager.CleanupInvocations);
    }

    [Fact]
    public async Task StdioStartupReapsOnlyProvablyStaleCanonicalWorkspaces()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "vba-debug-adapter-cli-tests",
            Guid.NewGuid().ToString("N"));
        const string staleSessionId = "11111111111111111111111111111111";
        const string newSessionId = "22222222222222222222222222222222";
        const string retainedSessionId = "33333333333333333333333333333333";
        var staleWorkspacePath = Path.Combine(root, "workspaces", staleSessionId);
        var retainedWorkspacePath = Path.Combine(root, "workspaces", retainedSessionId);
        var unrelatedWorkspacePath = Path.Combine(root, "workspaces", "retained-not-a-session");
        var newWorkspacePath = Path.Combine(root, "workspaces", newSessionId);
        Directory.CreateDirectory(staleWorkspacePath);
        Directory.CreateDirectory(retainedWorkspacePath);
        Directory.CreateDirectory(unrelatedWorkspacePath);
        await File.WriteAllTextAsync(
            Path.Combine(staleWorkspacePath, "lease.json"),
            """
            {"schemaVersion":1,"sessionId":"11111111111111111111111111111111","leaseId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","processId":2147483647,"processStartTimeUtc":"2020-01-02T03:04:05.0000000Z"}
            """);
        await File.WriteAllTextAsync(
            Path.Combine(retainedWorkspacePath, "lease.json"),
            "unverified");
        await File.WriteAllTextAsync(
            Path.Combine(unrelatedWorkspacePath, "keep.txt"),
            "unrelated");
        var runner = new RecordingStdioRunner
        {
            OnRun = _ =>
            {
                Assert.False(Directory.Exists(staleWorkspacePath));
                Assert.True(Directory.Exists(retainedWorkspacePath));
                Assert.True(Directory.Exists(unrelatedWorkspacePath));
                Assert.True(File.Exists(Path.Combine(newWorkspacePath, "lease.json")));
            }
        };
        var manager = new VbaDebugSessionWorkspaceManager(root);
        var commandLine = VbaDebugAdapterCommandLine.Create(
            runner,
            new RecordingVbaDevCapabilitiesProbe(
                new VbaDevCapabilitiesProbeResult(
                    0,
                    "{\"featureVersions\":{\"build.sourceSnapshot\":\"1.0\"}}",
                    string.Empty)),
            manager);

        try
        {
            var exitCode = await commandLine.InvokeAsync(
                [
                    "--stdio",
                    "--vba-dev", Path.GetFullPath("vba-dev.exe"),
                    "--session", newSessionId
                ],
                Stream.Null,
                Stream.Null,
                Stream.Null,
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.False(Directory.Exists(staleWorkspacePath));
            Assert.True(Directory.Exists(retainedWorkspacePath));
            Assert.True(Directory.Exists(unrelatedWorkspacePath));
            Assert.False(Directory.Exists(newWorkspacePath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StdioNeverReapsOrReusesTheRequestedExistingSessionIdentity()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "vba-debug-adapter-cli-tests",
            Guid.NewGuid().ToString("N"));
        const string sessionId = "0123456789abcdef0123456789abcdef";
        var sessionWorkspacePath = Path.Combine(root, "workspaces", sessionId);
        Directory.CreateDirectory(sessionWorkspacePath);
        await File.WriteAllTextAsync(
            Path.Combine(sessionWorkspacePath, "lease.json"),
            """
            {"schemaVersion":1,"sessionId":"0123456789abcdef0123456789abcdef","leaseId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","processId":2147483647,"processStartTimeUtc":"2020-01-02T03:04:05.0000000Z"}
            """);
        var runner = new RecordingStdioRunner();
        var commandLine = VbaDebugAdapterCommandLine.Create(
            runner,
            new RecordingVbaDevCapabilitiesProbe(
                new VbaDevCapabilitiesProbeResult(
                    0,
                    "{\"featureVersions\":{\"build.sourceSnapshot\":\"1.0\"}}",
                    string.Empty)),
            new VbaDebugSessionWorkspaceManager(root));

        try
        {
            using var standardError = new MemoryStream();
            var exitCode = await commandLine.InvokeAsync(
                [
                    "--stdio",
                    "--vba-dev", Path.GetFullPath("vba-dev.exe"),
                    "--session", sessionId
                ],
                Stream.Null,
                Stream.Null,
                standardError,
                CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Empty(runner.Invocations);
            Assert.True(Directory.Exists(sessionWorkspacePath));
            Assert.Contains("already exists", ReadUtf8(standardError), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StdioRejectsAPinnedCliWithoutTheRequiredSnapshotBuildFeature()
    {
        var runner = new RecordingStdioRunner();
        var probe = new RecordingVbaDevCapabilitiesProbe(
            new VbaDevCapabilitiesProbeResult(
                0,
                "{\"contractVersion\":\"1.0\",\"featureVersions\":{\"build.sourceSnapshot\":\"0.9\"}}",
                string.Empty));
        var commandLine = VbaDebugAdapterCommandLine.Create(runner, probe);
        var vbaDevPath = Path.GetFullPath("vba-dev.exe");
        const string sessionId = "0123456789abcdef0123456789abcdef";
        using var standardInput = new MemoryStream();
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();

        var exitCode = await commandLine.InvokeAsync(
            ["--stdio", "--vba-dev", vbaDevPath, "--session", sessionId],
            standardInput,
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Equal([vbaDevPath], probe.Invocations);
        Assert.Empty(runner.Invocations);
        Assert.Empty(ReadUtf8(standardOutput));
        Assert.Contains("build.sourceSnapshot 1.0", ReadUtf8(standardError), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StandaloneStdioReturnsDapInitializeCapabilities()
    {
        var probe = new RecordingVbaDevCapabilitiesProbe(
            new VbaDevCapabilitiesProbeResult(
                0,
                "{\"featureVersions\":{\"build.sourceSnapshot\":\"1.0\"}}",
                string.Empty));
        var commandLine = VbaDebugAdapterCommandLine.Create(
            new StandaloneVbaDebugAdapterStdioRunner(),
            probe);
        var vbaDevPath = Path.GetFullPath("vba-dev.exe");
        const string sessionId = "0123456789abcdef0123456789abcdef";
        using var standardInput = CreateDapInput(
            new { seq = 1, type = "request", command = "initialize", arguments = new { } });
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();

        var exitCode = await commandLine.InvokeAsync(
            ["--stdio", "--vba-dev", vbaDevPath, "--session", sessionId],
            standardInput,
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        var messages = ReadDapMessages(standardOutput);
        Assert.Equal(2, messages.Count);
        Assert.Equal("response", messages[0].GetProperty("type").GetString());
        Assert.Equal(1, messages[0].GetProperty("request_seq").GetInt32());
        Assert.True(messages[0].GetProperty("success").GetBoolean());
        Assert.Equal("initialize", messages[0].GetProperty("command").GetString());
        Assert.True(
            messages[0].GetProperty("body")
                .GetProperty("supportsConfigurationDoneRequest")
                .GetBoolean());
        Assert.True(
            messages[0].GetProperty("body")
                .GetProperty("supportsRestartRequest")
                .GetBoolean());
        Assert.True(
            messages[0].GetProperty("body")
                .GetProperty("supportsTerminateRequest")
                .GetBoolean());
        Assert.Equal("event", messages[1].GetProperty("type").GetString());
        Assert.Equal("initialized", messages[1].GetProperty("event").GetString());
        Assert.Empty(ReadUtf8(standardError));
    }

    [Fact]
    public async Task StandaloneStdioAcceptsAnOptionalContentTypeHeader()
    {
        var probe = new RecordingVbaDevCapabilitiesProbe(
            new VbaDevCapabilitiesProbeResult(
                0,
                "{\"featureVersions\":{\"build.sourceSnapshot\":\"1.0\"}}",
                string.Empty));
        var commandLine = VbaDebugAdapterCommandLine.Create(
            new StandaloneVbaDebugAdapterStdioRunner(),
            probe);
        var content = JsonSerializer.SerializeToUtf8Bytes(
            new { seq = 1, type = "request", command = "initialize", arguments = new { } });
        using var standardInput = new MemoryStream();
        standardInput.Write(Encoding.ASCII.GetBytes(
            $"Content-Length: {content.Length}\r\n" +
            "Content-Type: application/vscode-jsonrpc; charset=utf-8\r\n\r\n"));
        standardInput.Write(content);
        standardInput.Position = 0;
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();

        var exitCode = await commandLine.InvokeAsync(
            [
                "--stdio",
                "--vba-dev", Path.GetFullPath("vba-dev.exe"),
                "--session", "0123456789abcdef0123456789abcdef"
            ],
            standardInput,
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        var initializeResponse = Assert.Single(
            ReadDapMessages(standardOutput),
            message => message.TryGetProperty("command", out var command) &&
                       command.GetString() == "initialize");
        Assert.True(initializeResponse.GetProperty("success").GetBoolean());
        Assert.Empty(ReadUtf8(standardError));
    }

    [Fact]
    public async Task StandaloneLaunchPassesTheTransportedSnapshotToThePinnedSession()
    {
        var probe = new RecordingVbaDevCapabilitiesProbe(
            new VbaDevCapabilitiesProbeResult(
                0,
                "{\"featureVersions\":{\"build.sourceSnapshot\":\"1.0\"}}",
                string.Empty));
        var launchService = new RecordingDebugLaunchService();
        var commandLine = VbaDebugAdapterCommandLine.Create(
            new StandaloneVbaDebugAdapterStdioRunner(launchService),
            probe);
        var vbaDevPath = Path.GetFullPath("vba-dev.exe");
        var projectRoot = Path.GetFullPath("project");
        const string sessionId = "0123456789abcdef0123456789abcdef";
        var contentBase64 = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(
                "Attribute VB_Name = \"Module1\"\r\nPublic Sub Run()\r\nEnd Sub\r\n"));
        using var standardInput = CreateDapInput(
            new { seq = 1, type = "request", command = "initialize", arguments = new { } },
            new
            {
                seq = 2,
                type = "request",
                command = "launch",
                arguments = new
                {
                    project = projectRoot,
                    document = "Book1",
                    module = "Module1",
                    procedure = "Run",
                    __vbaDebugWorkbookFileName = "Book1.xlsm",
                    sourceSnapshot = new
                    {
                        schemaVersion = 1,
                        sources = new[]
                        {
                            new
                            {
                                relativePath = "Module1.bas",
                                sourceUri = "file:///C:/persistent/Module1.bas",
                                encoding = "utf8",
                                contentBase64
                            }
                        },
                        activeSource = new
                        {
                            sourceUri = "file:///C:/persistent/Module1.bas",
                            line = 1,
                            character = 4
                        },
                        breakpoints = new[]
                        {
                            new
                            {
                                sourceUri = "file:///C:/persistent/Module1.bas",
                                line = 2
                            }
                        }
                    }
                }
            },
            new { seq = 3, type = "request", command = "configurationDone", arguments = new { } });
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();

        var exitCode = await commandLine.InvokeAsync(
            ["--stdio", "--vba-dev", vbaDevPath, "--session", sessionId],
            standardInput,
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        var invocation = Assert.Single(launchService.Invocations);
        Assert.Equal(vbaDevPath, invocation.VbaDevPath);
        Assert.Equal(sessionId, invocation.SessionId);
        Assert.Equal(projectRoot, invocation.Request.ProjectRoot);
        Assert.Equal("Book1", invocation.Request.DocumentName);
        Assert.Equal("Book1.xlsm", invocation.Request.WorkbookFileName);
        Assert.Equal(contentBase64, Assert.Single(invocation.Request.SourceSnapshot.Sources).ContentBase64);
        Assert.Equal(
            new TransportedDebugSourcePosition(
                "file:///C:/persistent/Module1.bas",
                1,
                4),
            invocation.Request.SourceSnapshot.ActiveSource);
        Assert.Equal(
            new TransportedDebugSourceBreakpoint(
                "file:///C:/persistent/Module1.bas",
                2),
            Assert.Single(invocation.Request.SourceSnapshot.Breakpoints));
        var launchResponse = Assert.Single(
            ReadDapMessages(standardOutput),
            message => message.TryGetProperty("request_seq", out var sequence) &&
                       sequence.GetInt32() == 2);
        Assert.True(launchResponse.GetProperty("success").GetBoolean());
        Assert.Empty(ReadUtf8(standardError));
    }

    [Fact]
    public async Task StandaloneLaunchAcceptsAStrictRestartPreparationBinding()
    {
        var probe = new RecordingVbaDevCapabilitiesProbe(
            new VbaDevCapabilitiesProbeResult(
                0,
                "{\"featureVersions\":{\"build.sourceSnapshot\":\"1.0\"}}",
                string.Empty));
        var launchService = new RecordingDebugLaunchService();
        var commandLine = VbaDebugAdapterCommandLine.Create(
            new StandaloneVbaDebugAdapterStdioRunner(launchService),
            probe);
        var launchArguments = CreateValidLaunchArguments();
        launchArguments["__vbaRestartPreparation"] = new
        {
            protocolVersion = 1,
            id = "fedcba9876543210fedcba9876543210",
            generation = 0
        };
        using var standardInput = CreateDapInput(
            new { seq = 1, type = "request", command = "launch", arguments = launchArguments },
            new { seq = 2, type = "request", command = "configurationDone", arguments = new { } },
            new { seq = 3, type = "request", command = "disconnect", arguments = new { } });
        using var standardOutput = new MemoryStream();

        var exitCode = await commandLine.InvokeAsync(
            [
                "--stdio",
                "--vba-dev", Path.GetFullPath("vba-dev.exe"),
                "--session", "0123456789abcdef0123456789abcdef"
            ],
            standardInput,
            standardOutput,
            Stream.Null,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Single(launchService.Invocations);
        var launchResponse = Assert.Single(
            ReadDapMessages(standardOutput),
            message => message.TryGetProperty("request_seq", out var sequence) &&
                       sequence.GetInt32() == 1);
        Assert.True(launchResponse.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task FailedRestartPreparationRetainsTheOwnedSession()
    {
        const string sessionId = "0123456789abcdef0123456789abcdef";
        const string preparationId = "fedcba9876543210fedcba9876543210";
        var probe = new RecordingVbaDevCapabilitiesProbe(
            new VbaDevCapabilitiesProbeResult(
                0,
                "{\"featureVersions\":{\"build.sourceSnapshot\":\"1.0\"}}",
                string.Empty));
        var runningSession = new RecordingRunningSession();
        var launchService = new RecordingDebugLaunchService(runningSession);
        var commandLine = VbaDebugAdapterCommandLine.Create(
            new StandaloneVbaDebugAdapterStdioRunner(launchService),
            probe);
        var launchArguments = CreateValidLaunchArguments();
        launchArguments["__vbaRestartPreparation"] = new
        {
            protocolVersion = 1,
            id = preparationId,
            generation = 0
        };
        using var inputPrefix = CreateDapInput(
            new { seq = 1, type = "request", command = "launch", arguments = launchArguments },
            new { seq = 2, type = "request", command = "configurationDone", arguments = new { } },
            new { seq = 3, type = "request", command = "restart", arguments = new { } },
            new
            {
                seq = 4,
                type = "request",
                command = "vba/restartPrepared",
                arguments = new
                {
                    sessionId,
                    restartRequestSequence = 3,
                    preparationId,
                    generation = 1,
                    success = false,
                    message = "Synthetic snapshot capture failure."
                }
            });
        using var standardInput = new BlockingTailStream(inputPrefix.ToArray());
        using var standardOutput = new MemoryStream();
        var invocation = commandLine.InvokeAsync(
            [
                "--stdio",
                "--vba-dev", Path.GetFullPath("vba-dev.exe"),
                "--session", sessionId
            ],
            standardInput,
            standardOutput,
            Stream.Null,
            CancellationToken.None);

        try
        {
            Assert.True(SpinWait.SpinUntil(
                () => ReadUtf8(standardOutput).Contains(
                    "\"request_seq\":4",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(2)));
            var messages = ReadDapMessages(standardOutput);
            var preparationResponse = Assert.Single(
                messages,
                message => message.TryGetProperty("request_seq", out var sequence) &&
                           sequence.GetInt32() == 4);
            Assert.True(preparationResponse.GetProperty("success").GetBoolean());
            var restartResponse = Assert.Single(
                messages,
                message => message.TryGetProperty("request_seq", out var sequence) &&
                           sequence.GetInt32() == 3);
            Assert.False(restartResponse.GetProperty("success").GetBoolean());
            Assert.Equal(
                "Synthetic snapshot capture failure.",
                restartResponse.GetProperty("message").GetString());
            Assert.Equal(0, runningSession.TerminateCalls);
            Assert.Equal(0, runningSession.DisposeCalls);
        }
        finally
        {
            standardInput.Complete();
            _ = await invocation;
        }
    }

    [Fact]
    public async Task StaleRestartPreparationFailsOnlyRestartAndRetainsTheOwnedSession()
    {
        const string sessionId = "0123456789abcdef0123456789abcdef";
        const string preparationId = "fedcba9876543210fedcba9876543210";
        var runningSession = new RecordingRunningSession();
        var commandLine = VbaDebugAdapterCommandLine.Create(
            new StandaloneVbaDebugAdapterStdioRunner(
                new RecordingDebugLaunchService(runningSession)),
            new RecordingVbaDevCapabilitiesProbe(
                new VbaDevCapabilitiesProbeResult(
                    0,
                    "{\"featureVersions\":{\"build.sourceSnapshot\":\"1.0\"}}",
                    string.Empty)));
        var launchArguments = CreateValidLaunchArguments();
        launchArguments["__vbaRestartPreparation"] = new
        {
            protocolVersion = 1,
            id = preparationId,
            generation = 0
        };
        using var inputPrefix = CreateDapInput(
            new { seq = 1, type = "request", command = "launch", arguments = launchArguments },
            new { seq = 2, type = "request", command = "configurationDone", arguments = new { } },
            new { seq = 3, type = "request", command = "restart", arguments = new { } },
            new
            {
                seq = 4,
                type = "request",
                command = "vba/restartPrepared",
                arguments = new
                {
                    sessionId,
                    restartRequestSequence = 3,
                    preparationId,
                    generation = 2,
                    success = true
                }
            },
            new
            {
                seq = 5,
                type = "request",
                command = "vba/restartPrepared",
                arguments = new
                {
                    sessionId,
                    restartRequestSequence = 3,
                    preparationId,
                    generation = 2,
                    success = true
                }
            });
        using var standardInput = new BlockingTailStream(inputPrefix.ToArray());
        using var standardOutput = new MemoryStream();
        var invocation = commandLine.InvokeAsync(
            [
                "--stdio",
                "--vba-dev", Path.GetFullPath("vba-dev.exe"),
                "--session", sessionId
            ],
            standardInput,
            standardOutput,
            Stream.Null,
            CancellationToken.None);

        try
        {
            Assert.True(SpinWait.SpinUntil(
                () => ReadUtf8(standardOutput).Contains(
                    "\"request_seq\":5",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(2)));
            var messages = ReadDapMessages(standardOutput);
            var preparationResponse = Assert.Single(
                messages,
                message => message.TryGetProperty("request_seq", out var sequence) &&
                           sequence.GetInt32() == 4);
            var restartResponse = Assert.Single(
                messages,
                message => message.TryGetProperty("request_seq", out var sequence) &&
                           sequence.GetInt32() == 3);
            Assert.True(preparationResponse.GetProperty("success").GetBoolean());
            Assert.True(Assert.Single(
                messages,
                message => message.TryGetProperty("request_seq", out var sequence) &&
                           sequence.GetInt32() == 5).GetProperty("success").GetBoolean());
            Assert.False(restartResponse.GetProperty("success").GetBoolean());
            Assert.Contains(
                "generation",
                restartResponse.GetProperty("message").GetString(),
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, runningSession.TerminateCalls);
            Assert.Equal(0, runningSession.DisposeCalls);
        }
        finally
        {
            standardInput.Complete();
            _ = await invocation;
        }
    }

    [Fact]
    public async Task MatchingRestartPreparationReplacesTheOwnedSessionWithTheFreshSnapshot()
    {
        const string sessionId = "0123456789abcdef0123456789abcdef";
        const string preparationId = "fedcba9876543210fedcba9876543210";
        var events = new List<string>();
        var oldSession = new RecordingRunningSession(events: events, label: "old");
        var freshSession = new RecordingRunningSession(events: events, label: "fresh");
        var launchService = new SequencedDebugLaunchService(
            [oldSession, freshSession],
            events);
        var commandLine = VbaDebugAdapterCommandLine.Create(
            new StandaloneVbaDebugAdapterStdioRunner(launchService),
            new RecordingVbaDevCapabilitiesProbe(
                new VbaDevCapabilitiesProbeResult(
                    0,
                    "{\"featureVersions\":{\"build.sourceSnapshot\":\"1.0\"}}",
                    string.Empty)));
        var oldContent = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            "Attribute VB_Name = \"Module1\"\r\nPublic Sub Run()\r\nEnd Sub\r\n"));
        var freshContent = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            "Attribute VB_Name = \"Module1\"\r\nPublic Sub Run()\r\nDebug.Print \"fresh\"\r\nEnd Sub\r\n"));
        var initialLaunch = CreateValidLaunchArguments();
        SetLaunchContent(initialLaunch, oldContent);
        initialLaunch["__vbaRestartPreparation"] = new
        {
            protocolVersion = 1,
            id = preparationId,
            generation = 0
        };
        var freshLaunch = CreateValidLaunchArguments();
        SetLaunchContent(freshLaunch, freshContent);
        freshLaunch["__vbaRestartPreparation"] = new
        {
            protocolVersion = 1,
            id = preparationId,
            generation = 1
        };
        using var standardInput = CreateDapInput(
            new { seq = 1, type = "request", command = "launch", arguments = initialLaunch },
            new { seq = 2, type = "request", command = "configurationDone", arguments = new { } },
            new { seq = 3, type = "request", command = "restart", arguments = new { } },
            new
            {
                seq = 4,
                type = "request",
                command = "vba/restartPrepared",
                arguments = new
                {
                    sessionId,
                    restartRequestSequence = 3,
                    preparationId,
                    generation = 1,
                    success = true,
                    launch = freshLaunch
                }
            },
            new { seq = 5, type = "request", command = "disconnect", arguments = new { } });
        using var standardOutput = new MemoryStream();

        var exitCode = await commandLine.InvokeAsync(
            [
                "--stdio",
                "--vba-dev", Path.GetFullPath("vba-dev.exe"),
                "--session", sessionId
            ],
            standardInput,
            standardOutput,
            Stream.Null,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(2, launchService.Invocations.Count);
        Assert.All(launchService.Invocations, invocation =>
            Assert.Equal(sessionId, invocation.SessionId));
        Assert.Equal(
            [oldContent, freshContent],
            launchService.Invocations.Select(invocation =>
                Assert.Single(invocation.Request.SourceSnapshot.Sources).ContentBase64));
        Assert.Equal(
            ["launch:1", "old:terminate", "old:dispose", "launch:2", "fresh:terminate", "fresh:dispose"],
            events);
        var messages = ReadDapMessages(standardOutput);
        Assert.True(Assert.Single(
            messages,
            message => message.TryGetProperty("request_seq", out var sequence) &&
                       sequence.GetInt32() == 4).GetProperty("success").GetBoolean());
        Assert.True(Assert.Single(
            messages,
            message => message.TryGetProperty("request_seq", out var sequence) &&
                       sequence.GetInt32() == 3).GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task RestartAcceptsExplicitSelectorsWithDifferentDeclarationCasing()
    {
        const string sessionId = "0123456789abcdef0123456789abcdef";
        const string preparationId = "fedcba9876543210fedcba9876543210";
        var oldSession = new RecordingRunningSession(
            targetModuleName: "Module1",
            targetProcedureName: "Run");
        var freshSession = new RecordingRunningSession(
            targetModuleName: "Module1",
            targetProcedureName: "Run");
        var launchService = new SequencedDebugLaunchService(
            [oldSession, freshSession],
            []);
        var commandLine = VbaDebugAdapterCommandLine.Create(
            new StandaloneVbaDebugAdapterStdioRunner(launchService),
            new RecordingVbaDevCapabilitiesProbe(
                new VbaDevCapabilitiesProbeResult(
                    0,
                    "{\"featureVersions\":{\"build.sourceSnapshot\":\"1.0\"}}",
                    string.Empty)));
        var initialLaunch = CreateValidLaunchArguments();
        initialLaunch["module"] = "module1";
        initialLaunch["procedure"] = "run";
        initialLaunch["__vbaRestartPreparation"] = new
        {
            protocolVersion = 1,
            id = preparationId,
            generation = 0
        };
        var freshLaunch = CreateValidLaunchArguments();
        freshLaunch["module"] = "module1";
        freshLaunch["procedure"] = "run";
        freshLaunch["__vbaRestartPreparation"] = new
        {
            protocolVersion = 1,
            id = preparationId,
            generation = 1
        };
        using var standardInput = CreateDapInput(
            new { seq = 1, type = "request", command = "launch", arguments = initialLaunch },
            new { seq = 2, type = "request", command = "configurationDone", arguments = new { } },
            new { seq = 3, type = "request", command = "restart", arguments = new { } },
            new
            {
                seq = 4,
                type = "request",
                command = "vba/restartPrepared",
                arguments = new
                {
                    sessionId,
                    restartRequestSequence = 3,
                    preparationId,
                    generation = 1,
                    success = true,
                    launch = freshLaunch
                }
            },
            new { seq = 5, type = "request", command = "disconnect", arguments = new { } });
        using var standardOutput = new MemoryStream();

        var exitCode = await commandLine.InvokeAsync(
            [
                "--stdio",
                "--vba-dev", Path.GetFullPath("vba-dev.exe"),
                "--session", sessionId
            ],
            standardInput,
            standardOutput,
            Stream.Null,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(2, launchService.Invocations.Count);
        Assert.Equal("Module1", launchService.Invocations[1].Request.ModuleName);
        Assert.Equal("Run", launchService.Invocations[1].Request.ProcedureName);
        var restartResponse = Assert.Single(
            ReadDapMessages(standardOutput),
            message => message.TryGetProperty("request_seq", out var sequence) &&
                       sequence.GetInt32() == 3);
        Assert.True(restartResponse.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task InvalidFreshSnapshotFailsOnlyRestartAndRetainsTheOwnedSession()
    {
        const string sessionId = "0123456789abcdef0123456789abcdef";
        const string preparationId = "fedcba9876543210fedcba9876543210";
        var runningSession = new RecordingRunningSession();
        var validator = TransportedDebugSourceSnapshotValidator
            .CreateForCurrentWindowsSession();
        var launchService = new SequencedDebugLaunchService(
            [runningSession],
            [],
            request => validator.Validate(request.SourceSnapshot));
        var commandLine = VbaDebugAdapterCommandLine.Create(
            new StandaloneVbaDebugAdapterStdioRunner(launchService),
            new RecordingVbaDevCapabilitiesProbe(
                new VbaDevCapabilitiesProbeResult(
                    0,
                    "{\"featureVersions\":{\"build.sourceSnapshot\":\"1.0\"}}",
                    string.Empty)));
        var initialLaunch = CreateValidLaunchArguments();
        initialLaunch["__vbaRestartPreparation"] = new
        {
            protocolVersion = 1,
            id = preparationId,
            generation = 0
        };
        var invalidFreshLaunch = CreateValidLaunchArguments();
        SetLaunchContent(invalidFreshLaunch, "not-base64");
        invalidFreshLaunch["__vbaRestartPreparation"] = new
        {
            protocolVersion = 1,
            id = preparationId,
            generation = 1
        };
        using var inputPrefix = CreateDapInput(
            new { seq = 1, type = "request", command = "launch", arguments = initialLaunch },
            new { seq = 2, type = "request", command = "configurationDone", arguments = new { } },
            new { seq = 3, type = "request", command = "restart", arguments = new { } },
            new
            {
                seq = 4,
                type = "request",
                command = "vba/restartPrepared",
                arguments = new
                {
                    sessionId,
                    restartRequestSequence = 3,
                    preparationId,
                    generation = 1,
                    success = true,
                    launch = invalidFreshLaunch
                }
            });
        using var standardInput = new BlockingTailStream(inputPrefix.ToArray());
        using var standardOutput = new MemoryStream();
        var invocation = commandLine.InvokeAsync(
            [
                "--stdio",
                "--vba-dev", Path.GetFullPath("vba-dev.exe"),
                "--session", sessionId
            ],
            standardInput,
            standardOutput,
            Stream.Null,
            CancellationToken.None);

        try
        {
            Assert.True(SpinWait.SpinUntil(
                () => ReadUtf8(standardOutput).Contains(
                    "\"request_seq\":4",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(2)));
            var messages = ReadDapMessages(standardOutput);
            var preparationResponse = Assert.Single(
                messages,
                message => message.TryGetProperty("request_seq", out var sequence) &&
                           sequence.GetInt32() == 4);
            Assert.True(preparationResponse.GetProperty("success").GetBoolean());
            var restartResponse = Assert.Single(
                messages,
                message => message.TryGetProperty("request_seq", out var sequence) &&
                           sequence.GetInt32() == 3);
            Assert.False(restartResponse.GetProperty("success").GetBoolean());
            Assert.Contains(
                "base64",
                restartResponse.GetProperty("message").GetString(),
                StringComparison.OrdinalIgnoreCase);
            Assert.Single(launchService.Invocations);
            Assert.Equal(0, runningSession.TerminateCalls);
            Assert.Equal(0, runningSession.DisposeCalls);
        }
        finally
        {
            standardInput.Complete();
            _ = await invocation;
        }
    }

    [Fact]
    public async Task RestartPinsTheInitiallyResolvedModuleAndProcedure()
    {
        const string sessionId = "0123456789abcdef0123456789abcdef";
        const string preparationId = "fedcba9876543210fedcba9876543210";
        var oldSession = new RecordingRunningSession(
            targetModuleName: "Module1",
            targetProcedureName: "Run");
        var freshSession = new RecordingRunningSession(
            targetModuleName: "Module1",
            targetProcedureName: "Run");
        var launchService = new SequencedDebugLaunchService(
            [oldSession, freshSession],
            []);
        var commandLine = VbaDebugAdapterCommandLine.Create(
            new StandaloneVbaDebugAdapterStdioRunner(launchService),
            new RecordingVbaDevCapabilitiesProbe(
                new VbaDevCapabilitiesProbeResult(
                    0,
                    "{\"featureVersions\":{\"build.sourceSnapshot\":\"1.0\"}}",
                    string.Empty)));
        var initialLaunch = CreateValidLaunchArguments();
        initialLaunch.Remove("module");
        initialLaunch.Remove("procedure");
        SetLaunchActiveSource(initialLaunch, line: 1);
        initialLaunch["__vbaRestartPreparation"] = new
        {
            protocolVersion = 1,
            id = preparationId,
            generation = 0
        };
        var freshLaunch = CreateValidLaunchArguments();
        freshLaunch.Remove("module");
        freshLaunch.Remove("procedure");
        SetLaunchActiveSource(freshLaunch, line: 20);
        freshLaunch["__vbaRestartPreparation"] = new
        {
            protocolVersion = 1,
            id = preparationId,
            generation = 1
        };
        using var standardInput = CreateDapInput(
            new { seq = 1, type = "request", command = "launch", arguments = initialLaunch },
            new { seq = 2, type = "request", command = "configurationDone", arguments = new { } },
            new { seq = 3, type = "request", command = "restart", arguments = new { } },
            new
            {
                seq = 4,
                type = "request",
                command = "vba/restartPrepared",
                arguments = new
                {
                    sessionId,
                    restartRequestSequence = 3,
                    preparationId,
                    generation = 1,
                    success = true,
                    launch = freshLaunch
                }
            },
            new { seq = 5, type = "request", command = "disconnect", arguments = new { } });

        _ = await commandLine.InvokeAsync(
            [
                "--stdio",
                "--vba-dev", Path.GetFullPath("vba-dev.exe"),
                "--session", sessionId
            ],
            standardInput,
            new MemoryStream(),
            Stream.Null,
            CancellationToken.None);

        Assert.Equal(2, launchService.Invocations.Count);
        Assert.Null(launchService.Invocations[0].Request.ModuleName);
        Assert.Null(launchService.Invocations[0].Request.ProcedureName);
        Assert.Equal("Module1", launchService.Invocations[1].Request.ModuleName);
        Assert.Equal("Run", launchService.Invocations[1].Request.ProcedureName);
    }

    [Fact]
    public async Task RestartRejectsANonMonotonicDapRequestSequence()
    {
        const string sessionId = "0123456789abcdef0123456789abcdef";
        const string preparationId = "fedcba9876543210fedcba9876543210";
        var runningSession = new RecordingRunningSession();
        var launchService = new RecordingDebugLaunchService(runningSession);
        var commandLine = VbaDebugAdapterCommandLine.Create(
            new StandaloneVbaDebugAdapterStdioRunner(launchService),
            new RecordingVbaDevCapabilitiesProbe(
                new VbaDevCapabilitiesProbeResult(
                    0,
                    "{\"featureVersions\":{\"build.sourceSnapshot\":\"1.0\"}}",
                    string.Empty)));
        var launchArguments = CreateValidLaunchArguments();
        launchArguments["__vbaRestartPreparation"] = new
        {
            protocolVersion = 1,
            id = preparationId,
            generation = 0
        };
        using var standardInput = CreateDapInput(
            new { seq = 1, type = "request", command = "launch", arguments = launchArguments },
            new { seq = 2, type = "request", command = "configurationDone", arguments = new { } },
            new { seq = 10, type = "request", command = "restart", arguments = new { } },
            new
            {
                seq = 11,
                type = "request",
                command = "vba/restartPrepared",
                arguments = new
                {
                    sessionId,
                    restartRequestSequence = 10,
                    preparationId,
                    generation = 1,
                    success = false,
                    message = "Synthetic first preparation failure."
                }
            },
            new { seq = 9, type = "request", command = "restart", arguments = new { } },
            new { seq = 12, type = "request", command = "disconnect", arguments = new { } });
        using var standardOutput = new MemoryStream();

        _ = await commandLine.InvokeAsync(
            [
                "--stdio",
                "--vba-dev", Path.GetFullPath("vba-dev.exe"),
                "--session", sessionId
            ],
            standardInput,
            standardOutput,
            Stream.Null,
            CancellationToken.None);

        var response = Assert.Single(
            ReadDapMessages(standardOutput),
            message => message.TryGetProperty("request_seq", out var sequence) &&
                       sequence.GetInt32() == 9);
        Assert.False(response.GetProperty("success").GetBoolean());
        Assert.Contains(
            "sequence",
            response.GetProperty("message").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Single(launchService.Invocations);
        Assert.Equal(1, runningSession.TerminateCalls);
    }

    [Fact]
    public async Task OwnedSessionExitFailsAPendingRestartBeforeTermination()
    {
        const string sessionId = "0123456789abcdef0123456789abcdef";
        const string preparationId = "fedcba9876543210fedcba9876543210";
        var completion = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runningSession = new RecordingRunningSession(
            processId: 2718,
            completion: completion.Task);
        var launchService = new RecordingDebugLaunchService(runningSession);
        var commandLine = VbaDebugAdapterCommandLine.Create(
            new StandaloneVbaDebugAdapterStdioRunner(launchService),
            new RecordingVbaDevCapabilitiesProbe(
                new VbaDevCapabilitiesProbeResult(
                    0,
                    "{\"featureVersions\":{\"build.sourceSnapshot\":\"1.0\"}}",
                    string.Empty)));
        var launchArguments = CreateValidLaunchArguments();
        launchArguments["__vbaRestartPreparation"] = new
        {
            protocolVersion = 1,
            id = preparationId,
            generation = 0
        };
        using var inputPrefix = CreateDapInput(
            new { seq = 1, type = "request", command = "launch", arguments = launchArguments },
            new { seq = 2, type = "request", command = "configurationDone", arguments = new { } },
            new { seq = 3, type = "request", command = "restart", arguments = new { } });
        using var standardInput = new BlockingTailStream(inputPrefix.ToArray());
        using var standardOutput = new MemoryStream();
        var invocation = commandLine.InvokeAsync(
            [
                "--stdio",
                "--vba-dev", Path.GetFullPath("vba-dev.exe"),
                "--session", sessionId
            ],
            standardInput,
            standardOutput,
            Stream.Null,
            CancellationToken.None);

        try
        {
            Assert.True(SpinWait.SpinUntil(
                () => ReadUtf8(standardOutput).Contains(
                    "\"request_seq\":1",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(2)));
            completion.TrySetResult(23);
            Assert.Equal(0, await invocation.WaitAsync(TimeSpan.FromSeconds(2)));

            var messages = ReadDapMessages(standardOutput);
            var restartResponse = Assert.Single(
                messages,
                message => message.TryGetProperty("request_seq", out var sequence) &&
                           sequence.GetInt32() == 3);
            Assert.False(restartResponse.GetProperty("success").GetBoolean());
            Assert.Contains(
                "exited",
                restartResponse.GetProperty("message").GetString(),
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                ["output", "exited", "terminated"],
                messages
                    .Where(message => message.TryGetProperty("event", out var eventName) &&
                                      eventName.GetString() is "output" or "exited" or "terminated")
                    .Select(message => message.GetProperty("event").GetString()));
            Assert.Equal(0, runningSession.TerminateCalls);
            Assert.Equal(1, runningSession.DisposeCalls);
        }
        finally
        {
            standardInput.Complete();
            if (!invocation.IsCompleted)
            {
                _ = await invocation;
            }
        }
    }

    [Fact]
    public async Task OwnedSessionExitDuringFreshValidationCannotResurrectAWorkbook()
    {
        const string sessionId = "0123456789abcdef0123456789abcdef";
        const string preparationId = "fedcba9876543210fedcba9876543210";
        var completion = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var oldSession = new RecordingRunningSession(completion: completion.Task);
        var freshSession = new RecordingRunningSession();
        var launchService = new SequencedDebugLaunchService(
            [oldSession, freshSession],
            [],
            request =>
            {
                if (request.RestartPreparation?.Generation == 1)
                {
                    completion.TrySetResult(23);
                }
            });
        var commandLine = VbaDebugAdapterCommandLine.Create(
            new StandaloneVbaDebugAdapterStdioRunner(launchService),
            new RecordingVbaDevCapabilitiesProbe(
                new VbaDevCapabilitiesProbeResult(
                    0,
                    "{\"featureVersions\":{\"build.sourceSnapshot\":\"1.0\"}}",
                    string.Empty)));
        var initialLaunch = CreateValidLaunchArguments();
        initialLaunch["__vbaRestartPreparation"] = new
        {
            protocolVersion = 1,
            id = preparationId,
            generation = 0
        };
        var freshLaunch = CreateValidLaunchArguments();
        freshLaunch["__vbaRestartPreparation"] = new
        {
            protocolVersion = 1,
            id = preparationId,
            generation = 1
        };
        using var inputPrefix = CreateDapInput(
            new { seq = 1, type = "request", command = "launch", arguments = initialLaunch },
            new { seq = 2, type = "request", command = "configurationDone", arguments = new { } },
            new { seq = 3, type = "request", command = "restart", arguments = new { } },
            new
            {
                seq = 4,
                type = "request",
                command = "vba/restartPrepared",
                arguments = new
                {
                    sessionId,
                    restartRequestSequence = 3,
                    preparationId,
                    generation = 1,
                    success = true,
                    launch = freshLaunch
                }
            });
        using var standardInput = new BlockingTailStream(inputPrefix.ToArray());
        using var standardOutput = new MemoryStream();
        var invocation = commandLine.InvokeAsync(
            [
                "--stdio",
                "--vba-dev", Path.GetFullPath("vba-dev.exe"),
                "--session", sessionId
            ],
            standardInput,
            standardOutput,
            Stream.Null,
            CancellationToken.None);

        try
        {
            Assert.True(SpinWait.SpinUntil(
                () => ReadUtf8(standardOutput).Contains(
                    "\"request_seq\":4",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(2)));
            var messages = ReadDapMessages(standardOutput);
            Assert.True(Assert.Single(
                messages,
                message => message.TryGetProperty("request_seq", out var sequence) &&
                           sequence.GetInt32() == 4).GetProperty("success").GetBoolean());
            var restartResponse = Assert.Single(
                messages,
                message => message.TryGetProperty("request_seq", out var sequence) &&
                           sequence.GetInt32() == 3);
            Assert.False(restartResponse.GetProperty("success").GetBoolean());
            Assert.Contains(
                "exited",
                restartResponse.GetProperty("message").GetString(),
                StringComparison.OrdinalIgnoreCase);
            Assert.Single(launchService.Invocations);
        }
        finally
        {
            standardInput.Complete();
            _ = await invocation;
        }
    }

    [Fact]
    public async Task OwnedSessionExitWhileAcknowledgingFreshPreparationCannotResurrectAWorkbook()
    {
        const string sessionId = "0123456789abcdef0123456789abcdef";
        const string preparationId = "fedcba9876543210fedcba9876543210";
        var completion = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var oldSession = new RecordingRunningSession(completion: completion.Task);
        var freshSession = new RecordingRunningSession();
        var launchService = new SequencedDebugLaunchService(
            [oldSession, freshSession],
            []);
        var commandLine = VbaDebugAdapterCommandLine.Create(
            new StandaloneVbaDebugAdapterStdioRunner(launchService),
            new RecordingVbaDevCapabilitiesProbe(
                new VbaDevCapabilitiesProbeResult(
                    0,
                    "{\"featureVersions\":{\"build.sourceSnapshot\":\"1.0\"}}",
                    string.Empty)));
        var initialLaunch = CreateValidLaunchArguments();
        initialLaunch["__vbaRestartPreparation"] = new
        {
            protocolVersion = 1,
            id = preparationId,
            generation = 0
        };
        var freshLaunch = CreateValidLaunchArguments();
        freshLaunch["__vbaRestartPreparation"] = new
        {
            protocolVersion = 1,
            id = preparationId,
            generation = 1
        };
        using var inputPrefix = CreateDapInput(
            new { seq = 1, type = "request", command = "launch", arguments = initialLaunch },
            new { seq = 2, type = "request", command = "configurationDone", arguments = new { } },
            new { seq = 3, type = "request", command = "restart", arguments = new { } },
            new
            {
                seq = 4,
                type = "request",
                command = "vba/restartPrepared",
                arguments = new
                {
                    sessionId,
                    restartRequestSequence = 3,
                    preparationId,
                    generation = 1,
                    success = true,
                    launch = freshLaunch
                }
            });
        using var standardInput = new BlockingTailStream(inputPrefix.ToArray());
        using var standardOutput = new GatedDapResponseStream(requestSequence: 4);
        var invocation = commandLine.InvokeAsync(
            [
                "--stdio",
                "--vba-dev", Path.GetFullPath("vba-dev.exe"),
                "--session", sessionId
            ],
            standardInput,
            standardOutput,
            Stream.Null,
            CancellationToken.None);

        try
        {
            await standardOutput.ResponseWriteStarted.WaitAsync(TimeSpan.FromSeconds(2));
            completion.TrySetResult(23);
            standardOutput.ReleaseResponseWrite();
            Assert.True(SpinWait.SpinUntil(
                () => ReadUtf8(standardOutput).Contains(
                    "\"request_seq\":3",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(2)));

            var messages = ReadDapMessages(standardOutput);
            Assert.True(Assert.Single(
                messages,
                message => message.TryGetProperty("request_seq", out var sequence) &&
                           sequence.GetInt32() == 4).GetProperty("success").GetBoolean());
            var restartResponse = Assert.Single(
                messages,
                message => message.TryGetProperty("request_seq", out var sequence) &&
                           sequence.GetInt32() == 3);
            Assert.False(restartResponse.GetProperty("success").GetBoolean());
            Assert.Contains(
                "exited",
                restartResponse.GetProperty("message").GetString(),
                StringComparison.OrdinalIgnoreCase);
            Assert.Single(launchService.Invocations);
        }
        finally
        {
            standardOutput.ReleaseResponseWrite();
            standardInput.Complete();
            _ = await invocation;
        }
    }

    [Fact]
    public async Task StandaloneLaunchTransportsAnEmptyBase64Source()
    {
        var probe = new RecordingVbaDevCapabilitiesProbe(
            new VbaDevCapabilitiesProbeResult(
                0,
                "{\"featureVersions\":{\"build.sourceSnapshot\":\"1.0\"}}",
                string.Empty));
        var launchService = new RecordingDebugLaunchService();
        var commandLine = VbaDebugAdapterCommandLine.Create(
            new StandaloneVbaDebugAdapterStdioRunner(launchService),
            probe);
        using var standardInput = CreateDapInput(
            new
            {
                seq = 1,
                type = "request",
                command = "launch",
                arguments = new
                {
                    project = Path.GetFullPath("project"),
                    document = "Book1",
                    module = "Module1",
                    procedure = "Run",
                    __vbaDebugWorkbookFileName = "Book1.xlsm",
                    sourceSnapshot = new
                    {
                        schemaVersion = 1,
                        sources = new[]
                        {
                            new
                            {
                                relativePath = "Module1.bas",
                                sourceUri = "file:///C:/persistent/Module1.bas",
                                encoding = "utf8",
                                contentBase64 = string.Empty
                            }
                        }
                    }
                }
            },
            new { seq = 2, type = "request", command = "configurationDone", arguments = new { } });
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();

        var exitCode = await commandLine.InvokeAsync(
            [
                "--stdio",
                "--vba-dev", Path.GetFullPath("vba-dev.exe"),
                "--session", "0123456789abcdef0123456789abcdef"
            ],
            standardInput,
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        var invocation = Assert.Single(launchService.Invocations);
        Assert.Equal(
            string.Empty,
            Assert.Single(invocation.Request.SourceSnapshot.Sources).ContentBase64);
        var launchResponse = Assert.Single(
            ReadDapMessages(standardOutput),
            message => message.TryGetProperty("request_seq", out var sequence) &&
                       sequence.GetInt32() == 1);
        Assert.True(launchResponse.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task StandaloneLaunchAcceptsACompleteSnapshotBeyondFourMegabytes()
    {
        var probe = new RecordingVbaDevCapabilitiesProbe(
            new VbaDevCapabilitiesProbeResult(
                0,
                "{\"featureVersions\":{\"build.sourceSnapshot\":\"1.0\"}}",
                string.Empty));
        var launchService = new RecordingDebugLaunchService();
        var commandLine = VbaDebugAdapterCommandLine.Create(
            new StandaloneVbaDebugAdapterStdioRunner(launchService),
            probe);
        var formSidecarBase64 = Convert.ToBase64String(new byte[3_200_000]);
        using var standardInput = CreateDapInput(
            new
            {
                seq = 1,
                type = "request",
                command = "launch",
                arguments = new
                {
                    project = Path.GetFullPath("project"),
                    document = "Book1",
                    module = "Module1",
                    procedure = "Run",
                    __vbaDebugWorkbookFileName = "Book1.xlsm",
                    sourceSnapshot = new
                    {
                        schemaVersion = 1,
                        sources = new object[]
                        {
                            new
                            {
                                relativePath = "Form1.frx",
                                contentBase64 = formSidecarBase64
                            },
                            new
                            {
                                relativePath = "Module1.bas",
                                sourceUri = "file:///C:/persistent/Module1.bas",
                                encoding = "utf8",
                                contentBase64 = Convert.ToBase64String(
                                    Encoding.UTF8.GetBytes("Attribute VB_Name = \"Module1\"\r\n"))
                            }
                        }
                    }
                }
            },
            new { seq = 2, type = "request", command = "configurationDone", arguments = new { } });
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();

        var exitCode = await commandLine.InvokeAsync(
            [
                "--stdio",
                "--vba-dev", Path.GetFullPath("vba-dev.exe"),
                "--session", "0123456789abcdef0123456789abcdef"
            ],
            standardInput,
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        var invocation = Assert.Single(launchService.Invocations);
        Assert.Equal("Form1.frx", invocation.Request.SourceSnapshot.Sources[0].RelativePath);
        Assert.Equal(formSidecarBase64.Length, invocation.Request.SourceSnapshot.Sources[0].ContentBase64.Length);
    }

    [Fact]
    public Task StandaloneLaunchRejectsArgsBeforeInvokingTheLaunchService()
        => AssertUnsupportedLaunchFieldRejectedAsync("args", Array.Empty<string>());

    [Fact]
    public Task StandaloneLaunchRejectsNoBuildBeforeInvokingTheLaunchService()
        => AssertUnsupportedLaunchFieldRejectedAsync("noBuild", true);

    [Fact]
    public Task StandaloneLaunchRejectsStopOnEntryBeforeInvokingTheLaunchService()
        => AssertUnsupportedLaunchFieldRejectedAsync("stopOnEntry", true);

    [Fact]
    public Task StandaloneLaunchRejectsRunWithoutDebuggingBeforeInvokingTheLaunchService()
        => AssertUnsupportedLaunchFieldRejectedAsync("noDebug", true);

    [Fact]
    public Task StandaloneLaunchRejectsMalformedNoDebugBeforeInvokingTheLaunchService()
        => AssertUnsupportedLaunchFieldRejectedAsync("noDebug", "false");

    [Fact]
    public Task StandaloneLaunchRejectsAnUnknownSourceSnapshotProperty()
    {
        var arguments = CreateValidLaunchArguments();
        var snapshot = Assert.IsType<Dictionary<string, object?>>(arguments["sourceSnapshot"]);
        snapshot["unexpected"] = true;
        return AssertLaunchArgumentsRejectedAsync(arguments, "sourceSnapshot.unexpected");
    }

    [Fact]
    public Task StandaloneLaunchRejectsAnUnknownTransportedSourceProperty()
    {
        var arguments = CreateValidLaunchArguments();
        var snapshot = Assert.IsType<Dictionary<string, object?>>(arguments["sourceSnapshot"]);
        var sources = Assert.IsType<Dictionary<string, object?>[]>(snapshot["sources"]);
        Assert.Single(sources)["unexpected"] = true;
        return AssertLaunchArgumentsRejectedAsync(
            arguments,
            "sourceSnapshot.sources[].unexpected");
    }

    [Fact]
    public Task StandaloneLaunchRejectsAnUnknownActiveSourceProperty()
    {
        var arguments = CreateValidLaunchArguments();
        var snapshot = Assert.IsType<Dictionary<string, object?>>(arguments["sourceSnapshot"]);
        snapshot["activeSource"] = new Dictionary<string, object?>
        {
            ["sourceUri"] = "file:///C:/persistent/Module1.bas",
            ["line"] = 0,
            ["character"] = 0,
            ["unexpected"] = true
        };
        return AssertLaunchArgumentsRejectedAsync(
            arguments,
            "sourceSnapshot.activeSource.unexpected");
    }

    [Fact]
    public Task StandaloneLaunchRejectsAnUnknownTransportedBreakpointProperty()
    {
        var arguments = CreateValidLaunchArguments();
        var snapshot = Assert.IsType<Dictionary<string, object?>>(arguments["sourceSnapshot"]);
        snapshot["breakpoints"] = new[]
        {
            new Dictionary<string, object?>
            {
                ["sourceUri"] = "file:///C:/persistent/Module1.bas",
                ["line"] = 0,
                ["unexpected"] = true
            }
        };
        return AssertLaunchArgumentsRejectedAsync(
            arguments,
            "sourceSnapshot.breakpoints[].unexpected");
    }

    [Fact]
    public Task StandaloneLaunchRejectsAnUnknownTopLevelProperty()
    {
        var arguments = CreateValidLaunchArguments();
        arguments["unexpected"] = true;
        return AssertLaunchArgumentsRejectedAsync(arguments, "unexpected");
    }

    [Fact]
    public Task StandaloneLaunchReturnsDapFailureForAnInvalidAbsoluteProjectPath()
    {
        var arguments = CreateValidLaunchArguments();
        arguments["project"] = "C:\\invalid\0project";
        return AssertLaunchArgumentsRejectedAsync(arguments, "project");
    }

    [Fact]
    public Task StandaloneLaunchReturnsDapFailureForAnInvalidWorkbookFileName()
    {
        var arguments = CreateValidLaunchArguments();
        arguments["__vbaDebugWorkbookFileName"] = "Book\0.xlsm";
        return AssertLaunchArgumentsRejectedAsync(arguments, "workbook name");
    }

    [Fact]
    public async Task LaunchResponseTransportFailureTerminatesAndDisposesTheOwnedSession()
    {
        var probe = new RecordingVbaDevCapabilitiesProbe(
            new VbaDevCapabilitiesProbeResult(
                0,
                "{\"featureVersions\":{\"build.sourceSnapshot\":\"1.0\"}}",
                string.Empty));
        var runningSession = new RecordingRunningSession();
        var commandLine = VbaDebugAdapterCommandLine.Create(
            new StandaloneVbaDebugAdapterStdioRunner(
                new RecordingDebugLaunchService(runningSession)),
            probe);
        const string sourceUri = "file:///C:/persistent/DebugModule.bas";
        var contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            "Attribute VB_Name = \"DebugModule\"\r\nPublic Sub RunTarget()\r\nEnd Sub\r\n"));
        using var standardInput = CreateDapInput(
            new
            {
                seq = 1,
                type = "request",
                command = "launch",
                arguments = new
                {
                    project = Path.GetFullPath("project"),
                    document = "Book1",
                    module = "DebugModule",
                    procedure = "RunTarget",
                    __vbaDebugWorkbookFileName = "Book1.xlsm",
                    sourceSnapshot = new
                    {
                        schemaVersion = 1,
                        sources = new[]
                        {
                            new
                            {
                                relativePath = "DebugModule.bas",
                                sourceUri,
                                encoding = "utf8",
                                contentBase64
                            }
                        },
                        breakpoints = Array.Empty<object>()
                    }
                }
            },
            new
            {
                seq = 2,
                type = "request",
                command = "configurationDone",
                arguments = new { }
            });
        using var standardOutput = new DapResponseFailingStream("launch");
        using var standardError = new MemoryStream();

        await Assert.ThrowsAsync<IOException>(() => commandLine.InvokeAsync(
            [
                "--stdio",
                "--vba-dev", Path.GetFullPath("vba-dev.exe"),
                "--session", "0123456789abcdef0123456789abcdef"
            ],
            standardInput,
            standardOutput,
            standardError,
            CancellationToken.None));

        Assert.Equal(1, runningSession.TerminateCalls);
        Assert.Equal(1, runningSession.DisposeCalls);
    }

    [Theory]
    [InlineData("disconnect")]
    [InlineData("terminate")]
    public async Task StandaloneStopRequestTerminatesTheOwnedDebugSession(string stopCommand)
    {
        var probe = new RecordingVbaDevCapabilitiesProbe(
            new VbaDevCapabilitiesProbeResult(
                0,
                "{\"featureVersions\":{\"build.sourceSnapshot\":\"1.0\"}}",
                string.Empty));
        var runningSession = new RecordingRunningSession();
        var launchService = new RecordingDebugLaunchService(runningSession);
        var commandLine = VbaDebugAdapterCommandLine.Create(
            new StandaloneVbaDebugAdapterStdioRunner(launchService),
            probe);
        var contentBase64 = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(
                "Attribute VB_Name = \"Module1\"\r\nPublic Sub Run()\r\nEnd Sub\r\n"));
        using var standardInput = CreateDapInput(
            new
            {
                seq = 1,
                type = "request",
                command = "launch",
                arguments = new
                {
                    project = Path.GetFullPath("project"),
                    document = "Book1",
                    module = "Module1",
                    procedure = "Run",
                    __vbaDebugWorkbookFileName = "Book1.xlsm",
                    sourceSnapshot = new
                    {
                        schemaVersion = 1,
                        sources = new[]
                        {
                            new
                            {
                                relativePath = "Module1.bas",
                                sourceUri = "file:///C:/persistent/Module1.bas",
                                encoding = "utf8",
                                contentBase64
                            }
                        }
                    }
                }
            },
            new { seq = 2, type = "request", command = "configurationDone", arguments = new { } },
            new { seq = 3, type = "request", command = stopCommand, arguments = new { } });
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();

        var exitCode = await commandLine.InvokeAsync(
            [
                "--stdio",
                "--vba-dev", Path.GetFullPath("vba-dev.exe"),
                "--session", "0123456789abcdef0123456789abcdef"
            ],
            standardInput,
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, runningSession.TerminateCalls);
        Assert.Equal(1, runningSession.DisposeCalls);
        var stopResponse = Assert.Single(
            ReadDapMessages(standardOutput),
            message => message.TryGetProperty("request_seq", out var sequence) &&
                       sequence.GetInt32() == 3);
        Assert.True(stopResponse.GetProperty("success").GetBoolean());
    }

    [Theory]
    [InlineData("disconnect")]
    [InlineData("terminate")]
    public async Task StandaloneStopRequestCancelsAnInFlightLaunch(string stopCommand)
    {
        var probe = new RecordingVbaDevCapabilitiesProbe(
            new VbaDevCapabilitiesProbeResult(
                0,
                "{\"featureVersions\":{\"build.sourceSnapshot\":\"1.0\"}}",
                string.Empty));
        var launchService = new CancellationAwareDebugLaunchService();
        var commandLine = VbaDebugAdapterCommandLine.Create(
            new StandaloneVbaDebugAdapterStdioRunner(launchService),
            probe);
        var contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            "Attribute VB_Name = \"Module1\"\r\nPublic Sub Run()\r\nEnd Sub\r\n"));
        using var standardInput = CreateDapInput(
            new
            {
                seq = 1,
                type = "request",
                command = "launch",
                arguments = new
                {
                    project = Path.GetFullPath("project"),
                    document = "Book1",
                    module = "Module1",
                    procedure = "Run",
                    __vbaDebugWorkbookFileName = "Book1.xlsm",
                    sourceSnapshot = new
                    {
                        schemaVersion = 1,
                        sources = new[]
                        {
                            new
                            {
                                relativePath = "Module1.bas",
                                sourceUri = "file:///C:/persistent/Module1.bas",
                                encoding = "utf8",
                                contentBase64
                            }
                        }
                    }
                }
            },
            new { seq = 2, type = "request", command = "configurationDone", arguments = new { } },
            new { seq = 3, type = "request", command = stopCommand, arguments = new { } });
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var exitCode = await commandLine.InvokeAsync(
            [
                "--stdio",
                "--vba-dev", Path.GetFullPath("vba-dev.exe"),
                "--session", "0123456789abcdef0123456789abcdef"
            ],
            standardInput,
            standardOutput,
            standardError,
            timeout.Token);

        Assert.Equal(0, exitCode);
        Assert.True(launchService.CancellationObserved.Task.IsCompletedSuccessfully);
        var messages = ReadDapMessages(standardOutput);
        var launchResponse = Assert.Single(
            messages,
            message => message.TryGetProperty("request_seq", out var sequence) &&
                       sequence.GetInt32() == 1);
        Assert.False(launchResponse.GetProperty("success").GetBoolean());
        Assert.Contains(
            "cancelled",
            launchResponse.GetProperty("message").GetString(),
            StringComparison.OrdinalIgnoreCase);
        var cancellationOutput = Assert.Single(
            messages,
            message => message.TryGetProperty("event", out var eventName) &&
                       eventName.GetString() == "output");
        Assert.Contains(
            "cancelled",
            cancellationOutput.GetProperty("body").GetProperty("output").GetString(),
            StringComparison.OrdinalIgnoreCase);
        var stopResponse = Assert.Single(
            messages,
            message => message.TryGetProperty("request_seq", out var sequence) &&
                       sequence.GetInt32() == 3);
        Assert.True(stopResponse.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task CompletedOwnedSessionEmitsOutputExitedThenBodylessTerminated()
    {
        var probe = new RecordingVbaDevCapabilitiesProbe(
            new VbaDevCapabilitiesProbeResult(
                0,
                "{\"featureVersions\":{\"build.sourceSnapshot\":\"1.0\"}}",
                string.Empty));
        var runningSession = new RecordingRunningSession(
            processId: 2718,
            completion: Task.FromResult(7));
        var commandLine = VbaDebugAdapterCommandLine.Create(
            new StandaloneVbaDebugAdapterStdioRunner(
                new RecordingDebugLaunchService(runningSession)),
            probe);
        var contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            "Attribute VB_Name = \"Module1\"\r\nPublic Sub Run()\r\nEnd Sub\r\n"));
        using var inputPrefix = CreateDapInput(
            new
            {
                seq = 1,
                type = "request",
                command = "launch",
                arguments = new
                {
                    project = Path.GetFullPath("project"),
                    document = "Book1",
                    module = "Module1",
                    procedure = "Run",
                    __vbaDebugWorkbookFileName = "Book1.xlsm",
                    sourceSnapshot = new
                    {
                        schemaVersion = 1,
                        sources = new[]
                        {
                            new
                            {
                                relativePath = "Module1.bas",
                                sourceUri = "file:///C:/persistent/Module1.bas",
                                encoding = "utf8",
                                contentBase64
                            }
                        }
                    }
                }
            },
            new { seq = 2, type = "request", command = "configurationDone", arguments = new { } });
        using var standardInput = new BlockingTailStream(inputPrefix.ToArray());
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();

        var invocation = commandLine.InvokeAsync(
            [
                "--stdio",
                "--vba-dev", Path.GetFullPath("vba-dev.exe"),
                "--session", "0123456789abcdef0123456789abcdef"
            ],
            standardInput,
            standardOutput,
            standardError,
            CancellationToken.None);
        int exitCode;
        try
        {
            exitCode = await invocation.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            standardInput.Complete();
            if (!invocation.IsCompleted)
            {
                _ = await invocation;
            }
        }

        Assert.Equal(0, exitCode);
        var terminalEvents = ReadDapMessages(standardOutput)
            .Where(message =>
                message.TryGetProperty("event", out var eventName) &&
                eventName.GetString() is "output" or "exited" or "terminated")
            .ToArray();
        Assert.Equal(
            ["output", "exited", "terminated"],
            terminalEvents.Select(message => message.GetProperty("event").GetString()));
        Assert.Equal(
            "console",
            terminalEvents[0].GetProperty("body").GetProperty("category").GetString());
        Assert.Contains(
            "Owned Excel process 2718 exited with code 7.",
            terminalEvents[0].GetProperty("body").GetProperty("output").GetString(),
            StringComparison.Ordinal);
        Assert.Equal(
            7,
            terminalEvents[1].GetProperty("body").GetProperty("exitCode").GetInt32());
        Assert.False(terminalEvents[2].TryGetProperty("body", out _));
        Assert.Equal(0, runningSession.TerminateCalls);
        Assert.Equal(1, runningSession.DisposeCalls);
    }

    [Fact]
    public async Task NativeInputWaitEmitsConsoleOutput()
    {
        var probe = new RecordingVbaDevCapabilitiesProbe(
            new VbaDevCapabilitiesProbeResult(
                0,
                "{\"featureVersions\":{\"build.sourceSnapshot\":\"1.0\"}}",
                string.Empty));
        var launchService = new RecordingDebugLaunchService(
            inputWait: new DebugInputWait(
                DebugInputWaitKind.Excel,
                DebugInputWaitPhase.WorkbookOpen,
                2720));
        var commandLine = VbaDebugAdapterCommandLine.Create(
            new StandaloneVbaDebugAdapterStdioRunner(launchService),
            probe);
        using var standardInput = CreateDapInput(
            new
            {
                seq = 1,
                type = "request",
                command = "launch",
                arguments = CreateValidLaunchArguments()
            },
            new { seq = 2, type = "request", command = "configurationDone", arguments = new { } });
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();

        var exitCode = await commandLine.InvokeAsync(
            [
                "--stdio",
                "--vba-dev", Path.GetFullPath("vba-dev.exe"),
                "--session", "0123456789abcdef0123456789abcdef"
            ],
            standardInput,
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        var output = Assert.Single(
            ReadDapMessages(standardOutput),
            message => message.TryGetProperty("event", out var eventName) &&
                       eventName.GetString() == "output");
        Assert.Equal("console", output.GetProperty("body").GetProperty("category").GetString());
        Assert.Contains(
            "Owned Excel process 2720 is waiting for Excel input while opening the generated workbook.",
            output.GetProperty("body").GetProperty("output").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaunchSetupFailureEmitsImportantOutputAndBodylessTermination()
    {
        var probe = new RecordingVbaDevCapabilitiesProbe(
            new VbaDevCapabilitiesProbeResult(
                0,
                "{\"featureVersions\":{\"build.sourceSnapshot\":\"1.0\"}}",
                string.Empty));
        var commandLine = VbaDebugAdapterCommandLine.Create(
            new StandaloneVbaDebugAdapterStdioRunner(
                new FailingDebugLaunchService(
                    new DebugSetupException("Synthetic workbook setup failure."))),
            probe);
        using var standardInput = CreateDapInput(
            new
            {
                seq = 1,
                type = "request",
                command = "launch",
                arguments = CreateValidLaunchArguments()
            },
            new { seq = 2, type = "request", command = "configurationDone", arguments = new { } });
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();

        var exitCode = await commandLine.InvokeAsync(
            [
                "--stdio",
                "--vba-dev", Path.GetFullPath("vba-dev.exe"),
                "--session", "0123456789abcdef0123456789abcdef"
            ],
            standardInput,
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        var messages = ReadDapMessages(standardOutput);
        var output = Assert.Single(
            messages,
            message => message.TryGetProperty("event", out var eventName) &&
                       eventName.GetString() == "output");
        Assert.Equal("important", output.GetProperty("body").GetProperty("category").GetString());
        Assert.Contains(
            "DebugSetupError: Synthetic workbook setup failure.",
            output.GetProperty("body").GetProperty("output").GetString(),
            StringComparison.Ordinal);
        var launchResponse = Assert.Single(
            messages,
            message => message.TryGetProperty("request_seq", out var sequence) &&
                       sequence.GetInt32() == 1);
        Assert.False(launchResponse.GetProperty("success").GetBoolean());
        var terminated = Assert.Single(
            messages,
            message => message.TryGetProperty("event", out var eventName) &&
                       eventName.GetString() == "terminated");
        Assert.False(terminated.TryGetProperty("body", out _));
    }

    [Fact]
    public async Task SetBreakpointsReturnsAnUnverifiedOrdinaryLineBeforeLaunch()
    {
        var probe = new RecordingVbaDevCapabilitiesProbe(
            new VbaDevCapabilitiesProbeResult(
                0,
                "{\"featureVersions\":{\"build.sourceSnapshot\":\"1.0\"}}",
                string.Empty));
        var commandLine = VbaDebugAdapterCommandLine.Create(
            new StandaloneVbaDebugAdapterStdioRunner(new RecordingDebugLaunchService()),
            probe);
        var sourcePath = Path.GetFullPath("persistent/Module1.bas");
        using var standardInput = CreateDapInput(new
        {
            seq = 1,
            type = "request",
            command = "setBreakpoints",
            arguments = new
            {
                source = new { path = sourcePath },
                breakpoints = new[] { new { line = 3 } }
            }
        });
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();

        var exitCode = await commandLine.InvokeAsync(
            [
                "--stdio",
                "--vba-dev", Path.GetFullPath("vba-dev.exe"),
                "--session", "0123456789abcdef0123456789abcdef"
            ],
            standardInput,
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        var response = Assert.Single(ReadDapMessages(standardOutput));
        Assert.True(response.GetProperty("success").GetBoolean());
        var breakpoint = Assert.Single(
            response.GetProperty("body").GetProperty("breakpoints").EnumerateArray());
        Assert.False(breakpoint.GetProperty("verified").GetBoolean());
        Assert.Equal(3, breakpoint.GetProperty("line").GetInt32());
        Assert.Equal(
            sourcePath,
            breakpoint.GetProperty("source").GetProperty("path").GetString());
    }

    [Fact]
    public async Task SetBreakpointsReturnsDapFailureForAnInvalidAbsoluteSourcePath()
    {
        var probe = new RecordingVbaDevCapabilitiesProbe(
            new VbaDevCapabilitiesProbeResult(
                0,
                "{\"featureVersions\":{\"build.sourceSnapshot\":\"1.0\"}}",
                string.Empty));
        var commandLine = VbaDebugAdapterCommandLine.Create(
            new StandaloneVbaDebugAdapterStdioRunner(new RecordingDebugLaunchService()),
            probe);
        using var standardInput = CreateDapInput(
            new
            {
                seq = 1,
                type = "request",
                command = "setBreakpoints",
                arguments = new
                {
                    source = new { path = "C:\\invalid\0Module1.bas" },
                    breakpoints = new[] { new { line = 3 } }
                }
            },
            new { seq = 2, type = "request", command = "threads", arguments = new { } });
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();

        var exitCode = await commandLine.InvokeAsync(
            [
                "--stdio",
                "--vba-dev", Path.GetFullPath("vba-dev.exe"),
                "--session", "0123456789abcdef0123456789abcdef"
            ],
            standardInput,
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        var responses = ReadDapMessages(standardOutput);
        var setBreakpointsResponse = Assert.Single(
            responses,
            response => response.TryGetProperty("request_seq", out var sequence) &&
                        sequence.GetInt32() == 1);
        Assert.False(setBreakpointsResponse.GetProperty("success").GetBoolean());
        Assert.Contains(
            "source path",
            setBreakpointsResponse.GetProperty("message").GetString(),
            StringComparison.Ordinal);
        var threadsResponse = Assert.Single(
            responses,
            response => response.TryGetProperty("request_seq", out var sequence) &&
                        sequence.GetInt32() == 2);
        Assert.True(threadsResponse.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task ThreadsReturnsTheOwnedVbeThread()
    {
        var probe = new RecordingVbaDevCapabilitiesProbe(
            new VbaDevCapabilitiesProbeResult(
                0,
                "{\"featureVersions\":{\"build.sourceSnapshot\":\"1.0\"}}",
                string.Empty));
        var commandLine = VbaDebugAdapterCommandLine.Create(
            new StandaloneVbaDebugAdapterStdioRunner(new RecordingDebugLaunchService()),
            probe);
        using var standardInput = CreateDapInput(new
        {
            seq = 1,
            type = "request",
            command = "threads",
            arguments = new { }
        });
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();

        var exitCode = await commandLine.InvokeAsync(
            [
                "--stdio",
                "--vba-dev", Path.GetFullPath("vba-dev.exe"),
                "--session", "0123456789abcdef0123456789abcdef"
            ],
            standardInput,
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        var response = Assert.Single(ReadDapMessages(standardOutput));
        Assert.True(response.GetProperty("success").GetBoolean());
        var thread = Assert.Single(
            response.GetProperty("body").GetProperty("threads").EnumerateArray());
        Assert.Equal(1, thread.GetProperty("id").GetInt32());
        Assert.Equal("VBE", thread.GetProperty("name").GetString());
    }

    [Fact]
    public async Task InScopeConditionalBreakpointFailsLaunchBeforeTheService()
    {
        var probe = new RecordingVbaDevCapabilitiesProbe(
            new VbaDevCapabilitiesProbeResult(
                0,
                "{\"featureVersions\":{\"build.sourceSnapshot\":\"1.0\"}}",
                string.Empty));
        var launchService = new RecordingDebugLaunchService();
        var commandLine = VbaDebugAdapterCommandLine.Create(
            new StandaloneVbaDebugAdapterStdioRunner(launchService),
            probe);
        using var standardInput = CreateDapInput(
            new
            {
                seq = 1,
                type = "request",
                command = "setBreakpoints",
                arguments = new
                {
                    source = new { path = new Uri("file:///C:/persistent/Module1.bas").LocalPath },
                    breakpoints = new[] { new { line = 3, condition = "value > 0" } }
                }
            },
            new
            {
                seq = 2,
                type = "request",
                command = "launch",
                arguments = CreateValidLaunchArguments()
            },
            new
            {
                seq = 3,
                type = "request",
                command = "configurationDone",
                arguments = new { }
            });
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();

        var exitCode = await commandLine.InvokeAsync(
            [
                "--stdio",
                "--vba-dev", Path.GetFullPath("vba-dev.exe"),
                "--session", "0123456789abcdef0123456789abcdef"
            ],
            standardInput,
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Empty(launchService.Invocations);
        var responses = ReadDapMessages(standardOutput);
        var setBreakpointsResponse = Assert.Single(
            responses,
            response => response.TryGetProperty("request_seq", out var sequence) &&
                        sequence.GetInt32() == 1);
        Assert.True(setBreakpointsResponse.GetProperty("success").GetBoolean());
        var launchResponse = Assert.Single(
            responses,
            response => response.TryGetProperty("request_seq", out var sequence) &&
                        sequence.GetInt32() == 2);
        Assert.False(launchResponse.GetProperty("success").GetBoolean());
        Assert.Contains(
            "conditional",
            launchResponse.GetProperty("message").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InScopeConditionalBreakpointAddedAfterLaunchStillFailsBeforeTheService()
    {
        var probe = new RecordingVbaDevCapabilitiesProbe(
            new VbaDevCapabilitiesProbeResult(
                0,
                "{\"featureVersions\":{\"build.sourceSnapshot\":\"1.0\"}}",
                string.Empty));
        var launchService = new RecordingDebugLaunchService();
        var commandLine = VbaDebugAdapterCommandLine.Create(
            new StandaloneVbaDebugAdapterStdioRunner(launchService),
            probe);
        using var standardInput = CreateDapInput(
            new
            {
                seq = 1,
                type = "request",
                command = "launch",
                arguments = CreateValidLaunchArguments()
            },
            new
            {
                seq = 2,
                type = "request",
                command = "setBreakpoints",
                arguments = new
                {
                    source = new { path = new Uri("file:///C:/persistent/Module1.bas").LocalPath },
                    breakpoints = new[] { new { line = 3, condition = "value > 0" } }
                }
            },
            new
            {
                seq = 3,
                type = "request",
                command = "configurationDone",
                arguments = new { }
            });
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();

        var exitCode = await commandLine.InvokeAsync(
            [
                "--stdio",
                "--vba-dev", Path.GetFullPath("vba-dev.exe"),
                "--session", "0123456789abcdef0123456789abcdef"
            ],
            standardInput,
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Empty(launchService.Invocations);
        var responses = ReadDapMessages(standardOutput);
        var setBreakpointsResponse = Assert.Single(
            responses,
            response => response.TryGetProperty("request_seq", out var sequence) &&
                        sequence.GetInt32() == 2);
        Assert.True(setBreakpointsResponse.GetProperty("success").GetBoolean());
        var launchResponse = Assert.Single(
            responses,
            response => response.TryGetProperty("request_seq", out var sequence) &&
                        sequence.GetInt32() == 1);
        Assert.False(launchResponse.GetProperty("success").GetBoolean());
        Assert.Contains(
            "conditional",
            launchResponse.GetProperty("message").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MalformedSnapshotSourceUriFailsLaunchWithoutStoppingTheAdapter()
    {
        var arguments = CreateValidLaunchArguments();
        var sourceSnapshot = Assert.IsType<Dictionary<string, object?>>(
            arguments["sourceSnapshot"]);
        var sources = Assert.IsType<Dictionary<string, object?>[]>(
            sourceSnapshot["sources"]);
        sources[0]["sourceUri"] = "http://[";

        await AssertLaunchArgumentsRejectedAsync(arguments, "persistent file URI");
    }

    [Fact]
    public async Task EncodedNullSnapshotSourceUriFailsLaunchWithoutStoppingTheAdapter()
    {
        var arguments = CreateValidLaunchArguments();
        var sourceSnapshot = Assert.IsType<Dictionary<string, object?>>(
            arguments["sourceSnapshot"]);
        var sources = Assert.IsType<Dictionary<string, object?>[]>(
            sourceSnapshot["sources"]);
        sources[0]["sourceUri"] = "file:///C:/persistent/Mod%00ule1.bas";

        await AssertLaunchArgumentsRejectedAsync(arguments, "persistent file URI");
    }

    [Fact]
    public async Task OutOfScopeConditionalBreakpointDoesNotBlockLaunch()
    {
        var probe = new RecordingVbaDevCapabilitiesProbe(
            new VbaDevCapabilitiesProbeResult(
                0,
                "{\"featureVersions\":{\"build.sourceSnapshot\":\"1.0\"}}",
                string.Empty));
        var launchService = new RecordingDebugLaunchService();
        var commandLine = VbaDebugAdapterCommandLine.Create(
            new StandaloneVbaDebugAdapterStdioRunner(launchService),
            probe);
        var launchArguments = CreateValidLaunchArguments();
        using var standardInput = CreateDapInput(
            new
            {
                seq = 1,
                type = "request",
                command = "setBreakpoints",
                arguments = new
                {
                    source = new { path = Path.GetFullPath("outside/Other.bas") },
                    breakpoints = new[] { new { line = 3, condition = "value > 0" } }
                }
            },
            new
            {
                seq = 2,
                type = "request",
                command = "launch",
                arguments = launchArguments
            },
            new
            {
                seq = 3,
                type = "request",
                command = "configurationDone",
                arguments = new { }
            });
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();

        var exitCode = await commandLine.InvokeAsync(
            [
                "--stdio",
                "--vba-dev", Path.GetFullPath("vba-dev.exe"),
                "--session", "0123456789abcdef0123456789abcdef"
            ],
            standardInput,
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Single(launchService.Invocations);
        var responses = ReadDapMessages(standardOutput)
            .Where(message => message.TryGetProperty("request_seq", out _))
            .ToArray();
        Assert.True(Assert.Single(
            responses,
            response => response.GetProperty("request_seq").GetInt32() == 1)
            .GetProperty("success").GetBoolean());
        Assert.True(Assert.Single(
            responses,
            response => response.GetProperty("request_seq").GetInt32() == 2)
            .GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task EmptyUnsupportedBreakpointCategoriesRemainValidDapSetupRequests()
    {
        var probe = new RecordingVbaDevCapabilitiesProbe(
            new VbaDevCapabilitiesProbeResult(
                0,
                "{\"featureVersions\":{\"build.sourceSnapshot\":\"1.0\"}}",
                string.Empty));
        var commandLine = VbaDebugAdapterCommandLine.Create(
            new StandaloneVbaDebugAdapterStdioRunner(new RecordingDebugLaunchService()),
            probe);
        using var standardInput = CreateDapInput(
            new
            {
                seq = 1,
                type = "request",
                command = "setFunctionBreakpoints",
                arguments = new { breakpoints = Array.Empty<object>() }
            },
            new
            {
                seq = 2,
                type = "request",
                command = "setExceptionBreakpoints",
                arguments = new { filters = Array.Empty<string>() }
            },
            new
            {
                seq = 3,
                type = "request",
                command = "setDataBreakpoints",
                arguments = new { breakpoints = Array.Empty<object>() }
            });
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();

        var exitCode = await commandLine.InvokeAsync(
            [
                "--stdio",
                "--vba-dev", Path.GetFullPath("vba-dev.exe"),
                "--session", "0123456789abcdef0123456789abcdef"
            ],
            standardInput,
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        var responses = ReadDapMessages(standardOutput);
        Assert.Equal(3, responses.Count);
        Assert.All(responses, response =>
        {
            Assert.True(response.GetProperty("success").GetBoolean());
            Assert.Empty(response.GetProperty("body").GetProperty("breakpoints").EnumerateArray());
        });
    }

    [Fact]
    public async Task ConfiguredFunctionBreakpointFailsLaunchBeforeTheService()
    {
        var probe = new RecordingVbaDevCapabilitiesProbe(
            new VbaDevCapabilitiesProbeResult(
                0,
                "{\"featureVersions\":{\"build.sourceSnapshot\":\"1.0\"}}",
                string.Empty));
        var launchService = new RecordingDebugLaunchService();
        var commandLine = VbaDebugAdapterCommandLine.Create(
            new StandaloneVbaDebugAdapterStdioRunner(launchService),
            probe);
        using var standardInput = CreateDapInput(
            new
            {
                seq = 1,
                type = "request",
                command = "setFunctionBreakpoints",
                arguments = new { breakpoints = new[] { new { name = "RunTarget" } } }
            },
            new
            {
                seq = 2,
                type = "request",
                command = "launch",
                arguments = CreateValidLaunchArguments()
            },
            new
            {
                seq = 3,
                type = "request",
                command = "configurationDone",
                arguments = new { }
            });
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();

        var exitCode = await commandLine.InvokeAsync(
            [
                "--stdio",
                "--vba-dev", Path.GetFullPath("vba-dev.exe"),
                "--session", "0123456789abcdef0123456789abcdef"
            ],
            standardInput,
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Empty(launchService.Invocations);
        var launchResponse = Assert.Single(
            ReadDapMessages(standardOutput),
            response => response.TryGetProperty("request_seq", out var sequence) &&
                        sequence.GetInt32() == 2);
        Assert.False(launchResponse.GetProperty("success").GetBoolean());
        Assert.Contains(
            "function",
            launchResponse.GetProperty("message").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LaunchEmitsVerifiedBreakpointEventAfterNativeTransfer()
    {
        const string sourceUri = "file:///C:/persistent/Module1.bas";
        var mappedBreakpoint = new VbeBreakpoint(
            new DebugSourceBreakpoint(sourceUri, 2),
            new VbeCodeModuleSourceMap(
                "Module1",
                VbaLanguageServer.Syntax.VbaModuleKind.StandardModule,
                ["Public Sub Run()", "    Debug.Print \"break\"", "End Sub"]),
            2);
        var probe = new RecordingVbaDevCapabilitiesProbe(
            new VbaDevCapabilitiesProbeResult(
                0,
                "{\"featureVersions\":{\"build.sourceSnapshot\":\"1.0\"}}",
                string.Empty));
        var launchService = new RecordingDebugLaunchService(
            new RecordingRunningSession([mappedBreakpoint]));
        var commandLine = VbaDebugAdapterCommandLine.Create(
            new StandaloneVbaDebugAdapterStdioRunner(launchService),
            probe);
        var contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            "Attribute VB_Name = \"Module1\"\r\nPublic Sub Run()\r\n" +
            "    Debug.Print \"break\"\r\nEnd Sub\r\n"));
        var sourcePath = new Uri(sourceUri).LocalPath;
        using var standardInput = CreateDapInput(
            new
            {
                seq = 1,
                type = "request",
                command = "setBreakpoints",
                arguments = new
                {
                    source = new { path = sourcePath },
                    breakpoints = new[] { new { line = 3 } }
                }
            },
            new
            {
                seq = 2,
                type = "request",
                command = "launch",
                arguments = new
                {
                    project = Path.GetFullPath("project"),
                    document = "Book1",
                    module = "Module1",
                    procedure = "Run",
                    __vbaDebugWorkbookFileName = "Book1.xlsm",
                    sourceSnapshot = new
                    {
                        schemaVersion = 1,
                        sources = new[]
                        {
                            new
                            {
                                relativePath = "Module1.bas",
                                sourceUri,
                                encoding = "utf8",
                                contentBase64
                            }
                        },
                        breakpoints = new[] { new { sourceUri, line = 2 } }
                    }
                }
            },
            new { seq = 3, type = "request", command = "configurationDone", arguments = new { } });
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();

        var exitCode = await commandLine.InvokeAsync(
            [
                "--stdio",
                "--vba-dev", Path.GetFullPath("vba-dev.exe"),
                "--session", "0123456789abcdef0123456789abcdef"
            ],
            standardInput,
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        var messages = ReadDapMessages(standardOutput);
        var setBreakpointsResponse = Assert.Single(
            messages,
            message => message.TryGetProperty("request_seq", out var sequence) &&
                       sequence.GetInt32() == 1);
        var pendingBreakpoint = Assert.Single(
            setBreakpointsResponse.GetProperty("body")
                .GetProperty("breakpoints")
                .EnumerateArray());
        var breakpointId = pendingBreakpoint.GetProperty("id").GetInt32();
        var breakpointEvent = Assert.Single(
            messages,
            message => message.TryGetProperty("event", out var eventName) &&
                       eventName.GetString() == "breakpoint");
        var breakpoint = breakpointEvent.GetProperty("body").GetProperty("breakpoint");
        Assert.Equal(breakpointId, breakpoint.GetProperty("id").GetInt32());
        Assert.True(breakpoint.GetProperty("verified").GetBoolean());
        Assert.Equal(3, breakpoint.GetProperty("line").GetInt32());
        Assert.Equal(
            sourcePath,
            breakpoint.GetProperty("source").GetProperty("path").GetString());
    }

    private sealed class RecordingStdioRunner : IVbaDebugAdapterStdioRunner
    {
        public List<(string VbaDevPath, string SessionId)> Invocations { get; } = [];

        public Action<string>? OnRun { get; init; }

        public Task<int> RunAsync(
            string vbaDevPath,
            string sessionId,
            Stream standardInput,
            Stream standardOutput,
            Stream standardError,
            CancellationToken cancellationToken)
        {
            Invocations.Add((vbaDevPath, sessionId));
            OnRun?.Invoke(sessionId);
            return Task.FromResult(0);
        }
    }

    private sealed class RecordingDebugEnvironmentDoctor(
        DebugEnvironmentDiagnosticReport report) : IDebugEnvironmentDoctor
    {
        public int Invocations { get; private set; }

        public Task<DebugEnvironmentDiagnosticReport> RunAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Invocations++;
            return Task.FromResult(report);
        }
    }

    private sealed class ThrowingDebugEnvironmentDoctor(Exception error)
        : IDebugEnvironmentDoctor
    {
        public Task<DebugEnvironmentDiagnosticReport> RunAsync(
            CancellationToken cancellationToken)
            => Task.FromException<DebugEnvironmentDiagnosticReport>(error);
    }

    private sealed class AwaitingCancellationDebugEnvironmentDoctor
        : IDebugEnvironmentDoctor
    {
        public bool CancellationRequestedAtEntry { get; private set; }

        public bool CancellationObserved { get; private set; }

        public async Task<DebugEnvironmentDiagnosticReport> RunAsync(
            CancellationToken cancellationToken)
        {
            CancellationRequestedAtEntry = cancellationToken.IsCancellationRequested;
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved = true;
                throw;
            }

            throw new InvalidOperationException("An infinite delay completed unexpectedly.");
        }
    }

    private sealed class DelayedCancellationInspectionDoctor
        : IDebugEnvironmentDoctor
    {
        public bool CancellationObserved { get; private set; }

        public async Task<DebugEnvironmentDiagnosticReport> RunAsync(
            CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25));
            CancellationObserved = cancellationToken.IsCancellationRequested;
            return new DebugEnvironmentDiagnosticReport(
                "1.0",
                "0.1.0",
                DebugEnvironmentDiagnosticStatus.Pass,
                Complete: true,
                [new DebugEnvironmentDiagnosticCheck(
                    "platform.windows",
                    DebugEnvironmentDiagnosticStatus.Pass,
                    "Windows is available.",
                    DurationMilliseconds: 0)]);
        }
    }

    private static VbaDebugAdapterCommandLine CreateCommandLine(
        IDebugEnvironmentDoctor doctor)
        => VbaDebugAdapterCommandLine.Create(
            new RecordingStdioRunner(),
            new RecordingVbaDevCapabilitiesProbe(
                new VbaDevCapabilitiesProbeResult(0, string.Empty, string.Empty)),
            new RecordingSessionWorkspaceManager(),
            doctor);

    private sealed class RecordingCapabilitiesProcess(VbaDevBuildProcessResult result)
        : IVbaDevBuildProcess
    {
        public List<(string FileName, IReadOnlyList<string> Arguments)> Invocations { get; } = [];

        public Task<VbaDevBuildProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Invocations.Add((fileName, arguments));
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingSessionWorkspaceManager
        : IVbaDebugSessionWorkspaceManager
    {
        public List<string> CleanupInvocations { get; } = [];

        public ValueTask<IVbaDebugSessionWorkspaceLease> ClaimAsync(
            string sessionId,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("A cleanup test must not claim a session.");

        public ValueTask<VbaDebugSessionCleanupResult> CleanupAsync(
            string sessionId,
            CancellationToken cancellationToken)
        {
            CleanupInvocations.Add(sessionId);
            return ValueTask.FromResult(new VbaDebugSessionCleanupResult(
                Succeeded: true,
                RetainedPath: null,
                Message: null));
        }

        public ValueTask<IReadOnlyList<VbaDebugSessionCleanupResult>> ReapStaleAsync(
            string excludedSessionId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<VbaDebugSessionCleanupResult>>([]);
    }

    private sealed class FailingDisposeSessionWorkspaceManager(string retainedPath)
        : IVbaDebugSessionWorkspaceManager
    {
        public ValueTask<IVbaDebugSessionWorkspaceLease> ClaimAsync(
            string sessionId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<IVbaDebugSessionWorkspaceLease>(
                new FailingDisposeSessionWorkspaceLease(retainedPath));

        public ValueTask<VbaDebugSessionCleanupResult> CleanupAsync(
            string sessionId,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("The stdio test must not invoke cleanup.");

        public ValueTask<IReadOnlyList<VbaDebugSessionCleanupResult>> ReapStaleAsync(
            string excludedSessionId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<VbaDebugSessionCleanupResult>>([]);
    }

    private sealed class FailingDisposeSessionWorkspaceLease(string retainedPath)
        : IVbaDebugSessionWorkspaceLease
    {
        public string SessionWorkspacePath { get; } = retainedPath;

        public ValueTask DisposeAsync()
            => ValueTask.FromException(new IOException("Synthetic lease cleanup failure."));
    }

    private static string ReadUtf8(MemoryStream stream)
        => Encoding.UTF8.GetString(stream.ToArray());

    private static MemoryStream CreateDapInput(params object[] messages)
    {
        var stream = new MemoryStream();
        foreach (var message in messages)
        {
            var content = JsonSerializer.SerializeToUtf8Bytes(message);
            var header = Encoding.ASCII.GetBytes($"Content-Length: {content.Length}\r\n\r\n");
            stream.Write(header);
            stream.Write(content);
        }
        stream.Position = 0;
        return stream;
    }

    private static IReadOnlyList<JsonElement> ReadDapMessages(MemoryStream stream)
    {
        var payload = stream.ToArray();
        var messages = new List<JsonElement>();
        var offset = 0;
        while (offset < payload.Length)
        {
            var headerEnd = payload.AsSpan(offset).IndexOf("\r\n\r\n"u8);
            Assert.True(headerEnd >= 0);
            var header = Encoding.ASCII.GetString(payload, offset, headerEnd);
            Assert.StartsWith("Content-Length: ", header, StringComparison.Ordinal);
            var contentLength = int.Parse(
                header["Content-Length: ".Length..],
                System.Globalization.CultureInfo.InvariantCulture);
            offset += headerEnd + 4;
            using var document = JsonDocument.Parse(payload.AsMemory(offset, contentLength));
            messages.Add(document.RootElement.Clone());
            offset += contentLength;
        }

        return messages;
    }

    private static async Task AssertUnsupportedLaunchFieldRejectedAsync(
        string fieldName,
        object fieldValue)
    {
        var arguments = CreateValidLaunchArguments();
        arguments[fieldName] = fieldValue;
        await AssertLaunchArgumentsRejectedAsync(arguments, fieldName);
    }

    private static Dictionary<string, object?> CreateValidLaunchArguments()
        => new()
        {
            ["project"] = Path.GetFullPath("project"),
            ["document"] = "Book1",
            ["module"] = "Module1",
            ["procedure"] = "Run",
            ["__vbaDebugWorkbookFileName"] = "Book1.xlsm",
            ["sourceSnapshot"] = new Dictionary<string, object?>
            {
                ["schemaVersion"] = 1,
                ["sources"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["relativePath"] = "Module1.bas",
                        ["sourceUri"] = "file:///C:/persistent/Module1.bas",
                        ["encoding"] = "utf8",
                        ["contentBase64"] = Convert.ToBase64String(
                            Encoding.UTF8.GetBytes("Attribute VB_Name = \"Module1\"\r\n"))
                    }
                }
            }
        };

    private static void SetLaunchContent(
        Dictionary<string, object?> launchArguments,
        string contentBase64)
    {
        var snapshot = Assert.IsType<Dictionary<string, object?>>(
            launchArguments["sourceSnapshot"]);
        var sources = Assert.IsType<Dictionary<string, object?>[]>(snapshot["sources"]);
        Assert.Single(sources)["contentBase64"] = contentBase64;
    }

    private static void SetLaunchActiveSource(
        Dictionary<string, object?> launchArguments,
        int line)
    {
        var snapshot = Assert.IsType<Dictionary<string, object?>>(
            launchArguments["sourceSnapshot"]);
        snapshot["activeSource"] = new
        {
            sourceUri = "file:///C:/persistent/Module1.bas",
            line,
            character = 4
        };
    }

    private static async Task AssertLaunchArgumentsRejectedAsync(
        Dictionary<string, object?> arguments,
        string expectedMessage)
    {
        var probe = new RecordingVbaDevCapabilitiesProbe(
            new VbaDevCapabilitiesProbeResult(
                0,
                "{\"featureVersions\":{\"build.sourceSnapshot\":\"1.0\"}}",
                string.Empty));
        var launchService = new RecordingDebugLaunchService();
        var commandLine = VbaDebugAdapterCommandLine.Create(
            new StandaloneVbaDebugAdapterStdioRunner(launchService),
            probe);
        using var standardInput = CreateDapInput(
            new
            {
                seq = 1,
                type = "request",
                command = "launch",
                arguments
            },
            new
            {
                seq = 2,
                type = "request",
                command = "configurationDone",
                arguments = new { }
            });
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();

        var exitCode = await commandLine.InvokeAsync(
            [
                "--stdio",
                "--vba-dev", Path.GetFullPath("vba-dev.exe"),
                "--session", "0123456789abcdef0123456789abcdef"
            ],
            standardInput,
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Empty(launchService.Invocations);
        var response = Assert.Single(
            ReadDapMessages(standardOutput),
            message => message.TryGetProperty("request_seq", out var sequence) &&
                       sequence.GetInt32() == 1);
        Assert.False(response.GetProperty("success").GetBoolean());
        Assert.Contains(
            expectedMessage,
            response.GetProperty("message").GetString(),
            StringComparison.Ordinal);
    }

    private sealed class RecordingVbaDevCapabilitiesProbe(
        VbaDevCapabilitiesProbeResult result) : IVbaDevCapabilitiesProbe
    {
        public List<string> Invocations { get; } = [];

        public Task<VbaDevCapabilitiesProbeResult> ProbeAsync(
            string vbaDevPath,
            CancellationToken cancellationToken)
        {
            Invocations.Add(vbaDevPath);
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingDebugLaunchService(
        IStandaloneVbaDebugRunningSession? runningSession = null,
        DebugInputWait? inputWait = null) : IStandaloneVbaDebugLaunchService
    {
        public List<(
            string VbaDevPath,
            string SessionId,
            StandaloneVbaDebugLaunchRequest Request)> Invocations { get; } = [];

        public async Task<IStandaloneVbaDebugRunningSession> LaunchAsync(
            string vbaDevPath,
            string sessionId,
            StandaloneVbaDebugLaunchRequest request,
            CancellationToken cancellationToken,
            IDebugLifecycleSink? lifecycleSink = null)
        {
            Invocations.Add((vbaDevPath, sessionId, request));
            if (inputWait is not null && lifecycleSink is not null)
            {
                await lifecycleSink.InputRequiredAsync(inputWait, cancellationToken);
            }
            return runningSession ?? new RecordingRunningSession();
        }
    }

    private sealed class SequencedDebugLaunchService(
        IEnumerable<IStandaloneVbaDebugRunningSession> sessions,
        List<string> events,
        Action<StandaloneVbaDebugLaunchRequest>? validate = null)
        : IStandaloneVbaDebugLaunchService
    {
        private readonly Queue<IStandaloneVbaDebugRunningSession> sessions = new(sessions);

        public List<(
            string VbaDevPath,
            string SessionId,
            StandaloneVbaDebugLaunchRequest Request)> Invocations { get; } = [];

        public Task<IStandaloneVbaDebugRunningSession> LaunchAsync(
            string vbaDevPath,
            string sessionId,
            StandaloneVbaDebugLaunchRequest request,
            CancellationToken cancellationToken,
            IDebugLifecycleSink? lifecycleSink = null)
        {
            Invocations.Add((vbaDevPath, sessionId, request));
            events.Add($"launch:{Invocations.Count}");
            return Task.FromResult(sessions.Dequeue());
        }

        public void ValidateForLaunch(StandaloneVbaDebugLaunchRequest request)
            => validate?.Invoke(request);
    }

    private sealed class CancellationAwareDebugLaunchService : IStandaloneVbaDebugLaunchService
    {
        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IStandaloneVbaDebugRunningSession> LaunchAsync(
            string vbaDevPath,
            string sessionId,
            StandaloneVbaDebugLaunchRequest request,
            CancellationToken cancellationToken,
            IDebugLifecycleSink? lifecycleSink = null)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The in-flight launch unexpectedly completed.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                throw;
            }
        }
    }

    private sealed class FailingDebugLaunchService(Exception exception)
        : IStandaloneVbaDebugLaunchService
    {
        public Task<IStandaloneVbaDebugRunningSession> LaunchAsync(
            string vbaDevPath,
            string sessionId,
            StandaloneVbaDebugLaunchRequest request,
            CancellationToken cancellationToken,
            IDebugLifecycleSink? lifecycleSink = null)
            => Task.FromException<IStandaloneVbaDebugRunningSession>(exception);
    }

    private sealed class RecordingRunningSession(
        IReadOnlyList<VbeBreakpoint>? verifiedBreakpoints = null,
        int processId = 2718,
        Task<int>? completion = null,
        List<string>? events = null,
        string label = "session",
        string targetModuleName = "Module1",
        string targetProcedureName = "Run") : IStandaloneVbaDebugRunningSession
    {
        public int ProcessId { get; } = processId;

        public int TerminateCalls { get; private set; }

        public int DisposeCalls { get; private set; }

        public Task<int> Completion { get; } = completion ??
            new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously).Task;

        public IReadOnlyList<VbeBreakpoint> VerifiedBreakpoints { get; } =
            verifiedBreakpoints ?? [];

        public string TargetModuleName { get; } = targetModuleName;

        public string TargetProcedureName { get; } = targetProcedureName;

        public ValueTask TerminateAsync()
        {
            TerminateCalls++;
            events?.Add($"{label}:terminate");
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            events?.Add($"{label}:dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DapResponseFailingStream(string command) : MemoryStream
    {
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Encoding.UTF8.GetString(buffer.Span).Contains(
                $"\"command\":\"{command}\"",
                StringComparison.Ordinal))
            {
                throw new IOException($"Synthetic {command} response transport failure.");
            }

            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class GatedDapResponseStream(int requestSequence) : MemoryStream
    {
        private readonly TaskCompletionSource responseWriteStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseResponseWrite = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int gated;

        public Task ResponseWriteStarted => responseWriteStarted.Task;

        public void ReleaseResponseWrite() => releaseResponseWrite.TrySetResult();

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Encoding.UTF8.GetString(buffer.Span).Contains(
                    $"\"request_seq\":{requestSequence}",
                    StringComparison.Ordinal) &&
                Interlocked.Exchange(ref gated, 1) == 0)
            {
                responseWriteStarted.TrySetResult();
                await releaseResponseWrite.Task.WaitAsync(cancellationToken);
            }

            await base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class BlockingTailStream(byte[] prefix) : Stream
    {
        private readonly MemoryStream prefixStream = new(prefix, writable: false);
        private readonly TaskCompletionSource tailCompletion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void Complete() => tailCompletion.TrySetResult();

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var count = prefixStream.Read(buffer.Span);
            if (count != 0)
            {
                return count;
            }

            await tailCompletion.Task.ConfigureAwait(false);
            return 0;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Complete();
                prefixStream.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
