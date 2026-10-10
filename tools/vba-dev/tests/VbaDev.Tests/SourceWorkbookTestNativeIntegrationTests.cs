using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using VbaDev.App.Cli;
using VbaDev.App.Projects;
using VbaDev.App.Testing;
using VbaDev.App.Workbooks;
using VbaDev.Cli;
using VbaDev.Composition;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;
using VbaDev.Infrastructure.Debugging;
using VbaDev.Infrastructure.Workbooks;
using Xunit;
using Xunit.Abstractions;

namespace VbaDev.Tests;

[Collection(WindowsExcelIntegrationCollection.Name)]
public sealed class SourceWorkbookTestNativeIntegrationTests(ITestOutputHelper output)
{
    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task ClosedPublicTestImportsRunsHiddenOnExactSourceAndDiscardsWithoutSaving()
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var context = CreateProject(temp.CreateDirectory("Project"), "O'Brien");
        var sourcePath = WriteExternalProbe(context, "imported");
        var originalWorkbook = File.ReadAllBytes(context.TemplateDocumentPath);
        var originalSource = File.ReadAllBytes(sourcePath);
        var initialProcesses = CaptureExcelProcessIds();
        var pathRunner = new ForbiddenPathRunner();
        var commandLine = CreatePublicCommandLine(context.ProjectRoot, pathRunner);

        await ExcelIntegrationScenario.RunAsync(async () =>
        {
            var result = await InvokeHiddenAsync(commandLine,
                ["test", "--interactive", "false", "--format", "ndjson"], cancellation.Token);

            AssertPassedIdentity(result, "imported", context.TemplateDocumentPath, visible: null);
            Assert.Equal(0, pathRunner.Calls);
            Assert.Equal(originalWorkbook, File.ReadAllBytes(context.TemplateDocumentPath));
            Assert.Equal(originalSource, File.ReadAllBytes(sourcePath));
            Assert.False(File.Exists(context.BinDocumentPath));
            using var unlocked = File.Open(context.TemplateDocumentPath, FileMode.Open,
                FileAccess.ReadWrite, FileShare.None);
        },
        () => { },
        () => WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20)));
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task ClosedPublicTestAllowsOneExplicitVbaSaveAndStillReadsResultsBeforeDiscardClose()
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var context = CreateProject(temp.CreateDirectory("Project"));
        var sourcePath = Path.Combine(context.DocumentSourceSetPath, "NativeUnit.bas");
        File.WriteAllText(sourcePath, ProbeCode("explicit-save", includeAttribute: true, saveWorkbook: true),
            new UTF8Encoding(false));
        var originalWorkbook = File.ReadAllBytes(context.TemplateDocumentPath);
        var originalSource = File.ReadAllBytes(sourcePath);
        var initialProcesses = CaptureExcelProcessIds();
        var pathRunner = new ForbiddenPathRunner();
        var commandLine = CreatePublicCommandLine(context.ProjectRoot, pathRunner);

        await ExcelIntegrationScenario.RunAsync(async () =>
        {
            var result = await InvokeHiddenAsync(commandLine,
                ["test", "--interactive", "false", "--format", "ndjson"], cancellation.Token);

            AssertPassedIdentity(result, "explicit-save", context.TemplateDocumentPath, visible: null);
            Assert.Equal(0, pathRunner.Calls);
            Assert.False(originalWorkbook.AsSpan().SequenceEqual(File.ReadAllBytes(context.TemplateDocumentPath)));
            Assert.Equal(originalSource, File.ReadAllBytes(sourcePath));
            Assert.False(File.Exists(context.BinDocumentPath));
            using var unlocked = File.Open(context.TemplateDocumentPath, FileMode.Open,
                FileAccess.ReadWrite, FileShare.None);
        },
        () => { },
        () => WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20)));

        var explicitlySavedWorkbook = File.ReadAllBytes(context.TemplateDocumentPath);
        NativeExcelFixture? persisted = null;
        await ExcelIntegrationScenario.RunAsync(() =>
        {
            persisted = new NativeExcelFixture(context.TemplateDocumentPath);
            Assert.Equal(1, persisted.RunCount);
            Assert.Equal("explicit-save-side-effect", persisted.ReadSideEffect());
            Assert.Contains("\"explicit-save|\"", persisted.ReadModuleCode(), StringComparison.Ordinal);
            Assert.True(persisted.IsSaved);
            return Task.CompletedTask;
        },
        () => DisposeFixtures(persisted),
        () => WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20)));
        Assert.Equal(explicitlySavedWorkbook, File.ReadAllBytes(context.TemplateDocumentPath));
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task BorrowedPublicTestDeclinesThenImportsWithoutSavingOrInvalidatingCallerComReferences()
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var context = CreateProject(temp.CreateDirectory("Project"));
        var otherPath = Path.Combine(temp.CreateDirectory("Unrelated"), "Other.xlsm");
        CreateEmptyMacroEnabledWorkbook(otherPath);
        var initialProcesses = CaptureExcelProcessIds();
        NativeExcelFixture? source = null;
        NativeExcelFixture? other = null;
        byte[]? originalWorkbook = null;
        var originalOther = File.ReadAllBytes(otherPath);

        await ExcelIntegrationScenario.RunAsync(async () =>
        {
            source = new NativeExcelFixture(context.TemplateDocumentPath);
            source.SetModuleCode(ProbeCode("saved", includeAttribute: false));
            source.SaveSetup();
            originalWorkbook = ReadOpenWorkbookBytes(context.TemplateDocumentPath);
            source.SetVisible(true);
            source.SetHeldMarker("source-dirty");
            var originalCode = source.ReadModuleCode();
            Assert.False(source.IsSaved);

            other = new NativeExcelFixture(otherPath, source);
            other.SetHeldMarker("unrelated-dirty");
            var originalSettings = source.CaptureSettings();
            var otherSettings = other.CaptureSettings();
            Assert.Equal(otherPath, originalSettings.ActiveWorkbookPath);
            Assert.Equal(otherPath, otherSettings.ActiveWorkbookPath);
            Assert.Equal(source.ProcessId, other.ProcessId);
            var commandProcesses = CaptureExcelProcessIds();
            var sourcePath = WriteExternalProbe(context, "imported");
            var externalBytes = File.ReadAllBytes(sourcePath);
            var pathRunner = new ForbiddenPathRunner();
            var commandLine = CreatePublicCommandLine(context.ProjectRoot, pathRunner);

            var declined = await InvokeAsync(commandLine,
                ["test", "--format", "ndjson"], "n\n", cancellation.Token);

            Assert.Equal(1, declined.ExitCode);
            Assert.Empty(declined.StandardOutput);
            Assert.Contains("[y/N]", declined.StandardError, StringComparison.Ordinal);
            Assert.Equal(originalCode, source.ReadModuleCode());
            Assert.Equal(0, source.RunCount);
            source.AssertRetained(originalSettings, "source-dirty");
            other.AssertRetained(otherSettings, "unrelated-dirty");
            Assert.Equal(0, other.RunCount);
            Assert.True(commandProcesses.SetEquals(CaptureExcelProcessIds()));

            var accepted = await InvokeAsync(commandLine,
                ["test", "--format", "ndjson"], "yes\n", cancellation.Token);

            AssertPassedIdentity(accepted, "imported", context.TemplateDocumentPath, visible: true);
            Assert.Contains("[y/N]", accepted.StandardError, StringComparison.Ordinal);
            Assert.Equal(1, source.RunCount);
            Assert.Contains("\"imported|\"", source.ReadModuleCode(), StringComparison.Ordinal);
            source.AssertRetained(originalSettings, "source-dirty", "UNIT_TEST_SHEET");
            other.AssertRetained(otherSettings, "unrelated-dirty");
            Assert.Equal(0, other.RunCount);
            Assert.True(commandProcesses.SetEquals(CaptureExcelProcessIds()));
            Assert.Equal(0, pathRunner.Calls);
            Assert.Equal(originalWorkbook, ReadOpenWorkbookBytes(context.TemplateDocumentPath));
            Assert.Equal(originalOther, ReadOpenWorkbookBytes(otherPath));
            Assert.Equal(externalBytes, File.ReadAllBytes(sourcePath));
            Assert.False(File.Exists(context.BinDocumentPath));
        },
        () => DisposeFixtures(other, source),
        () => WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20)));

        Assert.NotNull(originalWorkbook);
        Assert.Equal(originalWorkbook, File.ReadAllBytes(context.TemplateDocumentPath));
        Assert.Equal(originalOther, File.ReadAllBytes(otherPath));
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task BorrowedNoBuildRunsCurrentLiveVbaWithoutReadingInvalidExternalSourceOrSaving()
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var context = CreateProject(temp.CreateDirectory("Project"));
        var initialProcesses = CaptureExcelProcessIds();
        NativeExcelFixture? source = null;
        byte[]? originalWorkbook = null;

        await ExcelIntegrationScenario.RunAsync(async () =>
        {
            source = new NativeExcelFixture(context.TemplateDocumentPath);
            source.SetModuleCode(ProbeCode("saved", includeAttribute: false));
            source.SaveSetup();
            originalWorkbook = ReadOpenWorkbookBytes(context.TemplateDocumentPath);
            source.SetModuleCode(ProbeCode("live", includeAttribute: false));
            source.SetHeldMarker("live-user-cell");
            var originalCode = source.ReadModuleCode();
            var settings = source.CaptureSettings();
            var sourcePath = Path.Combine(context.DocumentSourceSetPath, "NativeUnit.bas");
            File.WriteAllText(sourcePath, "this is invalid external VBA", new UTF8Encoding(false));
            var externalBytes = File.ReadAllBytes(sourcePath);
            var pathRunner = new ForbiddenPathRunner();
            var commandLine = CreatePublicCommandLine(context.ProjectRoot, pathRunner);

            var result = await InvokeAsync(commandLine,
                ["test", "--no-build", "--format", "ndjson"], "y\n", cancellation.Token);

            AssertPassedIdentity(result, "live", context.TemplateDocumentPath, visible: settings.Visible);
            Assert.Contains("current VBA", result.StandardError, StringComparison.Ordinal);
            Assert.Contains("Source locations were omitted", result.StandardError, StringComparison.Ordinal);
            using var finished = ReadFinished(result);
            Assert.False(finished.RootElement.TryGetProperty("location", out _));
            Assert.Equal(originalCode, source.ReadModuleCode());
            Assert.Equal(1, source.RunCount);
            source.AssertRetained(settings, "live-user-cell", "UNIT_TEST_SHEET");
            Assert.Equal(0, pathRunner.Calls);
            Assert.Equal(originalWorkbook, ReadOpenWorkbookBytes(context.TemplateDocumentPath));
            Assert.Equal(externalBytes, File.ReadAllBytes(sourcePath));
            Assert.False(File.Exists(context.BinDocumentPath));
        },
        () => DisposeFixtures(source),
        () => WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20)));

        Assert.NotNull(originalWorkbook);
        Assert.Equal(originalWorkbook, File.ReadAllBytes(context.TemplateDocumentPath));
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task ClosedNoBuildRunsSavedSourceVbaHiddenWithoutImportingExternalSourceOrSaving()
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var context = CreateProject(temp.CreateDirectory("Project"));
        var initialProcesses = CaptureExcelProcessIds();
        NativeExcelFixture? seed = null;
        await ExcelIntegrationScenario.RunAsync(() =>
        {
            seed = new NativeExcelFixture(context.TemplateDocumentPath);
            seed.SetModuleCode(ProbeCode("saved", includeAttribute: false));
            seed.SaveSetup();
            return Task.CompletedTask;
        },
        () => DisposeFixtures(seed),
        () => WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20)));

        var originalWorkbook = File.ReadAllBytes(context.TemplateDocumentPath);
        var sourcePath = Path.Combine(context.DocumentSourceSetPath, "NativeUnit.bas");
        File.WriteAllText(sourcePath, "this is invalid external VBA", new UTF8Encoding(false));
        var externalBytes = File.ReadAllBytes(sourcePath);
        var pathRunner = new ForbiddenPathRunner();
        var commandLine = CreatePublicCommandLine(context.ProjectRoot, pathRunner);

        await ExcelIntegrationScenario.RunAsync(async () =>
        {
            var result = await InvokeHiddenAsync(commandLine,
                ["test", "--no-build", "--interactive", "false", "--format", "ndjson"], cancellation.Token);

            AssertPassedIdentity(result, "saved", context.TemplateDocumentPath, visible: null);
            Assert.DoesNotContain("[y/N]", result.StandardError, StringComparison.Ordinal);
            using var finished = ReadFinished(result);
            Assert.False(finished.RootElement.TryGetProperty("location", out _));
            Assert.Equal(0, pathRunner.Calls);
            Assert.Equal(originalWorkbook, File.ReadAllBytes(context.TemplateDocumentPath));
            Assert.Equal(externalBytes, File.ReadAllBytes(sourcePath));
            Assert.False(File.Exists(context.BinDocumentPath));
        },
        () => { },
        () => WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20)));
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task FailedNativeTestRetainsImportedVbaAndSideEffectsWithoutRollbackReplayOrSave()
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var context = CreateProject(temp.CreateDirectory("Project"));
        var initialProcesses = CaptureExcelProcessIds();
        NativeExcelFixture? source = null;
        byte[]? originalWorkbook = null;

        await ExcelIntegrationScenario.RunAsync(async () =>
        {
            source = new NativeExcelFixture(context.TemplateDocumentPath);
            source.SetModuleCode(ProbeCode("saved", includeAttribute: false));
            source.SaveSetup();
            originalWorkbook = ReadOpenWorkbookBytes(context.TemplateDocumentPath);
            source.SetHeldMarker("original-user-cell");
            var settings = source.CaptureSettings();
            WriteExternalProbe(context, "failed-import", outcome: "NG");
            var pathRunner = new ForbiddenPathRunner();
            var commandLine = CreatePublicCommandLine(context.ProjectRoot, pathRunner);

            var result = await InvokeAsync(commandLine,
                ["test", "--format", "ndjson"], "y\n", cancellation.Token);

            output.WriteLine(result.StandardOutput);
            output.WriteLine(result.StandardError);
            Assert.Equal(1, result.ExitCode);
            using var finished = ReadFinished(result);
            Assert.Equal("failed", finished.RootElement.GetProperty("outcome").GetString());
            Assert.Equal($"failed-import|{context.TemplateDocumentPath}|{settings.Visible}|1",
                finished.RootElement.GetProperty("message").GetString());
            Assert.Equal(1, source.RunCount);
            Assert.Equal("failed-import-side-effect", source.ReadSideEffect());
            Assert.Contains("\"failed-import|\"", source.ReadModuleCode(), StringComparison.Ordinal);
            Assert.DoesNotContain("\"saved|\"", source.ReadModuleCode(), StringComparison.Ordinal);
            source.AssertRetained(settings, "original-user-cell", "UNIT_TEST_SHEET");
            Assert.Equal(0, pathRunner.Calls);
            Assert.Equal(originalWorkbook, ReadOpenWorkbookBytes(context.TemplateDocumentPath));
            Assert.False(File.Exists(context.BinDocumentPath));
        },
        () => DisposeFixtures(source),
        () => WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20)));

        Assert.NotNull(originalWorkbook);
        Assert.Equal(originalWorkbook, File.ReadAllBytes(context.TemplateDocumentPath));
    }

    private static ResolvedProjectContext CreateProject(string root, string documentName = "Book1")
    {
        var manifestStore = new JsonProjectManifestStore();
        manifestStore.Save(root, ProjectManifest.CreateDefault("SourceTestNativeProject", documentName, root, null));
        var context = new ProjectContextResolver(manifestStore).Resolve(new(root, documentName, root));
        Directory.CreateDirectory(context.DocumentSourceSetPath);
        CreateEmptyMacroEnabledWorkbook(context.TemplateDocumentPath);
        return context;
    }

    private static VbaDevCommandLine CreatePublicCommandLine(string root, ForbiddenPathRunner runner)
        => VbaDevCommandLine.Create(ToolingCompositionRoot.CreateApplicationComposition(root,
            environmentDiagnosticPort: new FakeEnvironmentDiagnosticPort(),
            workbookTestRunner: runner,
            persistSourceAnalysisFailureEvidence: false));

    private static async Task<CommandResult> InvokeAsync(VbaDevCommandLine commandLine,
        string[] arguments, string answer, CancellationToken cancellationToken)
    {
        using var input = new MemoryStream(Encoding.ASCII.GetBytes(answer));
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        var exitCode = await commandLine.InvokeAsync(arguments, input, standardOutput, standardError,
            cancellationToken);
        return new CommandResult(exitCode, standardOutput.ToString(), standardError.ToString());
    }

    private async Task<CommandResult> InvokeHiddenAsync(VbaDevCommandLine commandLine,
        string[] arguments, CancellationToken cancellationToken)
    {
        await using var desktopObservation = CallerDesktopObservation.Start();
        CommandResult? result = null;
        await ExcelIntegrationScenario.RunAsync(async () =>
        {
            result = await InvokeAsync(commandLine, arguments, "", cancellationToken);
        },
        () => { },
        () => desktopObservation.StopAsync());
        desktopObservation.AssertNoOwnedVisibleWindow(output);
        return result ?? throw new InvalidOperationException("The observed public Test invocation produced no result.");
    }

    private void AssertPassedIdentity(CommandResult result, string marker, string sourcePath, bool? visible)
    {
        output.WriteLine(result.StandardOutput);
        output.WriteLine(result.StandardError);
        Assert.True(result.ExitCode == 0, result.StandardError);
        using var finished = ReadFinished(result);
        Assert.Equal("passed", finished.RootElement.GetProperty("outcome").GetString());
        var fields = Assert.IsType<string>(finished.RootElement.GetProperty("message").GetString()).Split('|');
        Assert.Equal(4, fields.Length);
        Assert.Equal(marker, fields[0]);
        Assert.Equal(sourcePath, fields[1]);
        Assert.True(bool.TryParse(fields[2], out var actualVisible), "The macro must report Application.Visible.");
        output.WriteLine($"Macro Application.Visible={actualVisible}; closed-workbook UX is checked on the caller desktop.");
        if (visible.HasValue) Assert.Equal(visible.Value, actualVisible);
        Assert.Equal("1", fields[3]);
    }

    private static JsonDocument ReadFinished(CommandResult result)
        => JsonDocument.Parse(Assert.Single(result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            line => line.Contains("\"type\":\"testFinished\"", StringComparison.Ordinal)));

    private static byte[] ReadOpenWorkbookBytes(string path)
    {
        using var workbook = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var bytes = new MemoryStream();
        workbook.CopyTo(bytes);
        return bytes.ToArray();
    }

    // Application.Visible may be true on the private automation desktop. Observe
    // the caller desktop throughout the actual CLI invocation instead of treating
    // that COM property as evidence of user-visible UI.
    private sealed class CallerDesktopObservation : IAsyncDisposable
    {
        private readonly WindowsDesktopWindowObservationNativeApi nativeApi;
        private readonly DesktopWindowObservationScope callerDesktop;
        private readonly IReadOnlySet<int> originalProcesses;
        private readonly ConcurrentDictionary<int, byte> invocationProcesses = new();
        private readonly ConcurrentQueue<DesktopWindowSnapshot> windows = new();
        private readonly CancellationTokenSource cancellation = new();
        private readonly Task sampling;
        private int stopped;

        private CallerDesktopObservation(WindowsDesktopWindowObservationNativeApi nativeApi,
            DesktopWindowObservationScope callerDesktop, IReadOnlySet<int> originalProcesses)
        {
            this.nativeApi = nativeApi;
            this.callerDesktop = callerDesktop;
            this.originalProcesses = originalProcesses;
            Capture();
            sampling = SampleAsync();
        }

        internal static CallerDesktopObservation Start()
        {
            var nativeApi = WindowsDesktopWindowObservationNativeApi.Instance;
            return new(nativeApi, nativeApi.CaptureCurrentThreadDesktop(), CaptureExcelProcessIds());
        }

        internal void AssertNoOwnedVisibleWindow(ITestOutputHelper output)
        {
            var processIds = invocationProcesses.Keys.Order().ToArray();
            Assert.NotEmpty(processIds);
            var callerWindows = windows.ToArray();
            output.WriteLine($"Caller desktop {callerDesktop.QualifiedName}; invocation Excel PIDs " +
                $"{string.Join(",", processIds)}; observed caller windows {callerWindows.Length}.");
            Assert.DoesNotContain(callerWindows,
                window => invocationProcesses.ContainsKey(window.ProcessId) && window.IsVisible);
        }

        private void Capture()
        {
            foreach (var processId in CaptureExcelProcessIds())
                if (!originalProcesses.Contains(processId)) invocationProcesses.TryAdd(processId, 0);
            foreach (var window in nativeApi.EnumerateTopLevelWindows(callerDesktop))
                if (invocationProcesses.ContainsKey(window.ProcessId)) windows.Enqueue(window);
        }

        private async Task SampleAsync()
        {
            while (true)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                Capture();
                await Task.Delay(10, cancellation.Token).ConfigureAwait(false);
            }
        }

        internal async Task StopAsync()
        {
            if (Interlocked.Exchange(ref stopped, 1) != 0) return;
            cancellation.Cancel();
            try { await sampling.ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            finally { cancellation.Dispose(); }
        }

        public async ValueTask DisposeAsync() => await StopAsync();
    }

    private static string WriteExternalProbe(ResolvedProjectContext context, string marker, string outcome = "OK")
    {
        var path = Path.Combine(context.DocumentSourceSetPath, "NativeUnit.bas");
        File.WriteAllText(path, ProbeCode(marker, includeAttribute: true, outcome), new UTF8Encoding(false));
        return path;
    }

    private static string ProbeCode(string marker, bool includeAttribute, string outcome = "OK",
        bool saveWorkbook = false)
        => (includeAttribute ? "Attribute VB_Name = \"NativeUnit\"\r\n" : string.Empty) + $$"""
            Option Explicit

            Public Sub UnitTestMain()
                Dim ResultSheet As Object
                Set ResultSheet = ThisWorkbook.Worksheets(1)
                ResultSheet.Name = "UNIT_TEST_SHEET"
                ResultSheet.Cells(7, 7).Value2 = Val(ResultSheet.Cells(7, 7).Value2) + 1
                ResultSheet.Cells(8, 8).Value2 = "{{marker}}-side-effect"
                ResultSheet.Cells(1, 1).Value2 = "Module"
                ResultSheet.Cells(2, 1).Value2 = "NativeUnit"
                ResultSheet.Cells(2, 2).Value2 = "UnitTestMain"
                ResultSheet.Cells(2, 3).Value2 = "{{outcome}}"
                ResultSheet.Cells(2, 4).Value2 = "{{marker}}|" & ThisWorkbook.FullName & "|" & CStr(Application.Visible) & "|" & CStr(ResultSheet.Cells(7, 7).Value2)
            {{(saveWorkbook ? "    ThisWorkbook.Save" : string.Empty)}}
            End Sub
            """.ReplaceLineEndings("\r\n") + "\r\n";

    private sealed class ForbiddenPathRunner : IWorkbookTestRunner
    {
        internal int Calls { get; private set; }

        public Task<IReadOnlyList<WorkbookTestResultRow>> RunTestsAsync(string workbookPath,
            WorkbookTestSelector selector, TimeSpan executionTimeout, WorkbookAutomationTimeouts automationTimeouts,
            CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("The legacy path runner must never reopen a source Test workbook.");
        }
    }

    private sealed record ExcelSettings(bool Visible, bool DisplayAlerts, bool EnableEvents,
        int AutomationSecurity, int Calculation, int WindowState, long WindowHandle, string ActiveWorkbookPath,
        bool WorkbookWindowVisible, int WorkbookWindowState);

    /// <summary>Owns only its created workbook and acquired RCWs; only the root fixture owns the Application.</summary>
    private sealed class NativeExcelFixture : IDisposable
    {
        private readonly object? excelObject;
        private readonly object? workbooksObject;
        private readonly object? workbookObject;
        private readonly object? worksheetsObject;
        private readonly object? worksheetObject;
        private readonly object? heldRangeObject;
        private readonly string selectedPath;
        private readonly int processId;
        private readonly long processStartUtcTicks;
        private readonly bool ownsApplication;
        private bool disposed;

        private dynamic Excel => RequireReference(excelObject);
        private dynamic Workbook => RequireReference(workbookObject);
        private dynamic Worksheets => RequireReference(worksheetsObject);
        private dynamic Worksheet => RequireReference(worksheetObject);
        private dynamic HeldRange => RequireReference(heldRangeObject);

        internal NativeExcelFixture(string path, NativeExcelFixture? sameApplication = null)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("This fixture requires Windows Excel COM automation.");
            selectedPath = path;
            ownsApplication = sameApplication is null;
            try
            {
                if (ownsApplication)
                {
                    var excelType = Type.GetTypeFromProgID("Excel.Application")
                        ?? throw new InvalidOperationException("Excel COM automation is unavailable.");
                    excelObject = Activator.CreateInstance(excelType)
                        ?? throw new InvalidOperationException("The native fixture could not create Excel.");
                    dynamic createdExcel = Excel;
                    createdExcel.Visible = false;
                    createdExcel.DisplayAlerts = false;
                    createdExcel.AutomationSecurity = 1;
                }
                else
                {
                    // Acquire a new COM reference rather than borrowing the root fixture's managed field.
                    var rootFixture = sameApplication
                        ?? throw new InvalidOperationException("The same-Application root fixture is unavailable.");
                    excelObject = rootFixture.Workbook.Application;
                }
                dynamic excel = Excel;
                var hwnd = (nint)Convert.ToInt64(excel.Hwnd);
                Assert.NotEqual(0U, GetWindowThreadProcessId(hwnd, out var nativeProcessId));
                processId = checked((int)nativeProcessId);
                using (var process = Process.GetProcessById(processId))
                    processStartUtcTicks = process.StartTime.ToUniversalTime().Ticks;
                workbooksObject = RequireReference(excel.Workbooks);
                dynamic workbooks = RequireReference(workbooksObject);
                workbookObject = workbooks.Open(path);
                Assert.Equal(path, Convert.ToString(Workbook.FullName));
                worksheetsObject = Workbook.Worksheets;
                worksheetObject = Worksheets(1);
                object cellsObject = RequireReference(Worksheet.Cells);
                Exception? cellFailure = null;
                try { heldRangeObject = ((dynamic)cellsObject)[5, 6]; }
                catch (Exception error) { cellFailure = error; throw; }
                finally { ReleaseAcquisitions(cellFailure, cellsObject); }
            }
            catch (Exception originalFailure)
            {
                try { Dispose(); }
                catch (Exception cleanupFailure)
                {
                    throw new AggregateException("Native fixture creation and cleanup failed.",
                        originalFailure, cleanupFailure);
                }
                throw;
            }
        }

        internal int ProcessId => processId;
        internal bool IsSaved => (bool)Workbook.Saved;
        internal int RunCount => Convert.ToInt32(ReadCell(7, 7));
        internal string? ReadSideEffect() => Convert.ToString(ReadCell(8, 8));
        internal void SetVisible(bool value) => Excel.Visible = value;
        internal void SetHeldMarker(string value) => HeldRange.Value2 = value;

        internal void SaveSetup() => Workbook.Save();

        internal ExcelSettings CaptureSettings()
        {
            dynamic excel = Excel;
            object? activeWorkbookObject = null;
            object? windowsObject = null;
            object? windowObject = null;
            Exception? originalFailure = null;
            try
            {
                activeWorkbookObject = excel.ActiveWorkbook;
                windowsObject = Workbook.Windows;
                windowObject = ((dynamic)RequireReference(windowsObject)).Item(1);
                dynamic window = RequireReference(windowObject);
                return new((bool)excel.Visible, (bool)excel.DisplayAlerts, (bool)excel.EnableEvents,
                    Convert.ToInt32(excel.AutomationSecurity), Convert.ToInt32(excel.Calculation),
                    Convert.ToInt32(excel.WindowState), Convert.ToInt64(excel.Hwnd),
                    Convert.ToString(((dynamic)RequireReference(activeWorkbookObject)).FullName)
                        ?? throw new InvalidOperationException("The fixture's active workbook has no full path."),
                    (bool)window.Visible, Convert.ToInt32(window.WindowState));
            }
            catch (Exception error)
            {
                originalFailure = error;
                throw;
            }
            finally
            {
                ReleaseAcquisitions(originalFailure, windowObject, windowsObject, activeWorkbookObject);
            }
        }

        internal void AssertRetained(ExcelSettings expectedSettings, string marker, string worksheetName = "Sheet1")
        {
            Assert.Equal(selectedPath, Convert.ToString(Workbook.FullName));
            Assert.Equal(expectedSettings, CaptureSettings());
            Assert.Equal(marker, Convert.ToString(HeldRange.Value2));
            Assert.False(IsSaved);
            using var process = Process.GetProcessById(processId);
            Assert.False(process.HasExited);
            Assert.Equal(processStartUtcTicks, process.StartTime.ToUniversalTime().Ticks);
            Assert.Equal(1, Convert.ToInt32(Worksheets.Count));
            Assert.Equal(worksheetName, Convert.ToString(Worksheet.Name));
        }

        internal void SetModuleCode(string source)
            => WithCodeModule(create: true, module =>
            {
                dynamic code = module;
                var count = (int)code.CountOfLines;
                if (count > 0) code.DeleteLines(1, count);
                code.AddFromString(source);
                return true;
            });

        internal string ReadModuleCode()
            => WithCodeModule(create: false, module =>
            {
                dynamic code = module;
                var count = (int)code.CountOfLines;
                return count == 0 ? string.Empty : Convert.ToString(code.Lines[1, count])!;
            });

        private TResult WithCodeModule<TResult>(bool create, Func<object, TResult> operation)
        {
            object? projectObject = null;
            object? componentsObject = null;
            object? componentObject = null;
            object? codeModuleObject = null;
            Exception? originalFailure = null;
            try
            {
                projectObject = Workbook.VBProject;
                componentsObject = ((dynamic)RequireReference(projectObject)).VBComponents;
                dynamic components = RequireReference(componentsObject);
                var count = (int)components.Count;
                for (var index = 1; index <= count; index++)
                {
                    componentObject = components.Item(index);
                    if (Convert.ToString(((dynamic)RequireReference(componentObject)).Name) == "NativeUnit")
                    {
                        break;
                    }
                    var nonmatchingComponent = componentObject;
                    componentObject = null;
                    ReleaseAcquisitions(null, nonmatchingComponent);
                }
                if (componentObject is null && create)
                {
                    componentObject = components.Add(1);
                    ((dynamic)RequireReference(componentObject)).Name = "NativeUnit";
                }
                Assert.NotNull(componentObject);
                codeModuleObject = ((dynamic)RequireReference(componentObject)).CodeModule;
                return operation(RequireReference(codeModuleObject));
            }
            catch (Exception error)
            {
                originalFailure = error;
                throw;
            }
            finally
            {
                ReleaseAcquisitions(originalFailure, codeModuleObject, componentObject,
                    componentsObject, projectObject);
            }
        }

        private object? ReadCell(int row, int column)
        {
            object cellsObject = RequireReference(Worksheet.Cells);
            object? rangeObject = null;
            Exception? originalFailure = null;
            try
            {
                dynamic cells = cellsObject;
                rangeObject = cells[row, column];
                return ((dynamic)RequireReference(rangeObject)).Value2;
            }
            catch (Exception error)
            {
                originalFailure = error;
                throw;
            }
            finally
            {
                ReleaseAcquisitions(originalFailure, rangeObject, cellsObject);
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            var failures = new List<Exception>();
            Attempt(() =>
            {
                if (workbookObject is not null) ((dynamic)workbookObject).Close(false);
            });
            foreach (var acquisition in new[]
                { heldRangeObject, worksheetObject, worksheetsObject, workbookObject, workbooksObject })
                Attempt(() => ReleaseAcquisitions(null, acquisition));
            if (ownsApplication && excelObject is not null)
                Attempt(() => ((dynamic)excelObject).Quit());
            Attempt(() => ReleaseAcquisitions(null, excelObject));
            Attempt(ComObjectReleaser.CollectReleasedComObjects);
            if (failures.Count != 0)
                throw new AggregateException("Native fixture cleanup failed.", failures);

            void Attempt(Action action)
            {
                try { action(); }
                catch (Exception error) { failures.Add(error); }
            }
        }

        private static void ReleaseAcquisitions(Exception? originalFailure, params object?[] acquisitions)
        {
            var failures = new List<Exception>();
            if (OperatingSystem.IsWindows())
                foreach (var acquisition in acquisitions)
                    try
                    {
                        if (acquisition is not null && Marshal.IsComObject(acquisition))
                            Marshal.ReleaseComObject(acquisition);
                    }
                    catch (Exception error) { failures.Add(error); }
            if (failures.Count == 0) return;
            if (originalFailure is not null) failures.Insert(0, originalFailure);
            throw new AggregateException("Native fixture operation or balanced COM release failed.", failures);
        }

        private static object RequireReference(object? value)
            => value ?? throw new InvalidOperationException("A native fixture COM acquisition is unavailable.");
    }

    private static void DisposeFixtures(params NativeExcelFixture?[] fixtures)
    {
        var failures = new List<Exception>();
        foreach (var fixture in fixtures)
        {
            try { fixture?.Dispose(); }
            catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count != 0) throw new AggregateException("Native fixture cleanup failed.", failures);
    }

    private static IReadOnlySet<int> CaptureExcelProcessIds()
    {
        var result = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName("EXCEL"))
            using (process) result.Add(process.Id);
        return result;
    }

    private static async Task WaitForProcessSetAsync(IReadOnlySet<int> expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (CaptureExcelProcessIds().SetEquals(expected)) return;
            await Task.Delay(100);
        }
        Assert.Equal(expected.Order().ToArray(), CaptureExcelProcessIds().Order().ToArray());
    }

    private static void CreateEmptyMacroEnabledWorkbook(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.ms-excel.sheet.macroEnabled.main+xml"/>
              <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
            </Types>
            """);
        WriteEntry(archive, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);
        WriteEntry(archive, "xl/workbook.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets>
            </workbook>
            """);
        WriteEntry(archive, "xl/_rels/workbook.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
            </Relationships>
            """);
        WriteEntry(archive, "xl/worksheets/sheet1.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData/></worksheet>
            """);
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        using var stream = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);
}
