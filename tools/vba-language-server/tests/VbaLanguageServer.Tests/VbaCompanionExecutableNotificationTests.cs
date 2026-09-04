using System.Text.Json.Nodes;
using VbaLanguageServer.Lsp;
using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaCompanionExecutableNotificationTests
{
    [Fact]
    public void Valid_notification_parses_the_extension_validated_companion_contract()
    {
        var executablePath = Path.GetFullPath("vba-dev.exe");
        var parameters = new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["executablePath"] = executablePath,
            ["referenceListOutputSchemaVersion"] = "1.0"
        };

        var parsed = VbaCompanionExecutableNotification.TryParse(
            parameters,
            out var update);

        Assert.True(parsed);
        Assert.Equal(executablePath, update.ExecutablePath);
    }

    [Fact]
    public void Notification_rejects_every_payload_outside_the_exact_supported_contract()
    {
        var executablePath = Path.GetFullPath("vba-dev.exe");
        var invalidPayloads = new JsonNode?[]
        {
            null,
            new JsonArray(),
            CreatePayload(executablePath, schemaVersion: "2.0"),
            CreatePayload(executablePath, referenceListOutputSchemaVersion: "2.0"),
            CreatePayload("vba-dev.exe"),
            CreatePayload(" "),
            new JsonObject
            {
                ["schemaVersion"] = "1.0",
                ["executablePath"] = executablePath
            },
            new JsonObject
            {
                ["schemaVersion"] = "1.0",
                ["executablePath"] = executablePath,
                ["referenceListOutputSchemaVersion"] = "1.0",
                ["unexpected"] = true
            }
        };

        foreach (var payload in invalidPayloads)
        {
            Assert.False(
                VbaCompanionExecutableNotification.TryParse(payload, out _));
        }
    }

    [Fact]
    public void First_valid_notification_pins_and_refreshes_the_open_project_snapshot_once()
    {
        var registry = new MarkerDiscovery();
        var session = new SessionPinnedVbaDevReferenceCatalogDiscovery(
            registry,
            _ => new ContextFactoryDiscovery());
        var refresh = new RecordingCompanionRefresh();
        var executablePath = Path.GetFullPath("vba-dev.exe");
        var handler = new VbaCompanionExecutableNotificationHandler(
            session,
            () => ["file:///C:/work/Book1/Module1.bas"],
            refresh);

        Assert.True(handler.TryApply(CreatePayload(executablePath)));
        Assert.True(handler.TryApply(CreatePayload(executablePath)));
        Assert.False(handler.TryApply(CreatePayload(
            Path.GetFullPath("other-vba-dev.exe"))));
        Assert.False(handler.TryApply(new JsonObject()));

        Assert.Equal(
            ["file:///C:/work/Book1/Module1.bas"],
            Assert.Single(refresh.Snapshots));
    }

    [Fact]
    public void Same_notification_retries_after_open_document_capture_fails()
    {
        var session = new SessionPinnedVbaDevReferenceCatalogDiscovery(
            new MarkerDiscovery(),
            _ => new ContextFactoryDiscovery());
        var refresh = new RecordingCompanionRefresh();
        var executablePath = Path.GetFullPath("vba-dev.exe");
        var captureAttempts = 0;
        var handler = new VbaCompanionExecutableNotificationHandler(
            session,
            () => ++captureAttempts == 1
                ? throw new InvalidOperationException("capture failed")
                : ["file:///C:/work/Book1/Module1.bas"],
            refresh);

        Assert.False(handler.TryApply(CreatePayload(executablePath)));
        Assert.True(handler.TryApply(CreatePayload(executablePath)));
        Assert.True(handler.TryApply(CreatePayload(executablePath)));

        Assert.Equal(2, captureAttempts);
        Assert.Equal(
            ["file:///C:/work/Book1/Module1.bas"],
            Assert.Single(refresh.Snapshots));
    }

    [Fact]
    public void Same_notification_retries_after_active_project_refresh_fails()
    {
        var session = new SessionPinnedVbaDevReferenceCatalogDiscovery(
            new MarkerDiscovery(),
            _ => new ContextFactoryDiscovery());
        var refresh = new RecordingCompanionRefresh(failFirstAttempt: true);
        var executablePath = Path.GetFullPath("vba-dev.exe");
        var handler = new VbaCompanionExecutableNotificationHandler(
            session,
            () => ["file:///C:/work/Book1/Module1.bas"],
            refresh);

        Assert.False(handler.TryApply(CreatePayload(executablePath)));
        Assert.True(handler.TryApply(CreatePayload(executablePath)));
        Assert.True(handler.TryApply(CreatePayload(executablePath)));
        Assert.False(handler.TryApply(CreatePayload(
            Path.GetFullPath("other-vba-dev.exe"))));

        Assert.Equal(2, refresh.Attempts);
        Assert.Equal(
            ["file:///C:/work/Book1/Module1.bas"],
            Assert.Single(refresh.Snapshots));
    }

    private static JsonObject CreatePayload(
        string executablePath,
        string schemaVersion = "1.0",
        string referenceListOutputSchemaVersion = "1.0")
        => new()
        {
            ["schemaVersion"] = schemaVersion,
            ["executablePath"] = executablePath,
            ["referenceListOutputSchemaVersion"] =
                referenceListOutputSchemaVersion
        };

    private sealed class MarkerDiscovery
        : IVbaProjectReferenceCatalogDiscovery
    {
        public Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
            => Task.FromResult(
                VbaProjectReferenceCatalogDiscoveryResult.Failure(
                    referenceName,
                    "registry"));
    }

    private sealed class ContextFactoryDiscovery
        : IVbaProjectReferenceCatalogDiscovery,
          IVbaProjectReferenceCatalogContextDiscoveryFactory
    {
        public Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
            => Task.FromResult(
                VbaProjectReferenceCatalogDiscoveryResult.Failure(
                    referenceName,
                    "companion"));

        public IVbaProjectReferenceCatalogDiscovery CreateContextDiscovery(
            VbaProjectReferenceCatalogRefreshContext context)
            => this;
    }

    private sealed class RecordingCompanionRefresh(bool failFirstAttempt = false)
        : IVbaCompanionReferenceCatalogRefresh
    {
        public int Attempts { get; private set; }

        public List<IReadOnlyList<string>> Snapshots { get; } = [];

        public void RefreshActiveProjects(IReadOnlyList<string> openDocumentUris)
        {
            Attempts++;
            if (failFirstAttempt && Attempts == 1)
            {
                throw new InvalidOperationException("refresh failed");
            }

            Snapshots.Add(openDocumentUris);
        }
    }
}
