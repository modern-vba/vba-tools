using VbaTools.ProjectMetadata;
using Xunit;

namespace VbaTools.ProjectMetadata.Tests;

public sealed class VbaProjectPackageContentTypeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsDtdDeclarationsInEitherPackageXmlPart(bool relationships)
    {
        var options = relationships
            ? new PackageMetadataFixtureOptions
            {
                WorkbookRelationshipsXml = "<!DOCTYPE Relationships []>"
                    + PackageMetadataFixture.CreateWorkbookRelationshipsXml()
            }
            : new PackageMetadataFixtureOptions
            {
                ContentTypesXml = "<!DOCTYPE Types []>"
                    + PackageMetadataFixture.CreateContentTypesXml(
                        "xl/vbaProject.bin", "application/vnd.ms-office.vbaProject")
            };

        var result = new VbaProjectPackageMetadataReader().Read(
            PackageMetadataFixture.Create(options));

        Assert.Null(result.Metadata);
        Assert.Equal(VbaProjectPackageMetadataReadFailureKind.InvalidPackageTopology,
            Assert.IsType<VbaProjectPackageMetadataReadFailure>(result.Failure).Kind);
    }

    [Theory]
    [InlineData("default", null)]
    [InlineData("override", VbaProjectPackageMetadataReadFailureKind.InvalidVbaProjectPart)]
    [InlineData("namespace", VbaProjectPackageMetadataReadFailureKind.InvalidPackageTopology)]
    [InlineData("duplicate-default", VbaProjectPackageMetadataReadFailureKind.InvalidPackageTopology)]
    [InlineData("other-effective-vba", VbaProjectPackageMetadataReadFailureKind.InvalidPackageTopology)]
    [InlineData("unrelated-name", null)]
    public void UsesEffectiveOpcContentTypes(
        string scenario,
        VbaProjectPackageMetadataReadFailureKind? expectedFailure)
    {
        const string vbaContentType = "application/vnd.ms-office.vbaProject";
        var contentTypes = PackageMetadataFixture.CreateContentTypesXml(
            "xl/vbaProject.bin", vbaContentType);
        IReadOnlyList<PackageMetadataEntry> additionalEntries = [];
        if (scenario != "unrelated-name")
        {
            contentTypes = contentTypes
                .Replace("application/octet-stream", vbaContentType, StringComparison.Ordinal)
                .Replace(
                    "<Override PartName=\"/xl/vbaProject.bin\" ContentType=\"" + vbaContentType + "\"/>",
                    string.Empty,
                    StringComparison.Ordinal);
        }

        switch (scenario)
        {
            case "override":
                contentTypes = contentTypes.Replace(
                    "</Types>",
                    "<Override PartName=\"/xl/vbaProject.bin\" ContentType=\"application/octet-stream\"/></Types>",
                    StringComparison.Ordinal);
                break;
            case "namespace":
                contentTypes = contentTypes.Replace(
                    "http://schemas.openxmlformats.org/package/2006/content-types",
                    "urn:not-opc",
                    StringComparison.Ordinal);
                break;
            case "duplicate-default":
                contentTypes = contentTypes.Replace(
                    "</Types>",
                    "<Default Extension=\"BIN\" ContentType=\"" + vbaContentType + "\"/></Types>",
                    StringComparison.Ordinal);
                break;
            case "other-effective-vba":
                additionalEntries = [new PackageMetadataEntry("custom/other.bin", [0x01])];
                break;
            case "unrelated-name":
                additionalEntries = [new PackageMetadataEntry("custom/vbaProject.bin", [0x01])];
                break;
        }

        var result = new VbaProjectPackageMetadataReader().Read(
            PackageMetadataFixture.Create(new PackageMetadataFixtureOptions
            {
                ContentTypesXml = contentTypes,
                AdditionalEntries = additionalEntries
            }));

        if (expectedFailure is null)
        {
            Assert.Null(result.Failure);
            Assert.Equal("ContainingProject",
                Assert.IsType<VbaProjectPackageMetadata>(result.Metadata).ProjectName);
        }
        else
        {
            Assert.Null(result.Metadata);
            Assert.Equal(expectedFailure.Value,
                Assert.IsType<VbaProjectPackageMetadataReadFailure>(result.Failure).Kind);
        }
    }
}
