using System.Text.Json;
using System.Text;
using VbaDebugAdapter.Cli;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed partial class VbaDebugAdapterCliSurfaceTests
{
    [Theory]
    [InlineData("setFunctionBreakpoints", "{\"breakpoints\":[{\"name\":\"\\uD800\"}]}")]
    [InlineData("setFunctionBreakpoints", "{\"breakpoints\":[{\"\\uD800\":17,\"name\":\"Run\"}]}")]
    [InlineData("setFunctionBreakpoints", "{\"\\uD800\":17,\"breakpoints\":[]}")]
    [InlineData("setBreakpoints", "{\"source\":{\"path\":\"C:/persistent/Module1.bas\"},\"breakpoints\":[],\"abcdefgh\\uD800\":17}")]
    [InlineData("launch", "{\"project\":\"C:/persistent\",\"document\":\"Book1\",\"__vbaDebugWorkbookFileName\":\"Book1.xlsm\",\"sourceSnapshot\":{\"schemaVersion\":2,\"sources\":[]},\"abcdefgh\\uD800\":17}")]
    [InlineData("setBreakpoints", "{\"source\":{\"path\":\"\\uD800\"},\"breakpoints\":[]}")]
    [InlineData("setBreakpoints", "{\"source\":{\"path\":\"C:/persistent/Module1.bas\"},\"breakpoints\":[{\"line\":3,\"condition\":\"\\uD800\"}]}")]
    [InlineData("launch", "{\"project\":\"\\uD800\",\"document\":\"Book1\",\"__vbaDebugWorkbookFileName\":\"Book1.xlsm\",\"sourceSnapshot\":{\"schemaVersion\":2,\"sources\":[]}}")]
    public async Task MalformedArgumentUnicodeFailsOnlyItsRequest(string command, string arguments)
    {
        Assert.Contains("\\uD800", arguments, StringComparison.Ordinal);
        var rawRequest = "{\"seq\":1,\"type\":\"request\",\"command\":\"" + command + "\",\"arguments\":" + arguments + "}";
        var content = Encoding.ASCII.GetBytes(rawRequest);
        using var input = new MemoryStream();
        input.Write(Encoding.ASCII.GetBytes($"Content-Length: {content.Length}\r\n\r\n"));
        input.Write(content);
        using var tail = CreateDapInput(
            new { seq = 2, type = "request", command = "threads", arguments = new { } },
            new { seq = 3, type = "request", command = "launch", arguments = CreateValidLaunchArguments() },
            new { seq = 4, type = "request", command = "configurationDone", arguments = new { } });
        tail.CopyTo(input);
        input.Position = 0;
        var service = new RecordingDebugLaunchService();
        var commandLine = CreateCommandLine(new StandaloneVbaDebugAdapterStdioRunner(service),
            new RecordingVbaDevCapabilitiesProbe(new(0,
                "{\"featureVersions\":{\"build.sourceSnapshot\":\"2.0\",\"build.sourceSnapshotAnalysis\":\"1.0\"}}", "")));
        using var output = new MemoryStream();
        Assert.Equal(0, await commandLine.InvokeAsync(
            ["--stdio", "--vba-dev", Path.GetFullPath("vba-dev.exe"), "--session", "0123456789abcdef0123456789abcdef"],
            input, output, Stream.Null, CancellationToken.None));
        var messages = ReadDapMessages(output);
        Assert.False(AdmissionResponse(messages, 1).GetProperty("success").GetBoolean());
        Assert.True(AdmissionResponse(messages, 2).GetProperty("success").GetBoolean());
        Assert.True(AdmissionResponse(messages, 3).GetProperty("success").GetBoolean());
    }

    [Theory]
    [InlineData("setBreakpoints", "{\"source\":{\"path\":17,\"path\":\"C:\\\\persistent\\\\Module1.bas\"},\"breakpoints\":[]}")]
    [InlineData("setBreakpoints", "{\"source\":{\"path\":\"C:\\\\persistent\\\\Module1.bas\"},\"breakpoints\":[{\"line\":3,\"condition\":17,\"condition\":\"true\"}]}")]
    [InlineData("setFunctionBreakpoints", "{\"breakpoints\":[{\"name\":17,\"name\":\"Run\"}]}")]
    [InlineData("setDataBreakpoints", "{\"breakpoints\":[{\"dataId\":null,\"dataId\":\"id\"}]}")]
    [InlineData("setExceptionBreakpoints", "{\"filters\":[],\"filterOptions\":[{\"filterId\":17,\"filterId\":\"all\"}]}")]
    [InlineData("setExceptionBreakpoints", "{\"filters\":[],\"exceptionOptions\":[{\"breakMode\":false,\"breakMode\":\"always\"}]}")]
    [InlineData("setExceptionBreakpoints", "{\"filters\":[],\"exceptionOptions\":[{\"breakMode\":\"always\",\"path\":[{\"names\":17,\"names\":[\"x\"]}]}]}")]
    public async Task DuplicateConsumedBreakpointFieldsCannotHideMalformedValues(string command, string json)
    {
        var service = new RecordingDebugLaunchService();
        var messages = await RunAdmissionRequestsAsync(service,
            new { seq = 1, type = "request", command, arguments = JsonSerializer.Deserialize<JsonElement>(json) },
            new { seq = 2, type = "request", command = "launch", arguments = CreateValidLaunchArguments() },
            new { seq = 3, type = "request", command = "configurationDone", arguments = new { } });
        Assert.False(AdmissionResponse(messages, 1).GetProperty("success").GetBoolean());
        Assert.Contains("duplicate", AdmissionResponse(messages, 1).GetProperty("message").GetString());
        Assert.True(AdmissionResponse(messages, 2).GetProperty("success").GetBoolean());
    }

    [Theory]
    [InlineData("schemaVersion", "\"2\"")]
    [InlineData("schemaVersion", "null")]
    [InlineData("schemaVersion", "[]")]
    [InlineData("schemaVersion", "{}")]
    [InlineData("schemaVersion", "true")]
    [InlineData("schemaVersion", "2147483648")]
    [InlineData("schemaVersion", "1.5")]
    [InlineData("line", "-1")]
    [InlineData("character", "-1")]
    [InlineData("breakpointLine", "-1")]
    public async Task MalformedLaunchCoordinatesRejectBeforePendingStateAndAllowCorrection(string field, string json)
    {
        var malformed = CreateValidLaunchArguments();
        var snapshot = Assert.IsType<Dictionary<string, object?>>(malformed["sourceSnapshot"]);
        var value = JsonSerializer.Deserialize<JsonElement>(json);
        if (field == "schemaVersion") { snapshot[field] = value; }
        else if (field == "breakpointLine")
        {
            snapshot["breakpoints"] = new[] { new { sourceUri = "file:///C:/persistent/Module1.bas", line = value } };
        }
        else
        {
            snapshot["activeSource"] = new Dictionary<string, object?>
            {
                ["sourceUri"] = "file:///C:/persistent/Module1.bas", ["line"] = 0, ["character"] = 0, [field] = value
            };
        }
        var service = new RecordingDebugLaunchService();
        var messages = await RunAdmissionRequestsAsync(service,
            new { seq = 1, type = "request", command = "launch", arguments = malformed },
            new { seq = 2, type = "request", command = "threads", arguments = new { } },
            new { seq = 3, type = "request", command = "launch", arguments = CreateValidLaunchArguments() },
            new { seq = 4, type = "request", command = "configurationDone", arguments = new { } });

        Assert.False(AdmissionResponse(messages, 1).GetProperty("success").GetBoolean());
        Assert.True(AdmissionResponse(messages, 2).GetProperty("success").GetBoolean());
        Assert.True(AdmissionResponse(messages, 3).GetProperty("success").GetBoolean());
        Assert.Single(service.Invocations);
    }

    [Theory]
    [InlineData("setFunctionBreakpoints", "{\"breakpoints\":[{\"name\":\"Run\"}]}", "{}", "{\"breakpoints\":[]}")]
    [InlineData("setDataBreakpoints", "{\"breakpoints\":[{\"dataId\":\"id\"}]}", "{}", "{\"breakpoints\":[]}")]
    [InlineData("setExceptionBreakpoints", "{\"filters\":[\"all\"]}", "{}", "{\"filters\":[]}")]
    [InlineData("setExceptionBreakpoints", "{\"filters\":[\"all\"]}", "{\"filters\":[\"all\"],\"filters\":[]}", "{\"filters\":[]}")]
    public async Task MalformedCategoryUpdateCannotRemoveRememberedUnsupportedConfiguration(string command, string configured, string malformed, string cleared)
    {
        var service = new RecordingDebugLaunchService();
        var messages = await RunAdmissionRequestsAsync(service,
            new { seq = 1, type = "request", command, arguments = JsonSerializer.Deserialize<JsonElement>(configured) },
            new { seq = 2, type = "request", command, arguments = JsonSerializer.Deserialize<JsonElement>(malformed) },
            new { seq = 3, type = "request", command = "launch", arguments = CreateValidLaunchArguments() },
            new { seq = 4, type = "request", command = "configurationDone", arguments = new { } },
            new { seq = 5, type = "request", command, arguments = JsonSerializer.Deserialize<JsonElement>(cleared) },
            new { seq = 6, type = "request", command = "launch", arguments = CreateValidLaunchArguments() });

        Assert.False(AdmissionResponse(messages, 2).GetProperty("success").GetBoolean());
        Assert.False(AdmissionResponse(messages, 3).GetProperty("success").GetBoolean());
        Assert.True(AdmissionResponse(messages, 5).GetProperty("success").GetBoolean());
        Assert.True(AdmissionResponse(messages, 6).GetProperty("success").GetBoolean());
        Assert.Single(service.Invocations);
    }

    [Theory]
    [InlineData("setFunctionBreakpoints", "{\"breakpoints\":[{\"name\":\"Run\"},17]}")]
    [InlineData("setFunctionBreakpoints", "{\"breakpoints\":[{\"name\":true}]}")]
    [InlineData("setFunctionBreakpoints", "{\"breakpoints\":[{\"name\":\"Run\",\"condition\":1}]}")]
    [InlineData("setDataBreakpoints", "{\"breakpoints\":[{\"dataId\":\"id\",\"accessType\":\"execute\"}]}")]
    [InlineData("setDataBreakpoints", "{\"breakpoints\":[{\"dataId\":null}]}")]
    [InlineData("setExceptionBreakpoints", "{\"filters\":[\"all\",17]}")]
    [InlineData("setExceptionBreakpoints", "{\"filters\":[],\"filterOptions\":[{\"filterId\":\"all\",\"mode\":false}]}")]
    [InlineData("setExceptionBreakpoints", "{\"filters\":[],\"exceptionOptions\":[{\"breakMode\":\"sometimes\"}]}")]
    [InlineData("setExceptionBreakpoints", "{\"filters\":[],\"exceptionOptions\":[{\"breakMode\":\"always\",\"path\":[{\"names\":[\"x\",null]}]}]}")]
    [InlineData("setExceptionBreakpoints", "{\"filters\":[],\"exceptionOptions\":[{\"breakMode\":\"always\",\"path\":[{\"names\":[\"x\"],\"negate\":\"false\"}]}]}")]
    public async Task MalformedUnsupportedBreakpointElementsDoNotPoisonLaunch(string command, string json)
    {
        var service = new RecordingDebugLaunchService();
        var messages = await RunAdmissionRequestsAsync(service,
            new { seq = 1, type = "request", command, arguments = JsonSerializer.Deserialize<JsonElement>(json) },
            new { seq = 2, type = "request", command = "launch", arguments = CreateValidLaunchArguments() },
            new { seq = 3, type = "request", command = "configurationDone", arguments = new { } });

        Assert.False(AdmissionResponse(messages, 1).GetProperty("success").GetBoolean());
        Assert.True(AdmissionResponse(messages, 2).GetProperty("success").GetBoolean());
        Assert.Single(service.Invocations);
    }

    [Fact]
    public async Task MalformedExceptionOptionsAfterNonemptyFiltersDoNotRememberUnsupportedConfiguration()
    {
        var service = new RecordingDebugLaunchService();
        var messages = await RunAdmissionRequestsAsync(service,
            new { seq = 1, type = "request", command = "setExceptionBreakpoints", arguments = new { filters = new[] { "all" }, filterOptions = (object?)null } },
            new { seq = 2, type = "request", command = "launch", arguments = CreateValidLaunchArguments() },
            new { seq = 3, type = "request", command = "configurationDone", arguments = new { } });

        Assert.False(AdmissionResponse(messages, 1).GetProperty("success").GetBoolean());
        Assert.Contains("filterOptions", AdmissionResponse(messages, 1).GetProperty("message").GetString());
        Assert.True(AdmissionResponse(messages, 2).GetProperty("success").GetBoolean());
        Assert.Single(service.Invocations);
    }

    [Theory]
    [InlineData("condition", "17")]
    [InlineData("hitCondition", "true")]
    [InlineData("logMessage", "[]")]
    [InlineData("mode", "{}")]
    [InlineData("column", "\"2\"")]
    [InlineData("column", "0")]
    [InlineData("condition", "null")]
    public async Task MalformedSourceBreakpointOptionDoesNotBlockALaterLaunch(string field, string json)
    {
        var breakpoint = new Dictionary<string, object?> { ["line"] = 3, [field] = JsonSerializer.Deserialize<JsonElement>(json) };
        var service = new RecordingDebugLaunchService();
        var messages = await RunAdmissionRequestsAsync(service,
            new { seq = 1, type = "request", command = "setBreakpoints", arguments = new { source = new { path = "C:\\persistent\\Module1.bas" }, breakpoints = new[] { breakpoint } } },
            new { seq = 2, type = "request", command = "launch", arguments = CreateValidLaunchArguments() },
            new { seq = 3, type = "request", command = "configurationDone", arguments = new { } });

        Assert.False(AdmissionResponse(messages, 1).GetProperty("success").GetBoolean());
        Assert.True(AdmissionResponse(messages, 2).GetProperty("success").GetBoolean());
        Assert.Single(service.Invocations);
    }

    [Theory]
    [InlineData("\"5\"")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("true")]
    [InlineData("1.5")]
    [InlineData("2147483648")]
    [InlineData("0")]
    [InlineData("-1")]
    public async Task MalformedLaterSourceBreakpointFailsOnlyItsRequestWithoutChangingIds(string json)
    {
        var source = new { path = "C:\\persistent\\Module1.bas" };
        var otherSource = new { path = "C:\\persistent\\Other.bas" };
        var messages = await RunAdmissionRequestsAsync(new RecordingDebugLaunchService(),
            new { seq = 1, type = "request", command = "setBreakpoints", arguments = new { source, breakpoints = new[] { new { line = 3 } } } },
            new { seq = 2, type = "request", command = "setBreakpoints", arguments = new { source = otherSource, breakpoints = new[] { new { line = 8 } } } },
            new { seq = 3, type = "request", command = "setBreakpoints", arguments = new { source, breakpoints = new object[] { new { line = 4 }, new { line = JsonSerializer.Deserialize<JsonElement>(json) } } } },
            new { seq = 4, type = "request", command = "threads", arguments = new { } },
            new { seq = 5, type = "request", command = "setBreakpoints", arguments = new { source, breakpoints = new[] { new { line = 3 } } } },
            new { seq = 6, type = "request", command = "setBreakpoints", arguments = new { source = otherSource, breakpoints = new[] { new { line = 8 } } } });
        Assert.False(AdmissionResponse(messages, 3).GetProperty("success").GetBoolean());
        Assert.True(AdmissionResponse(messages, 4).GetProperty("success").GetBoolean());
        Assert.True(AdmissionResponse(messages, 5).GetProperty("success").GetBoolean());
        Assert.True(AdmissionResponse(messages, 6).GetProperty("success").GetBoolean());
        int Id(int sequence) => AdmissionResponse(messages, sequence).GetProperty("body").GetProperty("breakpoints")[0].GetProperty("id").GetInt32();
        Assert.Equal(Id(1), Id(5));
        Assert.Equal(Id(2), Id(6));
        Assert.DoesNotContain(messages, message => message.TryGetProperty("event", out var value) && value.GetString() == "terminated");
    }

    private static JsonElement AdmissionResponse(IReadOnlyList<JsonElement> messages, int sequence)
        => Assert.Single(messages, message => message.TryGetProperty("request_seq", out var value) && value.GetInt32() == sequence);

    private static async Task<IReadOnlyList<JsonElement>> RunAdmissionRequestsAsync(RecordingDebugLaunchService service, params object[] requests)
    {
        var commandLine = CreateCommandLine(new StandaloneVbaDebugAdapterStdioRunner(service),
            new RecordingVbaDevCapabilitiesProbe(new(0,
                "{\"featureVersions\":{\"build.sourceSnapshot\":\"2.0\",\"build.sourceSnapshotAnalysis\":\"1.0\"}}", "")));
        using var input = CreateDapInput(requests);
        using var output = new MemoryStream();
        var exitCode = await commandLine.InvokeAsync(
            ["--stdio", "--vba-dev", Path.GetFullPath("vba-dev.exe"), "--session", "0123456789abcdef0123456789abcdef"],
            input, output, Stream.Null, CancellationToken.None);
        Assert.Equal(0, exitCode);
        return ReadDapMessages(output);
    }
}
