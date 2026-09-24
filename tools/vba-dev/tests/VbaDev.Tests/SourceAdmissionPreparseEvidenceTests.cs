using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VbaDev.App.Workbooks;
using VbaDev.Domain;
using Xunit;

namespace VbaDev.Tests;

public sealed class SourceAdmissionPreparseEvidenceTests
{
    [Fact]
    public void PreparseReceiptIsDurableBeforeCompletionAndContainsOnlyInputIdentity()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "日本語.bas");
        var bytes = Encoding.UTF8.GetBytes("Attribute VB_Name = \"Module1\"\n' private-source-sentinel\n");
        var evidence = new SourceAdmissionPreparseEvidence(temp.Path, "test-run", maxRecords: 2);

        var receipt = evidence.Begin(path, VbaSourceKind.StandardModule, bytes,
            "utf8", 65001, 68, "ProjectBuild");

        Assert.NotNull(receipt);
        var session = Assert.Single(Directory.GetDirectories(
            Assert.Single(Directory.GetDirectories(temp.Path))));
        var prepared = Assert.Single(Directory.GetFiles(session, "*-preparse.json"));
        Assert.Empty(Directory.GetFiles(session, "*-complete.json"));
        var raw = File.ReadAllText(prepared);
        Assert.DoesNotContain("private-source-sentinel", raw, StringComparison.Ordinal);
        using (var record = JsonDocument.Parse(raw))
        {
            var root = record.RootElement;
            Assert.Equal("vba-source-preparse", root.GetProperty("kind").GetString());
            Assert.Equal(Environment.ProcessId, root.GetProperty("processId").GetInt32());
            Assert.Equal("test-run", root.GetProperty("runId").GetString());
            Assert.Equal("ProjectBuild", root.GetProperty("admissionPurpose").GetString());
            Assert.Equal(path.Length, root.GetProperty("sourcePathCodeUnitLength").GetInt32());
            Assert.True(root.GetProperty("sourcePathCaptureComplete").GetBoolean());
            Assert.Equal(string.Concat(path.Select(character => ((int)character).ToString("X4"))),
                root.GetProperty("sourcePathUtf16Hex").GetString());
            Assert.Equal(bytes.Length, root.GetProperty("sourceByteLength").GetInt32());
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)),
                root.GetProperty("sourceSha256").GetString());
            Assert.Equal("utf8", root.GetProperty("encodingToken").GetString());
            Assert.Equal(65001, root.GetProperty("activeCodePage").GetInt32());
        }

        receipt.Complete();

        var completed = Assert.Single(Directory.GetFiles(session, "*-complete.json"));
        using var completion = JsonDocument.Parse(File.ReadAllText(completed));
        Assert.Equal("parse-complete", completion.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void LiveAdmissionRecordsItsCapturedBytesWithoutReadingSourceAgain()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "Module1.bas");
        var bytes = Encoding.UTF8.GetBytes("Attribute VB_Name = \"Module1\"\nPublic Sub Run()\nEnd Sub\n");
        File.WriteAllBytes(path, bytes);
        var reads = 0;
        var admission = new VbaSourceAdmission(() => 65001,
            readAllBytes: candidate => { reads++; return File.ReadAllBytes(candidate); },
            preparseEvidence: new SourceAdmissionPreparseEvidence(temp.Path, "test-run"));

        var admitted = admission.AdmitExplicitImport(temp.Path);

        Assert.Single(admitted.Sources);
        Assert.Equal(1, reads);
        var session = Assert.Single(Directory.GetDirectories(
            Path.Combine(temp.Path, "source-admission")));
        var prepared = Assert.Single(Directory.GetFiles(session, "*-preparse.json"));
        Assert.Single(Directory.GetFiles(session, "*-complete.json"));
        using var record = JsonDocument.Parse(File.ReadAllText(prepared));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)),
            record.RootElement.GetProperty("sourceSha256").GetString());
    }

    [Fact]
    public void InvalidDiagnosticDestinationCannotChangeAdmissionResult()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "Module1.bas");
        File.WriteAllText(path, "Attribute VB_Name = \"Module1\"\n");
        var unavailable = Path.Combine(temp.Path, "absent-diagnostic-root");
        var admission = new VbaSourceAdmission(() => 65001,
            preparseEvidence: new SourceAdmissionPreparseEvidence(unavailable, "test-run"));

        var admitted = admission.AdmitExplicitImport(temp.Path);

        Assert.Single(admitted.Sources);
        Assert.False(Directory.Exists(unavailable));
    }

    [Fact]
    public void InvalidDiagnosticDestinationDoesNotReplaceTheOriginalAdmissionFailure()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "UserForm1.frm");
        File.WriteAllText(path,
            "VERSION 5.00\nBegin VB.UserForm UserForm1\nEnd\nAttribute VB_Name = \"UserForm1\"\n");
        File.WriteAllBytes(Path.Combine(temp.Path, "UserForm1.frx"), [1, 2, 3]);
        var unavailable = Path.Combine(temp.Path, "absent-diagnostic-root");
        var admission = new VbaSourceAdmission(() => 65001,
            readAllBytes: candidate => candidate.EndsWith(".frx", StringComparison.OrdinalIgnoreCase)
                ? throw new IOException("sidecar-sentinel") : File.ReadAllBytes(candidate),
            preparseEvidence: new SourceAdmissionPreparseEvidence(unavailable, "test-run"));

        var failure = Assert.Throws<IOException>(() => admission.AdmitExplicitImport(temp.Path));

        Assert.Equal("sidecar-sentinel", failure.Message);
        Assert.False(Directory.Exists(unavailable));
    }

    [Fact]
    public void DoctorCaptureRecordsPreparseWithoutASecondSourceRead()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "Module1.bas");
        File.WriteAllText(path, "Attribute VB_Name = \"Module1\"\n");
        var reads = 0;
        var admission = new VbaSourceAdmission(() => 65001,
            readAllBytes: candidate => { reads++; return File.ReadAllBytes(candidate); },
            preparseEvidence: new SourceAdmissionPreparseEvidence(temp.Path, "test-run"));

        var captured = admission.BeginDoctorRun().CaptureDocument(temp.Path);
        var selected = captured.AdmitProjectBuild([]);

        Assert.Single(selected.Sources);
        Assert.Equal(1, reads);
        var session = Assert.Single(Directory.GetDirectories(
            Path.Combine(temp.Path, "source-admission")));
        var prepared = Assert.Single(Directory.GetFiles(session, "*-preparse.json"));
        Assert.Single(Directory.GetFiles(session, "*-complete.json"));
        using var record = JsonDocument.Parse(File.ReadAllText(prepared));
        Assert.Equal("DoctorCapture", record.RootElement.GetProperty("admissionPurpose").GetString());
    }

    [Fact]
    public void CaptureStopsAtItsPerProcessBound()
    {
        using var temp = TempDirectory.Create();
        var evidence = new SourceAdmissionPreparseEvidence(temp.Path, "test-run", maxRecords: 1);
        var path = Path.Combine(temp.Path, "Module1.bas");

        Assert.NotNull(evidence.Begin(path, VbaSourceKind.StandardModule, [1, 2, 3],
            "utf8", 65001, 0, "ProjectBuild"));
        var secondAdmission = new SourceAdmissionPreparseEvidence(temp.Path, "test-run", maxRecords: 1);
        Assert.Null(secondAdmission.Begin(path, VbaSourceKind.StandardModule, [1, 2, 3],
            "utf8", 65001, 0, "ProjectBuild"));
        var session = Assert.Single(Directory.GetDirectories(
            Path.Combine(temp.Path, "source-admission")));
        Assert.Single(Directory.GetFiles(session, "*-preparse.json"));
    }
}
