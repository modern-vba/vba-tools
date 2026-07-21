using System.Diagnostics;
using System.Text;
using VbaDev.App.Debugging;
using VbaDev.App.Projects;
using VbaDev.Composition;
using VbaDev.Infrastructure.Debugging;
using Xunit;

namespace VbaDev.Tests;

[Collection(WindowsExcelIntegrationCollection.Name)]
public sealed class VbeDebugAutomationWindowsExcelIntegrationTests
{
    private const int VbeBreakMode = 1;
    private const int VbeDesignMode = 2;
    private const int RunOrContinueCommandId = 186;

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task OneNativeBreakpointHitsThenContinuesWhileTheOwnedExcelSessionRemainsRunning()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "DebugProject");
        var markerPath = Path.Combine(temp.Path, "vbe-breakpoint-marker.txt");
        var baselineProcessIds = CaptureExcelProcessIds();
        var commandLine = ToolingCompositionRoot.CreateCommandLineApplication(temp.Path);

        var createResult = commandLine.Run(
            ["new", "excel", "--name", "DebugProject", "--output", projectRoot]);

        Assert.True(
            createResult.ExitCode == 0,
            $"Project creation failed.{Environment.NewLine}{createResult.StandardError}");
        var sourcePath = Path.Combine(
            projectRoot,
            "src",
            "DebugProject",
            "DebugModule.bas");
        var sourceText = CreateDebugModule(markerPath);
        File.WriteAllText(sourcePath, sourceText, new UTF8Encoding(false));
        var breakpointLine = Array.IndexOf(
            sourceText.Split(["\r\n"], StringSplitOptions.None),
            "    fileNumber = FreeFile");
        Assert.True(breakpointLine >= 0);

        var applicationFactory = new CapturingExcelDebugApplicationFactory();
        var dispatcherFactory = new CapturingStaComDispatcherFactory();
        var automation = new VbeDebugAutomation(
            applicationFactory,
            new WindowsDebugExcelProcessApi(),
            new WindowsDebugWindowActivator(),
            dispatcherFactory);
        var composition = ToolingCompositionRoot.CreateDebugAdapterComposition(
            projectRoot,
            vbeDebugSessionFactory: automation);
        var context = composition.ProjectContextResolver.Resolve(
            new ProjectResolutionRequest(projectRoot, "DebugProject", projectRoot));
        var snapshot = new DebugSourceSnapshot(
            DebugSourceSnapshot.CurrentSchemaVersion,
            [new DebugSourceFileSnapshot(sourcePath, sourceText)],
            null)
        {
            Breakpoints = [new DebugSourceBreakpoint(sourcePath, breakpointLine)]
        };
        DebugRunningSession? running = null;
        try
        {
            try
            {
                running = await composition.LaunchCoordinator.LaunchAsync(
                    new DebugLaunchRequest(
                        context,
                        new DebugTargetProcedure("DebugModule", "RunTarget"),
                        snapshot),
                    new IntegrationDebugLifecycleSink(),
                    CancellationToken.None);
            }
            catch (DebugSetupException ex)
            {
                var foregroundPermission =
                    ex.Data["CoAllowSetForegroundWindow.HResult"] ?? "not recorded";
                var setForegroundResult =
                    ex.Data["SetForegroundWindow.Result"] ?? "not recorded";
                throw new InvalidOperationException(
                    $"{ex.Message} CoAllowSetForegroundWindow HRESULT: {foregroundPermission}; SetForegroundWindow result: {setForegroundResult}.",
                    ex);
            }

            await WaitForProjectModeAsync(
                applicationFactory,
                dispatcherFactory,
                VbeBreakMode,
                TimeSpan.FromSeconds(15));

            Assert.False(File.Exists(markerPath));
            Assert.False(running.Completion.IsCompleted);
            var newProcessIds = CaptureExcelProcessIds()
                .Except(baselineProcessIds)
                .Order()
                .ToArray();
            Assert.Equal([running.ProcessId], newProcessIds);

            await ContinueNativeExecutionAsync(
                applicationFactory,
                dispatcherFactory,
                CancellationToken.None);
            await WaitForFileAsync(markerPath, TimeSpan.FromSeconds(15));
            await WaitForProjectModeAsync(
                applicationFactory,
                dispatcherFactory,
                VbeDesignMode,
                TimeSpan.FromSeconds(15));

            var markerLines = File.ReadAllLines(markerPath);
            Assert.Equal(Path.GetFullPath(context.BinDocumentPath), markerLines[0]);
            Assert.Equal("True", markerLines[1]);
            Assert.Equal("True", markerLines[2]);
            Assert.False(running.Completion.IsCompleted);
        }
        finally
        {
            if (running is not null)
            {
                var processId = running.ProcessId;
                await running.TerminateAsync();
                await running.Completion.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.DoesNotContain(processId, CaptureExcelProcessIds());
            }
        }
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task ExplicitZeroBreakpointLaunchRunsTheGeneratedWorkbookAndOwnsExcelUntilTermination()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "DebugProject");
        var markerPath = Path.Combine(temp.Path, "vbe-run-marker.txt");
        var baselineProcessIds = CaptureExcelProcessIds();
        var commandLine = ToolingCompositionRoot.CreateCommandLineApplication(temp.Path);

        var createResult = commandLine.Run(
            ["new", "excel", "--name", "DebugProject", "--output", projectRoot]);

        Assert.True(
            createResult.ExitCode == 0,
            $"Project creation failed.{Environment.NewLine}{createResult.StandardError}");
        var sourcePath = Path.Combine(
            projectRoot,
            "src",
            "DebugProject",
            "DebugModule.bas");
        File.WriteAllText(sourcePath, CreateDebugModule(markerPath), new UTF8Encoding(false));

        var composition = ToolingCompositionRoot.CreateDebugAdapterComposition(projectRoot);
        var context = composition.ProjectContextResolver.Resolve(
            new ProjectResolutionRequest(projectRoot, "DebugProject", projectRoot));
        DebugRunningSession? running = null;
        try
        {
            try
            {
                running = await composition.LaunchCoordinator.LaunchAsync(
                    new DebugLaunchRequest(
                        context,
                        new DebugTargetProcedure("DebugModule", "RunTarget"),
                        new DebugSourceSnapshot(DebugSourceSnapshot.CurrentSchemaVersion, [], null)),
                    new IntegrationDebugLifecycleSink(),
                    CancellationToken.None);
            }
            catch (DebugSetupException ex)
            {
                var foregroundPermission =
                    ex.Data["CoAllowSetForegroundWindow.HResult"] ?? "not recorded";
                var setForegroundResult =
                    ex.Data["SetForegroundWindow.Result"] ?? "not recorded";
                throw new InvalidOperationException(
                    $"{ex.Message} CoAllowSetForegroundWindow HRESULT: {foregroundPermission}; SetForegroundWindow result: {setForegroundResult}.",
                    ex);
            }

            await WaitForFileAsync(markerPath, TimeSpan.FromSeconds(15));

            var newProcessIds = CaptureExcelProcessIds()
                .Except(baselineProcessIds)
                .Order()
                .ToArray();
            Assert.Equal([running.ProcessId], newProcessIds);
            Assert.True(File.Exists(context.BinDocumentPath));
            var markerLines = File.ReadAllLines(markerPath);
            Assert.Equal(Path.GetFullPath(context.BinDocumentPath), markerLines[0]);
            Assert.Equal("True", markerLines[1]);
            Assert.Equal("True", markerLines[2]);
            Assert.False(running.Completion.IsCompleted);
        }
        finally
        {
            if (running is not null)
            {
                var processId = running.ProcessId;
                await running.TerminateAsync();
                await running.Completion.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.DoesNotContain(processId, CaptureExcelProcessIds());
            }
        }
    }

    private static string CreateDebugModule(string markerPath)
    {
        var vbaMarkerPath = markerPath.Replace("\"", "\"\"", StringComparison.Ordinal);
        return string.Join(
            "\r\n",
            "Attribute VB_Name = \"DebugModule\"",
            "Option Explicit",
            "Option Private Module",
            string.Empty,
            "Public Sub RunTarget()",
            "    Dim fileNumber As Integer",
            "    fileNumber = FreeFile",
            $"    Open \"{vbaMarkerPath}\" For Output As #fileNumber",
            "    Print #fileNumber, ThisWorkbook.FullName",
            "    Print #fileNumber, CStr(Application.Visible)",
            "    Print #fileNumber, CStr(Application.VBE.MainWindow.Visible)",
            "    Close #fileNumber",
            "End Sub",
            string.Empty);
    }

    private static IReadOnlySet<int> CaptureExcelProcessIds()
    {
        var result = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName("EXCEL"))
        {
            using (process)
            {
                result.Add(process.Id);
            }
        }

        return result;
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!File.Exists(path))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellation.Token);
        }
    }

    private static async Task WaitForProjectModeAsync(
        CapturingExcelDebugApplicationFactory applicationFactory,
        CapturingStaComDispatcherFactory dispatcherFactory,
        int expectedMode,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            while (await GetProjectModeAsync(
                       applicationFactory,
                       dispatcherFactory,
                       cancellation.Token) != expectedMode)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The VBE project did not enter mode {expectedMode} within {timeout}.");
        }
    }

    private static Task<int> GetProjectModeAsync(
        CapturingExcelDebugApplicationFactory applicationFactory,
        CapturingStaComDispatcherFactory dispatcherFactory,
        CancellationToken cancellationToken)
    {
        var application = applicationFactory.Application
            ?? throw new InvalidOperationException("The debug Excel application was not captured.");
        var dispatcher = dispatcherFactory.Dispatcher
            ?? throw new InvalidOperationException("The VBE STA dispatcher was not captured.");
        return dispatcher.InvokeForTestAsync(
            () =>
            {
                dynamic excel = application;
                dynamic project = excel.VBE.ActiveVBProject;
                return (int)project.Mode;
            },
            cancellationToken);
    }

    private static Task ContinueNativeExecutionAsync(
        CapturingExcelDebugApplicationFactory applicationFactory,
        CapturingStaComDispatcherFactory dispatcherFactory,
        CancellationToken cancellationToken)
    {
        var application = applicationFactory.Application
            ?? throw new InvalidOperationException("The debug Excel application was not captured.");
        var dispatcher = dispatcherFactory.Dispatcher
            ?? throw new InvalidOperationException("The VBE STA dispatcher was not captured.");
        return dispatcher.InvokeForTestAsync(
            () =>
            {
                dynamic excel = application;
                dynamic vbe = excel.VBE;
                dynamic project = vbe.ActiveVBProject;
                Assert.Equal(VbeBreakMode, (int)project.Mode);

                object? commandObject = vbe.CommandBars.FindControl(
                    1,
                    RunOrContinueCommandId,
                    Type.Missing,
                    false);
                Assert.NotNull(commandObject);
                dynamic command = commandObject;
                Assert.Equal(RunOrContinueCommandId, (int)command.Id);
                Assert.True((bool)command.BuiltIn);
                Assert.True((bool)command.Enabled);
                command.Execute();
                return true;
            },
            cancellationToken);
    }

    private sealed class CapturingExcelDebugApplicationFactory : IExcelDebugApplicationFactory
    {
        public object? Application { get; private set; }

        public object Create()
        {
            Application = new ExcelDebugApplicationFactory().Create();
            return Application;
        }
    }

    private sealed class CapturingStaComDispatcherFactory : IStaComDispatcherFactory
    {
        public CapturingStaComDispatcher? Dispatcher { get; private set; }

        public IStaComDispatcher Create()
        {
            Dispatcher = new CapturingStaComDispatcher(new StaComDispatcher());
            return Dispatcher;
        }
    }

    private sealed class CapturingStaComDispatcher(IStaComDispatcher inner) : IStaComDispatcher
    {
        public Task<T> InvokeAsync<T>(
            Func<T> operation,
            CancellationToken cancellationToken)
            => inner.InvokeAsync(operation, cancellationToken);

        public Task<T> InvokeForTestAsync<T>(
            Func<T> operation,
            CancellationToken cancellationToken)
            => inner.InvokeAsync(operation, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class IntegrationDebugLifecycleSink : IDebugLifecycleSink
    {
        public ValueTask WriteAsync(
            DebugLifecycleMessage message,
            CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }
}

public sealed class WindowsExcelIntegrationFactAttribute : FactAttribute
{
    private const string OptInEnvironmentVariable =
        "VBA_TOOLS_RUN_EXCEL_INTEGRATION_TESTS";

    public WindowsExcelIntegrationFactAttribute()
    {
        Timeout = 120_000;
        if (!string.Equals(
                Environment.GetEnvironmentVariable(OptInEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            Skip = $"Set {OptInEnvironmentVariable}=1 to run Windows Excel integration tests.";
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WindowsExcelIntegrationCollection
{
    public const string Name = "Windows Excel Integration";
}
