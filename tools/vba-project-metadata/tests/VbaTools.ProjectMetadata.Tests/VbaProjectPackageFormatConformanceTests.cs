using System.Buffers.Binary;
using System.Text;
using VbaTools.ProjectMetadata;
using Xunit;

namespace VbaTools.ProjectMetadata.Tests;

public sealed class VbaProjectPackageFormatConformanceTests
{
    private const int ProjectNameRecordOffset = 38;

    [Theory]
    [InlineData(1252, "CaféLedger")]
    [InlineData(932, "請求mOdEl")]
    public void Read_returns_the_exact_project_name_from_captured_package_bytes(
        int codePage,
        string projectName)
    {
        var package = PackageMetadataFixture.Create(
            projectName,
            codePage);

        var identity = AssertSuccess(package);

        Assert.Equal(projectName, identity.ProjectName);
        Assert.Equal(codePage, identity.CodePage);
    }

    [Fact]
    public void Read_accepts_a_project_name_longer_than_the_module_name_limit()
    {
        var projectName = new string('P', 40);

        var identity = AssertSuccess(
            PackageMetadataFixture.Create(projectName, 1252));

        Assert.Equal(projectName, identity.ProjectName);
    }

    [Theory]
    [InlineData("Project Name")]
    [InlineData("Class")]
    public void Read_preserves_a_non_identifier_project_name_allowed_by_ms_ovba(
        string projectName)
    {
        var identity = AssertSuccess(
            PackageMetadataFixture.Create(projectName, 1252));

        Assert.Equal(projectName, identity.ProjectName);
    }

    [Fact]
    public void Read_accepts_the_128_byte_project_name_boundary()
    {
        var projectName = new string('P', 128);

        var identity = AssertSuccess(
            PackageMetadataFixture.Create(projectName, 1252));

        Assert.Equal(projectName, identity.ProjectName);
    }

    [Fact]
    public void Read_rejects_project_name_bytes_invalid_for_the_declared_code_page()
    {
        var package = PackageMetadataFixture.Create(
            new PackageMetadataFixtureOptions
            {
                CodePage = 932,
                ProjectNameBytes = [0x82]
            });

        AssertFailure(
            package,
            VbaProjectPackageMetadataReadFailureKind.InvalidProjectName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65535)]
    public void Read_rejects_unsupported_project_code_pages(int codePage)
    {
        var package = PackageMetadataFixture.Create(
            new PackageMetadataFixtureOptions
            {
                CodePage = codePage,
                ProjectNameBytes = Encoding.ASCII.GetBytes("Ledger")
            });

        AssertFailure(
            package,
            VbaProjectPackageMetadataReadFailureKind.UnsupportedCodePage);
    }

    [Fact]
    public void Read_rejects_an_empty_project_name_record()
        => AssertProjectNameFailure([]);

    [Fact]
    public void Read_rejects_a_project_name_record_over_128_bytes()
        => AssertProjectNameFailure(
            Enumerable.Repeat((byte)'A', 129).ToArray());

    [Fact]
    public void Read_rejects_a_project_name_record_containing_null()
        => AssertProjectNameFailure([(byte)'A', 0, (byte)'B']);

    [Fact]
    public void Read_rejects_a_missing_project_name_record()
    {
        var directory = CreateDefaultProjectInformation();

        var package = CreatePackageWithProjectInformation(
            directory[..ProjectNameRecordOffset]);

        AssertFailure(
            package,
            VbaProjectPackageMetadataReadFailureKind.InvalidProjectInformation);
    }

    [Fact]
    public void Read_rejects_a_wrong_project_name_record_identifier()
    {
        var directory = CreateDefaultProjectInformation();
        BinaryPrimitives.WriteUInt16LittleEndian(
            directory.AsSpan(ProjectNameRecordOffset),
            0x1234);

        AssertFailure(
            CreatePackageWithProjectInformation(directory),
            VbaProjectPackageMetadataReadFailureKind.InvalidProjectInformation);
    }

    [Fact]
    public void Read_rejects_a_duplicate_project_name_record()
    {
        var projectNameBytes = Encoding.ASCII.GetBytes("ContainingProject");
        var directory = PackageMetadataFixture
            .CreateProjectInformation(1252, projectNameBytes);
        var projectDocStringOffset =
            ProjectNameRecordOffset + 6 + projectNameBytes.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(
            directory.AsSpan(projectDocStringOffset),
            0x0004);

        AssertFailure(
            CreatePackageWithProjectInformation(directory),
            VbaProjectPackageMetadataReadFailureKind.InvalidProjectInformation);
    }

    [Fact]
    public void Read_rejects_a_truncated_required_record_after_project_name()
    {
        var projectNameBytes = Encoding.ASCII.GetBytes("ContainingProject");
        var directory = PackageMetadataFixture
            .CreateProjectInformation(1252, projectNameBytes);
        var afterProjectDocStringHeader =
            ProjectNameRecordOffset + 6 + projectNameBytes.Length + 6;

        AssertFailure(
            CreatePackageWithProjectInformation(
                directory[..afterProjectDocStringHeader]),
            VbaProjectPackageMetadataReadFailureKind.InvalidProjectInformation);
    }

    [Fact]
    public void Read_rejects_a_malformed_project_version_record_after_project_name()
    {
        var directory = CreateDefaultProjectInformation();
        BinaryPrimitives.WriteUInt32LittleEndian(
            directory.AsSpan(directory.Length - 10),
            5);

        AssertFailure(
            CreatePackageWithProjectInformation(directory),
            VbaProjectPackageMetadataReadFailureKind.InvalidProjectInformation);
    }

    [Theory]
    [InlineData(16, 0u)]
    [InlineData(26, 0u)]
    [InlineData(-16, 1u)]
    public void Read_rejects_invalid_required_project_information_values(
        int valueOffset,
        uint invalidValue)
    {
        var directory = CreateDefaultProjectInformation();
        var resolvedOffset = valueOffset < 0
            ? directory.Length + valueOffset
            : valueOffset;
        BinaryPrimitives.WriteUInt32LittleEndian(
            directory.AsSpan(resolvedOffset),
            invalidValue);

        AssertFailure(
            CreatePackageWithProjectInformation(directory),
            VbaProjectPackageMetadataReadFailureKind.InvalidProjectInformation);
    }

    [Fact]
    public void Read_validates_an_optional_project_constants_record()
    {
        var directory = AppendProjectConstants(
            CreateDefaultProjectInformation(),
            "TRACE = 1",
            unicodeMarker: 0x003c);

        var identity = AssertSuccess(
            CreatePackageWithProjectInformation(directory));

        Assert.Equal("ContainingProject", identity.ProjectName);
    }

    [Fact]
    public void Read_rejects_a_malformed_optional_project_constants_marker()
    {
        var directory = AppendProjectConstants(
            CreateDefaultProjectInformation(),
            "TRACE = 1",
            unicodeMarker: 0x1234);

        AssertFailure(
            CreatePackageWithProjectInformation(directory),
            VbaProjectPackageMetadataReadFailureKind.InvalidProjectInformation);
    }

    [Fact]
    public void Read_rejects_a_missing_content_types_part()
    {
        var package = PackageMetadataFixture.Create(
            new PackageMetadataFixtureOptions
            {
                IncludeContentTypesPart = false
            });

        AssertFailure(
            package,
            VbaProjectPackageMetadataReadFailureKind.InvalidPackageTopology);
    }

    [Fact]
    public void Read_rejects_a_duplicate_content_types_part()
    {
        var contentTypes = Encoding.UTF8.GetBytes(
            PackageMetadataFixture.CreateContentTypesXml(
                "xl/vbaProject.bin",
                "application/vnd.ms-office.vbaProject"));
        var package = PackageMetadataFixture.Create(
            new PackageMetadataFixtureOptions
            {
                AdditionalEntries =
                [
                    new PackageMetadataEntry(
                        "[CONTENT_TYPES].XML",
                        contentTypes)
                ]
            });

        AssertFailure(
            package,
            VbaProjectPackageMetadataReadFailureKind.InvalidPackageTopology);
    }

    [Fact]
    public void Read_rejects_malformed_content_types_xml()
    {
        var package = PackageMetadataFixture.Create(
            new PackageMetadataFixtureOptions
            {
                ContentTypesXml = "<Types>"
            });

        AssertFailure(
            package,
            VbaProjectPackageMetadataReadFailureKind.InvalidPackageTopology);
    }

    [Fact]
    public void Read_rejects_duplicate_case_equivalent_vba_overrides()
    {
        const string contentTypes =
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
            + "<Override PartName=\"/xl/vbaProject.bin\" ContentType=\"application/vnd.ms-office.vbaProject\"/>"
            + "<Override PartName=\"/XL/VBAPROJECT.BIN\" ContentType=\"application/vnd.ms-office.vbaProject\"/>"
            + "</Types>";
        var package = PackageMetadataFixture.Create(
            new PackageMetadataFixtureOptions
            {
                ContentTypesXml = contentTypes
            });

        AssertFailure(
            package,
            VbaProjectPackageMetadataReadFailureKind.InvalidPackageTopology);
    }

    [Fact]
    public void Read_accepts_case_equivalent_opc_part_names()
    {
        var package = PackageMetadataFixture.Create(
            new PackageMetadataFixtureOptions
            {
                VbaProjectPartName = "XL/VBAPROJECT.BIN"
            });

        Assert.Equal("ContainingProject", AssertSuccess(package).ProjectName);
    }

    [Fact]
    public void Read_rejects_a_missing_workbook_part()
    {
        var package = PackageMetadataFixture.Create(
            new PackageMetadataFixtureOptions
            {
                IncludeWorkbookPart = false
            });

        AssertFailure(
            package,
            VbaProjectPackageMetadataReadFailureKind.InvalidPackageTopology);
    }

    [Fact]
    public void Read_rejects_an_orphan_vba_part_without_a_workbook_relationship()
    {
        var package = PackageMetadataFixture.Create(
            new PackageMetadataFixtureOptions
            {
                IncludeWorkbookRelationshipsPart = false
            });

        AssertFailure(
            package,
            VbaProjectPackageMetadataReadFailureKind.InvalidPackageTopology);
    }

    [Fact]
    public void Read_rejects_duplicate_workbook_relationship_parts()
    {
        var package = PackageMetadataFixture.Create(
            new PackageMetadataFixtureOptions
            {
                AdditionalEntries =
                [
                    new PackageMetadataEntry(
                        "XL/_RELS/WORKBOOK.XML.RELS",
                        Encoding.UTF8.GetBytes(
                            PackageMetadataFixture
                                .CreateWorkbookRelationshipsXml()))
                ]
            });

        AssertFailure(
            package,
            VbaProjectPackageMetadataReadFailureKind.InvalidPackageTopology);
    }

    [Fact]
    public void Read_rejects_duplicate_vba_project_relationships()
    {
        var package = PackageMetadataFixture.Create(
            new PackageMetadataFixtureOptions
            {
                WorkbookRelationshipsXml = PackageMetadataFixture
                    .CreateWorkbookRelationshipsXml(duplicate: true)
            });

        AssertFailure(
            package,
            VbaProjectPackageMetadataReadFailureKind.InvalidPackageTopology);
    }

    [Theory]
    [InlineData("other.bin", "http://schemas.microsoft.com/office/2006/relationships/vbaProject", null)]
    [InlineData("vbaProject.bin", "urn:not-vba-project", null)]
    [InlineData("https://example.invalid/vbaProject.bin", "http://schemas.microsoft.com/office/2006/relationships/vbaProject", "External")]
    public void Read_rejects_a_wrong_or_external_vba_project_relationship(
        string target,
        string relationshipType,
        string? targetMode)
    {
        var package = PackageMetadataFixture.Create(
            new PackageMetadataFixtureOptions
            {
                WorkbookRelationshipsXml = PackageMetadataFixture
                    .CreateWorkbookRelationshipsXml(
                        target,
                        relationshipType,
                        targetMode)
            });

        AssertFailure(
            package,
            VbaProjectPackageMetadataReadFailureKind.InvalidPackageTopology);
    }

    [Fact]
    public void Read_rejects_a_missing_vba_project_part()
    {
        var package = PackageMetadataFixture.Create(
            new PackageMetadataFixtureOptions
            {
                IncludeVbaProjectPart = false
            });

        AssertFailure(
            package,
            VbaProjectPackageMetadataReadFailureKind.InvalidPackageTopology);
    }

    [Fact]
    public void Read_rejects_duplicate_case_equivalent_vba_project_parts()
    {
        var duplicatePart = PackageMetadataFixture
            .CreateCompoundFile(
                PackageMetadataFixture.CompressDirectory(
                    CreateDefaultProjectInformation()));
        var package = PackageMetadataFixture.Create(
            new PackageMetadataFixtureOptions
            {
                AdditionalEntries =
                [
                    new PackageMetadataEntry(
                        "XL/VBAPROJECT.BIN",
                        duplicatePart)
                ]
            });

        AssertFailure(
            package,
            VbaProjectPackageMetadataReadFailureKind.InvalidPackageTopology);
    }

    [Fact]
    public void Read_rejects_a_vba_project_part_at_the_wrong_path()
    {
        var package = PackageMetadataFixture.Create(
            new PackageMetadataFixtureOptions
            {
                VbaProjectPartName = "custom/vbaProject.bin"
            });

        AssertFailure(
            package,
            VbaProjectPackageMetadataReadFailureKind.InvalidVbaProjectPart);
    }

    [Fact]
    public void Read_rejects_a_vba_project_part_with_the_wrong_content_type()
    {
        var package = PackageMetadataFixture.Create(
            new PackageMetadataFixtureOptions
            {
                VbaProjectContentType = "application/octet-stream"
            });

        AssertFailure(
            package,
            VbaProjectPackageMetadataReadFailureKind.InvalidVbaProjectPart);
    }

    [Fact]
    public void Read_ignores_an_unrelated_part_with_a_similar_suffix()
    {
        var package = PackageMetadataFixture.Create(
            new PackageMetadataFixtureOptions
            {
                AdditionalEntries =
                [
                    new PackageMetadataEntry(
                        "custom/not-vbaProject.bin",
                        [0x01])
                ]
            });

        Assert.Equal("ContainingProject", AssertSuccess(package).ProjectName);
    }

    [Fact]
    public void Read_rejects_a_compound_file_without_the_vba_storage()
    {
        var package = PackageMetadataFixture.Create(
            new PackageMetadataFixtureOptions
            {
                VbaStorageName = "NotVba"
            });

        AssertFailure(
            package,
            VbaProjectPackageMetadataReadFailureKind.InvalidCompoundFile);
    }

    [Fact]
    public void Read_rejects_a_compound_file_without_the_directory_stream()
    {
        var package = PackageMetadataFixture.Create(
            new PackageMetadataFixtureOptions
            {
                DirectoryStreamName = "not-dir"
            });

        AssertFailure(
            package,
            VbaProjectPackageMetadataReadFailureKind.InvalidCompoundFile);
    }

    [Fact]
    public void Read_rejects_a_malformed_compound_file()
    {
        var package = PackageMetadataFixture.Create(
            new PackageMetadataFixtureOptions
            {
                VbaProjectPartBytes = [0x01, 0x02, 0x03]
            });

        AssertFailure(
            package,
            VbaProjectPackageMetadataReadFailureKind.InvalidCompoundFile);
    }

    [Fact]
    public void Read_rejects_an_invalid_compressed_directory_stream()
    {
        var invalidCompressedDirectory = new byte[4096];
        var package = PackageMetadataFixture.Create(
            new PackageMetadataFixtureOptions
            {
                VbaProjectPartBytes = PackageMetadataFixture
                    .CreateCompoundFile(invalidCompressedDirectory)
            });

        AssertFailure(
            package,
            VbaProjectPackageMetadataReadFailureKind.InvalidCompressedDirectory);
    }

    [Fact]
    public void Read_rejects_an_encrypted_style_non_opc_compound_package()
    {
        var compoundPackage = PackageMetadataFixture
            .CreateCompoundFile(new byte[4096]);

        AssertFailure(
            compoundPackage,
            VbaProjectPackageMetadataReadFailureKind.InvalidPackage);
    }

    [Fact]
    public void Read_rejects_null_empty_and_non_package_bytes()
    {
        AssertFailure(
            null,
            VbaProjectPackageMetadataReadFailureKind.InvalidPackage);
        AssertFailure(
            [],
            VbaProjectPackageMetadataReadFailureKind.InvalidPackage);
        AssertFailure(
            [0x01, 0x02, 0x03],
            VbaProjectPackageMetadataReadFailureKind.InvalidPackage);
    }

    [Fact]
    public void Read_enforces_its_package_length_bound()
        => AssertFailure(
            GC.AllocateUninitializedArray<byte>(
                VbaProjectPackageMetadataReader.MaximumPackageLength + 1),
            VbaProjectPackageMetadataReadFailureKind.InvalidPackage);

    [Fact]
    public void Read_enforces_the_content_types_length_bound()
    {
        var package = PackageMetadataFixture.Create(
            new PackageMetadataFixtureOptions
            {
                ContentTypesXml = new string(' ', (1024 * 1024) + 1)
            });

        AssertFailure(
            package,
            VbaProjectPackageMetadataReadFailureKind.InvalidPackageTopology);
    }

    [Fact]
    public void Read_propagates_cancellation()
    {
        var package = PackageMetadataFixture.Create(
            "ContainingProject",
            1252);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => new VbaProjectPackageMetadataReader().Read(
                package,
                cancellation.Token));
    }

    private static void AssertProjectNameFailure(byte[] projectNameBytes)
    {
        var package = PackageMetadataFixture.Create(
            new PackageMetadataFixtureOptions
            {
                ProjectNameBytes = projectNameBytes
            });

        AssertFailure(
            package,
            VbaProjectPackageMetadataReadFailureKind.InvalidProjectName);
    }

    private static VbaProjectPackageMetadata AssertSuccess(byte[] package)
    {
        var result = new VbaProjectPackageMetadataReader().Read(package);

        Assert.Null(result.Failure);
        return Assert.IsType<VbaProjectPackageMetadata>(result.Metadata);
    }

    private static void AssertFailure(
        byte[]? package,
        VbaProjectPackageMetadataReadFailureKind expectedKind)
    {
        var result = new VbaProjectPackageMetadataReader().Read(package!);

        Assert.Null(result.Metadata);
        Assert.Equal(
            expectedKind,
            Assert.IsType<VbaProjectPackageMetadataReadFailure>(result.Failure).Kind);
    }

    private static byte[] CreateDefaultProjectInformation()
        => PackageMetadataFixture.CreateProjectInformation(
            1252,
            Encoding.ASCII.GetBytes("ContainingProject"));

    private static byte[] CreatePackageWithProjectInformation(
        byte[] projectInformation)
        => PackageMetadataFixture.Create(
            new PackageMetadataFixtureOptions
            {
                ProjectInformationBytes = projectInformation
            });

    private static byte[] AppendProjectConstants(
        byte[] directory,
        string constants,
        ushort unicodeMarker)
    {
        var mbcs = Encoding.GetEncoding(1252).GetBytes(constants);
        var unicode = Encoding.Unicode.GetBytes(constants);
        using var stream = new MemoryStream();
        stream.Write(directory);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write((ushort)0x000c);
        writer.Write((uint)mbcs.Length);
        writer.Write(mbcs);
        writer.Write(unicodeMarker);
        writer.Write((uint)unicode.Length);
        writer.Write(unicode);
        return stream.ToArray();
    }
}
