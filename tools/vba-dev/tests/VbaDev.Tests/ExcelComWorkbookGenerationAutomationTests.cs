using System.Text;
using VbaDev.App.Testing;
using VbaDev.App.Workbooks;
using VbaDev.Domain;
using VbaDev.Infrastructure.Debugging;
using VbaDev.Infrastructure.Projects;
using VbaDev.Infrastructure.Workbooks;
using Xunit;

namespace VbaDev.Tests;

public sealed class ExcelComWorkbookGenerationAutomationTests
{
    [Fact]
    public async Task WorkbookTestsRunInsideTheOwnedSessionBeforeCleanupReturns()
    {
        var events = new List<string>();
        var dispatcher = new RecordingGenerationDispatcher(events);
        var lifecycle = new FakeWorkbookGenerationLifecycle(events);
        var automation = new ExcelComWorkbookBuildAutomation(
            new RecordingGenerationDispatcherFactory(dispatcher),
            lifecycle);
        var runner = new ExcelComWorkbookTestRunner(automation);

        var rows = await runner.RunTestsAsync(
            "staged.xlsm",
            new WorkbookTestSelector("Test_Module", "Test_Passes"),
            TimeSpan.FromSeconds(42),
            WorkbookAutomationTimeouts.Default,
            CancellationToken.None);

        Assert.Equal(
            [new WorkbookTestResultRow("Test_Module", "Test_Passes", "OK", "")],
            rows);
        Assert.Equal(
            [
                "start",
                "open:staged.xlsm",
                "test:Test_Module.Test_Passes",
                "cleanup-session:00:00:05",
                "dispatcher-dispose"
            ],
            events);
        Assert.True(lifecycle.EnableAutomationSecurityLow);
        Assert.True(lifecycle.Owner.HasExited);
    }

    [Fact]
    public void ConcreteLegacyWorkbookTestRunnerMethodUsesStrongOwnedAutomation()
    {
        var events = new List<string>();
        var lifecycle = new FakeWorkbookGenerationLifecycle(events);
        var runner = new ExcelComWorkbookTestRunner(
            new ExcelComWorkbookBuildAutomation(
                new RecordingGenerationDispatcherFactory(
                    new RecordingGenerationDispatcher(events)),
                lifecycle));

        var rows = runner.RunTests(
            "staged.xlsm",
            new WorkbookTestSelector("Test_Module", "Test_Passes"));

        Assert.Single(rows);
        Assert.Contains("test:Test_Module.Test_Passes", events);
        Assert.True(lifecycle.Owner.HasExited);
    }

    [Fact]
    public async Task WorkbookTestExecutionTimeoutIdentifiesTheMacroStageAndReleasesTheOwner()
    {
        var events = new List<string>();
        var lifecycle = new FakeWorkbookGenerationLifecycle(events)
        {
            BlockTestUntilTermination = true
        };
        var runner = new ExcelComWorkbookTestRunner(
            new ExcelComWorkbookBuildAutomation(
                new RecordingGenerationDispatcherFactory(
                    new AsynchronousCleanupGenerationDispatcher()),
                lifecycle));

        var error = await Assert.ThrowsAsync<WorkbookAutomationTimeoutException>(() =>
            runner.RunTestsAsync(
                "staged.xlsm",
                new WorkbookTestSelector(),
                TimeSpan.FromMilliseconds(20),
                WorkbookAutomationTimeouts.Default with
                {
                    ProcessCleanup = TimeSpan.Zero
                },
                CancellationToken.None));

        Assert.Equal(WorkbookAutomationStageKind.TestExecution, error.Stage.Kind);
        Assert.True(lifecycle.Owner.HasExited);
        Assert.Equal(1, lifecycle.Owner.TerminationCalls);
    }

    [Fact]
    public void ApplicationCompositionPrefersNativeGenerationAndRetainsLegacyBuildPort()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(
            root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceDirectory = Path.Combine(root, "src", "Book1");
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(
            Path.Combine(sourceDirectory, "Book1.xlsm"),
            "template",
            Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(sourceDirectory, "Feature.bas"),
            "Attribute VB_Name = \"Feature\"",
            Encoding.UTF8);
        var automation = new DualWorkbookGenerationAutomation();
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: automation);

        var result = application.Run(["build"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, automation.NativeRunCalls);
        Assert.Equal(0, automation.LegacyOpenCalls);
        Assert.Equal(["import:Feature.bas", "save"], automation.Events);
        Assert.True(File.Exists(Path.Combine(root, "bin", "Book1.xlsm")));
    }

    [Fact]
    public async Task RunsBoundedOperationsOnOneDispatcherAndCleansTheOwnedSessionBeforeReturning()
    {
        var events = new List<string>();
        var dispatcher = new RecordingGenerationDispatcher(events);
        var lifecycle = new FakeWorkbookGenerationLifecycle(events);
        var automation = new ExcelComWorkbookBuildAutomation(
            new RecordingGenerationDispatcherFactory(dispatcher),
            lifecycle);

        var result = await automation.RunAsync(
            "staged.xlsm",
            WorkbookAutomationTimeouts.Default,
            async (session, cancellationToken) =>
            {
                await session.GetReferencesAsync(cancellationToken);
                await session.RemoveReferenceAsync("Legacy", cancellationToken);
                await session.AddReferenceAsync(
                    new ResolvedVbaProjectReference("Scripting", "{guid}", 1, 0),
                    cancellationToken);
                await session.GetModulesAsync(cancellationToken);
                await session.RemoveModuleAsync("LegacyModule", cancellationToken);
                await session.ImportModuleAsync(
                    new VbeImportSourceFile(
                        "Feature.bas",
                        VbaSourceKind.StandardModule,
                        null,
                        new VbeImportVerification(
                            "Feature",
                            VbaSourceKind.StandardModule,
                            [],
                            "utf8")),
                    cancellationToken);
                await session.ExportModuleAsync(
                    "Feature",
                    "Feature.bas",
                    cancellationToken);
                await session.VerifyAsync(cancellationToken);
                await session.SaveAsync(cancellationToken);
                events.Add("callback-complete");
                return 42;
            },
            CancellationToken.None);

        Assert.Equal(42, result);
        Assert.Equal(
            [
                "start",
                "open:staged.xlsm",
                "get-references",
                "remove-reference:Legacy",
                "add-reference:Scripting",
                "get-modules",
                "remove-module:LegacyModule",
                "import:Feature.bas",
                "export:Feature",
                "verify",
                "save",
                "callback-complete",
                "cleanup-session:00:00:05",
                "dispatcher-dispose"
            ],
            events);
        Assert.Equal(12, dispatcher.InvokeCalls);
        Assert.False(lifecycle.EnableAutomationSecurityLow);
        Assert.True(lifecycle.Owner.HasExited);
        Assert.Equal(0, lifecycle.Owner.TerminationCalls);
    }

    [Fact]
    public async Task InSessionReferenceProbeRunsThroughTheBoundedOwnedSession()
    {
        var events = new List<string>();
        var dispatcher = new RecordingGenerationDispatcher(events);
        var lifecycle = new FakeWorkbookGenerationLifecycle(events);
        var automation = new ExcelComWorkbookBuildAutomation(
            new RecordingGenerationDispatcherFactory(dispatcher),
            lifecycle);
        var candidate = new ResolvedVbaProjectReference(
            "Ambiguous Library",
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            2,
            0);

        var result = await automation.RunAsync(
            "staged.xlsm",
            WorkbookAutomationTimeouts.Default,
            (session, cancellationToken) => session.TryResolveAsync(
                candidate.Name,
                candidate,
                cancellationToken),
            CancellationToken.None);

        Assert.Equal(
            VbaProjectReferenceProbeAttemptOutcome.Accepted,
            result.Outcome);
        Assert.Equal(candidate, result.Reference);
        Assert.Contains($"probe-reference:{candidate.Name}", events);
        Assert.True(lifecycle.Owner.HasExited);
    }

    [Fact]
    public async Task SaveTimeoutIdentifiesTheStageAndTerminatesOnlyTheAttachedOwnerAfterGrace()
    {
        var events = new List<string>();
        var dispatcher = new RecordingGenerationDispatcher(events);
        var lifecycle = new FakeWorkbookGenerationLifecycle(events)
        {
            BlockSaveUntilTermination = true
        };
        var automation = new ExcelComWorkbookBuildAutomation(
            new RecordingGenerationDispatcherFactory(dispatcher),
            lifecycle);
        var timeouts = WorkbookAutomationTimeouts.Default with
        {
            WorkbookSave = TimeSpan.FromMilliseconds(20),
            ProcessCleanup = TimeSpan.Zero
        };

        var error = await Assert.ThrowsAsync<WorkbookAutomationTimeoutException>(() => automation.RunAsync(
            "staged.xlsm",
            timeouts,
            async (session, cancellationToken) =>
            {
                await session.SaveAsync(cancellationToken);
                return true;
            },
            CancellationToken.None));

        Assert.Equal(WorkbookAutomationStageKind.WorkbookSave, error.Stage.Kind);
        Assert.Equal("staged.xlsm", error.Stage.Item);
        Assert.Equal(1, lifecycle.Owner.TerminationCalls);
        Assert.Contains("cleanup-session:00:00:00", events);
        Assert.DoesNotContain("cleanup-host:00:00:00", events);
    }

    [Fact]
    public async Task ModuleEnumerationTimeoutIdentifiesInspectionInsteadOfRemoval()
    {
        var events = new List<string>();
        var lifecycle = new FakeWorkbookGenerationLifecycle(events)
        {
            BlockGetModulesUntilTermination = true
        };
        var automation = new ExcelComWorkbookBuildAutomation(
            new RecordingGenerationDispatcherFactory(
                new AsynchronousCleanupGenerationDispatcher()),
            lifecycle);
        var timeouts = WorkbookAutomationTimeouts.Default with
        {
            ModuleImport = TimeSpan.FromMilliseconds(20),
            ProcessCleanup = TimeSpan.Zero
        };

        var error = await Assert.ThrowsAsync<WorkbookAutomationTimeoutException>(() => automation.RunAsync(
            "staged.xlsm",
            timeouts,
            async (session, cancellationToken) =>
            {
                await session.GetModulesAsync(cancellationToken);
                return true;
            },
            CancellationToken.None));

        Assert.Equal("module inspection", error.Stage.Description);
        Assert.NotEqual(WorkbookAutomationStageKind.ModuleRemoval, error.Stage.Kind);
        Assert.Equal(1, lifecycle.Owner.TerminationCalls);
    }

    [Fact]
    public async Task CleanupFailureBecomesPrimaryWhilePreservingTheStageFailure()
    {
        var events = new List<string>();
        var dispatcher = new RecordingGenerationDispatcher(events);
        var cleanupError = new InvalidOperationException("cleanup failed");
        var lifecycle = new FakeWorkbookGenerationLifecycle(events)
        {
            BlockSaveUntilTermination = true,
            CleanupError = cleanupError
        };
        var automation = new ExcelComWorkbookBuildAutomation(
            new RecordingGenerationDispatcherFactory(dispatcher),
            lifecycle);
        var timeouts = WorkbookAutomationTimeouts.Default with
        {
            WorkbookSave = TimeSpan.FromMilliseconds(20),
            ProcessCleanup = TimeSpan.Zero
        };

        var error = await Assert.ThrowsAsync<WorkbookAutomationReleasedProcessCleanupException>(() => automation.RunAsync(
            "staged.xlsm",
            timeouts,
            async (session, cancellationToken) =>
            {
                await session.SaveAsync(cancellationToken);
                return true;
            },
            CancellationToken.None));

        Assert.Contains("workbook save 'staged.xlsm'", error.Message);
        Assert.Contains("automation cleanup also failed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "could not be verified as released",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
        var aggregate = Assert.IsType<AggregateException>(error.InnerException);
        Assert.Contains(aggregate.InnerExceptions, item => item is WorkbookAutomationTimeoutException timeout &&
            timeout.Stage.Kind == WorkbookAutomationStageKind.WorkbookSave);
        Assert.Contains(aggregate.InnerExceptions, item => item is WorkbookAutomationReleasedProcessCleanupException cleanup &&
            ReferenceEquals(cleanup.InnerException, cleanupError));
    }

    [Fact]
    public async Task OpenFailureIdentifiesWorkbookOpenAndCleansTheStartedHost()
    {
        var events = new List<string>();
        var dispatcher = new RecordingGenerationDispatcher(events);
        var lifecycle = new FakeWorkbookGenerationLifecycle(events)
        {
            OpenError = new InvalidOperationException("open failed")
        };
        var automation = new ExcelComWorkbookBuildAutomation(
            new RecordingGenerationDispatcherFactory(dispatcher),
            lifecycle);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => automation.RunAsync(
            "staged.xlsm",
            WorkbookAutomationTimeouts.Default,
            static (_, _) => Task.FromResult(true),
            CancellationToken.None));

        Assert.Contains("workbook open 'staged.xlsm'", error.Message);
        Assert.Contains("cleanup-host:00:00:05", events);
        Assert.DoesNotContain(events, entry => entry.StartsWith("cleanup-session:", StringComparison.Ordinal));
        Assert.Equal("dispatcher-dispose", events[^1]);
    }

    [Fact]
    public async Task StartupFailureDisposesTheUnattachedControllerAndDispatcher()
    {
        var events = new List<string>();
        var dispatcher = new RecordingGenerationDispatcher(events);
        var lifecycle = new FakeWorkbookGenerationLifecycle(events)
        {
            StartError = new InvalidOperationException("start failed")
        };
        var automation = new ExcelComWorkbookBuildAutomation(
            new RecordingGenerationDispatcherFactory(dispatcher),
            lifecycle);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => automation.RunAsync(
            "staged.xlsm",
            WorkbookAutomationTimeouts.Default,
            static (_, _) => Task.FromResult(true),
            CancellationToken.None));

        Assert.Contains("Excel startup", error.Message);
        Assert.Equal(["start", "dispatcher-dispose"], events);
        Assert.NotNull(lifecycle.CapturedController);
        Assert.Throws<ObjectDisposedException>(() => lifecycle.CapturedController!.Attach(lifecycle.Owner));
    }

    [Fact]
    public async Task StartupDeadlineReturnsEvenWhenTheStaInvocationAndDispatcherNeverUnwind()
    {
        var events = new List<string>();
        var dispatcher = new NonReturningGenerationDispatcher();
        var lifecycle = new FakeWorkbookGenerationLifecycle(events);
        var automation = new ExcelComWorkbookBuildAutomation(
            new RecordingGenerationDispatcherFactory(dispatcher),
            lifecycle);
        var timeouts = WorkbookAutomationTimeouts.Default with
        {
            ExcelStartup = TimeSpan.FromMilliseconds(20),
            ProcessCleanup = TimeSpan.FromMilliseconds(20)
        };

        var execution = automation.RunAsync(
            "staged.xlsm",
            timeouts,
            static (_, _) => Task.FromResult(true),
            CancellationToken.None);
        var error = await Assert.ThrowsAsync<WorkbookAutomationTimeoutException>(
            () => execution.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal(WorkbookAutomationStageKind.ExcelStartup, error.Stage.Kind);
        Assert.Empty(events);
        Assert.Equal(1, dispatcher.InvokeCalls);
        Assert.Equal(1, dispatcher.DisposeCalls);
    }

    [Fact]
    public async Task StartupQueuedPastItsDeadlineCannotLaunchExcelAfterTheCommandReturns()
    {
        var events = new List<string>();
        var dispatcher = new DeferredGenerationDispatcher();
        var lifecycle = new FakeWorkbookGenerationLifecycle(events);
        var automation = new ExcelComWorkbookBuildAutomation(
            new RecordingGenerationDispatcherFactory(dispatcher),
            lifecycle);
        var timeouts = WorkbookAutomationTimeouts.Default with
        {
            ExcelStartup = TimeSpan.FromMilliseconds(20),
            ProcessCleanup = TimeSpan.FromMilliseconds(20)
        };

        var error = await Assert.ThrowsAsync<WorkbookAutomationTimeoutException>(() =>
            automation.RunAsync(
                "staged.xlsm",
                timeouts,
                static (_, _) => Task.FromResult(true),
                CancellationToken.None));

        dispatcher.Drain();
        await dispatcher.InvocationSettled.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(WorkbookAutomationStageKind.ExcelStartup, error.Stage.Kind);
        Assert.Empty(events);
    }

    [Fact]
    public async Task StuckCooperativeCleanupForceTerminatesAndVerifiesTheExactOwner()
    {
        var events = new List<string>();
        var dispatcher = new CleanupBlockingGenerationDispatcher();
        var lifecycle = new FakeWorkbookGenerationLifecycle(events);
        var automation = new ExcelComWorkbookBuildAutomation(
            new RecordingGenerationDispatcherFactory(dispatcher),
            lifecycle);

        var error = await Assert.ThrowsAsync<WorkbookAutomationTimeoutException>(() =>
            automation.RunAsync(
                "staged.xlsm",
                WorkbookAutomationTimeouts.Default with
                {
                    ProcessCleanup = TimeSpan.FromMilliseconds(20)
                },
                static (_, _) => Task.FromResult(true),
                CancellationToken.None));

        Assert.Equal(WorkbookAutomationStageKind.ProcessCleanup, error.Stage.Kind);
        Assert.Equal(1, lifecycle.Owner.TerminationCalls);
        Assert.True(lifecycle.Owner.HasExited);
        Assert.Equal(3, dispatcher.InvokeCalls);
    }

    [Fact]
    public async Task CancellationDuringOwnedProcessCleanupReportsTheCleanupStageAfterVerification()
    {
        using var cleanupStarted = new ManualResetEventSlim();
        using var cleanupRelease = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var events = new List<string>();
        var lifecycle = new FakeWorkbookGenerationLifecycle(events)
        {
            CleanupStarted = cleanupStarted,
            CleanupRelease = cleanupRelease
        };
        var automation = new ExcelComWorkbookBuildAutomation(
            new RecordingGenerationDispatcherFactory(
                new AsynchronousCleanupGenerationDispatcher()),
            lifecycle);

        var execution = automation.RunAsync(
            "staged.xlsm",
            WorkbookAutomationTimeouts.Default,
            static (_, _) => Task.FromResult(true),
            cancellation.Token);
        Assert.True(cleanupStarted.Wait(TimeSpan.FromSeconds(5)));
        cancellation.Cancel();
        cleanupRelease.Set();

        var error = await Assert.ThrowsAsync<WorkbookAutomationCanceledException>(
            () => execution);

        Assert.Equal(WorkbookAutomationStageKind.ProcessCleanup, error.Stage.Kind);
        Assert.True(lifecycle.Owner.HasExited);
    }

    [Fact]
    public async Task UnverifiedStartupCleanupIsReportedAsThePrimaryCleanupFailure()
    {
        var events = new List<string>();
        var dispatcher = new RecordingGenerationDispatcher(events);
        var startError = new InvalidOperationException("activation failed");
        var cleanupError = new InvalidOperationException("ownership cleanup failed");
        var lifecycle = new FakeWorkbookGenerationLifecycle(events)
        {
            StartError = new UnverifiedOwnedSessionStartFailure(startError, cleanupError),
            ExitOwnedProcessBeforeStartError = true
        };
        var automation = new ExcelComWorkbookBuildAutomation(
            new RecordingGenerationDispatcherFactory(dispatcher),
            lifecycle);

        var error = await Assert.ThrowsAsync<WorkbookAutomationCleanupException>(() => automation.RunAsync(
            "staged.xlsm",
            WorkbookAutomationTimeouts.Default,
            static (_, _) => Task.FromResult(true),
            CancellationToken.None));

        Assert.Contains("Excel startup", error.Message);
        var aggregate = Assert.IsType<AggregateException>(error.InnerException);
        Assert.Contains(startError, aggregate.InnerExceptions);
        Assert.Contains(cleanupError, aggregate.InnerExceptions);
        Assert.Equal(["start", "dispatcher-dispose"], events);
    }

    [Fact]
    public async Task StartupFailureAfterOwnershipForceTerminatesOnlyTheAttachedProcess()
    {
        var events = new List<string>();
        var dispatcher = new RecordingGenerationDispatcher(events);
        var lifecycle = new FakeWorkbookGenerationLifecycle(events)
        {
            StartError = new InvalidOperationException("configuration failed"),
            AttachOwnerBeforeStartError = true
        };
        var automation = new ExcelComWorkbookBuildAutomation(
            new RecordingGenerationDispatcherFactory(dispatcher),
            lifecycle);
        var timeouts = WorkbookAutomationTimeouts.Default with
        {
            ProcessCleanup = TimeSpan.Zero
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => automation.RunAsync(
            "staged.xlsm",
            timeouts,
            static (_, _) => Task.FromResult(true),
            CancellationToken.None));

        Assert.Contains("Excel startup", error.Message);
        Assert.Equal(1, lifecycle.Owner.TerminationCalls);
        Assert.True(lifecycle.Owner.HasExited);
        Assert.Equal(["start", "dispatcher-dispose"], events);
    }

    [Fact]
    public async Task OwnedProcessExitAfterTheLastOperationPreventsSuccessfulReturn()
    {
        var events = new List<string>();
        var dispatcher = new RecordingGenerationDispatcher(events);
        var lifecycle = new FakeWorkbookGenerationLifecycle(events)
        {
            CleanupError = new MissingMemberException(
                "The exited Excel process no longer exposes workbook.Close.")
        };
        var automation = new ExcelComWorkbookBuildAutomation(
            new RecordingGenerationDispatcherFactory(dispatcher),
            lifecycle);

        var error = await Assert.ThrowsAsync<WorkbookAutomationProcessLostException>(() => automation.RunAsync(
            "staged.xlsm",
            WorkbookAutomationTimeouts.Default,
            async (session, cancellationToken) =>
            {
                await session.SaveAsync(cancellationToken);
                lifecycle.Owner.CompleteCooperatively();
                return true;
            },
            CancellationToken.None));

        Assert.Equal(WorkbookAutomationStageKind.WorkbookSave, error.Stage.Kind);
        Assert.Contains("cleanup-session:00:00:05", events);
    }

    private sealed class RecordingGenerationDispatcherFactory(IStaComDispatcher dispatcher)
        : IStaComDispatcherFactory
    {
        public IStaComDispatcher Create() => dispatcher;
    }

    private sealed class RecordingGenerationDispatcher(List<string> events) : IStaComDispatcher
    {
        public int InvokeCalls { get; private set; }

        public Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvokeCalls++;
            return Task.FromResult(operation());
        }

        public ValueTask DisposeAsync()
        {
            events.Add("dispatcher-dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NonReturningGenerationDispatcher : IStaComDispatcher
    {
        private readonly TaskCompletionSource<object?> invocation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource disposal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int InvokeCalls { get; private set; }

        public int DisposeCalls { get; private set; }

        public Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken)
        {
            InvokeCalls++;
            return invocation.Task.ContinueWith(
                static task => (T)task.Result!,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return new ValueTask(disposal.Task);
        }
    }

    private sealed class DeferredGenerationDispatcher : IStaComDispatcher
    {
        private Action? drain;
        private readonly TaskCompletionSource invocationSettled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task InvocationSettled => invocationSettled.Task;

        public Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<T>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            drain = () =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    completion.TrySetResult(operation());
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
                finally
                {
                    invocationSettled.TrySetResult();
                }
            };
            return completion.Task;
        }

        public void Drain()
            => (Interlocked.Exchange(ref drain, null)
                ?? throw new InvalidOperationException("No startup invocation is queued."))();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CleanupBlockingGenerationDispatcher : IStaComDispatcher
    {
        private readonly TaskCompletionSource<object?> cleanup =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int InvokeCalls { get; private set; }

        public Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken)
        {
            InvokeCalls++;
            if (InvokeCalls < 3)
            {
                return Task.FromResult(operation());
            }

            return cleanup.Task.ContinueWith(
                static task => (T)task.Result!,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class AsynchronousCleanupGenerationDispatcher : IStaComDispatcher
    {
        private int invokeCalls;

        public Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken)
        {
            invokeCalls++;
            return invokeCalls < 3
                ? Task.FromResult(operation())
                : Task.Run(operation, CancellationToken.None);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeWorkbookGenerationLifecycle(List<string> events)
        : IExcelComWorkbookGenerationLifecycle
    {
        public FakeOwnedExcelProcessControl Owner { get; } = new();

        public bool BlockSaveUntilTermination { get; init; }

        public bool BlockGetModulesUntilTermination { get; init; }

        public bool BlockTestUntilTermination { get; init; }

        public Exception? StartError { get; init; }

        public Exception? OpenError { get; init; }

        public Exception? CleanupError { get; init; }

        public bool ExitOwnedProcessBeforeStartError { get; init; }

        public bool AttachOwnerBeforeStartError { get; init; }

        public ManualResetEventSlim? CleanupStarted { get; init; }

        public ManualResetEventSlim? CleanupRelease { get; init; }

        public OwnedExcelTerminationController? CapturedController { get; private set; }

        public bool EnableAutomationSecurityLow { get; private set; }

        public object Start(
            OwnedExcelTerminationController terminationController,
            bool enableAutomationSecurityLow,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("start");
            CapturedController = terminationController;
            EnableAutomationSecurityLow = enableAutomationSecurityLow;
            if (StartError is not null)
            {
                if (AttachOwnerBeforeStartError || ExitOwnedProcessBeforeStartError)
                {
                    terminationController.Attach(Owner);
                }

                if (ExitOwnedProcessBeforeStartError)
                {
                    Owner.CompleteCooperatively();
                }

                throw StartError;
            }

            terminationController.Attach(Owner);
            return new object();
        }

        public IWorkbookBuildSession Open(object host, string workbookPath)
        {
            events.Add($"open:{workbookPath}");
            if (OpenError is not null)
            {
                throw OpenError;
            }

            return new FakeWorkbookBuildSession(
                events,
                Owner,
                BlockSaveUntilTermination,
                BlockGetModulesUntilTermination,
                BlockTestUntilTermination);
        }

        public void DisposeHost(object host, TimeSpan cleanupGrace)
        {
            events.Add($"cleanup-host:{cleanupGrace}");
            WaitForCleanupRelease();
            Owner.CompleteCooperatively();
            if (CleanupError is not null)
            {
                throw CleanupError;
            }
        }

        public void DisposeSession(IWorkbookBuildSession session, TimeSpan cleanupGrace)
        {
            events.Add($"cleanup-session:{cleanupGrace}");
            WaitForCleanupRelease();
            Owner.CompleteCooperatively();
            if (CleanupError is not null)
            {
                throw CleanupError;
            }
        }

        private void WaitForCleanupRelease()
        {
            if (CleanupRelease is null)
            {
                return;
            }

            CleanupStarted?.Set();
            if (!CleanupRelease.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Synthetic cleanup gate was not released.");
            }
        }
    }

    private sealed class FakeWorkbookBuildSession(
        List<string> events,
        FakeOwnedExcelProcessControl owner,
        bool blockSaveUntilTermination,
        bool blockGetModulesUntilTermination,
        bool blockTestUntilTermination) :
        IWorkbookBuildSession,
        IExcelComWorkbookTestSession
    {
        public string GetProjectName()
        {
            events.Add("get-project-name");
            return "VbaProject";
        }

        public IReadOnlyList<WorkbookModule> GetModules()
        {
            if (blockGetModulesUntilTermination && !owner.Terminated.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The test owner was not terminated.");
            }

            events.Add("get-modules");
            return [];
        }

        public IReadOnlyList<WorkbookReference> GetReferences()
        {
            events.Add("get-references");
            return [];
        }

        public bool RemoveReference(string referenceName)
        {
            events.Add($"remove-reference:{referenceName}");
            return true;
        }

        public void AddReference(ResolvedVbaProjectReference reference)
            => events.Add($"add-reference:{reference.Name}");

        public VbaProjectReferenceProbeAttemptResult TryResolveReference(
            string referenceName,
            ResolvedVbaProjectReference candidate)
        {
            events.Add($"probe-reference:{referenceName}");
            return VbaProjectReferenceProbeAttemptResult.Accepted(candidate);
        }

        public void RemoveModule(string moduleName)
            => events.Add($"remove-module:{moduleName}");

        public void ImportModule(VbeImportSourceFile sourceFile)
            => events.Add($"import:{sourceFile.FileName}");

        public void ExportModule(string moduleName, string destinationPath)
            => events.Add($"export:{moduleName}");

        public VbeImportVerificationReport VerifyImportedModules()
        {
            events.Add("verify");
            return VbeImportVerificationReport.Empty;
        }

        public void Save()
        {
            if (blockSaveUntilTermination && !owner.Terminated.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The test owner was not terminated.");
            }

            events.Add("save");
        }

        public IReadOnlyList<WorkbookTestResultRow> RunTests(WorkbookTestSelector selector)
        {
            if (blockTestUntilTermination && !owner.Terminated.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The test owner was not terminated.");
            }

            events.Add($"test:{selector.ModuleName}.{selector.ProcedureName}");
            return [new WorkbookTestResultRow("Test_Module", "Test_Passes", "OK", "")];
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeOwnedExcelProcessControl : IOwnedExcelProcessControl
    {
        private readonly TaskCompletionSource completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim Terminated { get; } = new();

        public int TerminationCalls { get; private set; }

        public int DisposeCalls { get; private set; }

        public bool HasExited { get; private set; }

        public Task Completion => completion.Task;

        public Task TerminateAsync()
        {
            TerminationCalls++;
            HasExited = true;
            Terminated.Set();
            completion.TrySetResult();
            return Task.CompletedTask;
        }

        public void CompleteCooperatively()
        {
            HasExited = true;
            completion.TrySetResult();
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DualWorkbookGenerationAutomation :
        IWorkbookBuildAutomation,
        IWorkbookGenerationAutomation
    {
        public int LegacyOpenCalls { get; private set; }

        public int NativeRunCalls { get; private set; }

        public List<string> Events { get; } = [];

        public IWorkbookBuildSession OpenWorkbook(string workbookPath)
        {
            LegacyOpenCalls++;
            throw new InvalidOperationException("The legacy build port must not generate production output.");
        }

        public async Task<TResult> RunAsync<TResult>(
            string workbookPath,
            WorkbookAutomationTimeouts timeouts,
            Func<IWorkbookGenerationSession, CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken)
        {
            NativeRunCalls++;
            return await operation(
                new DualWorkbookGenerationSession(Events),
                cancellationToken);
        }
    }

    private sealed class DualWorkbookGenerationSession(List<string> events)
        : IWorkbookGenerationSession
    {
        public Task<string> GetProjectNameAsync(CancellationToken cancellationToken)
            => Task.FromResult("VbaProject");

        public Task<IReadOnlyList<WorkbookModule>> GetModulesAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<WorkbookModule>>([]);

        public Task<IReadOnlyList<WorkbookReference>> GetReferencesAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<WorkbookReference>>([]);

        public Task<bool> RemoveReferenceAsync(
            string referenceName,
            CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task AddReferenceAsync(
            ResolvedVbaProjectReference reference,
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task RemoveModuleAsync(string moduleName, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task ImportModuleAsync(
            VbeImportSourceFile sourceFile,
            CancellationToken cancellationToken)
        {
            events.Add($"import:{sourceFile.FileName}");
            return Task.CompletedTask;
        }

        public Task ExportModuleAsync(
            string moduleName,
            string destinationPath,
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<VbeImportVerificationReport> VerifyAsync(CancellationToken cancellationToken)
            => Task.FromResult(VbeImportVerificationReport.Empty);

        public Task SaveAsync(CancellationToken cancellationToken)
        {
            events.Add("save");
            return Task.CompletedTask;
        }
    }

    private sealed class UnverifiedOwnedSessionStartFailure(
        Exception startException,
        Exception cleanupException) : Exception(startException.Message, startException),
        IOwnedExcelSessionStartFailure
    {
        public Exception StartException { get; } = startException;

        public Exception? CleanupException { get; } = cleanupException;

        public bool CleanupVerified => false;
    }
}
