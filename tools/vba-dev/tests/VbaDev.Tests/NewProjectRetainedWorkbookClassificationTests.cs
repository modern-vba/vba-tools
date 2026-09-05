using System.Text;
using VbaDev.App.CommonModules;
using VbaDev.App.FileSystem;
using VbaDev.App.Projects;
using VbaDev.App.References;
using VbaDev.App.Workbooks;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;
using Xunit;

namespace VbaDev.Tests;

public sealed class NewProjectRetainedWorkbookClassificationTests
{
    [Fact]
    public async Task UnprovenDispositionRollbackNeverClaimsThatChangedContentWasPreserved()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "UnprovenRollback");
        var leaseProvider = new RecordingLeaseProvider();
        var command = CreateCommand(
            new RetainedWorkbookCreator(targetChanged: true, rollbackUnproven: true),
            leaseProvider);

        var result = await command.RunAsync(
            CreateRequest(projectRoot, "UnprovenRollback"), CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("newProjectCleanupIncomplete", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("Additional retained paths could not be determined conclusively", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("newProjectTargetChanged", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("foreign or changed content was preserved", result.StandardError, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(projectRoot, ProjectManifest.ManifestFileName)));
        Assert.True(leaseProvider.Released);
    }

    [Fact]
    public async Task RetainedOwnedWorkbookIsReportedOnlyAsCleanupIncomplete()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "RetainedOwnedWorkbook");
        var workbookPath = Path.Combine(
            projectRoot,
            "src",
            "RetainedOwnedWorkbook",
            "RetainedOwnedWorkbook.xlsm");
        var leaseProvider = new RecordingLeaseProvider();
        var command = CreateCommand(
            new RetainedWorkbookCreator(targetChanged: false),
            leaseProvider);

        var result = await command.RunAsync(
            CreateRequest(projectRoot, "RetainedOwnedWorkbook"),
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains(
            "newProjectCleanupIncomplete",
            result.StandardError,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "newProjectTargetChanged",
            result.StandardError,
            StringComparison.Ordinal);
        Assert.Contains(workbookPath, result.StandardError, StringComparison.Ordinal);
        Assert.Equal("owned workbook", File.ReadAllText(workbookPath));
        Assert.True(leaseProvider.Released);
    }

    [Fact]
    public async Task ChangedWorkbookIsReportedOnlyAsTargetChangedAndIsPreserved()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "ChangedWorkbook");
        var workbookPath = Path.Combine(
            projectRoot,
            "src",
            "ChangedWorkbook",
            "ChangedWorkbook.xlsm");
        var leaseProvider = new RecordingLeaseProvider();
        var command = CreateCommand(
            new RetainedWorkbookCreator(targetChanged: true),
            leaseProvider);

        var result = await command.RunAsync(
            CreateRequest(projectRoot, "ChangedWorkbook"),
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains(
            "newProjectTargetChanged",
            result.StandardError,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "newProjectCleanupIncomplete",
            result.StandardError,
            StringComparison.Ordinal);
        Assert.Contains(workbookPath, result.StandardError, StringComparison.Ordinal);
        Assert.Equal("foreign workbook", File.ReadAllText(workbookPath));
        Assert.False(File.Exists(Path.Combine(
            projectRoot,
            ProjectManifest.ManifestFileName)));
        Assert.True(leaseProvider.Released);
    }

    [Fact]
    public async Task NestedRetainedFailuresReportBothChangedAndCleanupIncompletePaths()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "NestedRetainedFailures");
        var workbookPath = Path.Combine(
            projectRoot,
            "src",
            "NestedRetainedFailures",
            "NestedRetainedFailures.xlsm");
        var stagingPath = Path.Combine(temp.Path, "retained-staging.xlsm");
        var leaseProvider = new RecordingLeaseProvider();
        var command = CreateCommand(
            new NestedRetainedWorkbookCreator(stagingPath),
            leaseProvider);

        var result = await command.RunAsync(
            CreateRequest(projectRoot, "NestedRetainedFailures"),
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("newProjectTargetChanged", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("newProjectCleanupIncomplete", result.StandardError, StringComparison.Ordinal);
        Assert.Contains(workbookPath, result.StandardError, StringComparison.Ordinal);
        Assert.Contains(stagingPath, result.StandardError, StringComparison.Ordinal);
        Assert.Equal("foreign workbook", File.ReadAllText(workbookPath));
        Assert.Equal("owned staging", File.ReadAllText(stagingPath));
        Assert.True(leaseProvider.Released);
    }

    private static NewProjectCommand CreateCommand(
        IInitialWorkbookCreator workbookCreator,
        IProjectManifestMutationLeaseProvider leaseProvider)
    {
        var manifestReader = new CommonModulesManifestReader();
        return new NewProjectCommand(
            new JsonProjectManifestStore(),
            workbookCreator,
            manifestReader,
            new VbaProjectReferencePlanner(new FakeVbaProjectReferenceResolver()),
            leaseProvider,
            new FileSystemPathIdentityResolver());
    }

    private static NewProjectCommandRequest CreateRequest(
        string projectRoot,
        string projectName)
        => new(
            projectName,
            DocumentName: null,
            projectRoot,
            Path.GetDirectoryName(projectRoot)!,
            ProjectNameSpecified: true,
            OutputDirectorySpecified: true,
            Format: "text");

    private sealed class RetainedWorkbookCreator(bool targetChanged, bool rollbackUnproven = false)
        : IReceiptInitialWorkbookCreator
    {
        public Task<InitialWorkbookCreationResult> CreateInitialWorkbookAsync(
            string workbookPath, ExactFileSystemObjectOwnership ownership, CancellationToken cancellationToken)
            => Task.FromResult(CreateInitialWorkbook(workbookPath));

        public InitialWorkbookCreationResult CreateInitialWorkbook(string workbookPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(workbookPath)!);
            File.WriteAllText(
                workbookPath,
                targetChanged ? "foreign workbook" : "owned workbook",
                new UTF8Encoding(false));
            if (rollbackUnproven)
            {
                throw new InitialWorkbookArtifactRetainedException(
                    workbookPath,
                    expectedArtifact: null,
                    targetChanged: false,
                    new ExactFileSystemObjectOwnership.FileCreationCleanupException(
                        workbookPath, retainedReceipt: null, targetChanged,
                        new OperationCanceledException("Creation was cancelled during the first copy chunk."),
                        new ExactFileSystemObjectOwnership.RollbackException(workbookPath)));
            }

            throw new InitialWorkbookArtifactRetainedException(
                workbookPath,
                expectedArtifact: null,
                targetChanged,
                new IOException("Workbook cleanup was intentionally retained."));
        }
    }

    private sealed class NestedRetainedWorkbookCreator(string stagingPath)
        : IReceiptInitialWorkbookCreator
    {
        public Task<InitialWorkbookCreationResult> CreateInitialWorkbookAsync(
            string workbookPath, ExactFileSystemObjectOwnership ownership, CancellationToken cancellationToken)
            => Task.FromResult(CreateInitialWorkbook(workbookPath));

        public InitialWorkbookCreationResult CreateInitialWorkbook(string workbookPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(workbookPath)!);
            File.WriteAllText(
                workbookPath,
                "foreign workbook",
                new UTF8Encoding(false));
            File.WriteAllText(
                stagingPath,
                "owned staging",
                new UTF8Encoding(false));
            var changedFinal = new InitialWorkbookArtifactRetainedException(
                workbookPath,
                expectedArtifact: null,
                targetChanged: true,
                new IOException("Final workbook was replaced."));
            throw new InitialWorkbookArtifactRetainedException(
                stagingPath,
                expectedArtifact: null,
                targetChanged: false,
                new AggregateException(
                    changedFinal,
                    new IOException("Staging cleanup failed.")));
        }
    }

    private sealed class RecordingLeaseProvider
        : IProjectManifestMutationLeaseProvider
    {
        public bool Released { get; private set; }

        public ValueTask<IProjectManifestMutationLease> AcquireAsync(
            string projectRoot,
            ProjectManifestMutationCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(projectRoot);
            var manifestPath = Path.Combine(
                projectRoot,
                ProjectManifest.ManifestFileName);
            var markerPath = manifestPath + ".vba-dev.lock";
            File.WriteAllText(markerPath, "owned", new UTF8Encoding(false));
            return ValueTask.FromResult<IProjectManifestMutationLease>(
                new RecordingLease(
                    new FileSystemPathIdentityResolver().Resolve(projectRoot),
                    manifestPath,
                    markerPath,
                    () => Released = true));
        }
    }

    private sealed class RecordingLease(
        FileSystemPathIdentity projectIdentity,
        string manifestPath,
        string markerPath,
        Action onReleased)
        : IProjectManifestMutationLease
    {
        public FileSystemPathIdentity ProjectIdentity { get; } = projectIdentity;

        public string ManifestPath { get; } = manifestPath;

        public void ProveOwnershipContinuity()
        {
        }

        public ValueTask<ProjectManifestLeaseRelease> ReleaseAsync()
        {
            if (File.Exists(markerPath))
            {
                File.Delete(markerPath);
            }

            onReleased();
            return ValueTask.FromResult(new ProjectManifestLeaseRelease([]));
        }
    }
}
