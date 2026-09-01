using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using OpenMcdf;
using VbaLanguageServer.Syntax;

namespace VbaLanguageServer.Workspace;

internal enum VbaProjectIdentityReadFailureKind
{
    InvalidPackage,
    InvalidPackageTopology,
    InvalidVbaProjectPart,
    InvalidCompoundFile,
    InvalidCompressedDirectory,
    InvalidProjectInformation,
    UnsupportedCodePage,
    InvalidProjectName
}

internal sealed record VbaProjectIdentityRead(
    string VbaProjectName,
    int ProjectCodePage,
    VbaSourceTemplateContentIdentity SourceTemplateContentIdentity);

/// <summary>
/// Identifies the complete captured source-template package without exposing
/// the digest representation.
/// </summary>
internal sealed class VbaSourceTemplateContentIdentity
    : IEquatable<VbaSourceTemplateContentIdentity>
{
    private readonly string digest;

    private VbaSourceTemplateContentIdentity(string digest)
    {
        this.digest = digest;
    }

    public bool Equals(VbaSourceTemplateContentIdentity? other)
        => other is not null
            && digest.Equals(other.digest, StringComparison.Ordinal);

    public override bool Equals(object? obj)
        => obj is VbaSourceTemplateContentIdentity other
            && Equals(other);

    public override int GetHashCode()
        => StringComparer.Ordinal.GetHashCode(digest);

    internal static VbaSourceTemplateContentIdentity FromBytes(
        ReadOnlySpan<byte> bytes)
        => new(Convert.ToHexString(SHA256.HashData(bytes)));
}

internal sealed record VbaProjectIdentityReadFailure(
    VbaProjectIdentityReadFailureKind Kind,
    string Message);

internal sealed class VbaProjectIdentityReadResult
{
    private VbaProjectIdentityReadResult(
        VbaProjectIdentityRead? identity,
        VbaProjectIdentityReadFailure? failure)
    {
        Identity = identity;
        Failure = failure;
    }

    public VbaProjectIdentityRead? Identity { get; }

    public VbaProjectIdentityReadFailure? Failure { get; }

    public static VbaProjectIdentityReadResult Success(
        VbaProjectIdentityRead identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new(identity, failure: null);
    }

    public static VbaProjectIdentityReadResult Failed(
        VbaProjectIdentityReadFailureKind kind,
        string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new(
            identity: null,
            new VbaProjectIdentityReadFailure(kind, message));
    }
}

internal interface IVbaProjectIdentityReader
{
    VbaProjectIdentityReadResult Read(
        byte[] sourceTemplateBytes,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads one project identity from exact captured Open XML package bytes.
/// </summary>
internal sealed class OpenXmlVbaProjectIdentityReader
    : IVbaProjectIdentityReader
{
    private const string ContentTypesPartName = "[Content_Types].xml";
    private const string WorkbookPartName = "xl/workbook.xml";
    private const string WorkbookRelationshipsPartName =
        "xl/_rels/workbook.xml.rels";
    private const string VbaProjectPartName = "xl/vbaProject.bin";
    private const string WorkbookContentType =
        "application/vnd.ms-excel.sheet.macroEnabled.main+xml";
    private const string RelationshipsContentType =
        "application/vnd.openxmlformats-package.relationships+xml";
    private const string VbaProjectContentType =
        "application/vnd.ms-office.vbaProject";
    private const string VbaProjectRelationshipType =
        "http://schemas.microsoft.com/office/2006/relationships/vbaProject";
    private const int MaximumSourceTemplateLength = 512 * 1024 * 1024;
    private const int MaximumArchiveEntryCount = 65_536;
    private const int MaximumContentTypesPartLength = 1024 * 1024;
    private const int MaximumWorkbookRelationshipsPartLength = 1024 * 1024;
    private const int MaximumVbaProjectPartLength = 64 * 1024 * 1024;
    private const int MaximumCompressedDirectoryLength = 16 * 1024 * 1024;
    private const int MaximumDecompressedDirectoryLength = 32 * 1024 * 1024;

    private readonly int maximumSourceTemplateLength;

    private static readonly XNamespace ContentTypesNamespace =
        "http://schemas.openxmlformats.org/package/2006/content-types";
    private static readonly XNamespace RelationshipsNamespace =
        "http://schemas.openxmlformats.org/package/2006/relationships";

    private static readonly HashSet<string> BuiltInCompilerConstants = new(
        ["VBA6", "VBA7", "Win16", "Win32", "Win64", "Mac"],
        StringComparer.OrdinalIgnoreCase);

    public OpenXmlVbaProjectIdentityReader()
        : this(MaximumSourceTemplateLength)
    {
    }

    internal OpenXmlVbaProjectIdentityReader(int maximumSourceTemplateLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            maximumSourceTemplateLength,
            1);
        this.maximumSourceTemplateLength = maximumSourceTemplateLength;
    }

    public VbaProjectIdentityReadResult Read(
        byte[] sourceTemplateBytes,
        CancellationToken cancellationToken = default)
    {
        if (sourceTemplateBytes is null)
        {
            return VbaProjectIdentityReadResult.Failed(
                VbaProjectIdentityReadFailureKind.InvalidPackage,
                "The captured source-template package bytes are missing.");
        }

        if (sourceTemplateBytes.Length is <= 0
            || sourceTemplateBytes.Length > maximumSourceTemplateLength)
        {
            return VbaProjectIdentityReadResult.Failed(
                VbaProjectIdentityReadFailureKind.InvalidPackage,
                "The captured source-template package has an invalid or excessive length.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var capturedPackageBytes = sourceTemplateBytes.ToArray();
        try
        {
            using var packageStream = new MemoryStream(
                capturedPackageBytes,
                writable: false);
            using var archive = new ZipArchive(
                packageStream,
                ZipArchiveMode.Read,
                leaveOpen: false);
            var vbaProjectPart = ReadUniqueVbaProjectPart(
                archive,
                cancellationToken);
            var compressedDirectory = ReadDirectoryStream(vbaProjectPart);
            byte[] directory;
            try
            {
                directory = MsOvbaCompression.Decompress(
                    compressedDirectory,
                    MaximumDecompressedDirectoryLength,
                    cancellationToken);
            }
            catch (InvalidDataException exception)
            {
                throw new VbaProjectIdentityFormatException(
                    VbaProjectIdentityReadFailureKind.InvalidCompressedDirectory,
                    exception.Message,
                    exception);
            }

            var projectInformation = ProjectInformation.Read(directory);
            cancellationToken.ThrowIfCancellationRequested();
            return VbaProjectIdentityReadResult.Success(
                new VbaProjectIdentityRead(
                    projectInformation.ProjectName,
                    projectInformation.CodePage,
                    VbaSourceTemplateContentIdentity.FromBytes(
                        capturedPackageBytes)));
        }
        catch (VbaProjectIdentityFormatException exception)
        {
            return VbaProjectIdentityReadResult.Failed(
                exception.Kind,
                exception.Message);
        }
        catch (Exception exception) when (exception is InvalidDataException
            or IOException
            or NotSupportedException
            or XmlException)
        {
            return VbaProjectIdentityReadResult.Failed(
                VbaProjectIdentityReadFailureKind.InvalidPackage,
                $"The captured source-template bytes are not a supported Open XML package: {exception.Message}");
        }
    }

    private static byte[] ReadUniqueVbaProjectPart(
        ZipArchive archive,
        CancellationToken cancellationToken)
    {
        if (archive.Entries.Count > MaximumArchiveEntryCount)
        {
            throw new VbaProjectIdentityFormatException(
                VbaProjectIdentityReadFailureKind.InvalidPackageTopology,
                "The captured source-template package contains too many parts.");
        }

        var contentTypeEntries = archive.Entries
            .Where(entry => entry.FullName.Equals(
                ContentTypesPartName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (contentTypeEntries.Length != 1)
        {
            throw new VbaProjectIdentityFormatException(
                VbaProjectIdentityReadFailureKind.InvalidPackageTopology,
                "The captured source-template package must contain exactly one [Content_Types].xml part.");
        }

        var contentTypesEntry = contentTypeEntries[0];
        if (contentTypesEntry.Length is <= 0 or > MaximumContentTypesPartLength)
        {
            throw new VbaProjectIdentityFormatException(
                VbaProjectIdentityReadFailureKind.InvalidPackageTopology,
                "The captured source-template content-types part has an invalid or excessive length.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var contentTypesBytes = ReadBoundedEntry(
            contentTypesEntry,
            MaximumContentTypesPartLength,
            VbaProjectIdentityReadFailureKind.InvalidPackageTopology,
            "content-types",
            cancellationToken);
        XDocument document;
        try
        {
            using var contentTypesStream = new MemoryStream(
                contentTypesBytes,
                writable: false);
            using var xmlReader = XmlReader.Create(
                contentTypesStream,
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    MaxCharactersInDocument = MaximumContentTypesPartLength,
                    XmlResolver = null
                });
            document = XDocument.Load(xmlReader, LoadOptions.None);
        }
        catch (XmlException exception)
        {
            throw new VbaProjectIdentityFormatException(
                VbaProjectIdentityReadFailureKind.InvalidPackageTopology,
                $"The captured source-template content-types part is malformed: {exception.Message}",
                exception);
        }

        if (document.Root?.Name != ContentTypesNamespace + "Types")
        {
            throw new VbaProjectIdentityFormatException(
                VbaProjectIdentityReadFailureKind.InvalidPackageTopology,
                "The captured source-template content type declarations are not in the OPC namespace.");
        }

        var elements = document.Root.Elements().ToArray();
        if (elements.Any(element =>
                element.Name != ContentTypesNamespace + "Default"
                && element.Name != ContentTypesNamespace + "Override"))
        {
            throw new VbaProjectIdentityFormatException(
                VbaProjectIdentityReadFailureKind.InvalidPackageTopology,
                "The captured source-template contains a non-OPC content type declaration.");
        }

        var defaults = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var declaration in elements.Where(element =>
                     element.Name == ContentTypesNamespace + "Default"))
        {
            var extension = (string?)declaration.Attribute("Extension");
            var contentType = (string?)declaration.Attribute("ContentType");
            if (string.IsNullOrWhiteSpace(extension)
                || string.IsNullOrWhiteSpace(contentType)
                || !defaults.TryAdd(extension, contentType))
            {
                throw new VbaProjectIdentityFormatException(
                    VbaProjectIdentityReadFailureKind.InvalidPackageTopology,
                    "The captured source-template contains invalid or ambiguous default content types.");
            }
        }

        var overrides = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var declaration in elements.Where(element =>
                     element.Name == ContentTypesNamespace + "Override"))
        {
            var partName = (string?)declaration.Attribute("PartName");
            var contentType = (string?)declaration.Attribute("ContentType");
            if (string.IsNullOrWhiteSpace(partName)
                || partName[0] != '/'
                || string.IsNullOrWhiteSpace(contentType)
                || !overrides.TryAdd(partName, contentType))
            {
                throw new VbaProjectIdentityFormatException(
                    VbaProjectIdentityReadFailureKind.InvalidPackageTopology,
                    "The captured source-template contains invalid or ambiguous content type overrides.");
            }
        }

        string? ResolveContentType(string partName)
        {
            if (overrides.TryGetValue($"/{partName}", out var overrideContentType))
            {
                return overrideContentType;
            }

            var extension = Path.GetExtension(partName).TrimStart('.');
            return extension.Length != 0
                   && defaults.TryGetValue(extension, out var defaultContentType)
                ? defaultContentType
                : null;
        }

        if (!string.Equals(
                ResolveContentType(VbaProjectPartName),
                VbaProjectContentType,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new VbaProjectIdentityFormatException(
                VbaProjectIdentityReadFailureKind.InvalidVbaProjectPart,
                "The effective content type of xl/vbaProject.bin is not the VBA project content type.");
        }

        ValidateWorkbookVbaProjectRelationship(
            archive,
            ResolveContentType,
            cancellationToken);

        var vbaProjectParts = archive.Entries
            .Where(entry => entry.Name.Length != 0)
            .Where(entry => string.Equals(
                ResolveContentType(entry.FullName),
                VbaProjectContentType,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (vbaProjectParts.Length != 1
            || !vbaProjectParts[0].FullName.Equals(
                VbaProjectPartName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new VbaProjectIdentityFormatException(
                VbaProjectIdentityReadFailureKind.InvalidPackageTopology,
                "The captured source-template package must contain exactly one xl/vbaProject.bin VBA project part.");
        }

        var part = vbaProjectParts[0];
        if (part.Length is <= 0 or > MaximumVbaProjectPartLength)
        {
            throw new VbaProjectIdentityFormatException(
                VbaProjectIdentityReadFailureKind.InvalidVbaProjectPart,
                "The captured source-template VBA project part has an invalid or excessive length.");
        }

        return ReadBoundedEntry(
            part,
            MaximumVbaProjectPartLength,
            VbaProjectIdentityReadFailureKind.InvalidVbaProjectPart,
            "VBA project",
            cancellationToken);
    }

    private static void ValidateWorkbookVbaProjectRelationship(
        ZipArchive archive,
        Func<string, string?> resolveContentType,
        CancellationToken cancellationToken)
    {
        var workbookParts = archive.Entries
            .Where(entry => entry.FullName.Equals(
                WorkbookPartName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (workbookParts.Length != 1
            || workbookParts[0].Length <= 0
            || !string.Equals(
                resolveContentType(WorkbookPartName),
                WorkbookContentType,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new VbaProjectIdentityFormatException(
                VbaProjectIdentityReadFailureKind.InvalidPackageTopology,
                "The captured source-template package must contain exactly one macro-enabled xl/workbook.xml part.");
        }

        if (!string.Equals(
                resolveContentType(WorkbookRelationshipsPartName),
                RelationshipsContentType,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new VbaProjectIdentityFormatException(
                VbaProjectIdentityReadFailureKind.InvalidPackageTopology,
                "The workbook relationships part does not have the OPC relationships content type.");
        }

        var relationshipParts = archive.Entries
            .Where(entry => entry.FullName.Equals(
                WorkbookRelationshipsPartName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (relationshipParts.Length != 1)
        {
            throw new VbaProjectIdentityFormatException(
                VbaProjectIdentityReadFailureKind.InvalidPackageTopology,
                "The captured source-template package must contain exactly one workbook relationships part.");
        }

        var relationshipBytes = ReadBoundedEntry(
            relationshipParts[0],
            MaximumWorkbookRelationshipsPartLength,
            VbaProjectIdentityReadFailureKind.InvalidPackageTopology,
            "workbook relationships",
            cancellationToken);
        XDocument relationships;
        try
        {
            using var relationshipStream = new MemoryStream(
                relationshipBytes,
                writable: false);
            using var xmlReader = XmlReader.Create(
                relationshipStream,
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    MaxCharactersInDocument =
                        MaximumWorkbookRelationshipsPartLength,
                    XmlResolver = null
                });
            relationships = XDocument.Load(xmlReader, LoadOptions.None);
        }
        catch (XmlException exception)
        {
            throw new VbaProjectIdentityFormatException(
                VbaProjectIdentityReadFailureKind.InvalidPackageTopology,
                $"The workbook relationships part is malformed: {exception.Message}",
                exception);
        }

        if (relationships.Root?.Name
            != RelationshipsNamespace + "Relationships"
            || relationships.Root.Elements().Any(element =>
                element.Name != RelationshipsNamespace + "Relationship"))
        {
            throw new VbaProjectIdentityFormatException(
                VbaProjectIdentityReadFailureKind.InvalidPackageTopology,
                "The workbook relationships are not in the OPC relationships namespace.");
        }

        var vbaRelationships = relationships.Root
            .Elements(RelationshipsNamespace + "Relationship")
            .Where(element => string.Equals(
                (string?)element.Attribute("Type"),
                VbaProjectRelationshipType,
                StringComparison.Ordinal))
            .ToArray();
        if (vbaRelationships.Length != 1)
        {
            throw new VbaProjectIdentityFormatException(
                VbaProjectIdentityReadFailureKind.InvalidPackageTopology,
                "The workbook must contain exactly one VBA project relationship.");
        }

        var relationship = vbaRelationships[0];
        var identifier = (string?)relationship.Attribute("Id");
        var target = (string?)relationship.Attribute("Target");
        var targetMode = (string?)relationship.Attribute("TargetMode");
        if (string.IsNullOrWhiteSpace(identifier)
            || string.IsNullOrWhiteSpace(target)
            || target.Contains('\\')
            || target.Contains('?')
            || target.Contains('#')
            || !(target.Equals(
                    "vbaProject.bin",
                    StringComparison.OrdinalIgnoreCase)
                || target.Equals(
                    "./vbaProject.bin",
                    StringComparison.OrdinalIgnoreCase))
            || targetMode is not null
                && !targetMode.Equals(
                    "Internal",
                    StringComparison.OrdinalIgnoreCase))
        {
            throw new VbaProjectIdentityFormatException(
                VbaProjectIdentityReadFailureKind.InvalidPackageTopology,
                "The workbook VBA project relationship must be an internal target of canonical xl/vbaProject.bin.");
        }
    }

    private static byte[] ReadBoundedEntry(
        ZipArchiveEntry entry,
        int maximumLength,
        VbaProjectIdentityReadFailureKind failureKind,
        string description,
        CancellationToken cancellationToken)
    {
        using var entryStream = entry.Open();
        using var buffer = new MemoryStream(
            checked((int)Math.Min(entry.Length, maximumLength)));
        var copyBuffer = new byte[81920];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = maximumLength - checked((int)buffer.Length);
            var read = entryStream.Read(
                copyBuffer,
                0,
                Math.Min(copyBuffer.Length, remaining + 1));
            if (read == 0)
            {
                break;
            }

            if (read > remaining)
            {
                throw new VbaProjectIdentityFormatException(
                    failureKind,
                    $"The captured source-template {description} part exceeds its configured bound.");
            }

            buffer.Write(copyBuffer, 0, read);
        }

        if (buffer.Length <= 0)
        {
            throw new VbaProjectIdentityFormatException(
                failureKind,
                $"The captured source-template {description} part is empty.");
        }

        return buffer.ToArray();
    }

    private static byte[] ReadDirectoryStream(byte[] vbaProjectPart)
    {
        try
        {
            using var partStream = new MemoryStream(
                vbaProjectPart,
                writable: false);
            using var root = RootStorage.Open(
                partStream,
                StorageModeFlags.LeaveOpen);
            if (!root.TryOpenStorage("VBA", out var vbaStorage))
            {
                throw new VbaProjectIdentityFormatException(
                    VbaProjectIdentityReadFailureKind.InvalidCompoundFile,
                    "The VBA project compound file does not contain the required VBA storage.");
            }

            if (!vbaStorage.TryOpenStream("dir", out var directory))
            {
                throw new VbaProjectIdentityFormatException(
                    VbaProjectIdentityReadFailureKind.InvalidCompoundFile,
                    "The VBA project compound file does not contain the required VBA directory stream.");
            }

            using (directory)
            {
                if (directory.Length is <= 0 or > MaximumCompressedDirectoryLength)
                {
                    throw new VbaProjectIdentityFormatException(
                        VbaProjectIdentityReadFailureKind.InvalidCompoundFile,
                        "The VBA directory stream has an invalid or excessive length.");
                }

                var bytes = new byte[checked((int)directory.Length)];
                directory.ReadExactly(bytes);
                return bytes;
            }
        }
        catch (VbaProjectIdentityFormatException)
        {
            throw;
        }
        catch (Exception exception) when (exception is FileFormatException
            or InvalidDataException
            or IOException
            or ArgumentException
            or OverflowException)
        {
            throw new VbaProjectIdentityFormatException(
                VbaProjectIdentityReadFailureKind.InvalidCompoundFile,
                $"The VBA project compound file is malformed: {exception.Message}",
                exception);
        }
    }

    private sealed record ProjectInformation(string ProjectName, int CodePage)
    {
        public static ProjectInformation Read(ReadOnlySpan<byte> directory)
        {
            try
            {
                var reader = new RecordReader(directory);
                reader.ExpectRecord(0x0001, 4);
                var systemKind = reader.ReadUInt32();
                if (systemKind > 3)
                {
                    throw new VbaProjectIdentityFormatException(
                        VbaProjectIdentityReadFailureKind.InvalidProjectInformation,
                        $"The VBA PROJECTSYSKIND value '{systemKind}' is unsupported.");
                }

                if (reader.PeekUInt16() == 0x004a)
                {
                    reader.ExpectRecord(0x004a, 4);
                    _ = reader.ReadUInt32();
                }

                reader.ExpectRecord(0x0002, 4);
                reader.ExpectUInt32(0x0409, "PROJECTLCID value");
                reader.ExpectRecord(0x0014, 4);
                reader.ExpectUInt32(0x0409, "PROJECTLCIDINVOKE value");
                reader.ExpectRecord(0x0003, 2);
                var codePage = reader.ReadUInt16();
                var encoding = GetEncoding(codePage);
                reader.ExpectVariableRecord(0x0004);
                var projectNameBytes = reader.ReadVariableBytes(
                    minimumLength: 1,
                    maximumLength: 128,
                    VbaProjectIdentityReadFailureKind.InvalidProjectName,
                    "PROJECTNAME");
                if (projectNameBytes.Contains((byte)0))
                {
                    throw new VbaProjectIdentityFormatException(
                        VbaProjectIdentityReadFailureKind.InvalidProjectName,
                        "The VBA PROJECTNAME record contains a null character.");
                }

                string projectName;
                try
                {
                    projectName = encoding.GetString(projectNameBytes);
                }
                catch (DecoderFallbackException exception)
                {
                    throw new VbaProjectIdentityFormatException(
                        VbaProjectIdentityReadFailureKind.InvalidProjectName,
                        "The VBA PROJECTNAME record is not valid in its declared code page.",
                        exception);
                }

                reader.ExpectVariableRecord(0x0005);
                var mbcsDocString = reader.ReadVariableBytes(
                    minimumLength: 0,
                    maximumLength: 2000,
                    VbaProjectIdentityReadFailureKind.InvalidProjectInformation,
                    "PROJECTDOCSTRING");
                reader.ExpectUInt16(
                    0x0040,
                    "PROJECTDOCSTRING Unicode marker");
                var unicodeDocString = reader.ReadLengthPrefixedBytes(
                    requireEvenLength: true,
                    maximumLength: 4000);
                ValidateEquivalentStrings(
                    encoding,
                    mbcsDocString,
                    unicodeDocString,
                    "PROJECTDOCSTRING");
                reader.ExpectVariableRecord(0x0006);
                var firstHelpFile = reader.ReadVariableBytes(
                    minimumLength: 0,
                    maximumLength: 260,
                    VbaProjectIdentityReadFailureKind.InvalidProjectInformation,
                    "PROJECTHELPFILEPATH");
                reader.ExpectUInt16(
                    0x003d,
                    "PROJECTHELPFILEPATH second-path marker");
                var secondHelpFile = reader.ReadLengthPrefixedBytes(
                    requireEvenLength: false,
                    maximumLength: 260);
                ValidateHelpFilePaths(
                    encoding,
                    firstHelpFile,
                    secondHelpFile);
                reader.ExpectRecord(0x0007, 4);
                _ = reader.ReadUInt32();
                reader.ExpectRecord(0x0008, 4);
                reader.ExpectUInt32(0, "PROJECTLIBFLAGS value");
                reader.ExpectUInt16(
                    0x0009,
                    "PROJECTVERSION record identifier");
                reader.ExpectUInt32(
                    4,
                    "PROJECTVERSION reserved size");
                _ = reader.ReadUInt32();
                _ = reader.ReadUInt16();

                if (reader.HasRemaining && reader.PeekUInt16() == 0x000c)
                {
                    reader.ExpectVariableRecord(0x000c);
                    var mbcsConstants = reader.ReadVariableBytes(
                        minimumLength: 0,
                        maximumLength: 1015,
                        VbaProjectIdentityReadFailureKind.InvalidProjectInformation,
                        "PROJECTCONSTANTS");
                    reader.ExpectUInt16(
                        0x003c,
                        "PROJECTCONSTANTS Unicode marker");
                    var unicodeConstants = reader.ReadLengthPrefixedBytes(
                        requireEvenLength: true);
                    ValidateConstants(
                        encoding,
                        mbcsConstants,
                        unicodeConstants);
                }

                return new ProjectInformation(projectName, codePage);
            }
            catch (VbaProjectIdentityFormatException)
            {
                throw;
            }
            catch (Exception exception) when (exception is InvalidDataException
                or DecoderFallbackException)
            {
                throw new VbaProjectIdentityFormatException(
                    VbaProjectIdentityReadFailureKind.InvalidProjectInformation,
                    exception.Message,
                    exception);
            }
        }

        private static void ValidateConstants(
            Encoding encoding,
            ReadOnlySpan<byte> mbcsBytes,
            ReadOnlySpan<byte> unicodeBytes)
        {
            if (mbcsBytes.Contains((byte)0)
                || ContainsUtf16Null(unicodeBytes))
            {
                throw new InvalidDataException(
                    "The VBA PROJECTCONSTANTS record contains a null character.");
            }

            var mbcs = encoding.GetString(mbcsBytes);
            var unicode = new UnicodeEncoding(
                bigEndian: false,
                byteOrderMark: false,
                throwOnInvalidBytes: true).GetString(unicodeBytes);
            if (!mbcs.Equals(unicode, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The VBA PROJECTCONSTANTS MBCS and Unicode values do not match.");
            }

            ValidateConstantsText(unicode);
        }

        private static void ValidateConstantsText(string text)
        {
            if (text.Length == 0)
            {
                return;
            }

            var constants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in text.Split(" : ", StringSplitOptions.None))
            {
                var assignment = item.Split(" = ", StringSplitOptions.None);
                if (assignment.Length != 2
                    || !VbaIdentifier.IsIdentifier(assignment[0])
                    || !TryParseProjectConstantValue(assignment[1], out _))
                {
                    throw new InvalidDataException(
                        $"The VBA PROJECTCONSTANTS value '{item}' is malformed.");
                }

                if (BuiltInCompilerConstants.Contains(assignment[0]))
                {
                    throw new InvalidDataException(
                        $"The VBA project constant '{assignment[0]}' collides with a built-in compiler constant.");
                }

                if (!constants.Add(assignment[0]))
                {
                    throw new InvalidDataException(
                        $"The VBA project constant '{assignment[0]}' is duplicated case-insensitively.");
                }
            }
        }

        private static bool TryParseProjectConstantValue(
            string text,
            out short value)
        {
            var digits = text.AsSpan();
            if (!digits.IsEmpty && digits[0] == '-')
            {
                digits = digits[1..];
            }

            if (digits.Length is < 1 or > 5
                || digits.ContainsAnyExceptInRange('0', '9'))
            {
                value = default;
                return false;
            }

            return short.TryParse(
                text,
                System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture,
                out value);
        }

        private static void ValidateEquivalentStrings(
            Encoding encoding,
            ReadOnlySpan<byte> mbcsBytes,
            ReadOnlySpan<byte> unicodeBytes,
            string recordName)
        {
            if (mbcsBytes.Contains((byte)0)
                || ContainsUtf16Null(unicodeBytes))
            {
                throw new InvalidDataException(
                    $"The VBA {recordName} record contains a null character.");
            }

            var mbcs = encoding.GetString(mbcsBytes);
            var unicode = new UnicodeEncoding(
                bigEndian: false,
                byteOrderMark: false,
                throwOnInvalidBytes: true).GetString(unicodeBytes);
            if (!mbcs.Equals(unicode, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The VBA {recordName} MBCS and Unicode values do not match.");
            }
        }

        private static void ValidateHelpFilePaths(
            Encoding encoding,
            ReadOnlySpan<byte> firstPath,
            ReadOnlySpan<byte> secondPath)
        {
            if (firstPath.Contains((byte)0)
                || secondPath.Contains((byte)0)
                || !firstPath.SequenceEqual(secondPath))
            {
                throw new InvalidDataException(
                    "The VBA PROJECTHELPFILEPATH values are invalid or do not match.");
            }

            _ = encoding.GetString(firstPath);
            _ = encoding.GetString(secondPath);
        }

        private static bool ContainsUtf16Null(ReadOnlySpan<byte> bytes)
        {
            for (var index = 0; index < bytes.Length; index += 2)
            {
                if (bytes[index] == 0 && bytes[index + 1] == 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static Encoding GetEncoding(int codePage)
        {
            if (codePage == 0)
            {
                throw new VbaProjectIdentityFormatException(
                    VbaProjectIdentityReadFailureKind.UnsupportedCodePage,
                    "The VBA PROJECTCODEPAGE value '0' is unsupported.");
            }

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            try
            {
                return Encoding.GetEncoding(
                    codePage,
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback);
            }
            catch (Exception exception) when (exception is ArgumentException
                or NotSupportedException)
            {
                throw new VbaProjectIdentityFormatException(
                    VbaProjectIdentityReadFailureKind.UnsupportedCodePage,
                    $"The VBA PROJECTCODEPAGE value '{codePage}' is unsupported.",
                    exception);
            }
        }
    }

    private ref struct RecordReader(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> bytes = bytes;
        private int offset;
        private int variableLength;

        public bool HasRemaining => offset < bytes.Length;

        public ushort PeekUInt16()
        {
            EnsureAvailable(2);
            return BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
        }

        public ushort ReadUInt16()
        {
            var value = PeekUInt16();
            offset += 2;
            return value;
        }

        public uint ReadUInt32()
        {
            EnsureAvailable(4);
            var value = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
            offset += 4;
            return value;
        }

        public void ExpectRecord(ushort identifier, uint size)
        {
            ExpectUInt16(identifier, $"record 0x{identifier:x4} identifier");
            ExpectUInt32(size, $"record 0x{identifier:x4} size");
        }

        public void ExpectVariableRecord(ushort identifier)
        {
            ExpectUInt16(identifier, $"record 0x{identifier:x4} identifier");
            var length = ReadUInt32();
            if (length > int.MaxValue)
            {
                throw new InvalidDataException(
                    "A VBA directory record length exceeds the supported bound.");
            }

            variableLength = (int)length;
            EnsureAvailable(variableLength);
        }

        public byte[] ReadVariableBytes(
            int minimumLength,
            int maximumLength,
            VbaProjectIdentityReadFailureKind failureKind,
            string recordName)
        {
            if (variableLength < minimumLength
                || variableLength > maximumLength)
            {
                throw new VbaProjectIdentityFormatException(
                    failureKind,
                    $"The VBA {recordName} record length must be between {minimumLength} and {maximumLength} bytes.");
            }

            var value = bytes.Slice(offset, variableLength).ToArray();
            offset += variableLength;
            variableLength = 0;
            return value;
        }

        public byte[] ReadLengthPrefixedBytes(
            bool requireEvenLength,
            int maximumLength = int.MaxValue)
        {
            var length = ReadUInt32();
            if (length > maximumLength
                || requireEvenLength && (length & 1) != 0)
            {
                throw new InvalidDataException(
                    "A VBA directory string length is invalid.");
            }

            EnsureAvailable((int)length);
            var value = bytes.Slice(offset, (int)length).ToArray();
            offset += (int)length;
            return value;
        }

        public void ExpectUInt16(ushort expected, string field)
        {
            var actual = ReadUInt16();
            if (actual != expected)
            {
                throw new InvalidDataException(
                    $"The VBA {field} is 0x{actual:x4}; expected 0x{expected:x4}.");
            }
        }

        public void ExpectUInt32(uint expected, string field)
        {
            var actual = ReadUInt32();
            if (actual != expected)
            {
                throw new InvalidDataException(
                    $"The VBA {field} is {actual}; expected {expected}.");
            }
        }

        private void EnsureAvailable(int length)
        {
            if (length < 0 || length > bytes.Length - offset)
            {
                throw new InvalidDataException(
                    "A VBA directory record is truncated or exceeds its bounds.");
            }
        }
    }

    private sealed class VbaProjectIdentityFormatException
        : Exception
    {
        public VbaProjectIdentityFormatException(
            VbaProjectIdentityReadFailureKind kind,
            string message,
            Exception? innerException = null)
            : base(message, innerException)
        {
            Kind = kind;
        }

        public VbaProjectIdentityReadFailureKind Kind { get; }
    }
}
