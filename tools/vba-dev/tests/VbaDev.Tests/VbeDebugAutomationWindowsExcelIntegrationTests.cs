using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using VbaDev.App.Cli;
using VbaDev.App.Debugging;
using VbaDev.App.Projects;
using VbaDev.Composition;
using VbaDev.Infrastructure.Debugging;
using VbaDev.Infrastructure.Workbooks;
using VbaLanguageServer.Syntax;
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
    public async Task ActualOwnedExcelAndVbeHostFactsProveCompilerBuiltIns()
    {
        var baselineProcessIds = CaptureExcelProcessIds();
        var automation = new VbeDebugAutomation();
        IVbeDebugSession? session = null;
        try
        {
            session = await automation.StartVisibleAsync(CancellationToken.None);

            var facts = await session.GetCompilationHostFactsAsync(CancellationToken.None);

            Assert.False(string.IsNullOrWhiteSpace(facts.ExcelVersion));
            Assert.False(string.IsNullOrWhiteSpace(facts.VbeVersion));
            Assert.StartsWith("Windows (", facts.OperatingSystem, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(DebugExcelProcessArchitecture.Unknown, facts.ExcelProcessArchitecture);
            Assert.Equal(DebugCompilationHostFactsStatus.Verified, facts.Status);
            var constants = Assert.IsType<DebugCompilerBuiltInConstants>(facts.BuiltInConstants);
            Assert.True(constants.Vba6);
            Assert.True(constants.Vba7);
            Assert.False(constants.Win16);
            Assert.True(constants.Win32);
            Assert.Equal(
                facts.ExcelProcessArchitecture is
                    DebugExcelProcessArchitecture.X64 or
                    DebugExcelProcessArchitecture.Arm64,
                constants.Win64);
            Assert.False(constants.Mac);
            Assert.Equal(
                [session.ProcessId],
                CaptureExcelProcessIds().Except(baselineProcessIds).Order().ToArray());
        }
        finally
        {
            if (session is not null)
            {
                var processId = session.ProcessId;
                await session.DisposeAsync();
                await session.Completion.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.DoesNotContain(processId, CaptureExcelProcessIds());
            }
        }
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task ExportedStandardClassAndFormBreakpointsToggleAtTheirActualVbideLines()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "DebugProject");
        var markerPath = Path.Combine(temp.Path, "vbe-mapped-modules-marker.txt");
        var baselineProcessIds = CaptureExcelProcessIds();
        var commandLine = ToolingCompositionRoot.CreateCommandLineApplication(temp.Path);

        CreateProject(commandLine, projectRoot);
        var sourceSetPath = Path.Combine(projectRoot, "src", "DebugProject");
        var fixtures = CreateExportedModuleBreakpointFixtures(sourceSetPath, markerPath);
        await WaitForNoNewExcelProcessesAsync(baselineProcessIds, TimeSpan.FromSeconds(15));
        WriteFixtures(fixtures);
        var snapshot = CreateSnapshot(fixtures);
        var mappedBreakpoints = snapshot.Breakpoints
            .Select(breakpoint => new BreakpointSourceMapper().Map(snapshot, breakpoint))
            .ToArray();
        Assert.Equal(
            [
                ("DebugClass", 4),
                ("DebugForm", 5),
                ("DebugModule", 13)
            ],
            mappedBreakpoints.Select(breakpoint =>
                (breakpoint.ModuleName, breakpoint.VbideLine)));

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
        DebugRunningSession? running = null;
        try
        {
            running = await composition.LaunchCoordinator.LaunchAsync(
                new DebugLaunchRequest(
                    context,
                    new DebugTargetProcedure("DebugModule", "RunTarget"),
                    snapshot),
                new IntegrationDebugLifecycleSink(),
                CancellationToken.None);

            await WaitForFileAsync(markerPath, TimeSpan.FromSeconds(15));
            await WaitForProjectModeAsync(
                applicationFactory,
                dispatcherFactory,
                VbeDesignMode,
                TimeSpan.FromSeconds(15));

            var actualLines = await ReadActualCodeLinesAsync(
                applicationFactory,
                dispatcherFactory,
                mappedBreakpoints,
                CancellationToken.None);
            Assert.Equal(mappedBreakpoints.Length, actualLines.Count);
            foreach (var breakpoint in mappedBreakpoints)
            {
                Assert.Equal(
                    breakpoint.ExpectedCodeLine,
                    actualLines[(breakpoint.ModuleName, breakpoint.VbideLine)]);
            }

            var newProcessIds = CaptureExcelProcessIds()
                .Except(baselineProcessIds)
                .Order()
                .ToArray();
            Assert.Equal([running.ProcessId], newProcessIds);
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
    public async Task ActualHostVba6AndVba7ActiveBranchesAcceptExactNativeBreakpoints()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "DebugProject");
        var markerPath = Path.Combine(temp.Path, "vbe-active-branch-marker.txt");
        var baselineProcessIds = CaptureExcelProcessIds();
        var commandLine = ToolingCompositionRoot.CreateCommandLineApplication(temp.Path);

        CreateProject(commandLine, projectRoot);
        var sourcePath = Path.Combine(
            projectRoot,
            "src",
            "DebugProject",
            "DebugModule.bas");
        var sourceText = CreateConditionalDebugModule(markerPath);
        var activeVba7Line = FindLine(sourceText, "    Debug.Print \"active VBA7 branch\"");
        var activeVba6Line = FindLine(sourceText, "    Debug.Print \"active VBA6 branch\"");
        var fixture = new ExportedBreakpointFixture(sourcePath, sourceText, activeVba7Line);
        WriteFixtures([fixture]);
        var snapshot = new DebugSourceSnapshot(
            DebugSourceSnapshot.CurrentSchemaVersion,
            [new DebugSourceFileSnapshot(sourcePath, sourceText)],
            null)
        {
            Breakpoints =
            [
                new DebugSourceBreakpoint(sourcePath, activeVba7Line),
                new DebugSourceBreakpoint(sourcePath, activeVba6Line)
            ]
        };
        var mappedBreakpoints = snapshot.Breakpoints
            .Select(breakpoint => new BreakpointSourceMapper().Map(snapshot, breakpoint))
            .ToArray();
        Assert.Equal([14, 22], mappedBreakpoints.Select(breakpoint => breakpoint.VbideLine));

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
        DebugRunningSession? running = null;
        try
        {
            running = await composition.LaunchCoordinator.LaunchAsync(
                new DebugLaunchRequest(
                    context,
                    new DebugTargetProcedure("DebugModule", "RunTarget"),
                    snapshot),
                new IntegrationDebugLifecycleSink(),
                CancellationToken.None);

            await WaitForFileAsync(markerPath, TimeSpan.FromSeconds(15));
            await WaitForProjectModeAsync(
                applicationFactory,
                dispatcherFactory,
                VbeDesignMode,
                TimeSpan.FromSeconds(15));
            var actualLines = await ReadActualCodeLinesAsync(
                applicationFactory,
                dispatcherFactory,
                mappedBreakpoints,
                CancellationToken.None);

            foreach (var breakpoint in mappedBreakpoints)
            {
                Assert.Equal(
                    breakpoint.ExpectedCodeLine,
                    actualLines[(breakpoint.ModuleName, breakpoint.VbideLine)]);
            }
            var newProcessIds = CaptureExcelProcessIds()
                .Except(baselineProcessIds)
                .Order()
                .ToArray();
            Assert.Equal([running.ProcessId], newProcessIds);
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
    public async Task ActualHostVba7InactiveBranchFailsBeforeRunWithoutRelocation()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "DebugProject");
        var markerPath = Path.Combine(temp.Path, "vbe-inactive-branch-marker.txt");
        var baselineProcessIds = CaptureExcelProcessIds();
        var commandLine = ToolingCompositionRoot.CreateCommandLineApplication(temp.Path);

        CreateProject(commandLine, projectRoot);
        var sourcePath = Path.Combine(
            projectRoot,
            "src",
            "DebugProject",
            "DebugModule.bas");
        var sourceText = CreateConditionalDebugModule(markerPath);
        var inactiveLine = FindLine(sourceText, "    Debug.Print \"inactive legacy branch\"");
        var fixture = new ExportedBreakpointFixture(sourcePath, sourceText, inactiveLine);
        WriteFixtures([fixture]);
        var snapshot = CreateSnapshot([fixture]);
        var mapped = new BreakpointSourceMapper().Map(snapshot, snapshot.Breakpoints[0]);
        Assert.Equal(16, mapped.VbideLine);

        var composition = ToolingCompositionRoot.CreateDebugAdapterComposition(projectRoot);
        var applicationFactory = new CapturingExcelDebugApplicationFactory();
        var dispatcherFactory = new CapturingStaComDispatcherFactory();
        var automation = new VbeDebugAutomation(
            applicationFactory,
            new WindowsDebugExcelProcessApi(),
            new WindowsDebugWindowActivator(),
            dispatcherFactory);
        composition = ToolingCompositionRoot.CreateDebugAdapterComposition(
            projectRoot,
            vbeDebugSessionFactory: automation);
        var context = composition.ProjectContextResolver.Resolve(
            new ProjectResolutionRequest(projectRoot, "DebugProject", projectRoot));

        DebugRunningSession? unexpectedRunning = null;
        DebugSetupException? error = null;
        try
        {
            try
            {
                unexpectedRunning = await composition.LaunchCoordinator.LaunchAsync(
                    new DebugLaunchRequest(
                        context,
                        new DebugTargetProcedure("DebugModule", "RunTarget"),
                        snapshot),
                    new IntegrationDebugLifecycleSink(),
                    CancellationToken.None);
            }
            catch (DebugSetupException ex)
            {
                error = ex;
            }

            Assert.NotNull(error);
            Assert.Contains("actual generated workbook compilation context", error.Message);
            Assert.Contains(
                "physical source line is inactive in the actual generated workbook compilation context",
                error.Message);
            Assert.Contains($":{inactiveLine + 1}'", error.Message);
            Assert.Contains("not relocated", error.Message);
            Assert.False(File.Exists(markerPath));
        }
        finally
        {
            if (unexpectedRunning is not null)
            {
                await unexpectedRunning.TerminateAsync();
                await unexpectedRunning.Completion.WaitAsync(TimeSpan.FromSeconds(15));
            }
        }

        await WaitForNoNewExcelProcessesAsync(baselineProcessIds, TimeSpan.FromSeconds(15));
    }

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

        var applicationFactory = new CapturingExcelDebugApplicationFactory(
            initialAutomationSecurity: 3);
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
            Assert.Equal(
                3,
                await ReadAutomationSecurityAsync(
                    applicationFactory,
                    dispatcherFactory,
                    CancellationToken.None));
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
    public async Task ExplicitStartupWrapperPromptCanBeStoppedWithoutOpenTimeStartupOrOrphanedExcel()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "DebugProject");
        var unexpectedOpenMarkerPath = Path.Combine(temp.Path, "unexpected-workbook-open.txt");
        var startupEnteredMarkerPath = Path.Combine(temp.Path, "startup-routine-entered.txt");
        var startupReturnedMarkerPath = Path.Combine(temp.Path, "startup-routine-returned.txt");
        var baselineProcessIds = CaptureExcelProcessIds();
        var commandLine = ToolingCompositionRoot.CreateCommandLineApplication(temp.Path);

        CreateProject(commandLine, projectRoot);
        var sourcePath = Path.Combine(
            projectRoot,
            "src",
            "DebugProject",
            "DebugModule.bas");
        var sourceText = CreateStartupWrapperModule();
        File.WriteAllText(sourcePath, sourceText, new UTF8Encoding(false));
        var contextResolver = ToolingCompositionRoot
            .CreateDebugAdapterComposition(projectRoot)
            .ProjectContextResolver;
        var context = contextResolver.Resolve(
            new ProjectResolutionRequest(projectRoot, "DebugProject", projectRoot));
        InstallStartupWrapperOpenHandler(
            context.TemplateDocumentPath,
            unexpectedOpenMarkerPath,
            startupEnteredMarkerPath,
            startupReturnedMarkerPath);
        var buildResult = commandLine.Run(
            ["build", "--project", projectRoot, "--document", "DebugProject"]);
        Assert.True(
            buildResult.ExitCode == 0,
            $"Project build failed.{Environment.NewLine}{buildResult.StandardError}");
        await WaitForNoNewExcelProcessesAsync(baselineProcessIds, TimeSpan.FromSeconds(15));

        var automation = new VbeDebugAutomation();
        IVbeDebugSession? session = null;
        using var launchCancellation = new CancellationTokenSource();
        try
        {
            session = await automation.StartVisibleAsync(CancellationToken.None);
            var sink = new IntegrationDebugLifecycleSink();
            await session.OpenGeneratedWorkbookAsync(
                context.BinDocumentPath,
                sink,
                launchCancellation.Token);
            Assert.False(File.Exists(unexpectedOpenMarkerPath));

            var run = session.RunTargetAsync(
                new DebugTargetProcedure("DebugModule", "DebugStartup"),
                sink,
                launchCancellation.Token);
            var inputWait = await sink.InputRequired.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(DebugInputWaitKind.ExcelOrVbe, inputWait.Kind);
            Assert.Equal(DebugInputWaitPhase.TargetStart, inputWait.Phase);
            Assert.Equal(session.ProcessId, inputWait.ProcessId);
            await WaitForFileAsync(startupEnteredMarkerPath, TimeSpan.FromSeconds(15));
            Assert.False(File.Exists(startupReturnedMarkerPath));

            launchCancellation.Cancel();
            await session.TerminateAsync();
            try
            {
                await run.WaitAsync(TimeSpan.FromSeconds(15));
            }
            catch (Exception ex) when (ex is OperationCanceledException or DebugSetupException)
            {
            }

            await session.Completion.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.DoesNotContain(session.ProcessId, CaptureExcelProcessIds());
            Assert.False(File.Exists(unexpectedOpenMarkerPath));
            Assert.False(File.Exists(startupReturnedMarkerPath));
        }
        finally
        {
            launchCancellation.Cancel();
            if (session is not null)
            {
                var processId = session.ProcessId;
                await session.DisposeAsync();
                await session.Completion.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.DoesNotContain(processId, CaptureExcelProcessIds());
            }
        }

        await WaitForNoNewExcelProcessesAsync(baselineProcessIds, TimeSpan.FromSeconds(15));
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task ExplicitZeroBreakpointLaunchRunsTheGeneratedWorkbookAndOwnsExcelUntilTermination()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "DebugProject");
        var markerPath = Path.Combine(temp.Path, "vbe-run-marker.txt");
        var workbookOpenMarkerPath = Path.Combine(temp.Path, "vbe-workbook-open-marker.txt");
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

        var composition = ToolingCompositionRoot.CreateDebugAdapterComposition(projectRoot);
        var context = composition.ProjectContextResolver.Resolve(
            new ProjectResolutionRequest(projectRoot, "DebugProject", projectRoot));
        InstallVisibleWorkbookOpenMarker(
            context.TemplateDocumentPath,
            workbookOpenMarkerPath);
        DebugRunningSession? running = null;
        try
        {
            try
            {
                running = await composition.LaunchCoordinator.LaunchAsync(
                    new DebugLaunchRequest(
                        context,
                        new DebugTargetProcedure("DebugModule", "RunTarget"),
                        new DebugSourceSnapshot(
                            DebugSourceSnapshot.CurrentSchemaVersion,
                            [new DebugSourceFileSnapshot(sourcePath, sourceText)],
                            null)
                        {
                            Breakpoints = []
                        }),
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

            Assert.False(File.Exists(workbookOpenMarkerPath));
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

    private static string CreateStartupWrapperModule()
        => string.Join(
            "\r\n",
            "Attribute VB_Name = \"DebugModule\"",
            "Option Explicit",
            "Option Private Module",
            string.Empty,
            "Public Sub DebugStartup()",
            "    ThisWorkbook.StartupRoutine",
            "End Sub",
            string.Empty);

    private static void InstallVisibleWorkbookOpenMarker(
        string workbookPath,
        string markerPath)
    {
        object? projectObject = null;
        object? componentsObject = null;
        object? componentObject = null;
        object? codeModuleObject = null;
        using var session = ExcelComWorkbookSession.Open(workbookPath);
        try
        {
            dynamic workbook = session.WorkbookObject;
            projectObject = workbook.VBProject;
            dynamic project = projectObject;
            componentsObject = project.VBComponents;
            dynamic components = componentsObject;
            componentObject = components.Item((string)workbook.CodeName);
            dynamic component = componentObject;
            codeModuleObject = component.CodeModule;
            dynamic codeModule = codeModuleObject;
            var vbaMarkerPath = markerPath.Replace("\"", "\"\"", StringComparison.Ordinal);
            codeModule.AddFromString(string.Join(
                "\r\n",
                "Option Explicit",
                string.Empty,
                "Private Sub Workbook_Open()",
                "    If Not Application.Visible Then Exit Sub",
                "    Dim fileNumber As Integer",
                "    fileNumber = FreeFile",
                $"    Open \"{vbaMarkerPath}\" For Output As #fileNumber",
                "    Print #fileNumber, \"opened before debug setup\"",
                "    Close #fileNumber",
                "End Sub",
                string.Empty,
                "Private Sub Workbook_WindowDeactivate(ByVal Wn As Window)",
                "    If Not Application.Visible Then Exit Sub",
                "    Dim fileNumber As Integer",
                "    fileNumber = FreeFile",
                $"    Open \"{vbaMarkerPath}\" For Output As #fileNumber",
                "    Print #fileNumber, \"window deactivated before native run\"",
                "    Close #fileNumber",
                "End Sub",
                string.Empty));
            workbook.Save();
        }
        finally
        {
            ComObjectReleaser.Release(codeModuleObject);
            ComObjectReleaser.Release(componentObject);
            ComObjectReleaser.Release(componentsObject);
            ComObjectReleaser.Release(projectObject);
        }
    }

    private static void InstallStartupWrapperOpenHandler(
        string workbookPath,
        string markerPath,
        string startupEnteredMarkerPath,
        string startupReturnedMarkerPath)
    {
        object? projectObject = null;
        object? componentsObject = null;
        object? componentObject = null;
        object? codeModuleObject = null;
        using var session = ExcelComWorkbookSession.Open(workbookPath);
        try
        {
            dynamic workbook = session.WorkbookObject;
            projectObject = workbook.VBProject;
            dynamic project = projectObject;
            componentsObject = project.VBComponents;
            dynamic components = componentsObject;
            componentObject = components.Item((string)workbook.CodeName);
            dynamic component = componentObject;
            codeModuleObject = component.CodeModule;
            dynamic codeModule = codeModuleObject;
            var vbaMarkerPath = markerPath.Replace("\"", "\"\"", StringComparison.Ordinal);
            var vbaStartupMarkerPath = startupEnteredMarkerPath.Replace(
                "\"",
                "\"\"",
                StringComparison.Ordinal);
            var vbaStartupReturnedMarkerPath = startupReturnedMarkerPath.Replace(
                "\"",
                "\"\"",
                StringComparison.Ordinal);
            codeModule.AddFromString(string.Join(
                "\r\n",
                "Option Explicit",
                string.Empty,
                "Private Sub Workbook_Open()",
                "    If Not Application.Visible Then Exit Sub",
                "    Dim fileNumber As Integer",
                "    fileNumber = FreeFile",
                $"    Open \"{vbaMarkerPath}\" For Output As #fileNumber",
                "    Print #fileNumber, \"unexpected open-time startup\"",
                "    Close #fileNumber",
                "    StartupRoutine",
                "End Sub",
                string.Empty,
                "Public Sub StartupRoutine()",
                "    Dim fileNumber As Integer",
                "    fileNumber = FreeFile",
                $"    Open \"{vbaStartupMarkerPath}\" For Output As #fileNumber",
                "    Print #fileNumber, \"entered\"",
                "    Close #fileNumber",
                "    MsgBox \"VBA Tools controlled startup prompt\", vbOKOnly, \"VBA Tools integration\"",
                "    fileNumber = FreeFile",
                $"    Open \"{vbaStartupReturnedMarkerPath}\" For Output As #fileNumber",
                "    Print #fileNumber, \"returned\"",
                "    Close #fileNumber",
                "End Sub",
                string.Empty));
            workbook.Save();
        }
        finally
        {
            ComObjectReleaser.Release(codeModuleObject);
            ComObjectReleaser.Release(componentObject);
            ComObjectReleaser.Release(componentsObject);
            ComObjectReleaser.Release(projectObject);
        }
    }

    private static IReadOnlyList<ExportedBreakpointFixture> CreateExportedModuleBreakpointFixtures(
        string sourceSetPath,
        string markerPath)
    {
        var standardSource = CreateMappedStandardModule(markerPath);
        var classSource = string.Join(
            "\r\n",
            "VERSION 1.0 CLASS",
            "BEGIN",
            "  MultiUse = -1  'True",
            "END",
            "Attribute VB_Name = \"DebugClass\"",
            "Attribute VB_GlobalNameSpace = False",
            "Attribute VB_Creatable = False",
            "Attribute VB_PredeclaredId = False",
            "Attribute VB_Exposed = False",
            "Option Explicit",
            string.Empty,
            "Public Sub ClassBreakpointTarget()",
            "    Debug.Print \"class breakpoint\"",
            "End Sub",
            string.Empty);
        var formPath = Path.Combine(sourceSetPath, "DebugForm.frm");
        var formSource = ExportDebugFormSource(formPath);

        return
        [
            new ExportedBreakpointFixture(
                Path.Combine(sourceSetPath, "DebugModule.bas"),
                standardSource,
                FindLine(standardSource, "    Debug.Print \"standard breakpoint\"")),
            new ExportedBreakpointFixture(
                Path.Combine(sourceSetPath, "DebugClass.cls"),
                classSource,
                FindLine(classSource, "    Debug.Print \"class breakpoint\"")),
            new ExportedBreakpointFixture(
                formPath,
                formSource,
                FindLine(formSource, "    Debug.Print \"form breakpoint\""))
        ];
    }

    private static string ExportDebugFormSource(string sourcePath)
    {
        object? projectObject = null;
        object? componentsObject = null;
        object? componentObject = null;
        object? codeModuleObject = null;
        using (var session = ExcelComWorkbookSession.Create())
        {
            try
            {
                dynamic workbook = session.WorkbookObject;
                projectObject = workbook.VBProject;
                dynamic project = projectObject;
                componentsObject = project.VBComponents;
                dynamic components = componentsObject;
                componentObject = components.Add(3);
                dynamic component = componentObject;
                component.Name = "DebugForm";
                codeModuleObject = component.CodeModule;
                dynamic codeModule = codeModuleObject;
                var existingLineCount = (int)codeModule.CountOfLines;
                if (existingLineCount > 0)
                {
                    codeModule.DeleteLines(1, existingLineCount);
                }

                codeModule.AddFromString(string.Join(
                    "\r\n",
                    "Option Explicit",
                    string.Empty,
                    "Public Sub FormBreakpointTarget()",
                    "    Debug.Print \"form breakpoint\"",
                    "End Sub",
                    string.Empty));
                component.Export(sourcePath);
            }
            finally
            {
                ComObjectReleaser.Release(codeModuleObject);
                ComObjectReleaser.Release(componentObject);
                ComObjectReleaser.Release(componentsObject);
                ComObjectReleaser.Release(projectObject);
            }
        }

        Assert.True(File.Exists(sourcePath), $"VBIDE did not export the UserForm source: {sourcePath}");
        var sidecarPath = Path.ChangeExtension(sourcePath, ".frx");
        Assert.True(File.Exists(sidecarPath), $"VBIDE did not export the UserForm sidecar: {sidecarPath}");
        return VbaSourceFileTextReader.Decode(File.ReadAllBytes(sourcePath));
    }

    private static string CreateMappedStandardModule(string markerPath)
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
            "    Print #fileNumber, \"ran\"",
            "    Close #fileNumber",
            "End Sub",
            string.Empty,
            "Public Sub StandardBreakpointTarget()",
            "    Debug.Print \"standard breakpoint\"",
            "End Sub",
            string.Empty);
    }

    private static string CreateConditionalDebugModule(string markerPath)
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
            "    Print #fileNumber, \"ran\"",
            "    Close #fileNumber",
            "End Sub",
            string.Empty,
            "Private Sub ConditionalBreakpointTarget()",
            "#If VBA7 Then",
            "    Debug.Print \"active VBA7 branch\"",
            "#Else",
            "    Debug.Print \"inactive legacy branch\"",
            "#End If",
            "End Sub",
            string.Empty,
            "Private Sub Vba6ConditionalBreakpointTarget()",
            "#If VBA6 Then",
            "    Debug.Print \"active VBA6 branch\"",
            "#Else",
            "    Debug.Print \"inactive pre-VBA6 branch\"",
            "#End If",
            "End Sub",
            string.Empty);
    }

    private static void CreateProject(CommandLineApplication commandLine, string projectRoot)
    {
        var createResult = commandLine.Run(
            ["new", "excel", "--name", "DebugProject", "--output", projectRoot]);
        Assert.True(
            createResult.ExitCode == 0,
            $"Project creation failed.{Environment.NewLine}{createResult.StandardError}");
    }

    private static void WriteFixtures(IEnumerable<ExportedBreakpointFixture> fixtures)
    {
        foreach (var fixture in fixtures)
        {
            if (File.Exists(fixture.SourcePath))
            {
                Assert.Equal(
                    fixture.SourceText,
                    VbaSourceFileTextReader.Decode(File.ReadAllBytes(fixture.SourcePath)));
                continue;
            }

            File.WriteAllText(fixture.SourcePath, fixture.SourceText, new UTF8Encoding(false));
        }
    }

    private static DebugSourceSnapshot CreateSnapshot(
        IEnumerable<ExportedBreakpointFixture> fixtures)
    {
        var materialized = fixtures
            .OrderBy(fixture => fixture.SourcePath, StringComparer.Ordinal)
            .ToArray();
        return new DebugSourceSnapshot(
            DebugSourceSnapshot.CurrentSchemaVersion,
            materialized
                .Select(fixture => new DebugSourceFileSnapshot(
                    fixture.SourcePath,
                    fixture.SourceText))
                .ToImmutableArray(),
            null)
        {
            Breakpoints = materialized
                .Select(fixture => new DebugSourceBreakpoint(
                    fixture.SourcePath,
                    fixture.BreakpointLine))
                .ToImmutableArray()
        };
    }

    private static int FindLine(string source, string expectedLine)
    {
        var line = Array.IndexOf(
            source.Split(["\r\n"], StringSplitOptions.None),
            expectedLine);
        Assert.True(line >= 0, $"Expected source line was not found: {expectedLine}");
        return line;
    }

    private static Task<Dictionary<(string ModuleName, int Line), string>> ReadActualCodeLinesAsync(
        CapturingExcelDebugApplicationFactory applicationFactory,
        CapturingStaComDispatcherFactory dispatcherFactory,
        IReadOnlyList<VbeBreakpoint> breakpoints,
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
                dynamic components = excel.VBE.ActiveVBProject.VBComponents;
                var result = new Dictionary<(string ModuleName, int Line), string>();
                foreach (var breakpoint in breakpoints)
                {
                    dynamic component = components.Item(breakpoint.ModuleName);
                    dynamic codeModule = component.CodeModule;
                    result.Add(
                        (breakpoint.ModuleName, breakpoint.VbideLine),
                        (string)codeModule.Lines(breakpoint.VbideLine, 1));
                }

                return result;
            },
            cancellationToken);
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

    private static async Task WaitForNoNewExcelProcessesAsync(
        IReadOnlySet<int> baselineProcessIds,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            while (CaptureExcelProcessIds().Except(baselineProcessIds).Any())
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            var remaining = CaptureExcelProcessIds()
                .Except(baselineProcessIds)
                .Order()
                .ToArray();
            throw new TimeoutException(
                $"Excel processes created by the failed debug launch did not exit within {timeout}: {string.Join(", ", remaining)}.");
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

    private static Task<int> ReadAutomationSecurityAsync(
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
                return (int)excel.AutomationSecurity;
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

    private sealed class CapturingExcelDebugApplicationFactory(
        int? initialAutomationSecurity = null) : IExcelDebugApplicationFactory
    {
        public object? Application { get; private set; }

        public object Create()
        {
            Application = new ExcelDebugApplicationFactory().Create();
            if (initialAutomationSecurity is int automationSecurity)
            {
                dynamic excel = Application;
                excel.AutomationSecurity = automationSecurity;
            }

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
        public TaskCompletionSource<DebugInputWait> InputRequired { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask WriteAsync(
            DebugLifecycleMessage message,
            CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask InputRequiredAsync(
            DebugInputWait inputWait,
            CancellationToken cancellationToken)
        {
            InputRequired.TrySetResult(inputWait);
            return ValueTask.CompletedTask;
        }
    }

    private sealed record ExportedBreakpointFixture(
        string SourcePath,
        string SourceText,
        int BreakpointLine);
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
