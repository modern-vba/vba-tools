using System.Text;
using VbaDev.App.Workbooks;
using Xunit;

namespace VbaDev.Tests;

public sealed class VbeImportSourceSetTests
{
    static VbeImportSourceSetTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [Fact]
    public void StagesEverySupportedSourceEncodingInTheFixedActiveCodePage()
    {
        const string sourceText = "Attribute VB_Name = \"Greeting\"\r\nPublic Const Message As String = \"日本語\"\r\n";
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true);
        var utf16Le = new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true);
        var utf16Be = new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true);
        var cp932 = StrictEncoding(932);
        var cases = new (string Token, byte[] Bytes)[]
        {
            ("utf8", utf8.GetBytes(sourceText)),
            ("utf8bom", WithPreamble(utf8Bom, sourceText)),
            ("utf16le", WithPreamble(utf16Le, sourceText)),
            ("utf16be", WithPreamble(utf16Be, sourceText)),
            ("windows-932", cp932.GetBytes(sourceText))
        };

        foreach (var testCase in cases)
        {
            using var temp = TempDirectory.Create();
            var sourcePath = Path.Combine(temp.Path, "Greeting.bas");
            File.WriteAllBytes(sourcePath, testCase.Bytes);

            using var sourceSet = VbeImportSourceSet.Create(
                [new VbaSourceFile(sourcePath, VbaSourceKind.StandardModule, null)],
                activeCodePage: 932);

            var staged = Assert.Single(sourceSet.SourceFiles);
            Assert.Equal(cp932.GetBytes(sourceText), File.ReadAllBytes(staged.SourcePath));
            Assert.Equal(testCase.Token, staged.ImportVerification.OriginalEncoding);
            Assert.Equal("Greeting", staged.ImportVerification.ComponentName);
            Assert.Equal(VbaSourceKind.StandardModule, staged.ImportVerification.ComponentKind);
            Assert.Equal(testCase.Bytes, File.ReadAllBytes(sourcePath));
        }
    }

    [Fact]
    public void PrefersStrictUtf8WhenBytesAreAlsoValidInTheActiveCodePage()
    {
        const string sourceText = "Attribute VB_Name = \"CopyrightModule\"\r\nPublic Const Mark As String = \"©\"\r\n";
        using var temp = TempDirectory.Create();
        var sourcePath = Path.Combine(temp.Path, "CopyrightModule.bas");
        File.WriteAllBytes(sourcePath, new UTF8Encoding(false, true).GetBytes(sourceText));

        using var sourceSet = VbeImportSourceSet.Create(
            [new VbaSourceFile(sourcePath, VbaSourceKind.StandardModule, null)],
            activeCodePage: 1252);

        var staged = Assert.Single(sourceSet.SourceFiles);
        Assert.Equal("utf8", staged.ImportVerification.OriginalEncoding);
        Assert.Equal(StrictEncoding(1252).GetBytes(sourceText), File.ReadAllBytes(staged.SourcePath));
    }

    [Fact]
    public void CanonicalizesActiveCodePage65001AsUtf8WithoutAddingABom()
    {
        const string sourceText = "Attribute VB_Name = \"EmojiModule\"\r\nPublic Const Face As String = \"🙂\"\r\n";
        using var temp = TempDirectory.Create();
        var sourcePath = Path.Combine(temp.Path, "EmojiModule.bas");
        var utf8Bom = new UTF8Encoding(true, true);
        File.WriteAllBytes(sourcePath, WithPreamble(utf8Bom, sourceText));

        using var sourceSet = VbeImportSourceSet.Create(
            [new VbaSourceFile(sourcePath, VbaSourceKind.StandardModule, null)],
            activeCodePage: 65001);

        var staged = Assert.Single(sourceSet.SourceFiles);
        Assert.Equal("utf8bom", staged.ImportVerification.OriginalEncoding);
        Assert.Equal(new UTF8Encoding(false, true).GetBytes(sourceText), File.ReadAllBytes(staged.SourcePath));
    }

    [Fact]
    public void RejectsAnUnavailableActiveCodePageDeterministically()
    {
        using var temp = TempDirectory.Create();
        var sourcePath = Path.Combine(temp.Path, "Module1.bas");
        File.WriteAllText(
            sourcePath,
            "Attribute VB_Name = \"Module1\"",
            new UTF8Encoding(false));

        var error = Assert.Throws<InvalidOperationException>(() =>
            VbeImportSourceSet.Create(
                [new VbaSourceFile(sourcePath, VbaSourceKind.StandardModule, null)],
                activeCodePage: int.MaxValue));

        Assert.Contains("not available", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsInvalidOrLossyTextBeforeCreatingAnImportSourceSet()
    {
        using var temp = TempDirectory.Create();
        var invalidPath = Path.Combine(temp.Path, "Invalid.bas");
        File.WriteAllBytes(invalidPath, [0x81]);

        var invalid = Assert.Throws<InvalidOperationException>(() =>
            VbeImportSourceSet.Create(
                [new VbaSourceFile(invalidPath, VbaSourceKind.StandardModule, null)],
                activeCodePage: 932));

        Assert.Contains("strictly decoded", invalid.Message, StringComparison.OrdinalIgnoreCase);

        var lossyPath = Path.Combine(temp.Path, "Lossy.bas");
        const string lossyText = "Attribute VB_Name = \"Lossy\"\r\nPublic Const Value As String = \"−🙂\"\r\n";
        File.WriteAllBytes(lossyPath, new UTF8Encoding(false, true).GetBytes(lossyText));

        var lossy = Assert.Throws<InvalidOperationException>(() =>
            VbeImportSourceSet.Create(
                [new VbaSourceFile(lossyPath, VbaSourceKind.StandardModule, null)],
                activeCodePage: 1252));

        Assert.Contains("Windows code page 1252", lossy.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new UTF8Encoding(false, true).GetBytes(lossyText), File.ReadAllBytes(lossyPath));
    }

    [Fact]
    public void RecognizedMalformedBomInputDoesNotFallBackToTheActiveCodePage()
    {
        var cases = new byte[][]
        {
            [0xef, 0xbb, 0xbf, 0xe9],
            [0xff, 0xfe, 0x41],
            [0xfe, 0xff, 0x41]
        };

        foreach (var bytes in cases)
        {
            using var temp = TempDirectory.Create();
            var sourcePath = Path.Combine(temp.Path, "Malformed.bas");
            File.WriteAllBytes(sourcePath, bytes);

            var error = Assert.Throws<InvalidOperationException>(() =>
                VbeImportSourceSet.Create(
                    [new VbaSourceFile(sourcePath, VbaSourceKind.StandardModule, null)],
                    activeCodePage: 1252));

            Assert.Contains("strictly decoded", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(bytes, File.ReadAllBytes(sourcePath));
        }
    }

    [Theory]
    [InlineData(new byte[] { 0xff, 0xfe, 0x00, 0x00 })]
    [InlineData(new byte[] { 0x00, 0x00, 0xfe, 0xff })]
    [InlineData(new byte[] { 0x2b, 0x2f, 0x76, 0x38 })]
    public void RejectsUnsupportedUnicodeByteOrderMarksBeforeImport(byte[] preamble)
    {
        using var temp = TempDirectory.Create();
        var sourcePath = Path.Combine(temp.Path, "Unsupported.bas");
        var sourceBytes = preamble.Concat(Encoding.ASCII.GetBytes("unsupported")).ToArray();
        File.WriteAllBytes(sourcePath, sourceBytes);

        var error = Assert.Throws<InvalidOperationException>(() =>
            VbeImportSourceSet.Create(
                [new VbaSourceFile(sourcePath, VbaSourceKind.StandardModule, null)],
                activeCodePage: 1252));

        Assert.Contains("unsupported", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(sourceBytes, File.ReadAllBytes(sourcePath));
    }

    [Fact]
    public void RejectsNonCanonicalActiveCodePageExtensionBytes()
    {
        using var temp = TempDirectory.Create();
        var sourcePath = Path.Combine(temp.Path, "Alias.bas");
        var cp932 = StrictEncoding(932);
        var prefix = cp932.GetBytes("Attribute VB_Name = \"Alias\"\r\n' ");
        var suffix = cp932.GetBytes("\r\n");
        var sourceBytes = prefix.Concat(new byte[] { 0xed, 0x40 }).Concat(suffix).ToArray();
        File.WriteAllBytes(sourcePath, sourceBytes);

        var error = Assert.Throws<InvalidOperationException>(() =>
            VbeImportSourceSet.Create(
                [new VbaSourceFile(sourcePath, VbaSourceKind.StandardModule, null)],
                activeCodePage: 932));

        Assert.Contains("without changing its bytes", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(sourceBytes, File.ReadAllBytes(sourcePath));
    }

    [Fact]
    public void DetectsBomlessActiveCodePage1252AfterUtf8Fails()
    {
        const string sourceText = "Attribute VB_Name = \"Cafe\"\r\nPublic Const Name As String = \"Café\"\r\n";
        using var temp = TempDirectory.Create();
        var sourcePath = Path.Combine(temp.Path, "Cafe.bas");
        var cp1252 = StrictEncoding(1252);
        var sourceBytes = cp1252.GetBytes(sourceText);
        File.WriteAllBytes(sourcePath, sourceBytes);

        using var sourceSet = VbeImportSourceSet.Create(
            [new VbaSourceFile(sourcePath, VbaSourceKind.StandardModule, null)],
            activeCodePage: 1252);

        var staged = Assert.Single(sourceSet.SourceFiles);
        Assert.Equal("windows-1252", staged.ImportVerification.OriginalEncoding);
        Assert.Equal(sourceBytes, File.ReadAllBytes(staged.SourcePath));
        Assert.Equal(sourceBytes, File.ReadAllBytes(sourcePath));
    }

    [Fact]
    public void RejectsBestFitSubstitutionInsteadOfAcceptingTheMappedByte()
    {
        using var temp = TempDirectory.Create();
        var sourcePath = Path.Combine(temp.Path, "BestFit.bas");
        const string sourceText = "Attribute VB_Name = \"BestFit\"\r\nPublic Const Minus As String = \"−\"\r\n";
        var sourceBytes = new UTF8Encoding(false, true).GetBytes(sourceText);
        File.WriteAllBytes(sourcePath, sourceBytes);

        var error = Assert.Throws<InvalidOperationException>(() =>
            VbeImportSourceSet.Create(
                [new VbaSourceFile(sourcePath, VbaSourceKind.StandardModule, null)],
                activeCodePage: 1252));

        Assert.Contains("losslessly", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(sourceBytes, File.ReadAllBytes(sourcePath));
    }

    [Fact]
    public void StoresTheReusableClassAndFormCodeModuleProjections()
    {
        using var temp = TempDirectory.Create();
        var classPath = Path.Combine(temp.Path, "WorkerClass.cls");
        var formPath = Path.Combine(temp.Path, "Dialog.frm");
        File.WriteAllText(
            classPath,
            string.Join("\r\n", [
                "VERSION 1.0 CLASS",
                "BEGIN",
                "  MultiUse = -1  'True",
                "END",
                "Attribute VB_Name = \"WorkerClass\"",
                "Attribute VB_Exposed = False",
                "Option Explicit",
                "Public Sub Run()",
                "    value = 1",
                "End Sub",
                string.Empty
            ]),
            new UTF8Encoding(false));
        File.WriteAllText(
            formPath,
            string.Join("\r\n", [
                "VERSION 5.00",
                "Begin VB.Form Dialog",
                "  Caption = \"Projection fixture\"",
                "End",
                "Attribute VB_Name = \"Dialog\"",
                "Attribute VB_PredeclaredId = True",
                "Option Explicit",
                "Private Sub Run()",
                "    Debug.Print \"run\"",
                "End Sub",
                string.Empty
            ]),
            new UTF8Encoding(false));

        using var sourceSet = VbeImportSourceSet.Create(
            [
                new VbaSourceFile(classPath, VbaSourceKind.ClassModule, null),
                new VbaSourceFile(formPath, VbaSourceKind.Form, null)
            ],
            activeCodePage: 1252);

        var stagedClass = Assert.Single(sourceSet.SourceFiles, source => source.Kind == VbaSourceKind.ClassModule);
        Assert.Equal(
            ["Option Explicit", "Public Sub Run()", "    value = 1", "End Sub"],
            stagedClass.ImportVerification.CodeModuleLines);
        var stagedForm = Assert.Single(sourceSet.SourceFiles, source => source.Kind == VbaSourceKind.Form);
        Assert.Equal(
            [string.Empty, "Option Explicit", "Private Sub Run()", "    Debug.Print \"run\"", "End Sub"],
            stagedForm.ImportVerification.CodeModuleLines);
    }

    [Fact]
    public void CapturesTheActiveCodePageOnceAndPreservesCallerOwnedSourcesAndFormSidecars()
    {
        using var temp = TempDirectory.Create();
        var modulePath = Path.Combine(temp.Path, "Module1.bas");
        var formPath = Path.Combine(temp.Path, "Dialog.frm");
        var frxPath = Path.Combine(temp.Path, "Dialog.frx");
        const string moduleText = "Attribute VB_Name = \"Module1\"\r\n";
        const string formText =
            "VERSION 5.00\r\nBegin VB.Form Dialog\r\nEnd\r\nAttribute VB_Name = \"Dialog\"\r\n";
        var moduleBytes = Encoding.UTF8.GetBytes(moduleText);
        var formBytes = Encoding.UTF8.GetBytes(formText);
        byte[] frxBytes = [0, 1, 2, 0xff, 0x80];
        File.WriteAllBytes(modulePath, moduleBytes);
        File.WriteAllBytes(formPath, formBytes);
        File.WriteAllBytes(frxPath, frxBytes);
        File.SetLastWriteTimeUtc(modulePath, DateTime.UtcNow.AddHours(-3));
        File.SetLastWriteTimeUtc(formPath, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(frxPath, DateTime.UtcNow.AddHours(-1));
        var moduleTimestamp = File.GetLastWriteTimeUtc(modulePath);
        var formTimestamp = File.GetLastWriteTimeUtc(formPath);
        var frxTimestamp = File.GetLastWriteTimeUtc(frxPath);
        var calls = 0;
        var factory = new VbeImportSourceSetFactory(() =>
        {
            calls++;
            return 1252;
        });
        string stagingPath;

        using (var sourceSet = factory.Create(
            [
                new VbaSourceFile(modulePath, VbaSourceKind.StandardModule, null)
                {
                    ExpectedUnicodeText = moduleText
                },
                new VbaSourceFile(formPath, VbaSourceKind.Form, frxPath)
                {
                    ExpectedUnicodeText = formText
                }
            ]))
        {
            stagingPath = sourceSet.StagingPath;
            Assert.Equal(1, calls);
            Assert.True(Directory.Exists(stagingPath));
            var stagedForm = Assert.Single(sourceSet.SourceFiles, source => source.Kind == VbaSourceKind.Form);
            Assert.NotNull(stagedForm.BinaryPath);
            Assert.Equal("Dialog.frx", Path.GetFileName(stagedForm.BinaryPath));
            Assert.Equal(Path.GetFileNameWithoutExtension(stagedForm.SourcePath), Path.GetFileNameWithoutExtension(stagedForm.BinaryPath));
            Assert.Equal(frxBytes, File.ReadAllBytes(stagedForm.BinaryPath));
        }

        Assert.False(Directory.Exists(stagingPath));
        Assert.Equal(moduleBytes, File.ReadAllBytes(modulePath));
        Assert.Equal(formBytes, File.ReadAllBytes(formPath));
        Assert.Equal(frxBytes, File.ReadAllBytes(frxPath));
        Assert.Equal(moduleTimestamp, File.GetLastWriteTimeUtc(modulePath));
        Assert.Equal(formTimestamp, File.GetLastWriteTimeUtc(formPath));
        Assert.Equal(frxTimestamp, File.GetLastWriteTimeUtc(frxPath));
    }

    [Fact]
    public void DisposeDoesNotReportSuccessWhenTheStagedMirrorCannotBeRemoved()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var sourcePath = Path.Combine(temp.Path, "Module1.bas");
        File.WriteAllText(
            sourcePath,
            "Attribute VB_Name = \"Module1\"\r\n",
            new UTF8Encoding(false));
        var sourceSet = VbeImportSourceSet.Create(
            [new VbaSourceFile(sourcePath, VbaSourceKind.StandardModule, null)],
            activeCodePage: 1252);
        var stagedPath = Assert.Single(sourceSet.SourceFiles).SourcePath;

        using (File.Open(stagedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = Assert.Throws<InvalidOperationException>(sourceSet.Dispose);
            Assert.Contains("could not be removed", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(sourceSet.StagingPath));
        }

        sourceSet.Dispose();
        Assert.False(Directory.Exists(sourceSet.StagingPath));
    }

    [Fact]
    public void VerifiesImportedIdentityKindLineCountAndEveryProjectedLineExactly()
    {
        var expected = new VbeImportVerification(
            componentName: "Module1",
            componentKind: VbaSourceKind.StandardModule,
            codeModuleLines: ["Option Explicit", string.Empty, "Public Sub Run()", "End Sub"],
            originalEncoding: "utf8");

        VbeImportedComponentVerifier.Verify(
            expected,
            new VbeImportedComponent(
                "Module1",
                VbaSourceKind.StandardModule,
                ["Option Explicit", string.Empty, "Public Sub Run()", "End Sub"]));

        Assert.Contains(
            "name",
            Assert.Throws<InvalidOperationException>(() => VbeImportedComponentVerifier.Verify(
                expected,
                new VbeImportedComponent("Other", VbaSourceKind.StandardModule, expected.CodeModuleLines))).Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "kind",
            Assert.Throws<InvalidOperationException>(() => VbeImportedComponentVerifier.Verify(
                expected,
                new VbeImportedComponent("Module1", VbaSourceKind.ClassModule, expected.CodeModuleLines))).Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "line count",
            Assert.Throws<InvalidOperationException>(() => VbeImportedComponentVerifier.Verify(
                expected,
                new VbeImportedComponent("Module1", VbaSourceKind.StandardModule, ["Option Explicit"]))).Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "line 3",
            Assert.Throws<InvalidOperationException>(() => VbeImportedComponentVerifier.Verify(
                expected,
                new VbeImportedComponent(
                    "Module1",
                    VbaSourceKind.StandardModule,
                    ["Option Explicit", string.Empty, "Public Sub Other()", "End Sub"]))).Message,
            StringComparison.OrdinalIgnoreCase);
    }

    private static Encoding StrictEncoding(int codePage)
        => codePage == 65001
            ? new UTF8Encoding(false, true)
            : Encoding.GetEncoding(
                codePage,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);

    private static byte[] WithPreamble(Encoding encoding, string text)
        => encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();

}
