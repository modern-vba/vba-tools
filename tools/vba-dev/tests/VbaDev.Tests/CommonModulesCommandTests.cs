using VbaDev.Infrastructure.FileSystem;
using System.Text;
using System.Text.Json;
using VbaDev.App.CommonModules;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;
using VbaDev.Cli;
using VbaDev.Composition;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;
using Xunit;

namespace VbaDev.Tests;

public sealed class CommonModulesCommandTests
{
    private const string TestModuleBodyMarker = "' vba-tools test body\r\n";

    [Fact]
    public void AddRejectsUtf8ManifestBeforeProjectStateChanges()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        File.WriteAllText(
            Path.Combine(commonRepo, "common-modules-manifest.tsv"),
            "ModuleFile\tCategories\tDependencies\r\nFeature.bas\toptional\t\r\n",
            new UTF8Encoding(false));
        WriteModule(commonRepo, "Feature.bas", "feature");
        var manifestPath = Path.Combine(projectRoot, ProjectManifest.ManifestFileName);
        var manifestBefore = File.ReadAllBytes(manifestPath);
        var application = CommandLineTestFactory.Create(projectRoot);

        var result = application.Run(["common-module", "add", "Feature"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("UTF-16LE BOM", result.StandardError, StringComparison.Ordinal);
        Assert.Equal(manifestBefore, File.ReadAllBytes(manifestPath));
        Assert.False(File.Exists(Path.Combine(
            projectRoot,
            "src",
            "Book1",
            "common-modules",
            "Feature.bas")));
    }

    [Fact]
    public void ManifestReaderReadsCanonicalRequiredReferences()
    {
        using var temp = TempDirectory.Create();
        var repo = temp.CreateDirectory("repo");
        File.WriteAllText(
            Path.Combine(repo, "common-modules-manifest.tsv"),
            "ModuleFile\tCategories\tDependencies\tRequiredReferences\r\n"
                + "Feature.bas\toptional\t\t[\"Microsoft Scripting Runtime\"]\r\n",
            new UnicodeEncoding(false, true, true));

        var entry = Assert.Single(new CommonModulesManifestReader().Load(repo));

        Assert.Equal(["Microsoft Scripting Runtime"], entry.RequiredReferences);
    }

    [Theory]
    [InlineData("[\"\"]")]
    [InlineData("[\" Reference\"]")]
    [InlineData("[\"Reference \u2003\"]")]
    [InlineData("[\"Reference\",\"reference\"]")]
    [InlineData("[\"visual basic for applications\"]")]
    [InlineData("[\"Reference\",]")]
    [InlineData("[/*invalid*/\"Reference\"]")]
    [InlineData("[\"\\uD800\"]")]
    [InlineData("[\"\\uDC00\"]")]
    public void ManifestReaderRejectsInvalidRequiredReferences(string requiredReferences)
    {
        using var temp = TempDirectory.Create();
        var repo = temp.CreateDirectory("repo");
        WriteManifestWithReferences(
            repo,
            ("Feature.bas", "optional", "", requiredReferences));

        Assert.Throws<CommonModulesManifestException>(
            () => new CommonModulesManifestReader().Load(repo));
    }

    [Fact]
    public void ManifestReaderRequiresExactlyOneFinalCrlf()
    {
        using var temp = TempDirectory.Create();
        var repo = temp.CreateDirectory("repo");
        File.WriteAllText(
            Path.Combine(repo, "common-modules-manifest.tsv"),
            "ModuleFile\tCategories\tDependencies\tRequiredReferences\r\n"
                + "Feature.bas\toptional\t\t[]",
            new UnicodeEncoding(false, true, true));

        var exception = Assert.Throws<CommonModulesManifestException>(
            () => new CommonModulesManifestReader().Load(repo));

        Assert.Contains("exactly one final CRLF", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ManifestReaderRejectsManifestWithoutModuleRows()
    {
        using var temp = TempDirectory.Create();
        var repo = temp.CreateDirectory("repo");
        File.WriteAllText(
            Path.Combine(repo, "common-modules-manifest.tsv"),
            "ModuleFile\tCategories\tDependencies\tRequiredReferences\r\n",
            new UnicodeEncoding(false, true, true));

        var exception = Assert.Throws<CommonModulesManifestException>(
            () => new CommonModulesManifestReader().Load(repo));

        Assert.Contains("at least one module row", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    public void ManifestReaderRequiresCrlfLineEndingsThroughout(string invalidLineEnding)
    {
        using var temp = TempDirectory.Create();
        var repo = temp.CreateDirectory("repo");
        File.WriteAllText(
            Path.Combine(repo, "common-modules-manifest.tsv"),
            "ModuleFile\tCategories\tDependencies\tRequiredReferences"
                + invalidLineEnding
                + "Feature.bas\toptional\t\t[]\r\n",
            new UnicodeEncoding(false, true, true));

        var exception = Assert.Throws<CommonModulesManifestException>(
            () => new CommonModulesManifestReader().Load(repo));

        Assert.Contains("CRLF line endings throughout", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData(" # indented\r\n")]
    [InlineData("# trailing \r\n")]
    [InlineData("# valid\r\nModuleFile\tCategories\tDependencies\tRequiredReferences\r\n# misplaced\r\n")]
    public void ManifestReaderAllowsOnlyCanonicalLeadingComments(string prefixOrManifestPrefix)
    {
        using var temp = TempDirectory.Create();
        var repo = temp.CreateDirectory("repo");
        var text = prefixOrManifestPrefix.Contains("RequiredReferences", StringComparison.Ordinal)
            ? prefixOrManifestPrefix + "Feature.bas\toptional\t\t[]\r\n"
            : prefixOrManifestPrefix
                + "ModuleFile\tCategories\tDependencies\tRequiredReferences\r\n"
                + "Feature.bas\toptional\t\t[]\r\n";
        File.WriteAllText(
            Path.Combine(repo, "common-modules-manifest.tsv"),
            text,
            new UnicodeEncoding(false, true, true));

        Assert.Throws<CommonModulesManifestException>(
            () => new CommonModulesManifestReader().Load(repo));
    }

    [Fact]
    public void ManifestReaderPreservesCanonicalLeadingComments()
    {
        using var temp = TempDirectory.Create();
        var repo = temp.CreateDirectory("repo");
        File.WriteAllText(
            Path.Combine(repo, "common-modules-manifest.tsv"),
            "# Unicode comment: 依存関係\r\n"
                + "# Declaration order is significant\r\n"
                + "ModuleFile\tCategories\tDependencies\tRequiredReferences\r\n"
                + "Feature.bas\toptional\t\t[]\r\n",
            new UnicodeEncoding(false, true, true));

        Assert.Single(new CommonModulesManifestReader().Load(repo));
    }

    [Fact]
    public void ManifestReaderRejectsMalformedRecordsAndUnknownDependencies()
    {
        using var temp = TempDirectory.Create();
        var malformedRepo = temp.CreateDirectory("malformed");
        File.WriteAllText(
            Path.Combine(malformedRepo, "common-modules-manifest.tsv"),
            "ModuleFile\tCategories\r\nBroken.bas\truntime-baseline\r\n",
            new UnicodeEncoding(false, true, true));
        var unknownDependencyRepo = temp.CreateDirectory("unknown");
        WriteManifest(
            unknownDependencyRepo,
            ("Feature.bas", "optional", "Missing.bas"));

        var reader = new CommonModulesManifestReader();

        Assert.Contains("header", Assert.Throws<CommonModulesManifestException>(() => reader.Load(malformedRepo)).Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unknown dependency", Assert.Throws<CommonModulesManifestException>(() => reader.Load(unknownDependencyRepo)).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ManifestReaderKeepsClassifications()
    {
        using var temp = TempDirectory.Create();
        var repo = temp.CreateDirectory("repo");
        WriteManifest(repo, ("Runtime.bas", "runtime-baseline,public-udf", ""));

        var entry = Assert.Single(new CommonModulesManifestReader().Load(repo));

        Assert.True(entry.HasCategory("runtime-baseline"));
        Assert.True(entry.HasCategory("public-udf"));
    }

    [Theory]
    [InlineData("Runtime-baseline")]
    [InlineData(" runtime-baseline")]
    [InlineData("runtime-baseline ")]
    [InlineData("public-udf")]
    [InlineData("public-udf,optional")]
    [InlineData("runtime-baseline,optional")]
    public void ManifestReaderRejectsNonCanonicalCategories(string categories)
    {
        using var temp = TempDirectory.Create();
        var repo = temp.CreateDirectory("repo");
        WriteManifest(repo, ("Feature.bas", categories, ""));

        var exception = Assert.Throws<CommonModulesManifestException>(
            () => new CommonModulesManifestReader().Load(repo));

        Assert.Contains("invalid Categories", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Base.bas, Base.bas")]
    [InlineData("Base.bas,,Base.bas")]
    [InlineData("Base.bas,base.bas")]
    [InlineData("base.bas")]
    [InlineData("Feature.bas")]
    public void ManifestReaderRejectsMalformedDependencies(string dependencies)
    {
        using var temp = TempDirectory.Create();
        var repo = temp.CreateDirectory("repo");
        WriteManifest(
            repo,
            ("Base.bas", "runtime-baseline", ""),
            ("Feature.bas", "optional", dependencies));

        Assert.Throws<CommonModulesManifestException>(
            () => new CommonModulesManifestReader().Load(repo));
    }

    [Theory]
    [InlineData("runtime-baseline", "test-foundation")]
    [InlineData("runtime-baseline,public-udf", "test-double")]
    [InlineData("optional", "test-double")]
    [InlineData("optional,public-udf", "test-foundation")]
    public void ManifestReaderRejectsRuntimeToTestDependencies(
        string runtimeCategories,
        string testCategories)
    {
        using var temp = TempDirectory.Create();
        var repo = temp.CreateDirectory("repo");
        WriteManifest(
            repo,
            ("Runtime.bas", runtimeCategories, "TestSupport.cls"),
            ("TestSupport.cls", testCategories, ""));

        var exception = Assert.Throws<CommonModulesManifestException>(
            () => new CommonModulesManifestReader().Load(repo));

        Assert.Contains("runtime-role", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddCopiesRequestedModuleAndTransitiveDependenciesInOrder()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifest(
            commonRepo,
            ("Base.bas", "runtime-baseline", ""),
            ("Feature.bas", "optional", "Base.bas"));
        WriteModule(commonRepo, "Base.bas", "base");
        WriteModule(commonRepo, "Feature.bas", "feature");
        var application = VbaDevCommandLine.Create(
            ToolingCompositionRoot.CreateApplicationComposition(projectRoot));
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await application.InvokeAsync(
            ["common-module", "add", "Feature"],
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Empty(standardError.ToString());
        var sourceSet = Path.Combine(projectRoot, "src", "Book1");
        Assert.Equal("base", ReadModuleBody(Path.Combine(sourceSet, "common-modules", "Base.bas")));
        Assert.Equal("feature", ReadModuleBody(Path.Combine(sourceSet, "common-modules", "Feature.bas")));
        Assert.False(File.Exists(Path.Combine(sourceSet, "Base.bas")));
        Assert.False(File.Exists(Path.Combine(sourceSet, "Feature.bas")));
        var manifest = new JsonProjectManifestStore().Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        Assert.Equal(
            [
                Installed("Base", requested: false),
                Installed("Feature", requested: true)
            ],
            manifest.Documents["Book1"].CommonModules);
        Assert.True(standardOutput.ToString().IndexOf("Copied common-modules/Base.bas", StringComparison.Ordinal) < standardOutput.ToString().IndexOf("Copied common-modules/Feature.bas", StringComparison.Ordinal));
    }

    [Fact]
    public void AddRejectsWhitespaceOnlyCommonModuleIdentity()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifest(commonRepo, ("\u00A0.bas", "optional", ""));
        WriteModule(commonRepo, "\u00A0.bas", "Attribute VB_Name = \"\u00A0\"\r\n");
        var application = CommandLineTestFactory.Create(projectRoot);

        var result = application.Run(["common-module", "add", "\u00A0"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("invalid flat ModuleFile", result.StandardError, StringComparison.Ordinal);
        var manifest = new JsonProjectManifestStore().Load(
            Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        Assert.Empty(manifest.Documents["Book1"].CommonModules);
    }

    [Fact]
    public void AddRejectsNestedModuleFileBeforeProjectStateChanges()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifest(commonRepo, ("runtime/Feature.bas", "test-double", ""));
        WriteModule(commonRepo, Path.Combine("runtime", "Feature.bas"), "feature");
        var manifestPath = Path.Combine(projectRoot, ProjectManifest.ManifestFileName);
        var manifestBefore = File.ReadAllBytes(manifestPath);
        var application = CommandLineTestFactory.Create(projectRoot);

        var result = application.Run(["common-module", "add", "Feature"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("ordinary file", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(manifestBefore, File.ReadAllBytes(manifestPath));
        var sourceSet = Path.Combine(projectRoot, "src", "Book1");
        Assert.False(File.Exists(Path.Combine(sourceSet, "Feature.bas")));
        Assert.False(File.Exists(Path.Combine(sourceSet, "common-modules", "Feature.bas")));
    }

    [Fact]
    public void AddRejectsUnexpectedPackageEntryBeforeProjectStateChanges()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifest(commonRepo, ("Feature.bas", "optional", ""));
        WriteModule(
            commonRepo,
            "Feature.bas",
            "Attribute VB_Name = \"Feature\"\r\nOption Explicit\r\n");
        WriteModule(commonRepo, "README.txt", "unexpected");
        var manifestPath = Path.Combine(projectRoot, ProjectManifest.ManifestFileName);
        var manifestBefore = File.ReadAllBytes(manifestPath);
        var application = CommandLineTestFactory.Create(projectRoot);

        var result = application.Run(["common-module", "add", "Feature"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("unexpected package entry", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(manifestBefore, File.ReadAllBytes(manifestPath));
        Assert.False(File.Exists(Path.Combine(
            projectRoot,
            "src",
            "Book1",
            "common-modules",
            "Feature.bas")));
    }

    [Theory]
    [InlineData(FileAccess.Read, FileShare.None)]
    [InlineData(FileAccess.Write, FileShare.Read)]
    public void AddRejectsUnreadableFormSidecarBeforeProjectStateChanges(
        FileAccess heldAccess,
        FileShare heldShare)
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifest(commonRepo, ("Dialog.frm", "optional", ""));
        WriteModule(commonRepo, "Dialog.frm", "dialog");
        var sidecarPath = Path.Combine(commonRepo, "Dialog.frx");
        WriteBytes(sidecarPath, [1, 2, 3]);
        var manifestPath = Path.Combine(projectRoot, ProjectManifest.ManifestFileName);
        var manifestBefore = File.ReadAllBytes(manifestPath);
        var application = CommandLineTestFactory.Create(projectRoot);
        using var lockedSidecar = new FileStream(
            sidecarPath,
            FileMode.Open,
            heldAccess,
            heldShare);

        var result = application.Run(["common-module", "add", "Dialog"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(manifestBefore, File.ReadAllBytes(manifestPath));
        var sourceSet = Path.Combine(projectRoot, "src", "Book1", "common-modules");
        Assert.False(File.Exists(Path.Combine(sourceSet, "Dialog.frm")));
        Assert.False(File.Exists(Path.Combine(sourceSet, "Dialog.frx")));
    }

    [Fact]
    public void AddRejectsSourceModuleIdentityMismatchBeforeProjectStateChanges()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifest(commonRepo, ("Feature.bas", "optional", ""));
        WriteModule(
            commonRepo,
            "Feature.bas",
            "Attribute VB_Name = \"feature\"\r\nOption Explicit\r\n");
        var manifestPath = Path.Combine(projectRoot, ProjectManifest.ManifestFileName);
        var manifestBefore = File.ReadAllBytes(manifestPath);
        var application = CommandLineTestFactory.Create(projectRoot);

        var result = application.Run(["common-module", "add", "Feature"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("ModuleIdentity", result.StandardError, StringComparison.Ordinal);
        Assert.Equal(manifestBefore, File.ReadAllBytes(manifestPath));
        Assert.False(File.Exists(Path.Combine(
            projectRoot,
            "src",
            "Book1",
            "common-modules",
            "Feature.bas")));
    }

    [Fact]
    public void PackageReaderRejectsClosedInventoryDefects()
    {
        using var temp = TempDirectory.Create();
        var defects = new (string Name, Action<string> Arrange)[]
        {
            ("missing-source", repository =>
                WriteManifest(repository, ("Feature.bas", "optional", ""))),
            ("nested-entry", repository =>
            {
                WriteManifest(repository, ("Feature.bas", "optional", ""));
                WriteModule(repository, "Feature.bas", "feature");
                Directory.CreateDirectory(Path.Combine(repository, "Nested"));
            }),
            ("orphan-sidecar", repository =>
            {
                WriteManifest(repository, ("Feature.bas", "optional", ""));
                WriteModule(repository, "Feature.bas", "feature");
                WriteBytes(Path.Combine(repository, "Feature.frx"), [1]);
            }),
            ("sidecar-case", repository =>
            {
                WriteManifest(repository, ("Dialog.frm", "optional", ""));
                WriteModule(repository, "Dialog.frm", "dialog");
                WriteBytes(Path.Combine(repository, "Dialog.FRX"), [1]);
            }),
            ("duplicate-common-name", repository =>
            {
                WriteManifest(
                    repository,
                    ("Feature.bas", "optional", ""),
                    ("Feature.cls", "optional", ""));
                WriteModule(repository, "Feature.bas", "standard");
                WriteModule(repository, "Feature.cls", "class");
            })
        };
        var reader = new CommonModulesPackageReader(new CommonModulesManifestReader());

        foreach (var defect in defects)
        {
            var repository = temp.CreateDirectory(defect.Name);
            defect.Arrange(repository);

            var error = Record.Exception(() => reader.Load(repository));

            Assert.IsType<CommonModulesManifestException>(error);
        }
    }

    [Fact]
    public void PackageReaderRejectsSourceKindAndIdentityDefects()
    {
        using var temp = TempDirectory.Create();
        var longName = new string('A', 32);
        var defects = new (string Name, string ModuleFile, string Source)[]
        {
            (
                "class-with-form-kind",
                "Feature.cls",
                "VERSION 5.00\r\nAttribute VB_Name = \"Feature\"\r\n"),
            (
                "form-with-class-kind",
                "Feature.frm",
                "VERSION 1.0 CLASS\r\nAttribute VB_Name = \"Feature\"\r\n"),
            (
                "standard-with-class-kind",
                "Feature.bas",
                "VERSION 1.0 CLASS\r\nAttribute VB_Name = \"Feature\"\r\n"),
            (
                "missing-module-identity",
                "Feature.bas",
                "Option Explicit\r\n"),
            (
                "duplicate-class-module-identity",
                "Feature.cls",
                "VERSION 1.0 CLASS\r\nBEGIN\r\nEND\r\n"
                    + "Attribute VB_Name = \"Feature\"\r\n"
                    + "Attribute VB_Name = \"Feature\"\r\n"),
            (
                "duplicate-form-module-identity",
                "Feature.frm",
                "VERSION 5.00\r\n"
                    + "Attribute VB_Name = \"Feature\"\r\n"
                    + "Attribute VB_Name = \"Feature\"\r\n"),
            (
                "invalid-module-identity",
                "Bad-Name.bas",
                "Attribute VB_Name = \"Bad-Name\"\r\n"),
            (
                "long-module-identity",
                $"{longName}.bas",
                $"Attribute VB_Name = \"{longName}\"\r\n")
        };
        var reader = new CommonModulesPackageReader(new CommonModulesManifestReader());

        foreach (var defect in defects)
        {
            var repository = temp.CreateDirectory(defect.Name);
            WriteManifest(repository, (defect.ModuleFile, "optional", ""));
            WriteRawModule(repository, defect.ModuleFile, defect.Source);

            var error = Record.Exception(() => reader.Load(repository));

            Assert.IsType<CommonModulesManifestException>(error);
        }
    }

    [Fact]
    public void PackageReaderAcceptsCanonicalModuleKindsAndOptionalFormSidecar()
    {
        using var temp = TempDirectory.Create();
        var repository = temp.CreateDirectory("repo");
        WriteManifest(
            repository,
            ("Feature.bas", "optional", ""),
            ("Service.cls", "optional", ""),
            ("Dialog.frm", "optional", ""));
        WriteModule(repository, "Feature.bas", "standard");
        WriteModule(repository, "Service.cls", "class");
        WriteModule(repository, "Dialog.frm", "form");
        WriteBytes(Path.Combine(repository, "Dialog.frx"), [1, 2, 3]);

        var package = new CommonModulesPackageReader(
            new CommonModulesManifestReader()).Load(repository);

        Assert.Equal(
            ["Feature.bas", "Service.cls", "Dialog.frm"],
            package.Entries.Select(entry => entry.ModuleFile));
    }

    [Fact]
    public void AddPlacesNewFormSidecarInCommonModulesDirectory()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifest(commonRepo, ("Dialog.frm", "optional", ""));
        WriteModule(commonRepo, "Dialog.frm", "repo form");
        WriteBytes(Path.Combine(commonRepo, "Dialog.frx"), [1, 2, 3]);
        var application = CommandLineTestFactory.Create(projectRoot);

        var result = application.Run(["common-module", "add", "Dialog"]);

        Assert.Equal(0, result.ExitCode);
        var sourceSet = Path.Combine(projectRoot, "src", "Book1");
        Assert.Equal("repo form", ReadModuleBody(Path.Combine(sourceSet, "common-modules", "Dialog.frm")));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(sourceSet, "common-modules", "Dialog.frx")));
        Assert.False(File.Exists(Path.Combine(sourceSet, "Dialog.frm")));
        Assert.False(File.Exists(Path.Combine(sourceSet, "Dialog.frx")));
        Assert.Contains("Copied common-modules/Dialog.frm", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void AddCopiesCyclicDependenciesOnceAndKeepsRequestedIntent()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifest(
            commonRepo,
            ("Root.bas", "optional", "ObjectList.cls"),
            ("ObjectList.cls", "optional", "ObjectSet.cls"),
            ("ObjectSet.cls", "optional", "ObjectList.cls"));
        WriteModule(commonRepo, "Root.bas", "root");
        WriteModule(commonRepo, "ObjectList.cls", "list");
        WriteModule(commonRepo, "ObjectSet.cls", "set");
        var application = CommandLineTestFactory.Create(projectRoot);

        var result = application.Run(["common-module", "add", "Root"]);

        Assert.Equal(0, result.ExitCode);
        var sourceSet = Path.Combine(projectRoot, "src", "Book1");
        Assert.Equal("root", ReadModuleBody(Path.Combine(sourceSet, "common-modules", "Root.bas")));
        Assert.Equal("list", ReadModuleBody(Path.Combine(sourceSet, "common-modules", "ObjectList.cls")));
        Assert.Equal("set", ReadModuleBody(Path.Combine(sourceSet, "common-modules", "ObjectSet.cls")));
        Assert.False(File.Exists(Path.Combine(sourceSet, "Root.bas")));
        Assert.False(File.Exists(Path.Combine(sourceSet, "ObjectList.cls")));
        Assert.False(File.Exists(Path.Combine(sourceSet, "ObjectSet.cls")));
        var manifest = new JsonProjectManifestStore().Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        Assert.Equal(
            [
                Installed("ObjectList", requested: false, moduleFile: "ObjectList.cls"),
                Installed("ObjectSet", requested: false, moduleFile: "ObjectSet.cls"),
                Installed("Root", requested: true)
            ],
            manifest.Documents["Book1"].CommonModules);
        Assert.Equal(1, manifest.Documents["Book1"].CommonModules.Count(module => module.Name.Equals("ObjectList", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(1, manifest.Documents["Book1"].CommonModules.Count(module => module.Name.Equals("ObjectSet", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void SelectionPlanOrdersCyclicClosureAndRequiredReferenceUnion()
    {
        using var temp = TempDirectory.Create();
        var repo = temp.CreateDirectory("repo");
        WriteManifestWithReferences(
            repo,
            ("Root.bas", "optional", "Alpha.cls", "[\"RootRef\",\"Shared\"]"),
            ("Alpha.cls", "optional", "Beta.cls", "[\"AlphaRef\",\"shared\"]"),
            ("Beta.cls", "optional", "Alpha.cls", "[\"BetaRef\"]"));
        var entries = new CommonModulesManifestReader().Load(repo);

        var plan = CommonModulesDependencyResolver.ResolveRequestedPlan(
            entries,
            ["Root"]);

        Assert.Equal(
            ["Alpha.cls", "Beta.cls", "Root.bas"],
            plan.Entries.Select(entry => entry.ModuleFile));
        Assert.Equal(
            ["AlphaRef", "shared", "BetaRef", "RootRef"],
            plan.RequiredReferences);
    }

    [Fact]
    public void SelectionPlanOrdersExternalDependenciesBeforeDeclarationOrderedStronglyConnectedComponent()
    {
        using var temp = TempDirectory.Create();
        var repo = temp.CreateDirectory("repo");
        WriteManifestWithReferences(
            repo,
            ("A.cls", "optional", "B.cls,X.cls", "[\"ARef\",\"shared\"]"),
            ("B.cls", "optional", "A.cls,Y.cls", "[\"BRef\"]"),
            ("X.cls", "optional", "", "[\"XRef\",\"Shared\"]"),
            ("Y.cls", "optional", "", "[\"YRef\"]"));
        var entries = new CommonModulesManifestReader().Load(repo);

        var plan = CommonModulesDependencyResolver.ResolveRequestedPlan(
            entries,
            ["A"]);

        Assert.Equal(
            ["X.cls", "Y.cls", "A.cls", "B.cls"],
            plan.Entries.Select(entry => entry.ModuleFile));
        Assert.Equal(
            ["XRef", "Shared", "YRef", "ARef", "BRef"],
            plan.RequiredReferences);
    }

    [Fact]
    public void AddResolvesEveryMissingRequiredReferenceBeforeSourceMutation()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifestWithReferences(
            commonRepo,
            ("Feature.bas", "optional", "", "[\"Available\",\"Missing\"]"));
        WriteModule(commonRepo, "Feature.bas", "feature");
        var resolver = new FakeVbaProjectReferenceResolver(
            new ResolvedVbaProjectReference(
                "Available",
                "{00000000-0000-0000-0000-000000000001}",
                1,
                0));
        var manifestPath = Path.Combine(projectRoot, ProjectManifest.ManifestFileName);
        var manifestBefore = File.ReadAllBytes(manifestPath);
        var application = CommandLineTestFactory.Create(
            projectRoot,
            vbaProjectReferenceResolver: resolver);

        var result = application.Run(["common-module", "add", "Feature"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Missing", result.StandardError, StringComparison.Ordinal);
        Assert.Equal(["Available", "Missing"], resolver.RequestedNames);
        Assert.Equal(manifestBefore, File.ReadAllBytes(manifestPath));
        Assert.False(File.Exists(Path.Combine(
            projectRoot,
            "src",
            "Book1",
            "common-modules",
            "Feature.bas")));
    }

    [Fact]
    public void AddPreservesExistingReferenceIntentAndAppendsCanonicalDependencies()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var store = new JsonProjectManifestStore();
        var manifestPath = Path.Combine(projectRoot, ProjectManifest.ManifestFileName);
        var manifest = store.Load(manifestPath);
        manifest.Documents["Book1"].References.Add(
            new VbaProjectReference("eXiSting Spelling", requested: true));
        store.Save(projectRoot, manifest);
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifestWithReferences(
            commonRepo,
            ("Base.bas", "optional", "", "[\"Existing Spelling\",\"alias a\"]"),
            ("Feature.bas", "optional", "Base.bas", "[\"Alias A\",\"alias b\"]"));
        WriteModule(commonRepo, "Base.bas", "base");
        WriteModule(commonRepo, "Feature.bas", "feature");
        var resolver = new FakeVbaProjectReferenceResolver(
            new ResolvedVbaProjectReference(
                "Alias A",
                "{00000000-0000-0000-0000-000000000001}",
                1,
                0),
            new ResolvedVbaProjectReference(
                "Alias B",
                "{00000000-0000-0000-0000-000000000002}",
                1,
                0));
        var application = CommandLineTestFactory.Create(
            projectRoot,
            vbaProjectReferenceResolver: resolver);

        var result = application.Run(["common-module", "add", "Feature"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["alias a", "alias b"], resolver.RequestedNames);
        var updated = store.Load(manifestPath);
        Assert.Equal(
            [
                new VbaProjectReference("eXiSting Spelling", requested: true),
                new VbaProjectReference("Alias A", requested: false),
                new VbaProjectReference("Alias B", requested: false)
            ],
            updated.Documents["Book1"].References);
        Assert.Equal("base", ReadModuleBody(Path.Combine(
            projectRoot,
            "src",
            "Book1",
            "common-modules",
            "Base.bas")));
        Assert.Equal("feature", ReadModuleBody(Path.Combine(
            projectRoot,
            "src",
            "Book1",
            "common-modules",
            "Feature.bas")));
    }

    [Fact]
    public void AddRejectsStaleRequiredReferenceEvidenceBeforeSourceMutation()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifestWithReferences(
            commonRepo,
            ("Feature.bas", "optional", "", "[\"Library\"]"));
        WriteModule(commonRepo, "Feature.bas", "feature");
        var manifestPath = Path.Combine(projectRoot, ProjectManifest.ManifestFileName);
        var manifestStore = new MutateAfterFirstLoadManifestStore(
            projectRoot,
            latest => latest.Documents["Book1"] = latest.Documents["Book1"] with
            {
                TemplatePath = "src/Book1/Other.xlsm"
            });
        var resolver = new FakeVbaProjectReferenceResolver(
            new ResolvedVbaProjectReference(
                "Library",
                "{00000000-0000-0000-0000-000000000001}",
                1,
                0));
        var application = CommandLineTestFactory.Create(
            projectRoot,
            vbaProjectReferenceResolver: resolver,
            projectManifestStore: manifestStore);

        var result = application.Run(["common-module", "add", "Feature"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(
            "[commonModulesRequiredReferencePlanChanged] "
            + "CommonModules required-reference planning changed while references were being resolved. "
            + "No source or manifest changes were made. Rerun the command."
            + Environment.NewLine,
            result.StandardError);
        Assert.Equal(["Library"], resolver.RequestedNames);
        Assert.False(File.Exists(Path.Combine(
            projectRoot,
            "src",
            "Book1",
            "common-modules",
            "Feature.bas")));
        var latest = new JsonProjectManifestStore().Load(manifestPath);
        Assert.Equal("src/Book1/Other.xlsm", latest.Documents["Book1"].TemplatePath);
        Assert.Empty(latest.Documents["Book1"].References);
        Assert.Empty(latest.Documents["Book1"].CommonModules);
    }

    [Fact]
    public void AddReportsStablePlanChangeWhenRebasedTemplateIdentityCannotBeResolved()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifestWithReferences(
            commonRepo,
            ("Feature.bas", "optional", "", "[\"Library\"]"));
        WriteModule(commonRepo, "Feature.bas", "feature");
        WriteBytes(Path.Combine(projectRoot, "src", "Book1", "Book1.xlsm"), [1]);
        var manifestStore = new MutateAfterFirstLoadManifestStore(
            projectRoot,
            latest => latest.Documents["Book1"] = latest.Documents["Book1"] with
            {
                TemplatePath = "src/Book1/Book1.xlsm/Other.xlsm"
            });
        var resolver = new FakeVbaProjectReferenceResolver(
            new ResolvedVbaProjectReference(
                "Library",
                "{00000000-0000-0000-0000-000000000001}",
                1,
                0));
        var application = CommandLineTestFactory.Create(
            projectRoot,
            vbaProjectReferenceResolver: resolver,
            projectManifestStore: manifestStore);

        var result = application.Run(["common-module", "add", "Feature"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[commonModulesRequiredReferencePlanChanged]", result.StandardError, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(
            projectRoot,
            "src",
            "Book1",
            "common-modules",
            "Feature.bas")));
    }

    [Fact]
    public void AddReportsStablePlanChangeWhenRebasedTemplatePathCannotBeNormalized()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifestWithReferences(
            commonRepo,
            ("Feature.bas", "optional", "", "[\"Library\"]"));
        WriteModule(commonRepo, "Feature.bas", "feature");
        var manifestStore = new MutateAfterFirstLoadManifestStore(
            projectRoot,
            latest => latest.Documents["Book1"] = latest.Documents["Book1"] with
            {
                TemplatePath = "src/\0/Book.xlsm"
            });
        var resolver = new FakeVbaProjectReferenceResolver(
            new ResolvedVbaProjectReference(
                "Library",
                "{00000000-0000-0000-0000-000000000001}",
                1,
                0));
        var application = CommandLineTestFactory.Create(
            projectRoot,
            vbaProjectReferenceResolver: resolver,
            projectManifestStore: manifestStore);

        var result = application.Run(["common-module", "add", "Feature"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[commonModulesRequiredReferencePlanChanged]", result.StandardError, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(
            projectRoot,
            "src",
            "Book1",
            "common-modules",
            "Feature.bas")));
    }

    [Fact]
    public void AddRejectsRebasedSelectedDocumentDisappearanceBeforeSourceMutation()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifestWithReferences(
            commonRepo,
            ("Feature.bas", "optional", "", "[\"Library\"]"));
        WriteModule(commonRepo, "Feature.bas", "feature");
        var store = new JsonProjectManifestStore();
        var manifestPath = Path.Combine(projectRoot, ProjectManifest.ManifestFileName);
        var resolver = new MutatingReferenceResolver(
            () =>
            {
                var latest = store.Load(manifestPath);
                latest.Documents.Remove("Book1");
                latest.Documents.Add("Other", ProjectDocument.CreateExcel("Other"));
                Directory.CreateDirectory(Path.Combine(projectRoot, "src", "Other"));
                store.Save(projectRoot, latest with { PrimaryDocument = "Other" });
            },
            new ResolvedVbaProjectReference(
                "Library",
                "{00000000-0000-0000-0000-000000000001}",
                1,
                0));
        var application = CommandLineTestFactory.Create(
            projectRoot,
            vbaProjectReferenceResolver: resolver);

        var result = application.Run(["common-module", "add", "Feature"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[commonModulesRequiredReferencePlanChanged]", result.StandardError, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(
            projectRoot,
            "src",
            "Book1",
            "common-modules",
            "Feature.bas")));
        var latest = store.Load(manifestPath);
        Assert.Equal("Other", latest.PrimaryDocument);
        Assert.DoesNotContain("Book1", latest.Documents.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Empty(latest.Documents["Other"].CommonModules);
        Assert.Empty(latest.Documents["Other"].References);
    }

    [Fact]
    public void AddRejectsRebasedRequiredReferenceChangeBeforeSourceMutation()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifestWithReferences(
            commonRepo,
            ("Feature.bas", "optional", "", "[\"Library\"]"));
        WriteModule(commonRepo, "Feature.bas", "feature");
        var resolver = new MutatingReferenceResolver(
            () => WriteManifestWithReferences(
                commonRepo,
                ("Feature.bas", "optional", "", "[\"Other Library\"]")),
            new ResolvedVbaProjectReference(
                "Library",
                "{00000000-0000-0000-0000-000000000001}",
                1,
                0));
        var application = CommandLineTestFactory.Create(
            projectRoot,
            vbaProjectReferenceResolver: resolver);

        var result = application.Run(["common-module", "add", "Feature"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[commonModulesRequiredReferencePlanChanged]", result.StandardError, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(
            projectRoot,
            "src",
            "Book1",
            "common-modules",
            "Feature.bas")));
        var latest = new JsonProjectManifestStore().Load(
            Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        Assert.Empty(latest.Documents["Book1"].References);
        Assert.Empty(latest.Documents["Book1"].CommonModules);
    }

    [Fact]
    public void AddRejectsRebasedMissingReferenceSubsetBeforeSourceMutation()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifestWithReferences(
            commonRepo,
            ("Feature.bas", "optional", "", "[\"First Library\",\"Second Library\"]"));
        WriteModule(commonRepo, "Feature.bas", "feature");
        var store = new JsonProjectManifestStore();
        var manifestPath = Path.Combine(projectRoot, ProjectManifest.ManifestFileName);
        var resolver = new MutatingReferenceResolver(
            () =>
            {
                var latest = store.Load(manifestPath);
                latest.Documents["Book1"].References.Add(
                    new VbaProjectReference("Second Library", requested: true));
                store.Save(projectRoot, latest);
            },
            new ResolvedVbaProjectReference(
                "First Library",
                "{00000000-0000-0000-0000-000000000001}",
                1,
                0),
            new ResolvedVbaProjectReference(
                "Second Library",
                "{00000000-0000-0000-0000-000000000002}",
                1,
                0));
        var application = CommandLineTestFactory.Create(
            projectRoot,
            vbaProjectReferenceResolver: resolver);

        var result = application.Run(["common-module", "add", "Feature"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[commonModulesRequiredReferencePlanChanged]", result.StandardError, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(
            projectRoot,
            "src",
            "Book1",
            "common-modules",
            "Feature.bas")));
        var latest = store.Load(manifestPath);
        Assert.Equal(
            [new VbaProjectReference("Second Library", requested: true)],
            latest.Documents["Book1"].References);
        Assert.Empty(latest.Documents["Book1"].CommonModules);
    }

    [Fact]
    public void AddRejectsReorderedRebasedMissingReferencePlanBeforeSourceMutation()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifestWithReferences(
            commonRepo,
            ("Feature.bas", "optional", "", "[\"First Library\",\"Second Library\"]"));
        WriteModule(commonRepo, "Feature.bas", "feature");
        var resolver = new MutatingReferenceResolver(
            () => WriteManifestWithReferences(
                commonRepo,
                ("Feature.bas", "optional", "", "[\"Second Library\",\"First Library\"]")),
            new ResolvedVbaProjectReference(
                "First Library",
                "{00000000-0000-0000-0000-000000000001}",
                1,
                0),
            new ResolvedVbaProjectReference(
                "Second Library",
                "{00000000-0000-0000-0000-000000000002}",
                1,
                0));
        var application = CommandLineTestFactory.Create(
            projectRoot,
            vbaProjectReferenceResolver: resolver);

        var result = application.Run(["common-module", "add", "Feature"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[commonModulesRequiredReferencePlanChanged]", result.StandardError, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(
            projectRoot,
            "src",
            "Book1",
            "common-modules",
            "Feature.bas")));
        var latest = new JsonProjectManifestStore().Load(
            Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        Assert.Empty(latest.Documents["Book1"].References);
        Assert.Empty(latest.Documents["Book1"].CommonModules);
    }

    [Fact]
    public void AddRejectsEmptyRebasedMissingReferencePlanBeforeSourceMutation()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifestWithReferences(
            commonRepo,
            ("Feature.bas", "optional", "", "[\"Library\"]"));
        WriteModule(commonRepo, "Feature.bas", "feature");
        var store = new JsonProjectManifestStore();
        var manifestPath = Path.Combine(projectRoot, ProjectManifest.ManifestFileName);
        var resolver = new MutatingReferenceResolver(
            () =>
            {
                var latest = store.Load(manifestPath);
                latest.Documents["Book1"].References.Add(
                    new VbaProjectReference("Library", requested: true));
                store.Save(projectRoot, latest);
            },
            new ResolvedVbaProjectReference(
                "Library",
                "{00000000-0000-0000-0000-000000000001}",
                1,
                0));
        var application = CommandLineTestFactory.Create(
            projectRoot,
            vbaProjectReferenceResolver: resolver);

        var result = application.Run(["common-module", "add", "Feature"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[commonModulesRequiredReferencePlanChanged]", result.StandardError, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(
            projectRoot,
            "src",
            "Book1",
            "common-modules",
            "Feature.bas")));
        var latest = store.Load(manifestPath);
        Assert.Equal(
            [new VbaProjectReference("Library", requested: true)],
            latest.Documents["Book1"].References);
        Assert.Empty(latest.Documents["Book1"].CommonModules);
    }

    [Fact]
    public void AddCompletesAmbiguityProbeBeforeManifestMutationWindow()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifestWithReferences(
            commonRepo,
            ("Feature.bas", "optional", "", "[\"Library\"]"));
        WriteModule(commonRepo, "Feature.bas", "feature");
        var window = new MutationWindowState();
        var resolver = new FakeVbaProjectReferenceResolver(
            new ResolvedVbaProjectReference(
                "Library",
                "{00000000-0000-0000-0000-000000000001}",
                1,
                0),
            new ResolvedVbaProjectReference(
                "Library",
                "{00000000-0000-0000-0000-000000000002}",
                1,
                0));
        var ambiguityProbe = new MutationWindowObservingAmbiguityProbe(window);
        var mutationCoordinator = new MutationWindowTrackingCoordinator(window);
        var application = CommandLineTestFactory.Create(
            projectRoot,
            vbaProjectReferenceResolver: resolver,
            vbaProjectReferenceAmbiguityProbe: ambiguityProbe,
            projectManifestMutationCoordinator: mutationCoordinator);

        var result = application.Run(["common-module", "add", "Feature"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, ambiguityProbe.CallCount);
        Assert.False(ambiguityProbe.ObservedMutationWindow);
    }

    [Fact]
    public void AddUpgradesInstalledDependencyWithoutRepositoryMetadataRefreshOrRecopy()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifest(commonRepo, ("Base.bas", "test-foundation", ""));
        WriteModule(commonRepo, "Base.bas", "base v2");
        var sourceSet = Path.Combine(projectRoot, "src", "Book1");
        WriteModule(sourceSet, "Base.bas", "base v1");
        var store = new JsonProjectManifestStore();
        var manifest = store.Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        manifest.Documents["Book1"].CommonModules.Add(
            new InstalledCommonModule("base", "base.bas", Requested: false, TestOnly: false));
        store.Save(projectRoot, manifest);
        var application = CommandLineTestFactory.Create(projectRoot);

        var result = application.Run(["common-module", "add", "Base"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("base v1", ReadModuleBody(Path.Combine(sourceSet, "Base.bas")));
        var updatedManifest = store.Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        Assert.Equal(
            [Installed("base", requested: true, testOnly: false, moduleFile: "base.bas")],
            updatedManifest.Documents["Book1"].CommonModules);
    }

    [Fact]
    public void AddIsIdempotentAndDoesNotDuplicateOrRecopyInstalledEntries()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifest(commonRepo, ("Feature.bas", "optional", ""));
        WriteModule(commonRepo, "Feature.bas", "feature v1");
        var application = CommandLineTestFactory.Create(projectRoot);

        Assert.Equal(0, application.Run(["common-module", "add", "Feature"]).ExitCode);
        WriteModule(commonRepo, "Feature.bas", "feature v2");
        var result = application.Run(["common-module", "add", "Feature"]);

        Assert.Equal(0, result.ExitCode);
        var sourceSet = Path.Combine(projectRoot, "src", "Book1");
        Assert.Equal("feature v1", ReadModuleBody(Path.Combine(sourceSet, "common-modules", "Feature.bas")));
        var manifest = new JsonProjectManifestStore().Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        Assert.Equal([Installed("Feature", requested: true)], manifest.Documents["Book1"].CommonModules);
    }

    [Fact]
    public void AddFailsOnUntrackedSourceConflictUnlessForceIsSpecified()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifest(commonRepo, ("Feature.bas", "optional", ""));
        WriteModule(commonRepo, "Feature.bas", "repo feature");
        var sourceSet = Path.Combine(projectRoot, "src", "Book1");
        WriteModule(sourceSet, "Feature.bas", "local feature");
        var application = CommandLineTestFactory.Create(projectRoot);

        var conflict = application.Run(["common-module", "add", "Feature"]);

        Assert.Equal(1, conflict.ExitCode);
        Assert.Contains("already exists", conflict.StandardError, StringComparison.Ordinal);
        Assert.Equal("local feature", ReadModuleBody(Path.Combine(sourceSet, "Feature.bas")));
        var afterConflict = new JsonProjectManifestStore().Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        Assert.Empty(afterConflict.Documents["Book1"].CommonModules);

        var forced = application.Run(["common-module", "add", "Feature", "--force"]);

        Assert.Equal(0, forced.ExitCode);
        Assert.Equal("repo feature", ReadModuleBody(Path.Combine(sourceSet, "Feature.bas")));
        var manifest = new JsonProjectManifestStore().Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        Assert.Equal([Installed("Feature", requested: true)], manifest.Documents["Book1"].CommonModules);
    }

    [Fact]
    public void AddUsesFlatNestedSourceIdentityForConflictsAndForcedOverwrite()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifest(commonRepo, ("Feature.bas", "optional", ""));
        WriteModule(commonRepo, "Feature.bas", "repo feature");
        var sourceSet = Path.Combine(projectRoot, "src", "Book1");
        WriteModule(sourceSet, Path.Combine("nested", "Feature.bas"), "local feature");
        var application = CommandLineTestFactory.Create(projectRoot);

        var conflict = application.Run(["common-module", "add", "Feature"]);

        Assert.Equal(1, conflict.ExitCode);
        Assert.Contains("already exists", conflict.StandardError, StringComparison.Ordinal);
        Assert.Equal("local feature", ReadModuleBody(Path.Combine(sourceSet, "nested", "Feature.bas")));
        Assert.False(File.Exists(Path.Combine(sourceSet, "Feature.bas")));

        var forced = application.Run(["common-module", "add", "Feature", "--force"]);

        Assert.Equal(0, forced.ExitCode);
        Assert.Equal("repo feature", ReadModuleBody(Path.Combine(sourceSet, "nested", "Feature.bas")));
        Assert.False(File.Exists(Path.Combine(sourceSet, "Feature.bas")));
        Assert.False(Directory.Exists(Path.Combine(sourceSet, "common-modules")));
        Assert.Contains("Copied nested/Feature.bas", forced.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void AddForceFailsOnDuplicateNestedMatchesBeforeAnyFileOrManifestMutation()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifest(
            commonRepo,
            ("Base.bas", "runtime-baseline", ""),
            ("Feature.bas", "optional", "Base.bas"));
        WriteModule(commonRepo, "Base.bas", "repo base");
        WriteModule(commonRepo, "Feature.bas", "repo feature");
        var sourceSet = Path.Combine(projectRoot, "src", "Book1");
        WriteModule(sourceSet, "Base.bas", "local base");
        WriteModule(sourceSet, Path.Combine("first", "Feature.bas"), "local feature 1");
        WriteModule(sourceSet, Path.Combine("second", "Feature.bas"), "local feature 2");
        var application = CommandLineTestFactory.Create(projectRoot);

        var result = application.Run(["common-module", "add", "Feature", "--force"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("multiple", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Feature.bas", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("local base", ReadModuleBody(Path.Combine(sourceSet, "Base.bas")));
        Assert.Equal("local feature 1", ReadModuleBody(Path.Combine(sourceSet, "first", "Feature.bas")));
        var manifest = new JsonProjectManifestStore().Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        Assert.Empty(manifest.Documents["Book1"].CommonModules);
    }

    [Fact]
    public void AddUsesPrimaryDocumentByDefaultAndHonorsExplicitDocument()
    {
        using var temp = TempDirectory.Create();
        var commonRepo = temp.CreateDirectory("common_modules_repo");
        var projectRoot = temp.CreateDirectory("Project");
        Directory.CreateDirectory(Path.Combine(projectRoot, "src", "Book1"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "src", "SecondBook"));
        new JsonProjectManifestStore().Save(projectRoot, ProjectManifestTestData.TwoDocumentManifest(projectRoot) with
        {
            CommonModulesRepository = "../common_modules_repo"
        });
        WriteManifest(commonRepo, ("Feature.bas", "optional", ""));
        WriteModule(commonRepo, "Feature.bas", "feature");
        var application = CommandLineTestFactory.Create(projectRoot);

        Assert.Equal(0, application.Run(["common-module", "add", "Feature"]).ExitCode);
        Assert.Equal(0, application.Run(["common-module", "add", "Feature", "--document", "SecondBook"]).ExitCode);

        var manifest = new JsonProjectManifestStore().Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        Assert.Equal([Installed("Feature", requested: true)], manifest.Documents["Book1"].CommonModules);
        Assert.Equal([Installed("Feature", requested: true)], manifest.Documents["SecondBook"].CommonModules);
    }

    [Fact]
    public async Task ListOutputsSelectedDocumentAsTextAndJson()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var store = new JsonProjectManifestStore();
        var manifest = store.Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        manifest.Documents["Book1"].CommonModules.AddRange(
            [
                Installed("Base", requested: false),
                Installed("Feature", requested: true)
            ]);
        store.Save(projectRoot, manifest);
        var application = VbaDevCommandLine.Create(
            ToolingCompositionRoot.CreateApplicationComposition(projectRoot));

        var text = await application.RunAsync(["common-module", "list"]);
        var json = await application.RunAsync(["common-module", "list", "--format", "json"]);

        Assert.Equal(0, text.ExitCode);
        Assert.Contains("Document: Book1", text.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Base", text.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("requested: false", text.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Feature", text.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("requested: true", text.StandardOutput, StringComparison.Ordinal);

        Assert.Equal(0, json.ExitCode);
        using var parsed = JsonDocument.Parse(json.StandardOutput);
        Assert.Equal("Book1", parsed.RootElement.GetProperty("document").GetString());
        var modules = parsed.RootElement.GetProperty("commonModules");
        Assert.Equal("Base", modules[0].GetProperty("name").GetString());
        Assert.False(modules[0].GetProperty("requested").GetBoolean());
        Assert.Equal("Feature", modules[1].GetProperty("name").GetString());
        Assert.True(modules[1].GetProperty("requested").GetBoolean());
    }

    [Fact]
    public void AddRejectsDuplicateCommonModuleNameAcrossExtensions()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifest(
            commonRepo,
            ("Foo.bas", "optional", ""),
            ("Foo.cls", "optional", ""));
        WriteModule(commonRepo, "Foo.bas", "bas");
        WriteModule(commonRepo, "Foo.cls", "cls");
        var application = CommandLineTestFactory.Create(projectRoot);

        var result = application.Run(["common-module", "add", "Foo"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("duplicate CommonModuleName", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddRejectsFlatInstalledIdentityCollisionBeforeMutation()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifest(
            commonRepo,
            ("Foo.bas", "optional", ""),
            ("FOO.cls", "optional", ""),
            ("Root.bas", "optional", "Foo.bas,FOO.cls"));
        WriteModule(commonRepo, "Foo.bas", "first foo");
        WriteModule(commonRepo, "FOO.cls", "second foo");
        WriteModule(commonRepo, "Root.bas", "root");
        var application = CommandLineTestFactory.Create(projectRoot);

        var result = application.Run(["common-module", "add", "Root"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("duplicate CommonModule", result.StandardError, StringComparison.OrdinalIgnoreCase);
        var sourceSet = Path.Combine(projectRoot, "src", "Book1");
        Assert.False(Directory.Exists(Path.Combine(sourceSet, "common-modules")));
        var unchangedManifest = new JsonProjectManifestStore().Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        Assert.Empty(unchangedManifest.Documents["Book1"].CommonModules);
        Assert.Empty(Directory.EnumerateFiles(projectRoot, "vba-project.failed-*.json"));
    }

    [Fact]
    public void AddFailsWhenCommonModulesRepositoryIsMissing()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = temp.CreateDirectory("Project");
        Directory.CreateDirectory(Path.Combine(projectRoot, "src", "Book1"));
        var missingRepo = Path.Combine(temp.Path, "missing_common_modules_repo");
        new JsonProjectManifestStore().Save(projectRoot, ProjectManifest.CreateDefault("Project", "Book1", projectRoot, missingRepo));
        var application = CommandLineTestFactory.Create(projectRoot);

        var result = application.Run(["common-module", "add", "Runtime.bas"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("CommonModulesRepository was not found", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateOverwritesInstalledModulesAddsDependenciesAndKeepsObsoleteFiles()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var store = new JsonProjectManifestStore();
        var manifest = store.Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        manifest.Documents["Book1"].CommonModules.AddRange(
            [
                Installed("Base", requested: false),
                Installed("Feature", requested: true)
            ]);
        store.Save(projectRoot, manifest);
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifest(
            commonRepo,
            ("Base.bas", "runtime-baseline", ""),
            ("Feature.bas", "optional", "Base.bas"),
            ("Unlisted.bas", "optional", ""));
        WriteModule(commonRepo, "Base.bas", "base v2");
        WriteModule(commonRepo, "Feature.bas", "feature v2");
        WriteModule(commonRepo, "Unlisted.bas", "unlisted v2");
        var sourceSet = Path.Combine(projectRoot, "src", "Book1");
        WriteModule(sourceSet, "Feature.bas", "feature v1");
        WriteModule(sourceSet, "Unlisted.bas", "unlisted v1");
        WriteModule(sourceSet, "Obsolete.bas", "obsolete");
        var application = VbaDevCommandLine.Create(
            ToolingCompositionRoot.CreateApplicationComposition(projectRoot));

        var result = await application.RunAsync(["common-module", "update"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("base v2", ReadModuleBody(Path.Combine(sourceSet, "common-modules", "Base.bas")));
        Assert.Equal("feature v2", ReadModuleBody(Path.Combine(sourceSet, "Feature.bas")));
        Assert.Equal("unlisted v1", ReadModuleBody(Path.Combine(sourceSet, "Unlisted.bas")));
        Assert.Equal("obsolete", ReadModuleBody(Path.Combine(sourceSet, "Obsolete.bas")));
        Assert.False(File.Exists(Path.Combine(sourceSet, "Base.bas")));
        var updatedManifest = store.Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        Assert.Equal(
            [
                Installed("Base", requested: false),
                Installed("Feature", requested: true)
            ],
            updatedManifest.Documents["Book1"].CommonModules);
        Assert.Contains("Updated Book1/common-modules/Base.bas", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Updated Book1/Feature.bas", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateOverwritesNestedInstalledModulesAndCopiesMissingDependenciesToCommonModulesDirectory()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var store = new JsonProjectManifestStore();
        var manifest = store.Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        manifest.Documents["Book1"].CommonModules.Add(Installed("Feature", requested: true));
        store.Save(projectRoot, manifest);
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifest(
            commonRepo,
            ("Base.bas", "runtime-baseline", ""),
            ("Feature.bas", "optional", "Base.bas"));
        WriteModule(commonRepo, "Base.bas", "base v2");
        WriteModule(commonRepo, "Feature.bas", "feature v2");
        var sourceSet = Path.Combine(projectRoot, "src", "Book1");
        WriteModule(sourceSet, Path.Combine("nested", "Feature.bas"), "feature v1");
        var application = CommandLineTestFactory.Create(projectRoot);

        var result = application.Run(["common-module", "update"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("feature v2", ReadModuleBody(Path.Combine(sourceSet, "nested", "Feature.bas")));
        Assert.Equal("base v2", ReadModuleBody(Path.Combine(sourceSet, "common-modules", "Base.bas")));
        Assert.False(File.Exists(Path.Combine(sourceSet, "Base.bas")));
        Assert.False(File.Exists(Path.Combine(sourceSet, "Feature.bas")));
        Assert.Contains("Updated Book1/nested/Feature.bas", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Updated Book1/common-modules/Base.bas", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateRefreshesCanonicalMetadataForInstalledModuleOutsideRequestedClosure()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var store = new JsonProjectManifestStore();
        var manifest = store.Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        manifest.Documents["Book1"].CommonModules.Add(
            new InstalledCommonModule("feature", "feature.bas", Requested: false, TestOnly: false));
        store.Save(projectRoot, manifest);
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifest(commonRepo, ("Feature.bas", "test-double", ""));
        WriteModule(commonRepo, "Feature.bas", "feature v2");
        var sourceSet = Path.Combine(projectRoot, "src", "Book1");
        WriteModule(sourceSet, "feature.bas", "feature v1");
        var application = CommandLineTestFactory.Create(projectRoot);

        var result = application.Run(["common-module", "update"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("feature v2", ReadModuleBody(Path.Combine(sourceSet, "feature.bas")));
        var updatedManifest = store.Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        Assert.Equal(
            [new InstalledCommonModule("Feature", "Feature.bas", Requested: false, TestOnly: true)],
            updatedManifest.Documents["Book1"].CommonModules);
    }

    [Fact]
    public void UpdateRejectsSubstantiveInstalledSourceIdentityChangeWithoutMutation()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var store = new JsonProjectManifestStore();
        var manifest = store.Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        manifest.Documents["Book1"].CommonModules.Add(Installed("Feature", requested: false));
        store.Save(projectRoot, manifest);
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifest(commonRepo, ("Feature.cls", "optional", ""));
        WriteModule(commonRepo, "Feature.cls", "feature class v2");
        var sourceSet = Path.Combine(projectRoot, "src", "Book1");
        WriteModule(sourceSet, "Feature.bas", "feature module v1");
        var application = CommandLineTestFactory.Create(projectRoot);

        var result = application.Run(["common-module", "update"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("source identity", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("feature module v1", ReadModuleBody(Path.Combine(sourceSet, "Feature.bas")));
        Assert.False(File.Exists(Path.Combine(sourceSet, "common-modules", "Feature.cls")));
        var unchangedManifest = store.Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        Assert.Equal([Installed("Feature", requested: false)], unchangedManifest.Documents["Book1"].CommonModules);
    }

    [Fact]
    public void UpdateRejectsFlatInstalledIdentityCollisionBeforeMutation()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var store = new JsonProjectManifestStore();
        var manifest = store.Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        manifest.Documents["Book1"].CommonModules.Add(Installed("Root", requested: true));
        store.Save(projectRoot, manifest);
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifest(
            commonRepo,
            ("Foo.bas", "optional", ""),
            ("FOO.cls", "optional", ""),
            ("Root.bas", "optional", "Foo.bas,FOO.cls"));
        WriteModule(commonRepo, "Foo.bas", "first foo");
        WriteModule(commonRepo, "FOO.cls", "second foo");
        WriteModule(commonRepo, "Root.bas", "root v2");
        var sourceSet = Path.Combine(projectRoot, "src", "Book1");
        WriteModule(sourceSet, "Root.bas", "root v1");
        var application = CommandLineTestFactory.Create(projectRoot);

        var result = application.Run(["common-module", "update"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("duplicate CommonModule", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("root v1", ReadModuleBody(Path.Combine(sourceSet, "Root.bas")));
        Assert.False(Directory.Exists(Path.Combine(sourceSet, "common-modules")));
        var unchangedManifest = store.Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        Assert.Equal([Installed("Root", requested: true)], unchangedManifest.Documents["Book1"].CommonModules);
        Assert.Empty(Directory.EnumerateFiles(projectRoot, "vba-project.failed-*.json"));
    }

    [Fact]
    public void UpdateFailsOnDuplicateNestedMatchesBeforeAnyFileOrManifestMutation()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var store = new JsonProjectManifestStore();
        var manifest = store.Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        manifest.Documents["Book1"].CommonModules.AddRange(
            [
                Installed("Base", requested: false),
                Installed("Feature", requested: true)
            ]);
        store.Save(projectRoot, manifest);
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifest(
            commonRepo,
            ("Base.bas", "runtime-baseline", ""),
            ("Feature.bas", "optional", "Base.bas"));
        WriteModule(commonRepo, "Base.bas", "base v2");
        WriteModule(commonRepo, "Feature.bas", "feature v2");
        var sourceSet = Path.Combine(projectRoot, "src", "Book1");
        WriteModule(sourceSet, "Base.bas", "base v1");
        WriteModule(sourceSet, Path.Combine("first", "Feature.bas"), "feature v1 first");
        WriteModule(sourceSet, Path.Combine("second", "Feature.bas"), "feature v1 second");
        var application = CommandLineTestFactory.Create(projectRoot);

        var result = application.Run(["common-module", "update"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("multiple", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("base v1", ReadModuleBody(Path.Combine(sourceSet, "Base.bas")));
        Assert.Equal("feature v1 first", ReadModuleBody(Path.Combine(sourceSet, "first", "Feature.bas")));
        var updatedManifest = store.Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        Assert.Equal(
            [
                Installed("Base", requested: false),
                Installed("Feature", requested: true)
            ],
            updatedManifest.Documents["Book1"].CommonModules);
    }

    [Fact]
    public void UpdateInstallsNewDependenciesRequiredByRequestedRoots()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var store = new JsonProjectManifestStore();
        var manifest = store.Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        manifest.Documents["Book1"].CommonModules.Add(Installed("Feature", requested: true));
        store.Save(projectRoot, manifest);
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifest(
            commonRepo,
            ("Base.bas", "runtime-baseline", ""),
            ("Feature.bas", "optional", "Base.bas"));
        WriteModule(commonRepo, "Base.bas", "base v2");
        WriteModule(commonRepo, "Feature.bas", "feature v2");
        var sourceSet = Path.Combine(projectRoot, "src", "Book1");
        WriteModule(sourceSet, "Feature.bas", "feature v1");
        var application = CommandLineTestFactory.Create(projectRoot);

        var result = application.Run(["common-module", "update"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("base v2", ReadModuleBody(Path.Combine(sourceSet, "common-modules", "Base.bas")));
        Assert.Equal("feature v2", ReadModuleBody(Path.Combine(sourceSet, "Feature.bas")));
        Assert.False(File.Exists(Path.Combine(sourceSet, "Base.bas")));
        var updatedManifest = store.Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        Assert.Equal(
            [
                Installed("Feature", requested: true),
                Installed("Base", requested: false)
            ],
            updatedManifest.Documents["Book1"].CommonModules);
    }

    [Fact]
    public void UpdateInstallsNewDependenciesAcrossAllDocumentSourceSets()
    {
        using var temp = TempDirectory.Create();
        var commonRepo = temp.CreateDirectory("common_modules_repo");
        var projectRoot = temp.CreateDirectory("Project");
        Directory.CreateDirectory(Path.Combine(projectRoot, "src", "Book1"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "src", "SecondBook"));
        var manifest = ProjectManifestTestData.TwoDocumentManifest(projectRoot) with
        {
            CommonModulesRepository = "../common_modules_repo"
        };
        manifest.Documents["Book1"].CommonModules.Add(Installed("Feature", requested: true));
        manifest.Documents["SecondBook"].CommonModules.Add(Installed("Feature", requested: true));
        var store = new JsonProjectManifestStore();
        store.Save(projectRoot, manifest);
        WriteManifest(
            commonRepo,
            ("Base.bas", "runtime-baseline", ""),
            ("Feature.bas", "optional", "Base.bas"));
        WriteModule(commonRepo, "Base.bas", "base v2");
        WriteModule(commonRepo, "Feature.bas", "feature v2");
        var firstSourceSet = Path.Combine(projectRoot, "src", "Book1");
        var secondSourceSet = Path.Combine(projectRoot, "src", "SecondBook");
        WriteModule(firstSourceSet, "Feature.bas", "first feature v1");
        WriteModule(secondSourceSet, "Feature.bas", "second feature v1");
        var application = CommandLineTestFactory.Create(projectRoot);

        var result = application.Run(["common-module", "update"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("base v2", ReadModuleBody(Path.Combine(firstSourceSet, "common-modules", "Base.bas")));
        Assert.Equal("base v2", ReadModuleBody(Path.Combine(secondSourceSet, "common-modules", "Base.bas")));
        Assert.False(File.Exists(Path.Combine(firstSourceSet, "Base.bas")));
        Assert.False(File.Exists(Path.Combine(secondSourceSet, "Base.bas")));
        var updatedManifest = store.Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        Assert.Equal(
            [
                Installed("Feature", requested: true),
                Installed("Base", requested: false)
            ],
            updatedManifest.Documents["Book1"].CommonModules);
        Assert.Equal(
            [
                Installed("Feature", requested: true),
                Installed("Base", requested: false)
            ],
            updatedManifest.Documents["SecondBook"].CommonModules);
    }

    [Fact]
    public void UpdateIsIdempotentAfterInstallingNewDependencies()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var store = new JsonProjectManifestStore();
        var manifest = store.Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        manifest.Documents["Book1"].CommonModules.Add(Installed("Feature", requested: true));
        store.Save(projectRoot, manifest);
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifest(
            commonRepo,
            ("Base.bas", "runtime-baseline", ""),
            ("Feature.bas", "optional", "Base.bas"));
        WriteModule(commonRepo, "Base.bas", "base v2");
        WriteModule(commonRepo, "Feature.bas", "feature v2");
        var sourceSet = Path.Combine(projectRoot, "src", "Book1");
        WriteModule(sourceSet, "Feature.bas", "feature v1");
        var application = CommandLineTestFactory.Create(projectRoot);

        Assert.Equal(0, application.Run(["common-module", "update"]).ExitCode);
        var result = application.Run(["common-module", "update"]);

        Assert.Equal(0, result.ExitCode);
        var updatedManifest = store.Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        Assert.Equal(
            [
                Installed("Feature", requested: true),
                Installed("Base", requested: false)
            ],
            updatedManifest.Documents["Book1"].CommonModules);
        Assert.Equal(1, updatedManifest.Documents["Book1"].CommonModules.Count(module => module.Name.Equals("Base", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void UpdateRepairsDoctorMissingDependencyFailure()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var sourceSet = Path.Combine(projectRoot, "src", "Book1");
        File.WriteAllText(Path.Combine(sourceSet, "Book1.xlsm"), string.Empty, new UTF8Encoding(false));
        var store = new JsonProjectManifestStore();
        var manifest = store.Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        manifest.Documents["Book1"].CommonModules.Add(Installed("Feature", requested: true));
        store.Save(projectRoot, manifest);
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifest(
            commonRepo,
            ("Base.bas", "runtime-baseline", ""),
            ("Feature.bas", "optional", "Base.bas"));
        WriteModule(commonRepo, "Base.bas", "base v2");
        WriteModule(commonRepo, "Feature.bas", "feature v2");
        WriteModule(sourceSet, "Feature.bas", "feature v1");
        var application = CommandLineTestFactory.Create(projectRoot, new FakeEnvironmentDiagnosticPort());

        var beforeUpdate = application.Run(["doctor"]);
        Assert.Equal(1, beforeUpdate.ExitCode);
        Assert.Contains("requires missing dependency 'Base'", beforeUpdate.StandardOutput, StringComparison.Ordinal);

        var update = application.Run(["common-module", "update"]);
        var afterUpdate = application.Run(["doctor"]);

        Assert.Equal(0, update.ExitCode);
        Assert.Equal(0, afterUpdate.ExitCode);
        Assert.DoesNotContain("requires missing dependency 'Base'", afterUpdate.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateAppliesToInstalledModulesAcrossAllDocumentSourceSets()
    {
        using var temp = TempDirectory.Create();
        var commonRepo = temp.CreateDirectory("common_modules_repo");
        var projectRoot = temp.CreateDirectory("Project");
        Directory.CreateDirectory(Path.Combine(projectRoot, "src", "Book1"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "src", "SecondBook"));
        var manifest = ProjectManifestTestData.TwoDocumentManifest(projectRoot) with
        {
            CommonModulesRepository = "../common_modules_repo"
        };
        manifest.Documents["Book1"].CommonModules.AddRange(
            [
                Installed("Base", requested: false),
                Installed("Feature", requested: true)
            ]);
        manifest.Documents["SecondBook"].CommonModules.AddRange(
            [
                Installed("Base", requested: false),
                Installed("Feature", requested: true)
            ]);
        new JsonProjectManifestStore().Save(projectRoot, manifest);
        WriteManifest(
            commonRepo,
            ("Base.bas", "runtime-baseline", ""),
            ("Feature.bas", "optional", "Base.bas"));
        WriteModule(commonRepo, "Base.bas", "base v2");
        WriteModule(commonRepo, "Feature.bas", "feature v2");
        var firstSourceSet = Path.Combine(projectRoot, "src", "Book1");
        var secondSourceSet = Path.Combine(projectRoot, "src", "SecondBook");
        WriteModule(firstSourceSet, "Feature.bas", "first feature v1");
        WriteModule(secondSourceSet, "Feature.bas", "second feature v1");
        var application = CommandLineTestFactory.Create(projectRoot);

        var result = application.Run(["common-module", "update"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("base v2", ReadModuleBody(Path.Combine(firstSourceSet, "common-modules", "Base.bas")));
        Assert.Equal("feature v2", ReadModuleBody(Path.Combine(firstSourceSet, "Feature.bas")));
        Assert.Equal("base v2", ReadModuleBody(Path.Combine(secondSourceSet, "common-modules", "Base.bas")));
        Assert.Equal("feature v2", ReadModuleBody(Path.Combine(secondSourceSet, "Feature.bas")));
        Assert.False(File.Exists(Path.Combine(firstSourceSet, "Base.bas")));
        Assert.False(File.Exists(Path.Combine(secondSourceSet, "Base.bas")));
        Assert.Contains("Updated Book1/common-modules/Base.bas", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Updated Book1/Feature.bas", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Updated SecondBook/common-modules/Base.bas", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Updated SecondBook/Feature.bas", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateResolvesRequiredReferencesBeforeAnySourceMutation()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var store = new JsonProjectManifestStore();
        var manifestPath = Path.Combine(projectRoot, ProjectManifest.ManifestFileName);
        var manifest = store.Load(manifestPath);
        manifest.Documents["Book1"].CommonModules.Add(Installed("Feature", requested: true));
        store.Save(projectRoot, manifest);
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifestWithReferences(
            commonRepo,
            ("Feature.bas", "optional", "", "[\"Missing\"]"));
        WriteModule(commonRepo, "Feature.bas", "feature v2");
        var sourcePath = Path.Combine(projectRoot, "src", "Book1", "Feature.bas");
        WriteModule(Path.GetDirectoryName(sourcePath)!, Path.GetFileName(sourcePath), "feature v1");
        var manifestBefore = File.ReadAllBytes(manifestPath);
        var resolver = new FakeVbaProjectReferenceResolver();
        var application = CommandLineTestFactory.Create(
            projectRoot,
            vbaProjectReferenceResolver: resolver);

        var result = application.Run(["common-module", "update"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Missing", result.StandardError, StringComparison.Ordinal);
        Assert.Equal(["Missing"], resolver.RequestedNames);
        Assert.Equal("feature v1", ReadModuleBody(sourcePath));
        Assert.Equal(manifestBefore, File.ReadAllBytes(manifestPath));
    }

    [Fact]
    public void UpdateRejectsInvalidPackageBeforeProjectStateChanges()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var store = new JsonProjectManifestStore();
        var manifestPath = Path.Combine(projectRoot, ProjectManifest.ManifestFileName);
        var manifest = store.Load(manifestPath);
        manifest.Documents["Book1"].CommonModules.Add(Installed("Feature", requested: true));
        store.Save(projectRoot, manifest);
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifest(commonRepo, ("Feature.bas", "optional", ""));
        WriteModule(commonRepo, "Feature.bas", "feature v2");
        WriteRawModule(commonRepo, "README.txt", "unexpected");
        var sourcePath = Path.Combine(projectRoot, "src", "Book1", "Feature.bas");
        WriteModule(Path.GetDirectoryName(sourcePath)!, Path.GetFileName(sourcePath), "feature v1");
        var manifestBefore = File.ReadAllBytes(manifestPath);
        var application = CommandLineTestFactory.Create(projectRoot);

        var result = application.Run(["common-module", "update"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("unexpected package entry", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("feature v1", ReadModuleBody(sourcePath));
        Assert.Equal(manifestBefore, File.ReadAllBytes(manifestPath));
    }

    [Fact]
    public void UpdateRejectsStaleRequiredReferenceEvidenceBeforeSourceMutation()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var store = new JsonProjectManifestStore();
        var manifestPath = Path.Combine(projectRoot, ProjectManifest.ManifestFileName);
        var manifest = store.Load(manifestPath);
        manifest.Documents["Book1"].CommonModules.Add(Installed("Feature", requested: true));
        store.Save(projectRoot, manifest);
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifestWithReferences(
            commonRepo,
            ("Feature.bas", "optional", "", "[\"Library\"]"));
        WriteModule(commonRepo, "Feature.bas", "feature v2");
        var sourcePath = Path.Combine(projectRoot, "src", "Book1", "Feature.bas");
        WriteModule(Path.GetDirectoryName(sourcePath)!, Path.GetFileName(sourcePath), "feature v1");
        var manifestStore = new MutateAfterFirstLoadManifestStore(
            projectRoot,
            latest => latest.Documents["Book1"] = latest.Documents["Book1"] with
            {
                TemplatePath = "src/Book1/Other.xlsm"
            });
        var resolver = new FakeVbaProjectReferenceResolver(
            new ResolvedVbaProjectReference(
                "Library",
                "{00000000-0000-0000-0000-000000000001}",
                1,
                0));
        var application = CommandLineTestFactory.Create(
            projectRoot,
            vbaProjectReferenceResolver: resolver,
            projectManifestStore: manifestStore);

        var result = application.Run(["common-module", "update"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[commonModulesRequiredReferencePlanChanged]", result.StandardError, StringComparison.Ordinal);
        Assert.Equal("feature v1", ReadModuleBody(sourcePath));
        var latest = store.Load(manifestPath);
        Assert.Equal("src/Book1/Other.xlsm", latest.Documents["Book1"].TemplatePath);
        Assert.Empty(latest.Documents["Book1"].References);
    }

    [Fact]
    public void UpdateRejectsRebasedTargetDisappearanceBeforeSourceMutation()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var store = new JsonProjectManifestStore();
        var manifestPath = Path.Combine(projectRoot, ProjectManifest.ManifestFileName);
        var manifest = store.Load(manifestPath);
        manifest.Documents["Book1"].CommonModules.Add(Installed("Feature", requested: true));
        store.Save(projectRoot, manifest);
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifestWithReferences(
            commonRepo,
            ("Feature.bas", "optional", "", "[\"Library\"]"));
        WriteModule(commonRepo, "Feature.bas", "feature v2");
        var sourcePath = Path.Combine(projectRoot, "src", "Book1", "Feature.bas");
        WriteModule(Path.GetDirectoryName(sourcePath)!, Path.GetFileName(sourcePath), "feature v1");
        var resolver = new MutatingReferenceResolver(
            () =>
            {
                var latest = store.Load(manifestPath);
                latest.Documents["Book1"].CommonModules.Clear();
                store.Save(projectRoot, latest);
            },
            new ResolvedVbaProjectReference(
                "Library",
                "{00000000-0000-0000-0000-000000000001}",
                1,
                0));
        var application = CommandLineTestFactory.Create(
            projectRoot,
            vbaProjectReferenceResolver: resolver);

        var result = application.Run(["common-module", "update"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[commonModulesRequiredReferencePlanChanged]", result.StandardError, StringComparison.Ordinal);
        Assert.Equal("feature v1", ReadModuleBody(sourcePath));
        var latest = store.Load(manifestPath);
        Assert.Empty(latest.Documents["Book1"].CommonModules);
        Assert.Empty(latest.Documents["Book1"].References);
    }

    [Fact]
    public void AddAndUpdateNormalizeFormSidecarsBesideTheForm()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifest(commonRepo, ("Dialog.frm", "optional", ""));
        WriteModule(commonRepo, "Dialog.frm", "repo form");
        WriteBytes(Path.Combine(commonRepo, "Dialog.frx"), [1, 2, 3]);
        var sourceSet = Path.Combine(projectRoot, "src", "Book1");
        WriteModule(sourceSet, Path.Combine("forms", "Dialog.frm"), "local form");
        WriteBytes(Path.Combine(sourceSet, "Dialog.frx"), [9]);
        WriteBytes(Path.Combine(sourceSet, "forms", "Dialog.frx"), [8]);
        WriteBytes(Path.Combine(sourceSet, "legacy", "Dialog.frx"), [7]);
        var application = CommandLineTestFactory.Create(projectRoot);

        var add = application.Run(["common-module", "add", "Dialog", "--force"]);

        Assert.Equal(0, add.ExitCode);
        Assert.Equal("repo form", ReadModuleBody(Path.Combine(sourceSet, "forms", "Dialog.frm")));
        Assert.Equal([Path.Combine(sourceSet, "forms", "Dialog.frx")], Directory.EnumerateFiles(sourceSet, "Dialog.frx", SearchOption.AllDirectories));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(sourceSet, "forms", "Dialog.frx")));

        File.Delete(Path.Combine(commonRepo, "Dialog.frx"));
        WriteBytes(Path.Combine(sourceSet, "Dialog.frx"), [6]);
        WriteBytes(Path.Combine(sourceSet, "other", "Dialog.frx"), [5]);
        var update = application.Run(["common-module", "update"]);

        Assert.Equal(0, update.ExitCode);
        Assert.Empty(Directory.EnumerateFiles(sourceSet, "Dialog.frx", SearchOption.AllDirectories));
    }

    [Fact]
    public void AddReportsFileCopyFailureWithoutSavingManifest()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifest(commonRepo, ("Feature.bas", "optional", ""));
        WriteModule(commonRepo, "Feature.bas", "repo feature");
        Directory.CreateDirectory(Path.Combine(projectRoot, "src", "Book1", "common-modules", "Feature.bas"));
        var application = CommandLineTestFactory.Create(projectRoot);

        var result = application.Run(["common-module", "add", "Feature", "--force"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("before source mutation began", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFiles(projectRoot, "vba-project.failed-*.json"));
        var manifest = new JsonProjectManifestStore().Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        Assert.Empty(manifest.Documents["Book1"].CommonModules);
    }

    [Fact]
    public void AddSavesManifestAfterCopiesAndWritesRecoveryFileWhenManifestSaveFails()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifest(commonRepo, ("Feature.bas", "optional", ""));
        WriteModule(commonRepo, "Feature.bas", "repo feature");
        var atomicWriter = new FailingCommitAtomicWriter(projectRoot);
        var mutationCoordinator = new ProjectManifestMutationCoordinator(
            atomicWriter,
            new ProjectManifestMutationLeaseProvider());
        var application = CommandLineTestFactory.Create(
            projectRoot,
            projectManifestMutationCoordinator: mutationCoordinator);

        var result = application.Run(["common-module", "add", "Feature"]);

        Assert.Equal(1, result.ExitCode);
        Assert.True(atomicWriter.FileExistedDuringReplace);
        var recoveryFile = Assert.Single(Directory.EnumerateFiles(projectRoot, "vba-project.failed-*.json"));
        Assert.Contains(recoveryFile, result.StandardError, StringComparison.Ordinal);
        Assert.Contains("manual merge", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            Path.Combine(projectRoot, "src", "Book1", "common-modules", "Feature.bas"),
            result.StandardError,
            StringComparison.Ordinal);
        var recoveryBytes = File.ReadAllBytes(recoveryFile);
        Assert.Equal(0xff, recoveryBytes[0]);
        Assert.Equal(0xfe, recoveryBytes[1]);
        var recoveryManifest = new JsonProjectManifestStore().Load(recoveryFile);
        Assert.Equal(
            [new InstalledCommonModule("Feature", "Feature.bas", Requested: true, TestOnly: false)],
            recoveryManifest.Documents["Book1"].CommonModules);
        var manifest = new JsonProjectManifestStore().Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        Assert.Empty(manifest.Documents["Book1"].CommonModules);
    }

    [Fact]
    public void InstallationTransactionRequiresManifestMutationCoordinator()
    {
        var atomicWriter = new ProjectManifestAtomicWriter();

        Assert.Throws<ArgumentNullException>(() => new CommonModulesInstallationTransaction(
            new WindowsExactFileSystemObjectOwnershipFactory(),
            new CommonModulesManifestReader(),
            new ProjectManifestEditor(atomicWriter),
            referencePlanner: null,
            manifestMutationCoordinator: null!,
            new FileSystemPathIdentityResolver()));
    }

    [Fact]
    public void AddCannotMutateWhenManifestMutationCoordinatorRejectsTheOperation()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifest(commonRepo, ("Feature.bas", "optional", ""));
        WriteModule(commonRepo, "Feature.bas", "repository feature");
        var coordinator = new RejectingCommonModulesMutationCoordinator();
        var application = CommandLineTestFactory.Create(
            projectRoot,
            projectManifestMutationCoordinator: coordinator);

        var result = application.Run(["common-module", "add", "Feature"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal([ProjectManifestMutationCommand.CommonModuleAdd], coordinator.Commands);
        Assert.Contains("coordinatorRejected", result.StandardError, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(
            projectRoot,
            "src",
            "Book1",
            "common-modules",
            "Feature.bas")));
        var manifest = new JsonProjectManifestStore().Load(
            Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        Assert.Empty(manifest.Documents["Book1"].CommonModules);
    }

    [Fact]
    public void UpdateCannotMutateWhenManifestMutationCoordinatorRejectsTheOperation()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifest(commonRepo, ("Feature.bas", "optional", ""));
        WriteModule(commonRepo, "Feature.bas", "repository feature");
        var projectSource = Path.Combine(projectRoot, "src", "Book1");
        WriteModule(projectSource, "Feature.bas", "local feature");
        var store = new JsonProjectManifestStore();
        var manifestPath = Path.Combine(projectRoot, ProjectManifest.ManifestFileName);
        var manifest = store.Load(manifestPath);
        manifest.Documents["Book1"].CommonModules.Add(Installed("Feature", requested: true));
        store.Save(projectRoot, manifest);
        var coordinator = new RejectingCommonModulesMutationCoordinator();
        var application = CommandLineTestFactory.Create(
            projectRoot,
            projectManifestMutationCoordinator: coordinator);

        var result = application.Run(["common-module", "update"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal([ProjectManifestMutationCommand.CommonModuleUpdate], coordinator.Commands);
        Assert.Contains("coordinatorRejected", result.StandardError, StringComparison.Ordinal);
        Assert.Equal("local feature", ReadModuleBody(Path.Combine(projectSource, "Feature.bas")));
        Assert.Equal(
            [Installed("Feature", requested: true)],
            store.Load(manifestPath).Documents["Book1"].CommonModules);
    }

    [Fact]
    public void UpdateRejectsDocumentSelection()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var application = CommandLineTestFactory.Create(projectRoot);

        var result = application.Run(["common-module", "update", "--document", "Book1"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--document", result.StandardError, StringComparison.Ordinal);
    }

    private static string CreateProjectWithCommonModules(TempDirectory temp, string projectName)
    {
        var commonRepo = temp.CreateDirectory("common_modules_repo");
        var projectRoot = temp.CreateDirectory(projectName);
        Directory.CreateDirectory(Path.Combine(projectRoot, "src", "Book1"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "bin"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "publish"));
        new JsonProjectManifestStore().Save(projectRoot, ProjectManifest.CreateDefault(projectName, "Book1", projectRoot, commonRepo));
        return projectRoot;
    }

    private static InstalledCommonModule Installed(
        string name,
        bool requested,
        string? moduleFile = null,
        bool testOnly = false)
        => new(name, moduleFile ?? $"{name}.bas", requested, testOnly);

    private static void WriteManifest(string repo, params (string ModuleFile, string Categories, string Dependencies)[] rows)
    {
        Directory.CreateDirectory(repo);
        var lines = new List<string>
        {
            "ModuleFile\tCategories\tDependencies\tRequiredReferences"
        };
        lines.AddRange(rows.Select(row => $"{row.ModuleFile}\t{row.Categories}\t{row.Dependencies}\t[]"));
        File.WriteAllText(
            Path.Combine(repo, "common-modules-manifest.tsv"),
            string.Join("\r\n", lines) + "\r\n",
            new UnicodeEncoding(false, true, true));
    }

    private static void WriteManifestWithReferences(
        string repo,
        params (string ModuleFile, string Categories, string Dependencies, string RequiredReferences)[] rows)
    {
        Directory.CreateDirectory(repo);
        var lines = new List<string>
        {
            "ModuleFile\tCategories\tDependencies\tRequiredReferences"
        };
        lines.AddRange(rows.Select(row =>
            $"{row.ModuleFile}\t{row.Categories}\t{row.Dependencies}\t{row.RequiredReferences}"));
        File.WriteAllText(
            Path.Combine(repo, "common-modules-manifest.tsv"),
            string.Join("\r\n", lines) + "\r\n",
            new UnicodeEncoding(false, true, true));
    }

    private static void WriteModule(string directory, string fileName, string content)
    {
        var path = Path.Combine(directory, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var extension = Path.GetExtension(fileName);
        if ((extension.Equals(".bas", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".cls", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".frm", StringComparison.OrdinalIgnoreCase))
            && !content.Contains("Attribute VB_Name", StringComparison.OrdinalIgnoreCase))
        {
            var moduleName = Path.GetFileNameWithoutExtension(fileName);
            var header = extension.Equals(".bas", StringComparison.OrdinalIgnoreCase)
                ? $"Attribute VB_Name = \"{moduleName}\"\r\n"
                : extension.Equals(".cls", StringComparison.OrdinalIgnoreCase)
                    ? "VERSION 1.0 CLASS\r\nBEGIN\r\nEND\r\n"
                        + $"Attribute VB_Name = \"{moduleName}\"\r\n"
                    : "VERSION 5.00\r\n"
                        + $"Attribute VB_Name = \"{moduleName}\"\r\n";
            content = header + TestModuleBodyMarker + content;
        }

        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private static void WriteRawModule(string directory, string fileName, string content)
    {
        var path = Path.Combine(directory, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private static string ReadModuleBody(string path)
    {
        var content = File.ReadAllText(path);
        var markerIndex = content.IndexOf(TestModuleBodyMarker, StringComparison.Ordinal);
        return markerIndex < 0
            ? content
            : content[(markerIndex + TestModuleBodyMarker.Length)..];
    }

    private static void WriteBytes(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private sealed class FailingCommitAtomicWriter(string projectRoot)
        : IProjectManifestAtomicWriter
    {
        public bool FileExistedDuringReplace { get; private set; }

        public void Save(string manifestPath, ProjectManifest manifest)
            => throw new InvalidOperationException("Save was not expected.");

        public void ReplaceExisting(
            string manifestPath,
            ReadOnlyMemory<byte> expectedRawBytes,
            ProjectManifest manifest,
            CancellationToken cancellationToken)
        {
            FileExistedDuringReplace = File.Exists(Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "common-modules",
                "Feature.bas"));
            throw new IOException("manifest save failed");
        }

        public void EstablishNoOp(
            string manifestPath,
            ReadOnlyMemory<byte> expectedRawBytes,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("No-op establishment was not expected.");

        public string CreateRecovery(string root, ProjectManifest manifest)
            => throw new InvalidOperationException("Coordinator recovery was not expected.");
    }

    private sealed class RejectingCommonModulesMutationCoordinator
        : IProjectManifestMutationCoordinator
    {
        public List<ProjectManifestMutationCommand> Commands { get; } = [];

        public Task<ProjectManifestMutationOutcome<TResult>> ExecuteAsync<TResult>(
            string projectRoot,
            ProjectManifestMutationCommand command,
            Func<ProjectManifestMutationSnapshot, ProjectManifestMutationPlan<TResult>> rebase,
            CancellationToken cancellationToken)
        {
            Commands.Add(command);
            throw new ProjectManifestMutationException(
                "coordinatorRejected",
                "The test coordinator rejected the mutation.");
        }
    }

    private sealed class MutatingReferenceResolver(
        Action mutate,
        params ResolvedVbaProjectReference[] references)
        : IVbaProjectReferenceResolver
    {
        private readonly FakeVbaProjectReferenceResolver inner = new(references);

        public VbaProjectReferenceResolutionBatch ResolveAvailable()
            => inner.ResolveAvailable();

        public VbaProjectReferenceResolutionBatch Resolve(IReadOnlyList<string> referenceNames)
        {
            var result = inner.Resolve(referenceNames);
            mutate();
            return result;
        }
    }

    private sealed class MutationWindowState
    {
        public bool IsActive { get; set; }
    }

    private sealed class MutationWindowTrackingCoordinator(MutationWindowState window)
        : IProjectManifestMutationCoordinator
    {
        private readonly ProjectManifestMutationCoordinator inner = new();

        public async Task<ProjectManifestMutationOutcome<TResult>> ExecuteAsync<TResult>(
            string projectRoot,
            ProjectManifestMutationCommand command,
            Func<ProjectManifestMutationSnapshot, ProjectManifestMutationPlan<TResult>> rebase,
            CancellationToken cancellationToken)
        {
            window.IsActive = true;
            try
            {
                return await inner.ExecuteAsync(
                    projectRoot,
                    command,
                    rebase,
                    cancellationToken);
            }
            finally
            {
                window.IsActive = false;
            }
        }
    }

    private sealed class MutationWindowObservingAmbiguityProbe(MutationWindowState window)
        : IVbaProjectReferenceAmbiguityProbe
    {
        public int CallCount { get; private set; }

        public bool ObservedMutationWindow { get; private set; }

        public Task<VbaProjectReferenceResolutionBatch> ResolveAsync(
            VbaProjectReferenceProbeBaseline baseline,
            VbaProjectReferenceResolutionBatch registryResolution,
            CancellationToken cancellationToken)
        {
            CallCount++;
            ObservedMutationWindow |= window.IsActive;
            return Task.FromResult(registryResolution with
            {
                References = registryResolution.References
                    .Select(reference => reference with
                    {
                        Matches = [reference.Matches[0]]
                    })
                    .ToArray()
            });
        }
    }

    private sealed class MutateAfterFirstLoadManifestStore(
        string projectRoot,
        Action<ProjectManifest> mutateLatest)
        : IProjectManifestStore
    {
        private readonly JsonProjectManifestStore inner = new();
        private int loadCount;

        public ProjectManifest Load(string manifestPath)
        {
            var invocationStart = inner.Load(manifestPath);
            if (Interlocked.Increment(ref loadCount) == 1)
            {
                var latest = ProjectManifestEditor.Clone(invocationStart);
                mutateLatest(latest);
                inner.Save(projectRoot, latest);
            }

            return invocationStart;
        }

        public void Save(string root, ProjectManifest manifest)
            => inner.Save(root, manifest);
    }
}
