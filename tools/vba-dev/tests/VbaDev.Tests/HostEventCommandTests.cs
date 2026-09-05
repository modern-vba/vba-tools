using System.Text.Json;
using VbaDev.App.HostEvents;
using VbaDev.App.Workbooks;
using Xunit;

namespace VbaDev.Tests;

public sealed class HostEventCommandTests
{
    [Fact]
    public async Task EmptyEventSurfaceFailsClosedInsteadOfPublishingAuthority()
    {
        var command = new HostEventListCommand(
            new StubHostEventCatalogAutomation(CreateEmptyCatalog()));

        var result = await command.RunAsync("json", CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("no Events", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CliListIsEnvironmentScopedAndNeedsNoProjectOrDocument()
    {
        using var temp = TempDirectory.Create();
        var automation = new StubHostEventCatalogAutomation(CreateCatalog());
        var application = CommandLineTestFactory.Create(
            temp.Path,
            hostEventCatalogAutomation: automation);

        var result = application.Run(["host-event", "list", "--format", "json"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        Assert.Equal(1, automation.ReadCount);
        using var parsed = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal("UserForm", parsed.RootElement.GetProperty("intrinsicEventSourceName").GetString());
        Assert.False(parsed.RootElement.TryGetProperty("project", out _));
        Assert.False(parsed.RootElement.TryGetProperty("document", out _));
    }

    [Theory]
    [InlineData("--project")]
    [InlineData("--document")]
    [InlineData("-d")]
    public void CliListRejectsProjectAndDocumentSelectors(string selector)
    {
        using var temp = TempDirectory.Create();
        var automation = new StubHostEventCatalogAutomation(CreateCatalog());
        var application = CommandLineTestFactory.Create(
            temp.Path,
            hostEventCatalogAutomation: automation);

        var result = application.Run(["host-event", "list", selector, "Book1"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(0, automation.ReadCount);
        Assert.Contains(
            selector,
            result.StandardOutput + result.StandardError,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task JsonListReturnsAnEnvironmentUserFormEventCatalog()
    {
        var catalog = new IntrinsicHostEventCatalog(
            "UserForm",
            [
                new HostEvent(
                    new HostEventIdentity("UserForm", "Initialize"),
                    new HostEventSignature([], "Occurs after an object is loaded."),
                    AuthoringAvailable: true,
                    ExistingHandlerRecognizable: true)
            ]);
        var command = new HostEventListCommand(new StubHostEventCatalogAutomation(catalog));

        var result = await command.RunAsync("json", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        using var parsed = JsonDocument.Parse(result.StandardOutput);
        var output = parsed.RootElement;
        Assert.Equal(
            ["schemaVersion", "sourceKind", "intrinsicEventSourceName", "events"],
            output.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("1.0", output.GetProperty("schemaVersion").GetString());
        Assert.Equal("userForm", output.GetProperty("sourceKind").GetString());
        Assert.Equal("UserForm", output.GetProperty("intrinsicEventSourceName").GetString());
        var inspectedEvent = Assert.Single(output.GetProperty("events").EnumerateArray());
        Assert.Equal(
            ["identity", "signature", "authoringAvailable", "existingHandlerRecognizable"],
            inspectedEvent.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("UserForm", inspectedEvent.GetProperty("identity").GetProperty("sourceName").GetString());
        Assert.Equal("Initialize", inspectedEvent.GetProperty("identity").GetProperty("name").GetString());
        Assert.Empty(inspectedEvent.GetProperty("signature").GetProperty("parameters").EnumerateArray());
        Assert.Equal(
            "Occurs after an object is loaded.",
            inspectedEvent.GetProperty("signature").GetProperty("documentation").GetString());
        Assert.True(inspectedEvent.GetProperty("authoringAvailable").GetBoolean());
        Assert.True(inspectedEvent.GetProperty("existingHandlerRecognizable").GetBoolean());
    }

    [Fact]
    public async Task JsonListPreservesStructuredTypesProvenanceAndCanonicalEventOrder()
    {
        var libraryGuid = Guid.Parse("00020813-0000-0000-c000-000000000046");
        var catalog = new IntrinsicHostEventCatalog(
            "UserForm",
            [
                new HostEvent(
                    new HostEventIdentity("UserForm", "QueryClose"),
                    new HostEventSignature(
                        [
                            new HostEventParameter(
                                "Cancel",
                                new IntrinsicHostEventTypeReference("Integer"),
                                HostEventPassingMechanism.ByRef,
                                HostEventArrayShape.Scalar,
                                Optional: false,
                                ParamArray: false),
                            new HostEventParameter(
                                "Target",
                                new TypeLibHostEventTypeReference(
                                    "Range",
                                    libraryGuid,
                                    1,
                                    9,
                                    0),
                                HostEventPassingMechanism.ByVal,
                                HostEventArrayShape.Scalar,
                                Optional: true,
                                ParamArray: false),
                            new HostEventParameter(
                                "Values",
                                new UnresolvedHostEventTypeReference("Vendor.Widget"),
                                HostEventPassingMechanism.ByRef,
                                HostEventArrayShape.Array,
                                Optional: false,
                                ParamArray: true)
                        ],
                        null),
                    AuthoringAvailable: true,
                    ExistingHandlerRecognizable: false),
                new HostEvent(
                    new HostEventIdentity("UserForm", "Initialize"),
                    new HostEventSignature([], null),
                    AuthoringAvailable: true,
                    ExistingHandlerRecognizable: true)
            ],
            new HostEventBaseTypeProvenance("_UserForm", libraryGuid, 2, 0, 0));
        var command = new HostEventListCommand(new StubHostEventCatalogAutomation(catalog));

        var result = await command.RunAsync("json", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        using var parsed = JsonDocument.Parse(result.StandardOutput);
        var output = parsed.RootElement;
        Assert.Equal(
            ["Initialize", "QueryClose"],
            output.GetProperty("events")
                .EnumerateArray()
                .Select(item => item.GetProperty("identity").GetProperty("name").GetString()!)
                .ToArray());
        var parameters = output.GetProperty("events")[1]
            .GetProperty("signature")
            .GetProperty("parameters");
        Assert.Equal("intrinsic", parameters[0].GetProperty("type").GetProperty("kind").GetString());
        Assert.Equal("typeLib", parameters[1].GetProperty("type").GetProperty("kind").GetString());
        Assert.Equal(
            libraryGuid.ToString("D"),
            parameters[1].GetProperty("type").GetProperty("libraryGuid").GetString());
        Assert.True(parameters[1].GetProperty("optional").GetBoolean());
        Assert.Equal("unresolved", parameters[2].GetProperty("type").GetProperty("kind").GetString());
        Assert.Equal("array", parameters[2].GetProperty("arrayShape").GetString());
        Assert.True(parameters[2].GetProperty("paramArray").GetBoolean());
        Assert.False(output.GetProperty("events")[1]
            .GetProperty("signature")
            .TryGetProperty("documentation", out _));
        var provenance = output.GetProperty("baseTypeProvenance");
        Assert.Equal("_UserForm", provenance.GetProperty("name").GetString());
        Assert.Equal(libraryGuid.ToString("D"), provenance.GetProperty("libraryGuid").GetString());
    }

    [Fact]
    public async Task CancellationPublishesNoCatalog()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelledCommand = new HostEventListCommand(
            new FailingHostEventCatalogAutomation(
                new OperationCanceledException(cancellation.Token)));

        var cancelled = await cancelledCommand.RunAsync("json", cancellation.Token);

        Assert.Equal(130, cancelled.ExitCode);
        Assert.Empty(cancelled.StandardOutput);
    }

    [Fact]
    public async Task FailurePublishesNoCatalog()
    {
        var failedCommand = new HostEventListCommand(
            new FailingHostEventCatalogAutomation(
                new InvalidOperationException("catalog unavailable")));

        var failed = await failedCommand.RunAsync("json", CancellationToken.None);

        Assert.Equal(1, failed.ExitCode);
        Assert.Empty(failed.StandardOutput);
        Assert.Contains("catalog unavailable", failed.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InspectionTimeoutPublishesNoCatalogAndRetainsItsStage()
    {
        var command = new HostEventListCommand(
            new FailingHostEventCatalogAutomation(
                new WorkbookAutomationTimeoutException(
                    new WorkbookAutomationStage(
                        WorkbookAutomationStageKind.HostEventInspection),
                    TimeSpan.FromSeconds(60))));

        var result = await command.RunAsync("json", CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("timed out", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "Host Event inspection",
            result.StandardError,
            StringComparison.Ordinal);
    }

    private static IntrinsicHostEventCatalog CreateEmptyCatalog()
        => new("UserForm", []);

    private static IntrinsicHostEventCatalog CreateCatalog()
        => new(
            "UserForm",
            [
                new HostEvent(
                    new HostEventIdentity("UserForm", "Initialize"),
                    new HostEventSignature([], null),
                    AuthoringAvailable: true,
                    ExistingHandlerRecognizable: true)
            ]);

    private sealed class StubHostEventCatalogAutomation(IntrinsicHostEventCatalog catalog)
        : IHostEventCatalogAutomation
    {
        public int ReadCount { get; private set; }

        public Task<IntrinsicHostEventCatalog> ReadAsync(CancellationToken cancellationToken)
        {
            ReadCount++;
            return Task.FromResult(catalog);
        }
    }

    private sealed class FailingHostEventCatalogAutomation(Exception error)
        : IHostEventCatalogAutomation
    {
        public Task<IntrinsicHostEventCatalog> ReadAsync(CancellationToken cancellationToken)
            => Task.FromException<IntrinsicHostEventCatalog>(error);
    }
}
