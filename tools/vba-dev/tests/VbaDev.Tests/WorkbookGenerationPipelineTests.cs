using System.Text;
using VbaDev.App.Build;
using VbaDev.App.References;
using VbaDev.App.Workbooks;
using Xunit;

namespace VbaDev.Tests;

public sealed class WorkbookGenerationPipelineTests
{
    [Fact]
    public async Task GenerationUsesOneOwnedSessionAndCommitsOnlyAfterCleanupIsProved()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Template.xlsm");
        var targetPath = Path.Combine(temp.Path, "bin", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.WriteAllText(templatePath, "new-workbook", Encoding.UTF8);
        File.WriteAllText(targetPath, "previous-workbook", Encoding.UTF8);
        var events = new List<string>();
        var automation = new RecordingWorkbookGenerationAutomation(events)
        {
            BeforeReturn = () =>
            {
                Assert.Equal("previous-workbook", File.ReadAllText(targetPath, Encoding.UTF8));
                events.Add("cleanup-proved");
            }
        };
        var timeouts = new WorkbookAutomationTimeouts(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(6));
        var pipeline = CreatePipeline(automation);

        await pipeline.GenerateAsync(
            "Book1",
            templatePath,
            targetPath,
            [],
            [],
            timeouts,
            CancellationToken.None);

        Assert.Same(timeouts, automation.Timeouts);
        Assert.Equal("new-workbook", File.ReadAllText(targetPath, Encoding.UTF8));
        Assert.Equal(
            ["open", "get-references", "get-modules", "verify", "save", "cleanup-proved"],
            events);
    }

    [Fact]
    public async Task CancellationBeforeCommitIdentifiesOutputCommitAndPreservesPreviousOutput()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Template.xlsm");
        var targetPath = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(templatePath, "new-workbook", Encoding.UTF8);
        File.WriteAllText(targetPath, "previous-workbook", Encoding.UTF8);
        using var cancellation = new CancellationTokenSource();
        var automation = new RecordingWorkbookGenerationAutomation([])
        {
            BeforeReturn = cancellation.Cancel
        };
        var pipeline = CreatePipeline(automation);

        var error = await Assert.ThrowsAsync<WorkbookAutomationCanceledException>(() =>
            pipeline.GenerateAsync(
                "Book1",
                templatePath,
                targetPath,
                [],
                [],
                WorkbookAutomationTimeouts.Default,
                cancellation.Token));

        Assert.Equal(WorkbookAutomationStageKind.OutputCommit, error.Stage.Kind);
        Assert.Equal("previous-workbook", File.ReadAllText(targetPath, Encoding.UTF8));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, ".Book1.*.tmp.xlsm"));
    }

    [Fact]
    public async Task CancellationThatArrivesInsideSuccessfulCommitDoesNotOverrideSuccess()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Template.xlsm");
        var targetPath = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(templatePath, "new-workbook", Encoding.UTF8);
        File.WriteAllText(targetPath, "previous-workbook", Encoding.UTF8);
        using var cancellation = new CancellationTokenSource();
        var transactionFactory = new CancelAfterCommitTransactionFactory(cancellation);
        var pipeline = CreatePipeline(
            new RecordingWorkbookGenerationAutomation([]),
            transactionFactory);

        var result = await pipeline.GenerateAsync(
            "Book1",
            templatePath,
            targetPath,
            [],
            [],
            WorkbookAutomationTimeouts.Default,
            cancellation.Token);

        Assert.Empty(result.Warnings);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal("new-workbook", File.ReadAllText(targetPath, Encoding.UTF8));
    }

    [Fact]
    public async Task CleanupFailureRetainsTheStageSpecificOperationFailure()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Template.xlsm");
        var targetPath = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(templatePath, "new-workbook", Encoding.UTF8);
        File.WriteAllText(targetPath, "previous-workbook", Encoding.UTF8);
        var timeout = new WorkbookAutomationTimeoutException(
            new WorkbookAutomationStage(
                WorkbookAutomationStageKind.ModuleImport,
                "Feature.bas"),
            TimeSpan.FromSeconds(30));
        var pipeline = CreatePipeline(
            new ThrowingWorkbookGenerationAutomation(timeout),
            new CleanupFailureTransactionFactory());

        var error = await Assert.ThrowsAsync<BuildCommandException>(() => pipeline.GenerateAsync(
            "Book1",
            templatePath,
            targetPath,
            [],
            [],
            WorkbookAutomationTimeouts.Default,
            CancellationToken.None));

        Assert.Contains("module import 'Feature.bas'", error.Message, StringComparison.Ordinal);
        Assert.Contains("retained staging", error.Message, StringComparison.Ordinal);
        var aggregate = Assert.IsType<AggregateException>(error.InnerException);
        Assert.Same(timeout, aggregate.InnerExceptions[0]);
        Assert.Equal("previous-workbook", File.ReadAllText(targetPath, Encoding.UTF8));
    }

    [Fact]
    public async Task LossySourceFailsBeforeOwnedExcelOrOutputStagingStarts()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Template.xlsm");
        var targetPath = Path.Combine(temp.Path, "Book1.xlsm");
        var sourcePath = Path.Combine(temp.Path, "Lossy.bas");
        File.WriteAllText(templatePath, "new-workbook", Encoding.UTF8);
        File.WriteAllText(targetPath, "previous-workbook", Encoding.UTF8);
        const string sourceText = "Attribute VB_Name = \"Lossy\"\r\nPublic Const Minus As String = \"−\"\r\n";
        var sourceBytes = new UTF8Encoding(false, true).GetBytes(sourceText);
        File.WriteAllBytes(sourcePath, sourceBytes);
        var events = new List<string>();
        var codePageReads = 0;
        var pipeline = CreatePipeline(
            new RecordingWorkbookGenerationAutomation(events),
            importSourceSetFactory: new VbeImportSourceSetFactory(() =>
            {
                codePageReads++;
                return 1252;
            }));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.GenerateAsync(
            "Book1",
            templatePath,
            targetPath,
            [],
            [new VbaSourceFile(sourcePath, VbaSourceKind.StandardModule, null)],
            WorkbookAutomationTimeouts.Default,
            CancellationToken.None));

        Assert.Contains("Windows code page 1252", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, codePageReads);
        Assert.Empty(events);
        Assert.Equal(sourceBytes, File.ReadAllBytes(sourcePath));
        Assert.Equal("previous-workbook", File.ReadAllText(targetPath, Encoding.UTF8));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, ".Book1.*.tmp.xlsm"));
    }

    [Fact]
    public async Task DebugSnapshotTextMismatchFailsBeforeOwnedExcelOrOutputStagingStarts()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Template.xlsm");
        var targetPath = Path.Combine(temp.Path, "Book1.xlsm");
        var sourcePath = Path.Combine(temp.Path, "Module1.bas");
        File.WriteAllText(templatePath, "new-workbook", Encoding.UTF8);
        File.WriteAllText(targetPath, "previous-workbook", Encoding.UTF8);
        const string sourceText = "Attribute VB_Name = \"Module1\"\r\nPublic Sub CurrentCode()\r\nEnd Sub\r\n";
        var sourceBytes = new UTF8Encoding(false, true).GetBytes(sourceText);
        File.WriteAllBytes(sourcePath, sourceBytes);
        var events = new List<string>();
        var codePageReads = 0;
        var pipeline = CreatePipeline(
            new RecordingWorkbookGenerationAutomation(events),
            importSourceSetFactory: new VbeImportSourceSetFactory(() =>
            {
                codePageReads++;
                return 65001;
            }));
        var source = new VbaSourceFile(sourcePath, VbaSourceKind.StandardModule, null)
        {
            ExpectedUnicodeText = sourceText.Replace("CurrentCode", "StaleCode", StringComparison.Ordinal),
            ExpectedUnicodeTextSourcePath = sourcePath
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.GenerateAsync(
            "Book1",
            templatePath,
            targetPath,
            [],
            [source],
            WorkbookAutomationTimeouts.Default,
            CancellationToken.None));

        Assert.Contains("snapshot", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(sourcePath, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, codePageReads);
        Assert.Empty(events);
        Assert.Equal(sourceBytes, File.ReadAllBytes(sourcePath));
        Assert.Equal("previous-workbook", File.ReadAllText(targetPath, Encoding.UTF8));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, ".Book1.*.tmp.xlsm"));
    }

    [Fact]
    public async Task ImportMirrorCleanupFailurePreventsFinalOutputCommit()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Template.xlsm");
        var targetPath = Path.Combine(temp.Path, "Book1.xlsm");
        var sourcePath = Path.Combine(temp.Path, "Module1.bas");
        File.WriteAllText(templatePath, "new-workbook", Encoding.UTF8);
        File.WriteAllText(targetPath, "previous-workbook", Encoding.UTF8);
        File.WriteAllText(
            sourcePath,
            "Attribute VB_Name = \"Module1\"\r\n",
            new UTF8Encoding(false));
        FileStream? stagingLock = null;
        string? importStagingPath = null;
        var events = new List<string>();
        var automation = new RecordingWorkbookGenerationAutomation(events)
        {
            OnImport = source =>
            {
                importStagingPath = Path.GetDirectoryName(source.SourcePath);
                stagingLock = File.Open(source.SourcePath, FileMode.Open, FileAccess.Read, FileShare.None);
            }
        };
        var pipeline = CreatePipeline(automation);

        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.GenerateAsync(
                "Book1",
                templatePath,
                targetPath,
                [],
                [new VbaSourceFile(sourcePath, VbaSourceKind.StandardModule, null)],
                WorkbookAutomationTimeouts.Default,
                CancellationToken.None));

            Assert.Contains("could not be removed", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("save", events);
            Assert.Equal("previous-workbook", File.ReadAllText(targetPath, Encoding.UTF8));
        }
        finally
        {
            stagingLock?.Dispose();
            if (importStagingPath is not null && Directory.Exists(importStagingPath))
            {
                Directory.Delete(importStagingPath, recursive: true);
            }
        }
    }

    private static WorkbookGenerationPipeline CreatePipeline(
        IWorkbookGenerationAutomation automation,
        IWorkbookOutputTransactionFactory? transactionFactory = null,
        VbeImportSourceSetFactory? importSourceSetFactory = null)
        => new(
            automation,
            new WorkbookReferenceNormalizer(
                new VbaProjectReferencePlanner(new FakeVbaProjectReferenceResolver())),
            transactionFactory ?? new WorkbookOutputTransactionFactory(),
            importSourceSetFactory ?? new VbeImportSourceSetFactory(() => 65001));

    private sealed class RecordingWorkbookGenerationAutomation(
        List<string> events) : IWorkbookGenerationAutomation
    {
        public Action? BeforeReturn { get; init; }

        public Action<VbeImportSourceFile>? OnImport { get; init; }

        public WorkbookAutomationTimeouts? Timeouts { get; private set; }

        public async Task<TResult> RunAsync<TResult>(
            string workbookPath,
            WorkbookAutomationTimeouts timeouts,
            Func<IWorkbookGenerationSession, CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken)
        {
            events.Add("open");
            Timeouts = timeouts;
            var result = await operation(
                new RecordingWorkbookGenerationSession(events, OnImport),
                cancellationToken);
            BeforeReturn?.Invoke();
            return result;
        }
    }

    private sealed class RecordingWorkbookGenerationSession(
        List<string> events,
        Action<VbeImportSourceFile>? onImport = null) : IWorkbookGenerationSession
    {
        public Task<IReadOnlyList<WorkbookModule>> GetModulesAsync(CancellationToken cancellationToken)
        {
            events.Add("get-modules");
            return Task.FromResult<IReadOnlyList<WorkbookModule>>([]);
        }

        public Task<IReadOnlyList<WorkbookReference>> GetReferencesAsync(CancellationToken cancellationToken)
        {
            events.Add("get-references");
            return Task.FromResult<IReadOnlyList<WorkbookReference>>([]);
        }

        public Task<bool> RemoveReferenceAsync(string referenceName, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task AddReferenceAsync(
            ResolvedVbaProjectReference reference,
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task RemoveModuleAsync(string moduleName, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task ImportModuleAsync(VbeImportSourceFile sourceFile, CancellationToken cancellationToken)
        {
            onImport?.Invoke(sourceFile);
            return Task.CompletedTask;
        }

        public Task VerifyAsync(CancellationToken cancellationToken)
        {
            events.Add("verify");
            return Task.CompletedTask;
        }

        public Task SaveAsync(CancellationToken cancellationToken)
        {
            events.Add("save");
            return Task.CompletedTask;
        }
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

    private sealed class ThrowingWorkbookGenerationAutomation(
        Exception error) : IWorkbookGenerationAutomation
    {
        public Task<TResult> RunAsync<TResult>(
            string workbookPath,
            WorkbookAutomationTimeouts timeouts,
            Func<IWorkbookGenerationSession, CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken)
            => Task.FromException<TResult>(error);
    }

    private sealed class CleanupFailureTransactionFactory : IWorkbookOutputTransactionFactory
    {
        public IWorkbookOutputTransaction Create(
            string templateWorkbookPath,
            string targetWorkbookPath)
            => new CleanupFailureTransaction(
                WorkbookOutputTransaction.Create(templateWorkbookPath, targetWorkbookPath));
    }

    private sealed class CleanupFailureTransaction(
        WorkbookOutputTransaction inner) : IWorkbookOutputTransaction
    {
        public string StagingWorkbookPath => inner.StagingWorkbookPath;

        public void Commit() => inner.Commit();

        public void Dispose()
            => throw new BuildCommandException("retained staging requires manual cleanup");
    }
}
