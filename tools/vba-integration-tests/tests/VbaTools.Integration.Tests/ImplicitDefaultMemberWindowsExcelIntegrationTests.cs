using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Xunit;

namespace VbaTools.Integration.Tests;

[Collection(WindowsExcelIntegrationCollection.Name)]
public sealed class ImplicitDefaultMemberWindowsExcelIntegrationTests
{
    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task PublishedVbaDevBuildsWorkbookWithImplicitWorkbooksDefaultMember()
    {
        var vbaDevPath = PrebuiltTools.VbaDevPath();
        using var temp = TempDirectory.Create();
        var sourceDirectory = Directory.CreateDirectory(
            Path.Combine(temp.Path, "src")).FullName;
        var templatePath = Path.Combine(temp.Path, "Template.xlsm");
        var outputPath = Path.Combine(temp.Path, "bin", "DefaultMember.xlsm");
        var initialProcesses = CaptureExcelProcessIds();
        CreateEmptyMacroEnabledWorkbook(templatePath);
        File.WriteAllText(
            Path.Combine(sourceDirectory, "DefaultMemberReceiver.bas"),
            """
            Attribute VB_Name = "DefaultMemberReceiver"
            Option Explicit

            Public Sub CloseWorkbook(ByVal bookName As String)
                Call Workbooks(bookName).Close(SaveChanges:=False)
            End Sub
            """ + "\n",
            new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(temp.Path, "vba-project.json"),
            """
            {
              "schemaVersion": 1,
              "projectName": "DefaultMember",
              "primaryDocument": "DefaultMember",
              "documents": {
                "DefaultMember": {
                  "kind": "excel",
                  "sourcePath": "src",
                  "templatePath": "Template.xlsm",
                  "binPath": "bin/DefaultMember.xlsm",
                  "publishPath": "publish/DefaultMember.xlsm",
                  "commonModules": [],
                  "references": [
                    {
                      "name": "Microsoft Excel 16.0 Object Library",
                      "requested": true
                    }
                  ]
                }
              }
            }
            """ + "\n",
            new UTF8Encoding(false));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        try
        {
            await PrebuiltTools.RunVbaDevAsync(
                vbaDevPath,
                ["build", "--project", temp.Path, "--document", "DefaultMember"],
                cancellation.Token);

            Assert.True(File.Exists(outputPath));
            Assert.NotEqual(0, new FileInfo(outputPath).Length);
        }
        finally
        {
            await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
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
                processIds.Add(process.Id);
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

        Assert.Equal(
            expected.Order().ToArray(),
            CaptureExcelProcessIds().Order().ToArray());
    }
}
