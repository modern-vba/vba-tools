using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VbaDev.App.Projects;
using VbaDev.App.Testing;
using VbaDev.Composition;
using Xunit;

namespace VbaDev.Tests;

[Collection(WindowsExcelIntegrationCollection.Name)]
public sealed class CallableResultWindowsExcelIntegrationTests
{
    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task TestBuildAdmitsFunctionAndPropertyResultReadsAndExecutesThemInExcel()
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var initialProcesses = CaptureExcelProcessIds();

        try
        {
            var projectRoot = Path.Combine(temp.Path, "ResultReadProject");
            var composition = ToolingCompositionRoot.CreateApplicationComposition(temp.Path);
            var created = await composition.NewProjectCommand.RunAsync(
                new NewProjectCommandRequest(
                    "ResultReadProject", null, projectRoot, temp.Path,
                    ProjectNameSpecified: true, OutputDirectorySpecified: true),
                cancellation.Token);
            Assert.True(created.ExitCode == 0, created.StandardError);
            var context = composition.ProjectContextResolver.Resolve(
                new ProjectResolutionRequest(projectRoot, null, temp.Path));

            WriteSource(Path.Combine(context.DocumentSourceSetPath, "ResultReadTests.bas"), """
                Attribute VB_Name = "ResultReadTests"
                Option Explicit

                Public Function AppendSlash(ByVal Value As String) As String
                    AppendSlash = Value
                    If AppendSlash <> "" Then
                        AppendSlash = AppendSlash & "/"
                    End If
                End Function

                Public Sub UnitTestMain()
                    Dim Reader As ResultReader
                    Set Reader = New ResultReader
                    Dim Actual As String
                    Actual = AppendSlash("function") & "|" & Reader.Label("property")
                    Dim ResultSheet As Object
                    Set ResultSheet = ThisWorkbook.Worksheets(1)
                    ResultSheet.Name = "UNIT_TEST_SHEET"
                    ResultSheet.Cells(1, 1).Value2 = "Module"
                    ResultSheet.Cells(2, 1).Value2 = "ResultReadTests"
                    ResultSheet.Cells(2, 2).Value2 = "UnitTestMain"
                    If Actual = "function/|property/" Then
                        ResultSheet.Cells(2, 3).Value2 = "OK"
                    Else
                        ResultSheet.Cells(2, 3).Value2 = "NG"
                    End If
                    ResultSheet.Cells(2, 4).Value2 = Actual
                End Sub
                """);
            WriteSource(Path.Combine(context.DocumentSourceSetPath, "ResultReader.cls"), """
                VERSION 1.0 CLASS
                BEGIN
                  MultiUse = -1  'True
                END
                Attribute VB_Name = "ResultReader"
                Attribute VB_GlobalNameSpace = False
                Attribute VB_Creatable = False
                Attribute VB_PredeclaredId = False
                Attribute VB_Exposed = False
                Option Explicit

                Public Property Get Label(ByVal Value As String) As String
                    Label = Value
                    If Label <> "" Then
                        Label = Label & "/"
                    End If
                End Property
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
            Assert.Equal("function/|property/", finished.RootElement.GetProperty("message").GetString());
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
