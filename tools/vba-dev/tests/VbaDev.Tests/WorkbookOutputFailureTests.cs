using VbaDev.Infrastructure.FileSystem;
using System.Text;
using VbaDev.App.Build;
using VbaDev.App.Cli;
using VbaDev.App.Diagnostics;
using VbaDev.App.Projects;
using VbaDev.App.References;
using VbaDev.App.Workbooks;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;
using Xunit;

namespace VbaDev.Tests;

public sealed class WorkbookOutputFailureTests
{
    public static IEnumerable<object[]> TerminalFailureMatrix()
    {
        foreach (var command in new[] { "build", "publish", "snapshot" })
        foreach (var category in new[] { "cancel", "timeout", "process-loss", "com", "process-release", "dispatcher", "released-cleanup" })
        foreach (var saved in new[] { false, true })
        foreach (var cancelled in new[] { false, true })
        {
            yield return [command, category, saved, cancelled];
        }
    }

    [Theory]
    [MemberData(nameof(TerminalFailureMatrix))]
    public async Task AllBuildPathsProjectEquivalentTerminalEvidenceWithoutCommitting(
        string commandName, string category, bool saved, bool cancelled)
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var project = CreateProject(temp);
        var snapshot = CreateCallerSnapshot(temp);
        var stage = new WorkbookAutomationStage(saved
            ? WorkbookAutomationStageKind.WorkbookSave : WorkbookAutomationStageKind.ModuleImport, "Local.bas");
        var automation = new TerminalWorkbookGenerationAutomation(saved, token =>
        {
            if (cancelled) cancellation.Cancel();
            Exception error = category switch
            {
                "cancel" => new WorkbookAutomationCanceledException(stage, token),
                "timeout" => new WorkbookAutomationTimeoutException(stage, TimeSpan.FromSeconds(1)),
                "process-loss" => new WorkbookAutomationProcessLostException(stage),
                "com" => new System.Runtime.InteropServices.COMException("COM detail"),
                "process-release" or "dispatcher" => new WorkbookAutomationCleanupException("Release detail"),
                _ => new WorkbookAutomationReleasedProcessCleanupException("Secondary cleanup detail")
            };
            if (error is IWorkbookAutomationLifecycleFailure lifecycle)
            {
                lifecycle.LifecycleEvidence = new(stage, category != "process-release", category != "dispatcher", cancelled);
            }
            if (cancelled && category != "cancel")
            {
                error = new AggregateException(new WorkbookAutomationCanceledException(stage, token), error);
            }
            return new WorkbookAutomationStageFailureException(stage, error);
        });
        var command = CreateCommand(project.Context, automation);

        var result = commandName == "snapshot"
            ? await RunCallerSnapshotAsync(command, project.Context, snapshot, cancellation.Token)
            : await RunAsync(commandName, command, project.Context, cancellation.Token);

        Assert.Equal(category == "cancel" ? 130 : 1, result.ExitCode);
        Assert.Equal(category == "process-release" ? OwnedProcessReleaseProof.Unproven
            : OwnedProcessReleaseProof.ProvenOrNotStarted, result.OwnedProcessReleaseProof);
        Assert.Contains(stage.Description, result.StandardError, StringComparison.Ordinal);
        Assert.Empty(result.StandardOutput);
        Assert.Equal("previous-bin", File.ReadAllText(project.Context.BinDocumentPath, Encoding.UTF8));
        Assert.Equal("previous-publish", File.ReadAllText(project.Context.PublishDocumentPath, Encoding.UTF8));
        Assert.Equal("previous-caller", File.ReadAllText(snapshot.OutputPath, Encoding.UTF8));
        Assert.Equal("Attribute VB_Name = \"Local\"", File.ReadAllText(Path.Combine(snapshot.SourcePath, "Local.bas")));
        var staging = EnumerateOwnedStaging(Path.GetDirectoryName(snapshot.OutputPath)!)
            .Concat(EnumerateOwnedStaging(project.SelectedOutputDirectory("build")))
            .Concat(EnumerateOwnedStaging(project.SelectedOutputDirectory("publish"))).ToArray();
        if (category == "process-release")
            Assert.Contains(Assert.Single(staging), result.StandardError, StringComparison.Ordinal);
        else
            Assert.Empty(staging);
    }

    [Fact]
    public async Task SnapshotCancellationAfterAtomicCommitKeepsCommittedCallerOutput()
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var project = CreateProject(temp);
        var snapshot = CreateCallerSnapshot(temp);
        var command = CreateCommand(project.Context, new CompletingWorkbookGenerationAutomation(),
            new CancelAfterCommitTransactionFactory(cancellation));

        var result = await RunCallerSnapshotAsync(command, project.Context, snapshot, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("new-template", File.ReadAllText(snapshot.OutputPath, Encoding.UTF8));
        Assert.Equal("previous-bin", File.ReadAllText(project.Context.BinDocumentPath, Encoding.UTF8));
        Assert.Equal("previous-publish", File.ReadAllText(project.Context.PublishDocumentPath, Encoding.UTF8));
        Assert.True(File.Exists(Path.Combine(snapshot.SourcePath, "Local.bas")));
    }

    private sealed record CallerSnapshot(string SourcePath, string OutputPath, string CaptureRoot);

    private static CallerSnapshot CreateCallerSnapshot(TempDirectory temp)
    {
        var source = temp.CreateDirectory("caller-snapshot");
        File.WriteAllText(Path.Combine(source, "Local.bas"), "Attribute VB_Name = \"Local\"", new UTF8Encoding(false));
        var output = Path.Combine(temp.CreateDirectory("caller-output"), "Book1.xlsm");
        File.WriteAllText(output, "previous-caller", Encoding.UTF8);
        return new(source, output, temp.CreateDirectory("snapshot-capture"));
    }

    private static Task<CommandResult> RunCallerSnapshotAsync(WorkbookOutputCommand command,
        ResolvedProjectContext context, CallerSnapshot snapshot, CancellationToken cancellationToken)
        => command.RunSnapshotBuildAsync(context, snapshot.SourcePath, snapshot.OutputPath,
            new BuildSourceSnapshotCaptureFactory(new WindowsExactFileSystemObjectOwnershipFactory(), snapshot.CaptureRoot),
            new BuildSourceSnapshotOutputSafetyValidator(new FileSystemPathIdentityResolver()), cancellationToken);

    private sealed class TerminalWorkbookGenerationAutomation(bool saved, Func<CancellationToken, Exception> failure)
        : IWorkbookGenerationAutomation
    {
        public async Task<TResult> RunAsync<TResult>(string workbookPath, WorkbookAutomationTimeouts timeouts,
            Func<IWorkbookGenerationSession, CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken)
        {
            if (saved) await operation(new EmptyWorkbookGenerationSession(), cancellationToken);
            throw failure(cancellationToken);
        }
    }

    [Theory]
    [InlineData("build")]
    [InlineData("publish")]
    public async Task CancellationWithUnprovedProcessReleaseReturnsFailure(string commandName)
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var project = CreateProject(temp);
        var automation = new FailingWorkbookGenerationAutomation(token =>
        {
            cancellation.Cancel();
            return new WorkbookAutomationCanceledException(
                new WorkbookAutomationStage(WorkbookAutomationStageKind.WorkbookSave), token,
                new WorkbookAutomationCleanupException("Owned release could not be proved."));
        });

        var result = await RunAsync(commandName, CreateCommand(project.Context, automation),
            project.Context, cancellation.Token);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(OwnedProcessReleaseProof.Unproven, result.OwnedProcessReleaseProof);
        Assert.Contains("workbook save", result.StandardError, StringComparison.Ordinal);
        Assert.Equal("previous-bin", File.ReadAllText(project.Context.BinDocumentPath, Encoding.UTF8));
        Assert.Equal("previous-publish", File.ReadAllText(project.Context.PublishDocumentPath, Encoding.UTF8));
        Assert.Contains(Assert.Single(EnumerateOwnedStaging(project.SelectedOutputDirectory(commandName))),
            result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CleanupProofFailureIsClassifiedForDependentWorkspaceRetention()
    {
        using var temp = TempDirectory.Create();
        var project = CreateProject(temp);
        var automation = new FailingWorkbookGenerationAutomation(
            _ => new WorkbookAutomationCleanupException(
                "The owned Excel process could not be verified as released."));
        var command = CreateCommand(project.Context, automation);

        var result = await RunAsync(
            "build",
            command,
            project.Context,
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(OwnedProcessReleaseProof.Unproven, result.OwnedProcessReleaseProof);
    }

    [Fact]
    public async Task CleanupFailureAfterReleaseDoesNotClaimReleaseProofFailure()
    {
        using var temp = TempDirectory.Create();
        var project = CreateProject(temp);
        var automation = new FailingWorkbookGenerationAutomation(
            _ => new WorkbookAutomationReleasedProcessCleanupException(
                "The Excel STA dispatcher could not be disposed cleanly."));
        var command = CreateCommand(project.Context, automation);

        var result = await RunAsync(
            "build",
            command,
            project.Context,
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(
            OwnedProcessReleaseProof.ProvenOrNotStarted,
            result.OwnedProcessReleaseProof);
    }

    [Theory]
    [InlineData("build")]
    [InlineData("publish")]
    public async Task StageTimeoutPreservesCompletedOutputsAndRemovesOwnedStaging(string commandName)
    {
        using var temp = TempDirectory.Create();
        var project = CreateProject(temp);
        var automation = new FailingWorkbookGenerationAutomation(
            _ => new WorkbookAutomationTimeoutException(
                new WorkbookAutomationStage(
                    WorkbookAutomationStageKind.ModuleImport,
                    "Local.bas"),
                TimeSpan.FromSeconds(30)));
        var command = CreateCommand(project.Context, automation);

        var result = await RunAsync(commandName, command, project.Context, CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("module import 'Local.bas'", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("30 seconds", result.StandardError, StringComparison.Ordinal);
        Assert.Equal("previous-bin", File.ReadAllText(project.Context.BinDocumentPath, Encoding.UTF8));
        Assert.Equal("previous-publish", File.ReadAllText(project.Context.PublishDocumentPath, Encoding.UTF8));
        Assert.Empty(EnumerateOwnedStaging(project.SelectedOutputDirectory(commandName)));
    }

    [Theory]
    [InlineData("build")]
    [InlineData("publish")]
    public async Task OwnedProcessLossReportsItsStageAndPreservesCompletedOutputs(string commandName)
    {
        using var temp = TempDirectory.Create();
        var project = CreateProject(temp);
        var automation = new FailingWorkbookGenerationAutomation(
            _ => new WorkbookAutomationProcessLostException(
                new WorkbookAutomationStage(WorkbookAutomationStageKind.WorkbookSave)));
        var command = CreateCommand(project.Context, automation);

        var result = await RunAsync(commandName, command, project.Context, CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("owned Excel process exited", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("workbook save", result.StandardError, StringComparison.Ordinal);
        Assert.Equal("previous-bin", File.ReadAllText(project.Context.BinDocumentPath, Encoding.UTF8));
        Assert.Equal("previous-publish", File.ReadAllText(project.Context.PublishDocumentPath, Encoding.UTF8));
        Assert.Empty(EnumerateOwnedStaging(project.SelectedOutputDirectory(commandName)));
    }

    [Theory]
    [InlineData("build")]
    [InlineData("publish")]
    public async Task CooperativeCancellationReturns130WithItsStageAndPreservesCompletedOutputs(string commandName)
    {
        using var temp = TempDirectory.Create();
        var project = CreateProject(temp);
        using var cancellation = new CancellationTokenSource();
        var automation = new FailingWorkbookGenerationAutomation(
            token =>
            {
                cancellation.Cancel();
                return new WorkbookAutomationCanceledException(
                    new WorkbookAutomationStage(
                        WorkbookAutomationStageKind.ReferenceAttempt,
                        "Microsoft Scripting Runtime"),
                    token);
            });
        var command = CreateCommand(project.Context, automation);

        var result = await RunAsync(commandName, command, project.Context, cancellation.Token);

        Assert.Equal(130, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("reference attempt 'Microsoft Scripting Runtime'", result.StandardError, StringComparison.Ordinal);
        Assert.Equal("previous-bin", File.ReadAllText(project.Context.BinDocumentPath, Encoding.UTF8));
        Assert.Equal("previous-publish", File.ReadAllText(project.Context.PublishDocumentPath, Encoding.UTF8));
        Assert.Empty(EnumerateOwnedStaging(project.SelectedOutputDirectory(commandName)));
    }

    [Theory]
    [InlineData("build")]
    [InlineData("publish")]
    public async Task CancellationAfterAtomicCommitDoesNotOverrideCommandSuccess(string commandName)
    {
        using var temp = TempDirectory.Create();
        var project = CreateProject(temp);
        using var cancellation = new CancellationTokenSource();
        var command = CreateCommand(
            project.Context,
            new CompletingWorkbookGenerationAutomation(),
            new CancelAfterCommitTransactionFactory(cancellation));

        var result = await RunAsync(commandName, command, project.Context, cancellation.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        Assert.True(cancellation.IsCancellationRequested);
        var selectedOutputPath = commandName == "build"
            ? project.Context.BinDocumentPath
            : project.Context.PublishDocumentPath;
        var siblingOutputPath = commandName == "build"
            ? project.Context.PublishDocumentPath
            : project.Context.BinDocumentPath;
        Assert.Equal("new-template", File.ReadAllText(selectedOutputPath, Encoding.UTF8));
        Assert.Equal(
            commandName == "build" ? "previous-publish" : "previous-bin",
            File.ReadAllText(siblingOutputPath, Encoding.UTF8));
        Assert.Empty(EnumerateOwnedStaging(project.SelectedOutputDirectory(commandName)));
    }

    [Theory]
    [InlineData("build", "project", "containing project 'Local'")]
    [InlineData("build", "module", "actual retained component identity at index 0 is incomplete")]
    [InlineData("build", "reference", "active reference 'Local'")]
    [InlineData("publish", "project", "containing project 'Local'")]
    [InlineData("publish", "module", "actual retained component identity at index 0 is incomplete")]
    [InlineData("publish", "reference", "active reference 'Local'")]
    public async Task FinalAuthorityChangedByImportPreventsSaveAndCommit(
        string commandName,
        string changedAuthority,
        string expectedConflict)
    {
        using var temp = TempDirectory.Create();
        var project = CreateProject(temp);
        var automation = new ImportChangingWorkbookAuthorityAutomation(changedAuthority);
        var command = CreateCommand(project.Context, automation);

        var result = await RunAsync(
            commandName,
            command,
            project.Context,
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains(
            expectedConflict,
            result.StandardError,
            StringComparison.Ordinal);
        Assert.Equal(0, automation.SaveCalls);
        Assert.Equal(
            "previous-bin",
            File.ReadAllText(project.Context.BinDocumentPath, Encoding.UTF8));
        Assert.Equal(
            "previous-publish",
            File.ReadAllText(project.Context.PublishDocumentPath, Encoding.UTF8));
        Assert.Empty(EnumerateOwnedStaging(project.SelectedOutputDirectory(commandName)));
    }

    [Theory]
    [InlineData("build", "Built")]
    [InlineData("publish", "Published")]
    public async Task VerifiedImportedComponentIsExcludedFromRetainedAuthorityAndResultStaysStable(
        string commandName,
        string completedVerb)
    {
        using var temp = TempDirectory.Create();
        var project = CreateProject(temp);
        var automation = new ImportChangingWorkbookAuthorityAutomation("valid");
        var command = CreateCommand(project.Context, automation);

        var result = await RunAsync(
            commandName,
            command,
            project.Context,
            CancellationToken.None);

        var selectedOutputPath = commandName == "build"
            ? project.Context.BinDocumentPath
            : project.Context.PublishDocumentPath;
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            $"{completedVerb} {selectedOutputPath}{Environment.NewLine}" +
            $"Imported 1 source files.{Environment.NewLine}",
            result.StandardOutput);
        Assert.Empty(result.StandardError);
        Assert.Equal(1, automation.SaveCalls);
        Assert.Equal("new-template", File.ReadAllText(selectedOutputPath, Encoding.UTF8));
        Assert.Empty(EnumerateOwnedStaging(project.SelectedOutputDirectory(commandName)));
    }

    [Theory]
    [InlineData("build", "empty", "saved staging workbook is empty")]
    [InlineData("build", "missing", "saved staging workbook could not be read")]
    [InlineData("publish", "empty", "saved staging workbook is empty")]
    [InlineData("publish", "missing", "saved staging workbook could not be read")]
    public async Task InvalidSavedStagingPreventsCommitAndPreservesPreviousOutput(
        string commandName,
        string invalidation,
        string expectedError)
    {
        using var temp = TempDirectory.Create();
        var project = CreateProject(temp);
        var command = CreateCommand(
            project.Context,
            new InvalidatingSavedStagingAutomation(invalidation));

        var result = await RunAsync(
            commandName,
            command,
            project.Context,
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains(
            expectedError,
            result.StandardError,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "previous-bin",
            File.ReadAllText(project.Context.BinDocumentPath, Encoding.UTF8));
        Assert.Equal(
            "previous-publish",
            File.ReadAllText(project.Context.PublishDocumentPath, Encoding.UTF8));
        Assert.Empty(EnumerateOwnedStaging(project.SelectedOutputDirectory(commandName)));
    }

    [Fact]
    public async Task PublishUsesItsIncludedCaptureWhenTheAuthoringSourceChangesBeforeStaging()
    {
        using var temp = TempDirectory.Create();
        var project = CreateProject(temp);
        var sourcePath = Path.Combine(project.Context.DocumentSourceSetPath, "Local.bas");
        var automation = new CompletingWorkbookGenerationAutomation();
        var originalBytes = File.ReadAllBytes(sourcePath);
        var reads = 0;
        var admission = new VbaSourceAdmission(() => 65001, readAllBytes: path =>
        {
            reads++;
            var bytes = File.ReadAllBytes(path);
            File.WriteAllText(
                sourcePath,
                "Attribute VB_Name = \"Local\"\r\n'#ExcludePublish\r\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            return bytes;
        });
        var stagedSourceObserved = false;
        var importSourceSetFactory = new VbeImportSourceSetFactory(new WindowsExactFileSystemObjectOwnershipFactory(),
            mirror =>
            {
                stagedSourceObserved = true;
                Assert.Equal(originalBytes, Assert.Single(mirror.Admission!.Sources).OriginalBytes.ToArray());
                Assert.DoesNotContain("ExcludePublish", File.ReadAllText(Assert.Single(mirror.SourceFiles).SourcePath));
            });
        var command = CreateCommand(
            project.Context,
            automation,
            importSourceSetFactory: importSourceSetFactory,
            sourcePlanner: new WorkbookSourcePlanner(admission));

        var result = await RunAsync(
            "publish",
            command,
            project.Context,
            CancellationToken.None);

        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.Equal(1, reads);
        Assert.True(stagedSourceObserved);
        Assert.Equal(1, automation.RunCount);
        Assert.Equal("new-template", File.ReadAllText(project.Context.PublishDocumentPath, Encoding.UTF8));
        Assert.Contains("ExcludePublish", File.ReadAllText(sourcePath));
        Assert.Empty(EnumerateOwnedStaging(project.SelectedOutputDirectory("publish")));
    }

    [Theory]
    [InlineData("build")]
    [InlineData("publish")]
    public void OutputGenerationDoesNotRunDoctorOrValidateTheCommonModulesRepository(string commandName)
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var commonModulesRepository = temp.CreateDirectory("common_modules_repo");
        File.WriteAllText(
            Path.Combine(commonModulesRepository, "common-modules-manifest.tsv"),
            "malformed repository metadata",
            Encoding.UTF8);
        var manifest = ProjectManifest.CreateDefault(
            "Project",
            "Book1",
            root,
            commonModulesRepository);
        manifest.Documents["Book1"].CommonModules.Add(
            new InstalledCommonModule(
                "Runtime",
                "Runtime.bas",
                Requested: true,
                TestOnly: false));
        new JsonProjectManifestStore().Save(root, manifest);
        var sourceDirectory = Path.Combine(root, "src", "Book1");
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(
            Path.Combine(sourceDirectory, "Book1.xlsm"),
            "new-template",
            Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(sourceDirectory, "Runtime.bas"),
            "Attribute VB_Name = \"Runtime\"",
            Encoding.UTF8);
        var automation = new FakeWorkbookGenerationAutomation();
        var application = CommandLineTestFactory.Create(
            root,
            environmentDiagnosticPort: new ThrowingEnvironmentDiagnosticPort(),
            workbookGenerationAutomation: automation);

        var result = application.Run([commandName]);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        Assert.Equal(["import:Runtime.bas", "save"], automation.Events);
    }

    [Theory]
    [InlineData("build")]
    [InlineData("publish")]
    public async Task CliForwardsInvocationCancellationToBuildAndPublish(string commandName)
    {
        using var temp = TempDirectory.Create();
        var project = CreateProject(temp);
        var automation = new BlockingOpenWorkbookAutomation();
        var application = CommandLineTestFactory.Create(
            project.Context.ProjectRoot,
            workbookGenerationAutomation: automation);
        using var cancellation = new CancellationTokenSource();
        var invocation = Task.Run(
            () => application.RunAsync([commandName], cancellation.Token));

        await automation.OpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var result = await invocation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(automation.ReceivedCancellationTokenCanBeCanceled);
        Assert.Equal(130, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("cancelled", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("previous-bin", File.ReadAllText(project.Context.BinDocumentPath, Encoding.UTF8));
        Assert.Equal("previous-publish", File.ReadAllText(project.Context.PublishDocumentPath, Encoding.UTF8));
        Assert.Empty(EnumerateOwnedStaging(project.SelectedOutputDirectory(commandName)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnprovedExcelReleaseRetainsEveryDependentScratch(bool inspectMirror)
    {
        using var temp = TempDirectory.Create();
        var project = CreateProject(temp);
        string? workbook = null;
        string? mirror = null;
        var automation = new FailingWorkbookGenerationAutomation(_ =>
        {
            workbook = Assert.Single(EnumerateOwnedStaging(project.SelectedOutputDirectory("build")));
            return new WorkbookAutomationCleanupException("Owned release was not proved");
        });
        var command = CreateCommand(project.Context, automation,
            importSourceSetFactory: new VbeImportSourceSetFactory(new WindowsExactFileSystemObjectOwnershipFactory(), sourceSet => mirror = sourceSet.StagingPath));
        try
        {
            var result = await RunAsync("build", command, project.Context, CancellationToken.None);

            Assert.Equal(1, result.ExitCode);
            if (inspectMirror) Assert.True(Directory.Exists(mirror));
            else Assert.True(File.Exists(workbook));
            Assert.Contains(inspectMirror ? mirror! : workbook!, result.StandardError);
            Assert.Equal("previous-bin", File.ReadAllText(project.Context.BinDocumentPath, Encoding.UTF8));
        }
        finally
        {
            if (mirror is not null && Directory.Exists(mirror)) Directory.Delete(mirror, recursive: true);
        }
    }

    private static WorkbookOutputCommand CreateCommand(
        ResolvedProjectContext context,
        IWorkbookGenerationAutomation automation,
        IWorkbookOutputTransactionFactory? transactionFactory = null,
        VbeImportSourceSetFactory? importSourceSetFactory = null,
        WorkbookSourcePlanner? sourcePlanner = null)
    {
        var pipeline = new WorkbookMaterializer(new WindowsExactFileSystemObjectOwnershipFactory(),
            sourcePlanner ?? new WorkbookSourcePlanner(),
            automation,
            new WorkbookReferenceNormalizer(
                new VbaProjectReferencePlanner(new FakeVbaProjectReferenceResolver())),
            transactionFactory ?? new WorkbookOutputTransactionFactory(new WindowsExactFileSystemObjectOwnershipFactory()),
            importSourceSetFactory ?? new VbeImportSourceSetFactory(new WindowsExactFileSystemObjectOwnershipFactory()));
        return new WorkbookOutputCommand(pipeline);
    }

    private static Task<VbaDev.App.Cli.CommandResult> RunAsync(
        string commandName,
        WorkbookOutputCommand outputCommand,
        ResolvedProjectContext context,
        CancellationToken cancellationToken)
        => commandName switch
        {
            "build" => new BuildCommand(outputCommand, new FileSystemPathIdentityResolver(), new WindowsExactFileSystemObjectOwnershipFactory()).RunAsync(context, cancellationToken),
            "publish" => new PublishCommand(outputCommand).RunAsync(context, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(commandName), commandName, null)
        };

    private static IEnumerable<string> EnumerateOwnedStaging(string directory)
        => Directory.EnumerateFiles(
            directory,
            ".Book1.*.tmp.xlsm",
            SearchOption.TopDirectoryOnly);

    private static ProjectFixture CreateProject(TempDirectory temp)
    {
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        new JsonProjectManifestStore().Save(root, manifest);
        var sourceDirectory = Path.Combine(root, "src", "Book1");
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(
            Path.Combine(sourceDirectory, "Book1.xlsm"),
            "new-template",
            Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(sourceDirectory, "Local.bas"),
            "Attribute VB_Name = \"Local\"",
            Encoding.UTF8);
        var context = new ProjectContextResolver(new JsonProjectManifestStore()).Resolve(
            new ProjectResolutionRequest(root, null, root));
        Directory.CreateDirectory(Path.GetDirectoryName(context.BinDocumentPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(context.PublishDocumentPath)!);
        File.WriteAllText(context.BinDocumentPath, "previous-bin", Encoding.UTF8);
        File.WriteAllText(context.PublishDocumentPath, "previous-publish", Encoding.UTF8);
        return new ProjectFixture(context);
    }

    private sealed record ProjectFixture(ResolvedProjectContext Context)
    {
        public string SelectedOutputDirectory(string commandName)
            => Path.GetDirectoryName(commandName == "build"
                ? Context.BinDocumentPath
                : Context.PublishDocumentPath)!;
    }

    private sealed class FailingWorkbookGenerationAutomation(
        Func<CancellationToken, Exception> createFailure) : IWorkbookGenerationAutomation
    {
        public Task<TResult> RunAsync<TResult>(
            string workbookPath,
            WorkbookAutomationTimeouts timeouts,
            Func<IWorkbookGenerationSession, CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken)
            => Task.FromException<TResult>(createFailure(cancellationToken));
    }

    private sealed class CompletingWorkbookGenerationAutomation : IWorkbookGenerationAutomation
    {
        public int RunCount { get; private set; }

        public Task<TResult> RunAsync<TResult>(
            string workbookPath,
            WorkbookAutomationTimeouts timeouts,
            Func<IWorkbookGenerationSession, CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken)
        {
            RunCount++;
            return operation(new EmptyWorkbookGenerationSession(), cancellationToken);
        }
    }

    private sealed class ImportChangingWorkbookAuthorityAutomation(string changedAuthority)
        : IWorkbookGenerationAutomation
    {
        public string ChangedAuthority { get; } = changedAuthority;

        public int SaveCalls { get; private set; }

        public Task<TResult> RunAsync<TResult>(
            string workbookPath,
            WorkbookAutomationTimeouts timeouts,
            Func<IWorkbookGenerationSession, CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken)
            => operation(new ImportChangingWorkbookAuthoritySession(this), cancellationToken);

        private sealed class ImportChangingWorkbookAuthoritySession(
            ImportChangingWorkbookAuthorityAutomation owner) : IWorkbookGenerationSession
        {
            private bool imported;

            public Task<string> GetProjectNameAsync(CancellationToken cancellationToken)
                => Task.FromResult(imported && owner.ChangedAuthority == "project"
                    ? "Local"
                    : "VbaProject");

            public Task<IReadOnlyList<WorkbookModule>> GetModulesAsync(
                CancellationToken cancellationToken)
            {
                if (owner.ChangedAuthority == "valid")
                {
                    return Task.FromResult<IReadOnlyList<WorkbookModule>>(imported
                        ?
                        [
                            new WorkbookModule("Local", WorkbookModuleKind.StandardModule),
                            new WorkbookModule("ThisWorkbook", WorkbookModuleKind.Document)
                        ]
                        : [new WorkbookModule("ThisWorkbook", WorkbookModuleKind.Document)]);
                }

                return Task.FromResult<IReadOnlyList<WorkbookModule>>(
                    imported && owner.ChangedAuthority == "module"
                        ? [new WorkbookModule(" ", WorkbookModuleKind.Document)]
                        : []);
            }

            public Task<IReadOnlyList<WorkbookReference>> GetReferencesAsync(
                CancellationToken cancellationToken)
                => Task.FromResult<IReadOnlyList<WorkbookReference>>(
                    imported && owner.ChangedAuthority == "reference"
                        ? [new WorkbookReference("ChangedReference", true, "Local")]
                        : []);

            public Task<bool> RemoveReferenceAsync(
                string referenceName,
                CancellationToken cancellationToken)
                => Task.FromResult(true);

            public Task AddReferenceAsync(
                ResolvedVbaProjectReference reference,
                CancellationToken cancellationToken)
                => Task.CompletedTask;

            public Task RemoveModuleAsync(
                string moduleName,
                CancellationToken cancellationToken)
                => Task.CompletedTask;

            public Task ImportModuleAsync(
                VbeImportSourceFile sourceFile,
                CancellationToken cancellationToken)
            {
                imported = true;
                return Task.CompletedTask;
            }

            public Task<VbeImportVerificationReport> VerifyAsync(
                CancellationToken cancellationToken)
                => Task.FromResult(VbeImportVerificationReport.Empty);

            public Task SaveAsync(CancellationToken cancellationToken)
            {
                owner.SaveCalls++;
                return Task.CompletedTask;
            }
        }
    }

    private sealed class InvalidatingSavedStagingAutomation(string invalidation)
        : IWorkbookGenerationAutomation
    {
        public async Task<TResult> RunAsync<TResult>(
            string workbookPath,
            WorkbookAutomationTimeouts timeouts,
            Func<IWorkbookGenerationSession, CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken)
        {
            var workbook = new FakeWorkbookGenerationAutomation
            {
                OnSave = () =>
                {
                    if (invalidation == "missing") File.Delete(workbookPath);
                    else File.WriteAllBytes(workbookPath, []);
                }
            };
            return await workbook.RunAsync(workbookPath, timeouts, operation, cancellationToken);
        }
    }

    private sealed class EmptyWorkbookGenerationSession : IWorkbookGenerationSession
    {
        public Task<string> GetProjectNameAsync(CancellationToken cancellationToken)
            => Task.FromResult("VbaProject");

        public Task<IReadOnlyList<WorkbookModule>> GetModulesAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<WorkbookModule>>([]);

        public Task<IReadOnlyList<WorkbookReference>> GetReferencesAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<WorkbookReference>>([]);

        public Task<bool> RemoveReferenceAsync(string referenceName, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task AddReferenceAsync(
            ResolvedVbaProjectReference reference,
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task RemoveModuleAsync(string moduleName, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task ImportModuleAsync(VbeImportSourceFile sourceFile, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task ExportModuleAsync(
            string moduleName,
            string destinationPath,
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<VbeImportVerificationReport> VerifyAsync(CancellationToken cancellationToken)
            => Task.FromResult(VbeImportVerificationReport.Empty);

        public Task SaveAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class CancelAfterCommitTransactionFactory(
        CancellationTokenSource cancellation) : IWorkbookOutputTransactionFactory
    {
        public IWorkbookOutputTransaction Create(string templateWorkbookPath, string targetWorkbookPath)
            => new CancelAfterCommitTransaction(
                WorkbookOutputTransaction.Create(new WindowsExactFileSystemObjectOwnershipFactory(), templateWorkbookPath, targetWorkbookPath),
                cancellation);
    }

    private sealed class CancelAfterCommitTransaction(
        WorkbookOutputTransaction inner,
        CancellationTokenSource cancellation) : IWorkbookOutputTransaction
    {
        public string StagingWorkbookPath => inner.StagingWorkbookPath;

        public void RetainWithoutCleanup() => inner.RetainWithoutCleanup();

        public void CaptureSavedWorkbook() => inner.CaptureSavedWorkbook();

        public void CompleteSavedCapture() => inner.CompleteSavedCapture();

        public void Commit()
        {
            inner.Commit();
            cancellation.Cancel();
        }

        public void Dispose() => inner.Dispose();
    }

    private sealed class ThrowingEnvironmentDiagnosticPort : IEnvironmentDiagnosticPort
    {
        public Task<EnvironmentDiagnosticRun> RunEnvironmentDiagnosticsAsync(
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("Doctor must not run during build or publish.");
    }

    private sealed class BlockingOpenWorkbookAutomation : IWorkbookGenerationAutomation
    {
        public TaskCompletionSource OpenStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ReceivedCancellationTokenCanBeCanceled { get; private set; }

        public async Task<TResult> RunAsync<TResult>(
            string workbookPath,
            WorkbookAutomationTimeouts timeouts,
            Func<IWorkbookGenerationSession, CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken)
        {
            ReceivedCancellationTokenCanBeCanceled = cancellationToken.CanBeCanceled;
            OpenStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Cancellation was expected to stop workbook generation.");
        }
    }
}
