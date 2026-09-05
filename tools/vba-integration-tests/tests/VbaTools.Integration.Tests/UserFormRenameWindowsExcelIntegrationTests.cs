using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VbaDev.Infrastructure.Debugging;
using VbaDev.Infrastructure.Workbooks;
using Xunit;

namespace VbaTools.Integration.Tests;

[Collection(WindowsExcelIntegrationCollection.Name)]
public sealed class UserFormRenameWindowsExcelIntegrationTests
{
    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task RenamedUserFormSourceUnitBuildsAndRoundTripsThroughExcel()
    {
        var languageServerPath = PrebuiltTools.LanguageServerPath();
        var vbaDevPath = PrebuiltTools.VbaDevPath();
        using var temp = TempDirectory.Create();
        var sourceDirectory = Directory.CreateDirectory(Path.Combine(temp.Path, "src")).FullName;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var initialProcesses = CaptureExcelProcessIds();
        var activeCodePage = checked((int)GetACP());
        var nonAsciiText = SelectNonAsciiFixtureText(activeCodePage);
        var formSourcePath = Path.Combine(sourceDirectory, "Dialog.frm");
        var formSidecarPath = Path.Combine(sourceDirectory, "Dialog.frx");
        var seedWorkbookPath = Path.Combine(temp.Path, "FormSeed.xlsm");
        CreateEmptyMacroEnabledWorkbook(seedWorkbookPath);
        ExportNestedUserFormFixture(
            seedWorkbookPath,
            formSourcePath,
            nonAsciiText);
        var originalSidecarBytes = File.ReadAllBytes(formSidecarPath);
        Assert.NotEmpty(originalSidecarBytes);

        var renamedFormSourcePath = Path.Combine(sourceDirectory, "DialogView.frm");
        var renamedFormSidecarPath = Path.Combine(sourceDirectory, "DialogView.frx");
        await ApplyProductionFormSourceUnitRenameAsync(
            languageServerPath,
            formSourcePath,
            formSidecarPath,
            renamedFormSourcePath,
            renamedFormSidecarPath,
            activeCodePage,
            "Dialog",
            "DialogView",
            cancellation.Token);
        Assert.False(File.Exists(formSourcePath));
        Assert.False(File.Exists(formSidecarPath));
        Assert.Equal(
            originalSidecarBytes,
            File.ReadAllBytes(renamedFormSidecarPath));

        var templatePath = Path.Combine(temp.Path, "Template.xlsm");
        var targetPath = Path.Combine(temp.Path, "bin", "RenamedForm.xlsm");
        CreateEmptyMacroEnabledWorkbook(templatePath);
        File.WriteAllText(
            Path.Combine(temp.Path, "vba-project.json"),
            """
            {
              "schemaVersion": 1,
              "projectName": "RenamedForm",
              "primaryDocument": "RenamedForm",
              "documents": {
                "RenamedForm": {
                  "kind": "excel",
                  "sourcePath": "src",
                  "templatePath": "Template.xlsm",
                  "binPath": "bin/RenamedForm.xlsm",
                  "publishPath": "publish/RenamedForm.xlsm",
                  "commonModules": [],
                  "references": []
                }
              }
            }
            """ + "\n",
            new UTF8Encoding(false));
        await PrebuiltTools.RunVbaDevAsync(
            vbaDevPath,
            ["build", "--project", temp.Path, "--document", "RenamedForm"],
            cancellation.Token);

        await UseOwnedExcelWorkbookAsync(
            targetPath,
            session =>
            {
                object? projectObject = null;
                object? componentsObject = null;
                object? formComponentObject = null;
                object? codeModuleObject = null;
                try
                {
                    dynamic workbook = session.WorkbookObject;
                    projectObject = workbook.VBProject;
                    dynamic project = projectObject;
                    componentsObject = project.VBComponents;
                    AssertNestedFormState(
                        componentsObject,
                        nonAsciiText,
                        "DialogView");
                    dynamic components = componentsObject;
                    formComponentObject = components.Item("DialogView");
                    dynamic formComponent = formComponentObject;
                    codeModuleObject = formComponent.CodeModule;
                    dynamic codeModule = codeModuleObject;
                    var code = Convert.ToString(codeModule.Lines(
                        1,
                        Math.Max(1, (int)codeModule.CountOfLines)))
                        ?? string.Empty;
                    Assert.Contains(
                        "Private Sub UserForm_Initialize()",
                        code,
                        StringComparison.Ordinal);
                    return true;
                }
                finally
                {
                    ComObjectReleaser.Release(codeModuleObject);
                    ComObjectReleaser.Release(formComponentObject);
                    ComObjectReleaser.Release(componentsObject);
                    ComObjectReleaser.Release(projectObject);
                }
            },
            cancellation.Token);

        var exportRoot = Path.Combine(temp.Path, "re-export");
        Directory.CreateDirectory(exportRoot);
        await PrebuiltTools.RunVbaDevAsync(
            vbaDevPath,
            ["export", "--from", targetPath, "--to", exportRoot],
            cancellation.Token);
        var exportedFormPath = Path.Combine(exportRoot, "DialogView.frm");
        var exportedSidecarPath = Path.Combine(exportRoot, "DialogView.frx");
        Assert.False(File.Exists(Path.Combine(exportRoot, "Dialog.frm")));
        Assert.False(File.Exists(Path.Combine(exportRoot, "Dialog.frx")));
        Assert.True(File.Exists(exportedFormPath));
        Assert.True(File.Exists(exportedSidecarPath));
        Assert.NotEmpty(File.ReadAllBytes(exportedSidecarPath));
        var exportedText = DecodeActiveCodePageFile(
            exportedFormPath,
            activeCodePage);
        Assert.Single(Regex.Matches(
            exportedText,
            "^Begin[ \\t]+\\S+[ \\t]+DialogView(?=[ \\t]*\\r?$)",
            RegexOptions.Multiline | RegexOptions.CultureInvariant));
        Assert.Contains(
            "Attribute VB_Name = \"DialogView\"",
            exportedText,
            StringComparison.Ordinal);
        Assert.Matches(
            new Regex(
                "\"DialogView\\.frx\"[ \\t]*:[ \\t]*[0-9A-Fa-f]+",
                RegexOptions.CultureInvariant),
            exportedText);
        Assert.DoesNotMatch(
            new Regex(
                "\"Dialog\\.frx\"[ \\t]*:",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            exportedText);
        Assert.Contains(
            "Private Sub UserForm_Initialize()",
            exportedText,
            StringComparison.Ordinal);
        await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
    }

    private static async Task<T> UseOwnedExcelWorkbookAsync<T>(
        string workbookPath,
        Func<ExcelComWorkbookSession, T> action,
        CancellationToken cancellationToken)
    {
        var cleanupGrace = TimeSpan.FromSeconds(5);
        using var terminationController = new OwnedExcelTerminationController();
        await using var dispatcher = new StaComDispatcher();
        try
        {
            return await dispatcher.InvokeAsync(
                () =>
                {
                    var host = ExcelComWorkbookSession.StartOwnedForGeneration(
                        terminationController,
                        cancellationToken);
                    ExcelComWorkbookSession? session = null;
                    try
                    {
                        session = ExcelComWorkbookSession.OpenOwnedForGeneration(
                            host,
                            workbookPath);
                        return action(session);
                    }
                    finally
                    {
                        if (session is not null)
                        {
                            session.DisposeOwnedGeneration(cleanupGrace);
                        }
                        else
                        {
                            ExcelComWorkbookSession.DisposeOwnedGenerationHost(
                                host,
                                cleanupGrace);
                        }
                    }
                },
                cancellationToken);
        }
        finally
        {
            await terminationController.RequestCleanupAsync(cleanupGrace);
        }
    }

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

    private static string ExportNestedUserFormFixture(
        string workbookPath,
        string formSourcePath,
        string nonAsciiText)
    {
        object? excelObject = null;
        object? workbooksObject = null;
        object? workbookObject = null;
        object? projectObject = null;
        object? componentsObject = null;
        object? formComponentObject = null;
        object? codeModuleObject = null;
        object? designerObject = null;
        object? controlsObject = null;
        object? frameObject = null;
        object? frameControlsObject = null;
        object? labelObject = null;
        object? textBoxObject = null;
        try
        {
            excelObject = CreateHiddenExcelApplication();
            dynamic excel = excelObject;
            var excelVersion = Convert.ToString(excel.Version) ?? string.Empty;
            workbooksObject = excel.Workbooks;
            dynamic workbooks = workbooksObject;
            workbookObject = workbooks.Open(workbookPath);
            dynamic workbook = workbookObject;
            projectObject = workbook.VBProject;
            dynamic project = projectObject;
            componentsObject = project.VBComponents;
            dynamic components = componentsObject;
            formComponentObject = components.Add(3);
            dynamic formComponent = formComponentObject;
            formComponent.Name = "Dialog";
            codeModuleObject = formComponent.CodeModule;
            dynamic codeModule = codeModuleObject;
            codeModule.AddFromString(
                "Option Explicit\r\nPrivate Sub UserForm_Initialize()\r\nEnd Sub\r\n");
            designerObject = formComponent.Designer;
            dynamic designer = designerObject;
            designer.Caption = $"Dialog {nonAsciiText}";
            controlsObject = designer.Controls;
            dynamic controls = controlsObject;
            frameObject = controls.Add("Forms.Frame.1", "FrameMain", true);
            dynamic frame = frameObject;
            frame.Caption = $"Frame {nonAsciiText}";
            frame.Left = 12;
            frame.Top = 12;
            frame.Width = 180;
            frame.Height = 96;
            frameControlsObject = frame.Controls;
            dynamic frameControls = frameControlsObject;
            labelObject = frameControls.Add("Forms.Label.1", "LabelMessage", true);
            dynamic label = labelObject;
            label.Caption = $"Label {nonAsciiText}";
            label.Left = 6;
            label.Top = 12;
            textBoxObject = frameControls.Add("Forms.TextBox.1", "InputText", true);
            dynamic textBox = textBoxObject;
            textBox.Left = 6;
            textBox.Top = 36;
            textBox.Width = 144;
            textBox.Height = 36;
            textBox.MultiLine = true;
            textBox.Value = $"{nonAsciiText}\r\nsidecar-value";
            formComponent.Export(formSourcePath);
            workbook.Close(false);
            ComObjectReleaser.Release(workbookObject);
            workbookObject = null;
            return excelVersion;
        }
        finally
        {
            ComObjectReleaser.Release(textBoxObject);
            ComObjectReleaser.Release(labelObject);
            ComObjectReleaser.Release(frameControlsObject);
            ComObjectReleaser.Release(frameObject);
            ComObjectReleaser.Release(controlsObject);
            ComObjectReleaser.Release(designerObject);
            ComObjectReleaser.Release(codeModuleObject);
            ComObjectReleaser.Release(formComponentObject);
            ComObjectReleaser.Release(componentsObject);
            ComObjectReleaser.Release(projectObject);
            if (workbookObject is not null)
            {
                try
                {
                    dynamic workbook = workbookObject;
                    workbook.Close(false);
                }
                catch
                {
                }
            }

            ComObjectReleaser.Release(workbookObject);
            ComObjectReleaser.Release(workbooksObject);
            QuitExcel(excelObject);
        }
    }

    private static void AssertNestedFormState(
        object componentsObject,
        string nonAsciiText,
        string componentName = "Dialog")
    {
        object? formComponentObject = null;
        object? designerObject = null;
        object? controlsObject = null;
        object? frameObject = null;
        object? frameControlsObject = null;
        object? labelObject = null;
        object? textBoxObject = null;
        object? labelParentObject = null;
        object? textBoxParentObject = null;
        try
        {
            dynamic components = componentsObject;
            formComponentObject = components.Item(componentName);
            dynamic formComponent = formComponentObject;
            Assert.Equal(3, (int)formComponent.Type);
            designerObject = formComponent.Designer;
            dynamic designer = designerObject;
            Assert.Equal($"Dialog {nonAsciiText}", Convert.ToString(designer.Caption));
            controlsObject = designer.Controls;
            dynamic controls = controlsObject;
            frameObject = controls.Item("FrameMain");
            dynamic frame = frameObject;
            Assert.Contains(
                "Frame",
                Microsoft.VisualBasic.Information.TypeName(frameObject),
                StringComparison.OrdinalIgnoreCase);
            frameControlsObject = frame.Controls;
            dynamic frameControls = frameControlsObject;
            labelObject = frameControls.Item("LabelMessage");
            textBoxObject = frameControls.Item("InputText");
            dynamic label = labelObject;
            dynamic textBox = textBoxObject;
            Assert.Contains(
                "Label",
                Microsoft.VisualBasic.Information.TypeName(labelObject),
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                Microsoft.VisualBasic.Information.TypeName(textBoxObject),
                new[] { "TextBox", "IMdcText" },
                StringComparer.OrdinalIgnoreCase);
            Assert.Equal($"Label {nonAsciiText}", Convert.ToString(label.Caption));
            Assert.True(Convert.ToBoolean(textBox.MultiLine));
            Assert.Equal($"{nonAsciiText}\r\nsidecar-value", Convert.ToString(textBox.Value));
            labelParentObject = label.Parent;
            textBoxParentObject = textBox.Parent;
            dynamic labelParent = labelParentObject;
            dynamic textBoxParent = textBoxParentObject;
            Assert.Equal("FrameMain", Convert.ToString(labelParent.Name));
            Assert.Equal("FrameMain", Convert.ToString(textBoxParent.Name));
        }
        finally
        {
            ComObjectReleaser.Release(textBoxParentObject);
            ComObjectReleaser.Release(labelParentObject);
            ComObjectReleaser.Release(textBoxObject);
            ComObjectReleaser.Release(labelObject);
            ComObjectReleaser.Release(frameControlsObject);
            ComObjectReleaser.Release(frameObject);
            ComObjectReleaser.Release(controlsObject);
            ComObjectReleaser.Release(designerObject);
            ComObjectReleaser.Release(formComponentObject);
        }
    }

    private static string DecodeActiveCodePageFile(string path, int activeCodePage)
        => StrictEncoding(activeCodePage).GetString(File.ReadAllBytes(path));

    private static async Task ApplyProductionFormSourceUnitRenameAsync(
        string languageServerPath,
        string sourcePath,
        string sidecarPath,
        string destinationPath,
        string sidecarDestinationPath,
        int activeCodePage,
        string oldName,
        string newName,
        CancellationToken cancellationToken)
    {
        var encoding = StrictEncoding(activeCodePage);
        var source = encoding.GetString(File.ReadAllBytes(sourcePath));
        var attribute = $"Attribute VB_Name = \"{oldName}\"";
        var uri = new Uri(sourcePath).AbsoluteUri;
        var attributeOffset = source.IndexOf(attribute, StringComparison.Ordinal);
        Assert.True(attributeOffset >= 0);
        var attributeLine = source[..attributeOffset].Count(character => character == '\n');
        await using var server = LanguageServerProcess.Start(languageServerPath);
        await server.InitializeAsync(
            new
            {
                workspace = new
                {
                    workspaceEdit = new
                    {
                        documentChanges = true,
                        resourceOperations = new[] { "rename" }
                    }
                }
            },
            cancellationToken: cancellationToken);
        await server.SendNotificationAsync(
            "textDocument/didOpen",
            new
            {
                textDocument = new
                {
                    uri,
                    languageId = "vba",
                    version = 1,
                    text = source
                }
            },
            cancellationToken);
        var rename = await server.SendRequestAsync(
            2,
            "textDocument/rename",
            new
            {
                textDocument = new { uri },
                position = new
                {
                    line = attributeLine,
                    character = "Attribute VB_Name = \"".Length
                },
                newName
            },
            timeout: TimeSpan.FromMinutes(1),
            cancellationToken: cancellationToken);
        Assert.False(
            rename.TryGetProperty("error", out var renameError),
            renameError.ToString());
        var documentChanges = rename
            .GetProperty("result")
            .GetProperty("documentChanges")
            .EnumerateArray()
            .ToArray();
        var textDocumentChange = Assert.Single(
            documentChanges,
            change => change.TryGetProperty("textDocument", out _));
        Assert.Equal(
            uri,
            textDocumentChange
                .GetProperty("textDocument")
                .GetProperty("uri")
                .GetString());
        var renamedSource = ApplyTextEdits(
            source,
            textDocumentChange.GetProperty("edits"));
        Assert.Contains(
            $"Attribute VB_Name = \"{newName}\"",
            renamedSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            attribute,
            renamedSource,
            StringComparison.Ordinal);
        File.WriteAllBytes(sourcePath, encoding.GetBytes(renamedSource));

        var fileRenames = documentChanges.Where(change =>
            change.TryGetProperty("kind", out var kind)
            && kind.GetString() == "rename").ToArray();
        Assert.Equal(2, fileRenames.Length);
        Assert.Equal(
            sourcePath,
            new Uri(fileRenames[0].GetProperty("oldUri").GetString()!).LocalPath,
            ignoreCase: true);
        Assert.Equal(
            destinationPath,
            new Uri(fileRenames[0].GetProperty("newUri").GetString()!).LocalPath,
            ignoreCase: true);
        Assert.Equal(
            sidecarPath,
            new Uri(fileRenames[1].GetProperty("oldUri").GetString()!).LocalPath,
            ignoreCase: true);
        Assert.Equal(
            sidecarDestinationPath,
            new Uri(fileRenames[1].GetProperty("newUri").GetString()!).LocalPath,
            ignoreCase: true);
        foreach (var fileRename in fileRenames)
        {
            File.Move(
                new Uri(fileRename.GetProperty("oldUri").GetString()!).LocalPath,
                new Uri(fileRename.GetProperty("newUri").GetString()!).LocalPath,
                fileRename
                    .GetProperty("options")
                    .GetProperty("overwrite")
                    .GetBoolean());
        }

        await server.ShutdownAsync(3, cancellationToken);
    }

    private static string ApplyTextEdits(
        string source,
        JsonElement edits)
    {
        var lineStarts = GetLineStartOffsets(source);
        var result = source;
        foreach (var edit in edits
                     .EnumerateArray()
                     .OrderByDescending(edit => edit
                         .GetProperty("range")
                         .GetProperty("start")
                         .GetProperty("line")
                         .GetInt32())
                     .ThenByDescending(edit => edit
                         .GetProperty("range")
                         .GetProperty("start")
                         .GetProperty("character")
                         .GetInt32()))
        {
            var range = edit.GetProperty("range");
            var startPosition = range.GetProperty("start");
            var endPosition = range.GetProperty("end");
            var start = lineStarts[startPosition.GetProperty("line").GetInt32()]
                + startPosition.GetProperty("character").GetInt32();
            var end = lineStarts[endPosition.GetProperty("line").GetInt32()]
                + endPosition.GetProperty("character").GetInt32();
            Assert.InRange(start, 0, source.Length);
            Assert.InRange(end, start, source.Length);
            result = result[..start]
                + edit.GetProperty("newText").GetString()
                + result[end..];
        }

        return result;
    }

    private static IReadOnlyList<int> GetLineStartOffsets(string source)
    {
        var result = new List<int> { 0 };
        for (var index = 0; index < source.Length; index++)
        {
            if (source[index] == '\r'
                && index + 1 < source.Length
                && source[index + 1] == '\n')
            {
                result.Add(++index + 1);
            }
            else if (source[index] is '\r' or '\n')
            {
                result.Add(index + 1);
            }
        }

        return result;
    }

    private static string SelectNonAsciiFixtureText(int activeCodePage)
    {
        var encoding = StrictEncoding(activeCodePage);
        foreach (var candidate in new[] { "日本語", "café", "δοκιμή", "тест" })
        {
            try
            {
                var bytes = encoding.GetBytes(candidate);
                if (bytes.Any(value => value >= 0x80) &&
                    encoding.GetString(bytes).Equals(candidate, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }
            catch (EncoderFallbackException)
            {
            }
        }

        throw new InvalidOperationException(
            $"Active Windows code page {activeCodePage} cannot represent the integration fixture's non-ASCII text.");
    }

    private static Encoding StrictEncoding(int codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return codePage == 65001
            ? new UTF8Encoding(false, true)
            : Encoding.GetEncoding(
                codePage,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
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

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();
}
