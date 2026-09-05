using VbaDev.App.FileSystem;
using VbaDev.Domain;

namespace VbaDev.App.Workbooks;

/// <summary>
/// Describes diagnostic identity and byte evidence for one initial workbook.
/// These reconstructible values do not grant filesystem mutation authority.
/// </summary>
/// <param name="WorkbookPath">The absolute workbook path.</param>
/// <param name="ObjectIdentity">The stable file identity on its volume.</param>
/// <param name="Length">The captured byte length.</param>
/// <param name="Sha256">The lowercase SHA-256 digest of the captured bytes.</param>
public sealed record InitialWorkbookArtifactEvidence(
    string WorkbookPath,
    FileSystemObjectIdentity ObjectIdentity,
    long Length,
    string Sha256);

/// <summary>
/// Describes a successfully created initial workbook and its default references.
/// </summary>
/// <param name="ReferenceNames">The reference names present in VBE order.</param>
/// <param name="ArtifactEvidence">Diagnostic evidence for the exact created workbook.</param>
public sealed record InitialWorkbookCreationResult(
    IReadOnlyList<string> ReferenceNames,
    InitialWorkbookArtifactEvidence ArtifactEvidence)
{
    internal ExactFileSystemObjectOwnership.FileReceipt? OwnedArtifactReceipt { get; init; }
}

/// <summary>
/// Creates the project artifact in the caller's invocation ownership session.
/// Diagnostic evidence alone never grants new-project rollback authority.
/// </summary>
internal interface IReceiptInitialWorkbookCreator : IInitialWorkbookCreator
{
    Task<InitialWorkbookCreationResult> CreateInitialWorkbookAsync(
        string workbookPath,
        ExactFileSystemObjectOwnership ownership,
        CancellationToken cancellationToken);
}

/// <summary>
/// Reports a workbook path that could not be safely removed after creation failed.
/// </summary>
public sealed class InitialWorkbookArtifactRetainedException : Exception
{
    /// <summary>
    /// Creates a retained-artifact failure.
    /// </summary>
    public InitialWorkbookArtifactRetainedException(
        string workbookPath,
        InitialWorkbookArtifactEvidence? expectedArtifact,
        bool targetChanged,
        Exception innerException)
        : base(
            targetChanged
                ? $"The initial workbook target changed and was preserved: '{workbookPath}'."
                : $"The initial workbook could not be safely removed: '{workbookPath}'.",
            innerException)
    {
        WorkbookPath = workbookPath;
        ExpectedArtifact = expectedArtifact;
        TargetChanged = targetChanged;
    }

    /// <summary>
    /// Gets the absolute path that was preserved.
    /// </summary>
    public string WorkbookPath { get; }

    /// <summary>
    /// Gets diagnostic saved-workbook evidence, when available. These values
    /// cannot confer rollback authority.
    /// </summary>
    public InitialWorkbookArtifactEvidence? ExpectedArtifact { get; }

    /// <summary>
    /// Gets whether the path no longer names the exact created object and bytes.
    /// </summary>
    public bool TargetChanged { get; }
}

/// <summary>
/// Creates the initial source template workbook for a new workbook-backed project.
/// </summary>
public interface IInitialWorkbookCreator
{
    /// <summary>
    /// Creates an initial macro-enabled workbook and returns its references and artifact evidence.
    /// </summary>
    /// <param name="workbookPath">The workbook path to create.</param>
    /// <returns>The created workbook result.</returns>
    InitialWorkbookCreationResult CreateInitialWorkbook(string workbookPath);

    /// <summary>
    /// Creates an initial macro-enabled workbook through a cancellation-aware path.
    /// </summary>
    /// <param name="workbookPath">The workbook path to create.</param>
    /// <param name="cancellationToken">The cancellation request for workbook creation.</param>
    /// <returns>The created workbook result.</returns>
    Task<InitialWorkbookCreationResult> CreateInitialWorkbookAsync(
        string workbookPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CreateInitialWorkbook(workbookPath));
    }
}
