using System.Text;
using VbaDev.App.CommonModules;
using Xunit;

namespace VbaDev.Tests;

public sealed class CommonModulesPackageSnapshotTests
{
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
    public void CaptureRejectsARepositoryGenerationThatChangesBeforeStabilityProof()
    {
        using var temp = TempDirectory.Create();
        var repository = temp.CreateDirectory("common_modules_repo");
        WriteManifest(repository, ("Feature.bas", "optional", string.Empty));
        WriteSource(repository, "Feature.bas", "feature generation one");
        var scratchRoot = temp.CreateDirectory("scratch");
        var factory = new CommonModulesPackageSnapshotFactory(
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
}
