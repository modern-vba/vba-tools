using System.Buffers.Binary;
using System.Text;
using VbaLanguageServer.Workspace;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class OpenXmlVbaProjectIdentityReaderTests
{
    private const int ProjectNameRecordOffset = 38;

    [Theory]
    [InlineData(1252, "CaféLedger")]
    [InlineData(932, "請求mOdEl")]
    public void Read_returns_the_exact_project_name_from_captured_package_bytes(
        int codePage,
        string projectName)
    {
        var package = VbaProjectIdentityWorkbookFixture.Create(
            projectName,
            codePage);

        var identity = AssertSuccess(package);

        Assert.Equal(projectName, identity.VbaProjectName);
        Assert.Equal(codePage, identity.ProjectCodePage);
        Assert.Equal(
            VbaSourceTemplateContentIdentity.FromBytes(package),
            identity.SourceTemplateContentIdentity);
    }

    [Fact]
    public void Fixture_is_deterministic_and_content_identity_covers_unrelated_bytes()
    {
        var firstPackage = VbaProjectIdentityWorkbookFixture.Create(
            new VbaProjectIdentityWorkbookFixtureOptions
            {
                AdditionalEntries =
                [
                    new VbaProjectIdentityPackageEntry(
                        "custom/unrelated.bin",
                        [0x01])
                ]
            });
        var equalPackage = VbaProjectIdentityWorkbookFixture.Create(
            new VbaProjectIdentityWorkbookFixtureOptions
            {
                AdditionalEntries =
                [
                    new VbaProjectIdentityPackageEntry(
                        "custom/unrelated.bin",
                        [0x01])
                ]
            });
        var changedPackage = VbaProjectIdentityWorkbookFixture.Create(
            new VbaProjectIdentityWorkbookFixtureOptions
            {
                AdditionalEntries =
                [
                    new VbaProjectIdentityPackageEntry(
                        "custom/unrelated.bin",
                        [0x02])
                ]
            });

        Assert.Equal(firstPackage, equalPackage);
        var first = AssertSuccess(firstPackage);
        var changed = AssertSuccess(changedPackage);
        Assert.Equal(first.VbaProjectName, changed.VbaProjectName);
        Assert.Equal(first.ProjectCodePage, changed.ProjectCodePage);
        Assert.NotEqual(
            first.SourceTemplateContentIdentity,
            changed.SourceTemplateContentIdentity);
    }

    [Fact]
    public void Read_accepts_a_project_name_longer_than_the_module_name_limit()
    {
        var projectName = new string('P', 40);

        var identity = AssertSuccess(
            VbaProjectIdentityWorkbookFixture.Create(projectName, 1252));

        Assert.Equal(projectName, identity.VbaProjectName);
    }

    [Theory]
    [InlineData("Project Name")]
    [InlineData("Class")]
    public void Read_preserves_a_non_identifier_project_name_allowed_by_ms_ovba(
        string projectName)
    {
        var identity = AssertSuccess(
            VbaProjectIdentityWorkbookFixture.Create(projectName, 1252));

        Assert.Equal(projectName, identity.VbaProjectName);
    }

    [Fact]
    public void Read_accepts_the_128_byte_project_name_boundary()
    {
        var projectName = new string('P', 128);

        var identity = AssertSuccess(
            VbaProjectIdentityWorkbookFixture.Create(projectName, 1252));

        Assert.Equal(projectName, identity.VbaProjectName);
    }

    [Fact]
    public void Read_rejects_project_name_bytes_invalid_for_the_declared_code_page()
    {
        var package = VbaProjectIdentityWorkbookFixture.Create(
            new VbaProjectIdentityWorkbookFixtureOptions
            {
                CodePage = 932,
                ProjectNameBytes = [0x82]
            });

        AssertFailure(
            package,
            VbaProjectIdentityReadFailureKind.InvalidProjectName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65535)]
    public void Read_rejects_unsupported_project_code_pages(int codePage)
    {
        var package = VbaProjectIdentityWorkbookFixture.Create(
            new VbaProjectIdentityWorkbookFixtureOptions
            {
                CodePage = codePage,
                ProjectNameBytes = Encoding.ASCII.GetBytes("Ledger")
            });

        AssertFailure(
            package,
            VbaProjectIdentityReadFailureKind.UnsupportedCodePage);
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
            VbaProjectIdentityReadFailureKind.InvalidProjectInformation);
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
            VbaProjectIdentityReadFailureKind.InvalidProjectInformation);
    }

    [Fact]
    public void Read_rejects_a_duplicate_project_name_record()
    {
        var projectNameBytes = Encoding.ASCII.GetBytes("ContainingProject");
        var directory = VbaProjectIdentityWorkbookFixture
            .CreateProjectInformation(1252, projectNameBytes);
        var projectDocStringOffset =
            ProjectNameRecordOffset + 6 + projectNameBytes.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(
            directory.AsSpan(projectDocStringOffset),
            0x0004);

        AssertFailure(
            CreatePackageWithProjectInformation(directory),
            VbaProjectIdentityReadFailureKind.InvalidProjectInformation);
    }

    [Fact]
    public void Read_rejects_a_truncated_required_record_after_project_name()
    {
        var projectNameBytes = Encoding.ASCII.GetBytes("ContainingProject");
        var directory = VbaProjectIdentityWorkbookFixture
            .CreateProjectInformation(1252, projectNameBytes);
        var afterProjectDocStringHeader =
            ProjectNameRecordOffset + 6 + projectNameBytes.Length + 6;

        AssertFailure(
            CreatePackageWithProjectInformation(
                directory[..afterProjectDocStringHeader]),
            VbaProjectIdentityReadFailureKind.InvalidProjectInformation);
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
            VbaProjectIdentityReadFailureKind.InvalidProjectInformation);
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
            VbaProjectIdentityReadFailureKind.InvalidProjectInformation);
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

        Assert.Equal("ContainingProject", identity.VbaProjectName);
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
            VbaProjectIdentityReadFailureKind.InvalidProjectInformation);
    }

    [Fact]
    public void Read_rejects_a_missing_content_types_part()
    {
        var package = VbaProjectIdentityWorkbookFixture.Create(
            new VbaProjectIdentityWorkbookFixtureOptions
            {
                IncludeContentTypesPart = false
            });

        AssertFailure(
            package,
            VbaProjectIdentityReadFailureKind.InvalidPackageTopology);
    }

    [Fact]
    public void Read_rejects_a_duplicate_content_types_part()
    {
        var contentTypes = Encoding.UTF8.GetBytes(
            VbaProjectIdentityWorkbookFixture.CreateContentTypesXml(
                "xl/vbaProject.bin",
                "application/vnd.ms-office.vbaProject"));
        var package = VbaProjectIdentityWorkbookFixture.Create(
            new VbaProjectIdentityWorkbookFixtureOptions
            {
                AdditionalEntries =
                [
                    new VbaProjectIdentityPackageEntry(
                        "[CONTENT_TYPES].XML",
                        contentTypes)
                ]
            });

        AssertFailure(
            package,
            VbaProjectIdentityReadFailureKind.InvalidPackageTopology);
    }

    [Fact]
    public void Read_rejects_malformed_content_types_xml()
    {
        var package = VbaProjectIdentityWorkbookFixture.Create(
            new VbaProjectIdentityWorkbookFixtureOptions
            {
                ContentTypesXml = "<Types>"
            });

        AssertFailure(
            package,
            VbaProjectIdentityReadFailureKind.InvalidPackageTopology);
    }

    [Fact]
    public void Read_rejects_duplicate_case_equivalent_vba_overrides()
    {
        const string contentTypes =
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
            + "<Override PartName=\"/xl/vbaProject.bin\" ContentType=\"application/vnd.ms-office.vbaProject\"/>"
            + "<Override PartName=\"/XL/VBAPROJECT.BIN\" ContentType=\"application/vnd.ms-office.vbaProject\"/>"
            + "</Types>";
        var package = VbaProjectIdentityWorkbookFixture.Create(
            new VbaProjectIdentityWorkbookFixtureOptions
            {
                ContentTypesXml = contentTypes
            });

        AssertFailure(
            package,
            VbaProjectIdentityReadFailureKind.InvalidPackageTopology);
    }

    [Fact]
    public void Read_accepts_case_equivalent_opc_part_names()
    {
        var package = VbaProjectIdentityWorkbookFixture.Create(
            new VbaProjectIdentityWorkbookFixtureOptions
            {
                VbaProjectPartName = "XL/VBAPROJECT.BIN"
            });

        Assert.Equal("ContainingProject", AssertSuccess(package).VbaProjectName);
    }

    [Fact]
    public void Read_rejects_a_missing_workbook_part()
    {
        var package = VbaProjectIdentityWorkbookFixture.Create(
            new VbaProjectIdentityWorkbookFixtureOptions
            {
                IncludeWorkbookPart = false
            });

        AssertFailure(
            package,
            VbaProjectIdentityReadFailureKind.InvalidPackageTopology);
    }

    [Fact]
    public void Read_rejects_an_orphan_vba_part_without_a_workbook_relationship()
    {
        var package = VbaProjectIdentityWorkbookFixture.Create(
            new VbaProjectIdentityWorkbookFixtureOptions
            {
                IncludeWorkbookRelationshipsPart = false
            });

        AssertFailure(
            package,
            VbaProjectIdentityReadFailureKind.InvalidPackageTopology);
    }

    [Fact]
    public void Read_rejects_duplicate_workbook_relationship_parts()
    {
        var package = VbaProjectIdentityWorkbookFixture.Create(
            new VbaProjectIdentityWorkbookFixtureOptions
            {
                AdditionalEntries =
                [
                    new VbaProjectIdentityPackageEntry(
                        "XL/_RELS/WORKBOOK.XML.RELS",
                        Encoding.UTF8.GetBytes(
                            VbaProjectIdentityWorkbookFixture
                                .CreateWorkbookRelationshipsXml()))
                ]
            });

        AssertFailure(
            package,
            VbaProjectIdentityReadFailureKind.InvalidPackageTopology);
    }

    [Fact]
    public void Read_rejects_duplicate_vba_project_relationships()
    {
        var package = VbaProjectIdentityWorkbookFixture.Create(
            new VbaProjectIdentityWorkbookFixtureOptions
            {
                WorkbookRelationshipsXml = VbaProjectIdentityWorkbookFixture
                    .CreateWorkbookRelationshipsXml(duplicate: true)
            });

        AssertFailure(
            package,
            VbaProjectIdentityReadFailureKind.InvalidPackageTopology);
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
        var package = VbaProjectIdentityWorkbookFixture.Create(
            new VbaProjectIdentityWorkbookFixtureOptions
            {
                WorkbookRelationshipsXml = VbaProjectIdentityWorkbookFixture
                    .CreateWorkbookRelationshipsXml(
                        target,
                        relationshipType,
                        targetMode)
            });

        AssertFailure(
            package,
            VbaProjectIdentityReadFailureKind.InvalidPackageTopology);
    }

    [Fact]
    public void Read_rejects_a_missing_vba_project_part()
    {
        var package = VbaProjectIdentityWorkbookFixture.Create(
            new VbaProjectIdentityWorkbookFixtureOptions
            {
                IncludeVbaProjectPart = false
            });

        AssertFailure(
            package,
            VbaProjectIdentityReadFailureKind.InvalidPackageTopology);
    }

    [Fact]
    public void Read_rejects_duplicate_case_equivalent_vba_project_parts()
    {
        var duplicatePart = VbaProjectIdentityWorkbookFixture
            .CreateCompoundFile(
                VbaProjectIdentityWorkbookFixture.CompressDirectory(
                    CreateDefaultProjectInformation()));
        var package = VbaProjectIdentityWorkbookFixture.Create(
            new VbaProjectIdentityWorkbookFixtureOptions
            {
                AdditionalEntries =
                [
                    new VbaProjectIdentityPackageEntry(
                        "XL/VBAPROJECT.BIN",
                        duplicatePart)
                ]
            });

        AssertFailure(
            package,
            VbaProjectIdentityReadFailureKind.InvalidPackageTopology);
    }

    [Fact]
    public void Read_rejects_a_vba_project_part_at_the_wrong_path()
    {
        var package = VbaProjectIdentityWorkbookFixture.Create(
            new VbaProjectIdentityWorkbookFixtureOptions
            {
                VbaProjectPartName = "custom/vbaProject.bin"
            });

        AssertFailure(
            package,
            VbaProjectIdentityReadFailureKind.InvalidVbaProjectPart);
    }

    [Fact]
    public void Read_rejects_a_vba_project_part_with_the_wrong_content_type()
    {
        var package = VbaProjectIdentityWorkbookFixture.Create(
            new VbaProjectIdentityWorkbookFixtureOptions
            {
                VbaProjectContentType = "application/octet-stream"
            });

        AssertFailure(
            package,
            VbaProjectIdentityReadFailureKind.InvalidVbaProjectPart);
    }

    [Fact]
    public void Read_ignores_an_unrelated_part_with_a_similar_suffix()
    {
        var package = VbaProjectIdentityWorkbookFixture.Create(
            new VbaProjectIdentityWorkbookFixtureOptions
            {
                AdditionalEntries =
                [
                    new VbaProjectIdentityPackageEntry(
                        "custom/not-vbaProject.bin",
                        [0x01])
                ]
            });

        Assert.Equal("ContainingProject", AssertSuccess(package).VbaProjectName);
    }

    [Fact]
    public void Read_rejects_a_compound_file_without_the_vba_storage()
    {
        var package = VbaProjectIdentityWorkbookFixture.Create(
            new VbaProjectIdentityWorkbookFixtureOptions
            {
                VbaStorageName = "NotVba"
            });

        AssertFailure(
            package,
            VbaProjectIdentityReadFailureKind.InvalidCompoundFile);
    }

    [Fact]
    public void Read_rejects_a_compound_file_without_the_directory_stream()
    {
        var package = VbaProjectIdentityWorkbookFixture.Create(
            new VbaProjectIdentityWorkbookFixtureOptions
            {
                DirectoryStreamName = "not-dir"
            });

        AssertFailure(
            package,
            VbaProjectIdentityReadFailureKind.InvalidCompoundFile);
    }

    [Fact]
    public void Read_rejects_a_malformed_compound_file()
    {
        var package = VbaProjectIdentityWorkbookFixture.Create(
            new VbaProjectIdentityWorkbookFixtureOptions
            {
                VbaProjectPartBytes = [0x01, 0x02, 0x03]
            });

        AssertFailure(
            package,
            VbaProjectIdentityReadFailureKind.InvalidCompoundFile);
    }

    [Fact]
    public void Read_rejects_an_invalid_compressed_directory_stream()
    {
        var invalidCompressedDirectory = new byte[4096];
        var package = VbaProjectIdentityWorkbookFixture.Create(
            new VbaProjectIdentityWorkbookFixtureOptions
            {
                VbaProjectPartBytes = VbaProjectIdentityWorkbookFixture
                    .CreateCompoundFile(invalidCompressedDirectory)
            });

        AssertFailure(
            package,
            VbaProjectIdentityReadFailureKind.InvalidCompressedDirectory);
    }

    [Fact]
    public void Read_rejects_an_encrypted_style_non_opc_compound_package()
    {
        var compoundPackage = VbaProjectIdentityWorkbookFixture
            .CreateCompoundFile(new byte[4096]);

        AssertFailure(
            compoundPackage,
            VbaProjectIdentityReadFailureKind.InvalidPackage);
    }

    [Fact]
    public void Read_rejects_null_empty_and_non_package_bytes()
    {
        AssertFailure(
            null,
            VbaProjectIdentityReadFailureKind.InvalidPackage);
        AssertFailure(
            [],
            VbaProjectIdentityReadFailureKind.InvalidPackage);
        AssertFailure(
            [0x01, 0x02, 0x03],
            VbaProjectIdentityReadFailureKind.InvalidPackage);
    }

    [Fact]
    public void Read_enforces_its_source_package_length_bound()
    {
        var package = VbaProjectIdentityWorkbookFixture.Create(
            "ContainingProject",
            1252);
        var reader = new OpenXmlVbaProjectIdentityReader(package.Length - 1);

        var result = reader.Read(package);

        Assert.Null(result.Identity);
        Assert.Equal(
            VbaProjectIdentityReadFailureKind.InvalidPackage,
            Assert.IsType<VbaProjectIdentityReadFailure>(result.Failure).Kind);
    }

    [Fact]
    public void Read_enforces_the_content_types_length_bound()
    {
        var package = VbaProjectIdentityWorkbookFixture.Create(
            new VbaProjectIdentityWorkbookFixtureOptions
            {
                ContentTypesXml = new string(' ', (1024 * 1024) + 1)
            });

        AssertFailure(
            package,
            VbaProjectIdentityReadFailureKind.InvalidPackageTopology);
    }

    [Fact]
    public void Read_propagates_cancellation()
    {
        var package = VbaProjectIdentityWorkbookFixture.Create(
            "ContainingProject",
            1252);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => new OpenXmlVbaProjectIdentityReader().Read(
                package,
                cancellation.Token));
    }

    private static void AssertProjectNameFailure(byte[] projectNameBytes)
    {
        var package = VbaProjectIdentityWorkbookFixture.Create(
            new VbaProjectIdentityWorkbookFixtureOptions
            {
                ProjectNameBytes = projectNameBytes
            });

        AssertFailure(
            package,
            VbaProjectIdentityReadFailureKind.InvalidProjectName);
    }

    private static VbaProjectIdentityRead AssertSuccess(byte[] package)
    {
        var result = new OpenXmlVbaProjectIdentityReader().Read(package);

        Assert.Null(result.Failure);
        return Assert.IsType<VbaProjectIdentityRead>(result.Identity);
    }

    private static void AssertFailure(
        byte[]? package,
        VbaProjectIdentityReadFailureKind expectedKind)
    {
        var result = new OpenXmlVbaProjectIdentityReader().Read(package!);

        Assert.Null(result.Identity);
        Assert.Equal(
            expectedKind,
            Assert.IsType<VbaProjectIdentityReadFailure>(result.Failure).Kind);
    }

    private static byte[] CreateDefaultProjectInformation()
        => VbaProjectIdentityWorkbookFixture.CreateProjectInformation(
            1252,
            Encoding.ASCII.GetBytes("ContainingProject"));

    private static byte[] CreatePackageWithProjectInformation(
        byte[] projectInformation)
        => VbaProjectIdentityWorkbookFixture.Create(
            new VbaProjectIdentityWorkbookFixtureOptions
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
