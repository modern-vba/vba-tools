using VbaDev.App.Debugging;
using VbaDev.Infrastructure.Debugging;
using VbaLanguageServer.Syntax;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Xunit;

namespace VbaDev.Tests;

public sealed class VbeDebugAutomationTests
{
    [Fact]
    public async Task SetNativeBreakpointsFailsBeforeToggleWhenAnyNonTargetModuleLineIsStale()
    {
        using var temp = TempDirectory.Create();
        var workbookPath = Path.Combine(temp.Path, "GeneratedBook.xlsm");
        File.WriteAllText(workbookPath, "test workbook placeholder");
        var sourcePath = Path.Combine(temp.Path, "DebugModule.bas");
        var events = new List<string>();
        var model = FakeVbeModel.Create(
            workbookPath,
            events,
            codeLines:
            [
                "Option Explicit",
                "Public Sub RunTarget()",
                "    Debug.Print \"break here\"",
                "End Sub"
            ]);
        var process = new FakeDebugOwnedProcess(
            22363,
            new DateTime(2026, 7, 21, 11, 1, 0, DateTimeKind.Local));
        var automation = new VbeDebugAutomation(
            new FakeExcelDebugApplicationFactory(model.Excel),
            new FakeDebugExcelProcessApi(22363, process),
            new FakeDebugWindowActivator(events),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()));
        var sourceMap = new VbeCodeModuleSourceMap(
            "DebugModule",
            VbaModuleKind.StandardModule,
            ImmutableArray.Create(
                "Option Private Module",
                "Public Sub RunTarget()",
                "    Debug.Print \"break here\"",
                "End Sub"));

        await using var session = await automation.StartVisibleAsync(CancellationToken.None);
        await session.OpenGeneratedWorkbookAsync(workbookPath, CancellationToken.None);
        var error = await Assert.ThrowsAsync<DebugSetupException>(() =>
            session.SetNativeBreakpointsAsync(
                [new VbeBreakpoint(
                    new DebugSourceBreakpoint(sourcePath, 3),
                    sourceMap,
                    VbideLine: 3)],
                CancellationToken.None));

        Assert.Contains("whole code module", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, process.KillCalls);
        Assert.DoesNotContain("allow-com-foreground", events);
        Assert.DoesNotContain(events, entry => entry.StartsWith("execute:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SetNativeBreakpointsVerifiesEveryDistinctModuleBeforeTogglingEitherModule()
    {
        using var temp = TempDirectory.Create();
        var workbookPath = Path.Combine(temp.Path, "GeneratedBook.xlsm");
        File.WriteAllText(workbookPath, "test workbook placeholder");
        var events = new List<string>();
        var model = FakeVbeModel.Create(
            workbookPath,
            events,
            secondaryComponentName: "WorkerModule",
            secondaryCodeLines:
            [
                "Option Private Module",
                "Public Sub Work()",
                "    Debug.Print \"break here too\"",
                "End Sub"
            ]);
        var process = new FakeDebugOwnedProcess(
            22364,
            new DateTime(2026, 7, 21, 11, 1, 30, DateTimeKind.Local));
        var automation = new VbeDebugAutomation(
            new FakeExcelDebugApplicationFactory(model.Excel),
            new FakeDebugExcelProcessApi(22364, process),
            new FakeDebugWindowActivator(events),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()));
        var workerSourceMap = new VbeCodeModuleSourceMap(
            "WorkerModule",
            VbaModuleKind.StandardModule,
            ImmutableArray.Create(
                "Option Explicit",
                "Public Sub Work()",
                "    Debug.Print \"break here too\"",
                "End Sub"));

        await using var session = await automation.StartVisibleAsync(CancellationToken.None);
        await session.OpenGeneratedWorkbookAsync(workbookPath, CancellationToken.None);
        var error = await Assert.ThrowsAsync<DebugSetupException>(() =>
            session.SetNativeBreakpointsAsync(
                [
                    CreateBreakpoint(temp.Path),
                    new VbeBreakpoint(
                        new DebugSourceBreakpoint(
                            Path.Combine(temp.Path, "WorkerModule.bas"),
                            2),
                        workerSourceMap,
                        VbideLine: 3)
                ],
                CancellationToken.None));

        Assert.Contains("whole code module", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("component:DebugModule", events);
        Assert.Contains("component:WorkerModule", events);
        Assert.Equal(1, process.KillCalls);
        Assert.DoesNotContain("allow-com-foreground", events);
        Assert.DoesNotContain(events, entry => entry.StartsWith("execute:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1, VbaModuleKind.StandardModule)]
    [InlineData(2, VbaModuleKind.ClassModule)]
    [InlineData(3, VbaModuleKind.FormModule)]
    public async Task SetNativeBreakpointsAcceptsTheExactStandardClassAndFormComponentKinds(
        int componentType,
        VbaModuleKind moduleKind)
    {
        using var temp = TempDirectory.Create();
        var workbookPath = Path.Combine(temp.Path, "GeneratedBook.xlsm");
        File.WriteAllText(workbookPath, "test workbook placeholder");
        var events = new List<string>();
        var model = FakeVbeModel.Create(
            workbookPath,
            events,
            componentType: componentType);
        var process = new FakeDebugOwnedProcess(
            22364 + componentType,
            new DateTime(2026, 7, 21, 11, 2, 0, DateTimeKind.Local));
        var automation = new VbeDebugAutomation(
            new FakeExcelDebugApplicationFactory(model.Excel),
            new FakeDebugExcelProcessApi(process.Id, process),
            new FakeDebugWindowActivator(events),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()));
        var breakpoint = new VbeBreakpoint(
            new DebugSourceBreakpoint(
                Path.Combine(temp.Path, $"DebugModule{GetSourceExtension(moduleKind)}"),
                10),
            CreateSourceMap(moduleKind),
            9);

        await using var session = await automation.StartVisibleAsync(CancellationToken.None);
        await session.OpenGeneratedWorkbookAsync(workbookPath, CancellationToken.None);
        await session.SetNativeBreakpointsAsync([breakpoint], CancellationToken.None);

        Assert.Contains("execute:51", events);
        Assert.False(session.Completion.IsCompleted);
        process.Exit(0);
        await session.Completion;
    }

    [Fact]
    public async Task SetNativeBreakpointsFailsBeforeToggleWhenTheComponentKindDoesNotMatch()
    {
        using var temp = TempDirectory.Create();
        var workbookPath = Path.Combine(temp.Path, "GeneratedBook.xlsm");
        File.WriteAllText(workbookPath, "test workbook placeholder");
        var events = new List<string>();
        var model = FakeVbeModel.Create(workbookPath, events, componentType: 2);
        var process = new FakeDebugOwnedProcess(
            22368,
            new DateTime(2026, 7, 21, 11, 3, 0, DateTimeKind.Local));
        var automation = new VbeDebugAutomation(
            new FakeExcelDebugApplicationFactory(model.Excel),
            new FakeDebugExcelProcessApi(22368, process),
            new FakeDebugWindowActivator(events),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()));

        await using var session = await automation.StartVisibleAsync(CancellationToken.None);
        await session.OpenGeneratedWorkbookAsync(workbookPath, CancellationToken.None);
        var error = await Assert.ThrowsAsync<DebugSetupException>(() =>
            session.SetNativeBreakpointsAsync(
                [CreateBreakpoint(temp.Path)],
                CancellationToken.None));

        Assert.Contains("component kind", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, process.KillCalls);
        Assert.DoesNotContain("allow-com-foreground", events);
        Assert.DoesNotContain(events, entry => entry.StartsWith("execute:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SetNativeBreakpointsFailsBeforeToggleWhenTheComponentIdentityDoesNotMatch()
    {
        using var temp = TempDirectory.Create();
        var workbookPath = Path.Combine(temp.Path, "GeneratedBook.xlsm");
        File.WriteAllText(workbookPath, "test workbook placeholder");
        var events = new List<string>();
        var model = FakeVbeModel.Create(
            workbookPath,
            events,
            componentName: "DifferentModule");
        var process = new FakeDebugOwnedProcess(
            22369,
            new DateTime(2026, 7, 21, 11, 4, 0, DateTimeKind.Local));
        var automation = new VbeDebugAutomation(
            new FakeExcelDebugApplicationFactory(model.Excel),
            new FakeDebugExcelProcessApi(22369, process),
            new FakeDebugWindowActivator(events),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()));

        await using var session = await automation.StartVisibleAsync(CancellationToken.None);
        await session.OpenGeneratedWorkbookAsync(workbookPath, CancellationToken.None);
        var error = await Assert.ThrowsAsync<DebugSetupException>(() =>
            session.SetNativeBreakpointsAsync(
                [CreateBreakpoint(temp.Path)],
                CancellationToken.None));

        Assert.Contains("component identity", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, process.KillCalls);
        Assert.DoesNotContain("allow-com-foreground", events);
        Assert.DoesNotContain(events, entry => entry.StartsWith("execute:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SetNativeBreakpointsFailsBeforeToggleWhenTheCodeModuleLineCountDoesNotMatch()
    {
        using var temp = TempDirectory.Create();
        var workbookPath = Path.Combine(temp.Path, "GeneratedBook.xlsm");
        File.WriteAllText(workbookPath, "test workbook placeholder");
        var events = new List<string>();
        var model = FakeVbeModel.Create(
            workbookPath,
            events,
            codeLines: Enumerable.Repeat(string.Empty, 8).ToArray());
        var process = new FakeDebugOwnedProcess(
            22370,
            new DateTime(2026, 7, 21, 11, 5, 0, DateTimeKind.Local));
        var automation = new VbeDebugAutomation(
            new FakeExcelDebugApplicationFactory(model.Excel),
            new FakeDebugExcelProcessApi(22370, process),
            new FakeDebugWindowActivator(events),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()));

        await using var session = await automation.StartVisibleAsync(CancellationToken.None);
        await session.OpenGeneratedWorkbookAsync(workbookPath, CancellationToken.None);
        var error = await Assert.ThrowsAsync<DebugSetupException>(() =>
            session.SetNativeBreakpointsAsync(
                [CreateBreakpoint(temp.Path)],
                CancellationToken.None));

        Assert.Contains("line count", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, process.KillCalls);
        Assert.DoesNotContain(events, entry => entry.StartsWith("code-line:", StringComparison.Ordinal));
        Assert.DoesNotContain(events, entry => entry.StartsWith("execute:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SetNativeBreakpointsRejectsAnOutOfRangeMappedLineBeforeReadingTheWorkbook()
    {
        using var temp = TempDirectory.Create();
        var workbookPath = Path.Combine(temp.Path, "GeneratedBook.xlsm");
        File.WriteAllText(workbookPath, "test workbook placeholder");
        var events = new List<string>();
        var model = FakeVbeModel.Create(workbookPath, events);
        var process = new FakeDebugOwnedProcess(
            22371,
            new DateTime(2026, 7, 21, 11, 6, 0, DateTimeKind.Local));
        var automation = new VbeDebugAutomation(
            new FakeExcelDebugApplicationFactory(model.Excel),
            new FakeDebugExcelProcessApi(22371, process),
            new FakeDebugWindowActivator(events),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()));
        var breakpoint = new VbeBreakpoint(
            new DebugSourceBreakpoint(Path.Combine(temp.Path, "DebugModule.bas"), 10),
            CreateSourceMap(),
            10);

        await using var session = await automation.StartVisibleAsync(CancellationToken.None);
        await session.OpenGeneratedWorkbookAsync(workbookPath, CancellationToken.None);
        var error = await Assert.ThrowsAsync<DebugSetupException>(() =>
            session.SetNativeBreakpointsAsync([breakpoint], CancellationToken.None));

        Assert.Contains("outside", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, process.KillCalls);
        Assert.DoesNotContain(events, entry => entry.StartsWith("component:", StringComparison.Ordinal));
        Assert.DoesNotContain(events, entry => entry.StartsWith("execute:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetCompilationHostFactsReturnsVerifiedWindows64VbaCompatibilityConstants()
    {
        var process = new FakeDebugOwnedProcess(
            31414,
            new DateTime(2026, 7, 21, 9, 59, 0, DateTimeKind.Local),
            DebugExcelProcessArchitecture.X64);
        var model = FakeVbeModel.Create(
            "GeneratedBook.xlsm",
            [],
            excelVersion: "16.0",
            vbeVersion: "7.01",
            operatingSystem: "Windows (64-bit) NT 10.00");
        var automation = new VbeDebugAutomation(
            new FakeExcelDebugApplicationFactory(model.Excel),
            new FakeDebugExcelProcessApi(31414, process),
            new FakeDebugWindowActivator(),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()));

        await using var session = await automation.StartVisibleAsync(CancellationToken.None);
        var facts = await session.GetCompilationHostFactsAsync(CancellationToken.None);

        Assert.Equal("16.0", facts.ExcelVersion);
        Assert.Equal("7.01", facts.VbeVersion);
        Assert.Equal("Windows (64-bit) NT 10.00", facts.OperatingSystem);
        Assert.Equal(DebugExcelProcessArchitecture.X64, facts.ExcelProcessArchitecture);
        Assert.Equal(DebugCompilationHostFactsStatus.Verified, facts.Status);
        var constants = Assert.IsType<DebugCompilerBuiltInConstants>(facts.BuiltInConstants);
        Assert.True(constants.Vba6);
        Assert.True(constants.Vba7);
        Assert.False(constants.Win16);
        Assert.True(constants.Win32);
        Assert.True(constants.Win64);
        Assert.False(constants.Mac);
        Assert.Null(facts.UnavailableReason);

        process.Exit(0);
        await session.Completion;
    }

    [Fact]
    public async Task GetCompilationHostFactsReturnsVerifiedWindows32Vba6Constants()
    {
        var process = new FakeDebugOwnedProcess(
            31413,
            new DateTime(2026, 7, 21, 9, 58, 0, DateTimeKind.Local),
            DebugExcelProcessArchitecture.X86);
        var model = FakeVbeModel.Create(
            "GeneratedBook.xlsm",
            [],
            excelVersion: "12.0",
            vbeVersion: "6.05",
            operatingSystem: "Windows (32-bit) NT 6.01");
        var automation = new VbeDebugAutomation(
            new FakeExcelDebugApplicationFactory(model.Excel),
            new FakeDebugExcelProcessApi(31413, process),
            new FakeDebugWindowActivator(),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()));

        await using var session = await automation.StartVisibleAsync(CancellationToken.None);
        var facts = await session.GetCompilationHostFactsAsync(CancellationToken.None);

        Assert.Equal(DebugCompilationHostFactsStatus.Verified, facts.Status);
        var constants = Assert.IsType<DebugCompilerBuiltInConstants>(facts.BuiltInConstants);
        Assert.True(constants.Vba6);
        Assert.False(constants.Vba7);
        Assert.False(constants.Win16);
        Assert.True(constants.Win32);
        Assert.False(constants.Win64);
        Assert.False(constants.Mac);

        process.Exit(0);
        await session.Completion;
    }

    [Fact]
    public async Task GetCompilationHostFactsReportsMismatchWhenOperatingSystemBitnessContradictsProcess()
    {
        var process = new FakeDebugOwnedProcess(
            31412,
            new DateTime(2026, 7, 21, 9, 57, 0, DateTimeKind.Local),
            DebugExcelProcessArchitecture.X64);
        var model = FakeVbeModel.Create(
            "GeneratedBook.xlsm",
            [],
            operatingSystem: "Windows (32-bit) NT 10.00");
        var automation = new VbeDebugAutomation(
            new FakeExcelDebugApplicationFactory(model.Excel),
            new FakeDebugExcelProcessApi(31412, process),
            new FakeDebugWindowActivator(),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()));

        await using var session = await automation.StartVisibleAsync(CancellationToken.None);
        var facts = await session.GetCompilationHostFactsAsync(CancellationToken.None);

        Assert.Equal(DebugCompilationHostFactsStatus.Mismatch, facts.Status);
        Assert.Null(facts.BuiltInConstants);
        Assert.Contains("bitness", facts.UnavailableReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, process.KillCalls);

        process.Exit(0);
        await session.Completion;
    }

    [Fact]
    public async Task GetCompilationHostFactsReportsMismatchWhenExcelAndVbeGenerationsContradict()
    {
        var process = new FakeDebugOwnedProcess(
            31411,
            new DateTime(2026, 7, 21, 9, 56, 0, DateTimeKind.Local),
            DebugExcelProcessArchitecture.X64);
        var model = FakeVbeModel.Create(
            "GeneratedBook.xlsm",
            [],
            excelVersion: "16.0",
            vbeVersion: "6.05");
        var automation = new VbeDebugAutomation(
            new FakeExcelDebugApplicationFactory(model.Excel),
            new FakeDebugExcelProcessApi(31411, process),
            new FakeDebugWindowActivator(),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()));

        await using var session = await automation.StartVisibleAsync(CancellationToken.None);
        var facts = await session.GetCompilationHostFactsAsync(CancellationToken.None);

        Assert.Equal(DebugCompilationHostFactsStatus.Mismatch, facts.Status);
        Assert.Null(facts.BuiltInConstants);
        Assert.Contains("version", facts.UnavailableReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, process.KillCalls);

        process.Exit(0);
        await session.Completion;
    }

    [Theory]
    [InlineData(
        DebugExcelProcessArchitecture.X86,
        "Windows (32-bit) NT 10.00",
        false)]
    [InlineData(
        DebugExcelProcessArchitecture.Arm64,
        "Windows (64-bit) NT 10.00",
        true)]
    public async Task GetCompilationHostFactsUsesTheOwnedExcelProcessArchitecture(
        DebugExcelProcessArchitecture architecture,
        string operatingSystem,
        bool expectedWin64)
    {
        var process = new FakeDebugOwnedProcess(
            31410,
            new DateTime(2026, 7, 21, 9, 55, 0, DateTimeKind.Local),
            architecture);
        var model = FakeVbeModel.Create(
            "GeneratedBook.xlsm",
            [],
            operatingSystem: operatingSystem);
        var automation = new VbeDebugAutomation(
            new FakeExcelDebugApplicationFactory(model.Excel),
            new FakeDebugExcelProcessApi(31410, process),
            new FakeDebugWindowActivator(),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()));

        await using var session = await automation.StartVisibleAsync(CancellationToken.None);
        var facts = await session.GetCompilationHostFactsAsync(CancellationToken.None);

        Assert.Equal(DebugCompilationHostFactsStatus.Verified, facts.Status);
        Assert.Equal(architecture, facts.ExcelProcessArchitecture);
        var constants = Assert.IsType<DebugCompilerBuiltInConstants>(facts.BuiltInConstants);
        Assert.True(constants.Vba6);
        Assert.True(constants.Vba7);
        Assert.Equal(expectedWin64, constants.Win64);

        process.Exit(0);
        await session.Completion;
    }

    [Fact]
    public async Task GetCompilationHostFactsLeavesBuiltInsUnprovedWhenProcessArchitectureIsUnknown()
    {
        var process = new FakeDebugOwnedProcess(
            31409,
            new DateTime(2026, 7, 21, 9, 54, 0, DateTimeKind.Local),
            DebugExcelProcessArchitecture.Unknown);
        var model = FakeVbeModel.Create("GeneratedBook.xlsm", []);
        var automation = new VbeDebugAutomation(
            new FakeExcelDebugApplicationFactory(model.Excel),
            new FakeDebugExcelProcessApi(31409, process),
            new FakeDebugWindowActivator(),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()));

        await using var session = await automation.StartVisibleAsync(CancellationToken.None);
        var facts = await session.GetCompilationHostFactsAsync(CancellationToken.None);

        Assert.Equal(DebugCompilationHostFactsStatus.Unknown, facts.Status);
        Assert.Null(facts.BuiltInConstants);
        Assert.NotNull(facts.UnavailableReason);

        process.Exit(0);
        await session.Completion;
    }

    [Fact]
    public async Task StartVisibleCreatesExcelOnTheStaBoundaryAndCapturesItsExactProcess()
    {
        var process = new FakeDebugOwnedProcess(
            31415,
            new DateTime(2026, 7, 21, 10, 0, 0, DateTimeKind.Local));
        var processApi = new FakeDebugExcelProcessApi(31415, process);
        var excel = new FakeExcelApplication { Hwnd = 2468 };
        var dispatcher = new RecordingStaComDispatcher();
        var automation = new VbeDebugAutomation(
            new FakeExcelDebugApplicationFactory(excel),
            processApi,
            new FakeDebugWindowActivator(),
            new FakeStaComDispatcherFactory(dispatcher));

        await using var session = await automation.StartVisibleAsync(CancellationToken.None);

        Assert.True(excel.Visible);
        Assert.Equal(31415, session.ProcessId);
        Assert.Equal(1, dispatcher.InvokeCalls);
        Assert.False(session.Completion.IsCompleted);

        process.Exit(0);
        Assert.Equal(0, (await session.Completion).ExitCode);
    }

    [Fact]
    public async Task OpenThenSetNativeBreakpointEstablishesExactContextAndExecutesToggleBreakpoint51()
    {
        using var temp = TempDirectory.Create();
        var workbookPath = Path.Combine(temp.Path, "GeneratedBook.xlsm");
        File.WriteAllText(workbookPath, "test workbook placeholder");
        var sourcePath = Path.Combine(temp.Path, "DebugModule.bas");
        var events = new List<string>();
        var model = FakeVbeModel.Create(workbookPath, events);
        var process = new FakeDebugOwnedProcess(
            27182,
            new DateTime(2026, 7, 21, 10, 30, 0, DateTimeKind.Local));
        var automation = new VbeDebugAutomation(
            new FakeExcelDebugApplicationFactory(model.Excel),
            new FakeDebugExcelProcessApi(27182, process),
            new FakeDebugWindowActivator(events),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()));

        await using var session = await automation.StartVisibleAsync(CancellationToken.None);
        await session.OpenGeneratedWorkbookAsync(workbookPath, CancellationToken.None);
        await session.SetNativeBreakpointsAsync(
            [new VbeBreakpoint(
                new DebugSourceBreakpoint(sourcePath, 10),
                CreateSourceMap(),
                9)],
            CancellationToken.None);

        Assert.Equal(
            [
                "enable-events:False",
                $"open:{Path.GetFullPath(workbookPath)}",
                "component:DebugModule",
                "code-line:1:1",
                "code-line:2:1",
                "code-line:3:1",
                "code-line:4:1",
                "code-line:5:1",
                "code-line:6:1",
                "code-line:7:1",
                "code-line:8:1",
                "code-line:9:1",
                "allow-com-foreground",
                "component-activate",
                "vbe-visible",
                "pane-show",
                "active-pane",
                "selection:9:1:9:1",
                "vbe-focus",
                "code-focus",
                "foreground:9753:27182",
                "active-pane-read",
                "selection-read",
                "find-control:1:51:False",
                "execute:51"
            ],
            events);
        Assert.False(session.Completion.IsCompleted);

        process.Exit(0);
        await session.Completion;
    }

    [Fact]
    public async Task WorkbookEventsRemainDisabledUntilBreakpointTransferCompletesAndRunStarts()
    {
        using var temp = TempDirectory.Create();
        var workbookPath = Path.Combine(temp.Path, "GeneratedBook.xlsm");
        File.WriteAllText(workbookPath, "test workbook placeholder");
        var events = new List<string>();
        var model = FakeVbeModel.Create(workbookPath, events);
        var process = new FakeDebugOwnedProcess(
            27183,
            new DateTime(2026, 7, 21, 10, 31, 0, DateTimeKind.Local));
        var automation = new VbeDebugAutomation(
            new FakeExcelDebugApplicationFactory(model.Excel),
            new FakeDebugExcelProcessApi(process.Id, process),
            new FakeDebugWindowActivator(events),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()));

        await using var session = await automation.StartVisibleAsync(CancellationToken.None);
        await session.OpenGeneratedWorkbookAsync(workbookPath, CancellationToken.None);
        Assert.False(model.Excel.EnableEvents);

        await session.SetNativeBreakpointsAsync(
            [CreateBreakpoint(temp.Path)],
            CancellationToken.None);
        Assert.False(model.Excel.EnableEvents);

        await session.RunTargetAsync(
            new DebugTargetProcedure("DebugModule", "RunTarget"),
            CancellationToken.None);

        Assert.True(model.Excel.EnableEvents);
        Assert.Equal(
            [
                "enable-events:False",
                $"open:{Path.GetFullPath(workbookPath)}",
                "find-control:1:51:False",
                "execute:51",
                "find-control:1:186:False",
                "enable-events:True",
                "execute:186"
            ],
            events.Where(entry =>
                    entry.StartsWith("enable-events:", StringComparison.Ordinal)
                    || entry.StartsWith("open:", StringComparison.Ordinal)
                    || entry.StartsWith("find-control:", StringComparison.Ordinal)
                    || entry.StartsWith("execute:", StringComparison.Ordinal))
                .ToArray());

        process.Exit(0);
        await session.Completion;
    }

    [Fact]
    public async Task WorkbookOpenFailureTerminatesTheOwnedProcessWithEventsStillDisabled()
    {
        using var temp = TempDirectory.Create();
        var workbookPath = Path.Combine(temp.Path, "GeneratedBook.xlsm");
        File.WriteAllText(workbookPath, "test workbook placeholder");
        var events = new List<string>();
        var openError = new COMException("open failed");
        var model = FakeVbeModel.Create(
            workbookPath,
            events,
            workbookOpenException: openError);
        var process = new FakeDebugOwnedProcess(
            27184,
            new DateTime(2026, 7, 21, 10, 32, 0, DateTimeKind.Local));
        var automation = new VbeDebugAutomation(
            new FakeExcelDebugApplicationFactory(model.Excel),
            new FakeDebugExcelProcessApi(process.Id, process),
            new FakeDebugWindowActivator(events),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()));

        await using var session = await automation.StartVisibleAsync(CancellationToken.None);
        var error = await Assert.ThrowsAsync<DebugSetupException>(() =>
            session.OpenGeneratedWorkbookAsync(workbookPath, CancellationToken.None));

        Assert.Same(openError, error.InnerException);
        Assert.False(model.Excel.EnableEvents);
        Assert.Equal(1, process.KillCalls);
        Assert.Equal(
            ["enable-events:False", $"open:{Path.GetFullPath(workbookPath)}"],
            events);
    }

    [Fact]
    public async Task SetNativeBreakpointFailsClosedWhenToggleBreakpoint51IsMissing()
    {
        using var temp = TempDirectory.Create();
        var workbookPath = Path.Combine(temp.Path, "GeneratedBook.xlsm");
        File.WriteAllText(workbookPath, "test workbook placeholder");
        var events = new List<string>();
        var model = FakeVbeModel.Create(
            workbookPath,
            events,
            breakpointCommandMissing: true);
        var process = new FakeDebugOwnedProcess(
            16180,
            new DateTime(2026, 7, 21, 10, 45, 0, DateTimeKind.Local));
        var automation = new VbeDebugAutomation(
            new FakeExcelDebugApplicationFactory(model.Excel),
            new FakeDebugExcelProcessApi(16180, process),
            new FakeDebugWindowActivator(
                events,
                unchecked((int)0x80070005)),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()));

        await using var session = await automation.StartVisibleAsync(CancellationToken.None);
        await session.OpenGeneratedWorkbookAsync(workbookPath, CancellationToken.None);
        var error = await Assert.ThrowsAsync<DebugSetupException>(() =>
            session.SetNativeBreakpointsAsync(
                [CreateBreakpoint(temp.Path)],
                CancellationToken.None));

        Assert.Contains("ID 51", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "0x80070005",
            error.Data["CoAllowSetForegroundWindow.HResult"]);
        Assert.Equal(1, process.KillCalls);
        Assert.DoesNotContain("execute:51", events);
        Assert.DoesNotContain("execute:186", events);
    }

    [Fact]
    public async Task SetNativeBreakpointFailsClosedWhenToggleBreakpoint51IsDisabled()
    {
        using var temp = TempDirectory.Create();
        var workbookPath = Path.Combine(temp.Path, "GeneratedBook.xlsm");
        File.WriteAllText(workbookPath, "test workbook placeholder");
        var events = new List<string>();
        var model = FakeVbeModel.Create(
            workbookPath,
            events,
            breakpointCommandEnabled: false);
        var process = new FakeDebugOwnedProcess(
            14142,
            new DateTime(2026, 7, 21, 10, 50, 0, DateTimeKind.Local));
        var automation = new VbeDebugAutomation(
            new FakeExcelDebugApplicationFactory(model.Excel),
            new FakeDebugExcelProcessApi(14142, process),
            new FakeDebugWindowActivator(events),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()));

        await using var session = await automation.StartVisibleAsync(CancellationToken.None);
        await session.OpenGeneratedWorkbookAsync(workbookPath, CancellationToken.None);
        var error = await Assert.ThrowsAsync<DebugSetupException>(() =>
            session.SetNativeBreakpointsAsync(
                [CreateBreakpoint(temp.Path)],
                CancellationToken.None));

        Assert.Contains("disabled", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("Invalid breakpoint", error.Message, StringComparison.Ordinal);
        Assert.Contains(
            "actual generated workbook compilation context",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("inactive or non-executable", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not relocated", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, process.KillCalls);
        Assert.DoesNotContain("execute:51", events);
        Assert.DoesNotContain("execute:186", events);
    }

    [Fact]
    public async Task SetNativeBreakpointFailsClosedWhenToggleBreakpoint51Throws()
    {
        using var temp = TempDirectory.Create();
        var workbookPath = Path.Combine(temp.Path, "GeneratedBook.xlsm");
        File.WriteAllText(workbookPath, "test workbook placeholder");
        var events = new List<string>();
        var executeError = new COMException("toggle breakpoint failed");
        var model = FakeVbeModel.Create(
            workbookPath,
            events,
            breakpointCommandException: executeError);
        var process = new FakeDebugOwnedProcess(
            17320,
            new DateTime(2026, 7, 21, 10, 55, 0, DateTimeKind.Local));
        var automation = new VbeDebugAutomation(
            new FakeExcelDebugApplicationFactory(model.Excel),
            new FakeDebugExcelProcessApi(17320, process),
            new FakeDebugWindowActivator(events),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()));

        await using var session = await automation.StartVisibleAsync(CancellationToken.None);
        await session.OpenGeneratedWorkbookAsync(workbookPath, CancellationToken.None);
        var error = await Assert.ThrowsAsync<DebugSetupException>(() =>
            session.SetNativeBreakpointsAsync(
                [CreateBreakpoint(temp.Path)],
                CancellationToken.None));

        Assert.Same(executeError, error.InnerException);
        Assert.Equal(1, process.KillCalls);
        Assert.Equal(1, events.Count(entry => entry == "execute:51"));
        Assert.DoesNotContain("execute:186", events);
    }

    [Fact]
    public async Task SetNativeBreakpointFailsClosedWhenTheExactSelectionIsNotRetained()
    {
        using var temp = TempDirectory.Create();
        var workbookPath = Path.Combine(temp.Path, "GeneratedBook.xlsm");
        File.WriteAllText(workbookPath, "test workbook placeholder");
        var events = new List<string>();
        var model = FakeVbeModel.Create(
            workbookPath,
            events,
            selectionMatches: false);
        var process = new FakeDebugOwnedProcess(
            22360,
            new DateTime(2026, 7, 21, 10, 57, 0, DateTimeKind.Local));
        var automation = new VbeDebugAutomation(
            new FakeExcelDebugApplicationFactory(model.Excel),
            new FakeDebugExcelProcessApi(22360, process),
            new FakeDebugWindowActivator(events),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()));

        await using var session = await automation.StartVisibleAsync(CancellationToken.None);
        await session.OpenGeneratedWorkbookAsync(workbookPath, CancellationToken.None);
        var error = await Assert.ThrowsAsync<DebugSetupException>(() =>
            session.SetNativeBreakpointsAsync(
                [CreateBreakpoint(temp.Path)],
                CancellationToken.None));

        Assert.Contains("exact VBE line selection", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, process.KillCalls);
        Assert.DoesNotContain(events, entry => entry.StartsWith("find-control:", StringComparison.Ordinal));
        Assert.DoesNotContain(events, entry => entry.StartsWith("execute:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SetNativeBreakpointFailsClosedWhenTheTargetCodePaneIsNotActive()
    {
        using var temp = TempDirectory.Create();
        var workbookPath = Path.Combine(temp.Path, "GeneratedBook.xlsm");
        File.WriteAllText(workbookPath, "test workbook placeholder");
        var events = new List<string>();
        var model = FakeVbeModel.Create(
            workbookPath,
            events,
            activeCodePaneMatches: false);
        var process = new FakeDebugOwnedProcess(
            22361,
            new DateTime(2026, 7, 21, 10, 58, 0, DateTimeKind.Local));
        var automation = new VbeDebugAutomation(
            new FakeExcelDebugApplicationFactory(model.Excel),
            new FakeDebugExcelProcessApi(22361, process),
            new FakeDebugWindowActivator(events),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()));

        await using var session = await automation.StartVisibleAsync(CancellationToken.None);
        await session.OpenGeneratedWorkbookAsync(workbookPath, CancellationToken.None);
        var error = await Assert.ThrowsAsync<DebugSetupException>(() =>
            session.SetNativeBreakpointsAsync(
                [CreateBreakpoint(temp.Path)],
                CancellationToken.None));

        Assert.Contains("code pane is not active", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, process.KillCalls);
        Assert.DoesNotContain("selection-read", events);
        Assert.DoesNotContain(events, entry => entry.StartsWith("execute:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SetNativeBreakpointFailsClosedWhenGeneratedCodeLineDoesNotMatchTheSnapshot()
    {
        using var temp = TempDirectory.Create();
        var workbookPath = Path.Combine(temp.Path, "GeneratedBook.xlsm");
        File.WriteAllText(workbookPath, "test workbook placeholder");
        var events = new List<string>();
        var model = FakeVbeModel.Create(
            workbookPath,
            events,
            codeLine: "    Debug.Print \"different line\"");
        var process = new FakeDebugOwnedProcess(
            22362,
            new DateTime(2026, 7, 21, 10, 59, 0, DateTimeKind.Local));
        var automation = new VbeDebugAutomation(
            new FakeExcelDebugApplicationFactory(model.Excel),
            new FakeDebugExcelProcessApi(22362, process),
            new FakeDebugWindowActivator(events),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()));

        await using var session = await automation.StartVisibleAsync(CancellationToken.None);
        await session.OpenGeneratedWorkbookAsync(workbookPath, CancellationToken.None);
        var error = await Assert.ThrowsAsync<DebugSetupException>(() =>
            session.SetNativeBreakpointsAsync(
                [CreateBreakpoint(temp.Path)],
                CancellationToken.None));

        Assert.Contains("does not exactly match", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, process.KillCalls);
        Assert.DoesNotContain("allow-com-foreground", events);
        Assert.DoesNotContain(events, entry => entry.StartsWith("execute:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpenAndRunActivatesTheExactProcedureAndExecutesNativeRunCommand186()
    {
        using var temp = TempDirectory.Create();
        var workbookPath = Path.Combine(temp.Path, "GeneratedBook.xlsm");
        File.WriteAllText(workbookPath, "test workbook placeholder");
        var events = new List<string>();
        var model = FakeVbeModel.Create(workbookPath, events);
        var process = new FakeDebugOwnedProcess(
            27182,
            new DateTime(2026, 7, 21, 10, 30, 0, DateTimeKind.Local));
        var windowActivator = new FakeDebugWindowActivator(events);
        var automation = new VbeDebugAutomation(
            new FakeExcelDebugApplicationFactory(model.Excel),
            new FakeDebugExcelProcessApi(27182, process),
            windowActivator,
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()));

        await using var session = await automation.StartVisibleAsync(CancellationToken.None);
        await session.OpenGeneratedWorkbookAsync(workbookPath, CancellationToken.None);
        await session.RunTargetAsync(
            new DebugTargetProcedure("DebugModule", "RunTarget"),
            CancellationToken.None);

        Assert.Equal(
            [
                "enable-events:False",
                $"open:{Path.GetFullPath(workbookPath)}",
                "component:DebugModule",
                "procedure:RunTarget:0",
                "allow-com-foreground",
                "component-activate",
                "vbe-visible",
                "pane-show",
                "active-pane",
                "selection:7:1:7:1",
                "vbe-focus",
                "code-focus",
                "foreground:9753:27182",
                "active-pane-read",
                "selection-read",
                "find-control:1:186:False",
                "enable-events:True",
                "execute:186"
            ],
            events);
        Assert.False(session.Completion.IsCompleted);

        process.Exit(0);
        await session.Completion;
    }

    [Fact]
    public async Task OpenAndRunFailsClosedWhenNativeRunCommand186IsMissing()
    {
        using var temp = TempDirectory.Create();
        var workbookPath = Path.Combine(temp.Path, "GeneratedBook.xlsm");
        File.WriteAllText(workbookPath, "test workbook placeholder");
        var events = new List<string>();
        var model = FakeVbeModel.Create(workbookPath, events, runCommandMissing: true);
        var process = new FakeDebugOwnedProcess(
            16180,
            new DateTime(2026, 7, 21, 11, 0, 0, DateTimeKind.Local));
        var automation = new VbeDebugAutomation(
            new FakeExcelDebugApplicationFactory(model.Excel),
            new FakeDebugExcelProcessApi(16180, process),
            new FakeDebugWindowActivator(
                events,
                unchecked((int)0x80070005)),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()));

        await using var session = await automation.StartVisibleAsync(CancellationToken.None);

        await session.OpenGeneratedWorkbookAsync(workbookPath, CancellationToken.None);
        var error = await Assert.ThrowsAsync<DebugSetupException>(() =>
            session.RunTargetAsync(
                new DebugTargetProcedure("DebugModule", "RunTarget"),
                CancellationToken.None));

        Assert.Contains("ID 186", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "0x80070005",
            error.Data["CoAllowSetForegroundWindow.HResult"]);
        Assert.False(model.Excel.EnableEvents);
        Assert.Equal(1, process.KillCalls);
        Assert.DoesNotContain("enable-events:True", events);
        Assert.DoesNotContain(events, entry => entry.StartsWith("execute:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpenAndRunFailsClosedWhenNativeRunCommand186IsDisabled()
    {
        using var temp = TempDirectory.Create();
        var workbookPath = Path.Combine(temp.Path, "GeneratedBook.xlsm");
        File.WriteAllText(workbookPath, "test workbook placeholder");
        var events = new List<string>();
        var model = FakeVbeModel.Create(workbookPath, events, runCommandEnabled: false);
        var process = new FakeDebugOwnedProcess(
            14142,
            new DateTime(2026, 7, 21, 11, 30, 0, DateTimeKind.Local));
        var automation = new VbeDebugAutomation(
            new FakeExcelDebugApplicationFactory(model.Excel),
            new FakeDebugExcelProcessApi(14142, process),
            new FakeDebugWindowActivator(events),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()));

        await using var session = await automation.StartVisibleAsync(CancellationToken.None);

        await session.OpenGeneratedWorkbookAsync(workbookPath, CancellationToken.None);
        var error = await Assert.ThrowsAsync<DebugSetupException>(() =>
            session.RunTargetAsync(
                new DebugTargetProcedure("DebugModule", "RunTarget"),
                CancellationToken.None));

        Assert.Contains("disabled", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(model.Excel.EnableEvents);
        Assert.Equal(1, process.KillCalls);
        Assert.DoesNotContain("enable-events:True", events);
        Assert.DoesNotContain(events, entry => entry.StartsWith("execute:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpenAndRunFailsClosedWhenNativeRunCommand186Throws()
    {
        using var temp = TempDirectory.Create();
        var workbookPath = Path.Combine(temp.Path, "GeneratedBook.xlsm");
        File.WriteAllText(workbookPath, "test workbook placeholder");
        var events = new List<string>();
        var executeError = new COMException("run failed");
        var model = FakeVbeModel.Create(
            workbookPath,
            events,
            runCommandException: executeError);
        var process = new FakeDebugOwnedProcess(
            17320,
            new DateTime(2026, 7, 21, 12, 0, 0, DateTimeKind.Local));
        var automation = new VbeDebugAutomation(
            new FakeExcelDebugApplicationFactory(model.Excel),
            new FakeDebugExcelProcessApi(17320, process),
            new FakeDebugWindowActivator(events),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()));

        await using var session = await automation.StartVisibleAsync(CancellationToken.None);

        await session.OpenGeneratedWorkbookAsync(workbookPath, CancellationToken.None);
        var error = await Assert.ThrowsAsync<DebugSetupException>(() =>
            session.RunTargetAsync(
                new DebugTargetProcedure("DebugModule", "RunTarget"),
                CancellationToken.None));

        Assert.Same(executeError, error.InnerException);
        Assert.Equal(1, process.KillCalls);
        Assert.Equal(1, events.Count(entry => entry == "execute:186"));
    }

    private static VbeBreakpoint CreateBreakpoint(string directory)
        => new(
            new DebugSourceBreakpoint(Path.Combine(directory, "DebugModule.bas"), 10),
            CreateSourceMap(),
            9);

    private static VbeCodeModuleSourceMap CreateSourceMap(
        VbaModuleKind moduleKind = VbaModuleKind.StandardModule)
        => new(
            "DebugModule",
            moduleKind,
            ImmutableArray.Create(
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "    Debug.Print \"break here\""));

    private static string GetSourceExtension(VbaModuleKind moduleKind)
        => moduleKind switch
        {
            VbaModuleKind.StandardModule => ".bas",
            VbaModuleKind.ClassModule => ".cls",
            VbaModuleKind.FormModule => ".frm",
            _ => throw new ArgumentOutOfRangeException(nameof(moduleKind), moduleKind, null)
        };
}

public sealed class FakeExcelApplication
{
    private bool enableEvents = true;

    public int Hwnd { get; init; }

    public string Version { get; init; } = "16.0";

    public string OperatingSystem { get; init; } = "Windows (64-bit) NT 10.00";

    public bool Visible { get; set; }

    public List<string>? Events { get; init; }

    public bool EnableEvents
    {
        get => enableEvents;
        set
        {
            enableEvents = value;
            Events?.Add($"enable-events:{value}");
        }
    }

    public FakeWorkbooks? Workbooks { get; init; }

    public FakeVbe? VBE { get; init; }
}

internal sealed class FakeExcelDebugApplicationFactory(object application) : IExcelDebugApplicationFactory
{
    public object Create() => application;
}

internal sealed class FakeDebugWindowActivator(
    List<string>? events = null,
    int foregroundPermissionHResult = 0) : IDebugWindowActivator
{
    public int AllowComServerForeground(object comServerObject)
    {
        events?.Add("allow-com-foreground");
        return foregroundPermissionHResult;
    }

    public void BringOwnedWindowToForeground(nint windowHandle, int processId)
    {
        events?.Add($"foreground:{windowHandle}:{processId}");
    }
}

internal sealed class FakeStaComDispatcherFactory(IStaComDispatcher dispatcher) : IStaComDispatcherFactory
{
    public IStaComDispatcher Create() => dispatcher;
}

internal sealed class RecordingStaComDispatcher : IStaComDispatcher
{
    public int InvokeCalls { get; private set; }

    public Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InvokeCalls++;
        return Task.FromResult(operation());
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed record FakeVbeModel(FakeExcelApplication Excel)
{
    public static FakeVbeModel Create(
        string workbookPath,
        List<string> events,
        bool breakpointCommandMissing = false,
        bool breakpointCommandEnabled = true,
        Exception? breakpointCommandException = null,
        bool selectionMatches = true,
        bool activeCodePaneMatches = true,
        string codeLine = "    Debug.Print \"break here\"",
        bool runCommandMissing = false,
        bool runCommandEnabled = true,
        Exception? runCommandException = null,
        IReadOnlyList<string>? codeLines = null,
        int componentType = 1,
        string componentName = "DebugModule",
        string? secondaryComponentName = null,
        IReadOnlyList<string>? secondaryCodeLines = null,
        int secondaryComponentType = 1,
        string excelVersion = "16.0",
        string vbeVersion = "7.01",
        string operatingSystem = "Windows (64-bit) NT 10.00",
        Exception? workbookOpenException = null)
    {
        var codeWindow = new FakeVbeWindow(8642, events, "code-focus");
        var codePane = new FakeCodePane(codeWindow, events, selectionMatches);
        var actualCodeLines = codeLines
            ?? Enumerable.Repeat(string.Empty, 8).Append(codeLine).ToArray();
        var codeModule = new FakeCodeModule(codePane, events, actualCodeLines);
        var component = new FakeVbComponent(
            componentName,
            componentType,
            codeModule,
            events);
        var componentsByName = new Dictionary<string, FakeVbComponent>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["DebugModule"] = component
        };
        if (secondaryComponentName is not null)
        {
            var secondaryCodeWindow = new FakeVbeWindow(8643, events, "code-focus");
            var secondaryCodePane = new FakeCodePane(
                secondaryCodeWindow,
                events,
                selectionMatches);
            var secondaryCodeModule = new FakeCodeModule(
                secondaryCodePane,
                events,
                secondaryCodeLines
                    ?? throw new ArgumentException(
                        "Secondary code lines are required with a secondary component.",
                        nameof(secondaryCodeLines)));
            componentsByName.Add(secondaryComponentName, new FakeVbComponent(
                secondaryComponentName,
                secondaryComponentType,
                secondaryCodeModule,
                events));
        }

        var components = new FakeVbComponents(componentsByName, events);
        var project = new FakeVbProject(components);
        var workbook = new FakeWorkbook(Path.GetFullPath(workbookPath), project);
        var workbooks = new FakeWorkbooks(workbook, events)
        {
            OpenException = workbookOpenException
        };
        var mainWindow = new FakeVbeWindow(9753, events, "vbe-focus");
        var breakpointControl = breakpointCommandMissing
            ? null
            : new FakeCommandBarControl(51, events)
            {
                Enabled = breakpointCommandEnabled,
                ExecuteException = breakpointCommandException
            };
        var runControl = runCommandMissing
            ? null
            : new FakeCommandBarControl(186, events)
            {
                Enabled = runCommandEnabled,
                ExecuteException = runCommandException
            };
        var commandBars = new FakeCommandBars(breakpointControl, runControl, events);
        var vbe = new FakeVbe(
            mainWindow,
            commandBars,
            events,
            activeCodePaneMatches,
            vbeVersion);
        var excel = new FakeExcelApplication
        {
            Hwnd = 2468,
            Version = excelVersion,
            OperatingSystem = operatingSystem,
            Events = events,
            Workbooks = workbooks,
            VBE = vbe
        };
        return new FakeVbeModel(excel);
    }
}

public sealed class FakeWorkbooks(FakeWorkbook workbook, List<string> events)
{
    public Exception? OpenException { get; init; }

    public object Open(string workbookPath)
    {
        events.Add($"open:{Path.GetFullPath(workbookPath)}");
        if (OpenException is not null)
        {
            throw OpenException;
        }

        return workbook;
    }
}

public sealed class FakeWorkbook(string fullName, FakeVbProject project)
{
    public string FullName { get; } = fullName;

    public FakeVbProject VBProject { get; } = project;
}

public sealed class FakeVbProject(FakeVbComponents components)
{
    public int Mode { get; init; } = 2;

    public FakeVbComponents VBComponents { get; } = components;
}

public sealed class FakeVbComponents(
    IReadOnlyDictionary<string, FakeVbComponent> componentsByName,
    List<string> events)
{
    public object Item(string moduleName)
    {
        events.Add($"component:{moduleName}");
        return componentsByName[moduleName];
    }
}

public sealed class FakeVbComponent(
    string name,
    int type,
    FakeCodeModule codeModule,
    List<string> events)
{
    public string Name { get; } = name;

    public int Type { get; } = type;

    public FakeCodeModule CodeModule { get; } = codeModule;

    public void Activate() => events.Add("component-activate");
}

public sealed class FakeCodeModule(
    FakeCodePane codePane,
    List<string> events,
    IReadOnlyList<string> codeLines)
{
    public FakeCodePane CodePane { get; } = codePane;

    public int CountOfLines => codeLines.Count;

    public int ProcBodyLine(string procedureName, int procedureKind)
    {
        events.Add($"procedure:{procedureName}:{procedureKind}");
        return 7;
    }

    public string Lines(int startLine, int count)
    {
        events.Add($"code-line:{startLine}:{count}");
        return string.Join(
            "\r\n",
            codeLines.Skip(startLine - 1).Take(count));
    }
}

public sealed class FakeCodePane(
    FakeVbeWindow window,
    List<string> events,
    bool selectionMatches)
{
    private int startLine;
    private int startColumn;
    private int endLine;
    private int endColumn;

    public FakeVbeWindow Window { get; } = window;

    public void Show() => events.Add("pane-show");

    public void SetSelection(int startLine, int startColumn, int endLine, int endColumn)
    {
        this.startLine = startLine;
        this.startColumn = startColumn;
        this.endLine = endLine;
        this.endColumn = endColumn;
        events.Add($"selection:{startLine}:{startColumn}:{endLine}:{endColumn}");
    }

    public void GetSelection(
        ref int startLine,
        ref int startColumn,
        ref int endLine,
        ref int endColumn)
    {
        events.Add("selection-read");
        startLine = selectionMatches ? this.startLine : this.startLine + 1;
        startColumn = this.startColumn;
        endLine = this.endLine;
        endColumn = this.endColumn;
    }
}

public sealed class FakeVbeWindow(
    int hwnd,
    List<string> events,
    string focusEvent)
{
    private bool visible;

    public int HWnd { get; } = hwnd;

    public bool Visible
    {
        get => visible;
        set
        {
            visible = value;
            if (value)
            {
                events.Add("vbe-visible");
            }
        }
    }

    public void SetFocus() => events.Add(focusEvent);
}

public sealed class FakeVbe(
    FakeVbeWindow mainWindow,
    FakeCommandBars commandBars,
    List<string> events,
    bool activeCodePaneMatches,
    string version)
{
    private object? activeCodePane;

    public FakeVbeWindow MainWindow { get; } = mainWindow;

    public FakeCommandBars CommandBars { get; } = commandBars;

    public string Version { get; } = version;

    public object? ActiveCodePane
    {
        get
        {
            events.Add("active-pane-read");
            return activeCodePaneMatches ? activeCodePane : new object();
        }
        set
        {
            activeCodePane = value;
            events.Add("active-pane");
        }
    }
}

public sealed class FakeCommandBars(
    FakeCommandBarControl? breakpointControl,
    FakeCommandBarControl? runControl,
    List<string> events)
{
    public object? FindControl(
        object type,
        object id,
        object tag,
        object visible)
    {
        events.Add($"find-control:{type}:{id}:{visible}");
        return Convert.ToInt32(id) switch
        {
            51 => breakpointControl,
            186 => runControl,
            _ => null
        };
    }
}

public sealed class FakeCommandBarControl(int id, List<string> events)
{
    public int Id { get; } = id;

    public bool BuiltIn { get; init; } = true;

    public bool Enabled { get; init; } = true;

    public Exception? ExecuteException { get; init; }

    public void Execute()
    {
        events.Add($"execute:{Id}");
        if (ExecuteException is not null)
        {
            throw ExecuteException;
        }
    }
}
