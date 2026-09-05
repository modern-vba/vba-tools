using System.Security.Cryptography;
using VbaTools.ProjectMetadata;
using Xunit;

namespace VbaTools.ProjectMetadata.Tests;

public sealed class VbaProjectPackageMetadataReaderTests
{
    [Fact]
    public void RejectsAVariableRecordLengthOutsideTheAvailableDirectory()
    {
        var directory = PackageMetadataFixture.CreateProjectInformation(
            1252, System.Text.Encoding.ASCII.GetBytes("Ledger"));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            directory.AsSpan(40), uint.MaxValue);

        var result = new VbaProjectPackageMetadataReader().Read(
            PackageMetadataFixture.Create(new PackageMetadataFixtureOptions
            {
                ProjectInformationBytes = directory
            }));

        Assert.Null(result.Metadata);
        Assert.Equal(VbaProjectPackageMetadataReadFailureKind.InvalidProjectInformation,
            Assert.IsType<VbaProjectPackageMetadataReadFailure>(result.Failure).Kind);
    }

    [Fact]
    public void PartIdentityRemainsValueEqualAcrossUnrelatedPackageChanges()
    {
        var part = PackageMetadataFixture.CreateCompoundFile(
            PackageMetadataFixture.CompressDirectory(
                PackageMetadataFixture.CreateProjectInformation(
                    1252, System.Text.Encoding.ASCII.GetBytes("Ledger"))));
        var firstPackage = PackageMetadataFixture.Create(new PackageMetadataFixtureOptions
        {
            VbaProjectPartBytes = part,
            AdditionalEntries = [new PackageMetadataEntry("custom/data.bin", [0x01])]
        });
        var secondPackage = PackageMetadataFixture.Create(new PackageMetadataFixtureOptions
        {
            VbaProjectPartBytes = part,
            AdditionalEntries = [new PackageMetadataEntry("custom/data.bin", [0x02])]
        });
        var reader = new VbaProjectPackageMetadataReader();

        var first = Assert.IsType<VbaProjectPackageMetadata>(reader.Read(firstPackage).Metadata);
        var second = Assert.IsType<VbaProjectPackageMetadata>(reader.Read(secondPackage).Metadata);

        Assert.NotEqual(firstPackage, secondPackage);
        Assert.NotSame(first.VbaProjectPartContentIdentity, second.VbaProjectPartContentIdentity);
        Assert.Equal(first.VbaProjectPartContentIdentity, second.VbaProjectPartContentIdentity);
        Assert.Equal(first.VbaProjectPartContentIdentity.GetHashCode(),
            second.VbaProjectPartContentIdentity.GetHashCode());
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(4u)]
    public void AdmitsExactlyTheFourDeclaredSystemKinds(uint systemKind)
    {
        var directory = PackageMetadataFixture.CreateProjectInformation(
            1252, System.Text.Encoding.ASCII.GetBytes("Ledger"));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            directory.AsSpan(6), systemKind);
        var result = new VbaProjectPackageMetadataReader().Read(
            PackageMetadataFixture.Create(new PackageMetadataFixtureOptions
            {
                ProjectInformationBytes = directory
            }));

        if (systemKind < 4)
        {
            Assert.Null(result.Failure);
            Assert.Equal((VbaProjectSystemKind)systemKind,
                Assert.IsType<VbaProjectPackageMetadata>(result.Metadata).SystemKind);
        }
        else
        {
            Assert.Null(result.Metadata);
            Assert.Equal(VbaProjectPackageMetadataReadFailureKind.InvalidProjectInformation,
                Assert.IsType<VbaProjectPackageMetadataReadFailure>(result.Failure).Kind);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsMismatchedOrInvalidUtf16Constants(bool invalidUtf16)
    {
        var unicode = invalidUtf16
            ? new byte[] { 0x00, 0xd8 }
            : System.Text.Encoding.Unicode.GetBytes("Feature = 2");
        var result = new VbaProjectPackageMetadataReader().Read(
            CreatePackageWithConstants("Feature = 1", unicodeBytes: unicode));

        Assert.Null(result.Metadata);
        Assert.Equal(VbaProjectPackageMetadataReadFailureKind.InvalidProjectInformation,
            Assert.IsType<VbaProjectPackageMetadataReadFailure>(result.Failure).Kind);
    }

    [Theory]
    [InlineData("Feature = 1 : feature = 2")]
    [InlineData("vBa7 = 1")]
    [InlineData("CDecl = 1")]
    [InlineData("Feature=1")]
    [InlineData("Feature = +1")]
    [InlineData("Feature = 32768")]
    [InlineData("Feature = -32769")]
    [InlineData("Feature = 000001")]
    public void RejectsAmbiguousOrMalformedProjectConstants(string constants)
    {
        var result = new VbaProjectPackageMetadataReader().Read(
            CreatePackageWithConstants(constants));

        Assert.Null(result.Metadata);
        Assert.Equal(VbaProjectPackageMetadataReadFailureKind.InvalidProjectInformation,
            Assert.IsType<VbaProjectPackageMetadataReadFailure>(result.Failure).Kind);
    }

    [Fact]
    public void ReadsMbcsProjectConstantsFromTheDeclaredCodePage()
    {
        var result = new VbaProjectPackageMetadataReader().Read(
            CreatePackageWithConstants("機能 = 1 : Trace = -2", codePage: 932));

        Assert.Null(result.Failure);
        var metadata = Assert.IsType<VbaProjectPackageMetadata>(result.Metadata);
        Assert.Equal(932, metadata.CodePage);
        Assert.Equal((short)1, metadata.ProjectConstants["機能"]);
        Assert.Equal((short)-2, metadata.ProjectConstants["trace"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x01, 0x02 })]
    public void CancellationTakesPrecedenceOverInvalidPackageAdmission(byte[]? package)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var failure = Assert.Throws<OperationCanceledException>(() =>
            new VbaProjectPackageMetadataReader().Read(package!, cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
    }

    [Theory]
    [InlineData(255, true)]
    [InlineData(256, false)]
    public void EnforcesThe255CharacterProjectConstantNameBoundary(int length, bool accepted)
    {
        var name = new string('C', length);
        var result = new VbaProjectPackageMetadataReader().Read(
            CreatePackageWithConstants($"{name} = 1"));

        if (accepted)
        {
            Assert.Null(result.Failure);
            Assert.Equal((short)1,
                Assert.IsType<VbaProjectPackageMetadata>(result.Metadata).ProjectConstants[name]);
        }
        else
        {
            Assert.Null(result.Metadata);
            Assert.Equal(VbaProjectPackageMetadataReadFailureKind.InvalidProjectInformation,
                Assert.IsType<VbaProjectPackageMetadataReadFailure>(result.Failure).Kind);
        }
    }

    [Fact]
    public void ReadsSignedProjectConstantsWithCaseInsensitiveLookup()
    {
        var package = CreatePackageWithConstants("FeatureFlag = -32768 : ReleaseMode = 32767");

        var result = new VbaProjectPackageMetadataReader().Read(package);

        Assert.Null(result.Failure);
        var metadata = Assert.IsType<VbaProjectPackageMetadata>(result.Metadata);
        Assert.Equal(2, metadata.ProjectConstants.Count);
        Assert.Equal(short.MinValue, metadata.ProjectConstants["featureflag"]);
        Assert.Equal(short.MaxValue, metadata.ProjectConstants["RELEASEMODE"]);
    }

    [Fact]
    public void ReadsProjectFactsAndExactVbaPartIdentity()
    {
        var part = PackageMetadataFixture.CreateCompoundFile(
            PackageMetadataFixture.CompressDirectory(
                PackageMetadataFixture.CreateProjectInformation(
                    1252, System.Text.Encoding.ASCII.GetBytes("Ledger"))));
        var package = PackageMetadataFixture.Create(new PackageMetadataFixtureOptions
        {
            VbaProjectPartBytes = part
        });

        var result = new VbaProjectPackageMetadataReader().Read(package);

        Assert.Null(result.Failure);
        var metadata = Assert.IsType<VbaProjectPackageMetadata>(result.Metadata);
        Assert.Equal("Ledger", metadata.ProjectName);
        Assert.Equal(1252, metadata.CodePage);
        Assert.Equal(VbaProjectSystemKind.Win64, metadata.SystemKind);
        Assert.Empty(metadata.ProjectConstants);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(part)),
            metadata.VbaProjectPartContentIdentity.Sha256);
    }

    private static byte[] CreatePackageWithConstants(
        string constants,
        int codePage = 1252,
        byte[]? unicodeBytes = null)
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var directory = PackageMetadataFixture.CreateProjectInformation(
            codePage, System.Text.Encoding.ASCII.GetBytes("Ledger"));
        using var stream = new MemoryStream();
        stream.Write(directory);
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        var mbcs = System.Text.Encoding.GetEncoding(codePage).GetBytes(constants);
        var unicode = unicodeBytes ?? System.Text.Encoding.Unicode.GetBytes(constants);
        writer.Write((ushort)0x000c);
        writer.Write((uint)mbcs.Length);
        writer.Write(mbcs);
        writer.Write((ushort)0x003c);
        writer.Write((uint)unicode.Length);
        writer.Write(unicode);
        return PackageMetadataFixture.Create(new PackageMetadataFixtureOptions
        {
            ProjectInformationBytes = stream.ToArray()
        });
    }
}
