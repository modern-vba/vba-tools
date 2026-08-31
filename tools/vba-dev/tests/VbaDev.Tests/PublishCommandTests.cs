using System.Text;
using System.Runtime.InteropServices;
using VbaDev.App.Build;
using VbaDev.App.Projects;
using VbaDev.App.Testing;
using VbaDev.App.Workbooks;
using VbaDev.Cli;
using VbaDev.Composition;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;
using Xunit;

namespace VbaDev.Tests;

public sealed class PublishCommandTests
{
    [Fact]
    public async Task PublishUsesSelectedDocumentPublishPathAndTemporaryGeneration()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifestTestData.TwoDocumentManifest(root));
        CreateWorkbookSource(root, "SecondBook", ("Local.bas", "Attribute VB_Name = \"Local\""));
        var automation = new FakeWorkbookBuildAutomation(new WorkbookModule("OldModule", WorkbookModuleKind.StandardModule));
        var runner = new FakeWorkbookTestRunner(new WorkbookTestResultRow("Test_Module", "Test_Fails", "NG", "should not run"));
        var application = VbaDevCommandLine.Create(
            ToolingCompositionRoot.CreateApplicationComposition(
                root,
                workbookBuildAutomation: automation,
                workbookTestRunner: runner));

        var result = await application.RunAsync(
            ["publish", "--project", root, "--document", "SecondBook"]);

        Assert.Equal(0, result.ExitCode);
        var expectedPublish = Path.Combine(root, "publish", "SecondBook.xlsm");
        Assert.True(File.Exists(expectedPublish));
        Assert.Equal("template:SecondBook", File.ReadAllText(expectedPublish, Encoding.UTF8));
        Assert.Single(automation.OpenedWorkbooks);
        Assert.NotEqual(expectedPublish, automation.OpenedWorkbooks[0]);
        Assert.Contains(Path.Combine(root, "publish"), automation.OpenedWorkbooks[0], StringComparison.Ordinal);
        Assert.Equal(["remove:OldModule", "import:Local.bas", "save"], automation.Events);
        Assert.Empty(runner.Workbooks);
    }

    [Fact]
    public void PublishKeepsSuccessOutputExactAndEmitsRecasingWarningsOnStandardError()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(
            root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(
            root,
            "Book1",
            ("Local.bas", "Attribute VB_Name = \"Local\""));
        var automation = new FakeWorkbookBuildAutomation();
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: automation);

        var exactResult = application.Run(["publish"]);
        automation.VerificationReport = new VbeImportVerificationReport(
        [
            new VbeIdentifierRecasingWarning(
                "Local",
                [new VbeIdentifierRecasingPair("FileName", "Filename")])
        ]);
        var warnedResult = application.Run(["publish"]);

        Assert.Equal(0, exactResult.ExitCode);
        Assert.Equal(0, warnedResult.ExitCode);
        Assert.Equal(exactResult.StandardOutput, warnedResult.StandardOutput);
        Assert.Empty(exactResult.StandardError);
        Assert.Equal(
            "[WARN] vbeIdentifierRecased: Imported component 'Local' identifier casing (source -> VBE): 'FileName' -> 'Filename'."
            + Environment.NewLine,
            warnedResult.StandardError);
    }

    [Fact]
    public void PublishExcludesRecordedTestOnlyCommonModulesWithoutRepositoryLookup()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var missingCommonModulesRepository = Path.Combine(temp.Path, "missing_common_modules_repo");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, missingCommonModulesRepository);
        manifest.Documents["Book1"].CommonModules.AddRange(
        [
            new InstalledCommonModule("Runtime", "Runtime.bas", Requested: true, TestOnly: false, Orphaned: true),
            new InstalledCommonModule("Lib_UnitTest", "Lib_UnitTest.bas", Requested: true, TestOnly: true, Orphaned: true),
            new InstalledCommonModule("WorkbookServiceTestDouble", "WorkbookServiceTestDouble.cls", Requested: false, TestOnly: true, Orphaned: true)
        ]);
        new JsonProjectManifestStore().Save(root, manifest);
        CreateWorkbookSource(
            root,
            "Book1",
            ("WorkbookServiceTestDouble.cls", "VERSION 1.0 CLASS"),
            ("Lib_UnitTest.bas", "Attribute VB_Name = \"Lib_UnitTest\""),
            ("Runtime.bas", "Attribute VB_Name = \"Runtime\""),
            ("Local.bas", "Attribute VB_Name = \"Local\""));
        var automation = new FakeWorkbookBuildAutomation();
        var application = CommandLineTestFactory.Create(root, workbookBuildAutomation: automation);

        var result = application.Run(["publish"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["import:Runtime.bas", "import:Local.bas", "save"], automation.Events);
    }

    [Fact]
    public void BuildIncludesTestOnlyIdentityConflictWhilePublishExcludesIt()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        manifest.Documents["Book1"].CommonModules.Add(
            new InstalledCommonModule(
                "TestOnly",
                "TestOnly.bas",
                Requested: true,
                TestOnly: true));
        new JsonProjectManifestStore().Save(root, manifest);
        CreateWorkbookSource(
            root,
            "Book1",
            ("Runtime.bas", "Attribute VB_Name = \"CollisionName\""),
            ("TestOnly.bas", "Attribute VB_Name = \"collisionname\""));
        var automation = new FakeWorkbookBuildAutomation();
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: automation);

        var build = application.Run(["build"]);
        var publish = application.Run(["publish"]);

        Assert.Equal(1, build.ExitCode);
        Assert.Contains("Source identity 'collisionname'", build.StandardError, StringComparison.Ordinal);
        Assert.Contains("Runtime.bas", build.StandardError, StringComparison.Ordinal);
        Assert.Contains("TestOnly.bas", build.StandardError, StringComparison.Ordinal);
        Assert.Equal(0, publish.ExitCode);
        Assert.Equal(["import:Runtime.bas", "save"], automation.Events);
        Assert.Single(automation.OpenedWorkbooks);
    }

    [Fact]
    public void PublishRejectsIncludedSourceIdentityConflictBeforeExcelOrOutputReplacement()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(
            root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(
            root,
            "Book1",
            ("First.bas", "Attribute VB_Name = \"CollisionName\""),
            ("Second.bas", "Attribute VB_Name = \"collisionname\""));
        var publishPath = Path.Combine(root, "publish", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(publishPath)!);
        File.WriteAllText(publishPath, "previous-publish", Encoding.UTF8);
        var automation = new FakeWorkbookBuildAutomation();
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: automation);

        var result = application.Run(["publish"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Source identity 'CollisionName'", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("First.bas", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("Second.bas", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(automation.OpenedWorkbooks);
        Assert.Empty(automation.Events);
        Assert.Equal("previous-publish", File.ReadAllText(publishPath, Encoding.UTF8));
    }

    [Fact]
    public void PublishExcludesProjectLocalMarkerNearTopWithoutFilenameOnlyTestPattern()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(
            root,
            "Book1",
            (Path.Combine("nested", "Hidden.bas"), "Attribute VB_Name = \"Hidden\"\n'#ExcludePublish\nPublic Sub Hidden()\nEnd Sub\n"),
            (Path.Combine("tests", "Test_Local.bas"), "Attribute VB_Name = \"Test_Local\"\nPublic Sub Test_StillPublishable()\nEnd Sub\n"),
            (Path.Combine("runtime", "Keep.bas"), "Attribute VB_Name = \"Keep\""));
        File.WriteAllBytes(Path.Combine(root, "src", "Book1", "nested", "Orphan.frx"), [1, 2, 3]);
        var automation = new FakeWorkbookBuildAutomation();
        var application = CommandLineTestFactory.Create(root, workbookBuildAutomation: automation);

        var result = application.Run(["publish"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["import:Keep.bas", "import:Test_Local.bas", "save"], automation.Events);
    }

    [Fact]
    public void PublishRejectsDuplicateFlatFileNamesBeforeApplyingExclusionMarkers()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(
            root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(
            root,
            "Book1",
            (Path.Combine("runtime", "Feature.bas"),
                "Attribute VB_Name = \"RuntimeFeature\"\r\n"),
            (Path.Combine("hidden", "feature.bas"),
                "Attribute VB_Name = \"HiddenFeature\"\r\n'#ExcludePublish\r\n"));
        var publishPath = Path.Combine(root, "publish", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(publishPath)!);
        File.WriteAllText(publishPath, "previous-publish", Encoding.UTF8);
        var automation = new FakeWorkbookBuildAutomation();
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: automation);

        var result = application.Run(["publish"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Duplicate VBA source file names", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("Feature.bas", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(automation.OpenedWorkbooks);
        Assert.Empty(automation.Events);
        Assert.Equal("previous-publish", File.ReadAllText(publishPath, Encoding.UTF8));
    }

    [Fact]
    public void PublishUsesVbaWhitespaceWhenRecognizingTheExclusionMarker()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        var sourceDirectory = Path.Combine(root, "src", "Book1");
        Directory.CreateDirectory(sourceDirectory);
        var templatePath = Path.Combine(sourceDirectory, "Book1.xlsm");
        File.WriteAllText(templatePath, "template", Encoding.UTF8);
        var utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        File.WriteAllText(
            Path.Combine(sourceDirectory, "IdeographicSpace.bas"),
            "Attribute VB_Name = \"IdeographicSpace\"\r\n\u3000'#ExcludePublish\r\n",
            utf8Bom);
        File.WriteAllText(
            Path.Combine(sourceDirectory, "NonBreakingSpace.bas"),
            "Attribute VB_Name = \"NonBreakingSpace\"\r\n\u00a0'#ExcludePublish\r\n",
            utf8Bom);
        var context = new ResolvedProjectContext(
            root,
            Path.Combine(root, ProjectManifest.ManifestFileName),
            manifest,
            "Book1",
            manifest.Documents["Book1"],
            sourceDirectory,
            templatePath,
            Path.Combine(root, "bin", "Book1.xlsm"),
            Path.Combine(root, "publish", "Book1.xlsm"),
            null);
        var planner = new WorkbookSourcePlanner(() => 1252);

        var selected = planner.ResolvePublishSourceFiles(context);

        Assert.Equal(["NonBreakingSpace.bas"], selected.Select(source => source.FileName));
    }

    [Fact]
    public void PublishFailsWhenMarkerSelectionCannotStrictlyDecodeTheCandidate()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(
            root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceDirectory = Path.Combine(root, "src", "Book1");
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(
            Path.Combine(sourceDirectory, "Book1.xlsm"),
            "template:Book1",
            Encoding.UTF8);
        var sourcePath = Path.Combine(sourceDirectory, "Broken.bas");
        File.WriteAllBytes(
            sourcePath,
            Encoding.ASCII.GetBytes("'#ExcludePublish\r\n").Concat(new byte[] { 0x81 }).ToArray());
        var automation = new FakeWorkbookBuildAutomation();
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: automation);

        var result = application.Run(["publish"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("strictly decoded", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(sourcePath, result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(automation.OpenedWorkbooks);
        Assert.Empty(automation.Events);
    }

    [Fact]
    public void PublishTreatsIncludedFormAndFrxAsOneSourceUnit()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(
            root,
            "Book1",
            ("Dialog.frm", "VERSION 5.00\r\nBegin VB.Form Dialog\r\nEnd\r\nAttribute VB_Name = \"Dialog\""));
        var frxPath = Path.Combine(root, "src", "Book1", "Dialog.frx");
        File.WriteAllBytes(frxPath, [1, 2, 3]);
        var automation = new FakeWorkbookBuildAutomation();
        var application = CommandLineTestFactory.Create(root, workbookBuildAutomation: automation);

        var result = application.Run(["publish"]);

        Assert.Equal(0, result.ExitCode);
        var importedForm = Assert.Single(automation.ImportedSources);
        Assert.Equal(VbaSourceKind.Form, importedForm.Kind);
        Assert.Equal("Dialog.frm", importedForm.FileName);
        Assert.NotEqual(Path.Combine(root, "src", "Book1", "Dialog.frm"), importedForm.SourcePath);
        Assert.NotNull(importedForm.BinaryPath);
        Assert.Equal(Path.GetDirectoryName(importedForm.SourcePath), Path.GetDirectoryName(importedForm.BinaryPath));
        Assert.Equal("Dialog.frx", Path.GetFileName(importedForm.BinaryPath));
        Assert.False(File.Exists(importedForm.SourcePath));
        Assert.False(File.Exists(importedForm.BinaryPath));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(frxPath));
    }

    [Fact]
    public void PublishRemovesReplaceableModulesBeforeNormalizingReferencesAndImportingSource()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        manifest.Documents["Book1"].References.Add(new VbaProjectReference("Microsoft Scripting Runtime"));
        new JsonProjectManifestStore().Save(root, manifest);
        CreateWorkbookSource(root, "Book1", ("Local.bas", "Attribute VB_Name = \"Local\""));
        var automation = new FakeWorkbookBuildAutomation(new WorkbookModule("OldModule", WorkbookModuleKind.StandardModule));
        automation.References.Add(new WorkbookReference("Unlisted Library", IsRemovable: true, NamespaceName: "UnlistedLibrary"));
        automation.AdoptedReferenceNamespaces["Microsoft Scripting Runtime"] = "Scripting";
        var resolver = new FakeVbaProjectReferenceResolver(
            new ResolvedVbaProjectReference("Microsoft Scripting Runtime", "{420B2830-E718-11CF-893D-00A0C9054228}", 1, 0));
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: automation,
            vbaProjectReferenceResolver: resolver);

        var result = application.Run(["publish"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            [
                "remove:OldModule",
                "remove-ref:Unlisted Library",
                "add-ref:Microsoft Scripting Runtime",
                "import:Local.bas",
                "save"
            ],
            automation.Events);
    }

    [Fact]
    public void PublishTreatsExistingDesiredWorkbookReferencesAsSatisfiedBeforeRegistryResolution()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        manifest.Documents["Book1"].References.Add(new VbaProjectReference("OLE Automation"));
        manifest.Documents["Book1"].References.Add(new VbaProjectReference("Microsoft Scripting Runtime"));
        new JsonProjectManifestStore().Save(root, manifest);
        CreateWorkbookSource(root, "Book1", ("Local.bas", "Attribute VB_Name = \"Local\""));
        var automation = new FakeWorkbookBuildAutomation(new WorkbookModule("OldModule", WorkbookModuleKind.StandardModule));
        automation.References.Add(new WorkbookReference("OLE Automation", IsRemovable: false, NamespaceName: "stdole"));
        automation.References.Add(new WorkbookReference("Unlisted Library", IsRemovable: true, NamespaceName: "UnlistedLibrary"));
        automation.AdoptedReferenceNamespaces["Microsoft Scripting Runtime"] = "Scripting";
        var resolver = new FakeVbaProjectReferenceResolver(
            new ResolvedVbaProjectReference("OLE Automation", "{00020430-0000-0000-C000-000000000046}", 1, 0),
            new ResolvedVbaProjectReference("OLE Automation", "{00020430-0000-0000-C000-000000000046}", 2, 0),
            new ResolvedVbaProjectReference("Microsoft Scripting Runtime", "{420B2830-E718-11CF-893D-00A0C9054228}", 1, 0));
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: automation,
            vbaProjectReferenceResolver: resolver);

        var result = application.Run(["publish"]);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("OLE Automation", resolver.RequestedNames);
        Assert.Contains("Microsoft Scripting Runtime", resolver.RequestedNames);
        Assert.Equal(
            [
                "remove:OldModule",
                "remove-ref:Unlisted Library",
                "add-ref:Microsoft Scripting Runtime",
                "import:Local.bas",
                "save"
            ],
            automation.Events);
    }

    [Fact]
    public void PublishLeavesExistingOutputUntouchedWhenGenerationFails()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(root, "Book1", ("Local.bas", "Attribute VB_Name = \"Local\""));
        var publishPath = Path.Combine(root, "publish", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(publishPath)!);
        File.WriteAllText(publishPath, "old-publish", Encoding.UTF8);
        var automation = new FakeWorkbookBuildAutomation
        {
            ThrowOnImport = true
        };
        var application = CommandLineTestFactory.Create(root, workbookBuildAutomation: automation);

        var result = application.Run(["publish"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("import failed", result.StandardError, StringComparison.Ordinal);
        Assert.Equal("old-publish", File.ReadAllText(publishPath, Encoding.UTF8));
        Assert.DoesNotContain(publishPath, automation.OpenedWorkbooks);
    }

    [Fact]
    public void PublishReportsComReferenceErrorsAsUsageErrors()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(root, "Book1", ("Local.bas", "Attribute VB_Name = \"Local\""));
        var automation = new FakeWorkbookBuildAutomation
        {
            ReferenceError = new COMException("0x800A801C", unchecked((int)0x800A801C))
        };
        var application = CommandLineTestFactory.Create(root, workbookBuildAutomation: automation);

        var result = application.Run(["publish"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Excel COM publish automation failed", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("coding agent", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("outside the sandbox", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("import:", automation.Events);
    }

    private static void CreateWorkbookSource(string root, string documentName, params (string FileName, string Content)[] sources)
    {
        var sourceDirectory = Path.Combine(root, "src", documentName);
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(Path.Combine(sourceDirectory, $"{documentName}.xlsm"), $"template:{documentName}", Encoding.UTF8);
        foreach (var source in sources)
        {
            var sourcePath = Path.Combine(sourceDirectory, source.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            File.WriteAllText(sourcePath, source.Content, Encoding.UTF8);
        }
    }
}
