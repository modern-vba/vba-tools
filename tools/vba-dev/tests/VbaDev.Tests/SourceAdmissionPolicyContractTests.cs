using System.Text;
using VbaDev.App.Workbooks;
using VbaDev.Domain;
using Xunit;

namespace VbaDev.Tests;

public sealed class SourceAdmissionPolicyContractTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void LiveAndCapturedProfilesChooseTheSameRelevantFailureFromMultipleDefects(
        bool reverseInventory,
        bool publishFirst)
    {
        using var temp = TempDirectory.Create();
        var testOnlyPath = Path.Combine(temp.Path, "ATestOnly.frm");
        var testOnlySidecarPath = Path.ChangeExtension(testOnlyPath, ".frx");
        var selectedPath = Path.Combine(temp.Path, "BSelected.frm");
        var selectedSidecarPath = Path.ChangeExtension(selectedPath, ".frx");
        var laterPath = Path.Combine(temp.Path, "CSelected.bas");
        File.WriteAllBytes(testOnlyPath, [0xef, 0xbb, 0xbf, 0xff]);
        File.WriteAllBytes(testOnlySidecarPath, [0, 1, 255]);
        File.WriteAllText(selectedPath,
            "VERSION 5.00\r\nBegin VB.Form BSelected\r\nEnd\r\nAttribute VB_Name = \"BSelected\"\r\n");
        File.WriteAllBytes(selectedSidecarPath, [2, 3, 254]);
        File.WriteAllBytes(laterPath, [0xef, 0xbb, 0xbf, 0xff]);
        string[] paths = [testOnlyPath, testOnlySidecarPath, selectedPath, selectedSidecarPath, laterPath];
        var inventory = reverseInventory ? paths.Reverse().ToArray() : paths;
        InstalledCommonModule[] installed =
        [
            new("Missing", "Missing.bas", Requested: true, TestOnly: false),
            new("ATestOnly", "ATestOnly.frm", Requested: false, TestOnly: true, Orphaned: true)
        ];
        var readFailures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [testOnlySidecarPath] = "excluded sidecar failed",
            [selectedSidecarPath] = "selected sidecar failed"
        };
        var liveBuild = new AdmissionObservation(inventory, readFailures);
        var livePublish = new AdmissionObservation(inventory, readFailures);
        var doctor = new AdmissionObservation(inventory, readFailures);

        var buildFailure = AssertAdmissionFails<InvalidOperationException>(() =>
            liveBuild.Admission.AdmitProjectBuild(temp.Path, installed));
        var publishFailure = AssertAdmissionFails<IOException>(() =>
            livePublish.Admission.AdmitProjectPublish(temp.Path, installed));
        var capture = doctor.Admission.BeginDoctorRun().CaptureDocument(temp.Path);
        InvalidOperationException capturedBuildFailure;
        IOException capturedPublishFailure;
        if (publishFirst)
        {
            capturedPublishFailure = AssertAdmissionFails<IOException>(() => capture.AdmitProjectPublish(installed));
            capturedBuildFailure = AssertAdmissionFails<InvalidOperationException>(() => capture.AdmitProjectBuild(installed));
        }
        else
        {
            capturedBuildFailure = AssertAdmissionFails<InvalidOperationException>(() => capture.AdmitProjectBuild(installed));
            capturedPublishFailure = AssertAdmissionFails<IOException>(() => capture.AdmitProjectPublish(installed));
        }

        Assert.Contains(testOnlyPath, buildFailure.Message, StringComparison.Ordinal);
        Assert.Contains("strictly decoded as utf8bom", buildFailure.Message, StringComparison.Ordinal);
        Assert.Equal(buildFailure.Message, capturedBuildFailure.Message);
        Assert.Equal("selected sidecar failed", publishFailure.Message);
        Assert.Equal(publishFailure.Message, capturedPublishFailure.Message);
        Assert.Equal([testOnlyPath], liveBuild.ReadPaths);
        Assert.Equal([selectedPath, selectedSidecarPath], livePublish.ReadPaths);
        Assert.Equal(paths.Order(StringComparer.Ordinal), doctor.ReadPaths.Order(StringComparer.Ordinal));
        Assert.All(new[] { liveBuild, livePublish, doctor }, observation =>
        {
            Assert.Equal(1, observation.CodePageReads);
            Assert.Equal(1, observation.Inventories);
        });
    }

    [Fact]
    public void LiveAndCapturedProfilesKeepTheSameOrderedSourceFactsAfterAuthoringFilesDisappear()
    {
        using var temp = TempDirectory.Create();
        var orphan = WriteSource(temp.Path, "ZOrphan.bas", "ZOrphan", VbaSourceKind.StandardModule,
            "Attribute VB_Name = \"ZOrphan\"\r\n'#ExcludePublish\r\nOption Explicit\r\n");
        var testOnly = WriteSource(temp.Path, "ATest.frm", "ATest", VbaSourceKind.Form,
            "VERSION 5.00\r\nBegin VB.Form ATest\r\nEnd\r\nAttribute VB_Name = \"ATest\"\r\nOption Explicit\r\n",
            sidecarBytes: [0, 1, 255]);
        var runtime = WriteSource(temp.Path, "ARuntime.bas", "ARuntime", VbaSourceKind.StandardModule,
            "Attribute VB_Name = \"ARuntime\"\r\n'#ExcludePublish\r\nOption Explicit\r\n");
        var local = WriteSource(temp.CreateDirectory("nested"), "aLocal.bas", "ALocal", VbaSourceKind.StandardModule,
            "Attribute VB_Name = \"ALocal\"\r\n' caf\u00e9\r\nOption Explicit\r\n", utf8Bom: true);
        var excluded = WriteSource(temp.Path, "bExcluded.frm", "BExcluded", VbaSourceKind.Form,
            "VERSION 5.00\r\nBegin VB.Form BExcluded\r\nEnd\r\nAttribute VB_Name = \"BExcluded\"\r\n'#ExcludePublish\r\n",
            sidecarBytes: [7, 8, 254]);
        var localClass = WriteSource(temp.Path, "MClass.cls", "MClass", VbaSourceKind.ClassModule,
            "VERSION 1.0 CLASS\r\nBEGIN\r\n  MultiUse = -1\r\nEND\r\nAttribute VB_Name = \"MClass\"\r\nOption Explicit\r\n");
        var localForm = WriteSource(temp.Path, "zLocal.frm", "ZLocal", VbaSourceKind.Form,
            "VERSION 5.00\r\nBegin VB.Form ZLocal\r\nEnd\r\nAttribute VB_Name = \"ZLocal\"\r\nOption Explicit\r\n",
            sidecarBytes: []);
        var testOnlySidecar = Path.ChangeExtension(testOnly.SourcePath, ".frx");
        var excludedSidecar = Path.ChangeExtension(excluded.SourcePath, ".frx");
        var localSidecar = Path.ChangeExtension(localForm.SourcePath, ".frx");
        string[] inventory =
        [
            localForm.SourcePath, testOnlySidecar, excluded.SourcePath, orphan.SourcePath,
            local.SourcePath, localSidecar, testOnly.SourcePath, localClass.SourcePath,
            runtime.SourcePath, excludedSidecar
        ];
        InstalledCommonModule[] installed =
        [
            new("Missing", "Missing.bas", Requested: true, TestOnly: false),
            new("ZOrphan", "ZOrphan.bas", Requested: false, TestOnly: false, Orphaned: true),
            new("ATest", "ATest.frm", Requested: true, TestOnly: true),
            new("ARuntime", "ARuntime.bas", Requested: false, TestOnly: false)
        ];
        var noReadFailures = new Dictionary<string, string>();
        var liveBuild = new AdmissionObservation(inventory, noReadFailures);
        var livePublish = new AdmissionObservation(inventory, noReadFailures);
        var doctor = new AdmissionObservation(inventory, noReadFailures);

        var build = liveBuild.Admission.AdmitProjectBuild(temp.Path, installed);
        var publish = livePublish.Admission.AdmitProjectPublish(temp.Path, installed);
        var capture = doctor.Admission.BeginDoctorRun().CaptureDocument(temp.Path);
        foreach (var path in inventory)
        {
            File.Delete(path);
        }
        var capturedPublish = capture.AdmitProjectPublish(installed);
        var capturedBuild = capture.AdmitProjectBuild(installed);

        ExpectedSource[] expectedBuild = [orphan, testOnly, runtime, local, excluded, localClass, localForm];
        ExpectedSource[] expectedPublish = [orphan, runtime, local, localClass, localForm];
        AssertSourceFacts(build, expectedBuild);
        AssertSourceFacts(capturedBuild, expectedBuild);
        AssertSourceFacts(publish, expectedPublish);
        AssertSourceFacts(capturedPublish, expectedPublish);
        Assert.Equal(
            [local.SourcePath, runtime.SourcePath, testOnly.SourcePath, testOnlySidecar,
                excluded.SourcePath, excludedSidecar, localClass.SourcePath,
                localForm.SourcePath, localSidecar, orphan.SourcePath],
            liveBuild.ReadPaths);
        Assert.Equal(
            [local.SourcePath, runtime.SourcePath, excluded.SourcePath, localClass.SourcePath,
                localForm.SourcePath, localSidecar, orphan.SourcePath],
            livePublish.ReadPaths);
        Assert.Equal(inventory.Order(StringComparer.Ordinal), doctor.ReadPaths.Order(StringComparer.Ordinal));
        Assert.All(new[] { liveBuild, livePublish, doctor }, observation =>
        {
            Assert.Equal(1, observation.CodePageReads);
            Assert.Equal(1, observation.Inventories);
        });
    }

    private static ExpectedSource WriteSource(
        string root,
        string fileName,
        string moduleName,
        VbaSourceKind kind,
        string text,
        bool utf8Bom = false,
        byte[]? sidecarBytes = null)
    {
        var path = Path.Combine(root, fileName);
        var bytes = utf8Bom
            ? new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.UTF8.GetBytes(text)).ToArray()
            : Encoding.ASCII.GetBytes(text);
        File.WriteAllBytes(path, bytes);
        if (sidecarBytes is not null)
        {
            File.WriteAllBytes(Path.ChangeExtension(path, ".frx"), sidecarBytes);
        }
        return new ExpectedSource(path, moduleName, kind, text, bytes,
            utf8Bom ? "utf8bom" : "windows-1252", sidecarBytes);
    }

    private static void AssertSourceFacts(AdmittedVbaSourceSet actual, IReadOnlyList<ExpectedSource> expected)
    {
        Assert.Equal(1252, actual.ActiveCodePage);
        Assert.Equal(expected.Select(source => source.SourcePath), actual.Sources.Select(source => source.SourcePath));
        for (var index = 0; index < expected.Count; index++)
        {
            var source = actual.Sources[index];
            var original = expected[index];
            Assert.Equal(Path.GetFileName(original.SourcePath), source.FileName);
            Assert.Equal(original.SourcePath, source.DiagnosticSourcePath);
            Assert.Equal(original.Kind, source.Kind);
            Assert.Equal(original.Text, source.Text);
            Assert.Equal(original.EncodingToken, source.OriginalEncoding);
            Assert.Equal(original.Bytes, source.OriginalBytes.ToArray());
            Assert.True(source.ModuleIdentityAuthority.IsAuthoritative);
            Assert.Equal(original.ModuleName, source.ModuleIdentityAuthority.Name);
            Assert.Null(source.ModuleIdentityAuthority.Failure);
            Assert.Equal(new Uri(original.SourcePath).AbsoluteUri, source.Syntax.Uri);
            Assert.Equal(original.Text, source.Syntax.Text);
            Assert.Empty(source.Syntax.Diagnostics);
            Assert.Equal(original.ModuleName, source.Projection.ModuleName);
            if (original.SidecarBytes is null)
            {
                Assert.Null(source.BinaryPath);
                Assert.Null(source.BinaryBytes);
            }
            else
            {
                Assert.Equal(Path.ChangeExtension(original.SourcePath, ".frx"), source.BinaryPath);
                Assert.True(source.BinaryBytes.HasValue);
                Assert.Equal(original.SidecarBytes, source.BinaryBytes!.Value.ToArray());
            }
        }
    }

    private sealed record ExpectedSource(
        string SourcePath,
        string ModuleName,
        VbaSourceKind Kind,
        string Text,
        byte[] Bytes,
        string EncodingToken,
        byte[]? SidecarBytes);

    private static TException AssertAdmissionFails<TException>(Func<AdmittedVbaSourceSet> admit)
        where TException : Exception
    {
        AdmittedVbaSourceSet? result = null;
        var failure = Assert.Throws<TException>(() => { result = admit(); });
        Assert.Null(result);
        return failure;
    }

    private sealed class AdmissionObservation
    {
        internal AdmissionObservation(
            IReadOnlyList<string> inventory,
            IReadOnlyDictionary<string, string> readFailures)
        {
            Admission = new VbaSourceAdmission(
                () => { CodePageReads++; return 1252; },
                inventory: _ => { Inventories++; return inventory; },
                readAllBytes: path =>
                {
                    ReadPaths.Add(path);
                    if (readFailures.TryGetValue(path, out var message))
                    {
                        throw new IOException(message);
                    }
                    return File.ReadAllBytes(path);
                });
        }

        internal VbaSourceAdmission Admission { get; }
        internal List<string> ReadPaths { get; } = [];
        internal int CodePageReads { get; private set; }
        internal int Inventories { get; private set; }
    }
}
