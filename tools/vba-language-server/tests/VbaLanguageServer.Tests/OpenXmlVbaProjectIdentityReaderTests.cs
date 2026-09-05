using System.Text;
using VbaLanguageServer.Workspace;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class OpenXmlVbaProjectIdentityReaderTests
{
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

    [Theory]
    [InlineData(nameof(VbaProjectIdentityReadFailureKind.InvalidPackage))]
    [InlineData(nameof(VbaProjectIdentityReadFailureKind.InvalidPackageTopology))]
    [InlineData(nameof(VbaProjectIdentityReadFailureKind.InvalidVbaProjectPart))]
    [InlineData(nameof(VbaProjectIdentityReadFailureKind.InvalidCompoundFile))]
    [InlineData(nameof(VbaProjectIdentityReadFailureKind.InvalidCompressedDirectory))]
    [InlineData(nameof(VbaProjectIdentityReadFailureKind.InvalidProjectInformation))]
    [InlineData(nameof(VbaProjectIdentityReadFailureKind.UnsupportedCodePage))]
    [InlineData(nameof(VbaProjectIdentityReadFailureKind.InvalidProjectName))]
    public void Read_projects_neutral_failures_into_source_template_failures(
        string failureKind)
    {
        var expectedKind = Enum.Parse<VbaProjectIdentityReadFailureKind>(failureKind);
        var package = CreateFailurePackage(expectedKind);

        var result = new OpenXmlVbaProjectIdentityReader().Read(package);

        Assert.Null(result.Identity);
        var failure = Assert.IsType<VbaProjectIdentityReadFailure>(result.Failure);
        Assert.Equal(expectedKind, failure.Kind);
        Assert.StartsWith(
            "The captured source-template package is unavailable:",
            failure.Message,
            StringComparison.Ordinal);
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

    private static VbaProjectIdentityRead AssertSuccess(byte[] package)
    {
        var result = new OpenXmlVbaProjectIdentityReader().Read(package);

        Assert.Null(result.Failure);
        return Assert.IsType<VbaProjectIdentityRead>(result.Identity);
    }

    private static byte[] CreateFailurePackage(
        VbaProjectIdentityReadFailureKind kind)
    {
        if (kind == VbaProjectIdentityReadFailureKind.InvalidPackage)
        {
            return [0x01, 0x02, 0x03];
        }

        var options = kind switch
        {
            VbaProjectIdentityReadFailureKind.InvalidPackageTopology =>
                new VbaProjectIdentityWorkbookFixtureOptions
                {
                    IncludeContentTypesPart = false
                },
            VbaProjectIdentityReadFailureKind.InvalidVbaProjectPart =>
                new VbaProjectIdentityWorkbookFixtureOptions
                {
                    VbaProjectPartName = "custom/vbaProject.bin"
                },
            VbaProjectIdentityReadFailureKind.InvalidCompoundFile =>
                new VbaProjectIdentityWorkbookFixtureOptions
                {
                    VbaProjectPartBytes = [0x01, 0x02, 0x03]
                },
            VbaProjectIdentityReadFailureKind.InvalidCompressedDirectory =>
                new VbaProjectIdentityWorkbookFixtureOptions
                {
                    CompressedDirectoryBytes = new byte[4096]
                },
            VbaProjectIdentityReadFailureKind.InvalidProjectInformation =>
                new VbaProjectIdentityWorkbookFixtureOptions
                {
                    ProjectInformationBytes = VbaProjectIdentityWorkbookFixture
                        .CreateProjectInformation(
                            1252,
                            Encoding.ASCII.GetBytes("ContainingProject"))[..38]
                },
            VbaProjectIdentityReadFailureKind.UnsupportedCodePage =>
                new VbaProjectIdentityWorkbookFixtureOptions
                {
                    CodePage = 65535,
                    ProjectNameBytes = Encoding.ASCII.GetBytes("Ledger")
                },
            VbaProjectIdentityReadFailureKind.InvalidProjectName =>
                new VbaProjectIdentityWorkbookFixtureOptions
                {
                    CodePage = 932,
                    ProjectNameBytes = [0x82]
                },
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
        return VbaProjectIdentityWorkbookFixture.Create(options);
    }
}
