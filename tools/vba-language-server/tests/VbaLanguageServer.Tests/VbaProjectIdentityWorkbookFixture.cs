using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace VbaLanguageServer.Tests;

internal sealed record VbaProjectIdentityWorkbookFixtureOptions
{
    public string ProjectName { get; init; } = "ContainingProject";

    public int CodePage { get; init; } = 1252;

    public byte[]? ProjectNameBytes { get; init; }

    public byte[]? ProjectInformationBytes { get; init; }

    public byte[]? CompressedDirectoryBytes { get; init; }

    public byte[]? VbaProjectPartBytes { get; init; }

    public string VbaStorageName { get; init; } = "VBA";

    public string DirectoryStreamName { get; init; } = "dir";

    public bool IncludeContentTypesPart { get; init; } = true;

    public string? ContentTypesXml { get; init; }

    public bool IncludeVbaProjectPart { get; init; } = true;

    public string VbaProjectPartName { get; init; } = "xl/vbaProject.bin";

    public string VbaProjectContentType { get; init; } =
        "application/vnd.ms-office.vbaProject";

    public bool IncludeWorkbookPart { get; init; } = true;

    public bool IncludeWorkbookRelationshipsPart { get; init; } = true;

    public string? WorkbookRelationshipsXml { get; init; }

    public IReadOnlyList<VbaProjectIdentityPackageEntry> AdditionalEntries
    { get; init; } = [];
}

internal sealed record VbaProjectIdentityPackageEntry(
    string Name,
    byte[] Bytes);

/// <summary>
/// Builds deterministic, self-contained OPC/CFB/MS-OVBA test packages.
/// </summary>
internal static class VbaProjectIdentityWorkbookFixture
{
    private static readonly DateTimeOffset FixedEntryTimestamp =
        new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static byte[] Create(
        string projectName,
        int codePage)
        => Create(new VbaProjectIdentityWorkbookFixtureOptions
        {
            ProjectName = projectName,
            CodePage = codePage
        });

    public static byte[] Create(
        VbaProjectIdentityWorkbookFixtureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var projectNameBytes = options.ProjectNameBytes
            ?? Encoding.GetEncoding(options.CodePage).GetBytes(
                options.ProjectName);
        var projectInformation = options.ProjectInformationBytes
            ?? CreateProjectInformation(options.CodePage, projectNameBytes);
        var compressedDirectory = options.CompressedDirectoryBytes
            ?? CompressDirectory(projectInformation);
        var vbaProjectPart = options.VbaProjectPartBytes
            ?? CreateCompoundFile(
                compressedDirectory,
                options.VbaStorageName,
                options.DirectoryStreamName);

        using var package = new MemoryStream();
        using (var archive = new ZipArchive(
                   package,
                   ZipArchiveMode.Create,
                   leaveOpen: true))
        {
            if (options.IncludeContentTypesPart)
            {
                WriteEntry(
                    archive,
                    "[Content_Types].xml",
                    Encoding.UTF8.GetBytes(
                        options.ContentTypesXml
                        ?? CreateContentTypesXml(
                            options.VbaProjectPartName,
                            options.VbaProjectContentType)));
            }

            WriteEntry(
                archive,
                "_rels/.rels",
                Encoding.UTF8.GetBytes(CreatePackageRelationshipsXml()));
            if (options.IncludeWorkbookPart)
            {
                WriteEntry(
                    archive,
                    "xl/workbook.xml",
                    Encoding.UTF8.GetBytes(
                        "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
                        + "<sheets/>"
                        + "</workbook>"));
            }

            if (options.IncludeWorkbookRelationshipsPart)
            {
                WriteEntry(
                    archive,
                    "xl/_rels/workbook.xml.rels",
                    Encoding.UTF8.GetBytes(
                        options.WorkbookRelationshipsXml
                        ?? CreateWorkbookRelationshipsXml()));
            }

            if (options.IncludeVbaProjectPart)
            {
                WriteEntry(
                    archive,
                    options.VbaProjectPartName,
                    vbaProjectPart);
            }

            foreach (var entry in options.AdditionalEntries)
            {
                WriteEntry(archive, entry.Name, entry.Bytes);
            }
        }

        return package.ToArray();
    }

    internal static byte[] CreateProjectInformation(
        int codePage,
        byte[] projectNameBytes)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        WriteFixedRecord(writer, 0x0001, 3u);
        WriteFixedRecord(writer, 0x0002, 0x0409u);
        WriteFixedRecord(writer, 0x0014, 0x0409u);
        writer.Write((ushort)0x0003);
        writer.Write(2u);
        writer.Write(checked((ushort)codePage));
        WriteVariableRecord(writer, 0x0004, projectNameBytes);
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
        return stream.ToArray();
    }

    internal static byte[] CompressDirectory(byte[] input)
    {
        if (input.Length > 4096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input),
                "The deterministic fixture supports one raw MS-OVBA chunk.");
        }

        using var container = new MemoryStream();
        container.WriteByte(0x01);
        Span<byte> rawHeader = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(rawHeader, 0x3fff);
        container.Write(rawHeader);
        container.Write(input);
        container.Write(new byte[4096 - input.Length]);
        return container.ToArray();
    }

    internal static byte[] CreateCompoundFile(
        byte[] compressedDirectory,
        string vbaStorageName = "VBA",
        string directoryStreamName = "dir")
    {
        const uint freeSector = 0xffffffff;
        const uint endOfChain = 0xfffffffe;
        const uint fatSector = 0xfffffffd;
        const int sectorLength = 512;
        var dataSectorCount =
            (compressedDirectory.Length + sectorLength - 1) / sectorLength;
        var file = new byte[sectorLength * (3 + dataSectorCount)];
        var header = file.AsSpan(0, sectorLength);
        new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 }
            .CopyTo(header);
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
            BinaryPrimitives.WriteUInt32LittleEndian(
                fat[((index + 2) * 4)..],
                next);
        }

        var directory = file.AsSpan(sectorLength * 2, sectorLength);
        WriteDirectoryEntry(directory, "Root Entry", 5, 1, endOfChain, 0);
        WriteDirectoryEntry(
            directory[128..],
            vbaStorageName,
            1,
            2,
            endOfChain,
            0);
        WriteDirectoryEntry(
            directory[256..],
            directoryStreamName,
            2,
            freeSector,
            2,
            checked((uint)compressedDirectory.Length));
        compressedDirectory.CopyTo(file.AsSpan(sectorLength * 3));
        return file;
    }

    internal static string CreateContentTypesXml(
        string vbaProjectPartName,
        string vbaProjectContentType)
        => "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
            + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
            + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
            + "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
            + "<Default Extension=\"bin\" ContentType=\"application/octet-stream\"/>"
            + "<Override PartName=\"/xl/workbook.xml\" "
            + "ContentType=\"application/vnd.ms-excel.sheet.macroEnabled.main+xml\"/>"
            + $"<Override PartName=\"/{vbaProjectPartName}\" "
            + $"ContentType=\"{vbaProjectContentType}\"/>"
            + "</Types>";

    internal static string CreateWorkbookRelationshipsXml(
        string target = "vbaProject.bin",
        string relationshipType =
            "http://schemas.microsoft.com/office/2006/relationships/vbaProject",
        string? targetMode = null,
        bool duplicate = false)
    {
        var targetModeAttribute = targetMode is null
            ? string.Empty
            : $" TargetMode=\"{targetMode}\"";
        var relationship =
            $"<Relationship Id=\"rIdVba\" Type=\"{relationshipType}\" "
            + $"Target=\"{target}\"{targetModeAttribute}/>";
        var duplicateRelationship = duplicate
            ? relationship.Replace("rIdVba", "rIdVbaDuplicate", StringComparison.Ordinal)
            : string.Empty;
        return "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
            + relationship
            + duplicateRelationship
            + "</Relationships>";
    }

    private static string CreatePackageRelationshipsXml()
        => "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
            + "<Relationship Id=\"rIdWorkbook\" "
            + "Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" "
            + "Target=\"xl/workbook.xml\"/>"
            + "</Relationships>";

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
        BinaryPrimitives.WriteUInt16LittleEndian(
            entry[64..],
            checked((ushort)nameBytes.Length));
        entry[66] = type;
        entry[67] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(entry[68..], freeSector);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[72..], freeSector);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[76..], child);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[116..], startSector);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[120..], streamLength);
    }

    private static void WriteFixedRecord(
        BinaryWriter writer,
        ushort identifier,
        uint value)
    {
        writer.Write(identifier);
        writer.Write(4u);
        writer.Write(value);
    }

    private static void WriteVariableRecord(
        BinaryWriter writer,
        ushort identifier,
        byte[] value)
    {
        writer.Write(identifier);
        writer.Write((uint)value.Length);
        writer.Write(value);
    }

    private static void WriteEntry(
        ZipArchive archive,
        string name,
        byte[] bytes)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        entry.LastWriteTime = FixedEntryTimestamp;
        using var stream = entry.Open();
        stream.Write(bytes);
    }
}
