using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Debugging;

namespace VbaDev.Infrastructure.Workbooks;

/// <summary>Runs debug preparation only against an already-open source workbook.</summary>
public sealed class ExcelComDebugSourceWorkbookAutomation : ISourceWorkbookAutomation
{
    private readonly ExcelComSourceWorkbookAutomation automation;

    public ExcelComDebugSourceWorkbookAutomation(
        int expectedExcelProcessId,
        long expectedExcelProcessStartUtcTicks)
        : this(expectedExcelProcessId, expectedExcelProcessStartUtcTicks,
            new StaComDispatcherFactory(), new WindowsSourceWorkbookOpenLocator())
    {
    }

    internal ExcelComDebugSourceWorkbookAutomation(
        int expectedExcelProcessId,
        long expectedExcelProcessStartUtcTicks,
        IStaComDispatcherFactory dispatcherFactory,
        ISourceWorkbookOpenLocator openLocator)
    {
        if (expectedExcelProcessId <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedExcelProcessId));
        if (expectedExcelProcessStartUtcTicks <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedExcelProcessStartUtcTicks));

        automation = new ExcelComSourceWorkbookAutomation(
            dispatcherFactory,
            new ExpectedProcessLocator(openLocator, expectedExcelProcessId,
                expectedExcelProcessStartUtcTicks),
            new MissingLiveSourceWorkbookAutomation(),
            validateSelectedWorkbookBeforeOperations: true);
    }

    public Task<TResult> RunAsync<TResult>(
        string workbookPath,
        WorkbookAutomationTimeouts timeouts,
        Func<ISourceWorkbookSession, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
        => automation.RunAsync(workbookPath, timeouts, operation, cancellationToken);

    private sealed class ExpectedProcessLocator(
        ISourceWorkbookOpenLocator inner,
        int expectedProcessId,
        long expectedProcessStartUtcTicks) : ISourceWorkbookOpenLocator
    {
        public SourceWorkbookBorrowedBinding? TryAttach(string workbookPath)
        {
            var binding = inner.TryAttach(workbookPath);
            if (binding is null) return null;
            if (binding.ProcessId == expectedProcessId
                && binding.ProcessStartUtcTicks == expectedProcessStartUtcTicks)
                return binding;

            binding.Dispose();
            throw new InvalidOperationException(
                $"The selected source workbook is not open in the requested Excel process " +
                $"{expectedProcessId} with the requested process start time.");
        }
    }

    private sealed class MissingLiveSourceWorkbookAutomation : ISourceWorkbookAutomation
    {
        public Task<TResult> RunAsync<TResult>(
            string workbookPath,
            WorkbookAutomationTimeouts timeouts,
            Func<ISourceWorkbookSession, CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException(
                $"The selected source workbook is not already open for debug preparation: {workbookPath}");
    }
}
