using System.Text;

namespace VbaLanguageServer.Workspace;

/// <summary>
/// Reads VBA source text using the encodings commonly emitted by the VBE and source-control tools.
/// </summary>
internal static class VbaSourceFileTextReader
{
    private static readonly byte[] Utf8Preamble = [0xEF, 0xBB, 0xBF];
    private static readonly byte[] Utf16LittleEndianPreamble = [0xFF, 0xFE];
    private static readonly byte[] Utf16BigEndianPreamble = [0xFE, 0xFF];

    private static readonly Encoding Utf8Strict = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly Lazy<Encoding> Cp932 = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932);
    });

    internal static string Decode(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        if (HasPrefix(bytes, Utf8Preamble))
        {
            return Utf8Strict.GetString(bytes.AsSpan(3));
        }

        if (HasPrefix(bytes, Utf16LittleEndianPreamble))
        {
            return Encoding.Unicode.GetString(bytes.AsSpan(2));
        }

        if (HasPrefix(bytes, Utf16BigEndianPreamble))
        {
            return Encoding.BigEndianUnicode.GetString(bytes.AsSpan(2));
        }

        try
        {
            return Utf8Strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Cp932.Value.GetString(bytes);
        }
    }

    private static bool HasPrefix(byte[] bytes, ReadOnlySpan<byte> prefix)
        => bytes.AsSpan().StartsWith(prefix);
}
