using System.Text;
using VbaDev.App.Build;
using VbaDev.App.Cli;
using VbaDev.App.Testing;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;
using VbaDev.Cli;
using VbaDev.Composition;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;
using VbaDev.Infrastructure.FileSystem;
using Xunit;

namespace VbaDev.Tests;

public sealed class SourceWorkbookTestCommandTests
{
    [Fact]
    public async Task NoBuildReaderReleaseFailureDoesNotClaimUnallocatedImportOrRecoveryStagingWasRetained()
    {
        using var fixture = new SourceTestFixture();
        fixture.Automation.Session.TestFailure = new WorkbookAutomationComReferenceReleaseException(null,
            [new("result cell", new InvalidOperationException("Cell release failed."))]);

        var result = await fixture.Application.RunAsync(["test", "--no-build", "--format", "ndjson"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("COM reference release", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("Cell release failed.", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("staging were retained", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("No import or recovery staging was allocated", result.StandardError, StringComparison.Ordinal);
        Assert.Equal(["test"], fixture.Automation.Session.Events);
    }

    [Fact]
    public async Task UnprovedReaderReleaseRetainsInputScratchWithoutClaimingAnUnreleasedExcelProcess()
    {
        using var fixture = new SourceTestFixture();
        using var temp = TempDirectory.Create();
        var snapshot = temp.CreateDirectory("editor-capture");
        File.Copy(fixture.SourcePath, Path.Combine(snapshot, "Local.bas"));
        var scratch = temp.CreateDirectory("input-scratch");
        var stage = new WorkbookAutomationStage(WorkbookAutomationStageKind.TestExecution);
        var primary = new InvalidOperationException("Original result cell read failed.");
        var reader = new WorkbookAutomationComReferenceReleaseException(primary,
            [new("result cell", new InvalidOperationException("Cell release failed."))]);
        var released = new WorkbookAutomationReleasedProcessCleanupException("Exact Excel release proved", reader);
        ((IWorkbookAutomationLifecycleFailure)released).LifecycleEvidence = new(stage, true, true, false);
        fixture.Automation.Session.WasAlreadyOpen = true;
        fixture.Automation.Session.TestFailure = released;
        var composition = ToolingCompositionRoot.CreateApplicationComposition(fixture.Root,
            sourceWorkbookAutomation: fixture.Automation,
            projectSemanticInputProvider: FakeProjectSemanticInputProvider.Empty);
        var command = new TestCommand(composition.BuildCommand, new FakeWorkbookTestRunner(),
            new TestResultOutputFormatter(), new TestProcedureSourceLocator(),
            new SnapshotTestExecutionWorkspaceFactory(new WindowsExactFileSystemObjectOwnershipFactory(),
                new FileSystemPathIdentityResolver(), scratch));
        var context = new ProjectContextResolver(new JsonProjectManifestStore())
            .Resolve(new ProjectResolutionRequest(fixture.Root, null, fixture.Root));

        var result = await command.RunAsync(context,
            new TestCommandRequest("ndjson", true, new(), TimeSpan.FromSeconds(600), SourceSnapshotPath: snapshot),
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal(OwnedProcessReleaseProof.ProvenOrNotStarted, result.OwnedProcessReleaseProof);
        var retained = Assert.Single(Directory.EnumerateDirectories(scratch));
        Assert.Contains(retained, result.StandardError, StringComparison.Ordinal);
        Assert.Contains("COM reference release", result.StandardError, StringComparison.Ordinal);
        Assert.Contains(primary.Message, result.StandardError, StringComparison.Ordinal);
        Assert.Equal(1, fixture.Automation.Session.Events.Count(item => item == "test"));
        Assert.DoesNotContain("save", fixture.Automation.Session.Events);
    }

    [Fact]
    public async Task CancellationAfterImportBeforeMacroRestoresTheBorrowedCodeWithoutSaving()
    {
        using var fixture = new SourceTestFixture();
        using var cancellation = new CancellationTokenSource();
        var session = fixture.Automation.Session;
        session.WasAlreadyOpen = true;
        session.Modules.Add(new("Original", WorkbookModuleKind.StandardModule));
        session.AfterVerify = cancellation.Cancel;

        var result = await fixture.Application.RunAsync(["test"], cancellation.Token);

        Assert.Equal(130, result.ExitCode);
        Assert.Equal(["ThisWorkbook", "Original"], session.Modules.Select(module => module.Name));
        Assert.Contains("import:Original.bas", session.Events);
        Assert.DoesNotContain("test", session.Events);
        Assert.DoesNotContain("save", session.Events);
        Assert.True(session.RecoveryImportUsedBoundedToken);
    }

    [Fact]
    public async Task CancellationFromMacroExecutionDoesNotRestoreOrReplayTheBorrowedCode()
    {
        using var fixture = new SourceTestFixture();
        using var cancellation = new CancellationTokenSource();
        var session = fixture.Automation.Session;
        session.WasAlreadyOpen = true;
        session.Modules.Add(new("Original", WorkbookModuleKind.StandardModule));
        session.BeforeTests = cancellation.Cancel;
        session.TestFailure = new WorkbookAutomationCanceledException(
            new WorkbookAutomationStage(WorkbookAutomationStageKind.TestExecution), cancellation.Token);

        var result = await fixture.Application.RunAsync(["test"], cancellation.Token);

        Assert.Equal(130, result.ExitCode);
        Assert.Equal(["ThisWorkbook", "Local"], session.Modules.Select(module => module.Name));
        Assert.Equal(1, session.Events.Count(item => item == "test"));
        Assert.DoesNotContain("import:Original.bas", session.Events);
        Assert.DoesNotContain("save", session.Events);
        Assert.Empty(result.StandardOutput);
    }

    [Fact]
    public async Task FailedBorrowedTestRestoresTheOriginalFormSidecarAndReferenceWithoutSaving()
    {
        using var fixture = new SourceTestFixture();
        var session = fixture.Automation.Session;
        session.WasAlreadyOpen = true;
        session.Modules.Add(new("Dialog", WorkbookModuleKind.Form));
        var reference = new WorkbookReference("UserLibrary", true, "UserLibrary",
            "11111111-1111-1111-1111-111111111111", 1, 0);
        session.References.Add(reference);
        session.ExportFormWithDeclaredSidecar = true;
        session.ThrowOnImportOnce = "Local";

        var result = await fixture.Application.RunAsync(["test"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(["ThisWorkbook", "Dialog"], session.Modules.Select(module => module.Name));
        Assert.Equal([reference], session.References);
        Assert.Equal([11, 22, 33, 44], session.RestoredFormSidecarBytes);
        Assert.DoesNotContain("test", session.Events);
        Assert.DoesNotContain("save", session.Events);
    }

    [Fact]
    public async Task IncompleteTestRecoveryReportsTheTestBoundaryAndRetainsOriginalFailure()
    {
        using var fixture = new SourceTestFixture();
        var session = fixture.Automation.Session;
        session.WasAlreadyOpen = true;
        session.Modules.Add(new("Original", WorkbookModuleKind.StandardModule));
        session.ThrowOnImportOnce = "Local";
        session.ThrowOnRecoveryImportOnce = "Original";

        var result = await fixture.Application.RunAsync(["test"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Test failed before VBA test execution", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("Simulated import failure", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("Simulated recovery import failure", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("Build failed before Save", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("test", session.Events);
        Assert.DoesNotContain("save", session.Events);
    }

    [Fact]
    public async Task OriginalTestCommandConstructorCannotFallBackToGeneratedBinOrPathReopening()
    {
        using var fixture = new SourceTestFixture();
        var context = new ProjectContextResolver(new JsonProjectManifestStore())
            .Resolve(new ProjectResolutionRequest(fixture.Root, null, fixture.Root));
        var composition = ToolingCompositionRoot.CreateApplicationComposition(fixture.Root,
            sourceWorkbookAutomation: fixture.Automation,
            workbookGenerationAutomation: fixture.Generation,
            projectSemanticInputProvider: FakeProjectSemanticInputProvider.Empty);
        var legacyRunner = new FakeWorkbookTestRunner(new WorkbookTestResultRow("Local", "Test_Passes", "OK", ""));
        var command = new TestCommand(composition.BuildCommand, legacyRunner,
            new TestResultOutputFormatter(), new TestProcedureSourceLocator(),
            new VbaDev.Infrastructure.FileSystem.FileSystemPathIdentityResolver(),
            new VbaDev.Infrastructure.FileSystem.WindowsExactFileSystemObjectOwnershipFactory());

        var result = await command.RunAsync(context,
            new TestCommandRequest("text", true, new()), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([fixture.WorkbookPath], fixture.Automation.OpenedPaths);
        Assert.Equal(["import:Local.bas", "test"], fixture.Automation.Session.Events);
        Assert.Empty(legacyRunner.Workbooks);
        Assert.Empty(fixture.Generation.OpenedWorkbooks);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "bin")));
    }

    [Fact]
    public async Task NoBuildExecutesLiveVbaWithoutExternalAdmissionImportSaveOrGuessedLocations()
    {
        using var fixture = new SourceTestFixture();
        File.WriteAllText(fixture.SourcePath, "Public Sub Broken(\n", Encoding.UTF8);
        fixture.Automation.Session.WasAlreadyOpen = true;
        fixture.Automation.Session.TestRows = [new("LiveModule", "Test_Live", "OK", "live state")];

        var result = await fixture.Application.RunAsync(["test", "--no-build", "--format", "ndjson"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([fixture.WorkbookPath], fixture.Automation.OpenedPaths);
        Assert.Equal(["test"], fixture.Automation.Session.Events);
        Assert.Contains("Test_Live", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("\"location\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--no-build", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(fixture.Generation.OpenedWorkbooks);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "bin")));
    }

    [Fact]
    public async Task NormalTestRejectsSyntaxErrorsBeforeSourceImportOrMacroExecution()
    {
        using var fixture = new SourceTestFixture();
        File.WriteAllText(fixture.SourcePath, "Attribute VB_Name = \"Local\"\nPublic Sub Broken(\n", Encoding.UTF8);

        var result = await fixture.Application.RunAsync(["test", "--format", "ndjson"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("sourceAnalysis", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(result.StandardOutput);
        Assert.Empty(fixture.Automation.OpenedPaths);
        Assert.Empty(fixture.Automation.Session.Events);
    }

    [Fact]
    public async Task ImportFailureRestoresBorrowedModulesWithoutSavingOrRunningTests()
    {
        using var fixture = new SourceTestFixture();
        var session = fixture.Automation.Session;
        session.WasAlreadyOpen = true;
        session.Modules.Add(new("Original", WorkbookModuleKind.StandardModule));
        session.ThrowOnImportOnce = "Local";

        var result = await fixture.Application.RunAsync(["test"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Simulated import failure", result.StandardError, StringComparison.Ordinal);
        Assert.Equal(["ThisWorkbook", "Original"], session.Modules.Select(module => module.Name));
        Assert.Contains("import:Original.bas", session.Events);
        Assert.DoesNotContain("test", session.Events);
        Assert.DoesNotContain("save", session.Events);
    }

    [Fact]
    public async Task MacroFailureIsNotReplayedOrRolledBackAsAnImportFailure()
    {
        using var fixture = new SourceTestFixture();
        var session = fixture.Automation.Session;
        session.WasAlreadyOpen = true;
        session.Modules.Add(new("Original", WorkbookModuleKind.StandardModule));
        session.TestFailure = new InvalidOperationException("Test VBA failed after possible side effects.");

        var result = await fixture.Application.RunAsync(["test"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(session.TestFailure.Message, result.StandardError, StringComparison.Ordinal);
        Assert.Equal(1, session.Events.Count(item => item == "test"));
        Assert.DoesNotContain("import:Original.bas", session.Events);
        Assert.DoesNotContain("save", session.Events);
        Assert.Equal(["ThisWorkbook", "Local"], session.Modules.Select(module => module.Name));
    }

    [Fact]
    public async Task ExecutedSourceLocationsSurviveExternalSourceDeletionDuringTheMacro()
    {
        using var fixture = new SourceTestFixture();
        fixture.Automation.Session.BeforeTests = () => File.Delete(fixture.SourcePath);

        var result = await fixture.Application.RunAsync(["test", "--format", "ndjson"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(new Uri(fixture.SourcePath).AbsoluteUri, result.StandardOutput, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.SourcePath));
    }

    [Fact]
    public async Task SnapshotTestImportsCapturedEditorBytesIntoSourceWithoutSavingOrCreatingOutput()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = temp.CreateDirectory(Path.Combine("Project", "src", "Book1"));
        var sourceWorkbook = Path.Combine(sourceSet, "Book1.xlsm");
        File.WriteAllText(sourceWorkbook, "source workbook", Encoding.UTF8);
        var diskCode = "Attribute VB_Name = \"Local\"\nPublic Sub Test_Disk()\nEnd Sub\n";
        File.WriteAllText(Path.Combine(sourceSet, "Local.bas"), diskCode, Encoding.UTF8);
        var snapshot = temp.CreateDirectory("editor-capture");
        var editorCode = "Attribute VB_Name = \"Local\"\nPublic Sub Test_Passes()\nEnd Sub\n";
        File.WriteAllText(Path.Combine(snapshot, "Local.bas"), editorCode, Encoding.UTF8);
        var automation = new RecordingSourceWorkbookAutomation();
        var application = VbaDevCommandLine.Create(
            ToolingCompositionRoot.CreateApplicationComposition(root,
                sourceWorkbookAutomation: automation,
                projectSemanticInputProvider: FakeProjectSemanticInputProvider.Empty));

        var result = await application.RunAsync(["test", "--source-snapshot", snapshot, "--format", "ndjson"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([sourceWorkbook], automation.OpenedPaths);
        Assert.Equal(["import:Local.bas", "test"], automation.Session.Events);
        Assert.Contains("Test_Passes", Assert.Single(automation.Session.ImportedSourceTexts), StringComparison.Ordinal);
        Assert.DoesNotContain("Test_Disk", Assert.Single(automation.Session.ImportedSourceTexts), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(root, "bin")));
        Assert.Equal(diskCode, File.ReadAllText(Path.Combine(sourceSet, "Local.bas")));
        Assert.Equal(editorCode, File.ReadAllText(Path.Combine(snapshot, "Local.bas")));
        Assert.Equal("source workbook", File.ReadAllText(sourceWorkbook));
        Assert.Contains("\"procedure\":\"Test_Passes\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(new Uri(Path.Combine(sourceSet, "Local.bas")).AbsoluteUri, result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(snapshot, result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrdinaryTestImportsSavedSourcesAndRunsTheExactSourceWithoutSavingOrGeneratingBin()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = Path.Combine(root, "src", "Book1");
        Directory.CreateDirectory(sourceSet);
        var sourceWorkbook = Path.Combine(sourceSet, "Book1.xlsm");
        var sourceBytes = Encoding.UTF8.GetBytes("saved source workbook");
        File.WriteAllBytes(sourceWorkbook, sourceBytes);
        File.WriteAllText(Path.Combine(sourceSet, "Local.bas"),
            "Attribute VB_Name = \"Local\"\nPublic Sub Test_Passes()\nEnd Sub\n", Encoding.UTF8);
        var binWorkbook = Path.Combine(root, "bin", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(binWorkbook)!);
        var binBytes = Encoding.UTF8.GetBytes("legacy bin workbook");
        File.WriteAllBytes(binWorkbook, binBytes);
        var sourceAutomation = new RecordingSourceWorkbookAutomation();
        var generation = new FakeWorkbookGenerationAutomation();
        var legacyPathRunner = new FakeWorkbookTestRunner(
            new WorkbookTestResultRow("Local", "Test_Passes", "OK", ""));
        var application = VbaDevCommandLine.Create(
            ToolingCompositionRoot.CreateApplicationComposition(root,
                sourceWorkbookAutomation: sourceAutomation,
                workbookGenerationAutomation: generation,
                workbookTestRunner: legacyPathRunner,
                projectSemanticInputProvider: FakeProjectSemanticInputProvider.Empty));

        var result = await application.RunAsync(["test"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([sourceWorkbook], sourceAutomation.OpenedPaths);
        Assert.Empty(generation.OpenedWorkbooks);
        Assert.Empty(legacyPathRunner.Workbooks);
        Assert.Equal(["import:Local.bas", "test"], sourceAutomation.Session.Events);
        Assert.Equal(sourceBytes, File.ReadAllBytes(sourceWorkbook));
        Assert.Equal(binBytes, File.ReadAllBytes(binWorkbook));
        Assert.Contains("1 passed", result.StandardOutput, StringComparison.Ordinal);
    }
}

internal sealed class SourceTestFixture : IDisposable
{
    private readonly TempDirectory temp = TempDirectory.Create();

    internal SourceTestFixture()
    {
        Root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(Root,
            ProjectManifest.CreateDefault("Project", "Book1", Root, null));
        var sourceSet = temp.CreateDirectory(Path.Combine("Project", "src", "Book1"));
        WorkbookPath = Path.Combine(sourceSet, "Book1.xlsm");
        SourcePath = Path.Combine(sourceSet, "Local.bas");
        File.WriteAllText(WorkbookPath, "source workbook", Encoding.UTF8);
        File.WriteAllText(SourcePath, "Attribute VB_Name = \"Local\"\nPublic Sub Test_Passes()\nEnd Sub\n", Encoding.UTF8);
        Application = VbaDevCommandLine.Create(
            ToolingCompositionRoot.CreateApplicationComposition(Root,
                sourceWorkbookAutomation: Automation,
                workbookGenerationAutomation: Generation,
                projectSemanticInputProvider: FakeProjectSemanticInputProvider.Empty));
    }

    internal string Root { get; }
    internal string WorkbookPath { get; }
    internal string SourcePath { get; }
    internal RecordingSourceWorkbookAutomation Automation { get; } = new();
    internal FakeWorkbookGenerationAutomation Generation { get; } = new();
    internal VbaDevCommandLine Application { get; }
    public void Dispose() => temp.Dispose();
}
