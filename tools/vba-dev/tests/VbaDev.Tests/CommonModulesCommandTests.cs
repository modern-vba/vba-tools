using System.Text;
using System.Text.Json;
using VbaDev.App.CommonModules;
using VbaDev.App.Projects;
using VbaDev.Composition;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;
using Xunit;

namespace VbaDev.Tests;

public sealed class CommonModulesCommandTests
{
    [Fact]
    public void ManifestReaderRejectsMalformedRecordsAndUnknownDependencies()
    {
        using var temp = TempDirectory.Create();
        var malformedRepo = temp.CreateDirectory("malformed");
        File.WriteAllText(Path.Combine(malformedRepo, "common-modules-manifest.tsv"), "ModuleFile\tCategories\nBroken.bas\truntime-baseline\n", new UTF8Encoding(false));
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

    [Fact]
    public void AddCopiesRequestedModuleAndTransitiveDependenciesInOrder()
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
        var application = ToolingCompositionRoot.CreateCommandLineApplication(projectRoot);

        var result = application.Run(["common-module", "add", "Feature"]);

        Assert.Equal(0, result.ExitCode);
        var sourceSet = Path.Combine(projectRoot, "src", "Book1");
        Assert.Equal("base", File.ReadAllText(Path.Combine(sourceSet, "common-modules", "Base.bas")));
        Assert.Equal("feature", File.ReadAllText(Path.Combine(sourceSet, "common-modules", "Feature.bas")));
        Assert.False(File.Exists(Path.Combine(sourceSet, "Base.bas")));
        Assert.False(File.Exists(Path.Combine(sourceSet, "Feature.bas")));
        var manifest = new JsonProjectManifestStore().Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        Assert.Equal(
            [
                new InstalledCommonModule("Base", Requested: false),
                new InstalledCommonModule("Feature", Requested: true)
            ],
            manifest.Documents["Book1"].CommonModules);
        Assert.True(result.StandardOutput.IndexOf("Copied common-modules/Base.bas", StringComparison.Ordinal) < result.StandardOutput.IndexOf("Copied common-modules/Feature.bas", StringComparison.Ordinal));
    }

    [Fact]
    public void AddFlattensRepositoryDirectoriesWhenCopyingToCommonModulesDirectory()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifest(commonRepo, ("runtime/Feature.bas", "optional", ""));
        WriteModule(commonRepo, Path.Combine("runtime", "Feature.bas"), "feature");
        var application = ToolingCompositionRoot.CreateCommandLineApplication(projectRoot);

        var result = application.Run(["common-module", "add", "Feature"]);

        Assert.Equal(0, result.ExitCode);
        var sourceSet = Path.Combine(projectRoot, "src", "Book1");
        Assert.Equal("feature", File.ReadAllText(Path.Combine(sourceSet, "common-modules", "Feature.bas")));
        Assert.False(File.Exists(Path.Combine(sourceSet, "Feature.bas")));
        Assert.False(File.Exists(Path.Combine(sourceSet, "common-modules", "runtime", "Feature.bas")));
        Assert.Contains("Copied common-modules/Feature.bas", result.StandardOutput, StringComparison.Ordinal);
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
        var application = ToolingCompositionRoot.CreateCommandLineApplication(projectRoot);

        var result = application.Run(["common-module", "add", "Dialog"]);

        Assert.Equal(0, result.ExitCode);
        var sourceSet = Path.Combine(projectRoot, "src", "Book1");
        Assert.Equal("repo form", File.ReadAllText(Path.Combine(sourceSet, "common-modules", "Dialog.frm")));
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
        var application = ToolingCompositionRoot.CreateCommandLineApplication(projectRoot);

        var result = application.Run(["common-module", "add", "Root"]);

        Assert.Equal(0, result.ExitCode);
        var sourceSet = Path.Combine(projectRoot, "src", "Book1");
        Assert.Equal("root", File.ReadAllText(Path.Combine(sourceSet, "common-modules", "Root.bas")));
        Assert.Equal("list", File.ReadAllText(Path.Combine(sourceSet, "common-modules", "ObjectList.cls")));
        Assert.Equal("set", File.ReadAllText(Path.Combine(sourceSet, "common-modules", "ObjectSet.cls")));
        Assert.False(File.Exists(Path.Combine(sourceSet, "Root.bas")));
        Assert.False(File.Exists(Path.Combine(sourceSet, "ObjectList.cls")));
        Assert.False(File.Exists(Path.Combine(sourceSet, "ObjectSet.cls")));
        var manifest = new JsonProjectManifestStore().Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        Assert.Equal(
            [
                new InstalledCommonModule("ObjectSet", Requested: false),
                new InstalledCommonModule("ObjectList", Requested: false),
                new InstalledCommonModule("Root", Requested: true)
            ],
            manifest.Documents["Book1"].CommonModules);
        Assert.Equal(1, manifest.Documents["Book1"].CommonModules.Count(module => module.Name.Equals("ObjectList", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(1, manifest.Documents["Book1"].CommonModules.Count(module => module.Name.Equals("ObjectSet", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void AddUpgradesInstalledDependencyToRequestedWithoutRecopying()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifest(commonRepo, ("Base.bas", "optional", ""));
        WriteModule(commonRepo, "Base.bas", "base v2");
        var sourceSet = Path.Combine(projectRoot, "src", "Book1");
        WriteModule(sourceSet, "Base.bas", "base v1");
        var store = new JsonProjectManifestStore();
        var manifest = store.Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        manifest.Documents["Book1"].CommonModules.Add(new InstalledCommonModule("Base", Requested: false));
        store.Save(projectRoot, manifest);
        var application = ToolingCompositionRoot.CreateCommandLineApplication(projectRoot);

        var result = application.Run(["common-module", "add", "Base"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("base v1", File.ReadAllText(Path.Combine(sourceSet, "Base.bas")));
        var updatedManifest = store.Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        Assert.Equal([new InstalledCommonModule("Base", Requested: true)], updatedManifest.Documents["Book1"].CommonModules);
    }

    [Fact]
    public void AddIsIdempotentAndDoesNotDuplicateOrRecopyInstalledEntries()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifest(commonRepo, ("Feature.bas", "optional", ""));
        WriteModule(commonRepo, "Feature.bas", "feature v1");
        var application = ToolingCompositionRoot.CreateCommandLineApplication(projectRoot);

        Assert.Equal(0, application.Run(["common-module", "add", "Feature"]).ExitCode);
        WriteModule(commonRepo, "Feature.bas", "feature v2");
        var result = application.Run(["common-module", "add", "Feature"]);

        Assert.Equal(0, result.ExitCode);
        var sourceSet = Path.Combine(projectRoot, "src", "Book1");
        Assert.Equal("feature v1", File.ReadAllText(Path.Combine(sourceSet, "common-modules", "Feature.bas")));
        var manifest = new JsonProjectManifestStore().Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        Assert.Equal([new InstalledCommonModule("Feature", Requested: true)], manifest.Documents["Book1"].CommonModules);
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
        var application = ToolingCompositionRoot.CreateCommandLineApplication(projectRoot);

        var conflict = application.Run(["common-module", "add", "Feature"]);

        Assert.Equal(1, conflict.ExitCode);
        Assert.Contains("already exists", conflict.StandardError, StringComparison.Ordinal);
        Assert.Equal("local feature", File.ReadAllText(Path.Combine(sourceSet, "Feature.bas")));
        var afterConflict = new JsonProjectManifestStore().Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        Assert.Empty(afterConflict.Documents["Book1"].CommonModules);

        var forced = application.Run(["common-module", "add", "Feature", "--force"]);

        Assert.Equal(0, forced.ExitCode);
        Assert.Equal("repo feature", File.ReadAllText(Path.Combine(sourceSet, "Feature.bas")));
        var manifest = new JsonProjectManifestStore().Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        Assert.Equal([new InstalledCommonModule("Feature", Requested: true)], manifest.Documents["Book1"].CommonModules);
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
        var application = ToolingCompositionRoot.CreateCommandLineApplication(projectRoot);

        var conflict = application.Run(["common-module", "add", "Feature"]);

        Assert.Equal(1, conflict.ExitCode);
        Assert.Contains("already exists", conflict.StandardError, StringComparison.Ordinal);
        Assert.Equal("local feature", File.ReadAllText(Path.Combine(sourceSet, "nested", "Feature.bas")));
        Assert.False(File.Exists(Path.Combine(sourceSet, "Feature.bas")));

        var forced = application.Run(["common-module", "add", "Feature", "--force"]);

        Assert.Equal(0, forced.ExitCode);
        Assert.Equal("repo feature", File.ReadAllText(Path.Combine(sourceSet, "nested", "Feature.bas")));
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
        var application = ToolingCompositionRoot.CreateCommandLineApplication(projectRoot);

        var result = application.Run(["common-module", "add", "Feature", "--force"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("multiple", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Feature.bas", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("local base", File.ReadAllText(Path.Combine(sourceSet, "Base.bas")));
        Assert.Equal("local feature 1", File.ReadAllText(Path.Combine(sourceSet, "first", "Feature.bas")));
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
        var application = ToolingCompositionRoot.CreateCommandLineApplication(projectRoot);

        Assert.Equal(0, application.Run(["common-module", "add", "Feature"]).ExitCode);
        Assert.Equal(0, application.Run(["common-module", "add", "Feature", "--document", "SecondBook"]).ExitCode);

        var manifest = new JsonProjectManifestStore().Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        Assert.Equal([new InstalledCommonModule("Feature", Requested: true)], manifest.Documents["Book1"].CommonModules);
        Assert.Equal([new InstalledCommonModule("Feature", Requested: true)], manifest.Documents["SecondBook"].CommonModules);
    }

    [Fact]
    public void ListOutputsSelectedDocumentAsTextAndJson()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var store = new JsonProjectManifestStore();
        var manifest = store.Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        manifest.Documents["Book1"].CommonModules.AddRange(
            [
                new InstalledCommonModule("Base", Requested: false),
                new InstalledCommonModule("Feature", Requested: true)
            ]);
        store.Save(projectRoot, manifest);
        var application = ToolingCompositionRoot.CreateCommandLineApplication(projectRoot);

        var text = application.Run(["common-module", "list"]);
        var json = application.Run(["common-module", "list", "--format", "json"]);

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
    public void AddFailsWhenExtensionlessModuleNameIsAmbiguous()
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
        var application = ToolingCompositionRoot.CreateCommandLineApplication(projectRoot);

        var result = application.Run(["common-module", "add", "Foo"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("ambiguous", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddFailsWhenCommonModulesRepositoryIsMissing()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = temp.CreateDirectory("Project");
        Directory.CreateDirectory(Path.Combine(projectRoot, "src", "Book1"));
        var missingRepo = Path.Combine(temp.Path, "missing_common_modules_repo");
        new JsonProjectManifestStore().Save(projectRoot, ProjectManifest.CreateDefault("Project", "Book1", projectRoot, missingRepo));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(projectRoot);

        var result = application.Run(["common-module", "add", "Runtime.bas"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("CommonModulesRepository was not found", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateOverwritesInstalledModulesAddsDependenciesAndKeepsObsoleteFiles()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var store = new JsonProjectManifestStore();
        var manifest = store.Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        manifest.Documents["Book1"].CommonModules.AddRange(
            [
                new InstalledCommonModule("Base", Requested: false),
                new InstalledCommonModule("Feature", Requested: true)
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
        var application = ToolingCompositionRoot.CreateCommandLineApplication(projectRoot);

        var result = application.Run(["common-module", "update"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("base v2", File.ReadAllText(Path.Combine(sourceSet, "common-modules", "Base.bas")));
        Assert.Equal("feature v2", File.ReadAllText(Path.Combine(sourceSet, "Feature.bas")));
        Assert.Equal("unlisted v1", File.ReadAllText(Path.Combine(sourceSet, "Unlisted.bas")));
        Assert.Equal("obsolete", File.ReadAllText(Path.Combine(sourceSet, "Obsolete.bas")));
        Assert.False(File.Exists(Path.Combine(sourceSet, "Base.bas")));
        var updatedManifest = store.Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        Assert.Equal(
            [
                new InstalledCommonModule("Base", Requested: false),
                new InstalledCommonModule("Feature", Requested: true)
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
        manifest.Documents["Book1"].CommonModules.Add(new InstalledCommonModule("Feature", Requested: true));
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
        var application = ToolingCompositionRoot.CreateCommandLineApplication(projectRoot);

        var result = application.Run(["common-module", "update"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("feature v2", File.ReadAllText(Path.Combine(sourceSet, "nested", "Feature.bas")));
        Assert.Equal("base v2", File.ReadAllText(Path.Combine(sourceSet, "common-modules", "Base.bas")));
        Assert.False(File.Exists(Path.Combine(sourceSet, "Base.bas")));
        Assert.False(File.Exists(Path.Combine(sourceSet, "Feature.bas")));
        Assert.Contains("Updated Book1/nested/Feature.bas", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Updated Book1/common-modules/Base.bas", result.StandardOutput, StringComparison.Ordinal);
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
                new InstalledCommonModule("Base", Requested: false),
                new InstalledCommonModule("Feature", Requested: true)
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
        var application = ToolingCompositionRoot.CreateCommandLineApplication(projectRoot);

        var result = application.Run(["common-module", "update"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("multiple", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("base v1", File.ReadAllText(Path.Combine(sourceSet, "Base.bas")));
        Assert.Equal("feature v1 first", File.ReadAllText(Path.Combine(sourceSet, "first", "Feature.bas")));
        var updatedManifest = store.Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        Assert.Equal(
            [
                new InstalledCommonModule("Base", Requested: false),
                new InstalledCommonModule("Feature", Requested: true)
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
        manifest.Documents["Book1"].CommonModules.Add(new InstalledCommonModule("Feature", Requested: true));
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
        var application = ToolingCompositionRoot.CreateCommandLineApplication(projectRoot);

        var result = application.Run(["common-module", "update"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("base v2", File.ReadAllText(Path.Combine(sourceSet, "common-modules", "Base.bas")));
        Assert.Equal("feature v2", File.ReadAllText(Path.Combine(sourceSet, "Feature.bas")));
        Assert.False(File.Exists(Path.Combine(sourceSet, "Base.bas")));
        var updatedManifest = store.Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        Assert.Equal(
            [
                new InstalledCommonModule("Feature", Requested: true),
                new InstalledCommonModule("Base", Requested: false)
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
        manifest.Documents["Book1"].CommonModules.Add(new InstalledCommonModule("Feature", Requested: true));
        manifest.Documents["SecondBook"].CommonModules.Add(new InstalledCommonModule("Feature", Requested: true));
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
        var application = ToolingCompositionRoot.CreateCommandLineApplication(projectRoot);

        var result = application.Run(["common-module", "update"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("base v2", File.ReadAllText(Path.Combine(firstSourceSet, "common-modules", "Base.bas")));
        Assert.Equal("base v2", File.ReadAllText(Path.Combine(secondSourceSet, "common-modules", "Base.bas")));
        Assert.False(File.Exists(Path.Combine(firstSourceSet, "Base.bas")));
        Assert.False(File.Exists(Path.Combine(secondSourceSet, "Base.bas")));
        var updatedManifest = store.Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        Assert.Equal(
            [
                new InstalledCommonModule("Feature", Requested: true),
                new InstalledCommonModule("Base", Requested: false)
            ],
            updatedManifest.Documents["Book1"].CommonModules);
        Assert.Equal(
            [
                new InstalledCommonModule("Feature", Requested: true),
                new InstalledCommonModule("Base", Requested: false)
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
        manifest.Documents["Book1"].CommonModules.Add(new InstalledCommonModule("Feature", Requested: true));
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
        var application = ToolingCompositionRoot.CreateCommandLineApplication(projectRoot);

        Assert.Equal(0, application.Run(["common-module", "update"]).ExitCode);
        var result = application.Run(["common-module", "update"]);

        Assert.Equal(0, result.ExitCode);
        var updatedManifest = store.Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        Assert.Equal(
            [
                new InstalledCommonModule("Feature", Requested: true),
                new InstalledCommonModule("Base", Requested: false)
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
        manifest.Documents["Book1"].CommonModules.Add(new InstalledCommonModule("Feature", Requested: true));
        store.Save(projectRoot, manifest);
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifest(
            commonRepo,
            ("Base.bas", "runtime-baseline", ""),
            ("Feature.bas", "optional", "Base.bas"));
        WriteModule(commonRepo, "Base.bas", "base v2");
        WriteModule(commonRepo, "Feature.bas", "feature v2");
        WriteModule(sourceSet, "Feature.bas", "feature v1");
        var application = ToolingCompositionRoot.CreateCommandLineApplication(projectRoot, new FakeEnvironmentDiagnosticPort());

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
                new InstalledCommonModule("Base", Requested: false),
                new InstalledCommonModule("Feature", Requested: true)
            ]);
        manifest.Documents["SecondBook"].CommonModules.AddRange(
            [
                new InstalledCommonModule("Base", Requested: false),
                new InstalledCommonModule("Feature", Requested: true)
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
        var application = ToolingCompositionRoot.CreateCommandLineApplication(projectRoot);

        var result = application.Run(["common-module", "update"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("base v2", File.ReadAllText(Path.Combine(firstSourceSet, "common-modules", "Base.bas")));
        Assert.Equal("feature v2", File.ReadAllText(Path.Combine(firstSourceSet, "Feature.bas")));
        Assert.Equal("base v2", File.ReadAllText(Path.Combine(secondSourceSet, "common-modules", "Base.bas")));
        Assert.Equal("feature v2", File.ReadAllText(Path.Combine(secondSourceSet, "Feature.bas")));
        Assert.False(File.Exists(Path.Combine(firstSourceSet, "Base.bas")));
        Assert.False(File.Exists(Path.Combine(secondSourceSet, "Base.bas")));
        Assert.Contains("Updated Book1/common-modules/Base.bas", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Updated Book1/Feature.bas", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Updated SecondBook/common-modules/Base.bas", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Updated SecondBook/Feature.bas", result.StandardOutput, StringComparison.Ordinal);
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
        var application = ToolingCompositionRoot.CreateCommandLineApplication(projectRoot);

        var add = application.Run(["common-module", "add", "Dialog", "--force"]);

        Assert.Equal(0, add.ExitCode);
        Assert.Equal("repo form", File.ReadAllText(Path.Combine(sourceSet, "forms", "Dialog.frm")));
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
        var application = ToolingCompositionRoot.CreateCommandLineApplication(projectRoot);

        var result = application.Run(["common-module", "add", "Feature", "--force"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("manifest was not saved", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("source files may have been partially updated", result.StandardError, StringComparison.OrdinalIgnoreCase);
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
        var manifestStore = new RecordingProjectManifestStore { ThrowOnSave = true };
        var application = ToolingCompositionRoot.CreateCommandLineApplication(projectRoot, projectManifestStore: manifestStore);

        var result = application.Run(["common-module", "add", "Feature"]);

        Assert.Equal(1, result.ExitCode);
        Assert.True(manifestStore.FileExistedDuringSave);
        var recoveryFile = Assert.Single(Directory.EnumerateFiles(projectRoot, "project.failed-*.json"));
        Assert.Equal(recoveryFile, result.StandardError.Trim());
        var recoveryBytes = File.ReadAllBytes(recoveryFile);
        Assert.Equal(0xff, recoveryBytes[0]);
        Assert.Equal(0xfe, recoveryBytes[1]);
        Assert.Contains("\"Feature\"", File.ReadAllText(recoveryFile, Encoding.Unicode), StringComparison.Ordinal);
        var manifest = new JsonProjectManifestStore().Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        Assert.Empty(manifest.Documents["Book1"].CommonModules);
    }

    [Fact]
    public void UpdateRejectsDocumentSelection()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var application = ToolingCompositionRoot.CreateCommandLineApplication(projectRoot);

        var result = application.Run(["common-module", "update", "--document", "Book1"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Unknown option '--document'", result.StandardError, StringComparison.Ordinal);
    }

    private static string CreateProjectWithCommonModules(TempDirectory temp, string projectName)
    {
        var commonRepo = temp.CreateDirectory("common_modules_repo");
        var projectRoot = temp.CreateDirectory(projectName);
        Directory.CreateDirectory(Path.Combine(projectRoot, "src", "Book1"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "bin", "Book1"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "publish", "Book1"));
        new JsonProjectManifestStore().Save(projectRoot, ProjectManifest.CreateDefault(projectName, "Book1", projectRoot, commonRepo));
        return projectRoot;
    }

    private static void WriteManifest(string repo, params (string ModuleFile, string Categories, string Dependencies)[] rows)
    {
        Directory.CreateDirectory(repo);
        var lines = new List<string>
        {
            "ModuleFile\tCategories\tDependencies"
        };
        lines.AddRange(rows.Select(row => $"{row.ModuleFile}\t{row.Categories}\t{row.Dependencies}"));
        File.WriteAllText(Path.Combine(repo, "common-modules-manifest.tsv"), string.Join("\n", lines) + "\n", new UTF8Encoding(false));
    }

    private static void WriteModule(string directory, string fileName, string content)
    {
        var path = Path.Combine(directory, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private static void WriteBytes(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private sealed class RecordingProjectManifestStore : IProjectManifestStore
    {
        private readonly JsonProjectManifestStore inner = new();

        public bool ThrowOnSave { get; init; }

        public bool FileExistedDuringSave { get; private set; }

        public ProjectManifest Load(string manifestPath) => inner.Load(manifestPath);

        public void Save(string projectRoot, ProjectManifest manifest)
        {
            FileExistedDuringSave = File.Exists(Path.Combine(projectRoot, "src", "Book1", "common-modules", "Feature.bas"));
            if (ThrowOnSave)
            {
                throw new IOException("manifest save failed");
            }

            inner.Save(projectRoot, manifest);
        }
    }
}
