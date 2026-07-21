using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using OpenMcdf;
using VbaDev.App.Debugging;

namespace VbaDev.Infrastructure.Debugging;

/// <summary>
/// Reads the persisted VBA compilation settings from an exact generated .xlsm artifact.
/// </summary>
public sealed class OpenXmlDebugCompilationSettingsReader
    : IDebugCompilationSettingsReader
{
    private const string ContentTypesPartName = "[Content_Types].xml";
    private const string VbaProjectPartName = "xl/vbaProject.bin";
    private const string VbaProjectContentType = "application/vnd.ms-office.vbaProject";
    private const int MaximumVbaProjectPartLength = 64 * 1024 * 1024;
    private const int MaximumCompressedDirectoryLength = 16 * 1024 * 1024;
    private const int MaximumDecompressedDirectoryLength = 32 * 1024 * 1024;

    private static readonly XNamespace ContentTypesNamespace =
        "http://schemas.openxmlformats.org/package/2006/content-types";

    private static readonly HashSet<string> BuiltInCompilerConstants = new(
        ["VBA6", "VBA7", "Win16", "Win32", "Win64", "Mac"],
        StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public DebugCompilationSettings Read(string workbookPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookPath);
        if (!Path.GetExtension(workbookPath).Equals(".xlsm", StringComparison.OrdinalIgnoreCase))
        {
            throw new DebugSetupException(
                $"Debug compilation settings require an .xlsm workbook: '{workbookPath}'.");
        }

        try
        {
            using var workbook = File.Open(
                workbookPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var archive = new ZipArchive(workbook, ZipArchiveMode.Read, leaveOpen: false);
            var partBytes = ReadUniqueVbaProjectPart(archive);
            var fingerprint = Convert.ToHexString(SHA256.HashData(partBytes));
            var directoryBytes = ReadDirectoryStream(partBytes);
            var decompressedDirectory = MsOvbaCompression.Decompress(
                directoryBytes,
                MaximumDecompressedDirectoryLength);
            var records = ProjectInformationRecords.Parse(decompressedDirectory);
            return new DebugCompilationSettings(
                records.SystemKind,
                records.CodePage,
                records.ProjectConstants,
                fingerprint);
        }
        catch (DebugSetupException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
            or InvalidDataException
            or FileFormatException
            or UnauthorizedAccessException
            or NotSupportedException
            or DecoderFallbackException
            or System.Xml.XmlException)
        {
            throw new DebugSetupException(
                $"Could not read VBA compilation settings from generated workbook '{workbookPath}'.",
                exception);
        }
    }

    private static byte[] ReadUniqueVbaProjectPart(ZipArchive archive)
    {
        var contentTypeEntries = archive.Entries
            .Where(entry => entry.FullName.Equals(
                ContentTypesPartName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (contentTypeEntries.Length != 1)
        {
            throw new DebugSetupException(
                "The generated workbook must contain exactly one [Content_Types].xml part.");
        }

        ZipArchiveEntry[] vbaProjectParts;
        using (var contentTypesStream = contentTypeEntries[0].Open())
        {
            var document = XDocument.Load(contentTypesStream, LoadOptions.None);
            if (document.Root?.Name != ContentTypesNamespace + "Types")
            {
                throw new DebugSetupException(
                    "The generated workbook content type declarations are not in the OPC namespace.");
            }

            var elements = document.Root.Elements().ToArray();
            if (elements.Any(element =>
                    element.Name != ContentTypesNamespace + "Default"
                    && element.Name != ContentTypesNamespace + "Override"))
            {
                throw new DebugSetupException(
                    "The generated workbook contains a non-OPC content type declaration.");
            }

            var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var declaration in elements.Where(element =>
                         element.Name == ContentTypesNamespace + "Default"))
            {
                var extension = (string?)declaration.Attribute("Extension");
                var contentType = (string?)declaration.Attribute("ContentType");
                if (string.IsNullOrWhiteSpace(extension)
                    || string.IsNullOrWhiteSpace(contentType))
                {
                    throw new DebugSetupException(
                        "The generated workbook contains an invalid default content type declaration.");
                }

                if (!defaults.TryAdd(extension, contentType))
                {
                    throw new DebugSetupException(
                        $"The generated workbook contains ambiguous default content types for the {extension} extension.");
                }
            }

            var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var declaration in elements.Where(element =>
                         element.Name == ContentTypesNamespace + "Override"))
            {
                var partName = (string?)declaration.Attribute("PartName");
                var contentType = (string?)declaration.Attribute("ContentType");
                if (string.IsNullOrWhiteSpace(partName)
                    || partName[0] != '/'
                    || string.IsNullOrWhiteSpace(contentType))
                {
                    throw new DebugSetupException(
                        "The generated workbook contains an invalid content type override declaration.");
                }

                if (!overrides.TryAdd(partName, contentType))
                {
                    throw new DebugSetupException(
                        $"The generated workbook contains ambiguous content type overrides for {partName}.");
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

            var effectiveContentType = ResolveContentType(VbaProjectPartName);
            if (!string.Equals(
                    effectiveContentType,
                    VbaProjectContentType,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new DebugSetupException(
                    "The effective content type of xl/vbaProject.bin is not the VBA project content type.");
            }

            vbaProjectParts = archive.Entries
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
                throw new DebugSetupException(
                    "The generated workbook must contain exactly one xl/vbaProject.bin VBA project part.");
            }
        }

        var namedParts = archive.Entries
            .Where(entry => entry.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var exactParts = namedParts
            .Where(entry => entry.FullName.Equals(
                VbaProjectPartName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (namedParts.Length != 1 || exactParts.Length != 1)
        {
            throw new DebugSetupException(
                "The generated workbook must contain exactly one xl/vbaProject.bin VBA project part.");
        }

        var part = exactParts[0];
        if (part.Length is <= 0 or > MaximumVbaProjectPartLength)
        {
            throw new DebugSetupException(
                "The generated workbook VBA project part has an invalid or excessive length.");
        }

        using var partStream = part.Open();
        using var buffer = new MemoryStream(checked((int)part.Length));
        partStream.CopyTo(buffer);
        if (buffer.Length != part.Length || buffer.Length > MaximumVbaProjectPartLength)
        {
            throw new DebugSetupException(
                "The generated workbook VBA project part length changed while it was read.");
        }

        return buffer.ToArray();
    }

    private static byte[] ReadDirectoryStream(byte[] partBytes)
    {
        using var partStream = new MemoryStream(partBytes, writable: false);
        using var root = RootStorage.Open(partStream, StorageModeFlags.LeaveOpen);
        if (!root.TryOpenStorage("VBA", out var vbaStorage))
        {
            throw new InvalidDataException(
                "The VBA project compound file does not contain the required VBA storage.");
        }

        if (!vbaStorage.TryOpenStream("dir", out var directory))
        {
            throw new InvalidDataException(
                "The VBA project compound file does not contain the required VBA directory stream.");
        }

        using (directory)
        {
            if (directory.Length is <= 0 or > MaximumCompressedDirectoryLength)
            {
                throw new InvalidDataException(
                    "The VBA directory stream has an invalid or excessive length.");
            }

            var bytes = new byte[checked((int)directory.Length)];
            directory.ReadExactly(bytes);
            return bytes;
        }
    }

    private sealed record ProjectInformationRecords(
        VbaProjectSystemKind SystemKind,
        int CodePage,
        IReadOnlyList<KeyValuePair<string, short>> ProjectConstants)
    {
        public static ProjectInformationRecords Parse(ReadOnlySpan<byte> directory)
        {
            var reader = new RecordReader(directory);
            reader.ExpectRecord(0x0001, 4);
            var systemKindValue = reader.ReadUInt32();
            if (systemKindValue > (uint)VbaProjectSystemKind.Win64)
            {
                throw new InvalidDataException(
                    $"The VBA PROJECTSYSKIND value '{systemKindValue}' is unsupported.");
            }

            if (reader.PeekUInt16() == 0x004a)
            {
                reader.ExpectRecord(0x004a, 4);
                _ = reader.ReadUInt32();
            }

            reader.ExpectRecord(0x0002, 4);
            _ = reader.ReadUInt32();
            reader.ExpectRecord(0x0014, 4);
            _ = reader.ReadUInt32();
            reader.ExpectRecord(0x0003, 2);
            var codePage = reader.ReadUInt16();
            var encoding = GetEncoding(codePage);

            reader.ExpectVariableRecord(0x0004);
            reader.SkipVariableBytes();
            reader.ExpectVariableRecord(0x0005);
            reader.SkipVariableBytes();
            reader.ExpectUInt16(0x0040, "PROJECTDOCSTRING Unicode marker");
            reader.SkipLengthPrefixedBytes(requireEvenLength: true);
            reader.ExpectVariableRecord(0x0006);
            reader.SkipVariableBytes();
            reader.ExpectUInt16(0x003d, "PROJECTHELPFILEPATH second-path marker");
            reader.SkipLengthPrefixedBytes(requireEvenLength: false);
            reader.ExpectRecord(0x0007, 4);
            _ = reader.ReadUInt32();
            reader.ExpectRecord(0x0008, 4);
            _ = reader.ReadUInt32();
            reader.ExpectUInt16(0x0009, "PROJECTVERSION record identifier");
            reader.ExpectUInt32(4, "PROJECTVERSION reserved size");
            _ = reader.ReadUInt32();
            _ = reader.ReadUInt16();

            IReadOnlyList<KeyValuePair<string, short>> constants = [];
            if (reader.HasRemaining && reader.PeekUInt16() == 0x000c)
            {
                constants = ReadConstants(ref reader, encoding);
            }

            return new ProjectInformationRecords(
                (VbaProjectSystemKind)systemKindValue,
                codePage,
                constants);
        }

        private static IReadOnlyList<KeyValuePair<string, short>> ReadConstants(
            ref RecordReader reader,
            Encoding encoding)
        {
            reader.ExpectVariableRecord(0x000c);
            var mbcsBytes = reader.ReadVariableBytes(maximumLength: 1015);
            reader.ExpectUInt16(0x003c, "PROJECTCONSTANTS Unicode marker");
            var unicodeBytes = reader.ReadLengthPrefixedBytes(requireEvenLength: true);
            if (mbcsBytes.Contains((byte)0) || ContainsUtf16Null(unicodeBytes))
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

            return ParseConstants(unicode);
        }

        private static IReadOnlyList<KeyValuePair<string, short>> ParseConstants(string text)
        {
            if (text.Length == 0)
            {
                return [];
            }

            var constants = new Dictionary<string, short>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in text.Split(" : ", StringSplitOptions.None))
            {
                var assignment = item.Split(" = ", StringSplitOptions.None);
                if (assignment.Length != 2
                    || !IsVbaIdentifier(assignment[0])
                    || !TryParseProjectConstantValue(assignment[1], out var value))
                {
                    throw new InvalidDataException(
                        $"The VBA PROJECTCONSTANTS value '{item}' is malformed.");
                }

                var name = assignment[0];
                if (BuiltInCompilerConstants.Contains(name))
                {
                    throw new InvalidDataException(
                        $"The VBA project constant '{name}' collides with a built-in compiler constant.");
                }

                if (!constants.TryAdd(name, value))
                {
                    throw new InvalidDataException(
                        $"The VBA project constant '{name}' is duplicated case-insensitively.");
                }
            }

            return constants.ToArray();
        }

        private static Encoding GetEncoding(int codePage)
        {
            if (codePage == 0)
            {
                throw new InvalidDataException(
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
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException)
            {
                throw new InvalidDataException(
                    $"The VBA PROJECTCODEPAGE value '{codePage}' is unsupported.",
                    exception);
            }
        }

        private static bool IsVbaIdentifier(string value)
            => value.Length != 0
               && char.IsLetter(value[0])
               && value.Skip(1).All(character => char.IsLetterOrDigit(character) || character == '_');

        private static bool TryParseProjectConstantValue(string text, out short value)
        {
            var digits = text.AsSpan();
            if (!digits.IsEmpty && digits[0] == '-')
            {
                digits = digits[1..];
            }

            if (digits.Length is < 1 or > 5)
            {
                value = default;
                return false;
            }

            foreach (var character in digits)
            {
                if (character is < '0' or > '9')
                {
                    value = default;
                    return false;
                }
            }

            return short.TryParse(
                text,
                System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture,
                out value);
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
                throw new InvalidDataException("A VBA directory record length exceeds the supported bound.");
            }

            variableLength = (int)length;
            EnsureAvailable(variableLength);
        }

        public void SkipVariableBytes()
        {
            offset += variableLength;
            variableLength = 0;
        }

        public byte[] ReadVariableBytes(int maximumLength)
        {
            if (variableLength > maximumLength)
            {
                throw new InvalidDataException("A VBA directory variable record exceeds its configured bound.");
            }

            var value = bytes.Slice(offset, variableLength).ToArray();
            offset += variableLength;
            variableLength = 0;
            return value;
        }

        public void SkipLengthPrefixedBytes(bool requireEvenLength)
            => _ = ReadLengthPrefixedBytes(requireEvenLength);

        public byte[] ReadLengthPrefixedBytes(bool requireEvenLength)
        {
            var length = ReadUInt32();
            if (length > int.MaxValue || requireEvenLength && (length & 1) != 0)
            {
                throw new InvalidDataException("A VBA directory string length is invalid.");
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
                throw new InvalidDataException("A VBA directory record is truncated or exceeds its bounds.");
            }
        }
    }
}
