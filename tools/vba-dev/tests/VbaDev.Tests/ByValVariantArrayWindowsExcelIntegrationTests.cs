using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VbaDev.App.Projects;
using VbaDev.App.Testing;
using VbaDev.Composition;
using Xunit;

namespace VbaDev.Tests;

[Collection(WindowsExcelIntegrationCollection.Name)]
public sealed class ByValVariantArrayWindowsExcelIntegrationTests
{
    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task TestBuildAdmitsWholeArraysAsByValVariantAndPreservesTheirNativePayloads()
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var initialProcesses = CaptureExcelProcessIds();

        try
        {
            var projectRoot = Path.Combine(temp.Path, "ArrayValueProject");
            var composition = ToolingCompositionRoot.CreateApplicationComposition(temp.Path);
            var created = await composition.NewProjectCommand.RunAsync(
                new NewProjectCommandRequest(
                    "ArrayValueProject", null, projectRoot, temp.Path,
                    ProjectNameSpecified: true, OutputDirectorySpecified: true),
                cancellation.Token);
            Assert.True(created.ExitCode == 0, created.StandardError);
            var context = composition.ProjectContextResolver.Resolve(
                new ProjectResolutionRequest(projectRoot, null, temp.Path));

            WriteSource(Path.Combine(context.DocumentSourceSetPath, "ArrayValueTests.bas"), """
                Attribute VB_Name = "ArrayValueTests"
                Option Explicit

                Private Function Describe(ByVal Values As Variant) As String
                    Describe = CStr(LBound(Values)) & ":" & CStr(UBound(Values))
                    Describe = Describe & ":" & CStr(Values(0)) & "," & CStr(Values(1))
                    Describe = Describe & ":" & CStr(VarType(Values(0))) & "," & CStr(VarType(Values(1)))
                End Function

                Private Function Forward(ParamArray Values() As Variant) As String
                    Forward = Describe(Values)
                End Function

                Public Sub UnitTestMain()
                    Dim Typed(0 To 1) As Long
                    Typed(0) = 42
                    Typed(1) = -7
                    Dim Mixed(0 To 1) As Variant
                    Mixed(0) = "alpha"
                    Mixed(1) = 17&
                    Dim Actual As String
                    Actual = Describe(Typed) & "|" & Describe(Mixed) & "|" & Forward(9&, "omega")
                    Dim ResultSheet As Object
                    Set ResultSheet = ThisWorkbook.Worksheets(1)
                    ResultSheet.Name = "UNIT_TEST_SHEET"
                    ResultSheet.Cells(1, 1).Value2 = "Module"
                    ResultSheet.Cells(2, 1).Value2 = "ArrayValueTests"
                    ResultSheet.Cells(2, 2).Value2 = "UnitTestMain"
                    If Actual = "0:1:42,-7:3,3|0:1:alpha,17:8,3|0:1:9,omega:3,8" Then
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
            Assert.Equal("0:1:42,-7:3,3|0:1:alpha,17:8,3|0:1:9,omega:3,8",
                finished.RootElement.GetProperty("message").GetString());
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
