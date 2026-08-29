using VbaDev.App.CommonModules;
using Xunit;

namespace VbaDev.Tests;

public sealed class CommonModulesSourceMutationWriterTests
{
    [Fact]
    public void PreflightConflictBeforeFirstMutationPreservesEveryTarget()
    {
        using var temp = TempDirectory.Create();
        var first = Path.Combine(temp.Path, "First.bas");
        var second = Path.Combine(temp.Path, "Second.bas");
        File.WriteAllBytes(first, [1]);
        File.WriteAllBytes(second, [2]);
        var plan = new[]
        {
            Replace(first, [1], [11]),
            Replace(second, [2], [22])
        };
        File.WriteAllBytes(second, [99]);

        var error = Assert.Throws<CommonModulesSourceMutationException>(() =>
            new CommonModulesSourceMutationWriter().Execute(plan, CancellationToken.None));

        Assert.False(error.SourceMutationCommitted);
        Assert.Equal([1], File.ReadAllBytes(first));
        Assert.Equal([99], File.ReadAllBytes(second));
        Assert.Equal([Path.GetFullPath(first), Path.GetFullPath(second)], error.ManualVerificationPaths);
    }

    [Fact]
    public void LaterConflictPreservesExternalBytesAndReportsEveryVerificationPath()
    {
        using var temp = TempDirectory.Create();
        var first = Path.Combine(temp.Path, "First.bas");
        var second = Path.Combine(temp.Path, "Second.bas");
        File.WriteAllBytes(first, [1]);
        File.WriteAllBytes(second, [2]);
        var plan = new[]
        {
            Replace(first, [1], [11]),
            Replace(second, [2], [22])
        };
        var writer = new CommonModulesSourceMutationWriter(beforeOperation: index =>
        {
            if (index == 1)
            {
                File.WriteAllBytes(second, [99]);
            }
        });

        var error = Assert.Throws<CommonModulesSourceMutationException>(() =>
            writer.Execute(plan, CancellationToken.None));

        Assert.True(error.SourceMutationCommitted);
        Assert.Equal([11], File.ReadAllBytes(first));
        Assert.Equal([99], File.ReadAllBytes(second));
        Assert.Equal([Path.GetFullPath(first), Path.GetFullPath(second)], error.ManualVerificationPaths);
        Assert.Contains(Path.GetFullPath(second), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CancellationBeforeCommitmentBoundaryLeavesTargetsUnchanged()
    {
        using var temp = TempDirectory.Create();
        var target = Path.Combine(temp.Path, "Feature.bas");
        File.WriteAllBytes(target, [1]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            new CommonModulesSourceMutationWriter().Execute(
                [Replace(target, [1], [2])],
                cancellation.Token));

        Assert.Equal([1], File.ReadAllBytes(target));
    }

    [Fact]
    public void CancellationDuringFinalPreconditionReadLeavesTargetsUnchanged()
    {
        using var temp = TempDirectory.Create();
        var target = Path.Combine(temp.Path, "Feature.bas");
        File.WriteAllBytes(target, [1]);
        using var cancellation = new CancellationTokenSource();
        var writer = new CommonModulesSourceMutationWriter(beforeCommitment: _ =>
            cancellation.Cancel());

        Assert.ThrowsAny<OperationCanceledException>(() =>
            writer.Execute([Replace(target, [1], [2])], cancellation.Token));

        Assert.Equal([1], File.ReadAllBytes(target));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*.vba-dev.*.tmp"));
    }

    [Fact]
    public void StagingFailureBeforeAtomicReplacementDoesNotCrossCommitmentBoundary()
    {
        using var temp = TempDirectory.Create();
        var target = Path.Combine(temp.Path, "Feature.bas");
        File.WriteAllBytes(target, [1]);
        var writer = new CommonModulesSourceMutationWriter(
            afterTemporaryFileFlushed: _ => throw new IOException("staging failed"));

        var error = Assert.Throws<CommonModulesSourceMutationException>(() =>
            writer.Execute([Replace(target, [1], [2])], CancellationToken.None));

        Assert.False(error.SourceMutationCommitted);
        Assert.Equal([1], File.ReadAllBytes(target));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*.vba-dev.*.tmp"));
    }

    [Fact]
    public void TemporaryWriteFailureIsNotRetriedAndCleansTheOwnedFile()
    {
        using var temp = TempDirectory.Create();
        var target = Path.Combine(temp.Path, "Feature.bas");
        File.WriteAllBytes(target, [1]);
        var writeAttempts = 0;
        var writer = new CommonModulesSourceMutationWriter(
            persistTemporaryFile: (stream, _) =>
            {
                writeAttempts++;
                stream.WriteByte(99);
                throw new IOException("write failed");
            });

        var error = Assert.Throws<CommonModulesSourceMutationException>(() =>
            writer.Execute([Replace(target, [1], [2])], CancellationToken.None));

        Assert.False(error.SourceMutationCommitted);
        Assert.Equal(1, writeAttempts);
        Assert.Equal([1], File.ReadAllBytes(target));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*.vba-dev.*.tmp"));
    }

    [Fact]
    public void TemporaryCleanupFailureReportsTheRetainedOwnedPath()
    {
        using var temp = TempDirectory.Create();
        var target = Path.Combine(temp.Path, "Feature.bas");
        File.WriteAllBytes(target, [1]);
        var writer = new CommonModulesSourceMutationWriter(
            persistTemporaryFile: (_, _) => throw new IOException("write failed"),
            deleteTemporaryFile: _ => throw new IOException("cleanup failed"));

        var error = Assert.Throws<CommonModulesSourceMutationException>(() =>
            writer.Execute([Replace(target, [1], [2])], CancellationToken.None));

        Assert.False(error.SourceMutationCommitted);
        var retained = Assert.Single(error.ManualVerificationPaths, path =>
            path.Contains(".vba-dev.", StringComparison.Ordinal)
            && path.EndsWith(".tmp", StringComparison.Ordinal));
        Assert.True(File.Exists(retained));
        Assert.Contains("write failed", error.Message, StringComparison.Ordinal);
        Assert.Contains(retained, error.Message, StringComparison.Ordinal);
        File.Delete(retained);
    }

    [Fact]
    public void CancellationAfterFirstReplacementIsDeferredThroughTheCompletePlan()
    {
        using var temp = TempDirectory.Create();
        var first = Path.Combine(temp.Path, "First.bas");
        var second = Path.Combine(temp.Path, "Second.bas");
        File.WriteAllBytes(first, [1]);
        File.WriteAllBytes(second, [2]);
        using var cancellation = new CancellationTokenSource();
        var writer = new CommonModulesSourceMutationWriter(beforeOperation: index =>
        {
            if (index == 1)
            {
                cancellation.Cancel();
            }
        });

        var result = writer.Execute(
            [Replace(first, [1], [11]), Replace(second, [2], [22])],
            cancellation.Token);

        Assert.True(result.SourceMutationCommitted);
        Assert.True(result.CancellationDeferred);
        Assert.Equal([11], File.ReadAllBytes(first));
        Assert.Equal([22], File.ReadAllBytes(second));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*.vba-dev.*.tmp"));
    }

    [Fact]
    public void ExistingTargetUsesExactObservedBytesEvenWhenOverwriteWasAuthorized()
    {
        using var temp = TempDirectory.Create();
        var target = Path.Combine(temp.Path, "Feature.bas");
        File.WriteAllBytes(target, [1]);
        var plan = Replace(target, [1], [2]);
        File.WriteAllBytes(target, [3]);

        var error = Assert.Throws<CommonModulesSourceMutationException>(() =>
            new CommonModulesSourceMutationWriter().Execute([plan], CancellationToken.None));

        Assert.False(error.SourceMutationCommitted);
        Assert.Equal([3], File.ReadAllBytes(target));
    }

    [Fact]
    public void CaseOnlyRecaseRefreshesTheFinalBasenameAndBytes()
    {
        using var temp = TempDirectory.Create();
        var observed = Path.Combine(temp.Path, "feature.bas");
        var canonical = Path.Combine(temp.Path, "Feature.bas");
        File.WriteAllBytes(observed, [1]);

        var result = new CommonModulesSourceMutationWriter().Execute(
            [new CommonModulesSourceFileMutation(
                observed,
                canonical,
                CommonModulesExpectedFile.Present([1]),
                DesiredBytes: [2])],
            CancellationToken.None);

        Assert.True(result.SourceMutationCommitted);
        Assert.Equal([2], File.ReadAllBytes(canonical));
        Assert.Contains(
            Directory.EnumerateFiles(temp.Path).Select(Path.GetFileName),
            name => name == "Feature.bas");
    }

    private static CommonModulesSourceFileMutation Replace(
        string path,
        byte[] expected,
        byte[] desired)
        => new(
            path,
            path,
            CommonModulesExpectedFile.Present(expected),
            desired);
}
