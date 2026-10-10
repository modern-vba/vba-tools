using VbaDev.Domain;
using System.Text.Json.Nodes;
using System.Text.Json;
using VbaDev.Infrastructure.Projects;
using Xunit;

namespace VbaDev.Tests;

public sealed class WorkbookBinRetirementTests
{
    [Fact]
    public void CapabilitiesIdentifyOptionalWorkbookBinConfigurationIndependentlyOfToolVersion()
    {
        using var temp = TempDirectory.Create();
        var result = CommandLineTestFactory.Create(temp.Path).Run(["capabilities"]);
        Assert.Equal(0, result.ExitCode);
        using var payload = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal("1.0", payload.RootElement.GetProperty("featureVersions")
            .GetProperty("projectManifest.optionalBinPath").GetString());
    }

    [Fact]
    public void ExplicitNullLegacyBinPathIsRejectedRatherThanTreatedAsOmission()
    {
        var node = JsonNode.Parse(ProjectManifestTestData.ValidJson("Project"))!;
        node["documents"]!["Book1"]!["binPath"] = null;

        var error = Assert.Throws<VbaProjectManifestException>(() =>
            ProjectManifestReader.Parse(node.ToJsonString(), "vba-project.json"));

        Assert.Contains("binPath", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectCommandsAcceptAnAbsentLegacyBinPath()
    {
        using var temp = TempDirectory.Create();
        new JsonProjectManifestStore().Save(temp.Path,
            ProjectManifest.CreateDefault("Project", "Book1", temp.Path, null));
        var application = CommandLineTestFactory.Create(temp.Path);

        var result = application.Run(["reference", "list"]);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("deprecated", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("legacy/custom.xlsm")]
    public void ManifestEditingPreservesAbsentOrConfiguredLegacyBinAndCustomSourcePaths(string? legacyBin)
    {
        using var temp = TempDirectory.Create();
        var store = new JsonProjectManifestStore();
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", temp.Path, null,
            references: [new VbaProjectReference("Microsoft Scripting Runtime")]);
        manifest.Documents["Book1"] = manifest.Documents["Book1"] with
        {
            BinPath = legacyBin,
            SourcePath = "custom/authoring",
            TemplatePath = "custom/authoring/OriginalName.xlsm"
        };
        store.Save(temp.Path, manifest);
        var source = Path.Combine(temp.CreateDirectory("custom/authoring"), "OriginalName.xlsm");
        File.WriteAllText(source, "saved-user-source");
        var oldBin = Path.Combine(temp.CreateDirectory("legacy"), "custom.xlsm");
        File.WriteAllText(oldBin, "saved-user-bin");

        var result = CommandLineTestFactory.Create(temp.Path).Run(
            ["reference", "remove", "Microsoft Scripting Runtime"]);

        Assert.Equal(0, result.ExitCode);
        var manifestPath = Path.Combine(temp.Path, ProjectManifest.ManifestFileName);
        var updated = store.Load(manifestPath);
        Assert.Empty(updated.Documents["Book1"].References);
        Assert.Equal(legacyBin, updated.Documents["Book1"].BinPath);
        Assert.Equal("custom/authoring/OriginalName.xlsm", updated.Documents["Book1"].TemplatePath);
        using var json = JsonDocument.Parse(File.ReadAllText(manifestPath));
        Assert.Equal(legacyBin is not null, json.RootElement.GetProperty("documents")
            .GetProperty("Book1").TryGetProperty("binPath", out _));
        var canonicalBytes = File.ReadAllBytes(manifestPath);
        store.Save(temp.Path, updated);
        Assert.Equal(canonicalBytes, File.ReadAllBytes(manifestPath));
        Assert.Equal("saved-user-source", File.ReadAllText(source));
        Assert.Equal("saved-user-bin", File.ReadAllText(oldBin));
    }

    [Fact]
    public void LegacyBinConfigurationWarnsWithoutChangingTheManifestOrExistingWorkbook()
    {
        using var temp = TempDirectory.Create();
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", temp.Path, null);
        manifest.Documents["Book1"] = manifest.Documents["Book1"] with { BinPath = "legacy/custom.xlsm" };
        new JsonProjectManifestStore().Save(temp.Path, manifest);
        var manifestPath = Path.Combine(temp.Path, ProjectManifest.ManifestFileName);
        var before = File.ReadAllBytes(manifestPath);
        var bin = Path.Combine(temp.CreateDirectory("legacy"), "custom.xlsm");
        File.WriteAllText(bin, "existing-user-workbook");

        var result = CommandLineTestFactory.Create(temp.Path).Run(["reference", "list"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("deprecated and scheduled for removal", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("Remove binPath from vba-project.json", result.StandardError, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(manifestPath));
        Assert.Equal("existing-user-workbook", File.ReadAllText(bin));
    }
}
