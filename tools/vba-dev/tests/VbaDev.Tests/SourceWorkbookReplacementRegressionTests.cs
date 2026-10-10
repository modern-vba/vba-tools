using System.Text;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;
using VbaDev.Composition;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;
using Xunit;

namespace VbaDev.Tests;

public sealed class SourceWorkbookReplacementRegressionTests
{
    [Fact]
    public async Task SaveFailureBeforeNativeSaveRestoresBorrowedCodeFromTheRetainedCapture()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = Path.Combine(root, "src", "Book1");
        Directory.CreateDirectory(sourceSet);
        var sourceWorkbook = Path.Combine(sourceSet, "Book1.xlsm");
        File.WriteAllText(sourceWorkbook, "saved workbook", Encoding.UTF8);
        var savedBytes = File.ReadAllBytes(sourceWorkbook);
        File.WriteAllText(Path.Combine(sourceSet, "Local.bas"),
            "Attribute VB_Name = \"Local\"", Encoding.UTF8);
        var automation = new RecordingSourceWorkbookAutomation();
        automation.Session.WasAlreadyOpen = true;
        automation.Session.Modules.Add(new WorkbookModule("Existing", WorkbookModuleKind.StandardModule));
        automation.Session.SaveFailureState = SourceWorkbookSaveState.NotStarted;
        var composition = ToolingCompositionRoot.CreateApplicationComposition(
            root,
            sourceWorkbookAutomation: automation,
            projectSemanticInputProvider: FakeProjectSemanticInputProvider.Empty);

        var result = await composition.BuildCommand.RunAsync(
            new ProjectContextResolver(new JsonProjectManifestStore())
                .Resolve(new ProjectResolutionRequest(root, null, root)),
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Simulated Save failure", result.StandardError);
        Assert.Equal(SourceWorkbookSaveState.NotStarted, automation.Session.SaveState);
        Assert.Equal(new[] { "ThisWorkbook", "Existing" },
            automation.Session.Modules.Select(module => module.Name));
        Assert.Equal(savedBytes, File.ReadAllBytes(sourceWorkbook));
        Assert.DoesNotContain("recovery was incomplete", result.StandardError);
    }
}
