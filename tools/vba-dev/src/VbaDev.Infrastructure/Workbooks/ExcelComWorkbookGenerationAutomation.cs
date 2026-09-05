using VbaDev.App.Testing;
using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Debugging;

namespace VbaDev.Infrastructure.Workbooks;

public sealed class ExcelComWorkbookGenerationAutomation : IWorkbookGenerationAutomation
{
    private readonly AutomationExcelProcessRuntime generationRuntime;

    /// <summary>
    /// Creates the production Excel COM workbook automation adapter.
    /// </summary>
    public ExcelComWorkbookGenerationAutomation()
        : this(new AutomationExcelProcessRuntime())
    {
    }

    internal ExcelComWorkbookGenerationAutomation(
        IStaComDispatcherFactory generationDispatcherFactory,
        IExcelComWorkbookGenerationLifecycle generationLifecycle)
        : this(new AutomationExcelProcessRuntime(
            generationDispatcherFactory,
            generationLifecycle))
    {
    }

    private ExcelComWorkbookGenerationAutomation(AutomationExcelProcessRuntime generationRuntime)
    {
        this.generationRuntime = generationRuntime;
    }

    /// <inheritdoc />
    public async Task<TResult> RunAsync<TResult>(
        string workbookPath,
        WorkbookAutomationTimeouts timeouts,
        Func<IWorkbookGenerationSession, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        var outcome = await generationRuntime.RunWorkbookAsync(
            workbookPath,
            timeouts,
            operation,
            cancellationToken).ConfigureAwait(false);
        return CompleteGeneration(outcome, cancellationToken);
    }

    internal async Task<IReadOnlyList<WorkbookTestResultRow>> RunWorkbookTestsAsync(
        string workbookPath,
        WorkbookAutomationTimeouts timeouts,
        TimeSpan executionTimeout,
        WorkbookTestSelector selector,
        CancellationToken cancellationToken)
    {
        var outcome = await generationRuntime.RunWorkbookTestsAsync(
            workbookPath,
            timeouts,
            executionTimeout,
            selector,
            cancellationToken).ConfigureAwait(false);
        return CompleteGeneration(outcome, cancellationToken);
    }

    private static TResult CompleteGeneration<TResult>(
        AutomationExcelProcessOutcome<TResult> outcome,
        CancellationToken cancellationToken)
    {
        // The adapter preserves generation's pre-commit cancellation policy.
        // Other scenarios may preserve an already committed result using the
        // same released evidence without changing runtime lifetime policy.
        var result = outcome.GetReleasedResult();
        if (outcome.Evidence.CancellationRequestedDuringCleanup
            || cancellationToken.IsCancellationRequested)
        {
            throw new WorkbookAutomationCanceledException(
                new WorkbookAutomationStage(WorkbookAutomationStageKind.ProcessCleanup),
                cancellationToken,
                isolationDiagnostics: outcome.Evidence.IsolationDiagnostics);
        }

        return result;
    }
}
