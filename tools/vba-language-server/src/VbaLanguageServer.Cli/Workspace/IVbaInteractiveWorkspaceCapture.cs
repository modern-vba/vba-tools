using VbaLanguageServer.SourceModel;

namespace VbaLanguageServer.Workspace;

/// <summary>
/// Captures only the immutable workspace views consumed by interactive language features.
/// </summary>
public interface IVbaInteractiveWorkspaceCapture
{
    /// <summary>
    /// Captures the semantic inventory for the project containing an active document.
    /// </summary>
    VbaSemanticInventory CaptureProjectSemanticInventory(
        string activeUri,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Captures one semantic inventory for every distinct tracked project scope.
    /// </summary>
    IReadOnlyList<VbaSemanticInventory> CaptureWorkspaceSemanticInventories(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Captures an exact-version open document without resolving project state.
    /// </summary>
    VbaVersionedDocumentSnapshot? CaptureExactDocumentSnapshot(
        string uri,
        int expectedVersion,
        CancellationToken cancellationToken = default);
}
