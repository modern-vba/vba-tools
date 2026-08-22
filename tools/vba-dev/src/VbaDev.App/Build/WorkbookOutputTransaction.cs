namespace VbaDev.App.Build;

/// <summary>
/// Owns a sibling staging workbook until it can replace one selected output atomically.
/// </summary>
public sealed class WorkbookOutputTransaction : IWorkbookOutputTransaction
{
    private readonly string targetWorkbookPath;
    private readonly IWorkbookOutputStageCleaner stageCleaner;
    private readonly WorkbookOutputCleanupPolicy cleanupPolicy;
    private bool committed;
    private bool disposed;

    private WorkbookOutputTransaction(
        string targetWorkbookPath,
        string stagingWorkbookPath,
        IWorkbookOutputStageCleaner stageCleaner,
        WorkbookOutputCleanupPolicy cleanupPolicy)
    {
        this.targetWorkbookPath = targetWorkbookPath;
        this.stageCleaner = stageCleaner;
        this.cleanupPolicy = cleanupPolicy;
        StagingWorkbookPath = stagingWorkbookPath;
    }

    /// <summary>
    /// Gets the absolute path of the command-owned staging workbook.
    /// </summary>
    public string StagingWorkbookPath { get; }

    /// <summary>
    /// Copies a template into a command-owned staging workbook beside the selected output.
    /// </summary>
    /// <param name="templateWorkbookPath">The source template workbook.</param>
    /// <param name="targetWorkbookPath">The selected completed output to replace on commit.</param>
    /// <returns>A transaction that owns the staging workbook.</returns>
    public static WorkbookOutputTransaction Create(string templateWorkbookPath, string targetWorkbookPath)
        => Create(
            templateWorkbookPath,
            targetWorkbookPath,
            FileSystemWorkbookOutputStageCleaner.Instance,
            WorkbookOutputCleanupPolicy.Default);

    /// <summary>
    /// Copies a template into a sibling staging workbook with an explicit cleanup boundary.
    /// </summary>
    /// <param name="templateWorkbookPath">The source template workbook.</param>
    /// <param name="targetWorkbookPath">The selected completed output to replace on commit.</param>
    /// <param name="stageCleaner">The file-system boundary used to delete and verify command-owned staging.</param>
    /// <param name="cleanupPolicy">The bounded retry policy for staging cleanup.</param>
    /// <returns>A transaction that owns the staging workbook.</returns>
    public static WorkbookOutputTransaction Create(
        string templateWorkbookPath,
        string targetWorkbookPath,
        IWorkbookOutputStageCleaner stageCleaner,
        WorkbookOutputCleanupPolicy cleanupPolicy)
        => Create(
            templateWorkbookPath,
            targetWorkbookPath,
            stageCleaner,
            cleanupPolicy,
            static (source, destination) => source.CopyTo(destination));

    internal static WorkbookOutputTransaction Create(
        string templateWorkbookPath,
        string targetWorkbookPath,
        IWorkbookOutputStageCleaner stageCleaner,
        WorkbookOutputCleanupPolicy cleanupPolicy,
        Action<Stream, Stream> copyStage)
    {
        ArgumentNullException.ThrowIfNull(stageCleaner);
        ArgumentNullException.ThrowIfNull(cleanupPolicy);
        ArgumentNullException.ThrowIfNull(copyStage);
        var absoluteTemplatePath = Path.GetFullPath(templateWorkbookPath);
        var absoluteTargetPath = Path.GetFullPath(targetWorkbookPath);
        var targetDirectory = Path.GetDirectoryName(absoluteTargetPath)
            ?? throw new BuildCommandException($"Target workbook path is invalid: {absoluteTargetPath}");
        Directory.CreateDirectory(targetDirectory);
        var stagingWorkbookPath = Path.Combine(
            targetDirectory,
            $".{Path.GetFileNameWithoutExtension(absoluteTargetPath)}.{Guid.NewGuid():N}.tmp{Path.GetExtension(absoluteTargetPath)}");
        WorkbookOutputTransaction? transaction = null;
        try
        {
            using var source = new FileStream(
                absoluteTemplatePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            using var destination = new FileStream(
                stagingWorkbookPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            transaction = new WorkbookOutputTransaction(
                absoluteTargetPath,
                stagingWorkbookPath,
                stageCleaner,
                cleanupPolicy);
            copyStage(source, destination);
            destination.Flush(flushToDisk: true);
            return transaction;
        }
        catch (Exception stagingError)
        {
            if (transaction is null)
            {
                throw;
            }

            try
            {
                transaction.Dispose();
            }
            catch (Exception cleanupError)
            {
                throw new BuildCommandException(
                    $"Workbook staging failed, and incomplete command-owned output cleanup also failed. {cleanupError.Message}",
                    new AggregateException(stagingError, cleanupError));
            }

            throw;
        }
    }

    /// <summary>
    /// Atomically replaces the selected output with the completed staging workbook.
    /// </summary>
    public void Commit()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (committed)
        {
            return;
        }

        File.Move(StagingWorkbookPath, targetWorkbookPath, overwrite: true);

        committed = true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (!committed)
        {
            DeleteStagingWorkbook();
        }
    }

    private void DeleteStagingWorkbook()
    {
        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= cleanupPolicy.MaximumAttempts; attempt++)
        {
            try
            {
                if (!stageCleaner.Exists(StagingWorkbookPath))
                {
                    return;
                }

                stageCleaner.Delete(StagingWorkbookPath);
                if (!stageCleaner.Exists(StagingWorkbookPath))
                {
                    return;
                }
            }
            catch (IOException ex)
            {
                lastFailure = ex;
            }
            catch (UnauthorizedAccessException ex)
            {
                lastFailure = ex;
            }

            if (attempt < cleanupPolicy.MaximumAttempts && cleanupPolicy.RetryDelay > TimeSpan.Zero)
            {
                Thread.Sleep(cleanupPolicy.RetryDelay);
            }
        }

        var message =
            $"Incomplete command-owned workbook cleanup did not complete after {cleanupPolicy.MaximumAttempts} attempts. " +
            $"The staging workbook was retained at the absolute path '{StagingWorkbookPath}'. " +
            "Close any application using that file, then delete it manually.";
        throw lastFailure is null
            ? new BuildCommandException(message)
            : new BuildCommandException(message, lastFailure);
    }
}

/// <summary>
/// Owns one staged workbook and its atomic commit boundary.
/// </summary>
public interface IWorkbookOutputTransaction : IDisposable
{
    string StagingWorkbookPath { get; }

    void Commit();
}

/// <summary>
/// Creates sibling-staged workbook output transactions.
/// </summary>
public interface IWorkbookOutputTransactionFactory
{
    IWorkbookOutputTransaction Create(string templateWorkbookPath, string targetWorkbookPath);
}

/// <summary>
/// Creates file-system-backed workbook output transactions.
/// </summary>
public sealed class WorkbookOutputTransactionFactory : IWorkbookOutputTransactionFactory
{
    public IWorkbookOutputTransaction Create(string templateWorkbookPath, string targetWorkbookPath)
        => WorkbookOutputTransaction.Create(templateWorkbookPath, targetWorkbookPath);
}

/// <summary>
/// Defines the file-system boundary used to delete and verify command-owned workbook staging.
/// </summary>
public interface IWorkbookOutputStageCleaner
{
    /// <summary>
    /// Returns whether the staging workbook still exists.
    /// </summary>
    bool Exists(string path);

    /// <summary>
    /// Attempts to delete the staging workbook.
    /// </summary>
    void Delete(string path);
}

/// <summary>
/// Defines the bounded retry policy for command-owned workbook cleanup.
/// </summary>
public sealed record WorkbookOutputCleanupPolicy
{
    /// <summary>
    /// Creates a cleanup policy.
    /// </summary>
    /// <param name="MaximumAttempts">The positive maximum number of verified delete attempts.</param>
    /// <param name="RetryDelay">The non-negative delay between attempts.</param>
    public WorkbookOutputCleanupPolicy(int MaximumAttempts, TimeSpan RetryDelay)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumAttempts);
        ArgumentOutOfRangeException.ThrowIfLessThan(RetryDelay, TimeSpan.Zero);
        this.MaximumAttempts = MaximumAttempts;
        this.RetryDelay = RetryDelay;
    }

    /// <summary>
    /// Gets the default bounded cleanup policy.
    /// </summary>
    public static WorkbookOutputCleanupPolicy Default { get; } = new(3, TimeSpan.FromMilliseconds(50));

    /// <summary>
    /// Gets the maximum number of verified delete attempts.
    /// </summary>
    public int MaximumAttempts { get; }

    /// <summary>
    /// Gets the delay between cleanup attempts.
    /// </summary>
    public TimeSpan RetryDelay { get; }
}

internal sealed class FileSystemWorkbookOutputStageCleaner : IWorkbookOutputStageCleaner
{
    private FileSystemWorkbookOutputStageCleaner()
    {
    }

    public static FileSystemWorkbookOutputStageCleaner Instance { get; } = new();

    public bool Exists(string path) => File.Exists(path);

    public void Delete(string path) => File.Delete(path);
}
