using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VbaDev.App.Projects;
using VbaDev.App.Testing;
using VbaDev.Composition;
using Xunit;

namespace VbaDev.Tests;

[Collection(WindowsExcelIntegrationCollection.Name)]
public sealed class CallableResultIndexWindowsExcelIntegrationTests
{
    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task TestBuildAdmitsEarlyBoundDictionaryKeysIndexAndRetrievesTheNativeKey()
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var initialProcesses = CaptureExcelProcessIds();

        try
        {
            var projectRoot = Path.Combine(temp.Path, "ResultIndexProject");
            var composition = ToolingCompositionRoot.CreateApplicationComposition(temp.Path);
            var created = await composition.NewProjectCommand.RunAsync(
                new NewProjectCommandRequest(
                    "ResultIndexProject", null, projectRoot, temp.Path,
                    ProjectNameSpecified: true, OutputDirectorySpecified: true),
                cancellation.Token);
            Assert.True(created.ExitCode == 0, created.StandardError);
            var resolution = new ProjectResolutionRequest(projectRoot, null, temp.Path);
            var context = composition.ProjectContextResolver.Resolve(resolution);
            var added = await composition.ReferenceService.AddAsync(
                context, ["Microsoft Scripting Runtime"], "json", cancellation.Token);
            Assert.True(added.ExitCode == 0, added.StandardError);
            context = composition.ProjectContextResolver.Resolve(resolution);
            Assert.Contains(context.Document.References, reference =>
                reference.Name == "Microsoft Scripting Runtime" && reference.Requested);

            WriteSource(Path.Combine(context.DocumentSourceSetPath, "ResultIndexTests.bas"), """
                Attribute VB_Name = "ResultIndexTests"
                Option Explicit

                Public Sub UnitTestMain()
                    Dim Values As Scripting.Dictionary
                    Set Values = New Scripting.Dictionary
                    Values.Add "alpha", 1
                    Dim Actual As String
                    Actual = CStr(Values.Keys(0))
                    Dim ResultSheet As Object
                    Set ResultSheet = ThisWorkbook.Worksheets(1)
                    ResultSheet.Name = "UNIT_TEST_SHEET"
                    ResultSheet.Cells(1, 1).Value2 = "Module"
                    ResultSheet.Cells(2, 1).Value2 = "ResultIndexTests"
                    ResultSheet.Cells(2, 2).Value2 = "UnitTestMain"
                    If Actual = "alpha" Then
                        ResultSheet.Cells(2, 3).Value2 = "OK"
                    Else
                        ResultSheet.Cells(2, 3).Value2 = "NG"
                    End If
                    ResultSheet.Cells(2, 4).Value2 = Actual
                End Sub
                """);

            var result = await composition.TestCommand.RunAsync(
                context,
                new TestCommandRequest("ndjson", true, new(), TimeSpan.FromMinutes(1)),
                cancellation.Token);

            Assert.True(result.ExitCode == 0, result.StandardError);
            Assert.True(File.Exists(context.BinDocumentPath));
            using var finished = JsonDocument.Parse(Assert.Single(
                result.StandardOutput.Split('\n'),
                line => line.Contains("\"type\":\"testFinished\"", StringComparison.Ordinal)));
            Assert.Equal("passed", finished.RootElement.GetProperty("outcome").GetString());
            Assert.Equal("alpha", finished.RootElement.GetProperty("message").GetString());
        }
        finally
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (!initialProcesses.SetEquals(CaptureExcelProcessIds()) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }

            Assert.True(initialProcesses.SetEquals(CaptureExcelProcessIds()),
                "The original Excel process inventory must be restored after owned automation completes.");
        }
    }

    private static void WriteSource(string path, string source)
        => File.WriteAllText(path, source.ReplaceLineEndings("\r\n") + "\r\n", new UTF8Encoding(false));

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
}
