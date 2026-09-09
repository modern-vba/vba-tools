using VbaDev.App.FileSystem;

namespace VbaDev.App.Build;

/// <summary>
/// Owns a sibling staging workbook until it can replace one selected output atomically.
/// </summary>
public sealed class WorkbookOutputTransaction : IWorkbookOutputTransaction
{
    private readonly string targetWorkbookPath;
    private readonly WorkbookStagingArtifact staging;
    private bool committed;
    private bool disposed;

    private WorkbookOutputTransaction(string targetWorkbookPath, WorkbookStagingArtifact staging)
    {
        this.targetWorkbookPath = targetWorkbookPath;
        this.staging = staging;
    }

    /// <summary>Gets the absolute path of the invocation-owned staging workbook.</summary>
    public string StagingWorkbookPath => staging.Path;

    internal InvocationScratchCleanupEvidence? CleanupEvidence => staging.CleanupEvidence;

    /// <summary>Creates an exact-owned sibling copy while leaving the selected output unchanged.</summary>
    public static WorkbookOutputTransaction Create(
        IExactFileSystemObjectOwnershipFactory ownershipFactory,
        CapturedWorkbookTemplate template,
        string targetWorkbookPath)
        => Create(targetWorkbookPath, (directory, name) => template.CreateStage(ownershipFactory, directory, name));

    public static WorkbookOutputTransaction Create(
        IExactFileSystemObjectOwnershipFactory ownershipFactory,
        string templateWorkbookPath,
        string targetWorkbookPath)
        => Create(ownershipFactory, templateWorkbookPath, targetWorkbookPath, afterCreated: null);

    internal static WorkbookOutputTransaction Create(
        IExactFileSystemObjectOwnershipFactory ownershipFactory,
        string templateWorkbookPath,
        string targetWorkbookPath,
        Action<string>? afterCreated)
        => Create(targetWorkbookPath, (directory, name) => WorkbookStagingArtifact.CreateCopy(
            ownershipFactory, templateWorkbookPath, directory, name, afterCreated: afterCreated));

    private static WorkbookOutputTransaction Create(string targetWorkbookPath,
        Func<string, string, WorkbookStagingArtifact> createStage)
    {
        var target = Path.GetFullPath(targetWorkbookPath);
        var directory = Path.GetDirectoryName(target)
            ?? throw new BuildCommandException($"Target workbook path is invalid: {target}");
        var name = $".{Path.GetFileNameWithoutExtension(target)}.{Guid.NewGuid():N}.tmp{Path.GetExtension(target)}";
        return new WorkbookOutputTransaction(target, createStage(directory, name));
    }

    /// <summary>Captures the candidate produced by a successful scenario Save.</summary>
    public void CaptureSavedWorkbook() => staging.CaptureSavedWorkbook();

    /// <summary>Completes saved-version proof after the runtime proves process release.</summary>
    public void CompleteSavedCapture() => staging.CompleteSavedCapture();

    /// <summary>Atomically replaces the selected output with the proved staging workbook.</summary>
    public void Commit()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (committed) return;
        staging.ProveUnchanged();
        File.Move(StagingWorkbookPath, targetWorkbookPath, overwrite: true);
        committed = true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (committed) staging.ReleaseWithoutCleanup();
        else staging.Dispose();
    }

    /// <summary>Closes the owner without deleting scratch whose Excel lifetime is unproved.</summary>
    public void RetainWithoutCleanup()
    {
        disposed = true;
        staging.ReleaseWithoutCleanup();
    }
}

/// <summary>Owns staged workbook version evidence, cleanup selection, and atomic commitment.</summary>
public interface IWorkbookOutputTransaction : IDisposable
{
    string StagingWorkbookPath { get; }
    void CaptureSavedWorkbook();
    void CompleteSavedCapture();
    void Commit();
    void RetainWithoutCleanup();
}

/// <summary>Creates sibling-staged workbook output transactions.</summary>
public interface IWorkbookOutputTransactionFactory
{
    IWorkbookOutputTransaction Create(string templateWorkbookPath, string targetWorkbookPath);
    IWorkbookOutputTransaction Create(CapturedWorkbookTemplate template, string targetWorkbookPath);
}

/// <summary>Creates output transactions with invocation-scoped exact ownership.</summary>
public sealed class WorkbookOutputTransactionFactory(IExactFileSystemObjectOwnershipFactory ownershipFactory)
    : IWorkbookOutputTransactionFactory
{
    public IWorkbookOutputTransaction Create(string templateWorkbookPath, string targetWorkbookPath)
        => WorkbookOutputTransaction.Create(ownershipFactory, templateWorkbookPath, targetWorkbookPath);

    public IWorkbookOutputTransaction Create(CapturedWorkbookTemplate template, string targetWorkbookPath)
        => WorkbookOutputTransaction.Create(ownershipFactory, template, targetWorkbookPath);
}
