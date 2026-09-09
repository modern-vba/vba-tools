using System.Text;
using System.Text.Json;
using VbaDev.Cli;
using VbaDev.Composition;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;
using VbaDev.App.References;
using VbaDev.App.HostEvents;
using VbaDev.App.Workbooks;
using VbaDev.App.Cli;
using VbaTools.Semantics;
using VbaTools.TestFixtures;
using VbaTools.TypeLibRegistry;
using VbaDev.App.Build;
using Xunit;

namespace VbaDev.Tests;

public sealed class ProjectSemanticInputAcquisitionTests
{
    [Theory]
    [InlineData("missing-standard")]
    [InlineData("missing-guid")]
    [InlineData("persisted-name-mismatch")]
    [InlineData("library-guid")]
    [InlineData("library-version")]
    [InlineData("library-namespace")]
    [InlineData("library-unreadable")]
    public async Task UnavailableObservedIdentityFailsAnalysisBeforeGeneration(string scenario)
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("DisplayProject", "Book1", root, null));
        var sourceDirectory = temp.CreateDirectory("Project/src/Book1");
        var templatePath = Path.Combine(sourceDirectory, "Book1.xlsm");
        File.WriteAllBytes(templatePath, PackageMetadataFixture.Create("ContainingProject", 65001));
        var sourcePath = Path.Combine(sourceDirectory, "Caller.bas");
        File.WriteAllText(sourcePath, "Attribute VB_Name = \"Caller\"\nPublic Sub Run()\nEnd Sub\n", new UTF8Encoding(false));
        var outputPath = Path.Combine(temp.CreateDirectory("Project/bin"), "Book1.xlsm");
        File.WriteAllText(outputPath, "previous output", new UTF8Encoding(false));
        var originals = new[] { templatePath, sourcePath, outputPath }.ToDictionary(path => path, File.ReadAllBytes);
        var observed = new WorkbookProjectIdentity(scenario == "persisted-name-mismatch" ? "DifferentProject" : "ContainingProject",
            scenario == "missing-standard" ? [] : [new(VbaProjectReferenceCatalogSet.StandardLibraryReferenceName,
                false, "VBA", scenario == "missing-guid" ? null : "000204ef-0000-0000-c000-000000000046", 4, 2,
                scenario.StartsWith("library-", StringComparison.Ordinal) ? "C:/runtime/VBE.dll" : null)]);
        var probe = new IdentityProbe((_, _) => Task.FromResult(observed));
        var generation = new FakeWorkbookGenerationAutomation();
        var metadata = new MetadataReader(new("VBA", [], "VBA"))
        {
            PathError = scenario == "library-unreadable" ? new IOException("Observed library access failed.") : null,
            Observed = new(new(VbaProjectReferenceCatalogSet.StandardLibraryReferenceName,
                scenario == "library-guid" ? "11111111-0000-0000-c000-000000000046" : "000204ef-0000-0000-c000-000000000046",
                scenario == "library-version" ? 5 : 4, 2, 1041, "C:/runtime/VBE.dll"),
                new("VBA", [], scenario == "library-namespace" ? "OtherLibrary" : "VBA"))
        };
        var commandLine = VbaDevCommandLine.Create(ToolingCompositionRoot.CreateApplicationComposition(root,
            workbookProjectIdentityProbe: probe, workbookGenerationAutomation: generation,
            typeLibRegistryCatalogReader: new RegistryReader(new(true, [StandardRegistration()], [], null)),
            typeLibCatalogMetadataReader: metadata));

        var result = await commandLine.RunAsync(["build"]);

        Assert.Equal(1, result.ExitCode);
        var report = Assert.Single(result.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.TrimStart().StartsWith('{')).Select(line => JsonSerializer.Deserialize<JsonElement>(line)));
        Assert.False(report.GetProperty("complete").GetBoolean());
        Assert.Empty(report.GetProperty("diagnostics").EnumerateArray());
        var failureMessage = Assert.Single(report.GetProperty("failures").EnumerateArray()).GetProperty("message").GetString();
        Assert.Contains(scenario.StartsWith("library-", StringComparison.Ordinal)
            ? VbaProjectReferenceCatalogSet.StandardLibraryReferenceName : "captured", failureMessage, StringComparison.OrdinalIgnoreCase);
        if (scenario == "library-unreadable")
        {
            Assert.Contains("C:/runtime/VBE.dll", failureMessage, StringComparison.Ordinal);
            Assert.Contains("Observed library access failed", failureMessage, StringComparison.Ordinal);
        }
        Assert.Equal(1, probe.Reads);
        Assert.Equal(scenario is "library-guid" or "library-version" or "library-namespace" ? 1 : 0, metadata.Identities.Count);
        Assert.Empty(generation.OpenedWorkbooks);
        foreach (var (path, bytes) in originals) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InitialWorkbookWithoutPersistedVbaUsesTheProbedProjectIdentity(bool actualVersionRegistered)
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("DisplayProject", "Book1", root, null));
        var sourceDirectory = temp.CreateDirectory("Project/src/Book1");
        var templatePath = Path.Combine(sourceDirectory, "Book1.xlsm");
        var template = PackageMetadataFixture.CreateWithoutVbaProject();
        File.WriteAllBytes(templatePath, template);
        File.WriteAllText(Path.Combine(sourceDirectory, "Caller.bas"),
            "Attribute VB_Name = \"Caller\"\nPublic Sub Run()\nEnd Sub\n", new UTF8Encoding(false));
        const string standard = VbaProjectReferenceCatalogSet.StandardLibraryReferenceName;
        const string guid = "000204ef-0000-0000-c000-000000000046";
        var versions = new List<TypeLibRegistryVersion> { new(6, 0, [new(0, [new("win64", "C:/runtime/VB6.dll")])]) };
        if (actualVersionRegistered) versions.Add(new(4, 2, [new(0, [new("win64", "C:/runtime/VBE.dll")]) ]));
        var registry = new RegistryReader(new TypeLibRegistryCatalog(true,
            [new(standard, [new(guid, versions)])], [], null));
        var generation = new FakeWorkbookGenerationAutomation { ProjectName = "ObservedProject" };
        generation.References.Add(new(standard, false, "VBA", guid, 4, 2));
        var probe = new IdentityProbe((captured, _) =>
        {
            Assert.Equal(Path.GetFullPath(templatePath), captured.SourcePath);
            Assert.Empty(generation.OpenedWorkbooks);
            return Task.FromResult(new WorkbookProjectIdentity("ObservedProject", [new(standard, false, "VBA", guid, 4, 2,
                actualVersionRegistered ? null : "C:/runtime/VBE.dll")]));
        });
        var metadata = new MetadataReader(new("VBA", [], "VBA"))
        {
            Observed = new(new(standard, guid, 4, 2, 1041, "C:/runtime/VBE.dll"), new("VBA", [], "VBA"))
        };
        var commandLine = VbaDevCommandLine.Create(ToolingCompositionRoot.CreateApplicationComposition(root,
            workbookGenerationAutomation: generation, typeLibRegistryCatalogReader: registry,
            typeLibCatalogMetadataReader: metadata,
            workbookProjectIdentityProbe: probe));

        var result = await commandLine.RunAsync(["build"]);

        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.Equal(1, probe.Reads);
        Assert.Equal(actualVersionRegistered ? 0 : 1041, Assert.Single(metadata.Identities).Lcid);
        Assert.Equal(1, generation.SaveCalls);
        Assert.Equal(template, File.ReadAllBytes(templatePath));
        Assert.Equal(template, File.ReadAllBytes(Path.Combine(root, "bin", "Book1.xlsm")));
    }

    [Fact]
    public async Task SourceClassEventsDoNotRequireAnUnrelatedIntrinsicHostCatalog()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("DisplayProject", "Book1", root, null));
        var sourceDirectory = temp.CreateDirectory("Project/src/Book1");
        var templatePath = Path.Combine(sourceDirectory, "Book1.xlsm");
        var template = PackageMetadataFixture.Create("ContainingProject", 65001);
        File.WriteAllBytes(templatePath, template);
        File.WriteAllText(Path.Combine(sourceDirectory, "Publisher.cls"),
            "VERSION 1.0 CLASS\nBEGIN\nEND\nAttribute VB_Name = \"Publisher\"\nPublic Event Changed()\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(sourceDirectory, "Observer.cls"),
            "VERSION 1.0 CLASS\nBEGIN\nEND\nAttribute VB_Name = \"Observer\"\nPrivate WithEvents source As Publisher\nPrivate Sub source_Changed()\nEnd Sub\n", new UTF8Encoding(false));
        const string standard = VbaProjectReferenceCatalogSet.StandardLibraryReferenceName;
        const string guid = "000204ef-0000-0000-c000-000000000046";
        var registry = new RegistryReader(new TypeLibRegistryCatalog(true,
            [new(standard, [new(guid, [new(4, 2, [new(0, [new("win64", "C:/runtime/VBE.dll")])])])])], [], null));
        var host = new HostReader(_ => throw new IOException("An unrelated host catalog is unavailable."));
        var generation = new FakeWorkbookGenerationAutomation { ProjectName = "ContainingProject" };
        generation.References.Add(new(standard, false, "VBA", guid, 4, 2));
        var commandLine = VbaDevCommandLine.Create(ToolingCompositionRoot.CreateApplicationComposition(root,
            workbookProjectIdentityProbe: IdentityProbe.Accepted, workbookGenerationAutomation: generation, typeLibRegistryCatalogReader: registry,
            typeLibCatalogMetadataReader: new MetadataReader(new("VBA", [], "VBA")), hostEventCatalogAutomation: host));

        var result = await commandLine.RunAsync(["build"]);

        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.Equal(0, host.Reads);
        Assert.Equal(2, generation.ImportedSources.Count);
        Assert.Equal(1, generation.SaveCalls);
        Assert.Equal(template, File.ReadAllBytes(Path.Combine(root, "bin", "Book1.xlsm")));
        Assert.Equal(template, File.ReadAllBytes(templatePath));
    }

    [Theory]
    [InlineData("process", false)]
    [InlineData("process", true)]
    [InlineData("dispatcher", true)]
    [InlineData("released-cleanup", true)]
    public async Task DefaultBuildPreservesProbeLifecycleFailureWhenCancellationWasAlsoRequested(string kind, bool processTrusted)
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("DisplayProject", "Book1", root, null, references: [new("Requested Runtime", true)]));
        var sourceDirectory = temp.CreateDirectory("Project/src/Book1");
        var templatePath = Path.Combine(sourceDirectory, "Book1.xlsm");
        File.WriteAllBytes(templatePath, PackageMetadataFixture.Create("ContainingProject", 65001));
        File.WriteAllText(Path.Combine(sourceDirectory, "Dialog.frm"),
            "VERSION 5.00\nBegin VB.Form Dialog\nEnd\nAttribute VB_Name = \"Dialog\"\n", new UTF8Encoding(false));
        var outputPath = Path.Combine(temp.CreateDirectory("Project/bin"), "Book1.xlsm");
        File.WriteAllText(outputPath, "previous output", new UTF8Encoding(false));
        var originalFiles = Directory.GetFiles(sourceDirectory).Append(outputPath).ToDictionary(path => path, File.ReadAllBytes);
        const string standard = "Requested Runtime";
        var registry = new RegistryReader(new TypeLibRegistryCatalog(true,
            [StandardRegistration(), new(standard, [
                new("000204ef-0000-0000-c000-000000000046", [new(4, 2, [new(0, [new("win64", "C:/runtime/VBE.dll")])])]),
                new("11111111-0000-0000-c000-000000000046", [new(4, 2, [new(0, [new("win64", "C:/other/VBE.dll")])])])])], [], null));
        var stage = new WorkbookAutomationStage(WorkbookAutomationStageKind.ReferenceAttempt);
        Exception cleanup = kind == "released-cleanup"
            ? new WorkbookAutomationReleasedProcessCleanupException("Probe cleanup failed after exact process release.")
            : new WorkbookAutomationCleanupException("Probe process or dispatcher release could not be proved.");
        ((IWorkbookAutomationLifecycleFailure)cleanup).LifecycleEvidence = new(stage, kind != "process", kind != "dispatcher", true);
        var cause = new VbaProjectReferenceProbeAttemptException("cleanupFailure", "Required reference probe cleanup failed.",
            processTrusted, new AggregateException(new WorkbookAutomationCanceledException(stage, cancellation.Token), cleanup));
        var probe = new VbaProjectReferenceAmbiguityProbe(new FailingProbeAutomation(cause, cancellation.Cancel));
        var host = new HostReader(_ => throw new InvalidOperationException("Host discovery must not start after reference acquisition fails."));
        var metadata = new MetadataReader(new("VBA", [], "VBA"));
        var generation = new FakeWorkbookGenerationAutomation();
        var commandLine = VbaDevCommandLine.Create(ToolingCompositionRoot.CreateApplicationComposition(root,
            workbookProjectIdentityProbe: IdentityProbe.Accepted, workbookGenerationAutomation: generation, typeLibRegistryCatalogReader: registry,
            typeLibCatalogMetadataReader: metadata, vbaProjectReferenceAmbiguityProbe: probe, hostEventCatalogAutomation: host));

        var result = await commandLine.RunAsync(["build"], cancellation.Token);

        Assert.Equal(1, result.ExitCode);
        // The CLI carries exit status/streams; the public process-proof contract is
        // asserted at the Build boundary in AcquisitionFailureReportsCollectedFindingsWithoutLosingLifecycleEvidence.
        var report = Assert.Single(result.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.TrimStart().StartsWith('{')).Select(line => JsonSerializer.Deserialize<JsonElement>(line)));
        Assert.False(report.GetProperty("complete").GetBoolean());
        Assert.Empty(report.GetProperty("diagnostics").EnumerateArray());
        Assert.Contains("Required reference probe cleanup failed", Assert.Single(report.GetProperty("failures").EnumerateArray())
            .GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(generation.OpenedWorkbooks);
        Assert.Empty(metadata.Identities);
        Assert.Equal(0, host.Reads);
        foreach (var (path, bytes) in originalFiles) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task DefaultBuildResolvesAmbiguityAgainstTheAlreadyCapturedTemplate()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("DisplayProject", "Book1", root, null, references: [new("Requested Runtime", true)]));
        var sourceDirectory = temp.CreateDirectory("Project/src/Book1");
        var templatePath = Path.Combine(sourceDirectory, "Book1.xlsm");
        var template = PackageMetadataFixture.Create("ContainingProject", 65001);
        File.WriteAllBytes(templatePath, template);
        File.WriteAllText(Path.Combine(sourceDirectory, "Caller.bas"),
            "Attribute VB_Name = \"Caller\"\nPublic Sub Run()\nEnd Sub\n", new UTF8Encoding(false));
        const string standard = "Requested Runtime";
        const string acceptedGuid = "000204ef-0000-0000-c000-000000000046";
        var registry = new RegistryReader(new TypeLibRegistryCatalog(true,
            [StandardRegistration(), new(standard, [
                new(acceptedGuid, [new(4, 2, [new(0, [new("win64", "C:/runtime/VBE.dll")])])]),
                new("11111111-0000-0000-c000-000000000046", [new(4, 2, [new(0, [new("win64", "C:/other/VBE.dll")])])])])], [], null),
            () => File.Delete(templatePath));
        var probe = new Probe((baseline, batch) =>
        {
            Assert.Equal(Path.GetFullPath(templatePath), baseline.WorkbookPath);
            Assert.NotNull(baseline.CapturedTemplate);
            Assert.False(File.Exists(templatePath));
            Assert.Equal("ContainingProject", baseline.CapturedTemplate.ReadMetadata(CancellationToken.None).Metadata!.ProjectName);
            var entry = Assert.Single(batch.References);
            return batch with { References = [entry with { Matches = [new(standard, acceptedGuid, 4, 2)] }] };
        });
        var automation = new FakeWorkbookGenerationAutomation { ProjectName = "ContainingProject" };
        automation.References.Add(new(VbaProjectReferenceCatalogSet.StandardLibraryReferenceName, false, "VBA", acceptedGuid, 4, 2));
        automation.References.Add(new(standard, true, "VBA", acceptedGuid, 4, 2));
        var commandLine = VbaDevCommandLine.Create(ToolingCompositionRoot.CreateApplicationComposition(root,
            workbookProjectIdentityProbe: IdentityProbe.Accepted, workbookGenerationAutomation: automation, typeLibRegistryCatalogReader: registry,
            typeLibCatalogMetadataReader: new MetadataReader(new("VBA", [], "VBA")), vbaProjectReferenceAmbiguityProbe: probe));

        var result = await commandLine.RunAsync(["build"]);

        Assert.True(result.ExitCode == 0, $"Probe calls: {probe.Reads}; registry reads: {registry.Reads}. {result.StandardError}");
        Assert.Equal(1, probe.Reads);
        Assert.Equal(1, registry.Reads);
        Assert.Equal(template, File.ReadAllBytes(Path.Combine(root, "bin", "Book1.xlsm")));
        Assert.False(File.Exists(templatePath));
    }

    [Fact]
    public async Task BuildDoesNotCommitWhenTheLiveProjectIdentityDiffersFromTheAnalyzedTemplate()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("DisplayProject", "Book1", root, null));
        var sourceDirectory = temp.CreateDirectory("Project/src/Book1");
        var templatePath = Path.Combine(sourceDirectory, "Book1.xlsm");
        var template = PackageMetadataFixture.Create("ContainingProject", 65001);
        File.WriteAllBytes(templatePath, template);
        File.WriteAllText(Path.Combine(sourceDirectory, "Caller.bas"),
            "Attribute VB_Name = \"Caller\"\nPublic Sub Run()\nEnd Sub\n", new UTF8Encoding(false));
        var outputPath = Path.Combine(temp.CreateDirectory("Project/bin"), "Book1.xlsm");
        File.WriteAllText(outputPath, "previous output", new UTF8Encoding(false));
        const string standard = VbaProjectReferenceCatalogSet.StandardLibraryReferenceName;
        var registry = new RegistryReader(new TypeLibRegistryCatalog(true,
            [new(standard, [new("000204ef-0000-0000-c000-000000000046", [new(4, 2, [new(0, [new("win64", "C:/runtime/VBE.dll")])])])])], [], null));
        var automation = new FakeWorkbookGenerationAutomation { ProjectName = "DifferentProject" };
        var commandLine = VbaDevCommandLine.Create(ToolingCompositionRoot.CreateApplicationComposition(root,
            workbookProjectIdentityProbe: IdentityProbe.Accepted, workbookGenerationAutomation: automation, typeLibRegistryCatalogReader: registry,
            typeLibCatalogMetadataReader: new MetadataReader(new("VBA", [], "VBA"))));

        var result = await commandLine.RunAsync(["build"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("ContainingProject", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("DifferentProject", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(automation.ImportedSources);
        Assert.Equal(0, automation.SaveCalls);
        Assert.Equal("previous output", File.ReadAllText(outputPath));
        Assert.Equal(template, File.ReadAllBytes(templatePath));
    }

    [Fact]
    public async Task DefaultBuildUsesAcquiredIntrinsicHostEventsToDiagnoseAFormHandler()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("DisplayProject", "Book1", root, null));
        var sourceDirectory = temp.CreateDirectory("Project/src/Book1");
        var templatePath = Path.Combine(sourceDirectory, "Book1.xlsm");
        File.WriteAllBytes(templatePath, PackageMetadataFixture.Create("ContainingProject", 65001));
        var sourcePath = Path.Combine(sourceDirectory, "Dialog.frm");
        File.WriteAllText(sourcePath,
            "VERSION 5.00\nBegin VB.Form Dialog\nEnd\nAttribute VB_Name = \"Dialog\"\nPrivate Function UserForm_Initialize() As Long\nEnd Function\n",
            new UTF8Encoding(false));
        var outputDirectory = temp.CreateDirectory("Project/bin");
        var outputPath = Path.Combine(outputDirectory, "Book1.xlsm");
        File.WriteAllText(outputPath, "previous completed output", new UTF8Encoding(false));
        var originals = new[] { templatePath, sourcePath, outputPath }.ToDictionary(path => path, File.ReadAllBytes);
        const string standard = VbaProjectReferenceCatalogSet.StandardLibraryReferenceName;
        var registry = new RegistryReader(new TypeLibRegistryCatalog(true,
            [new(standard, [new("000204ef-0000-0000-c000-000000000046", [new(4, 2, [new(0, [new("win64", "C:/runtime/VBE.dll")])])])])], [], null));
        var metadata = new MetadataReader(new("VBA", [], "VBA"));
        var host = new HostReader(_ => Task.FromResult(new IntrinsicHostEventCatalog("UserForm",
            [new(new("UserForm", "Initialize"), new([], "Initializes the form."), true, true)])));
        var automation = new FakeWorkbookGenerationAutomation();
        var commandLine = VbaDevCommandLine.Create(ToolingCompositionRoot.CreateApplicationComposition(root,
            workbookProjectIdentityProbe: IdentityProbe.Accepted, workbookGenerationAutomation: automation, typeLibRegistryCatalogReader: registry,
            typeLibCatalogMetadataReader: metadata, hostEventCatalogAutomation: host));

        var result = await commandLine.RunAsync(["build"]);

        Assert.Equal(1, result.ExitCode);
        var report = Assert.Single(result.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.TrimStart().StartsWith('{')).Select(line => JsonSerializer.Deserialize<JsonElement>(line)));
        Assert.True(report.GetProperty("complete").GetBoolean());
        var diagnostic = Assert.Single(report.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("validation.eventHandlerMustBeSub", diagnostic.GetProperty("code").GetString());
        Assert.Equal("Event handlers must be declared as Sub procedures.", diagnostic.GetProperty("message").GetString());
        Assert.Equal(new Uri(sourcePath).AbsoluteUri, diagnostic.GetProperty("uri").GetString());
        Assert.Equal(4, diagnostic.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(8, diagnostic.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(16, diagnostic.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32());
        Assert.Equal(1, host.Reads);
        Assert.Empty(automation.OpenedWorkbooks);
        Assert.Equal(0, automation.SaveCalls);
        Assert.Equal(new[] { outputPath }, Directory.GetFiles(outputDirectory));
        foreach (var (path, bytes) in originals) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("different-guid")]
    [InlineData("different-version")]
    [InlineData("different-namespace")]
    [InlineData("missing")]
    public async Task DefaultBuildRequiresTheAcquiredCatalogIdentityInTheGeneratedWorkbook(string scenario)
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("DisplayProject", "Book1", root, null));
        var sourceDirectory = temp.CreateDirectory("Project/src/Book1");
        var templatePath = Path.Combine(sourceDirectory, "Book1.xlsm");
        var template = PackageMetadataFixture.Create("ContainingProject", 65001);
        File.WriteAllBytes(templatePath, template);
        File.WriteAllText(Path.Combine(sourceDirectory, "Caller.bas"),
            "Attribute VB_Name = \"Caller\"\nPublic Sub Run()\nEnd Sub\n", new UTF8Encoding(false));
        const string standard = VbaProjectReferenceCatalogSet.StandardLibraryReferenceName;
        const string guid = "000204ef-0000-0000-c000-000000000046";
        var registry = new RegistryReader(new TypeLibRegistryCatalog(true,
            [new(standard, [new(guid, [new(4, 2, [new(0, [new("win64", "C:/runtime/VBE.dll")])])])])], [], null));
        var metadata = new MetadataReader(new("VBA", [], "VBA"));
        var automation = new FakeWorkbookGenerationAutomation { ProjectName = "ContainingProject" };
        if (scenario != "missing")
        {
            automation.References.Add(new WorkbookReference(standard, false,
                scenario == "different-namespace" ? "OtherLibrary" : "VBA",
                scenario == "different-guid" ? "11111111-0000-0000-c000-000000000046" : guid,
                scenario == "different-version" ? 3 : 4, 2));
        }
        var outputPath = Path.Combine(temp.CreateDirectory("Project/bin"), "Book1.xlsm");
        File.WriteAllText(outputPath, "previous output", new UTF8Encoding(false));
        var commandLine = VbaDevCommandLine.Create(ToolingCompositionRoot.CreateApplicationComposition(root,
            workbookProjectIdentityProbe: IdentityProbe.Accepted, workbookGenerationAutomation: automation, typeLibRegistryCatalogReader: registry,
            typeLibCatalogMetadataReader: metadata));

        var result = await commandLine.RunAsync(["build"]);

        Assert.Equal(scenario == "accepted" ? 0 : 1, result.ExitCode);
        Assert.Equal(1, registry.Reads);
        Assert.Equal(new VbaProjectReferenceCatalogIdentity(standard, guid, 4, 2, 0, "C:/runtime/VBE.dll"),
            Assert.Single(metadata.Identities));
        Assert.Single(automation.OpenedWorkbooks);
        if (scenario == "accepted")
        {
            Assert.Equal(1, automation.SaveCalls);
            Assert.Equal(template, File.ReadAllBytes(outputPath));
        }
        else
        {
            Assert.Contains(standard, result.StandardError, StringComparison.Ordinal);
            Assert.Equal(0, automation.SaveCalls);
            Assert.Equal("previous output", File.ReadAllText(outputPath));
        }
        Assert.Equal(template, File.ReadAllBytes(templatePath));
    }

    [Fact]
    public async Task DefaultBuildRejectsUnreadableTemplateIdentityWithoutGeneratingWorkbook()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("DisplayProject", "Book1", root, null));
        var sourceDirectory = Path.Combine(root, "src", "Book1");
        Directory.CreateDirectory(sourceDirectory);
        var templatePath = Path.Combine(sourceDirectory, "Book1.xlsm");
        var sourcePath = Path.Combine(sourceDirectory, "Caller.bas");
        File.WriteAllText(templatePath, "not an OPC/VBA package", new UTF8Encoding(false));
        File.WriteAllText(sourcePath, "Attribute VB_Name = \"Caller\"\nPublic Sub Run()\nEnd Sub\n", new UTF8Encoding(false));
        var outputDirectory = temp.CreateDirectory("Project/bin");
        var outputPath = Path.Combine(outputDirectory, "Book1.xlsm");
        File.WriteAllText(outputPath, "previous completed output", new UTF8Encoding(false));
        var originals = new[] { templatePath, sourcePath, outputPath }.ToDictionary(path => path, File.ReadAllBytes);
        var automation = new FakeWorkbookGenerationAutomation();
        var commandLine = VbaDevCommandLine.Create(ToolingCompositionRoot.CreateApplicationComposition(
            root, workbookProjectIdentityProbe: IdentityProbe.Accepted, workbookGenerationAutomation: automation, vbaProjectReferenceResolver: new FakeVbaProjectReferenceResolver()));

        var result = await commandLine.RunAsync(["build"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        var report = Assert.Single(result.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.TrimStart().StartsWith('{')).Select(line => JsonSerializer.Deserialize<JsonElement>(line)));
        Assert.Equal("sourceAnalysis", report.GetProperty("type").GetString());
        Assert.Equal("3.0", report.GetProperty("schemaVersion").GetString());
        Assert.False(report.GetProperty("complete").GetBoolean());
        Assert.Empty(report.GetProperty("diagnostics").EnumerateArray());
        var failure = Assert.Single(report.GetProperty("failures").EnumerateArray());
        Assert.Equal("project", failure.GetProperty("scope").GetString());
        Assert.Contains(templatePath, failure.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains("project", failure.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(automation.OpenedWorkbooks);
        Assert.Equal(0, automation.SaveCalls);
        Assert.Equal(new[] { outputPath }, Directory.GetFiles(outputDirectory));
        foreach (var (path, bytes) in originals) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    private static TypeLibRegistryCatalogName StandardRegistration()
        => new(VbaProjectReferenceCatalogSet.StandardLibraryReferenceName,
            [new("000204ef-0000-0000-c000-000000000046", [new(4, 2, [new(0, [new("win64", "C:/runtime/VBE.dll")])])])]);

    private sealed class RegistryReader(TypeLibRegistryCatalog catalog, Action? onRead = null) : ITypeLibRegistryCatalogReader
    {
        internal int Reads { get; private set; }
        public TypeLibRegistryCatalog Read()
        {
            Reads++;
            onRead?.Invoke();
            return catalog;
        }
    }

    private sealed class IdentityProbe(Func<CapturedWorkbookTemplate, CancellationToken, Task<WorkbookProjectIdentity>> read)
        : IWorkbookProjectIdentityProbe
    {
        internal static IdentityProbe Accepted => new((_, _) => Task.FromResult(new WorkbookProjectIdentity("ContainingProject",
            [new(VbaProjectReferenceCatalogSet.StandardLibraryReferenceName, false, "VBA", "000204ef-0000-0000-c000-000000000046", 4, 2)])));
        internal int Reads { get; private set; }
        public Task<WorkbookProjectIdentity> ReadAsync(CapturedWorkbookTemplate template,
            IReadOnlyList<string> requiredReferenceNames, CancellationToken cancellationToken)
        {
            Assert.Contains(VbaProjectReferenceCatalogSet.StandardLibraryReferenceName, requiredReferenceNames);
            Reads++;
            return read(template, cancellationToken);
        }
    }

    private sealed class Probe(Func<VbaProjectReferenceProbeBaseline, VbaProjectReferenceResolutionBatch, VbaProjectReferenceResolutionBatch> resolve)
        : IVbaProjectReferenceAmbiguityProbe
    {
        internal int Reads { get; private set; }
        public Task<VbaProjectReferenceResolutionBatch> ResolveAsync(VbaProjectReferenceProbeBaseline baseline,
            VbaProjectReferenceResolutionBatch registryResolution, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(resolve(baseline, registryResolution));
        }
    }

    private sealed class FailingProbeAutomation(Exception cause, Action beforeFailure) : IVbaProjectReferenceProbeAutomation
    {
        public Task<TResult> RunAsync<TResult>(VbaProjectReferenceProbeBaseline baseline, WorkbookAutomationTimeouts timeouts,
            Func<IVbaProjectReferenceProbeSession, CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken)
        {
            beforeFailure();
            return Task.FromException<TResult>(cause);
        }
    }

    private sealed class MetadataReader(TypeLibCatalogMetadata metadata) : ITypeLibCatalogMetadataReader
    {
        internal Exception? PathError { get; init; }
        internal AcquiredTypeLibCatalogMetadata? Observed { get; init; }
        internal List<VbaProjectReferenceCatalogIdentity> Identities { get; } = [];
        public AcquiredTypeLibCatalogMetadata ReadMetadataFromPath(string referenceName, string path)
        {
            if (PathError is not null) throw PathError;
            Assert.NotNull(Observed);
            Assert.Equal(referenceName, Observed.Identity.ReferenceName);
            Assert.Equal(path, Observed.Identity.Path);
            Identities.Add(Observed.Identity);
            return Observed;
        }
        public TypeLibCatalogMetadata ReadMetadata(VbaProjectReferenceCatalogIdentity identity)
        {
            Identities.Add(identity);
            return metadata;
        }
    }

    private sealed class HostReader(Func<CancellationToken, Task<IntrinsicHostEventCatalog>> read) : IHostEventCatalogAutomation
    {
        internal int Reads { get; private set; }
        public Task<IntrinsicHostEventCatalog> ReadAsync(CancellationToken cancellationToken)
        {
            Reads++;
            return read(cancellationToken);
        }
    }
}
