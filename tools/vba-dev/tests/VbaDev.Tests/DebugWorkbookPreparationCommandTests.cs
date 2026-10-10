using System.Text;
using System.Text.Json;
using VbaDev.App.Build;
using VbaDev.App.DebugPreparation;
using VbaDev.App.Projects;
using VbaDev.App.References;
using VbaDev.App.Workbooks;
using VbaDev.Domain;
using VbaDev.Infrastructure.FileSystem;
using VbaDev.Infrastructure.Projects;
using VbaTools.Semantics;
using VbaTools.Syntax;
using Xunit;

namespace VbaDev.Tests;

public sealed class DebugWorkbookPreparationCommandTests
{
    [Fact]
    public async Task MalformedPreparationGenerationIsRejectedBeforeWorkbookAcquisition()
    {
        using var fixture = new PreparationFixture();
        var result = await fixture.Command.RunAsync(fixture.Context,
            fixture.Request with { GenerationId = "unbound-generation" }, null,
            (_, _) => Task.FromResult(true), CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("32 lowercase hexadecimal", result.StandardError);
        Assert.Empty(fixture.Automation.OpenedPaths);
        Assert.Empty(fixture.Automation.Session.Events);
    }

    [Fact]
    public async Task InvalidUnrelatedSourceReachesImportWithoutSavingTheExactSourceWorkbook()
    {
        using var fixture = new PreparationFixture();
        File.WriteAllText(Path.Combine(fixture.Snapshot, "Unrelated.bas"),
            "Attribute VB_Name = \"Unrelated\"\nPublic Sub Broken()\nDim value As\nEnd Sub\n", Encoding.UTF8);
        var originalBytes = File.ReadAllBytes(fixture.WorkbookPath);
        DebugWorkbookPreparationReady? observedReady = null;

        var result = await fixture.Command.RunAsync(fixture.Context, fixture.Request,
            null, (ready, _) =>
            {
                observedReady = ready;
                Assert.Equal(new[] { "export:Existing" }, fixture.Automation.Session.Events);
                return Task.FromResult(true);
            }, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new DebugWorkbookPreparationReady(fixture.Request.GenerationId,
            fixture.WorkbookPath, fixture.Request.ExcelProcessId,
            fixture.Request.ExcelProcessStartUtcTicks), observedReady);
        Assert.Equal(fixture.WorkbookPath, Assert.Single(fixture.Automation.OpenedPaths));
        Assert.Contains("import:Boot.bas", fixture.Automation.Session.Events);
        Assert.Contains("import:Unrelated.bas", fixture.Automation.Session.Events);
        Assert.DoesNotContain("save", fixture.Automation.Session.Events);
        Assert.Equal(originalBytes, File.ReadAllBytes(fixture.WorkbookPath));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "bin")));
        using var receipt = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal("1.0", receipt.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal(fixture.WorkbookPath, receipt.RootElement.GetProperty("workbookPath").GetString());
        Assert.Equal(fixture.Request.GenerationId, receipt.RootElement.GetProperty("generationId").GetString());
        Assert.Equal(2, receipt.RootElement.GetProperty("importedSourceFileCount").GetInt32());
    }

    [Fact]
    public async Task FailedPreReplacementCapturePreservesBorrowedWorkbookAndReportsOriginalFailure()
    {
        using var fixture = new PreparationFixture();
        fixture.Automation.Session.ThrowOnExportOnce = "Existing";
        var originalModules = fixture.Automation.Session.Modules.ToArray();
        var savedBytes = File.ReadAllBytes(fixture.WorkbookPath);
        var snapshotPath = Path.Combine(fixture.Snapshot, "Boot.bas");
        var snapshotBytes = File.ReadAllBytes(snapshotPath);
        var continuationRequested = false;

        var result = await fixture.Command.RunAsync(fixture.Context, fixture.Request,
            null, (_, _) =>
            {
                continuationRequested = true;
                return Task.FromResult(true);
            }, CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("Simulated recovery capture failure", result.StandardError);
        Assert.False(continuationRequested);
        Assert.Equal(originalModules, fixture.Automation.Session.Modules);
        Assert.Equal(new[] { "export:Existing" }, fixture.Automation.Session.Events);
        Assert.Equal(SourceWorkbookSaveState.NotStarted, fixture.Automation.Session.SaveState);
        Assert.Equal(savedBytes, File.ReadAllBytes(fixture.WorkbookPath));
        Assert.Equal(snapshotBytes, File.ReadAllBytes(snapshotPath));
    }

    [Fact]
    public async Task CancellationAfterCaptureDoesNotRequestResetReadinessOrReplaceBorrowedCode()
    {
        using var fixture = new PreparationFixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Automation.Session.AfterExport = cancellation.Cancel;
        var continuationRequested = false;

        var result = await fixture.Command.RunAsync(fixture.Context, fixture.Request,
            null, (_, _) =>
            {
                continuationRequested = true;
                return Task.FromResult(true);
            }, cancellation.Token);

        Assert.Equal(130, result.ExitCode);
        Assert.False(continuationRequested);
        Assert.Equal(new[] { "ThisWorkbook", "Existing" },
            fixture.Automation.Session.Modules.Select(module => module.Name));
        Assert.Equal(new[] { "export:Existing" }, fixture.Automation.Session.Events);
    }

    [Fact]
    public async Task DecliningDirtyWorkbookPreservesCodeWithoutCaptureOrResetReadiness()
    {
        using var fixture = new PreparationFixture();
        fixture.Automation.Session.IsSaved = false;
        var continuationRequested = false;
        string? warning = null;

        var result = await fixture.Command.RunAsync(fixture.Context, fixture.Request,
            (message, _) =>
            {
                warning = message;
                return Task.FromResult(false);
            }, (_, _) =>
            {
                continuationRequested = true;
                return Task.FromResult(true);
            }, CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("will not be saved or discarded", warning);
        Assert.Contains("declined; no VBA code was replaced", result.StandardError);
        Assert.False(continuationRequested);
        Assert.Empty(fixture.Automation.Session.Events);
        Assert.Equal(new[] { "ThisWorkbook", "Existing" },
            fixture.Automation.Session.Modules.Select(module => module.Name));
    }

    [Fact]
    public async Task DecliningCapturedReadinessPreservesTheCurrentWorkbookWithoutReplacement()
    {
        using var fixture = new PreparationFixture();

        var result = await fixture.Command.RunAsync(fixture.Context, fixture.Request,
            null, (_, _) => Task.FromResult(false), CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("readiness was declined; no VBA code was replaced", result.StandardError);
        Assert.Equal(new[] { "export:Existing" }, fixture.Automation.Session.Events);
        Assert.Equal(new[] { "ThisWorkbook", "Existing" },
            fixture.Automation.Session.Modules.Select(module => module.Name));
        Assert.Equal(SourceWorkbookSaveState.NotStarted, fixture.Automation.Session.SaveState);
    }

    [Fact]
    public async Task ImportFailureRestoresCapturedBorrowedCodeWithoutSaving()
    {
        using var fixture = new PreparationFixture();
        fixture.Automation.Session.ThrowOnImportOnce = "Boot";
        var savedBytes = File.ReadAllBytes(fixture.WorkbookPath);

        var result = await fixture.Command.RunAsync(fixture.Context, fixture.Request,
            null, (_, _) => Task.FromResult(true), CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Simulated import failure", result.StandardError);
        Assert.DoesNotContain("recovery was incomplete", result.StandardError);
        Assert.Equal(new[] { "ThisWorkbook", "Existing" },
            fixture.Automation.Session.Modules.Select(module => module.Name));
        Assert.DoesNotContain("save", fixture.Automation.Session.Events);
        Assert.Equal(SourceWorkbookSaveState.NotStarted, fixture.Automation.Session.SaveState);
        Assert.Equal(savedBytes, File.ReadAllBytes(fixture.WorkbookPath));
    }

    [Fact]
    public async Task FailedRecoveryImportReportsBothFailuresAndRetainsManualRecoveryFilesWithoutSaving()
    {
        using var fixture = new PreparationFixture();
        fixture.Automation.Session.ThrowOnImportOnce = "Boot";
        fixture.Automation.Session.ThrowOnRecoveryImportOnce = "Existing";
        var savedBytes = File.ReadAllBytes(fixture.WorkbookPath);
        var snapshotPath = Path.Combine(fixture.Snapshot, "Boot.bas");
        var snapshotBytes = File.ReadAllBytes(snapshotPath);

        var result = await fixture.Command.RunAsync(fixture.Context, fixture.Request,
            null, (_, _) => Task.FromResult(true), CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("workbook recovery was incomplete", result.StandardError);
        Assert.Contains("Original failure: Simulated import failure.", result.StandardError);
        Assert.Contains("Recovery failure: Simulated recovery import failure.", result.StandardError);
        Assert.Contains("Restore modules manually from that directory, then inspect workbook references.",
            result.StandardError);
        var recoverySourcePath = Assert.IsType<string>(fixture.Automation.Session.LastImportedSourcePath);
        Assert.Equal("Existing.bas", Path.GetFileName(recoverySourcePath));
        var recoveryDirectory = Path.GetDirectoryName(recoverySourcePath)!;
        Assert.True(Path.IsPathFullyQualified(recoveryDirectory));
        Assert.Contains($"Recovery files: {recoveryDirectory}.", result.StandardError);
        Assert.True(Directory.Exists(recoveryDirectory));
        Assert.Equal(recoverySourcePath, Assert.Single(Directory.GetFiles(recoveryDirectory)));
        Assert.Equal("Attribute VB_Name = \"Existing\"", File.ReadAllText(recoverySourcePath));
        Assert.Equal(new[] { "ThisWorkbook" },
            fixture.Automation.Session.Modules.Select(module => module.Name));
        Assert.DoesNotContain("save", fixture.Automation.Session.Events);
        Assert.Equal(SourceWorkbookSaveState.NotStarted, fixture.Automation.Session.SaveState);
        Assert.Equal(savedBytes, File.ReadAllBytes(fixture.WorkbookPath));
        Assert.Equal(snapshotBytes, File.ReadAllBytes(snapshotPath));
    }

    private sealed class PreparationFixture : IDisposable
    {
        private readonly TempDirectory temp = TempDirectory.Create();

        internal PreparationFixture()
        {
            Root = temp.CreateDirectory("Project");
            new JsonProjectManifestStore().Save(Root,
                ProjectManifest.CreateDefault("Project", "Book1", Root, null));
            Context = new ProjectContextResolver(new JsonProjectManifestStore())
                .Resolve(new ProjectResolutionRequest(Root, null, Root));
            Directory.CreateDirectory(Context.DocumentSourceSetPath);
            WorkbookPath = Context.TemplateDocumentPath;
            File.WriteAllText(WorkbookPath, "saved workbook", Encoding.UTF8);
            Snapshot = temp.CreateDirectory("Snapshot");
            File.WriteAllText(Path.Combine(Snapshot, "Boot.bas"),
                "Attribute VB_Name = \"Boot\"\nPublic Sub Start()\nEnd Sub\n", Encoding.UTF8);
            Request = new(Snapshot, Root, Guid.NewGuid().ToString("N"),
                12345, DateTime.UtcNow.Ticks);
            Automation.Session.WasAlreadyOpen = true;
            Automation.Session.Modules.Add(new WorkbookModule("Existing", WorkbookModuleKind.StandardModule));
            var ownership = new WindowsExactFileSystemObjectOwnershipFactory();
            var references = new WorkbookReferenceNormalizer(
                new VbaProjectReferencePlanner(new FakeVbaProjectReferenceResolver()));
            var materializer = new WorkbookMaterializer(ownership,
                new VbaSourceAdmission(() => 932), new FakeWorkbookGenerationAutomation(),
                references, new WorkbookOutputTransactionFactory(ownership),
                new VbeImportSourceSetFactory(ownership),
                semanticInputProvider: new RejectUnexpectedSemanticAcquisition());
            Command = new DebugWorkbookPreparationCommand(materializer,
                (_, _) => Automation, references, ownership);
        }

        internal string Root { get; }
        internal string Snapshot { get; }
        internal string WorkbookPath { get; }
        internal ResolvedProjectContext Context { get; }
        internal DebugWorkbookPreparationRequest Request { get; }
        internal RecordingSourceWorkbookAutomation Automation { get; } = new();
        internal DebugWorkbookPreparationCommand Command { get; }

        public void Dispose() => temp.Dispose();
    }

    private sealed class RejectUnexpectedSemanticAcquisition : IProjectSemanticInputProvider
    {
        public Task<VbaProjectSemanticInputs> AcquireAsync(ResolvedProjectContext context,
            CapturedWorkbookTemplate template, IReadOnlyList<VbaSyntaxTree> sources,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("Debug preparation must not acquire semantic-quality inputs.");
    }
}
