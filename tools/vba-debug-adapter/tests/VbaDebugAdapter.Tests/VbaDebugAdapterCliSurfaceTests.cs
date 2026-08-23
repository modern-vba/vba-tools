using VbaDebugAdapter.Cli;
using VbaDebugAdapter.Build;
using VbaDebugAdapter.Debugging;
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
            "{\"toolVersion\":\"0.1.0\",\"contractVersion\":\"1.0\",\"protocolVersion\":\"1.1\",\"transports\":[\"stdio\"],\"sessionIdFormat\":\"lowercase-hex-32\",\"commands\":[\"cleanup\",\"doctor\"],\"commandSchemaVersions\":{\"doctor\":\"1.0\"},\"requiredVbaDevFeatureVersions\":{\"build.sourceSnapshot\":\"1.0\"}}" + Environment.NewLine,
            ReadUtf8(standardOutput));
        Assert.Empty(ReadUtf8(standardError));
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
        Assert.False(
            messages[0].GetProperty("body")
                .GetProperty("supportsRestartRequest")
                .GetBoolean());
        Assert.False(
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

    [Fact]
    public async Task StandaloneDisconnectTerminatesTheOwnedDebugSession()
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
            new { seq = 3, type = "request", command = "disconnect", arguments = new { } });
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
        var disconnectResponse = Assert.Single(
            ReadDapMessages(standardOutput),
            message => message.TryGetProperty("request_seq", out var sequence) &&
                       sequence.GetInt32() == 3);
        Assert.True(disconnectResponse.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task DisconnectCancelsAnInFlightStandaloneLaunch()
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
            new { seq = 3, type = "request", command = "disconnect", arguments = new { } });
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
        var disconnectResponse = Assert.Single(
            messages,
            message => message.TryGetProperty("request_seq", out var sequence) &&
                       sequence.GetInt32() == 3);
        Assert.True(disconnectResponse.GetProperty("success").GetBoolean());
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

        public Task<int> RunAsync(
            string vbaDevPath,
            string sessionId,
            Stream standardInput,
            Stream standardOutput,
            Stream standardError,
            CancellationToken cancellationToken)
        {
            Invocations.Add((vbaDevPath, sessionId));
            return Task.FromResult(0);
        }
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
        Task<int>? completion = null) : IStandaloneVbaDebugRunningSession
    {
        public int ProcessId { get; } = processId;

        public int TerminateCalls { get; private set; }

        public int DisposeCalls { get; private set; }

        public Task<int> Completion { get; } = completion ??
            new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously).Task;

        public IReadOnlyList<VbeBreakpoint> VerifiedBreakpoints { get; } =
            verifiedBreakpoints ?? [];

        public ValueTask TerminateAsync()
        {
            TerminateCalls++;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
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
