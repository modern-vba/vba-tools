using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;
using VbaDev.App.FileSystem;
using VbaDev.App.Workbooks;
using VbaDev.Domain;
using VbaDev.Infrastructure.Debugging;
using VbaDev.Infrastructure.FileSystem;
using VbaDev.Infrastructure.Workbooks;
using Xunit;

namespace VbaDev.Tests;

[Collection(WindowsExcelIntegrationCollection.Name)]
public sealed class InitialWorkbookCreationWindowsExcelIntegrationTests
{
    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task RealInitialWorkbookPostSaveCancellationReleasesOwnedExcelAndCleansStaging()
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var workbookPath = Path.Combine(temp.Path, "CancelledInitialWorkbook.xlsm");
        var initialProcesses = CaptureExcelProcessIds();
        var lifecycle = new PostSaveObservedNativeInitialWorkbookLifecycle(cancellation.Cancel);
        var creator = new ExcelComInitialWorkbookCreator(
            new StaComDispatcherFactory(),
            lifecycle,
            WorkbookAutomationTimeouts.Default,
            new InitialWorkbookArtifactGuard());

        try
        {
            var failure = await Record.ExceptionAsync(() =>
                creator.CreateInitialWorkbookAsync(workbookPath, cancellation.Token));

            Assert.True(lifecycle.SavedBaselineObserved);
            Assert.True(lifecycle.SavedWorkbookExisted);
            Assert.IsAssignableFrom<OperationCanceledException>(failure);
            Assert.NotNull(lifecycle.SavedWorkbookPath);
            Assert.False(File.Exists(workbookPath));
            Assert.False(File.Exists(lifecycle.SavedWorkbookPath));
            Assert.False(Directory.Exists(Path.GetDirectoryName(lifecycle.SavedWorkbookPath)!));
        }
        finally
        {
            await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
            Assert.True(initialProcesses.SetEquals(CaptureExcelProcessIds()));
        }
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task RealInitialWorkbookPostSaveFailureReleasesOwnedExcelAndCleansStaging()
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var workbookPath = Path.Combine(temp.Path, "FailedInitialWorkbook.xlsm");
        var initialProcesses = CaptureExcelProcessIds();
        var injectedFailure = new InvalidOperationException("Injected post-save baseline failure.");
        var lifecycle = new PostSaveObservedNativeInitialWorkbookLifecycle(() => throw injectedFailure);
        var creator = new ExcelComInitialWorkbookCreator(
            new StaComDispatcherFactory(),
            lifecycle,
            WorkbookAutomationTimeouts.Default,
            new InitialWorkbookArtifactGuard());

        try
        {
            var failure = await Record.ExceptionAsync(() =>
                creator.CreateInitialWorkbookAsync(workbookPath, cancellation.Token));

            Assert.True(lifecycle.SavedBaselineObserved);
            Assert.True(lifecycle.SavedWorkbookExisted);
            Assert.IsAssignableFrom<InvalidOperationException>(failure);
            Assert.Contains(injectedFailure.Message, failure.ToString(), StringComparison.Ordinal);
            Assert.NotNull(lifecycle.SavedWorkbookPath);
            Assert.False(File.Exists(workbookPath));
            Assert.False(File.Exists(lifecycle.SavedWorkbookPath));
            Assert.False(Directory.Exists(Path.GetDirectoryName(lifecycle.SavedWorkbookPath)!));
        }
        finally
        {
            await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
            Assert.True(initialProcesses.SetEquals(CaptureExcelProcessIds()));
        }
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task RealExcelReturnsTheInvocationsCreateOnlyReceiptForProjectRollback()
    {
        using var temp = TempDirectory.Create();
        using var ownership = new WindowsExactFileSystemObjectOwnershipFactory().Open();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var workbookPath = Path.Combine(temp.Path, "OwnedSample.xlsm");
        var initialProcesses = CaptureExcelProcessIds();
        IReceiptInitialWorkbookCreator creator = new ExcelComInitialWorkbookCreator();

        try
        {
            var result = await creator.CreateInitialWorkbookAsync(workbookPath, ownership, cancellation.Token);

            var receipt = Assert.IsType<ExactFileSystemObjectOwnership.FileReceipt>(result.OwnedArtifactReceipt);
            Assert.Equal(workbookPath, receipt.Route);
            Assert.Equal(ExactFileSystemObjectOwnership.ObservationResult.Unchanged, ownership.Observe(receipt));
            Assert.Equal(["Sheet1"], ReadWorksheetNames(workbookPath));
            Assert.True(ownership.TryDelete(receipt).Removed);
            Assert.False(File.Exists(workbookPath));
        }
        finally
        {
            await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
            Assert.True(initialProcesses.SetEquals(CaptureExcelProcessIds()));
        }
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task RealExcelCreatesAndReleasesTheExactInitialWorkbookBaseline()
    {
        using var temp = TempDirectory.Create();
        var workbookPath = Path.Combine(temp.Path, "Sample.xlsm");
        var initialProcesses = CaptureExcelProcessIds();
        var creator = new ExcelComInitialWorkbookCreator(
            WorkbookAutomationTimeouts.Default with
            {
                ExcelStartup = TimeSpan.FromMinutes(2)
            });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        InitialWorkbookCreationResult result;
        try
        {
            result = await creator.CreateInitialWorkbookAsync(
                workbookPath,
                cancellation.Token);
        }
        finally
        {
            await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
            Assert.True(initialProcesses.SetEquals(CaptureExcelProcessIds()));
        }

        Assert.True(File.Exists(workbookPath));
        Assert.Equal(workbookPath, result.ArtifactEvidence.WorkbookPath);
        Assert.DoesNotContain(result.ReferenceNames, VbaProjectReferenceName.IsStandardLibrary);
        Assert.Contains(result.ReferenceNames, reference =>
            reference.Contains("Excel", StringComparison.OrdinalIgnoreCase) &&
            reference.Contains("Object Library", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["Sheet1"], ReadWorksheetNames(workbookPath));
    }

    private sealed class PostSaveObservedNativeInitialWorkbookLifecycle(Action afterSavedBaseline)
        : IExcelComInitialWorkbookLifecycle
    {
        public string? SavedWorkbookPath { get; private set; }

        public bool SavedWorkbookExisted { get; private set; }

        public bool SavedBaselineObserved { get; private set; }

        public object Start(
            OwnedExcelTerminationController terminationController,
            CancellationToken cancellationToken)
            => ExcelComWorkbookSession.StartOwnedForGeneration(
                terminationController,
                cancellationToken);

        public IExcelComInitialWorkbookSession CreateWorkbook(object host, int template)
            => new PostSaveObservedNativeInitialWorkbookSession(
                new ExcelComInitialWorkbookSession(
                    ExcelComWorkbookSession.CreateOwnedForGeneration(
                        (ExcelComWorkbookSession.ExcelComHostObjects)host,
                        template)),
                workbookPath =>
                {
                    SavedWorkbookPath = workbookPath;
                    SavedWorkbookExisted = File.Exists(workbookPath);
                },
                () =>
                {
                    SavedBaselineObserved = true;
                    afterSavedBaseline();
                });

        public void DisposeHost(object host, TimeSpan cleanupGrace)
            => ExcelComWorkbookSession.DisposeOwnedGenerationHost(
                (ExcelComWorkbookSession.ExcelComHostObjects)host,
                cleanupGrace);

        public void DisposeSession(IExcelComInitialWorkbookSession session, TimeSpan cleanupGrace)
            => ((PostSaveObservedNativeInitialWorkbookSession)session).NativeSession
                .DisposeOwnedGeneration(cleanupGrace);
    }

    private sealed class PostSaveObservedNativeInitialWorkbookSession(
        ExcelComInitialWorkbookSession nativeSession,
        Action<string> afterSave,
        Action afterSavedBaseline) : IExcelComInitialWorkbookSession
    {
        public ExcelComInitialWorkbookSession NativeSession => nativeSession;

        public InitialWorkbookBaselineSnapshot EstablishAndReadBaseline()
            => nativeSession.EstablishAndReadBaseline();

        public void Save(string workbookPath, int fileFormat)
        {
            nativeSession.Save(workbookPath, fileFormat);
            afterSave(workbookPath);
        }

        public InitialWorkbookBaselineSnapshot ReadBaseline()
        {
            var baseline = nativeSession.ReadBaseline();
            afterSavedBaseline();
            return baseline;
        }
    }

    private static IReadOnlyList<string> ReadWorksheetNames(string workbookPath)
    {
        using var archive = ZipFile.OpenRead(workbookPath);
        var workbookEntry = archive.GetEntry("xl/workbook.xml")
            ?? throw new InvalidOperationException("The generated workbook has no xl/workbook.xml part.");
        using var stream = workbookEntry.Open();
        var workbook = XDocument.Load(stream);
        XNamespace spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        return workbook
            .Descendants(spreadsheet + "sheet")
            .Select(sheet => (string?)sheet.Attribute("name") ?? string.Empty)
            .ToArray();
    }

    private static HashSet<int> CaptureExcelProcessIds()
    {
        var processIds = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName("EXCEL"))
        {
            using (process)
            {
                processIds.Add(process.Id);
            }
        }

        return processIds;
    }

    private static async Task WaitForProcessSetAsync(
        HashSet<int> expectedProcessIds,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!expectedProcessIds.SetEquals(CaptureExcelProcessIds()) &&
               DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }
    }
}
