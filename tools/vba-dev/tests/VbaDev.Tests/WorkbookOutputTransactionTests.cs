using VbaDev.App.Build;
using VbaDev.App.FileSystem;
using VbaDev.Infrastructure.FileSystem;
using Xunit;

namespace VbaDev.Tests;

public sealed class WorkbookOutputTransactionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapturedTemplateRemainsTheWholeGenerationInputAfterItsAuthoringFileChanges(bool remove)
    {
        using var temp = TempDirectory.Create();
        var (template, target) = CreatePaths(temp);
        var originalBytes = File.ReadAllBytes(template);
        var capture = CapturedWorkbookTemplate.Capture(template);
        if (remove) File.Delete(template);
        else File.WriteAllText(template, "a different package with different non-VBA content");

        using var transaction = WorkbookOutputTransaction.Create(
            new WindowsExactFileSystemObjectOwnershipFactory(), capture, target);

        Assert.Equal(originalBytes, File.ReadAllBytes(transaction.StagingWorkbookPath));
        Assert.Equal("previous", File.ReadAllText(target));
        transaction.Commit();
        Assert.Equal(originalBytes, File.ReadAllBytes(target));
        if (remove) Assert.False(File.Exists(template));
        else Assert.Equal("a different package with different non-VBA content", File.ReadAllText(template));
    }

    [Fact]
    public void CommitReplacesOnlyTheSelectedOutputAndSurvivesLaterDisposal()
    {
        using var temp = TempDirectory.Create();
        var (template, target) = CreatePaths(temp);
        var other = Path.Combine(temp.Path, "other.xlsm");
        File.WriteAllText(other, "unrelated");
        var transaction = WorkbookOutputTransaction.Create(new WindowsExactFileSystemObjectOwnershipFactory(), template, target);
        Assert.Equal("previous", File.ReadAllText(target));
        transaction.Commit();
        File.WriteAllText(transaction.StagingWorkbookPath, "foreign replacement after commit");
        transaction.Dispose();
        Assert.Equal("template", File.ReadAllText(target));
        Assert.Equal("unrelated", File.ReadAllText(other));
        Assert.Equal("foreign replacement after commit", File.ReadAllText(transaction.StagingWorkbookPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisposalRemovesOnlyTheCreatedOrProvedSavedVersion(bool saved)
    {
        using var temp = TempDirectory.Create();
        var (template, target) = CreatePaths(temp);
        var transaction = WorkbookOutputTransaction.Create(new WindowsExactFileSystemObjectOwnershipFactory(), template, target);
        if (saved)
        {
            File.WriteAllText(transaction.StagingWorkbookPath, "scenario saved workbook");
            transaction.CaptureSavedWorkbook();
            transaction.CompleteSavedCapture();
        }
        transaction.Dispose();
        Assert.False(File.Exists(transaction.StagingWorkbookPath));
        Assert.Equal("previous", File.ReadAllText(target));
        Assert.Equal(InvocationScratchCleanupStatus.Removed, transaction.CleanupEvidence!.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisposalPreservesAnUnobservedReplacementWorkbook(bool replace)
    {
        using var temp = TempDirectory.Create();
        var (template, target) = CreatePaths(temp);
        var transaction = WorkbookOutputTransaction.Create(new WindowsExactFileSystemObjectOwnershipFactory(), template, target);
        if (replace) File.Move(transaction.StagingWorkbookPath, Path.Combine(temp.Path, "original-stage.xlsm"));
        File.WriteAllText(transaction.StagingWorkbookPath, "external replacement");
        var error = Assert.Throws<BuildCommandException>(transaction.Dispose);
        Assert.Contains(transaction.StagingWorkbookPath, error.Message);
        Assert.Equal("external replacement", File.ReadAllText(transaction.StagingWorkbookPath));
        Assert.Equal("previous", File.ReadAllText(target));
        Assert.Equal(InvocationScratchCleanupStatus.Retained, transaction.CleanupEvidence!.Status);
    }

    [Fact]
    public void ChangedSavedCandidateCannotAcquireCommitOrCleanupAuthority()
    {
        using var temp = TempDirectory.Create();
        var (template, target) = CreatePaths(temp);
        var transaction = WorkbookOutputTransaction.Create(new WindowsExactFileSystemObjectOwnershipFactory(), template, target);
        File.WriteAllText(transaction.StagingWorkbookPath, "saved");
        transaction.CaptureSavedWorkbook();
        File.WriteAllText(transaction.StagingWorkbookPath, "external change after Save");
        Assert.Throws<BuildCommandException>(transaction.CompleteSavedCapture);
        Assert.Throws<BuildCommandException>(transaction.Commit);
        Assert.Throws<BuildCommandException>(transaction.Dispose);
        Assert.Equal("previous", File.ReadAllText(target));
        Assert.Equal("external change after Save", File.ReadAllText(transaction.StagingWorkbookPath));
    }

    [Fact]
    public async Task LockedStageProducesBoundedStableInconclusiveEvidence()
    {
        using var temp = TempDirectory.Create();
        var (template, target) = CreatePaths(temp);
        var transaction = WorkbookOutputTransaction.Create(new WindowsExactFileSystemObjectOwnershipFactory(), template, target);
        using (File.Open(transaction.StagingWorkbookPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Assert.ThrowsAsync<BuildCommandException>(() => Task.Run(transaction.Dispose).WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Contains(transaction.StagingWorkbookPath, error.Message);
        }
        var evidence = transaction.CleanupEvidence;
        Assert.Equal(InvocationScratchCleanupStatus.Inconclusive, evidence!.Status);
        transaction.Dispose();
        Assert.Same(evidence, transaction.CleanupEvidence);
        Assert.True(File.Exists(transaction.StagingWorkbookPath));
        Assert.Equal("previous", File.ReadAllText(target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedCreationPreservesPrimaryAndRetainedEvidence(bool changed)
    {
        using var temp = TempDirectory.Create();
        var (template, target) = CreatePaths(temp);
        var primary = new IOException("Creation observer failed");
        string? stage = null;
        var error = Assert.ThrowsAny<Exception>(() => WorkbookOutputTransaction.Create(
            new WindowsExactFileSystemObjectOwnershipFactory(), template, target, path =>
            {
                stage = path;
                if (changed) File.WriteAllText(path, "external change");
                throw primary;
            }));
        if (changed)
        {
            var failure = Assert.IsType<WorkbookStagingPreparationException>(error);
            Assert.Same(primary, failure.InnerException);
            Assert.Equal(InvocationScratchCleanupStatus.Retained, failure.CleanupEvidence.Status);
            Assert.Contains(stage!, failure.Message);
            Assert.Equal("external change", File.ReadAllText(stage!));
        }
        else
        {
            Assert.Same(primary, error);
            Assert.False(File.Exists(stage));
        }
        Assert.Equal("previous", File.ReadAllText(target));
    }

    [Fact]
    public void UnprovedProcessReleaseGrantsNoCleanupAuthority()
    {
        using var temp = TempDirectory.Create();
        var (template, target) = CreatePaths(temp);
        var transaction = WorkbookOutputTransaction.Create(new WindowsExactFileSystemObjectOwnershipFactory(), template, target);
        transaction.RetainWithoutCleanup();
        transaction.Dispose();
        Assert.True(File.Exists(transaction.StagingWorkbookPath));
        Assert.Null(transaction.CleanupEvidence);
        Assert.Equal("previous", File.ReadAllText(target));
    }

    private static (string Template, string Target) CreatePaths(TempDirectory temp)
    {
        var template = Path.Combine(temp.Path, "template.xlsm");
        var target = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(template, "template");
        File.WriteAllText(target, "previous");
        return (template, target);
    }
}
