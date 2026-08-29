using System.Text;
using System.Text.Json;
using VbaDev.App.CommonModules;
using VbaDev.App.Projects;
using VbaDev.App.References;
using VbaDev.App.Workbooks;
using VbaDev.Cli;
using VbaDev.Composition;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;
using Xunit;

namespace VbaDev.Tests;

public sealed class NewProjectCommandTests
{
    [Fact]
    public void NewExcelRejectsAnExplicitEmptyNameWithoutCreatingArtifacts()
    {
        using var temp = TempDirectory.Create();
        var application = CommandLineTestFactory.Create(
            temp.Path,
            initialWorkbookCreator: new FakeInitialWorkbookCreator());

        var result = application.Run([
            "new",
            "excel",
            "--name",
            string.Empty,
            "--format",
            "json"
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("projectNameEmpty", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Fact]
    public void NewExcelRejectsAnExplicitWhitespaceNameWithoutCreatingArtifacts()
    {
        using var temp = TempDirectory.Create();
        var application = CommandLineTestFactory.Create(
            temp.Path,
            initialWorkbookCreator: new FakeInitialWorkbookCreator());

        var result = application.Run([
            "new",
            "excel",
            "--name",
            "  ",
            "--format",
            "json"
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains(
            ProjectCreationPathValidationReasons.ProjectNameHasLeadingOrTrailingWhitespace,
            result.StandardError,
            StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Fact]
    public void NewExcelDerivesNameAndRootWhenBothOptionsAreOmitted()
    {
        using var temp = TempDirectory.Create();
        var expectedName = Path.GetFileName(temp.Path);
        var application = CommandLineTestFactory.Create(
            temp.Path,
            initialWorkbookCreator: new FakeInitialWorkbookCreator());

        var result = application.Run([
            "new",
            "excel",
            "--format",
            "json"
        ]);

        Assert.Equal(0, result.ExitCode);
        using var receipt = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(temp.Path, receipt.RootElement.GetProperty("project").GetString());
        Assert.Equal(expectedName, receipt.RootElement.GetProperty("document").GetString());
        Assert.True(File.Exists(Path.Combine(
            temp.Path,
            ProjectManifest.ManifestFileName)));
    }

    [Fact]
    public void NewExcelRejectsAnExplicitEmptyOutputWithoutCreatingArtifacts()
    {
        using var temp = TempDirectory.Create();
        var application = CommandLineTestFactory.Create(
            temp.Path,
            initialWorkbookCreator: new FakeInitialWorkbookCreator());

        var result = application.Run([
            "new",
            "excel",
            "--name",
            "SampleProject",
            "--output",
            string.Empty,
            "--format",
            "json"
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("projectOutputEmpty", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Fact]
    public void NewExcelRejectsAnExplicitWhitespaceOutputWithoutCreatingArtifacts()
    {
        using var temp = TempDirectory.Create();
        var application = CommandLineTestFactory.Create(
            temp.Path,
            initialWorkbookCreator: new FakeInitialWorkbookCreator());

        var result = application.Run([
            "new",
            "excel",
            "--name",
            "SampleProject",
            "--output",
            " \t",
            "--format",
            "json"
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains(
            "projectOutputNotWindowsFilesystemPath",
            result.StandardError,
            StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Fact]
    public void NewExcelResolvesRelativeOutputFromInvocationStartDirectory()
    {
        using var temp = TempDirectory.Create();
        var startDirectory = temp.CreateDirectory("invocation");
        var application = CommandLineTestFactory.Create(
            startDirectory,
            initialWorkbookCreator: new FakeInitialWorkbookCreator());

        var result = application.Run([
            "new",
            "excel",
            "--name",
            "SampleProject",
            "--output",
            Path.Combine("nested", "target")
        ]);

        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.True(File.Exists(Path.Combine(
            startDirectory,
            "nested",
            "target",
            ProjectManifest.ManifestFileName)));
    }

    [Fact]
    public void NewExcelAcceptsAnExplicitDriveQualifiedOutput()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "DriveProject");
        Assert.True(Path.IsPathFullyQualified(projectRoot));
        var application = CommandLineTestFactory.Create(
            temp.Path,
            initialWorkbookCreator: new FakeInitialWorkbookCreator());

        var result = application.Run([
            "new",
            "excel",
            "--name",
            "DriveProject",
            "--output",
            projectRoot,
            "--format",
            "json"
        ]);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(Path.Combine(
            projectRoot,
            ProjectManifest.ManifestFileName)));
    }

    [Theory]
    [InlineData(@"\root-relative")]
    [InlineData("/root-relative")]
    [InlineData(@"C:drive-relative")]
    [InlineData(@"\\?\C:\temp\project")]
    [InlineData(@"\\.\C:\temp\project")]
    [InlineData(@"\??\C:\temp\project")]
    public void NewExcelRejectsUnsupportedWindowsPathFormsWithoutArtifacts(
        string output)
    {
        using var temp = TempDirectory.Create();
        var application = CommandLineTestFactory.Create(
            temp.Path,
            initialWorkbookCreator: new FakeInitialWorkbookCreator());

        var result = application.Run([
            "new",
            "excel",
            "--name",
            "SampleProject",
            "--output",
            output,
            "--format",
            "json"
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains(
            "projectOutputNotWindowsFilesystemPath",
            result.StandardError,
            StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Theory]
    [InlineData("file:///C:/temp/project")]
    [InlineData("vscode-remote://ssh-remote+host/project")]
    public void NewExcelRejectsUriOutputWithoutCreatingArtifacts(string output)
    {
        using var temp = TempDirectory.Create();
        var application = CommandLineTestFactory.Create(
            temp.Path,
            initialWorkbookCreator: new FakeInitialWorkbookCreator());

        var result = application.Run([
            "new",
            "excel",
            "--name",
            "SampleProject",
            "--output",
            output,
            "--format",
            "json"
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("Windows filesystem path", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Fact]
    public void NewExcelRejectsExcelBracketPathBeforeCreatingArtifacts()
    {
        using var temp = TempDirectory.Create();
        var application = CommandLineTestFactory.Create(
            temp.Path,
            initialWorkbookCreator: new FakeInitialWorkbookCreator());

        var result = application.Run([
            "new",
            "excel",
            "--name",
            "Sample[Project]",
            "--format",
            "json"
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains(
            ProjectCreationPathValidationReasons.ExcelPathContainsUnsupportedCharacter,
            result.StandardError,
            StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Fact]
    public void NewExcelJsonReceiptContainsTheExactCommittedManifest()
    {
        using var temp = TempDirectory.Create();
        var application = CommandLineTestFactory.Create(
            temp.Path,
            initialWorkbookCreator: new FakeInitialWorkbookCreator(
                "Visual Basic For Applications",
                "Microsoft Excel 16.0 Object Library"));

        var result = application.Run([
            "new",
            "excel",
            "--name",
            "SampleProject",
            "--format",
            "json"
        ]);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        using var output = JsonDocument.Parse(result.StandardOutput);
        var root = output.RootElement;
        Assert.Equal("1.0", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("project", root.GetProperty("scope").GetString());
        Assert.Equal(Path.Combine(temp.Path, "SampleProject"), root.GetProperty("project").GetString());
        Assert.Equal("SampleProject", root.GetProperty("document").GetString());
        Assert.Equal("new", root.GetProperty("operation").GetString());
        Assert.Equal("excel", root.GetProperty("template").GetString());
        Assert.True(root.GetProperty("complete").GetBoolean());
        Assert.Equal(
            Path.Combine(temp.Path, "SampleProject", ProjectManifest.ManifestFileName),
            root.GetProperty("manifestPath").GetString());
        var warnings = root.GetProperty("warnings").EnumerateArray().ToArray();
        var warning = Assert.Single(warnings);
        Assert.Equal("commonModulesRepositoryNotFound", warning.GetProperty("code").GetString());
        var manifest = root.GetProperty("manifest");
        Assert.Equal(1, manifest.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("SampleProject", manifest.GetProperty("projectName").GetString());
        Assert.Equal("SampleProject", manifest.GetProperty("primaryDocument").GetString());
        Assert.False(manifest.TryGetProperty("commonModulesRepository", out _));
        Assert.False(manifest.TryGetProperty("commandDefaults", out _));
        var document = manifest.GetProperty("documents").GetProperty("SampleProject");
        Assert.Equal("excel", document.GetProperty("kind").GetString());
        Assert.Empty(document.GetProperty("commonModules").EnumerateArray());
        var references = document.GetProperty("references").EnumerateArray().ToArray();
        var reference = Assert.Single(references);
        Assert.Equal("Microsoft Excel 16.0 Object Library", reference.GetProperty("name").GetString());
        Assert.True(reference.GetProperty("requested").GetBoolean());

        var manifestPath = root.GetProperty("manifestPath").GetString()!;
        var committed = new JsonProjectManifestStore().Load(manifestPath);
        Assert.Equal(committed.ProjectName, manifest.GetProperty("projectName").GetString());
        Assert.Equal(
            committed.Documents["SampleProject"].References.Select(item => item.Name),
            references.Select(item => item.GetProperty("name").GetString()));
    }

    [Fact]
    public void NewExcelRejectsAProjectInsideAnAncestorDocumentSourceSetBeforeWorkbookCreation()
    {
        using var temp = TempDirectory.Create();
        var ancestorRoot = temp.CreateDirectory("AncestorProject");
        var ancestorManifest = ProjectManifest.CreateDefault(
            "AncestorProject",
            "AncestorBook",
            ancestorRoot,
            null);
        new JsonProjectManifestStore().Save(ancestorRoot, ancestorManifest);
        var childRoot = Path.Combine(
            ancestorRoot,
            "src",
            "AncestorBook",
            "ChildProject");
        var workbookCreator = new FakeInitialWorkbookCreator();
        var application = CommandLineTestFactory.Create(
            temp.Path,
            initialWorkbookCreator: workbookCreator);

        var result = application.Run([
            "new",
            "excel",
            "--name",
            "ChildProject",
            "--output",
            childRoot
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("AncestorBook", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("src/AncestorBook", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(workbookCreator.CreatedPaths);
        Assert.False(Directory.Exists(childRoot));
    }

    [Fact]
    public void NewExcelRejectsAnInvalidAncestorManifestBeforeWorkbookCreation()
    {
        using var temp = TempDirectory.Create();
        var ancestorRoot = temp.CreateDirectory("AncestorProject");
        File.WriteAllText(
            Path.Combine(ancestorRoot, ProjectManifest.ManifestFileName),
            "{\"schemaVersion\":1}",
            new UTF8Encoding(false));
        var childRoot = Path.Combine(
            ancestorRoot,
            "projects",
            "ChildProject");
        var workbookCreator = new FakeInitialWorkbookCreator();
        var application = CommandLineTestFactory.Create(
            temp.Path,
            initialWorkbookCreator: workbookCreator);

        var result = application.Run([
            "new",
            "excel",
            "--name",
            "ChildProject",
            "--output",
            childRoot
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(ProjectManifest.ManifestFileName, result.StandardError, StringComparison.Ordinal);
        Assert.Empty(workbookCreator.CreatedPaths);
        Assert.False(Directory.Exists(childRoot));
    }

    [Fact]
    public void NewExcelFailsClosedForAnUnresolvedAncestorManifestLink()
    {
        using var temp = TempDirectory.Create();
        var ancestorRoot = temp.CreateDirectory("AncestorProject");
        File.CreateSymbolicLink(
            Path.Combine(ancestorRoot, ProjectManifest.ManifestFileName),
            Path.Combine(ancestorRoot, "missing-manifest.json"));
        var childRoot = Path.Combine(
            ancestorRoot,
            "projects",
            "ChildProject");
        var workbookCreator = new FakeInitialWorkbookCreator();
        var application = CommandLineTestFactory.Create(
            temp.Path,
            initialWorkbookCreator: workbookCreator);

        var result = application.Run([
            "new",
            "excel",
            "--name",
            "ChildProject",
            "--output",
            childRoot
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(ProjectManifest.ManifestFileName, result.StandardError, StringComparison.Ordinal);
        Assert.Empty(workbookCreator.CreatedPaths);
        Assert.False(Directory.Exists(childRoot));
    }

    [Fact]
    public void NewExcelFailsClosedWhenProjectRootIdentityCannotBeEstablished()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "ChildProject");
        var workbookCreator = new FakeInitialWorkbookCreator();
        var command = new NewProjectCommand(
            new JsonProjectManifestStore(),
            workbookCreator,
            new CommonModulesManifestReader(),
            new VbaProjectReferencePlanner(
                new FakeVbaProjectReferenceResolver()),
            new ProjectManifestMutationLeaseProvider(),
            new FailingNewProjectIdentityResolver());

        var result = command.Run(new NewProjectCommandRequest(
            "ChildProject",
            null,
            projectRoot,
            temp.Path));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("safely resolvable filesystem identity", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(workbookCreator.CreatedPaths);
        Assert.False(Directory.Exists(projectRoot));
    }

    [Fact]
    public void NewExcelRejectsAReplacedProjectRootBeforeManifestCommit()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = temp.CreateDirectory("ChildProject");
        var workbookCreator = new FakeInitialWorkbookCreator();
        var command = new NewProjectCommand(
            new JsonProjectManifestStore(),
            workbookCreator,
            new CommonModulesManifestReader(),
            new VbaProjectReferencePlanner(
                new FakeVbaProjectReferenceResolver()),
            new ProjectManifestMutationLeaseProvider(),
            new SequenceNewProjectIdentityResolver(
                new FileSystemPathIdentity(
                    projectRoot,
                    projectRoot,
                    new FileSystemObjectIdentity(1, 10)),
                new FileSystemPathIdentity(
                    projectRoot,
                    projectRoot,
                    new FileSystemObjectIdentity(1, 20))));

        var result = command.Run(new NewProjectCommandRequest(
            "ChildProject",
            null,
            projectRoot,
            temp.Path));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("newProjectTargetChanged", result.StandardError, StringComparison.Ordinal);
        Assert.True(Directory.Exists(projectRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(projectRoot));
    }

    [Fact]
    public void NewExcelRejectsAProjectReachedThroughAnAncestorSourceSetAlias()
    {
        using var temp = TempDirectory.Create();
        var ancestorRoot = temp.CreateDirectory("AncestorProject");
        new JsonProjectManifestStore().Save(
            ancestorRoot,
            ProjectManifest.CreateDefault(
                "AncestorProject",
                "AncestorBook",
                ancestorRoot,
                null));
        var sourceRoot = Path.Combine(
            ancestorRoot,
            "src",
            "AncestorBook");
        Directory.CreateDirectory(sourceRoot);
        var sourceAlias = Path.Combine(temp.Path, "SourceAlias");
        Directory.CreateSymbolicLink(sourceAlias, sourceRoot);
        var childRoot = Path.Combine(sourceAlias, "ChildProject");
        var workbookCreator = new FakeInitialWorkbookCreator();
        var application = CommandLineTestFactory.Create(
            temp.Path,
            initialWorkbookCreator: workbookCreator);

        var result = application.Run([
            "new",
            "excel",
            "--name",
            "ChildProject",
            "--output",
            childRoot
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("AncestorBook", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(workbookCreator.CreatedPaths);
        Assert.False(Directory.Exists(childRoot));
    }

    [Fact]
    public void NewExcelAllowsANestedProjectThatIsDisjointFromAncestorSourceSets()
    {
        using var temp = TempDirectory.Create();
        var ancestorRoot = temp.CreateDirectory("AncestorProject");
        new JsonProjectManifestStore().Save(
            ancestorRoot,
            ProjectManifest.CreateDefault(
                "AncestorProject",
                "AncestorBook",
                ancestorRoot,
                null));
        var childRoot = Path.Combine(
            ancestorRoot,
            "projects",
            "ChildProject");
        var application = CommandLineTestFactory.Create(
            temp.Path,
            initialWorkbookCreator: new FakeInitialWorkbookCreator());

        var result = application.Run([
            "new",
            "excel",
            "--name",
            "ChildProject",
            "--output",
            childRoot
        ]);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(Path.Combine(
            childRoot,
            ProjectManifest.ManifestFileName)));
    }

    [Fact]
    public void NewExcelRechecksAnAncestorManifestBeforeCommitAndRollsBackOwnedArtifacts()
    {
        using var temp = TempDirectory.Create();
        var ancestorRoot = temp.CreateDirectory("AncestorProject");
        var childRoot = Path.Combine(
            ancestorRoot,
            "src",
            "AncestorBook",
            "ChildProject");
        var workbookCreator = new FakeInitialWorkbookCreator
        {
            AfterCreate = _ => new JsonProjectManifestStore().Save(
                ancestorRoot,
                ProjectManifest.CreateDefault(
                    "AncestorProject",
                    "AncestorBook",
                    ancestorRoot,
                    null))
        };
        var application = CommandLineTestFactory.Create(
            temp.Path,
            initialWorkbookCreator: workbookCreator);

        var result = application.Run([
            "new",
            "excel",
            "--name",
            "ChildProject",
            "--output",
            childRoot
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("AncestorBook", result.StandardError, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(
            ancestorRoot,
            ProjectManifest.ManifestFileName)));
        Assert.False(File.Exists(Path.Combine(
            childRoot,
            ProjectManifest.ManifestFileName)));
        Assert.False(Directory.Exists(childRoot));
    }

    [Fact]
    public void NewExcelAcceptsALatestAncestorManifestThatRemainsDisjoint()
    {
        using var temp = TempDirectory.Create();
        var ancestorRoot = temp.CreateDirectory("AncestorProject");
        var childRoot = Path.Combine(
            ancestorRoot,
            "projects",
            "ChildProject");
        var workbookCreator = new FakeInitialWorkbookCreator
        {
            AfterCreate = _ => new JsonProjectManifestStore().Save(
                ancestorRoot,
                ProjectManifest.CreateDefault(
                    "AncestorProject",
                    "AncestorBook",
                    ancestorRoot,
                    null))
        };
        var application = CommandLineTestFactory.Create(
            temp.Path,
            initialWorkbookCreator: workbookCreator);

        var result = application.Run([
            "new",
            "excel",
            "--name",
            "ChildProject",
            "--output",
            childRoot
        ]);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(Path.Combine(
            childRoot,
            ProjectManifest.ManifestFileName)));
        Assert.True(File.Exists(Path.Combine(
            ancestorRoot,
            ProjectManifest.ManifestFileName)));
    }

    [Fact]
    public void NewExcelRollbackPreservesAByteIdenticalForeignWorkbookReplacement()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var ancestorRoot = temp.CreateDirectory("AncestorProject");
        var childRoot = Path.Combine(
            ancestorRoot,
            "src",
            "AncestorBook",
            "ChildProject");
        var workbookPath = Path.Combine(
            childRoot,
            "src",
            "ChildProject",
            "ChildProject.xlsm");
        var displacedOwnedWorkbook = workbookPath + ".owned";
        var resolver = new CallbackOnNewProjectIdentityResolution(3, () =>
        {
            File.Move(workbookPath, displacedOwnedWorkbook);
            File.Copy(displacedOwnedWorkbook, workbookPath);
            new JsonProjectManifestStore().Save(
                ancestorRoot,
                ProjectManifest.CreateDefault(
                    "AncestorProject",
                    "AncestorBook",
                    ancestorRoot,
                    null));
        });
        var command = new NewProjectCommand(
            new JsonProjectManifestStore(),
            new FakeInitialWorkbookCreator(),
            new CommonModulesManifestReader(),
            new VbaProjectReferencePlanner(
                new FakeVbaProjectReferenceResolver()),
            new ProjectManifestMutationLeaseProvider(),
            resolver);

        var result = command.Run(new NewProjectCommandRequest(
            "ChildProject",
            null,
            childRoot,
            temp.Path));

        Assert.Equal(1, result.ExitCode);
        Assert.True(File.Exists(workbookPath));
        Assert.Equal("fake xlsm", File.ReadAllText(workbookPath));
        Assert.True(File.Exists(displacedOwnedWorkbook));
    }

    [Fact]
    public void NewExcelRejectsAndPreservesAReplacementBeforeEvidenceRegistration()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "SampleProject");
        var workbookPath = Path.Combine(
            projectRoot,
            "src",
            "SampleProject",
            "SampleProject.xlsm");
        var displacedOwnedWorkbook = workbookPath + ".owned";
        var workbookCreator = new FakeInitialWorkbookCreator
        {
            AfterCreate = path =>
            {
                File.Move(path, displacedOwnedWorkbook);
                File.Copy(displacedOwnedWorkbook, path);
            }
        };
        var application = CommandLineTestFactory.Create(
            temp.Path,
            initialWorkbookCreator: workbookCreator);

        var result = application.Run([
            "new",
            "excel",
            "--name",
            "SampleProject",
            "--format",
            "json"
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(
            "newProjectTargetChanged",
            result.StandardError,
            StringComparison.Ordinal);
        Assert.True(File.Exists(workbookPath));
        Assert.Equal("fake xlsm", File.ReadAllText(workbookPath));
        Assert.True(File.Exists(displacedOwnedWorkbook));
        Assert.False(File.Exists(Path.Combine(
            projectRoot,
            ProjectManifest.ManifestFileName)));
    }

    [Fact]
    public async Task NewCreatesProjectLayoutWorkbookAndUtf16Manifest()
    {
        using var temp = TempDirectory.Create();
        var workbookCreator = new FakeInitialWorkbookCreator(
            "Visual Basic For Applications",
            "Microsoft Excel 16.0 Object Library");
        var application = VbaDevCommandLine.Create(
            ToolingCompositionRoot.CreateApplicationComposition(
                temp.Path,
                initialWorkbookCreator: workbookCreator));
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await application.InvokeAsync(
            ["new", "excel", "--name", "SampleProject"],
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains(
            "CommonModules repository was not found; the project was created without shared modules.",
            standardError.ToString(),
            StringComparison.Ordinal);
        var projectRoot = Path.Combine(temp.Path, "SampleProject");
        Assert.True(Directory.Exists(Path.Combine(projectRoot, "src", "SampleProject")));
        Assert.True(Directory.Exists(Path.Combine(projectRoot, "bin")));
        Assert.True(Directory.Exists(Path.Combine(projectRoot, "publish")));
        Assert.True(File.Exists(Path.Combine(projectRoot, "src", "SampleProject", "SampleProject.xlsm")));
        Assert.False(Directory.Exists(Path.Combine(projectRoot, "src", "SampleProject", "common-modules")));
        Assert.Contains(Path.Combine(projectRoot, "src", "SampleProject", "SampleProject.xlsm"), workbookCreator.CreatedPaths);

        var manifestPath = Path.Combine(projectRoot, ProjectManifest.ManifestFileName);
        var bytes = File.ReadAllBytes(manifestPath);
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xFE, bytes[1]);

        var manifest = new JsonProjectManifestStore().Load(manifestPath);
        Assert.Equal("SampleProject", manifest.ProjectName);
        Assert.Equal("SampleProject", manifest.PrimaryDocument);
        Assert.Null(manifest.CommonModulesRepository);
        Assert.Empty(manifest.Documents["SampleProject"].CommonModules);
        Assert.Equal("bin/SampleProject.xlsm", manifest.Documents["SampleProject"].BinPath);
        Assert.Equal("publish/SampleProject.xlsm", manifest.Documents["SampleProject"].PublishPath);
        Assert.Equal(
            [
                "Microsoft Excel 16.0 Object Library"
            ],
            manifest.Documents["SampleProject"].References.Select(reference => reference.Name));
    }

    [Fact]
    public void NewExcelDoesNotDuplicateStandardInitialReferences()
    {
        using var temp = TempDirectory.Create();
        var workbookCreator = new FakeInitialWorkbookCreator(
            "Visual Basic For Applications",
            "Microsoft Scripting Runtime",
            "Microsoft VBScript Regular Expressions 5.5");
        var application = CommandLineTestFactory.Create(
            temp.Path,
            initialWorkbookCreator: workbookCreator);

        var result = application.Run(["new", "excel", "--name", "SampleProject"]);

        Assert.Equal(0, result.ExitCode);
        var manifestPath = Path.Combine(temp.Path, "SampleProject", ProjectManifest.ManifestFileName);
        var manifest = new JsonProjectManifestStore().Load(manifestPath);
        Assert.Equal(
            [
                "Microsoft Scripting Runtime",
                "Microsoft VBScript Regular Expressions 5.5"
            ],
            manifest.Documents["SampleProject"].References.Select(reference => reference.Name));
    }

    [Fact]
    public void NewExcelUsesOutputAsProjectRootAndDerivesNameWhenNameIsOmitted()
    {
        using var temp = TempDirectory.Create();
        var output = Path.Combine(temp.Path, "OutputProject");
        var application = CommandLineTestFactory.Create(
            temp.Path,
            initialWorkbookCreator: new FakeInitialWorkbookCreator());

        var result = application.Run(["new", "excel", "--output", output]);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(Path.Combine(output, "src", "OutputProject", "OutputProject.xlsm")));
        var manifest = new JsonProjectManifestStore().Load(Path.Combine(output, ProjectManifest.ManifestFileName));
        Assert.Equal("OutputProject", manifest.ProjectName);
        Assert.Equal("OutputProject", manifest.PrimaryDocument);
    }

    [Fact]
    public void NewExcelUsesNameForProjectAndDocumentWhenOutputIsSpecified()
    {
        using var temp = TempDirectory.Create();
        var output = Path.Combine(temp.Path, "GeneratedProject");
        var application = CommandLineTestFactory.Create(
            temp.Path,
            initialWorkbookCreator: new FakeInitialWorkbookCreator());

        var result = application.Run(["new", "excel", "--name", "WorkbookMain", "--output", output]);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(Path.Combine(output, "src", "WorkbookMain", "WorkbookMain.xlsm")));
        var manifest = new JsonProjectManifestStore().Load(Path.Combine(output, ProjectManifest.ManifestFileName));
        Assert.Equal("WorkbookMain", manifest.ProjectName);
        Assert.Equal("WorkbookMain", manifest.PrimaryDocument);
    }

    [Fact]
    public void NewAcceptsEmptyDirectoryAndRejectsNonEmptyDirectoryWithoutDeletingFiles()
    {
        using var temp = TempDirectory.Create();
        var emptyProject = Path.Combine(temp.Path, "EmptyProject");
        Directory.CreateDirectory(emptyProject);
        var nonEmptyProject = Path.Combine(temp.Path, "NonEmptyProject");
        Directory.CreateDirectory(nonEmptyProject);
        var existingFile = Path.Combine(nonEmptyProject, "keep.txt");
        File.WriteAllText(existingFile, "keep", new UTF8Encoding(false));
        var application = CommandLineTestFactory.Create(
            temp.Path,
            initialWorkbookCreator: new FakeInitialWorkbookCreator());

        var emptyResult = application.Run(["new", "excel", "-n", "EmptyProject"]);
        var nonEmptyResult = application.Run(["new", "excel", "-n", "NonEmptyProject"]);

        Assert.Equal(0, emptyResult.ExitCode);
        Assert.Equal(1, nonEmptyResult.ExitCode);
        Assert.True(File.Exists(existingFile));
        Assert.Contains("newProjectTargetChanged", nonEmptyResult.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void NewRejectsExistingProjectManifestWithoutDeletingFiles()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "ExistingProject");
        Directory.CreateDirectory(projectRoot);
        var manifestPath = Path.Combine(projectRoot, ProjectManifest.ManifestFileName);
        File.WriteAllText(manifestPath, "{}", new UTF8Encoding(false));
        var application = CommandLineTestFactory.Create(
            temp.Path,
            initialWorkbookCreator: new FakeInitialWorkbookCreator());

        var result = application.Run(["new", "excel", "--name", "ExistingProject"]);

        Assert.Equal(1, result.ExitCode);
        Assert.True(File.Exists(manifestPath));
        Assert.Contains("newProjectTargetChanged", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void NewCopiesRuntimeBaselineAndTestFoundationFromCommonModulesManifest()
    {
        using var temp = TempDirectory.Create();
        var commonModulesRepository = Path.Combine(temp.Path, "common_modules_repo");
        Directory.CreateDirectory(commonModulesRepository);
        WriteCommonModulesManifest(commonModulesRepository);
        WriteModule(commonModulesRepository, "Core.bas", "core");
        WriteModule(commonModulesRepository, "Runtime.bas", "runtime");
        WriteModule(commonModulesRepository, "UnitTest.bas", "test");
        WriteModule(commonModulesRepository, "OptionalFeature.bas", "optional");
        var application = CommandLineTestFactory.Create(
            temp.Path,
            initialWorkbookCreator: new FakeInitialWorkbookCreator());

        var result = application.Run(["new", "excel", "--name", "SampleProject"]);

        Assert.True(result.ExitCode == 0, result.StandardError);
        var sourceSet = Path.Combine(temp.Path, "SampleProject", "src", "SampleProject");
        var commonModulesDirectory = Path.Combine(sourceSet, "common-modules");
        Assert.True(File.Exists(Path.Combine(commonModulesDirectory, "Core.bas")));
        Assert.True(File.Exists(Path.Combine(commonModulesDirectory, "Runtime.bas")));
        Assert.True(File.Exists(Path.Combine(commonModulesDirectory, "UnitTest.bas")));
        Assert.False(File.Exists(Path.Combine(sourceSet, "OptionalFeature.bas")));
        var manifest = new JsonProjectManifestStore().Load(Path.Combine(temp.Path, "SampleProject", ProjectManifest.ManifestFileName));
        Assert.Equal("../common_modules_repo", manifest.CommonModulesRepository);
        Assert.Equal(
            [
                new InstalledCommonModule("Core", "Core.bas", Requested: false, TestOnly: false),
                new InstalledCommonModule("Runtime", "Runtime.bas", Requested: true, TestOnly: false),
                new InstalledCommonModule("UnitTest", "UnitTest.bas", Requested: true, TestOnly: true)
            ],
            manifest.Documents["SampleProject"].CommonModules);
        Assert.All(
            manifest.Documents["SampleProject"].CommonModules,
            module => Assert.False(module.Orphaned));
    }

    [Fact]
    public void NewExcelUsesOneStablePackageSnapshotForSccAndReferencePlanning()
    {
        using var temp = TempDirectory.Create();
        var commonModulesRepository = temp.CreateDirectory("common_modules_repo");
        WriteCommonModulesManifestWithReferences(
            commonModulesRepository,
            (
                "CycleB.cls",
                "optional",
                "CycleA.bas",
                new[] { "Cycle Library B" }),
            (
                "CycleA.bas",
                "optional",
                "CycleB.cls",
                new[] { "Cycle Library A" }),
            (
                "RuntimeRoot.bas",
                "runtime-baseline",
                "CycleA.bas",
                new[] { "Runtime Package Library" }),
            (
                "TestRoot.bas",
                "test-foundation",
                "RuntimeRoot.bas",
                new[] { "Test Package Library" }));
        WriteModule(commonModulesRepository, "CycleB.cls", "cycle B generation one");
        WriteModule(commonModulesRepository, "CycleA.bas", "cycle A generation one");
        WriteModule(commonModulesRepository, "RuntimeRoot.bas", "runtime root");
        WriteModule(commonModulesRepository, "TestRoot.bas", "test root");
        var expectedCycleABytes = File.ReadAllBytes(Path.Combine(
            commonModulesRepository,
            "CycleA.bas"));
        var workbookCreator = new FakeInitialWorkbookCreator(
            "Visual Basic For Applications",
            "Microsoft Excel 16.0 Object Library",
            "OLE Automation",
            "Microsoft Office 16.0 Object Library")
        {
            AfterCreate = _ => WriteModule(
                commonModulesRepository,
                "CycleA.bas",
                "cycle A generation two")
        };
        var referenceResolver = new FakeVbaProjectReferenceResolver(
            new ResolvedVbaProjectReference(
                "Cycle Library B",
                "{11111111-1111-1111-1111-111111111111}",
                1,
                0),
            new ResolvedVbaProjectReference(
                "Cycle Library A",
                "{22222222-2222-2222-2222-222222222222}",
                1,
                0),
            new ResolvedVbaProjectReference(
                "Runtime Package Library",
                "{33333333-3333-3333-3333-333333333333}",
                1,
                0),
            new ResolvedVbaProjectReference(
                "Test Package Library",
                "{44444444-4444-4444-4444-444444444444}",
                1,
                0));
        var application = CommandLineTestFactory.Create(
            temp.Path,
            initialWorkbookCreator: workbookCreator,
            vbaProjectReferenceResolver: referenceResolver);

        var result = application.Run([
            "new",
            "excel",
            "--name",
            "SnapshotProject"
        ]);

        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.Empty(result.StandardError);
        var projectRoot = Path.Combine(temp.Path, "SnapshotProject");
        var manifest = new JsonProjectManifestStore().Load(Path.Combine(
            projectRoot,
            ProjectManifest.ManifestFileName));
        var document = manifest.Documents["SnapshotProject"];
        Assert.Equal(
            [
                new InstalledCommonModule(
                    "CycleB",
                    "CycleB.cls",
                    Requested: false,
                    TestOnly: false),
                new InstalledCommonModule(
                    "CycleA",
                    "CycleA.bas",
                    Requested: false,
                    TestOnly: false),
                new InstalledCommonModule(
                    "RuntimeRoot",
                    "RuntimeRoot.bas",
                    Requested: true,
                    TestOnly: false),
                new InstalledCommonModule(
                    "TestRoot",
                    "TestRoot.bas",
                    Requested: true,
                    TestOnly: true)
            ],
            document.CommonModules);
        Assert.Equal(
            [
                new VbaProjectReference(
                    "Microsoft Excel 16.0 Object Library",
                    requested: true),
                new VbaProjectReference("OLE Automation", requested: true),
                new VbaProjectReference(
                    "Microsoft Office 16.0 Object Library",
                    requested: true),
                new VbaProjectReference("Cycle Library B", requested: false),
                new VbaProjectReference("Cycle Library A", requested: false),
                new VbaProjectReference(
                    "Runtime Package Library",
                    requested: false),
                new VbaProjectReference(
                    "Test Package Library",
                    requested: false)
            ],
            document.References);
        Assert.Equal(
            [
                "Cycle Library B",
                "Cycle Library A",
                "Runtime Package Library",
                "Test Package Library"
            ],
            referenceResolver.RequestedNames);
        Assert.DoesNotContain(
            document.References,
            reference => reference.Name.Contains(
                "Scripting Runtime",
                StringComparison.OrdinalIgnoreCase)
                || reference.Name.Contains(
                    "Regular Expressions",
                    StringComparison.OrdinalIgnoreCase));
        var installedCycleAPath = Path.Combine(
            projectRoot,
            "src",
            "SnapshotProject",
            "common-modules",
            "CycleA.bas");
        Assert.Equal(expectedCycleABytes, File.ReadAllBytes(installedCycleAPath));
        Assert.NotEqual(
            expectedCycleABytes,
            File.ReadAllBytes(Path.Combine(commonModulesRepository, "CycleA.bas")));
    }

    [Fact]
    public void NewRejectsFlatInstalledIdentityCollisionBeforeCopyingCommonModules()
    {
        using var temp = TempDirectory.Create();
        var commonModulesRepository = Path.Combine(temp.Path, "common_modules_repo");
        Directory.CreateDirectory(commonModulesRepository);
        WriteCommonModulesManifest(
            commonModulesRepository,
            ("Foo.bas", "optional", ""),
            ("FOO.cls", "optional", ""),
            ("Root.bas", "runtime-baseline", "Foo.bas,FOO.cls"));
        WriteModule(commonModulesRepository, "Foo.bas", "first foo");
        WriteModule(commonModulesRepository, "FOO.cls", "second foo");
        WriteModule(commonModulesRepository, "Root.bas", "root");
        var application = CommandLineTestFactory.Create(
            temp.Path,
            initialWorkbookCreator: new FakeInitialWorkbookCreator());

        var result = application.Run(["new", "excel", "--name", "SampleProject"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("duplicate CommonModuleName", result.StandardError, StringComparison.OrdinalIgnoreCase);
        var projectRoot = Path.Combine(temp.Path, "SampleProject");
        var sourceSet = Path.Combine(projectRoot, "src", "SampleProject");
        Assert.False(Directory.Exists(Path.Combine(sourceSet, "common-modules")));
        Assert.False(File.Exists(Path.Combine(projectRoot, ProjectManifest.ManifestFileName)));
    }

    [Fact]
    public void NewPreflightsEverySelectedCommonModulesSourceBeforeCopyingCommonModules()
    {
        using var temp = TempDirectory.Create();
        var commonModulesRepository = temp.CreateDirectory("common_modules_repo");
        WriteCommonModulesManifest(
            commonModulesRepository,
            ("First.bas", "runtime-baseline", ""),
            ("Missing.bas", "test-foundation", ""));
        WriteModule(commonModulesRepository, "First.bas", "first");
        var application = CommandLineTestFactory.Create(
            temp.Path,
            initialWorkbookCreator: new FakeInitialWorkbookCreator());

        var result = application.Run(["new", "excel", "--name", "SampleProject"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("source file was not found", result.StandardError, StringComparison.OrdinalIgnoreCase);
        var projectRoot = Path.Combine(temp.Path, "SampleProject");
        var sourceSet = Path.Combine(projectRoot, "src", "SampleProject");
        Assert.False(Directory.Exists(Path.Combine(sourceSet, "common-modules")));
        Assert.False(File.Exists(Path.Combine(projectRoot, ProjectManifest.ManifestFileName)));
    }

    private static void WriteCommonModulesManifest(string commonModulesRepository)
    {
        var text = string.Join(
            "\r\n",
            "# test manifest",
            "ModuleFile\tCategories\tDependencies\tRequiredReferences",
            "Core.bas\toptional\t\t[]",
            "Runtime.bas\truntime-baseline\tCore.bas\t[]",
            "UnitTest.bas\ttest-foundation\tRuntime.bas\t[]",
            "OptionalFeature.bas\toptional\t\t[]") + "\r\n";
        File.WriteAllText(
            Path.Combine(commonModulesRepository, "common-modules-manifest.tsv"),
            text,
            new UnicodeEncoding(false, true, true));
    }

    private static void WriteCommonModulesManifest(
        string commonModulesRepository,
        params (string ModuleFile, string Categories, string Dependencies)[] rows)
    {
        var lines = new List<string>
        {
            "ModuleFile\tCategories\tDependencies\tRequiredReferences"
        };
        lines.AddRange(rows.Select(row =>
            $"{row.ModuleFile}\t{row.Categories}\t{row.Dependencies}\t[]"));
        File.WriteAllText(
            Path.Combine(commonModulesRepository, "common-modules-manifest.tsv"),
            string.Join("\r\n", lines) + "\r\n",
            new UnicodeEncoding(false, true, true));
    }

    private static void WriteCommonModulesManifestWithReferences(
        string commonModulesRepository,
        params (
            string ModuleFile,
            string Categories,
            string Dependencies,
            string[] RequiredReferences)[] rows)
    {
        var lines = new List<string>
        {
            "ModuleFile\tCategories\tDependencies\tRequiredReferences"
        };
        lines.AddRange(rows.Select(row =>
            $"{row.ModuleFile}\t{row.Categories}\t{row.Dependencies}\t"
            + JsonSerializer.Serialize(row.RequiredReferences)));
        File.WriteAllText(
            Path.Combine(
                commonModulesRepository,
                CommonModulesManifestReader.ManifestFileName),
            string.Join("\r\n", lines) + "\r\n",
            new UnicodeEncoding(false, true, true));
    }

    private static void WriteModule(string directory, string fileName, string content)
    {
        var path = Path.Combine(directory, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var moduleName = Path.GetFileNameWithoutExtension(fileName);
        var source = Path.GetExtension(fileName) switch
        {
            ".cls" => "VERSION 1.0 CLASS\r\nBEGIN\r\nEND\r\n"
                + $"Attribute VB_Name = \"{moduleName}\"\r\n",
            ".frm" => "VERSION 5.00\r\n"
                + $"Attribute VB_Name = \"{moduleName}\"\r\n",
            _ => $"Attribute VB_Name = \"{moduleName}\"\r\n"
        };
        File.WriteAllText(
            path,
            source + $"' {content}\r\n",
            new UTF8Encoding(false));
    }

    private sealed class FailingNewProjectIdentityResolver : IFileSystemPathIdentityResolver
    {
        public FileSystemPathIdentity Resolve(string path)
            => throw new UnauthorizedAccessException(path);
    }

    private sealed class SequenceNewProjectIdentityResolver(
        params FileSystemPathIdentity[] identities) : IFileSystemPathIdentityResolver
    {
        private readonly Queue<FileSystemPathIdentity> remaining = new(identities);

        public FileSystemPathIdentity Resolve(string path)
            => remaining.Dequeue();
    }

    private sealed class CallbackOnNewProjectIdentityResolution(
        int triggerResolutionCount,
        Action callback) : IFileSystemPathIdentityResolver
    {
        private readonly FileSystemPathIdentityResolver inner = new();
        private int resolutionCount;

        public FileSystemPathIdentity Resolve(string path)
        {
            var identity = inner.Resolve(path);
            resolutionCount++;
            if (resolutionCount == triggerResolutionCount)
            {
                callback();
            }

            return identity;
        }
    }
}

internal sealed class FakeInitialWorkbookCreator : IInitialWorkbookCreator
{
    private readonly IReadOnlyList<string> referenceNames;

    public FakeInitialWorkbookCreator(params string[] referenceNames)
    {
        this.referenceNames = referenceNames;
    }

    public List<string> CreatedPaths { get; } = [];

    public Action<string>? AfterCreate { get; init; }

    public InitialWorkbookCreationResult CreateInitialWorkbook(string workbookPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(workbookPath)!);
        File.WriteAllText(workbookPath, "fake xlsm", new UTF8Encoding(false));
        var evidence = InitialWorkbookTestArtifactEvidence.Capture(workbookPath);
        CreatedPaths.Add(workbookPath);
        AfterCreate?.Invoke(workbookPath);
        return new InitialWorkbookCreationResult(referenceNames, evidence);
    }
}
