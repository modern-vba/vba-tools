using VbaDev.App.Build;
using VbaDev.App.References;
using VbaDev.App.Workbooks;
using VbaTools.TestFixtures;
using Xunit;

namespace VbaDev.Tests;

public sealed class OfficeClickToRunTypeLibPathResolverTests
{
    [Theory]
    [InlineData("incomplete")]
    [InlineData("missing")]
    [InlineData("ambiguous")]
    [InlineData("name")]
    [InlineData("guid")]
    [InlineData("major")]
    [InlineData("minor")]
    [InlineData("alias")]
    [InlineData("installation-platform")]
    [InlineData("registry-view")]
    [InlineData("unrooted-installation")]
    [InlineData("missing-installation")]
    [InlineData("escaping-alias")]
    [InlineData("invalid-windows-character")]
    public void MissingAmbiguousOrMismatchedEvidenceCannotAuthorizeAPhysicalTypeLib(string scenario)
    {
        using var temp = TempDirectory.Create();
        var installationPath = Path.GetFullPath(temp.CreateDirectory("Office"));
        const string referenceName = "Microsoft Windows Common Controls 6.0 (SP6)";
        const string guid = "831fdd16-0c5c-11d2-a9fc-0000f8754da1";
        var observedPath = scenario switch
        {
            "escaping-alias" => "C:/Windows/System32/../MSCOMCTL.OCX",
            "invalid-windows-character" => "C:/Windows/System32/MSCO<CTL.OCX",
            _ => "C:/Windows/System32/MSCOMCTL.OCX"
        };
        var registration = new OfficeClickToRunTypeLibRegistrationEvidence(
            scenario == "name" ? "Different Library" : referenceName,
            scenario == "guid" ? "11111111-0c5c-11d2-a9fc-0000f8754da1" : guid,
            scenario == "major" ? 3 : 2,
            scenario == "minor" ? 1 : 2,
            0,
            scenario == "registry-view" ? "win32" : "win64",
            scenario == "alias" ? "C:/Windows/System32/OTHER.OCX" : observedPath);
        var installation = new OfficeClickToRunInstallationEvidence(
            scenario switch
            {
                "unrooted-installation" => "relative/Office",
                "missing-installation" => Path.Combine(installationPath, "missing"),
                _ => installationPath
            },
            scenario == "installation-platform" ? "x86" : "x64",
            [registration]);
        var snapshot = scenario switch
        {
            "incomplete" => new OfficeClickToRunTypeLibEvidenceSnapshot(false, [], "Injected incomplete evidence."),
            "missing" => OfficeClickToRunTypeLibEvidenceSnapshot.Empty,
            "ambiguous" => new(true, [installation, installation], null),
            _ => new OfficeClickToRunTypeLibEvidenceSnapshot(true, [installation], null)
        };

        var resolution = OfficeClickToRunTypeLibPathResolver.Resolve(
            snapshot, new ResolvedVbaProjectReference(referenceName, guid, 2, 2), observedPath);

        Assert.True(resolution.Applicable);
        Assert.Null(resolution.Candidate);
        Assert.False(string.IsNullOrWhiteSpace(resolution.Diagnostic));
    }

    [Fact]
    public void SameNamedLibraryOutsideAnAllowListedVirtualRootIsNotApplicable()
    {
        var snapshot = OfficeClickToRunTypeLibEvidenceSnapshot.Empty;
        var reference = new ResolvedVbaProjectReference(
            "Microsoft Windows Common Controls 6.0 (SP6)",
            "831fdd16-0c5c-11d2-a9fc-0000f8754da1",
            2,
            2);

        var resolution = OfficeClickToRunTypeLibPathResolver.Resolve(
            snapshot, reference, "C:/Arbitrary/Search/Root/MSCOMCTL.OCX");

        Assert.False(resolution.Applicable);
        Assert.Null(resolution.Candidate);
        Assert.Null(resolution.Diagnostic);
    }

    [Fact]
    public void NegativeRegistrationLcidCannotAuthorizeAPhysicalTypeLib()
    {
        using var temp = TempDirectory.Create();
        var installationPath = Path.GetFullPath(temp.CreateDirectory("Office"));
        const string referenceName = "Microsoft Windows Common Controls 6.0 (SP6)";
        const string guid = "831fdd16-0c5c-11d2-a9fc-0000f8754da1";
        const string observedPath = "C:/Windows/System32/MSCOMCTL.OCX";
        var snapshot = new OfficeClickToRunTypeLibEvidenceSnapshot(true,
            [new(installationPath, "x64", [new(referenceName, guid, 2, 2, -1, "win64", observedPath)])], null);

        var resolution = OfficeClickToRunTypeLibPathResolver.Resolve(
            snapshot, new ResolvedVbaProjectReference(referenceName, guid, 2, 2), observedPath);

        Assert.True(resolution.Applicable);
        Assert.Null(resolution.Candidate);
        Assert.Contains("LCID", resolution.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }
}
