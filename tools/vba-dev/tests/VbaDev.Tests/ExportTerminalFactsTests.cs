using VbaDev.App.Export;
using VbaDev.App.Workbooks;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;
using Xunit;

namespace VbaDev.Tests;

public sealed class ExportTerminalFactsTests
{
    public static IEnumerable<object[]> Failures()
    {
        foreach (var explicitWorkbook in new[] { false, true })
        foreach (var category in new[] { "cancel", "timeout", "process-loss", "com", "process-release", "dispatcher", "secondary-cleanup" })
        foreach (var cancelled in new[] { false, true })
        foreach (var nested in new[] { false, true })
            yield return [explicitWorkbook, category, cancelled, nested];
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task BothEntryPointsPreserveTerminalEvidence(bool explicitWorkbook, string category, bool cancelled, bool nested)
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var (workbook, destination) = CreateProject(temp);
        var stage = new WorkbookAutomationStage(WorkbookAutomationStageKind.ModuleExport, "Module1");
        Exception error = category switch
        {
            "cancel" => new WorkbookAutomationCanceledException(stage, cancellation.Token),
            "timeout" => new WorkbookAutomationTimeoutException(stage, TimeSpan.FromSeconds(1)),
            "process-loss" => new WorkbookAutomationProcessLostException(stage),
            "com" => new System.Runtime.InteropServices.COMException("COM detail"),
            "process-release" or "dispatcher" => new WorkbookAutomationCleanupException("Release detail"),
            _ => new WorkbookAutomationReleasedProcessCleanupException("Secondary cleanup detail")
        };
        if (error is IWorkbookAutomationLifecycleFailure lifecycle)
            lifecycle.LifecycleEvidence = new(stage, category != "process-release", category != "dispatcher", cancelled);
        if (nested)
            error = new AggregateException(new InvalidOperationException("Nested evidence", error),
                new WorkbookAutomationCanceledException(stage, cancellation.Token));
        error = new WorkbookAutomationStageFailureException(stage, error);
        var exporter = new FakeWorkbookModuleExporter
        {
            OnExport = _ =>
            {
                if (cancelled) cancellation.Cancel();
                throw error;
            }
        };
        var application = CommandLineTestFactory.Create(temp.Path, workbookModuleExporter: exporter);

        var result = await application.RunAsync(Arguments(explicitWorkbook, workbook, destination), cancellation.Token);

        Assert.Equal(category == "cancel" ? 130 : 1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("module export 'Module1'", result.StandardError);
        Assert.Equal("old module", File.ReadAllText(Path.Combine(destination, "Module1.bas")));
        Assert.Empty(Directory.GetDirectories(destination));
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
}
