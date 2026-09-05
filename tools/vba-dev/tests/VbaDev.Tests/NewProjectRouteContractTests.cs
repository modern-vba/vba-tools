using System.Text;
using System.Text.Json;
using VbaDev.App.CommonModules;
using VbaDev.App.Projects;
using VbaDev.App.References;
using VbaDev.Cli;
using VbaDev.Composition;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;
using Xunit;

namespace VbaDev.Tests;

public sealed class NewProjectRouteContractTests
{
    [Fact]
    public void AliasRouteRemainsUserVisibleAndAnotherAliasConvergesOnThePhysicalTarget()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var physicalParent = temp.CreateDirectory("physical-parent");
        var firstAlias = Path.Combine(temp.Path, "first-alias");
        var secondAlias = Path.Combine(temp.Path, "second-alias");
        if (!TryCreateDirectoryAlias(firstAlias, physicalParent))
        {
            return;
        }

        if (!TryCreateDirectoryAlias(secondAlias, physicalParent))
        {
            Directory.Delete(firstAlias);
            return;
        }

        try
        {
            var firstCreator = new FakeInitialWorkbookCreator();
            var firstApplication = CommandLineTestFactory.Create(
                temp.Path,
                initialWorkbookCreator: firstCreator);
            var requestedRoot = Path.Combine(firstAlias, "AliasProject");

            var firstResult = firstApplication.Run([
                "new",
                "excel",
                "--name",
                "AliasProject",
                "--output",
                requestedRoot,
                "--format",
                "json"
            ]);

            Assert.True(firstResult.ExitCode == 0, firstResult.StandardError);
            using var receipt = JsonDocument.Parse(firstResult.StandardOutput);
            Assert.Equal(
                Path.GetFullPath(requestedRoot),
                receipt.RootElement.GetProperty("project").GetString());
            Assert.Equal(
                Path.Combine(
                    Path.GetFullPath(requestedRoot),
                    ProjectManifest.ManifestFileName),
                receipt.RootElement.GetProperty("manifestPath").GetString());

            var physicalRoot = Path.Combine(physicalParent, "AliasProject");
            var physicalWorkbookPath = Path.Combine(
                physicalRoot,
                "src",
                "AliasProject",
                "AliasProject.xlsm");
            Assert.True(File.Exists(physicalWorkbookPath));
            Assert.Equal(
                Path.GetFullPath(physicalWorkbookPath),
                Assert.Single(firstCreator.CreatedPaths),
                ignoreCase: true);
            var physicalManifestPath = Path.Combine(
                physicalRoot,
                ProjectManifest.ManifestFileName);
            var committedManifest = File.ReadAllBytes(physicalManifestPath);

            var secondCreator = new FakeInitialWorkbookCreator();
            var secondApplication = CommandLineTestFactory.Create(
                temp.Path,
                initialWorkbookCreator: secondCreator);
            var secondResult = secondApplication.Run([
                "new",
                "excel",
                "--name",
                "AliasProject",
                "--output",
                Path.Combine(secondAlias, "AliasProject"),
                "--format",
                "json"
            ]);

            Assert.Equal(1, secondResult.ExitCode);
            Assert.Empty(secondResult.StandardOutput);
            Assert.Contains(
                "newProjectTargetChanged",
                secondResult.StandardError,
                StringComparison.Ordinal);
            Assert.Empty(secondCreator.CreatedPaths);
            Assert.Equal(committedManifest, File.ReadAllBytes(physicalManifestPath));
        }
        finally
        {
            Directory.Delete(secondAlias);
            Directory.Delete(firstAlias);
        }
    }

    [Fact]
    public void UncOutputPassesLexicalValidationBeforePhysicalIdentityResolution()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        const string uncOutput = @"\\unit-test-server\unit-test-share\UncProject";
        var identityResolver = new RecordingRejectingIdentityResolver();
        var workbookCreator = new FakeInitialWorkbookCreator();
        var command = CreateCommand(
            workbookCreator,
            identityResolver);
        var composition = ToolingCompositionRoot.CreateApplicationComposition(
            temp.Path,
            initialWorkbookCreator: workbookCreator);
        var application = VbaDevCommandLine.Create(
            composition with { NewProjectCommand = command });

        var result = application.Run([
            "new",
            "excel",
            "--name",
            "UncProject",
            "--output",
            uncOutput,
            "--format",
            "json"
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.DoesNotContain(
            "projectOutputNotWindowsFilesystemPath",
            result.StandardError,
            StringComparison.Ordinal);
        Assert.Equal(
            Path.GetFullPath(uncOutput),
            Assert.Single(identityResolver.RequestedPaths));
        Assert.Empty(workbookCreator.CreatedPaths);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Fact]
    public void DistinctAliasesStillRejectAnAncestorDocumentSourceSetOverlap()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var ancestorRoot = temp.CreateDirectory("AncestorProject");
        new JsonProjectManifestStore().Save(
            ancestorRoot,
            ProjectManifest.CreateDefault(
                "AncestorProject",
                "AncestorBook",
                ancestorRoot,
                null));
        var sharedPhysicalSource = temp.CreateDirectory("shared-physical-source");
        var sourceParent = Directory.CreateDirectory(
            Path.Combine(ancestorRoot, "src")).FullName;
        var sourceAlias = Path.Combine(sourceParent, "AncestorBook");
        if (!TryCreateDirectoryAlias(sourceAlias, sharedPhysicalSource))
        {
            return;
        }

        var projectsParent = Directory.CreateDirectory(
            Path.Combine(ancestorRoot, "projects")).FullName;
        var routeAlias = Path.Combine(projectsParent, "route-alias");
        if (!TryCreateDirectoryAlias(routeAlias, sharedPhysicalSource))
        {
            Directory.Delete(sourceAlias);
            return;
        }

        try
        {
            var workbookCreator = new FakeInitialWorkbookCreator();
            var application = CommandLineTestFactory.Create(
                temp.Path,
                initialWorkbookCreator: workbookCreator);
            var requestedRoot = Path.Combine(routeAlias, "ChildProject");

            var result = application.Run([
                "new",
                "excel",
                "--name",
                "ChildProject",
                "--output",
                requestedRoot
            ]);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("AncestorBook", result.StandardError, StringComparison.Ordinal);
            Assert.Contains(
                "src/AncestorBook",
                result.StandardError,
                StringComparison.Ordinal);
            Assert.Empty(workbookCreator.CreatedPaths);
            Assert.False(Directory.Exists(Path.Combine(
                sharedPhysicalSource,
                "ChildProject")));
        }
        finally
        {
            Directory.Delete(routeAlias);
            Directory.Delete(sourceAlias);
        }
    }

    [Fact]
    public void AliasCannotHideAnExcelBracketInThePhysicalWorkbookPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var physicalRoot = temp.CreateDirectory("physical[root]");
        var aliasRoot = Path.Combine(temp.Path, "short-route");
        if (!TryCreateDirectoryAlias(aliasRoot, physicalRoot))
        {
            return;
        }

        try
        {
            var workbookCreator = new FakeInitialWorkbookCreator();
            var application = CommandLineTestFactory.Create(
                temp.Path,
                initialWorkbookCreator: workbookCreator);

            var result = application.Run([
                "new",
                "excel",
                "--name",
                "Project",
                "--output",
                aliasRoot,
                "--format",
                "json"
            ]);

            Assert.Equal(1, result.ExitCode);
            Assert.Empty(result.StandardOutput);
            Assert.Contains(
                ProjectCreationPathValidationReasons.ExcelPathContainsUnsupportedCharacter,
                result.StandardError,
                StringComparison.Ordinal);
            Assert.Empty(workbookCreator.CreatedPaths);
            Assert.Empty(Directory.EnumerateFileSystemEntries(physicalRoot));
        }
        finally
        {
            Directory.Delete(aliasRoot);
        }
    }

    [Fact]
    public void AliasCannotHideAPhysicalWorkbookPathOver218Utf16CodeUnits()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        const string projectName = "P";
        var physicalComponentLength = 1;
        string physicalRoot;
        string physicalWorkbookPath;
        do
        {
            physicalRoot = Path.Combine(
                temp.Path,
                new string('p', physicalComponentLength));
            physicalWorkbookPath = Path.Combine(
                physicalRoot,
                "src",
                projectName,
                $"{projectName}.xlsm");
            physicalComponentLength++;
        }
        while (physicalWorkbookPath.Length
            <= ExcelWorkbookPathContract.MaximumUtf16CodeUnitLength);

        Directory.CreateDirectory(physicalRoot);
        var aliasRoot = Path.Combine(temp.Path, "short-route");
        if (!TryCreateDirectoryAlias(aliasRoot, physicalRoot))
        {
            return;
        }

        try
        {
            Assert.Equal(
                ExcelWorkbookPathContract.MaximumUtf16CodeUnitLength + 1,
                physicalWorkbookPath.Length);
            Assert.True(Path.Combine(
                aliasRoot,
                "src",
                projectName,
                $"{projectName}.xlsm").Length
                <= ExcelWorkbookPathContract.MaximumUtf16CodeUnitLength);
            var workbookCreator = new FakeInitialWorkbookCreator();
            var application = CommandLineTestFactory.Create(
                temp.Path,
                initialWorkbookCreator: workbookCreator);

            var result = application.Run([
                "new",
                "excel",
                "--name",
                projectName,
                "--output",
                aliasRoot,
                "--format",
                "json"
            ]);

            Assert.Equal(1, result.ExitCode);
            Assert.Empty(result.StandardOutput);
            Assert.Contains(
                ProjectCreationPathValidationReasons.ExcelPathTooLong,
                result.StandardError,
                StringComparison.Ordinal);
            Assert.Empty(workbookCreator.CreatedPaths);
            Assert.Empty(Directory.EnumerateFileSystemEntries(physicalRoot));
        }
        finally
        {
            Directory.Delete(aliasRoot);
        }
    }

    [Fact]
    public void DirectRootAliasRejectsAPackageRouteThatWouldNotRemainDurable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var lexicalParent = temp.CreateDirectory("lexical-parent");
        var physicalParent = temp.CreateDirectory("physical-parent");
        var physicalRoot = Directory.CreateDirectory(
            Path.Combine(physicalParent, "AliasProject")).FullName;
        _ = Directory.CreateDirectory(Path.Combine(
            physicalParent,
            "common_modules_repo"));
        var requestedRoot = Path.Combine(lexicalParent, "AliasProject");
        if (!TryCreateDirectoryAlias(requestedRoot, physicalRoot))
        {
            return;
        }

        try
        {
            var workbookCreator = new FakeInitialWorkbookCreator();
            var application = CommandLineTestFactory.Create(
                temp.Path,
                initialWorkbookCreator: workbookCreator);

            var result = application.Run([
                "new",
                "excel",
                "--name",
                "AliasProject",
                "--output",
                requestedRoot,
                "--format",
                "json"
            ]);

            Assert.Equal(1, result.ExitCode);
            Assert.Empty(result.StandardOutput);
            Assert.Contains(
                "durable CommonModules repository route",
                result.StandardError,
                StringComparison.Ordinal);
            Assert.Empty(workbookCreator.CreatedPaths);
            Assert.Empty(Directory.EnumerateFileSystemEntries(physicalRoot));
        }
        finally
        {
            Directory.Delete(requestedRoot);
        }
    }

    [Fact]
    public void DurablePackageRouteReplacementBeforeCommitIsPreservedAndRejected()
    {
        using var temp = TempDirectory.Create();
        var repository = temp.CreateDirectory("common_modules_repo");
        WriteMinimalCommonModulesPackage(repository);
        var displacedRepository = Path.Combine(
            temp.Path,
            "common_modules_repo.displaced");
        var foreignPath = Path.Combine(repository, "foreign.txt");
        var identityResolver = new ArmedRepositoryReplacementIdentityResolver(
            repository,
            displacedRepository,
            foreignPath);
        var projectRoot = Path.Combine(temp.Path, "RepositoryRouteProject");
        var workbookCreator = new FakeInitialWorkbookCreator
        {
            AfterCreate = _ => identityResolver.Arm()
        };
        var command = new NewProjectCommand(
            new JsonProjectManifestStore(),
            workbookCreator,
            new CommonModulesManifestReader(),
            new VbaProjectReferencePlanner(
                new FakeVbaProjectReferenceResolver()),
            new ProjectManifestMutationLeaseProvider(),
            identityResolver);

        var result = command.Run(new NewProjectCommandRequest(
            "RepositoryRouteProject",
            DocumentName: null,
            projectRoot,
            temp.Path,
            ProjectNameSpecified: true,
            OutputDirectorySpecified: true,
            Format: "json"));

        Assert.True(identityResolver.ReplacementOccurred);
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains(
            "durable CommonModules repository route changed",
            result.StandardError,
            StringComparison.Ordinal);
        Assert.True(File.Exists(foreignPath));
        Assert.Equal("foreign repository", File.ReadAllText(foreignPath));
        Assert.False(File.Exists(Path.Combine(
            projectRoot,
            ProjectManifest.ManifestFileName)));
    }

    [Fact]
    public void ProspectiveRootRejectsAnAncestorSourceSetReservedBelowIt()
    {
        using var temp = TempDirectory.Create();
        var ancestorRoot = temp.CreateDirectory("AncestorProject");
        var prospectiveRoot = Path.Combine(
            ancestorRoot,
            "projects",
            "ChildProject");
        var ancestorManifest = ProjectManifest.CreateDefault(
            "AncestorProject",
            "AncestorBook",
            ancestorRoot,
            null);
        ancestorManifest.Documents["AncestorBook"] =
            ancestorManifest.Documents["AncestorBook"] with
            {
                SourcePath = "projects/ChildProject/reserved-source"
            };
        new JsonProjectManifestStore().Save(ancestorRoot, ancestorManifest);
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
            prospectiveRoot,
            "--format",
            "json"
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("AncestorBook", result.StandardError, StringComparison.Ordinal);
        Assert.Contains(
            "projects/ChildProject/reserved-source",
            result.StandardError,
            StringComparison.Ordinal);
        Assert.Empty(workbookCreator.CreatedPaths);
        Assert.False(Directory.Exists(prospectiveRoot));
    }

    [Fact]
    public void RootEstablishedByTheLeaseCannotBeReplacedBeforeCommit()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "RootSwapProject");
        var displacedOwnedRoot = Path.Combine(temp.Path, "displaced-owned-root");
        var foreignPath = Path.Combine(projectRoot, "foreign-y.txt");
        var identityResolver = new ArmedRootReplacementIdentityResolver(
            projectRoot,
            displacedOwnedRoot,
            foreignPath);
        var workbookCreator = new FakeInitialWorkbookCreator
        {
            AfterCreate = _ => identityResolver.Arm()
        };
        var leaseProvider = new TestProjectCreationLeaseProvider(
            displacedOwnedRoot);
        var command = new NewProjectCommand(
            new JsonProjectManifestStore(),
            workbookCreator,
            new CommonModulesManifestReader(),
            new VbaProjectReferencePlanner(
                new FakeVbaProjectReferenceResolver()),
            leaseProvider,
            identityResolver);

        var result = command.Run(new NewProjectCommandRequest(
            "RootSwapProject",
            DocumentName: null,
            projectRoot,
            temp.Path,
            ProjectNameSpecified: true,
            OutputDirectorySpecified: true,
            Format: "json"));

        Assert.True(identityResolver.ReplacementAttempted);
        Assert.Single(workbookCreator.CreatedPaths);
        Assert.True(leaseProvider.Released);
        if (!identityResolver.ReplacementOccurred)
        {
            // Windows can prevent ancestor renaming while descendant receipt
            // anchors are live. A rejected external mutation is not a failed
            // project operation when the original root identity remains valid.
            var failure = Assert.IsType<IOException>(identityResolver.ReplacementFailure);
            Assert.Contains(failure.HResult & 0xffff, new[] { 5, 32 });
            Assert.Equal(0, result.ExitCode);
            Assert.Empty(result.StandardError);
            Assert.False(File.Exists(foreignPath));
            Assert.True(File.Exists(Path.Combine(projectRoot, ProjectManifest.ManifestFileName)));
            Assert.True(File.Exists(workbookCreator.CreatedPaths[0]));

            // Terminal success must release the anchors that prevented rename.
            Directory.Move(projectRoot, displacedOwnedRoot);
            Assert.True(File.Exists(Path.Combine(displacedOwnedRoot, ProjectManifest.ManifestFileName)));
            return;
        }

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains(
            "newProjectTargetChanged",
            result.StandardError,
            StringComparison.Ordinal);
        Assert.Single(workbookCreator.CreatedPaths);
        Assert.True(File.Exists(foreignPath));
        Assert.Equal("foreign root Y", File.ReadAllText(foreignPath));
        Assert.False(File.Exists(Path.Combine(
            projectRoot,
            ProjectManifest.ManifestFileName)));
        Assert.True(leaseProvider.Released);
    }

    private static NewProjectCommand CreateCommand(
        FakeInitialWorkbookCreator workbookCreator,
        IFileSystemPathIdentityResolver identityResolver)
        => new(
            new JsonProjectManifestStore(),
            workbookCreator,
            new CommonModulesManifestReader(),
            new VbaProjectReferencePlanner(
                new FakeVbaProjectReferenceResolver()),
            new ProjectManifestMutationLeaseProvider(),
            identityResolver);

    private static bool TryCreateDirectoryAlias(string aliasPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(aliasPath, targetPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static void WriteMinimalCommonModulesPackage(string repository)
    {
        var manifest = string.Join(
            "\r\n",
            "ModuleFile\tCategories\tDependencies\tRequiredReferences",
            "OptionalFeature.bas\toptional\t\t[]") + "\r\n";
        File.WriteAllText(
            Path.Combine(
                repository,
                CommonModulesManifestReader.ManifestFileName),
            manifest,
            new UnicodeEncoding(
                bigEndian: false,
                byteOrderMark: true,
                throwOnInvalidBytes: true));
        File.WriteAllText(
            Path.Combine(repository, "OptionalFeature.bas"),
            "Attribute VB_Name = \"OptionalFeature\"\r\nOption Explicit\r\n",
            new UTF8Encoding(false));
    }

    private sealed class RecordingRejectingIdentityResolver
        : IFileSystemPathIdentityResolver
    {
        public List<string> RequestedPaths { get; } = [];

        public FileSystemPathIdentity Resolve(string path)
        {
            RequestedPaths.Add(path);
            throw new UnauthorizedAccessException(
                "The UNC identity probe intentionally stopped after lexical validation.");
        }
    }

    private sealed class ArmedRootReplacementIdentityResolver(
        string projectRoot,
        string displacedOwnedRoot,
        string foreignPath) : IFileSystemPathIdentityResolver
    {
        private readonly FileSystemPathIdentityResolver inner = new();
        private bool armed;

        public bool ReplacementOccurred { get; private set; }

        public bool ReplacementAttempted { get; private set; }

        public Exception? ReplacementFailure { get; private set; }

        public void Arm()
            => armed = true;

        public FileSystemPathIdentity Resolve(string path)
        {
            if (armed && !ReplacementAttempted)
            {
                ReplacementAttempted = true;
                try
                {
                    Directory.Move(projectRoot, displacedOwnedRoot);
                }
                catch (IOException exception) when ((exception.HResult & 0xffff) is 5 or 32)
                {
                    ReplacementFailure = exception;
                    return inner.Resolve(path);
                }

                Directory.CreateDirectory(projectRoot);
                File.WriteAllText(foreignPath, "foreign root Y");
                ReplacementOccurred = true;
            }

            return inner.Resolve(path);
        }
    }

    private sealed class ArmedRepositoryReplacementIdentityResolver(
        string repository,
        string displacedRepository,
        string foreignPath) : IFileSystemPathIdentityResolver
    {
        private readonly FileSystemPathIdentityResolver inner = new();
        private bool armed;

        public bool ReplacementOccurred { get; private set; }

        public void Arm()
            => armed = true;

        public FileSystemPathIdentity Resolve(string path)
        {
            if (armed
                && !ReplacementOccurred
                && Path.GetFullPath(path).Equals(
                    Path.GetFullPath(repository),
                    StringComparison.OrdinalIgnoreCase))
            {
                Directory.Move(repository, displacedRepository);
                Directory.CreateDirectory(repository);
                File.WriteAllText(
                    foreignPath,
                    "foreign repository",
                    new UTF8Encoding(false));
                ReplacementOccurred = true;
            }

            return inner.Resolve(path);
        }
    }

    private sealed class TestProjectCreationLeaseProvider(
        string displacedOwnedRoot) : IProjectManifestMutationLeaseProvider
    {
        public bool Released { get; private set; }

        public ValueTask<IProjectManifestMutationLease> AcquireAsync(
            string projectRoot,
            ProjectManifestMutationCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(ProjectManifestMutationCommand.NewExcel, command);
            var identity = new FileSystemPathIdentityResolver().Resolve(projectRoot);
            var manifestPath = Path.Combine(
                identity.OperationPath,
                ProjectManifest.ManifestFileName);
            var markerPath = manifestPath + ".vba-dev.lock";
            File.WriteAllText(markerPath, "owned test marker");
            return ValueTask.FromResult<IProjectManifestMutationLease>(
                new TestProjectCreationLease(
                    identity,
                    manifestPath,
                    markerPath,
                    displacedOwnedRoot,
                    () => Released = true));
        }
    }

    private sealed class TestProjectCreationLease(
        FileSystemPathIdentity projectIdentity,
        string manifestPath,
        string markerPath,
        string displacedOwnedRoot,
        Action onReleased) : IProjectManifestMutationLease
    {
        public FileSystemPathIdentity ProjectIdentity { get; } = projectIdentity;

        public string ManifestPath { get; } = manifestPath;

        public void ProveOwnershipContinuity()
        {
        }

        public ValueTask<ProjectManifestLeaseRelease> ReleaseAsync()
        {
            DeleteIfPresent(markerPath);
            DeleteIfPresent(Path.Combine(
                displacedOwnedRoot,
                Path.GetFileName(markerPath)));
            onReleased();
            return ValueTask.FromResult(new ProjectManifestLeaseRelease([]));
        }

        private static void DeleteIfPresent(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
