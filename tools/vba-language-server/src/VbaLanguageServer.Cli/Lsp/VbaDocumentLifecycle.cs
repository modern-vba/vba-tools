using System.Text.Json.Nodes;
using VbaLanguageServer.Workspace;

namespace VbaLanguageServer.Lsp;

/// <summary>
/// Safely decodes LSP document notifications into discriminated pipeline changes.
/// </summary>
internal sealed class VbaDocumentLifecycle
    : IVbaProjectDiskReconciliationManifestEvents
{
    private readonly VbaLanguageWorkspace workspace;
    private readonly IReferenceCatalogLifecycle catalogLifecycle;
    private readonly VbaDiagnosticsPublisher diagnosticsPublisher;
    private readonly VbaDocumentChangePipeline pipeline;

    /// <summary>
    /// Creates a document lifecycle handler.
    /// </summary>
    /// <param name="transport">The transport used to publish diagnostics.</param>
    /// <param name="workspace">The workspace that tracks document authority.</param>
    /// <param name="catalogRefresh">The coordinator used for reference trace and refresh work.</param>
    public VbaDocumentLifecycle(
        LspMessageTransport transport,
        VbaLanguageWorkspace workspace,
        IReferenceCatalogLifecycle catalogRefresh)
    {
        this.workspace = workspace;
        catalogLifecycle = catalogRefresh;
        diagnosticsPublisher = new VbaDiagnosticsPublisher(transport, workspace);
        pipeline = new VbaDocumentChangePipeline(
            workspace,
            catalogRefresh,
            diagnosticsPublisher);
    }

    /// <summary>
    /// Attaches background diagnostics to the runtime-owned scheduler.
    /// </summary>
    public void AttachScheduler(VbaInteractiveWorkScheduler scheduler)
        => diagnosticsPublisher.AttachScheduler(scheduler);

    /// <summary>
    /// Stops pending document-owned background work before scheduler shutdown.
    /// </summary>
    public void Stop()
        => diagnosticsPublisher.Stop();

    internal VbaProjectReconciler CreateProjectReconciler()
        => new(
            workspace,
            diagnosticsPublisher,
            this);

    void IVbaProjectDiskReconciliationManifestEvents.ManifestSelectionChanged(
        string uri,
        string text,
        CancellationToken cancellationToken)
    {
        _ = diagnosticsPublisher.PublishManifestValidationDiagnosticAsync(
            uri,
            error: null,
            cancellationToken);
        catalogLifecycle.ApplyManifestSelectionChange(uri, text);
    }

    void IVbaProjectDiskReconciliationManifestEvents.ManifestDeleted(
        string uri,
        CancellationToken cancellationToken)
    {
        _ = diagnosticsPublisher.PublishManifestValidationDiagnosticAsync(
            uri,
            error: null,
            cancellationToken);
        catalogLifecycle.DeactivateManifest(uri);
    }

    void IVbaProjectDiskReconciliationManifestEvents.ManifestValidationFailed(
        string uri,
        VbaProjectManifestException error,
        CancellationToken cancellationToken)
        => _ = diagnosticsPublisher.PublishManifestValidationDiagnosticAsync(
            uri,
            error,
            cancellationToken);

    void IVbaProjectDiskReconciliationManifestEvents.ManifestValidationRecovered(
        string uri,
        CancellationToken cancellationToken)
        => _ = diagnosticsPublisher.PublishManifestValidationDiagnosticAsync(
            uri,
            error: null,
            cancellationToken);

    void IVbaProjectDiskReconciliationManifestEvents.ProjectAuthorityTransferred(
        string sourceUri,
        CancellationToken cancellationToken)
        => catalogLifecycle.ActivateProject(sourceUri);

    /// <summary>
    /// Records a valid textDocument/didOpen notification.
    /// </summary>
    public Task RecordOpenedDocumentAsync(JsonNode? parameters, CancellationToken cancellationToken)
        => TryCreateOpenedChange(parameters, out var change)
            ? pipeline.ApplyAsync(change, cancellationToken)
            : Task.CompletedTask;

    /// <summary>
    /// Records a valid full-text textDocument/didChange notification.
    /// </summary>
    public Task RecordChangedDocumentAsync(JsonNode? parameters, CancellationToken cancellationToken)
        => TryCreateChangedChange(parameters, out var change)
            ? pipeline.ApplyAsync(change, cancellationToken)
            : Task.CompletedTask;

    /// <summary>
    /// Records a valid textDocument/didClose notification.
    /// </summary>
    public Task RecordClosedDocumentAsync(JsonNode? parameters, CancellationToken cancellationToken)
        => TryCreateClosedChange(parameters, out var change)
            ? pipeline.ApplyAsync(change, cancellationToken)
            : Task.CompletedTask;

    /// <summary>
    /// Records valid workspace watched-file changes and ignores malformed entries.
    /// </summary>
    public async Task RecordWatchedFilesChangedAsync(
        JsonNode? parameters,
        CancellationToken cancellationToken)
    {
        if (parameters is not JsonObject parameterObject
            || parameterObject["changes"] is not JsonArray changes)
        {
            return;
        }

        foreach (var item in changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryCreateWatchedFileChange(item, out var change))
            {
                continue;
            }

            await pipeline.ApplyAsync(change, cancellationToken);
        }
    }

    private static bool TryCreateOpenedChange(
        JsonNode? parameters,
        out VbaTextDocumentOpenedChange change)
    {
        change = default!;
        if (!TryGetTextDocument(parameters, out var textDocument, out var uri)
            || !TryGetInt32(textDocument["version"], out var version)
            || !TryGetString(textDocument["text"], out var text))
        {
            return false;
        }

        change = new VbaTextDocumentOpenedChange(uri, version, text);
        return true;
    }

    private static bool TryCreateChangedChange(
        JsonNode? parameters,
        out VbaTextDocumentChangedChange change)
    {
        change = default!;
        if (!TryGetTextDocument(parameters, out var textDocument, out var uri)
            || !TryGetInt32(textDocument["version"], out var version)
            || parameters is not JsonObject parameterObject
            || parameterObject["contentChanges"] is not JsonArray contentChanges
            || contentChanges.Count == 0
            || contentChanges[contentChanges.Count - 1] is not JsonObject contentChange
            || !TryGetString(contentChange["text"], out var text))
        {
            return false;
        }

        change = new VbaTextDocumentChangedChange(uri, version, text);
        return true;
    }

    private static bool TryCreateClosedChange(
        JsonNode? parameters,
        out VbaTextDocumentClosedChange change)
    {
        change = default!;
        if (!TryGetTextDocument(parameters, out _, out var uri))
        {
            return false;
        }

        change = new VbaTextDocumentClosedChange(uri);
        return true;
    }

    private static bool TryCreateWatchedFileChange(
        JsonNode? item,
        out VbaDocumentChange change)
    {
        change = default!;
        if (item is not JsonObject itemObject
            || !TryGetString(itemObject["uri"], out var uri)
            || string.IsNullOrWhiteSpace(uri)
            || !TryGetInt32(itemObject["type"], out var type))
        {
            return false;
        }

        switch (type)
        {
            case 1:
            case 2:
                change = new VbaWatchedFileReloadChange(uri);
                return true;
            case 3:
                change = new VbaWatchedFileDeletedChange(uri);
                return true;
            default:
                return false;
        }
    }

    private static bool TryGetTextDocument(
        JsonNode? parameters,
        out JsonObject textDocument,
        out string uri)
    {
        textDocument = default!;
        uri = "";
        if (parameters is not JsonObject parameterObject
            || parameterObject["textDocument"] is not JsonObject documentObject
            || !TryGetString(documentObject["uri"], out uri)
            || string.IsNullOrWhiteSpace(uri))
        {
            return false;
        }

        textDocument = documentObject;
        return true;
    }

    private static bool TryGetString(JsonNode? node, out string value)
    {
        value = "";
        return node is JsonValue jsonValue
            && jsonValue.TryGetValue(out value!);
    }

    private static bool TryGetInt32(JsonNode? node, out int value)
    {
        value = 0;
        return node is JsonValue jsonValue
            && jsonValue.TryGetValue(out value);
    }
}
