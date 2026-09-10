using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VbaDev.App.Projects;
using VbaDev.App.Testing;
using VbaDev.Composition;
using Xunit;

namespace VbaDev.Tests;

[Collection(WindowsExcelIntegrationCollection.Name)]
public sealed class PropertyReceiverContextWindowsExcelIntegrationTests
{
    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task TestBuildAdmitsPropertyReceiversAndActivatesTheNativeTargetRanges()
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var initialProcesses = CaptureExcelProcessIds();

        try
        {
            var projectRoot = Path.Combine(temp.Path, "PropertyReceiverProject");
            var composition = ToolingCompositionRoot.CreateApplicationComposition(temp.Path);
            var created = await composition.NewProjectCommand.RunAsync(
                new NewProjectCommandRequest(
                    "PropertyReceiverProject", null, projectRoot, temp.Path,
                    ProjectNameSpecified: true, OutputDirectorySpecified: true),
                cancellation.Token);
            Assert.True(created.ExitCode == 0, created.StandardError);
            var context = composition.ProjectContextResolver.Resolve(
                new ProjectResolutionRequest(projectRoot, null, temp.Path));

            WriteSource(Path.Combine(context.DocumentSourceSetPath, "PropertyReceiverTests.bas"), """
                Attribute VB_Name = "PropertyReceiverTests"
                Option Explicit

                Public Sub UnitTestMain()
                    Dim Target As Excel.Worksheet
                    Set Target = ThisWorkbook.Worksheets(1)
                    ThisWorkbook.Activate
                    Target.Activate
                    Dim ImplicitAddress As String
                    Target.Range("C10").Activate
                    ImplicitAddress = Application.ActiveCell.Address
                    Dim ExplicitAddress As String
                    Call Target.Range("C11").Activate
                    ExplicitAddress = Application.ActiveCell.Address
                    Dim ChainedAddress As String
                    Target.Range("C10").Offset(1, 1).Activate
                    ChainedAddress = Application.ActiveCell.Address
                    Dim Actual As String
                    Actual = ImplicitAddress & "|" & ExplicitAddress & "|" & ChainedAddress
                    Dim ResultSheet As Object
                    Set ResultSheet = Target
                    ResultSheet.Name = "UNIT_TEST_SHEET"
                    ResultSheet.Cells(1, 1).Value2 = "Module"
                    ResultSheet.Cells(2, 1).Value2 = "PropertyReceiverTests"
                    ResultSheet.Cells(2, 2).Value2 = "UnitTestMain"
                    If Actual = "$C$10|$C$11|$D$11" Then
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
            Assert.Equal("$C$10|$C$11|$D$11", finished.RootElement.GetProperty("message").GetString());
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
