using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using VbaDev.Infrastructure.Debugging;
using Xunit;

namespace VbaDev.Tests;

[Collection(WindowsExcelIntegrationCollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class PackagedVbaDebugWindowsExcelIntegrationTests
{
    private const string BundledExecutablePath = "bin/vba-dev/win-x64/vba-dev.exe";
    private const int VbeBreakMode = 1;
    private const int VbeDesignMode = 2;
    private const int RunOrContinueCommandId = 186;
    private const uint ObjectIdNativeObjectModel = 0xfffffff0;
    private static readonly Guid IDispatchId = new("00020400-0000-0000-C000-000000000046");

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task PackagedVsCodeAssetsCompleteTheNativeBreakpointWorkflowAndReportTerminalLifecycle()
    {
        var repositoryRoot = ResolveRepositoryRoot();
        var executablePath = ResolvePackagedDebugAssets(repositoryRoot);
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "DebugProject");
        var launchMarkerPath = Path.Combine(temp.Path, "packaged-debug-started.txt");
        var completionMarkerPath = Path.Combine(temp.Path, "packaged-debug-completed.txt");
        var baselineExcelProcessIds = CaptureExcelProcessIds();
        int? ownedExcelProcessId = null;
        OwnedExcelProcessIdentity? ownedExcelProcess = null;

        try
        {
            var createResult = await RunProcessAsync(
                executablePath,
                temp.Path,
                ["new", "excel", "--name", "DebugProject", "--output", projectRoot],
                TimeSpan.FromSeconds(60));
            Assert.True(
                createResult.ExitCode == 0,
                $"The bundled vba-dev project creation failed.{Environment.NewLine}" +
                $"stdout:{Environment.NewLine}{createResult.StandardOutput}{Environment.NewLine}" +
                $"stderr:{Environment.NewLine}{createResult.StandardError}");

            var sourcePath = Path.GetFullPath(Path.Combine(
                projectRoot,
                "src",
                "DebugProject",
                "DebugModule.bas"));
            var sourceText = CreateDebugSource(launchMarkerPath, completionMarkerPath);
            File.WriteAllText(sourcePath, sourceText, new UTF8Encoding(false));
            var breakpointLine = FindLine(
                sourceText,
                "    ThisWorkbook.Worksheets(1).Range(\"A1\").Value2 = \"continued\"");

            Assert.Contains("Option Private Module", sourceText, StringComparison.Ordinal);
            Assert.Contains("Public Sub RunTarget()", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("Application.Run", sourceText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SendKeys", sourceText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Stop", sourceText, StringComparison.OrdinalIgnoreCase);

            await using var adapter = PackagedDebugAdapterProcess.Start(
                executablePath,
                projectRoot);
            await adapter.SendRequestAsync(
                1,
                "initialize",
                new { adapterID = "vba" });
            AssertSuccessfulResponse(
                await adapter.WaitForResponseAsync(1, TimeSpan.FromSeconds(15)),
                "initialize");

            await adapter.SendRequestAsync(
                2,
                "setBreakpoints",
                new
                {
                    source = new { path = sourcePath },
                    breakpoints = new[] { new { line = breakpointLine + 1 } }
                });
            var pendingBreakpointResponse = await adapter.WaitForResponseAsync(
                2,
                TimeSpan.FromSeconds(15));
            AssertSuccessfulResponse(pendingBreakpointResponse, "setBreakpoints");
            var pendingBreakpoint = Assert.Single(
                pendingBreakpointResponse
                    .GetProperty("body")
                    .GetProperty("breakpoints")
                    .EnumerateArray());
            Assert.False(pendingBreakpoint.GetProperty("verified").GetBoolean());

            using (var foregroundAssistCancellation = new CancellationTokenSource())
            {
                var foregroundAssist = AssistPackagedDebugForegroundAsync(
                    baselineExcelProcessIds,
                    foregroundAssistCancellation.Token);
                JsonElement launchResponse;
                try
                {
                    await adapter.SendRequestAsync(
                        3,
                        "launch",
                        new
                        {
                            project = projectRoot,
                            document = "DebugProject",
                            module = "DebugModule",
                            procedure = "RunTarget",
                            sourceSnapshot = new
                            {
                                schemaVersion = 1,
                                sources = new[] { new { path = sourcePath, text = sourceText } },
                                breakpoints = new[] { new { path = sourcePath, line = breakpointLine } }
                            }
                        });
                    await adapter.SendRequestAsync(4, "configurationDone", new { });
                    AssertSuccessfulResponse(
                        await adapter.WaitForResponseAsync(4, TimeSpan.FromSeconds(15)),
                        "configurationDone");
                    launchResponse = await adapter.WaitForResponseAsync(
                        3,
                        TimeSpan.FromSeconds(90));
                }
                finally
                {
                    foregroundAssistCancellation.Cancel();
                    try
                    {
                        await foregroundAssist;
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                AssertSuccessfulResponse(launchResponse, "launch");
            }

            var verifiedBreakpointEvent = await adapter.WaitForEventAsync(
                "breakpoint",
                TimeSpan.FromSeconds(15));
            var verifiedBreakpoint = verifiedBreakpointEvent
                .GetProperty("body")
                .GetProperty("breakpoint");
            Assert.True(verifiedBreakpoint.GetProperty("verified").GetBoolean());
            Assert.Equal(
                pendingBreakpoint.GetProperty("id").GetInt32(),
                verifiedBreakpoint.GetProperty("id").GetInt32());
            Assert.Equal(breakpointLine + 1, verifiedBreakpoint.GetProperty("line").GetInt32());

            var excelWindowHandle = await WaitForWindowHandleAsync(
                launchMarkerPath,
                TimeSpan.FromSeconds(15));
            ownedExcelProcessId = GetWindowProcessId(excelWindowHandle);
            ownedExcelProcess = CaptureOwnedExcelProcess(ownedExcelProcessId.Value);
            Assert.DoesNotContain(ownedExcelProcessId.Value, baselineExcelProcessIds);
            Assert.Equal(
                [ownedExcelProcessId.Value],
                CaptureExcelProcessIds().Except(baselineExcelProcessIds).Order().ToArray());

            await WaitForVbeModeAsync(
                excelWindowHandle,
                VbeBreakMode,
                TimeSpan.FromSeconds(15));
            await ExecuteNativeContinueAsync(excelWindowHandle);
            await WaitForFileTextAsync(
                completionMarkerPath,
                "completed",
                TimeSpan.FromSeconds(15));
            await WaitForVbeModeAsync(
                excelWindowHandle,
                VbeDesignMode,
                TimeSpan.FromSeconds(15));

            TerminateOwnedExcelProcess(ownedExcelProcess!);
            var terminalEvent = await adapter.WaitForEventAsync(
                "terminated",
                TimeSpan.FromSeconds(30));
            Assert.Equal("terminated", terminalEvent.GetProperty("event").GetString());

            var terminalLifecycle = adapter.Messages
                .Where(message =>
                    message.GetProperty("type").GetString() == "event" &&
                    message.TryGetProperty("event", out var eventName) &&
                    (eventName.GetString() == "output" ||
                     eventName.GetString() == "exited" ||
                     eventName.GetString() == "terminated"))
                .TakeLast(3)
                .ToArray();
            Assert.Equal(
                ["output", "exited", "terminated"],
                terminalLifecycle
                    .Select(message => message.GetProperty("event").GetString()!)
                    .ToArray());
            var exitCode = terminalLifecycle[1]
                .GetProperty("body")
                .GetProperty("exitCode")
                .GetInt32();
            Assert.Contains(
                $"Owned Excel process {ownedExcelProcessId.Value} exited with code {exitCode}.",
                terminalLifecycle[0]
                    .GetProperty("body")
                    .GetProperty("output")
                    .GetString(),
                StringComparison.Ordinal);

            await adapter.CompleteInputAndWaitForExitAsync(TimeSpan.FromSeconds(15));
            await WaitForProcessExitAsync(ownedExcelProcessId.Value, TimeSpan.FromSeconds(15));
        }
        finally
        {
            if (ownedExcelProcess is not null)
            {
                TryTerminateOwnedExcelProcess(ownedExcelProcess);
            }

            await WaitForNoNewExcelProcessesAsync(
                baselineExcelProcessIds,
                TimeSpan.FromSeconds(15));
        }
    }

    private static string ResolveRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "distribution-manifest.json")) &&
                File.Exists(Path.Combine(directory.FullName, "package.json")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "The vba-tools repository root could not be resolved from the test output directory.");
    }

    private static string ResolvePackagedDebugAssets(string repositoryRoot)
    {
        using var package = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(repositoryRoot, "package.json")));
        Assert.Equal(
            "./client/out/extension.js",
            package.RootElement.GetProperty("main").GetString());
        Assert.Contains(
            package.RootElement.GetProperty("activationEvents").EnumerateArray(),
            activation => activation.GetString() == "onDebugDynamicConfigurations");
        Assert.Contains(
            package.RootElement.GetProperty("activationEvents").EnumerateArray(),
            activation => activation.GetString() == "onDebugResolve:vba");
        Assert.Contains(
            package.RootElement
                .GetProperty("contributes")
                .GetProperty("debuggers")
                .EnumerateArray(),
            debugger =>
                debugger.GetProperty("type").GetString() == "vba" &&
                debugger.GetProperty("configurationAttributes").TryGetProperty("launch", out _));

        var extensionEntryPath = Path.Combine(
            repositoryRoot,
            "client",
            "out",
            "extension.js");
        Assert.True(
            File.Exists(extensionEntryPath),
            $"Compile the packaged extension entry before running this smoke test: {extensionEntryPath}");

        using var manifest = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(repositoryRoot, "distribution-manifest.json")));
        var relativePath = manifest.RootElement
            .GetProperty("runtimes")
            .GetProperty("vbaDev")
            .GetProperty("executablePath")
            .GetString();
        Assert.Equal(BundledExecutablePath, relativePath);

        var executablePath = Path.GetFullPath(Path.Combine(
            repositoryRoot,
            relativePath!.Replace('/', Path.DirectorySeparatorChar)));
        Assert.True(
            executablePath.StartsWith(
                Path.GetFullPath(repositoryRoot) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase),
            "The bundled vba-dev manifest path must remain inside the repository root.");
        Assert.True(
            File.Exists(executablePath),
            $"Publish the required bundled executable before running this smoke test: {executablePath}");
        return executablePath;
    }

    private static string CreateDebugSource(
        string launchMarkerPath,
        string completionMarkerPath)
    {
        var launchPath = launchMarkerPath.Replace("\"", "\"\"", StringComparison.Ordinal);
        var completionPath = completionMarkerPath.Replace("\"", "\"\"", StringComparison.Ordinal);
        return string.Join(
            "\r\n",
            "Attribute VB_Name = \"DebugModule\"",
            "Option Explicit",
            "Option Private Module",
            string.Empty,
            "Public Sub RunTarget()",
            "    Dim fileNumber As Integer",
            "    fileNumber = FreeFile",
            $"    Open \"{launchPath}\" For Output As #fileNumber",
            "    Print #fileNumber, CStr(Application.Hwnd)",
            "    Close #fileNumber",
            "    ThisWorkbook.Worksheets(1).Range(\"A1\").Value2 = \"continued\"",
            "    fileNumber = FreeFile",
            $"    Open \"{completionPath}\" For Output As #fileNumber",
            "    Print #fileNumber, \"completed\"",
            "    Close #fileNumber",
            "End Sub",
            string.Empty);
    }

    private static int FindLine(string source, string expectedLine)
    {
        var line = Array.IndexOf(
            source.Split(["\r\n"], StringSplitOptions.None),
            expectedLine);
        Assert.True(line >= 0, $"Expected source line was not found: {expectedLine}");
        return line;
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string executablePath,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Process did not start: {executablePath}");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(timeout);
        return new ProcessResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }

    private static void AssertSuccessfulResponse(JsonElement response, string command)
    {
        Assert.Equal("response", response.GetProperty("type").GetString());
        Assert.Equal(command, response.GetProperty("command").GetString());
        Assert.True(
            response.GetProperty("success").GetBoolean(),
            response.TryGetProperty("message", out var message)
                ? message.GetString()
                : response.GetRawText());
    }

    private static async Task<nint> WaitForWindowHandleAsync(string path, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                if (File.Exists(path) &&
                    long.TryParse(File.ReadAllText(path).Trim(), out var handle) &&
                    handle != 0)
                {
                    return new nint(handle);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The packaged debug target did not report its Excel window handle within {timeout}.");
        }
    }

    private static async Task WaitForFileTextAsync(
        string path,
        string expectedText,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            while (!File.Exists(path) ||
                   !File.ReadAllText(path).Trim().Equals(expectedText, StringComparison.Ordinal))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The packaged debug target did not complete within {timeout}.");
        }
    }

    private static async Task WaitForVbeModeAsync(
        nint excelWindowHandle,
        int expectedMode,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                try
                {
                    var mode = await RunInStaAsync(() => UseExcelApplication(
                        excelWindowHandle,
                        ReadVbeProjectMode));
                    if (mode == expectedMode)
                    {
                        return;
                    }
                }
                catch (COMException)
                {
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The packaged debug target VBE did not enter mode {expectedMode} within {timeout}.");
        }
    }

    private static int ReadVbeProjectMode(object application)
    {
        object? vbeObject = null;
        object? projectObject = null;
        try
        {
            dynamic excel = application;
            vbeObject = excel.VBE;
            dynamic vbe = vbeObject;
            projectObject = vbe.ActiveVBProject;
            dynamic project = projectObject;
            return (int)project.Mode;
        }
        finally
        {
            ReleaseComObject(projectObject);
            ReleaseComObject(vbeObject);
        }
    }

    private static Task ExecuteNativeContinueAsync(nint excelWindowHandle)
        => RunInStaAsync(() => UseExcelApplication(
            excelWindowHandle,
            application =>
            {
                object? vbeObject = null;
                object? projectObject = null;
                object? commandBarsObject = null;
                object? commandObject = null;
                try
                {
                    dynamic excel = application;
                    vbeObject = excel.VBE;
                    dynamic vbe = vbeObject;
                    projectObject = vbe.ActiveVBProject;
                    dynamic project = projectObject;
                    Assert.Equal(VbeBreakMode, (int)project.Mode);

                    commandBarsObject = vbe.CommandBars;
                    dynamic commandBars = commandBarsObject;
                    commandObject = commandBars.FindControl(
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
                }
                finally
                {
                    ReleaseComObject(commandObject);
                    ReleaseComObject(commandBarsObject);
                    ReleaseComObject(projectObject);
                    ReleaseComObject(vbeObject);
                }
            }));

    private static T UseExcelApplication<T>(
        nint excelWindowHandle,
        Func<object, T> operation)
    {
        var nativeObjectWindow = FindDescendantWindow(excelWindowHandle, "EXCEL7");
        if (nativeObjectWindow == nint.Zero)
        {
            throw new InvalidOperationException(
                $"The Excel native object-model window was not found below {excelWindowHandle}.");
        }

        object? nativeObject = null;
        object? application = null;
        try
        {
            var dispatchId = IDispatchId;
            Marshal.ThrowExceptionForHR(AccessibleObjectFromWindow(
                nativeObjectWindow,
                ObjectIdNativeObjectModel,
                ref dispatchId,
                out nativeObject));
            dynamic excelWindow = nativeObject;
            application = excelWindow.Application;
            return operation(application);
        }
        finally
        {
            ReleaseComObject(application);
            ReleaseComObject(nativeObject);
        }
    }

    private static nint FindDescendantWindow(nint parentWindow, string className)
    {
        nint result = nint.Zero;
        _ = EnumChildWindows(
            parentWindow,
            (window, _) =>
            {
                var buffer = new StringBuilder(256);
                _ = GetClassName(window, buffer, buffer.Capacity);
                if (!buffer.ToString().Equals(className, StringComparison.Ordinal))
                {
                    return true;
                }

                result = window;
                return false;
            },
            nint.Zero);
        return result;
    }

    private static Task<T> RunInStaAsync<T>(Func<T> operation)
    {
        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(operation());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Packaged VBA debug smoke COM observer"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.FinalReleaseComObject(value);
        }
    }

    private static int GetWindowProcessId(nint windowHandle)
    {
        _ = GetWindowThreadProcessId(windowHandle, out var processId);
        Assert.NotEqual(0u, processId);
        return checked((int)processId);
    }

    private static OwnedExcelProcessIdentity CaptureOwnedExcelProcess(int processId)
    {
        using var process = Process.GetProcessById(processId);
        Assert.Equal("EXCEL", process.ProcessName, ignoreCase: true);
        return new OwnedExcelProcessIdentity(process.Id, process.StartTime);
    }

    private static void TerminateOwnedExcelProcess(OwnedExcelProcessIdentity identity)
    {
        using var process = Process.GetProcessById(identity.ProcessId);
        Assert.Equal("EXCEL", process.ProcessName, ignoreCase: true);
        Assert.Equal(identity.StartTime, process.StartTime);
        process.Kill(entireProcessTree: false);
    }

    private static async Task AssistPackagedDebugForegroundAsync(
        IReadOnlySet<int> baselineExcelProcessIds,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var processId in CaptureExcelProcessIds()
                         .Except(baselineExcelProcessIds)
                         .Order())
            {
                try
                {
                    using var process = Process.GetProcessById(processId);
                    process.Refresh();
                    var excelWindowHandle = process.MainWindowHandle;
                    if (excelWindowHandle == nint.Zero ||
                        !IsWindowVisible(excelWindowHandle))
                    {
                        continue;
                    }

                    await RunInStaAsync(() => UseExcelApplication(
                        excelWindowHandle,
                        application => EstablishVbeForeground(application, processId)));
                    break;
                }
                catch (Exception ex) when (
                    ex is ArgumentException or
                        COMException or
                        UnauthorizedAccessException or
                        InvalidOperationException or
                        VbaDev.App.Debugging.DebugSetupException)
                {
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }
    }

    private static bool EstablishVbeForeground(object application, int processId)
    {
        object? vbeObject = null;
        object? mainWindowObject = null;
        object? codePaneObject = null;
        object? codeWindowObject = null;
        try
        {
            var windowActivator = new WindowsDebugWindowActivator();
            var foregroundPermission = windowActivator.AllowComServerForeground(application);

            dynamic excel = application;
            vbeObject = excel.VBE;
            dynamic vbe = vbeObject;
            mainWindowObject = vbe.MainWindow;
            dynamic mainWindow = mainWindowObject;
            mainWindow.Visible = true;
            mainWindow.SetFocus();
            codePaneObject = vbe.ActiveCodePane;
            if (codePaneObject is not null)
            {
                dynamic codePane = codePaneObject;
                codePane.Show();
                codeWindowObject = codePane.Window;
                dynamic codeWindow = codeWindowObject;
                codeWindow.SetFocus();
            }

            var vbeWindowHandle = new nint(Convert.ToInt64(mainWindow.HWnd));
            _ = SetForegroundFromAttachedInputQueues(vbeWindowHandle);
            try
            {
                windowActivator.BringOwnedWindowToForeground(
                    vbeWindowHandle,
                    processId);
            }
            catch (VbaDev.App.Debugging.DebugSetupException ex)
            {
                ex.Data["CoAllowSetForegroundWindow.HResult"] = foregroundPermission;
                throw;
            }
            return true;
        }
        finally
        {
            ReleaseComObject(codeWindowObject);
            ReleaseComObject(codePaneObject);
            ReleaseComObject(mainWindowObject);
            ReleaseComObject(vbeObject);
        }
    }

    private static bool SetForegroundFromAttachedInputQueues(nint targetWindow)
    {
        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == targetWindow)
        {
            return true;
        }

        var currentThreadId = GetCurrentThreadId();
        var foregroundThreadId = foregroundWindow == nint.Zero
            ? 0
            : GetWindowThreadProcessId(foregroundWindow, out _);
        var targetThreadId = GetWindowThreadProcessId(targetWindow, out _);
        var attachedForeground = foregroundThreadId != 0 &&
            foregroundThreadId != currentThreadId &&
            AttachThreadInput(currentThreadId, foregroundThreadId, true);
        var attachedTarget = targetThreadId != 0 &&
            targetThreadId != currentThreadId &&
            targetThreadId != foregroundThreadId &&
            AttachThreadInput(currentThreadId, targetThreadId, true);
        try
        {
            _ = ShowWindow(targetWindow, 9);
            _ = BringWindowToTop(targetWindow);
            var result = SetForegroundWindow(targetWindow);
            _ = SetFocus(targetWindow);
            return result && GetForegroundWindow() == targetWindow;
        }
        finally
        {
            if (attachedTarget)
            {
                _ = AttachThreadInput(currentThreadId, targetThreadId, false);
            }

            if (attachedForeground)
            {
                _ = AttachThreadInput(currentThreadId, foregroundThreadId, false);
            }
        }
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

    private static async Task WaitForProcessExitAsync(int processId, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            while (IsProcessRunning(processId, "EXCEL"))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Owned Excel process {processId} did not exit within {timeout}.");
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
                $"Packaged debug smoke Excel processes did not exit within {timeout}: " +
                string.Join(", ", remaining));
        }
    }

    private static bool IsProcessRunning(int processId, string processName)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void TryTerminateOwnedExcelProcess(
        OwnedExcelProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            if (process.ProcessName.Equals("EXCEL", StringComparison.OrdinalIgnoreCase) &&
                process.StartTime == identity.StartTime)
            {
                process.Kill(entireProcessTree: false);
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

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(
        nint windowHandle,
        uint objectId,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out object accessibleObject);

    private delegate bool EnumWindowsCallback(nint windowHandle, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(
        nint parentWindow,
        EnumWindowsCallback callback,
        nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(
        nint windowHandle,
        StringBuilder className,
        int maximumLength);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(
        uint sourceThreadId,
        uint targetThreadId,
        [MarshalAs(UnmanagedType.Bool)] bool attach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint windowHandle);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint windowHandle, int command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        nint windowHandle,
        out uint processId);

    private sealed class PackagedDebugAdapterProcess : IAsyncDisposable
    {
        private readonly Process process;
        private readonly Task<string> standardError;
        private readonly List<JsonElement> messages = [];

        private PackagedDebugAdapterProcess(Process process)
        {
            this.process = process;
            standardError = process.StandardError.ReadToEndAsync();
        }

        public IReadOnlyList<JsonElement> Messages => messages;

        public static PackagedDebugAdapterProcess Start(
            string executablePath,
            string workingDirectory)
        {
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
                    "The packaged vba-dev debug adapter process did not start.");
            return new PackagedDebugAdapterProcess(process);
        }

        public async Task SendRequestAsync(int sequence, string command, object arguments)
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

        public Task<JsonElement> WaitForResponseAsync(int requestSequence, TimeSpan timeout)
            => WaitForMessageAsync(
                message =>
                    message.GetProperty("type").GetString() == "response" &&
                    message.GetProperty("request_seq").GetInt32() == requestSequence,
                $"response {requestSequence}",
                timeout);

        public Task<JsonElement> WaitForEventAsync(string eventName, TimeSpan timeout)
            => WaitForMessageAsync(
                message =>
                    message.GetProperty("type").GetString() == "event" &&
                    message.GetProperty("event").GetString() == eventName,
                $"event '{eventName}'",
                timeout);

        public async Task CompleteInputAndWaitForExitAsync(TimeSpan timeout)
        {
            process.StandardInput.Close();
            if (!process.HasExited)
            {
                await process.WaitForExitAsync().WaitAsync(timeout);
            }

            Assert.Equal(0, process.ExitCode);
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
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    }
                    catch
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
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

        private async Task<JsonElement> WaitForMessageAsync(
            Func<JsonElement, bool> predicate,
            string description,
            TimeSpan timeout)
        {
            var previous = messages.FirstOrDefault(predicate);
            if (previous.ValueKind != JsonValueKind.Undefined)
            {
                return previous;
            }

            using var cancellation = new CancellationTokenSource(timeout);
            try
            {
                while (true)
                {
                    var message = await ReadMessageAsync(cancellation.Token);
                    messages.Add(message);
                    if (predicate(message))
                    {
                        return message;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"The packaged debug adapter did not return {description} within {timeout}." +
                    $"{Environment.NewLine}{RenderTranscript()}");
            }
            catch (EndOfStreamException ex)
            {
                var error = process.HasExited
                    ? await standardError
                    : "The packaged debug adapter process is still running.";
                throw new InvalidOperationException(
                    $"The packaged debug adapter output ended before {description}." +
                    $"{Environment.NewLine}{error}{Environment.NewLine}{RenderTranscript()}",
                    ex);
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

        private string RenderTranscript()
            => string.Join(
                Environment.NewLine,
                messages.Select(message => message.GetRawText()));
    }

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);

    private sealed record OwnedExcelProcessIdentity(
        int ProcessId,
        DateTime StartTime);
}
