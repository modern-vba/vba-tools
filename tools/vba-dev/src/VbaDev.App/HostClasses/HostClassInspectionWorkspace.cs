namespace VbaDev.App.HostClasses;

internal interface IHostClassInspectionWorkspaceFileSystem
{
    bool FileExists(string path);

    void CreateDirectory(string path);

    void CopyFile(string sourcePath, string destinationPath);

    void DeleteDirectory(string path);

    void Delay(TimeSpan delay);
}

internal sealed class HostClassInspectionWorkspaceFileSystem
    : IHostClassInspectionWorkspaceFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void CopyFile(string sourcePath, string destinationPath)
        => File.Copy(sourcePath, destinationPath, overwrite: false);

    public void DeleteDirectory(string path) => Directory.Delete(path, recursive: true);

    public void Delay(TimeSpan delay)
    {
        if (delay > TimeSpan.Zero)
        {
            Thread.Sleep(delay);
        }
    }
}

internal sealed record HostClassInspectionWorkspaceCleanupResult(
    bool Deleted,
    string? RetainedPath);

internal sealed class HostClassInspectionPreparationException : Exception
{
    public HostClassInspectionPreparationException(
        string sourceTemplatePath,
        string workspacePath,
        bool workspaceRetained,
        Exception innerException)
        : base(
            workspaceRetained
                ? $"The host-class inspection workspace could not be prepared for source template: {sourceTemplatePath}. Retained inspection workspace: {Path.GetFullPath(workspacePath)}"
                : $"The host-class inspection workspace could not be prepared for source template: {sourceTemplatePath}",
            innerException)
    {
        WorkspacePath = Path.GetFullPath(workspacePath);
    }

    public string WorkspacePath { get; }
}

internal sealed class HostClassInspectionWorkspaceFactory
{
    private readonly string scratchRoot;
    private readonly IHostClassInspectionWorkspaceFileSystem fileSystem;
    private readonly int cleanupAttempts;
    private readonly TimeSpan retryDelay;

    public HostClassInspectionWorkspaceFactory()
        : this(
            Path.Combine(Path.GetTempPath(), "vba-dev-host-class-inspection"),
            new HostClassInspectionWorkspaceFileSystem(),
            cleanupAttempts: 3,
            retryDelay: TimeSpan.FromMilliseconds(50))
    {
    }

    internal HostClassInspectionWorkspaceFactory(
        string scratchRoot,
        IHostClassInspectionWorkspaceFileSystem fileSystem,
        int cleanupAttempts,
        TimeSpan retryDelay)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchRoot);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cleanupAttempts);
        ArgumentOutOfRangeException.ThrowIfLessThan(retryDelay, TimeSpan.Zero);
        this.scratchRoot = Path.GetFullPath(scratchRoot);
        this.fileSystem = fileSystem;
        this.cleanupAttempts = cleanupAttempts;
        this.retryDelay = retryDelay;
    }

    public HostClassInspectionWorkspace Create(string sourceTemplatePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceTemplatePath);
        var sourcePath = Path.GetFullPath(sourceTemplatePath);
        var workspacePath = ValidateOwnedWorkspacePath(
            scratchRoot,
            Path.Combine(scratchRoot, Guid.NewGuid().ToString("N")));
        var workbookPath = Path.Combine(workspacePath, Path.GetFileName(sourcePath));
        try
        {
            if (!fileSystem.FileExists(sourcePath))
            {
                throw new FileNotFoundException(
                    "The selected source-template workbook was not found.",
                    sourcePath);
            }

            fileSystem.CreateDirectory(workspacePath);
            fileSystem.CopyFile(sourcePath, workbookPath);
            return new HostClassInspectionWorkspace(
                scratchRoot,
                workspacePath,
                workbookPath,
                fileSystem,
                cleanupAttempts,
                retryDelay);
        }
        catch (Exception exception)
        {
            var cleanup = Cleanup(
                scratchRoot,
                workspacePath,
                fileSystem,
                cleanupAttempts,
                retryDelay);
            throw new HostClassInspectionPreparationException(
                sourcePath,
                workspacePath,
                !cleanup.Deleted,
                exception);
        }
    }

    internal static string ValidateOwnedWorkspacePath(string scratchRoot, string workspacePath)
    {
        var absoluteScratchRoot = Path.GetFullPath(scratchRoot);
        var absoluteWorkspacePath = Path.GetFullPath(workspacePath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(
                Path.GetDirectoryName(absoluteWorkspacePath),
                absoluteScratchRoot,
                comparison) ||
            !Guid.TryParseExact(Path.GetFileName(absoluteWorkspacePath), "N", out _))
        {
            throw new InvalidOperationException(
                $"Host-class inspection workspace is not a direct GUID child of its scratch root: {absoluteWorkspacePath}");
        }

        return absoluteWorkspacePath;
    }

    internal static HostClassInspectionWorkspaceCleanupResult Cleanup(
        string scratchRoot,
        string workspacePath,
        IHostClassInspectionWorkspaceFileSystem fileSystem,
        int cleanupAttempts,
        TimeSpan retryDelay)
    {
        workspacePath = ValidateOwnedWorkspacePath(scratchRoot, workspacePath);
        for (var attempt = 1; attempt <= cleanupAttempts; attempt++)
        {
            try
            {
                fileSystem.DeleteDirectory(workspacePath);
                return new HostClassInspectionWorkspaceCleanupResult(true, null);
            }
            catch (DirectoryNotFoundException)
            {
                return new HostClassInspectionWorkspaceCleanupResult(true, null);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt < cleanupAttempts)
                {
                    fileSystem.Delay(retryDelay);
                }
            }
        }

        return new HostClassInspectionWorkspaceCleanupResult(
            false,
            Path.GetFullPath(workspacePath));
    }
}

internal sealed class HostClassInspectionWorkspace : IDisposable
{
    private readonly string scratchRoot;
    private readonly IHostClassInspectionWorkspaceFileSystem fileSystem;
    private readonly int cleanupAttempts;
    private readonly TimeSpan retryDelay;
    private HostClassInspectionWorkspaceCleanupResult? cleanupResult;

    internal HostClassInspectionWorkspace(
        string scratchRoot,
        string workspacePath,
        string workbookPath,
        IHostClassInspectionWorkspaceFileSystem fileSystem,
        int cleanupAttempts,
        TimeSpan retryDelay)
    {
        this.scratchRoot = Path.GetFullPath(scratchRoot);
        WorkspacePath = HostClassInspectionWorkspaceFactory.ValidateOwnedWorkspacePath(
            this.scratchRoot,
            workspacePath);
        WorkbookPath = Path.GetFullPath(workbookPath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(WorkbookPath), WorkspacePath, comparison))
        {
            throw new InvalidOperationException(
                $"Host-class inspection workbook must be directly contained by its owned workspace: {WorkbookPath}");
        }

        this.fileSystem = fileSystem;
        this.cleanupAttempts = cleanupAttempts;
        this.retryDelay = retryDelay;
    }

    public string WorkspacePath { get; }

    public string WorkbookPath { get; }

    public HostClassInspectionWorkspaceCleanupResult Cleanup()
        => cleanupResult ??= HostClassInspectionWorkspaceFactory.Cleanup(
            scratchRoot,
            WorkspacePath,
            fileSystem,
            cleanupAttempts,
            retryDelay);

    public void Dispose() => _ = Cleanup();
}
