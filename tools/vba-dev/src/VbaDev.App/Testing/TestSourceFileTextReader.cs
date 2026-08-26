using System.Text;

namespace VbaDev.App.Testing;

/// <summary>
/// Preserves the legacy source-location decoder used outside language-server disk analysis.
/// </summary>
internal static class TestSourceFileTextReader
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

        if (bytes.AsSpan().StartsWith(Utf8Preamble))
        {
            return Utf8Strict.GetString(bytes.AsSpan(Utf8Preamble.Length));
        }

        if (bytes.AsSpan().StartsWith(Utf16LittleEndianPreamble))
        {
            return Encoding.Unicode.GetString(
                bytes.AsSpan(Utf16LittleEndianPreamble.Length));
        }

        if (bytes.AsSpan().StartsWith(Utf16BigEndianPreamble))
        {
            return Encoding.BigEndianUnicode.GetString(
                bytes.AsSpan(Utf16BigEndianPreamble.Length));
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
}
