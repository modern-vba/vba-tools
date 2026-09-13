using System.Text;
using System.Text.Json;
using VbaDebugAdapter.Cli;
using VbaDebugAdapter.Infrastructure;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed partial class VbaDebugAdapterCliSurfaceTests
{
    [Theory]
    [InlineData("sessionId")]
    [InlineData("propertyName")]
    [InlineData("unknownProperty")]
    public async Task UndecodableRestartFieldsRespectCorrelationBeforeRejectingThePayload(string invalidPart)
    {
        const string sessionId = "0123456789abcdef0123456789abcdef";
        const string preparationId = "fedcba9876543210fedcba9876543210";
        var runningSession = new RecordingRunningSession();
        var launchService = new RecordingDebugLaunchService(runningSession);
        var commandLine = CreateCommandLine(
            new StandaloneVbaDebugAdapterStdioRunner(launchService),
            new RecordingVbaDevCapabilitiesProbe(new(
                0,
                "{\"featureVersions\":{\"build.sourceSnapshot\":\"2.0\",\"build.sourceSnapshotAnalysis\":\"1.0\"}}",
                string.Empty)));
        var initialLaunch = CreateValidLaunchArguments();
        initialLaunch["__vbaRestartPreparation"] = new
        {
            protocolVersion = 1,
            id = preparationId,
            generation = 0
        };
        using var inputPrefix = CreateDapInput(
            new { seq = 1, type = "request", command = "launch", arguments = initialLaunch },
            new { seq = 2, type = "request", command = "configurationDone", arguments = new { } },
            new { seq = 3, type = "request", command = "restart", arguments = new { } });
        var rawNotification = JsonSerializer.Serialize(new
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
                success = "malformed payload"
            }
        });
        rawNotification = invalidPart switch
        {
            "sessionId" => rawNotification.Replace($"\"sessionId\":\"{sessionId}\"", "\"sessionId\":\"\\uD800\"", StringComparison.Ordinal),
            "propertyName" => rawNotification.Replace("\"sessionId\":", "\"\\uD800\":", StringComparison.Ordinal),
            _ => rawNotification.Replace("\"success\":", "\"\\uD800\":null,\"success\":", StringComparison.Ordinal)
        };
        Assert.Contains("\\uD800", rawNotification, StringComparison.Ordinal);
        Assert.All(rawNotification, character => Assert.InRange((int)character, 0, 127));
        var rawContent = Encoding.ASCII.GetBytes(rawNotification);
        inputPrefix.Position = inputPrefix.Length;
        inputPrefix.Write(Encoding.ASCII.GetBytes($"Content-Length: {rawContent.Length}\r\n\r\n"));
        inputPrefix.Write(rawContent);
        using var tail = CreateDapInput(
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
                    generation = 1,
                    success = false,
                    message = "The correctly correlated preparation failed."
                }
            },
            new { seq = 6, type = "request", command = "threads", arguments = new { } });
        inputPrefix.Write(tail.ToArray());
        using var standardInput = new BlockingTailStream(inputPrefix.ToArray());
        using var standardOutput = new MemoryStream();
        var invocation = commandLine.InvokeAsync(
            ["--stdio", "--vba-dev", Path.GetFullPath("vba-dev.exe"), "--session", sessionId],
            standardInput, standardOutput, Stream.Null, CancellationToken.None);

        try
        {
            Assert.True(SpinWait.SpinUntil(
                () => ReadUtf8(standardOutput).Contains("\"request_seq\":6", StringComparison.Ordinal),
                TimeSpan.FromSeconds(2)));
            var messages = ReadDapMessages(standardOutput);
            Assert.True(AdmissionResponse(messages, 4).GetProperty("success").GetBoolean());
            Assert.True(AdmissionResponse(messages, 5).GetProperty("success").GetBoolean());
            var restartResponse = AdmissionResponse(messages, 3);
            Assert.False(restartResponse.GetProperty("success").GetBoolean());
            if (invalidPart == "unknownProperty")
            {
                Assert.Contains("invalid Unicode", restartResponse.GetProperty("message").GetString());
            }
            else
            {
                Assert.Equal("The correctly correlated preparation failed.", restartResponse.GetProperty("message").GetString());
            }
            Assert.True(AdmissionResponse(messages, 6).GetProperty("success").GetBoolean());
            Assert.Single(launchService.Invocations);
            Assert.Equal(0, runningSession.TerminateCalls);
            Assert.Equal(0, runningSession.DisposeCalls);
            Assert.False(invocation.IsCompleted);
        }
        finally
        {
            standardInput.Complete();
            Assert.Equal(0, await invocation);
        }
    }

    [Fact]
    public async Task CorrelatedMalformedLaunchFailsRestartAndConsumesTheNotificationOnce()
    {
        const string sessionId = "0123456789abcdef0123456789abcdef";
        const string preparationId = "fedcba9876543210fedcba9876543210";
        var runningSession = new RecordingRunningSession();
        var launchService = new RecordingDebugLaunchService(runningSession);
        var commandLine = CreateCommandLine(
            new StandaloneVbaDebugAdapterStdioRunner(launchService),
            new RecordingVbaDevCapabilitiesProbe(new(
                0,
                "{\"featureVersions\":{\"build.sourceSnapshot\":\"2.0\",\"build.sourceSnapshotAnalysis\":\"1.0\"}}",
                string.Empty)));
        var initialLaunch = CreateValidLaunchArguments();
        initialLaunch["__vbaRestartPreparation"] = new
        {
            protocolVersion = 1,
            id = preparationId,
            generation = 0
        };
        var malformedLaunch = CreateValidLaunchArguments();
        Assert.IsType<Dictionary<string, object?>>(malformedLaunch["sourceSnapshot"])["schemaVersion"] = "2";
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
                    success = false,
                    message = "The client reported a preparation failure.",
                    launch = malformedLaunch
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
                    generation = 1,
                    success = true,
                    launch = freshLaunch
                }
            },
            new { seq = 6, type = "request", command = "threads", arguments = new { } });
        using var standardInput = new BlockingTailStream(inputPrefix.ToArray());
        using var standardOutput = new MemoryStream();
        var invocation = commandLine.InvokeAsync(
            ["--stdio", "--vba-dev", Path.GetFullPath("vba-dev.exe"), "--session", sessionId],
            standardInput, standardOutput, Stream.Null, CancellationToken.None);

        try
        {
            Assert.True(SpinWait.SpinUntil(
                () => ReadUtf8(standardOutput).Contains("\"request_seq\":6", StringComparison.Ordinal),
                TimeSpan.FromSeconds(2)));
            var messages = ReadDapMessages(standardOutput);
            var restartResponse = AdmissionResponse(messages, 3);
            Assert.False(restartResponse.GetProperty("success").GetBoolean());
            Assert.Contains("schemaVersion", restartResponse.GetProperty("message").GetString());
            Assert.True(AdmissionResponse(messages, 4).GetProperty("success").GetBoolean());
            Assert.True(AdmissionResponse(messages, 5).GetProperty("success").GetBoolean());
            Assert.True(AdmissionResponse(messages, 6).GetProperty("success").GetBoolean());
            Assert.Single(launchService.Invocations);
            Assert.Equal(0, runningSession.TerminateCalls);
            Assert.Equal(0, runningSession.DisposeCalls);
            Assert.False(invocation.IsCompleted);
        }
        finally
        {
            standardInput.Complete();
            Assert.Equal(0, await invocation);
        }
    }

    [Fact]
    public async Task MalformedRequestRejectionOutputFailureRetainsOwnerReleaseWithoutRetry()
    {
        using var temp = TempDirectory.Create();
        var workspaceManager = new VbaDebugSessionWorkspaceManager(Path.Combine(temp.Path, "adapter-root"));
        await using var lease = await workspaceManager.ClaimAsync(
            DebugSessionId.Parse("0123456789abcdef0123456789abcdef"), CancellationToken.None);
        var runningSession = new RecordingRunningSession();
        var runner = new StandaloneVbaDebugAdapterStdioRunner(new RecordingDebugLaunchService(runningSession));
        using var input = CreateDapInput(
            new { seq = 1, type = "request", command = "launch", arguments = CreateValidLaunchArguments() },
            new { seq = 2, type = "request", command = "configurationDone", arguments = new { } },
            new
            {
                seq = 3,
                type = "request",
                command = "setBreakpoints",
                arguments = new
                {
                    source = new { path = "C:\\persistent\\Module1.bas" },
                    breakpoints = new[] { new { line = "4" } }
                }
            },
            new { seq = 4, type = "request", command = "threads", arguments = new { } });
        using var output = new DapResponseFailingStream("setBreakpoints");

        var exception = await Assert.ThrowsAsync<DebugFailureException>(() => runner.RunAsync(
            Path.GetFullPath("vba-dev.exe"), lease, input, output, Stream.Null, CancellationToken.None));

        var primary = Assert.IsType<IOException>(exception.FailureOutcome.PrimaryFailure);
        Assert.Contains("setBreakpoints response transport failure", primary.Message);
        Assert.False(exception.FailureOutcome.HasCleanupFailure);
        foreach (var kind in new[] { DebugResourceKind.Process, DebugResourceKind.Com, DebugResourceKind.Handle })
        {
            Assert.Contains(exception.FailureOutcome.Evidence, item => item.Kind == kind && item.Released);
        }
        Assert.Equal(1, runningSession.TerminateCalls);
        Assert.Equal(1, runningSession.DisposeCalls);
        Assert.Equal(0, output.WritesAfterFailure);
        Assert.DoesNotContain("\"request_seq\":4", ReadUtf8(output), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedBreakpointRequestPreservesTheInFlightLaunchUntilEof()
    {
        var launchService = new CancellationAwareDebugLaunchService();
        var commandLine = CreateCommandLine(
            new StandaloneVbaDebugAdapterStdioRunner(launchService),
            new RecordingVbaDevCapabilitiesProbe(new(
                0,
                "{\"featureVersions\":{\"build.sourceSnapshot\":\"2.0\",\"build.sourceSnapshotAnalysis\":\"1.0\"}}",
                string.Empty)));
        using var inputPrefix = CreateDapInput(
            new { seq = 1, type = "request", command = "launch", arguments = CreateValidLaunchArguments() },
            new { seq = 2, type = "request", command = "configurationDone", arguments = new { } },
            new
            {
                seq = 3,
                type = "request",
                command = "setBreakpoints",
                arguments = new
                {
                    source = new { path = "C:\\persistent\\Module1.bas" },
                    breakpoints = new object[] { new { line = 3 }, new { line = "4" } }
                }
            },
            new { seq = 4, type = "request", command = "threads", arguments = new { } });
        using var standardInput = new BlockingTailStream(inputPrefix.ToArray());
        using var standardOutput = new MemoryStream();
        var invocation = commandLine.InvokeAsync(
            [
                "--stdio",
                "--vba-dev", Path.GetFullPath("vba-dev.exe"),
                "--session", "0123456789abcdef0123456789abcdef"
            ],
            standardInput,
            standardOutput,
            Stream.Null,
            CancellationToken.None);

        try
        {
            Assert.True(SpinWait.SpinUntil(
                () => ReadUtf8(standardOutput).Contains("\"request_seq\":4", StringComparison.Ordinal),
                TimeSpan.FromSeconds(2)));
            var messages = ReadDapMessages(standardOutput);
            Assert.True(AdmissionResponse(messages, 2).GetProperty("success").GetBoolean());
            Assert.False(AdmissionResponse(messages, 3).GetProperty("success").GetBoolean());
            Assert.True(AdmissionResponse(messages, 4).GetProperty("success").GetBoolean());
            Assert.DoesNotContain(messages, message =>
                message.TryGetProperty("request_seq", out var sequence) && sequence.GetInt32() == 1);
            Assert.False(launchService.CancellationObserved.Task.IsCompleted);
            Assert.False(invocation.IsCompleted);
        }
        finally
        {
            standardInput.Complete();
            Assert.Equal(0, await invocation);
        }

        Assert.True(launchService.CancellationObserved.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task MalformedBreakpointRequestPreservesTheActiveSessionUntilEof()
    {
        var runningSession = new RecordingRunningSession();
        var launchService = new RecordingDebugLaunchService(runningSession);
        var commandLine = CreateCommandLine(
            new StandaloneVbaDebugAdapterStdioRunner(launchService),
            new RecordingVbaDevCapabilitiesProbe(new(
                0,
                "{\"featureVersions\":{\"build.sourceSnapshot\":\"2.0\",\"build.sourceSnapshotAnalysis\":\"1.0\"}}",
                string.Empty)));
        using var inputPrefix = CreateDapInput(
            new { seq = 1, type = "request", command = "launch", arguments = CreateValidLaunchArguments() },
            new { seq = 2, type = "request", command = "configurationDone", arguments = new { } },
            new
            {
                seq = 3,
                type = "request",
                command = "setBreakpoints",
                arguments = new
                {
                    source = new { path = "C:\\persistent\\Module1.bas" },
                    breakpoints = new object[] { new { line = 3 }, new { line = "4" } }
                }
            },
            new { seq = 4, type = "request", command = "threads", arguments = new { } });
        using var standardInput = new BlockingTailStream(inputPrefix.ToArray());
        using var standardOutput = new MemoryStream();
        var invocation = commandLine.InvokeAsync(
            [
                "--stdio",
                "--vba-dev", Path.GetFullPath("vba-dev.exe"),
                "--session", "0123456789abcdef0123456789abcdef"
            ],
            standardInput,
            standardOutput,
            Stream.Null,
            CancellationToken.None);

        try
        {
            Assert.True(SpinWait.SpinUntil(
                () => ReadUtf8(standardOutput).Contains("\"request_seq\":4", StringComparison.Ordinal),
                TimeSpan.FromSeconds(2)));
            var messages = ReadDapMessages(standardOutput);
            Assert.True(AdmissionResponse(messages, 1).GetProperty("success").GetBoolean());
            Assert.False(AdmissionResponse(messages, 3).GetProperty("success").GetBoolean());
            Assert.True(AdmissionResponse(messages, 4).GetProperty("success").GetBoolean());
            Assert.Single(launchService.Invocations);
            Assert.False(invocation.IsCompleted);
            Assert.Equal(0, runningSession.TerminateCalls);
            Assert.Equal(0, runningSession.DisposeCalls);
            Assert.DoesNotContain(messages, message =>
                message.TryGetProperty("event", out var value) && value.GetString() == "terminated");
        }
        finally
        {
            standardInput.Complete();
            Assert.Equal(0, await invocation);
        }

        Assert.Equal(1, runningSession.TerminateCalls);
        Assert.Equal(1, runningSession.DisposeCalls);
    }
}
