using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.Syntax;
using VbaLanguageServer.Workspace;

namespace VbaLanguageServer.Lsp;

/// <summary>
/// Publishes document diagnostics to the LSP transport.
/// </summary>
internal sealed class VbaDiagnosticsPublisher
{
    private readonly LspMessageTransport transport;
    private readonly VbaLanguageWorkspace workspace;

    /// <summary>
    /// Creates a diagnostics publisher.
    /// </summary>
    /// <param name="transport">The transport used to publish diagnostics.</param>
    /// <param name="workspace">The workspace that owns parsed syntax trees.</param>
    public VbaDiagnosticsPublisher(LspMessageTransport transport, VbaLanguageWorkspace workspace)
    {
        this.transport = transport;
        this.workspace = workspace;
    }

    /// <summary>
    /// Publishes diagnostics for a tracked document, or clears diagnostics when it is no longer parsed.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="cancellationToken">A cancellation token for transport work.</param>
    public Task PublishTrackedDiagnosticsAsync(string uri, CancellationToken cancellationToken)
    {
        var syntaxTree = workspace.GetDocumentSyntaxTree(uri, cancellationToken);
        return syntaxTree is null
            ? PublishEmptyDiagnosticsAsync(uri, cancellationToken)
            : PublishDiagnosticsAsync(uri, syntaxTree, cancellationToken);
    }

    /// <summary>
    /// Clears diagnostics for a document.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="cancellationToken">A cancellation token for transport work.</param>
    public Task PublishEmptyDiagnosticsAsync(string uri, CancellationToken cancellationToken)
        => transport.WriteNotificationAsync(
            "textDocument/publishDiagnostics",
            new
            {
                uri,
                diagnostics = Array.Empty<object>()
            },
            cancellationToken);

    private Task PublishDiagnosticsAsync(
        string uri,
        VbaSyntaxTree syntaxTree,
        CancellationToken cancellationToken)
        => transport.WriteNotificationAsync(
            "textDocument/publishDiagnostics",
            new
            {
                uri,
                diagnostics = VbaLspFeatureProjection.CreateDiagnostics(
                    VbaDocumentDiagnostics.Collect(syntaxTree, uri))
            },
            cancellationToken);
}
