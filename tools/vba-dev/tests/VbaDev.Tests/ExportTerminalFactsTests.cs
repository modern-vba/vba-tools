using VbaDev.App.Cli;
using VbaDev.App.Export;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;
using VbaDev.Domain;
using VbaDev.Infrastructure.FileSystem;
using VbaDev.Infrastructure.Projects;
using Xunit;

namespace VbaDev.Tests;

public sealed class ExportTerminalFactsTests
{
    [Theory]
    [InlineData(false, "direct")]
    [InlineData(false, "stage")]
    [InlineData(false, "aggregate")]
    [InlineData(true, "direct")]
    [InlineData(true, "stage")]
    [InlineData(true, "aggregate")]
    public async Task BothEntryPointsUseFriendlyGuidanceForNestedPrimaryComFailure(bool explicitWorkbook, string shape)
    {
        using var temp = TempDirectory.Create();
        var (workbook, destination) = CreateProject(temp);
        var modulePath = Path.Combine(destination, "Module1.bas");
        var previousModule = File.ReadAllBytes(modulePath);
        var stage = new WorkbookAutomationStage(WorkbookAutomationStageKind.ModuleExport, "Module1");
        var com = new System.Runtime.InteropServices.COMException("Nested export COM detail");
        Exception failure = shape switch
        {
            "direct" => com,
            "stage" => new WorkbookAutomationStageFailureException(stage, com),
            _ => new WorkbookAutomationStageFailureException(stage,
                new AggregateException(new InvalidOperationException("Export context", com),
                    new WorkbookAutomationCanceledException(stage, CancellationToken.None)))
        };
        var exporter = new FakeWorkbookModuleExporter { ExportError = failure };
        var application = CommandLineTestFactory.Create(temp.Path, workbookModuleExporter: exporter);

        var result = await application.RunAsync(
            Arguments(explicitWorkbook, workbook, destination),
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal(
            CommandErrorMessages.ExcelComAutomationFailed("export", failure) + Environment.NewLine,
            result.StandardError);
        Assert.Equal(previousModule, File.ReadAllBytes(modulePath));
        Assert.Empty(Directory.GetDirectories(destination));
        Assert.False(Directory.Exists(Assert.Single(exporter.Calls).DestinationDirectory));
    }

    public static IEnumerable<object[]> Failures()
    {
        foreach (var explicitWorkbook in new[] { false, true })
        {
            yield return [explicitWorkbook, "cancel", false];
            yield return [explicitWorkbook, "com", false];
            yield return [explicitWorkbook, "process-release", true];
            yield return [explicitWorkbook, "dispatcher", true];
            yield return [explicitWorkbook, "secondary-cleanup", true];
        }
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task BothEntryPointsProjectTerminalFailureAndScratchRelease(bool explicitWorkbook, string category, bool cancelled)
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var (workbook, destination) = CreateProject(temp);
        var stage = new WorkbookAutomationStage(WorkbookAutomationStageKind.ModuleExport, "Module1");
        Exception error = category switch
        {
            "cancel" => new WorkbookAutomationCanceledException(stage, cancellation.Token),
            "com" => new System.Runtime.InteropServices.COMException("COM detail"),
            "process-release" or "dispatcher" => new WorkbookAutomationCleanupException("Release detail"),
            _ => new WorkbookAutomationReleasedProcessCleanupException("Secondary cleanup detail")
        };
        if (error is IWorkbookAutomationLifecycleFailure lifecycle)
            lifecycle.LifecycleEvidence = new(stage, category != "process-release", category != "dispatcher", cancelled);
        error = new WorkbookAutomationStageFailureException(stage, error);
        var exporter = new FakeWorkbookModuleExporter
        {
            OnExport = _ =>
            {
                if (cancelled) cancellation.Cancel();
                throw error;
            }
        };
        var result = await RunExportAsync(temp.Path, explicitWorkbook, workbook, destination, exporter, cancellation.Token);

        Assert.Equal(category == "cancel" ? 130 : 1, result.ExitCode);
        Assert.Equal(category == "process-release" ? OwnedProcessReleaseProof.Unproven
            : OwnedProcessReleaseProof.ProvenOrNotStarted, result.OwnedProcessReleaseProof);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("module export 'Module1'", result.StandardError);
        Assert.Equal("old module", File.ReadAllText(Path.Combine(destination, "Module1.bas")));
        Assert.Empty(Directory.GetDirectories(destination));
        var stagingPath = Assert.Single(exporter.Calls).DestinationDirectory;
        var expectedError = (category == "com" ? CommandErrorMessages.ExcelComAutomationFailed("export", error) : error.Message)
            + Environment.NewLine;
        if (category == "process-release")
        {
            expectedError += $"Export staging was retained because owned Excel process release could not be proved: {stagingPath}{Environment.NewLine}";
            Assert.True(Directory.Exists(stagingPath));
            Directory.Delete(stagingPath, recursive: true);
        }
        else
        {
            Assert.False(Directory.Exists(stagingPath));
        }
        Assert.Equal(expectedError, result.StandardError);
    }

    [Theory]
    [InlineData(false, "direct")]
    [InlineData(false, "wrapped")]
    [InlineData(false, "unknown")]
    [InlineData(true, "direct")]
    [InlineData(true, "wrapped")]
    [InlineData(true, "unknown")]
    public async Task BothEntryPointsRethrowUntrustedCancellationAndUnknownDefects(bool explicitWorkbook, string shape)
    {
        using var temp = TempDirectory.Create();
        var (workbook, destination) = CreateProject(temp);
        var modulePath = Path.Combine(destination, "Module1.bas");
        var previousModule = File.ReadAllBytes(modulePath);
        var cancellation = new OperationCanceledException("Unrelated cancellation");
        Exception error = shape switch
        {
            "direct" => cancellation,
            "wrapped" => new InvalidOperationException("Unexpected wrapper", cancellation),
            _ => new AggregateException(cancellation, new NullReferenceException("Unexpected defect"))
        };
        var exporter = new FakeWorkbookModuleExporter { ExportError = error };

        var observed = await Record.ExceptionAsync(() => RunExportAsync(
            temp.Path, explicitWorkbook, workbook, destination, exporter, CancellationToken.None));

        Assert.Same(error, observed);
        Assert.Equal(previousModule, File.ReadAllBytes(modulePath));
        Assert.Empty(Directory.GetDirectories(destination));
        Assert.False(Directory.Exists(Assert.Single(exporter.Calls).DestinationDirectory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DestinationCommitmentSurvivesLaterCancellation(bool explicitWorkbook)
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var (workbook, destination) = CreateProject(temp);
        var application = CommandLineTestFactory.Create(temp.Path,
            workbookModuleExporter: new FakeWorkbookModuleExporter(("Module1.bas", "new module")),
            exportDestinationFileOperations: new CancelAfterDestinationCommit(cancellation));

        var result = await application.RunAsync(Arguments(explicitWorkbook, workbook, destination), cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        Assert.Equal("new module", File.ReadAllText(Path.Combine(destination, "Module1.bas")));
        Assert.Empty(Directory.GetDirectories(destination));
    }

    [Fact]
    public async Task ProjectExportKeepsRawInputErrorFallbackForWrappedUnknownCancellationMixture()
    {
        using var temp = TempDirectory.Create();
        var (workbook, destination) = CreateProject(temp);
        var modulePath = Path.Combine(destination, "Module1.bas");
        var previousModule = File.ReadAllBytes(modulePath);
        var error = new InvalidOperationException("Outer",
            new AggregateException(new OperationCanceledException(), new NullReferenceException("Defect")));
        var exporter = new FakeWorkbookModuleExporter { ExportError = error };

        var result = await RunExportAsync(
            temp.Path, false, workbook, destination, exporter, CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("Outer" + Environment.NewLine, result.StandardError);
        Assert.Empty(result.StandardOutput);
        Assert.Equal(OwnedProcessReleaseProof.ProvenOrNotStarted, result.OwnedProcessReleaseProof);
        Assert.Equal(previousModule, File.ReadAllBytes(modulePath));
        Assert.Empty(Directory.GetDirectories(destination));
        Assert.False(Directory.Exists(Assert.Single(exporter.Calls).DestinationDirectory));
    }

    private sealed class CancelAfterDestinationCommit(CancellationTokenSource cancellation) : IExportDestinationFileOperations
    {
        public void CreateDirectory(string path) => Directory.CreateDirectory(path);
        public void CopyFile(string source, string destination, bool overwrite) => File.Copy(source, destination, overwrite);
        public void DeleteFile(string path) => File.Delete(path);
        public void DeleteDirectory(string path, bool recursive)
        {
            Directory.Delete(path, recursive);
            if (Path.GetFileName(path).StartsWith(".vba-dev-export-recovery-", StringComparison.Ordinal))
                cancellation.Cancel();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationCannotHideUnprovedProcessRelease(bool explicitWorkbook)
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var (workbook, destination) = CreateProject(temp);
        var exporter = new FakeWorkbookModuleExporter
        {
            OnExport = _ =>
            {
                cancellation.Cancel();
                throw new WorkbookAutomationCanceledException(
                    new(WorkbookAutomationStageKind.ModuleExport, "Module1"), cancellation.Token,
                    new WorkbookAutomationCleanupException("Owned release could not be proved."));
            }
        };
        var application = CommandLineTestFactory.Create(temp.Path, workbookModuleExporter: exporter);

        var result = await application.RunAsync(Arguments(explicitWorkbook, workbook, destination), cancellation.Token);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("module export 'Module1'", result.StandardError);
        Assert.Equal("old module", File.ReadAllText(Path.Combine(destination, "Module1.bas")));
    }

    private static (string Workbook, string Destination) CreateProject(TempDirectory temp)
    {
        new JsonProjectManifestStore().Save(temp.Path,
            ProjectManifest.CreateDefault("Project", "Book1", temp.Path, null));
        var workbook = Path.Combine(temp.CreateDirectory("bin"), "Book1.xlsm");
        File.WriteAllText(workbook, "workbook");
        var destination = temp.CreateDirectory("src/Book1");
        File.WriteAllText(Path.Combine(destination, "Module1.bas"), "old module");
        return (workbook, destination);
    }

    private static string[] Arguments(bool explicitWorkbook, string workbook, string destination)
        => explicitWorkbook ? ["export", "--from", workbook, "--to", destination] : ["export"];

    private static Task<CommandResult> RunExportAsync(
        string projectRoot,
        bool explicitWorkbook,
        string workbook,
        string destination,
        IWorkbookModuleExporter exporter,
        CancellationToken cancellationToken)
    {
        var command = new ExportCommand(new WindowsExactFileSystemObjectOwnershipFactory(), exporter);
        if (explicitWorkbook)
        {
            return command.RunExplicitAsync(
                new ExplicitWorkbookExportCommandRequest(workbook, destination, projectRoot),
                cancellationToken);
        }

        var context = new ProjectContextResolver(new JsonProjectManifestStore()).Resolve(
            new ProjectResolutionRequest(projectRoot, null, projectRoot));
        return command.RunAsync(context, new ProjectExportCommandRequest(null, projectRoot), cancellationToken);
    }
}
