using System.Text;
using VbaDev.App.HostClasses;
using Xunit;

namespace VbaDev.Tests;

public sealed class HostClassInspectionWorkspaceTests
{
    [Fact]
    public void CreateCopiesTheSelectedTemplateOnceIntoAUniqueOwnedGuidLeaf()
    {
        using var temp = TempDirectory.Create();
        var sourceTemplate = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(sourceTemplate, "fixed template bytes", new UTF8Encoding(false));
        var scratchRoot = temp.CreateDirectory("scratch");
        var fileSystem = new RecordingHostClassWorkspaceFileSystem();
        var factory = new HostClassInspectionWorkspaceFactory(
            scratchRoot,
            fileSystem,
            cleanupAttempts: 3,
            retryDelay: TimeSpan.Zero);

        using var workspace = factory.Create(sourceTemplate);

        var copy = Assert.Single(fileSystem.CopyOperations);
        Assert.Equal(Path.GetFullPath(sourceTemplate), copy.Source);
        Assert.Equal(workspace.WorkbookPath, copy.Destination);
        Assert.Equal("Book1.xlsm", Path.GetFileName(workspace.WorkbookPath));
        Assert.Equal(workspace.WorkspacePath, Path.GetDirectoryName(workspace.WorkbookPath));
        Assert.Equal(Path.GetFullPath(scratchRoot), Path.GetDirectoryName(workspace.WorkspacePath));
        Assert.True(Guid.TryParseExact(Path.GetFileName(workspace.WorkspacePath), "N", out _));
        Assert.NotEqual(Path.GetFullPath(sourceTemplate), workspace.WorkbookPath);
        Assert.Equal("fixed template bytes", File.ReadAllText(workspace.WorkbookPath, Encoding.UTF8));
    }

    [Fact]
    public void CleanupRejectsANonGuidLeafWithoutDeletingIt()
    {
        using var temp = TempDirectory.Create();
        var scratchRoot = temp.CreateDirectory("scratch");
        var nonOwnedPath = Directory.CreateDirectory(
            Path.Combine(scratchRoot, "not-an-owned-guid")).FullName;
        var fileSystem = new RecordingHostClassWorkspaceFileSystem();

        var error = Assert.Throws<InvalidOperationException>(() =>
            HostClassInspectionWorkspaceFactory.Cleanup(
                scratchRoot,
                nonOwnedPath,
                fileSystem,
                cleanupAttempts: 3,
                retryDelay: TimeSpan.Zero));

        Assert.Contains("direct GUID child", error.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(nonOwnedPath));
    }

    [Fact]
    public void PreparationFailureReportsTheRetainedWorkspaceAfterBoundedCleanupRetries()
    {
        using var temp = TempDirectory.Create();
        var sourceTemplate = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(sourceTemplate, "fixed template bytes", new UTF8Encoding(false));
        var scratchRoot = temp.CreateDirectory("scratch");
        var fileSystem = new RecordingHostClassWorkspaceFileSystem
        {
            CopyException = new IOException("The private copy could not be created."),
            DeleteException = new IOException("The workspace is still locked.")
        };
        var factory = new HostClassInspectionWorkspaceFactory(
            scratchRoot,
            fileSystem,
            cleanupAttempts: 3,
            retryDelay: TimeSpan.Zero);

        var error = Assert.Throws<HostClassInspectionPreparationException>(() =>
            factory.Create(sourceTemplate));

        Assert.Contains(error.WorkspacePath, error.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(error.WorkspacePath));
        Assert.Equal(3, fileSystem.DeleteAttempts);
    }

    [Fact]
    public void CopyFailureRemovesTheOwnedWorkspaceBeforeReturningPreparationFailure()
    {
        using var temp = TempDirectory.Create();
        var sourceTemplate = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(sourceTemplate, "fixed template bytes", new UTF8Encoding(false));
        var scratchRoot = temp.CreateDirectory("scratch");
        var copyError = new IOException("The private copy could not be created.");
        var fileSystem = new RecordingHostClassWorkspaceFileSystem
        {
            CopyException = copyError
        };
        var factory = new HostClassInspectionWorkspaceFactory(
            scratchRoot,
            fileSystem,
            cleanupAttempts: 3,
            retryDelay: TimeSpan.Zero);

        var error = Assert.Throws<HostClassInspectionPreparationException>(() =>
            factory.Create(sourceTemplate));

        Assert.Same(copyError, error.InnerException);
        Assert.False(Directory.Exists(error.WorkspacePath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(scratchRoot));
        Assert.Equal(1, fileSystem.DeleteAttempts);
    }

    private sealed class RecordingHostClassWorkspaceFileSystem
        : IHostClassInspectionWorkspaceFileSystem
    {
        public List<(string Source, string Destination)> CopyOperations { get; } = [];

        public Exception? CopyException { get; init; }

        public Exception? DeleteException { get; init; }

        public int DeleteAttempts { get; private set; }

        public bool FileExists(string path) => File.Exists(path);

        public void CreateDirectory(string path) => Directory.CreateDirectory(path);

        public void CopyFile(string sourcePath, string destinationPath)
        {
            CopyOperations.Add((sourcePath, destinationPath));
            if (CopyException is not null)
            {
                throw CopyException;
            }

            File.Copy(sourcePath, destinationPath, overwrite: false);
        }

        public void DeleteDirectory(string path)
        {
            DeleteAttempts++;
            if (DeleteException is not null)
            {
                throw DeleteException;
            }

            Directory.Delete(path, recursive: true);
        }

        public void Delay(TimeSpan delay)
        {
        }
    }
}
