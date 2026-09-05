using System.Buffers.Binary;
using System.Text;
using VbaTools.ProjectMetadata;
using Xunit;

namespace VbaTools.ProjectMetadata.Tests;

public sealed class VbaProjectPackageCompressionTests
{
    [Fact]
    public void ReadsProjectMetadataFromA4096ByteUncompressedChunk()
    {
        var directory = PackageMetadataFixture.CreateProjectInformation(
            1252,
            Encoding.ASCII.GetBytes("RawChunkProject"));
        var container = new byte[1 + 2 + 4096];
        container[0] = 0x01;
        BinaryPrimitives.WriteUInt16LittleEndian(container.AsSpan(1), 0x3fff);
        directory.CopyTo(container, 3);

        var metadata = ReadMetadata(container);

        Assert.Equal("RawChunkProject", metadata.ProjectName);
        Assert.Equal(1252, metadata.CodePage);
        Assert.Equal(VbaProjectSystemKind.Win64, metadata.SystemKind);
        Assert.Empty(metadata.ProjectConstants);
    }

    [Fact]
    public void ReadsProjectMetadataFromCompressedLiteralTokens()
    {
        var directory = PackageMetadataFixture.CreateProjectInformation(
            1252,
            Encoding.ASCII.GetBytes("LiteralProject"));

        var metadata = ReadMetadata([0x01, .. CreateLiteralChunk(directory)]);

        Assert.Equal("LiteralProject", metadata.ProjectName);
        Assert.Equal(1252, metadata.CodePage);
    }

    [Fact]
    public void ReadsAProjectNameReconstructedByAnOverlappingCopyToken()
    {
        var directory = PackageMetadataFixture.CreateProjectInformation(
            1252,
            Encoding.ASCII.GetBytes("ABCABCABC"));
        // At output offset 47, offset 3 and length 6 use six offset bits.
        var chunk = CreateCompressedChunk([
            .. LiteralTokens(directory[..47]),
            new byte[] { 0x03, 0x08 },
            .. LiteralTokens(directory[53..])
        ]);

        var metadata = ReadMetadata([0x01, .. chunk]);

        Assert.Equal("ABCABCABC", metadata.ProjectName);
        Assert.Equal(1252, metadata.CodePage);
    }

    [Fact]
    public void ReadsRequiredRecordsWhenCopyOffsetsGrowBeyondSixteenBytes()
    {
        var directory = PackageMetadataFixture.CreateProjectInformation(
            1252,
            Encoding.ASCII.GetBytes("OffsetWidthProject"));
        // At output offset 17, offset 15 and length 3 reconstruct LCID bytes.
        var chunk = CreateCompressedChunk([
            .. LiteralTokens(directory[..17]),
            new byte[] { 0x00, 0x70 },
            .. LiteralTokens(directory[20..])
        ]);

        var metadata = ReadMetadata([0x01, .. chunk]);

        Assert.Equal("OffsetWidthProject", metadata.ProjectName);
        Assert.Equal(VbaProjectSystemKind.Win64, metadata.SystemKind);
    }

    [Fact]
    public void ReadsMetadataWhenTheFinalCompressedGroupContainsOnlyItsFlagByte()
    {
        var directory = PackageMetadataFixture.CreateProjectInformation(
            1252,
            Encoding.ASCII.GetBytes("TrailingFlagProject"));
        Array.Resize(ref directory, ((directory.Length + 2 + 7) / 8) * 8);
        byte[] chunk = [.. CreateLiteralChunk(directory), 0x00];
        BinaryPrimitives.WriteUInt16LittleEndian(
            chunk,
            checked((ushort)(0xb000 | (chunk.Length - 3))));

        var metadata = ReadMetadata([0x01, .. chunk]);

        Assert.Equal("TrailingFlagProject", metadata.ProjectName);
    }

    [Fact]
    public void ReadsConstantsAfterAStringCrossesThe4096ByteCopyWindowBoundary()
    {
        var directory = CreateProjectInformationWithLongDocString();
        const int unicodeDocStringEnd = 6071;
        Assert.Equal(new byte[] { 0x00, (byte)'a' }, directory[4096..4098]);
        // The new chunk starts with two literals, then repeats their UTF-16
        // pattern for 1973 bytes: offset 2, length 1973, four offset bits.
        var secondChunk = CreateCompressedChunk([
            .. LiteralTokens(directory[4096..4098]),
            new byte[] { 0xb2, 0x17 },
            .. LiteralTokens(directory[unicodeDocStringEnd..])
        ]);

        var metadata = ReadMetadata([
            0x01,
            0xff, 0x3f, .. directory[..4096],
            .. secondChunk
        ]);

        Assert.Equal("BoundaryProject", metadata.ProjectName);
        Assert.Equal(1252, metadata.CodePage);
        Assert.Equal((short)7, Assert.Single(metadata.ProjectConstants).Value);
        Assert.Equal((short)7, metadata.ProjectConstants["tail"]);
    }

    [Theory]
    [InlineData("00")]
    [InlineData("02")]
    public void RejectsACompressedDirectoryWithoutTheContainerSignature(string hex)
        => AssertCompressionFailure(Convert.FromHexString(hex), "container signature");

    [Fact]
    public void RejectsAnInvalidChunkSignature()
        => AssertCompressionFailure([0x01, 0x00, 0x80, 0x00], "chunk signature");

    [Fact]
    public void RejectsATruncatedChunkHeader()
        => AssertCompressionFailure([0x01, 0x00], "header is truncated");

    [Fact]
    public void RejectsAChunkWhoseDeclaredLengthExceedsTheDirectoryStream()
        => AssertCompressionFailure([0x01, 0x03, 0xb0, 0x00, 0x41], "chunk is truncated");

    [Fact]
    public void RejectsATruncatedCopyToken()
        => AssertCompressionFailure([0x01, 0x01, 0xb0, 0x01, 0x00], "copy token is truncated");

    [Fact]
    public void RejectsAnUncompressedChunkShorterThan4096Bytes()
        => AssertCompressionFailure([0x01, 0x00, 0x30, 0x00], "exactly 4096");

    [Fact]
    public void RejectsACopyTokenThatReferencesBeforeItsChunk()
        => AssertCompressionFailure([0x01, 0x03, 0xb0, 0x02, 0x41, 0x00, 0x10],
            "outside its decompressed chunk");

    [Fact]
    public void RejectsACopyTokenThatBorrowsBytesFromThePreviousChunk()
    {
        var firstChunk = new byte[4096];
        PackageMetadataFixture.CreateProjectInformation(
            1252, Encoding.ASCII.GetBytes("IsolatedWindows")).CopyTo(firstChunk, 0);

        AssertCompressionFailure([
            0x01,
            0xff, 0x3f, .. firstChunk,
            0x02, 0xb0, 0x01, 0x00, 0x00
        ], "no preceding bytes in its chunk");
    }

    [Fact]
    public void RejectsAShortCompressedChunkBeforeAnotherChunk()
    {
        var directory = PackageMetadataFixture.CreateProjectInformation(
            1252, Encoding.ASCII.GetBytes("ShortNonFinalChunk"));

        AssertCompressionFailure([
            0x01,
            .. CreateLiteralChunk(directory),
            0xff, 0x3f, .. new byte[4096]
        ], "non-final MS-OVBA compressed chunk");
    }

    [Fact]
    public void RejectsACopyTokenThatExpandsItsChunkBeyond4096Bytes()
        => AssertCompressionFailure([0x01, 0x03, 0xb0, 0x02, 0x41, 0xff, 0x0f],
            "expands beyond 4096");

    [Fact]
    public void ReadsMetadataAtThe32MiBDecompressedDirectoryBound()
    {
        var metadata = ReadMetadata(CreateContainerExpandingTo32MiB());

        Assert.Equal("BoundedProject", metadata.ProjectName);
        Assert.Equal(1252, metadata.CodePage);
    }

    [Fact]
    public void RejectsOutputOneByteBeyondThe32MiBDecompressedDirectoryBound()
        => AssertCompressionFailure([
            .. CreateContainerExpandingTo32MiB(),
            0x01, 0xb0, 0x00, 0x00
        ], "output exceeds the configured bound");

    [Theory]
    [InlineData("", VbaProjectPackageMetadataReadFailureKind.InvalidCompoundFile)]
    [InlineData("01", VbaProjectPackageMetadataReadFailureKind.InvalidProjectInformation)]
    public void ClassifiesEmptyDirectoryEvidenceAtItsActualFormatBoundary(
        string hex,
        VbaProjectPackageMetadataReadFailureKind expectedKind)
    {
        var result = ReadPackage(Convert.FromHexString(hex));

        Assert.Null(result.Metadata);
        var failure = Assert.IsType<VbaProjectPackageMetadataReadFailure>(result.Failure);
        Assert.Equal(expectedKind, failure.Kind);
    }

    [Fact]
    public void ReadsAProjectNameWithMultipleCopyTokensInOneFlagGroup()
    {
        var directory = PackageMetadataFixture.CreateProjectInformation(
            1252,
            Encoding.ASCII.GetBytes("ABABABABABABAB"));
        // Two offset-2, length-6 tokens occupy bits 6 and 7 of one flag byte.
        var chunk = CreateCompressedChunk([
            .. LiteralTokens(directory[..46]),
            new byte[] { 0x03, 0x04 },
            new byte[] { 0x03, 0x04 },
            .. LiteralTokens(directory[58..])
        ]);

        var metadata = ReadMetadata([0x01, .. chunk]);

        Assert.Equal("ABABABABABABAB", metadata.ProjectName);
    }

    private static byte[] CreateContainerExpandingTo32MiB()
    {
        var firstChunk = new byte[4096];
        PackageMetadataFixture.CreateProjectInformation(
            1252, Encoding.ASCII.GetBytes("BoundedProject")).CopyTo(firstChunk, 0);
        using var container = new MemoryStream();
        container.Write([0x01, 0xff, 0x3f]);
        container.Write(firstChunk);
        for (var chunk = 1; chunk < 8192; chunk++)
        {
            // One zero literal followed by an offset-1, length-4095 copy.
            container.Write([0x03, 0xb0, 0x02, 0x00, 0xfc, 0x0f]);
        }

        return container.ToArray();
    }

    private static byte[] CreateProjectInformationWithLongDocString()
    {
        var name = Encoding.ASCII.GetBytes("BoundaryProject");
        var directory = PackageMetadataFixture.CreateProjectInformation(1252, name);
        var docStringOffset = 44 + name.Length;
        var docString = new string('a', 2000);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(directory[..docStringOffset]);
        writer.Write((ushort)0x0005);
        writer.Write(2000u);
        writer.Write(Encoding.ASCII.GetBytes(docString));
        writer.Write((ushort)0x0040);
        writer.Write(4000u);
        writer.Write(Encoding.Unicode.GetBytes(docString));
        writer.Write(directory[(docStringOffset + 12)..]);
        var constants = "Tail = 7";
        writer.Write((ushort)0x000c);
        writer.Write((uint)constants.Length);
        writer.Write(Encoding.ASCII.GetBytes(constants));
        writer.Write((ushort)0x003c);
        writer.Write((uint)(constants.Length * 2));
        writer.Write(Encoding.Unicode.GetBytes(constants));
        return stream.ToArray();
    }

    private static byte[] CreateLiteralChunk(ReadOnlySpan<byte> bytes)
        => CreateCompressedChunk(LiteralTokens(bytes));

    private static IEnumerable<byte[]> LiteralTokens(ReadOnlySpan<byte> bytes)
        => bytes.ToArray().Select(value => new[] { value });

    private static byte[] CreateCompressedChunk(IEnumerable<byte[]> tokens)
    {
        using var payload = new MemoryStream();
        foreach (var group in tokens.Chunk(8))
        {
            byte flags = 0;
            for (var index = 0; index < group.Length; index++)
            {
                Assert.InRange(group[index].Length, 1, 2);
                if (group[index].Length == 2)
                {
                    flags |= (byte)(1 << index);
                }
            }

            payload.WriteByte(flags);
            foreach (var token in group)
            {
                payload.Write(token);
            }
        }

        var chunk = new byte[2 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(
            chunk,
            checked((ushort)(0xb000 | (payload.Length - 1))));
        payload.ToArray().CopyTo(chunk, 2);
        return chunk;
    }

    private static VbaProjectPackageMetadata ReadMetadata(byte[] container)
    {
        var result = ReadPackage(container);

        Assert.Null(result.Failure);
        return Assert.IsType<VbaProjectPackageMetadata>(result.Metadata);
    }

    private static void AssertCompressionFailure(byte[] container, string message)
    {
        var result = ReadPackage(container);

        Assert.Null(result.Metadata);
        var failure = Assert.IsType<VbaProjectPackageMetadataReadFailure>(result.Failure);
        Assert.Equal(VbaProjectPackageMetadataReadFailureKind.InvalidCompressedDirectory,
            failure.Kind);
        Assert.Contains(message, failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static VbaProjectPackageMetadataReadResult ReadPackage(byte[] container)
    {
        var package = PackageMetadataFixture.Create(new PackageMetadataFixtureOptions
        {
            CompressedDirectoryBytes = container
        });

        return new VbaProjectPackageMetadataReader().Read(package);
    }
}
