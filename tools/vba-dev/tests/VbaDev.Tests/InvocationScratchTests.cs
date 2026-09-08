using VbaDev.App.FileSystem;
using VbaDev.Infrastructure.FileSystem;
using Xunit;

namespace VbaDev.Tests;

public sealed class InvocationScratchTests
{
    [Fact]
    public async Task LockedScratchProducesBoundedStableInconclusiveEvidence()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = TempDirectory.Create();
        using var ownership = new WindowsExactFileSystemObjectOwnershipFactory().Open();
        var root = Assert.IsType<ExactFileSystemObjectOwnership.DirectoryReceipt>(
            ownership.TryCreateOnlyDirectory(temp.Path, "owned"));
        var file = ownership.CreateOnlyFile(root, "locked.bas", "owned bytes"u8);
        ownership.ReleaseCreationFence(root);
        var scratch = new InvocationScratch(ownership);
        scratch.Register(root);
        scratch.Register(file);
        InvocationScratchCleanupEvidence evidence;
        using (File.Open(file.Route, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            evidence = await Task.Run(() => scratch.Cleanup()).WaitAsync(TimeSpan.FromSeconds(2));
        }

        Assert.Equal(InvocationScratchCleanupStatus.Inconclusive, evidence.Status);
        Assert.Equal(new[] { root.Route, file.Route }, evidence.RetainedPaths.AsEnumerable());
        Assert.Equal(new[] { file.Route }, evidence.InconclusivePaths.AsEnumerable());
        Assert.Same(evidence, scratch.Cleanup());
        Assert.True(File.Exists(file.Route));
        var later = ownership.CreateOnlyFile(temp.Path, "later.bas", "later bytes"u8);
        Assert.Throws<InvalidOperationException>(() => scratch.Register(later));
        Assert.True(File.Exists(later.Route));
    }

    [Fact]
    public void RetainedChildDoesNotPreventCleanupOfAnIndependentOwnedSibling()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = TempDirectory.Create();
        using var ownership = new WindowsExactFileSystemObjectOwnershipFactory().Open();
        var root = Assert.IsType<ExactFileSystemObjectOwnership.DirectoryReceipt>(
            ownership.TryCreateOnlyDirectory(temp.Path, "owned"));
        var left = Assert.IsType<ExactFileSystemObjectOwnership.DirectoryReceipt>(
            ownership.TryCreateOnlyDirectory(root.Route, "left"));
        var right = Assert.IsType<ExactFileSystemObjectOwnership.DirectoryReceipt>(
            ownership.TryCreateOnlyDirectory(root.Route, "right"));
        var retainedFile = ownership.CreateOnlyFile(left, "retained.bas", "original"u8);
        var removedFile = ownership.CreateOnlyFile(right, "removed.bas", "owned"u8);
        foreach (var directory in new[] { root, left, right }) ownership.ReleaseCreationFence(directory);
        File.WriteAllText(retainedFile.Route, "changed content");
        var scratch = new InvocationScratch(ownership);
        foreach (var directory in new[] { root, left, right }) scratch.Register(directory);
        scratch.Register(retainedFile);
        scratch.Register(removedFile);

        var evidence = scratch.Cleanup();

        Assert.Equal(InvocationScratchCleanupStatus.Retained, evidence.Status);
        Assert.Equal("changed content", File.ReadAllText(retainedFile.Route));
        Assert.False(Directory.Exists(right.Route));
        Assert.Equal(new[] { root.Route, left.Route, retainedFile.Route }, evidence.RetainedPaths.AsEnumerable());
        Assert.Empty(evidence.InconclusivePaths);
    }

    [Fact]
    public void CleanupRemovesFilesAndDeepestOwnedDirectoriesBeforeTheirParents()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = TempDirectory.Create();
        using var ownership = new WindowsExactFileSystemObjectOwnershipFactory().Open();
        var root = Assert.IsType<ExactFileSystemObjectOwnership.DirectoryReceipt>(
            ownership.TryCreateOnlyDirectory(temp.Path, "owned"));
        var child = Assert.IsType<ExactFileSystemObjectOwnership.DirectoryReceipt>(
            ownership.TryCreateOnlyDirectory(root.Route, "child"));
        var file = ownership.CreateOnlyFile(child, "source.bas", "owned bytes"u8);
        ownership.ReleaseCreationFence(child);
        ownership.ReleaseCreationFence(root);
        var scratch = new InvocationScratch(ownership);
        scratch.Register(root);
        scratch.Register(child);
        scratch.Register(file);
        var proofs = new List<string>();

        var evidence = scratch.Cleanup(proofs.Add);

        Assert.Equal(InvocationScratchCleanupStatus.Removed, evidence.Status);
        Assert.Empty(evidence.RetainedPaths);
        Assert.Empty(evidence.InconclusivePaths);
        Assert.Equal(new[] { file.Route, child.Route, root.Route }, proofs);
        Assert.False(Directory.Exists(root.Route));
        Assert.Same(evidence, scratch.Cleanup());
        Assert.Equal(3, proofs.Count);
    }
}
