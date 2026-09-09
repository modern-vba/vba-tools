using VbaDev.Domain;
using VbaDev.App.Build;
using VbaDev.App.FileSystem;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;

namespace VbaDev.App.Testing;

internal sealed record SnapshotTestWorkspaceCleanupResult(
    bool Deleted, string? Warning, InvocationScratchCleanupEvidence Evidence);

internal interface ISnapshotSourceCaptureFactory
{
    BuildSourceSnapshotCapture Create(
        string scratchRoot,
        string sourceSnapshotPath,
        CancellationToken cancellationToken);
}

internal sealed class SnapshotSourceCaptureFactory : ISnapshotSourceCaptureFactory
{
    private readonly VbaSourceAdmission admission;
    private readonly IExactFileSystemObjectOwnershipFactory ownershipFactory;

    internal SnapshotSourceCaptureFactory(IExactFileSystemObjectOwnershipFactory ownershipFactory)
        : this(ownershipFactory, new VbaSourceAdmission(ActiveWindowsAnsiCodePage.Get))
    {
    }

    internal SnapshotSourceCaptureFactory(IExactFileSystemObjectOwnershipFactory ownershipFactory, VbaSourceAdmission admission)
    {
        this.admission = admission;
        this.ownershipFactory = ownershipFactory;
    }

    public BuildSourceSnapshotCapture Create(
        string scratchRoot,
        string sourceSnapshotPath,
        CancellationToken cancellationToken)
        => new BuildSourceSnapshotCaptureFactory(ownershipFactory, scratchRoot, admission)
            .Create(sourceSnapshotPath, cancellationToken);
}

internal sealed class SnapshotTestWorkspacePreparationException : Exception
{
    public SnapshotTestWorkspacePreparationException(
        Exception preparationError,
        string workspacePath,
        string cleanupWarning)
        : base(preparationError.Message, preparationError)
    {
        PreparationError = preparationError;
        WorkspacePath = Path.GetFullPath(workspacePath);
        CleanupWarning = cleanupWarning;
    }

    public Exception PreparationError { get; }

    public string WorkspacePath { get; }

    public string CleanupWarning { get; }
}

internal sealed class SnapshotTestExecutionWorkspaceFactory
{
    private readonly IExactFileSystemObjectOwnershipFactory ownershipFactory;
    private readonly string scratchRoot;
    private readonly BuildSourceSnapshotOutputSafetyValidator outputSafetyValidator;
    private readonly ISnapshotSourceCaptureFactory sourceCaptureFactory;
    private readonly Action<string>? afterWorkspaceCreated;

    public SnapshotTestExecutionWorkspaceFactory(
        IExactFileSystemObjectOwnershipFactory ownershipFactory,
        IFileSystemPathIdentityResolver pathIdentityResolver)
        : this(ownershipFactory, pathIdentityResolver,
            Path.Combine(Path.GetTempPath(), "vba-dev-snapshot-test"))
    {
    }

    internal SnapshotTestExecutionWorkspaceFactory(
        IExactFileSystemObjectOwnershipFactory ownershipFactory,
        IFileSystemPathIdentityResolver pathIdentityResolver,
        string scratchRoot,
        BuildSourceSnapshotOutputSafetyValidator? outputSafetyValidator = null,
        ISnapshotSourceCaptureFactory? sourceCaptureFactory = null,
        Action<string>? afterWorkspaceCreated = null)
    {
        this.ownershipFactory = ownershipFactory;
        this.scratchRoot = Path.GetFullPath(scratchRoot);
        this.outputSafetyValidator = outputSafetyValidator
            ?? new BuildSourceSnapshotOutputSafetyValidator(pathIdentityResolver);
        this.sourceCaptureFactory = sourceCaptureFactory ?? new SnapshotSourceCaptureFactory(ownershipFactory);
        this.afterWorkspaceCreated = afterWorkspaceCreated;
    }

    public SnapshotTestExecutionWorkspace Create(
        ResolvedProjectContext context, string sourceSnapshotPath,
        string workbookFileName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSnapshotPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookFileName);
        if (!string.Equals(workbookFileName, Path.GetFileName(workbookFileName), StringComparison.Ordinal))
            throw new InvalidOperationException($"Snapshot test workbook filename must be a basename: {workbookFileName}");

        var candidate = Path.Combine(scratchRoot, Guid.NewGuid().ToString("N"));
        BuildSourceSnapshotValidatedPaths validated;
        string workspacePath;
        try
        {
            validated = outputSafetyValidator.Validate(context, sourceSnapshotPath, Path.Combine(candidate, workbookFileName));
            workspacePath = Path.GetDirectoryName(validated.OutputPath)!;
            if (!Guid.TryParseExact(Path.GetFileName(workspacePath), "N", out _))
                throw new InvalidOperationException($"Snapshot test workspace must be a GUID child: {workspacePath}");
        }
        catch (Exception error)
        {
            throw new SnapshotTestWorkspacePreparationException(error, candidate, string.Empty);
        }

        // The shared scratch container is not adopted into this invocation's ledger.
        Directory.CreateDirectory(Path.GetDirectoryName(workspacePath)!);
        var ownership = ownershipFactory.Open();
        var scratch = new InvocationScratch(ownership);
        ExactFileSystemObjectOwnership.DirectoryReceipt? root = null;
        ExactFileSystemObjectOwnership.DirectoryReceipt? sourceRoot = null;
        BuildSourceSnapshotCapture? capture = null;
        var captureAccepted = false;
        var transferred = false;
        try
        {
            root = ownership.TryCreateOnlyDirectory(Path.GetDirectoryName(workspacePath)!, Path.GetFileName(workspacePath))
                ?? throw new IOException($"Snapshot test workspace already exists: {workspacePath}");
            scratch.Register(root);
            sourceRoot = ownership.TryCreateOnlyDirectory(root.Route, "source")
                ?? throw new IOException($"Snapshot test source container already exists: {workspacePath}");
            scratch.Register(sourceRoot);
            ReleaseFences();
            afterWorkspaceCreated?.Invoke(workspacePath);
            capture = sourceCaptureFactory.Create(sourceRoot.Route, validated.SourceSnapshotPath, cancellationToken);
            SnapshotTestExecutionWorkspace.ValidateLayout(root.Route, capture, validated.OutputPath);
            captureAccepted = true;
            var workspace = new SnapshotTestExecutionWorkspace(ownership, scratch, root.Route, capture, validated.OutputPath);
            transferred = true;
            return workspace;
        }
        catch (Exception error)
        {
            ReleaseFences();
            var facts = WorkbookAutomationTerminalFacts.Analyze(error);
            if (!facts.ProcessReleaseProven)
                throw new SnapshotTestWorkspacePreparationException(error, workspacePath,
                    $"Snapshot test workspace was retained because owned Excel process release could not be proved: {workspacePath}{Environment.NewLine}");
            var nested = captureAccepted ? capture!.Cleanup()
                : (error as BuildSourceSnapshotCaptureRetainedException)?.CleanupEvidence;
            var evidence = InvocationScratchCleanupEvidence.Combine(nested, scratch.Cleanup());
            var cleanup = SnapshotTestExecutionWorkspace.DescribeCleanup(evidence);
            throw new SnapshotTestWorkspacePreparationException(error, workspacePath, cleanup.Warning ?? string.Empty);
        }
        finally
        {
            if (!transferred) ownership.Dispose();
        }

        void ReleaseFences()
        {
            if (sourceRoot is not null) ownership.ReleaseCreationFence(sourceRoot);
            if (root is not null) ownership.ReleaseCreationFence(root);
        }
    }
}

internal sealed class SnapshotTestExecutionWorkspace : IDisposable
{
    private readonly ExactFileSystemObjectOwnership ownership;
    private readonly InvocationScratch scratch;
    private readonly BuildSourceSnapshotCapture sourceCapture;
    private bool sourceTransferred;
    private bool released;
    private bool workbookRegistered;
    private SnapshotTestWorkspaceCleanupResult? cleanupResult;

    internal SnapshotTestExecutionWorkspace(
        ExactFileSystemObjectOwnership ownership, InvocationScratch scratch,
        string workspacePath, BuildSourceSnapshotCapture sourceCapture, string workbookPath)
    {
        ValidateLayout(workspacePath, sourceCapture, workbookPath);
        this.ownership = ownership;
        this.scratch = scratch;
        this.sourceCapture = sourceCapture;
        WorkspacePath = Path.GetFullPath(workspacePath);
        WorkbookPath = Path.GetFullPath(workbookPath);
    }

    internal static void ValidateLayout(string workspacePath, BuildSourceSnapshotCapture capture, string workbookPath)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var workspace = Path.GetFullPath(workspacePath);
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(workbookPath)), workspace, comparison))
            throw new InvalidOperationException($"Snapshot test workbook must be contained directly in its owned workspace '{workspace}': {workbookPath}");
        var capturePath = Path.GetFullPath(capture.StagingPath);
        if (!string.Equals(Path.GetDirectoryName(capturePath), Path.Combine(workspace, "source"), comparison)
            || !Guid.TryParseExact(Path.GetFileName(capturePath), "N", out _))
            throw new InvalidOperationException($"Snapshot test source capture must use the owned workspace layout '{Path.Combine(workspace, "source", "<guid>")}': {capturePath}");
    }

    public string WorkspacePath { get; }
    public string WorkbookPath { get; }
    internal string SourceRootPath => sourceCapture.SourceRootPath;

    internal BuildSourceSnapshotCapture TakeSourceCapture()
    {
        if (sourceTransferred) throw new InvalidOperationException("The snapshot test source capture has already been transferred for materialization.");
        sourceTransferred = true;
        return sourceCapture;
    }

    // Called once at the successful materialization handoff, before test execution.
    // Cleanup later consumes this fixed receipt; it never captures the path again.
    internal void RegisterCommittedWorkbook(string committedPath)
    {
        ObjectDisposedException.ThrowIf(released, this);
        if (workbookRegistered || !string.Equals(Path.GetFullPath(committedPath), WorkbookPath, StringComparison.Ordinal))
            throw new InvalidOperationException("Snapshot materialization did not return its selected workspace workbook exactly once.");
        scratch.Register(ownership.CaptureTrustedStableFile(committedPath).Receipt);
        workbookRegistered = true;
    }

    public SnapshotTestWorkspaceCleanupResult Cleanup()
    {
        if (cleanupResult is not null) return cleanupResult;
        ObjectDisposedException.ThrowIf(released, this);
        try
        {
            var sourceEvidence = sourceCapture.Cleanup();
            var evidence = InvocationScratchCleanupEvidence.Combine(sourceEvidence, scratch.Cleanup());
            return cleanupResult = DescribeCleanup(evidence);
        }
        finally { RetainWithoutCleanup(); }
    }

    internal void RetainWithoutCleanup()
    {
        if (released) return;
        released = true;
        ownership.Dispose();
    }

    internal static SnapshotTestWorkspaceCleanupResult DescribeCleanup(InvocationScratchCleanupEvidence evidence)
        => evidence.Status == InvocationScratchCleanupStatus.Removed
            ? new(true, null, evidence)
            : new(false, $"Warning: Snapshot test workspace could not be removed ({evidence.Status}); retained absolute paths: {string.Join(", ", evidence.RetainedPaths)}{Environment.NewLine}", evidence);

    public void Dispose()
    {
        if (!released) Cleanup();
    }
}
