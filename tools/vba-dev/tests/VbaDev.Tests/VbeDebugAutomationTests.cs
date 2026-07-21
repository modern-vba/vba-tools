using VbaDev.App.Debugging;
using VbaDev.Infrastructure.Debugging;
using System.Runtime.InteropServices;
using Xunit;

namespace VbaDev.Tests;

public sealed class VbeDebugAutomationTests
{
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
        await session.SetNativeBreakpointAsync(
            new VbeBreakpoint(
                new DebugSourceBreakpoint(sourcePath, 10),
                "DebugModule",
                9,
                "    Debug.Print \"break here\""),
            CancellationToken.None);

        Assert.Equal(
            [
                $"open:{Path.GetFullPath(workbookPath)}",
                "component:DebugModule",
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
            session.SetNativeBreakpointAsync(
                CreateBreakpoint(temp.Path),
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
            session.SetNativeBreakpointAsync(
                CreateBreakpoint(temp.Path),
                CancellationToken.None));

        Assert.Contains("disabled", error.Message, StringComparison.OrdinalIgnoreCase);
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
            session.SetNativeBreakpointAsync(
                CreateBreakpoint(temp.Path),
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
            session.SetNativeBreakpointAsync(
                CreateBreakpoint(temp.Path),
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
            session.SetNativeBreakpointAsync(
                CreateBreakpoint(temp.Path),
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
            session.SetNativeBreakpointAsync(
                CreateBreakpoint(temp.Path),
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
        Assert.Equal(1, process.KillCalls);
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
        Assert.Equal(1, process.KillCalls);
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
            "DebugModule",
            9,
            "    Debug.Print \"break here\"");
}

public sealed class FakeExcelApplication
{
    public int Hwnd { get; init; }

    public bool Visible { get; set; }

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
        Exception? runCommandException = null)
    {
        var codeWindow = new FakeVbeWindow(8642, events, "code-focus");
        var codePane = new FakeCodePane(codeWindow, events, selectionMatches);
        var codeModule = new FakeCodeModule(codePane, events, codeLine);
        var component = new FakeVbComponent(codeModule, events);
        var components = new FakeVbComponents(component, events);
        var project = new FakeVbProject(components);
        var workbook = new FakeWorkbook(Path.GetFullPath(workbookPath), project);
        var workbooks = new FakeWorkbooks(workbook, events);
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
        var vbe = new FakeVbe(mainWindow, commandBars, events, activeCodePaneMatches);
        var excel = new FakeExcelApplication
        {
            Hwnd = 2468,
            Workbooks = workbooks,
            VBE = vbe
        };
        return new FakeVbeModel(excel);
    }
}

public sealed class FakeWorkbooks(FakeWorkbook workbook, List<string> events)
{
    public object Open(string workbookPath)
    {
        events.Add($"open:{Path.GetFullPath(workbookPath)}");
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

public sealed class FakeVbComponents(FakeVbComponent component, List<string> events)
{
    public object Item(string moduleName)
    {
        events.Add($"component:{moduleName}");
        return component;
    }
}

public sealed class FakeVbComponent(FakeCodeModule codeModule, List<string> events)
{
    public int Type { get; init; } = 1;

    public FakeCodeModule CodeModule { get; } = codeModule;

    public void Activate() => events.Add("component-activate");
}

public sealed class FakeCodeModule(
    FakeCodePane codePane,
    List<string> events,
    string codeLine)
{
    public FakeCodePane CodePane { get; } = codePane;

    public int ProcBodyLine(string procedureName, int procedureKind)
    {
        events.Add($"procedure:{procedureName}:{procedureKind}");
        return 7;
    }

    public string Lines(int startLine, int count)
    {
        events.Add($"code-line:{startLine}:{count}");
        return codeLine;
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
    bool activeCodePaneMatches)
{
    private object? activeCodePane;

    public FakeVbeWindow MainWindow { get; } = mainWindow;

    public FakeCommandBars CommandBars { get; } = commandBars;

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
