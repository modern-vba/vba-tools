using System.Text;
using VbaDev.App.Build;
using Xunit;

namespace VbaDev.Tests;

public sealed class WorkbookOutputTransactionTests
{
    [Fact]
    public void CreateStagesTemplateBesideTargetAndCommitAtomicallyReplacesOnlyThatTarget()
    {
        using var temp = TempDirectory.Create();
        var outputDirectory = temp.CreateDirectory("output");
        var templatePath = Path.Combine(temp.Path, "template.xlsm");
        var targetPath = Path.Combine(outputDirectory, "Book1.xlsm");
        var siblingPath = Path.Combine(outputDirectory, "Book2.xlsm");
        File.WriteAllText(templatePath, "new workbook", Encoding.UTF8);
        File.WriteAllText(targetPath, "previous workbook", Encoding.UTF8);
        File.WriteAllText(siblingPath, "other workbook", Encoding.UTF8);

        using var transaction = WorkbookOutputTransaction.Create(templatePath, targetPath);

        Assert.Equal(outputDirectory, Path.GetDirectoryName(transaction.StagingWorkbookPath));
        Assert.NotEqual(targetPath, transaction.StagingWorkbookPath);
        Assert.Equal("new workbook", File.ReadAllText(transaction.StagingWorkbookPath, Encoding.UTF8));
        Assert.Equal("previous workbook", File.ReadAllText(targetPath, Encoding.UTF8));

        transaction.Commit();

        Assert.Equal("new workbook", File.ReadAllText(targetPath, Encoding.UTF8));
        Assert.Equal("other workbook", File.ReadAllText(siblingPath, Encoding.UTF8));
        Assert.False(File.Exists(transaction.StagingWorkbookPath));
    }

    [Fact]
    public void DisposeBeforeCommitRemovesIncompleteStagingAndPreservesPreviousTarget()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "template.xlsm");
        var targetPath = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(templatePath, "new workbook", Encoding.UTF8);
        File.WriteAllText(targetPath, "previous workbook", Encoding.UTF8);
        var transaction = WorkbookOutputTransaction.Create(templatePath, targetPath);
        var stagingPath = transaction.StagingWorkbookPath;

        File.WriteAllText(stagingPath, "incomplete workbook", Encoding.UTF8);
        transaction.Dispose();

        Assert.False(File.Exists(stagingPath));
        Assert.Equal("previous workbook", File.ReadAllText(targetPath, Encoding.UTF8));
    }

    [Fact]
    public void PersistentCleanupFailureIsBoundedAndReportsRetainedAbsoluteStagingPath()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "template.xlsm");
        var targetPath = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(templatePath, "new workbook", Encoding.UTF8);
        var cleaner = new RetainingWorkbookStageCleaner();
        var transaction = WorkbookOutputTransaction.Create(
            templatePath,
            targetPath,
            cleaner,
            new WorkbookOutputCleanupPolicy(MaximumAttempts: 3, RetryDelay: TimeSpan.Zero));
        var stagingPath = transaction.StagingWorkbookPath;

        var error = Assert.Throws<BuildCommandException>(transaction.Dispose);

        Assert.Equal(3, cleaner.DeleteAttempts);
        Assert.True(Path.IsPathFullyQualified(stagingPath));
        Assert.Contains(stagingPath, error.Message, StringComparison.Ordinal);
        Assert.Contains("retained", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(stagingPath));
        File.Delete(stagingPath);
    }

    [Fact]
    public void DisposeAfterCommitDoesNotRunCleanupAgainstTheCompletedTarget()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "template.xlsm");
        var targetPath = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(templatePath, "completed workbook", Encoding.UTF8);
        File.WriteAllText(targetPath, "previous workbook", Encoding.UTF8);
        var cleaner = new TrackingWorkbookStageCleaner();
        var transaction = WorkbookOutputTransaction.Create(
            templatePath,
            targetPath,
            cleaner,
            new WorkbookOutputCleanupPolicy(MaximumAttempts: 1, RetryDelay: TimeSpan.Zero));

        transaction.Commit();
        transaction.Dispose();

        Assert.Equal(0, cleaner.DeleteAttempts);
        Assert.Equal("completed workbook", File.ReadAllText(targetPath, Encoding.UTF8));
    }

    [Fact]
    public void DisposeRetriesUntilStagingDeletionIsVerified()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "template.xlsm");
        var targetPath = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(templatePath, "incomplete workbook", Encoding.UTF8);
        var cleaner = new DeleteOnSecondAttemptWorkbookStageCleaner();
        var transaction = WorkbookOutputTransaction.Create(
            templatePath,
            targetPath,
            cleaner,
            new WorkbookOutputCleanupPolicy(MaximumAttempts: 3, RetryDelay: TimeSpan.Zero));
        var stagingPath = transaction.StagingWorkbookPath;

        transaction.Dispose();

        Assert.Equal(2, cleaner.DeleteAttempts);
        Assert.False(File.Exists(stagingPath));
    }

    [Fact]
    public void FailedTemplateCopyRemovesItsPartialCommandOwnedStagingFile()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "template.xlsm");
        var targetPath = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(templatePath, "template", Encoding.UTF8);

        var error = Assert.Throws<IOException>(() => WorkbookOutputTransaction.Create(
            templatePath,
            targetPath,
            new TrackingWorkbookStageCleaner(),
            new WorkbookOutputCleanupPolicy(MaximumAttempts: 1, RetryDelay: TimeSpan.Zero),
            (_, staging) =>
            {
                staging.Write(Encoding.UTF8.GetBytes("partial"));
                throw new IOException("copy failed");
            }));

        Assert.Equal("copy failed", error.Message);
        Assert.Empty(Directory.EnumerateFiles(temp.Path, ".Book1.*.tmp.xlsm"));
        Assert.False(File.Exists(targetPath));
    }

    private sealed class RetainingWorkbookStageCleaner : IWorkbookOutputStageCleaner
    {
        public int DeleteAttempts { get; private set; }

        public bool Exists(string path) => File.Exists(path);

        public void Delete(string path) => DeleteAttempts++;
    }

    private sealed class TrackingWorkbookStageCleaner : IWorkbookOutputStageCleaner
    {
        public int DeleteAttempts { get; private set; }

        public bool Exists(string path) => File.Exists(path);

        public void Delete(string path)
        {
            DeleteAttempts++;
            File.Delete(path);
        }
    }

    private sealed class DeleteOnSecondAttemptWorkbookStageCleaner : IWorkbookOutputStageCleaner
    {
        public int DeleteAttempts { get; private set; }

        public bool Exists(string path) => File.Exists(path);

        public void Delete(string path)
        {
            DeleteAttempts++;
            if (DeleteAttempts == 2)
            {
                File.Delete(path);
            }
        }
    }
}
