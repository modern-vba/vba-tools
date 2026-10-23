using System.Collections.Immutable;
using System.Runtime.InteropServices;
using VbaDebugAdapter.Debugging;
using VbaDebugAdapter.Infrastructure;
using VbaTools.Syntax;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class VbeBreakpointTransferTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Every_map_is_verified_before_any_toggle(bool source)
    {
        await using var fixture = await Fixture.CreateAsync(source);
        var bad = fixture.Breakpoint(module: "OtherModule") with
        {
            SourceMap = fixture.Map("OtherModule") with
            { CodeLines = fixture.Rows.Select((line, index) => index == 0 ? "changed" : line).ToImmutableArray() }
        };
        var error = await fixture.RejectAsync([fixture.Breakpoint(), bad], "SourceMapMismatch");
        Assert.DoesNotContain("execute:51", fixture.Events);
        Assert.Contains("component:DebugModule", fixture.Events);
        Assert.Contains("component:OtherModule", fixture.Events);
        Assert.Equal(bad.Source.SourceUri, error.NativeFailure!.SourceUri);
        Assert.Equal(bad.Source.EditorLine, error.NativeFailure.EditorLine);
        Assert.Equal(bad.VbideLine, error.NativeFailure.VbeLine);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Content_changed_during_UI_activation_is_rejected(bool source)
    {
        await using var fixture = await Fixture.CreateAsync(source);
        fixture.Model.Excel.VBE!.MainWindow.FocusAction = () => fixture.Rows[0] = "changed after activation";
        await fixture.RejectAsync([fixture.Breakpoint()], "SourceMapMismatch");
        Assert.DoesNotContain("execute:51", fixture.Events);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Selection_changed_during_content_revalidation_is_rejected(bool source)
    {
        await using var fixture = await Fixture.CreateAsync(source);
        var code = Assert.IsType<FakeVbComponent>(fixture.Model.Project.VBComponents.Item("DebugModule")).CodeModule;
        var reads = 0;
        code.ReadAction = _ =>
        {
            if (++reads == fixture.Rows.Count + 1)
                code.CodePane.SetSelection(99, 1, 99, 1);
        };
        await fixture.RejectAsync([fixture.Breakpoint()], "WrongSelection");
        Assert.DoesNotContain("execute:51", fixture.Events);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Replaced_component_is_not_treated_as_the_verified_module(bool source)
    {
        await using var fixture = await Fixture.CreateAsync(source);
        var components = fixture.Model.Project.VBComponents;
        var original = Assert.IsType<FakeVbComponent>(components.Item("DebugModule"));
        var replacement = new FakeVbComponent(original.Name, original.Type,
            original.CodeModule, fixture.Events);
        var lookups = 0;
        components.ItemOverride = _ => ++lookups == 1 ? original : replacement;
        await fixture.RejectAsync([fixture.Breakpoint()], "SourceMapMismatch");
        Assert.DoesNotContain("execute:51", fixture.Events);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Command_disabled_during_final_content_check_is_not_invoked(bool source)
    {
        await using var fixture = await Fixture.CreateAsync(source);
        var code = Assert.IsType<FakeVbComponent>(
            fixture.Model.Project.VBComponents.Item("DebugModule")).CodeModule;
        var command = Assert.IsType<FakeCommandBarControl>(
            fixture.Model.Excel.VBE!.CommandBars.FindControl(1, 51, Type.Missing, false));
        var reads = 0;
        code.ReadAction = _ =>
        {
            if (++reads == fixture.Rows.Count + 1) command.Enabled = false;
        };
        await fixture.RejectAsync([fixture.Breakpoint()], "CommandDisabled");
        Assert.DoesNotContain("execute:51", fixture.Events);
    }

    [Theory]
    [InlineData(true, "missing", "CommandMissing")]
    [InlineData(false, "missing", "CommandMissing")]
    [InlineData(true, "wrong", "WrongCommand")]
    [InlineData(false, "wrong", "WrongCommand")]
    [InlineData(true, "notBuiltIn", "WrongCommand")]
    [InlineData(false, "notBuiltIn", "WrongCommand")]
    [InlineData(true, "disabled", "CommandDisabled")]
    [InlineData(false, "disabled", "CommandDisabled")]
    [InlineData(true, "throws", "InvocationFailed")]
    [InlineData(false, "throws", "InvocationFailed")]
    public async Task Native_failure_facts_match_in_both_production_paths(
        bool source, string behavior, string expectedReason)
    {
        await using var fixture = await Fixture.CreateAsync(source, behavior);
        var breakpoint = fixture.Breakpoint();
        var error = await fixture.RejectAsync([breakpoint], expectedReason);
        Assert.Equal("Toggle Breakpoint", error.NativeFailure!.Operation);
        Assert.Equal(breakpoint.Source.SourceUri, error.NativeFailure.SourceUri);
        Assert.Equal(breakpoint.Source.EditorLine, error.NativeFailure.EditorLine);
        Assert.Equal(breakpoint.VbideLine, error.NativeFailure.VbeLine);
        Assert.Contains("mapped to", error.Message);
        if (behavior == "throws") Assert.IsType<COMException>(error.InnerException);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Long_module_identity_is_rejected_without_reading_components(bool source)
    {
        await using var fixture = await Fixture.CreateAsync(source);
        await fixture.RejectAsync([fixture.Breakpoint(module: new string('A', 32))], "SourceMapMismatch");
        Assert.DoesNotContain(fixture.Events, entry => entry.StartsWith("component:"));
        Assert.DoesNotContain("execute:51", fixture.Events);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_later_failure_does_not_retry_or_compensate_prior_toggles(bool source)
    {
        await using var fixture = await Fixture.CreateAsync(source, "mutateAfterToggle");
        await fixture.RejectAsync([fixture.Breakpoint(), fixture.Breakpoint(line: 4)], "SourceMapMismatch");
        Assert.Equal(1, fixture.Events.Count(entry => entry == "execute:51"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Foreground_refusal_does_not_invalidate_an_exact_native_context(bool source)
    {
        await using var fixture = await Fixture.CreateAsync(source, foreground: false);
        await fixture.TransferAsync([fixture.Breakpoint()], CancellationToken.None);
        Assert.Equal(1, fixture.Events.Count(entry => entry == "execute:51"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancellation_after_UI_preparation_prevents_invocation(bool source)
    {
        await using var fixture = await Fixture.CreateAsync(source);
        using var cancellation = new CancellationTokenSource();
        fixture.Model.Excel.VBE!.MainWindow.FocusAction = cancellation.Cancel;
        fixture.ExpectedCancellation = true;
        var failure = await Record.ExceptionAsync(() =>
            fixture.TransferAsync([fixture.Breakpoint()], cancellation.Token));
        Assert.NotNull(failure);
        if (failure is IDebugFailureEvidence evidence)
            Assert.IsAssignableFrom<OperationCanceledException>(evidence.FailureOutcome.PrimaryFailure);
        else Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.DoesNotContain("execute:51", fixture.Events);
    }

    [Fact]
    public async Task Source_binding_changed_during_activation_is_rejected()
    {
        await using var fixture = await Fixture.CreateAsync(source: true);
        fixture.Model.Excel.VBE!.MainWindow.FocusAction = () => fixture.BindingCurrent = false;
        await fixture.RejectAsync([fixture.Breakpoint()], "BindingChanged");
        Assert.DoesNotContain("execute:51", fixture.Events);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TempDirectory temp;
        private readonly LeaseIssuedVbeGenerationFixture generation;
        private IAsyncDisposable session = null!;
        private Func<IReadOnlyList<VbeBreakpoint>, CancellationToken, Task> transfer = null!;
        private bool expectedFailure;
        internal bool ExpectedCancellation;
        internal bool BindingCurrent = true;
        internal List<string> Rows { get; } =
            ["Option Explicit", "Public Sub Probe()", "    Debug.Print 1", "    Debug.Print 2", "End Sub"];
        internal List<string> Events { get; } = [];
        internal FakeVbeModel Model = null!;

        private Fixture(TempDirectory temp, LeaseIssuedVbeGenerationFixture generation)
        { this.temp = temp; this.generation = generation; }

        internal static async Task<Fixture> CreateAsync(
            bool source, string behavior = "", bool foreground = true)
        {
            var temp = TempDirectory.Create();
            var generation = await LeaseIssuedVbeGenerationFixture.CreateAsync(temp.Path, "Source.xlsm");
            var fixture = new Fixture(temp, generation);
            var path = generation.GenerationWorkspace.WorkbookPath;
            fixture.Model = FakeVbeModel.Create(path, fixture.Events, codeLines: fixture.Rows,
                secondaryComponentName: "OtherModule", secondaryCodeLines: fixture.Rows,
                breakpointCommandMissing: behavior == "missing",
                breakpointCommandEnabled: behavior != "disabled",
                breakpointCommandBuiltIn: behavior != "notBuiltIn",
                breakpointCommandId: behavior == "wrong" ? 99 : 51,
                breakpointCommandException: behavior == "throws" ? new COMException("native failure") : null,
                breakpointCommandExecuteAction: behavior == "mutateAfterToggle"
                    ? () => fixture.Rows[0] = "changed after toggle" : null);
            var window = new FakeDebugWindowActivator(fixture.Events, foregroundReached: foreground);
            var dispatchers = new FakeStaComDispatcherFactory(new RecordingStaComDispatcher());
            if (source)
            {
                var binding = new SourceVbeDesktopBinding(fixture.Model.Excel, fixture.Model.Workbook,
                    path, 12345, 638000000000000000, true,
                    () => fixture.BindingCurrent, () => fixture.Events.Add("source-reference-release"));
                var session = await new SourceVbeDebugAutomation(new Connector(binding), dispatchers, window)
                    .AttachOrOpenAsync(path, CancellationToken.None);
                fixture.session = session;
                fixture.transfer = session.SetNativeBreakpointsAsync;
            }
            else
            {
                var process = new FakeDebugOwnedProcess(27182,
                    new DateTime(2026, 7, 21, 10, 30, 0, DateTimeKind.Local));
                var session = await new VbeDebugAutomation(
                    new FakeExcelDebugApplicationFactory(fixture.Model.Excel),
                    new FakeDebugExcelProcessApi(27182, process), window, dispatchers)
                    .StartVisibleAsync(CancellationToken.None);
                fixture.session = session;
                fixture.transfer = session.SetNativeBreakpointsAsync;
                await session.OpenTestGenerationAsync(generation.GenerationWorkspace, CancellationToken.None);
            }
            return fixture;
        }

        internal VbeCodeModuleSourceMap Map(string module)
            => new(module, VbaModuleKind.StandardModule, Rows.ToImmutableArray());

        internal VbeBreakpoint Breakpoint(string module = "DebugModule", int line = 3)
            => new(new DebugSourceBreakpoint("file:///C:/work/" + module + ".bas", 7), Map(module), line);

        internal Task TransferAsync(IReadOnlyList<VbeBreakpoint> breakpoints, CancellationToken token)
            => transfer(breakpoints, token);

        internal async Task<DebugSetupException> RejectAsync(
            IReadOnlyList<VbeBreakpoint> breakpoints, string reason)
        {
            expectedFailure = true;
            var failure = await Assert.ThrowsAsync<DebugSetupException>(() =>
                TransferAsync(breakpoints, CancellationToken.None));
            Assert.NotNull(failure.NativeFailure);
            Assert.Equal(reason, failure.NativeFailure.Reason.ToString());
            return failure;
        }

        public async ValueTask DisposeAsync()
        {
            var failure = await Record.ExceptionAsync(() => session.DisposeAsync().AsTask());
            if (failure is not null)
            {
                Assert.True(expectedFailure || ExpectedCancellation);
                var owner = Assert.IsAssignableFrom<IDebugResourceOwnerEvidence>(session);
                Assert.NotNull(owner.CleanupOutcome);
                if (ExpectedCancellation)
                {
                    Assert.IsAssignableFrom<OperationCanceledException>(owner.CleanupOutcome.PrimaryFailure);
                    // The existing owner can retain cancelled observer cleanup as a
                    // secondary failure. Keep that evidence instead of suppressing it.
                    Assert.All(owner.CleanupOutcome.CleanupFailures, item =>
                        Assert.IsAssignableFrom<OperationCanceledException>(item.Exception));
                    if (failure is IDebugFailureEvidence evidence)
                        Assert.Same(owner.CleanupOutcome, evidence.FailureOutcome);
                    else Assert.IsAssignableFrom<OperationCanceledException>(failure);
                }
                else
                {
                    Assert.True(!owner.CleanupOutcome.HasCleanupFailure, owner.CleanupOutcome.Describe());
                    Assert.IsType<DebugSetupException>(failure);
                }
            }
            await generation.DisposeAsync();
            temp.Dispose();
        }
    }

    private sealed class Connector(SourceVbeDesktopBinding binding) : ISourceVbeDesktopConnector
    {
        public SourceVbeDesktopBinding AttachOrOpen(string exactSourceWorkbookPath) => binding;
    }
}
