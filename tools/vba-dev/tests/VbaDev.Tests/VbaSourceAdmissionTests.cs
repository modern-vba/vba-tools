using System.Text;
using System.Text.Json;
using VbaDev.App.Workbooks;
using VbaTools.Syntax;
using Xunit;

namespace VbaDev.Tests;

public sealed class VbaSourceAdmissionTests
{
    [Theory]
    [InlineData(new byte[] { 0xef, 0xbb, 0xbf }, "utf8bom")]
    [InlineData(new byte[] { 0xff, 0xfe }, "utf16le")]
    [InlineData(new byte[] { 0xfe, 0xff }, "utf16be")]
    public void CompleteBomWithEmptyPayloadRetainsInvalidIdentityForSourcePreflight(byte[] bytes, string encodingToken)
    {
        using var temp = TempDirectory.Create();
        File.WriteAllBytes(Path.Combine(temp.Path, "Empty.bas"), bytes);

        var admitted = new VbaSourceAdmission(() => 1252).Admit(temp.Path, VbaSourceAdmissionIntent.ExplicitImport);

        var source = Assert.Single(admitted.Sources);
        Assert.Empty(source.Text);
        Assert.Equal(encodingToken, source.OriginalEncoding);
        Assert.Equal(bytes, source.OriginalBytes.ToArray());
        Assert.False(source.ModuleIdentityAuthority.IsAuthoritative);
        using var mirror = VbeImportSourceSet.Create(admitted);
        var report = new WorkbookMaterializationNamePreflight().InspectSourcePhase(mirror.SourceFiles);
        Assert.True(report.LiveInspectionBlocked);
        Assert.Single(report.Findings);
    }

    [Fact]
    public void FactoryCapturesAcpOnceAndKeepsAllIdentityFindingsInSourceOrder()
    {
        using var temp = TempDirectory.Create();
        var missingPath = Path.Combine(temp.Path, "A_Missing.bas");
        var firstPath = Path.Combine(temp.Path, "B_First.bas");
        var secondPath = Path.Combine(temp.Path, "C_Second.bas");
        var invalidPath = Path.Combine(temp.Path, "Z_Invalid.bas");
        File.WriteAllText(missingPath, "Option Explicit\r\n");
        File.WriteAllText(firstPath, "Attribute VB_Name = \"SharedName\"\r\n");
        File.WriteAllText(secondPath, "Attribute VB_Name = \"SharedName\"\r\n");
        File.WriteAllText(invalidPath, "Attribute VB_Name = \"Option\"\r\n");
        var codePageReads = 0;
        var createdCalls = 0;
        VbeImportSourceSet? observed = null;
        var factory = new VbeImportSourceSetFactory(
            () => { codePageReads++; return 1252; },
            sourceSet => { createdCalls++; observed = sourceSet; });

        using var mirror = factory.CreateExplicitImport(temp.Path);
        var report = new WorkbookMaterializationNamePreflight().InspectSourcePhase(mirror.SourceFiles);

        Assert.Equal(1, codePageReads);
        Assert.Equal(1, createdCalls);
        Assert.Same(mirror, observed);
        Assert.True(report.LiveInspectionBlocked);
        Assert.Equal(3, report.Findings.Count);
        Assert.Contains(missingPath, report.Findings[0], StringComparison.Ordinal);
        Assert.Contains(firstPath, report.Findings[1], StringComparison.Ordinal);
        Assert.Contains(secondPath, report.Findings[1], StringComparison.Ordinal);
        Assert.Contains(invalidPath, report.Findings[2], StringComparison.Ordinal);
        Assert.Equal(4, mirror.Admission!.Sources.Length);
    }

    [Fact]
    public void DuplicateFlatSourceNamesFailBeforeAnySourceIsRead()
    {
        using var temp = TempDirectory.Create();
        var first = Path.Combine(temp.CreateDirectory("first"), "Duplicate.bas");
        var second = Path.Combine(temp.CreateDirectory("second"), "duplicate.bas");
        File.WriteAllText(first, "Attribute VB_Name = \"First\"\r\n");
        File.WriteAllText(second, "Attribute VB_Name = \"Second\"\r\n");
        var reads = 0;
        var admission = new VbaSourceAdmission(
            () => 65001,
            readAllBytes: path => { reads++; return File.ReadAllBytes(path); });

        var error = Assert.Throws<InvalidOperationException>(() =>
            admission.Admit(temp.Path, VbaSourceAdmissionIntent.ExplicitImport));

        Assert.Equal(0, reads);
        Assert.Contains(first, error.Message, StringComparison.Ordinal);
        Assert.Contains(second, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void InvalidActiveCodePageFailsBeforeSourceInventory(int activeCodePage)
    {
        using var temp = TempDirectory.Create();
        var inventoryCalls = 0;
        var admission = new VbaSourceAdmission(
            () => activeCodePage,
            _ => { inventoryCalls++; return []; });

        var error = Assert.Throws<InvalidOperationException>(() =>
            admission.Admit(temp.Path, VbaSourceAdmissionIntent.ExplicitImport));

        Assert.Contains($"'{activeCodePage}'", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, inventoryCalls);
    }

    [Fact]
    public void FactoryKeepsOriginalFailureWhenMirrorCleanupAlsoFails()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(Path.Combine(temp.Path, "Module1.bas"), "Attribute VB_Name = \"Module1\"\r\n");
        FileStream? mirrorLock = null;
        string? mirrorPath = null;
        var factory = new VbeImportSourceSetFactory(
            () => 65001,
            mirror =>
            {
                mirrorPath = mirror.StagingPath;
                mirrorLock = File.Open(Assert.Single(mirror.SourceFiles).SourcePath, FileMode.Open, FileAccess.Read, FileShare.None);
                throw new InvalidOperationException("source-set callback failed");
            });

        try
        {
            var error = Assert.Throws<InvalidOperationException>(() => factory.CreateExplicitImport(temp.Path));

            Assert.Contains("source-set callback failed", error.Message, StringComparison.Ordinal);
            Assert.NotNull(mirrorPath);
            Assert.True(Path.IsPathFullyQualified(mirrorPath));
            Assert.Contains(mirrorPath, error.Message, StringComparison.Ordinal);
            Assert.True(Directory.Exists(mirrorPath));
            Assert.IsType<AggregateException>(error.InnerException);
        }
        finally
        {
            mirrorLock?.Dispose();
            if (mirrorPath is not null && Directory.Exists(mirrorPath))
            {
                Directory.Delete(mirrorPath, recursive: true);
            }
        }
    }

    public static IEnumerable<object[]> EncodingCases()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "vba-source-encoding", "cases.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            yield return [item.GetProperty("id").GetString()!, item.GetRawText()];
        }
    }

    [Theory]
    [MemberData(nameof(EncodingCases))]
    public void CorpusDefinesExplicitAdmissionAndVbeProjection(string id, string caseJson)
    {
        using var document = JsonDocument.Parse(caseJson);
        var item = document.RootElement;
        using var temp = TempDirectory.Create();
        var sourceDirectory = temp.CreateDirectory(id);
        var sourcePath = Path.Combine(sourceDirectory, item.GetProperty("fileName").GetString()!);
        var bytes = Convert.FromBase64String(item.GetProperty("bytesBase64").GetString()!);
        var activeCodePage = item.GetProperty("activeCodePage").GetInt32();
        File.WriteAllBytes(sourcePath, bytes);
        var admission = new VbaSourceAdmission(() => activeCodePage);
        if (item.TryGetProperty("expectedFailure", out var expectedFailure) && expectedFailure.GetBoolean())
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                admission.Admit(sourceDirectory, VbaSourceAdmissionIntent.ExplicitImport));
            Assert.Contains(sourcePath, error.Message, StringComparison.Ordinal);
            Assert.Equal(bytes, File.ReadAllBytes(sourcePath));
            return;
        }

        var admitted = admission.Admit(sourceDirectory, VbaSourceAdmissionIntent.ExplicitImport);
        var source = Assert.Single(admitted.Sources);
        var expectedText = item.GetProperty("expectedText").GetString();
        Assert.Equal(expectedText, source.Text);
        Assert.Equal(item.GetProperty("expectedEncoding").GetString(), source.OriginalEncoding);
        Assert.Equal(bytes, source.OriginalBytes.ToArray());
        Assert.Equal(expectedText, source.Syntax.Text);
        Assert.Equal("Module1", source.ModuleIdentityAuthority.Name);
        if (item.TryGetProperty("expectedProjectionFailure", out var projectionFailure) && projectionFailure.GetBoolean())
        {
            var error = Assert.Throws<InvalidOperationException>(() => VbeImportSourceSet.Create(admitted));
            Assert.Contains(sourcePath, error.Message, StringComparison.Ordinal);
            Assert.Contains($"Windows code page {activeCodePage}", error.Message, StringComparison.Ordinal);
        }
        else
        {
            using var mirror = VbeImportSourceSet.Create(admitted);
            var staged = Assert.Single(mirror.SourceFiles);
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var encoding = activeCodePage == 65001
                ? new UTF8Encoding(false, true)
                : Encoding.GetEncoding(activeCodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            var stagedBytes = File.ReadAllBytes(staged.SourcePath);
            Assert.Equal(expectedText, encoding.GetString(stagedBytes));
            Assert.Equal(encoding.GetBytes(expectedText!), stagedBytes);
            Assert.Equal(source.Projection.CodeModuleLines, staged.ImportVerification.CodeModuleLines);
            Assert.Equal(source.OriginalEncoding, staged.ImportVerification.OriginalEncoding);
            Assert.Same(source.ModuleIdentityAuthority, staged.ModuleIdentityAuthority);
        }

        Assert.Equal(bytes, File.ReadAllBytes(sourcePath));
    }

    [Fact]
    public void CapturesOnceAndBuildsMirrorAfterCallerFilesDisappear()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("sources");
        var nested = Directory.CreateDirectory(Path.Combine(root, "nested")).FullName;
        var modulePath = Path.Combine(nested, "Greeting.bas");
        var formPath = Path.Combine(root, "Dialog.frm");
        var sidecarPath = Path.Combine(root, "Dialog.frx");
        const string moduleText = "Attribute VB_Name = \"Greeting\"\r\nOption Explicit\r\n";
        const string formText = "VERSION 5.00\r\nBegin VB.Form Dialog\r\nEnd\r\nAttribute VB_Name = \"Dialog\"\r\nOption Explicit\r\n";
        File.WriteAllBytes(modulePath, Encoding.ASCII.GetBytes(moduleText));
        File.WriteAllBytes(formPath, Encoding.ASCII.GetBytes(formText));
        File.WriteAllBytes(sidecarPath, [0, 1, 127, 255]);
        File.WriteAllBytes(Path.Combine(root, "Orphan.frx"), [4, 5]);
        File.WriteAllText(Path.Combine(root, "ignored.txt"), "not a source");
        var events = new List<string>();
        var reads = new Dictionary<string, byte[]>();
        var admission = new VbaSourceAdmission(
            () => { events.Add("acp"); return 1252; },
            directory =>
            {
                events.Add("inventory");
                return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToArray();
            },
            path =>
            {
                events.Add("read:" + path);
                var bytes = File.ReadAllBytes(path);
                Assert.True(reads.TryAdd(path, bytes), $"Source was read again: {path}");
                return bytes;
            });

        var admitted = admission.Admit(root, VbaSourceAdmissionIntent.ExplicitImport);
        var capturedEvents = events.ToArray();
        foreach (var (path, bytes) in reads)
        {
            Array.Fill(bytes, (byte)0);
            File.Delete(path);
        }
        File.WriteAllText(Path.Combine(root, "Late.bas"), "Attribute VB_Name = \"Late\"\r\n");

        using var mirror = VbeImportSourceSet.Create(admitted);

        Assert.Equal("acp", events[0]);
        Assert.Equal("inventory", events[1]);
        Assert.Equal(1, events.Count(value => value == "acp"));
        Assert.Equal(1, events.Count(value => value == "inventory"));
        Assert.Equal(3, reads.Count);
        Assert.Equal(capturedEvents, events);
        Assert.Equal(["Dialog.frm", "Greeting.bas"], admitted.Sources.Select(source => source.FileName));
        Assert.Same(admitted, mirror.Admission);
        Assert.Equal(1252, mirror.ActiveCodePage);
        foreach (var source in admitted.Sources)
        {
            var staged = Assert.Single(mirror.SourceFiles, item => item.FileName == source.FileName);
            Assert.Equal(source.Text, source.Syntax.Text);
            Assert.Equal(source.OriginalBytes.ToArray(), File.ReadAllBytes(staged.SourcePath));
            Assert.Equal(source.Projection.CodeModuleLines, staged.ImportVerification.CodeModuleLines);
            Assert.Same(source.ModuleIdentityAuthority, staged.ModuleIdentityAuthority);
            Assert.Equal(source.SourcePath, staged.DiagnosticSourcePath);
        }

        var form = Assert.Single(admitted.Sources, source => source.Kind == VbaSourceKind.Form);
        Assert.Equal(formText, form.Text);
        Assert.Equal(new byte[] { 0, 1, 127, 255 }, form.BinaryBytes!.Value.ToArray());
        var stagedForm = Assert.Single(mirror.SourceFiles, source => source.Kind == VbaSourceKind.Form);
        Assert.Equal(form.BinaryBytes.Value.ToArray(), File.ReadAllBytes(stagedForm.BinaryPath!));
    }

    [Theory]
    [InlineData(new byte[] { 0xff, 0xfe, 0x00, 0x00 })]
    [InlineData(new byte[] { 0x00, 0x00, 0xfe, 0xff })]
    [InlineData(new byte[] { 0x2b, 0x2f, 0x76, 0x38 })]
    [InlineData(new byte[] { 0x2b, 0x2f, 0x76, 0x39 })]
    [InlineData(new byte[] { 0x2b, 0x2f, 0x76, 0x2b })]
    [InlineData(new byte[] { 0x2b, 0x2f, 0x76, 0x2f })]
    public void UnsupportedBomCannotFallThroughToAnotherDecoder(byte[] preamble)
    {
        using var temp = TempDirectory.Create();
        var sourcePath = Path.Combine(temp.Path, "Unsupported.bas");
        var bytes = preamble.Concat(Encoding.ASCII.GetBytes("Option Explicit\n")).ToArray();
        File.WriteAllBytes(sourcePath, bytes);

        var error = Assert.Throws<InvalidOperationException>(() =>
            new VbaSourceAdmission(() => 1252).Admit(temp.Path, VbaSourceAdmissionIntent.ExplicitImport));

        Assert.Contains("unsupported Unicode byte-order mark", error.Message, StringComparison.Ordinal);
        Assert.Contains(sourcePath, error.Message, StringComparison.Ordinal);
        Assert.Equal(bytes, File.ReadAllBytes(sourcePath));
    }

    [Theory]
    [InlineData("utf8bom")]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    public void RecognizedBomSelectsItsStrictUnicodeDecoder(string encodingToken)
    {
        using var temp = TempDirectory.Create();
        var sourcePath = Path.Combine(temp.Path, "Cafe.bas");
        const string text = "Attribute VB_Name = \"Cafe\"\r\nPublic Const Name As String = \"Café\"\r\n";
        Encoding encoding = encodingToken switch
        {
            "utf8bom" => new UTF8Encoding(true, true),
            "utf16le" => new UnicodeEncoding(false, true, true),
            "utf16be" => new UnicodeEncoding(true, true, true),
            _ => throw new ArgumentOutOfRangeException(nameof(encodingToken))
        };
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
        File.WriteAllBytes(sourcePath, bytes);

        var admitted = new VbaSourceAdmission(() => 1252).Admit(
            temp.Path,
            VbaSourceAdmissionIntent.ExplicitImport);

        var source = Assert.Single(admitted.Sources);
        Assert.Equal(bytes, source.OriginalBytes.ToArray());
        Assert.Equal(text, source.Text);
        Assert.Equal(encodingToken, source.OriginalEncoding);
        Assert.Equal("Cafe", source.ModuleIdentityAuthority.Name);
    }

    [Fact]
    public void ExplicitImportUsesFixedAcpWhenBomlessBytesAreAlsoValidUtf8()
    {
        using var temp = TempDirectory.Create();
        var sourcePath = Path.Combine(temp.Path, "CopyrightModule.bas");
        const string utf8Text = "Attribute VB_Name = \"CopyrightModule\"\r\nPublic Const Mark As String = \"©\"\r\n";
        const string admittedText = "Attribute VB_Name = \"CopyrightModule\"\r\nPublic Const Mark As String = \"Â©\"\r\n";
        var bytes = new UTF8Encoding(false, true).GetBytes(utf8Text);
        File.WriteAllBytes(sourcePath, bytes);

        var admitted = new VbaSourceAdmission(() => 1252).Admit(
            temp.Path,
            VbaSourceAdmissionIntent.ExplicitImport);

        Assert.Equal(VbaSourceAdmissionIntent.ExplicitImport, admitted.Intent);
        Assert.Equal(1252, admitted.ActiveCodePage);
        var source = Assert.Single(admitted.Sources);
        Assert.Equal(bytes, source.OriginalBytes.ToArray());
        Assert.Equal(admittedText, source.Text);
        Assert.Equal("windows-1252", source.OriginalEncoding);
        Assert.Equal(sourcePath, source.SourcePath);
        Assert.Equal(sourcePath, source.DiagnosticSourcePath);
        Assert.Equal(VbaSourceKind.StandardModule, source.Kind);
        Assert.Equal(VbaModuleKind.StandardModule, source.Syntax.Module.Kind);
        Assert.Equal(admittedText, source.Syntax.Text);
        Assert.Equal("CopyrightModule", source.ModuleIdentityAuthority.Name);
        Assert.Equal(["Public Const Mark As String = \"Â©\""], source.Projection.CodeModuleLines);
        Assert.Equal(bytes, File.ReadAllBytes(sourcePath));
    }
}
