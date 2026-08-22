using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using VbaDev.App.References;
using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Workbooks;
using Xunit;

namespace VbaDev.Tests;

[Collection(WindowsExcelIntegrationCollection.Name)]
public sealed class VbaProjectReferenceAmbiguityProbeWindowsExcelIntegrationTests
{
    private const string ScriptingGuid = "420b2830-e718-11cf-893d-00a0c9054228";
    private const string WindowsScriptHostGuid = "f935dc20-1cf0-11d0-adb9-00c04fd58a0b";

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task RealVbeCoversFallbackAmbiguityAndOwnedCleanupFromOneSelectedTemplate()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Template.xlsm");
        CreateEmptyMacroEnabledWorkbook(templatePath);
        var originalTemplate = File.ReadAllBytes(templatePath);
        var initialProcesses = CaptureExcelProcessIds();
        var initialProbeWorkspaces = CaptureProbeWorkspaces();
        var probe = new VbaProjectReferenceAmbiguityProbe(
            new ExcelComVbaProjectReferenceProbeAutomation());
        var registryResolution = CreateRegistryResolution();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        VbaProjectReferenceResolutionBatch result;
        try
        {
            result = await probe.ResolveAsync(
                templatePath,
                registryResolution,
                cancellation.Token);
        }
        finally
        {
            await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
        }

        Assert.True(result.Complete);
        Assert.Empty(result.Diagnostics);
        var selected = Assert.Single(result.References[0].Matches);
        Assert.Equal(ScriptingGuid, selected.Guid);
        Assert.Equal(1, selected.Major);
        Assert.Equal(0, selected.Minor);
        Assert.Equal(
            [
                (ScriptingGuid, 1, 0),
                (WindowsScriptHostGuid, 1, 0)
            ],
            result.References[1].Matches
                .Select(identity => (identity.Guid, identity.Major, identity.Minor)));
        Assert.Equal(originalTemplate, File.ReadAllBytes(templatePath));
        Assert.True(initialProbeWorkspaces.SetEquals(CaptureProbeWorkspaces()));
        Assert.True(initialProcesses.SetEquals(CaptureExcelProcessIds()));
    }

    private static VbaProjectReferenceResolutionBatch CreateRegistryResolution()
    {
        var unavailableGuid = "11111111-2222-3333-4444-555555555555";
        var scriptingHigh = new ResolvedVbaProjectReference(
            "Microsoft Scripting Runtime",
            ScriptingGuid,
            ushort.MaxValue,
            0);
        var scriptingInstalled = scriptingHigh with { Major = 1 };
        var unavailable = new ResolvedVbaProjectReference(
            "Microsoft Scripting Runtime",
            unavailableGuid,
            1,
            0);
        var scriptingAmbiguity = new ResolvedVbaProjectReference(
            "Synthetic Probe Ambiguity",
            ScriptingGuid,
            1,
            0);
        var windowsScriptHost = new ResolvedVbaProjectReference(
            "Synthetic Probe Ambiguity",
            WindowsScriptHostGuid,
            1,
            0);
        return new VbaProjectReferenceResolutionBatch(
            true,
            [],
            null,
            [
                new VbaProjectReferenceNameResolution(
                    "Microsoft Scripting Runtime",
                    "Microsoft Scripting Runtime",
                    true,
                    [scriptingHigh, unavailable],
                    [
                        new VbaProjectReferenceCandidateLineage(
                            ScriptingGuid,
                            [scriptingHigh, scriptingInstalled]),
                        new VbaProjectReferenceCandidateLineage(
                            unavailableGuid,
                            [unavailable])
                    ]),
                new VbaProjectReferenceNameResolution(
                    "Synthetic Probe Ambiguity",
                    "Synthetic Probe Ambiguity",
                    true,
                    [scriptingAmbiguity, windowsScriptHost])
            ]);
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

    private static IReadOnlySet<string> CaptureProbeWorkspaces()
    {
        var root = Path.Combine(Path.GetTempPath(), "vba-dev-reference-probe");
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

        Assert.Equal(
            expected.Order().ToArray(),
            CaptureExcelProcessIds().Order().ToArray());
    }
}
