using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using VbaDebugAdapter.Debugging;
using VbaDebugAdapter.Infrastructure;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class OpenXmlDebugCompilationSettingsReaderTests
{
    [Fact]
    public void ReadRetainsTheVbaPartFingerprintWhenUnrelatedPackageBytesChange()
    {
        using var fixture = WorkbookFixture.Create();
        var reader = new OpenXmlDebugCompilationSettingsReader();
        var firstSettings = reader.Read(fixture.WorkbookPath);
        var firstPackageHash = SHA256.HashData(File.ReadAllBytes(fixture.WorkbookPath));
        fixture.MutateArchive(archive => WorkbookFixture.ReplaceEntry(
            archive,
            "custom/not-vbaProject.bin",
            "unrelated package content"u8.ToArray()));

        var secondSettings = reader.Read(fixture.WorkbookPath);

        Assert.NotEqual(firstPackageHash, SHA256.HashData(File.ReadAllBytes(fixture.WorkbookPath)));
        Assert.Equal(fixture.VbaProjectPartSha256, firstSettings.VbaProjectPartSha256);
        Assert.Equal(firstSettings.VbaProjectPartSha256, secondSettings.VbaProjectPartSha256);
        Assert.Equal(firstSettings.SystemKind, secondSettings.SystemKind);
        Assert.Equal(firstSettings.CodePage, secondSettings.CodePage);
        Assert.Equal(firstSettings.ProjectConstants, secondSettings.ProjectConstants);
    }

    [Fact]
    public void ReadProjectsAnExclusiveWorkbookLockAsDebugSetupError()
    {
        using var fixture = WorkbookFixture.Create();
        using var exclusiveHandle = new FileStream(
            fixture.WorkbookPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        var error = Assert.Throws<DebugSetupException>(
            () => new OpenXmlDebugCompilationSettingsReader().Read(fixture.WorkbookPath));

        Assert.Contains(fixture.WorkbookPath, error.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<IOException>(error.InnerException);
    }

    [Theory]
    [InlineData("DebugWorkbook.xlsx")]
    [InlineData("DebugWorkbook.xlsb")]
    [InlineData("DebugWorkbook")]
    public void ReadRejectsNonXlsmWorkbookExtensions(string workbookFileName)
    {
        using var fixture = WorkbookFixture.Create(workbookFileName: workbookFileName);

        var error = Assert.Throws<DebugSetupException>(
            () => new OpenXmlDebugCompilationSettingsReader().Read(fixture.WorkbookPath));

        Assert.Contains("require an .xlsm workbook", error.Message, StringComparison.Ordinal);
        Assert.Contains(fixture.WorkbookPath, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Win16", VbaProjectSystemKind.Win16)]
    [InlineData("Win32", VbaProjectSystemKind.Win32)]
    [InlineData("Mac", VbaProjectSystemKind.Macintosh)]
    [InlineData("Win64", VbaProjectSystemKind.Win64)]
    public void ReadProjectsEveryDeclaredSystemKindIntoDebugSettings(
        string packageName,
        VbaProjectSystemKind expectedSystemKind)
    {
        using var fixture = WorkbookFixture.Create(packageName);

        var settings = new OpenXmlDebugCompilationSettingsReader().Read(fixture.WorkbookPath);

        Assert.Equal(expectedSystemKind, settings.SystemKind);
        Assert.Equal(fixture.VbaProjectPartSha256, settings.VbaProjectPartSha256);
    }

    [Theory]
    [InlineData("NoncanonicalLcid", "PROJECTLCID")]
    [InlineData("NoncanonicalInvokeLcid", "PROJECTLCIDINVOKE")]
    [InlineData("NonzeroLibFlags", "PROJECTLIBFLAGS")]
    public void ReadProjectsNoncanonicalProjectInformationAsDebugSetupError(
        string packageName,
        string expectedField)
    {
        using var fixture = WorkbookFixture.Create(packageName);

        var error = AssertFailure(fixture, "InvalidProjectInformation");

        Assert.Contains(expectedField, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadProjectsInvalidWorkbookRelationshipAsDebugSetupError()
    {
        using var fixture = WorkbookFixture.Create();
        fixture.MutateArchive(archive => WorkbookFixture.ReplaceEntry(
            archive,
            "xl/_rels/workbook.xml.rels",
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"vba\" Type=\"http://schemas.microsoft.com/office/2006/relationships/vbaProject\" Target=\"vbaProject.bin\" TargetMode=\"External\"/></Relationships>"u8.ToArray()));

        AssertFailure(fixture, "InvalidPackageTopology");
    }

    [Theory]
    [InlineData("")]
    [InlineData("000102")]
    public void ReadProjectsEmptyOrInvalidPackageAsDebugSetupError(string packageHex)
    {
        using var fixture = WorkbookFixture.Create();
        File.WriteAllBytes(fixture.WorkbookPath, Convert.FromHexString(packageHex));

        AssertFailure(fixture, "InvalidPackage");
    }

    [Fact]
    public void ReadProjectsMissingWorkbookAsDebugSetupError()
    {
        using var fixture = WorkbookFixture.Create();
        var missingPath = Path.Combine(fixture.DirectoryPath, "Missing.xlsm");

        var error = Assert.Throws<DebugSetupException>(
            () => new OpenXmlDebugCompilationSettingsReader().Read(missingPath));

        Assert.Contains(missingPath, error.Message, StringComparison.Ordinal);
        Assert.IsType<FileNotFoundException>(error.InnerException);
    }

    [Fact]
    public void ReadRejectsOrphanVbaProjectPartAsDebugSetupError()
    {
        using var fixture = WorkbookFixture.Create();
        fixture.MutateArchive(archive =>
        {
            archive.GetEntry("xl/workbook.xml")!.Delete();
            archive.GetEntry("xl/_rels/workbook.xml.rels")!.Delete();
        });

        AssertFailure(fixture, "InvalidPackageTopology");
    }

    [Fact]
    public void ReadReturnsPersistedSettingsAndExactVbaProjectPartFingerprint()
    {
        using var fixture = WorkbookFixture.Create();

        var settings = new OpenXmlDebugCompilationSettingsReader().Read(fixture.WorkbookPath);

        Assert.Equal(VbaProjectSystemKind.Win64, settings.SystemKind);
        Assert.Equal(932, settings.CodePage);
        Assert.Equal((short)1, settings.ProjectConstants["機能"]);
        Assert.Equal((short)-2, settings.ProjectConstants["trace"]);
        Assert.Equal(fixture.VbaProjectPartSha256, settings.VbaProjectPartSha256);
    }

    [Fact]
    public void ReadAcceptsWorkbookHeldOpenForReadWriteByExcel()
    {
        using var fixture = WorkbookFixture.Create();
        using var excelHandle = new FileStream(
            fixture.WorkbookPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);

        var settings = new OpenXmlDebugCompilationSettingsReader().Read(fixture.WorkbookPath);

        Assert.Equal(fixture.VbaProjectPartSha256, settings.VbaProjectPartSha256);
    }

    [Fact]
    public void ReadProjectsInvalidCompoundFileAsDebugSetupError()
    {
        using var fixture = WorkbookFixture.Create();
        fixture.MutateArchive(archive =>
            WorkbookFixture.ReplaceEntry(archive, "xl/vbaProject.bin", [0x01, 0x02, 0x03]));

        AssertFailure(fixture, "InvalidCompoundFile");
    }

    [Fact]
    public void ReadProjectsInvalidUtf16ConstantsAsDebugSetupError()
    {
        using var fixture = WorkbookFixture.Create("InvalidUnicodeConstants");

        AssertFailure(fixture, "InvalidProjectInformation");
    }

    private static DebugSetupException AssertFailure(WorkbookFixture fixture, string expectedKind)
    {
        var error = Assert.Throws<DebugSetupException>(
            () => new OpenXmlDebugCompilationSettingsReader().Read(fixture.WorkbookPath));

        Assert.Contains("generated workbook", error.Message, StringComparison.Ordinal);
        Assert.Contains(fixture.WorkbookPath, error.Message, StringComparison.Ordinal);
        Assert.Contains($"({expectedKind})", error.Message, StringComparison.Ordinal);
        return error;
    }

    private sealed class WorkbookFixture : IDisposable
    {
        // Data-only, gzip-compressed JSON: named complete package bytes and independently
        // captured VBA-part SHA-256 values. Each package has macro-enabled workbook
        // topology, a CFB VBA/dir stream, PROJECTNAME=VBAProject, code page 932, and
        // constants "機能 = 1 : Trace = -2". Win16/Win32/Mac/Win64 vary only SYSKIND.
        // Named invalid packages vary only LCID, LCIDINVOKE, LIBFLAGS, or UTF-16 constants.
        // Format construction and conformance belong to the neutral reader's test owner.
        private const string FixedPackages =
            """
            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
            """;

        private static readonly IReadOnlyDictionary<string, PackageData> Packages = ReadPackages();

        private WorkbookFixture(string directoryPath, string workbookPath, string partSha256)
        {
            DirectoryPath = directoryPath;
            WorkbookPath = workbookPath;
            VbaProjectPartSha256 = partSha256;
        }

        public string DirectoryPath { get; }

        public string WorkbookPath { get; }

        public string VbaProjectPartSha256 { get; }

        public static WorkbookFixture Create(
            string packageName = "Win64",
            string workbookFileName = "DebugWorkbook.xlsm")
        {
            var payload = Packages[packageName];
            var directoryPath = Path.Combine(
                Path.GetTempPath(),
                $"vba-tools-settings-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directoryPath);
            var workbookPath = Path.Combine(directoryPath, workbookFileName);
            File.WriteAllBytes(workbookPath, Convert.FromBase64String(payload.Package));
            return new WorkbookFixture(directoryPath, workbookPath, payload.PartSha256);
        }

        public void MutateArchive(Action<ZipArchive> mutation)
        {
            using var archive = ZipFile.Open(WorkbookPath, ZipArchiveMode.Update);
            mutation(archive);
        }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }

        public static void ReplaceEntry(ZipArchive archive, string name, byte[] bytes)
        {
            archive.GetEntry(name)?.Delete();
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using var stream = entry.Open();
            stream.Write(bytes);
        }

        private static IReadOnlyDictionary<string, PackageData> ReadPackages()
        {
            using var bytes = new MemoryStream(Convert.FromBase64String(FixedPackages));
            using var decompressed = new GZipStream(bytes, CompressionMode.Decompress);
            return JsonSerializer.Deserialize<Dictionary<string, PackageData>>(decompressed)!;
        }

        private sealed record PackageData(string Package, string PartSha256);
    }
}
