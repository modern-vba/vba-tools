using System.Buffers.Binary;

namespace VbaDev.Infrastructure.Debugging;

/// <summary>
/// Decompresses the bounded MS-OVBA compressed-container format.
/// </summary>
internal static class MsOvbaCompression
{
    private const byte ContainerSignature = 0x01;
    private const int ChunkHeaderLength = 2;
    private const int DecompressedChunkLength = 4096;
    private const ushort ChunkSignatureMask = 0x7000;
    private const ushort ChunkSignature = 0x3000;
    private const ushort CompressedChunkFlag = 0x8000;
    private const ushort ChunkSizeMask = 0x0fff;

    public static byte[] Decompress(
        ReadOnlySpan<byte> compressedContainer,
        int maximumOutputLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumOutputLength);
        if (compressedContainer.IsEmpty
            || compressedContainer[0] != ContainerSignature)
        {
            throw new InvalidDataException(
                "The MS-OVBA compressed container signature is invalid.");
        }

        var output = new List<byte>(Math.Min(
            maximumOutputLength,
            compressedContainer.Length - 1));
        var compressedCurrent = 1;
        while (compressedCurrent < compressedContainer.Length)
        {
            var chunkStart = compressedCurrent;
            if (compressedContainer.Length - chunkStart < ChunkHeaderLength)
            {
                throw new InvalidDataException(
                    "The MS-OVBA compressed chunk header is truncated.");
            }

            var header = BinaryPrimitives.ReadUInt16LittleEndian(
                compressedContainer.Slice(chunkStart, ChunkHeaderLength));
            if ((header & ChunkSignatureMask) != ChunkSignature)
            {
                throw new InvalidDataException(
                    "The MS-OVBA compressed chunk signature is invalid.");
            }

            var chunkLength = (header & ChunkSizeMask) + 3;
            if (chunkLength > compressedContainer.Length - chunkStart)
            {
                throw new InvalidDataException(
                    "The MS-OVBA compressed chunk is truncated.");
            }

            var chunkEnd = chunkStart + chunkLength;
            compressedCurrent = chunkStart + ChunkHeaderLength;
            if ((header & CompressedChunkFlag) != 0)
            {
                DecompressTokenSequences(
                    compressedContainer,
                    ref compressedCurrent,
                    chunkEnd,
                    output,
                    maximumOutputLength);
                continue;
            }

            if (chunkLength != ChunkHeaderLength + DecompressedChunkLength)
            {
                throw new InvalidDataException(
                    "An uncompressed MS-OVBA chunk must contain exactly 4096 data bytes.");
            }

            EnsureOutputCapacity(output.Count, DecompressedChunkLength, maximumOutputLength);
            while (compressedCurrent < chunkEnd)
            {
                output.Add(compressedContainer[compressedCurrent]);
                compressedCurrent++;
            }
        }

        return [.. output];
    }

    private static void DecompressTokenSequences(
        ReadOnlySpan<byte> compressedContainer,
        ref int compressedCurrent,
        int chunkEnd,
        List<byte> output,
        int maximumOutputLength)
    {
        var decompressedChunkStart = output.Count;
        while (compressedCurrent < chunkEnd)
        {
            var flags = compressedContainer[compressedCurrent];
            compressedCurrent++;

            for (var tokenIndex = 0;
                 tokenIndex < 8 && compressedCurrent < chunkEnd;
                 tokenIndex++)
            {
                if ((flags & (1 << tokenIndex)) != 0)
                {
                    DecompressCopyToken(
                        compressedContainer,
                        ref compressedCurrent,
                        chunkEnd,
                        decompressedChunkStart,
                        output,
                        maximumOutputLength);
                    continue;
                }

                EnsureOutputCapacity(output.Count, 1, maximumOutputLength);
                EnsureChunkCapacity(output.Count - decompressedChunkStart, 1);
                output.Add(compressedContainer[compressedCurrent]);
                compressedCurrent++;
            }
        }
    }

    private static void DecompressCopyToken(
        ReadOnlySpan<byte> compressedContainer,
        ref int compressedCurrent,
        int chunkEnd,
        int decompressedChunkStart,
        List<byte> output,
        int maximumOutputLength)
    {
        if (chunkEnd - compressedCurrent < 2)
        {
            throw new InvalidDataException(
                "An MS-OVBA copy token is truncated.");
        }

        var decompressedCurrent = output.Count - decompressedChunkStart;
        if (decompressedCurrent == 0)
        {
            throw new InvalidDataException(
                "An MS-OVBA copy token has no preceding bytes in its chunk.");
        }

        var bitCount = 4;
        while ((1 << bitCount) < decompressedCurrent)
        {
            bitCount++;
        }

        var lengthMask = 0xffff >> bitCount;
        var offsetMask = 0xffff ^ lengthMask;
        var token = BinaryPrimitives.ReadUInt16LittleEndian(
            compressedContainer.Slice(compressedCurrent, 2));
        compressedCurrent += 2;
        var length = (token & lengthMask) + 3;
        var offset = ((token & offsetMask) >> (16 - bitCount)) + 1;
        if (offset > decompressedCurrent)
        {
            throw new InvalidDataException(
                "An MS-OVBA copy token references bytes outside its decompressed chunk.");
        }

        EnsureOutputCapacity(output.Count, length, maximumOutputLength);
        EnsureChunkCapacity(decompressedCurrent, length);
        var copySource = output.Count - offset;
        for (var index = 0; index < length; index++)
        {
            output.Add(output[copySource + index]);
        }
    }

    private static void EnsureOutputCapacity(
        int currentLength,
        int addedLength,
        int maximumOutputLength)
    {
        if (addedLength > maximumOutputLength - currentLength)
        {
            throw new InvalidDataException(
                "The MS-OVBA decompressed output exceeds the configured bound.");
        }
    }

    private static void EnsureChunkCapacity(int currentLength, int addedLength)
    {
        if (addedLength > DecompressedChunkLength - currentLength)
        {
            throw new InvalidDataException(
                "An MS-OVBA compressed chunk expands beyond 4096 bytes.");
        }
    }
}
