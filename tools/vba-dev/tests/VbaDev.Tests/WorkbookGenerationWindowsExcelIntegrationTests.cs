using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using VbaDev.App.Build;
using VbaDev.App.References;
using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Debugging;
using VbaDev.Infrastructure.Workbooks;
using Xunit;

namespace VbaDev.Tests;

[Collection(WindowsExcelIntegrationCollection.Name)]
public sealed class WorkbookGenerationWindowsExcelIntegrationTests
{
    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task ExplicitlyLaunchedOwnedExcelCanBeBoundAndReleased()
    {
        using var terminationController = new OwnedExcelTerminationController();
        await using var dispatcher = new StaComDispatcher();
        var host = await dispatcher.InvokeAsync(
            () => ExcelComWorkbookSession.StartOwnedForGeneration(
                terminationController,
                CancellationToken.None),
            CancellationToken.None);
        await dispatcher.InvokeAsync(
            () =>
            {
                try
                {
                    dynamic excel = host.ExcelObject;
                    Assert.False(string.IsNullOrWhiteSpace(Convert.ToString(excel.Version)));
                }
                finally
                {
                    ExcelComWorkbookSession.DisposeOwnedGenerationHost(
                        host,
                        TimeSpan.FromSeconds(5));
                }

                return true;
            },
            CancellationToken.None);
        await terminationController.RequestCleanupAsync(TimeSpan.FromSeconds(5));
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task StartupTimeoutPreservesExistingTargetAndCleansOwnedArtifacts()
    {
        using var temp = TempDirectory.Create();
        var initialProcesses = CaptureExcelProcessIds();
        var initialBootstrapFiles = CaptureBootstrapWorkbookPaths();
        var sourceDirectory = temp.CreateDirectory("src");
        var templatePath = Path.Combine(sourceDirectory, "OwnedBuildProject.xlsm");
        var targetPath = Path.Combine(temp.Path, "bin", "OwnedBuildProject.xlsm");
        CreateEmptyMacroEnabledWorkbook(templatePath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var originalTarget = Encoding.UTF8.GetBytes("existing-target");
        File.WriteAllBytes(targetPath, originalTarget);
        var pipeline = CreateGenerationPipeline();
        var timeouts = WorkbookAutomationTimeouts.Default with
        {
            ExcelStartup = TimeSpan.FromMilliseconds(1),
            ProcessCleanup = TimeSpan.Zero
        };

        var error = await Assert.ThrowsAsync<WorkbookAutomationTimeoutException>(() =>
            pipeline.GenerateAsync(
                "OwnedBuildProject",
                templatePath,
                targetPath,
                [],
                [],
                timeouts,
                CancellationToken.None));

        Assert.Equal(WorkbookAutomationStageKind.ExcelStartup, error.Stage.Kind);
        Assert.Equal(originalTarget, File.ReadAllBytes(targetPath));
        Assert.Empty(Directory.EnumerateFiles(
            Path.GetDirectoryName(targetPath)!,
            ".OwnedBuildProject.*.tmp.xlsm"));
        await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
        Assert.Equal(
            initialBootstrapFiles.Order(),
            CaptureBootstrapWorkbookPaths().Order());
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task CancellationDuringStartupPreservesExistingTargetAndCleansOwnedArtifacts()
    {
        using var temp = TempDirectory.Create();
        var initialProcesses = CaptureExcelProcessIds();
        var initialBootstrapFiles = CaptureBootstrapWorkbookPaths();
        var sourceDirectory = temp.CreateDirectory("src");
        var templatePath = Path.Combine(sourceDirectory, "OwnedBuildProject.xlsm");
        var targetPath = Path.Combine(temp.Path, "bin", "OwnedBuildProject.xlsm");
        CreateEmptyMacroEnabledWorkbook(templatePath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var originalTarget = Encoding.UTF8.GetBytes("existing-target");
        File.WriteAllBytes(targetPath, originalTarget);
        var pipeline = CreateGenerationPipeline();
        using var cancellation = new CancellationTokenSource();

        var generation = pipeline.GenerateAsync(
            "OwnedBuildProject",
            templatePath,
            targetPath,
            [],
            [],
            WorkbookAutomationTimeouts.Default with
            {
                ProcessCleanup = TimeSpan.Zero
            },
            cancellation.Token);
        _ = await WaitForOwnedExcelProcessAsync(
            initialProcesses,
            generation,
            TimeSpan.FromSeconds(20));
        cancellation.Cancel();
        var error = await Assert.ThrowsAsync<WorkbookAutomationCanceledException>(
            () => generation);

        Assert.Contains(
            error.Stage.Kind,
            new[]
            {
                WorkbookAutomationStageKind.ExcelStartup,
                WorkbookAutomationStageKind.WorkbookOpen
            });
        Assert.Equal(originalTarget, File.ReadAllBytes(targetPath));
        Assert.Empty(Directory.EnumerateFiles(
            Path.GetDirectoryName(targetPath)!,
            ".OwnedBuildProject.*.tmp.xlsm"));
        await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
        Assert.Equal(
            initialBootstrapFiles.Order(),
            CaptureBootstrapWorkbookPaths().Order());
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task UnexpectedOwnedProcessLossIsReportedWithoutTouchingOtherExcelProcesses()
    {
        using var temp = TempDirectory.Create();
        var initialProcesses = CaptureExcelProcessIds();
        var templatePath = Path.Combine(temp.Path, "OwnedBuildProject.xlsm");
        CreateEmptyMacroEnabledWorkbook(templatePath);
        var automation = new ExcelComWorkbookBuildAutomation();

        var error = await Assert.ThrowsAsync<WorkbookAutomationProcessLostException>(() =>
            automation.RunAsync(
                templatePath,
                WorkbookAutomationTimeouts.Default with
                {
                    ProcessCleanup = TimeSpan.Zero
                },
                async (session, cancellationToken) =>
                {
                    var ownedProcessId = Assert.Single(
                        CaptureExcelProcessIds().Except(initialProcesses));
                    using var ownedProcess = Process.GetProcessById(ownedProcessId);
                    ownedProcess.Kill(entireProcessTree: false);
                    await ownedProcess.WaitForExitAsync(cancellationToken);
                    await session.VerifyAsync(cancellationToken);
                    return true;
                },
                CancellationToken.None));

        Assert.Equal(WorkbookAutomationStageKind.Verification, error.Stage.Kind);
        await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task GenerationCompletesInItsOwnExcelProcessWithoutDisturbingPreExistingExcel()
    {
        using var temp = TempDirectory.Create();
        var initialProcesses = CaptureExcelProcessIds();
        var sourceDirectory = temp.CreateDirectory("src");
        var templatePath = Path.Combine(sourceDirectory, "OwnedBuildProject.xlsm");
        var sourcePath = Path.Combine(sourceDirectory, "Feature.bas");
        var targetPath = Path.Combine(temp.Path, "bin", "OwnedBuildProject.xlsm");
        CreateEmptyMacroEnabledWorkbook(templatePath);
        File.WriteAllText(
            sourcePath,
            "Attribute VB_Name = \"Feature\"\r\nOption Explicit\r\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        object? preExistingExcel = null;
        try
        {
            preExistingExcel = CreateHiddenExcelApplication();
            var processesWithPreExistingExcel = CaptureExcelProcessIds();
            var preExistingProcessId = Assert.Single(
                processesWithPreExistingExcel.Except(initialProcesses));

            var pipeline = CreateGenerationPipeline();
            await pipeline.GenerateAsync(
                "OwnedBuildProject",
                templatePath,
                targetPath,
                [],
                [new VbaSourceFile(sourcePath, VbaSourceKind.StandardModule, null)],
                WorkbookAutomationTimeouts.Default,
                CancellationToken.None);

            Assert.True(File.Exists(targetPath));
            Assert.Empty(Directory.EnumerateFiles(
                Path.GetDirectoryName(targetPath)!,
                ".OwnedBuildProject.*.tmp.xlsm"));
            await WaitForProcessSetAsync(processesWithPreExistingExcel, TimeSpan.FromSeconds(20));
            Assert.Contains(preExistingProcessId, CaptureExcelProcessIds());

            dynamic excel = preExistingExcel;
            Assert.False(string.IsNullOrWhiteSpace(Convert.ToString(excel.Version)));
        }
        finally
        {
            QuitExcel(preExistingExcel);
        }

        await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
    }

    private static WorkbookGenerationPipeline CreateGenerationPipeline()
        => new(
            (IWorkbookGenerationAutomation)new ExcelComWorkbookBuildAutomation(),
            new WorkbookReferenceNormalizer(
                new VbaProjectReferencePlanner(new FakeVbaProjectReferenceResolver())));

    private static IReadOnlySet<string> CaptureBootstrapWorkbookPaths()
        => Directory
            .EnumerateFiles(
                Path.GetTempPath(),
                "vba-dev-excel-bootstrap-*.xlsx",
                SearchOption.TopDirectoryOnly)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static object CreateHiddenExcelApplication()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Excel COM automation requires Windows.");
        }

        var excelType = Type.GetTypeFromProgID("Excel.Application")
            ?? throw new InvalidOperationException("Excel COM automation is unavailable.");
        var excelObject = Activator.CreateInstance(excelType)
            ?? throw new InvalidOperationException("Excel COM automation could not be started.");
        dynamic excel = excelObject;
        excel.Visible = false;
        excel.DisplayAlerts = false;
        return excelObject;
    }

    private static void QuitExcel(object? excelObject)
    {
        if (excelObject is null)
        {
            return;
        }

        try
        {
            dynamic excel = excelObject;
            excel.Quit();
        }
        finally
        {
            ComObjectReleaser.Release(excelObject);
            ComObjectReleaser.CollectReleasedComObjects();
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
        using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
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

            await Task.Delay(100);
        }

        Assert.Equal(expected.Order().ToArray(), CaptureExcelProcessIds().Order().ToArray());
    }

    private static async Task<int> WaitForOwnedExcelProcessAsync(
        IReadOnlySet<int> initialProcesses,
        Task generation,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var newProcesses = CaptureExcelProcessIds().Except(initialProcesses).ToArray();
            if (newProcesses.Length == 1)
            {
                return newProcesses[0];
            }

            if (generation.IsCompleted)
            {
                await generation;
                throw new InvalidOperationException(
                    "Workbook generation completed before an owned Excel process was observed.");
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("An owned Excel process was not observed before cancellation.");
    }
}
