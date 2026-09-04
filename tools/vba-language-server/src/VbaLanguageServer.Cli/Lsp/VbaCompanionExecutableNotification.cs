using System.Text.Json.Nodes;
using VbaLanguageServer.SourceModel;

namespace VbaLanguageServer.Lsp;

internal sealed record VbaCompanionExecutableUpdate(string ExecutablePath);

internal interface IVbaCompanionExecutableNotificationHandler
{
    VbaCompanionExecutableApplication? TryPrepare(
        VbaCompanionExecutableUpdate update);
}

internal sealed class VbaCompanionExecutableApplication
{
    private readonly Func<bool> apply;

    public VbaCompanionExecutableApplication(Func<bool> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        this.apply = apply;
    }

    public bool Apply()
        => apply();
}

internal interface IVbaCompanionReferenceCatalogRefresh
{
    void RefreshActiveProjects(IReadOnlyList<string> openDocumentUris);
}

/// <summary>
/// Decodes the validated companion executable supplied by the extension after startup.
/// </summary>
internal static class VbaCompanionExecutableNotification
{
    public const string Method = "vba/companionExecutable";
    private const string SchemaVersion = "1.0";
    private const string ReferenceListOutputSchemaVersion = "1.0";

    public static bool TryParse(
        JsonNode? parameters,
        out VbaCompanionExecutableUpdate update)
    {
        update = default!;
        if (parameters is not JsonObject root
            || root.Count != 3
            || root.Any(property => property.Key is not
                ("schemaVersion"
                    or "executablePath"
                    or "referenceListOutputSchemaVersion"))
            || !TryGetString(root["schemaVersion"], out var schemaVersion)
            || schemaVersion != SchemaVersion
            || !TryGetString(root["executablePath"], out var executablePath)
            || string.IsNullOrWhiteSpace(executablePath)
            || !Path.IsPathFullyQualified(executablePath)
            || !TryGetString(
                root["referenceListOutputSchemaVersion"],
                out var referenceListOutputSchemaVersion)
            || referenceListOutputSchemaVersion
                != ReferenceListOutputSchemaVersion)
        {
            return false;
        }

        update = new VbaCompanionExecutableUpdate(executablePath);
        return true;
    }

    private static bool TryGetString(JsonNode? node, out string value)
    {
        value = string.Empty;
        return node is JsonValue jsonValue
            && jsonValue.TryGetValue(out value!);
    }
}

internal sealed class VbaCompanionExecutableNotificationHandler
    : IVbaCompanionExecutableNotificationHandler
{
    private readonly SessionPinnedVbaDevReferenceCatalogDiscovery discovery;
    private readonly Func<IReadOnlyList<string>> captureOpenDocumentUris;
    private readonly IVbaCompanionReferenceCatalogRefresh catalogRefresh;
    private readonly object refreshGate = new();
    private bool initialRefreshCompleted;

    public VbaCompanionExecutableNotificationHandler(
        SessionPinnedVbaDevReferenceCatalogDiscovery discovery,
        Func<IReadOnlyList<string>> captureOpenDocumentUris,
        IVbaCompanionReferenceCatalogRefresh catalogRefresh)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(captureOpenDocumentUris);
        ArgumentNullException.ThrowIfNull(catalogRefresh);
        this.discovery = discovery;
        this.captureOpenDocumentUris = captureOpenDocumentUris;
        this.catalogRefresh = catalogRefresh;
    }

    public bool TryApply(JsonNode? parameters)
    {
        if (!VbaCompanionExecutableNotification.TryParse(
                parameters,
                out var update))
        {
            return false;
        }

        return TryApply(update);
    }

    public bool TryApply(VbaCompanionExecutableUpdate update)
        => TryPrepare(update)?.Apply() == true;

    public VbaCompanionExecutableApplication? TryPrepare(
        VbaCompanionExecutableUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var pinResult = discovery.TryPin(update.ExecutablePath);
        if (pinResult == VbaDevReferenceCatalogPinResult.Rejected)
        {
            return null;
        }

        return new VbaCompanionExecutableApplication(RefreshActiveProjects);
    }

    private bool RefreshActiveProjects()
    {
        lock (refreshGate)
        {
            if (initialRefreshCompleted)
            {
                return true;
            }

            try
            {
                catalogRefresh.RefreshActiveProjects(
                    captureOpenDocumentUris().ToArray());
            }
            catch (Exception)
            {
                return false;
            }

            initialRefreshCompleted = true;
        }

        return true;
    }
}
