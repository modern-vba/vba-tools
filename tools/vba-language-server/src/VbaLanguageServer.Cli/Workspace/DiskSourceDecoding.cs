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
        Exception innerException)
        : base(
            $"Unable to decode closed VBA source '{sourcePath}' with {policyDescription}. "
            + "Save the file as valid UTF-8 with BOM, UTF-16 LE with BOM, "
            + "or UTF-16 BE with BOM. On Windows, BOM-less source must use "
            + "the process-start ANSI code page; UTF-8 requires ACP 65001.",
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
    private static readonly byte[] Utf16LittleEndianPreamble = [0xFF, 0xFE];
    private static readonly byte[] Utf16BigEndianPreamble = [0xFE, 0xFF];
    private static readonly byte[][] UnsupportedUnicodePreambles =
    [
        [0xFF, 0xFE, 0x00, 0x00],
        [0x00, 0x00, 0xFE, 0xFF],
        [0x2B, 0x2F, 0x76, 0x38],
        [0x2B, 0x2F, 0x76, 0x39],
        [0x2B, 0x2F, 0x76, 0x2B],
        [0x2B, 0x2F, 0x76, 0x2F]
    ];
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

    private readonly Encoding? activeWindowsEncoding;
    private readonly string policyDescription;

    internal DiskSourceDecoding(
        bool hasWindowsAcpAuthority,
        int activeCodePage)
    {
        if (activeCodePage <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(activeCodePage));
        }

        if (!hasWindowsAcpAuthority)
        {
            policyDescription = "strict BOM-selected Unicode decoding without Windows ACP authority";
            return;
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        activeWindowsEncoding = activeCodePage == 65001
            ? Utf8Strict
            : Encoding.GetEncoding(
                activeCodePage,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
        policyDescription =
            $"strict BOM-selected Unicode or BOM-less Windows code page {activeCodePage}";
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
        catch (Exception error) when (error is DecoderFallbackException or EncoderFallbackException)
        {
            throw new DiskSourceDecodingException(
                sourcePath,
                policyDescription,
                error);
        }
    }

    private string DecodeCore(ReadOnlySpan<byte> bytes)
    {
        foreach (var preamble in UnsupportedUnicodePreambles)
        {
            if (bytes.StartsWith(preamble))
            {
                throw new DecoderFallbackException(
                    "The Unicode BOM is outside the supported disk-source policy.");
            }
        }

        if (bytes.StartsWith(Utf8Preamble))
        {
            return DecodeExactly(bytes[Utf8Preamble.Length..], Utf8Strict);
        }

        if (bytes.StartsWith(Utf16LittleEndianPreamble))
        {
            return DecodeExactly(
                bytes[Utf16LittleEndianPreamble.Length..],
                Utf16LittleEndianStrict);
        }

        if (bytes.StartsWith(Utf16BigEndianPreamble))
        {
            return DecodeExactly(
                bytes[Utf16BigEndianPreamble.Length..],
                Utf16BigEndianStrict);
        }

        if (IsTruncatedPreamble(bytes, Utf8Preamble)
            || IsTruncatedPreamble(bytes, Utf16LittleEndianPreamble)
            || IsTruncatedPreamble(bytes, Utf16BigEndianPreamble))
        {
            throw new DecoderFallbackException("The Unicode BOM is truncated.");
        }

        foreach (var preamble in UnsupportedUnicodePreambles)
        {
            if (IsTruncatedPreamble(bytes, preamble))
            {
                throw new DecoderFallbackException("The Unicode BOM is truncated.");
            }
        }

        if (activeWindowsEncoding is null)
        {
            throw new DecoderFallbackException(
                "BOM-less source requires Windows ACP authority.");
        }

        return DecodeExactly(bytes, activeWindowsEncoding);
    }

    private static string DecodeExactly(ReadOnlySpan<byte> bytes, Encoding encoding)
    {
        var text = encoding.GetString(bytes);
        if (!bytes.SequenceEqual(encoding.GetBytes(text)))
        {
            throw new DecoderFallbackException(
                "The decoded source cannot reproduce its original bytes.");
        }

        return text;
    }

    private static bool IsTruncatedPreamble(ReadOnlySpan<byte> bytes, byte[] preamble)
        => !bytes.IsEmpty
            && bytes.Length < preamble.Length
            && preamble.AsSpan().StartsWith(bytes);

    private static DiskSourceDecoding CreateForCurrentProcess()
        => OperatingSystem.IsWindows()
            ? new DiskSourceDecoding(
                hasWindowsAcpAuthority: true,
                activeCodePage: checked((int)GetACP()))
            : new DiskSourceDecoding(
                hasWindowsAcpAuthority: false,
                activeCodePage: 65001);

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();
}
