using VbaDebugAdapter.Build;
using VbaDebugAdapter.Cli;
using VbaDebugAdapter.Debugging;
using VbaDebugAdapter.Infrastructure;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed partial class VbaDebugAdapterCliSurfaceTests
{
    [Theory]
    [InlineData("parser-operation")]
    [InlineData("parser-setup")]
    [InlineData("encoding-service")]
    [InlineData("build-source")]
    public async Task FailureTypeTextOrPrebuildTimingDoesNotGrantRequestOnlySourceRejection(string failureSite)
    {
        using var temp = TempDirectory.Create();
        await using var ownedLease = await new VbaDebugSessionWorkspaceManager(Path.Combine(temp.Path, "adapter-root"))
            .ClaimAsync(DebugSessionId.Parse("0123456789abcdef0123456789abcdef"), CancellationToken.None);
        var lease = new RecordingSourceRejectionLease(ownedLease);
        const string message = "The transported source snapshot contains invalid base64 for 'Module1.bas'.";
        Exception? original = failureSite switch
        {
            "parser-operation" => new InvalidOperationException(message),
            "parser-setup" => new DebugSetupException(message),
            "build-source" => new DebugSourceRejectedException(message),
            _ => null
        };
        var resources = new SourceRejectionResourceSpy(failureSite == "build-source" ? original : null);
        var admission = failureSite.StartsWith("parser-", StringComparison.Ordinal)
            ? new DebugSourceAdmission(932, (_, _) => throw original!)
            : new DebugSourceAdmission(failureSite == "encoding-service" ? 77777 : 932);
        var service = new SourceRejectionPreparationSequence(new StandaloneVbaDebugLaunchService(admission, resources, resources));
        var arguments = CreateSourceRejectionLaunchArguments();
        if (failureSite == "encoding-service")
        {
            var snapshot = Assert.IsType<Dictionary<string, object?>>(arguments["sourceSnapshot"]);
            var source = Assert.Single(Assert.IsType<Dictionary<string, object?>[]>(snapshot["sources"]));
            source["encoding"] = "windows-77777";
            source["contentBase64"] = "QQ==";
        }
        using var input = CreateDapInput(
            new { seq = 1, type = "request", command = "launch", arguments },
            new { seq = 2, type = "request", command = "configurationDone", arguments = new { } });
        using var output = new MemoryStream();

        Assert.Equal(0, await new StandaloneVbaDebugAdapterStdioRunner(service).RunAsync(
            Path.GetFullPath("vba-dev.exe"), lease, input, output, Stream.Null, CancellationToken.None));

        var messages = ReadDapMessages(output);
        Assert.False(AdmissionResponse(messages, 1).GetProperty("success").GetBoolean());
        Assert.Single(messages, item => item.TryGetProperty("event", out var value) && value.GetString() == "terminated");
        var outcome = Assert.IsType<DebugFailureException>(service.LastFailure).FailureOutcome;
        Assert.False(outcome.HasUnprovedRelease, outcome.Describe());
        if (original is not null)
        {
            Assert.Same(original, outcome.PrimaryFailure);
            Assert.NotEmpty(original.StackTrace!);
            Assert.Contains(message, AdmissionResponse(messages, 1).GetProperty("message").GetString(), StringComparison.Ordinal);
        }
        else
        {
            Assert.IsAssignableFrom<ArgumentException>(outcome.PrimaryFailure);
        }
        Assert.Equal(0, lease.GenerationCalls);
        Assert.Equal(failureSite == "build-source" ? 1 : 0, resources.BuildCalls);
        Assert.Equal(0, resources.ExcelCalls);
    }

    [Fact]
    public async Task PreparationRejectionCarrierFromCommitUsesOrdinaryLifecycle()
    {
        using var temp = TempDirectory.Create();
        await using var lease = await new VbaDebugSessionWorkspaceManager(Path.Combine(temp.Path, "adapter-root"))
            .ClaimAsync(DebugSessionId.Parse("0123456789abcdef0123456789abcdef"), CancellationToken.None);
        var original = new DebugSourceRejectedException("A commit-stage failure resembles source rejection.");
        var carrier = new DebugSourceRejectedPreparationException(new DebugFailureCompletion(original).Complete());
        var service = new CommitSourceRejectionService(carrier);
        var runner = new StandaloneVbaDebugAdapterStdioRunner(service);
        using var input = CreateDapInput(
            new { seq = 1, type = "request", command = "launch", arguments = CreateSourceRejectionLaunchArguments() },
            new { seq = 2, type = "request", command = "configurationDone", arguments = new { } });
        using var output = new MemoryStream();

        Assert.Equal(0, await runner.RunAsync(Path.GetFullPath("vba-dev.exe"), lease,
            input, output, Stream.Null, CancellationToken.None));

        var messages = ReadDapMessages(output);
        Assert.False(AdmissionResponse(messages, 1).GetProperty("success").GetBoolean());
        Assert.Contains(original.Message, AdmissionResponse(messages, 1).GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Single(messages, message => message.TryGetProperty("event", out var value) && value.GetString() == "terminated");
        Assert.Equal(1, service.CommitCalls);
        Assert.False(Assert.IsType<DebugFailureOutcome>(service.Plan!.CleanupOutcome).HasUnprovedRelease);
        Assert.Same(original, carrier.FailureOutcome.PrimaryFailure);
    }

    [Theory]
    [InlineData("base64", "base64")]
    [InlineData("target", "was not found")]
    [InlineData("identity", "ambiguous exported module identity")]
    [InlineData("breakpoint", "breakpoint")]
    [InlineData("schema", "schema version")]
    [InlineData("inventory", "complete source inventory")]
    [InlineData("path", "path components")]
    [InlineData("uri", "persistent file URI")]
    [InlineData("raw-source-path", "persistent file URI")]
    [InlineData("duplicate-breakpoint-identity", "duplicate breakpoint")]
    [InlineData("duplicate-source-identity", "duplicate identity")]
    [InlineData("encoding", "canonical active Windows")]
    [InlineData("bytes", "strictly decode")]
    [InlineData("order", "canonical relative-path order")]
    [InlineData("sidecar", "same-directory form")]
    public async Task KnownInitialSourceRejectionAllowsCorrectedLaunchWithoutTerminationOrAcquisition(
        string rejection, string expectedMessage)
    {
        using var temp = TempDirectory.Create();
        var manager = new VbaDebugSessionWorkspaceManager(Path.Combine(temp.Path, "adapter-root"));
        await using var ownedLease = await manager.ClaimAsync(
            DebugSessionId.Parse("0123456789abcdef0123456789abcdef"), CancellationToken.None);
        var lease = new RecordingSourceRejectionLease(ownedLease);
        var resources = new SourceRejectionResourceSpy();
        var accepted = new RecordingDebugLaunchService();
        var service = new SourceRejectionPreparationSequence(
            new StandaloneVbaDebugLaunchService(new DebugSourceAdmission(932), resources, resources), accepted);
        var runner = new StandaloneVbaDebugAdapterStdioRunner(service);
        var rejected = CreateSourceRejectionLaunchArguments();
        ApplySourceRejection(rejected, rejection);
        using var prefix = CreateDapInput(
            new { seq = 1, type = "request", command = "launch", arguments = rejected },
            new { seq = 2, type = "request", command = "configurationDone", arguments = new { } },
            new { seq = 3, type = "request", command = "launch", arguments = CreateSourceRejectionLaunchArguments() },
            new { seq = 4, type = "request", command = "threads", arguments = new { } });
        using var input = new BlockingTailStream(prefix.ToArray());
        using var output = new MemoryStream();
        var invocation = runner.RunAsync(Path.GetFullPath("vba-dev.exe"), lease, input, output, Stream.Null, CancellationToken.None);
        try
        {
            Assert.True(SpinWait.SpinUntil(() => ReadUtf8(output).Contains("\"request_seq\":4", StringComparison.Ordinal), TimeSpan.FromSeconds(2)));
            var messages = ReadDapMessages(output);
            Assert.False(AdmissionResponse(messages, 1).GetProperty("success").GetBoolean());
            Assert.Contains(expectedMessage, AdmissionResponse(messages, 1).GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.True(AdmissionResponse(messages, 3).GetProperty("success").GetBoolean());
            Assert.True(AdmissionResponse(messages, 4).GetProperty("success").GetBoolean());
            Assert.DoesNotContain(messages, message => message.TryGetProperty("event", out var value) && value.GetString() == "terminated");
            Assert.Equal(0, lease.GenerationCalls);
            Assert.Equal(0, resources.BuildCalls);
            Assert.Equal(0, resources.ExcelCalls);
            Assert.Equal(2, service.Invocations);
            Assert.Single(accepted.Invocations);
        }
        finally
        {
            input.Complete();
            Assert.Equal(0, await invocation);
        }
    }

    private static void ApplySourceRejection(Dictionary<string, object?> arguments, string rejection)
    {
        var snapshot = Assert.IsType<Dictionary<string, object?>>(arguments["sourceSnapshot"]);
        var sources = Assert.IsType<Dictionary<string, object?>[]>(snapshot["sources"]);
        switch (rejection)
        {
            case "base64":
                SetLaunchContent(arguments, "%%%");
                break;
            case "target":
                arguments["procedure"] = "Missing";
                break;
            case "identity":
                snapshot["sources"] = new[]
                {
                    sources[0],
                    new Dictionary<string, object?>
                    {
                        ["relativePath"] = "Other1.bas", ["sourceUri"] = "file:///C:/persistent/Other1.bas",
                        ["encoding"] = "utf8bom", ["contentBase64"] = Convert.ToBase64String(
                            DebugSnapshotTestEncoding.Utf8BomBytes("Attribute VB_Name = \"Other\"\r\n"))
                    },
                    new Dictionary<string, object?>
                    {
                        ["relativePath"] = "Other2.bas", ["sourceUri"] = "file:///C:/persistent/Other2.bas",
                        ["encoding"] = "utf8bom", ["contentBase64"] = Convert.ToBase64String(
                            DebugSnapshotTestEncoding.Utf8BomBytes("Attribute VB_Name = \"Other\"\r\n"))
                    }
                };
                break;
            case "breakpoint":
                snapshot["breakpoints"] = new[] { new { sourceUri = "file:///C:/persistent/Module1.bas", line = 0 } };
                break;
            case "schema":
                snapshot["schemaVersion"] = 3;
                break;
            case "inventory":
                snapshot["sources"] = Array.Empty<Dictionary<string, object?>>();
                break;
            case "path":
                sources[0]["relativePath"] = "../Module1.bas";
                break;
            case "uri":
                sources[0]["sourceUri"] = "https://example.test/Module1.bas";
                break;
            case "raw-source-path":
                sources[0]["sourceUri"] = @"C:\persistent\Module1.bas";
                break;
            case "duplicate-breakpoint-identity":
                snapshot["breakpoints"] = new[]
                {
                    new { sourceUri = "file:///C:/persistent/Module1.bas?first", line = 2 },
                    new { sourceUri = "file://localhost/c%3A/persistent/sub/../%4dodule1.bas#second", line = 2 }
                };
                break;
            case "duplicate-source-identity":
                snapshot["sources"] = new[]
                {
                    sources[0],
                    new Dictionary<string, object?>(sources[0])
                    {
                        ["relativePath"] = "Other.bas",
                        ["sourceUri"] = "file://localhost/c%3A/persistent/sub/../%4dodule1.bas#second"
                    }
                };
                break;
            case "encoding":
                sources[0]["encoding"] = "windows-1252";
                break;
            case "bytes":
                sources[0]["contentBase64"] = Convert.ToBase64String(new byte[] { 0xef, 0xbb, 0xbf, 0xff });
                break;
            case "order":
                snapshot["sources"] = new[]
                {
                    sources[0],
                    new Dictionary<string, object?>
                    {
                        ["relativePath"] = "Earlier.bas", ["sourceUri"] = "file:///C:/persistent/Earlier.bas",
                        ["encoding"] = "utf8bom", ["contentBase64"] = Convert.ToBase64String(
                            DebugSnapshotTestEncoding.Utf8BomBytes("Attribute VB_Name = \"Earlier\"\r\n"))
                    }
                };
                break;
            case "sidecar":
                snapshot["sources"] = new[]
                {
                    sources[0],
                    new Dictionary<string, object?> { ["relativePath"] = "Orphan.frx", ["contentBase64"] = "AA==" }
                };
                break;
            default:
                throw new ArgumentException("Unknown test rejection.", nameof(rejection));
        }
    }

    private static Dictionary<string, object?> CreateSourceRejectionLaunchArguments()
    {
        var arguments = CreateValidLaunchArguments();
        SetLaunchContent(arguments, Convert.ToBase64String(DebugSnapshotTestEncoding.Utf8BomBytes(
            "Attribute VB_Name = \"Module1\"\r\nPublic Sub Run()\r\n    Debug.Print \"run\"\r\nEnd Sub\r\n")));
        return arguments;
    }

    private sealed class SourceRejectionPreparationSequence(params IStandaloneVbaDebugLaunchService[] services)
        : IStandaloneVbaDebugLaunchService
    {
        private readonly Queue<IStandaloneVbaDebugLaunchService> remaining = new(services);
        public int Invocations { get; private set; }
        public Exception? LastFailure { get; private set; }

        public async Task<IPreparedDebugLaunchPlan> PrepareAsync(string vbaDevPath, IVbaDebugSessionWorkspaceLease workspaceLease,
            StandaloneVbaDebugLaunchRequest request, DebugRestartLaunchBinding? restartBinding,
            CancellationToken cancellationToken, IDebugLifecycleSink? lifecycleSink = null)
        {
            Invocations++;
            try
            {
                return await remaining.Dequeue().PrepareAsync(vbaDevPath, workspaceLease, request, restartBinding, cancellationToken, lifecycleSink);
            }
            catch (Exception exception)
            {
                LastFailure = exception;
                throw;
            }
        }
    }

    private sealed class SourceRejectionResourceSpy(Exception? buildFailure = null) : IVbaDebugWorkbookBuilder, IVbeDebugSessionFactory
    {
        public int BuildCalls { get; private set; }
        public int ExcelCalls { get; private set; }

        public Task<VbaDevSnapshotBuildResult> BuildAsync(string vbaDevPath, IVbaDebugSessionWorkspaceLease workspaceLease,
            VbaDevSnapshotBuildRequest request, CancellationToken cancellationToken)
        {
            BuildCalls++;
            if (buildFailure is not null)
            {
                // This injected build boundary has acquired no resources and supplies its proof.
                try { throw buildFailure; }
                catch (Exception exception)
                {
                    new DebugFailureCompletion(exception).Complete().ThrowWithEvidence();
                }
            }
            throw new InvalidOperationException("Source rejection must precede workbook building.");
        }

        public Task<IVbeDebugSession> StartVisibleAsync(CancellationToken cancellationToken)
        {
            ExcelCalls++;
            throw new InvalidOperationException("Source rejection must precede Excel acquisition.");
        }
    }

    private sealed class CommitSourceRejectionService(Exception failure) : IStandaloneVbaDebugLaunchService
    {
        public int CommitCalls { get; private set; }
        public FakePreparedDebugLaunchPlan? Plan { get; private set; }

        public Task<IPreparedDebugLaunchPlan> PrepareAsync(string vbaDevPath, IVbaDebugSessionWorkspaceLease workspaceLease,
            StandaloneVbaDebugLaunchRequest request, DebugRestartLaunchBinding? restartBinding,
            CancellationToken cancellationToken, IDebugLifecycleSink? lifecycleSink = null)
        {
            Plan = new FakePreparedDebugLaunchPlan(request, restartBinding,
                _ => { CommitCalls++; throw failure; });
            return Task.FromResult<IPreparedDebugLaunchPlan>(Plan);
        }
    }
}
