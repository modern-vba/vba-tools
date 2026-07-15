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

    /// <summary>
    /// Publishes or clears the validation diagnostic for a project manifest.
    /// </summary>
    /// <param name="uri">The project manifest URI.</param>
    /// <param name="error">The current validation error, or null to clear it.</param>
    /// <param name="cancellationToken">A cancellation token for transport work.</param>
    public Task PublishManifestValidationDiagnosticAsync(
        string uri,
        VbaProjectManifestException? error,
        CancellationToken cancellationToken)
    {
        object[] diagnostics = error is null
            ? []
            :
            [
                new
                {
                    range = new
                    {
                        start = new { line = 0, character = 0 },
                        end = new { line = 0, character = 1 }
                    },
                    severity = 1,
                    code = "invalid-project-manifest",
                    source = "vba-language-server",
                    message = error.Message
                }
            ];
        return transport.WriteNotificationAsync(
            "textDocument/publishDiagnostics",
            new
            {
                uri,
                diagnostics
            },
            cancellationToken);
    }

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
