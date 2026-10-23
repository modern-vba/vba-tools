using VbaDev.Domain;
using VbaDev.App.Build;
using VbaDev.App.FileSystem;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;

namespace VbaDev.App.Testing;

internal sealed record SnapshotTestInputCleanupResult(
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

internal sealed class SnapshotTestInputPreparationException : Exception
{
    public SnapshotTestInputPreparationException(
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

internal sealed class SnapshotTestInputFactory
{
    private readonly IExactFileSystemObjectOwnershipFactory ownershipFactory;
    private readonly string scratchRoot;
    private readonly IFileSystemPathIdentityResolver pathIdentityResolver;
    private readonly ISnapshotSourceCaptureFactory sourceCaptureFactory;
    private readonly Action<string>? afterWorkspaceCreated;

    public SnapshotTestInputFactory(
        IExactFileSystemObjectOwnershipFactory ownershipFactory,
        IFileSystemPathIdentityResolver pathIdentityResolver)
        : this(ownershipFactory, pathIdentityResolver,
            Path.Combine(Path.GetTempPath(), "vba-dev-snapshot-test"))
    {
    }

    internal SnapshotTestInputFactory(
        IExactFileSystemObjectOwnershipFactory ownershipFactory,
        IFileSystemPathIdentityResolver pathIdentityResolver,
        string scratchRoot,
        ISnapshotSourceCaptureFactory? sourceCaptureFactory = null,
        Action<string>? afterWorkspaceCreated = null)
    {
        this.ownershipFactory = ownershipFactory;
        this.scratchRoot = Path.GetFullPath(scratchRoot);
        this.pathIdentityResolver = pathIdentityResolver;
        this.sourceCaptureFactory = sourceCaptureFactory ?? new SnapshotSourceCaptureFactory(ownershipFactory);
        this.afterWorkspaceCreated = afterWorkspaceCreated;
    }

    private string ValidateInputPlacement(ResolvedProjectContext context, string snapshotPath, string candidate)
    {
        var input = pathIdentityResolver.Resolve(candidate);
        var snapshot = pathIdentityResolver.Resolve(snapshotPath);
        if (FileSystemPathIdentityRelations.RootsOverlap(input, snapshot))
            throw new InvalidOperationException("Snapshot test input must be outside the caller-owned source snapshot.");
        foreach (var (name, document) in context.Manifest.Documents)
        {
            var source = pathIdentityResolver.Resolve(Path.GetFullPath(document.SourcePath, context.ProjectRoot));
            if (FileSystemPathIdentityRelations.RootsOverlap(input, source))
                throw new InvalidOperationException($"Snapshot test input must be outside manifest document source set '{name}'.");
            var workbook = pathIdentityResolver.Resolve(Path.GetFullPath(document.TemplatePath, context.ProjectRoot));
            if (FileSystemPathIdentityRelations.SameOrDescendant(workbook, input)
                || FileSystemPathIdentityRelations.SameOrDescendant(input, workbook))
                throw new InvalidOperationException($"Snapshot test input must be isolated from source workbook '{name}'.");
        }
        return input.OperationPath;
    }

    public SnapshotTestInput Create(
        ResolvedProjectContext context, string sourceSnapshotPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSnapshotPath);
        var candidate = Path.Combine(scratchRoot, Guid.NewGuid().ToString("N"));
        string workspacePath;
        try
        {
            workspacePath = ValidateInputPlacement(context, sourceSnapshotPath, candidate);
            if (!Guid.TryParseExact(Path.GetFileName(workspacePath), "N", out _))
                throw new InvalidOperationException($"Snapshot test workspace must be a GUID child: {workspacePath}");
        }
        catch (Exception error)
        {
            throw new SnapshotTestInputPreparationException(error, candidate, string.Empty);
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
            capture = sourceCaptureFactory.Create(sourceRoot.Route, sourceSnapshotPath, cancellationToken);
            SnapshotTestInput.ValidateLayout(root.Route, capture, pathIdentityResolver);
            captureAccepted = true;
            var workspace = new SnapshotTestInput(ownership, scratch, root.Route, capture, pathIdentityResolver);
            transferred = true;
            return workspace;
        }
        catch (Exception error)
        {
            ReleaseFences();
            var facts = WorkbookAutomationTerminalFacts.Analyze(error);
            if (!facts.ProcessReleaseProven)
                throw new SnapshotTestInputPreparationException(error, workspacePath,
                    $"Snapshot test workspace was retained because owned Excel process release could not be proved: {workspacePath}{Environment.NewLine}");
            var nested = captureAccepted ? capture!.Cleanup()
                : (error as BuildSourceSnapshotCaptureRetainedException)?.CleanupEvidence;
            var evidence = InvocationScratchCleanupEvidence.Combine(nested, scratch.Cleanup());
            var cleanup = SnapshotTestInput.DescribeCleanup(evidence);
            throw new SnapshotTestInputPreparationException(error, workspacePath, cleanup.Warning ?? string.Empty);
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

internal sealed class SnapshotTestInput : IDisposable
{
    private readonly ExactFileSystemObjectOwnership ownership;
    private readonly InvocationScratch scratch;
    private readonly BuildSourceSnapshotCapture sourceCapture;
    private bool sourceTransferred;
    private bool released;
    private SnapshotTestInputCleanupResult? cleanupResult;

    internal SnapshotTestInput(
        ExactFileSystemObjectOwnership ownership, InvocationScratch scratch,
        string workspacePath, BuildSourceSnapshotCapture sourceCapture, IFileSystemPathIdentityResolver pathIdentityResolver)
    {
        ValidateLayout(workspacePath, sourceCapture, pathIdentityResolver);
        this.ownership = ownership;
        this.scratch = scratch;
        this.sourceCapture = sourceCapture;
        WorkspacePath = Path.GetFullPath(workspacePath);
    }

    internal static void ValidateLayout(string workspacePath, BuildSourceSnapshotCapture capture,
        IFileSystemPathIdentityResolver pathIdentityResolver)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var workspace = Path.GetFullPath(workspacePath);
        var capturePath = Path.GetFullPath(capture.StagingPath);
        if (!string.Equals(Path.GetDirectoryName(capturePath), Path.Combine(workspace, "source"), comparison)
            || !Guid.TryParseExact(Path.GetFileName(capturePath), "N", out _))
            throw new InvalidOperationException($"Snapshot test source capture must use the owned workspace layout '{Path.Combine(workspace, "source", "<guid>")}': {capturePath}");
        var container = pathIdentityResolver.Resolve(Path.Combine(workspace, "source"));
        var captured = pathIdentityResolver.Resolve(capturePath);
        if (FileSystemPathIdentityRelations.Same(captured, container)
            || !FileSystemPathIdentityRelations.SameOrDescendant(captured, container))
            throw new InvalidOperationException($"Snapshot test source capture is outside its canonical input container: {capturePath}");
    }

    public string WorkspacePath { get; }
    internal string SourceRootPath => sourceCapture.SourceRootPath;

    internal BuildSourceSnapshotCapture TakeSourceCapture()
    {
        if (sourceTransferred) throw new InvalidOperationException("The snapshot test source capture has already been transferred for materialization.");
        sourceTransferred = true;
        return sourceCapture;
    }

    public SnapshotTestInputCleanupResult Cleanup()
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

    internal static SnapshotTestInputCleanupResult DescribeCleanup(InvocationScratchCleanupEvidence evidence)
        => evidence.Status == InvocationScratchCleanupStatus.Removed
            ? new(true, null, evidence)
            : new(false, $"Warning: Snapshot test workspace could not be removed ({evidence.Status}); retained absolute paths: {string.Join(", ", evidence.RetainedPaths)}{Environment.NewLine}", evidence);

    public void Dispose()
    {
        if (!released) Cleanup();
    }
}
