using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Text;
using VbaDebugAdapter.Debugging;
using VbaDebugAdapter.Infrastructure;
using VbaTools.Syntax;
using Xunit;
using Xunit.Abstractions;

namespace VbaDebugAdapter.Tests;

[Collection(WindowsExcelIntegrationCollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class SourceWorkbookResetWindowsExcelIntegrationTests(ITestOutputHelper output)
{
    private const int BreakMode = 1;
    private const int DesignMode = 2;

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task NativeSourceResetRetainsImportedCodeUnsavedCellsAndTheOtherWorkbook()
    {
        using var temp = TempDirectory.Create();
        await using var fixture = await OwnedExcelFixture.CreateAsync(temp.Path, output.WriteLine);
        await using var session = await new SourceVbeDebugAutomation()
            .AttachOrOpenAsync(fixture.SourcePath, CancellationToken.None);
        Assert.True(session.WasAlreadyOpen);
        Assert.Equal(fixture.Identity.ProcessId, session.ProcessId);
        Assert.Equal(fixture.Identity.StartUtcTicks, session.ProcessStartUtcTicks);
        var inspection = await session.InspectAsync(CancellationToken.None);
        Assert.Equal(fixture.SourcePath, inspection.WorkbookPath, ignoreCase: true);
        Assert.False(inspection.IsSaved);
        Assert.Equal(DebugCompilationHostFactsStatus.Verified,
            (await session.GetCompilationHostFactsAsync(CancellationToken.None)).Status);

        var map = await fixture.ReadSourceMapAsync();
        var selectedLine = map.CodeLines.IndexOf(
            "    ThisWorkbook.Worksheets(1).Range(\"C1\").Value2 = \"ran\"") + 1;
        Assert.True(selectedLine > 0);
        await session.SetNativeBreakpointsAsync(
            [new VbeBreakpoint(new DebugSourceBreakpoint(new Uri(fixture.ModulePath).AbsoluteUri, selectedLine + 1),
                map, selectedLine)], CancellationToken.None);
        await session.RunTargetAsync(new DebugTargetProcedure("ResetProbe", "RunProbe"),
            null, CancellationToken.None);
        await WaitForModeAsync(session, BreakMode);

        // Both projects are fixture-owned. A visible project selection must not
        // manufacture Reset authority for the source session.
        await fixture.SelectProjectAsync(source: false);
        var refusal = await Assert.ThrowsAsync<DebugSetupException>(() =>
            session.ResetExecutionAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
        Assert.Contains("Use Reset", refusal.Message);
        Assert.Equal(BreakMode, (await session.InspectAsync(CancellationToken.None)).ProjectMode);
        await fixture.AssertOtherProjectStillSelectedAsync();
        await fixture.SelectProjectAsync(source: true);
        await session.ResetExecutionAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Equal(DesignMode, (await session.InspectAsync(CancellationToken.None)).ProjectMode);
        await fixture.AssertRetainedStateAsync();

        await session.DisposeAsync();
        Assert.Equal(SourceVbeDebugSessionEndReason.Detached, (await session.Completion).Reason);
        Assert.Null((await session.Completion).ProcessExitCode);
        AssertReleasedWithoutProcessOwnership(session);
        await fixture.AssertRetainedStateAsync();
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task CanceledBeforeCloseDoesNotEndOrReopenTheLiveSourceSession()
    {
        using var temp = TempDirectory.Create();
        await using var fixture = await OwnedExcelFixture.CreateAsync(temp.Path, output.WriteLine);
        await fixture.AddCancelCloseEventAsync();
        await using var session = await new SourceVbeDebugAutomation()
            .AttachOrOpenAsync(fixture.SourcePath, CancellationToken.None);

        await fixture.AttemptCanceledCloseAsync();
        await Task.Delay(350);

        Assert.False(session.Completion.IsCompleted);
        Assert.Equal(fixture.Identity.ProcessId, session.ProcessId);
        Assert.False((await session.InspectAsync(CancellationToken.None)).IsSaved);
        await fixture.AssertRetainedStateAsync();

        await session.DisposeAsync();
        AssertReleasedWithoutProcessOwnership(session);
        var outcome = ((IDebugResourceOwnerEvidence)session).CleanupOutcome!;
        output.WriteLine("[Source fixture lifecycle] Product detach: " + string.Join("; ",
            outcome.Evidence.Select(item => $"{item.Kind}: released={item.Released}, {item.Reason}")));
        await fixture.AssertRetainedStateAsync();
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task ActualWorkbookCloseCompletesWithoutFabricatingExcelExitOrReopeningSource()
    {
        using var temp = TempDirectory.Create();
        await using var fixture = await OwnedExcelFixture.CreateAsync(temp.Path, output.WriteLine);
        await using var session = await new SourceVbeDebugAutomation()
            .AttachOrOpenAsync(fixture.SourcePath, CancellationToken.None);

        await fixture.CloseSourceAsync();
        var completion = await session.Completion.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(SourceVbeDebugSessionEndReason.WorkbookClosed, completion.Reason);
        Assert.Null(completion.ProcessExitCode);
        await session.DisposeAsync();
        await Task.Delay(250);
        AssertReleasedWithoutProcessOwnership(session);
        await fixture.AssertOnlyOtherWorkbookRemainsAsync();
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task AcquisitionFailureAfterExactNativeBindingDoesNotCloseOrSaveEitherWorkbook()
    {
        using var temp = TempDirectory.Create();
        await using var fixture = await OwnedExcelFixture.CreateAsync(temp.Path, output.WriteLine);
        var automation = CreateAutomation(new RecordingDesktopApi(fixture.Identity));

        var failure = await Assert.ThrowsAnyAsync<Exception>(() =>
            automation.AttachOrOpenAsync(fixture.SourcePath, CancellationToken.None));

        var outcome = Assert.IsAssignableFrom<IDebugFailureEvidence>(failure).FailureOutcome;
        Assert.Contains("fixture acquisition boundary", outcome.PrimaryFailure!.Message);
        Assert.False(outcome.HasUnprovedRelease);
        await fixture.AssertRetainedStateAsync();
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task UnrelatedCompileErrorReachesAnInteractiveNativeVbePromptWithoutClosingSource()
    {
        using var temp = TempDirectory.Create();
        var fixture = await OwnedExcelFixture.CreateAsync(temp.Path, output.WriteLine);
        ISourceVbeDebugSession? session = null;
        var failures = new List<Exception>();
        try
        {
            await fixture.AddUnrelatedCompileErrorAsync();
            session = await new SourceVbeDebugAutomation()
                .AttachOrOpenAsync(fixture.SourcePath, CancellationToken.None);
            await ProveNativeCompileFixtureAsync(fixture, session);
        }
        catch (Exception failure)
        {
            failures.Add(failure);
            output.WriteLine($"Native compile proof original failure: {failure}");
        }
        finally
        {
            if (session is not null)
            {
                try { await session.DisposeAsync(); }
                catch (Exception failure) { failures.Add(failure); }
            }
            try { await fixture.DisposeAsync(); }
            catch (Exception failure) { failures.Add(failure); }
        }
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1)
            throw new AggregateException("Original native compile proof and subsequent fixture cleanup failures.", failures);
    }

    private async Task ProveNativeCompileFixtureAsync(OwnedExcelFixture fixture, ISourceVbeDebugSession session)
    {
        var sink = new NativeCompilationLifecycleSink();
        output.WriteLine($"Exact compile fixture: {fixture.Identity}.");
        var existingDialogs = CaptureFixtureDialogs(fixture.Identity);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var running = session.RunTargetAsync(new DebugTargetProcedure("ResetProbe", "RunProbe"), sink, timeout.Token);
        nint compileDialog = 0;
        DateTimeOffset? completedAt = null;
        Exception? observationFailure = null;
        try
        {
            while (compileDialog == 0)
            {
                compileDialog = CaptureFixtureDialogs(fixture.Identity).FirstOrDefault(window => !existingDialogs.Contains(window));
                if (compileDialog == 0)
                {
                    if (running.IsCompleted)
                    {
                        await running;
                        completedAt ??= DateTimeOffset.UtcNow;
                        if (DateTimeOffset.UtcNow - completedAt > TimeSpan.FromSeconds(2))
                            throw new InvalidOperationException("Native Run completed without the required compile prompt.");
                    }
                    await Task.Delay(50, timeout.Token);
                }
            }
            Assert.True(IsWindowVisible(compileDialog));
            Assert.True(IsWindowEnabled(compileDialog));
            output.WriteLine($"Exact fixture native compile dialog {compileDialog} is visible and interactive.");
            Assert.False(session.Completion.IsCompleted);
            var message = await sink.Notified.Task.WaitAsync(timeout.Token);
            Assert.Contains($"Source Excel process {fixture.Identity.ProcessId} is waiting for", message.Output);
            Assert.DoesNotContain("Owned", message.Output);
        }
        catch (OperationCanceledException failure) when (timeout.IsCancellationRequested)
        {
            var diagnostic = $"Native compile prompt was not observed for exact Excel {fixture.Identity}. " +
                $"Run status: {running.Status}; Run failure: {running.Exception}; " +
                $"Visible windows: {string.Join("; ", DescribeFixtureWindows(fixture.Identity))}";
            output.WriteLine(diagnostic);
            observationFailure = new TimeoutException(diagnostic, failure);
            throw observationFailure;
        }
        catch (Exception failure)
        {
            observationFailure = failure;
            output.WriteLine($"Compile proof failed for exact Excel {fixture.Identity}: {failure}. " +
                $"Run status: {running.Status}; Run failure: {running.Exception}; " +
                $"Visible windows: {string.Join("; ", DescribeFixtureWindows(fixture.Identity))}");
            throw;
        }
        finally
        {
            if (compileDialog != 0)
            {
                try { AcknowledgeFixtureCompileDialog(fixture.Identity, compileDialog); }
                catch (Exception failure)
                {
                    if (observationFailure is not null)
                        throw new AggregateException("Compile observation failed and fixture acknowledgment also failed.",
                            observationFailure, failure);
                    throw;
                }
            }
        }
        try { await running.WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (COMException) { } // Native command may report the acknowledged compile failure.
        await session.ResetExecutionAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        await session.DisposeAsync();
        AssertReleasedWithoutProcessOwnership(session);
        await fixture.AssertRetainedStateAsync();
        output.WriteLine("Native compile proof, source Reset/detach release proof, and unchanged A/B state completed.");
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task ClosedSourceOpensInANewVisibleUserControlledExcelRunsAndSurvivesDetach()
    {
        using var temp = TempDirectory.Create();
        string sourcePath;
        byte[] savedBaseline;
        await using (var creator = await OwnedExcelFixture.CreateAsync(temp.Path, output.WriteLine))
        {
            sourcePath = creator.SourcePath;
            savedBaseline = await creator.SaveOpenEventForClosedSourceAsync();
        }

        var existing = CaptureExcelProcessIds();
        var api = new RecordingDesktopApi();
        await using var session = await CreateAutomation(api)
            .AttachOrOpenAsync(sourcePath, CancellationToken.None);
        var identity = new SourceVbeProcessIdentity(session.ProcessId, session.ProcessStartUtcTicks);
        OwnedExcelFixture? observer = null;
        try
        {
            Assert.False(session.WasAlreadyOpen);
            Assert.DoesNotContain(identity.ProcessId, existing);
            Assert.Equal(identity, api.CreatedIdentity);
            await session.RunTargetAsync(new DebugTargetProcedure("ResetProbe", "RunProbe"),
                null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
            await session.ResetExecutionAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            await session.DisposeAsync();
            AssertReleasedWithoutProcessOwnership(session);
            Assert.Equal(SourceVbeDebugSessionEndReason.Detached, (await session.Completion).Reason);
            AssertExactProcessAlive(identity);

            // Reacquire only AFTER source COM/STA references were released, so a
            // fixture-held reference cannot make the survival assertion pass.
            observer = await OwnedExcelFixture.AdoptNewSessionAsync(sourcePath, identity, existing, output.WriteLine);
            await observer.AssertVisibleOpenSettingsAndSuppressedEventAsync(
                api.OriginalEnableEvents, api.OriginalAutomationSecurity);
            Assert.Equal(savedBaseline, ReadSavedOpenWorkbookBytes(sourcePath));
        }
        finally
        {
            await session.DisposeAsync();
            observer ??= await OwnedExcelFixture.AdoptNewSessionAsync(sourcePath, identity, existing, output.WriteLine);
            await observer.DisposeAsync();
        }
    }

    private static SourceVbeDebugAutomation CreateAutomation(ISourceVbeDesktopApi api)
        => new(new WindowsSourceVbeDesktopConnector(api, new WindowsSourceVbePhysicalIdentityReader()),
            new StaComDispatcherFactory(), new WindowsDebugWindowActivator());

    private static void AssertReleasedWithoutProcessOwnership(ISourceVbeDebugSession session)
    {
        var outcome = Assert.IsAssignableFrom<IDebugResourceOwnerEvidence>(session).CleanupOutcome;
        Assert.NotNull(outcome);
        Assert.False(outcome.HasUnprovedRelease);
        Assert.Contains(outcome.Evidence, item => item.Kind == DebugResourceKind.Process && item.Released
            && item.Reason.Contains("no lifetime ownership", StringComparison.Ordinal));
        Assert.Contains(outcome.Evidence, item => item.Kind == DebugResourceKind.Com && item.Released);
        Assert.Contains(outcome.Evidence, item => item.Kind == DebugResourceKind.Handle && item.Released);
    }

    private static async Task WaitForModeAsync(ISourceVbeDebugSession session, int mode)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while ((await session.InspectAsync(timeout.Token)).ProjectMode != mode)
            await Task.Delay(100, timeout.Token);
    }

    private static HashSet<int> CaptureExcelProcessIds()
    {
        var processes = Process.GetProcessesByName("EXCEL");
        try { return processes.Select(process => process.Id).ToHashSet(); }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static void AssertExactProcessAlive(SourceVbeProcessIdentity identity)
    {
        using var process = Process.GetProcessById(identity.ProcessId);
        Assert.Equal(identity.StartUtcTicks, process.StartTime.ToUniversalTime().Ticks);
        Assert.False(process.HasExited);
    }

    private static byte[] ReadSavedOpenWorkbookBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static HashSet<nint> CaptureFixtureDialogs(SourceVbeProcessIdentity identity)
    {
        AssertExactProcessAlive(identity);
        var windows = new HashSet<nint>();
        Assert.True(EnumDesktopWindows(nint.Zero, (window, parameter) =>
        {
            _ = GetWindowThreadProcessId(window, out var pid);
            var className = new StringBuilder(64);
            if (pid == (uint)identity.ProcessId && IsWindowVisible(window)
                && GetClassName(window, className, className.Capacity) > 0 && className.ToString() == "#32770")
                windows.Add(window);
            return true;
        }, nint.Zero));
        return windows;
    }

    private static IReadOnlyList<string> DescribeFixtureWindows(SourceVbeProcessIdentity identity)
    {
        AssertExactProcessAlive(identity);
        var windows = new List<string>();
        Assert.True(EnumDesktopWindows(nint.Zero, (window, parameter) =>
        {
            _ = GetWindowThreadProcessId(window, out var pid);
            if (pid == (uint)identity.ProcessId && IsWindowVisible(window))
            {
                var className = new StringBuilder(128);
                var title = new StringBuilder(256);
                _ = GetClassName(window, className, className.Capacity);
                _ = GetWindowText(window, title, title.Capacity);
                windows.Add($"{window}: {className} [{title}], enabled={IsWindowEnabled(window)}");
            }
            return true;
        }, nint.Zero));
        return windows;
    }

    private static void AcknowledgeFixtureCompileDialog(SourceVbeProcessIdentity identity, nint dialog)
    {
        AssertExactProcessAlive(identity);
        _ = GetWindowThreadProcessId(dialog, out var dialogPid);
        Assert.Equal((uint)identity.ProcessId, dialogPid);
        var dialogClass = new StringBuilder(64);
        _ = GetClassName(dialog, dialogClass, dialogClass.Capacity);
        Assert.Equal("#32770", dialogClass.ToString());
        Assert.True(IsWindowVisible(dialog));
        Assert.True(IsWindowEnabled(dialog));
        // VBE compiler errors expose OK as native control ID 2, Help as 9;
        // their reported DM_GETDEFID can point at nonexistent ID 1. This
        // fixture-only acknowledgment proves both controls without captions.
        foreach (var controlId in new[] { 2, 9 })
        {
            var control = GetDlgItem(dialog, controlId);
            Assert.NotEqual(nint.Zero, control);
            _ = GetWindowThreadProcessId(control, out var controlPid);
            Assert.Equal(dialogPid, controlPid);
            var controlClass = new StringBuilder(64);
            _ = GetClassName(control, controlClass, controlClass.Capacity);
            Assert.Equal("Button", controlClass.ToString());
        }
        AssertExactProcessAlive(identity);
        Assert.True(PostMessage(GetDlgItem(dialog, 2), 0x00F5, nint.Zero, nint.Zero)); // Test-owned BM_CLICK only.
    }

    private sealed class NativeCompilationLifecycleSink : IDebugLifecycleSink
    {
        internal TaskCompletionSource<DebugLifecycleMessage> Notified { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask WriteAsync(DebugLifecycleMessage message, CancellationToken token)
        {
            Notified.TrySetResult(message);
            return ValueTask.CompletedTask;
        }
    }

    private delegate bool DesktopWindowCallback(nint window, nint parameter);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDesktopWindows(nint desktop, DesktopWindowCallback callback, nint parameter);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder name, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, StringBuilder text, int capacity);
    [DllImport("user32.dll")]
    private static extern nint GetDlgItem(nint dialog, int controlId);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    private sealed class RecordingDesktopApi(SourceVbeProcessIdentity? failArchitectureFor = null) : ISourceVbeDesktopApi
    {
        private readonly WindowsSourceVbeDesktopApi inner = new();
        internal SourceVbeProcessIdentity? CreatedIdentity { get; private set; }
        internal bool OriginalEnableEvents { get; private set; }
        internal int OriginalAutomationSecurity { get; private set; }
        public IReadOnlyList<SourceVbeProcessIdentity> CaptureExcelProcesses() => inner.CaptureExcelProcesses();
        public object? TryBindApplication(SourceVbeProcessIdentity process) => inner.TryBindApplication(process);
        public object CreateApplication()
        {
            var application = inner.CreateApplication();
            // Only value metadata is retained; no extra COM reference.
            dynamic excel = application;
            CreatedIdentity = inner.ReadProcessIdentity(application);
            OriginalEnableEvents = (bool)excel.EnableEvents;
            OriginalAutomationSecurity = (int)excel.AutomationSecurity;
            return application;
        }
        public SourceVbeProcessIdentity ReadProcessIdentity(object application) => inner.ReadProcessIdentity(application);
        public DebugExcelProcessArchitecture ReadProcessArchitecture(SourceVbeProcessIdentity process)
            => failArchitectureFor == process
                ? throw new InvalidOperationException("Native fixture acquisition boundary failed after exact binding.")
                : inner.ReadProcessArchitecture(process);
        public SourceVbeDebugSessionCompletion? ReadProcessCompletion(SourceVbeProcessIdentity process)
            => inner.ReadProcessCompletion(process);
    }

    private sealed class OwnedExcelFixture : IAsyncDisposable
    {
        private readonly Action<string>? receipt;
        private readonly StaComDispatcher dispatcher = new();
        private readonly List<object> comReferences = [];
        private readonly WindowsSourceVbeDesktopApi nativeApi = new();
        private object? application;
        private object? sourceWorkbook;
        private object? otherWorkbook;
        private object? sourceProject;
        private object? otherProject;
        private object? importedComponent;
        private byte[] sourceDiskBaseline = [];
        private byte[] otherDiskBaseline = [];
        private bool ownsApplication;
        private bool sourceClosed;
        private Task? disposal;
        internal string SourcePath { get; private set; } = "";
        internal string ModulePath { get; private set; } = "";
        internal SourceVbeProcessIdentity Identity { get; private set; }

        private OwnedExcelFixture(Action<string>? receipt = null) => this.receipt = receipt;

        internal static async Task<OwnedExcelFixture> CreateAsync(string directory, Action<string>? receipt = null)
        {
            var fixture = new OwnedExcelFixture(receipt);
            var existing = CaptureExcelProcessIds();
            try
            {
                await fixture.InvokeAsync(() => fixture.Create(directory, existing));
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        internal static async Task<OwnedExcelFixture> AdoptNewSessionAsync(
            string sourcePath, SourceVbeProcessIdentity identity, IReadOnlySet<int> existing, Action<string>? receipt = null)
        {
            var fixture = new OwnedExcelFixture(receipt);
            try
            {
                await fixture.InvokeAsync(() =>
                {
                    Assert.DoesNotContain(identity.ProcessId, existing);
                    AssertExactProcessAlive(identity);
                    fixture.application = fixture.Track(fixture.nativeApi.TryBindApplication(identity)
                        ?? throw new InvalidOperationException("The exact new test Excel cannot be observed."));
                    Assert.Equal(identity, fixture.nativeApi.ReadProcessIdentity(fixture.application));
                    fixture.Identity = identity;
                    fixture.ownsApplication = true;
                    fixture.SourcePath = sourcePath;
                    receipt?.Invoke($"[Source fixture lifecycle] Adopted exact new fixture {identity}; source={sourcePath}; " +
                        $"pre-existing PIDs={string.Join(",", existing.Order())}.");
                    dynamic excel = fixture.application;
                    dynamic books = fixture.Track(excel.Workbooks);
                    Assert.Equal(1, (int)books.Count);
                    fixture.sourceWorkbook = fixture.Track(books.Item(1));
                    Assert.Equal(sourcePath, (string)((dynamic)fixture.sourceWorkbook).FullName, ignoreCase: true);
                });
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        private void Create(string directory, IReadOnlySet<int> existing)
        {
            var sourceDirectory = Path.Combine(directory, "src", "Source");
            Directory.CreateDirectory(sourceDirectory);
            SourcePath = Path.Combine(sourceDirectory, "Source.xlsm");
            var otherPath = Path.Combine(directory, "Other.xlsm");
            ModulePath = Path.Combine(sourceDirectory, "ResetProbe.bas");
            File.WriteAllText(ModulePath, "Attribute VB_Name = \"ResetProbe\"\r\nOption Explicit\r\n" +
                "Public Sub RunProbe()\r\n" +
                "    ThisWorkbook.Worksheets(1).Range(\"C1\").Value2 = \"ran\"\r\nEnd Sub\r\n", new UTF8Encoding(false));
            application = Track(nativeApi.CreateApplication());
            Identity = nativeApi.ReadProcessIdentity(application);
            Assert.DoesNotContain(Identity.ProcessId, existing);
            ownsApplication = true;
            receipt?.Invoke($"[Source fixture lifecycle] Created exact fixture {Identity}; source={SourcePath}; " +
                $"pre-existing PIDs={string.Join(",", existing.Order())}.");
            dynamic excel = application;
            excel.Visible = true;
            excel.UserControl = true;
            excel.DisplayAlerts = false;
            dynamic books = Track(excel.Workbooks);
            sourceWorkbook = Track(books.Add());
            dynamic source = sourceWorkbook;
            sourceProject = Track(source.VBProject);
            dynamic components = Track(((dynamic)sourceProject).VBComponents);
            dynamic initial = Track(components.Add(1));
            initial.Name = "ResetProbe";
            dynamic initialCode = Track(initial.CodeModule);
            initialCode.AddFromString("Option Explicit\r\nPublic Sub RunProbe()\r\nEnd Sub\r\n");
            source.SaveAs(SourcePath, 52);
            components.Remove(initial);
            otherWorkbook = Track(books.Add());
            dynamic other = otherWorkbook;
            otherProject = Track(other.VBProject);
            SetCell(otherWorkbook, "A1", "other-saved-cell");
            other.SaveAs(otherPath, 52);
            sourceDiskBaseline = ReadSavedOpenWorkbookBytes(SourcePath);
            otherDiskBaseline = ReadSavedOpenWorkbookBytes(otherPath);
            SetCell(sourceWorkbook, "A1", "unsaved-cell-edit");
            importedComponent = Track(components.Import(ModulePath));
            Assert.Equal("ResetProbe", (string)((dynamic)importedComponent).Name);
            Assert.False((bool)source.Saved);
        }

        internal Task<VbeCodeModuleSourceMap> ReadSourceMapAsync()
            => dispatcher.InvokeAsync(() =>
            {
                dynamic code = Track(((dynamic)importedComponent!).CodeModule);
                var lines = ImmutableArray.CreateBuilder<string>((int)code.CountOfLines);
                for (var line = 1; line <= (int)code.CountOfLines; line++) lines.Add((string)code.Lines(line, 1));
                return new VbeCodeModuleSourceMap("ResetProbe", VbaModuleKind.StandardModule, lines.ToImmutable());
            }, CancellationToken.None);

        internal Task SelectProjectAsync(bool source) => InvokeAsync(() =>
        {
            dynamic vbe = Track(((dynamic)application!).VBE);
            vbe.ActiveVBProject = source ? sourceProject : otherProject;
        });

        internal Task AssertOtherProjectStillSelectedAsync() => InvokeAsync(() =>
        {
            dynamic vbe = Track(((dynamic)application!).VBE);
            Assert.Same(otherProject, Track(vbe.ActiveVBProject));
        });

        internal Task AssertRetainedStateAsync() => InvokeAsync(() =>
        {
            AssertExactProcessAlive(Identity);
            dynamic books = Track(((dynamic)application!).Workbooks);
            Assert.Equal(2, (int)books.Count);
            dynamic source = sourceWorkbook!;
            dynamic other = otherWorkbook!;
            Assert.Equal(SourcePath, (string)source.FullName, ignoreCase: true);
            Assert.Equal("unsaved-cell-edit", ReadCell(sourceWorkbook!, "A1"));
            Assert.False((bool)source.Saved);
            dynamic code = Track(((dynamic)importedComponent!).CodeModule);
            Assert.Contains("ThisWorkbook.Worksheets", (string)code.Lines(1, (int)code.CountOfLines));
            Assert.Equal("other-saved-cell", ReadCell(otherWorkbook!, "A1"));
            Assert.True((bool)other.Saved);
            Assert.Equal(sourceDiskBaseline, ReadSavedOpenWorkbookBytes(SourcePath));
            Assert.Equal(otherDiskBaseline, ReadSavedOpenWorkbookBytes((string)other.FullName));
        });

        internal Task AddCancelCloseEventAsync() => InvokeAsync(() => AddWorkbookEvent(
            "Private Sub Workbook_BeforeClose(Cancel As Boolean)\r\n    Cancel = True\r\nEnd Sub\r\n"));

        internal Task AddUnrelatedCompileErrorAsync() => InvokeAsync(() =>
        {
            dynamic components = Track(((dynamic)sourceProject!).VBComponents);
            dynamic broken = Track(components.Add(1));
            broken.Name = "UnrelatedNativeError";
            dynamic code = Track(broken.CodeModule);
            code.AddFromString("Option Explicit\r\nPublic InvalidGlobal As MissingNativeFixtureType\r\n" +
                "Public Function NativeFixtureDependency() As Long\r\nEnd Function\r\n");
            // Native VBA compilation is incremental. Reach the other module
            // from the valid target without moving its invalid declaration
            // inside the target or adding a product-side compile gate.
            dynamic targetCode = Track(((dynamic)importedComponent!).CodeModule);
            targetCode.InsertLines((int)targetCode.ProcBodyLine("RunProbe", 0) + 1,
                "    Debug.Print UnrelatedNativeError.NativeFixtureDependency()");
        });

        internal Task AttemptCanceledCloseAsync() => InvokeAsync(() =>
        {
            dynamic excel = application!;
            excel.EnableEvents = true;
            ((dynamic)sourceWorkbook!).Close(false);
            dynamic books = Track(excel.Workbooks);
            Assert.Equal(2, (int)books.Count);
        });

        internal Task CloseSourceAsync() => InvokeAsync(() =>
        {
            ((dynamic)sourceWorkbook!).Close(false);
            sourceClosed = true;
        });

        internal Task AssertOnlyOtherWorkbookRemainsAsync() => InvokeAsync(() =>
        {
            AssertExactProcessAlive(Identity);
            dynamic books = Track(((dynamic)application!).Workbooks);
            Assert.Equal(1, (int)books.Count);
            dynamic remaining = Track(books.Item(1));
            dynamic other = otherWorkbook!;
            Assert.Equal((string)other.FullName, (string)remaining.FullName, ignoreCase: true);
            Assert.Equal("other-saved-cell", ReadCell(otherWorkbook!, "A1"));
            Assert.True((bool)other.Saved);
            Assert.Equal(sourceDiskBaseline, ReadSavedOpenWorkbookBytes(SourcePath));
            Assert.Equal(otherDiskBaseline, ReadSavedOpenWorkbookBytes((string)other.FullName));
        });

        internal Task<byte[]> SaveOpenEventForClosedSourceAsync() => dispatcher.InvokeAsync(() =>
        {
            AddWorkbookEvent("Private Sub Workbook_Open()\r\n" +
                "    Me.Worksheets(1).Range(\"D1\").Value2 = \"open-event-ran\"\r\nEnd Sub\r\n");
            ((dynamic)sourceWorkbook!).Save(); // Fixture creation, not product action.
            return ReadSavedOpenWorkbookBytes(SourcePath);
        }, CancellationToken.None);

        internal Task AssertVisibleOpenSettingsAndSuppressedEventAsync(bool events, int security) => InvokeAsync(() =>
        {
            AssertExactProcessAlive(Identity);
            dynamic excel = application!;
            Assert.True((bool)excel.Visible);
            Assert.True((bool)excel.UserControl);
            Assert.Equal(events, (bool)excel.EnableEvents);
            Assert.Equal(security, (int)excel.AutomationSecurity);
            dynamic books = Track(excel.Workbooks);
            Assert.Equal(1, (int)books.Count);
            Assert.Equal(SourcePath, (string)((dynamic)sourceWorkbook!).FullName, ignoreCase: true);
            Assert.Null(ReadCell(sourceWorkbook!, "D1"));
            Assert.Equal("ran", ReadCell(sourceWorkbook!, "C1"));
        });

        private void AddWorkbookEvent(string text)
        {
            dynamic components = Track(((dynamic)sourceProject!).VBComponents);
            dynamic module = Track(components.Item((string)((dynamic)sourceWorkbook!).CodeName));
            dynamic code = Track(module.CodeModule);
            code.AddFromString(text);
        }

        private void SetCell(object book, string address, string value)
        {
            dynamic sheets = Track(((dynamic)book).Worksheets);
            dynamic sheet = Track(sheets.Item(1));
            dynamic range = Track(sheet.Range(address));
            range.Value2 = value;
        }

        private object? ReadCell(object book, string address)
        {
            dynamic sheets = Track(((dynamic)book).Worksheets);
            dynamic sheet = Track(sheets.Item(1));
            dynamic range = Track(sheet.Range(address));
            return range.Value2;
        }

        private object Track(object value)
        {
            if (Marshal.IsComObject(value)) comReferences.Add(value);
            return value;
        }

        private Task InvokeAsync(Action operation)
            => dispatcher.InvokeAsync(() => { operation(); return true; }, CancellationToken.None);

        public ValueTask DisposeAsync() => new(disposal ??= DisposeOnceAsync());

        private async Task DisposeOnceAsync()
        {
            var failures = new List<Exception>();
            var booklessQuitVerified = false;
            try
            {
                await InvokeAsync(() =>
                {
                    void Cleanup(Action operation)
                    {
                        try { operation(); }
                        catch (Exception failure) { failures.Add(failure); }
                    }
                    if (ownsApplication && application is not null)
                    {
                        // Test lifetime authority follows a fresh exact PID/start
                        // proof, not the product's borrowed/source COM reference.
                        AssertExactProcessAlive(Identity);
                        dynamic excel = application;
                        var projectMode = sourceProject is null ? "not captured" : ((int)((dynamic)sourceProject).Mode).ToString();
                        receipt?.Invoke($"[Source fixture lifecycle] Cleanup exact fixture {Identity}; " +
                            $"mode={projectMode}; refs={comReferences.Count}.");
                        excel.EnableEvents = false;
                        excel.DisplayAlerts = false;
                        receipt?.Invoke($"[Source fixture lifecycle] Events={(bool)excel.EnableEvents}; alerts={(bool)excel.DisplayAlerts}.");
                        Cleanup(() => ResetOwnedFixtureIfNeeded(excel));
                        if (otherWorkbook is not null) Cleanup(() => ((dynamic)otherWorkbook).Close(false));
                        Cleanup(() => RecordWorkbookInventory(excel, "after other Close"));
                        if (sourceWorkbook is not null && !sourceClosed) Cleanup(() => ((dynamic)sourceWorkbook).Close(false));
                        var allBooksClosed = false;
                        Cleanup(() => allBooksClosed = RecordWorkbookInventory(excel, "after source Close") == 0);
                        var quitReturned = false;
                        if (allBooksClosed)
                        {
                            Cleanup(() => { excel.Quit(); quitReturned = true; });
                            booklessQuitVerified = quitReturned;
                        }
                        else failures.Add(new InvalidOperationException("Fixture workbooks remain; application Quit was not authorized."));
                        receipt?.Invoke($"[Source fixture lifecycle] Quit returned={quitReturned}; fixture failures={failures.Count}.");
                    }
                    var releasedFixtureObjects = new HashSet<object>(ReferenceEqualityComparer.Instance);
                    for (var index = comReferences.Count - 1; index >= 0; index--)
                    {
                        var reference = comReferences[index];
                        if (ownsApplication)
                        {
                            // Product detach was verified first. This fixture
                            // owns the fresh Excel lifetime and may now release
                            // its own shared RCWs completely after fixture Quit.
                            if (releasedFixtureObjects.Add(reference))
                                Cleanup(() => { _ = Marshal.FinalReleaseComObject(reference); });
                        }
                        else Cleanup(() => { _ = Marshal.ReleaseComObject(reference); });
                    }
                    comReferences.Clear();
                    application = null;
                    sourceWorkbook = null;
                    otherWorkbook = null;
                    sourceProject = null;
                    otherProject = null;
                    importedComponent = null;
                    receipt?.Invoke("[Source fixture lifecycle] Fixture-held workbook/project/component aliases cleared.");
                    receipt?.Invoke($"[Source fixture lifecycle] Released unique fixture RCWs={releasedFixtureObjects.Count}; failures={failures.Count}.");
                }).WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (Exception failure) { failures.Add(failure); }
            try { await dispatcher.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception failure) { failures.Add(failure); }
            receipt?.Invoke($"[Source fixture lifecycle] Fixture STA retired={dispatcher.ReleaseVerified}; failures={failures.Count}.");
            if (ownsApplication)
            {
                try
                {
                    using var process = Process.GetProcessById(Identity.ProcessId);
                    if (!process.HasExited)
                    {
                        Assert.Equal(Identity.StartUtcTicks, process.StartTime.ToUniversalTime().Ticks);
                        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
                        catch (TimeoutException normalQuitTimeout) when (booklessQuitVerified && dispatcher.ReleaseVerified && failures.Count == 0)
                        {
                            // Native product retention/release assertions precede
                            // this OWNED TEST lifetime action. It proves harness
                            // isolation, not normal Quit or product process exit.
                            receipt?.Invoke($"[Source fixture lifecycle] Normal fixture Quit exit exceeded 15 seconds for {Identity}; " +
                                "using exact-owned, bookless harness isolation only.");
                            try
                            {
                                var harnessTerminationRequested = false;
                                try
                                {
                                    process.Refresh();
                                    if (!process.HasExited)
                                    {
                                        Assert.Equal("EXCEL", process.ProcessName, ignoreCase: true);
                                        Assert.Equal(Identity.StartUtcTicks, process.StartTime.ToUniversalTime().Ticks);
                                        Assert.Equal(nint.Zero, process.MainWindowHandle);
                                        process.Kill(entireProcessTree: false);
                                        harnessTerminationRequested = true;
                                    }
                                }
                                catch (InvalidOperationException) when (process.HasExited) { }
                                catch (Win32Exception) when (process.HasExited) { }
                                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                                Assert.True(process.HasExited);
                                receipt?.Invoke(harnessTerminationRequested
                                    ? $"[Source fixture lifecycle] Exact-owned harness termination confirmed for {Identity}; the preceding product survival proof is unchanged."
                                    : $"[Source fixture lifecycle] Exact fixture {Identity} exited naturally before harness termination.");
                            }
                            catch (Exception isolationFailure)
                            {
                                throw new AggregateException("Normal fixture Quit timed out and exact-owned isolation failed.",
                                    normalQuitTimeout, isolationFailure);
                            }
                        }
                    }
                    receipt?.Invoke($"[Source fixture lifecycle] Exact fixture {Identity} exited.");
                }
                catch (ArgumentException) { receipt?.Invoke($"[Source fixture lifecycle] Exact fixture {Identity} no longer exists."); }
                catch (Exception failure) { failures.Add(failure); }
            }
            if (failures.Count > 0) throw new AggregateException("Test-owned Excel cleanup was not proved.", failures);
        }

        private int RecordWorkbookInventory(dynamic excel, string stage)
        {
            object booksObject = excel.Workbooks;
            var paths = new List<string>();
            try
            {
                dynamic books = booksObject;
                for (var index = 1; index <= (int)books.Count; index++)
                {
                    object bookObject = books.Item(index);
                    try { paths.Add((string)((dynamic)bookObject).FullName); }
                    finally { _ = Marshal.ReleaseComObject(bookObject); }
                }
            }
            finally { _ = Marshal.ReleaseComObject(booksObject); }
            receipt?.Invoke($"[Source fixture lifecycle] {stage}: count={paths.Count}; paths={string.Join("; ", paths)}.");
            return paths.Count;
        }

        private void ResetOwnedFixtureIfNeeded(dynamic excel)
        {
            if (sourceProject is null || sourceClosed || (int)((dynamic)sourceProject).Mode == DesignMode) return;
            dynamic vbe = Track(excel.VBE);
            vbe.ActiveVBProject = sourceProject;
            dynamic bars = Track(vbe.CommandBars);
            dynamic command = Track(bars.FindControl(1, 228, Type.Missing, false));
            Assert.Equal(228, (int)command.Id);
            Assert.True((bool)command.BuiltIn);
            Assert.True((bool)command.Enabled);
            command.Execute();
        }
    }
}
