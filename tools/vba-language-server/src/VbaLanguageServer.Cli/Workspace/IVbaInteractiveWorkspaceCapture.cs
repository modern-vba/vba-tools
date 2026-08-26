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
    /// Captures the immutable semantic input and source-revision fence used by Rename.
    /// </summary>
    VbaRenameProjectSnapshotCapture CaptureRenameProjectSnapshot(
        string activeUri,
        CancellationToken cancellationToken = default)
        => VbaRenameProjectSnapshotCapture.CreateStable(
            CaptureProjectSemanticInventory(activeUri, cancellationToken));

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

/// <summary>
/// Retains one Rename request's immutable semantic snapshot and freshness fence.
/// </summary>
public sealed class VbaRenameProjectSnapshotCapture : IDisposable
{
    private readonly Func<bool> hasParticipatingSourceChanged;
    private IDisposable? revisionLease;

    internal VbaRenameProjectSnapshotCapture(
        VbaSemanticInventory semanticInventory,
        Func<bool> hasParticipatingSourceChanged,
        IDisposable? revisionLease,
        string? analysisFailureMessage = null)
    {
        SemanticInventory = semanticInventory;
        this.hasParticipatingSourceChanged = hasParticipatingSourceChanged;
        this.revisionLease = revisionLease;
        AnalysisFailureMessage = analysisFailureMessage;
    }

    /// <summary>
    /// Gets the immutable semantic inventory captured at request start.
    /// </summary>
    public VbaSemanticInventory SemanticInventory { get; }

    internal string? AnalysisFailureMessage { get; }

    internal bool HasParticipatingSourceChanged()
        => hasParticipatingSourceChanged();

    internal static VbaRenameProjectSnapshotCapture CreateStable(
        VbaSemanticInventory semanticInventory)
        => new(semanticInventory, static () => false, revisionLease: null);

    /// <inheritdoc />
    public void Dispose()
        => Interlocked.Exchange(ref revisionLease, null)?.Dispose();
}
