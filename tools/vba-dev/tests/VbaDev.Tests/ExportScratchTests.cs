using VbaDev.App.Export;
using VbaDev.App.Workbooks;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;
using Xunit;

namespace VbaDev.Tests;

public sealed class ExportScratchTests
{
    public static IEnumerable<object[]> LifecycleCases()
    {
        foreach (var explicitWorkbook in new[] { false, true })
        foreach (var phase in new[] { "failure", "cancel", "unproved", "committed", "committed-cancel" })
        foreach (var state in new[] { "removed", "changed", "locked", "missing" })
            if (state != "missing" || phase.StartsWith("committed", StringComparison.Ordinal))
                yield return [explicitWorkbook, phase, state];
    }

    [Theory]
    [MemberData(nameof(LifecycleCases))]
    public async Task BothEntryPointsPreserveCommitmentAndCleanupEvidence(bool explicitWorkbook, string phase, string state)
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var (workbook, destination) = CreateProject(temp);
        var committed = phase.StartsWith("committed", StringComparison.Ordinal);
        FileStream? fileLock = null;
        var exporter = new ScenarioExporter();
        void MutateStaging()
        {
            var path = Path.Combine(exporter.StagingPath!, "Module1.bas");
            if (state == "changed") File.WriteAllText(path, "external change");
            else if (state == "locked") fileLock = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            else if (state == "missing") File.Delete(path);
        }
        exporter.AfterProduced = () =>
        {
            if (committed) return;
            MutateStaging();
            if (phase == "cancel")
            {
                cancellation.Cancel();
                throw new WorkbookAutomationCanceledException(new(WorkbookAutomationStageKind.ModuleExport, "Module1"), cancellation.Token);
            }
            if (phase == "unproved") throw new WorkbookAutomationCleanupException("Primary unproved release");
            throw new IOException("Primary export failure");
        };
        var operations = new AfterCommitFileOperations(() =>
        {
            MutateStaging();
            if (phase == "committed-cancel") cancellation.Cancel();
        });
        var app = CommandLineTestFactory.Create(temp.Path, workbookModuleExporter: exporter,
            exportDestinationFileOperations: operations);
        try
        {
            var result = await app.RunAsync(Arguments(explicitWorkbook, workbook, destination), cancellation.Token);
            Assert.Equal(committed ? 0 : phase == "cancel" ? 130 : 1, result.ExitCode);
            Assert.Equal(committed ? "new module" : "old module", File.ReadAllText(Path.Combine(destination, "Module1.bas")));
            if (committed) Assert.StartsWith("Exported ", result.StandardOutput);
            else Assert.Empty(result.StandardOutput);
            if (phase == "unproved")
            {
                Assert.True(Directory.Exists(exporter.StagingPath));
                Assert.Contains(exporter.StagingPath!, result.StandardError);
                Assert.Contains("Primary unproved release", result.StandardError);
            }
            else if (state is "changed" or "locked")
            {
                Assert.Contains(exporter.StagingPath!, result.StandardError);
                Assert.Contains(state == "locked" ? "Inconclusive" : "Retained", result.StandardError);
                Assert.True(File.Exists(Path.Combine(exporter.StagingPath!, "Module1.bas")));
                if (committed) Assert.StartsWith("Warning:", result.StandardError);
            }
            else
            {
                Assert.False(Directory.Exists(exporter.StagingPath));
                if (committed) Assert.Empty(result.StandardError);
                else Assert.DoesNotContain("retained", result.StandardError, StringComparison.OrdinalIgnoreCase);
            }
            if (phase == "failure") Assert.Contains("Primary export failure", result.StandardError);
            if (phase == "cancel") Assert.Contains("cancelled", result.StandardError);
        }
        finally
        {
            fileLock?.Dispose();
            if (exporter.StagingPath is { } staging && Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    private sealed class ScenarioExporter : IWorkbookModuleExporter
    {
        internal string? StagingPath { get; private set; }
        internal Action? AfterProduced { get; set; }
        public async Task ExportModulesAsync(string workbookPath, WorkbookExportStaging staging,
            WorkbookAutomationTimeouts timeouts, CancellationToken cancellationToken)
        {
            StagingPath = staging.Path;
            await staging.WriteModuleAsync("Module1.bas", path =>
            {
                File.WriteAllText(path, "new module");
                return Task.CompletedTask;
            });
            AfterProduced?.Invoke();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnregisteredSourceCannotBecomeCommittedExport(bool explicitWorkbook)
    {
        using var temp = TempDirectory.Create();
        var (workbook, destination) = CreateProject(temp);
        var exporter = new ScenarioExporter();
        exporter.AfterProduced = () => File.WriteAllText(Path.Combine(exporter.StagingPath!, "Foreign.bas"), "foreign source");
        var app = CommandLineTestFactory.Create(temp.Path, workbookModuleExporter: exporter);
        try
        {
            var result = app.Run(Arguments(explicitWorkbook, workbook, destination));
            Assert.Equal(1, result.ExitCode);
            Assert.Equal("old module", File.ReadAllText(Path.Combine(destination, "Module1.bas")));
            Assert.False(File.Exists(Path.Combine(destination, "Foreign.bas")));
            Assert.Equal("foreign source", File.ReadAllText(Path.Combine(exporter.StagingPath!, "Foreign.bas")));
        }
        finally { if (Directory.Exists(exporter.StagingPath)) Directory.Delete(exporter.StagingPath!, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommittedDestinationKeepsSuccessAndReportsForeignStaging(bool explicitWorkbook)
    {
        using var temp = TempDirectory.Create();
        var (workbook, destination) = CreateProject(temp);
        var exporter = new FakeWorkbookModuleExporter(("Module1.bas", "new module"));
        var operations = new AfterCommitFileOperations(() => File.WriteAllText(
            Path.Combine(Assert.Single(exporter.Calls).DestinationDirectory, "foreign.txt"), "foreign content"));
        var app = CommandLineTestFactory.Create(temp.Path, workbookModuleExporter: exporter,
            exportDestinationFileOperations: operations);
        var result = app.Run(Arguments(explicitWorkbook, workbook, destination));
        var staging = Assert.Single(exporter.Calls).DestinationDirectory;
        try
        {
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("new module", File.ReadAllText(Path.Combine(destination, "Module1.bas")));
            Assert.Contains("Warning:", result.StandardError);
            Assert.Contains(staging, result.StandardError);
            Assert.Equal("foreign content", File.ReadAllText(Path.Combine(staging, "foreign.txt")));
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnprovedReleaseDoesNotDeleteDependentStaging(bool explicitWorkbook)
    {
        using var temp = TempDirectory.Create();
        var (workbook, destination) = CreateProject(temp);
        var exporter = new FakeWorkbookModuleExporter
        {
            ExportError = new WorkbookAutomationCleanupException("Owned release not proved")
        };
        var app = CommandLineTestFactory.Create(temp.Path, workbookModuleExporter: exporter);
        var result = app.Run(Arguments(explicitWorkbook, workbook, destination));
        var staging = Assert.Single(exporter.Calls).DestinationDirectory;
        try
        {
            Assert.Equal(1, result.ExitCode);
            Assert.True(Directory.Exists(staging));
            Assert.Contains(staging, result.StandardError);
            Assert.Equal("old module", File.ReadAllText(Path.Combine(destination, "Module1.bas")));
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    private sealed class AfterCommitFileOperations(Action afterCommit) : IExportDestinationFileOperations
    {
        public void CreateDirectory(string path) => Directory.CreateDirectory(path);
        public void CopyFile(string source, string target, bool overwrite) => File.Copy(source, target, overwrite);
        public void DeleteFile(string path) => File.Delete(path);
        public void DeleteDirectory(string path, bool recursive)
        {
            Directory.Delete(path, recursive);
            if (Path.GetFileName(path).StartsWith(".vba-dev-export-recovery-", StringComparison.Ordinal)) afterCommit();
        }
    }

    private static (string Workbook, string Destination) CreateProject(TempDirectory temp)
    {
        new JsonProjectManifestStore().Save(temp.Path, ProjectManifest.CreateDefault("Project", "Book1", temp.Path, null));
        var workbook = Path.Combine(temp.CreateDirectory("bin"), "Book1.xlsm");
        File.WriteAllText(workbook, "workbook");
        var destination = temp.CreateDirectory("src/Book1");
        File.WriteAllText(Path.Combine(destination, "Module1.bas"), "old module");
        return (workbook, destination);
    }

    private static string[] Arguments(bool explicitWorkbook, string workbook, string destination)
        => explicitWorkbook ? ["export", "--from", workbook, "--to", destination] : ["export"];
}
