using System.Runtime.InteropServices;
using System.Text;

namespace VbaLanguageServer.Workspace;

/// <summary>
/// Reports that one closed exported VBA source cannot be decoded without substitution.
/// </summary>
internal sealed class DiskSourceDecodingException : Exception
{
    internal DiskSourceDecodingException(
        string sourcePath,
        string policyDescription,
        DecoderFallbackException innerException)
        : base(
            $"Unable to decode closed VBA source '{sourcePath}' with {policyDescription}. "
            + "Save the file as valid UTF-8, UTF-8 with BOM, UTF-16 LE with BOM, "
            + "or UTF-16 BE with BOM, or use the active Windows ANSI code page.",
            innerException)
    {
    }
}

/// <summary>
/// Decodes closed exported VBA source bytes using one process-wide policy.
/// </summary>
internal sealed class DiskSourceDecoding
{
    private static readonly byte[] Utf8Preamble = [0xEF, 0xBB, 0xBF];
    private static readonly byte[] Utf32LittleEndianPreamble =
        [0xFF, 0xFE, 0x00, 0x00];
    private static readonly byte[] Utf32BigEndianPreamble =
        [0x00, 0x00, 0xFE, 0xFF];
    private static readonly byte[] Utf16LittleEndianPreamble = [0xFF, 0xFE];
    private static readonly byte[] Utf16BigEndianPreamble = [0xFE, 0xFF];
    private static readonly Encoding Utf8Strict = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly Encoding Utf16LittleEndianStrict =
        new UnicodeEncoding(
            bigEndian: false,
            byteOrderMark: true,
            throwOnInvalidBytes: true);
    private static readonly Encoding Utf16BigEndianStrict =
        new UnicodeEncoding(
            bigEndian: true,
            byteOrderMark: true,
            throwOnInvalidBytes: true);
    private static readonly Lazy<DiskSourceDecoding> CurrentProcess =
        new(CreateForCurrentProcess);

    private readonly Encoding? activeLegacyEncoding;
    private readonly string policyDescription;

    internal DiskSourceDecoding(
        bool supportsLegacyFallback,
        int activeCodePage)
    {
        if (activeCodePage <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(activeCodePage));
        }

        if (!supportsLegacyFallback || activeCodePage == 65001)
        {
            policyDescription = "strict Unicode decoding (including UTF-8)";
            return;
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        activeLegacyEncoding = Encoding.GetEncoding(
            activeCodePage,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
        policyDescription =
            $"strict Unicode decoding or active Windows code page {activeCodePage}";
    }

    internal static DiskSourceDecoding ForCurrentProcess
        => CurrentProcess.Value;

    internal string Decode(ReadOnlySpan<byte> bytes, string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        try
        {
            return DecodeCore(bytes);
        }
        catch (DecoderFallbackException error)
        {
            throw new DiskSourceDecodingException(
                sourcePath,
                policyDescription,
                error);
        }
    }

    private string DecodeCore(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        if (bytes.StartsWith(Utf32LittleEndianPreamble)
            || bytes.StartsWith(Utf32BigEndianPreamble))
        {
            throw new DecoderFallbackException(
                "UTF-32 BOMs are outside the supported disk-source policy.");
        }

        if (bytes.StartsWith(Utf8Preamble))
        {
            return Utf8Strict.GetString(bytes[Utf8Preamble.Length..]);
        }

        if (bytes.StartsWith(Utf16LittleEndianPreamble))
        {
            return Utf16LittleEndianStrict.GetString(
                bytes[Utf16LittleEndianPreamble.Length..]);
        }

        if (bytes.StartsWith(Utf16BigEndianPreamble))
        {
            return Utf16BigEndianStrict.GetString(
                bytes[Utf16BigEndianPreamble.Length..]);
        }

        try
        {
            return Utf8Strict.GetString(bytes);
        }
        catch (DecoderFallbackException) when (activeLegacyEncoding is not null)
        {
            return activeLegacyEncoding.GetString(bytes);
        }
    }

    private static DiskSourceDecoding CreateForCurrentProcess()
        => OperatingSystem.IsWindows()
            ? new DiskSourceDecoding(
                supportsLegacyFallback: true,
                activeCodePage: checked((int)GetACP()))
            : new DiskSourceDecoding(
                supportsLegacyFallback: false,
                activeCodePage: 65001);

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();
}
