using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using VbaDev.Infrastructure.References;
using VbaTools.Semantics;
using Xunit;

namespace VbaTools.Integration.Tests;

[Collection(WindowsExcelIntegrationCollection.Name)]
public sealed class CallByNameParamArrayWindowsExcelIntegrationTests
{
    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task PublishedVbaDevBuildsWorkbookWithReportedThreeArgumentCallByNameForms()
    {
        var installedCatalog = ReadInstalledVbe7Catalog();
        AssertProjectReferenceCallByNameTarget(installedCatalog);

        var vbaDevPath = PrebuiltTools.VbaDevPath();
        using var temp = TempDirectory.Create();
        var sourceDirectory = Directory.CreateDirectory(
            Path.Combine(temp.Path, "src")).FullName;
        var templatePath = Path.Combine(temp.Path, "Template.xlsm");
        var outputPath = Path.Combine(temp.Path, "bin", "CallByNameParamArray.xlsm");
        var initialProcesses = CaptureExcelProcessIds();
        CreateEmptyMacroEnabledWorkbook(templatePath);
        File.WriteAllText(
            Path.Combine(sourceDirectory, "CallByNameCaller.bas"),
            """
            Attribute VB_Name = "CallByNameCaller"
            Option Explicit

            Public Sub Exercise(ByVal target As Object)
                Dim value As Variant

                value = CallByName(target, "ValueExpression", VbGet)
                value = CallByName(target, "ValueExpression", VbGet)
                value = CallByName(target, "OutputValueType", VbGet)
                value = CallByName(target, "OutputValueType", VbGet)
                value = CallByName(target, "OutputValueType", VbGet)
                value = CallByName(target, "OutputValueType", VbGet)
                CallByName target, "QuitSession", VbMethod
            End Sub
            """ + "\n",
            new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(temp.Path, "vba-project.json"),
            """
            {
              "schemaVersion": 1,
              "projectName": "CallByNameParamArray",
              "primaryDocument": "CallByNameParamArray",
              "documents": {
                "CallByNameParamArray": {
                  "kind": "excel",
                  "sourcePath": "src",
                  "templatePath": "Template.xlsm",
                  "binPath": "bin/CallByNameParamArray.xlsm",
                  "publishPath": "publish/CallByNameParamArray.xlsm",
                  "commonModules": [],
                  "references": []
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
                ["build", "--project", temp.Path, "--document", "CallByNameParamArray"],
                cancellation.Token);

            Assert.True(File.Exists(outputPath));
            Assert.NotEqual(0, new FileInfo(outputPath).Length);
        }
        finally
        {
            await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
        }
    }

    private static VbaProjectReferenceCatalog ReadInstalledVbe7Catalog()
    {
        var candidates = GetInstalledVbe7PathCandidates();
        var vbePath = candidates.FirstOrDefault(File.Exists);
        Assert.True(
            vbePath is not null,
            "An opted-in Windows Excel integration run requires an installed VBE7 TypeLib. "
            + "None of the authoritative Click-to-Run or common-files candidates exists: "
            + string.Join(", ", candidates));

        const string referenceName =
            VbaProjectReferenceCatalogSet.StandardLibraryReferenceName;
        var acquired = new ComTypeLibCatalogMetadataReader()
            .ReadMetadataFromPath(referenceName, vbePath!);

        Assert.Equal("000204ef-0000-0000-c000-000000000046", acquired.Identity.Guid);
        Assert.Equal(4, acquired.Identity.MajorVersion);
        Assert.Equal(2, acquired.Identity.MinorVersion);
        Assert.Equal("VBA", acquired.Metadata.QualifierAlias);
        Assert.Equal("VBA", acquired.Metadata.ReferencedVbaProjectName);

        var interaction = Assert.Single(
            acquired.Metadata.Types,
            type => type.Name == "Interaction");
        var rawCallByName = Assert.Single(
            interaction.Members,
            member => member.Name == "CallByName");
        AssertCallByNameParameters(rawCallByName.Signature);

        var catalog = TypeLibReferenceCatalogBuilder.Build(
            referenceName,
            acquired.Metadata);
        var projectedCallByName = Assert.Single(
            catalog.Definitions,
            definition => definition.Name == "CallByName"
                && definition.ParentTypeName == "Interaction");
        AssertCallByNameParameters(projectedCallByName.Signature);
        return catalog;
    }

    private static void AssertProjectReferenceCallByNameTarget(
        VbaProjectReferenceCatalog catalog)
    {
        const string uri = "file:///C:/work/CallByNameCaller.bas";
        var selection = VbaReferenceSelection.Capture(
            [catalog.ReferenceName],
            catalog.ReferenceName);
        var resolution = new VbaNameResolutionService(
            [new VbaSourceDocument(uri, string.Empty, "CallByNameCaller", [])],
            selection,
            VbaProjectReferenceCatalogSet.Empty.WithCatalog(catalog));

        var target = Assert.IsType<VbaSourceDefinition>(
            resolution.Resolve(
                uri,
                new VbaPosition(0, 0),
                qualifier: null,
                identifier: "CallByName"));
        Assert.Equal("CallByName", target.Name);
        Assert.Equal(VbaDefinitionOrigin.ProjectReference, target.Identity.Origin);
    }

    private static void AssertCallByNameParameters(VbaCallableSignature? signature)
    {
        var parameters = Assert.IsAssignableFrom<
            IReadOnlyList<VbaCallableParameter>>(signature?.Parameters);
        Assert.Equal(
            ["Object", "ProcName", "CallType", "Args"],
            parameters.Select(parameter => parameter.Name));
        Assert.True(parameters[^1].IsParamArray);
        Assert.True(parameters[^1].IsArray);
        Assert.False(parameters[^1].IsOptional);
        Assert.Equal("Variant", parameters[^1].TypeReference?.Name);
        Assert.DoesNotContain(
            parameters,
            parameter => parameter.Name.Equals("lcid", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> GetInstalledVbe7PathCandidates()
    {
        var programFiles = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFilesX86);
        var commonProgramFiles = Environment.GetFolderPath(
            Environment.SpecialFolder.CommonProgramFiles);
        var commonProgramFilesX86 = Environment.GetFolderPath(
            Environment.SpecialFolder.CommonProgramFilesX86);

        return new[]
            {
                CreateClickToRunVbe7Path(programFiles, "ProgramFilesCommonX64"),
                CreateClickToRunVbe7Path(programFilesX86, "ProgramFilesCommonX86"),
                CreateClickToRunVbe7Path(programFiles, "ProgramFilesCommonX86"),
                CreateClickToRunVbe7Path(programFilesX86, "ProgramFilesCommonX64"),
                CreateCommonFilesVbe7Path(commonProgramFiles),
                CreateCommonFilesVbe7Path(commonProgramFilesX86)
            }
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? CreateClickToRunVbe7Path(
        string installationRoot,
        string commonFilesRoot)
        => string.IsNullOrWhiteSpace(installationRoot)
            ? null
            : Path.Combine(
                installationRoot,
                "Microsoft Office",
                "root",
                "vfs",
                commonFilesRoot,
                "Microsoft Shared",
                "VBA",
                "VBA7.1",
                "VBE7.DLL");

    private static string? CreateCommonFilesVbe7Path(string commonFilesRoot)
        => string.IsNullOrWhiteSpace(commonFilesRoot)
            ? null
            : Path.Combine(
                commonFilesRoot,
                "Microsoft Shared",
                "VBA",
                "VBA7.1",
                "VBE7.DLL");

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
