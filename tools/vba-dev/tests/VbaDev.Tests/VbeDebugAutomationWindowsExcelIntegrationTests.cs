using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
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
    public async Task DefaultDoctorRunsTheCompleteNativeVbeProbeAndLeavesNoState()
    {
        using var temp = TempDirectory.Create();
        var baselineProcessIds = CaptureExcelProcessIds();
        var baselineProbeDirectories = Directory
            .GetDirectories(Path.GetTempPath(), "vba-tools-doctor-*")
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            var application = ToolingCompositionRoot.CreateCommandLineApplication(temp.Path);

            var result = application.Run(["doctor"]);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("[PASS] VBA debug capability contract", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("Protocol 1.1", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("[PASS] Excel COM availability", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("[PASS] Owned Excel process", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("[PASS] Windows Job ownership", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("[PASS] VBIDE project access", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("[PASS] Native Toggle Breakpoint command (ID 51)", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("[PASS] Native Run command (ID 186)", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("[PASS] VBE break mode", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("[PASS] Native Continue command (ID 186)", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("[PASS] Debug procedure completion", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("[PASS] Native breakpoint cleanup (ID 51)", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("[PASS] Temporary debug probe cleanup", result.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("[FAIL]", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            await WaitForNoNewExcelProcessesAsync(
                baselineProcessIds,
                TimeSpan.FromSeconds(20));
            var remainingProbeDirectories = Directory
                .GetDirectories(Path.GetTempPath(), "vba-tools-doctor-*")
                .Select(Path.GetFullPath)
                .Where(path => !baselineProbeDirectories.Contains(path))
                .ToArray();
            Assert.Empty(remainingProbeDirectories);
        }
    }

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
    public async Task DapDisconnectStopsAStartupPromptAndLeavesNoOwnedExcelProcess()
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
        var context = ToolingCompositionRoot
            .CreateDebugAdapterComposition(projectRoot)
            .ProjectContextResolver
            .Resolve(new ProjectResolutionRequest(projectRoot, "DebugProject", projectRoot));
        InstallStartupWrapperOpenHandler(
            context.TemplateDocumentPath,
            unexpectedOpenMarkerPath,
            startupEnteredMarkerPath,
            startupReturnedMarkerPath);
        await WaitForNoNewExcelProcessesAsync(baselineProcessIds, TimeSpan.FromSeconds(15));

        await using (var adapter = DebugAdapterChildProcess.Start(projectRoot))
        {
            await InitializeDebugAdapterAsync(adapter);
            await adapter.SendRequestAsync(
                2,
                "launch",
                CreateDapLaunchArguments(
                    projectRoot,
                    sourcePath,
                    sourceText,
                    procedureName: "DebugStartup"));
            await adapter.SendRequestAsync(3, "configurationDone", new { });
            AssertSuccessfulResponse(
                await adapter.WaitForResponseAsync(3, TimeSpan.FromSeconds(15)),
                "configurationDone");

            await WaitForFileAsync(startupEnteredMarkerPath, TimeSpan.FromSeconds(60));
            Assert.False(File.Exists(unexpectedOpenMarkerPath));
            Assert.False(File.Exists(startupReturnedMarkerPath));
            Assert.Single(CaptureExcelProcessIds().Except(baselineProcessIds));

            await adapter.SendRequestAsync(
                4,
                "disconnect",
                new { terminateDebuggee = true });
            AssertSuccessfulResponse(
                await adapter.WaitForResponseAsync(4, TimeSpan.FromSeconds(30)),
                "disconnect");
            await adapter.WaitForExitAsync(TimeSpan.FromSeconds(30));
        }

        Assert.False(File.Exists(unexpectedOpenMarkerPath));
        Assert.False(File.Exists(startupReturnedMarkerPath));
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

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task AdditionalWorkbookOpenedByTargetRemainsOwnedUntilSessionTermination()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "DebugProject");
        var markerPath = Path.Combine(temp.Path, "additional-workbook-marker.txt");
        var additionalWorkbookPath = Path.Combine(temp.Path, "session-owned-extra.xlsx");
        var baselineProcessIds = CaptureExcelProcessIds();
        var commandLine = ToolingCompositionRoot.CreateCommandLineApplication(temp.Path);

        CreateProject(commandLine, projectRoot);
        var sourcePath = Path.Combine(
            projectRoot,
            "src",
            "DebugProject",
            "DebugModule.bas");
        var sourceText = CreateAdditionalWorkbookOwnershipModule(
            markerPath,
            additionalWorkbookPath);
        File.WriteAllText(sourcePath, sourceText, new UTF8Encoding(false));

        var composition = ToolingCompositionRoot.CreateDebugAdapterComposition(projectRoot);
        var context = composition.ProjectContextResolver.Resolve(
            new ProjectResolutionRequest(projectRoot, "DebugProject", projectRoot));
        DebugRunningSession? running = null;
        try
        {
            running = await LaunchZeroBreakpointTargetAsync(
                composition.LaunchCoordinator,
                context,
                sourcePath,
                sourceText);

            await WaitForFileAsync(markerPath, TimeSpan.FromSeconds(15));

            var markerLines = File.ReadAllLines(markerPath);
            Assert.Equal(4, markerLines.Length);
            Assert.Equal(Path.GetFullPath(context.BinDocumentPath), markerLines[0]);
            Assert.Equal(Path.GetFullPath(additionalWorkbookPath), markerLines[1]);
            Assert.Equal(
                running.ProcessId,
                new WindowsDebugExcelProcessApi().GetProcessId(
                    new nint(Convert.ToInt64(markerLines[2]))));
            Assert.True(Convert.ToInt32(markerLines[3]) >= 2);
            Assert.True(File.Exists(additionalWorkbookPath));
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
                AssertFileIsUnlocked(context.BinDocumentPath);
                AssertFileIsUnlocked(additionalWorkbookPath);
            }
        }

        await WaitForNoNewExcelProcessesAsync(baselineProcessIds, TimeSpan.FromSeconds(15));
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task TargetInitiatedExcelExitCompletesTheOwnedSession()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "DebugProject");
        var targetCompletedMarkerPath = Path.Combine(temp.Path, "target-completed-marker.txt");
        var exitRequestedMarkerPath = Path.Combine(temp.Path, "excel-exit-requested-marker.txt");
        var baselineProcessIds = CaptureExcelProcessIds();
        var commandLine = ToolingCompositionRoot.CreateCommandLineApplication(temp.Path);

        CreateProject(commandLine, projectRoot);
        var sourcePath = Path.Combine(
            projectRoot,
            "src",
            "DebugProject",
            "DebugModule.bas");
        var sourceText = CreateTargetInitiatedExcelExitModule(
            targetCompletedMarkerPath,
            exitRequestedMarkerPath);
        File.WriteAllText(sourcePath, sourceText, new UTF8Encoding(false));

        var composition = ToolingCompositionRoot.CreateDebugAdapterComposition(projectRoot);
        var context = composition.ProjectContextResolver.Resolve(
            new ProjectResolutionRequest(projectRoot, "DebugProject", projectRoot));
        DebugRunningSession? running = null;
        try
        {
            running = await LaunchZeroBreakpointTargetAsync(
                composition.LaunchCoordinator,
                context,
                sourcePath,
                sourceText);
            var processId = running.ProcessId;

            await WaitForFileAsync(targetCompletedMarkerPath, TimeSpan.FromSeconds(15));
            await WaitForFileAsync(exitRequestedMarkerPath, TimeSpan.FromSeconds(15));
            _ = await running.Completion.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.DoesNotContain(processId, CaptureExcelProcessIds());
        }
        finally
        {
            if (running is not null &&
                CaptureExcelProcessIds().Contains(running.ProcessId))
            {
                await running.TerminateAsync();
                await running.Completion.WaitAsync(TimeSpan.FromSeconds(15));
            }
        }

        await WaitForNoNewExcelProcessesAsync(baselineProcessIds, TimeSpan.FromSeconds(15));
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task DapRestartTerminatesOldExcelBeforeFreshBuildOpensANewProcess()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "DebugProject");
        var firstMarkerPath = Path.Combine(temp.Path, "restart-first-marker.txt");
        var secondMarkerPath = Path.Combine(temp.Path, "restart-second-marker.txt");
        var baselineProcessIds = CaptureExcelProcessIds();
        var commandLine = ToolingCompositionRoot.CreateCommandLineApplication(temp.Path);

        CreateProject(commandLine, projectRoot);
        var sourcePath = Path.Combine(
            projectRoot,
            "src",
            "DebugProject",
            "DebugModule.bas");
        var firstSource = CreateDapLifecycleModule(firstMarkerPath, "first launch");
        var secondSource = CreateDapLifecycleModule(secondMarkerPath, "restarted launch");
        const string restartPreparationId = "windows-excel-restart-preparation";
        File.WriteAllText(sourcePath, firstSource, new UTF8Encoding(false));

        await using (var adapter = DebugAdapterChildProcess.Start(projectRoot))
        {
            await InitializeDebugAdapterAsync(adapter);
            await adapter.SendRequestAsync(
                2,
                "launch",
                CreatePreparedDapLaunchArguments(
                    projectRoot,
                    sourcePath,
                    firstSource,
                    restartPreparationId));
            await adapter.SendRequestAsync(3, "configurationDone", new { });
            AssertSuccessfulResponse(
                await adapter.WaitForResponseAsync(3, TimeSpan.FromSeconds(15)),
                "configurationDone");
            AssertSuccessfulResponse(
                await adapter.WaitForResponseAsync(2, TimeSpan.FromSeconds(60)),
                "launch");

            await WaitForFileAsync(firstMarkerPath, TimeSpan.FromSeconds(15));
            var firstMarker = File.ReadAllLines(firstMarkerPath);
            Assert.Equal(3, firstMarker.Length);
            Assert.Equal("first launch", firstMarker[0]);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(projectRoot, "bin", "DebugProject.xlsm")),
                firstMarker[1]);
            var firstProcessId = ResolveExcelProcessId(firstMarker[2]);
            Assert.Equal(
                [firstProcessId],
                CaptureExcelProcessIds().Except(baselineProcessIds).Order().ToArray());

            File.WriteAllText(sourcePath, secondSource, new UTF8Encoding(false));
            await adapter.SendRequestAsync(
                4,
                "restart",
                new
                {
                    arguments = CreatePreparedDapLaunchArguments(
                        projectRoot,
                        sourcePath,
                        firstSource,
                        restartPreparationId)
                });
            await adapter.SendRequestAsync(
                5,
                "vba/restartPrepared",
                new
                {
                    restartRequestSequence = 4,
                    preparationId = restartPreparationId,
                    success = true
                });
            AssertSuccessfulResponse(
                await adapter.WaitForResponseAsync(5, TimeSpan.FromSeconds(15)),
                "vba/restartPrepared");
            AssertSuccessfulResponse(
                await adapter.WaitForResponseAsync(4, TimeSpan.FromSeconds(60)),
                "restart");

            await WaitForFileAsync(secondMarkerPath, TimeSpan.FromSeconds(15));
            var secondMarker = File.ReadAllLines(secondMarkerPath);
            Assert.Equal(3, secondMarker.Length);
            Assert.Equal("restarted launch", secondMarker[0]);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(projectRoot, "bin", "DebugProject.xlsm")),
                secondMarker[1]);
            var secondProcessId = ResolveExcelProcessId(secondMarker[2]);
            Assert.NotEqual(firstProcessId, secondProcessId);
            Assert.DoesNotContain(firstProcessId, CaptureExcelProcessIds());
            Assert.Equal(
                [secondProcessId],
                CaptureExcelProcessIds().Except(baselineProcessIds).Order().ToArray());

            await adapter.SendRequestAsync(
                6,
                "disconnect",
                new { terminateDebuggee = true });
            AssertSuccessfulResponse(
                await adapter.WaitForResponseAsync(6, TimeSpan.FromSeconds(15)),
                "disconnect");
            await adapter.WaitForExitAsync(TimeSpan.FromSeconds(30));
        }

        await WaitForNoNewExcelProcessesAsync(baselineProcessIds, TimeSpan.FromSeconds(15));
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task UnexpectedDebugAdapterTerminationClosesTheJobAndTerminatesOwnedExcel()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "DebugProject");
        var markerPath = Path.Combine(temp.Path, "adapter-termination-marker.txt");
        var baselineProcessIds = CaptureExcelProcessIds();
        var commandLine = ToolingCompositionRoot.CreateCommandLineApplication(temp.Path);

        CreateProject(commandLine, projectRoot);
        var sourcePath = Path.Combine(
            projectRoot,
            "src",
            "DebugProject",
            "DebugModule.bas");
        var sourceText = CreateDapLifecycleModule(markerPath, "adapter alive");
        File.WriteAllText(sourcePath, sourceText, new UTF8Encoding(false));

        OwnedExcelProcessIdentity? ownedExcelProcess = null;
        try
        {
            await using var adapter = DebugAdapterChildProcess.Start(projectRoot);
            await InitializeDebugAdapterAsync(adapter);
            await adapter.SendRequestAsync(
                2,
                "launch",
                CreateDapLaunchArguments(projectRoot, sourcePath, sourceText));
            await adapter.SendRequestAsync(3, "configurationDone", new { });
            AssertSuccessfulResponse(
                await adapter.WaitForResponseAsync(3, TimeSpan.FromSeconds(15)),
                "configurationDone");
            AssertSuccessfulResponse(
                await adapter.WaitForResponseAsync(2, TimeSpan.FromSeconds(60)),
                "launch");

            await WaitForFileAsync(markerPath, TimeSpan.FromSeconds(15));
            var marker = File.ReadAllLines(markerPath);
            Assert.Equal(3, marker.Length);
            Assert.Equal("adapter alive", marker[0]);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(projectRoot, "bin", "DebugProject.xlsm")),
                marker[1]);
            ownedExcelProcess = CaptureExcelProcessIdentity(
                ResolveExcelProcessId(marker[2]));
            Assert.Equal(
                [ownedExcelProcess.ProcessId],
                CaptureExcelProcessIds().Except(baselineProcessIds).Order().ToArray());
            Assert.False(adapter.HasExited);

            adapter.KillUnexpectedly();
            await adapter.WaitForExitAsync(TimeSpan.FromSeconds(15));
            await WaitForExcelProcessExitAsync(
                ownedExcelProcess,
                TimeSpan.FromSeconds(15));

            Assert.False(IsSameExcelProcessRunning(ownedExcelProcess));
        }
        finally
        {
            if (ownedExcelProcess is not null)
            {
                TryTerminateExcelProcess(ownedExcelProcess);
            }
        }

        await WaitForNoNewExcelProcessesAsync(baselineProcessIds, TimeSpan.FromSeconds(15));
    }

    private static async Task InitializeDebugAdapterAsync(
        DebugAdapterChildProcess adapter)
    {
        await adapter.SendRequestAsync(
            1,
            "initialize",
            new { adapterID = "vba" });
        var response = await adapter.WaitForResponseAsync(1, TimeSpan.FromSeconds(15));
        AssertSuccessfulResponse(response, "initialize");
        Assert.True(
            response
                .GetProperty("body")
                .GetProperty("supportsRestartRequest")
                .GetBoolean());
    }

    private static object CreateDapLaunchArguments(
        string projectRoot,
        string sourcePath,
        string sourceText,
        string procedureName = "RunTarget")
        => new
        {
            project = projectRoot,
            document = "DebugProject",
            module = "DebugModule",
            procedure = procedureName,
            sourceSnapshot = new
            {
                schemaVersion = DebugSourceSnapshot.CurrentSchemaVersion,
                sources = new[] { new { path = sourcePath, text = sourceText } },
                breakpoints = Array.Empty<object>()
            }
        };

    private static object CreatePreparedDapLaunchArguments(
        string projectRoot,
        string sourcePath,
        string sourceText,
        string preparationId)
        => new
        {
            project = projectRoot,
            document = "DebugProject",
            module = "DebugModule",
            procedure = "RunTarget",
            __vbaRestartPreparation = new
            {
                protocolVersion = 1,
                id = preparationId
            },
            sourceSnapshot = new
            {
                schemaVersion = DebugSourceSnapshot.CurrentSchemaVersion,
                sources = new[] { new { path = sourcePath, text = sourceText } },
                breakpoints = Array.Empty<object>()
            }
        };

    private static void AssertSuccessfulResponse(
        JsonElement response,
        string command)
    {
        Assert.Equal("response", response.GetProperty("type").GetString());
        Assert.Equal(command, response.GetProperty("command").GetString());
        Assert.True(
            response.GetProperty("success").GetBoolean(),
            response.TryGetProperty("message", out var message)
                ? message.GetString()
                : response.GetRawText());
    }

    private static int ResolveExcelProcessId(string windowHandle)
        => new WindowsDebugExcelProcessApi().GetProcessId(
            new nint(Convert.ToInt64(windowHandle)));

    private static OwnedExcelProcessIdentity CaptureExcelProcessIdentity(int processId)
    {
        using var process = Process.GetProcessById(processId);
        Assert.Equal("EXCEL", process.ProcessName, ignoreCase: true);
        return new OwnedExcelProcessIdentity(process.Id, process.StartTime);
    }

    private static async Task WaitForExcelProcessExitAsync(
        OwnedExcelProcessIdentity identity,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            while (IsSameExcelProcessRunning(identity))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Owned Excel process {identity.ProcessId} did not exit within {timeout} after its debug adapter terminated.");
        }
    }

    private static bool IsSameExcelProcessRunning(OwnedExcelProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            return process.ProcessName.Equals("EXCEL", StringComparison.OrdinalIgnoreCase) &&
                process.StartTime == identity.StartTime;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static void TryTerminateExcelProcess(OwnedExcelProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            if (process.ProcessName.Equals("EXCEL", StringComparison.OrdinalIgnoreCase) &&
                process.StartTime == identity.StartTime)
            {
                process.Kill();
                process.WaitForExit(15_000);
            }
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static async Task<DebugRunningSession> LaunchZeroBreakpointTargetAsync(
        DebugLaunchCoordinator launchCoordinator,
        ResolvedProjectContext context,
        string sourcePath,
        string sourceText)
    {
        try
        {
            return await launchCoordinator.LaunchAsync(
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
    }

    private static void AssertFileIsUnlocked(string path)
    {
        Assert.True(File.Exists(path), $"Expected workbook does not exist: {path}");
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        Assert.True(stream.CanRead);
        Assert.True(stream.CanWrite);
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

    private static string CreateAdditionalWorkbookOwnershipModule(
        string markerPath,
        string additionalWorkbookPath)
    {
        var vbaMarkerPath = markerPath.Replace("\"", "\"\"", StringComparison.Ordinal);
        var vbaAdditionalWorkbookPath = additionalWorkbookPath.Replace(
            "\"",
            "\"\"",
            StringComparison.Ordinal);
        return string.Join(
            "\r\n",
            "Attribute VB_Name = \"DebugModule\"",
            "Option Explicit",
            string.Empty,
            "Public Sub RunTarget()",
            "    Dim additionalWorkbook As Workbook",
            "    Dim previousDisplayAlerts As Boolean",
            "    Dim fileNumber As Integer",
            "    Set additionalWorkbook = Application.Workbooks.Add",
            "    previousDisplayAlerts = Application.DisplayAlerts",
            "    Application.DisplayAlerts = False",
            $"    additionalWorkbook.SaveAs \"{vbaAdditionalWorkbookPath}\", 51",
            "    Application.DisplayAlerts = previousDisplayAlerts",
            "    additionalWorkbook.Worksheets(1).Range(\"A1\").Value = \"unsaved session-owned change\"",
            "    fileNumber = FreeFile",
            $"    Open \"{vbaMarkerPath}\" For Output As #fileNumber",
            "    Print #fileNumber, ThisWorkbook.FullName",
            "    Print #fileNumber, additionalWorkbook.FullName",
            "    Print #fileNumber, CStr(Application.Hwnd)",
            "    Print #fileNumber, CStr(Application.Workbooks.Count)",
            "    Close #fileNumber",
            "End Sub",
            string.Empty);
    }

    private static string CreateTargetInitiatedExcelExitModule(
        string targetCompletedMarkerPath,
        string exitRequestedMarkerPath)
    {
        var vbaTargetCompletedMarkerPath = targetCompletedMarkerPath.Replace(
            "\"",
            "\"\"",
            StringComparison.Ordinal);
        var vbaExitRequestedMarkerPath = exitRequestedMarkerPath.Replace(
            "\"",
            "\"\"",
            StringComparison.Ordinal);
        return string.Join(
            "\r\n",
            "Attribute VB_Name = \"DebugModule\"",
            "Option Explicit",
            string.Empty,
            "#If VBA7 Then",
            "Private Declare PtrSafe Function SetTimer Lib \"user32\" (ByVal hWnd As LongPtr, ByVal eventId As LongPtr, ByVal intervalMilliseconds As Long, ByVal timerCallback As LongPtr) As LongPtr",
            "Private Declare PtrSafe Sub ExitProcess Lib \"kernel32\" (ByVal exitCode As Long)",
            "Private exitTimerId As LongPtr",
            "#Else",
            "Private Declare Function SetTimer Lib \"user32\" (ByVal hWnd As Long, ByVal eventId As Long, ByVal intervalMilliseconds As Long, ByVal timerCallback As Long) As Long",
            "Private Declare Sub ExitProcess Lib \"kernel32\" (ByVal exitCode As Long)",
            "Private exitTimerId As Long",
            "#End If",
            string.Empty,
            "Public Sub RunTarget()",
            "    Dim fileNumber As Integer",
            "    fileNumber = FreeFile",
            $"    Open \"{vbaTargetCompletedMarkerPath}\" For Output As #fileNumber",
            "    Print #fileNumber, \"target completed\"",
            "    Close #fileNumber",
            "    exitTimerId = SetTimer(0, 0, 1000, AddressOf ExitOwnedExcel)",
            "    If exitTimerId = 0 Then Err.Raise vbObjectError + 513, \"DebugModule.RunTarget\", \"Failed to schedule owned Excel exit.\"",
            "End Sub",
            string.Empty,
            "#If VBA7 Then",
            "Public Sub ExitOwnedExcel(ByVal hWnd As LongPtr, ByVal message As Long, ByVal eventId As LongPtr, ByVal tickCount As Long)",
            "#Else",
            "Public Sub ExitOwnedExcel(ByVal hWnd As Long, ByVal message As Long, ByVal eventId As Long, ByVal tickCount As Long)",
            "#End If",
            "    Dim fileNumber As Integer",
            "    fileNumber = FreeFile",
            $"    Open \"{vbaExitRequestedMarkerPath}\" For Output As #fileNumber",
            "    Print #fileNumber, \"exit requested\"",
            "    Close #fileNumber",
            "    Application.DisplayAlerts = False",
            "    Application.Quit",
            "    ExitProcess 0",
            "End Sub",
            string.Empty);
    }

    private static string CreateDapLifecycleModule(string markerPath, string markerText)
    {
        var vbaMarkerPath = markerPath.Replace("\"", "\"\"", StringComparison.Ordinal);
        var vbaMarkerText = markerText.Replace("\"", "\"\"", StringComparison.Ordinal);
        return string.Join(
            "\r\n",
            "Attribute VB_Name = \"DebugModule\"",
            "Option Explicit",
            string.Empty,
            "Public Sub RunTarget()",
            "    Dim fileNumber As Integer",
            "    fileNumber = FreeFile",
            $"    Open \"{vbaMarkerPath}\" For Output As #fileNumber",
            $"    Print #fileNumber, \"{vbaMarkerText}\"",
            "    Print #fileNumber, ThisWorkbook.FullName",
            "    Print #fileNumber, CStr(Application.Hwnd)",
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

    private sealed class DebugAdapterChildProcess : IAsyncDisposable
    {
        private readonly Process process;
        private readonly Task<string> standardError;
        private readonly List<string> transcript = [];

        private DebugAdapterChildProcess(Process process)
        {
            this.process = process;
            standardError = process.StandardError.ReadToEndAsync();
        }

        public bool HasExited => process.HasExited;

        public static DebugAdapterChildProcess Start(string workingDirectory)
        {
            var executablePath = ResolveDebugAdapterExecutablePath();

            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("debug-adapter");
            startInfo.ArgumentList.Add("--stdio");
            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "The current vba-dev debug adapter process did not start.");
            return new DebugAdapterChildProcess(process);
        }

        private static string ResolveDebugAdapterExecutablePath()
        {
            var outputDirectory = new DirectoryInfo(AppContext.BaseDirectory);
            var configuration = outputDirectory.Parent?.Name
                ?? throw new InvalidOperationException(
                    "The current test build configuration could not be resolved.");
            var targetFramework = outputDirectory.Name;
            for (var directory = outputDirectory; directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(
                    directory.FullName,
                    "tools",
                    "vba-dev",
                    "src",
                    "VbaDev.Cli",
                    "bin",
                    configuration,
                    targetFramework,
                    "win-x64",
                    "vba-dev.exe");
                if (File.Exists(candidate) &&
                    File.Exists(Path.Combine(Path.GetDirectoryName(candidate)!, "hostpolicy.dll")))
                {
                    return candidate;
                }
            }

            throw new FileNotFoundException(
                "The current vba-dev executable with its native runtime could not be found.");
        }

        public async Task SendRequestAsync(
            int sequence,
            string command,
            object arguments)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(new
            {
                seq = sequence,
                type = "request",
                command,
                arguments
            });
            var header = Encoding.ASCII.GetBytes(
                $"Content-Length: {body.Length}\r\n\r\n");
            await process.StandardInput.BaseStream.WriteAsync(header);
            await process.StandardInput.BaseStream.WriteAsync(body);
            await process.StandardInput.BaseStream.FlushAsync();
        }

        public async Task<JsonElement> WaitForResponseAsync(
            int requestSequence,
            TimeSpan timeout)
        {
            using var cancellation = new CancellationTokenSource(timeout);
            try
            {
                while (true)
                {
                    var message = await ReadMessageAsync(cancellation.Token);
                    transcript.Add(message.GetRawText());
                    if (message.GetProperty("type").GetString() == "response" &&
                        message.GetProperty("request_seq").GetInt32() == requestSequence)
                    {
                        return message;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"The debug adapter did not return response {requestSequence} within {timeout}." +
                    $"{Environment.NewLine}{string.Join(Environment.NewLine, transcript)}");
            }
            catch (EndOfStreamException ex)
            {
                var error = process.HasExited
                    ? await standardError
                    : "The debug adapter process is still running.";
                throw new InvalidOperationException(
                    $"The debug adapter output ended before response {requestSequence}." +
                    $"{Environment.NewLine}{error}" +
                    $"{Environment.NewLine}{string.Join(Environment.NewLine, transcript)}",
                    ex);
            }
        }

        public void KillUnexpectedly()
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: false);
            }
        }

        public async Task WaitForExitAsync(TimeSpan timeout)
        {
            if (!process.HasExited)
            {
                await process.WaitForExitAsync().WaitAsync(timeout);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.StandardInput.Close();
                    try
                    {
                        await WaitForExitAsync(TimeSpan.FromSeconds(15));
                    }
                    catch
                    {
                        KillUnexpectedly();
                        await WaitForExitAsync(TimeSpan.FromSeconds(15));
                    }
                }

                _ = await standardError;
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        private async Task<JsonElement> ReadMessageAsync(CancellationToken cancellationToken)
        {
            var headerBytes = new List<byte>();
            var singleByte = new byte[1];
            while (true)
            {
                var count = await process.StandardOutput.BaseStream.ReadAsync(
                    singleByte,
                    cancellationToken);
                if (count == 0)
                {
                    throw new EndOfStreamException();
                }

                headerBytes.Add(singleByte[0]);
                if (headerBytes.Count >= 4 &&
                    headerBytes[^4] == '\r' &&
                    headerBytes[^3] == '\n' &&
                    headerBytes[^2] == '\r' &&
                    headerBytes[^1] == '\n')
                {
                    break;
                }
            }

            var header = Encoding.ASCII.GetString(headerBytes[..^4].ToArray());
            var contentLengthHeader = header
                .Split(["\r\n"], StringSplitOptions.RemoveEmptyEntries)
                .Single(line => line.StartsWith(
                    "Content-Length: ",
                    StringComparison.OrdinalIgnoreCase));
            var contentLength = int.Parse(
                contentLengthHeader["Content-Length: ".Length..],
                System.Globalization.CultureInfo.InvariantCulture);
            var body = new byte[contentLength];
            await process.StandardOutput.BaseStream.ReadExactlyAsync(
                body,
                cancellationToken);
            using var document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
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

    private sealed record OwnedExcelProcessIdentity(int ProcessId, DateTime StartTime);
}

public sealed class WindowsExcelIntegrationFactAttribute : FactAttribute
{
    private const string OptInEnvironmentVariable =
        "VBA_TOOLS_RUN_EXCEL_INTEGRATION_TESTS";

    public WindowsExcelIntegrationFactAttribute()
    {
        Timeout = 360_000;
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
