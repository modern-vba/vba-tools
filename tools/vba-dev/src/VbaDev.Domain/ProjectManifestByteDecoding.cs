using System.Text;

namespace VbaDev.Domain;

/// <summary>Decodes the strict disk-byte contract for vba-project.json.</summary>
public static class ProjectManifestByteDecoding
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static readonly Encoding Utf16Le = new UnicodeEncoding(false, false, true);
    private static readonly Encoding Utf16Be = new UnicodeEncoding(true, false, true);

    /// <summary>Returns Unicode text without admitting replacement decoding or UTF-32.</summary>
    public static string Decode(ReadOnlySpan<byte> bytes, string manifestName)
    {
        try
        {
            // UTF-32LE shares the UTF-16LE prefix and must be classified first.
            if (bytes.StartsWith(new byte[] { 0xff, 0xfe, 0, 0 })
                || bytes.StartsWith(new byte[] { 0, 0, 0xfe, 0xff }))
            {
                throw new DecoderFallbackException("UTF-32 is not supported.");
            }

            Encoding encoding = Utf8;
            var offset = 0;
            if (bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
            {
                offset = 3;
            }
            else if (bytes.StartsWith(new byte[] { 0xef, 0xbb }))
            {
                throw new DecoderFallbackException("The UTF-8 BOM is malformed or truncated.");
            }
            else if (bytes.StartsWith(new byte[] { 0xff, 0xfe }))
            {
                encoding = Utf16Le;
                offset = 2;
            }
            else if (bytes.StartsWith(new byte[] { 0xfe, 0xff }))
            {
                encoding = Utf16Be;
                offset = 2;
            }

            var text = encoding.GetString(bytes[offset..]);
            if (text.Contains('\0'))
            {
                throw new DecoderFallbackException("NUL characters and BOM-less UTF-16 are not supported.");
            }
            return text;
        }
        catch (DecoderFallbackException error)
        {
            throw new VbaProjectManifestException(
                $"Project manifest encoding is invalid: {manifestName}. {error.Message}", error);
        }
    }
}
