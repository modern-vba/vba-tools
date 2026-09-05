namespace VbaDebugAdapter.Infrastructure;

/// <summary>
/// Keeps the debug-adapter seam while delegating to the shared MS-OVBA reader.
/// </summary>
internal static class MsOvbaCompression
{
    public static byte[] Decompress(
        ReadOnlySpan<byte> compressedContainer,
        int maximumOutputLength)
        => VbaTools.Syntax.MsOvbaCompression.Decompress(
            compressedContainer,
            maximumOutputLength);
}
