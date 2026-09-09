using VbaDev.Infrastructure.FileSystem;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using VbaDev.App.CommonModules;
using VbaDev.App.FileSystem;
using Xunit;

namespace VbaDev.Tests;

public sealed class CommonModulesPackageSnapshotTests
{
    [Fact]
    public void ReturnedSelectionPlanCannotBeRewrittenThroughPublishedCollections()
    {
        using var temp = TempDirectory.Create();
        var repository = temp.CreateDirectory("common_modules_repo");
        WriteManifest(repository,
            ("Feature.bas", "optional", "Base.bas"),
            ("Base.bas", "runtime-baseline", string.Empty));
        WriteSource(repository, "Feature.bas", "feature");
        WriteSource(repository, "Base.bas", "base");
        using var snapshot = new CommonModulesPackageSnapshotFactory(
            new WindowsExactFileSystemObjectOwnershipFactory(),
            new CommonModulesPackageReader(new CommonModulesManifestReader()),
            temp.CreateDirectory("scratch"))
            .Capture(repository, CancellationToken.None);
        var plan = snapshot.ResolveRequestedPlan(["Feature"]);

        TryReplaceOrAdd(plan.Entries, plan.Entries[1]);
        TryReplaceOrAdd(plan.RequiredReferences, "Injected Library");
        TryReplaceOrAdd(snapshot.Entries, plan.Entries[0]);
        foreach (var entry in plan.Entries)
        {
            TryReplaceOrAdd(entry.Categories, "test-double");
            TryReplaceOrAdd(entry.Dependencies, "Missing.bas");
            TryReplaceOrAdd(entry.RequiredReferences, "Injected Library");
        }

        Assert.Equal(["Base.bas", "Feature.bas"], plan.Entries.Select(entry => entry.ModuleFile));
        Assert.Empty(plan.RequiredReferences);
        Assert.Equal(["Feature.bas", "Base.bas"], snapshot.Entries.Select(entry => entry.ModuleFile));
        Assert.Equal(["runtime-baseline"], plan.Entries[0].Categories);
        Assert.Empty(plan.Entries[0].Dependencies);
        Assert.Equal(["optional"], plan.Entries[1].Categories);
        Assert.Equal(["Base.bas"], plan.Entries[1].Dependencies);
        Assert.All(plan.Entries, entry => Assert.Empty(entry.RequiredReferences));
        var subsequent = snapshot.ResolveRequestedPlan(["Feature"]);
        Assert.Equal(["Base.bas", "Feature.bas"], subsequent.Entries.Select(entry => entry.ModuleFile));
        Assert.Empty(subsequent.RequiredReferences);
    }

    private static void TryReplaceOrAdd<T>(IReadOnlyList<T> values, T replacement)
    {
        if (values is not IList<T> writableView)
        {
            return;
        }

        try
        {
            if (writableView.Count == 0)
            {
                writableView.Add(replacement);
            }
            else
            {
                writableView[0] = replacement;
            }
        }
        catch (NotSupportedException)
        {
            // Ordinary collection interfaces may reject writes to immutable views.
        }
    }

    [Fact]
    public void CleanupRetriesOwnedFilesAndFinishesAfterCommandCancellation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var repository = temp.CreateDirectory("common_modules_repo");
        WriteManifest(repository, ("Feature.bas", "optional", string.Empty));
        WriteSource(repository, "Feature.bas", "captured bytes");
        FileStream? fileLock = null;
        var proofs = new List<string>();
        var observer = new CallbackSnapshotCleanupObserver(path =>
        {
            proofs.Add(path);
            if (Path.GetFileName(path) == CommonModulesManifestReader.ManifestFileName)
            {
                cancellation.Cancel();
                fileLock?.Dispose();
                fileLock = null;
            }
        });
        using var snapshot = new CommonModulesPackageSnapshotFactory(
            new WindowsExactFileSystemObjectOwnershipFactory(),
            new CommonModulesPackageReader(new CommonModulesManifestReader()),
            temp.CreateDirectory("scratch"), beforeLiveStabilityProof: null, observer)
            .Capture(repository, cancellation.Token);
        var feature = Path.Combine(snapshot.StagingPath, "Feature.bas");
        fileLock = File.Open(feature, FileMode.Open, FileAccess.Read, FileShare.Read);

        try
        {
            var result = snapshot.Cleanup();

            Assert.True(cancellation.IsCancellationRequested);
            Assert.True(result.Deleted);
            Assert.True(result.IsConclusive);
            Assert.Empty(result.RetainedEntryPaths);
            Assert.Equal(new[] { Path.Combine(snapshot.StagingPath, CommonModulesManifestReader.ManifestFileName),
                feature, snapshot.StagingPath }, proofs);
            Assert.Same(result, snapshot.Cleanup());
            Assert.False(Directory.Exists(snapshot.StagingPath));
        }
        finally
        {
            fileLock?.Dispose();
        }
    }

    [Fact]
    public void SnapshotCleanupFailureClassificationNeverTreatsOwnershipRollbackAsRetention()
    {
        Assert.False(CommonModulesInstallationTransaction.IsRetainableSnapshotCleanupFailure(
            new ExactFileSystemObjectOwnership.RollbackException("C:\\owned")));
        Assert.True(CommonModulesInstallationTransaction.IsRetainableSnapshotCleanupFailure(
            new IOException("ordinary cleanup failure")));
        Assert.True(CommonModulesInstallationTransaction.IsRetainableSnapshotCleanupFailure(
            new UnauthorizedAccessException("ordinary cleanup failure")));
        Assert.True(CommonModulesInstallationTransaction.IsRetainableSnapshotCleanupFailure(
            new InvalidOperationException("ordinary cleanup failure")));
    }

    [Fact]
    public void CaptureFixesTheCompletePackageAndPlansOnlyFromCapturedBytes()
    {
        using var temp = TempDirectory.Create();
        var repository = temp.CreateDirectory("common_modules_repo");
        WriteManifest(
            repository,
            ("Feature.bas", "optional", "Service.cls,Dialog.frm"),
            ("Service.cls", "optional", string.Empty),
            ("Dialog.frm", "optional", string.Empty));
        WriteSource(repository, "Feature.bas", "feature generation one");
        WriteSource(repository, "Service.cls", "service generation one");
        WriteSource(repository, "Dialog.frm", "dialog generation one");
        var sidecarBytes = new byte[] { 0, 1, 2, 255 };
        File.WriteAllBytes(Path.Combine(repository, "Dialog.frx"), sidecarBytes);
        var expectedManifestBytes = File.ReadAllBytes(Path.Combine(
            repository,
            CommonModulesManifestReader.ManifestFileName));
        var expectedFeatureBytes = File.ReadAllBytes(Path.Combine(repository, "Feature.bas"));
        var expectedServiceBytes = File.ReadAllBytes(Path.Combine(repository, "Service.cls"));
        var expectedDialogBytes = File.ReadAllBytes(Path.Combine(repository, "Dialog.frm"));
        var scratchRoot = temp.CreateDirectory("scratch");
        var snapshot = new CommonModulesPackageSnapshotFactory(
            new WindowsExactFileSystemObjectOwnershipFactory(),
            new CommonModulesPackageReader(new CommonModulesManifestReader()),
            scratchRoot)
            .Capture(repository, CancellationToken.None);
        var stagingPath = snapshot.StagingPath;

        WriteSource(repository, "Feature.bas", "feature generation two");
        File.Delete(Path.Combine(repository, "Dialog.frx"));

        var plan = snapshot.ResolveRequestedPlan(["Feature"]);
        Assert.Equal(
            ["Feature.bas", "Service.cls", "Dialog.frm"],
            snapshot.Entries.Select(entry => entry.ModuleFile));
        Assert.Equal(
            ["Service.cls", "Dialog.frm", "Feature.bas"],
            plan.Entries.Select(entry => entry.ModuleFile));
        Assert.Equal(
            expectedManifestBytes,
            snapshot.ReadFileBytes(CommonModulesManifestReader.ManifestFileName));
        Assert.Equal(expectedFeatureBytes, snapshot.ReadFileBytes("Feature.bas"));
        Assert.Equal(expectedServiceBytes, snapshot.ReadFileBytes("Service.cls"));
        Assert.Equal(expectedDialogBytes, snapshot.ReadFileBytes("Dialog.frm"));
        Assert.True(snapshot.TryReadFileBytes("Dialog.frx", out var capturedSidecarBytes));
        Assert.Equal(sidecarBytes, capturedSidecarBytes);
        capturedSidecarBytes[0] = 99;
        Assert.Equal(sidecarBytes, snapshot.ReadFileBytes("Dialog.frx"));
        Assert.False(snapshot.TryReadFileBytes("Missing.frx", out var missingBytes));
        Assert.Empty(missingBytes);
        Assert.True(Directory.Exists(stagingPath));

        var cleanup = snapshot.Cleanup();

        Assert.True(cleanup.Deleted);
        Assert.Null(cleanup.RetainedPath);
        Assert.False(Directory.Exists(stagingPath));
        snapshot.Dispose();
    }

    [Fact]
    public void CapturePlansFromOriginalBytesWhenTheStagedManifestChangesBeforeLoad()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var repository = temp.CreateDirectory("common_modules_repo");
        WriteManifest(
            repository,
            ("Feature.bas", "optional", string.Empty),
            ("Service.bas", "optional", string.Empty));
        WriteSource(repository, "Feature.bas", "feature generation one");
        WriteSource(repository, "Service.bas", "service generation one");
        var expectedManifestBytes = File.ReadAllBytes(Path.Combine(
            repository,
            CommonModulesManifestReader.ManifestFileName));
        var scratchRoot = temp.CreateDirectory("scratch");
        var factory = new CommonModulesPackageSnapshotFactory(
            new WindowsExactFileSystemObjectOwnershipFactory(),
            new CommonModulesPackageReader(new CommonModulesManifestReader()),
            scratchRoot,
            beforePackageLoad: () =>
            {
                var stagingPath = Directory.EnumerateDirectories(scratchRoot).Single();
                WriteManifest(
                    stagingPath,
                    ("Feature.bas", "optional", "Service.bas"),
                    ("Service.bas", "optional", string.Empty));
            },
            beforeLiveStabilityProof: null,
            NoOpSnapshotCleanupObserver.Instance);

        var snapshot = factory.Capture(repository, CancellationToken.None);

        var plan = snapshot.ResolveRequestedPlan(["Feature"]);
        Assert.Equal(["Feature.bas"], plan.Entries.Select(entry => entry.ModuleFile));
        Assert.Equal(
            expectedManifestBytes,
            snapshot.ReadFileBytes(CommonModulesManifestReader.ManifestFileName));
    }

    [Fact]
    public void CaptureRejectsARepositoryGenerationThatChangesBeforeStabilityProof()
    {
        using var temp = TempDirectory.Create();
        var repository = temp.CreateDirectory("common_modules_repo");
        WriteManifest(repository, ("Feature.bas", "optional", string.Empty));
        WriteSource(repository, "Feature.bas", "feature generation one");
        var scratchRoot = temp.CreateDirectory("scratch");
        var factory = new CommonModulesPackageSnapshotFactory(
            new WindowsExactFileSystemObjectOwnershipFactory(),
            new CommonModulesPackageReader(new CommonModulesManifestReader()),
            scratchRoot,
            () => WriteSource(repository, "Feature.bas", "feature generation two"));

        var error = Assert.Throws<CommonModulesManifestException>(() =>
            factory.Capture(repository, CancellationToken.None));

        Assert.Contains("changed while its immutable snapshot was being captured", error.Message);
        Assert.Empty(Directory.EnumerateDirectories(scratchRoot));
        Assert.Contains(
            "feature generation two",
            File.ReadAllText(Path.Combine(repository, "Feature.bas"), Encoding.UTF8));
    }

    [Fact]
    public void CaptureRejectsAnInvalidStagedPackageAndRemovesOwnedScratch()
    {
        using var temp = TempDirectory.Create();
        var repository = temp.CreateDirectory("common_modules_repo");
        WriteManifest(repository, ("Feature.bas", "optional", string.Empty));
        var sourcePath = Path.Combine(repository, "Feature.bas");
        var invalidBytes = Encoding.ASCII.GetBytes("Option Explicit\r\n");
        File.WriteAllBytes(sourcePath, invalidBytes);
        var scratchRoot = temp.CreateDirectory("scratch");
        var factory = new CommonModulesPackageSnapshotFactory(
            new WindowsExactFileSystemObjectOwnershipFactory(),
            new CommonModulesPackageReader(new CommonModulesManifestReader()),
            scratchRoot);

        Assert.Throws<CommonModulesManifestException>(() =>
            factory.Capture(repository, CancellationToken.None));

        Assert.Equal(invalidBytes, File.ReadAllBytes(sourcePath));
        Assert.Empty(Directory.EnumerateDirectories(scratchRoot));
    }

    [Fact]
    public void CaptureRejectsAnUnreadablePackageWithoutChangingItsInputs()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var repository = temp.CreateDirectory("common_modules_repo");
        WriteManifest(repository, ("Feature.bas", "optional", string.Empty));
        WriteSource(repository, "Feature.bas", "feature generation one");
        var sourcePath = Path.Combine(repository, "Feature.bas");
        var expectedBytes = File.ReadAllBytes(sourcePath);
        var scratchRoot = temp.CreateDirectory("scratch");
        var factory = new CommonModulesPackageSnapshotFactory(
            new WindowsExactFileSystemObjectOwnershipFactory(),
            new CommonModulesPackageReader(new CommonModulesManifestReader()),
            scratchRoot);
        using var sourceLock = File.Open(
            sourcePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        var error = Assert.Throws<CommonModulesManifestException>(() =>
            factory.Capture(repository, CancellationToken.None));

        Assert.Contains("package entry could not be read", error.Message);
        sourceLock.Position = 0;
        var actualBytes = new byte[sourceLock.Length];
        sourceLock.ReadExactly(actualBytes);
        Assert.Equal(expectedBytes, actualBytes);
        Assert.Empty(Directory.EnumerateDirectories(scratchRoot));
    }

    [Fact]
    public void CancellationPreservesForeignContentAddedToStagingAndReportsTheRetainedWorkspace()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var repository = temp.CreateDirectory("common_modules_repo");
        WriteManifest(repository, ("Feature.bas", "optional", string.Empty));
        WriteSource(repository, "Feature.bas", "feature generation one");
        var scratchRoot = temp.CreateDirectory("scratch");
        string? stagingPath = null;
        var foreignBytes = Encoding.UTF8.GetBytes("foreign staging content");
        var factory = new CommonModulesPackageSnapshotFactory(
            new WindowsExactFileSystemObjectOwnershipFactory(),
            new CommonModulesPackageReader(new CommonModulesManifestReader()),
            scratchRoot,
            () =>
            {
                stagingPath = Directory.EnumerateDirectories(scratchRoot).Single();
                File.WriteAllBytes(Path.Combine(stagingPath, "foreign.txt"), foreignBytes);
                cancellation.Cancel();
            });

        var error = Assert.Throws<CommonModulesPackageSnapshotRetainedException>(() =>
            factory.Capture(repository, cancellation.Token));

        Assert.NotNull(stagingPath);
        Assert.Contains(stagingPath, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(stagingPath, error.CleanupResult.RetainedPath);
        Assert.True(error.CleanupResult.IsConclusive);
        Assert.Contains(
            Path.Combine(stagingPath, "foreign.txt"),
            error.CleanupResult.RetainedEntryPaths);
        Assert.True(Directory.Exists(stagingPath));
        Assert.Equal(
            foreignBytes,
            File.ReadAllBytes(Path.Combine(stagingPath, "foreign.txt")));
        Assert.False(File.Exists(Path.Combine(stagingPath, "Feature.bas")));
        Assert.False(File.Exists(Path.Combine(
            stagingPath,
            CommonModulesManifestReader.ManifestFileName)));
    }

    [Fact]
    public void CaptureFailureReportsInconclusiveCleanupWhenAnOwnedStagingFileIsLocked()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var repository = temp.CreateDirectory("common_modules_repo");
        WriteManifest(repository, ("Feature.bas", "optional", string.Empty));
        WriteSource(repository, "Feature.bas", "feature generation one");
        var scratchRoot = temp.CreateDirectory("scratch");
        string? stagingPath = null;
        FileStream? stagingLock = null;
        var factory = new CommonModulesPackageSnapshotFactory(
            new WindowsExactFileSystemObjectOwnershipFactory(),
            new CommonModulesPackageReader(new CommonModulesManifestReader()),
            scratchRoot,
            () =>
            {
                stagingPath = Directory.EnumerateDirectories(scratchRoot).Single();
                stagingLock = File.Open(
                    Path.Combine(stagingPath, "Feature.bas"),
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);
                WriteSource(repository, "Feature.bas", "feature generation two");
            });

        try
        {
            var error = Assert.Throws<CommonModulesPackageSnapshotRetainedException>(() =>
                factory.Capture(repository, CancellationToken.None));

            Assert.NotNull(stagingPath);
            Assert.Equal(stagingPath, error.CleanupResult.RetainedPath);
            Assert.False(error.CleanupResult.IsConclusive);
            Assert.Contains(
                Path.Combine(stagingPath, "Feature.bas"),
                error.CleanupResult.ObservationIncompletePaths);
            Assert.True(File.Exists(Path.Combine(stagingPath, "Feature.bas")));
            Assert.False(File.Exists(Path.Combine(
                stagingPath,
                CommonModulesManifestReader.ManifestFileName)));
        }
        finally
        {
            stagingLock?.Dispose();
        }
    }

    [Fact]
    public void CleanupFileProofHandleBlocksWritesAndRenamesUntilExactDisposition()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var repository = temp.CreateDirectory("common_modules_repo");
        WriteManifest(repository, ("Feature.bas", "optional", string.Empty));
        WriteSource(repository, "Feature.bas", "feature generation one");
        var scratchRoot = temp.CreateDirectory("scratch");
        var observer = new FileMutationAttemptCleanupObserver("Feature.bas");
        var snapshot = new CommonModulesPackageSnapshotFactory(
            new WindowsExactFileSystemObjectOwnershipFactory(),
            new CommonModulesPackageReader(new CommonModulesManifestReader()),
            scratchRoot,
            beforeLiveStabilityProof: null,
            observer)
            .Capture(repository, CancellationToken.None);

        var cleanup = snapshot.Cleanup();

        Assert.True(observer.WriteWasBlocked);
        Assert.True(observer.RenameWasBlocked);
        Assert.True(cleanup.Deleted);
        Assert.False(Directory.Exists(snapshot.StagingPath));
        Assert.False(File.Exists(observer.MovedPath));
    }

    [Fact]
    public void CleanupNeverDeletesThroughAHardLinkRaceAfterFileProof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var repository = temp.CreateDirectory("common_modules_repo");
        WriteManifest(repository, ("Feature.bas", "optional", string.Empty));
        WriteSource(repository, "Feature.bas", "feature generation one");
        var scratchRoot = temp.CreateDirectory("scratch");
        var observer = new HardLinkAfterProofCleanupObserver("Feature.bas");
        var snapshot = new CommonModulesPackageSnapshotFactory(
            new WindowsExactFileSystemObjectOwnershipFactory(),
            new CommonModulesPackageReader(new CommonModulesManifestReader()),
            scratchRoot,
            beforeLiveStabilityProof: null,
            observer)
            .Capture(repository, CancellationToken.None);
        var featurePath = Path.Combine(snapshot.StagingPath, "Feature.bas");

        var cleanup = snapshot.Cleanup();

        if (observer.HardLinkWasCreated)
        {
            Assert.False(cleanup.Deleted);
            Assert.True(cleanup.IsConclusive);
            Assert.Contains(featurePath, cleanup.RetainedEntryPaths);
            Assert.True(File.Exists(featurePath));
            Assert.True(File.Exists(observer.HardLinkPath));
        }
        else
        {
            Assert.Equal(32, observer.HardLinkError);
            Assert.True(cleanup.Deleted);
            Assert.False(Directory.Exists(snapshot.StagingPath));
            Assert.False(File.Exists(observer.HardLinkPath));
        }
    }

    [Fact]
    public void CleanupDirectoryProofHandleBlocksDeleteAndRenameUntilExactDisposition()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var repository = temp.CreateDirectory("common_modules_repo");
        WriteManifest(repository, ("Feature.bas", "optional", string.Empty));
        WriteSource(repository, "Feature.bas", "feature generation one");
        var scratchRoot = temp.CreateDirectory("scratch");
        var observer = new DirectoryMutationAttemptCleanupObserver();
        var snapshot = new CommonModulesPackageSnapshotFactory(
            new WindowsExactFileSystemObjectOwnershipFactory(),
            new CommonModulesPackageReader(new CommonModulesManifestReader()),
            scratchRoot,
            beforeLiveStabilityProof: null,
            observer)
            .Capture(repository, CancellationToken.None);
        observer.TargetPath = snapshot.StagingPath;

        var cleanup = snapshot.Cleanup();

        Assert.True(observer.DeleteWasBlocked);
        Assert.True(observer.RenameWasBlocked);
        Assert.True(cleanup.Deleted);
        Assert.False(Directory.Exists(snapshot.StagingPath));
        Assert.False(Directory.Exists(observer.MovedPath));
    }

    [Fact]
    public void CleanupPreservesASameByteReplacementForAnOwnedStagingFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var repository = temp.CreateDirectory("common_modules_repo");
        WriteManifest(repository, ("Feature.bas", "optional", string.Empty));
        WriteSource(repository, "Feature.bas", "feature generation one");
        var scratchRoot = temp.CreateDirectory("scratch");
        var snapshot = new CommonModulesPackageSnapshotFactory(
            new WindowsExactFileSystemObjectOwnershipFactory(),
            new CommonModulesPackageReader(new CommonModulesManifestReader()),
            scratchRoot)
            .Capture(repository, CancellationToken.None);
        var featurePath = Path.Combine(snapshot.StagingPath, "Feature.bas");
        var replacementBytes = File.ReadAllBytes(featurePath);
        File.Delete(featurePath);
        File.WriteAllBytes(featurePath, replacementBytes);

        var cleanup = snapshot.Cleanup();

        Assert.False(cleanup.Deleted);
        Assert.True(cleanup.IsConclusive);
        Assert.Equal(snapshot.StagingPath, cleanup.RetainedPath);
        Assert.Contains(featurePath, cleanup.RetainedEntryPaths);
        Assert.Equal(replacementBytes, File.ReadAllBytes(featurePath));
        Assert.False(File.Exists(Path.Combine(
            snapshot.StagingPath,
            CommonModulesManifestReader.ManifestFileName)));
    }

    [Fact]
    public void CleanupPreservesAHardLinkedOwnedStagingFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var repository = temp.CreateDirectory("common_modules_repo");
        WriteManifest(repository, ("Feature.bas", "optional", string.Empty));
        WriteSource(repository, "Feature.bas", "feature generation one");
        var scratchRoot = temp.CreateDirectory("scratch");
        var snapshot = new CommonModulesPackageSnapshotFactory(
            new WindowsExactFileSystemObjectOwnershipFactory(),
            new CommonModulesPackageReader(new CommonModulesManifestReader()),
            scratchRoot)
            .Capture(repository, CancellationToken.None);
        var featurePath = Path.Combine(snapshot.StagingPath, "Feature.bas");
        var aliasPath = Path.Combine(temp.Path, "Feature-hardlink.bas");
        Assert.True(
            CreateHardLink(aliasPath, featurePath, IntPtr.Zero),
            new Win32Exception(Marshal.GetLastWin32Error()).Message);

        var cleanup = snapshot.Cleanup();

        Assert.False(cleanup.Deleted);
        Assert.True(cleanup.IsConclusive);
        Assert.Equal(snapshot.StagingPath, cleanup.RetainedPath);
        Assert.Contains(featurePath, cleanup.RetainedEntryPaths);
        Assert.True(File.Exists(featurePath));
        Assert.True(File.Exists(aliasPath));
        Assert.Equal(File.ReadAllBytes(featurePath), File.ReadAllBytes(aliasPath));
    }

    [Fact]
    public void CleanupPreservesAReplacementForTheOwnedStagingDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var repository = temp.CreateDirectory("common_modules_repo");
        WriteManifest(repository, ("Feature.bas", "optional", string.Empty));
        WriteSource(repository, "Feature.bas", "feature generation one");
        var scratchRoot = temp.CreateDirectory("scratch");
        var snapshot = new CommonModulesPackageSnapshotFactory(
            new WindowsExactFileSystemObjectOwnershipFactory(),
            new CommonModulesPackageReader(new CommonModulesManifestReader()),
            scratchRoot)
            .Capture(repository, CancellationToken.None);
        Directory.Delete(snapshot.StagingPath, recursive: true);
        Directory.CreateDirectory(snapshot.StagingPath);
        var foreignPath = Path.Combine(snapshot.StagingPath, "foreign.txt");
        File.WriteAllText(foreignPath, "foreign");

        var cleanup = snapshot.Cleanup();

        Assert.False(cleanup.Deleted);
        Assert.True(cleanup.IsConclusive);
        Assert.Equal(snapshot.StagingPath, cleanup.RetainedPath);
        Assert.Contains(snapshot.StagingPath, cleanup.RetainedEntryPaths);
        Assert.True(File.Exists(foreignPath));
    }

    [Fact]
    public void CleanupPreservesAReparsePointReplacementForTheOwnedStagingDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var repository = temp.CreateDirectory("common_modules_repo");
        WriteManifest(repository, ("Feature.bas", "optional", string.Empty));
        WriteSource(repository, "Feature.bas", "feature generation one");
        var scratchRoot = temp.CreateDirectory("scratch");
        var snapshot = new CommonModulesPackageSnapshotFactory(
            new WindowsExactFileSystemObjectOwnershipFactory(),
            new CommonModulesPackageReader(new CommonModulesManifestReader()),
            scratchRoot)
            .Capture(repository, CancellationToken.None);
        Directory.Delete(snapshot.StagingPath, recursive: true);
        var sentinelDirectory = temp.CreateDirectory("sentinel-directory");
        var sentinelPath = Path.Combine(sentinelDirectory, "sentinel.txt");
        File.WriteAllText(sentinelPath, "sentinel");
        Directory.CreateSymbolicLink(snapshot.StagingPath, sentinelDirectory);

        var cleanup = snapshot.Cleanup();

        Assert.False(cleanup.Deleted);
        Assert.True(cleanup.IsConclusive);
        Assert.Equal(snapshot.StagingPath, cleanup.RetainedPath);
        Assert.Contains(snapshot.StagingPath, cleanup.RetainedEntryPaths);
        Assert.True(
            File.GetAttributes(snapshot.StagingPath).HasFlag(FileAttributes.ReparsePoint));
        Assert.Equal("sentinel", File.ReadAllText(sentinelPath));
    }

    [Fact]
    public void CleanupPreservesChangedBytesInAnOwnedStagingFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var repository = temp.CreateDirectory("common_modules_repo");
        WriteManifest(repository, ("Feature.bas", "optional", string.Empty));
        WriteSource(repository, "Feature.bas", "feature generation one");
        var scratchRoot = temp.CreateDirectory("scratch");
        var snapshot = new CommonModulesPackageSnapshotFactory(
            new WindowsExactFileSystemObjectOwnershipFactory(),
            new CommonModulesPackageReader(new CommonModulesManifestReader()),
            scratchRoot)
            .Capture(repository, CancellationToken.None);
        var featurePath = Path.Combine(snapshot.StagingPath, "Feature.bas");
        var changedBytes = Encoding.UTF8.GetBytes("externally changed staging bytes");
        File.WriteAllBytes(featurePath, changedBytes);

        var cleanup = snapshot.Cleanup();

        Assert.False(cleanup.Deleted);
        Assert.True(cleanup.IsConclusive);
        Assert.Contains(featurePath, cleanup.RetainedEntryPaths);
        Assert.Equal(changedBytes, File.ReadAllBytes(featurePath));
    }

    [Fact]
    public void CleanupTreatsAnExternallyMissingOwnedFileAsAlreadyRemoved()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var repository = temp.CreateDirectory("common_modules_repo");
        WriteManifest(repository, ("Feature.bas", "optional", string.Empty));
        WriteSource(repository, "Feature.bas", "feature generation one");
        var scratchRoot = temp.CreateDirectory("scratch");
        var snapshot = new CommonModulesPackageSnapshotFactory(
            new WindowsExactFileSystemObjectOwnershipFactory(),
            new CommonModulesPackageReader(new CommonModulesManifestReader()),
            scratchRoot)
            .Capture(repository, CancellationToken.None);
        File.Delete(Path.Combine(snapshot.StagingPath, "Feature.bas"));

        var cleanup = snapshot.Cleanup();

        Assert.True(cleanup.Deleted);
        Assert.Null(cleanup.RetainedPath);
        Assert.Empty(cleanup.RetainedEntryPaths);
        Assert.False(Directory.Exists(snapshot.StagingPath));
    }

    [Fact]
    public void CleanupTreatsAnExternallyMissingStagingDirectoryAsAlreadyRemoved()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var repository = temp.CreateDirectory("common_modules_repo");
        WriteManifest(repository, ("Feature.bas", "optional", string.Empty));
        WriteSource(repository, "Feature.bas", "feature generation one");
        var scratchRoot = temp.CreateDirectory("scratch");
        var snapshot = new CommonModulesPackageSnapshotFactory(
            new WindowsExactFileSystemObjectOwnershipFactory(),
            new CommonModulesPackageReader(new CommonModulesManifestReader()),
            scratchRoot)
            .Capture(repository, CancellationToken.None);
        Directory.Delete(snapshot.StagingPath, recursive: true);

        var cleanup = snapshot.Cleanup();

        Assert.True(cleanup.Deleted);
        Assert.Null(cleanup.RetainedPath);
        Assert.Empty(cleanup.RetainedEntryPaths);
        Assert.True(cleanup.IsConclusive);
    }

    [Fact]
    public void CaptureFailsBeforeCreatingScratchOnAnUnsupportedPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var repository = temp.CreateDirectory("common_modules_repo");
        WriteManifest(repository, ("Feature.bas", "optional", string.Empty));
        WriteSource(repository, "Feature.bas", "feature generation one");
        var scratchRoot = Path.Combine(temp.Path, "scratch-not-created");
        var factory = new CommonModulesPackageSnapshotFactory(
            new WindowsExactFileSystemObjectOwnershipFactory(),
            new CommonModulesPackageReader(new CommonModulesManifestReader()),
            scratchRoot);

        Assert.Throws<PlatformNotSupportedException>(
            () => factory.Capture(repository, CancellationToken.None));

        Assert.False(Directory.Exists(scratchRoot));
    }

    private static void WriteManifest(
        string repository,
        params (string ModuleFile, string Categories, string Dependencies)[] rows)
    {
        var lines = new List<string>
        {
            "ModuleFile\tCategories\tDependencies\tRequiredReferences"
        };
        lines.AddRange(rows.Select(row =>
            $"{row.ModuleFile}\t{row.Categories}\t{row.Dependencies}\t[]"));
        File.WriteAllText(
            Path.Combine(repository, CommonModulesManifestReader.ManifestFileName),
            string.Join("\r\n", lines) + "\r\n",
            new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true));
    }

    private static void WriteSource(string repository, string fileName, string body)
    {
        var moduleName = Path.GetFileNameWithoutExtension(fileName);
        var header = Path.GetExtension(fileName) switch
        {
            ".bas" => $"Attribute VB_Name = \"{moduleName}\"\r\n",
            ".cls" => "VERSION 1.0 CLASS\r\nBEGIN\r\nEND\r\n"
                + $"Attribute VB_Name = \"{moduleName}\"\r\n",
            ".frm" => "VERSION 5.00\r\n"
                + $"Attribute VB_Name = \"{moduleName}\"\r\n",
            _ => throw new ArgumentOutOfRangeException(nameof(fileName))
        };
        File.WriteAllText(
            Path.Combine(repository, fileName),
            header + body + "\r\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private sealed class FileMutationAttemptCleanupObserver(string fileName)
        : ICommonModulesPackageSnapshotCleanupObserver
    {
        public bool WriteWasBlocked { get; private set; }

        public bool RenameWasBlocked { get; private set; }

        public string? MovedPath { get; private set; }

        public void OnProofComplete(string path)
        {
            if (!Path.GetFileName(path).Equals(fileName, StringComparison.Ordinal))
            {
                return;
            }

            try
            {
                File.WriteAllText(path, "foreign replacement", new UTF8Encoding(false));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                WriteWasBlocked = true;
            }

            MovedPath = path + ".moved";
            try
            {
                File.Move(path, MovedPath);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                RenameWasBlocked = true;
            }
        }
    }

    private sealed class HardLinkAfterProofCleanupObserver(string fileName)
        : ICommonModulesPackageSnapshotCleanupObserver
    {
        public bool HardLinkWasCreated { get; private set; }

        public int HardLinkError { get; private set; }

        public string? HardLinkPath { get; private set; }

        public void OnProofComplete(string path)
        {
            if (!Path.GetFileName(path).Equals(fileName, StringComparison.Ordinal))
            {
                return;
            }

            HardLinkPath = path + ".hardlink";
            HardLinkWasCreated = CreateHardLink(
                HardLinkPath,
                path,
                IntPtr.Zero);
            if (!HardLinkWasCreated)
            {
                HardLinkError = Marshal.GetLastWin32Error();
            }
        }
    }

    private sealed class DirectoryMutationAttemptCleanupObserver
        : ICommonModulesPackageSnapshotCleanupObserver
    {
        public string? TargetPath { get; set; }

        public string? MovedPath { get; private set; }

        public bool DeleteWasBlocked { get; private set; }

        public bool RenameWasBlocked { get; private set; }

        public void OnProofComplete(string path)
        {
            if (TargetPath is null
                || !path.Equals(TargetPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
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
        }
    }

    private sealed class CallbackSnapshotCleanupObserver(Action<string> callback)
        : ICommonModulesPackageSnapshotCleanupObserver
    {
        public void OnProofComplete(string path) => callback(path);
    }

    private sealed class NoOpSnapshotCleanupObserver
        : ICommonModulesPackageSnapshotCleanupObserver
    {
        public static NoOpSnapshotCleanupObserver Instance { get; } = new();

        public void OnProofComplete(string path)
        {
        }
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateHardLinkW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);
}
