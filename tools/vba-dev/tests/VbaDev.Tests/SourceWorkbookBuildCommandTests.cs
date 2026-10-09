using System.Text;
using VbaDev.App.FileSystem;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;
using VbaDev.Composition;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;
using Xunit;

namespace VbaDev.Tests;

public sealed class SourceWorkbookBuildCommandTests
{
    [Fact]
    public void RecoveryBudgetCannotExceedCancellationTimerRange()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ToolingCompositionRoot.CreateApplicationComposition(
                root,
                sourceWorkbookRecoveryBudget: TimeSpan.FromDays(50)));
    }

    [Fact]
    public async Task OrdinaryBuildSavesSelectedSourceWorkbookWithoutCreatingBinWorkbook()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = Path.Combine(root, "src", "Book1");
        Directory.CreateDirectory(sourceSet);
        var sourceWorkbook = Path.Combine(sourceSet, "Book1.xlsm");
        File.WriteAllText(sourceWorkbook, "saved workbook", Encoding.UTF8);
        File.WriteAllText(Path.Combine(sourceSet, "Local.bas"),
            "Attribute VB_Name = \"Local\"", Encoding.UTF8);
        var automation = new RecordingSourceWorkbookAutomation();
        var composition = ToolingCompositionRoot.CreateApplicationComposition(
            root,
            sourceWorkbookAutomation: automation,
            projectSemanticInputProvider: FakeProjectSemanticInputProvider.Empty);
        var binWorkbook = Path.Combine(root, "bin", "Book1.xlsm");

        var result = await composition.BuildCommand.RunAsync(
            new ProjectContextResolver(new JsonProjectManifestStore())
                .Resolve(new ProjectResolutionRequest(root, null, root)),
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"Built {sourceWorkbook}{Environment.NewLine}Imported 1 source files.{Environment.NewLine}",
            result.StandardOutput);
        Assert.Equal(sourceWorkbook, Assert.Single(automation.OpenedPaths));
        Assert.Equal(["import:Local.bas", "save"], automation.Session.Events);
        Assert.False(File.Exists(binWorkbook));
    }

    [Fact]
    public async Task RecoveryStagingCreationFailureReleasesPreparedSourceMirrorBeforeExcelStarts()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = Path.Combine(root, "src", "Book1");
        Directory.CreateDirectory(sourceSet);
        File.WriteAllText(Path.Combine(sourceSet, "Book1.xlsm"), "saved workbook", Encoding.UTF8);
        var moduleName = "EarlyCleanup" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(Path.Combine(sourceSet, moduleName + ".bas"),
            $"Attribute VB_Name = \"{moduleName}\"", Encoding.UTF8);
        var flatParent = Path.Combine(Path.GetTempPath(), "vba-dev-vbe-import", "source-workbook-build");
        var pattern = "*_" + moduleName + ".bas";
        var automation = new RecordingSourceWorkbookAutomation();
        var composition = ToolingCompositionRoot.CreateApplicationComposition(
            root,
            sourceWorkbookAutomation: automation,
            sourceWorkbookRecoveryOwnershipFactory: new ThrowingExactOwnershipFactory(),
            projectSemanticInputProvider: FakeProjectSemanticInputProvider.Empty);

        var result = await composition.BuildCommand.RunAsync(
            new ProjectContextResolver(new JsonProjectManifestStore())
                .Resolve(new ProjectResolutionRequest(root, null, root)),
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Simulated recovery scratch creation failure", result.StandardError);
        Assert.Empty(automation.OpenedPaths);
        Assert.Empty(Directory.GetFiles(flatParent, pattern));
    }

    [Fact]
    public async Task BorrowedWorkbookRestoresCapturedModuleAfterImportFailureWithoutSaving()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = Path.Combine(root, "src", "Book1");
        Directory.CreateDirectory(sourceSet);
        File.WriteAllText(Path.Combine(sourceSet, "Book1.xlsm"), "saved workbook", Encoding.UTF8);
        File.WriteAllText(Path.Combine(sourceSet, "Local.bas"),
            "Attribute VB_Name = \"Local\"", Encoding.UTF8);
        var automation = new RecordingSourceWorkbookAutomation();
        automation.Session.WasAlreadyOpen = true;
        automation.Session.Modules.Add(new WorkbookModule("Existing", WorkbookModuleKind.StandardModule));
        automation.Session.ThrowOnImportOnce = "Local";
        var composition = ToolingCompositionRoot.CreateApplicationComposition(
            root,
            sourceWorkbookAutomation: automation,
            projectSemanticInputProvider: FakeProjectSemanticInputProvider.Empty);

        var result = await composition.BuildCommand.RunAsync(
            new ProjectContextResolver(new JsonProjectManifestStore())
                .Resolve(new ProjectResolutionRequest(root, null, root)),
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Existing", automation.Session.Modules.Select(module => module.Name));
        Assert.DoesNotContain(automation.Session.Events, entry => entry == "save");
        Assert.True(automation.Session.Events.IndexOf("export:Existing")
            < automation.Session.Events.IndexOf("remove:Existing"));
        Assert.Equal(SourceWorkbookSaveState.NotStarted, automation.Session.SaveState);
    }

    [Fact]
    public async Task FailedRecoveryCaptureLeavesBorrowedWorkbookUntouched()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = Path.Combine(root, "src", "Book1");
        Directory.CreateDirectory(sourceSet);
        File.WriteAllText(Path.Combine(sourceSet, "Book1.xlsm"), "saved workbook", Encoding.UTF8);
        File.WriteAllText(Path.Combine(sourceSet, "Local.bas"),
            "Attribute VB_Name = \"Local\"", Encoding.UTF8);
        var automation = new RecordingSourceWorkbookAutomation();
        automation.Session.WasAlreadyOpen = true;
        automation.Session.Modules.Add(new WorkbookModule("Existing", WorkbookModuleKind.StandardModule));
        automation.Session.ThrowOnExportOnce = "Existing";
        var composition = ToolingCompositionRoot.CreateApplicationComposition(
            root,
            sourceWorkbookAutomation: automation,
            projectSemanticInputProvider: FakeProjectSemanticInputProvider.Empty);

        var result = await composition.BuildCommand.RunAsync(
            new ProjectContextResolver(new JsonProjectManifestStore())
                .Resolve(new ProjectResolutionRequest(root, null, root)),
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Simulated recovery capture failure", result.StandardError);
        Assert.Equal(["ThisWorkbook", "Existing"],
            automation.Session.Modules.Select(module => module.Name).ToArray());
        Assert.DoesNotContain(automation.Session.Events,
            entry => entry.StartsWith("remove:", StringComparison.Ordinal)
                || entry.StartsWith("import:", StringComparison.Ordinal)
                || entry == "save");
    }

    [Fact]
    public async Task ImportFailureRemainsPrimaryWhenVbeMirrorCleanupAlsoFails()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = Path.Combine(root, "src", "Book1");
        Directory.CreateDirectory(sourceSet);
        File.WriteAllText(Path.Combine(sourceSet, "Book1.xlsm"), "saved workbook", Encoding.UTF8);
        File.WriteAllText(Path.Combine(sourceSet, "Local.bas"),
            "Attribute VB_Name = \"Local\"", Encoding.UTF8);
        var automation = new RecordingSourceWorkbookAutomation();
        automation.Session.WasAlreadyOpen = true;
        automation.Session.Modules.Add(new WorkbookModule("Existing", WorkbookModuleKind.StandardModule));
        automation.Session.ThrowOnImportOnce = "Local";
        automation.Session.MutateStagedMirrorFileOnImport = true;
        var composition = ToolingCompositionRoot.CreateApplicationComposition(
            root,
            sourceWorkbookAutomation: automation,
            projectSemanticInputProvider: FakeProjectSemanticInputProvider.Empty);

        var result = await composition.BuildCommand.RunAsync(
            new ProjectContextResolver(new JsonProjectManifestStore())
                .Resolve(new ProjectResolutionRequest(root, null, root)),
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Simulated import failure", result.StandardError);
        Assert.Contains("VBE import staging cleanup failed", result.StandardError);
        Assert.Contains(automation.Session.LastBuildImportSourcePath!, result.StandardError);
        Assert.Contains("Existing", automation.Session.Modules.Select(module => module.Name));
        Assert.DoesNotContain("save", automation.Session.Events);
    }

    [Fact]
    public async Task CancellationAfterCaptureBeforeReplacementDoesNotMutateBorrowedWorkbook()
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = Path.Combine(root, "src", "Book1");
        Directory.CreateDirectory(sourceSet);
        File.WriteAllText(Path.Combine(sourceSet, "Book1.xlsm"), "saved workbook", Encoding.UTF8);
        File.WriteAllText(Path.Combine(sourceSet, "Local.bas"),
            "Attribute VB_Name = \"Local\"", Encoding.UTF8);
        var automation = new RecordingSourceWorkbookAutomation();
        automation.Session.WasAlreadyOpen = true;
        automation.Session.Modules.Add(new WorkbookModule("Existing", WorkbookModuleKind.StandardModule));
        automation.Session.AfterExport = cancellation.Cancel;
        var composition = ToolingCompositionRoot.CreateApplicationComposition(
            root,
            sourceWorkbookAutomation: automation,
            projectSemanticInputProvider: FakeProjectSemanticInputProvider.Empty);

        var result = await composition.BuildCommand.RunAsync(
            new ProjectContextResolver(new JsonProjectManifestStore())
                .Resolve(new ProjectResolutionRequest(root, null, root)),
            cancellation.Token);

        Assert.Equal(130, result.ExitCode);
        Assert.Equal(["ThisWorkbook", "Existing"],
            automation.Session.Modules.Select(module => module.Name).ToArray());
        Assert.DoesNotContain(automation.Session.Events,
            entry => entry.StartsWith("remove:", StringComparison.Ordinal)
                || entry.StartsWith("import:", StringComparison.Ordinal)
                || entry == "save");
    }

    [Fact]
    public async Task CancellationAfterReplacementBeforeSaveRestoresBorrowedWorkbookWithoutSaving()
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = Path.Combine(root, "src", "Book1");
        Directory.CreateDirectory(sourceSet);
        File.WriteAllText(Path.Combine(sourceSet, "Book1.xlsm"), "saved workbook", Encoding.UTF8);
        File.WriteAllText(Path.Combine(sourceSet, "Local.bas"),
            "Attribute VB_Name = \"Local\"", Encoding.UTF8);
        var automation = new RecordingSourceWorkbookAutomation();
        automation.Session.WasAlreadyOpen = true;
        automation.Session.Modules.Add(new WorkbookModule("Existing", WorkbookModuleKind.StandardModule));
        automation.Session.AfterVerify = cancellation.Cancel;
        var composition = ToolingCompositionRoot.CreateApplicationComposition(
            root,
            sourceWorkbookAutomation: automation,
            projectSemanticInputProvider: FakeProjectSemanticInputProvider.Empty);

        var result = await composition.BuildCommand.RunAsync(
            new ProjectContextResolver(new JsonProjectManifestStore())
                .Resolve(new ProjectResolutionRequest(root, null, root)),
            cancellation.Token);

        Assert.Equal(130, result.ExitCode);
        Assert.Equal(["ThisWorkbook", "Existing"],
            automation.Session.Modules.Select(module => module.Name).ToArray());
        Assert.DoesNotContain("save", automation.Session.Events);
        Assert.DoesNotContain("remove:ThisWorkbook", automation.Session.Events);
    }

    [Fact]
    public async Task BorrowedWorkbookRecoveryUsesAnOverallBoundedBudget()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = Path.Combine(root, "src", "Book1");
        Directory.CreateDirectory(sourceSet);
        File.WriteAllText(Path.Combine(sourceSet, "Book1.xlsm"), "saved workbook", Encoding.UTF8);
        File.WriteAllText(Path.Combine(sourceSet, "Local.bas"),
            "Attribute VB_Name = \"Local\"", Encoding.UTF8);
        var automation = new RecordingSourceWorkbookAutomation();
        automation.Session.WasAlreadyOpen = true;
        automation.Session.Modules.Add(new WorkbookModule("Existing", WorkbookModuleKind.StandardModule));
        automation.Session.ThrowOnImportOnce = "Local";
        var composition = ToolingCompositionRoot.CreateApplicationComposition(
            root,
            sourceWorkbookAutomation: automation,
            projectSemanticInputProvider: FakeProjectSemanticInputProvider.Empty);

        var result = await composition.BuildCommand.RunAsync(
            new ProjectContextResolver(new JsonProjectManifestStore())
                .Resolve(new ProjectResolutionRequest(root, null, root)),
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.True(automation.Session.RecoveryImportUsedBoundedToken);
        Assert.Contains("Existing", automation.Session.Modules.Select(module => module.Name));
    }

    [Fact]
    public async Task BorrowedWorkbookReportsIncompleteRecoveryWhenRestoredContentDiffers()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = Path.Combine(root, "src", "Book1");
        Directory.CreateDirectory(sourceSet);
        File.WriteAllText(Path.Combine(sourceSet, "Book1.xlsm"), "saved workbook", Encoding.UTF8);
        File.WriteAllText(Path.Combine(sourceSet, "Local.bas"),
            "Attribute VB_Name = \"Local\"", Encoding.UTF8);
        var automation = new RecordingSourceWorkbookAutomation();
        automation.Session.WasAlreadyOpen = true;
        automation.Session.Modules.Add(new WorkbookModule("Existing", WorkbookModuleKind.StandardModule));
        automation.Session.ThrowOnImportOnce = "Local";
        automation.Session.CorruptRecoveryExport = true;
        var composition = ToolingCompositionRoot.CreateApplicationComposition(
            root,
            sourceWorkbookAutomation: automation,
            projectSemanticInputProvider: FakeProjectSemanticInputProvider.Empty);

        var result = await composition.BuildCommand.RunAsync(
            new ProjectContextResolver(new JsonProjectManifestStore())
                .Resolve(new ProjectResolutionRequest(root, null, root)),
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("recovery was incomplete", result.StandardError,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Recovery files:", result.StandardError);
        Assert.DoesNotContain("save", automation.Session.Events);
    }

    [Fact]
    public async Task RecoveryImportFailureRetainsManualFilesAndDoesNotClaimRestoration()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = Path.Combine(root, "src", "Book1");
        Directory.CreateDirectory(sourceSet);
        File.WriteAllText(Path.Combine(sourceSet, "Book1.xlsm"), "saved workbook", Encoding.UTF8);
        File.WriteAllText(Path.Combine(sourceSet, "Local.bas"),
            "Attribute VB_Name = \"Local\"", Encoding.UTF8);
        var automation = new RecordingSourceWorkbookAutomation();
        automation.Session.WasAlreadyOpen = true;
        automation.Session.Modules.Add(new WorkbookModule("Existing", WorkbookModuleKind.StandardModule));
        automation.Session.ThrowOnImportOnce = "Local";
        automation.Session.ThrowOnRecoveryImportOnce = "Existing";
        var composition = ToolingCompositionRoot.CreateApplicationComposition(
            root,
            sourceWorkbookAutomation: automation,
            projectSemanticInputProvider: FakeProjectSemanticInputProvider.Empty);

        var result = await composition.BuildCommand.RunAsync(
            new ProjectContextResolver(new JsonProjectManifestStore())
                .Resolve(new ProjectResolutionRequest(root, null, root)),
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("recovery was incomplete", result.StandardError,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Recovery files:", result.StandardError);
        Assert.Contains("Simulated recovery import failure", result.StandardError);
        Assert.DoesNotContain("Existing", automation.Session.Modules.Select(module => module.Name));
        Assert.DoesNotContain("save", automation.Session.Events);
    }

    [Fact]
    public async Task RecoveryDeadlineStopsBeforeQueuingTheNextModuleImport()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = Path.Combine(root, "src", "Book1");
        Directory.CreateDirectory(sourceSet);
        File.WriteAllText(Path.Combine(sourceSet, "Book1.xlsm"), "saved workbook", Encoding.UTF8);
        File.WriteAllText(Path.Combine(sourceSet, "Local.bas"),
            "Attribute VB_Name = \"Local\"", Encoding.UTF8);
        var automation = new RecordingSourceWorkbookAutomation();
        automation.Session.WasAlreadyOpen = true;
        automation.Session.Modules.Add(new WorkbookModule("First", WorkbookModuleKind.StandardModule));
        automation.Session.Modules.Add(new WorkbookModule("Second", WorkbookModuleKind.StandardModule));
        automation.Session.ThrowOnImportOnce = "Local";
        automation.Session.DelayFirstRecoveryImportUntilCancellation = true;
        var composition = ToolingCompositionRoot.CreateApplicationComposition(
            root,
            sourceWorkbookAutomation: automation,
            projectSemanticInputProvider: FakeProjectSemanticInputProvider.Empty,
            sourceWorkbookRecoveryBudget: TimeSpan.FromMilliseconds(500));

        var result = await composition.BuildCommand.RunAsync(
            new ProjectContextResolver(new JsonProjectManifestStore())
                .Resolve(new ProjectResolutionRequest(root, null, root)),
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("recovery was incomplete", result.StandardError,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("deadline expired", result.StandardError,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Recovery files:", result.StandardError);
        Assert.Contains("import:First.bas", automation.Session.Events);
        Assert.DoesNotContain("import:Second.bas", automation.Session.Events);
        Assert.DoesNotContain("save", automation.Session.Events);
    }

    [Fact]
    public async Task BorrowedWorkbookRestoresRemovedReferenceAfterImportFailure()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = Path.Combine(root, "src", "Book1");
        Directory.CreateDirectory(sourceSet);
        File.WriteAllText(Path.Combine(sourceSet, "Book1.xlsm"), "saved workbook", Encoding.UTF8);
        File.WriteAllText(Path.Combine(sourceSet, "Local.bas"),
            "Attribute VB_Name = \"Local\"", Encoding.UTF8);
        var automation = new RecordingSourceWorkbookAutomation();
        automation.Session.WasAlreadyOpen = true;
        automation.Session.References.Add(new WorkbookReference(
            "Legacy", true, "Legacy", "{11111111-1111-1111-1111-111111111111}", 1, 0));
        automation.Session.ThrowOnImportOnce = "Local";
        var composition = ToolingCompositionRoot.CreateApplicationComposition(
            root,
            sourceWorkbookAutomation: automation,
            projectSemanticInputProvider: FakeProjectSemanticInputProvider.Empty);

        var result = await composition.BuildCommand.RunAsync(
            new ProjectContextResolver(new JsonProjectManifestStore())
                .Resolve(new ProjectResolutionRequest(root, null, root)),
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(automation.Session.References,
            reference => reference.Name == "Legacy"
                && reference.Guid == "{11111111-1111-1111-1111-111111111111}");
        Assert.Contains("remove-ref:Legacy", automation.Session.Events);
        Assert.Contains("add-ref:Legacy", automation.Session.Events);
        Assert.DoesNotContain("save", automation.Session.Events);
    }

    [Fact]
    public async Task BuildRechecksLiveProjectAuthorityAfterImportBeforeSave()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = Path.Combine(root, "src", "Book1");
        Directory.CreateDirectory(sourceSet);
        File.WriteAllText(Path.Combine(sourceSet, "Book1.xlsm"), "saved workbook", Encoding.UTF8);
        File.WriteAllText(Path.Combine(sourceSet, "Local.bas"),
            "Attribute VB_Name = \"Local\"", Encoding.UTF8);
        var automation = new RecordingSourceWorkbookAutomation();
        automation.Session.WasAlreadyOpen = true;
        automation.Session.AfterVerify = () => automation.Session.ProjectName = "Local";
        var composition = ToolingCompositionRoot.CreateApplicationComposition(
            root,
            sourceWorkbookAutomation: automation,
            projectSemanticInputProvider: FakeProjectSemanticInputProvider.Empty);

        var result = await composition.BuildCommand.RunAsync(
            new ProjectContextResolver(new JsonProjectManifestStore())
                .Resolve(new ProjectResolutionRequest(root, null, root)),
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("project", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("save", automation.Session.Events);
    }

    [Fact]
    public async Task RecoveryReportsIncompleteWhenOriginalReferencePriorityCannotBeRestored()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = Path.Combine(root, "src", "Book1");
        Directory.CreateDirectory(sourceSet);
        File.WriteAllText(Path.Combine(sourceSet, "Book1.xlsm"), "saved workbook", Encoding.UTF8);
        File.WriteAllText(Path.Combine(sourceSet, "Local.bas"),
            "Attribute VB_Name = \"Local\"", Encoding.UTF8);
        var automation = new RecordingSourceWorkbookAutomation();
        automation.Session.WasAlreadyOpen = true;
        automation.Session.Modules.Add(new WorkbookModule("Existing", WorkbookModuleKind.StandardModule));
        automation.Session.References.Add(new WorkbookReference(
            "Legacy", true, "Legacy", "{11111111-1111-1111-1111-111111111111}", 1, 0));
        automation.Session.References.Add(new WorkbookReference(
            "Visual Basic For Applications", false, "VBA"));
        automation.Session.ThrowOnImportOnce = "Local";
        var composition = ToolingCompositionRoot.CreateApplicationComposition(
            root,
            sourceWorkbookAutomation: automation,
            projectSemanticInputProvider: FakeProjectSemanticInputProvider.Empty);

        var result = await composition.BuildCommand.RunAsync(
            new ProjectContextResolver(new JsonProjectManifestStore())
                .Resolve(new ProjectResolutionRequest(root, null, root)),
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("recovery was incomplete", result.StandardError,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Recovery files:", result.StandardError);
        Assert.DoesNotContain("save", automation.Session.Events);
    }

    [Fact]
    public async Task UnconfirmedSaveReportsUnknownOutcomeWithoutClaimingRollback()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = Path.Combine(root, "src", "Book1");
        Directory.CreateDirectory(sourceSet);
        var sourceWorkbook = Path.Combine(sourceSet, "Book1.xlsm");
        File.WriteAllText(sourceWorkbook, "saved workbook", Encoding.UTF8);
        File.WriteAllText(Path.Combine(sourceSet, "Local.bas"),
            "Attribute VB_Name = \"Local\"", Encoding.UTF8);
        var automation = new RecordingSourceWorkbookAutomation();
        automation.Session.SaveFailureState = SourceWorkbookSaveState.Unknown;
        var composition = ToolingCompositionRoot.CreateApplicationComposition(
            root,
            sourceWorkbookAutomation: automation,
            projectSemanticInputProvider: FakeProjectSemanticInputProvider.Empty);

        var result = await composition.BuildCommand.RunAsync(
            new ProjectContextResolver(new JsonProjectManifestStore())
                .Resolve(new ProjectResolutionRequest(root, null, root)),
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Save outcome is unknown", result.StandardError,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(sourceWorkbook, result.StandardError);
        Assert.DoesNotContain("rollback", result.StandardError,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NormallyReturnedSaveWithoutConfirmedSavedStateReportsUnknown()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = Path.Combine(root, "src", "Book1");
        Directory.CreateDirectory(sourceSet);
        var sourceWorkbook = Path.Combine(sourceSet, "Book1.xlsm");
        File.WriteAllText(sourceWorkbook, "saved workbook", Encoding.UTF8);
        File.WriteAllText(Path.Combine(sourceSet, "Local.bas"),
            "Attribute VB_Name = \"Local\"", Encoding.UTF8);
        var automation = new RecordingSourceWorkbookAutomation();
        automation.Session.ReturnFromSaveWithoutConfirmingSaved = true;
        var composition = ToolingCompositionRoot.CreateApplicationComposition(
            root,
            sourceWorkbookAutomation: automation,
            projectSemanticInputProvider: FakeProjectSemanticInputProvider.Empty);

        var result = await composition.BuildCommand.RunAsync(
            new ProjectContextResolver(new JsonProjectManifestStore())
                .Resolve(new ProjectResolutionRequest(root, null, root)),
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Save outcome is unknown", result.StandardError);
        Assert.Contains(sourceWorkbook, result.StandardError);
        Assert.Equal(SourceWorkbookSaveState.Unknown, automation.Session.SaveState);
        Assert.DoesNotContain("rollback", result.StandardError,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostSaveSourceMirrorCleanupFailureNamesSavedStateAndExactRetainedFile()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = Path.Combine(root, "src", "Book1");
        Directory.CreateDirectory(sourceSet);
        File.WriteAllText(Path.Combine(sourceSet, "Book1.xlsm"), "saved workbook", Encoding.UTF8);
        File.WriteAllText(Path.Combine(sourceSet, "Local.bas"),
            "Attribute VB_Name = \"Local\"", Encoding.UTF8);
        var automation = new RecordingSourceWorkbookAutomation();
        automation.Session.MutateStagedMirrorFileOnImport = true;
        var composition = ToolingCompositionRoot.CreateApplicationComposition(
            root,
            sourceWorkbookAutomation: automation,
            projectSemanticInputProvider: FakeProjectSemanticInputProvider.Empty);

        var result = await composition.BuildCommand.RunAsync(
            new ProjectContextResolver(new JsonProjectManifestStore())
                .Resolve(new ProjectResolutionRequest(root, null, root)),
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(SourceWorkbookSaveState.Saved, automation.Session.SaveState);
        Assert.Contains("Save completed", result.StandardError);
        Assert.Contains("VBE import staging cleanup failed", result.StandardError);
        Assert.Contains(automation.Session.LastBuildImportSourcePath!, result.StandardError);
    }

    [Fact]
    public async Task UnknownSaveAndMirrorCleanupFailurePreserveBothDiagnostics()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = Path.Combine(root, "src", "Book1");
        Directory.CreateDirectory(sourceSet);
        File.WriteAllText(Path.Combine(sourceSet, "Book1.xlsm"), "saved workbook", Encoding.UTF8);
        File.WriteAllText(Path.Combine(sourceSet, "Local.bas"),
            "Attribute VB_Name = \"Local\"", Encoding.UTF8);
        var automation = new RecordingSourceWorkbookAutomation();
        automation.Session.MutateStagedMirrorFileOnImport = true;
        automation.Session.SaveFailureState = SourceWorkbookSaveState.Unknown;
        var composition = ToolingCompositionRoot.CreateApplicationComposition(
            root,
            sourceWorkbookAutomation: automation,
            projectSemanticInputProvider: FakeProjectSemanticInputProvider.Empty);

        var result = await composition.BuildCommand.RunAsync(
            new ProjectContextResolver(new JsonProjectManifestStore())
                .Resolve(new ProjectResolutionRequest(root, null, root)),
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Save outcome is unknown", result.StandardError);
        Assert.Contains("VBE import staging cleanup failed", result.StandardError);
        Assert.Contains(automation.Session.LastBuildImportSourcePath!, result.StandardError);
        Assert.DoesNotContain("rollback", result.StandardError,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnprovedAutomationReleaseRetainsVbeImportMirror()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = Path.Combine(root, "src", "Book1");
        Directory.CreateDirectory(sourceSet);
        File.WriteAllText(Path.Combine(sourceSet, "Book1.xlsm"), "saved workbook", Encoding.UTF8);
        File.WriteAllText(Path.Combine(sourceSet, "Local.bas"),
            "Attribute VB_Name = \"Local\"", Encoding.UTF8);
        var automation = new RecordingSourceWorkbookAutomation
        {
            FailReleaseAfterOperation = true
        };
        var composition = ToolingCompositionRoot.CreateApplicationComposition(
            root,
            sourceWorkbookAutomation: automation,
            projectSemanticInputProvider: FakeProjectSemanticInputProvider.Empty);

        var result = await composition.BuildCommand.RunAsync(
            new ProjectContextResolver(new JsonProjectManifestStore())
                .Resolve(new ProjectResolutionRequest(root, null, root)),
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        var importMirrorFile = automation.Session.LastBuildImportSourcePath!;
        Assert.True(File.Exists(importMirrorFile));
        Assert.Contains(importMirrorFile, result.StandardError);
        Assert.Contains("retained", result.StandardError,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingDeclaredFormSidecarStopsBuildBeforeRemovingAnyModule()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = Path.Combine(root, "src", "Book1");
        Directory.CreateDirectory(sourceSet);
        File.WriteAllText(Path.Combine(sourceSet, "Book1.xlsm"), "saved workbook", Encoding.UTF8);
        File.WriteAllText(Path.Combine(sourceSet, "Local.bas"),
            "Attribute VB_Name = \"Local\"", Encoding.UTF8);
        var automation = new RecordingSourceWorkbookAutomation();
        automation.Session.WasAlreadyOpen = true;
        automation.Session.Modules.Add(new WorkbookModule("Dialog", WorkbookModuleKind.Form));
        automation.Session.ExportFormWithoutDeclaredSidecar = true;
        var composition = ToolingCompositionRoot.CreateApplicationComposition(
            root,
            sourceWorkbookAutomation: automation,
            projectSemanticInputProvider: FakeProjectSemanticInputProvider.Empty);

        var result = await composition.BuildCommand.RunAsync(
            new ProjectContextResolver(new JsonProjectManifestStore())
                .Resolve(new ProjectResolutionRequest(root, null, root)),
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Dialog.frx", result.StandardError,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Dialog", automation.Session.Modules.Select(module => module.Name));
        Assert.Contains("ThisWorkbook", automation.Session.Modules.Select(module => module.Name));
        Assert.DoesNotContain(automation.Session.Events,
            entry => entry.StartsWith("remove:", StringComparison.Ordinal));
        Assert.DoesNotContain("save", automation.Session.Events);
    }

    [Fact]
    public async Task FailedBorrowedBuildRestoresCapturedFormAndExactSidecarWithoutReplacingDocumentModule()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = Path.Combine(root, "src", "Book1");
        Directory.CreateDirectory(sourceSet);
        File.WriteAllText(Path.Combine(sourceSet, "Book1.xlsm"), "saved workbook", Encoding.UTF8);
        File.WriteAllText(Path.Combine(sourceSet, "Local.bas"),
            "Attribute VB_Name = \"Local\"", Encoding.UTF8);
        var automation = new RecordingSourceWorkbookAutomation();
        automation.Session.WasAlreadyOpen = true;
        automation.Session.Modules.Add(new WorkbookModule("Dialog", WorkbookModuleKind.Form));
        automation.Session.ExportFormWithDeclaredSidecar = true;
        automation.Session.ThrowOnImportOnce = "Local";
        var composition = ToolingCompositionRoot.CreateApplicationComposition(
            root,
            sourceWorkbookAutomation: automation,
            projectSemanticInputProvider: FakeProjectSemanticInputProvider.Empty);

        var result = await composition.BuildCommand.RunAsync(
            new ProjectContextResolver(new JsonProjectManifestStore())
                .Resolve(new ProjectResolutionRequest(root, null, root)),
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(["ThisWorkbook", "Dialog"],
            automation.Session.Modules.Select(module => module.Name).ToArray());
        Assert.Equal(new byte[] { 11, 22, 33, 44 }, automation.Session.RestoredFormSidecarBytes);
        Assert.DoesNotContain("remove:ThisWorkbook", automation.Session.Events);
        Assert.DoesNotContain("save", automation.Session.Events);
    }

    [Fact]
    public async Task CancellationAfterVerifiedSaveAndProvedReleaseKeepsBuildSuccessful()
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = Path.Combine(root, "src", "Book1");
        Directory.CreateDirectory(sourceSet);
        var sourceWorkbook = Path.Combine(sourceSet, "Book1.xlsm");
        File.WriteAllText(sourceWorkbook, "saved workbook", Encoding.UTF8);
        File.WriteAllText(Path.Combine(sourceSet, "Local.bas"),
            "Attribute VB_Name = \"Local\"", Encoding.UTF8);
        var automation = new RecordingSourceWorkbookAutomation
        {
            AfterOperation = cancellation.Cancel,
            AfterOperationFailure = new WorkbookAutomationCanceledException(
                new WorkbookAutomationStage(WorkbookAutomationStageKind.ProcessCleanup),
                cancellation.Token)
        };
        var composition = ToolingCompositionRoot.CreateApplicationComposition(
            root,
            sourceWorkbookAutomation: automation,
            projectSemanticInputProvider: FakeProjectSemanticInputProvider.Empty);

        var result = await composition.BuildCommand.RunAsync(
            new ProjectContextResolver(new JsonProjectManifestStore())
                .Resolve(new ProjectResolutionRequest(root, null, root)),
            cancellation.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains($"Built {sourceWorkbook}", result.StandardOutput);
        Assert.Equal(SourceWorkbookSaveState.Saved, automation.Session.SaveState);
    }
}

internal sealed class RecordingSourceWorkbookAutomation : ISourceWorkbookAutomation
{
    public RecordingSourceWorkbookSession Session { get; } = new();

    public List<string> OpenedPaths { get; } = [];

    public bool FailReleaseAfterOperation { get; init; }

    public Action? AfterOperation { get; init; }

    public Exception? AfterOperationFailure { get; init; }

    public async Task<TResult> RunAsync<TResult>(
        string workbookPath,
        WorkbookAutomationTimeouts timeouts,
        Func<ISourceWorkbookSession, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        OpenedPaths.Add(workbookPath);
        var result = await operation(Session, cancellationToken);
        AfterOperation?.Invoke();
        if (AfterOperationFailure is not null) throw AfterOperationFailure;
        if (FailReleaseAfterOperation)
            throw new WorkbookAutomationCleanupException("Simulated unproved automation release.");
        return result;
    }
}

internal sealed class ThrowingExactOwnershipFactory : IExactFileSystemObjectOwnershipFactory
{
    public ExactFileSystemObjectOwnership Open()
        => throw new IOException("Simulated recovery scratch creation failure.");
}

internal sealed class RecordingSourceWorkbookSession : ISourceWorkbookSession
{
    public bool WasAlreadyOpen { get; set; }

    public bool IsSaved { get; set; } = true;

    public string ProjectName { get; set; } = "VBAProject";

    public Action? AfterVerify { get; set; }

    public SourceWorkbookSaveState SaveState { get; private set; } = SourceWorkbookSaveState.NotStarted;

    public List<string> Events { get; } = [];

    public List<WorkbookModule> Modules { get; } =
    [
        new WorkbookModule("ThisWorkbook", WorkbookModuleKind.Document)
    ];

    public List<WorkbookReference> References { get; } = [];

    public string? ThrowOnImportOnce { get; set; }

    public string? ThrowOnRecoveryImportOnce { get; set; }

    public SourceWorkbookSaveState? SaveFailureState { get; set; }

    public bool ReturnFromSaveWithoutConfirmingSaved { get; set; }

    public string? LastImportedSourcePath { get; private set; }

    public string? LastBuildImportSourcePath { get; private set; }

    public bool ExportFormWithoutDeclaredSidecar { get; set; }

    public string? ThrowOnExportOnce { get; set; }

    public bool ExportFormWithDeclaredSidecar { get; set; }

    public bool CorruptRecoveryExport { get; set; }

    public bool DelayFirstRecoveryImportUntilCancellation { get; set; }

    private bool recoveryImportOccurred;

    public byte[]? RestoredFormSidecarBytes { get; private set; }

    public Action? AfterExport { get; set; }

    public bool MutateStagedMirrorFileOnImport { get; set; }

    public bool RecoveryImportUsedBoundedToken { get; private set; }

    public Task<bool> IsSavedAsync(CancellationToken cancellationToken)
        => Task.FromResult(IsSaved);

    public Task<string> GetProjectNameAsync(CancellationToken cancellationToken)
        => Task.FromResult(ProjectName);

    public Task<IReadOnlyList<WorkbookModule>> GetModulesAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<WorkbookModule>>(Modules.ToArray());

    public Task<IReadOnlyList<WorkbookReference>> GetReferencesAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<WorkbookReference>>(References.ToArray());

    public Task<bool> RemoveReferenceAsync(string referenceName, CancellationToken cancellationToken)
    {
        Events.Add($"remove-ref:{referenceName}");
        return Task.FromResult(References.RemoveAll(reference => reference.Name == referenceName) == 1);
    }

    public Task AddReferenceAsync(ResolvedVbaProjectReference reference, CancellationToken cancellationToken)
    {
        Events.Add($"add-ref:{reference.Name}");
        References.Add(new WorkbookReference(reference.Name, true, reference.Name,
            reference.Guid, reference.Major, reference.Minor));
        return Task.CompletedTask;
    }

    public Task RemoveModuleAsync(string moduleName, CancellationToken cancellationToken)
    {
        Events.Add($"remove:{moduleName}");
        Modules.RemoveAll(module => module.Name == moduleName);
        return Task.CompletedTask;
    }

    public async Task ImportModuleAsync(VbeImportSourceFile sourceFile, CancellationToken cancellationToken)
    {
        Events.Add($"import:{sourceFile.ImportVerification.ComponentName}{Path.GetExtension(sourceFile.SourcePath)}");
        LastImportedSourcePath = sourceFile.SourcePath;
        if (sourceFile.ImportVerification.OriginalEncoding != "recovery")
        {
            LastBuildImportSourcePath = sourceFile.SourcePath;
            if (MutateStagedMirrorFileOnImport)
            {
                File.AppendAllText(sourceFile.SourcePath, "'foreign", Encoding.UTF8);
                MutateStagedMirrorFileOnImport = false;
            }
        }
        if (sourceFile.ImportVerification.OriginalEncoding == "recovery")
        {
            RecoveryImportUsedBoundedToken = cancellationToken.CanBeCanceled;
            recoveryImportOccurred = true;
            if (sourceFile.Kind == VbaSourceKind.Form && sourceFile.BinaryPath is { } sidecarPath)
                RestoredFormSidecarBytes = File.ReadAllBytes(sidecarPath);
            if (DelayFirstRecoveryImportUntilCancellation)
            {
                DelayFirstRecoveryImportUntilCancellation = false;
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            if (sourceFile.ImportVerification.ComponentName == ThrowOnRecoveryImportOnce)
            {
                ThrowOnRecoveryImportOnce = null;
                throw new IOException("Simulated recovery import failure.");
            }
        }
        if (sourceFile.ImportVerification.ComponentName == ThrowOnImportOnce)
        {
            ThrowOnImportOnce = null;
            throw new IOException("Simulated import failure.");
        }
        Modules.Add(new WorkbookModule(sourceFile.ImportVerification.ComponentName,
            sourceFile.Kind switch
            {
                VbaSourceKind.StandardModule => WorkbookModuleKind.StandardModule,
                VbaSourceKind.ClassModule => WorkbookModuleKind.ClassModule,
                VbaSourceKind.Form => WorkbookModuleKind.Form,
                _ => throw new ArgumentOutOfRangeException()
            }));
    }

    public Task ExportModuleAsync(string moduleName, string destinationPath, CancellationToken cancellationToken)
    {
        Events.Add($"export:{moduleName}");
        if (ThrowOnExportOnce == moduleName)
        {
            ThrowOnExportOnce = null;
            throw new IOException("Simulated recovery capture failure.");
        }
        File.WriteAllText(destinationPath,
            (ExportFormWithoutDeclaredSidecar || ExportFormWithDeclaredSidecar) && moduleName == "Dialog"
                ? "VERSION 5.00\r\nBegin VB.UserForm Dialog\r\n    Picture = \"Dialog.frx\":0000\r\nEnd\r\nAttribute VB_Name = \"Dialog\""
                : CorruptRecoveryExport && recoveryImportOccurred
                    ? $"Attribute VB_Name = \"{moduleName}\"\r\n'changed"
                    : $"Attribute VB_Name = \"{moduleName}\"", Encoding.UTF8);
        if (ExportFormWithDeclaredSidecar && moduleName == "Dialog")
            File.WriteAllBytes(Path.ChangeExtension(destinationPath, ".frx"), [11, 22, 33, 44]);
        AfterExport?.Invoke();
        return Task.CompletedTask;
    }

    public Task<VbeImportVerificationReport> VerifyAsync(CancellationToken cancellationToken)
    {
        AfterVerify?.Invoke();
        return Task.FromResult(VbeImportVerificationReport.Empty);
    }

    public Task SaveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Events.Add("save");
        if (ReturnFromSaveWithoutConfirmingSaved)
        {
            SaveState = SourceWorkbookSaveState.Unknown;
            IsSaved = false;
            return Task.CompletedTask;
        }
        if (SaveFailureState is { } failureState)
        {
            SaveState = failureState;
            throw new IOException("Simulated Save failure.");
        }
        SaveState = SourceWorkbookSaveState.Saved;
        IsSaved = true;
        return Task.CompletedTask;
    }
}
