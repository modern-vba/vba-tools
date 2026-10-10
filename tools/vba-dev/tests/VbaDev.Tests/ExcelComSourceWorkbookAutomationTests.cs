using VbaDev.App.Testing;
using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Debugging;
using VbaDev.Infrastructure.Workbooks;
using Xunit;

namespace VbaDev.Tests;

public sealed class ExcelComSourceWorkbookAutomationTests
{
    [Fact]
    public async Task TimedOutBorrowedTestRetainsLateReaderReleaseFailureAfterStaRetirement()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var readFailure = new InvalidDataException("Late result read failed");
        var readerFailure = new WorkbookAutomationComReferenceReleaseException(readFailure,
            [new("result cell", new InvalidOperationException("Late result-cell release failed"))]);
        var workbook = new RecordingWorkbookBuildSession
        {
            OnRunTests = () =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Fixture did not release the bounded Test call.");
                throw readerFailure;
            }
        };
        var binding = new SourceWorkbookBorrowedBinding(workbook, () => workbook.IsSaved,
            () => workbook.Releases++);
        var automation = new ExcelComSourceWorkbookAutomation(new StaComDispatcherFactory(),
            new RecordingSourceWorkbookOpenLocator(binding), new UnexpectedClosedSourceAutomation());
        WorkbookAutomationTimeoutException? originalTimeout = null;
        try
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() => automation.RunAsync(
                "C:\\project\\src\\Book1\\Book1.xlsm", WorkbookAutomationTimeouts.Default,
                async (session, token) =>
                {
                    var pending = ((IWorkbookTestExecutionSession)session).RunTestsAsync(
                        new WorkbookTestSelector("Test_Source", "Test_BoundWorkbook"),
                        TimeSpan.FromMilliseconds(50), token);
                    Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                    try { return await pending; }
                    catch (WorkbookAutomationTimeoutException timeout)
                    {
                        originalTimeout = timeout;
                        release.Set();
                        throw;
                    }
                }, CancellationToken.None));

            var facts = WorkbookAutomationTerminalFacts.Analyze(error);
            Assert.Contains(facts.Failures, failure => ReferenceEquals(failure.Error, readerFailure));
            Assert.NotNull(originalTimeout);
            Assert.Contains(facts.Failures, failure => ReferenceEquals(failure.Error, originalTimeout));
            Assert.StartsWith(originalTimeout.Message, error.Message, StringComparison.Ordinal);
            Assert.False(facts.ComReferenceReleaseProven);
            Assert.True(facts.ProcessReleaseProven);
            Assert.True(facts.DispatcherRetired);
            Assert.Equal(1, workbook.TestCalls);
            Assert.Equal(1, workbook.Releases);
            Assert.Equal(0, workbook.SaveCalls);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task BorrowedReferenceReleaseFailureDoesNotInventProcessOrDispatcherUncertainty()
    {
        var workbook = new RecordingWorkbookBuildSession();
        var releaseFailure = new WorkbookAutomationComReferenceReleaseException(null,
            [new("borrowed workbook", new InvalidOperationException("Workbook release failed"))]);
        var binding = new SourceWorkbookBorrowedBinding(workbook, () => workbook.IsSaved, () =>
        {
            workbook.Releases++;
            throw releaseFailure;
        });
        var automation = new ExcelComSourceWorkbookAutomation(new StaComDispatcherFactory(),
            new RecordingSourceWorkbookOpenLocator(binding), new UnexpectedClosedSourceAutomation());

        var error = await Assert.ThrowsAnyAsync<Exception>(() => automation.RunAsync(
            "C:\\project\\src\\Book1\\Book1.xlsm", WorkbookAutomationTimeouts.Default,
            async (session, token) => await ((IWorkbookTestExecutionSession)session).RunTestsAsync(
                new WorkbookTestSelector(), TimeSpan.FromSeconds(42), token), CancellationToken.None));

        var facts = WorkbookAutomationTerminalFacts.Analyze(error);
        Assert.True(facts.ProcessReleaseProven);
        Assert.True(facts.DispatcherRetired);
        Assert.False(facts.ComReferenceReleaseProven);
        Assert.False(facts.HasUnprovedLifecycle);
        Assert.Equal(WorkbookAutomationDisposition.Failed, facts.Disposition);
        Assert.Contains(facts.Failures, failure => ReferenceEquals(failure.Error, releaseFailure));
        Assert.Equal(1, workbook.TestCalls);
        Assert.Equal(1, workbook.Releases);
        Assert.Equal(0, workbook.SaveCalls);
    }

    [Fact]
    public async Task AlreadyOpenSourceTestsRunOnTheBoundWorkbookWithoutSavingOrReopening()
    {
        var workbook = new RecordingWorkbookBuildSession();
        var binding = new SourceWorkbookBorrowedBinding(workbook, () => workbook.IsSaved,
            () => workbook.Releases++);
        var locator = new RecordingSourceWorkbookOpenLocator(binding);
        var automation = new ExcelComSourceWorkbookAutomation(
            new StaComDispatcherFactory(), locator, new UnexpectedClosedSourceAutomation());
        var selector = new WorkbookTestSelector("Test_Source", "Test_BoundWorkbook");

        var rows = await automation.RunAsync(
            "C:\\project\\src\\Book1\\Book1.xlsm", WorkbookAutomationTimeouts.Default,
            async (session, token) =>
            {
                Assert.True(session.WasAlreadyOpen);
                var tests = Assert.IsAssignableFrom<IWorkbookTestExecutionSession>(session);
                return await tests.RunTestsAsync(selector, TimeSpan.FromSeconds(42), token);
            }, CancellationToken.None);

        Assert.Equal([new WorkbookTestResultRow("Test_Source", "Test_BoundWorkbook", "OK", "")], rows);
        Assert.Equal(1, workbook.TestCalls);
        Assert.Same(selector, workbook.TestSelector);
        Assert.Equal(0, workbook.SaveCalls);
        Assert.Equal(1, workbook.Releases);
        Assert.Equal("C:\\project\\src\\Book1\\Book1.xlsm", locator.SelectedPath);
    }

    [Fact]
    public async Task AlreadyOpenSourceWorkbookIsSavedWithoutClosingItsExcelSession()
    {
        var workbook = new RecordingWorkbookBuildSession();
        var binding = new SourceWorkbookBorrowedBinding(workbook, () => workbook.IsSaved, () => workbook.Releases++);
        var locator = new RecordingSourceWorkbookOpenLocator(binding);
        var automation = new ExcelComSourceWorkbookAutomation(
            new StaComDispatcherFactory(), locator, new UnexpectedClosedSourceAutomation());

        var saveState = await automation.RunAsync(
            "C:\\project\\Book1.xlsm", WorkbookAutomationTimeouts.Default,
            async (session, token) =>
            {
                Assert.True(session.WasAlreadyOpen);
                Assert.False(await session.IsSavedAsync(token));
                Assert.Equal(SourceWorkbookSaveState.NotStarted, session.SaveState);
                await session.SaveAsync(token);
                return session.SaveState;
            }, CancellationToken.None);

        Assert.Equal(SourceWorkbookSaveState.Saved, saveState);
        Assert.Equal(1, workbook.SaveCalls);
        Assert.Equal(1, workbook.Releases);
        Assert.Equal("C:\\project\\Book1.xlsm", locator.SelectedPath);
    }

    [Fact]
    public async Task NativeSaveFailureLeavesOutcomeUnknownAndUserSessionUntouched()
    {
        var workbook = new RecordingWorkbookBuildSession { FailSave = true };
        var binding = new SourceWorkbookBorrowedBinding(workbook, () => workbook.IsSaved, () => workbook.Releases++);
        var automation = new ExcelComSourceWorkbookAutomation(
            new StaComDispatcherFactory(), new RecordingSourceWorkbookOpenLocator(binding),
            new UnexpectedClosedSourceAutomation());

        var saveState = await automation.RunAsync(
            "C:\\project\\Book1.xlsm", WorkbookAutomationTimeouts.Default,
            async (session, token) =>
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.SaveAsync(token));
                Assert.Equal("native save failed", error.Message);
                return session.SaveState;
            }, CancellationToken.None);

        Assert.Equal(SourceWorkbookSaveState.Unknown, saveState);
        Assert.Equal(1, workbook.SaveCalls);
        Assert.Equal(1, workbook.Releases);
    }

    [Fact]
    public async Task NormallyReturningCanceledNativeSaveRemainsUnknownAndDoesNotRetry()
    {
        var workbook = new RecordingWorkbookBuildSession { CancelSaveWithoutThrowing = true };
        var binding = new SourceWorkbookBorrowedBinding(workbook, () => workbook.IsSaved, () => workbook.Releases++);
        var automation = new ExcelComSourceWorkbookAutomation(
            new StaComDispatcherFactory(), new RecordingSourceWorkbookOpenLocator(binding),
            new UnexpectedClosedSourceAutomation());

        var saveState = await automation.RunAsync(
            "C:\\project\\Book1.xlsm", WorkbookAutomationTimeouts.Default,
            async (session, token) =>
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.SaveAsync(token));
                Assert.Contains("remains unsaved", error.Message, StringComparison.OrdinalIgnoreCase);
                return session.SaveState;
            }, CancellationToken.None);

        Assert.Equal(SourceWorkbookSaveState.Unknown, saveState);
        Assert.Equal(1, workbook.SaveCalls);
        Assert.Equal(1, workbook.Releases);
        Assert.False(workbook.IsSaved);
    }

    [Fact]
    public async Task SaveAsPathChangeDoesNotProveTheSelectedSourceWasSaved()
    {
        var workbook = new RecordingWorkbookBuildSession();
        var binding = new SourceWorkbookBorrowedBinding(workbook, () => workbook.IsSaved,
            () => workbook.Releases++, isStillSelected: () => false);
        var automation = new ExcelComSourceWorkbookAutomation(
            new StaComDispatcherFactory(), new RecordingSourceWorkbookOpenLocator(binding),
            new UnexpectedClosedSourceAutomation());

        var saveState = await automation.RunAsync(
            "C:\\project\\Book1.xlsm", WorkbookAutomationTimeouts.Default,
            async (session, token) =>
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.SaveAsync(token));
                Assert.Contains("selected source workbook path", error.Message, StringComparison.OrdinalIgnoreCase);
                return session.SaveState;
            }, CancellationToken.None);

        Assert.Equal(SourceWorkbookSaveState.Unknown, saveState);
        Assert.Equal(1, workbook.SaveCalls);
        Assert.Equal(1, workbook.Releases);
    }

    [Fact]
    public async Task ClosedSourceWorkbookUsesCommandOwnedAutomation()
    {
        var closed = new RecordingClosedSourceAutomation();
        var automation = new ExcelComSourceWorkbookAutomation(
            new StaComDispatcherFactory(), new RecordingSourceWorkbookOpenLocator(null), closed);

        var sourcePath = "C:\\project\\Book1.xlsm";
        var selectedPath = await automation.RunAsync(
            sourcePath, WorkbookAutomationTimeouts.Default,
            (_, _) => Task.FromResult("operation"), CancellationToken.None);

        Assert.Equal(sourcePath, selectedPath);
        Assert.Equal(1, closed.Calls);
    }

    [Fact]
    public async Task CancellationDuringNativeSaveDoesNotEraseKnownSavedOutcome()
    {
        using var cancellation = new CancellationTokenSource();
        var workbook = new RecordingWorkbookBuildSession { OnSave = cancellation.Cancel };
        var binding = new SourceWorkbookBorrowedBinding(workbook, () => workbook.IsSaved, () => workbook.Releases++);
        var automation = new ExcelComSourceWorkbookAutomation(
            new StaComDispatcherFactory(), new RecordingSourceWorkbookOpenLocator(binding),
            new UnexpectedClosedSourceAutomation());

        var completedSession = await automation.RunAsync(
            "C:\\project\\Book1.xlsm", WorkbookAutomationTimeouts.Default,
            async (session, token) =>
            {
                try { await session.SaveAsync(token); }
                catch (WorkbookAutomationCanceledException) { }
                return session;
            }, cancellation.Token);

        Assert.Equal(SourceWorkbookSaveState.Saved, completedSession.SaveState);
        Assert.Equal(1, workbook.Releases);
    }

    [Fact]
    public async Task TimedOutBorrowedComCallRetainsUnprovedCleanupEvidence()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var workbook = new RecordingWorkbookBuildSession
        {
            OnGetModules = () =>
            {
                entered.Set();
                release.Wait();
            }
        };
        var binding = new SourceWorkbookBorrowedBinding(workbook, () => workbook.IsSaved, () => workbook.Releases++);
        var automation = new ExcelComSourceWorkbookAutomation(
            new StaComDispatcherFactory(), new RecordingSourceWorkbookOpenLocator(binding),
            new UnexpectedClosedSourceAutomation());
        var timeouts = WorkbookAutomationTimeouts.Default with
        {
            ModuleImport = TimeSpan.FromMilliseconds(50),
            ProcessCleanup = TimeSpan.FromMilliseconds(50)
        };

        try
        {
            var pending = automation.RunAsync(
                "C:\\project\\Book1.xlsm", timeouts,
                async (session, token) => await session.GetModulesAsync(token),
                CancellationToken.None);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
            Assert.True(WorkbookAutomationTerminalFacts.Analyze(error).HasUnprovedLifecycle);
            Assert.Equal(0, workbook.Releases);
        }
        finally
        {
            release.Set();
        }

        Assert.True(SpinWait.SpinUntil(() => workbook.Releases == 1,
            TimeSpan.FromSeconds(5)));
    }

    private sealed class RecordingSourceWorkbookOpenLocator(SourceWorkbookBorrowedBinding? binding)
        : ISourceWorkbookOpenLocator
    {
        public string? SelectedPath { get; private set; }

        public SourceWorkbookBorrowedBinding? TryAttach(string workbookPath)
        {
            SelectedPath = workbookPath;
            return binding;
        }
    }

    private sealed class RecordingClosedSourceAutomation : ISourceWorkbookAutomation
    {
        public int Calls { get; private set; }

        public Task<TResult> RunAsync<TResult>(
            string workbookPath,
            WorkbookAutomationTimeouts timeouts,
            Func<ISourceWorkbookSession, CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult((TResult)(object)workbookPath);
        }
    }

    private sealed class UnexpectedClosedSourceAutomation : ISourceWorkbookAutomation
    {
        public Task<TResult> RunAsync<TResult>(
            string workbookPath,
            WorkbookAutomationTimeouts timeouts,
            Func<ISourceWorkbookSession, CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken)
            => throw new Xunit.Sdk.XunitException("The already-open workbook must not be opened again.");
    }

    private sealed class RecordingWorkbookBuildSession : IWorkbookBuildSession, IExcelComWorkbookTestSession
    {
        public bool FailSave { get; init; }

        public bool CancelSaveWithoutThrowing { get; init; }

        public Action? OnSave { get; init; }

        public Action? OnGetModules { get; init; }

        public Action? OnRunTests { get; init; }

        public bool IsSaved { get; private set; }

        public int SaveCalls { get; private set; }

        public int TestCalls { get; private set; }

        public WorkbookTestSelector? TestSelector { get; private set; }

        public int Releases { get; set; }

        public string GetProjectName() => "VbaProject";

        public IReadOnlyList<WorkbookModule> GetModules()
        {
            OnGetModules?.Invoke();
            return [];
        }

        public IReadOnlyList<WorkbookReference> GetReferences() => [];

        public bool RemoveReference(string referenceName) => false;

        public void AddReference(ResolvedVbaProjectReference reference) { }

        public void RemoveModule(string moduleName) { }

        public void ImportModule(VbeImportSourceFile sourceFile) { }

        public VbeImportVerificationReport VerifyImportedModules()
            => VbeImportVerificationReport.Empty;

        public IReadOnlyList<WorkbookTestResultRow> RunTests(WorkbookTestSelector selector)
        {
            TestCalls++;
            TestSelector = selector;
            OnRunTests?.Invoke();
            return [new WorkbookTestResultRow(selector.ModuleName!, selector.ProcedureName!, "OK", "")];
        }

        public void Save()
        {
            SaveCalls++;
            if (FailSave) throw new InvalidOperationException("native save failed");
            if (CancelSaveWithoutThrowing) return;
            IsSaved = true;
            OnSave?.Invoke();
        }
    }
}
