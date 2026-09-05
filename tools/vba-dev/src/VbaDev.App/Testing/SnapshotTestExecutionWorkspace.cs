using VbaDev.Domain;
using VbaDev.App.Build;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;

namespace VbaDev.App.Testing;

internal interface ISnapshotTestWorkspaceFileSystem
{
    void DeleteDirectory(string path);

    void Delay(TimeSpan delay);
}

internal sealed class SnapshotTestWorkspaceFileSystem : ISnapshotTestWorkspaceFileSystem
{
    public void DeleteDirectory(string path) => Directory.Delete(path, recursive: true);

    public void Delay(TimeSpan delay)
    {
        if (delay > TimeSpan.Zero)
        {
            Thread.Sleep(delay);
        }
    }
}

internal sealed record SnapshotTestWorkspaceCleanupResult(bool Deleted, string? Warning);

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

    internal SnapshotSourceCaptureFactory()
        : this(new VbaSourceAdmission(ActiveWindowsAnsiCodePage.Get))
    {
    }

    internal SnapshotSourceCaptureFactory(VbaSourceAdmission admission)
    {
        this.admission = admission;
    }

    public BuildSourceSnapshotCapture Create(
        string scratchRoot,
        string sourceSnapshotPath,
        CancellationToken cancellationToken)
        => new BuildSourceSnapshotCaptureFactory(scratchRoot, admission)
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

internal static class SnapshotTestWorkspaceCleanup
{
    public static string ValidateOwnedWorkspacePath(
        string scratchRoot,
        string workspacePath)
    {
        var absoluteScratchRoot = Path.GetFullPath(scratchRoot);
        var absoluteWorkspacePath = Path.GetFullPath(workspacePath);
        if (!string.Equals(
                Path.GetDirectoryName(absoluteWorkspacePath),
                absoluteScratchRoot,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal)
            || !Guid.TryParseExact(Path.GetFileName(absoluteWorkspacePath), "N", out _))
        {
            throw new InvalidOperationException(
                $"Snapshot test workspace is not a direct GUID child of its scratch root: {absoluteWorkspacePath}");
        }

        return absoluteWorkspacePath;
    }

    public static SnapshotTestWorkspaceCleanupResult Run(
        string workspacePath,
        ISnapshotTestWorkspaceFileSystem fileSystem,
        int cleanupAttempts,
        TimeSpan retryDelay)
    {
        for (var attempt = 1; attempt <= cleanupAttempts; attempt++)
        {
            try
            {
                fileSystem.DeleteDirectory(workspacePath);
                return new SnapshotTestWorkspaceCleanupResult(
                    Deleted: true,
                    Warning: null);
            }
            catch (DirectoryNotFoundException)
            {
                return new SnapshotTestWorkspaceCleanupResult(
                    Deleted: true,
                    Warning: null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A transient file-system owner can release between bounded attempts.
            }

            if (attempt < cleanupAttempts)
            {
                fileSystem.Delay(retryDelay);
            }
        }

        return new SnapshotTestWorkspaceCleanupResult(
            Deleted: false,
            Warning: $"Warning: Snapshot test workspace could not be removed and was retained at: {workspacePath}{Environment.NewLine}");
    }
}

internal sealed class SnapshotTestExecutionWorkspaceFactory
{
    private readonly string scratchRoot;
    private readonly ISnapshotTestWorkspaceFileSystem fileSystem;
    private readonly int cleanupAttempts;
    private readonly TimeSpan retryDelay;
    private readonly BuildSourceSnapshotOutputSafetyValidator outputSafetyValidator;
    private readonly ISnapshotSourceCaptureFactory sourceCaptureFactory;

    public SnapshotTestExecutionWorkspaceFactory(IFileSystemPathIdentityResolver pathIdentityResolver)
        : this(
            pathIdentityResolver,
            Path.Combine(Path.GetTempPath(), "vba-dev-snapshot-test"),
            new SnapshotTestWorkspaceFileSystem(),
            cleanupAttempts: 3,
            retryDelay: TimeSpan.FromMilliseconds(50),
            new BuildSourceSnapshotOutputSafetyValidator(pathIdentityResolver),
            new SnapshotSourceCaptureFactory())
    {
    }

    internal SnapshotTestExecutionWorkspaceFactory(
        IFileSystemPathIdentityResolver pathIdentityResolver,
        string scratchRoot)
        : this(
            pathIdentityResolver,
            scratchRoot,
            new SnapshotTestWorkspaceFileSystem(),
            cleanupAttempts: 3,
            retryDelay: TimeSpan.FromMilliseconds(50),
            new BuildSourceSnapshotOutputSafetyValidator(pathIdentityResolver),
            new SnapshotSourceCaptureFactory())
    {
    }

    internal SnapshotTestExecutionWorkspaceFactory(
        IFileSystemPathIdentityResolver pathIdentityResolver,
        string scratchRoot,
        ISnapshotTestWorkspaceFileSystem fileSystem,
        int cleanupAttempts,
        TimeSpan retryDelay,
        BuildSourceSnapshotOutputSafetyValidator? outputSafetyValidator = null,
        ISnapshotSourceCaptureFactory? sourceCaptureFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchRoot);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cleanupAttempts);
        ArgumentOutOfRangeException.ThrowIfLessThan(retryDelay, TimeSpan.Zero);
        this.scratchRoot = Path.GetFullPath(scratchRoot);
        this.fileSystem = fileSystem;
        this.cleanupAttempts = cleanupAttempts;
        this.retryDelay = retryDelay;
        this.outputSafetyValidator = outputSafetyValidator
            ?? new BuildSourceSnapshotOutputSafetyValidator(pathIdentityResolver);
        this.sourceCaptureFactory = sourceCaptureFactory
            ?? new SnapshotSourceCaptureFactory();
    }

    public SnapshotTestExecutionWorkspace Create(
        ResolvedProjectContext context,
        string sourceSnapshotPath,
        string workbookFileName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSnapshotPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookFileName);
        if (!string.Equals(
            workbookFileName,
            Path.GetFileName(workbookFileName),
            StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Snapshot test workbook filename must be a basename: {workbookFileName}");
        }

        var candidateWorkspacePath = Path.Combine(
            scratchRoot,
            Guid.NewGuid().ToString("N"));
        var candidateWorkbookPath = Path.Combine(
            candidateWorkspacePath,
            workbookFileName);
        BuildSourceSnapshotValidatedPaths validatedPaths;
        string workspacePath;
        string operationScratchRoot;
        var privateWorkspacePath = candidateWorkspacePath;
        try
        {
            validatedPaths = outputSafetyValidator.Validate(
                context,
                sourceSnapshotPath,
                candidateWorkbookPath);
            workspacePath = Path.GetDirectoryName(validatedPaths.OutputPath)
                ?? throw new InvalidOperationException(
                    $"Snapshot test workspace could not be resolved: {candidateWorkspacePath}");
            privateWorkspacePath = workspacePath;
            operationScratchRoot = Path.GetDirectoryName(workspacePath)
                ?? throw new InvalidOperationException(
                    $"Snapshot test scratch root could not be resolved: {workspacePath}");
            workspacePath = SnapshotTestWorkspaceCleanup.ValidateOwnedWorkspacePath(
                operationScratchRoot,
                workspacePath);
        }
        catch (Exception preparationError)
        {
            throw new SnapshotTestWorkspacePreparationException(
                preparationError,
                privateWorkspacePath,
                cleanupWarning: string.Empty);
        }

        BuildSourceSnapshotCapture? sourceCapture = null;
        try
        {
            Directory.CreateDirectory(workspacePath);
            sourceCapture = sourceCaptureFactory.Create(
                Path.Combine(workspacePath, "source"),
                validatedPaths.SourceSnapshotPath,
                cancellationToken);
            return new SnapshotTestExecutionWorkspace(
                operationScratchRoot,
                workspacePath,
                sourceCapture,
                validatedPaths.OutputPath,
                fileSystem,
                cleanupAttempts,
                retryDelay);
        }
        catch (Exception preparationError)
        {
            var cleanup = SnapshotTestWorkspaceCleanup.Run(
                workspacePath,
                fileSystem,
                cleanupAttempts,
                retryDelay);
            throw new SnapshotTestWorkspacePreparationException(
                preparationError,
                workspacePath,
                cleanup.Warning ?? string.Empty);
        }
    }
}

internal sealed class SnapshotTestExecutionWorkspace : IDisposable
{
    private BuildSourceSnapshotCapture? sourceCapture;
    private readonly AdmittedVbaSourceSet admission;
    private readonly string sourceRootPath;
    private readonly ISnapshotTestWorkspaceFileSystem fileSystem;
    private readonly int cleanupAttempts;
    private readonly TimeSpan retryDelay;
    private SnapshotTestWorkspaceCleanupResult? cleanupResult;

    internal SnapshotTestExecutionWorkspace(
        string scratchRoot,
        string workspacePath,
        BuildSourceSnapshotCapture sourceCapture,
        string workbookPath,
        ISnapshotTestWorkspaceFileSystem fileSystem,
        int cleanupAttempts,
        TimeSpan retryDelay)
    {
        WorkspacePath = SnapshotTestWorkspaceCleanup.ValidateOwnedWorkspacePath(
            scratchRoot,
            workspacePath);

        var absoluteWorkbookPath = Path.GetFullPath(workbookPath);
        if (!string.Equals(
                Path.GetDirectoryName(absoluteWorkbookPath),
                WorkspacePath,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Snapshot test workbook must be contained directly in its owned workspace '{WorkspacePath}': {absoluteWorkbookPath}");
        }

        var absoluteSourceCapturePath = Path.GetFullPath(sourceCapture.StagingPath);
        var sourceCaptureRoot = Path.GetDirectoryName(absoluteSourceCapturePath);
        var sourceCaptureWorkspace = sourceCaptureRoot is null
            ? null
            : Path.GetDirectoryName(sourceCaptureRoot);
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(sourceCaptureWorkspace, WorkspacePath, pathComparison)
            || !string.Equals(
                Path.GetFileName(sourceCaptureRoot),
                "source",
                pathComparison)
            || !Guid.TryParseExact(
                Path.GetFileName(absoluteSourceCapturePath),
                "N",
                out _))
        {
            throw new InvalidOperationException(
                $"Snapshot test source capture must use the owned workspace layout '{Path.Combine(WorkspacePath, "source", "<guid>")}': {absoluteSourceCapturePath}");
        }

        this.sourceCapture = sourceCapture;
        admission = sourceCapture.Admission;
        sourceRootPath = sourceCapture.SourceRootPath;
        WorkbookPath = absoluteWorkbookPath;
        this.fileSystem = fileSystem;
        this.cleanupAttempts = cleanupAttempts;
        this.retryDelay = retryDelay;
    }

    public string WorkspacePath { get; }

    internal AdmittedVbaSourceSet Admission => admission;

    internal string SourceRootPath => sourceRootPath;

    public string WorkbookPath { get; }

    internal BuildSourceSnapshotCapture TakeSourceCapture()
        => Interlocked.Exchange(ref sourceCapture, null)
            ?? throw new InvalidOperationException(
                "The snapshot test source capture has already been transferred for materialization.");

    public SnapshotTestWorkspaceCleanupResult Cleanup()
    {
        if (cleanupResult is not null)
        {
            return cleanupResult;
        }

        cleanupResult = SnapshotTestWorkspaceCleanup.Run(
            WorkspacePath,
            fileSystem,
            cleanupAttempts,
            retryDelay);
        return cleanupResult;
    }

    public void Dispose() => Cleanup();
}
