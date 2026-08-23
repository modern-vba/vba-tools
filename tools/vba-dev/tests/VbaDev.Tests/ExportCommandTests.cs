using System.Text;
using VbaDev.App.Export;
using VbaDev.App.Workbooks;
using VbaDev.Cli;
using VbaDev.Composition;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;
using Xunit;

namespace VbaDev.Tests;

public sealed class ExportCommandTests
{
    [Fact]
    public void DefaultExportUsesTempCleanupAndPreservesExistingLayout()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifestTestData.TwoDocumentManifest(root));
        var sourceSet = CreateDocumentSourceSet(root, "Book1");
        CreateDocumentSourceSet(root, "SecondBook", ("Other.bas", "Attribute VB_Name = \"Other\""));
        var binPath = CreateWorkbook(root, "bin", "Book1");
        WriteText(Path.Combine(sourceSet, "modules", "Module1.bas"), "'#ExcludePublish\nold");
        WriteText(Path.Combine(sourceSet, "old", "Old.cls"), "old");
        WriteText(Path.Combine(sourceSet, "forms", "Dialog.frm"), "old");
        WriteBytes(Path.Combine(sourceSet, "forms", "Dialog.frx"), [9, 9, 9]);
        WriteBytes(Path.Combine(sourceSet, "Dialog.frx"), [8, 8, 8]);
        File.WriteAllText(Path.Combine(sourceSet, "notes.txt"), "keep", Encoding.UTF8);
        Directory.CreateDirectory(Path.Combine(sourceSet, "empty"));
        var exporter = new FakeWorkbookModuleExporter(
            ("Module1.bas", "Attribute VB_Name = \"Module1\""),
            ("Dialog.frm", "VERSION 5.00"),
            ("Dialog.frx", "frx"),
            ("NewModule.cls", "VERSION 1.0 CLASS"));
        var application = CommandLineTestFactory.Create(root, workbookModuleExporter: exporter);

        var result = application.Run(["export"]);

        Assert.Equal(0, result.ExitCode);
        var call = Assert.Single(exporter.Calls);
        Assert.Equal(binPath, call.WorkbookPath);
        Assert.NotEqual(sourceSet, call.DestinationDirectory);
        Assert.False(File.Exists(Path.Combine(sourceSet, "old", "Old.cls")));
        Assert.False(File.Exists(Path.Combine(sourceSet, "Dialog.frx")));
        Assert.True(Directory.Exists(Path.Combine(sourceSet, "old")));
        Assert.True(Directory.Exists(Path.Combine(sourceSet, "empty")));
        Assert.True(File.Exists(Path.Combine(sourceSet, "Book1.xlsm")));
        Assert.True(File.Exists(Path.Combine(root, ProjectManifest.ManifestFileName)));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(sourceSet, "notes.txt"), Encoding.UTF8));
        Assert.True(File.Exists(Path.Combine(root, "src", "SecondBook", "Other.bas")));
        Assert.True(File.Exists(Path.Combine(sourceSet, "modules", "Module1.bas")));
        Assert.DoesNotContain("#ExcludePublish", File.ReadAllText(Path.Combine(sourceSet, "modules", "Module1.bas"), Encoding.UTF8), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(sourceSet, "forms", "Dialog.frm")));
        Assert.Equal("frx", File.ReadAllText(Path.Combine(sourceSet, "forms", "Dialog.frx"), Encoding.UTF8));
        Assert.Equal("VERSION 1.0 CLASS", File.ReadAllText(Path.Combine(sourceSet, "NewModule.cls"), Encoding.UTF8));
    }

    [Fact]
    public void CleanupEnabledExportKeepsAppliedSnapshotWhenRecoveryCleanupPartiallyFails()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = CreateDocumentSourceSet(root, "Book1");
        CreateWorkbook(root, "bin", "Book1");
        WriteText(Path.Combine(sourceSet, "modules", "Module1.bas"), "old module");
        WriteText(Path.Combine(sourceSet, "old", "Old.cls"), "old class");
        File.WriteAllText(Path.Combine(sourceSet, "notes.txt"), "keep", Encoding.UTF8);
        var exporter = new FakeWorkbookModuleExporter(
            ("Module1.bas", "new module"),
            ("NewModule.cls", "new class"));
        var fileOperations = new PostApplyRecoveryCleanupFailureExportDestinationFileOperations();
        var application = CommandLineTestFactory.Create(
            root,
            workbookModuleExporter: exporter,
            exportDestinationFileOperations: fileOperations);

        var result = application.Run(["export"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("apply completed", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("new exact source snapshot remains applied", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rollback", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("copy the recovery area contents back", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("new module", File.ReadAllText(Path.Combine(sourceSet, "modules", "Module1.bas"), Encoding.UTF8));
        Assert.Equal("new class", File.ReadAllText(Path.Combine(sourceSet, "NewModule.cls"), Encoding.UTF8));
        Assert.False(File.Exists(Path.Combine(sourceSet, "old", "Old.cls")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(sourceSet, "notes.txt"), Encoding.UTF8));
        var recoveryDirectory = Assert.Single(Directory.EnumerateDirectories(
            sourceSet,
            ".vba-dev-export-recovery-*",
            SearchOption.TopDirectoryOnly));
        Assert.Contains(Path.GetFullPath(recoveryDirectory), result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(recoveryDirectory, "modules", "Module1.bas")));
        Assert.Equal(
            "old class",
            File.ReadAllText(Path.Combine(recoveryDirectory, "old", "Old.cls"), Encoding.UTF8));
    }

    [Fact]
    public void ProjectExportUsesManifestWorkbookOpenTimeout()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null) with
        {
            CommandDefaults = new CommandDefaults(
                ExcelAutomation: new ExcelAutomationCommandDefaults(
                    WorkbookOpenTimeoutSeconds: 41))
        };
        new JsonProjectManifestStore().Save(root, manifest);
        CreateDocumentSourceSet(root, "Book1");
        CreateWorkbook(root, "bin", "Book1");
        var exporter = new FakeWorkbookModuleExporter(("Module1.bas", "new module"));
        var application = CommandLineTestFactory.Create(root, workbookModuleExporter: exporter);

        var result = application.Run(["export"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            TimeSpan.FromSeconds(41),
            Assert.Single(exporter.AutomationTimeouts).WorkbookOpen);
    }

    [Fact]
    public void ExplicitWorkbookExportDefaultsToWorkingDirectoryWithoutCleaning()
    {
        using var temp = TempDirectory.Create();
        var explicitWorkbook = Path.Combine(temp.Path, "explicit.xlsm");
        File.WriteAllText(explicitWorkbook, "workbook", Encoding.UTF8);
        File.WriteAllText(Path.Combine(temp.Path, "Old.bas"), "old", Encoding.UTF8);
        File.WriteAllText(Path.Combine(temp.Path, "Module1.bas"), "old module", Encoding.UTF8);
        Directory.CreateDirectory(Path.Combine(temp.Path, "forms"));
        File.WriteAllBytes(Path.Combine(temp.Path, "forms", "Dialog.frx"), [9, 9, 9]);
        var exporter = new ExistingFileRejectingWorkbookModuleExporter(
            ("Module1.bas", "new"),
            ("Dialog.frm", "VERSION 5.00"),
            ("Dialog.frx", "frx"));
        var application = CommandLineTestFactory.Create(temp.Path, workbookModuleExporter: exporter);

        var result = application.Run(["export", "--from", explicitWorkbook]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(explicitWorkbook, Assert.Single(exporter.Calls).WorkbookPath);
        Assert.Equal("old", File.ReadAllText(Path.Combine(temp.Path, "Old.bas"), Encoding.UTF8));
        Assert.Equal("new", File.ReadAllText(Path.Combine(temp.Path, "Module1.bas"), Encoding.UTF8));
        Assert.True(File.Exists(Path.Combine(temp.Path, "forms", "Dialog.frx")));
        Assert.Equal("frx", File.ReadAllText(Path.Combine(temp.Path, "Dialog.frx"), Encoding.UTF8));
    }

    [Fact]
    public void ExplicitNonCleanupExportIgnoresUnrelatedReparseSubtrees()
    {
        using var temp = TempDirectory.Create();
        var explicitWorkbook = Path.Combine(temp.Path, "explicit.xlsm");
        File.WriteAllText(explicitWorkbook, "workbook", Encoding.UTF8);
        File.WriteAllText(Path.Combine(temp.Path, "Module1.bas"), "old module", Encoding.UTF8);
        var externalDirectory = temp.CreateDirectory("external");
        var externalSourcePath = Path.Combine(externalDirectory, "External.bas");
        File.WriteAllText(externalSourcePath, "external source", Encoding.UTF8);
        var unrelatedLink = Path.Combine(temp.Path, "unrelated-link");
        Directory.CreateSymbolicLink(unrelatedLink, externalDirectory);
        var exporter = new ExistingFileRejectingWorkbookModuleExporter(
            ("Module1.bas", "new module"));
        var application = CommandLineTestFactory.Create(temp.Path, workbookModuleExporter: exporter);

        var result = application.Run(["export", "--from", explicitWorkbook]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("new module", File.ReadAllText(Path.Combine(temp.Path, "Module1.bas"), Encoding.UTF8));
        Assert.Equal("external source", File.ReadAllText(externalSourcePath, Encoding.UTF8));
        Assert.True(Directory.Exists(unrelatedLink));
    }

    [Fact]
    public void ExplicitNonCleanupExportRejectsAReparsePlacementTarget()
    {
        using var temp = TempDirectory.Create();
        var workingDirectory = temp.CreateDirectory("working");
        var explicitWorkbook = Path.Combine(workingDirectory, "explicit.xlsm");
        File.WriteAllText(explicitWorkbook, "workbook", Encoding.UTF8);
        var externalDirectory = temp.CreateDirectory("external-target");
        var externalSourcePath = Path.Combine(externalDirectory, "External.bas");
        File.WriteAllText(externalSourcePath, "external source", Encoding.UTF8);
        var linkedTarget = Path.Combine(workingDirectory, "Module1.bas");
        File.CreateSymbolicLink(linkedTarget, externalSourcePath);
        var exporter = new FakeWorkbookModuleExporter(("Module1.bas", "new module"));
        var application = CommandLineTestFactory.Create(
            workingDirectory,
            workbookModuleExporter: exporter);

        var result = application.Run(["export", "--from", explicitWorkbook]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Export destination contains a reparse point", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.GetFullPath(linkedTarget), result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("external source", File.ReadAllText(externalSourcePath, Encoding.UTF8));
        Assert.True(File.ResolveLinkTarget(linkedTarget, returnFinalTarget: false) is not null);
    }

    [Fact]
    public void ExplicitNonCleanupExportRejectsADanglingReparsePlacementTarget()
    {
        using var temp = TempDirectory.Create();
        var workingDirectory = temp.CreateDirectory("working");
        var explicitWorkbook = Path.Combine(workingDirectory, "explicit.xlsm");
        File.WriteAllText(explicitWorkbook, "workbook", Encoding.UTF8);
        var missingExternalTarget = Path.Combine(temp.Path, "missing", "External.bas");
        var linkedTarget = Path.Combine(workingDirectory, "Module1.bas");
        File.CreateSymbolicLink(linkedTarget, missingExternalTarget);
        var exporter = new FakeWorkbookModuleExporter(("Module1.bas", "new module"));
        var application = CommandLineTestFactory.Create(
            workingDirectory,
            workbookModuleExporter: exporter);

        var result = application.Run(["export", "--from", explicitWorkbook]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Export destination contains a reparse point", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.GetFullPath(linkedTarget), result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(missingExternalTarget));
        Assert.True(File.ResolveLinkTarget(linkedTarget, returnFinalTarget: false) is not null);
    }

    [Fact]
    public void ExplicitNonCleanupExportReportsAppliedOverlayWhenRecoveryCleanupFails()
    {
        using var temp = TempDirectory.Create();
        var explicitWorkbook = Path.Combine(temp.Path, "explicit.xlsm");
        File.WriteAllText(explicitWorkbook, "workbook", Encoding.UTF8);
        File.WriteAllText(Path.Combine(temp.Path, "Old.bas"), "stale module", Encoding.UTF8);
        File.WriteAllText(Path.Combine(temp.Path, "Module1.bas"), "old module", Encoding.UTF8);
        var exporter = new FakeWorkbookModuleExporter(("Module1.bas", "new module"));
        var application = CommandLineTestFactory.Create(
            temp.Path,
            workbookModuleExporter: exporter,
            exportDestinationFileOperations: new PostApplyRecoveryCleanupFailureExportDestinationFileOperations());

        var result = application.Run(["export", "--from", explicitWorkbook]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("requested source overlay remains applied", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exact source snapshot", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("new module", File.ReadAllText(Path.Combine(temp.Path, "Module1.bas"), Encoding.UTF8));
        Assert.Equal("stale module", File.ReadAllText(Path.Combine(temp.Path, "Old.bas"), Encoding.UTF8));
    }

    [Fact]
    public void ExplicitWorkbookExportSupportsLegacySynchronousExporter()
    {
        using var temp = TempDirectory.Create();
        var explicitWorkbook = Path.Combine(temp.Path, "explicit.xlsm");
        File.WriteAllText(explicitWorkbook, "workbook", Encoding.UTF8);
        var exporter = new LegacyWorkbookModuleExporter();
        var application = CommandLineTestFactory.Create(temp.Path, workbookModuleExporter: exporter);

        var result = application.Run(["export", "--from", explicitWorkbook]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(explicitWorkbook, Assert.Single(exporter.Calls).WorkbookPath);
        Assert.Equal("legacy", File.ReadAllText(Path.Combine(temp.Path, "Legacy.bas"), Encoding.UTF8));
    }

    [Fact]
    public void ExportCommandRetainsSynchronousExplicitEntryPoint()
    {
        using var temp = TempDirectory.Create();
        var explicitWorkbook = Path.Combine(temp.Path, "explicit.xlsm");
        File.WriteAllText(explicitWorkbook, "workbook", Encoding.UTF8);
        var command = new ExportCommand(
            new FakeWorkbookModuleExporter(("Module1.bas", "new module")));

        var result = command.RunExplicit(new ExportCommandRequest(
            FromPath: explicitWorkbook,
            ToPath: null,
            WorkingDirectory: temp.Path));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("new module", File.ReadAllText(Path.Combine(temp.Path, "Module1.bas"), Encoding.UTF8));
    }

    [Fact]
    public async Task ExplicitWorkbookExportDoesNotRequireProjectContext()
    {
        using var temp = TempDirectory.Create();
        var explicitWorkbook = Path.Combine(temp.Path, "explicit.xlsm");
        File.WriteAllText(explicitWorkbook, "workbook", Encoding.UTF8);
        var explicitDestination = temp.CreateDirectory("explicit-export");
        var exporter = new FakeWorkbookModuleExporter(("Module1.bas", "new"));
        var application = VbaDevCommandLine.Create(
            ToolingCompositionRoot.CreateApplicationComposition(
                temp.Path,
                workbookModuleExporter: exporter));

        var result = await application.RunAsync(
            ["export", "--from", explicitWorkbook, "--to", explicitDestination]);

        Assert.Equal(0, result.ExitCode);
        var call = Assert.Single(exporter.Calls);
        Assert.Equal(explicitWorkbook, call.WorkbookPath);
        Assert.NotEqual(explicitDestination, call.DestinationDirectory);
        Assert.Equal("new", File.ReadAllText(Path.Combine(explicitDestination, "Module1.bas"), Encoding.UTF8));
    }

    [Theory]
    [InlineData("--project")]
    [InlineData("--document")]
    public void ExplicitWorkbookExportRejectsProjectContextOptions(string optionName)
    {
        using var temp = TempDirectory.Create();
        var explicitWorkbook = Path.Combine(temp.Path, "explicit.xlsm");
        File.WriteAllText(explicitWorkbook, "workbook", Encoding.UTF8);
        var exporter = new FakeWorkbookModuleExporter(("Module1.bas", "new"));
        var application = CommandLineTestFactory.Create(temp.Path, workbookModuleExporter: exporter);

        var optionValue = optionName.Equals("--project", StringComparison.Ordinal)
            ? temp.Path
            : "Book1";
        var result = application.Run(["export", "--from", explicitWorkbook, optionName, optionValue]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"{optionName} cannot be used with --from.", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(exporter.Calls);
    }

    [Fact]
    public void ExportToOptionCleansSpecifiedDirectory()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateDocumentSourceSet(root, "Book1");
        var binPath = CreateWorkbook(root, "bin", "Book1");
        var explicitDestination = temp.CreateDirectory("explicit-export");
        File.WriteAllText(Path.Combine(explicitDestination, "Old.bas"), "old", Encoding.UTF8);
        WriteText(Path.Combine(explicitDestination, "nested", "Old.frm"), "old");
        WriteBytes(Path.Combine(explicitDestination, "nested", "Old.frx"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(explicitDestination, "notes.txt"), "keep", Encoding.UTF8);
        var exporter = new FakeWorkbookModuleExporter(("Module1.bas", "new"));
        var application = CommandLineTestFactory.Create(root, workbookModuleExporter: exporter);

        var result = application.Run(["export", "--to", explicitDestination]);

        Assert.Equal(0, result.ExitCode);
        var call = Assert.Single(exporter.Calls);
        Assert.Equal(binPath, call.WorkbookPath);
        Assert.NotEqual(explicitDestination, call.DestinationDirectory);
        Assert.False(File.Exists(Path.Combine(explicitDestination, "Old.bas")));
        Assert.False(File.Exists(Path.Combine(explicitDestination, "nested", "Old.frm")));
        Assert.False(File.Exists(Path.Combine(explicitDestination, "nested", "Old.frx")));
        Assert.True(Directory.Exists(Path.Combine(explicitDestination, "nested")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(explicitDestination, "notes.txt"), Encoding.UTF8));
        Assert.Equal("new", File.ReadAllText(Path.Combine(explicitDestination, "Module1.bas"), Encoding.UTF8));
    }

    [Fact]
    public void CleanupEnabledExportLeavesDestinationUntouchedWhenExporterFails()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = CreateDocumentSourceSet(root, "Book1");
        var binPath = CreateWorkbook(root, "bin", "Book1");
        WriteText(Path.Combine(sourceSet, "modules", "Old.bas"), "old");
        WriteBytes(Path.Combine(sourceSet, "forms", "Old.frx"), [1, 2, 3]);
        var exporter = new FakeWorkbookModuleExporter(("Module1.bas", "new"))
        {
            ThrowOnExport = true
        };
        var application = CommandLineTestFactory.Create(root, workbookModuleExporter: exporter);

        var result = application.Run(["export"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("export failed", result.StandardError, StringComparison.Ordinal);
        var call = Assert.Single(exporter.Calls);
        Assert.Equal(binPath, call.WorkbookPath);
        Assert.NotEqual(sourceSet, call.DestinationDirectory);
        Assert.Equal("old", File.ReadAllText(Path.Combine(sourceSet, "modules", "Old.bas"), Encoding.UTF8));
        Assert.True(File.Exists(Path.Combine(sourceSet, "forms", "Old.frx")));
        Assert.False(File.Exists(Path.Combine(sourceSet, "Module1.bas")));
    }

    [Fact]
    public async Task CleanupEnabledExportReportsOwnedAutomationTimeoutWithoutMutatingDestination()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = CreateDocumentSourceSet(root, "Book1");
        CreateWorkbook(root, "bin", "Book1");
        WriteText(Path.Combine(sourceSet, "modules", "Old.bas"), "old");
        var exporter = new FakeWorkbookModuleExporter
        {
            ExportError = new WorkbookAutomationTimeoutException(
                new WorkbookAutomationStage(WorkbookAutomationStageKind.ModuleExport, "Module1"),
                TimeSpan.FromSeconds(30))
        };
        var application = CommandLineTestFactory.Create(root, workbookModuleExporter: exporter);

        var result = await application.RunAsync(["export"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("timed out during module export 'Module1'", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Unhandled exception", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("old", File.ReadAllText(Path.Combine(sourceSet, "modules", "Old.bas"), Encoding.UTF8));
        Assert.Empty(Directory.EnumerateDirectories(
            sourceSet,
            ".vba-dev-export-recovery-*",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task CleanupEnabledExportReportsOwnedProcessLossWithoutMutatingDestination()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = CreateDocumentSourceSet(root, "Book1");
        CreateWorkbook(root, "bin", "Book1");
        WriteText(Path.Combine(sourceSet, "modules", "Old.bas"), "old");
        var exporter = new FakeWorkbookModuleExporter
        {
            ExportError = new WorkbookAutomationProcessLostException(
                new WorkbookAutomationStage(WorkbookAutomationStageKind.ModuleExport, "Module1"))
        };
        var application = CommandLineTestFactory.Create(root, workbookModuleExporter: exporter);

        var result = await application.RunAsync(["export"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("owned Excel process exited during module export 'Module1'", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Unhandled exception", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("old", File.ReadAllText(Path.Combine(sourceSet, "modules", "Old.bas"), Encoding.UTF8));
        Assert.Empty(Directory.EnumerateDirectories(
            sourceSet,
            ".vba-dev-export-recovery-*",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task CleanupEnabledExportReportsUnprovenOwnedProcessCleanupWithoutMutatingDestination()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = CreateDocumentSourceSet(root, "Book1");
        CreateWorkbook(root, "bin", "Book1");
        WriteText(Path.Combine(sourceSet, "modules", "Old.bas"), "old");
        var exporter = new FakeWorkbookModuleExporter
        {
            ExportError = new WorkbookAutomationCleanupException(
                "Could not prove release of the owned Excel process.")
        };
        var application = CommandLineTestFactory.Create(root, workbookModuleExporter: exporter);

        var result = await application.RunAsync(["export"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Could not prove release of the owned Excel process", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("old", File.ReadAllText(Path.Combine(sourceSet, "modules", "Old.bas"), Encoding.UTF8));
        Assert.Empty(Directory.EnumerateDirectories(
            sourceSet,
            ".vba-dev-export-recovery-*",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task CleanupEnabledExportReportsReleasedProcessCleanupWithoutMutatingDestination()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = CreateDocumentSourceSet(root, "Book1");
        CreateWorkbook(root, "bin", "Book1");
        WriteText(Path.Combine(sourceSet, "modules", "Old.bas"), "old");
        var exporter = new FakeWorkbookModuleExporter
        {
            ExportError = new WorkbookAutomationReleasedProcessCleanupException(
                "Owned Excel exited, but secondary cleanup failed.")
        };
        var application = CommandLineTestFactory.Create(root, workbookModuleExporter: exporter);

        var result = await application.RunAsync(["export"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Owned Excel exited, but secondary cleanup failed", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("old", File.ReadAllText(Path.Combine(sourceSet, "modules", "Old.bas"), Encoding.UTF8));
        Assert.Empty(Directory.EnumerateDirectories(
            sourceSet,
            ".vba-dev-export-recovery-*",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task CleanupEnabledExportCancellationDuringStagingReturns130AndLeavesDestinationUntouched()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = CreateDocumentSourceSet(root, "Book1");
        CreateWorkbook(root, "bin", "Book1");
        WriteText(Path.Combine(sourceSet, "modules", "Old.bas"), "old");
        using var cancellation = new CancellationTokenSource();
        var exporter = new FakeWorkbookModuleExporter(("Module1.bas", "new"))
        {
            OnExport = _ => cancellation.Cancel()
        };
        var application = CommandLineTestFactory.Create(root, workbookModuleExporter: exporter);

        var result = await application.RunAsync(["export"], cancellation.Token);

        Assert.Equal(130, result.ExitCode);
        Assert.Equal("old", File.ReadAllText(Path.Combine(sourceSet, "modules", "Old.bas"), Encoding.UTF8));
        Assert.False(File.Exists(Path.Combine(sourceSet, "Module1.bas")));
        Assert.Empty(Directory.EnumerateDirectories(
            sourceSet,
            ".vba-dev-export-recovery-*",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void CleanupEnabledExportValidatesTheCompletePlanBeforeDestinationMutation()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = CreateDocumentSourceSet(root, "Book1");
        CreateWorkbook(root, "bin", "Book1");
        WriteBytes(Path.Combine(sourceSet, "modules", "Old.bas"), [0xff, 0x00, 0x81]);
        File.WriteAllText(Path.Combine(sourceSet, "notes.txt"), "keep", Encoding.UTF8);
        var exporter = new FakeWorkbookModuleExporter(
            ("Module1.bas", "new module"),
            ("orphan/Dialog.frx", "orphan sidecar"));
        var application = CommandLineTestFactory.Create(root, workbookModuleExporter: exporter);

        var result = application.Run(["export"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("sidecar without a same-directory form", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([0xff, 0x00, 0x81], File.ReadAllBytes(Path.Combine(sourceSet, "modules", "Old.bas")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(sourceSet, "notes.txt"), Encoding.UTF8));
        Assert.False(File.Exists(Path.Combine(sourceSet, "Module1.bas")));
        Assert.Empty(Directory.EnumerateDirectories(
            sourceSet,
            ".vba-dev-export-recovery-*",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void CleanupEnabledExportRejectsDestinationReparsePointsBeforeMutation()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = CreateDocumentSourceSet(root, "Book1");
        CreateWorkbook(root, "bin", "Book1");
        WriteText(Path.Combine(sourceSet, "Local.bas"), "old local");
        var externalDirectory = temp.CreateDirectory("outside-source-set");
        var externalSourcePath = Path.Combine(externalDirectory, "External.bas");
        WriteText(externalSourcePath, "external source");
        var linkedDirectory = Path.Combine(sourceSet, "linked");
        Directory.CreateSymbolicLink(linkedDirectory, externalDirectory);
        var exporter = new FakeWorkbookModuleExporter(("Module1.bas", "new module"));
        var application = CommandLineTestFactory.Create(root, workbookModuleExporter: exporter);

        var result = application.Run(["export"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("reparse point", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.GetFullPath(linkedDirectory), result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("old local", File.ReadAllText(Path.Combine(sourceSet, "Local.bas"), Encoding.UTF8));
        Assert.Equal("external source", File.ReadAllText(externalSourcePath, Encoding.UTF8));
        Assert.False(File.Exists(Path.Combine(sourceSet, "Module1.bas")));
        Assert.Empty(Directory.EnumerateDirectories(
            sourceSet,
            ".vba-dev-export-recovery-*",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void CleanupEnabledExportReportsPartialProtectionWithoutUnsafeRestoreInstructions()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = CreateDocumentSourceSet(root, "Book1");
        CreateWorkbook(root, "bin", "Book1");
        WriteText(Path.Combine(sourceSet, "a", "Alpha.bas"), "old alpha");
        WriteText(Path.Combine(sourceSet, "z", "Zulu.cls"), "old zulu");
        var exporter = new FakeWorkbookModuleExporter(("Module1.bas", "new module"));
        var fileOperations = new ProtectionFailureExportDestinationFileOperations("Zulu.cls");
        var application = CommandLineTestFactory.Create(
            root,
            workbookModuleExporter: exporter,
            exportDestinationFileOperations: fileOperations);

        var result = application.Run(["export"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("protection failed before mutation", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no destination source or sidecar file was changed", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("incomplete protection data", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rollback was incomplete", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("copy the recovery area contents back", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Manually remove affected", result.StandardError, StringComparison.Ordinal);
        Assert.Equal("old alpha", File.ReadAllText(Path.Combine(sourceSet, "a", "Alpha.bas"), Encoding.UTF8));
        Assert.Equal("old zulu", File.ReadAllText(Path.Combine(sourceSet, "z", "Zulu.cls"), Encoding.UTF8));
        Assert.False(File.Exists(Path.Combine(sourceSet, "Module1.bas")));
        var recoveryDirectory = Assert.Single(Directory.EnumerateDirectories(
            sourceSet,
            ".vba-dev-export-recovery-*",
            SearchOption.TopDirectoryOnly));
        Assert.Contains(Path.GetFullPath(recoveryDirectory), result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "old alpha",
            File.ReadAllText(Path.Combine(recoveryDirectory, "a", "Alpha.bas"), Encoding.UTF8));
        var missingProtectionPath = Path.Combine(recoveryDirectory, "z", "Zulu.cls");
        Assert.False(File.Exists(missingProtectionPath));
        Assert.DoesNotContain(
            Path.GetFullPath(missingProtectionPath),
            result.StandardError,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CleanupEnabledExportOmitsRemovedProtectionPathsAfterSuccessfulCleanup()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = CreateDocumentSourceSet(root, "Book1");
        CreateWorkbook(root, "bin", "Book1");
        WriteText(Path.Combine(sourceSet, "Zulu.cls"), "old zulu");
        var exporter = new FakeWorkbookModuleExporter(("Module1.bas", "new module"));
        var fileOperations = new ProtectionFailureExportDestinationFileOperations(
            "Zulu.cls",
            failRecoveryCleanup: false);
        var application = CommandLineTestFactory.Create(
            root,
            workbookModuleExporter: exporter,
            exportDestinationFileOperations: fileOperations);

        var result = application.Run(["export"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("protection failed before mutation", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no destination source or sidecar file was changed", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".vba-dev-export-recovery-", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("copy the recovery area contents back", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("old zulu", File.ReadAllText(Path.Combine(sourceSet, "Zulu.cls"), Encoding.UTF8));
        Assert.False(File.Exists(Path.Combine(sourceSet, "Module1.bas")));
        Assert.Empty(Directory.EnumerateDirectories(
            sourceSet,
            ".vba-dev-export-recovery-*",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void CleanupEnabledExportRequiresNeutralInspectionOfRetainedExportData()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = CreateDocumentSourceSet(root, "Book1");
        CreateWorkbook(root, "bin", "Book1");
        WriteText(Path.Combine(sourceSet, "Module1.bas"), "old module");
        var retainedDirectory = Directory.CreateDirectory(
            Path.Combine(sourceSet, ".vba-dev-export-recovery-partial")).FullName;
        WriteText(Path.Combine(retainedDirectory, "OnlyOne.bas"), "partial protection data");
        var exporter = new FakeWorkbookModuleExporter(("Module1.bas", "new module"));
        var application = CommandLineTestFactory.Create(root, workbookModuleExporter: exporter);

        var result = application.Run(["export"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("retained export recovery or protection data", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("inspection", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.GetFullPath(retainedDirectory), result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("requires manual recovery", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("copy the recovery area contents back", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("old module", File.ReadAllText(Path.Combine(sourceSet, "Module1.bas"), Encoding.UTF8));
        Assert.False(File.Exists(Path.Combine(sourceSet, "OnlyOne.bas")));
    }

    [Fact]
    public void CleanupEnabledExportReportsOnlyTheRetainedEmptyDestinationAfterSetupFailure()
    {
        using var temp = TempDirectory.Create();
        var workbookPath = Path.Combine(temp.Path, "Book1.xlsm");
        var destinationPath = Path.Combine(temp.Path, "exported-source");
        File.WriteAllText(workbookPath, "workbook", Encoding.UTF8);
        var exporter = new FakeWorkbookModuleExporter(("Module1.bas", "new module"));
        var fileOperations = new DestinationSetupFailureExportDestinationFileOperations(destinationPath);
        var application = CommandLineTestFactory.Create(
            temp.Path,
            workbookModuleExporter: exporter,
            exportDestinationFileOperations: fileOperations);

        var result = application.Run([
            "export",
            "--from", workbookPath,
            "--to", destinationPath
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("protection failed before mutation", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no destination source or sidecar file was changed", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            $"Empty destination directory remains at '{Path.GetFullPath(destinationPath)}'",
            result.StandardError,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".vba-dev-export-recovery-", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("copy the recovery area contents back", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(destinationPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(destinationPath));
    }

    [Fact]
    public void CleanupEnabledExportRestoresPriorDestinationWhenApplyFails()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = CreateDocumentSourceSet(root, "Book1");
        CreateWorkbook(root, "bin", "Book1");
        WriteText(Path.Combine(sourceSet, "modules", "Module1.bas"), "old module");
        WriteText(Path.Combine(sourceSet, "old", "Old.cls"), "old class");
        WriteText(Path.Combine(sourceSet, "forms", "Dialog.frm"), "old form");
        WriteBytes(Path.Combine(sourceSet, "forms", "Dialog.frx"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(sourceSet, "notes.txt"), "keep", Encoding.UTF8);
        var exporter = new FakeWorkbookModuleExporter(
            ("Module1.bas", "new module"),
            ("Dialog.frm", "new form"),
            ("Dialog.frx", "new sidecar"),
            ("NewModule.cls", "new class"));
        var fileOperations = new FaultInjectingExportDestinationFileOperations("Module1.bas");
        var application = CommandLineTestFactory.Create(
            root,
            workbookModuleExporter: exporter,
            exportDestinationFileOperations: fileOperations);

        var result = application.Run(["export"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("prior destination was restored", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("old module", File.ReadAllText(Path.Combine(sourceSet, "modules", "Module1.bas"), Encoding.UTF8));
        Assert.Equal("old class", File.ReadAllText(Path.Combine(sourceSet, "old", "Old.cls"), Encoding.UTF8));
        Assert.Equal("old form", File.ReadAllText(Path.Combine(sourceSet, "forms", "Dialog.frm"), Encoding.UTF8));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(sourceSet, "forms", "Dialog.frx")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(sourceSet, "notes.txt"), Encoding.UTF8));
        Assert.True(File.Exists(Path.Combine(sourceSet, "Book1.xlsm")));
        Assert.False(File.Exists(Path.Combine(sourceSet, "NewModule.cls")));
        Assert.Empty(Directory.EnumerateDirectories(
            sourceSet,
            ".vba-dev-export-recovery-*",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void CleanupEnabledExportRestoresAbsentDestinationWhenApplyFails()
    {
        using var temp = TempDirectory.Create();
        var workbookPath = Path.Combine(temp.Path, "Book1.xlsm");
        var destinationPath = Path.Combine(temp.Path, "exported-source");
        File.WriteAllText(workbookPath, "workbook", Encoding.UTF8);
        var exporter = new FakeWorkbookModuleExporter(("Module1.bas", "new module"));
        var fileOperations = new FaultInjectingExportDestinationFileOperations("Module1.bas");
        var application = CommandLineTestFactory.Create(
            temp.Path,
            workbookModuleExporter: exporter,
            exportDestinationFileOperations: fileOperations);

        var result = application.Run([
            "export",
            "--from", workbookPath,
            "--to", destinationPath
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("prior destination was restored", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(destinationPath));
    }

    [Fact]
    public void CleanupEnabledExportReportsOnlyRetainedDirectoryAfterRollbackCleanupFails()
    {
        using var temp = TempDirectory.Create();
        var workbookPath = Path.Combine(temp.Path, "Book1.xlsm");
        var destinationPath = Path.Combine(temp.Path, "exported-source");
        File.WriteAllText(workbookPath, "workbook", Encoding.UTF8);
        var exporter = new FakeWorkbookModuleExporter(("Module1.bas", "new module"));
        var fileOperations = new RollbackCleanupFailureExportDestinationFileOperations(
            "Module1.bas",
            destinationPath);
        var application = CommandLineTestFactory.Create(
            temp.Path,
            workbookModuleExporter: exporter,
            exportDestinationFileOperations: fileOperations);

        var result = application.Run([
            "export",
            "--from", workbookPath,
            "--to", destinationPath
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("prior destination source state was restored", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cleanup was incomplete", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.GetFullPath(destinationPath), result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".vba-dev-export-recovery-", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("copy the recovery area contents back", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Manually remove affected", result.StandardError, StringComparison.Ordinal);
        Assert.True(Directory.Exists(destinationPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(destinationPath));
    }

    [Fact]
    public void CleanupEnabledExportKeepsRestoredSourceWhenRecoveryCleanupPartiallyFails()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = CreateDocumentSourceSet(root, "Book1");
        CreateWorkbook(root, "bin", "Book1");
        WriteText(Path.Combine(sourceSet, "modules", "Module1.bas"), "old module");
        WriteText(Path.Combine(sourceSet, "old", "Old.cls"), "old class");
        var exporter = new FakeWorkbookModuleExporter(
            ("Module1.bas", "new module"),
            ("NewModule.cls", "new class"));
        var fileOperations = new RollbackRecoveryCleanupFailureExportDestinationFileOperations(
            "Module1.bas");
        var application = CommandLineTestFactory.Create(
            root,
            workbookModuleExporter: exporter,
            exportDestinationFileOperations: fileOperations);

        var result = application.Run(["export"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("prior destination source state was restored", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cleanup was incomplete", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rollback was incomplete", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("copy the recovery area contents back", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Manually remove affected", result.StandardError, StringComparison.Ordinal);
        Assert.Equal("old module", File.ReadAllText(Path.Combine(sourceSet, "modules", "Module1.bas"), Encoding.UTF8));
        Assert.Equal("old class", File.ReadAllText(Path.Combine(sourceSet, "old", "Old.cls"), Encoding.UTF8));
        Assert.False(File.Exists(Path.Combine(sourceSet, "NewModule.cls")));
        var recoveryDirectory = Assert.Single(Directory.EnumerateDirectories(
            sourceSet,
            ".vba-dev-export-recovery-*",
            SearchOption.TopDirectoryOnly));
        Assert.Contains(Path.GetFullPath(recoveryDirectory), result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(recoveryDirectory, "modules", "Module1.bas")));
        Assert.Equal(
            "old class",
            File.ReadAllText(Path.Combine(recoveryDirectory, "old", "Old.cls"), Encoding.UTF8));
    }

    [Fact]
    public void CleanupEnabledExportRetainsRecoveryAndInstructionsWhenRollbackIsIncomplete()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = CreateDocumentSourceSet(root, "Book1");
        CreateWorkbook(root, "bin", "Book1");
        WriteText(Path.Combine(sourceSet, "modules", "Module1.bas"), "old module");
        File.WriteAllText(Path.Combine(sourceSet, "notes.txt"), "keep", Encoding.UTF8);
        var exporter = new FakeWorkbookModuleExporter(("Module1.bas", "new module"));
        var fileOperations = new FaultInjectingExportDestinationFileOperations(
            failedPlacementFileName: "Module1.bas",
            failedRestoreFileName: "Module1.bas");
        var application = CommandLineTestFactory.Create(
            root,
            workbookModuleExporter: exporter,
            exportDestinationFileOperations: fileOperations);

        var result = application.Run(["export"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("rollback was incomplete", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Manually remove affected VBA source and sidecar files", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("copy the recovery area contents back", result.StandardError, StringComparison.Ordinal);
        var recoveryDirectory = Assert.Single(Directory.EnumerateDirectories(
            sourceSet,
            ".vba-dev-export-recovery-*",
            SearchOption.TopDirectoryOnly));
        Assert.Contains(Path.GetFullPath(recoveryDirectory), result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "old module",
            File.ReadAllText(Path.Combine(recoveryDirectory, "modules", "Module1.bas"), Encoding.UTF8));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(sourceSet, "notes.txt"), Encoding.UTF8));
        Assert.True(File.Exists(Path.Combine(sourceSet, "Book1.xlsm")));
    }

    [Fact]
    public void ExportToOptionStillRequiresProjectContext()
    {
        using var temp = TempDirectory.Create();
        var explicitDestination = temp.CreateDirectory("explicit-export");
        var exporter = new FakeWorkbookModuleExporter(("Module1.bas", "new"));
        var application = CommandLineTestFactory.Create(temp.Path, workbookModuleExporter: exporter);

        var result = application.Run(["export", "--to", explicitDestination]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Project manifest was not found", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(exporter.Calls);
    }

    [Fact]
    public void ExplicitWorkbookExportRejectsDestinationFile()
    {
        using var temp = TempDirectory.Create();
        var explicitWorkbook = Path.Combine(temp.Path, "explicit.xlsm");
        var destinationFile = Path.Combine(temp.Path, "Module1.bas");
        File.WriteAllText(explicitWorkbook, "workbook", Encoding.UTF8);
        File.WriteAllText(destinationFile, "old", Encoding.UTF8);
        var exporter = new FakeWorkbookModuleExporter(("Module1.bas", "new"));
        var application = CommandLineTestFactory.Create(temp.Path, workbookModuleExporter: exporter);

        var result = application.Run(["export", "--from", explicitWorkbook, "--to", destinationFile]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"Export destination is not a directory: {destinationFile}", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(exporter.Calls);
    }

    [Fact]
    public void ExplicitWorkbookExportRejectsBlankFromPath()
    {
        using var temp = TempDirectory.Create();
        var exporter = new FakeWorkbookModuleExporter(("Module1.bas", "new"));
        var application = CommandLineTestFactory.Create(temp.Path, workbookModuleExporter: exporter);

        var result = application.Run(["export", "--from="]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--from", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(exporter.Calls);
    }

    [Fact]
    public void ExportOptionsResolveRelativePathsFromWorkingDirectory()
    {
        using var temp = TempDirectory.Create();
        var workingDirectory = temp.CreateDirectory("work");
        var relativeWorkbook = Path.Combine(workingDirectory, "relative.xlsm");
        File.WriteAllText(relativeWorkbook, "workbook", Encoding.UTF8);
        var exporter = new FakeWorkbookModuleExporter(("Module1.bas", "new"));
        var application = CommandLineTestFactory.Create(workingDirectory, workbookModuleExporter: exporter);

        var result = application.Run(["export", "--from", "relative.xlsm", "--to", "out"]);

        Assert.Equal(0, result.ExitCode);
        var call = Assert.Single(exporter.Calls);
        Assert.Equal(relativeWorkbook, call.WorkbookPath);
        Assert.NotEqual(Path.Combine(workingDirectory, "out"), call.DestinationDirectory);
        Assert.Equal("new", File.ReadAllText(Path.Combine(workingDirectory, "out", "Module1.bas"), Encoding.UTF8));
    }

    private static string CreateDocumentSourceSet(string root, string documentName, params (string FileName, string Content)[] files)
    {
        var sourceSet = Path.Combine(root, "src", documentName);
        Directory.CreateDirectory(sourceSet);
        File.WriteAllText(Path.Combine(sourceSet, $"{documentName}.xlsm"), $"template:{documentName}", Encoding.UTF8);
        foreach (var file in files)
        {
            var filePath = Path.Combine(sourceSet, file.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, file.Content, Encoding.UTF8);
        }

        return sourceSet;
    }

    private static string CreateWorkbook(string root, string outputDirectory, string documentName)
    {
        var workbookPath = Path.Combine(root, outputDirectory, $"{documentName}.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(workbookPath)!);
        File.WriteAllText(workbookPath, $"workbook:{documentName}", Encoding.UTF8);
        return workbookPath;
    }

    private static void WriteText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, Encoding.UTF8);
    }

    private static void WriteBytes(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }
}

internal sealed class FakeWorkbookModuleExporter : IWorkbookModuleExporter
{
    private readonly IReadOnlyList<(string FileName, string Content)> exports;

    public FakeWorkbookModuleExporter(params (string FileName, string Content)[] exports)
    {
        this.exports = exports;
    }

    public List<(string WorkbookPath, string DestinationDirectory)> Calls { get; } = [];

    public List<WorkbookAutomationTimeouts> AutomationTimeouts { get; } = [];

    public bool ThrowOnExport { get; init; }

    public Exception? ExportError { get; init; }

    public Action<CancellationToken>? OnExport { get; init; }

    public void ExportModules(string workbookPath, string destinationDirectory)
        => ExportModulesAsync(workbookPath, destinationDirectory, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    public Task ExportModulesAsync(
        string workbookPath,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        Calls.Add((workbookPath, destinationDirectory));
        OnExport?.Invoke(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (ExportError is not null)
        {
            throw ExportError;
        }
        if (ThrowOnExport)
        {
            throw new InvalidOperationException("export failed");
        }

        Directory.CreateDirectory(destinationDirectory);
        foreach (var export in exports)
        {
            var exportPath = Path.Combine(destinationDirectory, export.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(exportPath)!);
            File.WriteAllText(exportPath, export.Content, Encoding.UTF8);
        }

        return Task.CompletedTask;
    }

    public Task ExportModulesAsync(
        string workbookPath,
        string destinationDirectory,
        WorkbookAutomationTimeouts automationTimeouts,
        CancellationToken cancellationToken)
    {
        AutomationTimeouts.Add(automationTimeouts);
        return ExportModulesAsync(workbookPath, destinationDirectory, cancellationToken);
    }
}

internal sealed class LegacyWorkbookModuleExporter : IWorkbookModuleExporter
{
    public List<(string WorkbookPath, string DestinationDirectory)> Calls { get; } = [];

    public void ExportModules(string workbookPath, string destinationDirectory)
    {
        Calls.Add((workbookPath, destinationDirectory));
        Directory.CreateDirectory(destinationDirectory);
        File.WriteAllText(Path.Combine(destinationDirectory, "Legacy.bas"), "legacy", Encoding.UTF8);
    }
}

internal sealed class ExistingFileRejectingWorkbookModuleExporter(
    params (string FileName, string Content)[] exports)
    : IWorkbookModuleExporter
{
    public List<(string WorkbookPath, string DestinationDirectory)> Calls { get; } = [];

    public void ExportModules(string workbookPath, string destinationDirectory)
    {
        Calls.Add((workbookPath, destinationDirectory));
        Directory.CreateDirectory(destinationDirectory);
        foreach (var export in exports)
        {
            var exportPath = Path.Combine(destinationDirectory, export.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(exportPath)!);
            using var stream = new FileStream(exportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(export.Content);
        }
    }
}

internal sealed class FaultInjectingExportDestinationFileOperations(
    string failedPlacementFileName,
    string? failedRestoreFileName = null)
    : IExportDestinationFileOperations
{
    private bool placementFailed;
    private bool restoreFailed;

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void CopyFile(string sourcePath, string destinationPath, bool overwrite)
    {
        if (
            !placementFailed
            && Path.GetFileName(sourcePath).Equals(failedPlacementFileName, StringComparison.OrdinalIgnoreCase)
            && sourcePath.Contains("vba-dev-export-", StringComparison.OrdinalIgnoreCase)
            && !sourcePath.Contains("recovery", StringComparison.OrdinalIgnoreCase)
        )
        {
            placementFailed = true;
            throw new IOException("Injected export apply failure.");
        }

        if (
            !restoreFailed
            && failedRestoreFileName is not null
            && Path.GetFileName(sourcePath).Equals(failedRestoreFileName, StringComparison.OrdinalIgnoreCase)
            && sourcePath.Contains("recovery", StringComparison.OrdinalIgnoreCase)
        )
        {
            restoreFailed = true;
            throw new IOException("Injected export rollback failure.");
        }

        File.Copy(sourcePath, destinationPath, overwrite);
    }

    public void DeleteFile(string path) => File.Delete(path);

    public void DeleteDirectory(string path, bool recursive) => Directory.Delete(path, recursive);
}

internal sealed class ProtectionFailureExportDestinationFileOperations(
    string failedProtectionFileName,
    bool failRecoveryCleanup = true)
    : IExportDestinationFileOperations
{
    private bool protectionFailed;

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void CopyFile(string sourcePath, string destinationPath, bool overwrite)
    {
        if (
            !protectionFailed
            && Path.GetFileName(sourcePath).Equals(failedProtectionFileName, StringComparison.OrdinalIgnoreCase)
            && destinationPath.Contains(".vba-dev-export-recovery-", StringComparison.OrdinalIgnoreCase)
        )
        {
            protectionFailed = true;
            throw new IOException($"Injected export protection failure at '{destinationPath}'.");
        }

        File.Copy(sourcePath, destinationPath, overwrite);
    }

    public void DeleteFile(string path) => File.Delete(path);

    public void DeleteDirectory(string path, bool recursive)
    {
        if (
            failRecoveryCleanup
            && path.Contains(".vba-dev-export-recovery-", StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new IOException("Injected protection cleanup failure.");
        }

        Directory.Delete(path, recursive);
    }
}

internal sealed class DestinationSetupFailureExportDestinationFileOperations(string destinationPath)
    : IExportDestinationFileOperations
{
    private readonly string destination = Path.GetFullPath(destinationPath);
    private bool setupFailed;

    public void CreateDirectory(string path)
    {
        if (!setupFailed && SamePath(path, destination))
        {
            setupFailed = true;
            Directory.CreateDirectory(path);
            throw new IOException("Injected destination setup failure.");
        }

        Directory.CreateDirectory(path);
    }

    public void CopyFile(string sourcePath, string destinationPath, bool overwrite)
        => File.Copy(sourcePath, destinationPath, overwrite);

    public void DeleteFile(string path) => File.Delete(path);

    public void DeleteDirectory(string path, bool recursive)
    {
        if (SamePath(path, destination))
        {
            throw new IOException("Injected empty destination cleanup failure.");
        }

        Directory.Delete(path, recursive);
    }

    private static bool SamePath(string left, string right)
        => Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}

internal sealed class RollbackCleanupFailureExportDestinationFileOperations(
    string failedPlacementFileName,
    string destinationPath)
    : IExportDestinationFileOperations
{
    private readonly string destination = Path.GetFullPath(destinationPath);
    private bool placementFailed;

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void CopyFile(string sourcePath, string destinationPath, bool overwrite)
    {
        if (
            !placementFailed
            && Path.GetFileName(sourcePath).Equals(failedPlacementFileName, StringComparison.OrdinalIgnoreCase)
            && sourcePath.Contains("vba-dev-export-", StringComparison.OrdinalIgnoreCase)
            && !sourcePath.Contains("recovery", StringComparison.OrdinalIgnoreCase)
        )
        {
            placementFailed = true;
            throw new IOException("Injected export apply failure.");
        }

        File.Copy(sourcePath, destinationPath, overwrite);
    }

    public void DeleteFile(string path) => File.Delete(path);

    public void DeleteDirectory(string path, bool recursive)
    {
        if (SamePath(path, destination))
        {
            throw new IOException("Injected rollback directory cleanup failure.");
        }

        Directory.Delete(path, recursive);
    }

    private static bool SamePath(string left, string right)
        => Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}

internal sealed class PostApplyRecoveryCleanupFailureExportDestinationFileOperations
    : IExportDestinationFileOperations
{
    private bool cleanupFailed;

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void CopyFile(string sourcePath, string destinationPath, bool overwrite)
        => File.Copy(sourcePath, destinationPath, overwrite);

    public void DeleteFile(string path) => File.Delete(path);

    public void DeleteDirectory(string path, bool recursive)
    {
        if (
            !cleanupFailed
            && path.Contains(".vba-dev-export-recovery-", StringComparison.OrdinalIgnoreCase)
        )
        {
            cleanupFailed = true;
            var removedBackup = Directory.EnumerateFiles(path, "Module1.bas", SearchOption.AllDirectories).Single();
            File.Delete(removedBackup);
            throw new IOException($"Injected partial recovery cleanup failure after deleting '{removedBackup}'.");
        }

        Directory.Delete(path, recursive);
    }
}

internal sealed class RollbackRecoveryCleanupFailureExportDestinationFileOperations(
    string failedPlacementFileName)
    : IExportDestinationFileOperations
{
    private bool placementFailed;
    private bool cleanupFailed;

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void CopyFile(string sourcePath, string destinationPath, bool overwrite)
    {
        if (
            !placementFailed
            && Path.GetFileName(sourcePath).Equals(failedPlacementFileName, StringComparison.OrdinalIgnoreCase)
            && sourcePath.Contains("vba-dev-export-", StringComparison.OrdinalIgnoreCase)
            && !sourcePath.Contains("recovery", StringComparison.OrdinalIgnoreCase)
        )
        {
            placementFailed = true;
            throw new IOException("Injected export apply failure.");
        }

        File.Copy(sourcePath, destinationPath, overwrite);
    }

    public void DeleteFile(string path) => File.Delete(path);

    public void DeleteDirectory(string path, bool recursive)
    {
        if (
            !cleanupFailed
            && path.Contains(".vba-dev-export-recovery-", StringComparison.OrdinalIgnoreCase)
        )
        {
            cleanupFailed = true;
            var removedBackup = Directory.EnumerateFiles(path, "Module1.bas", SearchOption.AllDirectories).Single();
            File.Delete(removedBackup);
            throw new IOException($"Injected partial rollback cleanup failure after deleting '{removedBackup}'.");
        }

        Directory.Delete(path, recursive);
    }
}
