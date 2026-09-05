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
        var importSourceSetFactory = new VbeImportSourceSetFactory(
            () => throw new InvalidOperationException("ACP must come from admission."),
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
        var automation = new FakeWorkbookBuildAutomation();
        var application = CommandLineTestFactory.Create(
            root,
            environmentDiagnosticPort: new ThrowingEnvironmentDiagnosticPort(),
            workbookBuildAutomation: automation);

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
            workbookBuildAutomation: automation);
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

    private static WorkbookOutputCommand CreateCommand(
        ResolvedProjectContext context,
        IWorkbookGenerationAutomation automation,
        IWorkbookOutputTransactionFactory? transactionFactory = null,
        VbeImportSourceSetFactory? importSourceSetFactory = null,
        WorkbookSourcePlanner? sourcePlanner = null)
    {
        var pipeline = new WorkbookGenerationPipeline(
            automation,
            new WorkbookReferenceNormalizer(
                new VbaProjectReferencePlanner(new FakeVbaProjectReferenceResolver())),
            transactionFactory ?? new WorkbookOutputTransactionFactory(),
            importSourceSetFactory ?? new VbeImportSourceSetFactory());
        return new WorkbookOutputCommand(sourcePlanner ?? new WorkbookSourcePlanner(), pipeline);
    }

    private static Task<VbaDev.App.Cli.CommandResult> RunAsync(
        string commandName,
        WorkbookOutputCommand outputCommand,
        ResolvedProjectContext context,
        CancellationToken cancellationToken)
        => commandName switch
        {
            "build" => new BuildCommand(outputCommand).RunAsync(context, cancellationToken),
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
                WorkbookOutputTransaction.Create(templateWorkbookPath, targetWorkbookPath),
                cancellation);
    }

    private sealed class CancelAfterCommitTransaction(
        WorkbookOutputTransaction inner,
        CancellationTokenSource cancellation) : IWorkbookOutputTransaction
    {
        public string StagingWorkbookPath => inner.StagingWorkbookPath;

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

    private sealed class BlockingOpenWorkbookAutomation : IWorkbookBuildAutomation
    {
        public TaskCompletionSource OpenStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ReceivedCancellationTokenCanBeCanceled { get; private set; }

        public IWorkbookBuildSession OpenWorkbook(string workbookPath)
            => throw new InvalidOperationException("The cancellable workbook-open overload was not used.");

        public IWorkbookBuildSession OpenWorkbook(
            string workbookPath,
            CancellationToken cancellationToken)
        {
            ReceivedCancellationTokenCanBeCanceled = cancellationToken.CanBeCanceled;
            OpenStarted.TrySetResult();
            if (!cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The CLI did not forward invocation cancellation.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Cancellation was expected to stop workbook open.");
        }
    }
}
