using System.Text;
using VbaDev.App.Build;
using Xunit;

namespace VbaDev.Tests;

public sealed class BuildSourceSnapshotCaptureTests
{
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
        var capture = new BuildSourceSnapshotCaptureFactory(scratchRoot)
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
    public void CopyFailureRemovesOnlyInvocationOwnedScratch()
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
        var factory = new BuildSourceSnapshotCaptureFactory(scratchRoot);

        Assert.Throws<IOException>(() =>
            factory.Create(snapshotPath, CancellationToken.None));

        sourceLock.Position = 0;
        var actualSourceBytes = new byte[sourceLock.Length];
        sourceLock.ReadExactly(actualSourceBytes);
        Assert.Equal(sourceBytes, actualSourceBytes);
        Assert.Equal("caller-owned", File.ReadAllText(sentinelPath, Encoding.UTF8));
        Assert.Empty(Directory.EnumerateDirectories(scratchRoot));
    }
}
