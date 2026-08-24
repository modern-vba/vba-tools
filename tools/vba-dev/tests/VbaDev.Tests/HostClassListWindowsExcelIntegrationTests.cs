using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using VbaDev.App.HostClasses;
using VbaDev.Domain;
using VbaDev.Infrastructure.Debugging;
using VbaDev.Infrastructure.Projects;
using VbaDev.Infrastructure.Workbooks;
using Xunit;

namespace VbaDev.Tests;

[Collection(WindowsExcelIntegrationCollection.Name)]
public sealed class HostClassListWindowsExcelIntegrationTests
{
    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task PublicCommandInspectsFormAndDocumentEventsFromAnUnchangedPrivateTemplateCopy()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var documentRoot = Path.Combine(root, "src", "Book1");
        Directory.CreateDirectory(documentRoot);
        var templatePath = Path.Combine(documentRoot, "Book1.xlsm");
        var sentinelPath = Path.Combine(temp.Path, "macro-ran.txt");
        CreateEmptyMacroEnabledWorkbook(templatePath);
        var expectedWorksheetStructuralEvents =
            await ProvisionHostClassFixtureAsync(templatePath, sentinelPath);
        new JsonProjectManifestStore().Save(
            root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var originalTemplate = File.ReadAllBytes(templatePath);
        var manifestPath = Path.Combine(root, "vba-project.json");
        var originalManifest = File.ReadAllBytes(manifestPath);
        var initialProcesses = CaptureExcelProcessIds();
        var initialWorkspaces = CaptureHostClassWorkspaces();
        var observedOwnedProcesses = new HashSet<int>();
        var lifecycle = new ObservingProductionHostClassInspectionLifecycle();
        var application = CommandLineTestFactory.Create(
            root,
            hostClassInspectionAutomation: new ExcelComHostClassInspectionAutomation(
                new StaComDispatcherFactory(),
                lifecycle,
                new HostClassInspectionWorkspaceFactory()));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        var invocation = application.RunAsync(
            ["host-class", "list", "--format", "json"],
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
        await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));

        Assert.True(
            result.ExitCode == 0,
            $"Expected exit 0 but received {result.ExitCode}. stdout: {result.StandardOutput} stderr: {result.StandardError}");
        Assert.Empty(result.StandardError);
        Assert.Single(observedOwnedProcesses);
        using var parsed = JsonDocument.Parse(result.StandardOutput);
        var output = parsed.RootElement;
        Assert.Equal("1.0", output.GetProperty("schemaVersion").GetString());
        Assert.True(output.GetProperty("classEnumerationComplete").GetBoolean());
        Assert.True(output.GetProperty("complete").GetBoolean());
        Assert.Empty(output.GetProperty("diagnostics").EnumerateArray());
        Assert.Empty(output.GetProperty("warnings").EnumerateArray());
        Assert.True(lifecycle.AutomationSecurityForceDisabled);
        Assert.True(lifecycle.ExcelEventsDisabled);
        Assert.Equal(0, lifecycle.OpenWorkbookCountBeforePrivateCopy);
        Assert.Equal(1, lifecycle.OpenWorkbookCountAfterPrivateCopy);
        Assert.NotEqual(
            Path.GetFullPath(templatePath),
            lifecycle.RequestedPrivateCopyPath);
        Assert.Equal(
            lifecycle.RequestedPrivateCopyPath,
            lifecycle.OpenedWorkbookPath);
        var observedWorkspace = Path.GetDirectoryName(
            lifecycle.RequestedPrivateCopyPath)!;
        Assert.Equal(
            Path.GetFullPath(Path.Combine(
                Path.GetTempPath(),
                "vba-dev-host-class-inspection")),
            Path.GetDirectoryName(observedWorkspace),
            StringComparer.OrdinalIgnoreCase);
        Assert.True(Guid.TryParseExact(
            Path.GetFileName(observedWorkspace),
            "N",
            out _));
        var classes = output.GetProperty("classes").EnumerateArray().ToArray();
        AssertHostEvent(classes, "HostForm", "form", "UserForm", "Initialize");
        AssertHostEvent(classes, "HostForm", "form", "UserForm", "QueryClose");
        AssertHostEvent(classes, "ThisWorkbook", "document", "Workbook", "BeforeClose");
        var change = AssertHostEvent(
            classes,
            "Sheet1",
            "document",
            "Worksheet",
            "Change");
        var target = Assert.Single(change.GetProperty("parameters").EnumerateArray());
        Assert.Equal("Target", target.GetProperty("name").GetString());
        Assert.Equal("byVal", target.GetProperty("passing").GetString());
        Assert.Equal("scalar", target.GetProperty("arrayShape").GetString());
        var targetType = target.GetProperty("type");
        Assert.Equal("typeLib", targetType.GetProperty("kind").GetString());
        Assert.Equal("Range", targetType.GetProperty("name").GetString());
        Assert.Equal(
            "00020813-0000-0000-c000-000000000046",
            targetType.GetProperty("libraryGuid").GetString());
        Assert.True(targetType.GetProperty("majorVersion").GetInt32() > 0);
        Assert.True(targetType.GetProperty("minorVersion").GetInt32() >= 0);
        Assert.True(targetType.GetProperty("lcid").GetInt32() >= 0);
        var worksheetClass = Assert.Single(classes, candidate =>
            candidate.GetProperty("identity").GetProperty("name").GetString() == "Sheet1" &&
            candidate.GetProperty("identity").GetProperty("kind").GetString() == "document");
        var worksheetBase = worksheetClass.GetProperty("baseTypeProvenance");
        Assert.Equal("_Worksheet", worksheetBase.GetProperty("name").GetString());
        Assert.Equal(
            "00020813-0000-0000-c000-000000000046",
            worksheetBase.GetProperty("libraryGuid").GetString());
        var projectedWorksheetEvents = worksheetClass
            .GetProperty("events")
            .EnumerateArray()
            .ToArray();
        foreach (var structuralEventName in expectedWorksheetStructuralEvents)
        {
            var matchingEvents = projectedWorksheetEvents.Where(
                candidate => candidate.GetProperty("name").GetString() ==
                    structuralEventName).ToArray();
            Assert.True(
                matchingEvents.Length == 1,
                $"Expected structural Worksheet Event '{structuralEventName}' once, " +
                $"but received {matchingEvents.Length} matching projections. " +
                $"Structural surface: {string.Join(", ", expectedWorksheetStructuralEvents)}");
            _ = matchingEvents[0].GetProperty("authoringAvailable").GetBoolean();
            _ = matchingEvents[0]
                .GetProperty("existingHandlerRecognizable")
                .GetBoolean();
        }
        var remoteBeforeDelete = Assert.Single(
            projectedWorksheetEvents,
            candidate => candidate.GetProperty("name").GetString() ==
                "RemoteBeforeDelete");
        Assert.False(remoteBeforeDelete.GetProperty("authoringAvailable").GetBoolean());
        Assert.True(
            remoteBeforeDelete
                .GetProperty("existingHandlerRecognizable")
                .GetBoolean());
        var remoteChange = Assert.Single(
            projectedWorksheetEvents,
            candidate => candidate.GetProperty("name").GetString() ==
                "RemoteChange");
        Assert.False(remoteChange.GetProperty("authoringAvailable").GetBoolean());
        Assert.True(remoteChange.GetProperty("existingHandlerRecognizable").GetBoolean());
        var remoteTarget = Assert.Single(
            remoteChange.GetProperty("parameters").EnumerateArray());
        Assert.Equal("Target", remoteTarget.GetProperty("name").GetString());
        Assert.Equal("byVal", remoteTarget.GetProperty("passing").GetString());
        Assert.Equal("scalar", remoteTarget.GetProperty("arrayShape").GetString());
        var remoteTargetType = remoteTarget.GetProperty("type");
        Assert.Equal("typeLib", remoteTargetType.GetProperty("kind").GetString());
        Assert.Equal("Range", remoteTargetType.GetProperty("name").GetString());
        Assert.Equal(
            "00020813-0000-0000-c000-000000000046",
            remoteTargetType.GetProperty("libraryGuid").GetString());
        Assert.DoesNotContain(
            projectedWorksheetEvents,
            candidate => candidate.GetProperty("name").GetString() == "AddRef");
        Assert.False(File.Exists(sentinelPath));
        Assert.Equal(originalTemplate, File.ReadAllBytes(templatePath));
        Assert.Equal(originalManifest, File.ReadAllBytes(manifestPath));
        Assert.True(initialWorkspaces.SetEquals(CaptureHostClassWorkspaces()));
        Assert.True(initialProcesses.SetEquals(CaptureExcelProcessIds()));
    }

    private static JsonElement AssertHostEvent(
        IReadOnlyList<JsonElement> classes,
        string className,
        string classKind,
        string sourceName,
        string eventName)
    {
        var projectedClass = Assert.Single(classes, candidate =>
            candidate.GetProperty("identity").GetProperty("name").GetString() == className &&
            candidate.GetProperty("identity").GetProperty("kind").GetString() == classKind);
        Assert.Equal("resolved", projectedClass.GetProperty("status").GetString());
        Assert.Equal(sourceName, projectedClass.GetProperty("intrinsicEventSourceName").GetString());
        var projectedEvents = projectedClass.GetProperty("events").EnumerateArray().ToArray();
        var matchingEvents = projectedEvents.Where(candidate =>
            candidate.GetProperty("name").GetString() == eventName).ToArray();
        Assert.True(
            matchingEvents.Length == 1,
            $"Expected Event '{eventName}' once in class '{className}', but received: {string.Join(", ", projectedEvents.Select(candidate => candidate.GetProperty("name").GetString()))}. Class JSON: {projectedClass}");
        var projectedEvent = matchingEvents[0];
        _ = projectedEvent.GetProperty("authoringAvailable").GetBoolean();
        _ = projectedEvent.GetProperty("existingHandlerRecognizable").GetBoolean();
        return projectedEvent;
    }

    private sealed class ObservingProductionHostClassInspectionLifecycle
        : IExcelComHostClassInspectionLifecycle
    {
        private readonly ExcelComHostClassInspectionAutomation
            .ExcelComHostClassInspectionLifecycle inner = new();

        public bool AutomationSecurityForceDisabled { get; private set; }

        public bool ExcelEventsDisabled { get; private set; }

        public int OpenWorkbookCountBeforePrivateCopy { get; private set; } = -1;

        public int OpenWorkbookCountAfterPrivateCopy { get; private set; } = -1;

        public string RequestedPrivateCopyPath { get; private set; } = string.Empty;

        public string OpenedWorkbookPath { get; private set; } = string.Empty;

        public void ValidateSafePrivateCopy(string workbookPath)
            => inner.ValidateSafePrivateCopy(workbookPath);

        public object Start(
            OwnedExcelTerminationController terminationController,
            CancellationToken cancellationToken)
            => inner.Start(terminationController, cancellationToken);

        public void ForceDisableAutomationSecurity(object host)
        {
            inner.ForceDisableAutomationSecurity(host);
            dynamic excel = ((ExcelComWorkbookSession.ExcelComHostObjects)host).ExcelObject;
            AutomationSecurityForceDisabled = (int)excel.AutomationSecurity == 3;
        }

        public void DisableExcelEvents(object host)
        {
            inner.DisableExcelEvents(host);
            dynamic excel = ((ExcelComWorkbookSession.ExcelComHostObjects)host).ExcelObject;
            ExcelEventsDisabled = !(bool)excel.EnableEvents;
        }

        public object OpenPrivateWorkbookReadOnly(object host, string workbookPath)
        {
            dynamic workbooks = ((ExcelComWorkbookSession.ExcelComHostObjects)host).WorkbooksObject;
            OpenWorkbookCountBeforePrivateCopy = (int)workbooks.Count;
            RequestedPrivateCopyPath = Path.GetFullPath(workbookPath);
            var workbook = inner.OpenPrivateWorkbookReadOnly(host, workbookPath);
            OpenWorkbookCountAfterPrivateCopy = (int)workbooks.Count;
            dynamic openedWorkbook = workbook;
            OpenedWorkbookPath = Path.GetFullPath((string)openedWorkbook.FullName);
            return workbook;
        }

        public HostClassIdentityEnumeration EnumerateClasses(object host, object workbook)
            => inner.EnumerateClasses(host, workbook);

        public HostClassInspectionEntry InspectClass(
            object host,
            object workbook,
            HostClassComponentDescriptor component)
            => inner.InspectClass(host, workbook, component);

        public void CloseWorkbookWithoutSave(object workbook)
            => inner.CloseWorkbookWithoutSave(workbook);

        public void DisposeHost(object host, TimeSpan cleanupGrace)
            => inner.DisposeHost(host, cleanupGrace);
    }

    private static async Task<IReadOnlyList<string>> ProvisionHostClassFixtureAsync(
        string workbookPath,
        string sentinelPath)
    {
        using var terminationController = new OwnedExcelTerminationController();
        var dispatcher = new StaComDispatcher();
        object? hostObject = null;
        object? workbookObject = null;
        IReadOnlyList<string> worksheetStructuralEvents = [];
        Exception? operationError = null;
        try
        {
            await dispatcher.InvokeAsync(
                () =>
                {
                    hostObject = ExcelComWorkbookSession.StartOwnedForGeneration(
                        terminationController,
                        CancellationToken.None);
                    var host = (ExcelComWorkbookSession.ExcelComHostObjects)hostObject;
                    dynamic excel = host.ExcelObject;
                    excel.AutomationSecurity = 3;
                    excel.EnableEvents = false;
                    dynamic workbooks = host.WorkbooksObject;
                    workbookObject = workbooks.Open(workbookPath, 0, false);
                    AddFixtureComponentsAndHandlers(workbookObject, sentinelPath);
                    worksheetStructuralEvents =
                        ReadWorksheetStructuralEventNames(workbookObject);
                    dynamic workbook = workbookObject;
                    workbook.Save();
                    return true;
                },
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            operationError = exception;
        }

        try
        {
            await dispatcher.InvokeAsync(
                () =>
                {
                    if (workbookObject is not null)
                    {
                        try
                        {
                            dynamic workbook = workbookObject;
                            workbook.Close(false);
                        }
                        finally
                        {
                            ComObjectReleaser.Release(workbookObject);
                            workbookObject = null;
                        }
                    }

                    if (hostObject is not null)
                    {
                        ExcelComWorkbookSession.DisposeOwnedGenerationHost(
                            (ExcelComWorkbookSession.ExcelComHostObjects)hostObject,
                            TimeSpan.FromSeconds(5));
                        hostObject = null;
                    }

                    return true;
                },
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            operationError = operationError is null
                ? exception
                : new AggregateException(operationError, exception);
        }

        terminationController.RequestForcedTermination(TimeSpan.FromSeconds(5));
        await terminationController.ObserveCleanupWithinAsync(TimeSpan.FromSeconds(1));
        await dispatcher.DisposeAsync();
        if (operationError is not null)
        {
            ExceptionDispatchInfo.Capture(operationError).Throw();
        }

        return worksheetStructuralEvents;
    }

    private static IReadOnlyList<string> ReadWorksheetStructuralEventNames(
        object workbookObject)
    {
        object? sheetsObject = null;
        object? sheetObject = null;
        try
        {
            dynamic workbook = workbookObject;
            sheetsObject = workbook.Sheets;
            dynamic sheets = sheetsObject;
            sheetObject = sheets.Item("Sheet1");
            if (!HostClassTypeLibEventSurfaceReader.TryRead(
                    sheetObject,
                    out var surface))
            {
                throw new InvalidOperationException(
                    "The fixture Worksheet TypeLib Event surface could not be read.");
            }

            return surface.Events.Keys
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            ComObjectReleaser.Release(sheetObject);
            ComObjectReleaser.Release(sheetsObject);
        }
    }

    private static void AddFixtureComponentsAndHandlers(
        object workbookObject,
        string sentinelPath)
    {
        object? projectObject = null;
        object? componentsObject = null;
        object? formObject = null;
        object? formCodeObject = null;
        object? workbookComponentObject = null;
        object? workbookCodeObject = null;
        object? sheetComponentObject = null;
        object? sheetCodeObject = null;
        try
        {
            dynamic workbook = workbookObject;
            projectObject = workbook.VBProject;
            dynamic project = projectObject;
            componentsObject = project.VBComponents;
            dynamic components = componentsObject;
            formObject = components.Add(3);
            dynamic form = formObject;
            form.Name = "HostForm";
            formCodeObject = form.CodeModule;
            dynamic formCode = formCodeObject;
            _ = (int)formCode.CreateEventProc("Initialize", "UserForm");
            _ = (int)formCode.CreateEventProc("QueryClose", "UserForm");

            workbookComponentObject = components.Item("ThisWorkbook");
            dynamic workbookComponent = workbookComponentObject;
            workbookCodeObject = workbookComponent.CodeModule;
            dynamic workbookCode = workbookCodeObject;
            workbookCode.InsertLines(
                1,
                "Private WithEvents AppEvents As Excel.Application");
            _ = (int)workbookCode.CreateEventProc("BeforeClose", "Workbook");
            var openBodyLine = (int)workbookCode.CreateEventProc("Open", "Workbook");
            workbookCode.InsertLines(
                openBodyLine,
                $"Open \"{sentinelPath.Replace("\"", "\"\"")}\" For Output As #1: Close #1");

            sheetComponentObject = components.Item("Sheet1");
            dynamic sheetComponent = sheetComponentObject;
            sheetCodeObject = sheetComponent.CodeModule;
            dynamic sheetCode = sheetCodeObject;
            _ = (int)sheetCode.CreateEventProc("Change", "Worksheet");
        }
        finally
        {
            ComObjectReleaser.Release(sheetCodeObject);
            ComObjectReleaser.Release(sheetComponentObject);
            ComObjectReleaser.Release(workbookCodeObject);
            ComObjectReleaser.Release(workbookComponentObject);
            ComObjectReleaser.Release(formCodeObject);
            ComObjectReleaser.Release(formObject);
            ComObjectReleaser.Release(componentsObject);
            ComObjectReleaser.Release(projectObject);
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

    private static void WriteEntry(
        ZipArchive archive,
        string name,
        string content)
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

    private static IReadOnlySet<string> CaptureHostClassWorkspaces()
    {
        var root = Path.Combine(Path.GetTempPath(), "vba-dev-host-class-inspection");
        return Directory.Exists(root)
            ? Directory.EnumerateDirectories(root)
                .Select(Path.GetFullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
    }
}
