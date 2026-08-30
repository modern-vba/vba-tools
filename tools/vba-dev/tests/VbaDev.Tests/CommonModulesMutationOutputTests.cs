using System.Text;
using System.Text.Json;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;
using Xunit;

namespace VbaDev.Tests;

public sealed class CommonModulesMutationOutputTests
{
    private const string TestModuleBodyMarker = "' vba-tools mutation output test body\r\n";

    [Fact]
    public void AddJsonReturnsTheCompleteDependencyClosureAndAddedReferences()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProject(temp, "Project");
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifest(
            commonRepo,
            ("Base.bas", "runtime-baseline", "", "[]"),
            ("Feature.bas", "optional", "Base.bas", "[\"Microsoft Scripting Runtime\"]"));
        WriteModule(commonRepo, "Base.bas", "base");
        WriteModule(commonRepo, "Feature.bas", "feature");
        var resolver = new FakeVbaProjectReferenceResolver(
            new ResolvedVbaProjectReference(
                "Microsoft Scripting Runtime",
                "{00000000-0000-0000-0000-000000000001}",
                1,
                0));
        var application = CommandLineTestFactory.Create(
            projectRoot,
            vbaProjectReferenceResolver: resolver);

        var result = application.Run(
            ["common-module", "add", "Feature", "--format", "json"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        using var json = JsonDocument.Parse(result.StandardOutput);
        var root = json.RootElement;
        Assert.Equal("1.0", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("project", root.GetProperty("scope").GetString());
        Assert.Equal(Path.GetFullPath(projectRoot), root.GetProperty("project").GetString());
        Assert.Equal("Book1", root.GetProperty("document").GetString());
        Assert.Equal("add", root.GetProperty("operation").GetString());
        Assert.True(root.GetProperty("complete").GetBoolean());
        Assert.Empty(root.GetProperty("warnings").EnumerateArray());

        var document = Assert.Single(root.GetProperty("documents").EnumerateArray());
        Assert.Equal("Book1", document.GetProperty("document").GetString());
        var modules = document.GetProperty("modules").EnumerateArray().ToArray();
        Assert.Equal(["Base", "Feature"], modules.Select(module => module.GetProperty("name").GetString()));
        Assert.All(modules, module =>
        {
            Assert.Equal("changed", module.GetProperty("status").GetString());
            var change = Assert.Single(module.GetProperty("changes").EnumerateArray());
            Assert.Equal("installed", change.GetProperty("kind").GetString());
            Assert.Equal(
                $"common-modules/{module.GetProperty("moduleFile").GetString()}",
                change.GetProperty("sourceSetRelativePath").GetString());
        });
        Assert.False(modules[0].GetProperty("requested").GetBoolean());
        Assert.True(modules[1].GetProperty("requested").GetBoolean());
        Assert.All(modules, module => Assert.False(module.GetProperty("orphaned").GetBoolean()));

        var referenceChange = Assert.Single(document.GetProperty("referenceChanges").EnumerateArray());
        Assert.Equal("added", referenceChange.GetProperty("kind").GetString());
        Assert.Equal("Microsoft Scripting Runtime", referenceChange.GetProperty("name").GetString());
        Assert.False(referenceChange.GetProperty("requested").GetBoolean());
    }

    [Fact]
    public void AddJsonReportsPromotionAndThenAnOrdinaryNoOpWithoutWarnings()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProject(temp, "Project");
        var store = new JsonProjectManifestStore();
        var manifestPath = Path.Combine(projectRoot, ProjectManifest.ManifestFileName);
        var manifest = store.Load(manifestPath);
        manifest.Documents["Book1"].CommonModules.Add(
            new InstalledCommonModule(
                "Feature",
                "Feature.bas",
                Requested: false,
                TestOnly: false,
                Orphaned: false));
        store.Save(projectRoot, manifest);
        WriteModule(Path.Combine(projectRoot, "src", "Book1"), "Feature.bas", "retained");
        var application = CommandLineTestFactory.Create(projectRoot);

        var promoted = application.Run(
            ["common-module", "add", "Feature", "--format", "json"]);
        var unchanged = application.Run(
            ["common-module", "add", "Feature", "--format", "json"]);

        Assert.Equal(0, promoted.ExitCode);
        Assert.Equal(0, unchanged.ExitCode);
        using var promotedJson = JsonDocument.Parse(promoted.StandardOutput);
        using var unchangedJson = JsonDocument.Parse(unchanged.StandardOutput);
        var promotedModule = Assert.Single(
            Assert.Single(promotedJson.RootElement.GetProperty("documents").EnumerateArray())
                .GetProperty("modules")
                .EnumerateArray());
        Assert.True(promotedModule.GetProperty("requested").GetBoolean());
        Assert.False(promotedModule.GetProperty("orphaned").GetBoolean());
        Assert.Equal("changed", promotedModule.GetProperty("status").GetString());
        Assert.Equal(
            "directRequestPromoted",
            Assert.Single(promotedModule.GetProperty("changes").EnumerateArray())
                .GetProperty("kind")
                .GetString());

        var unchangedRoot = unchangedJson.RootElement;
        Assert.Empty(unchangedRoot.GetProperty("warnings").EnumerateArray());
        var unchangedModule = Assert.Single(
            Assert.Single(unchangedRoot.GetProperty("documents").EnumerateArray())
                .GetProperty("modules")
                .EnumerateArray());
        Assert.Equal("unchanged", unchangedModule.GetProperty("status").GetString());
        Assert.Empty(unchangedModule.GetProperty("changes").EnumerateArray());
    }

    [Fact]
    public void UpdateJsonReturnsEveryTargetInDocumentAndFinalManifestOrder()
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
        manifest.Documents["Book1"].CommonModules.Add(
            new InstalledCommonModule("Feature", "Feature.bas", true, false, false));
        manifest.Documents["SecondBook"].CommonModules.Add(
            new InstalledCommonModule("Retained", "Retained.cls", false, true, false));
        new JsonProjectManifestStore().Save(projectRoot, manifest);
        WriteManifest(
            commonRepo,
            ("Base.bas", "runtime-baseline", "", "[]"),
            ("Feature.bas", "optional", "Base.bas", "[]"));
        WriteModule(commonRepo, "Base.bas", "base v2");
        WriteModule(commonRepo, "Feature.bas", "feature stable");
        WriteModule(Path.Combine(projectRoot, "src", "Book1"), "Feature.bas", "feature stable");
        WriteModule(Path.Combine(projectRoot, "src", "SecondBook"), "Retained.cls", "retained");
        var application = CommandLineTestFactory.Create(projectRoot);

        var result = application.Run(
            ["common-module", "update", "--format", "json"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        using var json = JsonDocument.Parse(result.StandardOutput);
        var root = json.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("document").ValueKind);
        Assert.Equal("update", root.GetProperty("operation").GetString());
        var documents = root.GetProperty("documents").EnumerateArray().ToArray();
        Assert.Equal(["Book1", "SecondBook"], documents.Select(document => document.GetProperty("document").GetString()));

        var firstModules = documents[0].GetProperty("modules").EnumerateArray().ToArray();
        Assert.Equal(["Feature", "Base"], firstModules.Select(module => module.GetProperty("name").GetString()));
        Assert.Equal("unchanged", firstModules[0].GetProperty("status").GetString());
        Assert.Equal(
            "installed",
            Assert.Single(firstModules[1].GetProperty("changes").EnumerateArray())
                .GetProperty("kind")
                .GetString());

        var retained = Assert.Single(documents[1].GetProperty("modules").EnumerateArray());
        Assert.True(retained.GetProperty("orphaned").GetBoolean());
        Assert.Equal(
            "orphanedChanged",
            Assert.Single(retained.GetProperty("changes").EnumerateArray())
                .GetProperty("kind")
                .GetString());
        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal("orphanedCommonModulesRetained", warning.GetProperty("code").GetString());
    }

    [Fact]
    public void UpdateJsonReturnsAnEmptyDocumentSetForAProjectWithoutInstalledTargets()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProject(temp, "Project");
        var application = CommandLineTestFactory.Create(projectRoot);

        var result = application.Run(
            ["common-module", "update", "--format", "json"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        using var json = JsonDocument.Parse(result.StandardOutput);
        Assert.Empty(json.RootElement.GetProperty("documents").EnumerateArray());
        Assert.Empty(json.RootElement.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public void UpdateTextWithoutInstalledTargetsReturnsNoChangeAndZeroCounts()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProject(temp, "Project");
        var application = CommandLineTestFactory.Create(projectRoot);

        var result = application.Run(["common-module", "update"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        Assert.Equal(
            "No installed CommonModules entries were found." + Environment.NewLine
            + "No CommonModules changes." + Environment.NewLine
            + "Installed CommonModules: 0" + Environment.NewLine
            + "Source-updated CommonModules: 0" + Environment.NewLine
            + "Direct-request promotions: 0" + Environment.NewLine
            + "Metadata-updated CommonModules: 0" + Environment.NewLine
            + "Added required references: 0" + Environment.NewLine
            + "Unchanged CommonModules: 0" + Environment.NewLine,
            result.StandardOutput);
    }

    [Fact]
    public void UpdateJsonOrdersEveryExistingChangeAndUsesFinalMetadataPayloads()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProject(temp, "Project");
        var store = new JsonProjectManifestStore();
        var manifestPath = Path.Combine(projectRoot, ProjectManifest.ManifestFileName);
        var manifest = store.Load(manifestPath);
        manifest.Documents["Book1"].CommonModules.Add(
            new InstalledCommonModule(
                "Feature",
                "Feature.bas",
                Requested: true,
                TestOnly: false,
                Orphaned: true));
        store.Save(projectRoot, manifest);
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifest(commonRepo, ("Feature.bas", "test-double", "", "[]"));
        WriteModule(commonRepo, "Feature.bas", "feature v2");
        WriteModule(Path.Combine(projectRoot, "src", "Book1"), "Feature.bas", "feature v1");
        var application = CommandLineTestFactory.Create(projectRoot);

        var result = application.Run(
            ["common-module", "update", "--format", "json"]);

        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.StandardOutput);
        var module = Assert.Single(
            Assert.Single(json.RootElement.GetProperty("documents").EnumerateArray())
                .GetProperty("modules")
                .EnumerateArray());
        Assert.True(module.GetProperty("testOnly").GetBoolean());
        Assert.False(module.GetProperty("orphaned").GetBoolean());
        var changes = module.GetProperty("changes").EnumerateArray().ToArray();
        Assert.Equal(
            ["sourceUpdated", "testOnlyChanged", "orphanedChanged"],
            changes.Select(change => change.GetProperty("kind").GetString()));
        Assert.Equal("Feature.bas", changes[0].GetProperty("sourceSetRelativePath").GetString());
        Assert.True(changes[1].GetProperty("testOnly").GetBoolean());
        Assert.False(changes[2].GetProperty("orphaned").GetBoolean());
        Assert.False(changes[0].TryGetProperty("testOnly", out _));
        Assert.False(changes[1].TryGetProperty("sourceSetRelativePath", out _));
    }

    [Fact]
    public void UpdateJsonReportsCanonicalIdentityRefreshWhenSourceBytesAlreadyMatch()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProject(temp, "Project");
        var store = new JsonProjectManifestStore();
        var manifestPath = Path.Combine(projectRoot, ProjectManifest.ManifestFileName);
        var manifest = store.Load(manifestPath);
        manifest.Documents["Book1"].CommonModules.Add(
            new InstalledCommonModule("feature", "Feature.bas", true, false, false));
        store.Save(projectRoot, manifest);
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifest(commonRepo, ("Feature.bas", "optional", "", "[]"));
        WriteModule(commonRepo, "Feature.bas", "identical");
        WriteModule(Path.Combine(projectRoot, "src", "Book1"), "Feature.bas", "identical");
        var application = CommandLineTestFactory.Create(projectRoot);

        var result = application.Run(
            ["common-module", "update", "--format", "json"]);

        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.StandardOutput);
        var module = Assert.Single(
            Assert.Single(json.RootElement.GetProperty("documents").EnumerateArray())
                .GetProperty("modules")
                .EnumerateArray());
        Assert.Equal("Feature", module.GetProperty("name").GetString());
        Assert.Equal("changed", module.GetProperty("status").GetString());
        var change = Assert.Single(module.GetProperty("changes").EnumerateArray());
        Assert.Equal("sourceUpdated", change.GetProperty("kind").GetString());
        Assert.Equal("Feature.bas", change.GetProperty("sourceSetRelativePath").GetString());
    }

    [Fact]
    public void JsonSuccessContainsStableWarningsInCanonicalOrderAndNoWarningStderr()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProject(temp, "Project");
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifest(commonRepo, ("Feature.bas", "optional", "", "[]"));
        WriteModule(commonRepo, "Feature.bas", "feature");
        var coordinator = new WarningMutationCoordinator(
            new ProjectManifestMutationWarning(
                "leaseMarkerCleanupFailed",
                "The released lease marker was retained."),
            new ProjectManifestMutationWarning(
                "cancellationDeferred",
                "Cancellation was deferred through commit."));
        var application = CommandLineTestFactory.Create(
            projectRoot,
            projectManifestMutationCoordinator: coordinator);

        var result = application.Run(
            ["common-module", "add", "Feature", "--format", "json"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        using var json = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(
            ["cancellationDeferred", "leaseMarkerCleanupFailed"],
            json.RootElement.GetProperty("warnings").EnumerateArray()
                .Select(warning => warning.GetProperty("code").GetString()));
    }

    [Fact]
    public void UnsupportedProducerWarningFailsWithoutPartialJsonSuccess()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProject(temp, "Project");
        var store = new JsonProjectManifestStore();
        var manifestPath = Path.Combine(projectRoot, ProjectManifest.ManifestFileName);
        var manifest = store.Load(manifestPath);
        manifest.Documents["Book1"].CommonModules.Add(
            new InstalledCommonModule("Feature", "Feature.bas", true, false, false));
        store.Save(projectRoot, manifest);
        WriteModule(Path.Combine(projectRoot, "src", "Book1"), "Feature.bas", "feature");
        var application = CommandLineTestFactory.Create(
            projectRoot,
            projectManifestMutationCoordinator: new WarningMutationCoordinator(
                new ProjectManifestMutationWarning("futureWarning", "Future warning.")));

        var result = application.Run(
            ["common-module", "add", "Feature", "--format", "json"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("unsupported warning code", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonFailureDoesNotEmitAPartialSuccessObject()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProject(temp, "Project");
        var application = CommandLineTestFactory.Create(projectRoot);

        var result = application.Run(
            ["common-module", "add", "Unknown", "--format", "json"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.NotEmpty(result.StandardError);
    }

    private static string CreateProject(TempDirectory temp, string projectName)
    {
        var commonRepo = temp.CreateDirectory("common_modules_repo");
        var projectRoot = temp.CreateDirectory(projectName);
        Directory.CreateDirectory(Path.Combine(projectRoot, "src", "Book1"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "bin"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "publish"));
        new JsonProjectManifestStore().Save(
            projectRoot,
            ProjectManifest.CreateDefault(projectName, "Book1", projectRoot, commonRepo));
        return projectRoot;
    }

    private static void WriteManifest(
        string repository,
        params (string ModuleFile, string Categories, string Dependencies, string RequiredReferences)[] rows)
    {
        Directory.CreateDirectory(repository);
        var lines = new List<string>
        {
            "ModuleFile\tCategories\tDependencies\tRequiredReferences"
        };
        lines.AddRange(rows.Select(row =>
            $"{row.ModuleFile}\t{row.Categories}\t{row.Dependencies}\t{row.RequiredReferences}"));
        File.WriteAllText(
            Path.Combine(repository, "common-modules-manifest.tsv"),
            string.Join("\r\n", lines) + "\r\n",
            new UnicodeEncoding(false, true, true));
    }

    private static void WriteModule(string directory, string fileName, string content)
    {
        var path = Path.Combine(directory, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var extension = Path.GetExtension(fileName);
        var moduleName = Path.GetFileNameWithoutExtension(fileName);
        var header = extension.Equals(".bas", StringComparison.OrdinalIgnoreCase)
            ? $"Attribute VB_Name = \"{moduleName}\"\r\n"
            : "VERSION 1.0 CLASS\r\nBEGIN\r\nEND\r\n"
                + $"Attribute VB_Name = \"{moduleName}\"\r\n";
        File.WriteAllText(path, header + TestModuleBodyMarker + content, new UTF8Encoding(false));
    }

    private sealed class WarningMutationCoordinator(
        params ProjectManifestMutationWarning[] warnings)
        : IProjectManifestMutationCoordinator
    {
        private readonly ProjectManifestMutationCoordinator inner = new();

        public async Task<ProjectManifestMutationOutcome<TResult>> ExecuteAsync<TResult>(
            string projectRoot,
            ProjectManifestMutationCommand command,
            Func<ProjectManifestMutationSnapshot, ProjectManifestMutationPlan<TResult>> rebase,
            CancellationToken cancellationToken)
        {
            var outcome = await inner.ExecuteAsync(
                projectRoot,
                command,
                rebase,
                cancellationToken);
            return outcome with { Warnings = outcome.Warnings.Concat(warnings).ToArray() };
        }
    }
}
