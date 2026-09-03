using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using VbaDev.App.Cli;
using VbaDev.App.Testing;
using VbaDev.App.Workbooks;
using VbaDev.Domain;
using VbaDev.Infrastructure.Debugging;
using VbaDev.Infrastructure.Projects;
using VbaDev.Infrastructure.Workbooks;
using Xunit;

namespace VbaDev.Tests;

[Collection(WindowsExcelIntegrationCollection.Name)]
public sealed class AutomationDesktopIsolationWindowsExcelIntegrationTests
{
    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task SharedProductionAutomationKeepsRepresentativeWorkOffCallerDesktop()
    {
        using var temp = TempDirectory.Create();
        var initialProcesses = CaptureExcelProcessIds();
        var initialBootstrapArtifacts = CaptureBootstrapWorkbookPaths();
        var workbookPath = Path.Combine(temp.Path, "PrivateDesktopProduction.xlsm");
        var sourcePath = Path.Combine(temp.Path, "PrivateDesktopFeature.bas");
        CreateEmptyMacroEnabledWorkbook(workbookPath);
        File.WriteAllText(
            sourcePath,
            string.Join("\r\n", [
                "Attribute VB_Name = \"PrivateDesktopFeature\"",
                "Option Explicit",
                "Public Function ProofValue() As String",
                "    ProofValue = \"private-desktop-production\"",
                "End Function",
                string.Empty
            ]),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        using var importSourceSet = VbeImportSourceSet.Create(
            [new VbaSourceFile(sourcePath, VbaSourceKind.StandardModule, null)],
            ActiveWindowsAnsiCodePage.Get());
        var stagedSource = Assert.Single(importSourceSet.SourceFiles);
        var operationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOperation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var callerDesktop = CallerDesktopSampler.Start();
        var automation = new ExcelComWorkbookBuildAutomation();
        var operation = automation.RunAsync(
            workbookPath,
            WorkbookAutomationTimeouts.Default,
            async (session, cancellationToken) =>
            {
                operationEntered.TrySetResult();
                await releaseOperation.Task.WaitAsync(cancellationToken);
                await session.ImportModuleAsync(stagedSource, cancellationToken);
                _ = await session.VerifyAsync(cancellationToken);
                await session.SaveAsync(cancellationToken);
                return await session.GetModulesAsync(cancellationToken);
            },
            CancellationToken.None);

        Exception? inspectionFailure = null;
        var ownedProcessId = 0;
        try
        {
            await operationEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            ownedProcessId = await WaitForOwnedExcelProcessAsync(
                initialProcesses,
                operation,
                TimeSpan.FromSeconds(20));
            callerDesktop.Capture();
            Assert.Empty(callerDesktop.ForProcess(ownedProcessId));
        }
        catch (Exception ex)
        {
            inspectionFailure = ex;
        }
        finally
        {
            releaseOperation.TrySetResult();
        }

        var modules = await operation;
        if (inspectionFailure is not null)
        {
            ExceptionDispatchInfo.Capture(inspectionFailure).Throw();
        }

        await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
        callerDesktop.Capture();

        Assert.Contains(modules, module =>
            module.Name.Equals("PrivateDesktopFeature", StringComparison.Ordinal));
        Assert.True(File.Exists(workbookPath));
        Assert.Empty(callerDesktop.ForProcess(ownedProcessId));
        Assert.Equal(
            initialBootstrapArtifacts.Order(StringComparer.OrdinalIgnoreCase),
            CaptureBootstrapWorkbookPaths().Order(StringComparer.OrdinalIgnoreCase));
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task BlockingMacroTimesOutWithPrivateWindowEvidenceAndExactCleanup()
    {
        using var temp = TempDirectory.Create();
        var initialProcesses = CaptureExcelProcessIds();
        var initialBootstrapArtifacts = CaptureBootstrapWorkbookPaths();
        var workbookPath = Path.Combine(temp.Path, "PrivateDesktopBlockingUi.xlsm");
        var sourcePath = Path.Combine(temp.Path, "PrivateDesktopBlockingTests.bas");
        var dialogTitle = $"vba-dev private desktop {Guid.NewGuid():N}";
        CreateEmptyMacroEnabledWorkbook(workbookPath);
        File.WriteAllText(
            sourcePath,
            string.Join("\r\n", [
                "Attribute VB_Name = \"PrivateDesktopBlockingTests\"",
                "Option Explicit",
                "Public Sub UnitTestMain()",
                $"    MsgBox \"This automation UI must remain private.\", vbOKOnly, \"{dialogTitle}\"",
                "End Sub",
                string.Empty
            ]),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await ImportAndSaveModuleAsync(workbookPath, sourcePath);
        await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));

        await using var callerDesktop = CallerDesktopSampler.Start();
        var automation = new ExcelComWorkbookBuildAutomation();
        var clock = Stopwatch.StartNew();
        var execution = automation.RunWorkbookTestsAsync(
            workbookPath,
            WorkbookAutomationTimeouts.Default with
            {
                ProcessCleanup = TimeSpan.Zero
            },
            TimeSpan.FromSeconds(2),
            new WorkbookTestSelector(),
            CancellationToken.None);
        var ownedProcessId = await WaitForOwnedExcelProcessAsync(
            initialProcesses,
            execution,
            TimeSpan.FromSeconds(20));
        callerDesktop.Capture();
        Assert.Empty(callerDesktop.ForProcess(ownedProcessId));

        var error = await Assert.ThrowsAsync<WorkbookAutomationTimeoutException>(
            () => execution);
        clock.Stop();
        await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
        callerDesktop.Capture();

        Assert.Equal(WorkbookAutomationStageKind.TestExecution, error.Stage.Kind);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20));
        Assert.NotNull(error.IsolationDiagnostics);
        Assert.Contains($"PID={ownedProcessId}", error.IsolationDiagnostics, StringComparison.Ordinal);
        Assert.Contains("HWND=0x", error.IsolationDiagnostics, StringComparison.Ordinal);
        Assert.Contains(
            "privateDesktop='WinSta0\\vba-dev-automation-",
            error.IsolationDiagnostics,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "desktop='WinSta0\\vba-dev-automation-",
            error.IsolationDiagnostics,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("class='", error.IsolationDiagnostics, StringComparison.Ordinal);
        Assert.Contains($"title='{dialogTitle}'", error.IsolationDiagnostics, StringComparison.Ordinal);
        Assert.Contains("phase=TestExecution", error.IsolationDiagnostics, StringComparison.Ordinal);
        Assert.Empty(callerDesktop.ForProcess(ownedProcessId));
        Assert.Equal(
            initialBootstrapArtifacts.Order(StringComparer.OrdinalIgnoreCase),
            CaptureBootstrapWorkbookPaths().Order(StringComparer.OrdinalIgnoreCase));
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task CommandTestBuildFirstAndNoBuildUseExpectedHiddenProcessesAndPreserveNdjson()
    {
        using var temp = TempDirectory.Create();
        var initialProcesses = CaptureExcelProcessIds();
        var initialBootstrapArtifacts = CaptureBootstrapWorkbookPaths();
        var project = CreateCommandTestProject(temp.CreateDirectory("CommandProject"));
        var application = CommandLineTestFactory.Create(project.Root);
        var selectedArguments = new[]
        {
            "--format",
            "ndjson",
            "--module",
            project.TestModule,
            "--procedure",
            project.TestProcedure
        };
        Assert.False(File.Exists(project.BinPath));

        var buildFirst = await ObserveCommandAsync(
            () => application.RunAsync(["test", .. selectedArguments]),
            initialProcesses,
            project.BinPath);
        AssertSuccessfulSelectedTestResult(buildFirst.Result, project);
        var buildFirstLifetimes = buildFirst.ProcessTimeline.Lifetimes
            .OrderBy(lifetime => lifetime.StartedAt)
            .ToArray();
        Assert.Equal(2, buildFirstLifetimes.Length);
        Assert.Equal(2, buildFirstLifetimes.Select(lifetime => lifetime.ProcessId).Distinct().Count());
        Assert.All(
            buildFirstLifetimes,
            lifetime => Assert.True(lifetime.HasExited, lifetime.ObservationError));
        Assert.DoesNotContain(
            buildFirst.ProcessTimeline.Samples,
            sample => sample.ProcessIds.Count > 1);
        Assert.True(
            buildFirstLifetimes[0].ExitedAt <= buildFirstLifetimes[1].StartedAt,
            $"Expected build PID {buildFirstLifetimes[0].ProcessId} to exit before " +
            $"test PID {buildFirstLifetimes[1].ProcessId} started, but the exact lifetimes were " +
            $"{buildFirstLifetimes[0].StartedAt:o}..{buildFirstLifetimes[0].ExitedAt:o} and " +
            $"{buildFirstLifetimes[1].StartedAt:o}..{buildFirstLifetimes[1].ExitedAt:o}.");
        var buildProcessSamples = buildFirst.ProcessTimeline.Samples
            .Where(sample => sample.ProcessIds.Contains(buildFirstLifetimes[0].ProcessId))
            .ToArray();
        var testProcessSamples = buildFirst.ProcessTimeline.Samples
            .Where(sample => sample.ProcessIds.Contains(buildFirstLifetimes[1].ProcessId))
            .ToArray();
        Assert.NotEmpty(buildProcessSamples);
        Assert.NotEmpty(testProcessSamples);
        Assert.All(buildProcessSamples, sample => Assert.False(sample.OutputExists));
        Assert.All(testProcessSamples, sample => Assert.True(sample.OutputExists));
        AssertNoCallerDesktopWindows(buildFirst, buildFirstLifetimes);
        Assert.True(File.Exists(project.BinPath));
        var builtWorkbook = File.ReadAllBytes(project.BinPath);

        var noBuild = await ObserveCommandAsync(
            () => application.RunAsync(["test", "--no-build", .. selectedArguments]),
            initialProcesses,
            project.BinPath);
        AssertSuccessfulSelectedTestResult(noBuild.Result, project);
        var noBuildLifetime = Assert.Single(noBuild.ProcessTimeline.Lifetimes);
        Assert.True(noBuildLifetime.HasExited, noBuildLifetime.ObservationError);
        Assert.All(
            noBuild.ProcessTimeline.Samples.Where(
                sample => sample.ProcessIds.Contains(noBuildLifetime.ProcessId)),
            sample => Assert.True(sample.OutputExists));
        AssertNoCallerDesktopWindows(noBuild, [noBuildLifetime]);

        Assert.Equal(buildFirst.Result.ExitCode, noBuild.Result.ExitCode);
        Assert.Equal(buildFirst.Result.StandardOutput, noBuild.Result.StandardOutput);
        Assert.Equal(buildFirst.Result.StandardError, noBuild.Result.StandardError);
        Assert.Equal(builtWorkbook, File.ReadAllBytes(project.BinPath));
        Assert.True(initialProcesses.SetEquals(CaptureExcelProcessIds()));
        Assert.Equal(
            initialBootstrapArtifacts.Order(StringComparer.OrdinalIgnoreCase),
            CaptureBootstrapWorkbookPaths().Order(StringComparer.OrdinalIgnoreCase));
    }

    private static CommandTestProject CreateCommandTestProject(string root)
    {
        const string document = "Book1";
        const string testModule = "Test_CommandDesktop";
        const string testProcedure = "Test_Selected";
        var sourceDirectory = Path.Combine(root, "src", document);
        var templatePath = Path.Combine(sourceDirectory, $"{document}.xlsm");
        var binPath = Path.Combine(root, "bin", $"{document}.xlsm");
        var testSourcePath = Path.Combine(sourceDirectory, $"{testModule}.bas");
        Directory.CreateDirectory(sourceDirectory);
        CreateEmptyMacroEnabledWorkbook(templatePath);
        File.WriteAllText(
            Path.Combine(sourceDirectory, "CommandTestHarness.bas"),
            string.Join("\r\n", [
                "Attribute VB_Name = \"CommandTestHarness\"",
                "Option Explicit",
                string.Empty,
                "Public Sub UnitTestMain(ByVal moduleName As String, ByVal procedureName As String)",
                "    Dim resultSheet As Object",
                "    Set resultSheet = ThisWorkbook.Worksheets(1)",
                "    If resultSheet.Name <> \"UNIT_TEST_SHEET\" Then",
                "        resultSheet.Name = \"UNIT_TEST_SHEET\"",
                "    End If",
                "    resultSheet.Cells.Clear",
                "    Application.Run \"'\" & ThisWorkbook.Name & \"'!\" & moduleName & \".\" & procedureName",
                "    resultSheet.Cells(1, 1).Value2 = \"Module\"",
                "    resultSheet.Cells(1, 2).Value2 = \"Procedure\"",
                "    resultSheet.Cells(1, 3).Value2 = \"Result\"",
                "    resultSheet.Cells(1, 4).Value2 = \"Message\"",
                "    resultSheet.Cells(2, 1).Value2 = moduleName",
                "    resultSheet.Cells(2, 2).Value2 = procedureName",
                "    If CStr(resultSheet.Cells(1, 6).Value2) = \"selected-via-application-run\" Then",
                "        resultSheet.Cells(2, 3).Value2 = \"OK\"",
                "        resultSheet.Cells(2, 4).Value2 = vbNullString",
                "    Else",
                "        resultSheet.Cells(2, 3).Value2 = \"NG\"",
                "        resultSheet.Cells(2, 4).Value2 = \"Selected procedure was not invoked.\"",
                "    End If",
                "End Sub",
                string.Empty
            ]),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(
            testSourcePath,
            string.Join("\r\n", [
                $"Attribute VB_Name = \"{testModule}\"",
                "Option Explicit",
                string.Empty,
                $"Public Sub {testProcedure}()",
                "    ThisWorkbook.Worksheets(\"UNIT_TEST_SHEET\").Cells(1, 6).Value2 = \"selected-via-application-run\"",
                "End Sub",
                string.Empty
            ]),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        new JsonProjectManifestStore().Save(
            root,
            ProjectManifest.CreateDefault("PrivateDesktopCommand", document, root, null));
        return new CommandTestProject(
            root,
            binPath,
            testSourcePath,
            testModule,
            testProcedure);
    }

    private static async Task<ObservedCommandInvocation> ObserveCommandAsync(
        Func<Task<CommandResult>> invoke,
        IReadOnlySet<int> initialProcesses,
        string outputPath)
    {
        await using var callerDesktop = CallerDesktopSampler.Start();
        await using var processTimeline = ExcelProcessTimelineSampler.Start(
            initialProcesses,
            outputPath);
        var result = await invoke();
        await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
        processTimeline.Capture();
        callerDesktop.Capture();
        await processTimeline.StopAsync();
        await callerDesktop.StopAsync();
        return new ObservedCommandInvocation(
            result,
            processTimeline.Snapshot,
            callerDesktop.Snapshot());
    }

    private static void AssertSuccessfulSelectedTestResult(
        CommandResult result,
        CommandTestProject project)
    {
        Assert.True(
            result.ExitCode == 0,
            $"Expected exit 0 but received {result.ExitCode}. " +
            $"stdout: {result.StandardOutput} stderr: {result.StandardError}");
        Assert.Equal(string.Empty, result.StandardError);
        var lines = result.StandardOutput.Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        var records = lines.Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            AssertJsonProperties(records[0].RootElement, "type", "project", "document");
            Assert.Equal("runStarted", records[0].RootElement.GetProperty("type").GetString());
            Assert.Equal("PrivateDesktopCommand", records[0].RootElement.GetProperty("project").GetString());
            Assert.Equal("Book1", records[0].RootElement.GetProperty("document").GetString());

            AssertJsonProperties(
                records[1].RootElement,
                "type",
                "project",
                "document",
                "module",
                "procedure");
            Assert.Equal("testStarted", records[1].RootElement.GetProperty("type").GetString());
            Assert.Equal(project.TestModule, records[1].RootElement.GetProperty("module").GetString());
            Assert.Equal(project.TestProcedure, records[1].RootElement.GetProperty("procedure").GetString());

            AssertJsonProperties(
                records[2].RootElement,
                "type",
                "project",
                "document",
                "module",
                "procedure",
                "outcome",
                "message",
                "location");
            Assert.Equal("testFinished", records[2].RootElement.GetProperty("type").GetString());
            Assert.Equal(project.TestModule, records[2].RootElement.GetProperty("module").GetString());
            Assert.Equal(project.TestProcedure, records[2].RootElement.GetProperty("procedure").GetString());
            Assert.Equal("passed", records[2].RootElement.GetProperty("outcome").GetString());
            Assert.Equal(string.Empty, records[2].RootElement.GetProperty("message").GetString());
            var location = records[2].RootElement.GetProperty("location");
            AssertJsonProperties(location, "uri", "range");
            Assert.Equal(new Uri(project.TestSourcePath).AbsoluteUri, location.GetProperty("uri").GetString());
            var range = location.GetProperty("range");
            AssertJsonProperties(range, "start", "end");
            Assert.Equal(3, range.GetProperty("start").GetProperty("line").GetInt32());
            Assert.Equal(11, range.GetProperty("start").GetProperty("character").GetInt32());
            Assert.Equal(3, range.GetProperty("end").GetProperty("line").GetInt32());
            Assert.Equal(24, range.GetProperty("end").GetProperty("character").GetInt32());

            AssertJsonProperties(
                records[3].RootElement,
                "type",
                "project",
                "document",
                "outcome",
                "total",
                "passed",
                "failed",
                "errors");
            Assert.Equal("runFinished", records[3].RootElement.GetProperty("type").GetString());
            Assert.Equal("passed", records[3].RootElement.GetProperty("outcome").GetString());
            Assert.Equal(1, records[3].RootElement.GetProperty("total").GetInt32());
            Assert.Equal(1, records[3].RootElement.GetProperty("passed").GetInt32());
            Assert.Equal(0, records[3].RootElement.GetProperty("failed").GetInt32());
            Assert.Equal(0, records[3].RootElement.GetProperty("errors").GetInt32());
        }
        finally
        {
            foreach (var record in records)
            {
                record.Dispose();
            }
        }
    }

    private static void AssertJsonProperties(JsonElement element, params string[] expected)
        => Assert.Equal(
            expected.Order(StringComparer.Ordinal),
            element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));

    private static void AssertNoCallerDesktopWindows(
        ObservedCommandInvocation invocation,
        IReadOnlyCollection<ExcelProcessLifetime> lifetimes)
    {
        var processIds = lifetimes
            .Select(lifetime => lifetime.ProcessId)
            .ToHashSet();
        Assert.DoesNotContain(
            invocation.CallerDesktopWindows,
            window => processIds.Contains(window.ProcessId));
    }

    private static async Task ImportAndSaveModuleAsync(
        string workbookPath,
        string sourcePath)
    {
        using var importSourceSet = VbeImportSourceSet.Create(
            [new VbaSourceFile(sourcePath, VbaSourceKind.StandardModule, null)],
            ActiveWindowsAnsiCodePage.Get());
        var stagedSource = Assert.Single(importSourceSet.SourceFiles);
        var automation = new ExcelComWorkbookBuildAutomation();
        _ = await automation.RunAsync(
            workbookPath,
            WorkbookAutomationTimeouts.Default,
            async (session, cancellationToken) =>
            {
                await session.ImportModuleAsync(stagedSource, cancellationToken);
                _ = await session.VerifyAsync(cancellationToken);
                await session.SaveAsync(cancellationToken);
                return true;
            },
            CancellationToken.None);
    }

    private static void CreateEmptyMacroEnabledWorkbook(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(
            archive,
            "[Content_Types].xml",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.ms-excel.sheet.macroEnabled.main+xml"/>
              <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
            </Types>
            """);
        WriteEntry(
            archive,
            "_rels/.rels",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);
        WriteEntry(
            archive,
            "xl/workbook.xml",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets>
            </workbook>
            """);
        WriteEntry(
            archive,
            "xl/_rels/workbook.xml.rels",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
            </Relationships>
            """);
        WriteEntry(
            archive,
            "xl/worksheets/sheet1.xml",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData/></worksheet>
            """);
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static IReadOnlySet<int> CaptureExcelProcessIds()
    {
        var processIds = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName("EXCEL"))
        {
            using (process)
            {
                try
                {
                    processIds.Add(process.Id);
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        return processIds;
    }

    private static IReadOnlySet<string> CaptureBootstrapWorkbookPaths()
        => Directory
            .EnumerateFiles(
                Path.GetTempPath(),
                "vba-dev-excel-bootstrap-*.xlsx",
                SearchOption.TopDirectoryOnly)
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static async Task WaitForProcessSetAsync(
        IReadOnlySet<int> expected,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (CaptureExcelProcessIds().SetEquals(expected))
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Equal(expected.Order().ToArray(), CaptureExcelProcessIds().Order().ToArray());
    }

    private static async Task<int> WaitForOwnedExcelProcessAsync(
        IReadOnlySet<int> initialProcesses,
        Task operation,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var newProcesses = CaptureExcelProcessIds().Except(initialProcesses).ToArray();
            if (newProcesses.Length == 1)
            {
                return newProcesses[0];
            }

            if (operation.IsCompleted)
            {
                await operation;
                throw new InvalidOperationException(
                    "Workbook automation completed before its owned Excel process was observed.");
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("An owned Excel process was not observed before the deadline.");
    }

    private sealed record CommandTestProject(
        string Root,
        string BinPath,
        string TestSourcePath,
        string TestModule,
        string TestProcedure);

    private sealed record ObservedCommandInvocation(
        CommandResult Result,
        ExcelProcessTimeline ProcessTimeline,
        IReadOnlyList<DesktopWindowSnapshot> CallerDesktopWindows);

    private sealed record ExcelProcessTimeline(
        IReadOnlyList<ExcelProcessSample> Samples,
        IReadOnlyList<ExcelProcessLifetime> Lifetimes);

    private sealed record ExcelProcessSample(
        long Sequence,
        DateTimeOffset ObservedAt,
        IReadOnlyList<int> ProcessIds,
        bool OutputExists);

    private sealed record ExcelProcessLifetime(
        int ProcessId,
        DateTimeOffset StartedAt,
        bool HasExited,
        DateTimeOffset? ExitedAt,
        string? ObservationError);

    private sealed class ExcelProcessTimelineSampler : IAsyncDisposable
    {
        private readonly IReadOnlySet<int> initialProcesses;
        private readonly string outputPath;
        private readonly ConcurrentQueue<ExcelProcessSample> samples = new();
        private readonly Dictionary<int, TrackedExcelProcess> trackedProcesses = [];
        private readonly object trackedProcessesGate = new();
        private readonly CancellationTokenSource cancellation = new();
        private readonly Task sampling;
        private long sequence;
        private int stopped;
        private ExcelProcessTimeline? snapshot;

        private ExcelProcessTimelineSampler(
            IReadOnlySet<int> initialProcesses,
            string outputPath)
        {
            this.initialProcesses = initialProcesses;
            this.outputPath = outputPath;
            Capture();
            sampling = SampleAsync();
        }

        public ExcelProcessTimeline Snapshot => snapshot ?? throw new InvalidOperationException(
            "The Excel process timeline must be stopped before it is inspected.");

        public static ExcelProcessTimelineSampler Start(
            IReadOnlySet<int> initialProcesses,
            string outputPath)
            => new(initialProcesses, outputPath);

        public void Capture()
        {
            var processIds = CaptureExcelProcessIds()
                .Except(initialProcesses)
                .Order()
                .ToArray();
            foreach (var processId in processIds)
            {
                Track(processId);
            }

            samples.Enqueue(new ExcelProcessSample(
                Interlocked.Increment(ref sequence),
                DateTimeOffset.UtcNow,
                processIds,
                File.Exists(outputPath)));
        }

        public async ValueTask StopAsync()
        {
            if (Interlocked.Exchange(ref stopped, 1) != 0)
            {
                return;
            }

            cancellation.Cancel();
            try
            {
                await sampling.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }

            List<ExcelProcessLifetime> lifetimes;
            lock (trackedProcessesGate)
            {
                lifetimes = trackedProcesses.Values
                    .Select(tracked => tracked.CaptureLifetime())
                    .ToList();
                foreach (var tracked in trackedProcesses.Values)
                {
                    tracked.Process.Dispose();
                }
            }

            snapshot = new ExcelProcessTimeline(samples.ToArray(), lifetimes);
            cancellation.Dispose();
        }

        public ValueTask DisposeAsync()
            => StopAsync();

        private void Track(int processId)
        {
            lock (trackedProcessesGate)
            {
                if (trackedProcesses.ContainsKey(processId))
                {
                    return;
                }

                try
                {
                    var process = Process.GetProcessById(processId);
                    trackedProcesses.Add(
                        processId,
                        new TrackedExcelProcess(
                            process,
                            process.Handle,
                            new DateTimeOffset(process.StartTime.ToUniversalTime())));
                }
                catch (ArgumentException)
                {
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        private async Task SampleAsync()
        {
            while (true)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                Capture();
                await Task.Delay(5, cancellation.Token).ConfigureAwait(false);
            }
        }

        private sealed record TrackedExcelProcess(
            Process Process,
            nint ProcessHandle,
            DateTimeOffset StartedAt)
        {
            public ExcelProcessLifetime CaptureLifetime()
            {
                try
                {
                    if (!Process.HasExited)
                    {
                        _ = Process.WaitForExit(milliseconds: 10000);
                    }

                    var hasExited = Process.HasExited;
                    DateTimeOffset? exitedAt = null;
                    string? observationError = null;
                    if (hasExited)
                    {
                        if (GetProcessTimes(
                            ProcessHandle,
                            out _,
                            out var exitTime,
                            out _,
                            out _))
                        {
                            exitedAt = new DateTimeOffset(DateTime.FromFileTimeUtc(exitTime));
                        }
                        else
                        {
                            var errorCode = Marshal.GetLastWin32Error();
                            observationError = new Win32Exception(errorCode).ToString();
                        }
                    }

                    return new ExcelProcessLifetime(
                        Process.Id,
                        StartedAt,
                        hasExited,
                        exitedAt,
                        observationError);
                }
                catch (InvalidOperationException exception)
                {
                    return new ExcelProcessLifetime(
                        Process.Id,
                        StartedAt,
                        HasExited: false,
                        ExitedAt: null,
                        exception.ToString());
                }
            }

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool GetProcessTimes(
                nint processHandle,
                out long creationTime,
                out long exitTime,
                out long kernelTime,
                out long userTime);
        }
    }

    private sealed class CallerDesktopSampler : IAsyncDisposable
    {
        private readonly WindowsDesktopWindowObservationNativeApi nativeApi;
        private readonly DesktopWindowObservationScope callerDesktop;
        private readonly ConcurrentQueue<DesktopWindowSnapshot> snapshots = new();
        private readonly CancellationTokenSource cancellation = new();
        private readonly Task sampling;
        private int stopped;

        private CallerDesktopSampler(
            WindowsDesktopWindowObservationNativeApi nativeApi,
            DesktopWindowObservationScope callerDesktop)
        {
            this.nativeApi = nativeApi;
            this.callerDesktop = callerDesktop;
            Capture();
            sampling = SampleAsync();
        }

        public static CallerDesktopSampler Start()
        {
            var nativeApi = WindowsDesktopWindowObservationNativeApi.Instance;
            return new CallerDesktopSampler(
                nativeApi,
                nativeApi.CaptureCurrentThreadDesktop());
        }

        public void Capture()
        {
            foreach (var snapshot in nativeApi.EnumerateTopLevelWindows(callerDesktop))
            {
                snapshots.Enqueue(snapshot);
            }
        }

        public IReadOnlyList<DesktopWindowSnapshot> ForProcess(int processId)
            => snapshots.Where(snapshot => snapshot.ProcessId == processId).ToArray();

        public IReadOnlyList<DesktopWindowSnapshot> Snapshot()
            => snapshots.ToArray();

        public async ValueTask StopAsync()
        {
            if (Interlocked.Exchange(ref stopped, 1) != 0)
            {
                return;
            }

            cancellation.Cancel();
            try
            {
                await sampling.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            finally
            {
                cancellation.Dispose();
            }
        }

        public ValueTask DisposeAsync()
            => StopAsync();

        private async Task SampleAsync()
        {
            while (true)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                Capture();
                await Task.Delay(10, cancellation.Token).ConfigureAwait(false);
            }
        }
    }
}
