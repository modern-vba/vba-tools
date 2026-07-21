using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using VbaDev.App.Debugging;
using VbaDev.Infrastructure.Debugging;
using Xunit;

namespace VbaDev.Tests;

public sealed class OpenXmlDebugCompilationSettingsReaderTests
{
    [Fact]
    public void ReadReturnsPersistedSettingsAndExactVbaProjectPartFingerprint()
    {
        using var fixture = WorkbookFixture.Create(
            VbaProjectSystemKind.Win64,
            932,
            "機能 = 1 : Trace = -2");

        var settings = new OpenXmlDebugCompilationSettingsReader().Read(fixture.WorkbookPath);

        Assert.Equal(VbaProjectSystemKind.Win64, settings.SystemKind);
        Assert.Equal(932, settings.CodePage);
        Assert.Equal((short)1, settings.ProjectConstants["機能"]);
        Assert.Equal((short)-2, settings.ProjectConstants["trace"]);
        Assert.Equal(fixture.VbaProjectPartSha256, settings.VbaProjectPartSha256);
    }

    [Fact]
    public void ReadAcceptsVbaProjectPartDeclaredByDefaultContentType()
    {
        using var fixture = WorkbookFixture.Create(
            VbaProjectSystemKind.Win64,
            932,
            string.Empty,
            useDefaultContentType: true);

        var settings = new OpenXmlDebugCompilationSettingsReader().Read(fixture.WorkbookPath);

        Assert.Equal(VbaProjectSystemKind.Win64, settings.SystemKind);
        Assert.Equal(fixture.VbaProjectPartSha256, settings.VbaProjectPartSha256);
    }

    [Fact]
    public void ReadRejectsVbaProjectPartWhenOverrideReplacesItsDefaultContentType()
    {
        using var fixture = WorkbookFixture.Create(
            VbaProjectSystemKind.Win64,
            932,
            string.Empty,
            useDefaultContentType: true,
            additionalContentTypeDeclaration:
                "<Override PartName=\"/xl/vbaProject.bin\" ContentType=\"application/octet-stream\"/>");

        var error = Assert.Throws<DebugSetupException>(
            () => new OpenXmlDebugCompilationSettingsReader().Read(fixture.WorkbookPath));

        Assert.Contains("content type", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadTreatsVbaProjectPartNamesAsAsciiCaseInsensitive()
    {
        using var fixture = WorkbookFixture.Create(
            VbaProjectSystemKind.Win64,
            932,
            string.Empty,
            vbaProjectPartName: "XL/VBAPROJECT.BIN");

        var settings = new OpenXmlDebugCompilationSettingsReader().Read(fixture.WorkbookPath);

        Assert.Equal(fixture.VbaProjectPartSha256, settings.VbaProjectPartSha256);
    }

    [Fact]
    public void ReadAcceptsWorkbookHeldOpenForReadWriteByExcel()
    {
        using var fixture = WorkbookFixture.Create(
            VbaProjectSystemKind.Win64,
            932,
            string.Empty,
            useDefaultContentType: true);
        using var excelHandle = new FileStream(
            fixture.WorkbookPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);

        var settings = new OpenXmlDebugCompilationSettingsReader().Read(fixture.WorkbookPath);

        Assert.Equal(fixture.VbaProjectPartSha256, settings.VbaProjectPartSha256);
    }

    [Fact]
    public void ReadRejectsContentTypeDeclarationsOutsideTheOpcNamespace()
    {
        using var fixture = WorkbookFixture.Create(
            VbaProjectSystemKind.Win64,
            932,
            string.Empty,
            contentTypesNamespace: "urn:not-opc");

        var error = Assert.Throws<DebugSetupException>(
            () => new OpenXmlDebugCompilationSettingsReader().Read(fixture.WorkbookPath));

        Assert.Contains("content type", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadRejectsCaseInsensitiveDuplicateDefaultContentTypes()
    {
        using var fixture = WorkbookFixture.Create(
            VbaProjectSystemKind.Win64,
            932,
            string.Empty,
            useDefaultContentType: true,
            additionalContentTypeDeclaration:
                "<Default Extension=\"BIN\" ContentType=\"application/vnd.ms-office.vbaProject\"/>");

        var error = Assert.Throws<DebugSetupException>(
            () => new OpenXmlDebugCompilationSettingsReader().Read(fixture.WorkbookPath));

        Assert.Contains("ambiguous", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadRejectsCaseInsensitiveDuplicateContentTypeOverrides()
    {
        using var fixture = WorkbookFixture.Create(
            VbaProjectSystemKind.Win64,
            932,
            string.Empty,
            additionalContentTypeDeclaration:
                "<Override PartName=\"/XL/VBAPROJECT.BIN\" ContentType=\"application/vnd.ms-office.vbaProject\"/>");

        var error = Assert.Throws<DebugSetupException>(
            () => new OpenXmlDebugCompilationSettingsReader().Read(fixture.WorkbookPath));

        Assert.Contains("ambiguous", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadRejectsMalformedCompoundFileAsDebugSetupError()
    {
        using var fixture = WorkbookFixture.Create(
            VbaProjectSystemKind.Win64,
            932,
            string.Empty,
            transformPart: _ => [0x01, 0x02, 0x03]);

        var error = Assert.Throws<DebugSetupException>(
            () => new OpenXmlDebugCompilationSettingsReader().Read(fixture.WorkbookPath));

        Assert.Contains("generated workbook", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadWrapsOpenMcdfFileFormatErrorsAsDebugSetupErrors()
    {
        using var fixture = WorkbookFixture.Create(
            VbaProjectSystemKind.Win64,
            932,
            string.Empty,
            transformPart: bytes =>
            {
                bytes[0] = 0;
                return bytes;
            });

        var error = Assert.Throws<DebugSetupException>(
            () => new OpenXmlDebugCompilationSettingsReader().Read(fixture.WorkbookPath));

        Assert.Equal(
            "OpenMcdf.FileFormatException",
            error.InnerException?.GetType().FullName);
    }

    [Fact]
    public void ReadRejectsCompoundFileWithoutVbaStorageAsDebugSetupError()
    {
        using var fixture = WorkbookFixture.Create(
            VbaProjectSystemKind.Win64,
            932,
            string.Empty,
            transformPart: bytes => WorkbookFixture.RenameCompoundDirectoryEntry(
                bytes,
                entryIndex: 1,
                "BAD"));

        var error = Assert.Throws<DebugSetupException>(
            () => new OpenXmlDebugCompilationSettingsReader().Read(fixture.WorkbookPath));

        Assert.Contains("VBA storage", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadRejectsCompoundFileWithoutVbaDirectoryStreamAsDebugSetupError()
    {
        using var fixture = WorkbookFixture.Create(
            VbaProjectSystemKind.Win64,
            932,
            string.Empty,
            transformPart: bytes => WorkbookFixture.RenameCompoundDirectoryEntry(
                bytes,
                entryIndex: 2,
                "bad"));

        var error = Assert.Throws<DebugSetupException>(
            () => new OpenXmlDebugCompilationSettingsReader().Read(fixture.WorkbookPath));

        Assert.Contains("directory stream", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadRejectsMismatchedMbcsAndUnicodeProjectConstants()
    {
        using var fixture = WorkbookFixture.Create(
            VbaProjectSystemKind.Win64,
            932,
            "Feature = 1",
            unicodeConstants: "Feature = 2");

        var error = Assert.Throws<DebugSetupException>(
            () => new OpenXmlDebugCompilationSettingsReader().Read(fixture.WorkbookPath));

        Assert.Contains("do not match", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadWrapsInvalidUtf16ProjectConstantsAsDebugSetupError()
    {
        using var fixture = WorkbookFixture.Create(
            VbaProjectSystemKind.Win64,
            932,
            "Feature = 1",
            unicodeConstantsBytes: [0x00, 0xd8]);

        var error = Assert.Throws<DebugSetupException>(
            () => new OpenXmlDebugCompilationSettingsReader().Read(fixture.WorkbookPath));

        Assert.IsType<DecoderFallbackException>(error.InnerException);
    }

    [Theory]
    [InlineData("Feature = 1 : feature = 2", "duplicated")]
    [InlineData("vBa7 = 1", "built-in")]
    [InlineData("Feature=1", "malformed")]
    [InlineData("Feature = +1", "malformed")]
    public void ReadRejectsAmbiguousOrMalformedProjectConstants(
        string constants,
        string expectedReason)
    {
        using var fixture = WorkbookFixture.Create(
            VbaProjectSystemKind.Win64,
            932,
            constants);

        var error = Assert.Throws<DebugSetupException>(
            () => new OpenXmlDebugCompilationSettingsReader().Read(fixture.WorkbookPath));

        Assert.Contains(expectedReason, error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadRejectsDirectoryRecordLengthOutsideAvailableBytes()
    {
        using var fixture = WorkbookFixture.Create(
            VbaProjectSystemKind.Win64,
            932,
            string.Empty,
            mutateDirectory: bytes =>
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), uint.MaxValue));

        var error = Assert.Throws<DebugSetupException>(
            () => new OpenXmlDebugCompilationSettingsReader().Read(fixture.WorkbookPath));

        Assert.Contains("bound", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadRejectsMoreThanOneVbaProjectPart()
    {
        using var fixture = WorkbookFixture.Create(
            VbaProjectSystemKind.Win64,
            932,
            string.Empty,
            mutateArchive: archive => WorkbookFixture.WriteEntry(
                archive,
                "custom/vbaProject.bin",
                [0x01]));

        var error = Assert.Throws<DebugSetupException>(
            () => new OpenXmlDebugCompilationSettingsReader().Read(fixture.WorkbookPath));

        Assert.Contains("exactly one", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadRejectsAnotherPartWithTheEffectiveVbaProjectContentType()
    {
        using var fixture = WorkbookFixture.Create(
            VbaProjectSystemKind.Win64,
            932,
            string.Empty,
            useDefaultContentType: true,
            mutateArchive: archive => WorkbookFixture.WriteEntry(
                archive,
                "custom/other.bin",
                [0x01]));

        var error = Assert.Throws<DebugSetupException>(
            () => new OpenXmlDebugCompilationSettingsReader().Read(fixture.WorkbookPath));

        Assert.Contains("exactly one", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadRejectsCaseInsensitiveDuplicateContentTypesParts()
    {
        using var fixture = WorkbookFixture.Create(
            VbaProjectSystemKind.Win64,
            932,
            string.Empty,
            mutateArchive: archive => WorkbookFixture.WriteEntry(
                archive,
                "[CONTENT_TYPES].XML",
                "<Types/>"u8.ToArray()));

        var error = Assert.Throws<DebugSetupException>(
            () => new OpenXmlDebugCompilationSettingsReader().Read(fixture.WorkbookPath));

        Assert.Contains("exactly one", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(4, 932, "PROJECTSYSKIND")]
    [InlineData(3, 0, "PROJECTCODEPAGE")]
    [InlineData(3, 65535, "PROJECTCODEPAGE")]
    public void ReadRejectsUnsupportedProjectInformationValues(
        int systemKind,
        int codePage,
        string expectedField)
    {
        using var fixture = WorkbookFixture.Create(
            (VbaProjectSystemKind)systemKind,
            codePage,
            string.Empty);

        var error = Assert.Throws<DebugSetupException>(
            () => new OpenXmlDebugCompilationSettingsReader().Read(fixture.WorkbookPath));

        Assert.Contains(expectedField, error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class WorkbookFixture : IDisposable
    {
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
            VbaProjectSystemKind systemKind,
            int codePage,
            string constants,
            Action<byte[]>? mutateDirectory = null,
            Action<ZipArchive>? mutateArchive = null,
            Func<byte[], byte[]>? transformPart = null,
            string? unicodeConstants = null,
            bool useDefaultContentType = false,
            string additionalContentTypeDeclaration = "",
            string vbaProjectPartName = "xl/vbaProject.bin",
            string contentTypesNamespace =
                "http://schemas.openxmlformats.org/package/2006/content-types",
            byte[]? unicodeConstantsBytes = null)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var directory = BuildProjectInformation(
                systemKind,
                codePage,
                constants,
                unicodeConstants ?? constants,
                unicodeConstantsBytes);
            mutateDirectory?.Invoke(directory);
            var compressed = CompressAsLiteralChunk(directory);
            var partBytes = BuildCompoundFile(compressed);
            partBytes = transformPart?.Invoke(partBytes) ?? partBytes;
            var directoryPath = Path.Combine(
                Path.GetTempPath(),
                $"vba-tools-settings-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directoryPath);
            var workbookPath = Path.Combine(directoryPath, "DebugWorkbook.xlsm");
            using (var file = File.Create(workbookPath))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                WriteEntry(
                    archive,
                    "[Content_Types].xml",
                    Encoding.UTF8.GetBytes(
                        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                        $"<Types xmlns=\"{contentTypesNamespace}\">" +
                        (useDefaultContentType
                            ? "<Default Extension=\"bin\" "
                            : $"<Override PartName=\"/{vbaProjectPartName}\" ") +
                        "ContentType=\"application/vnd.ms-office.vbaProject\"/>" +
                        additionalContentTypeDeclaration +
                        "</Types>"));
                WriteEntry(archive, vbaProjectPartName, partBytes);
                mutateArchive?.Invoke(archive);
            }

            return new WorkbookFixture(
                directoryPath,
                workbookPath,
                Convert.ToHexString(SHA256.HashData(partBytes)));
        }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }

        private static byte[] BuildProjectInformation(
            VbaProjectSystemKind systemKind,
            int codePage,
            string constants,
            string unicodeConstants,
            byte[]? unicodeConstantsBytes)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            WriteFixedRecord(writer, 0x0001, (uint)systemKind);
            WriteFixedRecord(writer, 0x0002, 0x0409u);
            WriteFixedRecord(writer, 0x0014, 0x0409u);
            writer.Write((ushort)0x0003);
            writer.Write(2u);
            writer.Write((ushort)codePage);
            WriteVariableRecord(writer, 0x0004, Encoding.ASCII.GetBytes("VBAProject"));
            WriteVariableRecord(writer, 0x0005, []);
            writer.Write((ushort)0x0040);
            writer.Write(0u);
            WriteVariableRecord(writer, 0x0006, []);
            writer.Write((ushort)0x003d);
            writer.Write(0u);
            WriteFixedRecord(writer, 0x0007, 0u);
            WriteFixedRecord(writer, 0x0008, 0u);
            writer.Write((ushort)0x0009);
            writer.Write(4u);
            writer.Write(0u);
            writer.Write((ushort)7);
            var mbcsConstants = constants.Length == 0
                ? []
                : Encoding.GetEncoding(codePage).GetBytes(constants);
            WriteVariableRecord(writer, 0x000c, mbcsConstants);
            writer.Write((ushort)0x003c);
            var unicode = unicodeConstantsBytes ?? Encoding.Unicode.GetBytes(unicodeConstants);
            writer.Write((uint)unicode.Length);
            writer.Write(unicode);
            return stream.ToArray();
        }

        private static byte[] CompressAsLiteralChunk(byte[] input)
        {
            using var payload = new MemoryStream();
            for (var offset = 0; offset < input.Length; offset += 8)
            {
                payload.WriteByte(0);
                payload.Write(input, offset, Math.Min(8, input.Length - offset));
            }

            var chunkLength = checked((ushort)(payload.Length + 2));
            var header = checked((ushort)(0xb000 | (chunkLength - 3)));
            using var container = new MemoryStream();
            container.WriteByte(0x01);
            Span<byte> headerBytes = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(headerBytes, header);
            container.Write(headerBytes);
            payload.Position = 0;
            payload.CopyTo(container);
            Span<byte> rawHeader = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(rawHeader, 0x3fff);
            container.Write(rawHeader);
            container.Write(new byte[4096]);
            return container.ToArray();
        }

        private static byte[] BuildCompoundFile(byte[] compressedDirectory)
        {
            const uint freeSector = 0xffffffff;
            const uint endOfChain = 0xfffffffe;
            const uint fatSector = 0xfffffffd;
            const int sectorLength = 512;
            var dataSectorCount = (compressedDirectory.Length + sectorLength - 1) / sectorLength;
            var file = new byte[sectorLength * (3 + dataSectorCount)];
            var header = file.AsSpan(0, sectorLength);
            new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 }.CopyTo(header);
            BinaryPrimitives.WriteUInt16LittleEndian(header[24..], 0x003e);
            BinaryPrimitives.WriteUInt16LittleEndian(header[26..], 0x0003);
            BinaryPrimitives.WriteUInt16LittleEndian(header[28..], 0xfffe);
            BinaryPrimitives.WriteUInt16LittleEndian(header[30..], 9);
            BinaryPrimitives.WriteUInt16LittleEndian(header[32..], 6);
            BinaryPrimitives.WriteUInt32LittleEndian(header[44..], 1);
            BinaryPrimitives.WriteUInt32LittleEndian(header[48..], 1);
            BinaryPrimitives.WriteUInt32LittleEndian(header[56..], 4096);
            BinaryPrimitives.WriteUInt32LittleEndian(header[60..], endOfChain);
            BinaryPrimitives.WriteUInt32LittleEndian(header[68..], endOfChain);
            header[76..].Fill(0xff);
            BinaryPrimitives.WriteUInt32LittleEndian(header[76..], 0);

            var fat = file.AsSpan(sectorLength, sectorLength);
            fat.Fill(0xff);
            BinaryPrimitives.WriteUInt32LittleEndian(fat, fatSector);
            BinaryPrimitives.WriteUInt32LittleEndian(fat[4..], endOfChain);
            for (var index = 0; index < dataSectorCount; index++)
            {
                var next = index == dataSectorCount - 1
                    ? endOfChain
                    : checked((uint)(index + 3));
                BinaryPrimitives.WriteUInt32LittleEndian(fat[((index + 2) * 4)..], next);
            }

            var directory = file.AsSpan(sectorLength * 2, sectorLength);
            WriteDirectoryEntry(directory, "Root Entry", 5, 1, endOfChain, 0);
            WriteDirectoryEntry(directory[128..], "VBA", 1, 2, endOfChain, 0);
            WriteDirectoryEntry(
                directory[256..],
                "dir",
                2,
                freeSector,
                2,
                checked((uint)compressedDirectory.Length));
            compressedDirectory.CopyTo(file.AsSpan(sectorLength * 3));
            return file;
        }

        private static void WriteDirectoryEntry(
            Span<byte> entry,
            string name,
            byte type,
            uint child,
            uint startSector,
            uint streamLength)
        {
            const uint freeSector = 0xffffffff;
            var nameBytes = Encoding.Unicode.GetBytes(name + '\0');
            nameBytes.CopyTo(entry);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[64..], checked((ushort)nameBytes.Length));
            entry[66] = type;
            entry[67] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(entry[68..], freeSector);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[72..], freeSector);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[76..], child);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[116..], startSector);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[120..], streamLength);
        }

        private static void WriteFixedRecord(BinaryWriter writer, ushort identifier, uint value)
        {
            writer.Write(identifier);
            writer.Write(4u);
            writer.Write(value);
        }

        private static void WriteVariableRecord(BinaryWriter writer, ushort identifier, byte[] value)
        {
            writer.Write(identifier);
            writer.Write((uint)value.Length);
            writer.Write(value);
        }

        internal static void WriteEntry(ZipArchive archive, string name, byte[] bytes)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
            using var stream = entry.Open();
            stream.Write(bytes);
        }

        internal static byte[] RenameCompoundDirectoryEntry(
            byte[] compoundFile,
            int entryIndex,
            string name)
        {
            const int sectorLength = 512;
            const int directoryEntryLength = 128;
            var entryOffset = sectorLength * 2 + directoryEntryLength * entryIndex;
            var nameBytes = Encoding.Unicode.GetBytes(name + '\0');
            Array.Clear(compoundFile, entryOffset, 64);
            nameBytes.CopyTo(compoundFile, entryOffset);
            BinaryPrimitives.WriteUInt16LittleEndian(
                compoundFile.AsSpan(entryOffset + 64),
                checked((ushort)nameBytes.Length));
            return compoundFile;
        }
    }
}
