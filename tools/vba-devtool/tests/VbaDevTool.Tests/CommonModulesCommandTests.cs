using System.Text;
using System.Text.Json;
using VbaDevTools.App.CommonModules;
using VbaDevTools.Composition;
using VbaDevTools.Domain;
using VbaDevTools.Infrastructure.Projects;
using Xunit;

namespace VbaDevTools.Tests;

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
        Assert.Equal("base", File.ReadAllText(Path.Combine(sourceSet, "Base.bas")));
        Assert.Equal("feature", File.ReadAllText(Path.Combine(sourceSet, "Feature.bas")));
        var manifest = new JsonProjectManifestStore().Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        Assert.Equal(
            [
                new InstalledCommonModule("Base", Requested: false),
                new InstalledCommonModule("Feature", Requested: true)
            ],
            manifest.Documents["Book1"].CommonModules);
        Assert.True(result.StandardOutput.IndexOf("Copied Base.bas", StringComparison.Ordinal) < result.StandardOutput.IndexOf("Copied Feature.bas", StringComparison.Ordinal));
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
        Assert.Equal("feature v1", File.ReadAllText(Path.Combine(sourceSet, "Feature.bas")));
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
        Assert.Equal("base v2", File.ReadAllText(Path.Combine(sourceSet, "Base.bas")));
        Assert.Equal("feature v2", File.ReadAllText(Path.Combine(sourceSet, "Feature.bas")));
        Assert.Equal("unlisted v1", File.ReadAllText(Path.Combine(sourceSet, "Unlisted.bas")));
        Assert.Equal("obsolete", File.ReadAllText(Path.Combine(sourceSet, "Obsolete.bas")));
        var updatedManifest = store.Load(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        Assert.Equal(
            [
                new InstalledCommonModule("Base", Requested: false),
                new InstalledCommonModule("Feature", Requested: true)
            ],
            updatedManifest.Documents["Book1"].CommonModules);
        Assert.Contains("Updated Book1/Feature.bas", result.StandardOutput, StringComparison.Ordinal);
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
        Assert.Equal("base v2", File.ReadAllText(Path.Combine(sourceSet, "Base.bas")));
        Assert.Equal("feature v2", File.ReadAllText(Path.Combine(sourceSet, "Feature.bas")));
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
        Assert.Equal("base v2", File.ReadAllText(Path.Combine(firstSourceSet, "Base.bas")));
        Assert.Equal("base v2", File.ReadAllText(Path.Combine(secondSourceSet, "Base.bas")));
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
        Assert.Equal("base v2", File.ReadAllText(Path.Combine(firstSourceSet, "Base.bas")));
        Assert.Equal("feature v2", File.ReadAllText(Path.Combine(firstSourceSet, "Feature.bas")));
        Assert.Equal("base v2", File.ReadAllText(Path.Combine(secondSourceSet, "Base.bas")));
        Assert.Equal("feature v2", File.ReadAllText(Path.Combine(secondSourceSet, "Feature.bas")));
        Assert.Contains("Updated Book1/Feature.bas", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Updated SecondBook/Feature.bas", result.StandardOutput, StringComparison.Ordinal);
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
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), content, new UTF8Encoding(false));
    }
}
