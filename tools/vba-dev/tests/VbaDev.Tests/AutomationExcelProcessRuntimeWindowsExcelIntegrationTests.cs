using System.IO.Compression;
using System.Text;
using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Debugging;
using VbaDev.Infrastructure.Workbooks;
using Xunit;

namespace VbaDev.Tests;

[Collection(WindowsExcelIntegrationCollection.Name)]
public sealed class AutomationExcelProcessRuntimeWindowsExcelIntegrationTests
{
    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task WorkbookResultWaitsForExactProcessTreeDesktopAndStaRelease()
    {
        using var temp = TempDirectory.Create();
        var workbookPath = Path.Combine(temp.Path, "RuntimeOwnership.xlsm");
        CreateEmptyMacroEnabledWorkbook(workbookPath);
        var lifecycle = new ObservedNativeWorkbookLifecycle();
        var runtime = new AutomationExcelProcessRuntime(
            new StaComDispatcherFactory(),
            lifecycle);

        var outcome = await runtime.RunWorkbookAsync(
            workbookPath,
            WorkbookAutomationTimeouts.Default,
            async (session, cancellationToken) =>
            {
                var modules = await session.GetModulesAsync(cancellationToken);
                await session.SaveAsync(cancellationToken);
                return modules;
            },
            CancellationToken.None);

        var modules = outcome.GetReleasedResult();
        Assert.Contains(modules, module => module.Name == "ThisWorkbook");
        Assert.True(outcome.Evidence.ProcessReleaseVerified);
        Assert.True(outcome.Evidence.DispatcherRetired);
        Assert.Null(outcome.Evidence.OperationFailure);
        Assert.Null(outcome.Evidence.CleanupFailure);
        Assert.Null(outcome.Evidence.DispatcherFailure);
        Assert.Equal(
            WorkbookAutomationStageKind.WorkbookSave,
            outcome.Evidence.LastOperationStage!.Kind);

        var owner = Assert.IsType<DebugExcelProcessOwner>(lifecycle.Owner);
        Assert.True(owner.Completion.IsCompletedSuccessfully);
        var desktop = Assert.IsType<ObservedNativeDesktop>(lifecycle.DesktopFactory.Desktop);
        Assert.Equal(0u, desktop.ActiveJobProcessesAfterExit);
        Assert.True(desktop.ExactProcessExitCompleted);
        Assert.True(desktop.DisposeCompleted);
        Assert.Throws<ObjectDisposedException>(() => _ = desktop.NativeDesktop.DesktopHandle);
        var windowEvidence = Assert.IsType<DesktopWindowExposureEvidence>(desktop.FinalEvidence);
        Assert.Equal(owner.ProcessId, windowEvidence.ExactProcessId);
        Assert.False(windowEvidence.HasCallerDesktopExposure);
        Assert.DoesNotContain(
            windowEvidence.Observations,
            observation => observation.Cause == DesktopWindowObservationCause.ProcessExitSnapshot);
        Assert.Single(lifecycle.AutomationThreads.Distinct());
        Assert.All(lifecycle.AutomationApartments, apartment => Assert.Equal(ApartmentState.STA, apartment));
    }

    private sealed class ObservedNativeWorkbookLifecycle : IExcelComWorkbookGenerationLifecycle
    {
        public ObservedNativeWorkbookLifecycle()
        {
            DesktopFactory = new ObservedNativeDesktopFactory(() => Owner);
        }

        public DebugExcelProcessOwner? Owner { get; private set; }

        public ObservedNativeDesktopFactory DesktopFactory { get; }

        public List<int> AutomationThreads { get; } = [];

        public List<ApartmentState> AutomationApartments { get; } = [];

        public object Start(
            OwnedExcelTerminationController terminationController,
            bool enableAutomationSecurityLow,
            CancellationToken cancellationToken)
        {
            RecordAutomationThread();
            var bootstrapper = new OwnedExcelApplicationBootstrapper(
                new WindowsExcelOwnedProcessLauncher(),
                new WindowsDebugExcelProcessApi(),
                new WindowsExcelNativeObjectModelBinder(),
                DesktopFactory);
            return ExcelComWorkbookSession.StartExplicitlyOwnedHiddenExcel(
                enableAutomationSecurityLow,
                terminationController,
                cancellationToken,
                (controller, token) =>
                {
                    var application = bootstrapper.Start(controller, token);
                    Owner = application.ProcessOwner;
                    return application;
                },
                ExcelBootstrapWorkbookFile.Delete);
        }

        public IWorkbookBuildSession Open(object host, string workbookPath)
        {
            RecordAutomationThread();
            return new ExcelComWorkbookBuildSession(
                ExcelComWorkbookSession.OpenOwnedForGeneration(
                    (ExcelComWorkbookSession.ExcelComHostObjects)host,
                    workbookPath));
        }

        public void DisposeHost(object host, TimeSpan cleanupGrace)
        {
            RecordAutomationThread();
            ExcelComWorkbookSession.DisposeOwnedGenerationHost(
                (ExcelComWorkbookSession.ExcelComHostObjects)host,
                cleanupGrace);
        }

        public void DisposeSession(IWorkbookBuildSession session, TimeSpan cleanupGrace)
        {
            RecordAutomationThread();
            ((ExcelComWorkbookBuildSession)session).DisposeOwnedGeneration(cleanupGrace);
        }

        private void RecordAutomationThread()
        {
            AutomationThreads.Add(Environment.CurrentManagedThreadId);
            AutomationApartments.Add(Thread.CurrentThread.GetApartmentState());
        }
    }

    private sealed class ObservedNativeDesktopFactory(Func<DebugExcelProcessOwner?> getOwner)
        : IExcelAutomationDesktopIsolationFactory
    {
        public ObservedNativeDesktop? Desktop { get; private set; }

        public IExcelAutomationDesktopIsolation Create()
            => Desktop = new ObservedNativeDesktop(
                WindowsExcelAutomationDesktopIsolation.Create(),
                getOwner);
    }

    private sealed class ObservedNativeDesktop(
        WindowsExcelAutomationDesktopIsolation nativeDesktop,
        Func<DebugExcelProcessOwner?> getOwner)
        : IExcelAutomationDesktopIsolation,
          IExcelAutomationDesktopEvidence
    {
        public WindowsExcelAutomationDesktopIsolation NativeDesktop => nativeDesktop;

        public uint? ActiveJobProcessesAfterExit { get; private set; }

        public bool ExactProcessExitCompleted { get; private set; }

        public bool DisposeCompleted { get; private set; }

        public DesktopWindowExposureEvidence? FinalEvidence { get; private set; }

        public string QualifiedDesktopName => nativeDesktop.QualifiedDesktopName;

        public nint DesktopHandle => nativeDesktop.DesktopHandle;

        public DesktopWindowExposureEvidence Evidence => nativeDesktop.Evidence;

        public Task StartObservingBeforeResumeAsync(
            int exactProcessId,
            CancellationToken cancellationToken)
            => nativeDesktop.StartObservingBeforeResumeAsync(exactProcessId, cancellationToken);

        public void Capture(DesktopWindowLifecyclePhase phase)
            => nativeDesktop.Capture(phase);

        public async Task<DesktopWindowExposureEvidence> CompleteAfterExitAsync(
            Task exactProcessExit,
            CancellationToken cancellationToken)
        {
            var owner = getOwner();
            ActiveJobProcessesAfterExit = owner?.ActiveJobProcessCount;
            ExactProcessExitCompleted = owner?.Completion.IsCompletedSuccessfully == true;
            FinalEvidence = await nativeDesktop.CompleteAfterExitAsync(
                exactProcessExit,
                cancellationToken);
            return FinalEvidence;
        }

        public async ValueTask DisposeAsync()
        {
            await nativeDesktop.DisposeAsync();
            DisposeCompleted = true;
        }
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
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }
}
