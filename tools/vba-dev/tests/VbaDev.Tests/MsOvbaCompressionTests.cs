using VbaDev.Infrastructure.Debugging;
using Xunit;

namespace VbaDev.Tests;

public sealed class MsOvbaCompressionTests
{
    [Fact]
    public void DecompressesAnEmptyContainerWithTheRequiredSignature()
    {
        var result = MsOvbaCompression.Decompress([0x01], maximumOutputLength: 0);

        Assert.Empty(result);
    }

    [Fact]
    public void DecompressesA4096ByteUncompressedChunk()
    {
        var expected = Enumerable.Range(0, 4096)
            .Select(index => (byte)(index % 251))
            .ToArray();
        var container = new byte[1 + 2 + expected.Length];
        container[0] = 0x01;
        container[1] = 0xff;
        container[2] = 0x3f;
        expected.CopyTo(container, 3);

        var result = MsOvbaCompression.Decompress(
            container,
            maximumOutputLength: expected.Length);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void DecompressesLiteralTokensFromACompressedChunk()
    {
        byte[] container =
        [
            0x01,
            0x03, 0xb0,
            0x00, (byte)'A', (byte)'B', (byte)'C'
        ];

        var result = MsOvbaCompression.Decompress(container, maximumOutputLength: 3);

        Assert.Equal("ABC"u8.ToArray(), result);
    }

    [Fact]
    public void DecompressesACompressedChunkEndingWithAFlagByteWithoutTokens()
    {
        byte[] container =
        [
            0x01,
            0x09, 0xb0,
            0x00,
            (byte)'A', (byte)'B', (byte)'C', (byte)'D',
            (byte)'E', (byte)'F', (byte)'G', (byte)'H',
            0x00
        ];

        var result = MsOvbaCompression.Decompress(container, maximumOutputLength: 8);

        Assert.Equal("ABCDEFGH"u8.ToArray(), result);
    }

    [Fact]
    public void DecompressesAnOverlappingCopyTokenWithinItsChunk()
    {
        byte[] container =
        [
            0x01,
            0x05, 0xb0,
            0x08, (byte)'A', (byte)'B', (byte)'C', 0x03, 0x20
        ];

        var result = MsOvbaCompression.Decompress(container, maximumOutputLength: 9);

        Assert.Equal("ABCABCABC"u8.ToArray(), result);
    }

    [Fact]
    public void DecompressesTheOfficialNormalCompressionExample()
    {
        byte[] container =
        [
            0x01, 0x2f, 0xb0, 0x00, 0x23, 0x61, 0x61, 0x61,
            0x62, 0x63, 0x64, 0x65, 0x82, 0x66, 0x00, 0x70,
            0x61, 0x67, 0x68, 0x69, 0x6a, 0x01, 0x38, 0x08,
            0x61, 0x6b, 0x6c, 0x00, 0x20, 0x6d, 0x6e, 0x6f,
            0x70, 0x06, 0x71, 0x02, 0x70, 0x04, 0x00, 0x72,
            0x73, 0x74, 0x75, 0x76, 0x10, 0x77, 0x78, 0x79,
            0x7a, 0x00, 0x2c
        ];
        var expected = "#aaabcdefaaaaghijaaaaaklaaamnopqaaaaaaaaaaaarstuvwxyzaaa"u8
            .ToArray();

        var result = MsOvbaCompression.Decompress(
            container,
            maximumOutputLength: expected.Length);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void IncreasesCopyTokenOffsetBitsAfterSixteenDecompressedBytes()
    {
        byte[] container =
        [
            0x01,
            0x15, 0xb0,
            0x00, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
            0x00, 0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f,
            0x02, 0x10, 0x00, 0x80
        ];
        var expected = Enumerable.Range(0, 17)
            .Concat(Enumerable.Range(0, 3))
            .Select(value => (byte)value)
            .ToArray();

        var result = MsOvbaCompression.Decompress(
            container,
            maximumOutputLength: expected.Length);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ResetsTheCopyTokenWindowAtThe4096ByteChunkBoundary()
    {
        var firstChunk = Enumerable.Repeat((byte)'x', 4096).ToArray();
        byte[] secondChunk = [0x03, 0xb0, 0x02, 0x61, 0x45, 0x00];
        var container = new byte[1 + 2 + firstChunk.Length + secondChunk.Length];
        container[0] = 0x01;
        container[1] = 0xff;
        container[2] = 0x3f;
        firstChunk.CopyTo(container, 3);
        secondChunk.CopyTo(container, 3 + firstChunk.Length);
        var expected = firstChunk
            .Concat(Enumerable.Repeat((byte)'a', 73))
            .ToArray();

        var result = MsOvbaCompression.Decompress(
            container,
            maximumOutputLength: expected.Length);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("00")]
    [InlineData("02")]
    public void RejectsAContainerWithoutTheRequiredSignature(string containerHex)
    {
        var container = Convert.FromHexString(containerHex);
        var error = Assert.Throws<InvalidDataException>(() =>
            MsOvbaCompression.Decompress(container, maximumOutputLength: 4096));

        Assert.Contains("signature", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsAnInvalidCompressedChunkSignature()
    {
        byte[] container = [0x01, 0x00, 0x80, 0x00];

        var error = Assert.Throws<InvalidDataException>(() =>
            MsOvbaCompression.Decompress(container, maximumOutputLength: 4096));

        Assert.Contains("chunk signature", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("0100")]
    [InlineData("0103B00041")]
    public void RejectsATruncatedChunk(string containerHex)
    {
        var container = Convert.FromHexString(containerHex);
        Assert.Throws<InvalidDataException>(() =>
            MsOvbaCompression.Decompress(container, maximumOutputLength: 4096));
    }

    [Fact]
    public void RejectsATruncatedCopyToken()
    {
        byte[] container = [0x01, 0x01, 0xb0, 0x01, 0x00];

        var error = Assert.Throws<InvalidDataException>(() =>
            MsOvbaCompression.Decompress(container, maximumOutputLength: 4096));

        Assert.Contains("copy token", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("truncated", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsAnUncompressedFlagWithoutA4096ByteRawChunk()
    {
        byte[] container = [0x01, 0x00, 0x30, 0x00];

        var error = Assert.Throws<InvalidDataException>(() =>
            MsOvbaCompression.Decompress(container, maximumOutputLength: 4096));

        Assert.Contains("4096", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsACopyTokenThatReferencesBeforeItsChunk()
    {
        byte[] container =
        [
            0x01,
            0x03, 0xb0,
            0x02, (byte)'A', 0x00, 0x10
        ];

        var error = Assert.Throws<InvalidDataException>(() =>
            MsOvbaCompression.Decompress(container, maximumOutputLength: 4096));

        Assert.Contains("outside", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsOutputBeyondTheCallerProvidedBound()
    {
        byte[] container =
        [
            0x01,
            0x03, 0xb0,
            0x00, (byte)'A', (byte)'B', (byte)'C'
        ];

        var error = Assert.Throws<InvalidDataException>(() =>
            MsOvbaCompression.Decompress(container, maximumOutputLength: 2));

        Assert.Contains("bound", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsACopyTokenThatExpandsItsChunkBeyond4096Bytes()
    {
        byte[] container =
        [
            0x01,
            0x03, 0xb0,
            0x02, (byte)'A', 0xff, 0x0f
        ];

        var error = Assert.Throws<InvalidDataException>(() =>
            MsOvbaCompression.Decompress(container, maximumOutputLength: 5000));

        Assert.Contains("4096", error.Message, StringComparison.Ordinal);
    }
}
