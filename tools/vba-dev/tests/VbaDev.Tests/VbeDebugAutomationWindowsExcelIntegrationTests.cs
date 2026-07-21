using System.Diagnostics;
using System.Text;
using VbaDev.App.Debugging;
using VbaDev.App.Projects;
using VbaDev.Composition;
using Xunit;

namespace VbaDev.Tests;

[Collection(WindowsExcelIntegrationCollection.Name)]
public sealed class VbeDebugAutomationWindowsExcelIntegrationTests
{
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
                        new DebugTargetProcedure("DebugModule", "RunTarget")),
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
