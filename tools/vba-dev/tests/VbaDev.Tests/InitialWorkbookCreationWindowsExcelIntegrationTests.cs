using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;
using VbaDev.App.Workbooks;
using VbaDev.Domain;
using VbaDev.Infrastructure.Workbooks;
using Xunit;

namespace VbaDev.Tests;

[Collection(WindowsExcelIntegrationCollection.Name)]
public sealed class InitialWorkbookCreationWindowsExcelIntegrationTests
{
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
        }

        Assert.True(File.Exists(workbookPath));
        Assert.Equal(workbookPath, result.ArtifactEvidence.WorkbookPath);
        Assert.DoesNotContain(result.ReferenceNames, VbaProjectReferenceName.IsStandardLibrary);
        Assert.Contains(result.ReferenceNames, reference =>
            reference.Contains("Excel", StringComparison.OrdinalIgnoreCase) &&
            reference.Contains("Object Library", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["Sheet1"], ReadWorksheetNames(workbookPath));
        Assert.True(initialProcesses.SetEquals(CaptureExcelProcessIds()));
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
