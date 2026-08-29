using System.Text;
using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Workbooks;
using Xunit;

namespace VbaDev.Tests;

public sealed class InitialWorkbookArtifactGuardTests
{
    [Fact]
    public void AtomicStagingDirectoryHandleBlocksRenameDuringOwnershipCapture()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var observer = new RenameStagingDirectoryObserver();
        var guard = new InitialWorkbookArtifactGuard(observer);
        var staging = guard.CreateStagingArtifact();
        try
        {
            Assert.True(observer.RenameWasBlocked);
            Assert.Equal(staging.DirectoryPath, observer.DirectoryPath);
            Assert.True(Directory.Exists(staging.DirectoryPath));
            Assert.False(Directory.Exists(observer.DisplacedPath));
        }
        finally
        {
            _ = guard.TryDeleteStaging(staging, expectedArtifact: null);
        }
    }

    [Fact]
    public void StagingCreationFailurePreservesForeignChildAndReportsItsPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var observer = new AddForeignChildThenThrowObserver();
        var guard = new InitialWorkbookArtifactGuard(observer);

        var error = Assert.Throws<InitialWorkbookArtifactRetainedException>(
            guard.CreateStagingArtifact);

        Assert.Equal(observer.DirectoryPath, error.WorkbookPath);
        Assert.NotNull(observer.ForeignPath);
        Assert.Equal("foreign", File.ReadAllText(observer.ForeignPath!));
        Directory.Delete(observer.DirectoryPath!, recursive: true);
    }

    [Fact]
    public void StagingDirectoryRemainsPinnedThroughCaptureAndCleanup()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var guard = new InitialWorkbookArtifactGuard();
        var staging = guard.CreateStagingArtifact();
        var displacedPath = staging.DirectoryPath + "-displaced";
        try
        {
            Assert.Throws<IOException>(() =>
                Directory.Move(staging.DirectoryPath, displacedPath));
            File.WriteAllText(
                staging.WorkbookPath,
                "created workbook",
                new UTF8Encoding(false));
            var evidence = guard.Capture(staging.WorkbookPath);
            Assert.Throws<IOException>(() =>
                Directory.Move(staging.DirectoryPath, displacedPath));

            var cleanup = guard.TryDeleteStaging(staging, evidence);

            Assert.True(cleanup.RemovedOrAbsent);
            Assert.False(Directory.Exists(staging.DirectoryPath));
            Assert.False(Directory.Exists(displacedPath));
        }
        finally
        {
            if (Directory.Exists(staging.DirectoryPath))
            {
                _ = guard.TryDeleteStaging(staging, expectedArtifact: null);
            }

            if (Directory.Exists(staging.DirectoryPath))
            {
                Directory.Delete(staging.DirectoryPath, recursive: true);
            }

            if (Directory.Exists(displacedPath))
            {
                Directory.Delete(displacedPath, recursive: true);
            }
        }
    }

    [Fact]
    public void InvocationOwnedTempStagingIsRemovedOnlyWhenExactAndEmpty()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var guard = new InitialWorkbookArtifactGuard();
        var staging = guard.CreateStagingArtifact();
        try
        {
            File.WriteAllBytes(staging.WorkbookPath, Encoding.UTF8.GetBytes("staging workbook"));
            var evidence = guard.Capture(staging.WorkbookPath);

            var cleanup = guard.TryDeleteStaging(staging, evidence);

            Assert.True(cleanup.RemovedOrAbsent);
            Assert.False(Directory.Exists(staging.DirectoryPath));
        }
        finally
        {
            if (Directory.Exists(staging.DirectoryPath))
            {
                Directory.Delete(staging.DirectoryPath, recursive: true);
            }
        }
    }

    [Fact]
    public void ReplacedStagingDirectoryIsPreservedByItsCapturedIdentity()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var guard = new InitialWorkbookArtifactGuard();
        var staging = guard.CreateStagingArtifact();
        var displacedDirectory = staging.DirectoryPath + "-displaced";
        try
        {
            File.WriteAllBytes(staging.WorkbookPath, Encoding.UTF8.GetBytes("staging workbook"));
            var evidence = guard.Capture(staging.WorkbookPath);
            staging.TakeDirectoryOwnershipHandle()?.Dispose();
            Directory.Move(staging.DirectoryPath, displacedDirectory);
            Directory.CreateDirectory(staging.DirectoryPath);

            var cleanup = guard.TryDeleteStaging(staging, evidence);

            Assert.False(cleanup.RemovedOrAbsent);
            Assert.True(cleanup.TargetChanged);
            Assert.True(Directory.Exists(staging.DirectoryPath));
            Assert.True(Directory.Exists(displacedDirectory));
        }
        finally
        {
            if (Directory.Exists(staging.DirectoryPath))
            {
                Directory.Delete(staging.DirectoryPath, recursive: true);
            }

            if (Directory.Exists(displacedDirectory))
            {
                Directory.Delete(displacedDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void CreateOnlyMaterializationCopiesExactBytesAndReturnsFinalEvidence()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var stagingPath = Path.Combine(temp.Path, "staging.xlsm");
        var workbookPath = Path.Combine(temp.Path, "Sample.xlsm");
        var bytes = Enumerable.Repeat((byte)0x27, 128 * 1024).ToArray();
        File.WriteAllBytes(stagingPath, bytes);
        var guard = new InitialWorkbookArtifactGuard();
        var stagingEvidence = guard.Capture(stagingPath);

        var finalEvidence = guard.MaterializeCreateOnly(
            stagingEvidence,
            workbookPath,
            CancellationToken.None);

        Assert.Equal(workbookPath, finalEvidence.WorkbookPath);
        Assert.Equal(stagingEvidence.Length, finalEvidence.Length);
        Assert.Equal(stagingEvidence.Sha256, finalEvidence.Sha256);
        Assert.Equal(bytes, File.ReadAllBytes(workbookPath));
        Assert.True(File.Exists(stagingPath));
    }

    [Fact]
    public void DestinationCannotBeRenamedAfterFinalProofUntilEvidenceIsReturned()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var stagingPath = Path.Combine(temp.Path, "staging.xlsm");
        var workbookPath = Path.Combine(temp.Path, "Sample.xlsm");
        var movedPath = Path.Combine(temp.Path, "moved.xlsm");
        File.WriteAllBytes(stagingPath, Encoding.UTF8.GetBytes("created workbook"));
        var observer = new RenameAfterDestinationProofObserver(movedPath);
        var guard = new InitialWorkbookArtifactGuard(observer);
        var stagingEvidence = guard.Capture(stagingPath);

        var evidence = guard.MaterializeCreateOnly(
            stagingEvidence,
            workbookPath,
            CancellationToken.None);

        Assert.True(observer.RenameWasBlocked);
        Assert.Equal(workbookPath, evidence.WorkbookPath);
        Assert.True(File.Exists(workbookPath));
        Assert.False(File.Exists(movedPath));
        File.Move(workbookPath, movedPath);
        Assert.True(File.Exists(movedPath));
    }

    [Fact]
    public void ExistingFinalDestinationIsPreservedByCreateOnlyMaterialization()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var stagingPath = Path.Combine(temp.Path, "staging.xlsm");
        var workbookPath = Path.Combine(temp.Path, "Sample.xlsm");
        var foreignBytes = Encoding.UTF8.GetBytes("foreign workbook");
        File.WriteAllBytes(stagingPath, Encoding.UTF8.GetBytes("created workbook"));
        File.WriteAllBytes(workbookPath, foreignBytes);
        var guard = new InitialWorkbookArtifactGuard();
        var stagingEvidence = guard.Capture(stagingPath);

        var error = Assert.Throws<InitialWorkbookArtifactRetainedException>(() =>
            guard.MaterializeCreateOnly(
                stagingEvidence,
                workbookPath,
                CancellationToken.None));

        Assert.True(error.TargetChanged);
        Assert.Null(error.ExpectedArtifact);
        Assert.Equal(foreignBytes, File.ReadAllBytes(workbookPath));
        Assert.True(File.Exists(stagingPath));
    }

    [Fact]
    public void CopyFailureRemovesOnlyTheExactPartialFinalWorkbook()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var stagingPath = Path.Combine(temp.Path, "staging.xlsm");
        var workbookPath = Path.Combine(temp.Path, "Sample.xlsm");
        File.WriteAllBytes(stagingPath, Enumerable.Repeat((byte)0x5a, 128 * 1024).ToArray());
        var guard = new InitialWorkbookArtifactGuard(
            new ThrowAfterFirstCopyObserver());
        var stagingEvidence = guard.Capture(stagingPath);

        var error = Assert.Throws<InvalidOperationException>(() =>
            guard.MaterializeCreateOnly(
                stagingEvidence,
                workbookPath,
                CancellationToken.None));

        Assert.Equal("synthetic copy failure", error.Message);
        Assert.False(File.Exists(workbookPath));
        Assert.True(File.Exists(stagingPath));
    }

    [Fact]
    public void CancellationAfterCreateRemovesOnlyTheExactPartialFinalWorkbook()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var stagingPath = Path.Combine(temp.Path, "staging.xlsm");
        var workbookPath = Path.Combine(temp.Path, "Sample.xlsm");
        File.WriteAllBytes(stagingPath, Enumerable.Repeat((byte)0x4c, 128 * 1024).ToArray());
        var guard = new InitialWorkbookArtifactGuard(
            new CancelOnDestinationCreateObserver(cancellation));
        var stagingEvidence = guard.Capture(stagingPath);

        Assert.ThrowsAny<OperationCanceledException>(() =>
            guard.MaterializeCreateOnly(
                stagingEvidence,
                workbookPath,
                cancellation.Token));

        Assert.False(File.Exists(workbookPath));
        Assert.True(File.Exists(stagingPath));
    }

    [Fact]
    public void RenameDuringCopyIsBlockedAndTheOwnedObjectIsRemovedOnFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var stagingPath = Path.Combine(temp.Path, "staging.xlsm");
        var workbookPath = Path.Combine(temp.Path, "Sample.xlsm");
        var movedOwnedPath = Path.Combine(temp.Path, "moved-owned.xlsm");
        File.WriteAllBytes(stagingPath, Enumerable.Repeat((byte)0x33, 128 * 1024).ToArray());
        var observer = new RenameDestinationObserver(movedOwnedPath);
        var guard = new InitialWorkbookArtifactGuard(observer);
        var stagingEvidence = guard.Capture(stagingPath);

        var error = Assert.Throws<InvalidOperationException>(() =>
            guard.MaterializeCreateOnly(
                stagingEvidence,
                workbookPath,
                CancellationToken.None));

        Assert.Equal("synthetic post-rename failure", error.Message);
        Assert.True(observer.RenameWasBlocked);
        Assert.False(File.Exists(workbookPath));
        Assert.False(File.Exists(movedOwnedPath));
        Assert.True(File.Exists(stagingPath));
    }

    [Fact]
    public void ExactCapturedWorkbookIsDeletedByItsOpenFileHandle()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var workbookPath = Path.Combine(temp.Path, "Sample.xlsm");
        File.WriteAllBytes(workbookPath, Encoding.UTF8.GetBytes("created workbook"));
        var guard = new InitialWorkbookArtifactGuard();
        var evidence = guard.Capture(workbookPath);

        var result = guard.TryDeleteIfUnchanged(workbookPath, evidence);

        Assert.True(result.RemovedOrAbsent);
        Assert.False(result.TargetChanged);
        Assert.Null(result.Failure);
        Assert.False(File.Exists(workbookPath));
    }

    [Fact]
    public void CleanupProofHandleBlocksWritesAndRenamesUntilDisposition()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var workbookPath = Path.Combine(temp.Path, "Sample.xlsm");
        var movedPath = Path.Combine(temp.Path, "moved.xlsm");
        File.WriteAllBytes(workbookPath, Encoding.UTF8.GetBytes("created workbook"));
        var observer = new MutationAttemptCleanupObserver(movedPath);
        var guard = new InitialWorkbookArtifactGuard(observer);
        var evidence = guard.Capture(workbookPath);

        var result = guard.TryDeleteIfUnchanged(workbookPath, evidence);

        Assert.True(observer.WriteWasBlocked);
        Assert.True(observer.RenameWasBlocked);
        Assert.True(result.RemovedOrAbsent);
        Assert.False(File.Exists(workbookPath));
        Assert.False(File.Exists(movedPath));
    }

    [Fact]
    public void StagingDirectoryCleanupProofHandleBlocksRenameAndDeleteUntilDisposition()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var observer = new DirectoryMutationAttemptCleanupObserver();
        var guard = new InitialWorkbookArtifactGuard(observer);
        var staging = guard.CreateStagingArtifact();

        var result = guard.TryDeleteStaging(staging, expectedArtifact: null);

        Assert.True(observer.RenameWasBlocked);
        Assert.True(observer.DeleteWasBlocked);
        Assert.True(result.RemovedOrAbsent);
        Assert.False(Directory.Exists(staging.DirectoryPath));
        Assert.False(Directory.Exists(observer.MovedPath));
    }

    [Fact]
    public void FileSymlinkIsRejectedAndItsForeignTargetIsPreserved()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var sentinelPath = Path.Combine(temp.Path, "sentinel.xlsm");
        var stagingPath = Path.Combine(temp.Path, "initial.xlsm");
        var sentinelBytes = Encoding.UTF8.GetBytes("foreign sentinel");
        File.WriteAllBytes(sentinelPath, sentinelBytes);
        try
        {
            File.CreateSymbolicLink(stagingPath, sentinelPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var guard = new InitialWorkbookArtifactGuard();

        Assert.ThrowsAny<IOException>(() => guard.Capture(stagingPath));
        Assert.Equal(sentinelBytes, File.ReadAllBytes(sentinelPath));
    }

    [Fact]
    public void SymlinkReplacementIsPreservedAndClassifiedAsTargetChanged()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var workbookPath = Path.Combine(temp.Path, "Sample.xlsm");
        var sentinelPath = Path.Combine(temp.Path, "sentinel.xlsm");
        var sentinelBytes = Encoding.UTF8.GetBytes("foreign sentinel");
        File.WriteAllText(workbookPath, "created workbook", new UTF8Encoding(false));
        var guard = new InitialWorkbookArtifactGuard();
        var evidence = guard.Capture(workbookPath);
        File.Delete(workbookPath);
        File.WriteAllBytes(sentinelPath, sentinelBytes);
        try
        {
            File.CreateSymbolicLink(workbookPath, sentinelPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var cleanup = guard.TryDeleteIfUnchanged(workbookPath, evidence);

        Assert.False(cleanup.RemovedOrAbsent);
        Assert.True(cleanup.TargetChanged);
        Assert.Equal(sentinelBytes, File.ReadAllBytes(sentinelPath));
    }

    [Fact]
    public void DirectoryReplacementIsPreservedAndClassifiedAsTargetChanged()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var workbookPath = Path.Combine(temp.Path, "Sample.xlsm");
        File.WriteAllText(workbookPath, "created workbook", new UTF8Encoding(false));
        var guard = new InitialWorkbookArtifactGuard();
        var evidence = guard.Capture(workbookPath);
        File.Delete(workbookPath);
        Directory.CreateDirectory(workbookPath);

        var cleanup = guard.TryDeleteIfUnchanged(workbookPath, evidence);

        Assert.False(cleanup.RemovedOrAbsent);
        Assert.True(cleanup.TargetChanged);
        Assert.True(Directory.Exists(workbookPath));
    }

    [Fact]
    public void SameBytesInAReplacementFileArePreserved()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var workbookPath = Path.Combine(temp.Path, "Sample.xlsm");
        var bytes = Encoding.UTF8.GetBytes("same workbook bytes");
        File.WriteAllBytes(workbookPath, bytes);
        var guard = new InitialWorkbookArtifactGuard();
        var evidence = guard.Capture(workbookPath);
        File.Delete(workbookPath);
        File.WriteAllBytes(workbookPath, bytes);

        var result = guard.TryDeleteIfUnchanged(workbookPath, evidence);

        Assert.False(result.RemovedOrAbsent);
        Assert.True(result.TargetChanged);
        Assert.Null(result.Failure);
        Assert.Equal(bytes, File.ReadAllBytes(workbookPath));
    }

    [Fact]
    public void ChangedBytesInTheCapturedFileArePreserved()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var workbookPath = Path.Combine(temp.Path, "Sample.xlsm");
        File.WriteAllText(workbookPath, "created workbook", new UTF8Encoding(false));
        var guard = new InitialWorkbookArtifactGuard();
        var evidence = guard.Capture(workbookPath);
        File.WriteAllText(workbookPath, "externally changed", new UTF8Encoding(false));

        var result = guard.TryDeleteIfUnchanged(workbookPath, evidence);

        Assert.False(result.RemovedOrAbsent);
        Assert.True(result.TargetChanged);
        Assert.Null(result.Failure);
        Assert.Equal("externally changed", File.ReadAllText(workbookPath));
    }

    private sealed class ThrowAfterFirstCopyObserver : IInitialWorkbookCopyObserver
    {
        public void OnDestinationCreated(string workbookPath)
        {
        }

        public void OnBytesCopied(string workbookPath, long bytesCopied)
            => throw new InvalidOperationException("synthetic copy failure");
    }

    private sealed class CancelOnDestinationCreateObserver(
        CancellationTokenSource cancellation) : IInitialWorkbookCopyObserver
    {
        public void OnDestinationCreated(string workbookPath)
            => cancellation.Cancel();

        public void OnBytesCopied(string workbookPath, long bytesCopied)
        {
        }
    }

    private sealed class RenameDestinationObserver(string movedOwnedPath)
        : IInitialWorkbookCopyObserver
    {
        public bool RenameWasBlocked { get; private set; }

        public void OnDestinationCreated(string workbookPath)
        {
            try
            {
                File.Move(workbookPath, movedOwnedPath);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                RenameWasBlocked = true;
            }

            throw new InvalidOperationException("synthetic post-rename failure");
        }

        public void OnBytesCopied(string workbookPath, long bytesCopied)
        {
        }
    }

    private sealed class MutationAttemptCleanupObserver(string movedPath)
        : IInitialWorkbookCleanupObserver
    {
        public bool WriteWasBlocked { get; private set; }

        public bool RenameWasBlocked { get; private set; }

        public void OnProofComplete(string path)
        {
            try
            {
                File.WriteAllText(path, "foreign replacement", new UTF8Encoding(false));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                WriteWasBlocked = true;
            }

            try
            {
                File.Move(path, movedPath);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                RenameWasBlocked = true;
            }
        }
    }

    private sealed class RenameStagingDirectoryObserver
        : IInitialWorkbookStagingObserver
    {
        public string? DirectoryPath { get; private set; }

        public string? DisplacedPath { get; private set; }

        public bool RenameWasBlocked { get; private set; }

        public void OnDirectoryCreated(string path)
        {
            DirectoryPath = path;
            DisplacedPath = path + "-displaced";
            try
            {
                Directory.Move(path, DisplacedPath);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                RenameWasBlocked = true;
            }
        }
    }

    private sealed class AddForeignChildThenThrowObserver
        : IInitialWorkbookStagingObserver
    {
        public string? DirectoryPath { get; private set; }

        public string? ForeignPath { get; private set; }

        public void OnDirectoryCreated(string path)
        {
            DirectoryPath = path;
            ForeignPath = Path.Combine(path, "foreign.txt");
            File.WriteAllText(ForeignPath, "foreign", new UTF8Encoding(false));
            throw new InvalidOperationException("synthetic staging setup failure");
        }
    }

    private sealed class RenameAfterDestinationProofObserver(string movedPath)
        : IInitialWorkbookCopyObserver
    {
        public bool RenameWasBlocked { get; private set; }

        public void OnDestinationCreated(string workbookPath)
        {
        }

        public void OnBytesCopied(string workbookPath, long bytesCopied)
        {
        }

        public void OnDestinationProved(string workbookPath)
        {
            try
            {
                File.Move(workbookPath, movedPath);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                RenameWasBlocked = true;
            }
        }
    }

    private sealed class DirectoryMutationAttemptCleanupObserver
        : IInitialWorkbookCleanupObserver
    {
        public string? MovedPath { get; private set; }

        public bool RenameWasBlocked { get; private set; }

        public bool DeleteWasBlocked { get; private set; }

        public void OnProofComplete(string path)
        {
            MovedPath = path + "-moved";
            try
            {
                Directory.Move(path, MovedPath);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                RenameWasBlocked = true;
            }

            try
            {
                Directory.Delete(path, recursive: false);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                DeleteWasBlocked = true;
            }
        }
    }
}
