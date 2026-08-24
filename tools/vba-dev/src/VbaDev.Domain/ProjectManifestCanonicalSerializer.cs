using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VbaDev.Domain;

/// <summary>
/// Serializes a ProjectManifest using the one canonical on-disk representation.
/// </summary>
public static class ProjectManifestCanonicalSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        IndentCharacter = ' ',
        IndentSize = 2,
        NewLine = "\r\n",
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly UnicodeEncoding Utf16LeWithBom = new(
        bigEndian: false,
        byteOrderMark: true,
        throwOnInvalidBytes: true);

    /// <summary>
    /// Returns UTF-16LE bytes with a BOM, fixed CRLF formatting, and one trailing CRLF.
    /// </summary>
    /// <param name="manifest">The manifest to serialize.</param>
    /// <returns>The complete canonical file bytes.</returns>
    public static byte[] SerializeToUtf16LeBytes(ProjectManifest manifest)
    {
        var json = JsonSerializer.Serialize(manifest, JsonOptions) + "\r\n";
        var preamble = Utf16LeWithBom.GetPreamble();
        var content = Utf16LeWithBom.GetBytes(json);
        var bytes = new byte[preamble.Length + content.Length];
        Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
        Buffer.BlockCopy(content, 0, bytes, preamble.Length, content.Length);
        return bytes;
    }
}
