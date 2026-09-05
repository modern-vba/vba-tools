using System.Runtime.InteropServices;
using System.Text;
using VbaDev.App.FileSystem;
using VbaDev.App.Projects;
using Xunit;

namespace VbaDev.Tests;

public sealed class NewProjectArtifactTrackerTests
{
    [Fact]
    public void PartialCreationFailureKeepsTargetChangeAndUnprovenRollbackDistinct()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "Project");
        var workbookPath = Path.Combine(projectRoot, "Book1.xlsm");
        using var tracker = new NewProjectArtifactTracker();
        tracker.EnsureDirectory(projectRoot);
        var failure = new ExactFileSystemObjectOwnership.FileCreationCleanupException(
            workbookPath,
            retainedReceipt: null,
            targetChanged: true,
            originalFailure: new IOException("partial copy failed"),
            cleanupFailure: new ExactFileSystemObjectOwnership.RollbackException(workbookPath));

        // This is an exception-translation contract, not a native rollback-failure proof.
        var reported = Assert.Throws<ExactFileSystemObjectOwnership.FileCreationCleanupException>(() =>
            tracker.CreateFile(
                workbookPath,
                Encoding.UTF8.GetBytes("owned workbook"),
                onBytesWritten: (_, _) => throw failure));
        var result = tracker.Rollback();
        var repeated = tracker.Rollback();

        Assert.Same(failure, reported);
        Assert.Equal([workbookPath], result.TargetChangedPaths);
        Assert.Equal([workbookPath], result.CleanupIncompletePaths);
        Assert.Contains(workbookPath, result.RetainedOwnedPaths);
        Assert.Equal(result.TargetChangedPaths, repeated.TargetChangedPaths);
        Assert.Equal(result.CleanupIncompletePaths, repeated.CleanupIncompletePaths);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void PartialCreationCleanupFailureStaysReportedWithoutARollbackRetry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "Project");
        var workbookPath = Path.Combine(projectRoot, "Book1.xlsm");
        using var tracker = new NewProjectArtifactTracker();
        tracker.EnsureDirectory(projectRoot);
        var bytes = Enumerable.Repeat((byte)7, 80 * 1024).ToArray();

        ExactFileSystemObjectOwnership.FileCreationCleanupException error;
        try
        {
            error = Assert.Throws<ExactFileSystemObjectOwnership.FileCreationCleanupException>(() =>
                tracker.CreateFile(workbookPath, bytes, onBytesWritten: (path, _) =>
                {
                    File.SetAttributes(path, FileAttributes.ReadOnly);
                    throw new IOException("partial copy failed");
                }));
        }
        finally
        {
            if (File.Exists(workbookPath))
            {
                File.SetAttributes(workbookPath, FileAttributes.Normal);
            }
        }

        Assert.Contains("partial copy failed", error.Message, StringComparison.Ordinal);
        Assert.Equal(workbookPath, error.Route);
        var partialBytes = File.ReadAllBytes(workbookPath);
        var result = tracker.Rollback();
        var repeated = tracker.Rollback();

        Assert.Equal(partialBytes, File.ReadAllBytes(workbookPath));
        Assert.Empty(result.TargetChangedPaths);
        Assert.Equal([workbookPath], result.CleanupIncompletePaths);
        Assert.Contains(workbookPath, result.RetainedOwnedPaths);
        Assert.Equal(result.CleanupIncompletePaths, repeated.CleanupIncompletePaths);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void RollbackPreservesAnOwnedFileThatAcquiredAnotherHardLink()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "Project");
        var workbookPath = Path.Combine(projectRoot, "Book1.xlsm");
        var aliasPath = Path.Combine(temp.Path, "linked.xlsm");
        var bytes = Encoding.UTF8.GetBytes("owned workbook");
        using var tracker = new NewProjectArtifactTracker();
        tracker.EnsureDirectory(projectRoot);
        tracker.CreateFile(workbookPath, bytes);
        Assert.True(CreateHardLink(aliasPath, workbookPath, IntPtr.Zero));

        var result = tracker.Rollback();

        Assert.True(File.Exists(workbookPath));
        Assert.Equal(bytes, File.ReadAllBytes(workbookPath));
        Assert.Equal(bytes, File.ReadAllBytes(aliasPath));
        Assert.Equal([workbookPath], result.TargetChangedPaths);
        Assert.Empty(result.CleanupIncompletePaths);
        Assert.Equal([projectRoot], result.RetainedOwnedPaths);
    }

    [Fact]
    public void CreateOnlyReceiptRejectsAndPreservesASameByteReplacement()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "Project");
        var workbookPath = Path.Combine(projectRoot, "Book1.xlsm");
        var bytes = Encoding.UTF8.GetBytes("same workbook bytes");
        using var tracker = new NewProjectArtifactTracker();
        tracker.EnsureDirectory(projectRoot);
        var receipt = tracker.Ownership.CreateOnlyFile(projectRoot, "Book1.xlsm", bytes);
        File.Delete(workbookPath);
        File.WriteAllBytes(workbookPath, bytes);

        Assert.Throws<NewProjectArtifactEvidenceMismatchException>(() =>
            tracker.RecordCreatedFile(receipt));
        var rollback = tracker.Rollback();

        Assert.Equal(bytes, File.ReadAllBytes(workbookPath));
        Assert.Equal([workbookPath], rollback.TargetChangedPaths);
        Assert.Contains(projectRoot, rollback.RetainedOwnedPaths);
    }

    [Fact]
    public void CreateOnlyReceiptRegistersAndRemovesTheExactWorkbook()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "Project");
        var workbookPath = Path.Combine(projectRoot, "Book1.xlsm");
        using var tracker = new NewProjectArtifactTracker();
        tracker.EnsureDirectory(projectRoot);
        var receipt = tracker.Ownership.CreateOnlyFile(
            projectRoot,
            "Book1.xlsm",
            Encoding.UTF8.GetBytes("owned workbook"));

        tracker.RecordCreatedFile(receipt);
        var rollback = tracker.Rollback();

        Assert.True(rollback.IsComplete);
        Assert.False(Directory.Exists(projectRoot));
    }

    [Fact]
    public void CompleteInventoryAcceptsOnlyTrackedArtifactsAndTheExplicitLeaseMarker()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "Project");
        var sourceSet = Path.Combine(projectRoot, "src", "Book1");
        var workbookPath = Path.Combine(sourceSet, "Book1.xlsm");
        var leaseMarkerPath = Path.Combine(projectRoot, "vba-project.json.vba-dev.lock");
        using var tracker = new NewProjectArtifactTracker();
        tracker.EnsureDirectory(sourceSet);
        tracker.CreateFile(workbookPath, Encoding.UTF8.GetBytes("owned workbook"));
        File.WriteAllText(leaseMarkerPath, "owned lease", new UTF8Encoding(false));

        var result = tracker.ProveCompleteTargetInventory(projectRoot, leaseMarkerPath);

        Assert.True(result.IsComplete);
        Assert.Empty(result.TargetChangedPaths);
    }

    [Fact]
    public void CompleteInventoryRejectsAnOwnedFileWhoseBytesChanged()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "Project");
        var workbookPath = Path.Combine(projectRoot, "Book1.xlsm");
        var leaseMarkerPath = Path.Combine(projectRoot, "vba-project.json.vba-dev.lock");
        using var tracker = new NewProjectArtifactTracker();
        tracker.EnsureDirectory(projectRoot);
        tracker.CreateFile(workbookPath, Encoding.UTF8.GetBytes("owned workbook"));
        File.WriteAllText(leaseMarkerPath, "owned lease", new UTF8Encoding(false));
        File.WriteAllBytes(workbookPath, Encoding.UTF8.GetBytes("foreign replacement"));

        var result = tracker.ProveCompleteTargetInventory(projectRoot, leaseMarkerPath);

        Assert.Equal([workbookPath], result.TargetChangedPaths);
    }

    [Fact]
    public void RollbackRemovesUnchangedOwnedArtifactsAndReportsCompleteCleanup()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "Project");
        var sourceSet = Path.Combine(projectRoot, "src", "Book1");
        var workbookPath = Path.Combine(sourceSet, "Book1.xlsm");
        using var tracker = new NewProjectArtifactTracker();
        tracker.EnsureDirectory(sourceSet);
        tracker.CreateFile(workbookPath, Encoding.UTF8.GetBytes("owned workbook"));

        var result = tracker.Rollback();

        Assert.False(Directory.Exists(projectRoot));
        Assert.Empty(result.TargetChangedPaths);
        Assert.Empty(result.CleanupIncompletePaths);
    }

    [Fact]
    public void RollbackPreservesChangedArtifactsAndReportsRetainedPaths()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "Project");
        var workbookPath = Path.Combine(projectRoot, "Book1.xlsm");
        using var tracker = new NewProjectArtifactTracker();
        tracker.EnsureDirectory(projectRoot);
        tracker.CreateFile(workbookPath, Encoding.UTF8.GetBytes("owned workbook"));
        var foreignBytes = Encoding.UTF8.GetBytes("foreign replacement");
        File.WriteAllBytes(workbookPath, foreignBytes);

        var result = tracker.Rollback();

        Assert.Equal(foreignBytes, File.ReadAllBytes(workbookPath));
        Assert.Equal([workbookPath], result.TargetChangedPaths);
        Assert.Equal([projectRoot], result.RetainedOwnedPaths);
    }

    [Fact]
    public void CompleteInventoryReturnsTargetChangedWhenTheTrackedRootDisappears()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "Project");
        var leaseMarkerPath = Path.Combine(projectRoot, "vba-project.json.vba-dev.lock");
        using var tracker = new NewProjectArtifactTracker();
        tracker.EnsureDirectory(projectRoot);
        File.WriteAllText(leaseMarkerPath, "owned lease", new UTF8Encoding(false));
        Directory.Delete(projectRoot, recursive: true);

        var result = tracker.ProveCompleteTargetInventory(projectRoot, leaseMarkerPath);

        Assert.False(result.IsComplete);
        Assert.True(result.IsConclusive);
        Assert.Contains(projectRoot, result.TargetChangedPaths);
    }

    [Fact]
    public void RepeatedRollbackRemovesTheRootAfterTheAllowedLeaseMarkerIsReleased()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "Project");
        var workbookPath = Path.Combine(projectRoot, "Book1.xlsm");
        var leaseMarkerPath = Path.Combine(projectRoot, "vba-project.json.vba-dev.lock");
        using var tracker = new NewProjectArtifactTracker();
        tracker.EnsureDirectory(projectRoot);
        tracker.CreateFile(workbookPath, Encoding.UTF8.GetBytes("owned workbook"));
        File.WriteAllText(leaseMarkerPath, "owned lease", new UTF8Encoding(false));
        Assert.True(
            tracker.ProveCompleteTargetInventory(projectRoot, leaseMarkerPath).IsComplete);

        var underLease = tracker.RollbackUnderLease(projectRoot);
        File.Delete(leaseMarkerPath);
        var afterRelease = tracker.RollbackAfterLeaseRelease(projectRoot);
        var repeated = tracker.RollbackAfterLeaseRelease(projectRoot);

        Assert.Equal([projectRoot], underLease.RetainedOwnedPaths);
        Assert.True(afterRelease.IsComplete);
        Assert.True(repeated.IsComplete);
        Assert.False(Directory.Exists(projectRoot));
    }

    [Fact]
    public void RollbackReportsAndRetriesAnUnchangedOwnedFileWhoseDeleteIsBlocked()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "Project");
        var workbookPath = Path.Combine(projectRoot, "Book1.xlsm");
        using var tracker = new NewProjectArtifactTracker();
        tracker.EnsureDirectory(projectRoot);
        tracker.CreateFile(workbookPath, Encoding.UTF8.GetBytes("owned workbook"));
        NewProjectRollbackResult blocked;
        using (new FileStream(
                   workbookPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            blocked = tracker.Rollback();
        }

        var retried = tracker.Rollback();

        Assert.Equal([workbookPath], blocked.CleanupIncompletePaths);
        Assert.Contains(workbookPath, blocked.RetainedOwnedPaths);
        Assert.False(blocked.IsComplete);
        Assert.True(retried.IsComplete);
        Assert.False(Directory.Exists(projectRoot));
    }

    [Fact]
    public void PostLeaseRollbackDoesNotRetryAFileThatWasRetainedUnderLease()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "Project");
        var workbookPath = Path.Combine(projectRoot, "Book1.xlsm");
        using var tracker = new NewProjectArtifactTracker();
        tracker.EnsureDirectory(projectRoot);
        tracker.CreateFile(workbookPath, Encoding.UTF8.GetBytes("owned workbook"));
        NewProjectRollbackResult underLease;
        using (new FileStream(
                   workbookPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            underLease = tracker.RollbackUnderLease(projectRoot);
        }

        var afterRelease = tracker.RollbackAfterLeaseRelease(projectRoot);

        Assert.Equal([workbookPath], underLease.CleanupIncompletePaths);
        Assert.True(File.Exists(workbookPath));
        Assert.Contains(workbookPath, afterRelease.CleanupIncompletePaths);
        Assert.Contains(projectRoot, afterRelease.RetainedOwnedPaths);
    }

    [Fact]
    public void PostLeaseRollbackDoesNotRetryAChildDirectoryRetainedUnderLease()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "Project");
        var childDirectory = Path.Combine(projectRoot, "src");
        var blockChildDelete = true;
        var childProofCount = 0;
        var observer = new ProofBoundaryObserver(path =>
        {
            if (!path.Equals(childDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            childProofCount++;
            if (blockChildDelete)
            {
                throw new IOException("child directory delete is blocked");
            }
        });
        using var tracker = new NewProjectArtifactTracker(observer);
        tracker.EnsureDirectory(childDirectory);

        var underLease = tracker.RollbackUnderLease(projectRoot);
        blockChildDelete = false;
        var afterRelease = tracker.RollbackAfterLeaseRelease(projectRoot);

        Assert.Contains(childDirectory, underLease.CleanupIncompletePaths);
        Assert.True(Directory.Exists(childDirectory));
        Assert.Equal(1, childProofCount);
        Assert.Contains(childDirectory, afterRelease.CleanupIncompletePaths);
        Assert.Contains(projectRoot, afterRelease.RetainedOwnedPaths);
    }

    [Fact]
    public void RollbackBlocksAnInPlaceWriteAfterProofAndDeletesTheExactOwnedFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "Project");
        var workbookPath = Path.Combine(projectRoot, "Book1.xlsm");
        var observerWriteBlocked = false;
        var observer = new ProofBoundaryObserver(path =>
        {
            if (!path.Equals(workbookPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                File.WriteAllText(path, "foreign bytes", new UTF8Encoding(false));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                observerWriteBlocked = true;
            }
        });
        using var tracker = new NewProjectArtifactTracker(observer);
        tracker.EnsureDirectory(projectRoot);
        tracker.CreateFile(workbookPath, Encoding.UTF8.GetBytes("owned workbook"));

        var result = tracker.Rollback();

        Assert.True(observerWriteBlocked);
        Assert.True(result.IsComplete);
        Assert.False(File.Exists(workbookPath));
    }

    [Fact]
    public void RollbackBlocksFileRenameAndReplacementAfterProof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "Project");
        var workbookPath = Path.Combine(projectRoot, "Book1.xlsm");
        var movedPath = Path.Combine(temp.Path, "moved.xlsm");
        var renameBlocked = false;
        var replacementBlocked = false;
        var observer = new ProofBoundaryObserver(path =>
        {
            if (!path.Equals(workbookPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                File.Move(path, movedPath);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                renameBlocked = true;
            }

            try
            {
                File.Delete(path);
                File.WriteAllText(path, "foreign replacement", new UTF8Encoding(false));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                replacementBlocked = true;
            }
        });
        using var tracker = new NewProjectArtifactTracker(observer);
        tracker.EnsureDirectory(projectRoot);
        tracker.CreateFile(workbookPath, Encoding.UTF8.GetBytes("owned workbook"));

        var result = tracker.Rollback();

        Assert.True(renameBlocked);
        Assert.True(replacementBlocked);
        Assert.True(result.IsComplete);
        Assert.False(File.Exists(workbookPath));
        Assert.False(File.Exists(movedPath));
    }

    [Fact]
    public void RollbackBlocksDirectoryRenameAndReplacementAfterProof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "Project");
        var movedRoot = Path.Combine(temp.Path, "MovedProject");
        var renameBlocked = false;
        var replacementBlocked = false;
        var observer = new ProofBoundaryObserver(path =>
        {
            if (!path.Equals(projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                Directory.Move(path, movedRoot);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                renameBlocked = true;
            }

            try
            {
                Directory.Delete(path, recursive: false);
                Directory.CreateDirectory(path);
                File.WriteAllText(
                    Path.Combine(path, "foreign.txt"),
                    "foreign replacement",
                    new UTF8Encoding(false));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                replacementBlocked = true;
            }
        });
        using var tracker = new NewProjectArtifactTracker(observer);
        tracker.EnsureDirectory(projectRoot);

        var result = tracker.Rollback();

        Assert.True(renameBlocked);
        Assert.True(replacementBlocked);
        Assert.True(result.IsComplete);
        Assert.False(Directory.Exists(projectRoot));
        Assert.False(Directory.Exists(movedRoot));
    }

    [Fact]
    public void RollbackRejectsAndPreservesAReparsePointReplacement()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "Project");
        var workbookPath = Path.Combine(projectRoot, "Book1.xlsm");
        var sentinelPath = Path.Combine(temp.Path, "sentinel.xlsm");
        var sentinelBytes = Encoding.UTF8.GetBytes("foreign sentinel");
        using var tracker = new NewProjectArtifactTracker();
        tracker.EnsureDirectory(projectRoot);
        tracker.CreateFile(workbookPath, Encoding.UTF8.GetBytes("owned workbook"));
        File.WriteAllBytes(sentinelPath, sentinelBytes);
        File.Delete(workbookPath);
        File.CreateSymbolicLink(workbookPath, sentinelPath);

        var result = tracker.Rollback();

        Assert.Equal(sentinelBytes, File.ReadAllBytes(sentinelPath));
        Assert.Equal(sentinelBytes, File.ReadAllBytes(workbookPath));
        Assert.Equal([workbookPath], result.TargetChangedPaths);
        Assert.Contains(projectRoot, result.RetainedOwnedPaths);
    }

    [Fact]
    public void ExplicitLeaseAllowanceProtectsEarlyRollbackBeforeInventoryProof()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "Project");
        var leaseMarkerPath = Path.Combine(projectRoot, "vba-project.json.vba-dev.lock");
        using var tracker = new NewProjectArtifactTracker();
        tracker.EnsureDirectory(projectRoot);
        File.WriteAllText(leaseMarkerPath, "owned lease", new UTF8Encoding(false));
        tracker.AllowLeaseMarker(projectRoot, leaseMarkerPath);

        var underLease = tracker.Rollback();
        File.Delete(leaseMarkerPath);
        var afterRelease = tracker.Rollback();

        Assert.Empty(underLease.TargetChangedPaths);
        Assert.Equal([projectRoot], underLease.RetainedOwnedPaths);
        Assert.True(afterRelease.IsComplete);
    }

    [Fact]
    public void EnsureDirectoryDoesNotAdoptAForeignDirectoryThatWinsTheCreateRace()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "RacedProject");
        using var tracker = new NewProjectArtifactTracker(path =>
        {
            Directory.CreateDirectory(path);
            throw new IOException("exclusive create lost the race");
        });

        Assert.Throws<IOException>(() => tracker.EnsureDirectory(projectRoot));
        var rollback = tracker.Rollback();

        Assert.True(Directory.Exists(projectRoot));
        Assert.True(rollback.IsComplete);
        Assert.Empty(rollback.TargetChangedPaths);
    }

    [Fact]
    public void EnsureDirectoryBlocksRenameAndReplacementDuringOwnershipCapture()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "Project");
        var movedRoot = Path.Combine(temp.Path, "MovedProject");
        var observer = new DirectoryCreationBoundaryObserver(movedRoot);
        using var tracker = new NewProjectArtifactTracker(observer);

        tracker.EnsureDirectory(projectRoot);
        var rollback = tracker.Rollback();

        Assert.True(observer.RenameWasBlocked);
        Assert.False(observer.ReplacementWasInstalled);
        Assert.True(rollback.IsComplete);
        Assert.False(Directory.Exists(projectRoot));
        Assert.False(Directory.Exists(movedRoot));
    }

    private sealed class DirectoryCreationBoundaryObserver(string movedPath)
        : INewProjectDirectoryCreationObserver
    {
        public bool RenameWasBlocked { get; private set; }

        public bool ReplacementWasInstalled { get; private set; }

        public void OnDirectoryCreated(string path)
        {
            try
            {
                Directory.Move(path, movedPath);
                Directory.CreateDirectory(path);
                ReplacementWasInstalled = true;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                RenameWasBlocked = true;
            }
        }
    }

    private sealed class ProofBoundaryObserver(Action<string> callback)
        : INewProjectArtifactRollbackObserver
    {
        public void OnProofComplete(string path)
            => callback(path);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode,
        ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);
}
