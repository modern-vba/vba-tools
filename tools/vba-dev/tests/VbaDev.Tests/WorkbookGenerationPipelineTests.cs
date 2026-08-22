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

    private static WorkbookGenerationPipeline CreatePipeline(
        IWorkbookGenerationAutomation automation,
        IWorkbookOutputTransactionFactory? transactionFactory = null)
        => new(
            automation,
            new WorkbookReferenceNormalizer(
                new VbaProjectReferencePlanner(new FakeVbaProjectReferenceResolver())),
            transactionFactory ?? new WorkbookOutputTransactionFactory());

    private sealed class RecordingWorkbookGenerationAutomation(
        List<string> events) : IWorkbookGenerationAutomation
    {
        public Action? BeforeReturn { get; init; }

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
                new RecordingWorkbookGenerationSession(events),
                cancellationToken);
            BeforeReturn?.Invoke();
            return result;
        }
    }

    private sealed class RecordingWorkbookGenerationSession(
        List<string> events) : IWorkbookGenerationSession
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

        public Task ImportModuleAsync(VbaSourceFile sourceFile, CancellationToken cancellationToken)
            => Task.CompletedTask;

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
