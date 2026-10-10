using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Debugging;
using VbaDev.Infrastructure.Workbooks;
using Xunit;

namespace VbaDev.Tests;

public sealed class ExcelComDebugSourceWorkbookAutomationTests
{
    [Fact]
    public async Task MissingLiveSourceWorkbookIsRejectedWithoutStartingDebugPreparation()
    {
        var locator = new RecordingLocator(null);
        var automation = new ExcelComDebugSourceWorkbookAutomation(
            expectedExcelProcessId: 42,
            expectedExcelProcessStartUtcTicks: 638955648000000000,
            new StaComDispatcherFactory(), locator);
        var callbackStarted = false;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => automation.RunAsync(
            "C:\\project\\Book1.xlsm", WorkbookAutomationTimeouts.Default,
            (_, _) =>
            {
                callbackStarted = true;
                return Task.FromResult(true);
            }, CancellationToken.None));

        Assert.Contains("already open", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(callbackStarted);
        Assert.Equal("C:\\project\\Book1.xlsm", locator.SelectedPath);
    }

    [Fact]
    public async Task DifferentExcelProcessIsRejectedBeforeDebugPreparation()
    {
        var releases = 0;
        var binding = new SourceWorkbookBorrowedBinding(
            null!, () => true, () => releases++,
            processId: 43, processStartUtcTicks: 638955648000000000);
        var automation = new ExcelComDebugSourceWorkbookAutomation(
            expectedExcelProcessId: 42,
            expectedExcelProcessStartUtcTicks: 638955648000000000,
            new StaComDispatcherFactory(), new RecordingLocator(binding));
        var callbackStarted = false;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => automation.RunAsync(
            "C:\\project\\Book1.xlsm", WorkbookAutomationTimeouts.Default,
            (_, _) =>
            {
                callbackStarted = true;
                return Task.FromResult(true);
            }, CancellationToken.None));

        Assert.Contains("Excel process", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(callbackStarted);
        Assert.Equal(1, releases);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(638955648000000001L)]
    public async Task UnverifiedOrDifferentProcessStartIsRejectedBeforeDebugPreparation(
        long? observedStartUtcTicks)
    {
        var releases = 0;
        var binding = new SourceWorkbookBorrowedBinding(
            null!, () => true, () => releases++,
            processId: 42, processStartUtcTicks: observedStartUtcTicks);
        var automation = new ExcelComDebugSourceWorkbookAutomation(
            expectedExcelProcessId: 42,
            expectedExcelProcessStartUtcTicks: 638955648000000000,
            new StaComDispatcherFactory(), new RecordingLocator(binding));
        var callbackStarted = false;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => automation.RunAsync(
            "C:\\project\\Book1.xlsm", WorkbookAutomationTimeouts.Default,
            (_, _) =>
            {
                callbackStarted = true;
                return Task.FromResult(true);
            }, CancellationToken.None));

        Assert.Contains("process start time", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(callbackStarted);
        Assert.Equal(1, releases);
    }

    [Fact]
    public async Task MatchingProcessAndWorkbookBindingRunsPreparationAndReleasesOnlyItsComHandle()
    {
        var workbook = new RecordingWorkbook();
        var releases = 0;
        var binding = new SourceWorkbookBorrowedBinding(
            workbook, () => true, () => releases++, isStillSelected: () => true,
            processId: 42, processStartUtcTicks: 638955648000000000);
        var automation = new ExcelComDebugSourceWorkbookAutomation(
            expectedExcelProcessId: 42,
            expectedExcelProcessStartUtcTicks: 638955648000000000,
            new StaComDispatcherFactory(), new RecordingLocator(binding));

        var projectName = await automation.RunAsync(
            "C:\\project\\Book1.xlsm", WorkbookAutomationTimeouts.Default,
            async (session, token) =>
            {
                Assert.True(session.WasAlreadyOpen);
                return await session.GetProjectNameAsync(token);
            }, CancellationToken.None);

        Assert.Equal("VbaProject", projectName);
        Assert.Equal(1, releases);
    }

    [Fact]
    public async Task OriginalWorkbookChangingIdentityStopsMutationBeforeItStarts()
    {
        var workbook = new RecordingWorkbook();
        var selected = true;
        var releases = 0;
        var binding = new SourceWorkbookBorrowedBinding(
            workbook, () => true, () => releases++,
            isStillSelected: () => selected,
            processId: 42, processStartUtcTicks: 638955648000000000);
        var automation = new ExcelComDebugSourceWorkbookAutomation(
            expectedExcelProcessId: 42,
            expectedExcelProcessStartUtcTicks: 638955648000000000,
            new StaComDispatcherFactory(), new RecordingLocator(binding));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => automation.RunAsync(
            "C:\\project\\Book1.xlsm", WorkbookAutomationTimeouts.Default,
            async (session, token) =>
            {
                selected = false;
                await session.RemoveModuleAsync("Victim", token);
                return true;
            }, CancellationToken.None));

        Assert.Contains("selected source workbook", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, workbook.RemovedModules);
        Assert.Equal(1, releases);
    }

    private sealed class RecordingLocator(SourceWorkbookBorrowedBinding? binding)
        : ISourceWorkbookOpenLocator
    {
        public string? SelectedPath { get; private set; }

        public SourceWorkbookBorrowedBinding? TryAttach(string workbookPath)
        {
            SelectedPath = workbookPath;
            return binding;
        }
    }

    private sealed class RecordingWorkbook : IWorkbookBuildSession
    {
        public int RemovedModules { get; private set; }

        public string GetProjectName() => "VbaProject";
        public IReadOnlyList<WorkbookModule> GetModules() => [];
        public IReadOnlyList<WorkbookReference> GetReferences() => [];
        public bool RemoveReference(string referenceName) => false;
        public void AddReference(ResolvedVbaProjectReference reference) { }
        public void RemoveModule(string moduleName) => RemovedModules++;
        public void ImportModule(VbeImportSourceFile sourceFile) { }
        public VbeImportVerificationReport VerifyImportedModules() => VbeImportVerificationReport.Empty;
        public void Save() { }
    }
}
