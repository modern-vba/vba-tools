using VbaDev.Infrastructure.FileSystem;
using System.Text;
using System.Runtime.InteropServices;
using VbaDev.App.Build;
using VbaDev.App.FileSystem;
using VbaDev.App.Workbooks;
using Xunit;

namespace VbaDev.Tests;

public sealed class BuildSourceSnapshotCaptureTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PreparationFailurePreservesPrimaryAndReportsIndependentCleanup(bool cancelled, bool retained)
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var snapshot = CreateSource(temp);
        var scratchRoot = temp.CreateDirectory("scratch");
        Exception primary = cancelled ? new OperationCanceledException(cancellation.Token) : new IOException("Copy preparation failed");
        string? copied = null;
        var factory = new BuildSourceSnapshotCaptureFactory(new WindowsExactFileSystemObjectOwnershipFactory(), scratchRoot,
            new VbaSourceAdmission(() => 65001), path =>
            {
                copied = path;
                if (retained) File.WriteAllText(path, "external content");
                if (cancelled) cancellation.Cancel();
                throw primary;
            });

        var error = Assert.ThrowsAny<Exception>(() => factory.Create(snapshot, cancellation.Token));

        if (retained)
        {
            var failure = Assert.IsType<BuildSourceSnapshotCaptureRetainedException>(error);
            Assert.Same(primary, failure.CaptureError);
            Assert.Equal(InvocationScratchCleanupStatus.Retained, failure.CleanupEvidence.Status);
            Assert.Contains(copied!, failure.CleanupEvidence.RetainedPaths);
            Assert.Contains(copied!, error.Message);
            Assert.Equal("external content", File.ReadAllText(copied!));
        }
        else
        {
            Assert.Same(primary, error);
            Assert.Empty(Directory.GetDirectories(scratchRoot));
        }
        Assert.Contains("Attribute VB_Name", File.ReadAllText(Path.Combine(snapshot, "Module1.bas")));
    }

    [Theory]
    [InlineData("replaced")]
    [InlineData("hard-link")]
    [InlineData("reparse")]
    public void CleanupPreservesUnprovedIdentity(string mutation)
    {
        using var temp = TempDirectory.Create();
        var capture = new BuildSourceSnapshotCaptureFactory(new WindowsExactFileSystemObjectOwnershipFactory(), temp.CreateDirectory("scratch"))
            .Create(CreateSource(temp), CancellationToken.None);
        var path = Assert.Single(capture.SourceFiles).SourcePath;
        var bytes = File.ReadAllBytes(path);
        var outside = Path.Combine(temp.Path, "outside.bas");
        if (mutation == "hard-link")
            Assert.True(CreateHardLink(outside, path, IntPtr.Zero));
        else
        {
            File.Move(path, outside);
            if (mutation == "reparse") File.CreateSymbolicLink(path, outside);
            else File.WriteAllBytes(path, bytes);
        }

        var evidence = capture.Cleanup();

        Assert.Equal(InvocationScratchCleanupStatus.Retained, evidence.Status);
        Assert.Contains(path, evidence.RetainedPaths);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(bytes, File.ReadAllBytes(outside));
        Assert.Same(evidence, capture.Cleanup());
        Assert.Throws<InvalidOperationException>(capture.Dispose);
    }

    [Fact]
    public void CleanupContinuesAfterCancellationAndReturnsStableRemovedEvidence()
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var capture = new BuildSourceSnapshotCaptureFactory(new WindowsExactFileSystemObjectOwnershipFactory(), temp.CreateDirectory("scratch"))
            .Create(CreateSource(temp), CancellationToken.None);

        var evidence = capture.Cleanup(_ => cancellation.Cancel());

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(InvocationScratchCleanupStatus.Removed, evidence.Status);
        Assert.Empty(evidence.RetainedPaths);
        Assert.False(Directory.Exists(capture.StagingPath));
        Assert.Same(evidence, capture.Cleanup());
        capture.Dispose();
    }

    [Fact]
    public async Task LockedCaptureReturnsBoundedStableInconclusiveEvidence()
    {
        using var temp = TempDirectory.Create();
        var capture = new BuildSourceSnapshotCaptureFactory(new WindowsExactFileSystemObjectOwnershipFactory(), temp.CreateDirectory("scratch"))
            .Create(CreateSource(temp), CancellationToken.None);
        var path = Assert.Single(capture.SourceFiles).SourcePath;
        InvocationScratchCleanupEvidence evidence;
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            evidence = await Task.Run(() => capture.Cleanup()).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(InvocationScratchCleanupStatus.Inconclusive, evidence.Status);
        Assert.Equal(new[] { path }, evidence.InconclusivePaths.AsEnumerable());
        Assert.Same(evidence, capture.Cleanup());
        Assert.True(File.Exists(path));
        Assert.Throws<InvalidOperationException>(capture.Dispose);
    }

    [Fact]
    public void SourceChangeBetweenAdmissionAndCopyCannotChangeCapturedBytes()
    {
        using var temp = TempDirectory.Create();
        var snapshot = CreateSource(temp);
        var laterPath = Path.Combine(snapshot, "Zeta.bas");
        var original = Encoding.UTF8.GetBytes("Attribute VB_Name = \"Zeta\"\n");
        File.WriteAllBytes(laterPath, original);
        using var capture = new BuildSourceSnapshotCaptureFactory(new WindowsExactFileSystemObjectOwnershipFactory(), temp.CreateDirectory("scratch"),
            new VbaSourceAdmission(() => 65001), _ => File.WriteAllText(laterPath, "caller mutation"))
            .Create(snapshot, CancellationToken.None);

        Assert.Equal(original, File.ReadAllBytes(Assert.Single(capture.SourceFiles, file => file.FileName == "Zeta.bas").SourcePath));
        Assert.Equal("caller mutation", File.ReadAllText(laterPath));
    }

    [Fact]
    public void ForeignDirectoryDuringCaptureIsNeverAdopted()
    {
        using var temp = TempDirectory.Create();
        var snapshot = CreateSource(temp);
        Directory.CreateDirectory(Path.Combine(snapshot, "nested"));
        File.WriteAllText(Path.Combine(snapshot, "nested", "Zeta.bas"), "Attribute VB_Name = \"Zeta\"\n");
        string? foreign = null;
        var factory = new BuildSourceSnapshotCaptureFactory(new WindowsExactFileSystemObjectOwnershipFactory(), temp.CreateDirectory("scratch"),
            new VbaSourceAdmission(() => 65001), path =>
            {
                var directory = Path.Combine(Path.GetDirectoryName(path)!, "nested");
                Directory.CreateDirectory(directory);
                foreign = Path.Combine(directory, "foreign.txt");
                File.WriteAllText(foreign, "keep");
            });

        var error = Assert.Throws<BuildSourceSnapshotCaptureRetainedException>(() => factory.Create(snapshot, CancellationToken.None));

        Assert.IsType<IOException>(error.CaptureError);
        Assert.Equal("keep", File.ReadAllText(foreign!));
        Assert.Contains(Path.GetDirectoryName(foreign!)!, error.Message);
        Assert.Equal(InvocationScratchCleanupStatus.Retained, error.CleanupEvidence.Status);
    }

    private static string CreateSource(TempDirectory temp)
    {
        var snapshot = temp.CreateDirectory("snapshot");
        File.WriteAllText(Path.Combine(snapshot, "Module1.bas"), "Attribute VB_Name = \"Module1\"\n");
        return snapshot;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string linkPath, string existingPath, IntPtr securityAttributes);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CleanupPreservesChangedOrForeignContent(bool foreign)
    {
        using var temp = TempDirectory.Create();
        var snapshot = temp.CreateDirectory("snapshot");
        File.WriteAllText(Path.Combine(snapshot, "Module1.bas"), "Attribute VB_Name = \"Module1\"\n");
        var capture = new BuildSourceSnapshotCaptureFactory(new WindowsExactFileSystemObjectOwnershipFactory(), temp.CreateDirectory("scratch"))
            .Create(snapshot, CancellationToken.None);
        var retainedPath = Path.Combine(capture.StagingPath, foreign ? "foreign.txt" : "Module1.bas");
        File.WriteAllText(retainedPath, "external content");

        var error = Assert.Throws<InvalidOperationException>(capture.Dispose);

        Assert.Equal("external content", File.ReadAllText(retainedPath));
        Assert.Contains(Path.GetFullPath(retainedPath), error.Message);
        Assert.True(File.Exists(Path.Combine(snapshot, "Module1.bas")));
    }

    [Fact]
    public void CaptureFixesRecursiveSourceAndSidecarBytesAndRemovesOwnedScratch()
    {
        using var temp = TempDirectory.Create();
        var snapshotPath = temp.CreateDirectory("snapshot");
        var modulePath = Path.Combine(snapshotPath, "nested", "Module1.bas");
        var classPath = Path.Combine(snapshotPath, "classes", "Feature.cls");
        var formPath = Path.Combine(snapshotPath, "forms", "Dialog.frm");
        var sidecarPath = Path.Combine(snapshotPath, "forms", "Dialog.frx");
        Directory.CreateDirectory(Path.GetDirectoryName(modulePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(classPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(formPath)!);
        var moduleBytes = new UTF8Encoding(false).GetBytes(
            "Attribute VB_Name = \"Module1\"\r\n");
        var formBytes = new UTF8Encoding(false).GetBytes(
            "VERSION 5.00\r\nBegin VB.Form Dialog\r\nEnd\r\n");
        var classBytes = new UTF8Encoding(false).GetBytes(
            "VERSION 1.0 CLASS\r\nAttribute VB_Name = \"Feature\"\r\n");
        byte[] sidecarBytes = [0, 1, 2, 255];
        File.WriteAllBytes(modulePath, moduleBytes);
        File.WriteAllBytes(classPath, classBytes);
        File.WriteAllBytes(formPath, formBytes);
        File.WriteAllBytes(sidecarPath, sidecarBytes);
        var scratchRoot = temp.CreateDirectory("scratch");
        var capture = new BuildSourceSnapshotCaptureFactory(new WindowsExactFileSystemObjectOwnershipFactory(), scratchRoot)
            .Create(snapshotPath, CancellationToken.None);
        var capturePath = capture.StagingPath;

        File.WriteAllText(modulePath, "caller mutation", Encoding.UTF8);
        File.Delete(formPath);
        File.Delete(sidecarPath);

        Assert.True(Directory.Exists(capturePath));
        var capturedModule = Assert.Single(
            capture.SourceFiles,
            source => source.FileName == "Module1.bas");
        Assert.Equal(moduleBytes, File.ReadAllBytes(capturedModule.SourcePath));
        Assert.Equal(
            Path.Combine("nested", "Module1.bas"),
            Path.GetRelativePath(capturePath, capturedModule.SourcePath));
        var capturedClass = Assert.Single(
            capture.SourceFiles,
            source => source.FileName == "Feature.cls");
        Assert.Equal(classBytes, File.ReadAllBytes(capturedClass.SourcePath));
        Assert.Equal(
            Path.Combine("classes", "Feature.cls"),
            Path.GetRelativePath(capturePath, capturedClass.SourcePath));
        var capturedForm = Assert.Single(
            capture.SourceFiles,
            source => source.FileName == "Dialog.frm");
        Assert.Equal(formBytes, File.ReadAllBytes(capturedForm.SourcePath));
        Assert.NotNull(capturedForm.BinaryPath);
        Assert.Equal(sidecarBytes, File.ReadAllBytes(capturedForm.BinaryPath));
        Assert.Equal(
            Path.Combine("forms", "Dialog.frx"),
            Path.GetRelativePath(capturePath, capturedForm.BinaryPath));

        capture.Dispose();

        Assert.False(Directory.Exists(capturePath));
        Assert.Equal("caller mutation", File.ReadAllText(modulePath, Encoding.UTF8));
        Assert.False(File.Exists(formPath));
        Assert.False(File.Exists(sidecarPath));
    }

    [Fact]
    public async Task ReadFailureIsRetainedForAnalysisAndCleanupRemovesOnlyInvocationOwnedScratch()
    {
        using var temp = TempDirectory.Create();
        var snapshotPath = temp.CreateDirectory("snapshot");
        var sourcePath = Path.Combine(snapshotPath, "Locked.bas");
        var sourceBytes = new UTF8Encoding(false).GetBytes(
            "Attribute VB_Name = \"Locked\"\r\n");
        File.WriteAllBytes(sourcePath, sourceBytes);
        var scratchRoot = temp.CreateDirectory("scratch");
        var sentinelPath = Path.Combine(scratchRoot, "caller-sentinel.txt");
        File.WriteAllText(sentinelPath, "caller-owned", Encoding.UTF8);
        using var sourceLock = File.Open(
            sourcePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        var factory = new BuildSourceSnapshotCaptureFactory(new WindowsExactFileSystemObjectOwnershipFactory(), scratchRoot);

        using (var capture = factory.Create(snapshotPath, CancellationToken.None))
        {
            var error = await Assert.ThrowsAsync<VbaSourceAnalysisException>(() => capture.AdmitAnalyzedAsync(
                (_, _) => Task.FromResult(VbaTools.Semantics.VbaProjectSemanticInputs.Empty), CancellationToken.None));
            Assert.False(error.Report.Complete);
            Assert.Equal(new Uri(sourcePath).AbsoluteUri, Assert.Single(error.Report.Failures).SourceUri);
        }

        sourceLock.Position = 0;
        var actualSourceBytes = new byte[sourceLock.Length];
        sourceLock.ReadExactly(actualSourceBytes);
        Assert.Equal(sourceBytes, actualSourceBytes);
        Assert.Equal("caller-owned", File.ReadAllText(sentinelPath, Encoding.UTF8));
        Assert.Empty(Directory.EnumerateDirectories(scratchRoot));
    }

    [Fact]
    public void CaptureOrdersTheFlatSourceInventoryDeterministically()
    {
        using var temp = TempDirectory.Create();
        var snapshotPath = temp.CreateDirectory("snapshot");
        File.WriteAllText(
            Path.Combine(snapshotPath, "Zeta.bas"),
            "Attribute VB_Name = \"Zeta\"\r\n",
            Encoding.UTF8);
        var nestedPath = Path.Combine(snapshotPath, "nested");
        Directory.CreateDirectory(nestedPath);
        File.WriteAllText(
            Path.Combine(nestedPath, "Alpha.bas"),
            "Attribute VB_Name = \"Alpha\"\r\n",
            Encoding.UTF8);
        var scratchRoot = temp.CreateDirectory("scratch");

        using var capture = new BuildSourceSnapshotCaptureFactory(new WindowsExactFileSystemObjectOwnershipFactory(), scratchRoot)
            .Create(snapshotPath, CancellationToken.None);

        Assert.Equal(
            ["Alpha.bas", "Zeta.bas"],
            capture.SourceFiles.Select(source => source.FileName));
    }
}
