using System.Text;
using System.Text.Json;
using VbaDev.App.Debugging;
using VbaDev.App.Projects;
using VbaDev.Cli.Debugging;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;
using Xunit;

namespace VbaDev.Tests;

public sealed class VbaDebugAdapterTests
{
    [Fact]
    public async Task InitializeAdvertisesTheMinimalConfigurationContractBeforeInitialized()
    {
        var adapter = CreateAdapter();
        await using var input = CreateInput(
            """
            {"seq":1,"type":"request","command":"initialize","arguments":{"adapterID":"vba"}}
            """);
        await using var output = new MemoryStream();

        await adapter.RunAsync(input, output, CancellationToken.None);

        var messages = ReadMessages(output);
        Assert.Equal(2, messages.Count);
        var initialize = messages[0].RootElement;
        Assert.Equal("response", initialize.GetProperty("type").GetString());
        Assert.Equal(1, initialize.GetProperty("request_seq").GetInt32());
        Assert.Equal("initialize", initialize.GetProperty("command").GetString());
        Assert.True(initialize.GetProperty("success").GetBoolean());
        Assert.True(initialize.GetProperty("body").GetProperty("supportsConfigurationDoneRequest").GetBoolean());
        Assert.Equal("initialized", messages[1].RootElement.GetProperty("event").GetString());
    }

    [Fact]
    public async Task AnEmptySourceBreakpointSetIsAccepted()
    {
        var adapter = CreateAdapter();
        await using var input = CreateInput(
            """
            {"seq":1,"type":"request","command":"initialize","arguments":{"adapterID":"vba"}}
            """,
            """
            {"seq":2,"type":"request","command":"setBreakpoints","arguments":{"source":{"path":"C:\\Project\\DebugModule.bas"},"breakpoints":[]}}
            """);
        await using var output = new MemoryStream();

        await adapter.RunAsync(input, output, CancellationToken.None);

        var response = ReadMessages(output)
            .Select(message => message.RootElement)
            .Single(message =>
                message.GetProperty("type").GetString() == "response" &&
                message.GetProperty("command").GetString() == "setBreakpoints");
        Assert.True(response.GetProperty("success").GetBoolean());
        Assert.Empty(response.GetProperty("body").GetProperty("breakpoints").EnumerateArray());
    }

    [Fact]
    public async Task ThreadsReportsTheSingleVbeExecutionOwner()
    {
        var adapter = CreateAdapter();
        await using var input = CreateInput(
            """
            {"seq":1,"type":"request","command":"initialize","arguments":{"adapterID":"vba"}}
            """,
            """
            {"seq":2,"type":"request","command":"threads","arguments":{}}
            """);
        await using var output = new MemoryStream();

        await adapter.RunAsync(input, output, CancellationToken.None);

        var response = ReadMessages(output)
            .Select(message => message.RootElement)
            .Single(message =>
                message.GetProperty("type").GetString() == "response" &&
                message.GetProperty("command").GetString() == "threads");
        Assert.True(response.GetProperty("success").GetBoolean());
        var thread = Assert.Single(response.GetProperty("body").GetProperty("threads").EnumerateArray());
        Assert.Equal(1, thread.GetProperty("id").GetInt32());
        Assert.Equal("VBE", thread.GetProperty("name").GetString());
    }

    [Fact]
    public async Task AnExplicitLaunchCompletesConfigurationAndTerminatesWhenOwnedExcelExits()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("DebugProject");
        var manifest = ProjectManifest.CreateDefault("DebugProject", "Book1", root, null);
        manifest.Documents["Book1"] = manifest.Documents["Book1"] with
        {
            BinPath = "custom-bin/GeneratedBook.xlsm"
        };
        var manifestStore = new JsonProjectManifestStore();
        manifestStore.Save(root, manifest);
        var sourceSetPath = Path.GetFullPath(Path.Combine(root, manifest.Documents["Book1"].SourcePath));
        Directory.CreateDirectory(sourceSetPath);
        var sourcePath = Path.Combine(sourceSetPath, "DebugModule.bas");
        var source =
            """
            Attribute VB_Name = "DebugModule"

            Public Sub RunTarget()
            End Sub
            """;
        File.WriteAllText(sourcePath, source);
        var events = new List<string>();
        var vbeSession = new CompletingVbeDebugSession(events, 2718, 0);
        var adapter = new VbaDebugAdapter(
            new ProjectContextResolver(manifestStore),
            new DebugLaunchCoordinator(
                new RecordingDebugWorkbookBuilder(events, ["WARN Protected reference remains."]),
                new RecordingVbeDebugSessionFactory(events, vbeSession)),
            () => root);
        var launch = JsonSerializer.Serialize(new
        {
            seq = 2,
            type = "request",
            command = "launch",
            arguments = new
            {
                project = root,
                document = "Book1",
                module = "DebugModule",
                procedure = "RunTarget",
                sourceSnapshot = new
                {
                    schemaVersion = 1,
                    sources = new[] { new { path = sourcePath, text = source } }
                }
            }
        });
        await using var input = CreateInput(
            """
            {"seq":1,"type":"request","command":"initialize","arguments":{"adapterID":"vba"}}
            """,
            launch,
            """
            {"seq":3,"type":"request","command":"configurationDone","arguments":{}}
            """);
        await using var output = new MemoryStream();

        await adapter.RunAsync(input, output, CancellationToken.None);

        var messages = ReadMessages(output).Select(message => message.RootElement).ToArray();
        Assert.True(Response(messages, "configurationDone").GetProperty("success").GetBoolean());
        Assert.True(Response(messages, "launch").GetProperty("success").GetBoolean());
        Assert.Contains(messages, message =>
            message.GetProperty("type").GetString() == "event" &&
            message.GetProperty("event").GetString() == "output" &&
            message.GetProperty("body").GetProperty("output").GetString()!.Contains("WARN Protected reference", StringComparison.Ordinal));
        Assert.Equal(
            [
                "build:Book1",
                "start-visible",
                $"open-and-run:{Path.Combine(root, "custom-bin", "GeneratedBook.xlsm")}:DebugModule.RunTarget"
            ],
            events);
        var exited = messages.Single(message =>
            message.GetProperty("type").GetString() == "event" &&
            message.GetProperty("event").GetString() == "exited");
        Assert.Equal(0, exited.GetProperty("body").GetProperty("exitCode").GetInt32());
        Assert.Contains(messages, message =>
            message.GetProperty("type").GetString() == "event" &&
            message.GetProperty("event").GetString() == "output" &&
            message.GetProperty("body").GetProperty("output").GetString()!.Contains(
                "Owned Excel process 2718 exited with code 0.",
                StringComparison.Ordinal));
        Assert.Equal("terminated", messages[^1].GetProperty("event").GetString());
    }

    [Fact]
    public async Task AnActivePostSaveSnapshotPositionLaunchesWithoutExplicitModuleOrProcedure()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("DebugProject");
        var manifest = ProjectManifest.CreateDefault("DebugProject", "Book1", root, null);
        var manifestStore = new JsonProjectManifestStore();
        manifestStore.Save(root, manifest);
        var sourceSetPath = Path.GetFullPath(Path.Combine(root, manifest.Documents["Book1"].SourcePath));
        Directory.CreateDirectory(sourceSetPath);
        var sourcePath = Path.Combine(sourceSetPath, "DebugModule.bas");
        var source =
            """
            Attribute VB_Name = "DebugModule"

            Public Sub RunTarget()
                Debug.Print "ready"
            End Sub
            """;
        File.WriteAllText(sourcePath, source);
        var events = new List<string>();
        var adapter = new VbaDebugAdapter(
            new ProjectContextResolver(manifestStore),
            new DebugLaunchCoordinator(
                new RecordingDebugWorkbookBuilder(events, []),
                new RecordingVbeDebugSessionFactory(
                    events,
                    new CompletingVbeDebugSession(events, 2719, 0))),
            () => root);
        var launch = JsonSerializer.Serialize(new
        {
            seq = 2,
            type = "request",
            command = "launch",
            arguments = new
            {
                project = root,
                document = "Book1",
                sourceSnapshot = new
                {
                    schemaVersion = 1,
                    sources = new[] { new { path = sourcePath, text = source } },
                    activeSource = new { path = sourcePath, line = 3, character = 4 }
                }
            }
        });
        await using var input = CreateInput(
            """
            {"seq":1,"type":"request","command":"initialize","arguments":{"adapterID":"vba"}}
            """,
            launch,
            """
            {"seq":3,"type":"request","command":"configurationDone","arguments":{}}
            """);
        await using var output = new MemoryStream();

        await adapter.RunAsync(input, output, CancellationToken.None);

        var messages = ReadMessages(output).Select(message => message.RootElement).ToArray();
        Assert.True(Response(messages, "launch").GetProperty("success").GetBoolean());
        Assert.Contains(
            events,
            item => item.EndsWith(":DebugModule.RunTarget", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("args")]
    [InlineData("arguments")]
    [InlineData("noBuild")]
    [InlineData("stopOnEntry")]
    [InlineData("compilerConstants")]
    public async Task KnownUnsupportedLaunchFieldFailsBeforeBuild(string unsupportedField)
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("DebugProject");
        var manifest = ProjectManifest.CreateDefault("DebugProject", "Book1", root, null);
        var manifestStore = new JsonProjectManifestStore();
        manifestStore.Save(root, manifest);
        var sourceSetPath = Path.GetFullPath(Path.Combine(root, manifest.Documents["Book1"].SourcePath));
        Directory.CreateDirectory(sourceSetPath);
        var sourcePath = Path.Combine(sourceSetPath, "DebugModule.bas");
        var source =
            """
            Attribute VB_Name = "DebugModule"

            Public Sub RunTarget()
            End Sub
            """;
        File.WriteAllText(sourcePath, source);
        var events = new List<string>();
        var adapter = new VbaDebugAdapter(
            new ProjectContextResolver(manifestStore),
            new DebugLaunchCoordinator(
                new RecordingDebugWorkbookBuilder(events, []),
                new RecordingVbeDebugSessionFactory(
                    events,
                    new CompletingVbeDebugSession(events, 2720, 0))),
            () => root);
        var launchArguments = new Dictionary<string, object?>
        {
            ["project"] = root,
            ["document"] = "Book1",
            ["module"] = "DebugModule",
            ["procedure"] = "RunTarget",
            ["sourceSnapshot"] = new
            {
                schemaVersion = 1,
                sources = new[] { new { path = sourcePath, text = source } }
            }
        };
        launchArguments[unsupportedField] = unsupportedField switch
        {
            "args" or "arguments" => Array.Empty<string>(),
            "compilerConstants" => new { VBA7 = true },
            _ => true
        };
        var launch = JsonSerializer.Serialize(new
        {
            seq = 2,
            type = "request",
            command = "launch",
            arguments = launchArguments
        });
        await using var input = CreateInput(
            """
            {"seq":1,"type":"request","command":"initialize","arguments":{"adapterID":"vba"}}
            """,
            launch,
            """
            {"seq":3,"type":"request","command":"configurationDone","arguments":{}}
            """);
        await using var output = new MemoryStream();

        await adapter.RunAsync(input, output, CancellationToken.None);

        var messages = ReadMessages(output).Select(message => message.RootElement).ToArray();
        var response = Response(messages, "launch");
        Assert.False(response.GetProperty("success").GetBoolean());
        Assert.Contains(unsupportedField, response.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(events);
    }

    [Fact]
    public async Task UnknownSourceSnapshotPropertyIsRejectedBeforeBuild()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("DebugProject");
        var manifest = ProjectManifest.CreateDefault("DebugProject", "Book1", root, null);
        var manifestStore = new JsonProjectManifestStore();
        manifestStore.Save(root, manifest);
        var sourceSetPath = Path.GetFullPath(Path.Combine(root, manifest.Documents["Book1"].SourcePath));
        Directory.CreateDirectory(sourceSetPath);
        var sourcePath = Path.Combine(sourceSetPath, "DebugModule.bas");
        var source =
            """
            Attribute VB_Name = "DebugModule"

            Public Sub RunTarget()
            End Sub
            """;
        File.WriteAllText(sourcePath, source);
        var events = new List<string>();
        var adapter = new VbaDebugAdapter(
            new ProjectContextResolver(manifestStore),
            new DebugLaunchCoordinator(
                new RecordingDebugWorkbookBuilder(events, []),
                new RecordingVbeDebugSessionFactory(
                    events,
                    new CompletingVbeDebugSession(events, 2721, 0))),
            () => root);
        var launch = JsonSerializer.Serialize(new
        {
            seq = 2,
            type = "request",
            command = "launch",
            arguments = new
            {
                project = root,
                document = "Book1",
                module = "DebugModule",
                procedure = "RunTarget",
                sourceSnapshot = new
                {
                    schemaVersion = 1,
                    sources = new[] { new { path = sourcePath, text = source } },
                    unexpected = true
                }
            }
        });
        await using var input = CreateInput(
            """
            {"seq":1,"type":"request","command":"initialize","arguments":{"adapterID":"vba"}}
            """,
            launch,
            """
            {"seq":3,"type":"request","command":"configurationDone","arguments":{}}
            """);
        await using var output = new MemoryStream();

        await adapter.RunAsync(input, output, CancellationToken.None);

        var messages = ReadMessages(output).Select(message => message.RootElement).ToArray();
        var response = Response(messages, "launch");
        Assert.False(response.GetProperty("success").GetBoolean());
        Assert.Contains("unexpected", response.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(events);
    }

    [Fact]
    public async Task AttachRequestIsExplicitlyRejected()
    {
        var adapter = CreateAdapter();
        await using var input = CreateInput(
            """
            {"seq":1,"type":"request","command":"initialize","arguments":{"adapterID":"vba"}}
            """,
            """
            {"seq":2,"type":"request","command":"attach","arguments":{}}
            """);
        await using var output = new MemoryStream();

        await adapter.RunAsync(input, output, CancellationToken.None);

        var messages = ReadMessages(output).Select(message => message.RootElement).ToArray();
        var response = Response(messages, "attach");
        Assert.False(response.GetProperty("success").GetBoolean());
        Assert.Contains("attach", response.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IneligibleTargetFailsBeforeWorkbookBuild()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("DebugProject");
        var manifest = ProjectManifest.CreateDefault("DebugProject", "Book1", root, null);
        var manifestStore = new JsonProjectManifestStore();
        manifestStore.Save(root, manifest);
        var sourceSetPath = Path.GetFullPath(Path.Combine(root, manifest.Documents["Book1"].SourcePath));
        Directory.CreateDirectory(sourceSetPath);
        var sourcePath = Path.Combine(sourceSetPath, "DebugModule.bas");
        var source =
            """
            Attribute VB_Name = "DebugModule"

            Private Sub RunTarget()
            End Sub
            """;
        File.WriteAllText(sourcePath, source);
        var events = new List<string>();
        var adapter = new VbaDebugAdapter(
            new ProjectContextResolver(manifestStore),
            new DebugLaunchCoordinator(
                new RecordingDebugWorkbookBuilder(events, []),
                new RecordingVbeDebugSessionFactory(
                    events,
                    new CompletingVbeDebugSession(events, 2722, 0))),
            () => root);
        var launch = JsonSerializer.Serialize(new
        {
            seq = 2,
            type = "request",
            command = "launch",
            arguments = new
            {
                project = root,
                document = "Book1",
                module = "DebugModule",
                procedure = "RunTarget",
                sourceSnapshot = new
                {
                    schemaVersion = 1,
                    sources = new[] { new { path = sourcePath, text = source } }
                }
            }
        });
        await using var input = CreateInput(
            """
            {"seq":1,"type":"request","command":"initialize","arguments":{"adapterID":"vba"}}
            """,
            launch,
            """
            {"seq":3,"type":"request","command":"configurationDone","arguments":{}}
            """);
        await using var output = new MemoryStream();

        await adapter.RunAsync(input, output, CancellationToken.None);

        var messages = ReadMessages(output).Select(message => message.RootElement).ToArray();
        var response = Response(messages, "launch");
        Assert.False(response.GetProperty("success").GetBoolean());
        Assert.Contains("public", response.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(events);
    }

    private static JsonElement Response(IReadOnlyList<JsonElement> messages, string command)
        => messages.Single(message =>
            message.GetProperty("type").GetString() == "response" &&
            message.GetProperty("command").GetString() == command);

    private static VbaDebugAdapter CreateAdapter()
        => new(
            new ProjectContextResolver(new UnusedProjectManifestStore()),
            new DebugLaunchCoordinator(
                new UnusedDebugWorkbookBuilder(),
                new UnusedVbeDebugSessionFactory()),
            () => Directory.GetCurrentDirectory());

    private static MemoryStream CreateInput(params string[] jsonMessages)
    {
        var stream = new MemoryStream();
        foreach (var json in jsonMessages)
        {
            var body = Encoding.UTF8.GetBytes(json);
            var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
            stream.Write(header);
            stream.Write(body);
        }

        stream.Position = 0;
        return stream;
    }

    private static IReadOnlyList<JsonDocument> ReadMessages(MemoryStream stream)
    {
        stream.Position = 0;
        var result = new List<JsonDocument>();
        while (stream.Position < stream.Length)
        {
            var header = ReadHeader(stream);
            var prefix = "Content-Length: ";
            Assert.StartsWith(prefix, header, StringComparison.Ordinal);
            var contentLength = int.Parse(header[prefix.Length..], System.Globalization.CultureInfo.InvariantCulture);
            var body = new byte[contentLength];
            stream.ReadExactly(body);
            result.Add(JsonDocument.Parse(body));
        }

        return result;
    }

    private static string ReadHeader(Stream stream)
    {
        var bytes = new List<byte>();
        while (true)
        {
            var next = stream.ReadByte();
            Assert.NotEqual(-1, next);
            bytes.Add((byte)next);
            if (bytes.Count >= 4 &&
                bytes[^4] == '\r' &&
                bytes[^3] == '\n' &&
                bytes[^2] == '\r' &&
                bytes[^1] == '\n')
            {
                return Encoding.ASCII.GetString(bytes[..^4].ToArray());
            }
        }
    }

    private sealed class UnusedProjectManifestStore : IProjectManifestStore
    {
        public ProjectManifest Load(string manifestPath) => throw new InvalidOperationException("Project resolution was not expected.");

        public void Save(string projectRoot, ProjectManifest manifest) => throw new InvalidOperationException("Project persistence was not expected.");
    }

    private sealed class UnusedDebugWorkbookBuilder : IDebugWorkbookBuilder
    {
        public Task<DebugWorkbookBuildResult> BuildAsync(
            ResolvedProjectContext context,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("A build was not expected.");
    }

    private sealed class UnusedVbeDebugSessionFactory : IVbeDebugSessionFactory
    {
        public Task<IVbeDebugSession> StartVisibleAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("Visible Excel was not expected.");
    }

    private sealed class RecordingDebugWorkbookBuilder(
        List<string> events,
        IReadOnlyList<string> output) : IDebugWorkbookBuilder
    {
        public Task<DebugWorkbookBuildResult> BuildAsync(
            ResolvedProjectContext context,
            CancellationToken cancellationToken)
        {
            events.Add($"build:{context.DocumentName}");
            return Task.FromResult(new DebugWorkbookBuildResult(output));
        }
    }

    private sealed class RecordingVbeDebugSessionFactory(
        List<string> events,
        IVbeDebugSession session) : IVbeDebugSessionFactory
    {
        public Task<IVbeDebugSession> StartVisibleAsync(CancellationToken cancellationToken)
        {
            events.Add("start-visible");
            return Task.FromResult(session);
        }
    }

    private sealed class CompletingVbeDebugSession(
        List<string> events,
        int processId,
        int exitCode) : IVbeDebugSession
    {
        public int ProcessId => processId;

        public Task<DebugProcessExit> Completion { get; } =
            Task.FromResult(new DebugProcessExit(exitCode));

        public Task OpenGeneratedWorkbookAndRunAsync(
            string workbookPath,
            DebugTargetProcedure target,
            CancellationToken cancellationToken)
        {
            events.Add($"open-and-run:{workbookPath}:{target.ModuleName}.{target.ProcedureName}");
            return Task.CompletedTask;
        }

        public ValueTask TerminateAsync() => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
