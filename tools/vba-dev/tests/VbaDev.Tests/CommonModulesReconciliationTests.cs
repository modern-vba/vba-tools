using System.Text;
using VbaDev.App.CommonModules;
using VbaDev.App.Projects;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;
using Xunit;

namespace VbaDev.Tests;

public sealed class CommonModulesReconciliationTests
{
    [Fact]
    public void UpdateRetainsAConclusiveMissingIdentityAsAnOrphan()
    {
        using var temp = TempDirectory.Create();
        var (root, repository) = CreateProject(temp);
        WritePackageModule(repository, "Other.bas", "other");
        WriteManifest(repository, "Other.bas");
        var sourcePath = Path.Combine(root, "src", "Book1", "Feature.bas");
        WriteModule(sourcePath, "Feature", "local feature");
        SetInstalled(root, new InstalledCommonModule(
            "Feature",
            "Feature.bas",
            Requested: true,
            TestOnly: false,
            Orphaned: false));

        var result = CommandLineTestFactory.Create(root).Run(["common-module", "update"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("orphanedCommonModulesRetained", result.StandardError, StringComparison.Ordinal);
        Assert.Equal("local feature", ReadBody(sourcePath));
        var installed = Assert.Single(LoadInstalled(root));
        Assert.True(installed.Orphaned);
        Assert.True(installed.Requested);
        Assert.False(installed.TestOnly);
    }

    [Fact]
    public void UpdateClearsAnOrphanOnlyWithCanonicalSourceAndMetadataRefresh()
    {
        using var temp = TempDirectory.Create();
        var (root, repository) = CreateProject(temp);
        WritePackageModule(repository, "Feature.bas", "repository feature");
        WriteManifest(repository, "Feature.bas");
        var sourcePath = Path.Combine(root, "src", "Book1", "Feature.bas");
        WriteModule(sourcePath, "Feature", "local feature");
        SetInstalled(root, new InstalledCommonModule(
            "Feature",
            "Feature.bas",
            Requested: true,
            TestOnly: true,
            Orphaned: true));

        var result = CommandLineTestFactory.Create(root).Run(["common-module", "update"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("repository feature", ReadBody(sourcePath));
        var installed = Assert.Single(LoadInstalled(root));
        Assert.False(installed.Orphaned);
        Assert.False(installed.TestOnly);
    }

    [Fact]
    public void UpdateAppliesCanonicalCaseToManifestSourceAndFormSidecar()
    {
        using var temp = TempDirectory.Create();
        var (root, repository) = CreateProject(temp);
        WritePackageModule(repository, "Feature.frm", "repository form");
        File.WriteAllBytes(Path.Combine(repository, "Feature.frx"), [1, 2, 3]);
        WriteManifest(repository, "Feature.frm");
        var sourceDirectory = Path.Combine(root, "src", "Book1");
        var oldForm = Path.Combine(sourceDirectory, "feature.frm");
        var oldSidecar = Path.Combine(sourceDirectory, "feature.frx");
        WriteModule(oldForm, "feature", "local form", form: true);
        File.WriteAllBytes(oldSidecar, [9]);
        SetInstalled(root, new InstalledCommonModule(
            "feature",
            "feature.frm",
            Requested: true,
            TestOnly: false,
            Orphaned: false));

        var result = CommandLineTestFactory.Create(root).Run(["common-module", "update"]);

        Assert.Equal(0, result.ExitCode);
        var canonicalForm = Assert.Single(
            Directory.EnumerateFiles(sourceDirectory, "*.frm", SearchOption.AllDirectories));
        var canonicalSidecar = Assert.Single(
            Directory.EnumerateFiles(sourceDirectory, "*.frx", SearchOption.AllDirectories));
        Assert.Equal("Feature.frm", Path.GetFileName(canonicalForm));
        Assert.Equal("Feature.frx", Path.GetFileName(canonicalSidecar));
        Assert.Equal("repository form", ReadBody(canonicalForm));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(canonicalSidecar));
        var installed = Assert.Single(LoadInstalled(root));
        Assert.Equal("Feature", installed.Name);
        Assert.Equal("Feature.frm", installed.ModuleFile);
    }

    [Fact]
    public void UpdateRejectsARepositoryKindChangeForTheSameIdentityWithoutMutation()
    {
        using var temp = TempDirectory.Create();
        var (root, repository) = CreateProject(temp);
        WritePackageModule(repository, "Feature.cls", "repository class", classModule: true);
        WriteManifest(repository, "Feature.cls");
        var sourcePath = Path.Combine(root, "src", "Book1", "Feature.bas");
        WriteModule(sourcePath, "Feature", "local feature");
        var installedBefore = new InstalledCommonModule(
            "Feature",
            "Feature.bas",
            Requested: true,
            TestOnly: false,
            Orphaned: true);
        SetInstalled(root, installedBefore);

        var result = CommandLineTestFactory.Create(root).Run(["common-module", "update"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("source identity changed", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("local feature", ReadBody(sourcePath));
        Assert.Equal(installedBefore, Assert.Single(LoadInstalled(root)));
    }

    [Fact]
    public void AddPromotesAnInstalledOrphanWithoutReadingTheRepository()
    {
        using var temp = TempDirectory.Create();
        var (root, repository) = CreateProject(temp);
        Directory.Delete(repository, recursive: true);
        var sourcePath = Path.Combine(root, "src", "Book1", "Feature.bas");
        WriteModule(sourcePath, "Feature", "local feature");
        SetInstalled(root, new InstalledCommonModule(
            "Feature",
            "Feature.bas",
            Requested: false,
            TestOnly: true,
            Orphaned: true));

        var result = CommandLineTestFactory.Create(root).Run([
            "common-module", "add", "Feature"
        ]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("local feature", ReadBody(sourcePath));
        var installed = Assert.Single(LoadInstalled(root));
        Assert.True(installed.Requested);
        Assert.True(installed.TestOnly);
        Assert.True(installed.Orphaned);
    }

    [Fact]
    public async Task CancellationAfterTheFirstSourceReplacementDefersThroughManifestCommit()
    {
        using var temp = TempDirectory.Create();
        var (root, repository) = CreateProject(temp);
        WritePackageModule(repository, "First.bas", "repository first");
        WritePackageModule(repository, "Second.bas", "repository second");
        WriteManifest(repository, "First.bas", "Second.bas");
        var first = Path.Combine(root, "src", "Book1", "First.bas");
        var second = Path.Combine(root, "src", "Book1", "Second.bas");
        WriteModule(first, "First", "local first");
        WriteModule(second, "Second", "local second");
        SetInstalled(
            root,
            new InstalledCommonModule("First", "First.bas", true, false, false),
            new InstalledCommonModule("Second", "Second.bas", true, false, false));
        using var cancellation = new CancellationTokenSource();
        var transaction = CreateTransaction(
            temp,
            new CommonModulesSourceMutationWriter(beforeOperation: index =>
            {
                if (index == 1)
                {
                    cancellation.Cancel();
                }
            }));
        var project = ResolveProject(root);

        var completion = await transaction.UpdateAsync(project, cancellation.Token);

        Assert.Equal("repository first", ReadBody(first));
        Assert.Equal("repository second", ReadBody(second));
        Assert.Contains(completion.Warnings, warning => warning.Code == "cancellationDeferred");
        Assert.Empty(Directory.EnumerateFiles(root, "vba-project.failed-*.json"));
    }

    [Fact]
    public async Task CancellationImmediatelyBeforeTheFirstSourceMutationLeavesProjectUnchanged()
    {
        using var temp = TempDirectory.Create();
        var (root, repository) = CreateProject(temp);
        WritePackageModule(repository, "Feature.bas", "repository feature");
        WriteManifest(repository, "Feature.bas");
        var manifestPath = Path.Combine(root, ProjectManifest.ManifestFileName);
        var manifestBefore = File.ReadAllBytes(manifestPath);
        using var cancellation = new CancellationTokenSource();
        var transaction = CreateTransaction(
            temp,
            new CommonModulesSourceMutationWriter(beforeOperation: _ => cancellation.Cancel()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            transaction.AddAsync(
                ResolveContext(root),
                ["Feature"],
                force: false,
                cancellation.Token));

        Assert.Equal(manifestBefore, File.ReadAllBytes(manifestPath));
        Assert.False(File.Exists(Path.Combine(
            root,
            "src",
            "Book1",
            "common-modules",
            "Feature.bas")));
        Assert.Empty(Directory.EnumerateFiles(root, "vba-project.failed-*.json"));
    }

    [Fact]
    public async Task StagingFailureBeforeFirstCreateLeavesProjectUnchangedWithoutRecovery()
    {
        using var temp = TempDirectory.Create();
        var (root, repository) = CreateProject(temp);
        WritePackageModule(repository, "Feature.bas", "repository feature");
        WriteManifest(repository, "Feature.bas");
        var manifestPath = Path.Combine(root, ProjectManifest.ManifestFileName);
        var manifestBefore = File.ReadAllBytes(manifestPath);
        var commonModulesDirectory = Path.Combine(root, "src", "Book1", "common-modules");
        var transaction = CreateTransaction(
            temp,
            new CommonModulesSourceMutationWriter(
                afterTemporaryFileFlushed: _ => throw new IOException("staging failed")));

        await Assert.ThrowsAsync<CommonModulesTransactionException>(() =>
            transaction.AddAsync(
                ResolveContext(root),
                ["Feature"],
                force: false,
                CancellationToken.None));

        Assert.Equal(manifestBefore, File.ReadAllBytes(manifestPath));
        Assert.False(Directory.Exists(commonModulesDirectory));
        Assert.Empty(Directory.EnumerateFiles(root, "*.vba-dev.*.tmp", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(root, "vba-project.failed-*.json"));
    }

    [Fact]
    public async Task LateSourceConflictKeepsExternalBytesAndPersistsRecoveryWithAllPaths()
    {
        using var temp = TempDirectory.Create();
        var (root, repository) = CreateProject(temp);
        WritePackageModule(repository, "First.bas", "repository first");
        WritePackageModule(repository, "Second.bas", "repository second");
        WriteManifest(repository, "First.bas", "Second.bas");
        var first = Path.Combine(root, "src", "Book1", "First.bas");
        var second = Path.Combine(root, "src", "Book1", "Second.bas");
        WriteModule(first, "First", "local first");
        WriteModule(second, "Second", "local second");
        SetInstalled(
            root,
            new InstalledCommonModule("First", "First.bas", true, false, false),
            new InstalledCommonModule("Second", "Second.bas", true, false, false));
        var transaction = CreateTransaction(
            temp,
            new CommonModulesSourceMutationWriter(beforeOperation: index =>
            {
                if (index == 1)
                {
                    WriteModule(second, "Second", "external second");
                }
            }));
        var manifestPath = Path.Combine(root, ProjectManifest.ManifestFileName);
        var manifestBefore = File.ReadAllBytes(manifestPath);

        var error = await Assert.ThrowsAsync<CommonModulesTransactionException>(() =>
            transaction.UpdateAsync(ResolveProject(root), CancellationToken.None));

        Assert.Equal("repository first", ReadBody(first));
        Assert.Equal("external second", ReadBody(second));
        Assert.Equal(manifestBefore, File.ReadAllBytes(manifestPath));
        Assert.Contains(Path.GetFullPath(first), error.Message, StringComparison.Ordinal);
        Assert.Contains(Path.GetFullPath(second), error.Message, StringComparison.Ordinal);
        var recovery = Assert.Single(Directory.EnumerateFiles(root, "vba-project.failed-*.json"));
        Assert.Contains(recovery, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForceDoesNotOverwriteAnEditMadeAfterTheExactTargetWasPlanned()
    {
        using var temp = TempDirectory.Create();
        var (root, repository) = CreateProject(temp);
        WritePackageModule(repository, "Feature.bas", "repository feature");
        WriteManifest(repository, "Feature.bas");
        var target = Path.Combine(root, "src", "Book1", "Feature.bas");
        WriteModule(target, "Feature", "observed feature");
        var transaction = CreateTransaction(
            temp,
            new CommonModulesSourceMutationWriter(beforeOperation: _ =>
                WriteModule(target, "Feature", "external feature")));
        var manifestPath = Path.Combine(root, ProjectManifest.ManifestFileName);
        var manifestBefore = File.ReadAllBytes(manifestPath);

        await Assert.ThrowsAsync<CommonModulesTransactionException>(() =>
            transaction.AddAsync(
                ResolveContext(root),
                ["Feature"],
                force: true,
                CancellationToken.None));

        Assert.Equal("external feature", ReadBody(target));
        Assert.Equal(manifestBefore, File.ReadAllBytes(manifestPath));
        Assert.Empty(Directory.EnumerateFiles(root, "vba-project.failed-*.json"));
    }

    [Fact]
    public async Task AcceptedSnapshotBytesRemainAuthoritativeAfterTheLivePackageChanges()
    {
        using var temp = TempDirectory.Create();
        var (root, repository) = CreateProject(temp);
        WritePackageModule(repository, "Feature.bas", "captured feature");
        WriteManifest(repository, "Feature.bas");
        var transaction = CreateTransaction(
            temp,
            new CommonModulesSourceMutationWriter(beforeOperation: _ =>
                WritePackageModule(repository, "Feature.bas", "later feature")));

        await transaction.AddAsync(
            ResolveContext(root),
            ["Feature"],
            force: false,
            CancellationToken.None);

        Assert.Equal(
            "captured feature",
            ReadBody(Path.Combine(root, "src", "Book1", "common-modules", "Feature.bas")));
        Assert.Equal("later feature", ReadBody(Path.Combine(repository, "Feature.bas")));
    }

    [Fact]
    public async Task UpdateWithCanonicalBytesDoesNotChurnTheSourceTimestamp()
    {
        using var temp = TempDirectory.Create();
        var (root, repository) = CreateProject(temp);
        WritePackageModule(repository, "Feature.bas", "canonical feature");
        WriteManifest(repository, "Feature.bas");
        var target = Path.Combine(root, "src", "Book1", "Feature.bas");
        File.Copy(Path.Combine(repository, "Feature.bas"), target);
        var fixedTimestamp = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(target, fixedTimestamp);
        SetInstalled(root, new InstalledCommonModule(
            "Feature",
            "Feature.bas",
            Requested: true,
            TestOnly: false,
            Orphaned: false));
        var transaction = CreateTransaction(temp, new CommonModulesSourceMutationWriter());

        await transaction.UpdateAsync(ResolveProject(root), CancellationToken.None);

        Assert.Equal(fixedTimestamp, File.GetLastWriteTimeUtc(target));
    }

    [Fact]
    public async Task ReappearedOrphanRevalidatesCanonicalBytesBeforeClearingItsMarker()
    {
        using var temp = TempDirectory.Create();
        var (root, repository) = CreateProject(temp);
        WritePackageModule(repository, "Feature.bas", "canonical feature");
        WriteManifest(repository, "Feature.bas");
        var target = Path.Combine(root, "src", "Book1", "Feature.bas");
        File.Copy(Path.Combine(repository, "Feature.bas"), target);
        SetInstalled(root, new InstalledCommonModule(
            "Feature",
            "Feature.bas",
            Requested: true,
            TestOnly: false,
            Orphaned: true));
        var transaction = CreateTransaction(
            temp,
            new CommonModulesSourceMutationWriter(beforeOperation: _ =>
                WriteModule(target, "Feature", "external feature")));

        await Assert.ThrowsAsync<CommonModulesTransactionException>(() =>
            transaction.UpdateAsync(ResolveProject(root), CancellationToken.None));

        Assert.Equal("external feature", ReadBody(target));
        Assert.True(Assert.Single(LoadInstalled(root)).Orphaned);
        Assert.Empty(Directory.EnumerateFiles(root, "vba-project.failed-*.json"));
    }

    [Fact]
    public async Task SnapshotCleanupRunsOnlyAfterTheManifestCommitBoundary()
    {
        using var temp = TempDirectory.Create();
        var (root, repository) = CreateProject(temp);
        WritePackageModule(repository, "Feature.bas", "repository feature");
        WriteManifest(repository, "Feature.bas");
        var snapshotScratch = temp.CreateDirectory("snapshot-scratch");
        var innerWriter = new ProjectManifestAtomicWriter();
        var observingWriter = new SnapshotObservingAtomicWriter(innerWriter, snapshotScratch);
        var manifestReader = new CommonModulesManifestReader();
        var transaction = new CommonModulesInstallationTransaction(
            manifestReader,
            new ProjectManifestEditor(new JsonProjectManifestStore(), observingWriter),
            referencePlanner: null,
            manifestMutationCoordinator: new ProjectManifestMutationCoordinator(
                observingWriter,
                new ProjectManifestMutationLeaseProvider()),
            pathIdentityResolver: null,
            packageSnapshotFactory: new CommonModulesPackageSnapshotFactory(
                new CommonModulesPackageReader(manifestReader),
                snapshotScratch),
            sourceMutationWriter: new CommonModulesSourceMutationWriter());

        await transaction.AddAsync(
            ResolveContext(root),
            ["Feature"],
            force: false,
            CancellationToken.None);

        Assert.True(observingWriter.SnapshotExistedDuringCommit);
        Assert.Empty(Directory.EnumerateDirectories(snapshotScratch));
    }

    [Fact]
    public async Task ManifestConflictAfterSourceMutationReportsEverySourceAndRecoveryPath()
    {
        using var temp = TempDirectory.Create();
        var (root, repository) = CreateProject(temp);
        WritePackageModule(repository, "First.bas", "repository first");
        WritePackageModule(repository, "Second.bas", "repository second");
        WriteManifest(repository, "First.bas", "Second.bas");
        var first = Path.Combine(root, "src", "Book1", "First.bas");
        var second = Path.Combine(root, "src", "Book1", "Second.bas");
        WriteModule(first, "First", "local first");
        WriteModule(second, "Second", "local second");
        SetInstalled(
            root,
            new InstalledCommonModule("First", "First.bas", true, false, false),
            new InstalledCommonModule("Second", "Second.bas", true, false, false));
        var innerWriter = new ProjectManifestAtomicWriter();
        var conflictingWriter = new ManifestConflictingAtomicWriter(innerWriter);
        var manifestReader = new CommonModulesManifestReader();
        var snapshotScratch = temp.CreateDirectory("snapshot-scratch");
        var transaction = new CommonModulesInstallationTransaction(
            manifestReader,
            new ProjectManifestEditor(new JsonProjectManifestStore(), conflictingWriter),
            referencePlanner: null,
            manifestMutationCoordinator: new ProjectManifestMutationCoordinator(
                conflictingWriter,
                new ProjectManifestMutationLeaseProvider()),
            pathIdentityResolver: null,
            packageSnapshotFactory: new CommonModulesPackageSnapshotFactory(
                new CommonModulesPackageReader(manifestReader),
                snapshotScratch),
            sourceMutationWriter: new CommonModulesSourceMutationWriter());

        var error = await Assert.ThrowsAsync<CommonModulesTransactionException>(() =>
            transaction.UpdateAsync(ResolveProject(root), CancellationToken.None));

        Assert.Equal("repository first", ReadBody(first));
        Assert.Equal("repository second", ReadBody(second));
        Assert.Equal(
            "External manifest edit",
            new JsonProjectManifestStore()
                .Load(Path.Combine(root, ProjectManifest.ManifestFileName))
                .ProjectName);
        var recovery = Assert.Single(Directory.EnumerateFiles(root, "vba-project.failed-*.json"));
        Assert.Contains(Path.GetFullPath(first), error.Message, StringComparison.Ordinal);
        Assert.Contains(Path.GetFullPath(second), error.Message, StringComparison.Ordinal);
        Assert.Contains(Path.GetFullPath(ProjectManifestPath(root)), error.Message, StringComparison.Ordinal);
        Assert.Contains(Path.GetFullPath(recovery), error.Message, StringComparison.Ordinal);
        Assert.Contains("manual merge", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateDirectories(snapshotScratch));
    }

    private static (string Root, string Repository) CreateProject(TempDirectory temp)
    {
        var root = temp.CreateDirectory("Project");
        var repository = temp.CreateDirectory("common_modules_repo");
        Directory.CreateDirectory(Path.Combine(root, "src", "Book1"));
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        Directory.CreateDirectory(Path.Combine(root, "publish"));
        new JsonProjectManifestStore().Save(
            root,
            ProjectManifest.CreateDefault("Project", "Book1", root, repository));
        return (root, repository);
    }

    private static void SetInstalled(string root, params InstalledCommonModule[] modules)
    {
        var store = new JsonProjectManifestStore();
        var manifest = store.Load(Path.Combine(root, ProjectManifest.ManifestFileName));
        manifest.Documents["Book1"].CommonModules.Clear();
        manifest.Documents["Book1"].CommonModules.AddRange(modules);
        store.Save(root, manifest);
    }

    private static IReadOnlyList<InstalledCommonModule> LoadInstalled(string root)
        => new JsonProjectManifestStore()
            .Load(Path.Combine(root, ProjectManifest.ManifestFileName))
            .Documents["Book1"]
            .CommonModules;

    private static CommonModulesInstallationTransaction CreateTransaction(
        TempDirectory temp,
        CommonModulesSourceMutationWriter writer)
    {
        var manifestReader = new CommonModulesManifestReader();
        var atomicWriter = new ProjectManifestAtomicWriter();
        return new CommonModulesInstallationTransaction(
            manifestReader,
            new ProjectManifestEditor(new JsonProjectManifestStore(), atomicWriter),
            referencePlanner: null,
            manifestMutationCoordinator: new ProjectManifestMutationCoordinator(
                atomicWriter,
                new ProjectManifestMutationLeaseProvider()),
            pathIdentityResolver: null,
            packageSnapshotFactory: new CommonModulesPackageSnapshotFactory(
                new CommonModulesPackageReader(manifestReader),
                temp.CreateDirectory("snapshot-scratch-" + Guid.NewGuid().ToString("N"))),
            sourceMutationWriter: writer);
    }

    private static ResolvedProject ResolveProject(string root)
        => new ProjectContextResolver(new JsonProjectManifestStore()).ResolveProject(
            new ProjectResolutionRequest(root, null, root));

    private static ResolvedProjectContext ResolveContext(string root)
        => new ProjectContextResolver(new JsonProjectManifestStore()).Resolve(
            new ProjectResolutionRequest(root, null, root));

    private static void WriteManifest(string repository, params string[] moduleFiles)
    {
        var rows = moduleFiles.Select(file => $"{file}\toptional\t\t[]");
        var text = "ModuleFile\tCategories\tDependencies\tRequiredReferences\r\n"
                   + string.Join("\r\n", rows)
                   + "\r\n";
        File.WriteAllText(
            Path.Combine(repository, "common-modules-manifest.tsv"),
            text,
            new UnicodeEncoding(false, true, true));
    }

    private static void WritePackageModule(
        string repository,
        string moduleFile,
        string body,
        bool classModule = false)
        => WriteModule(
            Path.Combine(repository, moduleFile),
            Path.GetFileNameWithoutExtension(moduleFile),
            body,
            classModule: classModule,
            form: moduleFile.EndsWith(".frm", StringComparison.Ordinal));

    private static void WriteModule(
        string path,
        string name,
        string body,
        bool classModule = false,
        bool form = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var header = form
            ? $"VERSION 5.00\r\nAttribute VB_Name = \"{name}\"\r\n"
            : classModule
                ? $"VERSION 1.0 CLASS\r\nBEGIN\r\nEND\r\nAttribute VB_Name = \"{name}\"\r\n"
                : $"Attribute VB_Name = \"{name}\"\r\n";
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        File.WriteAllText(path, header + "' body\r\n" + body, Encoding.GetEncoding(932));
    }

    private static string ReadBody(string path)
    {
        var text = File.ReadAllText(path, Encoding.GetEncoding(932));
        return text[(text.IndexOf("' body\r\n", StringComparison.Ordinal) + 8)..];
    }

    private static string ProjectManifestPath(string root)
        => Path.Combine(root, ProjectManifest.ManifestFileName);

    private sealed class SnapshotObservingAtomicWriter(
        IProjectManifestAtomicWriter inner,
        string snapshotScratch) : IProjectManifestAtomicWriter
    {
        public bool SnapshotExistedDuringCommit { get; private set; }

        public void Save(string manifestPath, ProjectManifest manifest)
            => inner.Save(manifestPath, manifest);

        public void ReplaceExisting(
            string manifestPath,
            ReadOnlyMemory<byte> expectedRawBytes,
            ProjectManifest manifest,
            CancellationToken cancellationToken)
        {
            SnapshotExistedDuringCommit = Directory.EnumerateDirectories(snapshotScratch).Any();
            inner.ReplaceExisting(manifestPath, expectedRawBytes, manifest, cancellationToken);
        }

        public void EstablishNoOp(
            string manifestPath,
            ReadOnlyMemory<byte> expectedRawBytes,
            CancellationToken cancellationToken)
        {
            SnapshotExistedDuringCommit = Directory.EnumerateDirectories(snapshotScratch).Any();
            inner.EstablishNoOp(manifestPath, expectedRawBytes, cancellationToken);
        }

        public string CreateRecovery(string projectRoot, ProjectManifest manifest)
            => inner.CreateRecovery(projectRoot, manifest);
    }

    private sealed class ManifestConflictingAtomicWriter(IProjectManifestAtomicWriter inner)
        : IProjectManifestAtomicWriter
    {
        public void Save(string manifestPath, ProjectManifest manifest)
            => inner.Save(manifestPath, manifest);

        public void ReplaceExisting(
            string manifestPath,
            ReadOnlyMemory<byte> expectedRawBytes,
            ProjectManifest manifest,
            CancellationToken cancellationToken)
        {
            IntroduceExternalEdit(manifestPath);
            inner.ReplaceExisting(manifestPath, expectedRawBytes, manifest, cancellationToken);
        }

        public void EstablishNoOp(
            string manifestPath,
            ReadOnlyMemory<byte> expectedRawBytes,
            CancellationToken cancellationToken)
        {
            IntroduceExternalEdit(manifestPath);
            inner.EstablishNoOp(manifestPath, expectedRawBytes, cancellationToken);
        }

        public string CreateRecovery(string projectRoot, ProjectManifest manifest)
            => inner.CreateRecovery(projectRoot, manifest);

        private void IntroduceExternalEdit(string manifestPath)
        {
            var externalManifest = new JsonProjectManifestStore().Load(manifestPath) with
            {
                ProjectName = "External manifest edit"
            };
            inner.Save(manifestPath, externalManifest);
        }
    }
}
