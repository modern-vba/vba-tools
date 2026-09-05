using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using VbaDev.App.FileSystem;
using VbaDev.App.CommonModules;
using VbaDev.App.Projects;
using VbaDev.App.References;
using VbaDev.App.Workbooks;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;
using Xunit;

namespace VbaDev.Tests;

public sealed class NewProjectTransactionContractTests
{
    [Fact]
    public async Task DiagnosticEvidenceOnlyCreatorCannotGrantProjectRollbackAuthority()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "NoReceipt");
        var creator = new EvidenceOnlyWorkbookCreator();
        var leaseProvider = new RecordingLeaseProvider();

        var result = await CreateCommand(creator, leaseProvider).RunAsync(
            CreateRequest(projectRoot, "NoReceipt"), CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("cannot issue an invocation-owned", result.StandardError, StringComparison.Ordinal);
        Assert.Equal(0, creator.Calls);
        Assert.True(leaseProvider.Released);
        Assert.False(Directory.Exists(projectRoot));
    }

    [Fact]
    public async Task CancellationPreservesAWorkbookThatAcquiredAnotherHardLink()
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var projectRoot = Path.Combine(temp.Path, "AliasedWorkbook");
        var aliasPath = Path.Combine(temp.Path, "external-alias.xlsm");
        string? createdWorkbookPath = null;
        var leaseProvider = new RecordingLeaseProvider();
        var creator = new CallbackInitialWorkbookCreator(path =>
        {
            createdWorkbookPath = path;
            Assert.True(CreateHardLink(aliasPath, path, IntPtr.Zero));
            cancellation.Cancel();
        });

        var result = await CreateCommand(creator, leaseProvider).RunAsync(
            CreateRequest(projectRoot, "AliasedWorkbook"), cancellation.Token);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("newProjectTargetChanged", result.StandardError, StringComparison.Ordinal);
        Assert.NotNull(createdWorkbookPath);
        Assert.Contains(createdWorkbookPath, result.StandardError, StringComparison.Ordinal);
        Assert.Equal("fake xlsm", File.ReadAllText(createdWorkbookPath));
        Assert.Equal("fake xlsm", File.ReadAllText(aliasPath));
        Assert.False(File.Exists(Path.Combine(projectRoot, ProjectManifest.ManifestFileName)));
        Assert.True(leaseProvider.Released);
        AssertOwnershipReleased(creator);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 130)]
    public async Task TerminalCreationReleasesItsOwnershipSession(bool cancelBeforeCommit, int expectedExitCode)
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var projectRoot = Path.Combine(temp.Path, "TerminalSession");
        var creator = new CallbackInitialWorkbookCreator(_ =>
        {
            if (cancelBeforeCommit)
            {
                cancellation.Cancel();
            }
        });
        var leaseProvider = new RecordingLeaseProvider();

        var result = await CreateCommand(creator, leaseProvider).RunAsync(
            CreateRequest(projectRoot, "TerminalSession"), cancellation.Token);

        Assert.Equal(expectedExitCode, result.ExitCode);
        Assert.True(leaseProvider.Released);
        AssertOwnershipReleased(creator);
    }

    private static void AssertOwnershipReleased(CallbackInitialWorkbookCreator creator)
    {
        Assert.NotNull(creator.LastOwnership);
        Assert.NotNull(creator.LastReceipt);
        Assert.Throws<ObjectDisposedException>(() => creator.LastOwnership.Observe(creator.LastReceipt));
    }

    [Fact]
    public async Task NewExcelAcquiresProjectLeaseBeforeCreatingAnyProjectArtifact()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "LeaseFirst");
        var leaseProvider = new RecordingLeaseProvider();
        var workbookCreator = new CallbackInitialWorkbookCreator(_ =>
            Assert.True(leaseProvider.Acquired));
        var command = CreateCommand(workbookCreator, leaseProvider);

        var result = await command.RunAsync(
            CreateRequest(projectRoot, "LeaseFirst"),
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.True(leaseProvider.Acquired);
        Assert.True(leaseProvider.Released);
        Assert.Equal(ProjectManifestMutationCommand.NewExcel, leaseProvider.Command);
        Assert.Empty(leaseProvider.EntriesBeforeMarker);
    }

    [Fact]
    public async Task NewExcelAcceptsAPreExistingEmptyTargetWithOnlyItsOwnedLeaseMarker()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = temp.CreateDirectory("MarkerOnly");
        var leaseProvider = new RecordingLeaseProvider();
        var command = CreateCommand(
            new CallbackInitialWorkbookCreator(),
            leaseProvider);

        var result = await command.RunAsync(
            CreateRequest(projectRoot, "MarkerOnly"),
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.True(leaseProvider.Acquired);
        Assert.True(leaseProvider.Released);
        Assert.True(File.Exists(Path.Combine(
            projectRoot,
            ProjectManifest.ManifestFileName)));
    }

    [Fact]
    public async Task NewExcelRejectsAChangedTargetWithoutDeletingForeignContent()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "ChangedTarget");
        var foreignPath = Path.Combine(projectRoot, "foreign.txt");
        var leaseProvider = new RecordingLeaseProvider();
        var workbookCreator = new CallbackInitialWorkbookCreator(_ =>
            File.WriteAllText(foreignPath, "foreign", new UTF8Encoding(false)));
        var command = CreateCommand(workbookCreator, leaseProvider);

        var result = await command.RunAsync(
            CreateRequest(projectRoot, "ChangedTarget"),
            CancellationToken.None);

        Assert.NotEqual(0, result.ExitCode);
        Assert.True(leaseProvider.Released);
        Assert.True(File.Exists(foreignPath));
        Assert.Equal("foreign", File.ReadAllText(foreignPath));
        Assert.False(File.Exists(Path.Combine(
            projectRoot,
            ProjectManifest.ManifestFileName)));
    }

    [Fact]
    public async Task NewExcelPreservesACompetingManifestThatWinsBeforeCreateOnlyCommit()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "ManifestRace");
        var manifestPath = Path.Combine(
            projectRoot,
            ProjectManifest.ManifestFileName);
        var competingBytes = Encoding.UTF8.GetBytes("competing manifest bytes");
        var leaseProvider = new RecordingLeaseProvider();
        var workbookCreator = new CallbackInitialWorkbookCreator(_ =>
            File.WriteAllBytes(manifestPath, competingBytes));
        var command = CreateCommand(workbookCreator, leaseProvider);

        var result = await command.RunAsync(
            CreateRequest(projectRoot, "ManifestRace"),
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("newProjectTargetChanged", result.StandardError, StringComparison.Ordinal);
        Assert.Equal(competingBytes, File.ReadAllBytes(manifestPath));
        Assert.True(leaseProvider.Released);
    }

    [Fact]
    public async Task FinalLeaseProofPreservesAReplacementMarkerAsTargetChanged()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "MarkerReplacement");
        var foreignMarkerBytes = Encoding.UTF8.GetBytes("foreign marker");
        var leaseProvider = new RecordingLeaseProvider(
            retainMarkerOnRelease: true,
            duringContinuityProof: (proofNumber, markerPath) =>
            {
                if (proofNumber != 2)
                {
                    return;
                }

                File.Delete(markerPath);
                File.WriteAllBytes(markerPath, foreignMarkerBytes);
                throw new ProjectManifestMutationException(
                    "manifestMutationLeaseChanged",
                    "The project mutation lease marker changed while the command was running.");
            });
        var command = CreateCommand(
            new CallbackInitialWorkbookCreator(),
            leaseProvider);

        var result = await command.RunAsync(
            CreateRequest(projectRoot, "MarkerReplacement"),
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("manifestMutationLeaseChanged", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("newProjectTargetChanged", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("newProjectCleanupIncomplete", result.StandardError, StringComparison.Ordinal);
        Assert.Equal(foreignMarkerBytes, File.ReadAllBytes(leaseProvider.MarkerPath));
        Assert.False(File.Exists(Path.Combine(
            projectRoot,
            ProjectManifest.ManifestFileName)));
        Assert.True(leaseProvider.Released);
    }

    [Fact]
    public async Task CancellationBeforeManifestCommitReturns130AfterRollbackAndLeaseRelease()
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var projectRoot = Path.Combine(temp.Path, "CancelledProject");
        var leaseProvider = new RecordingLeaseProvider();
        var workbookCreator = new CallbackInitialWorkbookCreator(_ =>
            cancellation.Cancel());
        var command = CreateCommand(workbookCreator, leaseProvider);

        var result = await command.RunAsync(
            CreateRequest(projectRoot, "CancelledProject"),
            cancellation.Token);

        Assert.Equal(130, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.True(leaseProvider.Released);
        Assert.Equal([leaseProvider.MarkerPath], leaseProvider.EntriesAtRelease);
        Assert.False(Directory.Exists(projectRoot));
    }

    [Fact]
    public async Task CancellationWithAnUnremovableOwnedWorkbookReportsItsRetainedPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var projectRoot = Path.Combine(temp.Path, "RetainedWorkbook");
        var workbookPath = Path.Combine(
            projectRoot,
            "src",
            "RetainedWorkbook",
            "RetainedWorkbook.xlsm");
        var leaseProvider = new RecordingLeaseProvider();
        FileStream? workbookLock = null;
        var workbookCreator = new CallbackInitialWorkbookCreator(path =>
        {
            workbookLock = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            cancellation.Cancel();
        });
        var command = CreateCommand(workbookCreator, leaseProvider);

        try
        {
            var result = await command.RunAsync(
                CreateRequest(projectRoot, "RetainedWorkbook"),
                cancellation.Token);

            Assert.Equal(1, result.ExitCode);
            Assert.Empty(result.StandardOutput);
            Assert.Contains("newProjectCleanupIncomplete", result.StandardError, StringComparison.Ordinal);
            Assert.DoesNotContain("newProjectTargetChanged", result.StandardError, StringComparison.Ordinal);
            Assert.Contains(workbookPath, result.StandardError, StringComparison.Ordinal);
            Assert.Contains(
                "Inspect the retained paths before retrying.",
                result.StandardError,
                StringComparison.Ordinal);
            Assert.True(File.Exists(workbookPath));
            Assert.True(leaseProvider.Released);
        }
        finally
        {
            workbookLock?.Dispose();
        }
    }

    [Fact]
    public async Task PostReleaseCleanupNeverRetriesAnOwnedWorkbookThatWasLockedUnderLease()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var projectRoot = Path.Combine(temp.Path, "ReleaseUnlockedWorkbook");
        var workbookPath = Path.Combine(
            projectRoot,
            "src",
            "ReleaseUnlockedWorkbook",
            "ReleaseUnlockedWorkbook.xlsm");
        FileStream? workbookLock = null;
        var leaseProvider = new RecordingLeaseProvider(
            () =>
            {
                workbookLock?.Dispose();
                workbookLock = null;
            });
        var workbookCreator = new CallbackInitialWorkbookCreator(path =>
        {
            workbookLock = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            cancellation.Cancel();
        });
        var command = CreateCommand(workbookCreator, leaseProvider);

        try
        {
            var result = await command.RunAsync(
                CreateRequest(projectRoot, "ReleaseUnlockedWorkbook"),
                cancellation.Token);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains(
                "newProjectCleanupIncomplete",
                result.StandardError,
                StringComparison.Ordinal);
            Assert.Contains(workbookPath, result.StandardError, StringComparison.Ordinal);
            Assert.True(leaseProvider.Released);
            Assert.True(File.Exists(workbookPath));
        }
        finally
        {
            workbookLock?.Dispose();
        }
    }

    [Fact]
    public async Task CancellationRequestedDuringPostCommitLeaseReleaseCannotReplaceSuccess()
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var projectRoot = Path.Combine(temp.Path, "CommittedProject");
        var leaseProvider = new RecordingLeaseProvider(cancellation.Cancel);
        var command = CreateCommand(
            new CallbackInitialWorkbookCreator(),
            leaseProvider);

        var result = await command.RunAsync(
            CreateRequest(projectRoot, "CommittedProject"),
            cancellation.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(leaseProvider.ManifestExistedAtRelease);
        Assert.True(leaseProvider.Released);
        Assert.True(File.Exists(Path.Combine(
            projectRoot,
            ProjectManifest.ManifestFileName)));
    }

    [Fact]
    public async Task JsonCreationFailureEmitsNoSuccessObjectOnStandardOutput()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "BusyProject");
        var command = CreateCommand(
            new CallbackInitialWorkbookCreator(),
            new FailingLeaseProvider());

        var result = await command.RunAsync(
            CreateRequest(projectRoot, "BusyProject", format: "json"),
            CancellationToken.None);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("manifestMutationBusy", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("newProjectTargetChanged", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("newProjectCleanupIncomplete", result.StandardError, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(
            projectRoot,
            ProjectManifest.ManifestFileName)));
        Assert.False(Directory.Exists(projectRoot));
    }

    [Fact]
    public async Task LeaseAcquisitionFailurePreservesAndReportsForeignContentInAnInvocationCreatedRoot()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "BusyProjectWithForeignContent");
        var foreignPath = Path.Combine(projectRoot, "foreign.txt");
        var command = CreateCommand(
            new CallbackInitialWorkbookCreator(),
            new ForeignWritingFailingLeaseProvider(foreignPath));

        var result = await command.RunAsync(
            CreateRequest(projectRoot, "BusyProjectWithForeignContent", format: "json"),
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("manifestMutationBusy", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("newProjectTargetChanged", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("newProjectCleanupIncomplete", result.StandardError, StringComparison.Ordinal);
        Assert.Contains(foreignPath, result.StandardError, StringComparison.Ordinal);
        Assert.Equal("foreign", File.ReadAllText(foreignPath));
    }

    [Fact]
    public async Task CancellationDuringLeaseAcquisitionFailsWhenForeignContentRetainsTheInvocationCreatedRoot()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "CancelledProjectWithForeignContent");
        var foreignPath = Path.Combine(
            projectRoot,
            ProjectManifest.ManifestFileName + ".vba-dev.lock");
        var command = CreateCommand(
            new CallbackInitialWorkbookCreator(),
            new ForeignWritingFailingLeaseProvider(
                foreignPath,
                new OperationCanceledException("Project creation was cancelled.")));

        var result = await command.RunAsync(
            CreateRequest(projectRoot, "CancelledProjectWithForeignContent", format: "json"),
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("Project creation was cancelled.", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("newProjectTargetChanged", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("newProjectCleanupIncomplete", result.StandardError, StringComparison.Ordinal);
        Assert.Contains(foreignPath, result.StandardError, StringComparison.Ordinal);
        Assert.Equal("foreign", File.ReadAllText(foreignPath));
    }

    [Fact]
    public async Task UnstableCommonModulesPackageFailsBeforeArtifactsAndCleansOwnedState()
    {
        using var temp = TempDirectory.Create();
        var repository = temp.CreateDirectory("common_modules_repo");
        WriteOptionalCommonModulePackage(repository);
        var scratchRoot = temp.CreateDirectory("snapshot-scratch");
        var modulePath = Path.Combine(repository, "OptionalFeature.bas");
        var packageSnapshotFactory = new CommonModulesPackageSnapshotFactory(
            new CommonModulesPackageReader(new CommonModulesManifestReader()),
            scratchRoot,
            () => File.WriteAllText(
                modulePath,
                "Attribute VB_Name = \"OptionalFeature\"\r\nOption Explicit\r\n' changed\r\n",
                new UTF8Encoding(false)));
        var projectRoot = Path.Combine(temp.Path, "UnstablePackage");
        var leaseProvider = new RecordingLeaseProvider();
        var command = CreateCommand(
            new CallbackInitialWorkbookCreator(),
            leaseProvider,
            packageSnapshotFactory);

        var result = await command.RunAsync(
            CreateRequest(projectRoot, "UnstablePackage", format: "json"),
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains(
            "changed while its immutable snapshot was being captured",
            result.StandardError,
            StringComparison.Ordinal);
        Assert.True(leaseProvider.Released);
        Assert.False(Directory.Exists(projectRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(scratchRoot));
    }

    [Fact]
    public async Task SnapshotCaptureCleanupUncertaintyReportsStableFailureAndStagingPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var repository = temp.CreateDirectory("common_modules_repo");
        WriteOptionalCommonModulePackage(repository);
        var scratchRoot = temp.CreateDirectory("snapshot-scratch");
        var modulePath = Path.Combine(repository, "OptionalFeature.bas");
        string? stagingPath = null;
        FileStream? stagingLock = null;
        var packageSnapshotFactory = new CommonModulesPackageSnapshotFactory(
            new CommonModulesPackageReader(new CommonModulesManifestReader()),
            scratchRoot,
            () =>
            {
                stagingPath = Directory.EnumerateDirectories(scratchRoot).Single();
                stagingLock = File.Open(
                    Path.Combine(stagingPath, "OptionalFeature.bas"),
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);
                File.WriteAllText(
                    modulePath,
                    "Attribute VB_Name = \"OptionalFeature\"\r\nOption Explicit\r\n' changed\r\n",
                    new UTF8Encoding(false));
            });
        var projectRoot = Path.Combine(temp.Path, "RetainedSnapshot");
        var leaseProvider = new RecordingLeaseProvider();
        var command = CreateCommand(
            new CallbackInitialWorkbookCreator(),
            leaseProvider,
            packageSnapshotFactory);

        try
        {
            var result = await command.RunAsync(
                CreateRequest(projectRoot, "RetainedSnapshot", format: "json"),
                CancellationToken.None);

            Assert.Equal(1, result.ExitCode);
            Assert.Empty(result.StandardOutput);
            Assert.Contains(
                "newProjectCleanupIncomplete",
                result.StandardError,
                StringComparison.Ordinal);
            Assert.NotNull(stagingPath);
            Assert.Contains(stagingPath, result.StandardError, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                "Inspect the retained paths before retrying.",
                result.StandardError,
                StringComparison.Ordinal);
            Assert.True(Directory.Exists(stagingPath));
            Assert.True(leaseProvider.Released);
        }
        finally
        {
            stagingLock?.Dispose();
            if (stagingPath is not null && Directory.Exists(stagingPath))
            {
                Directory.Delete(stagingPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task JsonSuccessUsesTheExactEnvelopeAndNormalizedRequestedPaths()
    {
        using var temp = TempDirectory.Create();
        var requestedOutput = Path.Combine(
            temp.Path,
            "unused-segment",
            "..",
            "JsonProject");
        var expectedProjectRoot = Path.GetFullPath(requestedOutput);
        var expectedManifestPath = Path.Combine(
            expectedProjectRoot,
            ProjectManifest.ManifestFileName);
        var command = CreateCommand(
            new CallbackInitialWorkbookCreator(),
            new RecordingLeaseProvider());

        var result = await command.RunAsync(
            CreateRequest(requestedOutput, "JsonProject", format: "json"),
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        Assert.EndsWith(Environment.NewLine, result.StandardOutput, StringComparison.Ordinal);
        using var receipt = JsonDocument.Parse(result.StandardOutput);
        var root = receipt.RootElement;
        Assert.Equal(
            [
                "schemaVersion",
                "scope",
                "project",
                "document",
                "operation",
                "template",
                "complete",
                "warnings",
                "manifestPath",
                "manifest"
            ],
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal("1.0", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("project", root.GetProperty("scope").GetString());
        Assert.Equal(expectedProjectRoot, root.GetProperty("project").GetString());
        Assert.Equal("JsonProject", root.GetProperty("document").GetString());
        Assert.Equal("new", root.GetProperty("operation").GetString());
        Assert.Equal("excel", root.GetProperty("template").GetString());
        Assert.True(root.GetProperty("complete").GetBoolean());
        Assert.Equal(expectedManifestPath, root.GetProperty("manifestPath").GetString());
        Assert.False(root.TryGetProperty("projectName", out _));

        var warnings = root.GetProperty("warnings").EnumerateArray().ToArray();
        var warning = Assert.Single(warnings);
        Assert.Equal(
            ["code", "message"],
            warning.EnumerateObject().Select(property => property.Name));
        Assert.Equal("commonModulesRepositoryNotFound", warning.GetProperty("code").GetString());
        Assert.Equal(
            "CommonModules repository was not found; the project was created without shared modules.",
            warning.GetProperty("message").GetString());

        var manifest = root.GetProperty("manifest");
        Assert.Equal(1, manifest.GetProperty("schemaVersion").GetInt32());
        var document = manifest
            .GetProperty("documents")
            .GetProperty("JsonProject");
        Assert.Equal("src/JsonProject", document.GetProperty("sourcePath").GetString());
        Assert.Equal(
            "src/JsonProject/JsonProject.xlsm",
            document.GetProperty("templatePath").GetString());
        Assert.Equal("bin/JsonProject.xlsm", document.GetProperty("binPath").GetString());
        Assert.Equal(
            "publish/JsonProject.xlsm",
            document.GetProperty("publishPath").GetString());

        using var committed = JsonDocument.Parse(File.ReadAllText(expectedManifestPath));
        Assert.True(JsonElement.DeepEquals(manifest, committed.RootElement));
    }

    [Fact]
    public async Task TextSuccessUsesTheExactHumanReceiptWithModuleProvenance()
    {
        using var temp = TempDirectory.Create();
        var repository = temp.CreateDirectory("common_modules_repo");
        WriteReceiptCommonModulePackage(repository);
        var projectRoot = Path.Combine(temp.Path, "TextProject");
        var manifestPath = Path.Combine(
            projectRoot,
            ProjectManifest.ManifestFileName);
        var resolver = new FakeVbaProjectReferenceResolver(
            new ResolvedVbaProjectReference(
                "Baseline Library",
                "11111111-1111-1111-1111-111111111111",
                1,
                0),
            new ResolvedVbaProjectReference(
                "Package Library",
                "22222222-2222-2222-2222-222222222222",
                1,
                0));
        var command = CreateCommand(
            new CallbackInitialWorkbookCreator(
                referenceNames: ["Baseline Library"]),
            new RecordingLeaseProvider(),
            referenceResolver: resolver);

        var result = await command.RunAsync(
            CreateRequest(projectRoot, "TextProject"),
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        var expected = string.Join(
            Environment.NewLine,
            "Created Excel VBA project \"TextProject\".",
            $"Project: {projectRoot}",
            $"Manifest: {manifestPath}",
            "Document: TextProject",
            "Source set: src/TextProject",
            "Source template: src/TextProject/TextProject.xlsm",
            "Build target: bin/TextProject.xlsm",
            "Publish target: publish/TextProject.xlsm",
            "CommonModules:",
            "  - dependency: OptionalFeature (OptionalFeature.bas)",
            "  - requested: RuntimeRoot (RuntimeRoot.bas)",
            "References:",
            "  - requested: Baseline Library",
            "  - CommonModules: Package Library",
            "Summary:",
            "  CommonModules: 2 CommonModules (1 requested, 1 dependency)",
            "  References: 2 references (1 requested, 1 from CommonModules)",
            string.Empty);
        Assert.Equal(expected, result.StandardOutput);
        Assert.True(File.Exists(manifestPath));
    }

    [Fact]
    public async Task TextSuccessKeepsWarningsOnStderrAndPrintsEmptyCollections()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "EmptyCollections");
        var command = CreateCommand(
            new CallbackInitialWorkbookCreator(),
            new RecordingLeaseProvider());

        var result = await command.RunAsync(
            CreateRequest(projectRoot, "EmptyCollections"),
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            "[WARN] commonModulesRepositoryNotFound: CommonModules repository was "
            + "not found; the project was created without shared modules."
            + Environment.NewLine,
            result.StandardError);
        Assert.DoesNotContain("Warnings:", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            "CommonModules:" + Environment.NewLine + "  (none)",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains(
            "References:" + Environment.NewLine + "  (none)",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.EndsWith(
            "  CommonModules: 0 CommonModules (0 requested, 0 dependencies)"
            + Environment.NewLine
            + "  References: 0 references (0 requested, 0 from CommonModules)"
            + Environment.NewLine,
            result.StandardOutput,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostCommitJsonWarningsUseSnapshotThenLeaseOrderWithoutStderrDuplication()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var repository = temp.CreateDirectory("common_modules_repo");
        WriteOptionalCommonModulePackage(repository);
        var scratchRoot = temp.CreateDirectory("snapshot-scratch");
        string? stagingPath = null;
        FileStream? snapshotLock = null;
        var packageSnapshotFactory = new CommonModulesPackageSnapshotFactory(
            new CommonModulesPackageReader(new CommonModulesManifestReader()),
            scratchRoot,
            () =>
            {
                stagingPath = Assert.Single(Directory.EnumerateDirectories(scratchRoot));
                snapshotLock = new FileStream(
                    Path.Combine(stagingPath, "OptionalFeature.bas"),
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);
            });
        var projectRoot = Path.Combine(temp.Path, "WarningProject");
        var leaseProvider = new RecordingLeaseProvider(
            retainMarkerOnRelease: true,
            releaseWarnings:
            [
                new ProjectManifestMutationWarning(
                    "leaseMarkerCleanupFailed",
                    "The fake retained its owned marker.")
            ]);
        var command = CreateCommand(
            new CallbackInitialWorkbookCreator(),
            leaseProvider,
            packageSnapshotFactory);

        try
        {
            var result = await command.RunAsync(
                CreateRequest(projectRoot, "WarningProject", format: "json"),
                CancellationToken.None);

            Assert.True(result.ExitCode == 0, result.StandardError);
            Assert.Empty(result.StandardError);
            using var receipt = JsonDocument.Parse(result.StandardOutput);
            var warnings = receipt.RootElement
                .GetProperty("warnings")
                .EnumerateArray()
                .ToArray();
            Assert.Equal(
                ["commonModulesSnapshotCleanupFailed", "leaseMarkerCleanupFailed"],
                warnings.Select(warning => warning.GetProperty("code").GetString()));
            Assert.Equal(
                "The project was created, but its non-authoritative CommonModules "
                + $"snapshot workspace could not be removed: \"{stagingPath}\".",
                warnings[0].GetProperty("message").GetString());
            Assert.Equal(
                "The project was created and its project lease was released, "
                + "but the lease marker could not be removed: "
                + $"\"{leaseProvider.MarkerPath}\".",
                warnings[1].GetProperty("message").GetString());
            Assert.True(File.Exists(Path.Combine(
                projectRoot,
                ProjectManifest.ManifestFileName)));
            Assert.True(File.Exists(leaseProvider.MarkerPath));
        }
        finally
        {
            snapshotLock?.Dispose();
            if (stagingPath is not null && Directory.Exists(stagingPath))
            {
                Directory.Delete(stagingPath, recursive: true);
            }

            if (File.Exists(leaseProvider.MarkerPath))
            {
                File.Delete(leaseProvider.MarkerPath);
            }
        }
    }

    [Fact]
    public async Task PostCommitLeaseReleaseUncertaintyFailsWithoutRollingBackTheManifest()
    {
        using var temp = TempDirectory.Create();
        var repository = temp.CreateDirectory("common_modules_repo");
        WriteOptionalCommonModulePackage(repository);
        var projectRoot = Path.Combine(temp.Path, "ReleaseFailureProject");
        var leaseProvider = new RecordingLeaseProvider(
            () => throw new IOException("simulated release failure"));
        var command = CreateCommand(
            new CallbackInitialWorkbookCreator(),
            leaseProvider);

        var result = await command.RunAsync(
            CreateRequest(projectRoot, "ReleaseFailureProject", format: "json"),
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains(
            "lease release and marker cleanup could not be proved",
            result.StandardError,
            StringComparison.Ordinal);
        Assert.Contains(
            "newProjectCleanupIncomplete",
            result.StandardError,
            StringComparison.Ordinal);
        Assert.Contains(
            "Additional retained paths could not be determined conclusively",
            result.StandardError,
            StringComparison.Ordinal);
        Assert.Contains(
            leaseProvider.MarkerPath,
            result.StandardError,
            StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(
            projectRoot,
            ProjectManifest.ManifestFileName)));
        Assert.True(File.Exists(leaseProvider.MarkerPath));
    }

    private static NewProjectCommand CreateCommand(
        IInitialWorkbookCreator workbookCreator,
        IProjectManifestMutationLeaseProvider leaseProvider,
        CommonModulesPackageSnapshotFactory? packageSnapshotFactory = null,
        IVbaProjectReferenceResolver? referenceResolver = null)
    {
        var manifestReader = new CommonModulesManifestReader();
        return new NewProjectCommand(
            new JsonProjectManifestStore(),
            workbookCreator,
            manifestReader,
            new VbaProjectReferencePlanner(
                referenceResolver ?? new FakeVbaProjectReferenceResolver()),
            leaseProvider,
            new FileSystemPathIdentityResolver(),
            packageSnapshotFactory);
    }

    private static NewProjectCommandRequest CreateRequest(
        string projectRoot,
        string projectName,
        string format = "text")
        => new(
            projectName,
            DocumentName: null,
            projectRoot,
            Path.GetDirectoryName(projectRoot)!,
            ProjectNameSpecified: true,
            OutputDirectorySpecified: true,
            Format: format);

    private static void WriteOptionalCommonModulePackage(string repository)
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

    private static void WriteReceiptCommonModulePackage(string repository)
    {
        var manifest = string.Join(
            "\r\n",
            "ModuleFile\tCategories\tDependencies\tRequiredReferences",
            "RuntimeRoot.bas\truntime-baseline\tOptionalFeature.bas\t[]",
            "OptionalFeature.bas\toptional\t\t[\"Package Library\"]") + "\r\n";
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
            Path.Combine(repository, "RuntimeRoot.bas"),
            "Attribute VB_Name = \"RuntimeRoot\"\r\nOption Explicit\r\n",
            new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(repository, "OptionalFeature.bas"),
            "Attribute VB_Name = \"OptionalFeature\"\r\nOption Explicit\r\n",
            new UTF8Encoding(false));
    }

    private sealed class CallbackInitialWorkbookCreator(
        Action<string>? afterCreate = null,
        IReadOnlyList<string>? referenceNames = null) : IReceiptInitialWorkbookCreator
    {
        public ExactFileSystemObjectOwnership? LastOwnership { get; private set; }

        public ExactFileSystemObjectOwnership.FileReceipt? LastReceipt { get; private set; }

        public InitialWorkbookCreationResult CreateInitialWorkbook(string workbookPath)
        {
            using var ownership = ExactFileSystemObjectOwnership.Open();
            return CreateInitialWorkbookAsync(workbookPath, ownership, CancellationToken.None).GetAwaiter().GetResult();
        }

        public Task<InitialWorkbookCreationResult> CreateInitialWorkbookAsync(
            string workbookPath,
            ExactFileSystemObjectOwnership ownership,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(workbookPath)!);
            var receipt = ownership.CreateOnlyFile(Path.GetDirectoryName(workbookPath)!, Path.GetFileName(workbookPath), "fake xlsm"u8);
            LastOwnership = ownership;
            LastReceipt = receipt;
            var evidence = InitialWorkbookTestArtifactEvidence.Capture(workbookPath);
            afterCreate?.Invoke(workbookPath);
            return Task.FromResult(new InitialWorkbookCreationResult(
                referenceNames ?? [],
                evidence)
            { OwnedArtifactReceipt = receipt });
        }
    }

    private sealed class EvidenceOnlyWorkbookCreator : IInitialWorkbookCreator
    {
        public int Calls { get; private set; }

        public InitialWorkbookCreationResult CreateInitialWorkbook(string workbookPath)
        {
            Calls++;
            throw new InvalidOperationException("Evidence-only creator must not be invoked by new excel.");
        }
    }

    private sealed class RecordingLeaseProvider(
        Action? duringRelease = null,
        bool retainMarkerOnRelease = false,
        IReadOnlyList<ProjectManifestMutationWarning>? releaseWarnings = null,
        Action<int, string>? duringContinuityProof = null)
        : IProjectManifestMutationLeaseProvider
    {
        public bool Acquired { get; private set; }

        public bool Released { get; private set; }

        public ProjectManifestMutationCommand? Command { get; private set; }

        public IReadOnlyList<string> EntriesBeforeMarker { get; private set; } = [];

        public IReadOnlyList<string> EntriesAtRelease { get; private set; } = [];

        public string MarkerPath { get; private set; } = string.Empty;

        public bool ManifestExistedAtRelease { get; private set; }

        public ValueTask<IProjectManifestMutationLease> AcquireAsync(
            string projectRoot,
            ProjectManifestMutationCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Command = command;
            EntriesBeforeMarker = Directory.Exists(projectRoot)
                ? Directory.EnumerateFileSystemEntries(projectRoot).ToArray()
                : [];
            Directory.CreateDirectory(projectRoot);
            var manifestPath = Path.Combine(projectRoot, ProjectManifest.ManifestFileName);
            var markerPath = manifestPath + ".vba-dev.lock";
            MarkerPath = markerPath;
            File.WriteAllText(markerPath, "owned", new UTF8Encoding(false));
            Acquired = true;
            return ValueTask.FromResult<IProjectManifestMutationLease>(
                new RecordingLease(
                    new FileSystemPathIdentityResolver().Resolve(projectRoot),
                    manifestPath,
                    markerPath,
                    () =>
                    {
                        ManifestExistedAtRelease = File.Exists(manifestPath);
                        EntriesAtRelease = Directory.Exists(projectRoot)
                            ? Directory.EnumerateFileSystemEntries(projectRoot).ToArray()
                            : [];
                        duringRelease?.Invoke();
                    },
                    () => Released = true,
                    retainMarkerOnRelease,
                    releaseWarnings ?? [],
                    duringContinuityProof));
        }
    }

    private sealed class RecordingLease(
        FileSystemPathIdentity projectIdentity,
        string manifestPath,
        string markerPath,
        Action beforeRelease,
        Action onReleased,
        bool retainMarkerOnRelease,
        IReadOnlyList<ProjectManifestMutationWarning> releaseWarnings,
        Action<int, string>? duringContinuityProof)
        : IProjectManifestMutationLease
    {
        private int proofCount;

        public FileSystemPathIdentity ProjectIdentity { get; } = projectIdentity;

        public string ManifestPath { get; } = manifestPath;

        public void ProveOwnershipContinuity()
        {
            proofCount++;
            duringContinuityProof?.Invoke(proofCount, markerPath);
        }

        public ValueTask<ProjectManifestLeaseRelease> ReleaseAsync()
        {
            beforeRelease();
            if (!retainMarkerOnRelease && File.Exists(markerPath))
            {
                File.Delete(markerPath);
            }

            onReleased();
            return ValueTask.FromResult(new ProjectManifestLeaseRelease(releaseWarnings));
        }
    }

    private sealed class FailingLeaseProvider : IProjectManifestMutationLeaseProvider
    {
        public ValueTask<IProjectManifestMutationLease> AcquireAsync(
            string projectRoot,
            ProjectManifestMutationCommand command,
            CancellationToken cancellationToken)
            => throw new ProjectManifestMutationException(
                "manifestMutationBusy",
                "Another project mutation owns the target.");
    }

    private sealed class ForeignWritingFailingLeaseProvider(
        string foreignPath,
        Exception? failure = null)
        : IProjectManifestMutationLeaseProvider
    {
        public ValueTask<IProjectManifestMutationLease> AcquireAsync(
            string projectRoot,
            ProjectManifestMutationCommand command,
            CancellationToken cancellationToken)
        {
            File.WriteAllText(foreignPath, "foreign", new UTF8Encoding(false));
            throw failure ?? new ProjectManifestMutationException(
                    "manifestMutationBusy",
                    "Another project mutation owns the target.");
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode,
        SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}
