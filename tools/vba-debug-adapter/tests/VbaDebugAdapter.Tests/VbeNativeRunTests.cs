using VbaDebugAdapter.Debugging;
using VbaDebugAdapter.Infrastructure;
using Xunit;
using Fixture = VbaDebugAdapter.Tests.VbeBreakpointTransferTests.Fixture;

namespace VbaDebugAdapter.Tests;

public sealed class VbeNativeRunTests
{
    private static readonly DebugTargetProcedure Target = new("DebugModule", "Probe");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Baseline_follows_exact_UI_preparation_and_the_owner_retires_it_once(bool source)
    {
        var monitor = new BaselineMonitor();
        await using (var fixture = await Fixture.CreateAsync(source, foreground: false, monitor: monitor))
        {
            monitor.BeginAction = () =>
            {
                Assert.True(fixture.Model.Excel.VBE!.MainWindow.Visible);
                Assert.Contains("selection:7:1:7:1", fixture.Events);
                Assert.Contains("find-control:1:186:False", fixture.Events);
                Assert.DoesNotContain("execute:186", fixture.Events);
            };
            await fixture.RunAsync(Target, new Sink(), CancellationToken.None);
            Assert.Equal(1, monitor.Begins);
            Assert.Equal(0, monitor.Phase!.Disposals);
            Assert.Equal(1, fixture.Events.Count(entry => entry == "execute:186"));
        }
        Assert.Equal(1, monitor.Phase!.Disposals);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Baseline_failure_never_executes_Run(bool source)
    {
        var original = new IOException("baseline unavailable");
        var monitor = new BaselineMonitor { BeginFailure = original };
        await using var fixture = await Fixture.CreateAsync(source, monitor: monitor);
        fixture.ExpectedFailure = true;
        var failure = await Record.ExceptionAsync(() =>
            fixture.RunAsync(Target, new Sink(), CancellationToken.None));
        Assert.NotNull(failure);
        Assert.True(Contains(failure, original), failure.ToString());
        Assert.DoesNotContain("execute:186", fixture.Events);
        Assert.Null(monitor.Phase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Already_faulted_observation_prevents_Run_and_retains_the_original_failure(bool source)
    {
        var original = new IOException("observation failed at start");
        var monitor = new BaselineMonitor { ObservationFailure = original };
        await using var fixture = await Fixture.CreateAsync(source, monitor: monitor);
        fixture.ExpectedFailure = true;
        fixture.AllowObservationFailure = true;
        fixture.ExpectedObservationFailure = original;
        var failure = await Record.ExceptionAsync(() =>
            fixture.RunAsync(Target, new Sink(), CancellationToken.None));
        Assert.NotNull(failure);
        Assert.True(Contains(failure, original), failure.ToString());
        Assert.DoesNotContain("execute:186", fixture.Events);
        Assert.Equal(1, monitor.Phase!.Disposals);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancellation_after_baseline_prevents_Run_and_retires_the_phase(bool source)
    {
        using var cancellation = new CancellationTokenSource();
        var monitor = new BaselineMonitor { BeginAction = cancellation.Cancel };
        await using var fixture = await Fixture.CreateAsync(source, monitor: monitor);
        fixture.ExpectedCancellation = true;
        var failure = await Record.ExceptionAsync(() =>
            fixture.RunAsync(Target, new Sink(), cancellation.Token));
        Assert.NotNull(failure);
        var primary = failure is IDebugFailureEvidence evidence
            ? evidence.FailureOutcome.PrimaryFailure : failure;
        Assert.IsAssignableFrom<OperationCanceledException>(primary);
        Assert.DoesNotContain("execute:186", fixture.Events);
        Assert.Equal(1, monitor.Phase!.Disposals);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Mode_changed_after_baseline_prevents_Run_and_retires_the_phase(bool source)
    {
        var monitor = new BaselineMonitor();
        await using var fixture = await Fixture.CreateAsync(source, monitor: monitor);
        fixture.ExpectedFailure = true;
        monitor.BeginAction = () => fixture.Model.Project.Mode = 1;
        var failure = await Assert.ThrowsAsync<DebugSetupException>(() =>
            fixture.RunAsync(Target, new Sink(), CancellationToken.None));
        Assert.Equal(VbeNativeFailureReason.BindingChanged, failure.NativeFailure!.Reason);
        Assert.Equal("Run Sub/UserForm", failure.NativeFailure.Operation);
        Assert.Equal("DebugModule", failure.NativeFailure.ModuleName);
        Assert.Equal(7, failure.NativeFailure.VbeLine);
        Assert.DoesNotContain("execute:186", fixture.Events);
        Assert.Equal(1, monitor.Phase!.Disposals);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Failed_start_retains_a_distinct_retirement_failure(bool source)
    {
        var secondary = new IOException("retirement failed");
        var monitor = new BaselineMonitor { RetirementFailure = secondary };
        await using var fixture = await Fixture.CreateAsync(source, monitor: monitor);
        fixture.ExpectedFailure = true;
        fixture.AllowObservationFailure = true;
        fixture.ExpectedObservationFailure = secondary;
        monitor.BeginAction = () => fixture.Model.Project.Mode = 1;
        var failure = await Record.ExceptionAsync(() =>
            fixture.RunAsync(Target, new Sink(), CancellationToken.None));
        var evidence = Assert.IsAssignableFrom<IDebugFailureEvidence>(failure);
        var primary = Assert.IsType<DebugSetupException>(evidence.FailureOutcome.PrimaryFailure);
        Assert.Equal(VbeNativeFailureReason.BindingChanged, primary.NativeFailure!.Reason);
        Assert.Contains(evidence.FailureOutcome.CleanupFailures,
            item => ReferenceEquals(item.Exception, secondary));
        Assert.DoesNotContain("execute:186", fixture.Events);
        Assert.Equal(1, monitor.Phase!.Disposals);
    }

    [Theory]
    [InlineData(true, "runMissing", "CommandMissing")]
    [InlineData(false, "runMissing", "CommandMissing")]
    [InlineData(true, "runWrong", "WrongCommand")]
    [InlineData(false, "runWrong", "WrongCommand")]
    [InlineData(true, "runNotBuiltIn", "WrongCommand")]
    [InlineData(false, "runNotBuiltIn", "WrongCommand")]
    [InlineData(true, "runDisabled", "CommandDisabled")]
    [InlineData(false, "runDisabled", "CommandDisabled")]
    [InlineData(true, "runThrows", "InvocationFailed")]
    [InlineData(false, "runThrows", "InvocationFailed")]
    public async Task Run_command_failures_have_the_same_facts_in_both_paths(
        bool source, string behavior, string reason)
    {
        var monitor = new BaselineMonitor();
        await using var fixture = await Fixture.CreateAsync(source, behavior, monitor: monitor);
        fixture.ExpectedFailure = true;
        var failure = await Assert.ThrowsAsync<DebugSetupException>(() =>
            fixture.RunAsync(Target, new Sink(), CancellationToken.None));
        Assert.Equal(reason, failure.NativeFailure!.Reason.ToString());
        Assert.Equal("Run Sub/UserForm", failure.NativeFailure.Operation);
        Assert.Equal("DebugModule", failure.NativeFailure.ModuleName);
        if (behavior == "runThrows")
        {
            Assert.Equal(1, monitor.Phase!.Disposals);
            Assert.Equal(1, fixture.Events.Count(entry => entry == "execute:186"));
        }
        else
        {
            Assert.Equal(0, monitor.Begins);
            Assert.DoesNotContain("execute:186", fixture.Events);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_target_moved_during_activation_is_not_reselected(bool source)
    {
        await using var fixture = await Fixture.CreateAsync(source);
        fixture.ExpectedFailure = true;
        var code = Assert.IsType<FakeVbComponent>(
            fixture.Model.Project.VBComponents.Item("DebugModule")).CodeModule;
        fixture.Model.Excel.VBE!.MainWindow.FocusAction = () => code.ProcedureLine = _ => 8;
        var failure = await Assert.ThrowsAsync<DebugSetupException>(() =>
            fixture.RunAsync(Target, null, CancellationToken.None));
        Assert.Equal(VbeNativeFailureReason.SourceMapMismatch, failure.NativeFailure!.Reason);
        Assert.DoesNotContain("execute:186", fixture.Events);
    }

    [Fact]
    public async Task Doctor_inspect_prepares_both_contexts_without_execution_or_owned_setting_changes()
    {
        var monitor = new BaselineMonitor();
        await using var fixture = await Fixture.CreateAsync(source: false, monitor: monitor);
        var writes = fixture.Model.Excel.VBE!.UserSettingWriteCount;
        await fixture.InspectAsync(fixture.Breakpoint(), Target, CancellationToken.None);
        Assert.Contains("find-control:1:51:False", fixture.Events);
        Assert.Contains("find-control:1:186:False", fixture.Events);
        Assert.DoesNotContain(fixture.Events, entry => entry.StartsWith("execute:"));
        Assert.DoesNotContain("enable-events:True", fixture.Events);
        Assert.Equal(writes, fixture.Model.Excel.VBE.UserSettingWriteCount);
        Assert.Equal(0, monitor.Begins);
    }

    [Fact]
    public async Task Continue_uses_break_mode_without_a_new_baseline_or_setting_change()
    {
        var monitor = new BaselineMonitor();
        await using var fixture = await Fixture.CreateAsync(source: false, monitor: monitor);
        fixture.Model.Project.Mode = 1;
        await fixture.ContinueAsync(Target, CancellationToken.None);
        Assert.Equal(1, fixture.Events.Count(entry => entry == "execute:186"));
        Assert.DoesNotContain("enable-events:True", fixture.Events);
        Assert.Equal(0, monitor.Begins);
    }

    private static bool Contains(Exception failure, Exception expected)
    {
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<Exception>();
        pending.Push(failure);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current)) continue;
            if (ReferenceEquals(current, expected)) return true;
            if (current is IDebugFailureEvidence evidence)
            {
                if (evidence.FailureOutcome.PrimaryFailure is { } primary) pending.Push(primary);
                foreach (var cleanup in evidence.FailureOutcome.CleanupFailures) pending.Push(cleanup.Exception);
            }
            if (current is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
            else if (current.InnerException is { } inner) pending.Push(inner);
        }
        return false;
    }

    private sealed class Sink : IDebugInputWaitSink
    {
        public ValueTask InputRequiredAsync(DebugInputWait wait, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    private sealed class BaselineMonitor : IDebugModalPromptMonitor
    {
        internal int Begins;
        internal Action? BeginAction;
        internal Exception? BeginFailure;
        internal Exception? ObservationFailure;
        internal Exception? RetirementFailure;
        internal Phase? Phase;

        public IDebugModalPromptPhase BeginPhase(DebugInputWait wait,
            Task<DebugProcessExit> process, IDebugInputWaitSink sink)
        {
            Assert.Equal(DebugInputWaitPhase.TargetStart, wait.Phase);
            Begins++;
            if (BeginFailure is { } failure) throw failure;
            Phase = new Phase(ObservationFailure, RetirementFailure);
            BeginAction?.Invoke();
            return Phase;
        }
    }

    private sealed class Phase : IDebugModalPromptPhase
    {
        private readonly TaskCompletionSource completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Disposals;
        public Task Completion { get; }
        private readonly Exception? retirement;
        public Phase(Exception? failure, Exception? retirement)
        {
            Completion = failure is null ? completion.Task : Task.FromException(failure);
            this.retirement = retirement;
        }
        public Task<T> ObserveOperationAsync<T>(Func<Task<T>> operation, CancellationToken token)
            => operation().WaitAsync(token);
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Disposals);
            completion.TrySetResult();
            return retirement is null ? new ValueTask(Completion) : ValueTask.FromException(retirement);
        }
    }
}
