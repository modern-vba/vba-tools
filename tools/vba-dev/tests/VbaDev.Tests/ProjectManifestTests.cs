using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VbaDev.App.Projects;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;
using Xunit;

namespace VbaDev.Tests;

public sealed class ProjectManifestTests
{
    [Fact]
    public void LegacyTestCommandDefaultsConstructorAndDeconstructRemainAvailable()
    {
        var constructor = typeof(TestCommandDefaults).GetConstructor([typeof(string)]);
        var deconstruct = typeof(TestCommandDefaults).GetMethod(
            nameof(TestCommandDefaults.Deconstruct),
            [typeof(string).MakeByRefType()]);

        Assert.NotNull(constructor);
        Assert.NotNull(deconstruct);
        var defaults = Assert.IsType<TestCommandDefaults>(constructor.Invoke(["ndjson"]));
        object?[] values = [null];
        deconstruct.Invoke(defaults, values);
        Assert.Equal("ndjson", values[0]);
        Assert.Null(defaults.ExecutionTimeoutSeconds);
    }

    [Fact]
    public void SaveWritesUtf16LeBomAndRelativeCommonModulesRepository()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = temp.CreateDirectory("SampleProject");
        var commonModulesRepository = Path.GetFullPath(Path.Combine(projectRoot, "..", "common_modules_repo"));
        var manifest = ProjectManifest.CreateDefault(
            "SampleProject",
            "Book1",
            projectRoot,
            commonModulesRepository,
            [
                new InstalledCommonModule("Runtime", "Runtime.bas", Requested: true, TestOnly: false),
                new InstalledCommonModule("CommonDependency", "CommonDependency.cls", Requested: false, TestOnly: true)
            ],
            [new VbaProjectReference("Microsoft Scripting Runtime")]);
        var store = new JsonProjectManifestStore();

        store.Save(projectRoot, manifest);

        var manifestPath = Path.Combine(projectRoot, ProjectManifest.ManifestFileName);
        var bytes = File.ReadAllBytes(manifestPath);
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xFE, bytes[1]);

        using var document = JsonDocument.Parse(Encoding.Unicode.GetString(bytes[2..]));
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("../common_modules_repo", document.RootElement.GetProperty("commonModulesRepository").GetString());
        var book = document.RootElement.GetProperty("documents").GetProperty("Book1");
        var commonModules = book.GetProperty("commonModules");
        Assert.Equal("Runtime", commonModules[0].GetProperty("name").GetString());
        Assert.Equal("Runtime.bas", commonModules[0].GetProperty("moduleFile").GetString());
        Assert.True(commonModules[0].GetProperty("requested").GetBoolean());
        Assert.False(commonModules[0].GetProperty("testOnly").GetBoolean());
        Assert.Equal("CommonDependency", commonModules[1].GetProperty("name").GetString());
        Assert.Equal("CommonDependency.cls", commonModules[1].GetProperty("moduleFile").GetString());
        Assert.False(commonModules[1].GetProperty("requested").GetBoolean());
        Assert.True(commonModules[1].GetProperty("testOnly").GetBoolean());
        Assert.Equal("Microsoft Scripting Runtime", book.GetProperty("references")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void SaveWritesCanonicalBytesAndOmitsAbsentOptionalState()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = temp.CreateDirectory("CanonicalProject");
        var manifest = ProjectManifest.CreateDefault(
            "CanonicalProject",
            "Book1",
            projectRoot,
            null) with
        {
            CommandDefaults = null
        };
        var store = new JsonProjectManifestStore();

        store.Save(projectRoot, manifest);

        var bytes = File.ReadAllBytes(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xFE, bytes[1]);
        var actual = Encoding.Unicode.GetString(bytes[2..]);
        var expected = """
        {
          "schemaVersion": 1,
          "projectName": "CanonicalProject",
          "primaryDocument": "Book1",
          "documents": {
            "Book1": {
              "kind": "excel",
              "sourcePath": "src/Book1",
              "templatePath": "src/Book1/Book1.xlsm",
              "binPath": "bin/Book1.xlsm",
              "publishPath": "publish/Book1.xlsm",
              "commonModules": [],
              "references": []
            }
          }
        }
        """.ReplaceLineEndings("\r\n") + "\r\n";

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SaveWritesCompleteCanonicalBytesInSchemaPropertyOrder()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = temp.CreateDirectory("CanonicalProject");
        var store = new JsonProjectManifestStore();

        store.Save(projectRoot, ProjectManifestTestData.FullManifest(projectRoot));

        var actual = File.ReadAllBytes(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        Assert.Equal(
            ProjectManifestTestData.ExpectedFullCanonicalBytes(),
            actual);
    }

    [Fact]
    public void ReorderedMinifiedUtf8InputRoundTripsToStableCanonicalBytes()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = temp.CreateDirectory("CanonicalProject");
        var manifestPath = Path.Combine(projectRoot, ProjectManifest.ManifestFileName);
        var nonCanonicalJson = """
        {"commandDefaults":{"excelAutomation":{"workbookSaveTimeoutSeconds":180,"workbookOpenTimeoutSeconds":120},"test":{"executionTimeoutSeconds":240,"format":"ndjson"}},"commonModulesRepository":"../common_modules_repo","documents":{"Book1":{"references":[{"requested":true,"name":"Microsoft Scripting Runtime"}],"commonModules":[{"testOnly":false,"requested":true,"moduleFile":"Runtime.bas","name":"Runtime"}],"publishPath":"publish/Book1.xlsm","binPath":"bin/Book1.xlsm","templatePath":"src/Book1/Book1.xlsm","sourcePath":"src/Book1","kind":"excel"}},"primaryDocument":"Book1","projectName":"CanonicalProject","schemaVersion":1}
        """;
        File.WriteAllText(manifestPath, nonCanonicalJson, new UTF8Encoding(false));
        var store = new JsonProjectManifestStore();

        var manifest = store.Load(manifestPath);
        store.Save(projectRoot, manifest);
        var firstCanonicalBytes = File.ReadAllBytes(manifestPath);
        var reloadedManifest = store.Load(manifestPath);
        store.Save(projectRoot, reloadedManifest);
        var secondCanonicalBytes = File.ReadAllBytes(manifestPath);

        Assert.Equal(ProjectManifestTestData.ExpectedFullCanonicalBytes(), firstCanonicalBytes);
        Assert.Equal(firstCanonicalBytes, secondCanonicalBytes);
    }

    [Fact]
    public void SaveAtomicallyReplacesTheManifestWithoutOpeningTheDestinationForWrite()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = temp.CreateDirectory("AtomicProject");
        var store = new JsonProjectManifestStore();
        var initial = ProjectManifest.CreateDefault(
            "Initial Project",
            "Book1",
            projectRoot,
            null);
        store.Save(projectRoot, initial);
        var manifestPath = Path.Combine(projectRoot, ProjectManifest.ManifestFileName);
        var initialBytes = File.ReadAllBytes(manifestPath);
        using var oldVersionReader = new FileStream(
            manifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete);

        store.Save(projectRoot, initial with { ProjectName = "Updated Project" });

        var bytesVisibleThroughOldHandle = new byte[initialBytes.Length];
        oldVersionReader.ReadExactly(bytesVisibleThroughOldHandle);
        Assert.Equal(initialBytes, bytesVisibleThroughOldHandle);
        Assert.Equal("Updated Project", store.Load(manifestPath).ProjectName);
    }

    [Fact]
    public void TemporaryWriteFailurePreservesThePriorManifestAndRemovesPartialStaging()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = temp.CreateDirectory("AtomicWriteFailure");
        var manifestPath = Path.Combine(projectRoot, ProjectManifest.ManifestFileName);
        var initial = ProjectManifest.CreateDefault(
            "Initial Project",
            "Book1",
            projectRoot,
            null);
        new JsonProjectManifestStore().Save(projectRoot, initial);
        var priorBytes = File.ReadAllBytes(manifestPath);
        var failingWriter = new ProjectManifestAtomicWriter(
            TimeProvider.System,
            (stream, bytes) =>
            {
                stream.Write(bytes.Span[..1]);
                stream.Flush();
                throw new IOException("simulated stable-write failure");
            });
        var store = new JsonProjectManifestStore(failingWriter);

        var error = Assert.Throws<IOException>(() => store.Save(
            projectRoot,
            initial with { ProjectName = "Updated Project" }));

        Assert.Contains("simulated stable-write failure", error.Message, StringComparison.Ordinal);
        Assert.Equal(priorBytes, File.ReadAllBytes(manifestPath));
        Assert.Empty(Directory.EnumerateFiles(
            projectRoot,
            ProjectManifest.ManifestFileName + ".vba-dev.*.tmp"));
    }

    [Fact]
    public void OverlappingSaveIsRejectedBeforeReplacingTheExistingManifest()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = temp.CreateDirectory("Project");
        var manifestPath = Path.Combine(projectRoot, ProjectManifest.ManifestFileName);
        var store = new JsonProjectManifestStore();
        store.Save(
            projectRoot,
            ProjectManifest.CreateDefault("Project", "Book1", projectRoot, null));
        var priorBytes = File.ReadAllBytes(manifestPath);
        var invalidManifest = ProjectManifestTestData.TwoDocumentManifest(projectRoot);
        invalidManifest.Documents["SecondBook"] = invalidManifest.Documents["SecondBook"] with
        {
            SourcePath = "src/Book1"
        };

        var error = Assert.Throws<ProjectManifestException>(() =>
            store.Save(projectRoot, invalidManifest));

        Assert.Contains("Book1", error.Message, StringComparison.Ordinal);
        Assert.Contains("SecondBook", error.Message, StringComparison.Ordinal);
        Assert.Equal(priorBytes, File.ReadAllBytes(manifestPath));
    }

    [Fact]
    public void DefaultManifestOmitsRedundantBuiltInCommandDefaults()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = temp.CreateDirectory("DefaultProject");
        var manifest = ProjectManifest.CreateDefault(
            "DefaultProject",
            "Book1",
            projectRoot,
            null);
        var store = new JsonProjectManifestStore();

        store.Save(projectRoot, manifest);

        var bytes = File.ReadAllBytes(Path.Combine(projectRoot, ProjectManifest.ManifestFileName));
        using var document = JsonDocument.Parse(Encoding.Unicode.GetString(bytes[2..]));
        Assert.False(document.RootElement.TryGetProperty("commandDefaults", out _));
    }

    [Fact]
    public void LoadAcceptsUtf16LeBomAndUtf8Inputs()
    {
        using var temp = TempDirectory.Create();
        var utf16Root = temp.CreateDirectory("Utf16Project");
        var utf8Root = temp.CreateDirectory("Utf8Project");
        var store = new JsonProjectManifestStore();
        var manifest = ProjectManifest.CreateDefault("SampleProject", "Book1", utf16Root, null);
        store.Save(utf16Root, manifest);
        File.WriteAllText(Path.Combine(utf8Root, ProjectManifest.ManifestFileName), ProjectManifestTestData.ValidJson("Utf8Project"), new UTF8Encoding(false));

        var utf16Manifest = store.Load(Path.Combine(utf16Root, ProjectManifest.ManifestFileName));
        var utf8Manifest = store.Load(Path.Combine(utf8Root, ProjectManifest.ManifestFileName));

        Assert.Equal("SampleProject", utf16Manifest.ProjectName);
        Assert.Equal("Utf8Project", utf8Manifest.ProjectName);
        Assert.Empty(utf8Manifest.Documents["Book1"].CommonModules);
        Assert.Empty(utf8Manifest.Documents["Book1"].References);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("moduleFile")]
    [InlineData("requested")]
    [InlineData("testOnly")]
    public void InstalledCommonModuleRequiresCompleteBaseMetadata(string propertyToRemove)
    {
        var json = $$"""
        {
          "schemaVersion": 1,
          "projectName": "Project",
          "primaryDocument": "Book1",
          "documents": {
            "Book1": {
              "kind": "excel",
              "sourcePath": "src/Book1",
              "templatePath": "src/Book1/Book1.xlsm",
              "binPath": "bin/Book1.xlsm",
              "publishPath": "publish/Book1.xlsm",
              "commonModules": [
                {
                  "name": "Runtime",
                  "moduleFile": "Runtime.bas",
                  "requested": true,
                  "testOnly": false
                }
              ],
              "references": []
            }
          }
        }
        """;
        var node = JsonNode.Parse(json)!;
        var installed = node["documents"]!["Book1"]!["commonModules"]![0]!.AsObject();
        installed.Remove(propertyToRemove);

        var ex = Assert.Throws<VbaProjectManifestException>(
            () => ProjectManifestReader.Parse(node.ToJsonString(), "vba-project.json"));

        Assert.Contains(propertyToRemove, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstalledCommonModuleRejectsNullArrayEntryAsManifestError()
    {
        var json = """
        {
          "schemaVersion": 1,
          "projectName": "Project",
          "primaryDocument": "Book1",
          "documents": {
            "Book1": {
              "kind": "excel",
              "sourcePath": "src/Book1",
              "templatePath": "src/Book1/Book1.xlsm",
              "binPath": "bin/Book1.xlsm",
              "publishPath": "publish/Book1.xlsm",
              "commonModules": [null],
              "references": []
            }
          }
        }
        """;

        var ex = Assert.Throws<VbaProjectManifestException>(
            () => ProjectManifestReader.Parse(json, "vba-project.json"));

        Assert.Contains("null CommonModules entry", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProjectManifestEditorWritesRecoveryFileWhenSaveFails()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifestTestData.FullManifest(root);
        var editor = new ProjectManifestEditor(
            new FailingProjectManifestStore(),
            new ProjectManifestAtomicWriter());

        var error = Assert.Throws<ProjectManifestEditException>(() => editor.SaveWithRecovery(root, manifest));

        var recoveryFile = Assert.Single(Directory.EnumerateFiles(root, "vba-project.failed-*.json"));
        Assert.Equal(recoveryFile, error.Message);
        Assert.Matches(
            @"^vba-project\.failed-\d{8}T\d{6}\.\d{3}Z-[0-9a-f]{32}\.json$",
            Path.GetFileName(recoveryFile));
        var recoveryBytes = File.ReadAllBytes(recoveryFile);
        Assert.Equal(0xff, recoveryBytes[0]);
        Assert.Equal(0xfe, recoveryBytes[1]);
        Assert.Equal(
            ProjectManifestTestData.ExpectedFullCanonicalBytes(),
            recoveryBytes);
    }

    [Fact]
    public void ProjectManifestEditorReportsOriginalAndRecoveryValidationFailures()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifestTestData.FullManifest(root);
        manifest.Documents["Book1"].References.Add(
            new VbaProjectReference(VbaProjectReferenceName.StandardLibrary));
        var editor = new ProjectManifestEditor(
            new FailingProjectManifestStore(),
            new ProjectManifestAtomicWriter());

        var error = Assert.Throws<ProjectManifestEditException>(
            () => editor.SaveWithRecovery(root, manifest));

        Assert.IsType<IOException>(error.InnerException);
        Assert.Contains("manifest save failed", error.Message, StringComparison.Ordinal);
        Assert.Contains("recovery file could not be written", error.Message, StringComparison.Ordinal);
        Assert.Contains(
            VbaProjectReferenceName.StandardLibrary,
            error.Message,
            StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(root, "vba-project.failed-*.json"));
    }

    [Fact]
    public void UnknownSchemaVersionIsRejectedAsUsageError()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("BadSchema");
        File.WriteAllText(Path.Combine(root, ProjectManifest.ManifestFileName), ProjectManifestTestData.ValidJson("BadSchema").Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal), new UTF8Encoding(false));
        var store = new JsonProjectManifestStore();

        var ex = Assert.Throws<ProjectManifestException>(() => store.Load(Path.Combine(root, ProjectManifest.ManifestFileName)));

        Assert.Contains("schemaVersion", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolverUsesExplicitProjectAndDocumentOptions()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var nested = temp.CreateDirectory("OtherLocation");
        var store = new JsonProjectManifestStore();
        store.Save(root, ProjectManifestTestData.TwoDocumentManifest(root));
        var resolver = new ProjectContextResolver(store);

        var context = resolver.Resolve(new ProjectResolutionRequest(ProjectRoot: root, DocumentName: "SecondBook", StartDirectory: nested));

        Assert.Equal(root, context.ProjectRoot);
        Assert.Equal("SecondBook", context.DocumentName);
        Assert.Equal(Path.Combine(root, "src", "SecondBook"), context.DocumentSourceSetPath);
        Assert.Equal(Path.Combine(root, "bin", "SecondBook.xlsm"), context.BinDocumentPath);
        Assert.Equal(Path.Combine(root, "publish", "SecondBook.xlsm"), context.PublishDocumentPath);
    }

    [Fact]
    public void ResolverHonorsManifestDefinedNestedOutputPathsForExistingProjects()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var store = new JsonProjectManifestStore();
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        manifest.Documents["Book1"] = manifest.Documents["Book1"] with
        {
            BinPath = "bin/Book1/Book1.xlsm",
            PublishPath = "publish/Book1/Book1.xlsm"
        };
        store.Save(root, manifest);
        var resolver = new ProjectContextResolver(store);

        var context = resolver.Resolve(new ProjectResolutionRequest(ProjectRoot: root, DocumentName: null, StartDirectory: root));

        Assert.Equal(Path.Combine(root, "bin", "Book1", "Book1.xlsm"), context.BinDocumentPath);
        Assert.Equal(Path.Combine(root, "publish", "Book1", "Book1.xlsm"), context.PublishDocumentPath);
    }

    [Fact]
    public void ResolverWalksUpToNearestProjectManifestAndUsesPrimaryDocument()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var startDirectory = Directory.CreateDirectory(Path.Combine(root, "src", "Book1", "nested")).FullName;
        var store = new JsonProjectManifestStore();
        store.Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var resolver = new ProjectContextResolver(store);

        var context = resolver.Resolve(new ProjectResolutionRequest(ProjectRoot: null, DocumentName: null, StartDirectory: startDirectory));

        Assert.Equal(root, context.ProjectRoot);
        Assert.Equal("Book1", context.DocumentName);
        Assert.Equal(Path.Combine(root, "src", "Book1"), context.DocumentSourceSetPath);
    }

    [Fact]
    public void CommandDefaultResolutionPrefersOptionThenManifestThenBuiltInDefault()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null) with
        {
            CommandDefaults = new CommandDefaults(Test: new TestCommandDefaults(Format: "text"))
        };

        Assert.Equal("ndjson", CommandDefaultResolver.ResolveTestFormat(manifest, "ndjson"));
        Assert.Equal("text", CommandDefaultResolver.ResolveTestFormat(manifest, null));
        Assert.Equal("text", CommandDefaultResolver.ResolveTestFormat(ProjectManifest.CreateDefault("Project", "Book1", root, null), null));

        var unsupportedManifest = manifest with
        {
            CommandDefaults = new CommandDefaults(Test: new TestCommandDefaults(Format: "json"))
        };
        var ex = Assert.Throws<ProjectManifestException>(() => CommandDefaultResolver.ResolveTestFormat(unsupportedManifest, null));
        Assert.Contains("Unsupported test format default 'json'.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkbookOpenTimeoutResolutionUsesManifestThenBuiltInDefault()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null) with
        {
            CommandDefaults = new CommandDefaults(
                ExcelAutomation: new ExcelAutomationCommandDefaults(WorkbookOpenTimeoutSeconds: 42))
        };

        Assert.Equal(TimeSpan.FromSeconds(42), CommandDefaultResolver.ResolveWorkbookOpenTimeout(manifest));
        Assert.Equal(
            TimeSpan.FromSeconds(300),
            CommandDefaultResolver.ResolveWorkbookOpenTimeout(ProjectManifest.CreateDefault("Project", "Book1", root, null)));
    }

    [Fact]
    public void TestExecutionTimeoutResolutionUsesCliThenManifestThenBuiltInDefault()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null) with
        {
            CommandDefaults = new CommandDefaults(
                Test: new TestCommandDefaults(ExecutionTimeoutSeconds: 77))
        };

        Assert.Equal(
            TimeSpan.FromSeconds(42),
            CommandDefaultResolver.ResolveTestExecutionTimeout(manifest, 42));
        Assert.Equal(
            TimeSpan.FromSeconds(77),
            CommandDefaultResolver.ResolveTestExecutionTimeout(manifest, null));
        Assert.Equal(
            TimeSpan.FromSeconds(600),
            CommandDefaultResolver.ResolveTestExecutionTimeout(
                ProjectManifest.CreateDefault("Project", "Book1", root, null),
                null));
    }

    [Fact]
    public void WorkbookSaveTimeoutResolutionUsesManifestThenBuiltInDefault()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null) with
        {
            CommandDefaults = new CommandDefaults(
                ExcelAutomation: new ExcelAutomationCommandDefaults(WorkbookSaveTimeoutSeconds: 73))
        };

        Assert.Equal(TimeSpan.FromSeconds(73), CommandDefaultResolver.ResolveWorkbookSaveTimeout(manifest));
        Assert.Equal(
            TimeSpan.FromSeconds(300),
            CommandDefaultResolver.ResolveWorkbookSaveTimeout(ProjectManifest.CreateDefault("Project", "Book1", root, null)));
    }

    [Fact]
    public void ProjectManifestRejectsNonPositiveWorkbookOpenTimeout()
    {
        var json = ProjectManifestTestData.ValidJson("Project").Replace(
            "\"commandDefaults\": {",
            "\"commandDefaults\": { \"excelAutomation\": { \"workbookOpenTimeoutSeconds\": 0 },",
            StringComparison.Ordinal);

        var ex = Assert.Throws<VbaProjectManifestException>(
            () => ProjectManifestReader.Parse(json, "vba-project.json"));

        Assert.Contains("workbookOpenTimeoutSeconds", ex.Message, StringComparison.Ordinal);
        Assert.Contains("positive whole seconds", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    public void ProjectManifestRejectsInvalidTestExecutionTimeout(string value)
    {
        var json = ProjectManifestTestData.ValidJson("Project").Replace(
            "\"format\": \"ndjson\"",
            $"\"format\": \"ndjson\", \"executionTimeoutSeconds\": {value}",
            StringComparison.Ordinal);

        var ex = Assert.Throws<VbaProjectManifestException>(
            () => ProjectManifestReader.Parse(json, "vba-project.json"));

        Assert.Contains("executionTimeoutSeconds", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectManifestRejectsNonPositiveWorkbookSaveTimeout()
    {
        var json = ProjectManifestTestData.ValidJson("Project").Replace(
            "\"commandDefaults\": {",
            "\"commandDefaults\": { \"excelAutomation\": { \"workbookSaveTimeoutSeconds\": -1 },",
            StringComparison.Ordinal);

        var ex = Assert.Throws<VbaProjectManifestException>(
            () => ProjectManifestReader.Parse(json, "vba-project.json"));

        Assert.Contains("workbookSaveTimeoutSeconds", ex.Message, StringComparison.Ordinal);
        Assert.Contains("positive whole seconds", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectManifestRejectsFractionalExcelAutomationTimeout()
    {
        var json = ProjectManifestTestData.ValidJson("Project").Replace(
            "\"commandDefaults\": {",
            "\"commandDefaults\": { \"excelAutomation\": { \"workbookOpenTimeoutSeconds\": 1.5 },",
            StringComparison.Ordinal);

        var ex = Assert.Throws<VbaProjectManifestException>(
            () => ProjectManifestReader.Parse(json, "vba-project.json"));

        Assert.Contains("workbookOpenTimeoutSeconds", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveOmitsUnspecifiedExcelAutomationTimeouts()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null) with
        {
            CommandDefaults = new CommandDefaults(
                ExcelAutomation: new ExcelAutomationCommandDefaults(WorkbookOpenTimeoutSeconds: 45))
        };
        var store = new JsonProjectManifestStore();

        store.Save(root, manifest);

        var bytes = File.ReadAllBytes(Path.Combine(root, ProjectManifest.ManifestFileName));
        using var document = JsonDocument.Parse(Encoding.Unicode.GetString(bytes[2..]));
        var commandDefaults = document.RootElement.GetProperty("commandDefaults");
        Assert.False(commandDefaults.TryGetProperty("test", out _));
        var excelAutomation = commandDefaults.GetProperty("excelAutomation");
        Assert.Equal(45, excelAutomation.GetProperty("workbookOpenTimeoutSeconds").GetInt32());
        Assert.False(excelAutomation.TryGetProperty("workbookSaveTimeoutSeconds", out _));
    }

    [Fact]
    public void CliRejectsInvalidManifestAsUsageErrorBeforePlaceholderAction()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("BadSchema");
        File.WriteAllText(Path.Combine(root, ProjectManifest.ManifestFileName), ProjectManifestTestData.ValidJson("BadSchema").Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal), new UTF8Encoding(false));
        var application = CommandLineTestFactory.Create(root);

        var result = application.Run(["build"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("schemaVersion", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("primary-document.json", "PrimaryDocumentProject", "Book1", 1)]
    [InlineData("document-source-set.json", "DocumentSourceSetProject", "Book1", 1)]
    [InlineData("references.json", "ReferencesProject", "Book1", 1)]
    [InlineData("source-template.json", "SourceTemplateProject", "Book1", 1)]
    [InlineData("multi-document.json", "MultiDocumentProject", "Book1", 2)]
    public void SharedFixturesLoadAsVbaDevProjectManifests(
        string fixtureName,
        string expectedProjectName,
        string expectedPrimaryDocument,
        int expectedDocumentCount)
    {
        var manifest = new JsonProjectManifestStore().Load(ProjectManifestFixturePath(fixtureName));

        Assert.Equal(expectedProjectName, manifest.ProjectName);
        Assert.Equal(expectedPrimaryDocument, manifest.PrimaryDocument);
        Assert.Equal(expectedDocumentCount, manifest.Documents.Count);
    }

    [Fact]
    public void SharedPrimaryDocumentFixtureDefinesExcelAutomationTimeoutDefaults()
    {
        var manifest = new JsonProjectManifestStore().Load(ProjectManifestFixturePath("primary-document.json"));

        Assert.Equal(TimeSpan.FromSeconds(240), CommandDefaultResolver.ResolveTestExecutionTimeout(manifest, null));
        Assert.Equal(TimeSpan.FromSeconds(120), CommandDefaultResolver.ResolveWorkbookOpenTimeout(manifest));
        Assert.Equal(TimeSpan.FromSeconds(180), CommandDefaultResolver.ResolveWorkbookSaveTimeout(manifest));
        Assert.Equal(ProjectManifest.CurrentSchemaVersion, manifest.SchemaVersion);
    }

    [Theory]
    [InlineData("invalid-missing-primary-document.json", "primaryDocument")]
    [InlineData("invalid-missing-reference-requested.json", "requested")]
    [InlineData("invalid-primary-document-not-defined.json", "primaryDocument")]
    [InlineData("invalid-empty-reference-name.json", "reference name")]
    [InlineData("invalid-empty-common-modules-repository.json", "commonModulesRepository")]
    [InlineData("invalid-empty-command-defaults.json", "commandDefaults")]
    [InlineData("invalid-empty-excel-automation-defaults.json", "commandDefaults.excelAutomation")]
    [InlineData("invalid-empty-project-name.json", "projectName")]
    [InlineData("invalid-empty-primary-document.json", "primaryDocument")]
    [InlineData("invalid-empty-test-defaults.json", "commandDefaults.test")]
    [InlineData("invalid-unknown-root-property.json", "unexpected")]
    [InlineData("invalid-unknown-document-property.json", "unexpected")]
    [InlineData("invalid-unknown-common-module-property.json", "unexpected")]
    [InlineData("invalid-unknown-command-default-property.json", "unexpected")]
    [InlineData("invalid-unknown-excel-automation-default-property.json", "unexpected")]
    [InlineData("invalid-unknown-test-default-property.json", "unexpected")]
    [InlineData("invalid-workbook-open-timeout.json", "workbookOpenTimeoutSeconds")]
    [InlineData("invalid-workbook-save-timeout.json", "workbookSaveTimeoutSeconds")]
    [InlineData("invalid-mis-cased-root-property.json", "ProjectName")]
    [InlineData("invalid-mis-cased-test-default-property.json", "Format")]
    [InlineData("invalid-mis-cased-document-kind.json", "EXCEL")]
    [InlineData("invalid-test-execution-timeout.json", "executionTimeoutSeconds")]
    [InlineData("invalid-test-format.json", "JSON")]
    [InlineData("invalid-missing-selection-arrays.json", "commonModules")]
    [InlineData("invalid-missing-template-path.json", "templatePath")]
    [InlineData("invalid-null-optional-property.json", "commonModulesRepository")]
    [InlineData("invalid-null-command-default.json", "test")]
    [InlineData("invalid-null-document.json", "Book1")]
    [InlineData("invalid-null-reference.json", "null")]
    [InlineData("invalid-schema-version.json", "schemaVersion")]
    [InlineData("invalid-standard-library-reference.json", "always active")]
    [InlineData("invalid-untrimmed-reference-name.json", "leading or trailing")]
    [InlineData("invalid-duplicate-reference-name.json", "duplicate")]
    [InlineData("invalid-equal-source-roots.json", "Book1")]
    [InlineData("invalid-nested-source-roots.json", "Book2")]
    public void SharedInvalidFixturesFailVbaDevManifestValidation(string fixtureName, string expectedMessage)
    {
        var ex = Assert.Throws<ProjectManifestException>(
            () => new JsonProjectManifestStore().Load(ProjectManifestFixturePath(fixtureName)));

        Assert.Contains(expectedMessage, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicateReferenceDiagnosticRetainsBothConflictingSpellings()
    {
        var ex = Assert.Throws<ProjectManifestException>(
            () => new JsonProjectManifestStore().Load(
                ProjectManifestFixturePath("invalid-duplicate-reference-name.json")));

        Assert.Contains("Alpha Library", ex.Message, StringComparison.Ordinal);
        Assert.Contains("aLPHA Library", ex.Message, StringComparison.Ordinal);
    }

    private static string ProjectManifestFixturePath(string fixtureName)
        => Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "..",
            "..",
            "fixtures",
            "project-manifest",
            fixtureName));
}

internal static class ProjectManifestTestData
{
    public static ProjectManifest FullManifest(string projectRoot)
        => ProjectManifest.CreateDefault(
            "CanonicalProject",
            "Book1",
            projectRoot,
            Path.GetFullPath(Path.Combine(projectRoot, "..", "common_modules_repo")),
            [new InstalledCommonModule("Runtime", "Runtime.bas", Requested: true, TestOnly: false)],
            [new VbaProjectReference("Microsoft Scripting Runtime")]) with
        {
            CommandDefaults = new CommandDefaults(
                Test: new TestCommandDefaults(
                    Format: "ndjson",
                    ExecutionTimeoutSeconds: 240),
                ExcelAutomation: new ExcelAutomationCommandDefaults(
                    WorkbookOpenTimeoutSeconds: 120,
                    WorkbookSaveTimeoutSeconds: 180))
        };

    public static byte[] ExpectedFullCanonicalBytes()
    {
        var json = """
        {
          "schemaVersion": 1,
          "projectName": "CanonicalProject",
          "primaryDocument": "Book1",
          "documents": {
            "Book1": {
              "kind": "excel",
              "sourcePath": "src/Book1",
              "templatePath": "src/Book1/Book1.xlsm",
              "binPath": "bin/Book1.xlsm",
              "publishPath": "publish/Book1.xlsm",
              "commonModules": [
                {
                  "name": "Runtime",
                  "moduleFile": "Runtime.bas",
                  "requested": true,
                  "testOnly": false
                }
              ],
              "references": [
                {
                  "name": "Microsoft Scripting Runtime",
                  "requested": true
                }
              ]
            }
          },
          "commonModulesRepository": "../common_modules_repo",
          "commandDefaults": {
            "test": {
              "format": "ndjson",
              "executionTimeoutSeconds": 240
            },
            "excelAutomation": {
              "workbookOpenTimeoutSeconds": 120,
              "workbookSaveTimeoutSeconds": 180
            }
          }
        }
        """.ReplaceLineEndings("\r\n") + "\r\n";
        var content = Encoding.Unicode.GetBytes(json);
        var bytes = new byte[2 + content.Length];
        bytes[0] = 0xff;
        bytes[1] = 0xfe;
        Buffer.BlockCopy(content, 0, bytes, 2, content.Length);
        return bytes;
    }

    public static string ExpectedMinimalCanonicalJson(string projectName)
        => $$"""
        {
          "schemaVersion": 1,
          "projectName": "{{projectName}}",
          "primaryDocument": "Book1",
          "documents": {
            "Book1": {
              "kind": "excel",
              "sourcePath": "src/Book1",
              "templatePath": "src/Book1/Book1.xlsm",
              "binPath": "bin/Book1.xlsm",
              "publishPath": "publish/Book1.xlsm",
              "commonModules": [],
              "references": []
            }
          }
        }
        """.ReplaceLineEndings("\r\n") + "\r\n";

    public static string ValidJson(string projectName)
        => $$"""
        {
          "schemaVersion": 1,
          "projectName": "{{projectName}}",
          "primaryDocument": "Book1",
          "documents": {
            "Book1": {
              "kind": "excel",
              "sourcePath": "src/Book1",
              "templatePath": "src/Book1/Book1.xlsm",
              "binPath": "bin/Book1.xlsm",
              "publishPath": "publish/Book1.xlsm",
              "commonModules": [],
              "references": []
            }
          },
          "commonModulesRepository": "../common_modules_repo",
          "commandDefaults": {
            "test": {
              "format": "ndjson"
            }
          }
        }
        """;

    public static ProjectManifest TwoDocumentManifest(string projectRoot)
        => ProjectManifest.CreateDefault("Project", "Book1", projectRoot, null) with
        {
            Documents = new Dictionary<string, ProjectDocument>(StringComparer.OrdinalIgnoreCase)
            {
                ["Book1"] = ProjectDocument.CreateExcel("Book1"),
                ["SecondBook"] = ProjectDocument.CreateExcel("SecondBook")
            }
        };
}

internal sealed class FailingProjectManifestStore : IProjectManifestStore
{
    public ProjectManifest Load(string manifestPath)
        => throw new NotSupportedException();

    public void Save(string projectRoot, ProjectManifest manifest)
        => throw new IOException("manifest save failed");
}

internal sealed class TempDirectory : IDisposable
{
    private TempDirectory(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public static TempDirectory Create()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vba-devtools-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new TempDirectory(path);
    }

    public string CreateDirectory(string name)
    {
        var path = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
