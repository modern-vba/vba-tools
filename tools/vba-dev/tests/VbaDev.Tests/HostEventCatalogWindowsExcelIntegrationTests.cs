using System.Diagnostics;
using System.Text.Json;
using VbaDev.App.HostEvents;
using VbaDev.Infrastructure.Debugging;
using VbaDev.Infrastructure.Workbooks;
using Xunit;

namespace VbaDev.Tests;

[Collection(WindowsExcelIntegrationCollection.Name)]
public sealed class HostEventCatalogWindowsExcelIntegrationTests
{
    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task RealExcelPostBootstrapFailureReleasesTheProcessAndBootstrapArtifact()
    {
        await AssertPostBootstrapTerminalCleanupAsync(cancel: false);
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task RealExcelPostBootstrapCancellationReleasesTheProcessAndBootstrapArtifact()
    {
        await AssertPostBootstrapTerminalCleanupAsync(cancel: true);
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task RealExcelReturnsGenericUserFormEventsWithoutOpeningOrSavingWorkspaceTemplates()
    {
        using var temp = TempDirectory.Create();
        var sentinels = Enumerable.Range(1, 15)
            .Select(index =>
            {
                var path = Path.Combine(temp.Path, $"Sentinel{index}.xlsm");
                var bytes = Enumerable.Repeat((byte)(0x40 + index), 128 + index).ToArray();
                File.WriteAllBytes(path, bytes);
                return (path, bytes);
            })
            .ToArray();
        var sentinelLocks = sentinels
            .Select(sentinel => new FileStream(
                sentinel.path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None))
            .ToArray();
        var initialBootstrapArtifacts = CaptureBootstrapArtifacts();
        var initialProcesses = CaptureExcelProcessIds();
        var observedOwnedProcesses = new HashSet<int>();
        var automation = new ExcelComHostEventCatalogAutomation();
        var application = CommandLineTestFactory.Create(
            temp.Path,
            hostEventCatalogAutomation: automation);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        var invocation = application.RunAsync(
            ["host-event", "list", "--format", "json"],
            cancellation.Token);
        while (!invocation.IsCompleted)
        {
            foreach (var processId in CaptureExcelProcessIds())
            {
                if (!initialProcesses.Contains(processId))
                {
                    observedOwnedProcesses.Add(processId);
                }
            }

            await Task.Delay(20, CancellationToken.None);
        }

        var result = await invocation;
        foreach (var sentinelLock in sentinelLocks)
        {
            sentinelLock.Dispose();
        }

        await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));

        Assert.True(
            result.ExitCode == 0,
            $"Expected exit 0 but received {result.ExitCode}. " +
            $"stdout: {result.StandardOutput} stderr: {result.StandardError}");
        Assert.Empty(result.StandardError);
        Assert.Single(observedOwnedProcesses);
        using var parsed = JsonDocument.Parse(result.StandardOutput);
        var output = parsed.RootElement;
        Assert.Equal("1.0", output.GetProperty("schemaVersion").GetString());
        Assert.Equal("userForm", output.GetProperty("sourceKind").GetString());
        Assert.Equal("UserForm", output.GetProperty("intrinsicEventSourceName").GetString());
        var events = output.GetProperty("events").EnumerateArray().ToArray();
        var initialize = Assert.Single(events, candidate =>
            candidate.GetProperty("identity").GetProperty("name").GetString() == "Initialize");
        Assert.Equal(
            "UserForm",
            initialize.GetProperty("identity").GetProperty("sourceName").GetString());
        Assert.Empty(initialize.GetProperty("signature").GetProperty("parameters").EnumerateArray());
        Assert.True(initialize.GetProperty("authoringAvailable").GetBoolean());
        Assert.True(initialize.GetProperty("existingHandlerRecognizable").GetBoolean());

        var queryClose = Assert.Single(events, candidate =>
            candidate.GetProperty("identity").GetProperty("name").GetString() == "QueryClose");
        var queryCloseParameters = queryClose
            .GetProperty("signature")
            .GetProperty("parameters")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(["Cancel", "CloseMode"], queryCloseParameters
            .Select(parameter => parameter.GetProperty("name").GetString()!)
            .ToArray());
        Assert.All(queryCloseParameters, parameter =>
        {
            Assert.Equal("byRef", parameter.GetProperty("passing").GetString());
            Assert.Equal("scalar", parameter.GetProperty("arrayShape").GetString());
            Assert.Equal("intrinsic", parameter.GetProperty("type").GetProperty("kind").GetString());
            Assert.Equal("Integer", parameter.GetProperty("type").GetProperty("name").GetString());
        });
        Assert.True(queryClose.GetProperty("authoringAvailable").GetBoolean());
        Assert.True(queryClose.GetProperty("existingHandlerRecognizable").GetBoolean());
        var provenance = output.GetProperty("baseTypeProvenance");
        Assert.False(string.IsNullOrWhiteSpace(provenance.GetProperty("name").GetString()));
        Assert.True(Guid.TryParse(provenance.GetProperty("libraryGuid").GetString(), out _));

        var metrics = automation.LifecycleMetrics;
        Assert.Equal(1, metrics.OwnedExcelProcessesStarted);
        Assert.Equal(1, metrics.BlankWorkbooksCreated);
        Assert.Equal(1, metrics.EmptyUserFormsCreated);
        Assert.Equal(1, metrics.EmptyUserFormsRemoved);
        Assert.Equal(1, metrics.WorkbooksClosedWithoutSave);
        Assert.Equal(0, metrics.TemplatesOpened);
        Assert.Equal(0, metrics.WorksheetsEnumerated);
        Assert.Equal(0, metrics.ControlsEnumerated);
        Assert.Equal(0, metrics.ModulesImported);
        Assert.Equal(0, metrics.WorkbooksSaved);
        Assert.Equal(0, metrics.PerDocumentFallbacks);
        foreach (var (path, bytes) in sentinels)
        {
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }

        Assert.True(initialProcesses.SetEquals(CaptureExcelProcessIds()));
        Assert.True(initialBootstrapArtifacts.SetEquals(CaptureBootstrapArtifacts()));
    }

    private static async Task AssertPostBootstrapTerminalCleanupAsync(bool cancel)
    {
        var initialProcesses = CaptureExcelProcessIds();
        var initialBootstrapArtifacts = CaptureBootstrapArtifacts();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var lifecycle = new PostBootstrapTerminalLifecycle(
            cancel,
            cancellation.Cancel);
        var automation = new ExcelComHostEventCatalogAutomation(
            new StaComDispatcherFactory(),
            lifecycle,
            HostEventCatalogTimeouts.Default);

        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => automation.ReadAsync(cancellation.Token));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => automation.ReadAsync(cancellation.Token));
        }

        await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
        Assert.True(initialProcesses.SetEquals(CaptureExcelProcessIds()));
        Assert.True(initialBootstrapArtifacts.SetEquals(CaptureBootstrapArtifacts()));
        Assert.Equal(1, automation.LifecycleMetrics.OwnedExcelProcessesStarted);
        Assert.Equal(0, automation.LifecycleMetrics.BlankWorkbooksCreated);
        Assert.Equal(0, automation.LifecycleMetrics.EmptyUserFormsCreated);
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

    private static IReadOnlySet<string> CaptureBootstrapArtifacts()
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

            await Task.Delay(100, CancellationToken.None);
        }

        Assert.Equal(
            expected.Order().ToArray(),
            CaptureExcelProcessIds().Order().ToArray());
    }

    private sealed class PostBootstrapTerminalLifecycle(
        bool cancel,
        Action requestCancellation) : IExcelComHostEventCatalogLifecycle
    {
        private readonly ExcelComHostEventCatalogAutomation.ExcelComHostEventCatalogLifecycle inner =
            new();

        public HostEventCatalogLifecycleCounters Counters => inner.Counters;

        public void ForceDisableAutomationSecurity(object host)
        {
            inner.ForceDisableAutomationSecurity(host);
            if (!cancel)
            {
                throw new InvalidOperationException("Injected post-bootstrap failure.");
            }
        }

        public void DisableExcelEvents(object host)
        {
            inner.DisableExcelEvents(host);
            if (cancel)
            {
                requestCancellation();
            }
        }

        public object CreateUnsavedBlankWorkbook(object host)
            => inner.CreateUnsavedBlankWorkbook(host);

        public object AddEmptyUserForm(object workbook)
            => inner.AddEmptyUserForm(workbook);

        public IntrinsicHostEventCatalog InspectEmptyUserForm(
            object host,
            object workbook,
            object userForm)
            => inner.InspectEmptyUserForm(host, workbook, userForm);

        public void RemoveUserForm(object workbook, object userForm)
            => inner.RemoveUserForm(workbook, userForm);

        public void CloseWorkbookWithoutSave(object workbook)
            => inner.CloseWorkbookWithoutSave(workbook);
    }
}
