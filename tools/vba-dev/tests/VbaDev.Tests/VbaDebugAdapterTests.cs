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
        var capabilities = initialize.GetProperty("body");
        Assert.True(capabilities.GetProperty("supportsConfigurationDoneRequest").GetBoolean());
        Assert.False(capabilities.GetProperty("supportsConditionalBreakpoints").GetBoolean());
        Assert.False(capabilities.GetProperty("supportsHitConditionalBreakpoints").GetBoolean());
        Assert.False(capabilities.GetProperty("supportsLogPoints").GetBoolean());
        Assert.False(capabilities.GetProperty("supportsFunctionBreakpoints").GetBoolean());
        Assert.False(capabilities.GetProperty("supportsDataBreakpoints").GetBoolean());
        Assert.True(capabilities.GetProperty("supportsTerminateRequest").GetBoolean());
        Assert.Empty(capabilities.GetProperty("exceptionBreakpointFilters").EnumerateArray());
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
    public async Task SourceBreakpointUpdatesReplaceOnlyTheirOwnSource()
    {
        var adapter = CreateAdapter();
        await using var input = CreateInput(
            """
            {"seq":1,"type":"request","command":"initialize","arguments":{"adapterID":"vba"}}
            """,
            """
            {"seq":2,"type":"request","command":"setBreakpoints","arguments":{"source":{"path":"C:\\Project\\First.bas"},"breakpoints":[{"line":2},{"line":4}]}}
            """,
            """
            {"seq":3,"type":"request","command":"setBreakpoints","arguments":{"source":{"path":"C:\\Project\\Second.bas"},"breakpoints":[{"line":3}]}}
            """,
            """
            {"seq":4,"type":"request","command":"setBreakpoints","arguments":{"source":{"path":"C:\\Project\\First.bas"},"breakpoints":[{"line":6}]}}
            """);
        await using var output = new MemoryStream();

        await adapter.RunAsync(input, output, CancellationToken.None);

        var responses = ReadMessages(output)
            .Select(message => message.RootElement)
            .Where(message =>
                message.GetProperty("type").GetString() == "response" &&
                message.GetProperty("command").GetString() == "setBreakpoints")
            .ToArray();
        Assert.Equal(3, responses.Length);
        Assert.Equal(2, responses[0].GetProperty("body").GetProperty("breakpoints").GetArrayLength());
        var secondSource = Assert.Single(
            responses[1].GetProperty("body").GetProperty("breakpoints").EnumerateArray());
        var replacement = Assert.Single(
            responses[2].GetProperty("body").GetProperty("breakpoints").EnumerateArray());
        Assert.Equal(6, replacement.GetProperty("line").GetInt32());
        var ids = responses[0]
            .GetProperty("body")
            .GetProperty("breakpoints")
            .EnumerateArray()
            .Select(item => item.GetProperty("id").GetInt32())
            .Append(secondSource.GetProperty("id").GetInt32())
            .Append(replacement.GetProperty("id").GetInt32())
            .ToArray();
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.All(responses, response => Assert.True(response.GetProperty("success").GetBoolean()));
    }

    [Fact]
    public async Task BreakpointConfigurationAfterConfigurationDoneIsCapturedForRestart()
    {
        var adapter = CreateAdapter();
        await using var input = CreateInput(
            """
            {"seq":1,"type":"request","command":"initialize","arguments":{"adapterID":"vba"}}
            """,
            """
            {"seq":2,"type":"request","command":"configurationDone","arguments":{}}
            """,
            """
            {"seq":3,"type":"request","command":"setBreakpoints","arguments":{"source":{"path":"C:\\Project\\DebugModule.bas"},"breakpoints":[{"line":2}]}}
            """,
            """
            {"seq":4,"type":"request","command":"setFunctionBreakpoints","arguments":{"breakpoints":[{"name":"RunTarget"}]}}
            """);
        await using var output = new MemoryStream();

        await adapter.RunAsync(input, output, CancellationToken.None);

        var messages = ReadMessages(output).Select(message => message.RootElement).ToArray();
        Assert.True(Response(messages, "configurationDone").GetProperty("success").GetBoolean());
        var mutation = Response(messages, "setBreakpoints");
        Assert.True(mutation.GetProperty("success").GetBoolean());
        var pendingBreakpoint = Assert.Single(
            mutation.GetProperty("body").GetProperty("breakpoints").EnumerateArray());
        Assert.False(pendingBreakpoint.GetProperty("verified").GetBoolean());
        var functionMutation = Response(messages, "setFunctionBreakpoints");
        Assert.False(functionMutation.GetProperty("success").GetBoolean());
        Assert.Contains(
            "unsupported",
            functionMutation.GetProperty("message").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OneSavedSourceBreakpointRemainsPendingUntilNativeTransferSucceeds()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("DebugProject");
        var manifest = ProjectManifest.CreateDefault("DebugProject", "Book1", root, null);
        var manifestStore = new JsonProjectManifestStore();
        manifestStore.Save(root, manifest);
        var sourceSetPath = Path.GetFullPath(Path.Combine(root, manifest.Documents["Book1"].SourcePath));
        Directory.CreateDirectory(sourceSetPath);
        var sourcePath = Path.Combine(sourceSetPath, "DebugModule.bas");
        var source = string.Join(
            '\n',
            [
                "Attribute VB_Name = \"DebugModule\"",
                "Option Explicit",
                "",
                "Public Sub RunTarget()",
                "    Debug.Print \"hit\"",
                "End Sub"
            ]);
        File.WriteAllText(sourcePath, source);
        var dapSourcePath = sourcePath.ToUpperInvariant();
        var events = new List<string>();
        var adapter = new VbaDebugAdapter(
            new ProjectContextResolver(manifestStore),
            new DebugLaunchCoordinator(
                new RecordingDebugWorkbookBuilder(events, []),
                new RecordingVbeDebugSessionFactory(
                    events,
                    new CompletingVbeDebugSession(events, 2723, 0))),
            () => root);
        var setBreakpoints = JsonSerializer.Serialize(new
        {
            seq = 2,
            type = "request",
            command = "setBreakpoints",
            arguments = new
            {
                source = new { path = dapSourcePath },
                breakpoints = new[] { new { line = 5 } }
            }
        });
        var launch = JsonSerializer.Serialize(new
        {
            seq = 3,
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
                    breakpoints = new[] { new { path = sourcePath, line = 4 } }
                }
            }
        });
        await using var input = CreateInput(
            """
            {"seq":1,"type":"request","command":"initialize","arguments":{"adapterID":"vba"}}
            """,
            setBreakpoints,
            launch,
            """
            {"seq":4,"type":"request","command":"configurationDone","arguments":{}}
            """);
        await using var output = new MemoryStream();

        await adapter.RunAsync(input, output, CancellationToken.None);

        var messages = ReadMessages(output).Select(message => message.RootElement).ToArray();
        var pending = Response(messages, "setBreakpoints");
        Assert.True(pending.GetProperty("success").GetBoolean());
        var pendingBreakpoint = Assert.Single(
            pending.GetProperty("body").GetProperty("breakpoints").EnumerateArray());
        Assert.False(pendingBreakpoint.GetProperty("verified").GetBoolean());
        Assert.Equal(5, pendingBreakpoint.GetProperty("line").GetInt32());
        Assert.Equal(dapSourcePath, pendingBreakpoint.GetProperty("source").GetProperty("path").GetString());

        var verifiedIndex = Array.FindIndex(messages, message =>
            message.GetProperty("type").GetString() == "event" &&
            message.GetProperty("event").GetString() == "breakpoint");
        var launchIndex = Array.FindIndex(messages, message =>
            message.GetProperty("type").GetString() == "response" &&
            message.GetProperty("command").GetString() == "launch");
        Assert.True(verifiedIndex >= 0 && verifiedIndex < launchIndex);
        var verified = messages[verifiedIndex].GetProperty("body").GetProperty("breakpoint");
        Assert.Equal("changed", messages[verifiedIndex].GetProperty("body").GetProperty("reason").GetString());
        Assert.True(verified.GetProperty("verified").GetBoolean());
        Assert.Equal(pendingBreakpoint.GetProperty("id").GetInt32(), verified.GetProperty("id").GetInt32());
        Assert.Equal(5, verified.GetProperty("line").GetInt32());
        Assert.Equal(dapSourcePath, verified.GetProperty("source").GetProperty("path").GetString());
        Assert.Equal(
            [
                "build:Book1",
                "start-visible",
                $"open:{Path.Combine(root, "bin", "Book1.xlsm")}",
                "set:DebugModule:4:    Debug.Print \"hit\"",
                "run:DebugModule.RunTarget"
            ],
            events);
    }

    [Fact]
    public async Task MultipleSnapshotBreakpointsTransferWhenOneHasNoPendingDapId()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("DebugProject");
        var manifest = ProjectManifest.CreateDefault("DebugProject", "Book1", root, null);
        var manifestStore = new JsonProjectManifestStore();
        manifestStore.Save(root, manifest);
        var sourceSetPath = Path.GetFullPath(Path.Combine(root, manifest.Documents["Book1"].SourcePath));
        Directory.CreateDirectory(sourceSetPath);
        var sourcePath = Path.Combine(sourceSetPath, "DebugModule.bas");
        var source = string.Join(
            '\n',
            [
                "Attribute VB_Name = \"DebugModule\"",
                "Option Explicit",
                "",
                "Public Sub RunTarget()",
                "    Debug.Print \"first\"",
                "    Debug.Print \"second\"",
                "End Sub"
            ]);
        File.WriteAllText(sourcePath, source);
        var events = new List<string>();
        var adapter = new VbaDebugAdapter(
            new ProjectContextResolver(manifestStore),
            new DebugLaunchCoordinator(
                new RecordingDebugWorkbookBuilder(events, []),
                new RecordingVbeDebugSessionFactory(
                    events,
                    new CompletingVbeDebugSession(events, 2728, 0))),
            () => root);
        var setBreakpoints = JsonSerializer.Serialize(new
        {
            seq = 2,
            type = "request",
            command = "setBreakpoints",
            arguments = new
            {
                source = new { path = sourcePath },
                breakpoints = new[] { new { line = 5 } }
            }
        });
        var launch = JsonSerializer.Serialize(new
        {
            seq = 3,
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
                    breakpoints = new[]
                    {
                        new { path = sourcePath, line = 4 },
                        new { path = sourcePath, line = 5 }
                    }
                }
            }
        });
        await using var input = CreateInput(
            """
            {"seq":1,"type":"request","command":"initialize","arguments":{"adapterID":"vba"}}
            """,
            setBreakpoints,
            launch,
            """
            {"seq":4,"type":"request","command":"configurationDone","arguments":{}}
            """);
        await using var output = new MemoryStream();

        await adapter.RunAsync(input, output, CancellationToken.None);

        var messages = ReadMessages(output).Select(message => message.RootElement).ToArray();
        var pending = Response(messages, "setBreakpoints")
            .GetProperty("body")
            .GetProperty("breakpoints");
        Assert.Single(pending.EnumerateArray());
        Assert.Single(messages, message =>
            message.GetProperty("type").GetString() == "event" &&
            message.GetProperty("event").GetString() == "breakpoint");
        Assert.True(Response(messages, "launch").GetProperty("success").GetBoolean());
        Assert.Equal(
            [
                "build:Book1",
                "start-visible",
                $"open:{Path.Combine(root, "bin", "Book1.xlsm")}",
                "set:DebugModule:4:    Debug.Print \"first\"",
                "set:DebugModule:5:    Debug.Print \"second\"",
                "run:DebugModule.RunTarget"
            ],
            events);
    }

    [Theory]
    [InlineData("condition", "Conditional")]
    [InlineData("hitCondition", "HitCondition")]
    [InlineData("logMessage", "Logpoint")]
    [InlineData("column", "Column")]
    [InlineData("mode", "Mode")]
    public async Task InScopeUnsupportedSourceBreakpointFailsBeforeBuild(
        string propertyName,
        string expectedKind)
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
                Debug.Print "hit"
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
                    new CompletingVbeDebugSession(events, 2724, 0))),
            () => root);
        var breakpoint = new Dictionary<string, object?>
        {
            ["line"] = 4,
            [propertyName] = propertyName.Equals("column", StringComparison.Ordinal)
                ? 2
                : "unsupported"
        };
        var ordinaryBreakpoints = JsonSerializer.Serialize(new
        {
            seq = 2,
            type = "request",
            command = "setBreakpoints",
            arguments = new
            {
                source = new { path = sourcePath },
                breakpoints = new[] { new { line = 4 } }
            }
        });
        var setBreakpoints = JsonSerializer.Serialize(new
        {
            seq = 3,
            type = "request",
            command = "setBreakpoints",
            arguments = new
            {
                source = new { path = sourcePath },
                breakpoints = new[] { breakpoint }
            }
        });
        var launch = JsonSerializer.Serialize(new
        {
            seq = 4,
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
                    breakpoints = new[] { new { path = sourcePath, line = 3 } }
                }
            }
        });
        await using var input = CreateInput(
            """
            {"seq":1,"type":"request","command":"initialize","arguments":{"adapterID":"vba"}}
            """,
            ordinaryBreakpoints,
            setBreakpoints,
            launch,
            """
            {"seq":5,"type":"request","command":"configurationDone","arguments":{}}
            """);
        await using var output = new MemoryStream();

        await adapter.RunAsync(input, output, CancellationToken.None);

        var messages = ReadMessages(output).Select(message => message.RootElement).ToArray();
        var setResponses = messages.Where(message =>
                message.GetProperty("type").GetString() == "response" &&
                message.GetProperty("command").GetString() == "setBreakpoints").ToArray();
        Assert.All(setResponses, response => Assert.True(response.GetProperty("success").GetBoolean()));
        Assert.Equal(
            setResponses[0].GetProperty("body").GetProperty("breakpoints")[0].GetProperty("id").GetInt32(),
            setResponses[1].GetProperty("body").GetProperty("breakpoints")[0].GetProperty("id").GetInt32());
        var launchResponse = Response(messages, "launch");
        Assert.False(launchResponse.GetProperty("success").GetBoolean());
        Assert.Contains(expectedKind, launchResponse.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(events);
    }

    [Fact]
    public async Task OutOfScopeAndFrxBreakpointFeaturesDoNotParticipate()
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
                    new CompletingVbeDebugSession(events, 2725, 0))),
            () => root);
        var outsidePath = Path.Combine(root, "OtherProject", "Outside.bas");
        var frxPath = Path.Combine(sourceSetPath, "Dialog.frx");
        var outsideUpdate = JsonSerializer.Serialize(new
        {
            seq = 2,
            type = "request",
            command = "setBreakpoints",
            arguments = new
            {
                source = new { path = outsidePath },
                breakpoints = new[] { new { line = 1, condition = "outside" } }
            }
        });
        var frxUpdate = JsonSerializer.Serialize(new
        {
            seq = 3,
            type = "request",
            command = "setBreakpoints",
            arguments = new
            {
                source = new { path = frxPath },
                breakpoints = new[] { new { line = 1, logMessage = "binary" } }
            }
        });
        var launch = JsonSerializer.Serialize(new
        {
            seq = 4,
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
                    breakpoints = new[]
                    {
                        new { path = outsidePath, line = 0 },
                        new { path = frxPath, line = 0 }
                    }
                }
            }
        });
        await using var input = CreateInput(
            """
            {"seq":1,"type":"request","command":"initialize","arguments":{"adapterID":"vba"}}
            """,
            outsideUpdate,
            frxUpdate,
            launch,
            """
            {"seq":5,"type":"request","command":"configurationDone","arguments":{}}
            """);
        await using var output = new MemoryStream();

        await adapter.RunAsync(input, output, CancellationToken.None);

        var messages = ReadMessages(output).Select(message => message.RootElement).ToArray();
        Assert.All(
            messages.Where(message =>
                message.GetProperty("type").GetString() == "response" &&
                message.GetProperty("command").GetString() == "setBreakpoints"),
            response => Assert.True(response.GetProperty("success").GetBoolean()));
        Assert.True(Response(messages, "launch").GetProperty("success").GetBoolean());
        Assert.Contains("run:DebugModule.RunTarget", events);
        Assert.DoesNotContain(events, item => item.StartsWith("set:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DuplicateInScopeSourceBreakpointsFailBeforeBuild()
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
                Debug.Print "hit"
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
                    new CompletingVbeDebugSession(events, 2726, 0))),
            () => root);
        var setBreakpoints = JsonSerializer.Serialize(new
        {
            seq = 2,
            type = "request",
            command = "setBreakpoints",
            arguments = new
            {
                source = new { path = sourcePath },
                breakpoints = new[] { new { line = 4 }, new { line = 4 } }
            }
        });
        var launch = JsonSerializer.Serialize(new
        {
            seq = 3,
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
                    breakpoints = new[] { new { path = sourcePath, line = 3 } }
                }
            }
        });
        await using var input = CreateInput(
            """
            {"seq":1,"type":"request","command":"initialize","arguments":{"adapterID":"vba"}}
            """,
            setBreakpoints,
            launch,
            """
            {"seq":4,"type":"request","command":"configurationDone","arguments":{}}
            """);
        await using var output = new MemoryStream();

        await adapter.RunAsync(input, output, CancellationToken.None);

        var messages = ReadMessages(output).Select(message => message.RootElement).ToArray();
        var launchResponse = Response(messages, "launch");
        Assert.False(launchResponse.GetProperty("success").GetBoolean());
        Assert.Contains("duplicate", launchResponse.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(events);
    }

    [Theory]
    [InlineData("setFunctionBreakpoints", "{\"breakpoints\":[{\"name\":\"RunTarget\"}]}", "Function")]
    [InlineData("setExceptionBreakpoints", "{\"filters\":[\"all\"]}", "Exception")]
    [InlineData("setExceptionBreakpoints", "{\"filters\":[],\"filterOptions\":[{\"filterId\":\"all\"}]}", "Exception")]
    [InlineData("setExceptionBreakpoints", "{\"filters\":[],\"exceptionOptions\":[{\"breakMode\":\"always\"}]}", "Exception")]
    [InlineData("setDataBreakpoints", "{\"breakpoints\":[{\"dataId\":\"value\"}]}", "Data")]
    [InlineData("setFunctionBreakpoints", "{\"breakpoints\":{\"name\":\"RunTarget\"}}", "Function")]
    [InlineData("setExceptionBreakpoints", "{\"filters\":[],\"filterOptions\":{\"filterId\":\"all\"}}", "Exception")]
    [InlineData("setDataBreakpoints", "{\"breakpoints\":{\"dataId\":\"value\"}}", "Data")]
    public async Task UnsupportedGlobalBreakpointConfigurationFailsRequestAndLaunchBeforeBuild(
        string command,
        string argumentsJson,
        string expectedKind)
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
                    new CompletingVbeDebugSession(events, 2727, 0))),
            () => root);
        using var argumentsDocument = JsonDocument.Parse(argumentsJson);
        var unsupportedRequest = JsonSerializer.Serialize(new
        {
            seq = 2,
            type = "request",
            command,
            arguments = argumentsDocument.RootElement
        });
        var launch = JsonSerializer.Serialize(new
        {
            seq = 3,
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
                    breakpoints = Array.Empty<object>()
                }
            }
        });
        await using var input = CreateInput(
            """
            {"seq":1,"type":"request","command":"initialize","arguments":{"adapterID":"vba"}}
            """,
            unsupportedRequest,
            launch,
            """
            {"seq":4,"type":"request","command":"configurationDone","arguments":{}}
            """);
        await using var output = new MemoryStream();

        await adapter.RunAsync(input, output, CancellationToken.None);

        var messages = ReadMessages(output).Select(message => message.RootElement).ToArray();
        var unsupportedResponse = Response(messages, command);
        Assert.False(unsupportedResponse.GetProperty("success").GetBoolean());
        Assert.Contains(expectedKind, unsupportedResponse.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        var launchResponse = Response(messages, "launch");
        Assert.False(launchResponse.GetProperty("success").GetBoolean());
        Assert.Contains(expectedKind, launchResponse.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(events);
    }

    [Fact]
    public async Task EmptyGlobalBreakpointUpdatesClearTheirLatchesAndDataInfoDoesNotLatch()
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
                    new CompletingVbeDebugSession(events, 2729, 0))),
            () => root);
        var launch = JsonSerializer.Serialize(new
        {
            seq = 9,
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
                    breakpoints = Array.Empty<object>()
                }
            }
        });
        await using var input = CreateInput(
            """
            {"seq":1,"type":"request","command":"initialize","arguments":{"adapterID":"vba"}}
            """,
            """
            {"seq":2,"type":"request","command":"setFunctionBreakpoints","arguments":{"breakpoints":[{"name":"RunTarget"}]}}
            """,
            """
            {"seq":3,"type":"request","command":"setFunctionBreakpoints","arguments":{"breakpoints":[]}}
            """,
            """
            {"seq":4,"type":"request","command":"setExceptionBreakpoints","arguments":{"filters":["all"]}}
            """,
            """
            {"seq":5,"type":"request","command":"setExceptionBreakpoints","arguments":{"filters":[],"filterOptions":[],"exceptionOptions":[]}}
            """,
            """
            {"seq":6,"type":"request","command":"setDataBreakpoints","arguments":{"breakpoints":[{"dataId":"value"}]}}
            """,
            """
            {"seq":7,"type":"request","command":"setDataBreakpoints","arguments":{"breakpoints":[]}}
            """,
            """
            {"seq":8,"type":"request","command":"dataBreakpointInfo","arguments":{"name":"value"}}
            """,
            launch,
            """
            {"seq":10,"type":"request","command":"configurationDone","arguments":{}}
            """);
        await using var output = new MemoryStream();

        await adapter.RunAsync(input, output, CancellationToken.None);

        var messages = ReadMessages(output).Select(message => message.RootElement).ToArray();
        foreach (var command in new[]
        {
            "setFunctionBreakpoints",
            "setExceptionBreakpoints",
            "setDataBreakpoints"
        })
        {
            var responses = messages.Where(message =>
                message.GetProperty("type").GetString() == "response" &&
                message.GetProperty("command").GetString() == command).ToArray();
            Assert.Equal(2, responses.Length);
            Assert.False(responses[0].GetProperty("success").GetBoolean());
            Assert.True(responses[1].GetProperty("success").GetBoolean());
        }

        Assert.False(Response(messages, "dataBreakpointInfo").GetProperty("success").GetBoolean());
        Assert.True(Response(messages, "launch").GetProperty("success").GetBoolean());
        Assert.Contains("run:DebugModule.RunTarget", events);
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
                $"open:{Path.Combine(root, "custom-bin", "GeneratedBook.xlsm")}",
                "run:DebugModule.RunTarget"
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
    public async Task LaunchResponseTransportFailureTerminatesAndDisposesTheOwnedSession()
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
        var vbeSession = new RunningVbeDebugSession(events)
        {
            TerminateError = new InvalidOperationException("Synthetic cleanup termination failure."),
            ExitOnDispose = true
        };
        var adapter = new VbaDebugAdapter(
            new ProjectContextResolver(manifestStore),
            new DebugLaunchCoordinator(
                new RecordingDebugWorkbookBuilder(events, []),
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
        await using var inputPrefix = CreateInput(
            """
            {"seq":1,"type":"request","command":"initialize","arguments":{"adapterID":"vba"}}
            """,
            launch,
            """
            {"seq":3,"type":"request","command":"configurationDone","arguments":{}}
            """);
        await using var input = new PendingAfterPrefixInputStream(inputPrefix.ToArray());
        await using var output = new DapResponseFailingStream("launch");
        using var adapterCancellation = new CancellationTokenSource();
        var runTask = adapter.RunAsync(input, output, adapterCancellation.Token);

        try
        {
            var completed = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(1)));
            Assert.Same(runTask, completed);
            await Assert.ThrowsAsync<IOException>(() => runTask);
        }
        finally
        {
            adapterCancellation.Cancel();
            try
            {
                await runTask;
            }
            catch
            {
            }
        }

        Assert.True(vbeSession.Terminated);
        Assert.True(vbeSession.Disposed);
    }

    [Fact]
    public async Task MonitorTransportFailureEndsTheAdapterWithoutWaitingForMoreInput()
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
        var vbeSession = new RunningVbeDebugSession(events, processId: 2720);
        var adapter = new VbaDebugAdapter(
            new ProjectContextResolver(manifestStore),
            new DebugLaunchCoordinator(
                new RecordingDebugWorkbookBuilder(events, []),
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
        await using var inputPrefix = CreateInput(
            """
            {"seq":1,"type":"request","command":"initialize","arguments":{"adapterID":"vba"}}
            """,
            launch,
            """
            {"seq":3,"type":"request","command":"configurationDone","arguments":{}}
            """);
        await using var input = new PendingAfterPrefixInputStream(inputPrefix.ToArray());
        await using var output = new MonitorEventFailingStream();
        using var adapterCancellation = new CancellationTokenSource();
        var runTask = adapter.RunAsync(input, output, adapterCancellation.Token);

        try
        {
            await output.LaunchResponseWritten.WaitAsync(TimeSpan.FromSeconds(5));
            vbeSession.Exit(91);
            var completed = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(1)));
            Assert.Same(runTask, completed);
            await Assert.ThrowsAsync<IOException>(() => runTask);
        }
        finally
        {
            adapterCancellation.Cancel();
            try
            {
                await runTask;
            }
            catch
            {
            }
        }

        Assert.True(vbeSession.Disposed);
    }

    [Fact]
    public async Task RestartResponseTransportFailureEndsTheAdapterWithoutWaitingForMoreInput()
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
        var firstSession = new RunningVbeDebugSession(events, processId: 2721);
        var secondSession = new RunningVbeDebugSession(events, processId: 2722)
        {
            ExitOnDispose = true
        };
        var adapter = new VbaDebugAdapter(
            new ProjectContextResolver(manifestStore),
            new DebugLaunchCoordinator(
                new RecordingDebugWorkbookBuilder(events, []),
                new SequenceVbeDebugSessionFactory(events, firstSession, secondSession)),
            () => root);
        var launchArguments = new
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
        };
        var launch = JsonSerializer.Serialize(new
        {
            seq = 2,
            type = "request",
            command = "launch",
            arguments = launchArguments
        });
        var restart = JsonSerializer.Serialize(new
        {
            seq = 4,
            type = "request",
            command = "restart",
            arguments = new { arguments = launchArguments }
        });
        await using var inputPrefix = CreateInput(
            """
            {"seq":1,"type":"request","command":"initialize","arguments":{"adapterID":"vba"}}
            """,
            launch,
            """
            {"seq":3,"type":"request","command":"configurationDone","arguments":{}}
            """,
            restart);
        await using var input = new PendingAfterPrefixInputStream(inputPrefix.ToArray());
        await using var output = new DapResponseFailingStream("restart");
        using var adapterCancellation = new CancellationTokenSource();
        var runTask = adapter.RunAsync(input, output, adapterCancellation.Token);

        try
        {
            var completed = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(1)));
            Assert.Same(runTask, completed);
            await Assert.ThrowsAsync<IOException>(() => runTask);
        }
        finally
        {
            adapterCancellation.Cancel();
            try
            {
                await runTask;
            }
            catch
            {
            }
        }

        Assert.True(firstSession.Terminated);
        Assert.True(firstSession.Disposed);
        Assert.True(secondSession.Terminated);
        Assert.True(secondSession.Disposed);
    }

    [Fact]
    public async Task DisconnectTerminationFailureStillDisposesContainmentAndEndsTheProtocol()
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
        var vbeSession = new RunningVbeDebugSession(events)
        {
            TerminateError = new InvalidOperationException("Synthetic termination failure."),
            ExitOnDispose = true
        };
        var adapter = new VbaDebugAdapter(
            new ProjectContextResolver(manifestStore),
            new DebugLaunchCoordinator(
                new RecordingDebugWorkbookBuilder(events, []),
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
            """,
            """
            {"seq":4,"type":"request","command":"disconnect","arguments":{"terminateDebuggee":true}}
            """);
        await using var output = new MemoryStream();

        await adapter.RunAsync(input, output, CancellationToken.None);

        Assert.True(vbeSession.Terminated);
        Assert.True(vbeSession.Disposed);
        var messages = ReadMessages(output).Select(message => message.RootElement).ToArray();
        Assert.True(Response(messages, "disconnect").GetProperty("success").GetBoolean());
        Assert.Equal(
            1,
            messages.Count(message =>
                message.GetProperty("type").GetString() == "event" &&
                message.GetProperty("event").GetString() == "terminated"));
    }

    [Fact]
    public async Task RestartUsesFreshArgumentsAndRunsTheFullPipelineAfterTheOldSessionEnds()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("DebugProject");
        var manifest = ProjectManifest.CreateDefault("DebugProject", "Book1", root, null);
        var manifestStore = new JsonProjectManifestStore();
        manifestStore.Save(root, manifest);
        var sourceSetPath = Path.GetFullPath(Path.Combine(root, manifest.Documents["Book1"].SourcePath));
        Directory.CreateDirectory(sourceSetPath);
        var sourcePath = Path.Combine(sourceSetPath, "DebugModule.bas");
        var firstSource =
            """
            Attribute VB_Name = "DebugModule"

            Public Sub RunTarget()
                Debug.Print "first"
            End Sub
            """;
        var secondSource = firstSource.Replace("first", "second", StringComparison.Ordinal);
        File.WriteAllText(sourcePath, firstSource);
        var events = new List<string>();
        var snapshots = new List<string>();
        var firstSession = new RunningVbeDebugSession(events, processId: 16181);
        var secondSession = new RunningVbeDebugSession(events, processId: 16182);
        var adapter = new VbaDebugAdapter(
            new ProjectContextResolver(manifestStore),
            new DebugLaunchCoordinator(
                new SnapshotRecordingDebugWorkbookBuilder(events, snapshots),
                new SequenceVbeDebugSessionFactory(events, firstSession, secondSession)),
            () => root);
        object LaunchArguments(string sourceText) => new
        {
            project = root,
            document = "Book1",
            module = "DebugModule",
            procedure = "RunTarget",
            __vbaRestartPreparation = new
            {
                protocolVersion = 1,
                id = "debug-restart-preparation"
            },
            sourceSnapshot = new
            {
                schemaVersion = 1,
                sources = new[] { new { path = sourcePath, text = sourceText } }
            }
        };
        var launch = JsonSerializer.Serialize(new
        {
            seq = 2,
            type = "request",
            command = "launch",
            arguments = LaunchArguments(firstSource)
        });
        var restart = JsonSerializer.Serialize(new
        {
            seq = 4,
            type = "request",
            command = "restart",
            arguments = new { arguments = LaunchArguments(firstSource) }
        });
        await using var prefix = CreateInput(
            """
            {"seq":1,"type":"request","command":"initialize","arguments":{"adapterID":"vba"}}
            """,
            launch,
            """
            {"seq":3,"type":"request","command":"configurationDone","arguments":{}}
            """,
            restart);
        await using var suffix = CreateInput(
            """
            {"seq":5,"type":"request","command":"vba/restartPrepared","arguments":{"restartRequestSequence":999,"preparationId":"debug-restart-preparation","success":true}}
            """,
            """
            {"seq":6,"type":"request","command":"vba/restartPrepared","arguments":{"restartRequestSequence":4,"preparationId":"debug-restart-preparation","success":true}}
            """,
            """
            {"seq":7,"type":"request","command":"disconnect","arguments":{"terminateDebuggee":true}}
            """);
        await using var input = new CallbackBetweenBuffersInputStream(
            prefix.ToArray(),
            suffix.ToArray(),
            () => File.WriteAllText(sourcePath, secondSource));
        await using var output = new MemoryStream();

        await adapter.RunAsync(input, output, CancellationToken.None);

        var messages = ReadMessages(output).Select(message => message.RootElement).ToArray();
        var initialize = Response(messages, "initialize");
        Assert.True(initialize.GetProperty("body").GetProperty("supportsRestartRequest").GetBoolean());
        var stalePreparationResponse = messages.Single(message =>
            message.GetProperty("type").GetString() == "response" &&
            message.GetProperty("request_seq").GetInt32() == 5);
        Assert.False(stalePreparationResponse.GetProperty("success").GetBoolean());
        var acceptedPreparationResponse = messages.Single(message =>
            message.GetProperty("type").GetString() == "response" &&
            message.GetProperty("request_seq").GetInt32() == 6);
        Assert.True(acceptedPreparationResponse.GetProperty("success").GetBoolean());
        var restartResponse = Response(messages, "restart");
        Assert.True(restartResponse.GetProperty("success").GetBoolean());
        Assert.True(
            Array.IndexOf(messages, acceptedPreparationResponse) <
            Array.IndexOf(messages, restartResponse));
        Assert.Equal([firstSource, secondSource], snapshots);
        Assert.Equal(2, events.Count(item => item == "build:Book1"));
        Assert.True(firstSession.Terminated);
        Assert.True(firstSession.Disposed);
        Assert.True(secondSession.Terminated);
        Assert.True(secondSession.Disposed);
        Assert.True(
            events.IndexOf("dispose:16181") < events.IndexOf("start-visible:16182"),
            string.Join(Environment.NewLine, events));
        Assert.Equal(
            1,
            messages.Count(message =>
                message.GetProperty("type").GetString() == "event" &&
                message.GetProperty("event").GetString() == "terminated"));
    }

    [Fact]
    public async Task OldExcelExitDuringRestartPreparationPreventsAFreshLaunch()
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
        var firstSession = new RunningVbeDebugSession(events, processId: 16186);
        var unusedSecondSession = new RunningVbeDebugSession(events, processId: 16187);
        var adapter = new VbaDebugAdapter(
            new ProjectContextResolver(manifestStore),
            new DebugLaunchCoordinator(
                new RecordingDebugWorkbookBuilder(events, []),
                new SequenceVbeDebugSessionFactory(events, firstSession, unusedSecondSession)),
            () => root);
        var launchArguments = new
        {
            project = root,
            document = "Book1",
            module = "DebugModule",
            procedure = "RunTarget",
            __vbaRestartPreparation = new
            {
                protocolVersion = 1,
                id = "debug-restart-preparation"
            },
            sourceSnapshot = new
            {
                schemaVersion = 1,
                sources = new[] { new { path = sourcePath, text = source } }
            }
        };
        var launch = JsonSerializer.Serialize(new
        {
            seq = 2,
            type = "request",
            command = "launch",
            arguments = launchArguments
        });
        var restart = JsonSerializer.Serialize(new
        {
            seq = 4,
            type = "request",
            command = "restart",
            arguments = new { arguments = launchArguments }
        });
        await using var prefix = CreateInput(
            """
            {"seq":1,"type":"request","command":"initialize","arguments":{"adapterID":"vba"}}
            """,
            launch,
            """
            {"seq":3,"type":"request","command":"configurationDone","arguments":{}}
            """,
            restart);
        await using var suffix = CreateInput(
            """
            {"seq":5,"type":"request","command":"vba/restartPrepared","arguments":{"restartRequestSequence":4,"preparationId":"debug-restart-preparation","success":true}}
            """,
            """
            {"seq":6,"type":"request","command":"disconnect","arguments":{"terminateDebuggee":true}}
            """);
        await using var output = new MemoryStream();
        await using var input = new CallbackBetweenBuffersInputStream(
            prefix.ToArray(),
            suffix.ToArray(),
            () => firstSession.Exit(23));
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await adapter.RunAsync(input, output, testTimeout.Token);

        var messages = ReadMessages(output).Select(message => message.RootElement).ToArray();
        Assert.True(Response(messages, "vba/restartPrepared").GetProperty("success").GetBoolean());
        var restartResponse = Response(messages, "restart");
        Assert.False(restartResponse.GetProperty("success").GetBoolean());
        Assert.Contains(
            "exited",
            restartResponse.GetProperty("message").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.True(Response(messages, "disconnect").GetProperty("success").GetBoolean());
        Assert.True(
            Array.IndexOf(messages, restartResponse) <
            Array.IndexOf(messages, Response(messages, "disconnect")));
        Assert.Equal(1, events.Count(item => item == "build:Book1"));
        Assert.True(firstSession.Disposed);
        Assert.False(unusedSecondSession.Terminated);
        Assert.False(unusedSecondSession.Disposed);
        Assert.Single(messages, message =>
            message.GetProperty("type").GetString() == "event" &&
            message.GetProperty("event").GetString() == "terminated");
    }

    [Fact]
    public async Task RestartPreparationFailureLeavesTheOldSessionRunningUntilDisconnect()
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
        var firstSession = new RunningVbeDebugSession(events, processId: 16188);
        var unusedSecondSession = new RunningVbeDebugSession(events, processId: 16189);
        var adapter = new VbaDebugAdapter(
            new ProjectContextResolver(manifestStore),
            new DebugLaunchCoordinator(
                new RecordingDebugWorkbookBuilder(events, []),
                new SequenceVbeDebugSessionFactory(events, firstSession, unusedSecondSession)),
            () => root);
        var launchArguments = new
        {
            project = root,
            document = "Book1",
            module = "DebugModule",
            procedure = "RunTarget",
            __vbaRestartPreparation = new
            {
                protocolVersion = 1,
                id = "debug-restart-preparation"
            },
            sourceSnapshot = new
            {
                schemaVersion = 1,
                sources = new[] { new { path = sourcePath, text = source } }
            }
        };
        var launch = JsonSerializer.Serialize(new
        {
            seq = 2,
            type = "request",
            command = "launch",
            arguments = launchArguments
        });
        var restart = JsonSerializer.Serialize(new
        {
            seq = 4,
            type = "request",
            command = "restart",
            arguments = new { arguments = launchArguments }
        });
        await using var input = CreateInput(
            """
            {"seq":1,"type":"request","command":"initialize","arguments":{"adapterID":"vba"}}
            """,
            launch,
            """
            {"seq":3,"type":"request","command":"configurationDone","arguments":{}}
            """,
            restart,
            """
            {"seq":5,"type":"request","command":"vba/restartPrepared","arguments":{"restartRequestSequence":4,"preparationId":"debug-restart-preparation","success":false,"message":"The selected VBA source could not be saved."}}
            """,
            """
            {"seq":6,"type":"request","command":"disconnect","arguments":{"terminateDebuggee":true}}
            """);
        await using var output = new MemoryStream();

        await adapter.RunAsync(input, output, CancellationToken.None);

        var messages = ReadMessages(output).Select(message => message.RootElement).ToArray();
        Assert.True(Response(messages, "vba/restartPrepared").GetProperty("success").GetBoolean());
        var restartResponse = Response(messages, "restart");
        Assert.False(restartResponse.GetProperty("success").GetBoolean());
        Assert.Contains(
            "could not be saved",
            restartResponse.GetProperty("message").GetString(),
            StringComparison.Ordinal);
        Assert.True(Response(messages, "disconnect").GetProperty("success").GetBoolean());
        Assert.Equal(1, events.Count(item => item == "build:Book1"));
        Assert.True(firstSession.Terminated);
        Assert.True(firstSession.Disposed);
        Assert.False(unusedSecondSession.Terminated);
        Assert.False(unusedSecondSession.Disposed);
    }

    [Theory]
    [InlineData(
        "{\"restartRequestSequence\":4,\"preparationId\":\"other-preparation\",\"success\":true}",
        "identity")]
    [InlineData(
        "{\"restartRequestSequence\":4,\"preparationId\":\"debug-restart-preparation\"}",
        "Boolean 'success'")]
    public async Task MatchingInvalidRestartPreparationFailsAndClearsTheOriginalRestart(
        string preparationResult,
        string expectedMessage)
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
        var firstSession = new RunningVbeDebugSession(events, processId: 16192);
        var unusedSecondSession = new RunningVbeDebugSession(events, processId: 16193);
        var adapter = new VbaDebugAdapter(
            new ProjectContextResolver(manifestStore),
            new DebugLaunchCoordinator(
                new RecordingDebugWorkbookBuilder(events, []),
                new SequenceVbeDebugSessionFactory(events, firstSession, unusedSecondSession)),
            () => root);
        var launchArguments = new
        {
            project = root,
            document = "Book1",
            module = "DebugModule",
            procedure = "RunTarget",
            __vbaRestartPreparation = new
            {
                protocolVersion = 1,
                id = "debug-restart-preparation"
            },
            sourceSnapshot = new
            {
                schemaVersion = 1,
                sources = new[] { new { path = sourcePath, text = source } }
            }
        };
        var launch = JsonSerializer.Serialize(new
        {
            seq = 2,
            type = "request",
            command = "launch",
            arguments = launchArguments
        });
        var restart = JsonSerializer.Serialize(new
        {
            seq = 4,
            type = "request",
            command = "restart",
            arguments = new { arguments = launchArguments }
        });
        var prepared =
            $"{{\"seq\":5,\"type\":\"request\",\"command\":\"vba/restartPrepared\",\"arguments\":{preparationResult}}}";
        await using var input = CreateInput(
            """
            {"seq":1,"type":"request","command":"initialize","arguments":{"adapterID":"vba"}}
            """,
            launch,
            """
            {"seq":3,"type":"request","command":"configurationDone","arguments":{}}
            """,
            restart,
            prepared,
            """
            {"seq":6,"type":"request","command":"disconnect","arguments":{"terminateDebuggee":true}}
            """);
        await using var output = new MemoryStream();

        await adapter.RunAsync(input, output, CancellationToken.None);

        var messages = ReadMessages(output).Select(message => message.RootElement).ToArray();
        var preparedResponse = Response(messages, "vba/restartPrepared");
        Assert.False(preparedResponse.GetProperty("success").GetBoolean());
        Assert.Contains(
            expectedMessage,
            preparedResponse.GetProperty("message").GetString(),
            StringComparison.OrdinalIgnoreCase);
        var restartResponse = Response(messages, "restart");
        Assert.False(restartResponse.GetProperty("success").GetBoolean());
        Assert.Contains(
            expectedMessage,
            restartResponse.GetProperty("message").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.True(Response(messages, "disconnect").GetProperty("success").GetBoolean());
        Assert.Equal(1, events.Count(item => item == "build:Book1"));
        Assert.True(firstSession.Terminated);
        Assert.True(firstSession.Disposed);
        Assert.False(unusedSecondSession.Terminated);
        Assert.False(unusedSecondSession.Disposed);
    }

    [Fact]
    public async Task DisconnectCancelsAPendingRestartPreparationWithoutStartingFreshExcel()
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
        var firstSession = new RunningVbeDebugSession(events, processId: 16190);
        var unusedSecondSession = new RunningVbeDebugSession(events, processId: 16191);
        var adapter = new VbaDebugAdapter(
            new ProjectContextResolver(manifestStore),
            new DebugLaunchCoordinator(
                new RecordingDebugWorkbookBuilder(events, []),
                new SequenceVbeDebugSessionFactory(events, firstSession, unusedSecondSession)),
            () => root);
        var launchArguments = new
        {
            project = root,
            document = "Book1",
            module = "DebugModule",
            procedure = "RunTarget",
            __vbaRestartPreparation = new
            {
                protocolVersion = 1,
                id = "debug-restart-preparation"
            },
            sourceSnapshot = new
            {
                schemaVersion = 1,
                sources = new[] { new { path = sourcePath, text = source } }
            }
        };
        var launch = JsonSerializer.Serialize(new
        {
            seq = 2,
            type = "request",
            command = "launch",
            arguments = launchArguments
        });
        var restart = JsonSerializer.Serialize(new
        {
            seq = 4,
            type = "request",
            command = "restart",
            arguments = new { arguments = launchArguments }
        });
        await using var input = CreateInput(
            """
            {"seq":1,"type":"request","command":"initialize","arguments":{"adapterID":"vba"}}
            """,
            launch,
            """
            {"seq":3,"type":"request","command":"configurationDone","arguments":{}}
            """,
            restart,
            """
            {"seq":5,"type":"request","command":"threads","arguments":{}}
            """,
            """
            {"seq":6,"type":"request","command":"disconnect","arguments":{"terminateDebuggee":true}}
            """);
        await using var output = new MemoryStream();

        await adapter.RunAsync(input, output, CancellationToken.None);

        var messages = ReadMessages(output).Select(message => message.RootElement).ToArray();
        var restartResponse = Response(messages, "restart");
        Assert.False(restartResponse.GetProperty("success").GetBoolean());
        Assert.Contains(
            "cancelled",
            restartResponse.GetProperty("message").GetString(),
            StringComparison.Ordinal);
        Assert.True(Response(messages, "threads").GetProperty("success").GetBoolean());
        var disconnectResponse = Response(messages, "disconnect");
        Assert.True(disconnectResponse.GetProperty("success").GetBoolean());
        Assert.True(
            Array.IndexOf(messages, restartResponse) <
            Array.IndexOf(messages, disconnectResponse));
        Assert.Equal(1, events.Count(item => item == "build:Book1"));
        Assert.True(firstSession.Terminated);
        Assert.True(firstSession.Disposed);
        Assert.False(unusedSecondSession.Terminated);
        Assert.False(unusedSecondSession.Disposed);
    }

    [Fact]
    public async Task RestartDoesNotCancelAStartedExitMonitorFrameOrEndTheAdapter()
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
        var firstSession = new RunningVbeDebugSession(events, processId: 16186);
        var secondSession = new RunningVbeDebugSession(events, processId: 16187);
        var adapter = new VbaDebugAdapter(
            new ProjectContextResolver(manifestStore),
            new DebugLaunchCoordinator(
                new RecordingDebugWorkbookBuilder(events, []),
                new SequenceVbeDebugSessionFactory(events, firstSession, secondSession)),
            () => root);
        var launchArguments = new
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
        };
        var launch = JsonSerializer.Serialize(new
        {
            seq = 2,
            type = "request",
            command = "launch",
            arguments = launchArguments
        });
        var restart = JsonSerializer.Serialize(new
        {
            seq = 4,
            type = "request",
            command = "restart",
            arguments = new { arguments = launchArguments }
        });
        await using var prefix = CreateInput(
            """
            {"seq":1,"type":"request","command":"initialize","arguments":{"adapterID":"vba"}}
            """,
            launch,
            """
            {"seq":3,"type":"request","command":"configurationDone","arguments":{}}
            """);
        await using var suffix = CreateInput(
            restart,
            """
            {"seq":5,"type":"request","command":"disconnect","arguments":{"terminateDebuggee":true}}
            """);
        await using var output = new StartedExitMonitorFrameStream();
        await using var input = new RestartAfterMonitorWriteInputStream(
            prefix.ToArray(),
            suffix.ToArray(),
            () => firstSession.Exit(0),
            output.MonitorWriteStarted);
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await adapter.RunAsync(input, output, testTimeout.Token);

        Assert.False(output.MonitorWriteCancellationObserved);
        Assert.True(output.MonitorFrameCompleted);
        Assert.True(firstSession.Disposed);
        Assert.True(secondSession.Terminated);
        Assert.True(secondSession.Disposed);
        var messages = ReadMessages(output).Select(message => message.RootElement).ToArray();
        Assert.Contains(
            messages,
            message =>
                message.GetProperty("type").GetString() == "event" &&
                message.GetProperty("event").GetString() == "output" &&
                message.GetProperty("body").GetProperty("output").GetString()!.Contains(
                    "Owned Excel process 16186 exited",
                    StringComparison.Ordinal));
        Assert.True(Response(messages, "restart").GetProperty("success").GetBoolean());
        Assert.True(Response(messages, "disconnect").GetProperty("success").GetBoolean());
        Assert.Equal(
            1,
            messages.Count(message =>
                message.GetProperty("type").GetString() == "event" &&
                message.GetProperty("event").GetString() == "terminated"));
    }

    [Fact]
    public async Task BreakpointChangesAfterConfigurationDoneApplyOnlyToTheRestartedSession()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("DebugProject");
        var manifest = ProjectManifest.CreateDefault("DebugProject", "Book1", root, null);
        var manifestStore = new JsonProjectManifestStore();
        manifestStore.Save(root, manifest);
        var sourceSetPath = Path.GetFullPath(Path.Combine(root, manifest.Documents["Book1"].SourcePath));
        Directory.CreateDirectory(sourceSetPath);
        var sourcePath = Path.Combine(sourceSetPath, "DebugModule.bas");
        var source = string.Join(
            '\n',
            [
                "Attribute VB_Name = \"DebugModule\"",
                string.Empty,
                "Public Sub RunTarget()",
                "    Debug.Print \"first\"",
                "    Debug.Print \"restart breakpoint\"",
                "End Sub"
            ]);
        File.WriteAllText(sourcePath, source);
        var events = new List<string>();
        var firstSession = new RunningVbeDebugSession(events, processId: 16184);
        var secondSession = new RunningVbeDebugSession(events, processId: 16185);
        var adapter = new VbaDebugAdapter(
            new ProjectContextResolver(manifestStore),
            new DebugLaunchCoordinator(
                new RecordingDebugWorkbookBuilder(events, []),
                new SequenceVbeDebugSessionFactory(events, firstSession, secondSession)),
            () => root);
        var launchArguments = new
        {
            project = root,
            document = "Book1",
            module = "DebugModule",
            procedure = "RunTarget",
            sourceSnapshot = new
            {
                schemaVersion = 1,
                sources = new[] { new { path = sourcePath, text = source } },
                breakpoints = Array.Empty<object>()
            }
        };
        var launch = JsonSerializer.Serialize(new
        {
            seq = 2,
            type = "request",
            command = "launch",
            arguments = launchArguments
        });
        var restart = JsonSerializer.Serialize(new
        {
            seq = 5,
            type = "request",
            command = "restart",
            arguments = new { arguments = launchArguments }
        });
        var breakpointUpdate = JsonSerializer.Serialize(new
        {
            seq = 4,
            type = "request",
            command = "setBreakpoints",
            arguments = new
            {
                source = new { path = sourcePath },
                breakpoints = new[] { new { line = 5 } }
            }
        });
        await using var input = CreateInput(
            """
            {"seq":1,"type":"request","command":"initialize","arguments":{"adapterID":"vba"}}
            """,
            launch,
            """
            {"seq":3,"type":"request","command":"configurationDone","arguments":{}}
            """,
            breakpointUpdate,
            restart,
            """
            {"seq":6,"type":"request","command":"disconnect","arguments":{"terminateDebuggee":true}}
            """);
        await using var output = new MemoryStream();

        await adapter.RunAsync(input, output, CancellationToken.None);

        var messages = ReadMessages(output).Select(message => message.RootElement).ToArray();
        Assert.True(Response(messages, "setBreakpoints").GetProperty("success").GetBoolean());
        Assert.True(Response(messages, "restart").GetProperty("success").GetBoolean());
        var nativeSet = Assert.Single(
            events,
            item => item.StartsWith("set:", StringComparison.Ordinal));
        Assert.Equal("set:DebugModule:4:    Debug.Print \"restart breakpoint\"", nativeSet);
        Assert.True(
            events.IndexOf("start-visible:16185") < events.IndexOf(nativeSet),
            string.Join(Environment.NewLine, events));
    }

    [Fact]
    public async Task DisconnectDuringBuildCancelsBeforeVisibleExcelStarts()
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
        var builder = new FirstBuildCancellationDebugWorkbookBuilder(events);
        var adapter = new VbaDebugAdapter(
            new ProjectContextResolver(manifestStore),
            new DebugLaunchCoordinator(
                builder,
                new UnusedVbeDebugSessionFactory()),
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
            """,
            """
            {"seq":4,"type":"request","command":"disconnect","arguments":{"terminateDebuggee":true}}
            """);
        await using var output = new MemoryStream();

        await adapter.RunAsync(input, output, CancellationToken.None);

        var messages = ReadMessages(output).Select(message => message.RootElement).ToArray();
        Assert.True(Response(messages, "disconnect").GetProperty("success").GetBoolean());
        Assert.False(Response(messages, "launch").GetProperty("success").GetBoolean());
        Assert.True(builder.FirstBuildCancellationObserved);
        Assert.Equal(["build:1", "cancel-build:1"], events);
        Assert.Equal(
            1,
            messages.Count(message =>
                message.GetProperty("type").GetString() == "event" &&
                message.GetProperty("event").GetString() == "terminated"));
    }

    [Fact]
    public async Task RestartDuringBuildCancelsTheOldBuildBeforeStartingTheFreshPipeline()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("DebugProject");
        var manifest = ProjectManifest.CreateDefault("DebugProject", "Book1", root, null);
        var manifestStore = new JsonProjectManifestStore();
        manifestStore.Save(root, manifest);
        var sourceSetPath = Path.GetFullPath(Path.Combine(root, manifest.Documents["Book1"].SourcePath));
        Directory.CreateDirectory(sourceSetPath);
        var sourcePath = Path.Combine(sourceSetPath, "DebugModule.bas");
        var firstSource =
            """
            Attribute VB_Name = "DebugModule"

            Public Sub RunTarget()
                Debug.Print "first"
            End Sub
            """;
        var secondSource = firstSource.Replace("first", "second", StringComparison.Ordinal);
        File.WriteAllText(sourcePath, secondSource);
        var events = new List<string>();
        var snapshots = new List<string>();
        var builder = new FirstBuildCancellationDebugWorkbookBuilder(events, snapshots);
        var session = new RunningVbeDebugSession(events, processId: 16183);
        var adapter = new VbaDebugAdapter(
            new ProjectContextResolver(manifestStore),
            new DebugLaunchCoordinator(
                builder,
                new RecordingVbeDebugSessionFactory(events, session)),
            () => root);
        object LaunchArguments(string sourceText) => new
        {
            project = root,
            document = "Book1",
            module = "DebugModule",
            procedure = "RunTarget",
            sourceSnapshot = new
            {
                schemaVersion = 1,
                sources = new[] { new { path = sourcePath, text = sourceText } }
            }
        };
        var launch = JsonSerializer.Serialize(new
        {
            seq = 2,
            type = "request",
            command = "launch",
            arguments = LaunchArguments(firstSource)
        });
        var restart = JsonSerializer.Serialize(new
        {
            seq = 4,
            type = "request",
            command = "restart",
            arguments = new { arguments = LaunchArguments(secondSource) }
        });
        await using var input = CreateInput(
            """
            {"seq":1,"type":"request","command":"initialize","arguments":{"adapterID":"vba"}}
            """,
            launch,
            """
            {"seq":3,"type":"request","command":"configurationDone","arguments":{}}
            """,
            restart,
            """
            {"seq":5,"type":"request","command":"disconnect","arguments":{"terminateDebuggee":true}}
            """);
        await using var output = new MemoryStream();

        await adapter.RunAsync(input, output, CancellationToken.None);

        var messages = ReadMessages(output).Select(message => message.RootElement).ToArray();
        Assert.True(Response(messages, "restart").GetProperty("success").GetBoolean());
        Assert.True(builder.FirstBuildCancellationObserved);
        Assert.Equal([firstSource, secondSource], snapshots);
        Assert.Equal(2, builder.BuildCalls);
        Assert.Equal(
            ["build:1", "cancel-build:1", "build:2", "start-visible", $"open:{Path.Combine(root, "bin", "Book1.xlsm")}", "run:DebugModule.RunTarget", "terminate:16183", "dispose:16183"],
            events);
    }

    [Fact]
    public async Task DisconnectStopsTheOwnedProcessWhileWorkbookOpenIsWaitingForModalInput()
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
        var vbeSession = new ModalWaitingVbeDebugSession(events);
        var adapter = new VbaDebugAdapter(
            new ProjectContextResolver(manifestStore),
            new DebugLaunchCoordinator(
                new RecordingDebugWorkbookBuilder(events, []),
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
            """,
            """
            {"seq":4,"type":"request","command":"disconnect","arguments":{"terminateDebuggee":true}}
            """);
        await using var output = new MemoryStream();
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await adapter.RunAsync(input, output, testTimeout.Token);

        Assert.True(vbeSession.Terminated);
        Assert.True(vbeSession.Disposed);
        Assert.DoesNotContain(events, entry => entry.StartsWith("run:", StringComparison.Ordinal));
        var messages = ReadMessages(output).Select(message => message.RootElement).ToArray();
        Assert.True(Response(messages, "disconnect").GetProperty("success").GetBoolean());
        Assert.Contains(messages, message =>
            message.GetProperty("type").GetString() == "event" &&
            message.GetProperty("event").GetString() == "output" &&
            message.GetProperty("body").GetProperty("output").GetString()!.Contains(
                "waiting for Excel input",
                StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(messages, message =>
            message.GetProperty("type").GetString() == "event" &&
            message.GetProperty("event").GetString() == "output" &&
            message.GetProperty("body").GetProperty("output").GetString()!.Contains(
                "DebugSetupError",
                StringComparison.Ordinal));
        Assert.Equal(
            1,
            messages.Count(message =>
                message.GetProperty("type").GetString() == "event" &&
                message.GetProperty("event").GetString() == "terminated"));
    }

    [Fact]
    public async Task RepeatedConfigurationDoneDoesNotLoseTheActiveLaunchBeforeDisconnect()
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
        var vbeSession = new ModalWaitingVbeDebugSession(events);
        var adapter = new VbaDebugAdapter(
            new ProjectContextResolver(manifestStore),
            new DebugLaunchCoordinator(
                new RecordingDebugWorkbookBuilder(events, []),
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
        var duplicateLaunch = launch.Replace("\"seq\":2", "\"seq\":3", StringComparison.Ordinal);
        await using var input = CreateInput(
            """
            {"seq":1,"type":"request","command":"initialize","arguments":{"adapterID":"vba"}}
            """,
            launch,
            duplicateLaunch,
            """
            {"seq":4,"type":"request","command":"configurationDone","arguments":{}}
            """,
            """
            {"seq":5,"type":"request","command":"configurationDone","arguments":{}}
            """,
            """
            {"seq":6,"type":"request","command":"disconnect","arguments":{"terminateDebuggee":true}}
            """);
        await using var output = new MemoryStream();
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        try
        {
            await adapter.RunAsync(input, output, testTimeout.Token);

            Assert.True(vbeSession.Terminated);
            Assert.True(vbeSession.Disposed);
            Assert.Equal(1, events.Count(entry => entry == "start-visible"));
            var messages = ReadMessages(output).Select(message => message.RootElement).ToArray();
            var duplicateResponse = messages.Single(message =>
                message.GetProperty("type").GetString() == "response" &&
                message.GetProperty("request_seq").GetInt32() == 3);
            Assert.False(duplicateResponse.GetProperty("success").GetBoolean());
            Assert.Equal(
                1,
                messages.Count(message =>
                    message.GetProperty("type").GetString() == "event" &&
                    message.GetProperty("event").GetString() == "terminated"));
        }
        finally
        {
            if (!vbeSession.Terminated)
            {
                await vbeSession.TerminateAsync();
            }
        }
    }

    [Fact]
    public async Task DelayedRepeatedConfigurationDoneDoesNotLoseTheRunningSession()
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
        var vbeSession = new RunningVbeDebugSession(events);
        var adapter = new VbaDebugAdapter(
            new ProjectContextResolver(manifestStore),
            new DebugLaunchCoordinator(
                new RecordingDebugWorkbookBuilder(events, []),
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
        using var prefix = CreateInput(
            """
            {"seq":1,"type":"request","command":"initialize","arguments":{"adapterID":"vba"}}
            """,
            launch,
            """
            {"seq":3,"type":"request","command":"configurationDone","arguments":{}}
            """);
        using var suffix = CreateInput(
            """
            {"seq":4,"type":"request","command":"configurationDone","arguments":{}}
            """,
            """
            {"seq":5,"type":"request","command":"threads","arguments":{}}
            """,
            """
            {"seq":6,"type":"request","command":"disconnect","arguments":{"terminateDebuggee":true}}
            """);
        await using var output = new LaunchResponseObservingStream();
        await using var input = new SuffixAfterSignalInputStream(
            prefix.ToArray(),
            suffix.ToArray(),
            output.LaunchResponseWritten);
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        try
        {
            await adapter.RunAsync(input, output, testTimeout.Token);

            Assert.True(vbeSession.Terminated);
            Assert.True(vbeSession.Disposed);
            Assert.Equal(1, events.Count(entry => entry == "start-visible"));
            var messages = ReadMessages(output).Select(message => message.RootElement).ToArray();
            Assert.True(messages.Single(message =>
                message.GetProperty("type").GetString() == "response" &&
                message.GetProperty("request_seq").GetInt32() == 4)
                .GetProperty("success").GetBoolean());
            Assert.True(messages.Single(message =>
                message.GetProperty("type").GetString() == "response" &&
                message.GetProperty("request_seq").GetInt32() == 5)
                .GetProperty("success").GetBoolean());
        }
        finally
        {
            if (!vbeSession.Terminated)
            {
                await vbeSession.TerminateAsync();
            }
        }
    }

    [Fact]
    public async Task DisconnectAfterSetupFailureDoesNotRepeatTheTerminatedEvent()
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
        var adapter = new VbaDebugAdapter(
            new ProjectContextResolver(manifestStore),
            new DebugLaunchCoordinator(
                new FailingDebugWorkbookBuilder(),
                new UnusedVbeDebugSessionFactory()),
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
            """,
            """
            {"seq":4,"type":"request","command":"disconnect","arguments":{"terminateDebuggee":true}}
            """);
        await using var output = new MemoryStream();

        await adapter.RunAsync(input, output, CancellationToken.None);

        var messages = ReadMessages(output).Select(message => message.RootElement).ToArray();
        Assert.False(Response(messages, "launch").GetProperty("success").GetBoolean());
        Assert.True(Response(messages, "disconnect").GetProperty("success").GetBoolean());
        Assert.Equal(
            1,
            messages.Count(message =>
                message.GetProperty("type").GetString() == "event" &&
                message.GetProperty("event").GetString() == "terminated"));
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
    public async Task RejectedRestartPreparationMarkerDoesNotSeedRestartState()
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
                __vbaRestartPreparation = new
                {
                    protocolVersion = 99,
                    id = "rejected-preparation"
                },
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
            {"seq":3,"type":"request","command":"restart","arguments":{}}
            """);
        await using var output = new MemoryStream();

        await adapter.RunAsync(input, output, CancellationToken.None);

        var messages = ReadMessages(output).Select(message => message.RootElement).ToArray();
        var launchResponse = Response(messages, "launch");
        Assert.False(launchResponse.GetProperty("success").GetBoolean());
        Assert.Contains(
            "protocol version '99'",
            launchResponse.GetProperty("message").GetString(),
            StringComparison.Ordinal);
        var restartResponse = Response(messages, "restart");
        Assert.False(restartResponse.GetProperty("success").GetBoolean());
        Assert.Contains(
            "requires a previous launch",
            restartResponse.GetProperty("message").GetString(),
            StringComparison.Ordinal);
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
            DebugSourceSnapshot sourceSnapshot,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("A build was not expected.");
    }

    private sealed class FailingDebugWorkbookBuilder : IDebugWorkbookBuilder
    {
        public Task<DebugWorkbookBuildResult> BuildAsync(
            ResolvedProjectContext context,
            DebugSourceSnapshot sourceSnapshot,
            CancellationToken cancellationToken)
            => Task.FromException<DebugWorkbookBuildResult>(
                new DebugSetupException("Synthetic setup failure."));
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
            DebugSourceSnapshot sourceSnapshot,
            CancellationToken cancellationToken)
        {
            events.Add($"build:{context.DocumentName}");
            return Task.FromResult(new DebugWorkbookBuildResult(output));
        }
    }

    private sealed class FirstBuildCancellationDebugWorkbookBuilder(
        List<string> events,
        List<string>? snapshots = null) : IDebugWorkbookBuilder
    {
        private int buildCalls;

        public int BuildCalls => Volatile.Read(ref buildCalls);

        public bool FirstBuildCancellationObserved { get; private set; }

        public async Task<DebugWorkbookBuildResult> BuildAsync(
            ResolvedProjectContext context,
            DebugSourceSnapshot sourceSnapshot,
            CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref buildCalls);
            events.Add($"build:{call}");
            snapshots?.Add(Assert.Single(sourceSnapshot.Sources).Text);
            if (call == 1)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    FirstBuildCancellationObserved = true;
                    events.Add("cancel-build:1");
                    throw;
                }
            }

            return new DebugWorkbookBuildResult([]);
        }
    }

    private sealed class SnapshotRecordingDebugWorkbookBuilder(
        List<string> events,
        List<string> snapshots) : IDebugWorkbookBuilder
    {
        public Task<DebugWorkbookBuildResult> BuildAsync(
            ResolvedProjectContext context,
            DebugSourceSnapshot sourceSnapshot,
            CancellationToken cancellationToken)
        {
            events.Add($"build:{context.DocumentName}");
            snapshots.Add(Assert.Single(sourceSnapshot.Sources).Text);
            return Task.FromResult(new DebugWorkbookBuildResult([]));
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

    private sealed class SequenceVbeDebugSessionFactory(
        List<string> events,
        params IVbeDebugSession[] sessions) : IVbeDebugSessionFactory
    {
        private int nextSession;

        public Task<IVbeDebugSession> StartVisibleAsync(CancellationToken cancellationToken)
        {
            var session = sessions[nextSession++];
            events.Add($"start-visible:{session.ProcessId}");
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

        public Task<DebugCompilationHostFacts> GetCompilationHostFactsAsync(
            CancellationToken cancellationToken)
            => throw new InvalidOperationException(
                "Compilation host facts were not expected.");

        public Task OpenGeneratedWorkbookAsync(
            string workbookPath,
            IDebugInputWaitSink? inputWaitSink,
            CancellationToken cancellationToken)
        {
            events.Add($"open:{workbookPath}");
            return Task.CompletedTask;
        }

        public Task SetNativeBreakpointsAsync(
            IReadOnlyList<VbeBreakpoint> breakpoints,
            CancellationToken cancellationToken)
        {
            foreach (var breakpoint in breakpoints)
            {
                events.Add(
                    $"set:{breakpoint.ModuleName}:{breakpoint.VbideLine}:{breakpoint.ExpectedCodeLine}");
            }

            return Task.CompletedTask;
        }

        public Task RunTargetAsync(
            DebugTargetProcedure target,
            IDebugInputWaitSink? inputWaitSink,
            CancellationToken cancellationToken)
        {
            events.Add($"run:{target.ModuleName}.{target.ProcedureName}");
            return Task.CompletedTask;
        }

        public ValueTask TerminateAsync() => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RunningVbeDebugSession(
        List<string> events,
        int processId = 16180) : IVbeDebugSession
    {
        private readonly TaskCompletionSource<DebugProcessExit> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ProcessId => processId;

        public Task<DebugProcessExit> Completion => completion.Task;

        public bool Terminated { get; private set; }

        public bool Disposed { get; private set; }

        public Exception? TerminateError { get; init; }

        public bool ExitOnDispose { get; init; }

        public Task<DebugCompilationHostFacts> GetCompilationHostFactsAsync(
            CancellationToken cancellationToken)
            => throw new InvalidOperationException(
                "Compilation host facts were not expected.");

        public Task OpenGeneratedWorkbookAsync(
            string workbookPath,
            IDebugInputWaitSink? inputWaitSink,
            CancellationToken cancellationToken)
        {
            events.Add($"open:{workbookPath}");
            return Task.CompletedTask;
        }

        public Task SetNativeBreakpointsAsync(
            IReadOnlyList<VbeBreakpoint> breakpoints,
            CancellationToken cancellationToken)
        {
            foreach (var breakpoint in breakpoints)
            {
                events.Add(
                    $"set:{breakpoint.ModuleName}:{breakpoint.VbideLine}:{breakpoint.ExpectedCodeLine}");
            }

            return Task.CompletedTask;
        }

        public Task RunTargetAsync(
            DebugTargetProcedure target,
            IDebugInputWaitSink? inputWaitSink,
            CancellationToken cancellationToken)
        {
            events.Add($"run:{target.ModuleName}.{target.ProcedureName}");
            return Task.CompletedTask;
        }

        public ValueTask TerminateAsync()
        {
            Terminated = true;
            events.Add($"terminate:{ProcessId}");
            if (TerminateError is not null)
            {
                return ValueTask.FromException(TerminateError);
            }

            completion.TrySetResult(new DebugProcessExit(-1));
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            events.Add($"dispose:{ProcessId}");
            if (ExitOnDispose)
            {
                completion.TrySetResult(new DebugProcessExit(-2));
            }

            return ValueTask.CompletedTask;
        }

        public void Exit(int exitCode)
            => completion.TrySetResult(new DebugProcessExit(exitCode));
    }

    private sealed class PendingAfterPrefixInputStream(byte[] prefix) : Stream
    {
        private int offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (offset < prefix.Length)
            {
                var count = Math.Min(buffer.Length, prefix.Length - offset);
                prefix.AsMemory(offset, count).CopyTo(buffer);
                offset += count;
                return count;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override void Flush()
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
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
                throw new IOException(
                    $"Synthetic {command} response transport failure.");
            }

            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class LaunchResponseObservingStream : MemoryStream
    {
        private readonly TaskCompletionSource launchResponseWritten =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task LaunchResponseWritten => launchResponseWritten.Task;

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken);
            if (Encoding.UTF8.GetString(buffer.Span).Contains(
                    "\"command\":\"launch\"",
                    StringComparison.Ordinal))
            {
                launchResponseWritten.TrySetResult();
            }
        }
    }

    private sealed class SuffixAfterSignalInputStream(
        byte[] prefix,
        byte[] suffix,
        Task signal) : Stream
    {
        private int prefixOffset;
        private int suffixOffset;
        private bool signalObserved;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (prefixOffset < prefix.Length)
            {
                var count = Math.Min(buffer.Length, prefix.Length - prefixOffset);
                prefix.AsMemory(prefixOffset, count).CopyTo(buffer);
                prefixOffset += count;
                return count;
            }

            if (!signalObserved)
            {
                signalObserved = true;
                await signal.WaitAsync(cancellationToken);
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            }

            if (suffixOffset >= suffix.Length)
            {
                return 0;
            }

            var suffixCount = Math.Min(buffer.Length, suffix.Length - suffixOffset);
            suffix.AsMemory(suffixOffset, suffixCount).CopyTo(buffer);
            suffixOffset += suffixCount;
            return suffixCount;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override void Flush()
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
    }

    private sealed class MonitorEventFailingStream : MemoryStream
    {
        private readonly TaskCompletionSource launchResponseWritten =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task LaunchResponseWritten => launchResponseWritten.Task;

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var text = Encoding.UTF8.GetString(buffer.Span);
            if (text.Contains("\"command\":\"launch\"", StringComparison.Ordinal))
            {
                launchResponseWritten.TrySetResult();
            }

            if (text.Contains("Owned Excel process 2720 exited", StringComparison.Ordinal))
            {
                throw new IOException("Synthetic monitor transport failure.");
            }

            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class RestartAfterMonitorWriteInputStream(
        byte[] prefix,
        byte[] suffix,
        Action completeFirstSession,
        Task monitorWriteStarted) : Stream
    {
        private int prefixOffset;
        private int suffixOffset;
        private bool firstSessionCompleted;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (prefixOffset < prefix.Length)
            {
                var count = Math.Min(buffer.Length, prefix.Length - prefixOffset);
                prefix.AsMemory(prefixOffset, count).CopyTo(buffer);
                prefixOffset += count;
                return count;
            }

            if (!firstSessionCompleted)
            {
                firstSessionCompleted = true;
                completeFirstSession();
                await monitorWriteStarted.WaitAsync(cancellationToken);
            }

            if (suffixOffset >= suffix.Length)
            {
                return 0;
            }

            var countFromSuffix = Math.Min(buffer.Length, suffix.Length - suffixOffset);
            suffix.AsMemory(suffixOffset, countFromSuffix).CopyTo(buffer);
            suffixOffset += countFromSuffix;
            return countFromSuffix;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override void Flush()
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
    }

    private sealed class CallbackBetweenBuffersInputStream(
        byte[] prefix,
        byte[] suffix,
        Action onBoundary) : Stream
    {
        private int prefixOffset;
        private int suffixOffset;
        private bool boundaryReached;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (prefixOffset < prefix.Length)
            {
                var count = Math.Min(buffer.Length, prefix.Length - prefixOffset);
                prefix.AsMemory(prefixOffset, count).CopyTo(buffer);
                prefixOffset += count;
                return ValueTask.FromResult(count);
            }

            if (!boundaryReached)
            {
                boundaryReached = true;
                onBoundary();
            }

            if (suffixOffset >= suffix.Length)
            {
                return ValueTask.FromResult(0);
            }

            var suffixCount = Math.Min(buffer.Length, suffix.Length - suffixOffset);
            suffix.AsMemory(suffixOffset, suffixCount).CopyTo(buffer);
            suffixOffset += suffixCount;
            return ValueTask.FromResult(suffixCount);
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override void Flush()
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
    }

    private sealed class StartedExitMonitorFrameStream : MemoryStream
    {
        private readonly TaskCompletionSource monitorWriteStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task MonitorWriteStarted => monitorWriteStarted.Task;

        public bool MonitorWriteCancellationObserved { get; private set; }

        public bool MonitorFrameCompleted { get; private set; }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var text = Encoding.UTF8.GetString(buffer.Span);
            if (text.Contains("Owned Excel process 16186 exited", StringComparison.Ordinal))
            {
                monitorWriteStarted.TrySetResult();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    MonitorWriteCancellationObserved = true;
                    throw;
                }
            }

            await base.WriteAsync(buffer, cancellationToken);
            if (text.Contains("Owned Excel process 16186 exited", StringComparison.Ordinal))
            {
                MonitorFrameCompleted = true;
            }
        }
    }

    private sealed class ModalWaitingVbeDebugSession(List<string> events) : IVbeDebugSession
    {
        private readonly TaskCompletionSource<DebugProcessExit> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ProcessId => 31415;

        public Task<DebugProcessExit> Completion => completion.Task;

        public bool Terminated { get; private set; }

        public bool Disposed { get; private set; }

        public Task<DebugCompilationHostFacts> GetCompilationHostFactsAsync(
            CancellationToken cancellationToken)
            => throw new InvalidOperationException(
                "Compilation host facts were not expected.");

        public Task OpenGeneratedWorkbookAsync(
            string workbookPath,
            IDebugInputWaitSink? inputWaitSink,
            CancellationToken cancellationToken)
            => WaitForTerminationAsync(workbookPath, inputWaitSink, cancellationToken);

        private async Task WaitForTerminationAsync(
            string workbookPath,
            IDebugInputWaitSink? inputWaitSink,
            CancellationToken cancellationToken)
        {
            events.Add($"open:{workbookPath}");
            if (inputWaitSink is not null)
            {
                await inputWaitSink.InputRequiredAsync(
                    new DebugInputWait(
                        DebugInputWaitKind.Excel,
                        DebugInputWaitPhase.WorkbookOpen,
                        ProcessId),
                    cancellationToken);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public Task SetNativeBreakpointsAsync(
            IReadOnlyList<VbeBreakpoint> breakpoints,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("Breakpoint setup was not expected.");

        public Task RunTargetAsync(
            DebugTargetProcedure target,
            IDebugInputWaitSink? inputWaitSink,
            CancellationToken cancellationToken)
        {
            events.Add($"run:{target.ModuleName}.{target.ProcedureName}");
            return Task.CompletedTask;
        }

        public ValueTask TerminateAsync()
        {
            Terminated = true;
            completion.TrySetResult(new DebugProcessExit(-1));
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
